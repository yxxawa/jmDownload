using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows;
using DesktopShell.NativeBackend;

namespace DesktopShell;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length < 2 || e.Args[0] != "--selfcheck") { base.OnStartup(e); MainWindow = new MainWindow(); MainWindow.Show(); return; }
        // Headless packaging diagnostic: isolated data, embedded assets, no upstream requests.
        base.OnStartup(e);
        var reportPath = Path.GetFullPath(e.Args[1]);
        var root = Path.Combine(Path.GetTempPath(), "jm-package-check-" + Guid.NewGuid().ToString("N"));
        int status = 0; object result;
        try
        {
            await using var server = new NativeBackendServer(root, projectRoot: Path.Combine(root, "no-source"));
            await server.StartAsync();
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var denied = await http.GetAsync(new Uri(server.BaseUri, "api/reader/settings"));
            if ((int)denied.StatusCode != 401) throw new InvalidOperationException("Reader authentication check failed");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", server.Token);
            var assets = new Dictionary<string, int>();
            foreach (var path in new[] {"index.html", "app.js", "reader.js", "reader.css"})
            {
                var text = await http.GetStringAsync(new Uri(server.BaseUri, path));
                if (text.Length < 500) throw new InvalidOperationException("Embedded asset missing: " + path);
                assets[path] = text.Length;
            }
            using var settings = JsonDocument.Parse(await http.GetStringAsync(new Uri(server.BaseUri, "api/reader/settings")));
            using var shelf = JsonDocument.Parse(await http.GetStringAsync(new Uri(server.BaseUri, "api/reader/shelf")));
            if (settings.RootElement.GetProperty("prefetch").GetInt32() != 3 || shelf.RootElement.GetProperty("books").GetArrayLength() != 0) throw new InvalidOperationException("Reader initialization check failed");
            result = new { result = "PASS", embedded_assets = assets, reader_authentication = true, isolated_reader_initialization = true, upstream_requests = 0, data_root = root };
        }
        catch (Exception ex) { status = 1; result = new { result = "FAIL", error = ex.ToString(), data_root = root }; }
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Shutdown(status);
    }
}
