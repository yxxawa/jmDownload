using System.Net.Http.Headers;
using System.Net.Http;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopShell.NativeBackend;

namespace DesktopShell;

internal static class NativeBackendSmoke
{
    public static async Task<int> Main(string[] args)
    {
        await using var server = new NativeBackendServer();
        await server.StartAsync(new Progress<string>(Console.WriteLine));

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", server.Token);

        if (args.Length > 0 && args[0] == "--artifact-selfcheck")
        {
            return RunArtifactSelfCheck(args.Length > 1 ? args[1] : null);
        }

        if (args.Length > 0 && args[0] == "--download")
        {
            var id = args.Length > 1 ? args[1] : "1437914";
            var baseDir = args.Length > 2
                ? args[2]
                : Path.Combine(Path.GetTempPath(), "jm-csharp-smoke-" + Guid.NewGuid().ToString("N"));
            var format = args.Length > 3 ? args[3] : "images";
            var autoPath = args.Length <= 4 || bool.Parse(args[4]);
            var pdfMode = args.Length > 5 ? args[5] : "merged";
            var payload = new
            {
                ids = new[] { id },
                base_dir = baseDir,
                image_format = ".jpg",
                output_format = format,
                pdf_mode = pdfMode,
                photo_threads = 1,
                image_threads = 3,
                auto_path = autoPath,
            };
            var json = JsonSerializer.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await http.PostAsync(new Uri(server.BaseUri, "api/download"), content);
            Console.WriteLine(await response.Content.ReadAsStringAsync());
            response.EnsureSuccessStatusCode();

            for (var i = 0; i < 240; i++)
            {
                await Task.Delay(1000);
                var tasksText = await http.GetStringAsync(new Uri(server.BaseUri, "api/tasks"));
                Console.WriteLine(tasksText);
                using var document = JsonDocument.Parse(tasksText);
                if (!document.RootElement.GetProperty("running").GetBoolean())
                {
                    return document.RootElement.GetProperty("last_failed_ids").GetArrayLength() == 0 ? 0 : 2;
                }
            }

            return 3;
        }

        foreach (var path in args.Length == 0 ? new[] { "health", "api/config" } : args)
        {
            var uri = new Uri(server.BaseUri, path);
            var text = await http.GetStringAsync(uri);
            Console.WriteLine("GET " + uri);
            Console.WriteLine(text.Length > 500 ? text[..500] : text);
        }

        return 0;
    }

    private static int RunArtifactSelfCheck(string? requestedRoot)
    {
        var root = Path.GetFullPath(requestedRoot ?? Path.Combine(Path.GetTempPath(), "jm-csharp-artifact-" + Guid.NewGuid().ToString("N")));
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
        Directory.CreateDirectory(root);

        var source = Path.Combine(root, "source", "多章节标题");
        MakeTestImage(Path.Combine(source, "001 第一章", "00001.jpg"), Colors.Red);
        MakeTestImage(Path.Combine(source, "001 第一章", "00002.jpg"), Colors.Orange);
        MakeTestImage(Path.Combine(source, "002 第二章", "00001.jpg"), Colors.SteelBlue);
        MakeTestImage(Path.Combine(source, "002 第二章", "00002.jpg"), Colors.SeaGreen);

        var chapterDirs = ArtifactTools.GroupChapterDirectories(source);
        Require(chapterDirs.Count == 2, "multi-chapter grouping failed");

        var mergedPdf = Path.Combine(root, "多章节标题.pdf");
        ArtifactTools.MakePdf(source, mergedPdf);
        Require(File.Exists(mergedPdf), "merged PDF missing");
        Require(CountPdfImages(mergedPdf) == 4, "merged PDF image count mismatch");

        var chapterRoot = Path.Combine(root, "chapter-pdf");
        Directory.CreateDirectory(chapterRoot);
        foreach (var dir in chapterDirs)
        {
            ArtifactTools.MakePdf(dir, ArtifactTools.UniqueFilePath(chapterRoot, "多章节标题 - " + Path.GetFileName(dir), ".pdf"));
        }
        Require(Directory.EnumerateFiles(chapterRoot, "*.pdf").Count() == 2, "chapter PDF count mismatch");

        var zipPath = Path.Combine(root, "多章节标题.zip");
        ArtifactTools.MakeZip(source, zipPath);
        Require(CountZipImages(zipPath) == 4, "zip image count mismatch");

        var zipImages = Path.Combine(root, "zip-to-images");
        ArtifactTools.ExtractZipToImages(zipPath, zipImages);
        Require(ArtifactTools.EnumerateImages(zipImages).Count == 4, "zip-to-images count mismatch");

        var pdfImages = Path.Combine(root, "pdf-to-images");
        ArtifactTools.ExtractPdfImages(mergedPdf, pdfImages);
        Require(ArtifactTools.EnumerateImages(pdfImages).Count == 4, "pdf-to-images count mismatch");

        var pdfToZip = Path.Combine(root, "PDF转ZIP标题.zip");
        ArtifactTools.MakeZip(pdfImages, pdfToZip);
        Require(Path.GetFileName(pdfToZip) == "PDF转ZIP标题.zip", "pdf-to-zip filename mismatch");
        Require(CountZipImages(pdfToZip) == 4, "pdf-to-zip count mismatch");

        Require(!Directory.EnumerateDirectories(root, ".jmdownload_*", SearchOption.AllDirectories).Any(), "temporary directory leaked");

        Console.WriteLine("artifact selfcheck ok: " + root);
        return 0;
    }

    private static void MakeTestImage(string path, Color color)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var width = 80;
        var height = 120;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = color.B;
            pixels[i + 1] = color.G;
            pixels[i + 2] = color.R;
            pixels[i + 3] = 255;
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    private static int CountZipImages(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        return archive.Entries.Count(entry =>
            !string.IsNullOrWhiteSpace(entry.Name)
            && ArtifactTools.ImageExtensions.Contains(Path.GetExtension(entry.Name).ToLowerInvariant()));
    }

    private static int CountPdfImages(string path)
    {
        var temp = Path.Combine(Path.GetTempPath(), "jm-csharp-pdf-count-" + Guid.NewGuid().ToString("N"));
        try
        {
            ArtifactTools.ExtractPdfImages(path, temp);
            return ArtifactTools.EnumerateImages(temp).Count;
        }
        finally
        {
            if (Directory.Exists(temp))
            {
                Directory.Delete(temp, recursive: true);
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
