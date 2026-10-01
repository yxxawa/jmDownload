using System.IO;
using System.Text.Json;

namespace DesktopShell.NativeBackend;

public sealed class ReaderStore
{
    private readonly object _lock = new();
    private readonly string _path;
    private State _state;

    public sealed class State
    {
        public ReaderSettings Settings { get; set; } = new();
        public Dictionary<string, ReaderBook> Books { get; set; } = [];
        public List<ReaderBookmark> Bookmarks { get; set; } = [];
        public Dictionary<string, Dictionary<string, List<string>>> LocalPages { get; set; } = [];
    }

    public ReaderStore(string appDataRoot)
    {
        _path = Path.Combine(appDataRoot, "reader", "state.json");

        try
        {
            _state = JsonSerializer.Deserialize<State>(File.ReadAllText(_path), NativeJson.JsonOptions) ?? new();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            _state = new();
        }
    }

    private T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, NativeJson.ApiOptions), NativeJson.ApiOptions)!;

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(_state, NativeJson.JsonOptions));
        File.Move(tempPath, _path, overwrite: true);
    }

    public ReaderSettings Settings
    {
        get
        {
            lock (_lock)
            {
                return Clone(_state.Settings);
            }
        }
    }

    public ReaderSettings SetSettings(ReaderSettings value)
    {
        lock (_lock)
        {
            value.Mode = value.Mode is "single" or "spread" ? value.Mode : "vertical";
            value.Fit = value.Fit is "width" or "height" or "original" ? value.Fit : "comfortable";
            value.Direction = value.Direction == "rtl" ? "rtl" : "ltr";
            value.Prefetch = Math.Clamp(value.Prefetch, 0, 8);
            value.CacheMb = Math.Clamp(value.CacheMb, 128, 4096);
            value.Theme = value.Theme is "light" or "dark" ? value.Theme : "system";

            _state.Settings = value;
            Save();
            return Clone(value);
        }
    }

    public ReaderBook? Get(string id)
    {
        lock (_lock)
        {
            if (!_state.Books.TryGetValue(id, out var book))
            {
                return null;
            }

            var result = Clone(book);
            if (_state.LocalPages.TryGetValue(id, out var pages))
            {
                result.LocalPages = Clone(pages);
            }

            return result;
        }
    }

    public ReaderBook Remember(AlbumDetailDto album)
    {
        lock (_lock)
        {
            _state.Books.TryGetValue(album.Id, out var old);
            var book = new ReaderBook
            {
                Id = album.Id,
                Title = album.Title,
                Author = album.Author,
                Chapters = album.Chapters
                    .Select(chapter => new ReaderChapterInfo
                    {
                        Id = chapter.Id,
                        Title = chapter.Title,
                        Sort = chapter.Sort,
                    })
                    .OrderBy(chapter => chapter.Sort)
                    .ToList(),
                Progress = old?.Progress,
                Favorite = old?.Favorite ?? false,
                UpdatedAt = old?.UpdatedAt ?? DateTimeOffset.UtcNow,
                Local = old?.Local ?? false,
            };

            _state.Books[book.Id] = book;
            if (_state.Books.Count > 500)
            {
                var removableIds = _state.Books.Values
                    .Where(item => !item.Favorite && !item.Local)
                    .OrderBy(item => item.UpdatedAt)
                    .Take(_state.Books.Count - 500)
                    .Select(item => item.Id)
                    .ToList();

                foreach (var id in removableIds)
                {
                    _state.Books.Remove(id);
                }
            }

            Save();
            return Clone(book);
        }
    }

    public ReaderBook Progress(string id, ReaderProgress value)
    {
        lock (_lock)
        {
            if (!_state.Books.TryGetValue(id, out var book))
            {
                throw new KeyNotFoundException("作品未加入阅读记录");
            }

            if (!book.Chapters.Any(chapter => chapter.Id == value.ChapterId))
            {
                throw new ArgumentException("章节不属于当前作品");
            }

            if (value.Revision > 0 && book.Progress is { Revision: > 0 } old && old.Revision > value.Revision)
            {
                return Clone(book);
            }

            value.Page = Math.Max(0, value.Page);
            value.Offset = Math.Clamp(value.Offset, 0, 1);
            value.UpdatedAt = DateTimeOffset.UtcNow;
            book.Progress = value;
            book.UpdatedAt = value.UpdatedAt;
            Save();
            return Clone(book);
        }
    }

    public ReaderBook Favorite(string id, bool value)
    {
        lock (_lock)
        {
            if (!_state.Books.TryGetValue(id, out var book))
            {
                throw new KeyNotFoundException("作品尚未打开");
            }

            book.Favorite = value;
            Save();
            return Clone(book);
        }
    }

    public List<ReaderBook> Shelf()
    {
        lock (_lock)
        {
            return Clone(_state.Books.Values.OrderByDescending(book => book.UpdatedAt).Take(500).ToList());
        }
    }

    public List<ReaderBookmark> Bookmarks(string? id = null)
    {
        lock (_lock)
        {
            return Clone(_state.Bookmarks.Where(bookmark => id == null || bookmark.AlbumId == id).ToList());
        }
    }

    public bool ToggleBookmark(ReaderBookmark value)
    {
        lock (_lock)
        {
            if (!_state.Books.TryGetValue(value.AlbumId, out var book)
                || !book.Chapters.Any(chapter => chapter.Id == value.ChapterId)
                || value.Page < 0)
            {
                throw new ArgumentException("书签位置无效");
            }

            var existing = _state.Bookmarks.Find(bookmark =>
                bookmark.AlbumId == value.AlbumId
                && bookmark.ChapterId == value.ChapterId
                && bookmark.Page == value.Page);

            if (existing is not null)
            {
                _state.Bookmarks.Remove(existing);
                Save();
                return false;
            }

            value.CreatedAt = DateTimeOffset.UtcNow;
            _state.Bookmarks.Add(value);
            Save();
            return true;
        }
    }

    public void RegisterLocal(AlbumDetailDto album, Dictionary<string, List<string>> pages)
    {
        lock (_lock)
        {
            var book = Remember(album);
            book.Local = true;
            _state.Books[album.Id] = book;
            _state.LocalPages[album.Id] = pages;
            Save();
        }
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            _state.Books.Remove(id);
            _state.LocalPages.Remove(id);
            _state.Bookmarks.RemoveAll(bookmark => bookmark.AlbumId == id);
            Save();
        }
    }
}