using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;

namespace DesktopShell.NativeBackend;

public sealed class NativeBackendServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, WebSocket> _sockets = new();
    private readonly JmClient _client = new();
    private readonly AppConfigStore _configStore;
    private readonly NativeDownloadManager _downloadManager;
    private Task? _listenTask;

    public NativeBackendServer()
    {
        ProjectRoot = ResolveProjectRoot();
        AppDataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JMComicDesktop");
        FrontendRoot = Path.Combine(ProjectRoot, "frontend");
        _configStore = new AppConfigStore(AppDataRoot);
        _downloadManager = new NativeDownloadManager(_client, PublishEvent);
    }

    public int Port { get; private set; }
    public string Token { get; private set; } = string.Empty;
    public Uri BaseUri => new($"http://127.0.0.1:{Port}/");
    public string ProjectRoot { get; }
    public string AppDataRoot { get; }
    public string FrontendRoot { get; }

    public void RequestShutdown()
    {
        _downloadManager.RequestStop();
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
            // Shutdown may race with listener startup/stop.
        }

        foreach (var socket in _sockets.Values)
        {
            try
            {
                socket.Abort();
            }
            catch
            {
                // Ignore socket shutdown failures.
            }
        }
    }

    public Task StartAsync(IProgress<string>? progress = null)
    {
        Port = GetFreeTcpPort();
        Token = GenerateToken();
        Directory.CreateDirectory(AppDataRoot);

        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _listenTask = Task.Run(ListenLoopAsync);
        progress?.Report($"C# 原生后端已启动，端口 {Port}");
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        RequestShutdown();

        foreach (var socket in _sockets.Values)
        {
            try
            {
                socket.Abort();
            }
            catch
            {
                // Ignore socket shutdown failures.
            }
            finally
            {
                socket.Dispose();
            }
        }

        if (_listenTask is not null)
        {
            try
            {
                await _listenTask.ConfigureAwait(false);
            }
            catch
            {
                // Listener is expected to fault when stopped.
            }
        }

        _client.Dispose();
        _cts.Dispose();
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                if (!_listener.IsListening)
                {
                    return;
                }
                continue;
            }

            _ = Task.Run(() => HandleContextAsync(context), _cts.Token);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context)
    {
        try
        {
            if (context.Request.IsWebSocketRequest && context.Request.Url?.AbsolutePath == "/ws/events")
            {
                await HandleWebSocketAsync(context).ConfigureAwait(false);
                return;
            }

            if (RequiresAuth(context.Request.Url?.AbsolutePath) && !IsAuthorized(context.Request))
            {
                await WriteJsonAsync(context.Response, 401, new { detail = "invalid token" }).ConfigureAwait(false);
                return;
            }

            await RouteAsync(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (context.Response.OutputStream.CanWrite)
            {
                await WriteJsonAsync(context.Response, 500, new { detail = ex.Message }).ConfigureAwait(false);
            }
        }
    }

    private async Task RouteAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var path = request.Url?.AbsolutePath ?? "/";

        if (request.HttpMethod == "GET" && path == "/health")
        {
            await WriteJsonAsync(response, 200, new
            {
                ok = true,
                service = "jmcomic-csharp-backend",
                download = _downloadManager.Snapshot(),
            }).ConfigureAwait(false);
            return;
        }

        if (request.HttpMethod == "GET" && path == "/api/session")
        {
            await WriteJsonAsync(response, 200, new { authenticated = true, token_required = true }).ConfigureAwait(false);
            return;
        }

        if (request.HttpMethod == "GET" && path == "/api/config")
        {
            await WriteJsonAsync(response, 200, _configStore.Load()).ConfigureAwait(false);
            return;
        }

        if (request.HttpMethod == "PUT" && path == "/api/config")
        {
            var config = await ReadJsonAsync<AppConfigDto>(request).ConfigureAwait(false);
            await WriteJsonAsync(response, 200, _configStore.Save(config ?? new AppConfigDto())).ConfigureAwait(false);
            return;
        }

        if (request.HttpMethod == "GET" && path == "/api/search")
        {
            var query = request.QueryString["q"] ?? string.Empty;
            var page = int.TryParse(request.QueryString["page"], out var pageValue) ? pageValue : 1;
            if (string.IsNullOrWhiteSpace(query))
            {
                await WriteJsonAsync(response, 400, new { detail = "q is required" }).ConfigureAwait(false);
                return;
            }

            var items = await _client.SearchAsync(query, page, _cts.Token).ConfigureAwait(false);
            await WriteJsonAsync(response, 200, new
            {
                items,
                page,
                has_next = items.Count > 0,
            }).ConfigureAwait(false);
            return;
        }

        if (request.HttpMethod == "GET" && path == "/api/ranking")
        {
            var type = request.QueryString["type"] ?? "day";
            var items = await _client.RankingAsync(type, _cts.Token).ConfigureAwait(false);
            await WriteJsonAsync(response, 200, new { items }).ConfigureAwait(false);
            return;
        }

        if (request.HttpMethod == "GET" && path.StartsWith("/api/album/", StringComparison.Ordinal))
        {
            var albumId = WebUtility.UrlDecode(path["/api/album/".Length..]);
            var detail = await _client.GetAlbumDetailAsync(albumId, _cts.Token).ConfigureAwait(false);
            await WriteJsonAsync(response, 200, detail).ConfigureAwait(false);
            return;
        }

        if (request.HttpMethod == "GET" && path.StartsWith("/api/cover/", StringComparison.Ordinal))
        {
            var albumId = WebUtility.UrlDecode(path["/api/cover/".Length..]);
            var bytes = await _client.DownloadCoverAsync(albumId, _cts.Token).ConfigureAwait(false);
            response.StatusCode = 200;
            response.ContentType = "image/jpeg";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
            response.Close();
            return;
        }

        if (request.HttpMethod == "POST" && path == "/api/download")
        {
            var downloadRequest = await ReadJsonAsync<DownloadRequestDto>(request).ConfigureAwait(false);
            if (downloadRequest is null)
            {
                await WriteJsonAsync(response, 400, new { detail = "invalid request" }).ConfigureAwait(false);
                return;
            }

            var ids = ParseIds(downloadRequest.Ids);
            if (ids.Count == 0)
            {
                await WriteJsonAsync(response, 400, new { detail = "no valid ids" }).ConfigureAwait(false);
                return;
            }

            var jobs = ids.Select(id => new DownloadJob
            {
                ItemId = id,
                Settings = _configStore.ToSettings(downloadRequest, id),
            }).ToList();

            try
            {
                _downloadManager.Start(jobs);
            }
            catch (InvalidOperationException ex)
            {
                await WriteJsonAsync(response, 409, new { detail = ex.Message }).ConfigureAwait(false);
                return;
            }

            await WriteJsonAsync(response, 200, new { started = true, ids }).ConfigureAwait(false);
            return;
        }

        if (request.HttpMethod == "POST" && path == "/api/download/stop")
        {
            await WriteJsonAsync(response, 200, new { stopping = _downloadManager.RequestStop() }).ConfigureAwait(false);
            return;
        }

        if (request.HttpMethod == "GET" && path == "/api/tasks")
        {
            await WriteJsonAsync(response, 200, _downloadManager.Snapshot()).ConfigureAwait(false);
            return;
        }

        await ServeStaticAsync(context).ConfigureAwait(false);
    }

    private async Task ServeStaticAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        if (path == "/")
        {
            path = "/index.html";
        }

        var relativePath = WebUtility.UrlDecode(path.TrimStart('/'));
        var diskRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(FrontendRoot, diskRelativePath));
        var frontendRoot = Path.GetFullPath(FrontendRoot);
        if (!fullPath.StartsWith(frontendRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            if (await ServeEmbeddedStaticAsync(context, relativePath).ConfigureAwait(false))
            {
                return;
            }

            await WriteJsonAsync(context.Response, 404, new { detail = "not found" }).ConfigureAwait(false);
            return;
        }

        var bytes = await File.ReadAllBytesAsync(fullPath, _cts.Token).ConfigureAwait(false);
        context.Response.StatusCode = 200;
        context.Response.ContentType = ContentTypeFor(fullPath);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
        context.Response.Close();
    }

    private static async Task<bool> ServeEmbeddedStaticAsync(HttpListenerContext context, string relativePath)
    {
        relativePath = relativePath.Replace('\\', '/');
        if (relativePath.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        var resourceName = "frontend/" + relativePath;
        var assembly = Assembly.GetExecutingAssembly();
        await using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return false;
        }

        context.Response.StatusCode = 200;
        context.Response.ContentType = ContentTypeFor(relativePath);
        context.Response.ContentLength64 = stream.Length;
        await stream.CopyToAsync(context.Response.OutputStream).ConfigureAwait(false);
        context.Response.Close();
        return true;
    }

    private async Task HandleWebSocketAsync(HttpListenerContext context)
    {
        var token = context.Request.QueryString["token"] ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(Token) && token != Token)
        {
            context.Response.StatusCode = 401;
            context.Response.Close();
            return;
        }

        var wsContext = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
        var socket = wsContext.WebSocket;
        var id = Guid.NewGuid();
        _sockets[id] = socket;

        var buffer = new byte[1024];
        try
        {
            while (socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch
        {
            // Client disconnected.
        }
        finally
        {
            _sockets.TryRemove(id, out _);
            socket.Dispose();
        }
    }

    private void PublishEvent(DownloadEventDto evt)
    {
        var payload = JsonSerializer.Serialize(evt, NativeJson.JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(payload);

        foreach (var (id, socket) in _sockets.ToArray())
        {
            if (socket.State != WebSocketState.Open)
            {
                _sockets.TryRemove(id, out _);
                socket.Dispose();
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
                }
                catch
                {
                    _sockets.TryRemove(id, out _);
                    socket.Dispose();
                }
            });
        }
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        if (string.IsNullOrWhiteSpace(Token))
        {
            return true;
        }

        return request.Headers["Authorization"] == "Bearer " + Token;
    }

    private static bool RequiresAuth(string? path)
    {
        return path == "/health" || path == "/ws/events" || path?.StartsWith("/api/", StringComparison.Ordinal) == true;
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, request.ContentEncoding);
        var text = await reader.ReadToEndAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(text))
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(text, NativeJson.JsonOptions);
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, int statusCode, object payload)
    {
        var json = JsonSerializer.Serialize(payload, NativeJson.JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    private static List<string> ParseIds(IEnumerable<string> rawIds)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var value in rawIds)
        {
            foreach (var part in value.Split([',', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (seen.Add(part))
                {
                    result.Add(part);
                }
            }
        }

        return result;
    }

    private static string ContentTypeFor(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".svg" => "image/svg+xml",
            _ => "application/octet-stream",
        };
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private static string ResolveProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "frontend")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Directory.GetCurrentDirectory();
    }
}
