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
    private readonly Random _random = new();
    private readonly SemaphoreSlim _domainInitLock = new(1, 1);
    private List<string> _apiDomains = [.. BuiltInApiDomains];
    private bool _domainsInitialized;

    public JmClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            UseProxy = true,
        };

        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public async Task<List<AlbumItemDto>> SearchAsync(string query, int page, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        var data = await ApiGetAsync("/search", new Dictionary<string, string>
        {
            ["main_tag"] = "0",
            ["search_query"] = query,
            ["page"] = page.ToString(CultureInfo.InvariantCulture),
            ["o"] = "mr",
            ["t"] = "a",
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
        var parameters = type switch
        {
            "day" => new Dictionary<string, string>
            {
                ["page"] = "1",
                ["order"] = string.Empty,
                ["c"] = "0",
                ["o"] = "mv_t",
            },
            "week" => new Dictionary<string, string>
            {
                ["page"] = "1",
                ["order"] = "mv_w",
                ["c"] = "0",
                ["o"] = string.Empty,
            },
            "month" => new Dictionary<string, string>
            {
                ["page"] = "1",
                ["order"] = string.Empty,
                ["c"] = "0",
                ["o"] = "mv_m",
            },
            _ => throw new ArgumentException("unsupported ranking type: " + type),
        };

        var data = await ApiGetAsync("/categories/filter", parameters, cancellationToken).ConfigureAwait(false);

        return ParseAlbumItems(data["content"] as JsonArray, ranked: true);
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

    public async Task<byte[]> DownloadCoverAsync(string albumId, CancellationToken cancellationToken)
    {
        var errors = new List<Exception>();
        foreach (var domain in Shuffled(ImageDomains))
        {
            var url = $"https://{domain}/media/albums/{ParseJmId(albumId)}.jpg";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                AddImageHeaders(request);
                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                if (bytes.Length > 0)
                {
                    return bytes;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                errors.Add(ex);
            }
        }

        throw new InvalidOperationException("封面下载失败: " + albumId, errors.LastOrDefault());
    }

    public async Task DownloadImageAsync(
        string url,
        string savePath,
        string? scrambleId,
        bool decode,
        string? targetSuffix,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddImageHeaders(request);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
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

        foreach (var domain in await GetApiDomainsAsync(cancellationToken).ConfigureAwait(false))
        {
            var url = "https://" + domain + urlPath;
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddApiHeaders(request, token, tokenParam);
            try
            {
                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
                return scrambleId;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException("获取 scramble_id 失败: " + photoId, lastError);
    }

    public void Dispose()
    {
        _http.Dispose();
        _domainInitLock.Dispose();
    }

    private async Task<JsonObject> ApiGetAsync(
        string path,
        Dictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        var urlPath = AppendQuery(path, parameters);
        var ts = UnixTimestamp().ToString(CultureInfo.InvariantCulture);
        var (token, tokenParam) = JmCrypto.TokenAndTokenParam(ts);
        Exception? lastError = null;

        foreach (var domain in await GetApiDomainsAsync(cancellationToken).ConfigureAwait(false))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://" + domain + urlPath);
            AddApiHeaders(request, token, tokenParam);

            try
            {
                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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

                return dataNode;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException("JM API 请求失败: " + path, lastError);
    }

    private async Task<List<string>> GetApiDomainsAsync(CancellationToken cancellationToken)
    {
        if (_domainsInitialized)
        {
            return _apiDomains;
        }

        await _domainInitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_domainsInitialized)
            {
                return _apiDomains;
            }

            foreach (var url in ApiDomainServerUrls)
            {
                try
                {
                    var text = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                    text = TrimLeadingNonAscii(text);
                    var decoded = JmCrypto.DecodeResponseData(text, string.Empty, JmCrypto.ApiDomainServerSecret);
                    var node = JsonNode.Parse(decoded) as JsonObject;
                    var servers = node?["Server"] as JsonArray;
                    var list = servers?
                        .Select(item => item?.GetValue<string>())
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Select(value => value!)
                        .ToList();

                    if (list is { Count: > 0 })
                    {
                        _apiDomains = list;
                        break;
                    }
                }
                catch
                {
                    // Domain update is best-effort; built-in domains remain as fallback.
                }
            }

            _domainsInitialized = true;
            return _apiDomains;
        }
        finally
        {
            _domainInitLock.Release();
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
        yield return BuildImageUrl(photo, imageName);

        foreach (var domain in ImageDomains)
        {
            if (!domain.Equals(photo.ImageDomain, StringComparison.OrdinalIgnoreCase))
            {
                yield return $"https://{domain}/media/photos/{photo.Id}/{imageName}";
            }
        }
    }

    private string PickImageDomain()
    {
        lock (_random)
        {
            return ImageDomains[_random.Next(ImageDomains.Length)];
        }
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
