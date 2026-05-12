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
    private string? _currentItemId;
    private List<string> _lastFailedIds = [];
    private List<string> _lastSuccessIds = [];
    private bool _lastStopped;
    private List<DownloadTaskState> _tasks = [];

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

    public void Start(IReadOnlyList<DownloadJob> jobs)
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
            _currentItemId = null;
            _lastFailedIds = [];
            _lastSuccessIds = [];
            _lastStopped = false;
            _tasks = jobs.Select(job => new DownloadTaskState
            {
                ItemId = job.ItemId,
                BaseDir = job.Settings.BaseDir,
                Status = "queued",
            }).ToList();

            _downloadTask = Task.Run(() => RunAsync(jobs, _downloadCts.Token));
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
            if (!string.IsNullOrWhiteSpace(_currentItemId))
            {
                SetTaskStatusUnlocked(_currentItemId, "cancelled", "正在中断当前请求", null);
            }

            snapshot = SnapshotUnlocked();
        }

        Emit("stop_requested", "WARNING", "用户已请求停止下载，正在中断当前请求", data: new()
        {
            ["snapshot"] = snapshot,
        });
        return true;
    }

    private async Task RunAsync(IReadOnlyList<DownloadJob> jobs, CancellationToken cancellationToken)
    {
        var failedIds = new List<string>();
        var successIds = new List<string>();
        var stopped = false;

        try
        {
            Emit("started", "INFO", $"开始下载任务，共 {jobs.Count} 个ID: {string.Join(", ", jobs.Select(job => job.ItemId))}", data: new()
            {
                ["snapshot"] = Snapshot(),
            });

            for (var index = 0; index < jobs.Count; index++)
            {
                var job = jobs[index];
                if (cancellationToken.IsCancellationRequested)
                {
                    stopped = true;
                    MarkRemainingCancelled(jobs.Skip(index));
                    Emit("stopped", "WARNING", "下载任务已停止", data: new()
                    {
                        ["snapshot"] = Snapshot(),
                    });
                    break;
                }

                lock (_lock)
                {
                    _currentItemId = job.ItemId;
                    SetTaskStatusUnlocked(job.ItemId, "running", "下载中", job.Settings.BaseDir);
                }

                try
                {
                    Emit("item_start", "INFO", $"开始下载（剩余{jobs.Count - index}个）: {job.ItemId}", job.ItemId, new()
                    {
                        ["base_dir"] = job.Settings.BaseDir,
                        ["snapshot"] = Snapshot(),
                    });

                    var result = await DownloadOneAsync(job, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();

                    successIds.Add(job.ItemId);
                    lock (_lock)
                    {
                        SetTaskStatusUnlocked(job.ItemId, "success", result.Message, result.BaseDir);
                        _tasks.RemoveAll(task => task.ItemId == job.ItemId && task.Status == "success");
                    }

                    Emit("item_success", "SUCCESS", result.Message, job.ItemId, new()
                    {
                        ["output_path"] = result.OutputPath,
                        ["output_format"] = result.OutputFormat,
                        ["snapshot"] = Snapshot(),
                    });
                }
                catch (OperationCanceledException)
                {
                    stopped = true;
                    lock (_lock)
                    {
                        SetTaskStatusUnlocked(job.ItemId, "cancelled", "已中断", job.Settings.BaseDir);
                    }
                    MarkRemainingCancelled(jobs.Skip(index + 1));
                    Emit("item_cancelled", "WARNING", $"下载 {job.ItemId} 已中断", job.ItemId, new()
                    {
                        ["snapshot"] = Snapshot(),
                    });
                    break;
                }
                catch (Exception ex)
                {
                    failedIds.Add(job.ItemId);
                    lock (_lock)
                    {
                        SetTaskStatusUnlocked(job.ItemId, "failed", ex.Message, job.Settings.BaseDir);
                    }

                    Emit("item_failed", "ERROR", $"下载 {job.ItemId} 失败: {ex.Message}", job.ItemId, new()
                    {
                        ["snapshot"] = Snapshot(),
                    });
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
                _currentItemId = null;
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
        var title = ArtifactTools.SafeFilename(target.Title, target.Id);
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
                    ? Path.Combine(albumDir, ArtifactTools.SafeFilename($"{chapter.Sort:D3} {chapter.Title}", chapter.Id))
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

    private void MarkRemainingCancelled(IEnumerable<DownloadJob> jobs)
    {
        lock (_lock)
        {
            foreach (var job in jobs)
            {
                SetTaskStatusUnlocked(job.ItemId, "cancelled", "已取消", job.Settings.BaseDir);
            }
        }
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
        CurrentItemId = _currentItemId,
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
