using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace DesktopShell.NativeBackend;

// Persist API payloads (including chapter lists) rather than public DTOs with JsonIgnore fields.
// Stale content remains usable while one shared refresh runs in the background.
public sealed class JmContentCache : IDisposable
{
    private readonly string _root;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> _jobs = new();
    private int _sweeping;
    public JmContentCache(string appDataRoot)
    {
        _root = Path.Combine(appDataRoot, "Cache", "content");
        Directory.CreateDirectory(_root);
    }
    private static string Key(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public async Task<JsonObject> JsonAsync(string key, TimeSpan ttl, Func<CancellationToken, Task<JsonObject>> factory, CancellationToken token)
    {
        var data = await BytesAsync("json|" + key, ttl, async ct => Encoding.UTF8.GetBytes((await factory(ct).ConfigureAwait(false)).ToJsonString()), token).ConfigureAwait(false);
        try { return JsonNode.Parse(data) as JsonObject ?? throw new InvalidDataException("缓存内容无效"); }
        catch (System.Text.Json.JsonException)
        {
            File.Delete(Path.Combine(_root, Key("json|" + key) + ".cache"));
            return await factory(token).ConfigureAwait(false);
        }
    }
    public async Task<byte[]> BytesAsync(string key, TimeSpan ttl, Func<CancellationToken, Task<byte[]>> factory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var file = Path.Combine(_root, Key(key) + ".cache");
        byte[]? cached = null;
        try { if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(7)) cached = await File.ReadAllBytesAsync(file, token).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (cached is { Length: > 0 })
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) >= ttl) _ = ObserveRefresh(Refresh(key, file, factory));
            return cached;
        }
        return await Refresh(key, file, factory).WaitAsync(token).ConfigureAwait(false);
    }
    private Task<byte[]> Refresh(string key, string file, Func<CancellationToken, Task<byte[]>> factory)
    {
        var lazy = _jobs.GetOrAdd(key, _ => new Lazy<Task<byte[]>>(async () =>
        {
            var bytes = await factory(_stop.Token).ConfigureAwait(false);
            if (bytes.Length == 0) throw new InvalidDataException("空响应不写入缓存");
            var tmp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllBytesAsync(tmp, bytes, _stop.Token).ConfigureAwait(false); File.Move(tmp, file, true); }
            finally { try { File.Delete(tmp); } catch (IOException) { } }
            Sweep();
            return bytes;
        }, LazyThreadSafetyMode.ExecutionAndPublication));
        return Complete(lazy);
        async Task<byte[]> Complete(Lazy<Task<byte[]>> current)
        {
            try { return await current.Value.ConfigureAwait(false); }
            finally { _jobs.TryRemove(new KeyValuePair<string, Lazy<Task<byte[]>>>(key, current)); }
        }
    }
    private static async Task ObserveRefresh(Task<byte[]> task) { try { await task.ConfigureAwait(false); } catch { /* Preserve last good content. */ } }
    private void Sweep()
    {
        if (Interlocked.Exchange(ref _sweeping, 1) != 0) return;
        try
        {
            var files = new DirectoryInfo(_root).GetFiles("*.cache").OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
            long size = 0;
            for (int i = 0; i < files.Length; i++)
            {
                size += files[i].Length;
                if (i >= 512 || size > 64L * 1024 * 1024)
                    try { files[i].Delete(); } catch (IOException) { }
            }
        }
        catch (IOException) { }
        finally { Volatile.Write(ref _sweeping, 0); }
    }
    public void Dispose() => _stop.Cancel();
}
