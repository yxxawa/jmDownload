using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Channels;

namespace DesktopShell.NativeBackend;

public sealed class NativeDownloadManager : IDisposable
{
    private readonly object _lock = new();
    private readonly JmClient _client;
    private readonly ImageRepository _images;
    private readonly ReaderStore? _readerStore;
    private readonly bool _ownsImages;
    private readonly Dictionary<string, CancellationTokenSource> _jobTokens = [];
    private readonly HashSet<string> _dirty = [];
    private readonly Timer _progressTimer;
    private bool _running;
    private string _runId = "";
    private long _sequence;
    private CancellationTokenSource? _downloadCts;
    private Task? _downloadTask;
    private readonly HashSet<string> _runningItemIds = [];
    private List<string> _lastFailedIds = [];
    private List<string> _lastSuccessIds = [];
    private bool _lastStopped;
    private List<DownloadTaskState> _tasks = [];
    private List<DownloadJob> _pendingJobs = [];

    public NativeDownloadManager(JmClient client, Action<DownloadEventDto> eventSink, ImageRepository? images = null, ReaderStore? readerStore = null)
    {
        _client = client; EventSink = eventSink; _readerStore = readerStore; _ownsImages = images is null;
        _images = images ?? new ImageRepository(client, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JMComicDesktop"));
        _progressTimer = new Timer(_ => FlushProgress(), null, 150, 150);
    }

    private Action<DownloadEventDto> EventSink { get; }

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _running;
            }
        }
    }

    public DownloadSnapshot Snapshot()
    {
        lock (_lock)
        {
            return SnapshotUnlocked();
        }
    }

    public void Start(IReadOnlyList<DownloadJob> jobs) => Start(jobs, 1);

    public void Start(IReadOnlyList<DownloadJob> jobs, int albumThreads)
    {
        if (jobs.Count == 0)
        {
            throw new ArgumentException("download job list is empty");
        }

        lock (_lock)
        {
            if (_running)
            {
                throw new InvalidOperationException("download task is already running");
            }

            _downloadCts?.Dispose();
            _downloadCts = new CancellationTokenSource();
            _runningItemIds.Clear();
            _jobTokens.Clear();
            _dirty.Clear();
            _running = true;
            _runId = Guid.NewGuid().ToString("N");
            _lastFailedIds = [];
            _lastSuccessIds = [];
            _lastStopped = false;
            _tasks = jobs.Select(job => new DownloadTaskState
            {
                ItemId = job.ItemId,
                BaseDir = job.Settings.BaseDir,
                Status = "queued",
            }).ToList();
            _pendingJobs = [.. jobs];

            var token = _downloadCts.Token;
            var runId = _runId;
            _downloadTask = Task.Run(() => RunAsync(Math.Clamp(albumThreads, 1, 8), token, runId));
        }
    }

    public bool CancelTask(string itemId, string? baseDir = null, string? outputFormat = null)
    {
        lock (_lock)
        {
            if (_jobTokens.TryGetValue(itemId, out var token))
            {
                token.Cancel();
                SetTaskStatusUnlocked(itemId, "stopping", "正在取消当前作品", null);
            }
            else
            {
                var pending = _pendingJobs.Find(j => j.ItemId == itemId);
                if (pending is null) return false;
                _pendingJobs.Remove(pending);
                SetTaskStatusUnlocked(itemId, "cancelled", "已取消", null);
            }
            // Do not delete a directory while a worker writes, or destroy already completed formats.
            // Atomic writes leave reusable completed pages; only transient .tmp files are cleaned up.
            Emit("item_cancel_requested", "WARNING", $"取消作品: {itemId}", itemId, new() { ["snapshot"] = SnapshotUnlocked() });
            return true;
        }
    }

    public bool ReorderTask(string itemId, int direction)
    {
        lock (_lock)
        {
            var pi = _pendingJobs.FindIndex(j => j.ItemId == itemId);
            if (pi < 0)
            {
                return false;
            }

            var newPi = pi + direction;
            if (newPi < 0 || newPi >= _pendingJobs.Count)
            {
                return false;
            }

            (_pendingJobs[pi], _pendingJobs[newPi]) = (_pendingJobs[newPi], _pendingJobs[pi]);

            // Mirror reorder in _tasks (only among queued entries).
            var ti = _tasks.FindIndex(t => t.ItemId == itemId && t.Status == "queued");
            var swapId = _pendingJobs[pi].ItemId; // the item now at pi after swap
            var ti2 = _tasks.FindIndex(t => t.ItemId == swapId && t.Status == "queued");
            if (ti >= 0 && ti2 >= 0)
            {
                (_tasks[ti], _tasks[ti2]) = (_tasks[ti2], _tasks[ti]);
            }

            return true;
        }
    }

    public bool RequestStop()
    {
        DownloadSnapshot snapshot;
        lock (_lock)
        {
            if (!_running || _downloadCts is null)
            {
                return false;
            }

            _downloadCts.Cancel();
            foreach (var rid in _runningItemIds)
                SetTaskStatusUnlocked(rid, "cancelled", "正在中断当前请求", null);

            snapshot = SnapshotUnlocked();
        }

        Emit("stop_requested", "WARNING", "用户已请求停止下载，正在中断当前请求", data: new()
        {
            ["snapshot"] = snapshot,
        });
        return true;
    }

    private async Task RunAsync(int albumThreads, CancellationToken cancellationToken, string runId)
    {
        var failedIds = new List<string>();
        var successIds = new List<string>();
        var stopped = false;
        try
        {
            Emit("started", "INFO", $"开始下载任务，共 {_tasks.Count} 个ID", data: new() { ["snapshot"] = Snapshot() });
            await Task.WhenAll(Enumerable.Range(0, albumThreads).Select(_ => WorkerAsync())).ConfigureAwait(false);
            async Task WorkerAsync()
            {
                while (true)
                {
                    DownloadJob job;
                    CancellationTokenSource jobCts;
                    lock (_lock)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            stopped = true;
                            foreach (var pending in _pendingJobs) SetTaskStatusUnlocked(pending.ItemId, "cancelled", "已取消", null);
                            _pendingJobs.Clear();
                            return;
                        }
                        if (_pendingJobs.Count == 0) return;
                        job = _pendingJobs[0]; _pendingJobs.RemoveAt(0);
                        jobCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        _jobTokens[job.ItemId] = jobCts;
                        _runningItemIds.Add(job.ItemId);
                        SetTaskStatusUnlocked(job.ItemId, "running", "下载中", job.Settings.BaseDir);
                    }
                    try
                    {
                        Emit("item_start", "INFO", $"开始下载: {job.ItemId}", job.ItemId, new() { ["base_dir"] = job.Settings.BaseDir, ["snapshot"] = Snapshot() });
                        var result = await DownloadOneAsync(job, jobCts.Token).ConfigureAwait(false);
                        jobCts.Token.ThrowIfCancellationRequested();
                        lock (_lock)
                        {
                            successIds.Add(job.ItemId); _runningItemIds.Remove(job.ItemId);
                            SetTaskStatusUnlocked(job.ItemId, "success", result.Message, result.BaseDir);
                            _dirty.Remove(job.ItemId);
                            Emit("item_success", "SUCCESS", result.Message, job.ItemId, new() { ["output_path"] = result.OutputPath, ["output_format"] = result.OutputFormat, ["snapshot"] = SnapshotUnlocked() });
                        }
                    }
                    catch (OperationCanceledException) when (jobCts.IsCancellationRequested)
                    {
                        lock (_lock)
                        {
                            stopped |= cancellationToken.IsCancellationRequested;
                            _runningItemIds.Remove(job.ItemId); _dirty.Remove(job.ItemId);
                            SetTaskStatusUnlocked(job.ItemId, "cancelled", "已中断", job.Settings.BaseDir);
                            Emit("item_cancelled", "WARNING", $"下载 {job.ItemId} 已中断", job.ItemId, new() { ["snapshot"] = SnapshotUnlocked() });
                        }
                        // A single-item cancellation continues with the next queued item.
                    }
                    catch (Exception ex)
                    {
                        lock (_lock)
                        {
                            failedIds.Add(job.ItemId); _runningItemIds.Remove(job.ItemId); _dirty.Remove(job.ItemId);
                            SetTaskStatusUnlocked(job.ItemId, "failed", ex.Message, job.Settings.BaseDir);
                            Emit("item_failed", "ERROR", $"下载 {job.ItemId} 失败: {ex.Message}", job.ItemId, new() { ["snapshot"] = SnapshotUnlocked() });
                        }
                    }
                    finally { lock (_lock) _jobTokens.Remove(job.ItemId); jobCts.Dispose(); }
                }
            }
        }
        finally
        {
            lock (_lock)
            {
                // Commit business state BEFORE the final event; Task.IsCompleted is not a business state.
                if (_runId == runId)
                {
                    stopped |= cancellationToken.IsCancellationRequested;
                    _lastFailedIds = failedIds; _lastSuccessIds = successIds; _lastStopped = stopped;
                    _runningItemIds.Clear(); _dirty.Clear(); _running = false;
                    var message = stopped ? "下载任务已停止" : failedIds.Count > 0 ? $"下载任务结束，失败 {failedIds.Count} 个ID" : "所有ID下载完成";
                    Emit("finished", stopped || failedIds.Count > 0 ? "WARNING" : "SUCCESS", message, data: new()
                    { ["failed_ids"] = failedIds, ["success_ids"] = successIds, ["stopped"] = stopped, ["snapshot"] = SnapshotUnlocked() });
                }
            }
        }
    }

    private async Task<DownloadResult> DownloadOneAsync(DownloadJob job, CancellationToken cancellationToken)
    {
        var itemId = job.ItemId;
        var settings = job.Settings;
        Directory.CreateDirectory(settings.BaseDir);

        var target = await ResolveDownloadTargetAsync(itemId, cancellationToken).ConfigureAwait(false);
        var album = target.Album;
        var title = ArtifactTools.SafeFilename(target.Title, target.Id, job.Settings.FilenameLang);
        var downloadedTemp = false;
        var convertedTemp = false;
        string? tempImageSource = null;
        string? tempRoot = null;

        if (settings.OutputFormat is "zip" or "pdf")
        {
            var existing = ArtifactTools.FindExistingArtifact(settings.BaseDir, title, "." + settings.OutputFormat, itemId);
            if (existing is not null)
            {
                return new DownloadResult(
                    $"{itemId} 已下载（{settings.OutputFormat.ToUpperInvariant()}：{existing}）",
                    settings.BaseDir,
                    existing,
                    settings.OutputFormat);
            }
        }

        var existingImages = ArtifactTools.FindImageSource(settings.BaseDir, title);
        if (existingImages is not null && !File.Exists(Path.Combine(existingImages, ".jm-complete.json"))) existingImages = null;
        string imageSource;

        if (existingImages is not null)
        {
            imageSource = existingImages;
        }
        else if (settings.OutputFormat == "images"
                 && TryFindConvertibleArtifact(settings.BaseDir, title, itemId, out var artifactForImages))
        {
            imageSource = Path.Combine(settings.BaseDir, title);
            ConvertArtifactToImages(artifactForImages, imageSource);
        }
        else if (settings.OutputFormat == "zip"
                 && ArtifactTools.FindExistingArtifact(settings.BaseDir, title, ".pdf", itemId) is { } pdfSource)
        {
            tempRoot = Path.Combine(settings.BaseDir, ".jmdownload_" + Guid.NewGuid().ToString("N"));
            imageSource = Path.Combine(tempRoot, title);
            ArtifactTools.ExtractPdfImages(pdfSource, imageSource);
            convertedTemp = true;
        }
        else if (settings.OutputFormat == "pdf"
                 && ArtifactTools.FindExistingArtifact(settings.BaseDir, title, ".zip", itemId) is { } zipSource)
        {
            tempRoot = Path.Combine(settings.BaseDir, ".jmdownload_" + Guid.NewGuid().ToString("N"));
            imageSource = Path.Combine(tempRoot, title);
            ArtifactTools.ExtractZipToImages(zipSource, imageSource);
            convertedTemp = true;
        }
        else if (settings.OutputFormat == "images")
        {
            imageSource = await DownloadImagesAsync(
                itemId,
                album,
                settings.BaseDir,
                title,
                target.PrefetchedPhotos,
                settings,
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            tempRoot = Path.Combine(settings.BaseDir, ".jmdownload_" + Guid.NewGuid().ToString("N"));
            tempImageSource = tempRoot;
            downloadedTemp = true;
            imageSource = await DownloadImagesAsync(
                itemId,
                album,
                tempRoot,
                title,
                target.PrefetchedPhotos,
                settings,
                cancellationToken).ConfigureAwait(false);
        }

        try
        {
            if (settings.OutputFormat == "images")
            {
                RegisterCompletedLocal(album, imageSource);
                return new DownloadResult($"{itemId} 下载完成（路径：{imageSource}）", settings.BaseDir, imageSource, "images");
            }

            if (settings.OutputFormat == "zip")
            {
                var zipPath = ArtifactTools.UniqueFilePath(settings.BaseDir, title, ".zip");
                ArtifactTools.MakeZip(imageSource, zipPath);
                return new DownloadResult($"{itemId} 已导出 ZIP（路径：{zipPath}）", settings.BaseDir, zipPath, "zip");
            }

            if (settings.OutputFormat == "pdf")
            {
                var chapterDirs = ArtifactTools.GroupChapterDirectories(imageSource);
                if (settings.PdfMode == "chapters" && chapterDirs.Count > 1)
                {
                    var outputPaths = new List<string>();
                    foreach (var chapterDir in chapterDirs)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var chapterName = Path.GetFileName(chapterDir);
                        var chapterPdfPath = ArtifactTools.UniqueFilePath(settings.BaseDir, title + " - " + chapterName, ".pdf");
                        ArtifactTools.MakePdf(chapterDir, chapterPdfPath);
                        outputPaths.Add(chapterPdfPath);
                    }

                    return new DownloadResult($"{itemId} 已按章节导出 PDF（{outputPaths.Count} 个文件）", settings.BaseDir, outputPaths[0], "pdf");
                }

                var pdfPath = ArtifactTools.UniqueFilePath(settings.BaseDir, title, ".pdf");
                ArtifactTools.MakePdf(imageSource, pdfPath);
                return new DownloadResult($"{itemId} 已导出 PDF（路径：{pdfPath}）", settings.BaseDir, pdfPath, "pdf");
            }

            throw new InvalidOperationException("不支持的输出格式: " + settings.OutputFormat);
        }
        finally
        {
            if ((downloadedTemp || convertedTemp) && tempRoot is not null)
            {
                ArtifactTools.DeleteDirectoryIfChild(settings.BaseDir, tempRoot);
            }
        }
    }

    private async Task<string> DownloadImagesAsync(
        string itemId, AlbumDetailDto album, string outputRoot, string title,
        IReadOnlyDictionary<string, PhotoDetailDto> prefetchedPhotos, DownloadSettings settings, CancellationToken cancellationToken)
    {
        var albumDir = Path.Combine(outputRoot, title);
        Directory.CreateDirectory(albumDir);
        var channel = Channel.CreateBounded<(PhotoDetailDto Photo, int Index, string Dir, string Title)>(new BoundedChannelOptions(Math.Clamp(settings.ImageThreads * 2, 2, 40)) { FullMode = BoundedChannelFullMode.Wait });
        using var pipeline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = pipeline.Token;
        var localPages = new Dictionary<string, List<string>>();
        int done = 0, total = 0;
        SetProgress(itemId, 0, 0, album.Title, "边解析章节边下载", "album_start");
        var workers = Enumerable.Range(0, Math.Clamp(settings.ImageThreads, 1, 20)).Select(_ => Consume()).ToArray();
        Exception? producerError = null;
        try
        {
            await Parallel.ForEachAsync(album.Chapters.OrderBy(c => c.Sort), new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(settings.PhotoThreads, 1, 5), CancellationToken = token }, async (chapter, ct) =>
            {
                if (!prefetchedPhotos.TryGetValue(chapter.Id, out var photo)) photo = await _client.GetPhotoDetailAsync(chapter.Id, album, true, ct).ConfigureAwait(false);
                if (photo.Images.Count == 0) throw new IOException("章节图片列表为空: " + chapter.Title);
                Interlocked.Add(ref total, photo.Images.Count);
                var dir = album.Chapters.Count > 1 ? Path.Combine(albumDir, ArtifactTools.SafeFilename($"{chapter.Sort:D3} {chapter.Title}", chapter.Id, settings.FilenameLang)) : albumDir;
                Directory.CreateDirectory(dir);
                lock (localPages) localPages[chapter.Id] = Enumerable.Range(0, photo.Images.Count).Select(i => Path.Combine(dir, (i + 1).ToString("D5") + ImageRepository.OutputSuffix(photo, i, settings.ImageSuffix))).ToList();
                SetProgress(itemId, Volatile.Read(ref done), Volatile.Read(ref total), chapter.Title, $"已解析章节，开始取图: {chapter.Title}", "photo_start");
                for (int i = 0; i < photo.Images.Count; i++) await channel.Writer.WriteAsync((photo, i, dir, chapter.Title), ct).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception ex) { producerError = ex; pipeline.Cancel(); }
        finally { channel.Writer.TryComplete(producerError); }
        if (producerError is null) SetProgress(itemId, Volatile.Read(ref done), Volatile.Read(ref total), album.Title, "全部章节已解析，继续下载剩余图片", "metadata_ready");
        try { await Task.WhenAll(workers).ConfigureAwait(false); }
        catch { pipeline.Cancel(); if (producerError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(producerError).Throw(); throw; }
        if (producerError is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(producerError).Throw();
        cancellationToken.ThrowIfCancellationRequested();
        if (done == 0 || done != total) throw new IOException("图片下载不完整");
        var marker = Path.Combine(albumDir, ".jm-complete.json");
        var tmp = marker + ".tmp";
        await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(localPages, NativeJson.JsonOptions), cancellationToken).ConfigureAwait(false);
        File.Move(tmp, marker, true);
        SetProgress(itemId, done, total, album.Title, $"图片下载完成 {done}/{total}", "album_done");
        return albumDir;

        async Task Consume()
        {
            try
            {
                await foreach (var entry in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
                {
                    await DownloadImageWithFallbackAsync(entry.Photo, entry.Index, entry.Dir, settings, token).ConfigureAwait(false);
                    var progress = Interlocked.Increment(ref done);
                    SetProgress(itemId, progress, Volatile.Read(ref total), entry.Title, $"图片下载进度 {progress}/{Volatile.Read(ref total)}", "image_done");
                }
            }
            catch { pipeline.Cancel(); throw; }
        }
    }
    private async Task DownloadImageWithFallbackAsync(PhotoDetailDto photo, int index, string dir, DownloadSettings settings, CancellationToken token)
    {
        var destination = Path.Combine(dir, (index + 1).ToString("D5") + ImageRepository.OutputSuffix(photo, index, settings.ImageSuffix));
        if (File.Exists(destination) && JmImageDecoder.IsUsableImage(destination)) return;
        var cached = await _images.GetFileAsync(photo, index, settings.ImageSuffix, 2, token).ConfigureAwait(false);
        await _images.CopyToAsync(cached, destination, token).ConfigureAwait(false);
    }
    private void RegisterCompletedLocal(AlbumDetailDto album, string dir)
    {
        if (_readerStore is null) return;
        try
        {
            var file = Path.Combine(dir, ".jm-complete.json");
            var pages = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(file), NativeJson.JsonOptions);
            var prefix = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (pages is null || !album.Chapters.All(c => pages.TryGetValue(c.Id, out var paths) && paths.Count > 0 && paths.All(path => Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && File.Exists(path)))) return;
            _readerStore.RegisterLocal(album, pages);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
    }
    private void SetProgress(string itemId, int progress, int total, string detail, string message, string stage)
    {
        lock (_lock)
        {
            var task = _tasks.Find(t => t.ItemId == itemId);
            if (task is null || task.Status is not ("running" or "stopping")) return;
            task.Progress = Math.Max(task.Progress, Math.Max(0, progress));
            task.Total = Math.Max(task.Total, Math.Max(0, total)); task.Detail = detail; task.Message = message;
            if (stage == "album_start") task.TotalKnown = false;
            if (stage is "metadata_ready" or "album_done") task.TotalKnown = true;
            _dirty.Add(itemId);
            if (stage != "image_done") FlushProgress();
        }
    }
    private void FlushProgress()
    {
        lock (_lock)
        {
            if (_dirty.Count == 0) return;
            var changed = _tasks.Where(t => _dirty.Contains(t.ItemId)).Select(CloneTask).ToList();
            _dirty.Clear();
            Emit("tasks_delta", "INFO", "下载进度已更新", data: new() { ["changed_tasks"] = changed, ["running"] = _running, ["stopping"] = _running && _downloadCts?.IsCancellationRequested == true });
        }
    }
    private static DownloadTaskState CloneTask(DownloadTaskState t) => new() { ItemId = t.ItemId, Status = t.Status, BaseDir = t.BaseDir, Message = t.Message, Progress = t.Progress, Total = t.Total, TotalKnown = t.TotalKnown, Detail = t.Detail };
    public async Task StopAsync() { RequestStop(); var task = _downloadTask; if (task is not null) try { await task.ConfigureAwait(false); } catch { } }
    public void Dispose() { _progressTimer.Dispose(); _downloadCts?.Cancel(); if (_ownsImages) _images.Dispose(); }

    private void SetTaskStatusUnlocked(string itemId, string status, string message, string? baseDir)
    {
        var task = _tasks.FirstOrDefault(task => task.ItemId == itemId);
        if (task is null)
        {
            return;
        }

        task.Status = status;
        if (status == "running") task.TotalKnown = false;
        task.Message = message;
        if (baseDir is not null)
        {
            task.BaseDir = baseDir;
        }
    }

    private DownloadSnapshot SnapshotUnlocked() => new()
    {
        RunId = _runId,
        Running = _running,
        Stopping = _running && _downloadCts?.IsCancellationRequested == true,
        CurrentItemId = _runningItemIds.FirstOrDefault(),
        LastFailedIds = [.. _lastFailedIds],
        LastSuccessIds = [.. _lastSuccessIds],
        LastStopped = _lastStopped,
        Tasks = _tasks
            .Select(task => new DownloadTaskState
            {
                ItemId = task.ItemId,
                Status = task.Status,
                BaseDir = task.BaseDir,
                Message = task.Message,
                Progress = task.Progress,
                Total = task.Total,
                Detail = task.Detail,
                TotalKnown = task.TotalKnown,
            })
            .ToList(),
    };

    private void Emit(
        string type,
        string level,
        string message,
        string? itemId = null,
        Dictionary<string, object?>? data = null)
    {
        lock (_lock)
        {
            data ??= [];
            if (data.ContainsKey("snapshot")) data["snapshot"] = SnapshotUnlocked();
            data["run_id"] = _runId; data["sequence"] = ++_sequence;
            EventSink(new DownloadEventDto { Type = type, Level = level, Message = message, ItemId = itemId, Data = data });
        }
    }

    private sealed record DownloadResult(string Message, string BaseDir, string OutputPath, string OutputFormat);

    private sealed record DownloadTarget(
        string Id,
        string Title,
        AlbumDetailDto Album,
        IReadOnlyDictionary<string, PhotoDetailDto> PrefetchedPhotos);

    private async Task<DownloadTarget> ResolveDownloadTargetAsync(string itemId, CancellationToken cancellationToken)
    {
        if (itemId.StartsWith("p", StringComparison.OrdinalIgnoreCase) && itemId.Length > 1)
        {
            var photoId = itemId[1..];
            var photo = await _client.GetPhotoDetailAsync(photoId, album: null, fetchScramble: true, cancellationToken)
                .ConfigureAwait(false);
            var chapter = new ChapterDto
            {
                Id = photo.Id,
                Title = photo.Title,
                Sort = 1,
            };
            var album = new AlbumDetailDto
            {
                Id = photo.Id,
                Title = photo.Title,
                PageCount = photo.Images.Count,
                Chapters = [chapter],
            };

            return new DownloadTarget(
                itemId,
                photo.Title,
                album,
                new Dictionary<string, PhotoDetailDto> { [photo.Id] = photo });
        }

        var albumDetail = await _client.GetAlbumDetailAsync(itemId, cancellationToken).ConfigureAwait(false);
        return new DownloadTarget(albumDetail.Id, albumDetail.Title, albumDetail, new Dictionary<string, PhotoDetailDto>());
    }

    private static bool TryFindConvertibleArtifact(string outputDir, string title, string itemId, out string artifactPath)
    {
        artifactPath = ArtifactTools.FindExistingArtifact(outputDir, title, ".zip", itemId)
                       ?? ArtifactTools.FindExistingArtifact(outputDir, title, ".pdf", itemId)
                       ?? string.Empty;
        return artifactPath.Length > 0;
    }

    private static void ConvertArtifactToImages(string artifactPath, string imageOutputDir)
    {
        if (Directory.Exists(imageOutputDir) && ArtifactTools.EnumerateImages(imageOutputDir).Count > 0)
        {
            return;
        }

        if (Directory.Exists(imageOutputDir))
        {
            Directory.Delete(imageOutputDir, recursive: true);
        }

        Directory.CreateDirectory(imageOutputDir);
        var suffix = Path.GetExtension(artifactPath).ToLowerInvariant();
        if (suffix == ".zip")
        {
            ArtifactTools.ExtractZipToImages(artifactPath, imageOutputDir);
            return;
        }

        if (suffix == ".pdf")
        {
            ArtifactTools.ExtractPdfImages(artifactPath, imageOutputDir);
            return;
        }

        throw new InvalidOperationException("不支持转换为图片目录的文件: " + artifactPath);
    }
}
