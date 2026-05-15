using System.Text.Json.Serialization;

namespace DesktopShell.NativeBackend;

public sealed class AlbumItemDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("rank")]
    public int? Rank { get; set; }
}

public sealed class AlbumDetailDto
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("tags")]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("page_count")]
    public int? PageCount { get; set; }

    [JsonIgnore]
    public List<ChapterDto> Chapters { get; set; } = [];
}

public sealed class ChapterDto
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int Sort { get; set; } = 1;
}

public sealed class PhotoDetailDto
{
    public string Id { get; set; } = string.Empty;
    public string AlbumId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public int Sort { get; set; } = 1;
    public string ScrambleId { get; set; } = string.Empty;
    public string ImageDomain { get; set; } = string.Empty;
    public List<string> Images { get; set; } = [];
}

public class AppConfigDto
{
    [JsonPropertyName("base_dir")]
    public string BaseDir { get; set; } = string.Empty;

    [JsonPropertyName("image_format")]
    public string ImageFormat { get; set; } = ".png";

    [JsonPropertyName("output_format")]
    public string OutputFormat { get; set; } = "images";

    [JsonPropertyName("pdf_mode")]
    public string PdfMode { get; set; } = "merged";

    [JsonPropertyName("photo_threads")]
    public int PhotoThreads { get; set; } = 1;

    [JsonPropertyName("image_threads")]
    public int ImageThreads { get; set; } = 5;

    [JsonPropertyName("album_threads")]
    public int AlbumThreads { get; set; } = 1;

    [JsonPropertyName("filename_lang")]
    public string FilenameLang { get; set; } = "traditional"; // "traditional" | "simplified"

    [JsonPropertyName("auto_path")]
    public bool AutoPath { get; set; } = true;

    [JsonPropertyName("default_base_dir")]
    public string DefaultBaseDir { get; set; } = string.Empty;
}

public sealed class DownloadRequestDto : AppConfigDto
{
    [JsonPropertyName("ids")]
    public List<string> Ids { get; set; } = [];
}

public sealed class DownloadSettings
{
    public string BaseDir { get; set; } = string.Empty;
    public string? ImageSuffix { get; set; } = ".png";
    public string OutputFormat { get; set; } = "images";
    public string PdfMode { get; set; } = "merged";
    public int PhotoThreads { get; set; } = 1;
    public int ImageThreads { get; set; } = 5;
    public string FilenameLang { get; set; } = "traditional";
}

public sealed class DownloadJob
{
    public string ItemId { get; set; } = string.Empty;
    public DownloadSettings Settings { get; set; } = new();
}

public sealed class DownloadTaskState
{
    [JsonPropertyName("item_id")]
    public string ItemId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "queued";

    [JsonPropertyName("base_dir")]
    public string BaseDir { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("progress")]
    public int Progress { get; set; }

    [JsonPropertyName("total")]
    public int Total { get; set; }

    [JsonPropertyName("detail")]
    public string Detail { get; set; } = string.Empty;
}

public sealed class DownloadSnapshot
{
    [JsonPropertyName("running")]
    public bool Running { get; set; }

    [JsonPropertyName("stopping")]
    public bool Stopping { get; set; }

    [JsonPropertyName("current_item_id")]
    public string? CurrentItemId { get; set; }

    [JsonPropertyName("last_failed_ids")]
    public List<string> LastFailedIds { get; set; } = [];

    [JsonPropertyName("last_success_ids")]
    public List<string> LastSuccessIds { get; set; } = [];

    [JsonPropertyName("last_stopped")]
    public bool LastStopped { get; set; }

    [JsonPropertyName("tasks")]
    public List<DownloadTaskState> Tasks { get; set; } = [];
}

public sealed class DownloadEventDto
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "log";

    [JsonPropertyName("level")]
    public string Level { get; set; } = "INFO";

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("item_id")]
    public string? ItemId { get; set; }

    [JsonPropertyName("data")]
    public Dictionary<string, object?> Data { get; set; } = [];
}
