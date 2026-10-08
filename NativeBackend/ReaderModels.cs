using System.Text.Json.Serialization;
namespace DesktopShell.NativeBackend;
public sealed class ReaderChapterInfo {
 [JsonPropertyName("id")] public string Id { get; set; }="";
 [JsonPropertyName("title")] public string Title { get; set; }="";
 [JsonPropertyName("sort")] public int Sort { get; set; }
}
public sealed class ReaderBook {
 [JsonPropertyName("id")] public string Id {get;set;}="";
 [JsonPropertyName("title")] public string Title {get;set;}="";
 [JsonPropertyName("author")] public string? Author {get;set;}
 [JsonPropertyName("chapters")] public List<ReaderChapterInfo> Chapters {get;set;}=[];
 [JsonPropertyName("progress")] public ReaderProgress? Progress {get;set;}
 [JsonPropertyName("favorite")] public bool Favorite {get;set;}
 [JsonPropertyName("hidden_from_recent")] public bool HiddenFromRecent {get;set;}
 [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt {get;set;}
 [JsonPropertyName("local")] public bool Local {get;set;}
 [JsonIgnore] public Dictionary<string,List<string>> LocalPages {get;set;}=[];
}
public sealed class ReaderProgress {
 [JsonPropertyName("chapter_id")] public string ChapterId {get;set;}="";
 [JsonPropertyName("page")] public int Page {get;set;}
 [JsonPropertyName("offset")] public double Offset {get;set;}
 [JsonPropertyName("revision")] public long Revision {get;set;}
 [JsonPropertyName("updated_at")] public DateTimeOffset UpdatedAt {get;set;}
}
public sealed class ReaderBookmark {
 [JsonPropertyName("album_id")] public string AlbumId {get;set;}="";
 [JsonPropertyName("chapter_id")] public string ChapterId {get;set;}="";
 [JsonPropertyName("page")] public int Page {get;set;}
 [JsonPropertyName("created_at")] public DateTimeOffset CreatedAt {get;set;}
}
public sealed class ReaderSettings {
 [JsonPropertyName("mode")] public string Mode {get;set;}="vertical";
 [JsonPropertyName("fit")] public string Fit {get;set;}="comfortable";
 [JsonPropertyName("direction")] public string Direction {get;set;}="ltr";
 [JsonPropertyName("prefetch")] public int Prefetch {get;set;}=3;
 [JsonPropertyName("cache_mb")] public int CacheMb {get;set;}=512;
 [JsonPropertyName("theme")] public string Theme {get;set;}="system";
}
public sealed class ReaderChapterManifest {
 [JsonPropertyName("id")] public string Id {get;set;}="";
 [JsonPropertyName("album_id")] public string AlbumId {get;set;}="";
 [JsonPropertyName("title")] public string Title {get;set;}="";
 [JsonPropertyName("page_count")] public int PageCount {get;set;}
 [JsonPropertyName("pages")] public List<ReaderPageInfo> Pages {get;set;}=[];
 [JsonPropertyName("local")] public bool Local {get;set;}
}
public sealed class ReaderPageInfo {
 [JsonPropertyName("index")] public int Index {get;set;}
 [JsonPropertyName("url")] public string Url {get;set;}="";
}

public sealed class ReaderCollectionRequest {
 [JsonPropertyName("action")] public string Action {get;set;}="";
 [JsonPropertyName("ids")] public List<string> Ids {get;set;}=[];
 [JsonPropertyName("bookmarks")] public List<ReaderBookmark> Bookmarks {get;set;}=[];
 [JsonPropertyName("favorite")] public bool Favorite {get;set;}=true;
}
