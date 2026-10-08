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
        if (args.Length > 0 && args[0] == "--filename-selfcheck") return RunFilenameSelfCheck();
        if (args.Length > 0 && args[0] == "--webp-selfcheck") return RunWebPSelfCheck();
        if (args.Length > 1 && args[0] == "--storage-selfcheck") return await RunStorageSelfCheck(args[1], args.Length > 2 && args[2] == "--expect-masked");
        if (args.Length > 0 && args[0] == "--collection-selfcheck") return await RunCollectionSelfCheck();
        await using var server = new NativeBackendServer();
        await server.StartAsync(new Progress<string>(Console.WriteLine));

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", server.Token);

        if (args.Length > 0 && args[0] == "--artifact-selfcheck")
        {
            return RunArtifactSelfCheck(args.Length > 1 ? args[1] : null);
        }

        if (args.Length > 0 && args[0] == "--serve")
        {
            var seconds = args.Length > 1 && int.TryParse(args[1], out var parsedSeconds) ? parsedSeconds : 60;
            Console.WriteLine("SMOKE_BASE=" + server.BaseUri);
            Console.WriteLine("SMOKE_TOKEN=" + server.Token);
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, seconds)));
            return 0;
        }

        if (args.Length > 0 && args[0] == "--cover-auth-selfcheck")
        {
            var id = args.Length > 1 ? args[1] : "1444613";
            using var anonymous = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            using var denied = await anonymous.GetAsync(new Uri(server.BaseUri, $"api/cover/{id}"));
            Require((int)denied.StatusCode == 401, "cover endpoint accepted a request without a token");

            var coverUri = new Uri(server.BaseUri, $"api/cover/{id}?token={Uri.EscapeDataString(server.Token)}");
            using var allowed = await anonymous.GetAsync(coverUri);
            var bytes = await allowed.Content.ReadAsByteArrayAsync();
            allowed.EnsureSuccessStatusCode();
            Require(bytes.Length > 0, "cover endpoint returned an empty image");
            Require(allowed.Content.Headers.ContentType?.MediaType == "image/jpeg", "cover endpoint returned the wrong content type");
            Console.WriteLine($"cover auth selfcheck ok: {bytes.Length} bytes, cache={allowed.Headers.CacheControl}");
            return 0;
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

    private static int RunWebPSelfCheck()
    {
        var root = Path.Combine(Path.GetTempPath(), "jm-webp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var bitmap = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(32, 15));
        for (int y = 0; y < 15; y++) for (int x = 0; x < 32; x++)
            bitmap.SetPixel(x, y, y < 5 ? SkiaSharp.SKColors.Red : y < 10 ? SkiaSharp.SKColors.Lime : SkiaSharp.SKColors.Blue);
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Webp, 100);
        var bytes = encoded.ToArray();
        File.WriteAllBytes(Path.Combine(root, "fixture.webp"), bytes);
        Directory.CreateDirectory(".ui-test");
        File.WriteAllText(Path.Combine(".ui-test", "webp-fixture.txt"), Convert.ToBase64String(bytes));
        // Confirm whether the pre-fix system-codec path works on this host.
        try
        {
            using var input = new MemoryStream(bytes);
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            Require(decoder.Frames[0].PixelWidth == 32, "system decoder returned incorrect WebP dimensions");
            Console.WriteLine("System WebP codec is present; bundled decoder is tested independently.");
        }
        catch (Exception e) when (e is IOException or NotSupportedException or System.Runtime.InteropServices.COMException)
        { Console.WriteLine("Baseline WebP failure reproduced: " + e.GetType().Name + ": " + e.Message); }
        Require(JmImageDecoder.IsUsableBytes(bytes, ".webp"), "valid WebP rejected");
        Require(!JmImageDecoder.IsUsableBytes(bytes[..14], ".webp"), "truncated WebP accepted");
        foreach (var suffix in new[] { ".png", ".jpg", ".webp" })
        {
            var output = Path.Combine(root, "converted" + suffix);
            JmImageDecoder.DecodeAndSave(bytes, 0, output);
            Require(JmImageDecoder.IsUsableImage(output), "WebP conversion output unusable: " + suffix);
            if (suffix == ".webp") Require(System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(output), 0, 4) == "RIFF", "PNG bytes were written with a WebP extension");
        }
        var restored = Path.Combine(root, "restored.png");
        JmImageDecoder.DecodeAndSave(bytes, 3, restored);
        using (var input = File.OpenRead(restored))
        {
            var decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
            var pixels = new byte[32 * 15 * 4]; frame.CopyPixels(pixels, 32 * 4, 0);
            var top = (2 * 32 + 16) * 4; var bottom = (12 * 32 + 16) * 4;
            Require(pixels[top] > 180 && pixels[top + 2] < 80 && pixels[bottom + 2] > 180 && pixels[bottom] < 80, "WebP strip order was not restored correctly");
        }
        var pdfImage = JmImageDecoder.LoadPdfImage(Path.Combine(root, "converted.webp"));
        Require(pdfImage.Width == 32 && pdfImage.Height == 15 && pdfImage.JpegBytes.Length > 20, "WebP PDF conversion failed");
        Console.WriteLine("WebP selfcheck ok: independent decoding, corrupt-input rejection, strip restoration, PNG/JPEG/WebP encoding and PDF image conversion");
        return 0;
    }

    private static async Task<int> RunStorageSelfCheck(string root, bool expectMasked)
    {
        var blocked = Path.Combine(root, "blocked");
        bool denied = false;
        try { StorageAccess.RequireWritableDirectory(blocked, "测试下载目录"); }
        catch (StorageAccessException) { denied = true; }
        Require(denied, "fixture directory is writable; real ACL denial was not applied");
        using (var cache = new JmContentCache(blocked))
        {
            var data = await cache.JsonAsync("fixture", TimeSpan.FromHours(1), _ => Task.FromResult(new System.Text.Json.Nodes.JsonObject { ["fixture"] = true }), CancellationToken.None);
            Require(data["fixture"]!.GetValue<bool>(), "metadata cache failure broke a successful response");
        }
        using var client = new JmClient(root);
        using var images = new ImageRepository(client, blocked);
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var imageRoot = (string)typeof(ImageRepository).GetField("_root", flags)!.GetValue(images)!;
        Require(!imageRoot.StartsWith(blocked, StringComparison.OrdinalIgnoreCase), "unwritable image cache did not use a private fallback");
        var photo = new PhotoDetailDto { Id = "9011", Title = "Fixture chapter", ScrambleId = "0", Images = Enumerable.Repeat("00001.png", 100).ToList() };
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("decode-v3|9011|00001.png|0|.png"))).ToLowerInvariant();
        File.WriteAllBytes(Path.Combine(imageRoot, key + ".png"), [1, 2, 3]);
        using var manager = new NativeDownloadManager(client, _ => { }, images);
        var album = new AlbumDetailDto { Id = "901", Title = "Fixture", Chapters = [new ChapterDto { Id = "9011", Title = "Fixture chapter", Sort = 1 }] };
        var method = typeof(NativeDownloadManager).GetMethod("DownloadImagesAsync", flags)!;
        var task = (Task<string>)method.Invoke(manager, ["901", album, blocked, "Fixture", new Dictionary<string, PhotoDetailDto> { ["9011"] = photo }, new DownloadSettings { BaseDir = blocked, ImageThreads = 1, PhotoThreads = 1 }, CancellationToken.None])!;
        bool correctError = false;
        try { await task; }
        catch (OperationCanceledException e) when (expectMasked) { correctError = true; Console.WriteLine("baseline reproduced: " + e.Message); }
        catch (StorageAccessException e) when (!expectMasked) { correctError = true; Console.WriteLine("fixed pipeline: " + e.Message); }
        Require(correctError, expectMasked ? "baseline did not reproduce the masked cancellation" : "permission error was masked by pipeline cancellation");
        Console.WriteLine("storage selfcheck ok: genuine Windows ACL denial, metadata response preserved, image-cache fallback and original write failure");
        return 0;
    }

    private static async Task<int> RunCollectionSelfCheck()
    {
        var root = Path.Combine(Path.GetTempPath(), "jm-collections-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pagePath = Path.Combine(root, "page.png");
        File.WriteAllBytes(pagePath, [1, 2, 3]);
        var store = new ReaderStore(root);
        foreach (var id in new[] { "101", "102", "103" })
            store.Remember(new AlbumDetailDto { Id = id, Title = "Fixture " + id, Chapters = [new ChapterDto { Id = id + "1", Title = "Chapter", Sort = 1 }] });
        var album = new AlbumDetailDto { Id = "101", Title = "Fixture 101", Chapters = [new ChapterDto { Id = "1011", Title = "Chapter", Sort = 1 }] };
        store.RegisterLocal(album, new() { ["1011"] = [pagePath] });
        store.Progress("101", new ReaderProgress { ChapterId = "1011", Page = 3, Revision = 1 });
        var first = new ReaderBookmark { AlbumId = "101", ChapterId = "1011", Page = 0 };
        var second = new ReaderBookmark { AlbumId = "101", ChapterId = "1011", Page = 1 };
        store.ToggleBookmark(first); store.ToggleBookmark(second);
        await using var server = new NativeBackendServer(root);
        await server.StartAsync();
        using var http = new HttpClient { BaseAddress = server.BaseUri, Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", server.Token);
        async Task<JsonElement> Send(string path, object body, string method = "POST")
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = new StringContent(JsonSerializer.Serialize(body, NativeJson.ApiOptions), Encoding.UTF8, "application/json") };
            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        }
        async Task<JsonElement> Shelf()
        {
            using var response = await http.GetAsync("api/reader/shelf"); response.EnsureSuccessStatusCode();
            return JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        }
        async Task Invalid(string path, object body)
        {
            using var response = await http.PostAsync(path, new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
            Require((int)response.StatusCode == 400, "invalid collection operation was accepted");
        }
        await Send("api/reader/favorites/101", new { favorite = true }, "PUT");
        var favorites = await Send("api/reader/favorites", new { ids = new[] { "101", "102", "101" }, favorite = true });
        Require(favorites.GetProperty("succeeded").GetArrayLength() == 2 && favorites.GetProperty("failed").GetArrayLength() == 0, "batch favorites did not deduplicate");
        Require(favorites.GetProperty("changed").EnumerateArray().Select(item => item.GetString()).SequenceEqual(new[] { "102" }), "batch operation included pre-existing favorites in its undo list");
        var repeated = await Send("api/reader/favorites", new { ids = new[] { "101", "102" }, favorite = true });
        Require(repeated.GetProperty("changed").GetArrayLength() == 0, "repeated favorite was not idempotent");
        await Send("api/reader/favorites", new { ids = favorites.GetProperty("changed").EnumerateArray().Select(item => item.GetString()).ToArray(), favorite = false });
        using var favoriteStatus = JsonDocument.Parse(await http.GetStringAsync("api/reader/favorites"));
        Require(favoriteStatus.RootElement.GetProperty("ids").EnumerateArray().Select(item => item.GetString()).SequenceEqual(new[] { "101" }), "undo removed a pre-existing favorite");
        await Send("api/reader/favorites", new { ids = new[] { "101", "102" }, favorite = true });
        await Invalid("api/reader/favorites", new { ids = new[] { "101", "invalid" }, favorite = false });
        await Invalid("api/reader/favorites", new { ids = Enumerable.Repeat("101", 501).ToArray() });
        await Send("api/reader/manage", new { action = "history", ids = new[] { "101" } });
        var shelf = await Shelf();
        var book = shelf.GetProperty("books").EnumerateArray().Single(b => b.GetProperty("id").GetString() == "101");
        Require(book.GetProperty("hidden_from_recent").GetBoolean() && book.GetProperty("favorite").GetBoolean() && book.GetProperty("local").GetBoolean(), "history deletion removed a favorite or local book");
        Require(book.GetProperty("progress").GetProperty("page").GetInt32() == 3 && shelf.GetProperty("bookmarks").GetArrayLength() == 2, "history deletion removed progress or bookmarks");
        await Send("api/reader/manage", new { action = "bookmarks", bookmarks = new[] { first } });
        Require((await Shelf()).GetProperty("bookmarks").GetArrayLength() == 1, "bookmark deletion removed other bookmarks");
        await Send("api/reader/manage", new { action = "local", ids = new[] { "101" } });
        Require(File.Exists(pagePath), "local shelf deletion removed a downloaded file");
        await Send("api/reader/manage", new { action = "unfavorite", ids = new[] { "102" } });
        var single = await Send("api/reader/favorites/102", new { favorite = true }, "PUT");
        Require(single.GetProperty("favorite").GetBoolean(), "single favorite endpoint failed");
        await Send("api/reader/progress/101", new ReaderProgress { ChapterId = "1011", Page = 4, Revision = 2 }, "PUT");
        Require(!new ReaderStore(root).Get("101")!.HiddenFromRecent, "reading again did not restore recent history");
        await Invalid("api/reader/manage", new { action = "unknown", ids = new[] { "101" } });
        using var anonymous = new HttpClient { BaseAddress = server.BaseUri };
        using var denied = await anonymous.PostAsync("api/reader/manage", new StringContent("{}", Encoding.UTF8, "application/json"));
        Require((int)denied.StatusCode == 401, "collection mutation bypassed authentication");
        // Seed task states without starting downloads or contacting upstream services.
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var manager = (NativeDownloadManager)typeof(NativeBackendServer).GetField("_downloadManager", flags)!.GetValue(server)!;
        typeof(NativeDownloadManager).GetField("_tasks", flags)!.SetValue(manager, new List<DownloadTaskState> { new() { ItemId = "101", Status = "success" }, new() { ItemId = "102", Status = "queued" }, new() { ItemId = "103", Status = "failed" }, new() { ItemId = "p104", Status = "cancelled" } });
        await Invalid("api/tasks/remove", new { ids = new[] { "101", "102" } });
        Require(manager.Snapshot().Tasks.Count == 4, "rejected batch partially removed tasks");
        await Send("api/tasks/remove", new { ids = new[] { "103" } });
        Require(manager.Snapshot().Tasks.Count == 3 && manager.Snapshot().Tasks.All(task => task.ItemId != "103"), "failed task was not removed");
        await Send("api/tasks/remove", new { ids = new[] { "p104" } });
        Require(manager.Snapshot().Tasks.Count == 2 && manager.Snapshot().Tasks.All(task => task.ItemId != "p104"), "chapter task ID was normalized incorrectly");
        var tasks = await Send("api/tasks/remove", new { ids = new[] { "101" } });
        Require(tasks.GetProperty("tasks").GetArrayLength() == 1 && manager.Snapshot().Tasks[0].ItemId == "102", "completed task removal changed an active task");
        var persisted = new ReaderStore(root);
        Require(persisted.Get("101") is { Favorite: true, Local: false } && persisted.Bookmarks("101").Count == 1 && File.Exists(pagePath), "collection changes did not persist safely");
        Console.WriteLine("collection selfcheck ok: favorites, history, bookmarks, local shelf, authentication, task protection and persistence");
        return 0;
    }

    private static int RunFilenameSelfCheck()
    {
        static string AndroidName(string name, string fallback = "download") =>
            ArtifactTools.SafeFilename(name, fallback, "traditional", android: true);
        static void Valid(string name)
        {
            Require(!string.IsNullOrWhiteSpace(name), "filename became empty");
            Require(Encoding.UTF8.GetByteCount(name) <= ArtifactTools.MaxSafeFilenameUtf8Bytes, "filename exceeded Android byte budget");
            Require(!name.Any(ch => ch is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || char.IsControl(ch)), "unsafe filename character survived");
            Require(!name.EndsWith(' ') && !name.EndsWith('.'), "invalid trailing filename character");
            _ = new UTF8Encoding(false, true).GetBytes(name);
        }
        foreach (var name in new[] { new string('A', 120), new string('界', 40), string.Concat(Enumerable.Repeat("📚", 30)) })
        {
            Require(AndroidName(name) == name, "a name at the byte boundary was modified");
            Valid(AndroidName(name));
        }
        foreach (var name in new[] { new string('A', 121), new string('界', 41), string.Concat(Enumerable.Repeat("📚", 31)), new string('界', 1000), new string('A', 1000) + "📚", "abc<>:/\\|?*\n\t. " })
        {
            Valid(AndroidName(name));
            Require(AndroidName(name) == AndroidName(name), "filename abbreviation was unstable");
        }
        Valid(AndroidName(" . ", new string('界', 1000) + "/?"));
        Require(AndroidName("CON", "NUL") == "download", "reserved fallback name remained invalid");
        Require(AndroidName("普通标题 01") == "普通标题 01", "short title changed");
        Require(ArtifactTools.SafeFilename(new string('界', 160), "download", "traditional", android: false).Length == 160, "desktop title compatibility changed");
        var first = AndroidName(new string('界', 400) + " A");
        var second = AndroidName(new string('界', 400) + " B");
        Require(first != second, "different long titles mapped to the same filename");
        var root = Path.Combine(Path.GetTempPath(), "jm-filenames-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var album = Path.Combine(root, first);
            var chapterA = AndroidName("001 " + string.Concat(Enumerable.Repeat("超长章节📚", 100)));
            var chapterB = AndroidName("002 " + string.Concat(Enumerable.Repeat("超长章节📚", 100)));
            MakeTestImage(Path.Combine(album, chapterA, "00001.jpg"), Colors.Red);
            MakeTestImage(Path.Combine(album, chapterB, "00001.jpg"), Colors.Blue);
            var marker = Path.Combine(album, ".jm-complete.json");
            File.WriteAllText(marker + ".tmp", "{}");
            File.Move(marker + ".tmp", marker);
            var duplicates = Path.Combine(root, "duplicates");
            var firstDir = ArtifactTools.UniqueDirectoryPath(duplicates, first);
            Directory.CreateDirectory(firstDir);
            var secondDir = ArtifactTools.UniqueDirectoryPath(duplicates, first);
            Directory.CreateDirectory(secondDir);
            Require(firstDir != secondDir, "duplicate directory reused the same path");
            var exports = Path.Combine(root, "exports");
            var zip = ArtifactTools.UniqueFilePath(exports, first, ".zip");
            ArtifactTools.MakeZip(album, zip);
            Require(CountZipImages(zip) == 2, "long-title ZIP export lost images");
            var merged = ArtifactTools.UniqueFilePath(exports, first, ".pdf");
            ArtifactTools.MakePdf(album, merged);
            Require(CountPdfImages(merged) == 2, "long-title merged PDF export lost images");
            var duplicatePdf = ArtifactTools.UniqueFilePath(exports, first, ".pdf");
            Require(duplicatePdf != merged, "duplicate PDF replaced an existing export");
            ArtifactTools.MakePdf(album, duplicatePdf);
            foreach (var directory in ArtifactTools.GroupChapterDirectories(album))
            {
                var name = AndroidName(first + " - " + Path.GetFileName(directory));
                var pdf = ArtifactTools.UniqueFilePath(exports, name, ".pdf");
                ArtifactTools.MakePdf(directory, pdf);
                Require(CountPdfImages(pdf) == 1, "long-title chapter PDF failed");
            }
            foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
            {
                Require(Encoding.UTF8.GetByteCount(Path.GetFileName(entry)) <= 255, "file component exceeded Android NAME_MAX");
                Require(!entry.EndsWith(".tmp", StringComparison.Ordinal), "temporary export file was left behind");
            }
            Console.WriteLine("filename selfcheck ok: UTF-8 boundaries, emoji, fallback sanitization, deterministic distinct names, desktop compatibility, nested image directories, duplicate names, ZIP and PDF exports");
        }
        finally
        {
            var absolute = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(absolute).StartsWith("jm-filenames-", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe filename-test cleanup path");
            Directory.Delete(absolute, recursive: true);
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
