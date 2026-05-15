using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace DesktopShell.NativeBackend;

public static class ArtifactTools
{
    public static readonly HashSet<string> ImageExtensions =
    [
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif",
    ];

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int LCMapStringEx(
        string? lpLocaleName, uint dwMapFlags,
        string lpSrcStr, int cchSrc,
        [Out] char[]? lpDestStr, int cchDest,
        IntPtr lpVersionInfo, IntPtr lpReserved, IntPtr sortHandle);

    private const uint LCMAP_SIMPLIFIED_CHINESE = 0x02000000;
    private const uint LCMAP_TRADITIONAL_CHINESE = 0x04000000;

    public static string ConvertChineseScript(string text, bool toSimplified)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var flag = toSimplified ? LCMAP_SIMPLIFIED_CHINESE : LCMAP_TRADITIONAL_CHINESE;
        var dest = new char[text.Length];
        var len = LCMapStringEx("zh-Hans", flag, text, text.Length, dest, dest.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        return len > 0 ? new string(dest, 0, len) : text;
    }

    public static string SafeFilename(string name, string fallback = "download", string filenameLang = "traditional")
    {
        if (filenameLang == "simplified")
            name = ConvertChineseScript(name ?? string.Empty, toSimplified: true);

        name = Regex.Replace(name ?? string.Empty, """[<>:"/\\|?*\x00-\x1f]+""", "_");
        name = Regex.Replace(name, @"\s+", " ").Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(name)) return fallback;

        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON","PRN","AUX","NUL",
            "COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9",
            "LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9",
        };
        if (reserved.Contains(name)) return fallback;
        return name.Length > 160 ? name[..160] : name;
    }

    public static string UniqueFilePath(string directory, string name, string suffix)
    {
        Directory.CreateDirectory(directory);
        var safe = SafeFilename(name);
        var path = Path.Combine(directory, safe + suffix);
        if (!File.Exists(path))
        {
            return path;
        }

        for (var index = 2; index < 1000; index++)
        {
            var candidate = Path.Combine(directory, $"{safe} ({index}){suffix}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, safe + "_" + Guid.NewGuid().ToString("N")[..8] + suffix);
    }

    public static string UniqueDirectoryPath(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var safe = SafeFilename(name);
        var path = Path.Combine(directory, safe);
        if (!Directory.Exists(path))
        {
            return path;
        }

        for (var index = 2; index < 1000; index++)
        {
            var candidate = Path.Combine(directory, $"{safe} ({index})");
            if (!Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(directory, safe + "_" + Guid.NewGuid().ToString("N")[..8]);
    }

    public static List<string> EnumerateImages(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            .OrderBy(path => path, NaturalStringComparer.Instance)
            .ToList();
    }

    public static List<string> EnumerateDirectImages(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly)
            .Where(path => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            .OrderBy(path => path, NaturalStringComparer.Instance)
            .ToList();
    }

    public static List<string> GroupChapterDirectories(string sourceDir)
    {
        if (!Directory.Exists(sourceDir))
        {
            return [sourceDir];
        }

        var groups = Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories)
            .Where(dir => !Path.GetFileName(dir).StartsWith(".jmdownload_", StringComparison.Ordinal)
                          && EnumerateDirectImages(dir).Count > 0)
            .OrderBy(dir => dir, NaturalStringComparer.Instance)
            .ToList();

        if (groups.Count > 1)
        {
            return groups;
        }

        if (groups.Count == 1 && EnumerateDirectImages(sourceDir).Count == 0)
        {
            return groups;
        }

        return [sourceDir];
    }

    public static string? FindExistingArtifact(string outputDir, string title, string suffix, string? itemId = null)
    {
        if (!Directory.Exists(outputDir))
        {
            return null;
        }

        foreach (var name in CandidateNames(title, itemId))
        {
            var expected = Path.Combine(outputDir, name + suffix);
            if (File.Exists(expected) && new FileInfo(expected).Length > 0)
            {
                return expected;
            }
        }

        return Directory.EnumerateFiles(outputDir, "*" + suffix, SearchOption.TopDirectoryOnly)
            .FirstOrDefault(path => CandidateNames(title, itemId)
                .Contains(Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase));
    }

    public static string? FindImageSource(string outputDir, string title)
    {
        if (!Directory.Exists(outputDir))
        {
            return null;
        }

        var titleDir = Path.Combine(outputDir, SafeFilename(title));
        if (Directory.Exists(titleDir) && EnumerateImages(titleDir).Count > 0)
        {
            return titleDir;
        }

        if (EnumerateDirectImages(outputDir).Count > 0)
        {
            return outputDir;
        }

        var childSources = Directory.EnumerateDirectories(outputDir, "*", SearchOption.TopDirectoryOnly)
            .Where(dir => !Path.GetFileName(dir).StartsWith(".jmdownload_", StringComparison.Ordinal)
                          && EnumerateImages(dir).Count > 0)
            .ToList();

        return childSources.Count == 1 ? childSources[0] : null;
    }

    public static void DeleteDirectoryIfChild(string parent, string child)
    {
        try
        {
            var parentFullPath = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var childFullPath = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!Directory.Exists(childFullPath))
            {
                return;
            }

            if (Path.GetDirectoryName(childFullPath)?.Equals(parentFullPath, StringComparison.OrdinalIgnoreCase) != true)
            {
                return;
            }

            if (!Path.GetFileName(childFullPath).StartsWith(".jmdownload_", StringComparison.Ordinal))
            {
                return;
            }

            Directory.Delete(childFullPath, recursive: true);
        }
        catch
        {
            // Cleanup is best-effort.
        }
    }

    private static List<string> CandidateNames(string title, string? itemId)
    {
        var names = new List<string> { SafeFilename(title) };
        if (!string.IsNullOrWhiteSpace(itemId))
        {
            names.Add(SafeFilename(itemId));
            if (itemId.StartsWith("p", StringComparison.OrdinalIgnoreCase) && itemId.Length > 1)
            {
                names.Add(SafeFilename(itemId[1..]));
            }
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void MakeZip(string sourceDir, string outputPath)
    {
        var images = EnumerateImages(sourceDir);
        if (images.Count == 0)
        {
            throw new InvalidOperationException("没有找到可打包的图片");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var tempPath = outputPath + ".tmp";
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        using (var archive = ZipFile.Open(tempPath, ZipArchiveMode.Create))
        {
            foreach (var image in images)
            {
                var relative = Path.GetRelativePath(sourceDir, image).Replace('\\', '/');
                archive.CreateEntryFromFile(image, relative, CompressionLevel.Optimal);
            }
        }

        ReplaceFile(tempPath, outputPath);
    }

    public static void ExtractZipToImages(string zipPath, string outputDir)
    {
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException("ZIP 文件不存在", zipPath);
        }

        Directory.CreateDirectory(outputDir);
        var outputRoot = Path.GetFullPath(outputDir);
        using var archive = ZipFile.OpenRead(zipPath);
        var index = 1;
        foreach (var entry in archive.Entries
                     .Where(entry => !string.IsNullOrWhiteSpace(entry.Name)
                                     && ImageExtensions.Contains(Path.GetExtension(entry.Name).ToLowerInvariant()))
                     .OrderBy(entry => entry.FullName, NaturalStringComparer.Instance))
        {
            var relative = entry.FullName.Replace('\\', '/');
            if (relative.Contains("..", StringComparison.Ordinal))
            {
                relative = $"{index:D5}{Path.GetExtension(entry.Name)}";
            }

            var targetPath = Path.GetFullPath(Path.Combine(outputDir, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!targetPath.StartsWith(outputRoot, StringComparison.OrdinalIgnoreCase))
            {
                targetPath = Path.Combine(outputDir, $"{index:D5}{Path.GetExtension(entry.Name)}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, overwrite: true);
            index++;
        }

        if (EnumerateImages(outputDir).Count == 0)
        {
            throw new InvalidOperationException("已有 ZIP 中没有可复用图片: " + zipPath);
        }
    }

    public static void ExtractPdfImages(string pdfPath, string outputDir)
    {
        if (!File.Exists(pdfPath))
        {
            throw new FileNotFoundException("PDF 文件不存在", pdfPath);
        }

        Directory.CreateDirectory(outputDir);
        var bytes = File.ReadAllBytes(pdfPath);
        var count = ExtractJpegStreams(bytes, outputDir);
        if (count == 0)
        {
            throw new InvalidOperationException("PDF 中没有可直接提取的 JPEG 图片，暂不能反向转换: " + pdfPath);
        }
    }

    private static int ExtractJpegStreams(byte[] pdfBytes, string outputDir)
    {
        var count = 0;
        var marker = Encoding.ASCII.GetBytes("/DCTDecode");
        var searchIndex = 0;
        while (searchIndex < pdfBytes.Length)
        {
            var markerIndex = IndexOf(pdfBytes, marker, searchIndex);
            if (markerIndex < 0)
            {
                break;
            }

            var streamIndex = IndexOf(pdfBytes, Encoding.ASCII.GetBytes("stream"), markerIndex);
            var endStreamIndex = IndexOf(pdfBytes, Encoding.ASCII.GetBytes("endstream"), markerIndex);
            if (streamIndex < 0 || endStreamIndex < 0 || streamIndex > endStreamIndex)
            {
                searchIndex = markerIndex + marker.Length;
                continue;
            }

            var start = streamIndex + "stream".Length;
            if (start < pdfBytes.Length && pdfBytes[start] == 13)
            {
                start++;
            }
            if (start < pdfBytes.Length && pdfBytes[start] == 10)
            {
                start++;
            }

            var end = endStreamIndex;
            while (end > start && (pdfBytes[end - 1] == 10 || pdfBytes[end - 1] == 13))
            {
                end--;
            }

            if (end > start)
            {
                count++;
                var targetPath = Path.Combine(outputDir, $"{count:D5}.jpg");
                File.WriteAllBytes(targetPath, pdfBytes[start..end]);
            }

            searchIndex = endStreamIndex + "endstream".Length;
        }

        return count;
    }

    private static int IndexOf(byte[] source, byte[] pattern, int startIndex)
    {
        for (var i = Math.Max(0, startIndex); i <= source.Length - pattern.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (source[i + j] != pattern[j])
                {
                    matched = false;
                    break;
                }
            }

            if (matched)
            {
                return i;
            }
        }

        return -1;
    }

    public static void MakePdf(string sourceDir, string outputPath)
    {
        MakePdfFromImages(EnumerateImages(sourceDir), outputPath);
    }

    public static void MakePdfFromImages(List<string> images, string outputPath)
    {
        if (images.Count == 0)
        {
            throw new InvalidOperationException("没有找到可转换为 PDF 的图片");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var tempPath = outputPath + ".tmp";
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath);
        }

        var pdfImages = images.Select(JmImageDecoder.LoadPdfImage).ToList();
        SimplePdfWriter.WriteImagePdf(pdfImages, tempPath);
        ReplaceFile(tempPath, outputPath);
    }

    public static void ReplaceFile(string tempPath, string outputPath)
    {
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        File.Move(tempPath, outputPath);
    }
}

public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();
    private static readonly Regex SplitRegex = new(@"(\d+)", RegexOptions.Compiled);

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var xs = SplitRegex.Split(x.ToLowerInvariant());
        var ys = SplitRegex.Split(y.ToLowerInvariant());
        var count = Math.Min(xs.Length, ys.Length);
        for (var i = 0; i < count; i++)
        {
            var xIsNumber = long.TryParse(xs[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var xn);
            var yIsNumber = long.TryParse(ys[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var yn);

            var cmp = xIsNumber && yIsNumber
                ? xn.CompareTo(yn)
                : string.Compare(xs[i], ys[i], StringComparison.Ordinal);
            if (cmp != 0)
            {
                return cmp;
            }
        }

        return xs.Length.CompareTo(ys.Length);
    }
}

public static class SimplePdfWriter
{
    public static void WriteImagePdf(IReadOnlyList<PdfImageData> images, string outputPath)
    {
        using var stream = File.Create(outputPath);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            NewLine = "\n",
        };

        var offsets = new List<long> { 0 };
        var objectId = 1;
        var pagesId = objectId++;
        var catalogId = objectId++;
        var pageIds = new List<int>();
        var contentIds = new List<int>();
        var imageIds = new List<int>();

        foreach (var _ in images)
        {
            pageIds.Add(objectId++);
            contentIds.Add(objectId++);
            imageIds.Add(objectId++);
        }

        writer.Write("%PDF-1.4\n");
        writer.Flush();

        WriteObject(writer, stream, offsets, catalogId, $"<< /Type /Catalog /Pages {pagesId} 0 R >>");

        var kids = string.Join(" ", pageIds.Select(id => $"{id} 0 R"));
        WriteObject(writer, stream, offsets, pagesId, $"<< /Type /Pages /Kids [{kids}] /Count {images.Count} >>");

        for (var i = 0; i < images.Count; i++)
        {
            var image = images[i];
            var widthPt = image.Width * 72.0 / 96.0;
            var heightPt = image.Height * 72.0 / 96.0;
            var content = FormattableString.Invariant($"q\n{widthPt:0.###} 0 0 {heightPt:0.###} 0 0 cm\n/Im0 Do\nQ\n");
            var contentBytes = Encoding.ASCII.GetBytes(content);

            WriteObject(writer, stream, offsets, pageIds[i],
                FormattableString.Invariant(
                    $"<< /Type /Page /Parent {pagesId} 0 R /MediaBox [0 0 {widthPt:0.###} {heightPt:0.###}] /Resources << /XObject << /Im0 {imageIds[i]} 0 R >> >> /Contents {contentIds[i]} 0 R >>"));

            WriteStreamObject(writer, stream, offsets, contentIds[i], "<< /Length " + contentBytes.Length + " >>", contentBytes);
            WriteStreamObject(
                writer,
                stream,
                offsets,
                imageIds[i],
                $"<< /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {image.JpegBytes.Length} >>",
                image.JpegBytes);
        }

        var xrefOffset = stream.Position;
        writer.Write("xref\n");
        writer.Write($"0 {objectId}\n");
        writer.Write("0000000000 65535 f \n");
        for (var i = 1; i < objectId; i++)
        {
            writer.Write(offsets[i].ToString("D10", CultureInfo.InvariantCulture));
            writer.Write(" 00000 n \n");
        }

        writer.Write("trailer\n");
        writer.Write($"<< /Size {objectId} /Root {catalogId} 0 R >>\n");
        writer.Write("startxref\n");
        writer.Write(xrefOffset.ToString(CultureInfo.InvariantCulture));
        writer.Write("\n%%EOF\n");
        writer.Flush();
    }

    private static void WriteObject(StreamWriter writer, Stream stream, List<long> offsets, int id, string body)
    {
        EnsureOffset(offsets, id);
        writer.Flush();
        offsets[id] = stream.Position;
        writer.Write($"{id} 0 obj\n");
        writer.Write(body);
        writer.Write("\nendobj\n");
        writer.Flush();
    }

    private static void WriteStreamObject(StreamWriter writer, Stream stream, List<long> offsets, int id, string header, byte[] bytes)
    {
        EnsureOffset(offsets, id);
        writer.Flush();
        offsets[id] = stream.Position;
        writer.Write($"{id} 0 obj\n");
        writer.Write(header);
        writer.Write("\nstream\n");
        writer.Flush();
        stream.Write(bytes, 0, bytes.Length);
        writer.Write("\nendstream\nendobj\n");
        writer.Flush();
    }

    private static void EnsureOffset(List<long> offsets, int id)
    {
        while (offsets.Count <= id)
        {
            offsets.Add(0);
        }
    }
}
