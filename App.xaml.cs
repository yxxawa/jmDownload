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
            var assetPaths = new[]
            {
                "index.html", "app.js", "styles.css", "desktop.css", "reader.js", "reader.css", "bulk.js", "bulk.css",
                "brand/brand-avatar.webp", "brand/mascot-collection.webp", "brand/mascot-download.webp",
                "brand/mascot-history.webp", "brand/mascot-home.webp", "brand/mascot-reading.webp",
                "brand/mascot-retry.webp", "brand/mascot-search.webp", "brand/mascot-welcome.webp",
            }.Concat(Enumerable.Range(1, 8).Select(index => $"brand/demo-cover-{index}.svg"));
            foreach (var path in assetPaths)
            {
                using var response = await http.GetAsync(new Uri(server.BaseUri, path));
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Embedded asset missing: {path} ({(int)response.StatusCode})");
                var bytes = await response.Content.ReadAsByteArrayAsync();
                if (bytes.Length < 500) throw new InvalidOperationException("Embedded asset empty or truncated: " + path);
                var expectedType = Path.GetExtension(path) switch
                {
                    ".html" => "text/html", ".js" => "text/javascript", ".css" => "text/css",
                    ".webp" => "image/webp", ".svg" => "image/svg+xml", _ => throw new InvalidOperationException("Unknown asset type: " + path),
                };
                if (response.Content.Headers.ContentType?.MediaType != expectedType) throw new InvalidOperationException("Embedded asset content type mismatch: " + path);
                if (path.EndsWith(".webp", StringComparison.Ordinal) && !JmImageDecoder.IsUsableBytes(bytes, ".webp")) throw new InvalidOperationException("Embedded image cannot be decoded: " + path);
                assets[path] = bytes.Length;
            }
            using var settings = JsonDocument.Parse(await http.GetStringAsync(new Uri(server.BaseUri, "api/reader/settings")));
            using var shelf = JsonDocument.Parse(await http.GetStringAsync(new Uri(server.BaseUri, "api/reader/shelf")));
            if (settings.RootElement.GetProperty("prefetch").GetInt32() != 3 || shelf.RootElement.GetProperty("books").GetArrayLength() != 0) throw new InvalidOperationException("Reader initialization check failed");
            var webpOutput = Path.Combine(root, "webp-check.png");
            JmImageDecoder.DecodeAndSave(Convert.FromBase64String("UklGRkgAAABXRUJQVlA4TDsAAAAvH4ADABcgEEjaH3qN+RcQFPk/2vwHXwoEAkmSS04c0f/4////////////r6qqqqqqqqqqqgIAAAAAAAA="), 3, webpOutput);
            if (!JmImageDecoder.IsUsableImage(webpOutput)) throw new InvalidOperationException("Bundled WebP decoder check failed");
            result = new { result = "PASS", webp_decoding = true, embedded_assets = assets, reader_authentication = true, isolated_reader_initialization = true, upstream_requests = 0, data_root = root };
        }
        catch (Exception ex) { status = 1; result = new { result = "FAIL", error = ex.ToString(), data_root = root }; }
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Shutdown(status);
    }
}
