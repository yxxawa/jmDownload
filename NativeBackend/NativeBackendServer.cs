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
using System.Threading.Channels;

namespace DesktopShell.NativeBackend;

public sealed class NativeBackendServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<Guid, WebSocket> _sockets = new();
    private readonly ConcurrentDictionary<Guid, Channel<byte[]>> _socketQueues = new();
    private readonly JmClient _client;
    private readonly ImageRepository _images;
    private readonly ReaderService _reader;
    private readonly AppConfigStore _configStore;
    private readonly NativeDownloadManager _downloadManager;
    private Task? _listenTask;

    private static readonly HashSet<string> SearchOrders = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mv", "mp", "tf",
    };

    private static readonly HashSet<string> SearchTimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "t", "w", "m",
    };

    public NativeBackendServer(string? appDataRoot = null, JmClient? client = null, string? projectRoot = null)
    {
        ProjectRoot = projectRoot ?? ResolveProjectRoot();
        AppDataRoot = appDataRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "JMComicDesktop");
        FrontendRoot = Path.Combine(ProjectRoot, "frontend");
        _configStore = new AppConfigStore(AppDataRoot);
        _client = client ?? new JmClient(AppDataRoot);
        _images = new ImageRepository(_client, AppDataRoot);
        _reader = new ReaderService(_client, _images, AppDataRoot);
        _downloadManager = new NativeDownloadManager(_client, PublishEvent, _images, _reader.Store);
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

        await _downloadManager.StopAsync().ConfigureAwait(false);
        _downloadManager.Dispose();
        _reader.Dispose();
        _images.Dispose();
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

            _ = HandleContextAsync(context);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext context)
    {
        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
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

            await RouteAsync(context, requestCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var status = ex is StorageAccessException or UnauthorizedAccessException ? 403 : ex is ArgumentException or System.Text.Json.JsonException ? 400 : ex is KeyNotFoundException ? 404 : ex is OperationCanceledException ? 409 : 500;
            try { await WriteJsonAsync(context.Response, status, new { detail = StorageAccess.Describe(ex) }).ConfigureAwait(false); }
            catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException) { }
        }
    }

    private async Task RouteAsync(HttpListenerContext context, CancellationToken cancellationToken)
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

        if (path.StartsWith("/api/reader/", StringComparison.Ordinal))
        { await RouteReaderAsync(context, cancellationToken).ConfigureAwait(false); return; }

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
            var page = int.TryParse(request.QueryString["page"], out var pageValue) ? Math.Max(1, pageValue) : 1;
            var mainTag = int.TryParse(request.QueryString["main_tag"], out var tagValue) ? Math.Max(0, tagValue) : 0;
            var orderBy = (request.QueryString["sort"] ?? request.QueryString["o"] ?? "mr").Trim().ToLowerInvariant();
            var time = (request.QueryString["time"] ?? request.QueryString["t"] ?? "a").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(query))
            {
                await WriteJsonAsync(response, 400, new { detail = "q is required" }).ConfigureAwait(false);
                return;
            }

            if (!SearchOrders.Contains(orderBy))
            {
                await WriteJsonAsync(response, 400, new { detail = "sort must be one of: mr, mv, mp, tf" }).ConfigureAwait(false);
                return;
            }

            if (!SearchTimes.Contains(time))
            {
                await WriteJsonAsync(response, 400, new { detail = "time must be one of: a, t, w, m" }).ConfigureAwait(false);
                return;
            }

            var items = await _client.SearchAsync(query, page, cancellationToken, mainTag, orderBy, time).ConfigureAwait(false);
            await WriteJsonAsync(response, 200, new
            {
                items,
                page,
                sort = orderBy,
                time,
                main_tag = mainTag,
                has_next = items.Count > 0,
            }).ConfigureAwait(false);
            return;
        }

        if (request.HttpMethod == "GET" && path == "/api/ranking")
        {
            var type = request.QueryString["type"] ?? "day";
            var items = await _client.RankingAsync(type, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(response, 200, new { items }).ConfigureAwait(false);
            return;
        }

        if (request.HttpMethod == "GET" && path.StartsWith("/api/album/", StringComparison.Ordinal))
        {
            var albumId = WebUtility.UrlDecode(path["/api/album/".Length..]);
            var detail = await _client.GetAlbumDetailAsync(albumId, cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(response, 200, detail).ConfigureAwait(false);
            return;
        }

        if (request.HttpMethod == "GET" && path.StartsWith("/api/cover/", StringComparison.Ordinal))
        {
            var albumId = WebUtility.UrlDecode(path["/api/cover/".Length..]);
            var bytes = await _client.DownloadCoverAsync(albumId, cancellationToken).ConfigureAwait(false);
            response.StatusCode = 200;
            response.ContentType = "image/jpeg";
            response.Headers["Cache-Control"] = "private, max-age=86400";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
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
                _downloadManager.Start(jobs, downloadRequest.AlbumThreads);
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

        if (request.HttpMethod == "POST" && path.StartsWith("/api/download/cancel/", StringComparison.Ordinal))
        {
            var itemId = WebUtility.UrlDecode(path["/api/download/cancel/".Length..]);
            await WriteJsonAsync(response, 200, new { cancelled = _downloadManager.CancelTask(itemId) }).ConfigureAwait(false);
            return;
        }

        if (request.HttpMethod == "POST" && path == "/api/download/reorder")
        {
            var body = await ReadJsonAsync<JsonNode>(request).ConfigureAwait(false);
            var itemId = body?["item_id"]?.GetValue<string>() ?? string.Empty;
            var direction = body?["direction"]?.GetValue<int>() ?? 0;
            await WriteJsonAsync(response, 200, new { reordered = _downloadManager.ReorderTask(itemId, direction) }).ConfigureAwait(false);
            return;
        }

        if (request.HttpMethod == "POST" && path == "/api/tasks/remove")
        {
            var body = await ReadJsonAsync<ReaderCollectionRequest>(request).ConfigureAwait(false) ?? new();
            if (body.Ids is null) throw new ArgumentException("请选择已结束任务");
            await WriteJsonAsync(response, 200, _downloadManager.RemoveTasks(body.Ids)).ConfigureAwait(false);
            return;
        }

        if (request.HttpMethod == "GET" && path == "/api/tasks")
        {
            await WriteJsonAsync(response, 200, _downloadManager.Snapshot()).ConfigureAwait(false);
            return;
        }

        await ServeStaticAsync(context).ConfigureAwait(false);
    }

    private async Task RouteReaderAsync(HttpListenerContext context, CancellationToken token)
    {
        var request = context.Request; var response = context.Response;
        var segments = (request.Url?.AbsolutePath ?? "").Split('/', StringSplitOptions.RemoveEmptyEntries);
        var method = request.HttpMethod;
        var resource = segments.Length > 2 ? segments[2] : "";
        var id = segments.Length > 3 ? WebUtility.UrlDecode(segments[3]) : "";
        object? result = null;
        if (method == "GET" && resource == "albums" && id != "") result = await _reader.BookAsync(id, token).ConfigureAwait(false);
        else if (method == "GET" && resource == "chapters" && id != "") result = await _reader.ChapterAsync(request.QueryString["session"] ?? "", id, token).ConfigureAwait(false);
        else if (method == "GET" && resource == "images" && segments.Length == 5)
        {
            if (!int.TryParse(segments[4], out var index)) throw new ArgumentException("页码无效");
            var file = await _reader.ImageAsync(request.QueryString["session"] ?? "", id, index, token).ConfigureAwait(false);
            await using var stream = _images.OpenRead(file);
            response.StatusCode = 200; response.ContentType = ContentTypeFor(file);
            response.Headers["Cache-Control"] = "private, max-age=86400";
            response.ContentLength64 = stream.Length;
            await stream.CopyToAsync(response.OutputStream, token).ConfigureAwait(false); response.Close(); return;
        }
        else if (method == "POST" && resource == "sessions")
        { var body = await ReadJsonAsync<JsonObject>(request).ConfigureAwait(false); result = await _reader.OpenAsync(body?["album_id"]?.GetValue<string>() ?? "", token).ConfigureAwait(false); }
        else if (method == "PATCH" && resource == "sessions" && id != "" && segments.Length == 5 && segments[4] == "window")
        {
            var body = await ReadJsonAsync<JsonObject>(request).ConfigureAwait(false) ?? new();
            _reader.Window(id, body["chapter_id"]?.GetValue<string>() ?? "", body["start"]?.GetValue<int>() ?? 0, body["end"]?.GetValue<int>() ?? 0, body["revision"]?.GetValue<long>() ?? 0);
            result = new { updated = true };
        }
        else if (method == "DELETE" && resource == "sessions" && id != "") result = new { closed = _reader.Close(id) };
        else if (method == "PUT" && resource == "progress" && id != "") result = _reader.Progress(id, await ReadJsonAsync<ReaderProgress>(request).ConfigureAwait(false) ?? new());
        else if (method == "GET" && resource == "shelf") result = _reader.Shelf();
        else if (method == "DELETE" && resource == "shelf" && id != "") { _reader.Store.Remove(id); result = new { removed = true }; }
        else if (method == "POST" && resource == "manage")
        { result = new { removed = _reader.Store.Manage(await ReadJsonAsync<ReaderCollectionRequest>(request).ConfigureAwait(false) ?? new()) }; }
        else if (method == "GET" && resource == "favorites" && id == "") result = new { ids = _reader.Store.FavoriteIds() };
        else if (method == "POST" && resource == "favorites" && id == "")
        { result = await _reader.FavoritesAsync(await ReadJsonAsync<ReaderCollectionRequest>(request).ConfigureAwait(false) ?? new(), token).ConfigureAwait(false); }
        else if (method == "PUT" && resource == "favorites" && id != "")
        {
            id = JmClient.ParseJmId(id);
            var body = await ReadJsonAsync<JsonObject>(request).ConfigureAwait(false);
            bool favorite = body?["favorite"]?.GetValue<bool>() ?? false;
            if (favorite && _reader.Store.Get(id) is null) await _reader.BookAsync(id, token).ConfigureAwait(false);
            result = _reader.Store.Favorite(id, favorite);
        }
        else if (method == "GET" && resource == "bookmarks") result = new { bookmarks = _reader.Store.Bookmarks(request.QueryString["album_id"]) };
        else if (method == "POST" && resource == "bookmarks") result = new { bookmarked = _reader.Store.ToggleBookmark(await ReadJsonAsync<ReaderBookmark>(request).ConfigureAwait(false) ?? new()) };
        else if (method == "GET" && resource == "settings") result = _reader.Store.Settings;
        else if (method == "PUT" && resource == "settings") result = _reader.Settings(await ReadJsonAsync<ReaderSettings>(request).ConfigureAwait(false) ?? new());
        else if (method == "GET" && resource == "cache") result = _images.Stats();
        else if (method == "DELETE" && resource == "cache") result = new { removed = _images.Clear(), cache = _images.Stats() };
        if (result is null) { await WriteJsonAsync(response, 404, new { detail = "reader route not found" }).ConfigureAwait(false); return; }
        await WriteJsonAsync(response, 200, result).ConfigureAwait(false);
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
        if (!fullPath.StartsWith(frontendRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
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
        if (string.IsNullOrWhiteSpace(Token) || token != Token)
        {
            context.Response.StatusCode = 401;
            context.Response.Close();
            return;
        }

        var wsContext = await context.AcceptWebSocketAsync(null).ConfigureAwait(false);
        var socket = wsContext.WebSocket;
        var id = Guid.NewGuid();
        _sockets[id] = socket;
        var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
        _socketQueues[id] = queue;
        var sender = SendLoopAsync();
        var buffer = new byte[1024];
        try
        {
            while (socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            _sockets.TryRemove(id, out _); _socketQueues.TryRemove(id, out _);
            queue.Writer.TryComplete(); socket.Abort();
            try { await sender.ConfigureAwait(false); } catch { }
            socket.Dispose();
        }
        async Task SendLoopAsync()
        {
            try
            {
                await foreach (var bytes in queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(5));
                    await socket.SendAsync(bytes, WebSocketMessageType.Text, true, deadline.Token).ConfigureAwait(false);
                }
            }
            catch { socket.Abort(); }
        }
    }

    private void PublishEvent(DownloadEventDto evt)
    {
        if (_sockets.IsEmpty) return;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(evt, NativeJson.ApiOptions));
        foreach (var (id, socket) in _sockets)
        {
            if (!_socketQueues.TryGetValue(id, out var queue)) continue;
            // Never enqueue unbounded Tasks. A slow connection reconnects and loads a full snapshot.
            if (socket.State != WebSocketState.Open || !queue.Writer.TryWrite(bytes))
            { queue.Writer.TryComplete(); socket.Abort(); }
        }
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        if (string.IsNullOrWhiteSpace(Token))
        {
            return true;
        }

        if (request.Headers["Authorization"] == "Bearer " + Token)
        {
            return true;
        }

        // Browser image elements cannot attach the API Authorization header.
        // Permit the local cover endpoint to use the same short-lived token in
        // its query string so covers can begin loading immediately.
        var path = request.Url?.AbsolutePath ?? "";
        return (path.StartsWith("/api/cover/", StringComparison.Ordinal) ||
                (path.StartsWith("/api/reader/images/", StringComparison.Ordinal) && path.Split('/', StringSplitOptions.RemoveEmptyEntries).Length == 5))
               && request.QueryString["token"] == Token;
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
        var json = JsonSerializer.Serialize(payload, NativeJson.ApiOptions);
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
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
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
        var depth = 0;
        while (directory is not null && depth < 6)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "frontend")))
                return directory.FullName;
            directory = directory.Parent;
            depth++;
        }
        return AppContext.BaseDirectory;
    }
}
