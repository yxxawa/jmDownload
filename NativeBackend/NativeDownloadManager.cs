using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace DesktopShell.NativeBackend;

public sealed class NativeDownloadManager
{
    private readonly object _lock = new();
    private readonly JmClient _client;
    private CancellationTokenSource? _downloadCts;
    private Task? _downloadTask;
    private readonly HashSet<string> _runningItemIds = [];
    private List<string> _lastFailedIds = [];
    private List<string> _lastSuccessIds = [];
    private bool _lastStopped;
    private List<DownloadTaskState> _tasks = [];
    private List<DownloadJob> _pendingJobs = [];

    public NativeDownloadManager(JmClient client, Action<DownloadEventDto> eventSink)
    {
        _client = client;
        EventSink = eventSink;
    }

    private Action<DownloadEventDto> EventSink { get; }

    public bool IsRunning
    {
        get
        {
            lock (_lock)
            {
                return _downloadTask is { IsCompleted: false };
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
            if (_downloadTask is { IsCompleted: false })
            {
                throw new InvalidOperationException("download task is already running");
            }

            _downloadCts?.Dispose();
            _downloadCts = new CancellationTokenSource();
            _runningItemIds.Clear();
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

            _downloadTask = Task.Run(() => RunAsync(Math.Max(1, albumThreads), _downloadCts.Token));
        }
    }

    public bool CancelTask(string itemId, string? baseDir = null, string? outputFormat = null)
    {
        DownloadJob? cancelledJob = null;

        lock (_lock)
        {
            if (_runningItemIds.Contains(itemId))
            {
                _downloadCts?.Cancel();
            }
            else
            {
                var pending = _pendingJobs.FirstOrDefault(j => j.ItemId == itemId);
                if (pending is null) return false;
                cancelledJob = pending;
                _pendingJobs.Remove(pending);
                SetTaskStatusUnlocked(itemId, "cancelled", "已取消", null);
                _tasks.RemoveAll(t => t.ItemId == itemId);
            }
        }

        // Delete partial download files for the cancelled task
        var dirToDelete = baseDir ?? cancelledJob?.Settings.BaseDir;
        if (!string.IsNullOrWhiteSpace(dirToDelete) && Directory.Exists(dirToDelete))
        {
            var fmt = outputFormat ?? cancelledJob?.Settings.OutputFormat ?? "images";
            TryDeleteDownloadArtifacts(dirToDelete, fmt);
        }

        return true;
    }

    // Deletes the download directory/files for a cancelled task.
    // Only deletes if no other format artifacts exist alongside the current one.
    private static void TryDeleteDownloadArtifacts(string baseDir, string outputFormat)
    {
        // Whitelist outputFormat to prevent glob injection
        if (outputFormat is not ("images" or "zip" or "pdf")) return;
        try
        {
            if (outputFormat is "zip" or "pdf")
            {
                // Delete partial zip/pdf files (they won't be complete)
                var ext = "." + outputFormat;
                foreach (var f in Directory.GetFiles(baseDir, "*" + ext))
                {
                    try { File.Delete(f); } catch { }
                }
                // If directory is now empty, remove it
                if (!Directory.EnumerateFileSystemEntries(baseDir).Any())
                    Directory.Delete(baseDir, recursive: false);
            }
            else
            {
                // images format: baseDir is the ID subfolder — delete it entirely
                // but only if it contains no zip/pdf artifacts (those belong to other formats)
                var hasOtherFormats = Directory.EnumerateFiles(baseDir, "*.zip").Any()
                                   || Directory.EnumerateFiles(baseDir, "*.pdf").Any();
                if (!hasOtherFormats)
                    Directory.Delete(baseDir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException) { /* best-effort */ }
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
            if (_downloadTask is not { IsCompleted: false } || _downloadCts is null)
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

    private async Task RunAsync(int albumThreads, CancellationToken cancellationToken)
    {
        var failedIds = new List<string>();
        var successIds = new List<string>();
        var stopped = false;
        var stoppedEmitted = 0; // Interlocked flag: only first worker emits "stopped"

        int totalCount;
        lock (_lock)
        {
            totalCount = _pendingJobs.Count;
        }

        try
        {
            Emit("started", "INFO", $"开始下载任务，共 {totalCount} 个ID", data: new()
            {
                ["snapshot"] = Snapshot(),
            });

            using var semaphore = new SemaphoreSlim(albumThreads, albumThreads);

            DownloadJob? DequeueNext()
            {
                lock (_lock)
                {
                    if (_pendingJobs.Count == 0) return null;
                    var job = _pendingJobs[0];
                    _pendingJobs.RemoveAt(0);
                    return job;
                }
            }

            var workerTasks = new List<Task>();
            // Seed initial workers up to albumThreads.
            for (var i = 0; i < albumThreads; i++)
            {
                workerTasks.Add(WorkerAsync());
            }
            await Task.WhenAll(workerTasks).ConfigureAwait(false);

            async Task WorkerAsync()
            {
                while (true)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        lock (_lock)
                        {
                            stopped = true;
                            foreach (var remaining in _pendingJobs)
                                SetTaskStatusUnlocked(remaining.ItemId, "cancelled", "已取消", remaining.Settings.BaseDir);
                            _pendingJobs.Clear();
                        }
                        // Only the first worker to detect cancellation emits the event
                        if (Interlocked.CompareExchange(ref stoppedEmitted, 1, 0) == 0)
                            Emit("stopped", "WARNING", "下载任务已停止", data: new() { ["snapshot"] = Snapshot() });
                        return;
                    }

                    var job = DequeueNext();
                    if (job is null) return;

                    lock (_lock)
                    {
                        _runningItemIds.Add(job.ItemId);
                        SetTaskStatusUnlocked(job.ItemId, "running", "下载中", job.Settings.BaseDir);
                    }

                    try
                    {
                        Emit("item_start", "INFO", $"开始下载: {job.ItemId}", job.ItemId, new()
                        {
                            ["base_dir"] = job.Settings.BaseDir,
                            ["snapshot"] = Snapshot(),
                        });

                        var result = await DownloadOneAsync(job, cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();

                        lock (_lock)
                        {
                            successIds.Add(job.ItemId);
                            _runningItemIds.Remove(job.ItemId);
                            SetTaskStatusUnlocked(job.ItemId, "success", result.Message, result.BaseDir);
                        }

                        Emit("item_success", "SUCCESS", result.Message, job.ItemId, new()
                        {
                            ["output_path"] = result.OutputPath,
                            ["output_format"] = result.OutputFormat,
                            ["snapshot"] = Snapshot(),
                        });

                        lock (_lock)
                        {
                            _tasks.RemoveAll(t => t.ItemId == job.ItemId && t.Status == "success");
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        lock (_lock)
                        {
                            stopped = true;
                            _runningItemIds.Remove(job.ItemId);
                            SetTaskStatusUnlocked(job.ItemId, "cancelled", "已中断", job.Settings.BaseDir);
                            foreach (var remaining in _pendingJobs)
                                SetTaskStatusUnlocked(remaining.ItemId, "cancelled", "已取消", remaining.Settings.BaseDir);
                            _pendingJobs.Clear();
                            _tasks.RemoveAll(t => t.Status == "cancelled");
                        }
                        Emit("item_cancelled", "WARNING", $"下载 {job.ItemId} 已中断", job.ItemId, new()
                        {
                            ["snapshot"] = Snapshot(),
                        });
                        return;
                    }
                    catch (Exception ex)
                    {
                        lock (_lock)
                        {
                            failedIds.Add(job.ItemId);
                            _runningItemIds.Remove(job.ItemId);
                            SetTaskStatusUnlocked(job.ItemId, "failed", ex.Message, job.Settings.BaseDir);
                        }
                        Emit("item_failed", "ERROR", $"下载 {job.ItemId} 失败: {ex.Message}", job.ItemId, new()
                        {
                            ["snapshot"] = Snapshot(),
                        });
                    }
                }
            }

            if (!stopped && cancellationToken.IsCancellationRequested)
            {
                stopped = true;
            }

            var finishLevel = stopped ? "WARNING" : failedIds.Count > 0 ? "WARNING" : "SUCCESS";
            var finishMessage = stopped ? "下载任务已停止" : failedIds.Count > 0 ? $"下载任务结束，失败 {failedIds.Count} 个ID" : "所有ID下载完成";
            Emit("finished", finishLevel, finishMessage, data: new()
            {
                ["failed_ids"] = failedIds,
                ["success_ids"] = successIds,
                ["stopped"] = stopped,
                ["snapshot"] = Snapshot(),
            });
        }
        finally
        {
            lock (_lock)
            {
                _lastFailedIds = failedIds;
                _lastSuccessIds = successIds;
                _lastStopped = stopped;
                _runningItemIds.Clear();
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
        string itemId,
        AlbumDetailDto album,
        string outputRoot,
        string title,
        IReadOnlyDictionary<string, PhotoDetailDto> prefetchedPhotos,
        DownloadSettings settings,
        CancellationToken cancellationToken)
    {
        var albumDir = Path.Combine(outputRoot, title);
        Directory.CreateDirectory(albumDir);

        SetProgress(itemId, 0, 0, album.Title, "解析章节图片列表", "album_start");

        var photos = new List<(ChapterDto Chapter, PhotoDetailDto Photo)>();
        foreach (var chapter in album.Chapters.OrderBy(chapter => chapter.Sort))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!prefetchedPhotos.TryGetValue(chapter.Id, out var photo))
            {
                photo = await _client.GetPhotoDetailAsync(chapter.Id, album, fetchScramble: true, cancellationToken)
                    .ConfigureAwait(false);
            }
            if (photo.Images.Count > 0)
            {
                photos.Add((chapter, photo));
            }
        }

        var totalImages = photos.Sum(item => item.Photo.Images.Count);
        SetProgress(itemId, 0, totalImages, album.Title, $"解析完成，准备下载 {totalImages} 张图片", "album_start");

        var done = 0;
        using var photoSemaphore = new SemaphoreSlim(Math.Max(1, settings.PhotoThreads), Math.Max(1, settings.PhotoThreads));
        using var imageSemaphore = new SemaphoreSlim(Math.Max(1, settings.ImageThreads), Math.Max(1, settings.ImageThreads));

        var chapterTasks = photos.Select(item => DownloadChapterAsync(item.Chapter, item.Photo)).ToList();
        await Task.WhenAll(chapterTasks).ConfigureAwait(false);

        SetProgress(itemId, done, totalImages, album.Title, $"图片下载完成 {done}/{totalImages}", "album_done");
        return albumDir;

        async Task DownloadChapterAsync(ChapterDto chapter, PhotoDetailDto photo)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await photoSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var chapterDir = album.Chapters.Count > 1
                    ? Path.Combine(albumDir, ArtifactTools.SafeFilename($"{chapter.Sort:D3} {chapter.Title}", chapter.Id, settings.FilenameLang))
                    : albumDir;
                Directory.CreateDirectory(chapterDir);

                SetProgress(itemId, done, totalImages, chapter.Title, $"章节下载中: {chapter.Title}", "photo_start");

                var tasks = new List<Task>();
                for (var i = 0; i < photo.Images.Count; i++)
                {
                    tasks.Add(DownloadImageSlotAsync(photo, i, chapterDir, chapter.Title));
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);
                SetProgress(itemId, done, totalImages, chapter.Title, $"章节完成: {chapter.Title}", "photo_done");
            }
            finally
            {
                photoSemaphore.Release();
            }
        }

        async Task DownloadImageSlotAsync(PhotoDetailDto photo, int index, string chapterDir, string chapterTitle)
        {
            await imageSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await DownloadImageWithFallbackAsync(photo, index, chapterDir, settings, cancellationToken)
                    .ConfigureAwait(false);
                var current = Interlocked.Increment(ref done);
                SetProgress(itemId, current, totalImages, chapterTitle, $"图片下载进度 {current}/{totalImages}", "image_done");
            }
            finally
            {
                imageSemaphore.Release();
            }
        }
    }

    private async Task DownloadImageWithFallbackAsync(
        PhotoDetailDto photo,
        int imageIndex,
        string chapterDir,
        DownloadSettings settings,
        CancellationToken cancellationToken)
    {
        var imageName = photo.Images[imageIndex];
        var sourceSuffix = Path.GetExtension(imageName);
        var targetSuffix = settings.ImageSuffix ?? sourceSuffix;
        if (string.IsNullOrWhiteSpace(targetSuffix))
        {
            targetSuffix = sourceSuffix;
        }

        var savePath = Path.Combine(chapterDir, (imageIndex + 1).ToString("D5") + targetSuffix);
        if (File.Exists(savePath))
        {
            return;
        }

        Exception? lastError = null;
        foreach (var url in _client.BuildImageUrls(photo, imageName))
        {
            try
            {
                await _client.DownloadImageAsync(
                    url,
                    savePath,
                    photo.ScrambleId,
                    decode: true,
                    targetSuffix,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException)
            {
                lastError = ex;
                if (File.Exists(savePath))
                {
                    try
                    {
                        File.Delete(savePath);
                    }
                    catch
                    {
                        // Best effort cleanup.
                    }
                }
            }
        }

        throw new InvalidOperationException("图片下载失败: " + imageName, lastError);
    }

    private void SetProgress(string itemId, int progress, int total, string detail, string message, string stage)
    {
        DownloadSnapshot snapshot;
        lock (_lock)
        {
            var task = _tasks.FirstOrDefault(task => task.ItemId == itemId);
            if (task is not null)
            {
                task.Progress = Math.Max(0, progress);
                task.Total = Math.Max(0, total);
                task.Detail = detail;
                task.Message = message;
            }

            snapshot = SnapshotUnlocked();
        }

        Emit("item_progress", "INFO", message, itemId, new()
        {
            ["stage"] = stage,
            ["progress"] = progress,
            ["total"] = total,
            ["detail"] = detail,
            ["snapshot"] = snapshot,
        });
    }

    private void SetTaskStatusUnlocked(string itemId, string status, string message, string? baseDir)
    {
        var task = _tasks.FirstOrDefault(task => task.ItemId == itemId);
        if (task is null)
        {
            return;
        }

        task.Status = status;
        task.Message = message;
        if (baseDir is not null)
        {
            task.BaseDir = baseDir;
        }
    }

    private DownloadSnapshot SnapshotUnlocked() => new()
    {
        Running = _downloadTask is { IsCompleted: false },
        Stopping = _downloadCts?.IsCancellationRequested == true,
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
        EventSink(new DownloadEventDto
        {
            Type = type,
            Level = level,
            Message = message,
            ItemId = itemId,
            Data = data ?? [],
        });
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
