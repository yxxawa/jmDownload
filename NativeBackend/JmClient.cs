using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DesktopShell.NativeBackend;

public sealed class JmClient : IDisposable
{
    private static readonly string[] BuiltInApiDomains =
    [
        "www.cdnhjk.net",
        "www.cdngwc.cc",
        "www.cdngwc.net",
        "www.cdngwc.club",
        "www.cdnhjk.cc",
    ];

    private static readonly string[] ImageDomains =
    [
        "cdn-msp.jmapiproxy1.cc",
        "cdn-msp.jmapiproxy2.cc",
        "cdn-msp2.jmapiproxy2.cc",
        "cdn-msp3.jmapiproxy2.cc",
        "cdn-msp.jmapinodeudzn.net",
        "cdn-msp3.jmapinodeudzn.net",
    ];

    private static readonly string[] ApiDomainServerUrls =
    [
        "https://rup4a04-c01.tos-ap-southeast-1.bytepluses.com/newsvr-2025.txt",
        "https://rup4a04-c02.tos-cn-hongkong.bytepluses.com/newsvr-2025.txt",
    ];

    private static readonly Regex ScrambleRegex = new(@"var\s+scramble_id\s*=\s*(\d+)", RegexOptions.Compiled);
    private readonly ConcurrentDictionary<string, string> _scrambleCache = new();
    private readonly HttpClient _http;
    public ImagePriorityGate ImageGate { get; } = new();
    private readonly JmContentCache _cache;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _nodeLock = new();
    private readonly string _nodeFile;
    private readonly bool _refreshDomains;
    private List<string> _apiDomains = [.. BuiltInApiDomains];
    private readonly Dictionary<string, DateTime> _cooldown = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastApiDomain, _lastImageDomain;
    private int _refreshStarted;
    private bool _nodesDirty;

    public JmClient(string? appDataRoot = null, HttpClient? http = null, bool refreshDomains = true)
    {
        appDataRoot ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JMComicDesktop");
        _cache = new JmContentCache(appDataRoot);
        _nodeFile = Path.Combine(appDataRoot, "Cache", "nodes.json");
        _refreshDomains = refreshDomains;
        _http = http ?? new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All, UseCookies = true,
            CookieContainer = new CookieContainer(), UseProxy = true,
        }) { Timeout = TimeSpan.FromSeconds(30) };
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(_nodeFile));
            _lastApiDomain = SafeDomain(node?["api"]?.GetValue<string>()) ? node?["api"]?.GetValue<string>() : null;
            _lastImageDomain = SafeDomain(node?["image"]?.GetValue<string>()) ? node?["image"]?.GetValue<string>() : null;
            var saved = (node?["domains"] as JsonArray)?.Select(x => x?.GetValue<string>()).Where(SafeDomain).Select(x => x!).ToList();
            if (saved is { Count: > 0 }) _apiDomains = saved.Concat(BuiltInApiDomains).Distinct().ToList();
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException) { }
    }
    private static bool SafeDomain(string? value) => !string.IsNullOrEmpty(value) &&
        Uri.CheckHostName(value) == UriHostNameType.Dns && !value.Equals("api.zxcbug.com", StringComparison.OrdinalIgnoreCase) &&
        !value.EndsWith(".api.zxcbug.com", StringComparison.OrdinalIgnoreCase);
    private IEnumerable<string> OrderedNodes(IEnumerable<string> nodes, string? preferred)
    {
        lock (_nodeLock) return nodes.Prepend(preferred ?? "").Where(SafeDomain).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(d => _cooldown.TryGetValue(d, out var until) && until > DateTime.UtcNow ? 1 : 0)
            .ThenBy(d => d.Equals(preferred, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToArray();
    }
    private void NodeFailed(string domain) { lock (_nodeLock) _cooldown[domain] = DateTime.UtcNow.AddSeconds(30); }
    private void NodeSucceeded(string domain, bool image = false)
    {
        lock (_nodeLock)
        {
            _cooldown.Remove(domain);
            var previous = image ? _lastImageDomain : _lastApiDomain;
            if (string.Equals(previous, domain, StringComparison.OrdinalIgnoreCase) && !_nodesDirty) return;
            if (image) _lastImageDomain = domain; else _lastApiDomain = domain;
            _nodesDirty = true;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_nodeFile)!);
                var tmp = _nodeFile + ".tmp";
                File.WriteAllText(tmp, new JsonObject { ["api"] = _lastApiDomain, ["image"] = _lastImageDomain,
                    ["domains"] = new JsonArray(_apiDomains.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray()) }.ToJsonString());
                File.Move(tmp, _nodeFile, true);
                _nodesDirty = false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    public async Task<List<AlbumItemDto>> SearchAsync(
        string query,
        int page,
        CancellationToken cancellationToken,
        int mainTag = 0,
        string orderBy = "mr",
        string time = "a")
    {
        page = Math.Max(1, page);
        var data = await ApiGetAsync("/search", new Dictionary<string, string>
        {
            ["main_tag"] = mainTag.ToString(CultureInfo.InvariantCulture),
            ["search_query"] = query,
            ["page"] = page.ToString(CultureInfo.InvariantCulture),
            ["o"] = orderBy,
            ["t"] = time,
        }, cancellationToken).ConfigureAwait(false);

        if (TryGetString(data, "redirect_aid", out var redirectAid) && !string.IsNullOrWhiteSpace(redirectAid))
        {
            var album = await GetAlbumDetailAsync(redirectAid, cancellationToken).ConfigureAwait(false);
            return
            [
                new AlbumItemDto
                {
                    Id = album.Id,
                    Title = album.Title,
                },
            ];
        }

        return ParseAlbumItems(data["content"] as JsonArray, ranked: false);
    }

    public async Task<List<AlbumItemDto>> RankingAsync(string type, CancellationToken cancellationToken)
    {
        var rankingOrder = type.Trim().ToLowerInvariant() switch
        {
            "day" => "mv_t",
            "week" => "mv_w",
            "month" => "mv_m",
            _ => throw new ArgumentException("unsupported ranking type: " + type),
        };

        // Different JM API nodes accept the ranking value through either
        // `order` or `o`. Prefer the currently active form, then fall back to
        // the older form when a node returns an empty list.
        var queryVariants = new[]
        {
            new Dictionary<string, string>
            {
                ["page"] = "1",
                ["order"] = rankingOrder,
                ["c"] = "0",
                ["o"] = string.Empty,
            },
            new Dictionary<string, string>
            {
                ["page"] = "1",
                ["order"] = string.Empty,
                ["c"] = "0",
                ["o"] = rankingOrder,
            },
        };

        List<AlbumItemDto> items = [];
        foreach (var parameters in queryVariants)
        {
            var data = await ApiGetAsync("/categories/filter", parameters, cancellationToken).ConfigureAwait(false);
            items = ParseAlbumItems(data["content"] as JsonArray, ranked: true);
            if (items.Count > 0)
            {
                return items;
            }
        }

        return items;
    }

    public async Task<AlbumDetailDto> GetAlbumDetailAsync(string albumId, CancellationToken cancellationToken)
    {
        albumId = ParseJmId(albumId);
        var data = await ApiGetAsync("/album", new Dictionary<string, string>
        {
            ["id"] = albumId,
        }, cancellationToken).ConfigureAwait(false);

        var title = GetString(data, "name");
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException("未找到本子详情: " + albumId);
        }

        var chapters = ParseChapters(data["series"] as JsonArray, albumId, title);
        var pageCount = TryGetArray(data, "images", out var images) && images.Count > 0 ? images.Count : (int?)null;

        return new AlbumDetailDto
        {
            Id = GetString(data, "id", albumId),
            Title = title,
            Author = ParseStringList(data["author"]).FirstOrDefault(),
            Tags = ParseStringList(data["tags"]),
            PageCount = pageCount,
            Chapters = chapters,
        };
    }

    public async Task<PhotoDetailDto> GetPhotoDetailAsync(
        string photoId,
        AlbumDetailDto? album,
        bool fetchScramble,
        CancellationToken cancellationToken)
    {
        photoId = ParseJmId(photoId);
        var data = await ApiGetAsync("/chapter", new Dictionary<string, string>
        {
            ["id"] = photoId,
        }, cancellationToken).ConfigureAwait(false);

        var albumId = GetAlbumIdFromPhoto(data, photoId);
        var title = GetString(data, "name", photoId);
        var sort = GetSortFromPhoto(data, photoId);
        var scrambleId = fetchScramble
            ? await GetScrambleIdAsync(photoId, albumId, cancellationToken).ConfigureAwait(false)
            : string.Empty;

        if (string.IsNullOrWhiteSpace(scrambleId) && album is not null)
        {
            scrambleId = "220980";
        }

        return new PhotoDetailDto
        {
            Id = photoId,
            AlbumId = albumId,
            Title = title,
            Sort = sort,
            ScrambleId = scrambleId,
            ImageDomain = PickImageDomain(),
            Images = ParseStringList(data["images"]),
        };
    }

    public Task<byte[]> DownloadCoverAsync(string albumId, CancellationToken cancellationToken)
    {
        albumId = ParseJmId(albumId);
        return _cache.BytesAsync("cover|" + albumId, TimeSpan.FromDays(1), async ct =>
        {
            Exception? last = null;
            foreach (var domain in OrderedNodes(ImageDomains, _lastImageDomain).Take(3))
            {
                try { var bytes = await FetchImageBytesAsync($"https://{domain}/media/albums/{albumId}.jpg", ct).ConfigureAwait(false); NodeSucceeded(domain, true); return bytes; }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) { last = ex; NodeFailed(domain); }
            }
            throw new IOException("封面下载失败: " + albumId, last);
        }, cancellationToken);
    }

    public async Task<byte[]> GetImageBytesAsync(PhotoDetailDto photo, string imageName, CancellationToken cancellationToken, Func<int>? priority = null)
    {
        Exception? last = null;
        foreach (var url in BuildImageUrls(photo, imageName).Take(3))
        {
            var host = new Uri(url).Host;
            try { var bytes = await FetchImageBytesAsync(url, cancellationToken, priority).ConfigureAwait(false); NodeSucceeded(host, true); return bytes; }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is IOException or HttpRequestException or OperationCanceledException) { last = ex; NodeFailed(host); }
        }
        throw new IOException("图片请求失败（已有限换节点重试）: " + imageName, last);
    }

    private async Task<byte[]> FetchImageBytesAsync(string url, CancellationToken token, Func<int>? priority = null)
    {
        using var slot = await ImageGate.EnterAsync(priority ?? (() => 2), token).ConfigureAwait(false);
        using var headersCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        headersCts.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddImageHeaders(request);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersCts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var bodyCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        bodyCts.CancelAfter(TimeSpan.FromSeconds(20));
        await using var stream = await response.Content.ReadAsStreamAsync(bodyCts.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        int read;
        while ((read = await stream.ReadAsync(buffer, bodyCts.Token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > 48L * 1024 * 1024) throw new IOException("图片超过 48MB 限制");
            output.Write(buffer, 0, read);
        }
        var bytes = output.ToArray();
        if (bytes.Length == 0) throw new IOException("图片正文为空");
        // Never cache a successful HTTP response containing a login/error HTML page.
        if (response.Content.Headers.ContentType?.MediaType is "text/html" or "application/json") throw new IOException("上游未返回图片");
        if (!JmImageDecoder.HasImageSignature(bytes)) throw new IOException("上游返回的正文不是有效图片");
        return bytes;
    }

    public async Task DownloadImageAsync(
        string url,
        string savePath,
        string? scrambleId,
        bool decode,
        string? targetSuffix,
        CancellationToken cancellationToken)
    {
        var bytes = await FetchImageBytesAsync(url, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);

        if (!decode || url.Split('?')[0].EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
        {
            await File.WriteAllBytesAsync(savePath, bytes, cancellationToken).ConfigureAwait(false);
            return;
        }

        var segments = JmImageDecoder.GetSegmentCount(scrambleId, ExtractAidFromImageUrl(url), Path.GetFileNameWithoutExtension(url.Split('?')[0]));
        if (segments == 0 && (targetSuffix is null || Path.GetExtension(savePath).Equals(Path.GetExtension(url.Split('?')[0]), StringComparison.OrdinalIgnoreCase)))
        {
            await File.WriteAllBytesAsync(savePath, bytes, cancellationToken).ConfigureAwait(false);
            return;
        }

        JmImageDecoder.DecodeAndSave(bytes, segments, savePath);
    }

    public async Task<string> GetScrambleIdAsync(string photoId, string? albumId, CancellationToken cancellationToken)
    {
        var data = await _cache.JsonAsync("scramble|" + ParseJmId(photoId), TimeSpan.FromDays(3),
            async ct => new JsonObject { ["value"] = await FetchScrambleIdAsync(photoId, albumId, ct).ConfigureAwait(false) }, cancellationToken).ConfigureAwait(false);
        return data["value"]!.GetValue<string>();
    }

    private async Task<string> FetchScrambleIdAsync(string photoId, string? albumId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(albumId) && _scrambleCache.TryGetValue(albumId, out var byAlbum))
        {
            return byAlbum;
        }

        if (_scrambleCache.TryGetValue(photoId, out var cached))
        {
            return cached;
        }

        var path = "/chapter_view_template";
        var parameters = new Dictionary<string, string>
        {
            ["id"] = ParseJmId(photoId),
            ["mode"] = "vertical",
            ["page"] = "0",
            ["app_img_shunt"] = "1",
            ["express"] = "off",
            ["v"] = UnixTimestamp().ToString(CultureInfo.InvariantCulture),
        };

        var urlPath = AppendQuery(path, parameters);
        var ts = UnixTimestamp().ToString(CultureInfo.InvariantCulture);
        var (token, tokenParam) = JmCrypto.TokenAndTokenParam(ts, JmCrypto.AppTokenSecretContent);
        Exception? lastError = null;

        foreach (var domain in (await GetApiDomainsAsync(cancellationToken).ConfigureAwait(false)).Take(3))
        {
            var url = "https://" + domain + urlPath;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddApiHeaders(request, token, tokenParam);
            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attempt.CancelAfter(TimeSpan.FromSeconds(10));
                using var response = await _http.SendAsync(request, attempt.Token).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(attempt.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {text}");
                }

                var match = ScrambleRegex.Match(text);
                var scrambleId = match.Success ? match.Groups[1].Value : "220980";
                _scrambleCache[photoId] = scrambleId;
                if (!string.IsNullOrWhiteSpace(albumId))
                {
                    _scrambleCache[albumId] = scrambleId;
                }
                NodeSucceeded(domain);
                return scrambleId;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
            {
                NodeFailed(domain);
                lastError = ex;
            }
        }

        throw new InvalidOperationException("获取 scramble_id 失败: " + photoId, lastError);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _http.Dispose();
        _cache.Dispose();
    }

    private Task<JsonObject> ApiGetAsync(string path, Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        var key = AppendQuery(path, parameters.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value));
        var ttl = path == "/album" ? TimeSpan.FromMinutes(30) : path == "/chapter" ? TimeSpan.FromHours(12) : TimeSpan.FromMinutes(3);
        return _cache.JsonAsync(key, ttl, ct => ApiFetchAsync(path, parameters, ct), cancellationToken);
    }

    private async Task<JsonObject> ApiFetchAsync(
        string path,
        Dictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        var urlPath = AppendQuery(path, parameters);
        var ts = UnixTimestamp().ToString(CultureInfo.InvariantCulture);
        var (token, tokenParam) = JmCrypto.TokenAndTokenParam(ts);
        Exception? lastError = null;

        foreach (var domain in (await GetApiDomainsAsync(cancellationToken).ConfigureAwait(false)).Take(3))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://" + domain + urlPath);
            AddApiHeaders(request, token, tokenParam);

            try
            {
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attempt.CancelAfter(TimeSpan.FromSeconds(10));
                using var response = await _http.SendAsync(request, attempt.Token).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(attempt.Token).ConfigureAwait(false);
                if ((int)response.StatusCode >= 500)
                {
                    throw new HttpRequestException("JM API 服务器错误: " + (int)response.StatusCode);
                }

                response.EnsureSuccessStatusCode();
                var payload = ParseJsonObject(text);
                if (GetInt(payload, "code") != 200)
                {
                    throw new InvalidOperationException("JM API 返回错误: " + text);
                }

                var encoded = GetString(payload, "data");
                if (string.IsNullOrWhiteSpace(encoded))
                {
                    throw new InvalidOperationException("JM API 返回空 data");
                }

                var decoded = JmCrypto.DecodeResponseData(encoded, ts);
                var dataNode = JsonNode.Parse(decoded) as JsonObject;
                if (dataNode is null)
                {
                    throw new JsonException("JM API 解密结果不是对象: " + decoded);
                }

                NodeSucceeded(domain);
                return dataNode;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or IOException or FormatException or System.Security.Cryptography.CryptographicException)
            {
                NodeFailed(domain);
                lastError = ex;
            }
        }

        throw new InvalidOperationException("JM API 请求失败: " + path, lastError);
    }

    private Task<List<string>> GetApiDomainsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_refreshDomains && Interlocked.Exchange(ref _refreshStarted, 1) == 0) _ = Task.Run(RefreshDomainsAsync);
        lock (_nodeLock) return Task.FromResult(OrderedNodes(_apiDomains, _lastApiDomain).ToList());
    }
    private async Task RefreshDomainsAsync()
    {
        // Background maintenance is not a prerequisite for the first search/ranking.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(4));
        foreach (var url in ApiDomainServerUrls)
        {
            try
            {
                var text = TrimLeadingNonAscii(await _http.GetStringAsync(url, deadline.Token).ConfigureAwait(false));
                var decoded = JmCrypto.DecodeResponseData(text, string.Empty, JmCrypto.ApiDomainServerSecret);
                var node = JsonNode.Parse(decoded) as JsonObject;
                var list = (node?["Server"] as JsonArray)?.Select(x => x?.GetValue<string>()).Where(SafeDomain).Select(x => x!).ToList();
                if (list is { Count: > 0 }) { lock (_nodeLock) { _apiDomains = list.Concat(BuiltInApiDomains).Distinct().ToList(); _nodesDirty = true; } return; }
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or FormatException or System.Security.Cryptography.CryptographicException) { }
        }
    }

    private static JsonObject ParseJsonObject(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith('{'))
        {
            var start = trimmed.IndexOf('{');
            var end = trimmed.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                trimmed = trimmed[start..(end + 1)];
            }
        }

        return JsonNode.Parse(trimmed) as JsonObject
               ?? throw new JsonException("响应不是 JSON 对象");
    }

    private static List<AlbumItemDto> ParseAlbumItems(JsonArray? content, bool ranked)
    {
        var result = new List<AlbumItemDto>();
        if (content is null)
        {
            return result;
        }

        var rank = 1;
        foreach (var node in content.OfType<JsonObject>())
        {
            var id = GetString(node, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            result.Add(new AlbumItemDto
            {
                Id = id,
                Title = GetString(node, "name", id),
                Rank = ranked ? rank : null,
            });
            rank++;
        }

        return result;
    }

    private static List<ChapterDto> ParseChapters(JsonArray? series, string albumId, string albumTitle)
    {
        var chapters = new List<ChapterDto>();
        if (series is not null)
        {
            foreach (var node in series.OfType<JsonObject>())
            {
                var id = GetString(node, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                chapters.Add(new ChapterDto
                {
                    Id = id,
                    Title = GetString(node, "name", albumTitle),
                    Sort = GetInt(node, "sort", 1),
                });
            }
        }

        if (chapters.Count == 0)
        {
            chapters.Add(new ChapterDto
            {
                Id = albumId,
                Title = albumTitle,
                Sort = 1,
            });
        }

        return chapters
            .GroupBy(chapter => chapter.Sort)
            .Select(group => group.First())
            .OrderBy(chapter => chapter.Sort)
            .ToList();
    }

    private static string GetAlbumIdFromPhoto(JsonObject data, string photoId)
    {
        var seriesId = GetString(data, "series_id");
        return string.IsNullOrWhiteSpace(seriesId) || seriesId == "0" ? photoId : seriesId;
    }

    private static int GetSortFromPhoto(JsonObject data, string photoId)
    {
        var series = data["series"] as JsonArray;
        if (series is not null)
        {
            foreach (var item in series.OfType<JsonObject>())
            {
                if (GetString(item, "id") == photoId)
                {
                    return GetInt(item, "sort", 1);
                }
            }
        }

        return 1;
    }

    public static string ParseJmId(string text)
    {
        text = (text ?? string.Empty).Trim();
        if (text.StartsWith("jm", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        var match = Regex.Match(text, @"(?:photos?|albums?)/(\d+)|id=(\d+)|(\d+)");
        if (!match.Success)
        {
            throw new ArgumentException("无法解析 JM ID: " + text);
        }

        for (var i = 1; i < match.Groups.Count; i++)
        {
            if (match.Groups[i].Success)
            {
                return match.Groups[i].Value;
            }
        }

        throw new ArgumentException("无法解析 JM ID: " + text);
    }

    public string BuildImageUrl(PhotoDetailDto photo, string imageName)
    {
        return $"https://{photo.ImageDomain}/media/photos/{photo.Id}/{imageName}";
    }

    public IEnumerable<string> BuildImageUrls(PhotoDetailDto photo, string imageName)
    {
        foreach (var domain in OrderedNodes(ImageDomains.Prepend(photo.ImageDomain), _lastImageDomain))
            yield return $"https://{domain}/media/photos/{photo.Id}/{imageName}";
    }

    private static string PickImageDomain()
    {
        return ImageDomains[Random.Shared.Next(ImageDomains.Length)];
    }

    private static IEnumerable<string> Shuffled(IReadOnlyList<string> items)
    {
        return items.OrderBy(_ => Random.Shared.Next());
    }

    private static string AppendQuery(string path, Dictionary<string, string> parameters)
    {
        var query = string.Join("&", parameters.Select(pair =>
            Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        return path + "?" + query;
    }

    private static void AddApiHeaders(HttpRequestMessage request, string token, string tokenParam)
    {
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
        request.Headers.TryAddWithoutValidation(
            "user-agent",
            "Mozilla/5.0 (Linux; Android 9; V1938CT Build/PQ3A.190705.11211812; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/91.0.4472.114 Safari/537.36");
        request.Headers.TryAddWithoutValidation("token", token);
        request.Headers.TryAddWithoutValidation("tokenparam", tokenParam);
    }

    private static void AddImageHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate");
        request.Headers.TryAddWithoutValidation(
            "user-agent",
            "Mozilla/5.0 (Linux; Android 9; V1938CT Build/PQ3A.190705.11211812; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/91.0.4472.114 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Accept", "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("X-Requested-With", "com.JMComic3.app");
        request.Headers.TryAddWithoutValidation("Referer", "https://" + BuiltInApiDomains[0]);
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en-US;q=0.8,en;q=0.7");
    }

    private static string TrimLeadingNonAscii(string text)
    {
        var index = 0;
        while (index < text.Length && text[index] > 127)
        {
            index++;
        }

        return text[index..];
    }

    private static long UnixTimestamp() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static bool TryGetString(JsonObject data, string name, out string value)
    {
        value = GetString(data, name);
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string GetString(JsonObject data, string name, string fallback = "")
    {
        var node = data[name];
        if (node is null)
        {
            return fallback;
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.String => node.GetValue<string>() ?? fallback,
            JsonValueKind.Number => node.ToJsonString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => fallback,
        };
    }

    private static int GetInt(JsonObject data, string name, int fallback = 0)
    {
        var value = GetString(data, name);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? number
            : fallback;
    }

    private static bool TryGetArray(JsonObject data, string name, out JsonArray array)
    {
        array = data[name] as JsonArray ?? [];
        return data[name] is JsonArray;
    }

    private static List<string> ParseStringList(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            return array
                .Select(item => item?.GetValueKind() == JsonValueKind.String ? item.GetValue<string>() : item?.ToJsonString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToList();
        }

        if (node is null)
        {
            return [];
        }

        var text = node.GetValueKind() == JsonValueKind.String ? node.GetValue<string>() : node.ToJsonString();
        return string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static string ExtractAidFromImageUrl(string url)
    {
        var match = Regex.Match(url, @"/media/photos/(\d+)/");
        return match.Success ? match.Groups[1].Value : "0";
    }
}
