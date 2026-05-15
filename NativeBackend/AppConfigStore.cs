using System.IO;
using System.Text.Json;

namespace DesktopShell.NativeBackend;

public sealed class AppConfigStore
{
    private static readonly HashSet<string> ImageFormats = ["原始格式", ".png", ".jpg"];
    private static readonly HashSet<string> OutputFormats = ["images", "zip", "pdf"];
    private static readonly HashSet<string> PdfModes = ["merged", "chapters"];
    private readonly object _lock = new();

    public AppConfigStore(string appDataRoot)
    {
        AppDataRoot = appDataRoot;
        ConfigPath = Path.Combine(AppDataRoot, "config.json");
        DefaultDownloadDir = Path.Combine(AppContext.BaseDirectory, "JMDownLoad");
        LegacyDefaultDownloadDir = Path.Combine(AppDataRoot, "JMDownLoad");
    }

    public string AppDataRoot { get; }
    public string ConfigPath { get; }
    public string DefaultDownloadDir { get; }
    public string LegacyDefaultDownloadDir { get; }

    public AppConfigDto Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var config = JsonSerializer.Deserialize<AppConfigDto>(
                        File.ReadAllText(ConfigPath),
                        NativeJson.JsonOptions);
                    return Normalize(config);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // Bad config should not stop application startup.
                _ = ex;
            }

            return DefaultConfig();
        }
    }

    public AppConfigDto Save(AppConfigDto config)
    {
        lock (_lock)
        {
            var normalized = Normalize(config);
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            var tempPath = ConfigPath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(normalized, NativeJson.JsonOptions) + Environment.NewLine);
            File.Move(tempPath, ConfigPath, overwrite: true);
            return normalized;
        }
    }

    public AppConfigDto Normalize(AppConfigDto? config)
    {
        var defaults = DefaultConfig();
        config ??= defaults;

        var baseDir = string.IsNullOrWhiteSpace(config.BaseDir)
            ? defaults.BaseDir
            : config.BaseDir.Trim();

        if (IsLegacyDefaultDownloadDir(baseDir))
        {
            baseDir = defaults.BaseDir;
        }
        else if (IsPreviousExeDefaultDownloadDir(baseDir, config.DefaultBaseDir))
        {
            baseDir = defaults.BaseDir;
        }
        else if (IsGeneratedExeDefaultDownloadDir(baseDir))
        {
            baseDir = defaults.BaseDir;
        }

        return new AppConfigDto
        {
            BaseDir = baseDir,
            ImageFormat = NormalizeImageFormat(config.ImageFormat),
            OutputFormat = NormalizeOutputFormat(config.OutputFormat),
            PdfMode = NormalizePdfMode(config.PdfMode),
            PhotoThreads = Clamp(config.PhotoThreads, 1, 5, defaults.PhotoThreads),
            ImageThreads = Clamp(config.ImageThreads, 1, 20, defaults.ImageThreads),
            AlbumThreads = Clamp(config.AlbumThreads, 1, 8, defaults.AlbumThreads),
            FilenameLang = config.FilenameLang == "simplified" ? "simplified" : "traditional",
            AutoPath = config.AutoPath,
            DefaultBaseDir = defaults.BaseDir,
        };
    }

    public DownloadSettings ToSettings(AppConfigDto config, string itemId)
    {
        var normalized = Normalize(config);
        var baseDir = normalized.BaseDir;
        if (normalized.AutoPath)
        {
            var pathId = itemId.StartsWith("p", StringComparison.OrdinalIgnoreCase)
                ? itemId[1..]
                : itemId;
            baseDir = Path.Combine(baseDir, pathId);
        }

        return new DownloadSettings
        {
            BaseDir = Path.GetFullPath(baseDir),
            ImageSuffix = normalized.ImageFormat == "原始格式" ? null : normalized.ImageFormat,
            OutputFormat = normalized.OutputFormat,
            PdfMode = normalized.PdfMode,
            PhotoThreads = normalized.PhotoThreads,
            ImageThreads = normalized.ImageThreads,
            FilenameLang = normalized.FilenameLang,
        };
    }

    private AppConfigDto DefaultConfig() => new()
    {
        BaseDir = DefaultDownloadDir,
        ImageFormat = ".png",
        OutputFormat = "images",
        PdfMode = "merged",
        PhotoThreads = 1,
        ImageThreads = 5,
        AlbumThreads = 1,
        FilenameLang = "traditional",
        AutoPath = true,
        DefaultBaseDir = DefaultDownloadDir,
    };

    private bool IsLegacyDefaultDownloadDir(string path)
    {
        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Equals(
                    Path.GetFullPath(LegacyDefaultDownloadDir)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool IsPreviousExeDefaultDownloadDir(string path, string? recordedDefaultBaseDir)
    {
        if (string.IsNullOrWhiteSpace(recordedDefaultBaseDir))
        {
            return false;
        }

        try
        {
            var current = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var recorded = Path.GetFullPath(recordedDefaultBaseDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return current.Equals(recorded, StringComparison.OrdinalIgnoreCase)
                   && !current.Equals(
                       Path.GetFullPath(DefaultDownloadDir)
                           .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private bool IsGeneratedExeDefaultDownloadDir(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var currentDefault = Path.GetFullPath(DefaultDownloadDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (fullPath.Equals(currentDefault, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!Path.GetFileName(fullPath).Equals("JMDownLoad", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var parent = Path.GetDirectoryName(fullPath);
            return !string.IsNullOrWhiteSpace(parent)
                   && File.Exists(Path.Combine(parent, "DesktopShell.exe"));
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeImageFormat(string? value)
    {
        var imageFormat = string.IsNullOrWhiteSpace(value) ? ".png" : value.Trim();
        if (imageFormat != "原始格式" && !imageFormat.StartsWith('.'))
        {
            imageFormat = "." + imageFormat;
        }

        return ImageFormats.Contains(imageFormat) ? imageFormat : ".png";
    }

    private static string NormalizeOutputFormat(string? value)
    {
        var outputFormat = string.IsNullOrWhiteSpace(value) ? "images" : value.Trim().ToLowerInvariant();
        return OutputFormats.Contains(outputFormat) ? outputFormat : "images";
    }

    private static string NormalizePdfMode(string? value)
    {
        var pdfMode = string.IsNullOrWhiteSpace(value) ? "merged" : value.Trim().ToLowerInvariant();
        return PdfModes.Contains(pdfMode) ? pdfMode : "merged";
    }

    private static int Clamp(int value, int min, int max, int fallback)
    {
        if (value <= 0)
        {
            value = fallback;
        }

        return Math.Max(min, Math.Min(max, value));
    }
}

internal static class NativeJson
{
    // Used for config file on disk (indented for readability)
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = true,
    };

    // Used for HTTP/WebSocket responses (compact)
    public static readonly JsonSerializerOptions ApiOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };
}
