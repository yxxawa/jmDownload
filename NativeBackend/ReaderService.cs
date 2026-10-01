using System.Collections.Concurrent;
using System.IO;

namespace DesktopShell.NativeBackend;

public sealed class ReaderService : IDisposable
{
    private readonly JmClient _client;
    private readonly ImageRepository _images;
    public ReaderStore Store { get; }
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly Timer _expiry;
    private sealed class Session(string albumId, CancellationToken lifetime) : IDisposable
    {
        public readonly object Sync = new();
        public readonly string AlbumId = albumId;
        public readonly CancellationTokenSource Cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        public readonly ConcurrentDictionary<string, PhotoDetailDto> Photos = new();
        public CancellationTokenSource? WindowCts;
        public DateTime LastSeen = DateTime.UtcNow;
        public long Revision;
        public string ChapterId = "";
        public int Start, End;
        public void Dispose() { lock (Sync) { Cts.Cancel(); WindowCts?.Cancel(); } }
    }
    public ReaderService(JmClient client, ImageRepository images, string appDataRoot)
    {
        _client = client; _images = images; Store = new ReaderStore(appDataRoot);
        _images.SetBudget(Store.Settings.CacheMb);
        _expiry = new Timer(_ => { foreach (var (id, s) in _sessions) if (DateTime.UtcNow - s.LastSeen > TimeSpan.FromMinutes(45)) Close(id); }, null, 60000, 60000);
    }
    public async Task<ReaderBook> BookAsync(string id, CancellationToken token)
    {
        id = JmClient.ParseJmId(id);
        var saved = Store.Get(id);
        if (saved is not null && CompleteLocal(saved)) return saved;
        try { return Store.Remember(await _client.GetAlbumDetailAsync(id, token).ConfigureAwait(false)); }
        catch (Exception e) when (saved is not null && e is not OperationCanceledException) { saved.Local = CompleteLocal(saved); return saved; }
    }
    private static bool CompleteLocal(ReaderBook book) => book.Local && book.Chapters.Count > 0 &&
        book.Chapters.All(c => book.LocalPages.TryGetValue(c.Id, out var pages) && pages.Count > 0 && pages.All(ValidFile));
    private static bool ValidFile(string path) { try { return new FileInfo(path) is { Exists: true, Length: > 0 }; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; } }
    public object Shelf() => new { books = Store.Shelf().Select(b => { b.Local = CompleteLocal(Store.Get(b.Id)!); return b; }).ToList(), bookmarks = Store.Bookmarks(), settings = Store.Settings };
    public async Task<object> OpenAsync(string albumId, CancellationToken token)
    {
        var book = await BookAsync(albumId, token).ConfigureAwait(false);
        if (_sessions.Count >= 16) throw new InvalidOperationException("阅读会话过多，请先关闭旧会话");
        var id = Guid.NewGuid().ToString("N");
        _sessions[id] = new Session(book.Id, _stop.Token);
        return new { session_id = id, book, settings = Store.Settings, bookmarks = Store.Bookmarks(book.Id) };
    }
    private Session Get(string id)
    {
        if (!_sessions.TryGetValue(id, out var session) || session.Cts.IsCancellationRequested) throw new KeyNotFoundException("阅读会话已关闭，请重新打开作品");
        session.LastSeen = DateTime.UtcNow;
        return session;
    }
    public async Task<ReaderChapterManifest> ChapterAsync(string sessionId, string chapterId, CancellationToken token)
    {
        var session = Get(sessionId);
        chapterId = JmClient.ParseJmId(chapterId);
        var book = Store.Get(session.AlbumId) ?? throw new KeyNotFoundException("作品不存在");
        var chapter = book.Chapters.Find(c => c.Id == chapterId) ?? throw new ArgumentException("章节不属于当前作品");
        bool local = book.LocalPages.TryGetValue(chapterId, out var paths) && paths.Count > 0 && paths.All(ValidFile);
        int count;
        if (local) count = paths!.Count;
        else
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, session.Cts.Token);
            var photo = await _client.GetPhotoDetailAsync(chapterId, null, true, linked.Token).ConfigureAwait(false);
            if (photo.Images.Count == 0) throw new IOException("章节没有图片");
            session.Photos[chapterId] = photo;
            count = photo.Images.Count;
        }
        return new ReaderChapterManifest { Id = chapterId, AlbumId = book.Id, Title = chapter.Title, PageCount = count, Local = local,
            Pages = Enumerable.Range(0, count).Select(i => new ReaderPageInfo { Index = i, Url = $"/api/reader/images/{chapterId}/{i}?session={sessionId}" }).ToList() };
    }
    public async Task<string> ImageAsync(string sessionId, string chapterId, int index, CancellationToken token)
    {
        var session = Get(sessionId);
        var book = Store.Get(session.AlbumId)!;
        if (!book.Chapters.Any(c => c.Id == chapterId)) throw new ArgumentException("章节不属于当前作品");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, session.Cts.Token);
        if (book.LocalPages.TryGetValue(chapterId, out var paths) && index >= 0 && index < paths.Count && ValidFile(paths[index])) return paths[index];
        if (!session.Photos.TryGetValue(chapterId, out var photo)) throw new ArgumentException("请先加载章节目录");
        if (index < 0 || index >= photo.Images.Count) throw new ArgumentOutOfRangeException(nameof(index));
        int priority;
        lock (session.Sync) priority = chapterId == session.ChapterId && index >= session.Start && index <= session.End ? 0 : 1;
        return await _images.GetFileAsync(photo, index, null, priority, linked.Token).ConfigureAwait(false);
    }
    public void Window(string id, string chapterId, int start, int end, long revision)
    {
        var session = Get(id);
        var book = Store.Get(session.AlbumId)!;
        if (!book.Chapters.Any(c => c.Id == chapterId)) throw new ArgumentException("章节不属于当前作品");
        PhotoDetailDto? photo; CancellationToken ct;
        lock (session.Sync)
        {
            if (revision <= session.Revision) return;
            session.Revision = revision;
            session.WindowCts?.Cancel();
            var previous = session.WindowCts;
            session.WindowCts = CancellationTokenSource.CreateLinkedTokenSource(session.Cts.Token);
            ct = session.WindowCts.Token;
            previous?.Dispose();
            session.ChapterId = chapterId;
            session.Start = Math.Max(0, start); session.End = Math.Clamp(end, session.Start, session.Start + 5);
            session.Photos.TryGetValue(chapterId, out photo);
        }
        if (photo is null) return; // Completed local chapter needs no network/prefetch.
        var settings = Store.Settings;
        int first = Math.Max(0, start - 1), last = Math.Min(photo.Images.Count - 1, end + settings.Prefetch);
        _ = Observe(PrefetchAsync());
        async Task PrefetchAsync()
        {
            var indexes = Enumerable.Range(first, Math.Max(0, last - first + 1)).OrderBy(i => Math.Abs(i - start));
            await Parallel.ForEachAsync(indexes, new ParallelOptions { MaxDegreeOfParallelism = 3, CancellationToken = ct },
                async (i, t) => { try { await _images.GetFileAsync(photo, i, null, i >= start && i <= end ? 0 : 1, t).ConfigureAwait(false); } catch (Exception e) when (e is not OperationCanceledException) { } }).ConfigureAwait(false);
            // Only warm the next chapter metadata near the end. Never fetch an entire following book.
            if (end >= photo.Images.Count - 3 && !ct.IsCancellationRequested)
            {
                int ci = book.Chapters.FindIndex(c => c.Id == chapterId);
                if (ci >= 0 && ci + 1 < book.Chapters.Count) await ChapterAsync(id, book.Chapters[ci + 1].Id, ct).ConfigureAwait(false);
            }
        }
    }
    private static async Task Observe(Task task) { try { await task.ConfigureAwait(false); } catch { } }
    public ReaderSettings Settings(ReaderSettings value) { var settings = Store.SetSettings(value); _images.SetBudget(settings.CacheMb); return settings; }
    public ReaderBook Progress(string id, ReaderProgress progress)
    {
        var book = Store.Get(id) ?? throw new KeyNotFoundException("作品尚未打开");
        if (book.LocalPages.TryGetValue(progress.ChapterId, out var local)) progress.Page = Math.Clamp(progress.Page, 0, Math.Max(0, local.Count - 1));
        else
        {
            var photo = _sessions.Values.Where(s => s.AlbumId == id).Select(s => s.Photos.GetValueOrDefault(progress.ChapterId)).FirstOrDefault(p => p is not null);
            if (photo is not null) progress.Page = Math.Clamp(progress.Page, 0, Math.Max(0, photo.Images.Count - 1));
            else progress.Page = Math.Clamp(progress.Page, 0, 10000);
        }
        return Store.Progress(id, progress);
    }
    public bool Close(string id) { if (!_sessions.TryRemove(id, out var session)) return false; session.Dispose(); return true; }
    public void Dispose() { _expiry.Dispose(); _stop.Cancel(); foreach (var id in _sessions.Keys) Close(id); }
}
