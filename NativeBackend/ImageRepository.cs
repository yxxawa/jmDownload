using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DesktopShell.NativeBackend;

// A single network budget for reader and downloader. Six background slots, two reserved foreground slots.
public sealed class ImagePriorityGate
{
    private readonly object _sync = new();
    private readonly List<Waiter> _queue = [];
    private int _active, _background;
    private readonly int _limit, _backgroundLimit;
    private sealed record Waiter(Func<int> Priority, CancellationToken Token, TaskCompletionSource<IDisposable> Source);
    public ImagePriorityGate(int limit = 8, int foregroundReserve = 2)
    { _limit = Math.Max(2, limit); _backgroundLimit = Math.Clamp(_limit - foregroundReserve, 1, _limit); }
    public async Task<IDisposable> EnterAsync(Func<int> priority, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var waiter = new Waiter(priority, token, new(TaskCreationOptions.RunContinuationsAsynchronously));
        lock (_sync) { _queue.Add(waiter); Pump(); }
        using var registration = token.Register(() => { lock (_sync) { if (_queue.Remove(waiter)) waiter.Source.TrySetCanceled(token); Pump(); } });
        return await waiter.Source.Task.ConfigureAwait(false);
    }
    public void Wake() { lock (_sync) Pump(); }
    private void Pump()
    {
        for (int i = _queue.Count - 1; i >= 0; i--) if (_queue[i].Token.IsCancellationRequested)
        { _queue[i].Source.TrySetCanceled(_queue[i].Token); _queue.RemoveAt(i); }
        while (_active < _limit)
        {
            var next = _queue.Where(w => w.Priority() == 0 || _background < _backgroundLimit).OrderBy(w => w.Priority()).FirstOrDefault();
            if (next is null) return;
            _queue.Remove(next);
            bool background = next.Priority() != 0;
            _active++; if (background) _background++;
            next.Source.TrySetResult(new Lease(this, background));
        }
    }
    private void Exit(bool background) { lock (_sync) { _active--; if (background) _background--; Pump(); } }
    private sealed class Lease(ImagePriorityGate owner, bool background) : IDisposable
    { private int _released; public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) owner.Exit(background); } }
}

public sealed class ImageRepository : IDisposable
{
    private readonly JmClient _client;
    private readonly string _root;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _decode = new(2, 2);
    private readonly ImagePriorityGate _gate;
    private readonly object _sync = new(), _files = new();
    private readonly Dictionary<string, Job> _jobs = [];
    private readonly Dictionary<string, int> _pins = [];
    private readonly Timer _sweep;
    private long _budget = 512L * 1024 * 1024, _hits, _misses;
    private sealed class Job(CancellationToken lifetime)
    {
        public readonly CancellationTokenSource Cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        public Task<string> Task = null!;
        public int Readers, Priority = 2;
    }
    public ImageRepository(JmClient client, string appDataRoot)
    {
        _client = client; _gate = client.ImageGate; _root = StorageAccess.ImageCacheDirectory(appDataRoot);
        _sweep = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public void SetBudget(int mb) { Interlocked.Exchange(ref _budget, Math.Clamp(mb, 128, 4096) * 1024L * 1024L); Sweep(); }
    private async Task<string> Shared(string key, int priority, CancellationToken token, Func<Job, CancellationToken, Task<string>> factory)
    {
        token.ThrowIfCancellationRequested();
        Job job;
        lock (_sync)
        {
            if (!_jobs.TryGetValue(key, out job!))
            {
                job = new Job(_stop.Token) { Priority = priority };
                var captured = job;
                _jobs[key] = job;
                job.Task = Task.Run(() => factory(captured, captured.Cts.Token));
                _ = Observe(job.Task);
            }
            job.Readers++;
            Volatile.Write(ref job.Priority, Math.Min(job.Priority, priority));
        }
        _gate.Wake();
        try { return await job.Task.WaitAsync(token).ConfigureAwait(false); }
        finally
        {
            lock (_sync)
            {
                if (--job.Readers == 0)
                {
                    if (_jobs.TryGetValue(key, out var current) && ReferenceEquals(current, job)) _jobs.Remove(key);
                    if (!job.Task.IsCompleted) job.Cts.Cancel();
                    // Disposal occurs only after the actual job stops, not when a waiter abandons it.
                    _ = job.Task.ContinueWith(_ => job.Cts.Dispose(), TaskScheduler.Default);
                }
            }
        }
    }
    private static async Task Observe(Task task) { try { await task.ConfigureAwait(false); } catch { } }
    private static bool Valid(string file) { try { return new FileInfo(file) is { Exists: true, Length: > 0 }; } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; } }
    private static void Touch(string file) { try { File.SetLastAccessTimeUtc(file, DateTime.UtcNow); } catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { } }
    public static string OutputSuffix(PhotoDetailDto photo, int index, string? suffix)
    {
        var source = Path.GetExtension(photo.Images[index].Split('?')[0]).ToLowerInvariant();
        var ext = source == ".gif" ? ".gif" : suffix ?? source;
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp")) ext = ".png";
        var segments = JmImageDecoder.GetSegmentCount(photo.ScrambleId, photo.Id, Path.GetFileNameWithoutExtension(photo.Images[index].Split('?')[0]));
        return ext == ".webp" && segments > 0 ? ".png" : ext;
    }
    public async Task<string> GetFileAsync(PhotoDetailDto photo, int index, string? suffix, int priority, CancellationToken token)
    {
        if (index < 0 || index >= photo.Images.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var name = photo.Images[index];
        var ext = OutputSuffix(photo, index, suffix);
        var sourceExt = Path.GetExtension(name.Split('?')[0]).ToLowerInvariant();
        var segments = JmImageDecoder.GetSegmentCount(photo.ScrambleId, photo.Id, Path.GetFileNameWithoutExtension(name.Split('?')[0]));
        var key = Hash($"decode-v3|{photo.Id}|{name}|{photo.ScrambleId}|{ext}");
        var file = Path.Combine(_root, key + ext);
        token.ThrowIfCancellationRequested();
        if (Valid(file)) { Interlocked.Increment(ref _hits); Touch(file); return file; }
        return await Shared(key, priority, token, async (job, ct) =>
        {
            if (Valid(file)) { Touch(file); return file; }
            Interlocked.Increment(ref _misses);
            var rawKey = Hash($"source-v2|{photo.Id}|{name}");
            var raw = Path.Combine(_root, rawKey + ".raw");
            using var sourcePin = Pin(rawKey);
            if (!Valid(raw)) await Shared(rawKey, job.Priority, ct, async (rawJob, rawCt) =>
            {
                if (Valid(raw)) return raw;
                var bytes = await _client.GetImageBytesAsync(photo, name, rawCt, () => Math.Min(Volatile.Read(ref job.Priority), Volatile.Read(ref rawJob.Priority))).ConfigureAwait(false);
                await Atomic(raw, bytes, rawCt).ConfigureAwait(false);
                return raw;
            }).ConfigureAwait(false);
            // Acquire CPU capacity before reading the full compressed image into memory.
            // Queueing workers retain disk paths, not dozens of large byte arrays.
            await _decode.WaitAsync(ct).ConfigureAwait(false);
            var tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp" + ext;
            try
            {
                byte[] data;
                using (var input = OpenRead(raw)) { using var output = new MemoryStream(); await input.CopyToAsync(output, ct).ConfigureAwait(false); data = output.ToArray(); }
                Touch(raw);
                if (sourceExt == ".gif" || (segments == 0 && ext == sourceExt))
                {
                    if (!await Task.Run(() => JmImageDecoder.IsUsableBytes(data, sourceExt), ct).ConfigureAwait(false)) throw new IOException("\u56fe\u7247\u6b63\u6587\u635f\u574f");
                    await Atomic(file, data, ct).ConfigureAwait(false);
                }
                else
                {
                    await Task.Run(() => JmImageDecoder.DecodeAndSave(data, segments, tmp), ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    lock (_files) File.Move(tmp, file, true);
                }
                Touch(file);
                return file;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            { try { lock (_files) File.Delete(raw); } catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { } throw new IOException("图片处理失败：" + name + "。具体原因：" + e.Message, e); }
            finally { _decode.Release(); try { File.Delete(tmp); } catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { } }
        }).ConfigureAwait(false);
    }
    private IDisposable Pin(string key)
    {
        lock (_sync) _pins[key] = _pins.GetValueOrDefault(key) + 1;
        return new PinLease(this, key);
    }
    private sealed class PinLease(ImageRepository owner, string key) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            lock (owner._sync) { if (owner._pins[key] <= 1) owner._pins.Remove(key); else owner._pins[key]--; }
        }
    }
    private async Task Atomic(string file, byte[] bytes, CancellationToken token)
    {
        var tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllBytesAsync(tmp, bytes, token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); lock (_files) File.Move(tmp, file, true); }
        finally { try { File.Delete(tmp); } catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { } }
    }
    public FileStream OpenRead(string file)
    { lock (_files) return new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan); }
    public async Task CopyToAsync(string cachedFile, string destination, CancellationToken token)
    {
        var tmp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = OpenRead(cachedFile))
            await using (var output = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await input.CopyToAsync(output, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); File.Move(tmp, destination, true);
        }
        catch (UnauthorizedAccessException e) { throw new StorageAccessException("下载目录无法写入：" + Path.GetDirectoryName(destination) + "。请检查目录权限，或在安卓上授予文件访问权限。", e); }
        finally { try { File.Delete(tmp); } catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { } }
    }
    public object Stats()
    {
        lock (_files)
        {
            var files = new DirectoryInfo(_root).EnumerateFiles().Where(f => !f.Name.Contains(".tmp")).ToList();
            return new { bytes = files.Sum(f => f.Length), files = files.Count, budget_bytes = Interlocked.Read(ref _budget), hits = Interlocked.Read(ref _hits), misses = Interlocked.Read(ref _misses) };
        }
    }
    public int Clear() => Prune(true);
    private void Sweep() { try { Prune(false); } catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { } }
    private int Prune(bool clear)
    {
        // Keep lock order consistent with Shared; never hold _files while waiting on _sync.
        lock (_sync) lock (_files)
        {
            var files = new DirectoryInfo(_root).EnumerateFiles().Where(f => !f.Name.Contains(".tmp")).OrderBy(f => f.LastAccessTimeUtc).ToList();
            long size = files.Sum(f => f.Length); int removed = 0;
            foreach (var file in files)
            {
                if (!clear && size <= Interlocked.Read(ref _budget)) break;
                if (_jobs.Keys.Concat(_pins.Keys).Any(k => file.Name.StartsWith(k, StringComparison.Ordinal))) continue;
                try { var length = file.Length; file.Delete(); size -= length; removed++; } catch (Exception cleanupError) when (cleanupError is IOException or UnauthorizedAccessException) { }
            }
            return removed;
        }
    }
    public void Dispose() { _sweep.Dispose(); _stop.Cancel(); }
}
