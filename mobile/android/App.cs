using Android.App;
using Android.Runtime;
using Android.Util;
using DesktopShell.NativeBackend;
using System.Text;

namespace JmDownload.Mobile;

internal static class AppLog
{
    private const string Tag = "JmDownload";
    private static string? _file;
    private static bool _initialized;

    /// <summary>第一次写日志时再定位日志文件，避免依赖自定义 Application 子类。</summary>
    private static void EnsureFile()
    {
        if (_initialized) return;
        _initialized = true;
        try
        {
            var files = Android.App.Application.Context.FilesDir!.AbsolutePath;
            _file = Path.Combine(files, "jm-mobile.log");
            if (File.Exists(_file) && new FileInfo(_file).Length > 512 * 1024) File.Delete(_file);
        }
        catch { _file = null; }
    }

    public static void Info(string message) => Write("I", message);
    public static void Error(string message) => Write("E", message);

    private static void Write(string level, string message)
    {
        EnsureFile();
        if (level == "E") Log.Error(Tag, message); else Log.Info(Tag, message);
        if (_file is null) return;
        try
        {
            File.AppendAllText(_file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch { }
    }
}

/// <summary>
/// 进程内保存本地后端实例。它直接复用电脑端 NativeBackend 的同一份实现，
/// 因此搜索、下载、图片还原、导出与阅读行为与电脑端完全一致。
/// </summary>
internal static class BackendHost
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static NativeBackendServer? _server;

    public static int Port { get; private set; }
    public static string Token { get; private set; } = string.Empty;
    public static bool Ready => _server is not null && Port > 0;
    public static string BaseUrl => $"http://127.0.0.1:{Port}";
    public static string? LastError { get; private set; }

    public static async Task<int> StartAsync(Android.Content.Context context)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_server is not null) return Port;
            var files = context.FilesDir!.AbsolutePath;
            var dataRoot = Path.Combine(files, "data");
            Directory.CreateDirectory(dataRoot);

            // 电脑端这里走 Win32 的 LCMapStringEx，Android 换成 ICU 音译器。
            ArtifactTools.MobileConverter = ChineseScript.Convert;

            var server = new NativeBackendServer(dataRoot, projectRoot: Path.Combine(files, "no-source"));
            await server.StartAsync(new Progress<string>(AppLog.Info)).ConfigureAwait(false);

            _server = server;
            Port = server.Port;
            Token = server.Token;
            LastError = null;
            AppLog.Info($"本地服务已启动 {server.BaseUri}");

            // 必须在界面加载之前改好，否则页面会先读到电脑端遗留的默认目录。
            await ApplyMobileDefaultDirectoryAsync(context, dataRoot).ConfigureAwait(false);
            return Port;
        }
        catch (Exception exception)
        {
            LastError = exception.Message;
            AppLog.Error("本地服务启动失败: " + exception);
            throw;
        }
        finally
        {
            Gate.Release();
        }
    }

    public static void Stop()
    {
        var server = _server;
        _server = null;
        Port = 0;
        Token = string.Empty;
        if (server is null) return;
        try { server.RequestShutdown(); } catch { }
        try { server.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { }
    }

    /// <summary>手机端默认把作品保存到公共存储的 JMDownload 目录。</summary>
    public static string DefaultDownloadDirectory
    {
        get
        {
            var external = Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;
            if (!string.IsNullOrEmpty(external)) return Path.Combine(external, "JMDownload");
            return Path.Combine(Android.App.Application.Context.FilesDir!.AbsolutePath, "JMDownload");
        }
    }

    /// <summary>
    /// 电脑端的默认目录是 exe 旁边的 JMDownLoad，在手机上会落到 /system/bin 这类只读位置。
    /// 首次运行时把它换成手机上的公共目录；用户自己改过的路径（/storage、/sdcard 等）不动。
    /// </summary>
    private static async Task ApplyMobileDefaultDirectoryAsync(Android.Content.Context context, string dataRoot)
    {
        try
        {
            var preferences = context.GetSharedPreferences("jm-mobile", Android.Content.FileCreationMode.Private);
            if (preferences?.GetBoolean("default-dir-applied", false) == true) return;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);

            var json = await http.GetStringAsync($"{BaseUrl}/api/config").ConfigureAwait(false);
            var node = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject
                ?? new System.Text.Json.Nodes.JsonObject();
            var current = node["base_dir"]?.GetValue<string>() ?? string.Empty;
            var wanted = DefaultDownloadDirectory;

            var looksLikeDesktopPath = string.IsNullOrWhiteSpace(current)
                || !(current.StartsWith("/storage", StringComparison.Ordinal)
                     || current.StartsWith("/sdcard", StringComparison.Ordinal)
                     || current.StartsWith("/mnt", StringComparison.Ordinal));

            if (looksLikeDesktopPath)
            {
                // 只改 base_dir。default_base_dir 保留服务端自己算出来的值：
                // AppConfigStore 会用「base_dir 是否等于记录的默认目录」来判断这是不是
                // 该被纠正的旧默认路径，两者写成一样会把手机目录误判成需要重置。
                node["base_dir"] = wanted;
                var body = new StringContent(node.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
                using var response = await http.PutAsync($"{BaseUrl}/api/config", body).ConfigureAwait(false);
                AppLog.Info($"下载目录已设为 {wanted}（HTTP {(int)response.StatusCode}）");
            }
            else
            {
                AppLog.Info($"沿用已有下载目录 {current}");
            }

            preferences?.Edit()?.PutBoolean("default-dir-applied", true)?.Apply();
        }
        catch (Exception exception)
        {
            AppLog.Error("设置默认下载目录失败: " + exception.Message);
        }
    }
}
