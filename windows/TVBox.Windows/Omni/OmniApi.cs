using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TVBoxForWindows.Core;
using TVBoxForWindows.Models;
using TVBoxForWindows.Net;

namespace TVBoxForWindows.Omni;

/// <summary>网络/协议错误（移植自 OmniApi.OmniException），Code 为 HTTP 状态码，-1 表示网络异常。</summary>
public sealed class OmniException : Exception
{
    public int Code { get; }

    public OmniException(int code, string message) : base(message) => Code = code;

    public bool IsAuthError => Code == 401 || Code == 403;
}

/// <summary>CloudTV App API 客户端（移植自 omni/OmniApi.java）：登录换取 Bearer token，其余接口均返回 {success,data}。</summary>
public static class OmniApi
{
    const int LoginTimeoutMs = 8000;
    const int ConfigTimeoutMs = 8000;
    const int ApiTimeoutMs = 30000;

    /// <summary>令牌失效（401/403）时触发，由应用壳回到登录页。</summary>
    public static event Action AuthExpired;
    static long _authExpiredAt;

    static void NotifyAuthExpired()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _authExpiredAt) < 3000) return;
        Interlocked.Exchange(ref _authExpiredAt, now);
        try { AuthExpired?.Invoke(); } catch { }
    }

    // ---------------------------------------------------------------- 响应模型

    public sealed class LoginResult
    {
        public string Token { get; set; } = "";
        public string UserId { get; set; } = "";
        public string UserName { get; set; } = "";
    }

    sealed class LoginData
    {
        [JsonPropertyName("token")] public string Token { get; set; }
        [JsonPropertyName("user")] public LoginUser User { get; set; }
    }

    sealed class LoginUser
    {
        [JsonPropertyName("id")] public string Id { get; set; }
        [JsonPropertyName("username")] public string Username { get; set; }
    }

    public sealed class OmniLibrary
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("searchable")] public bool Searchable { get; set; }
        [JsonPropertyName("homeCategory")] public bool HomeCategory { get; set; }
    }

    public sealed class OmniCategory
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("libraryId")] public string LibraryId { get; set; } = "";
        [JsonPropertyName("libraryName")] public string LibraryName { get; set; } = "";
    }

    public sealed class OmniItem
    {
        [JsonPropertyName("sourceType")] public string SourceType { get; set; } = "spider";
        [JsonPropertyName("libraryId")] public string LibraryId { get; set; } = "";
        [JsonPropertyName("libraryName")] public string LibraryName { get; set; } = "";
        [JsonPropertyName("videoId")] public string VideoId { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("poster")] public string Poster { get; set; } = "";
        [JsonPropertyName("remark")] public string Remark { get; set; } = "";
        [JsonPropertyName("year")] public string Year { get; set; } = "";
        [JsonPropertyName("score")] public string Score { get; set; } = "";
        [JsonPropertyName("typeName")] public string TypeName { get; set; } = "";
    }

    public sealed class OmniItemPage
    {
        [JsonPropertyName("items")] public List<OmniItem> Items { get; set; } = new();
        [JsonPropertyName("page")] public int Page { get; set; } = 1;
        [JsonPropertyName("pageCount")] public int PageCount { get; set; } = 1;
        [JsonPropertyName("total")] public int Total { get; set; }
    }

    public sealed class OmniResumeItem
    {
        [JsonPropertyName("libraryId")] public string LibraryId { get; set; } = "";
        [JsonPropertyName("libraryName")] public string LibraryName { get; set; } = "";
        [JsonPropertyName("videoId")] public string VideoId { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("poster")] public string Poster { get; set; } = "";
        [JsonPropertyName("playId")] public string PlayId { get; set; } = "";
        [JsonPropertyName("episodeName")] public string EpisodeName { get; set; } = "";
        [JsonPropertyName("positionMs")] public long PositionMs { get; set; }
        [JsonPropertyName("durationMs")] public long DurationMs { get; set; }
        [JsonPropertyName("updatedAt")] public long UpdatedAt { get; set; }
    }

    public sealed class OmniRecommendItem
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("poster")] public string Poster { get; set; } = "";
        [JsonPropertyName("subtitle")] public string Subtitle { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("rating")] public double Rating { get; set; }
        [JsonPropertyName("isNew")] public bool IsNew { get; set; }
        [JsonPropertyName("sourceType")] public string SourceType { get; set; } = "";
        [JsonPropertyName("libraryId")] public string LibraryId { get; set; } = "";
        [JsonPropertyName("videoId")] public string VideoId { get; set; } = "";
    }

    public sealed class OmniRecommendSection
    {
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("items")] public List<OmniRecommendItem> Items { get; set; } = new();
    }

    public sealed class OmniRecommendHome
    {
        [JsonPropertyName("mode")] public string Mode { get; set; } = "";
        [JsonPropertyName("showBanner")] public bool ShowBanner { get; set; }
        [JsonPropertyName("banner")] public OmniRecommendItem Banner { get; set; }
        [JsonPropertyName("banners")] public List<OmniRecommendItem> Banners { get; set; } = new();
        [JsonPropertyName("sections")] public List<OmniRecommendSection> Sections { get; set; } = new();
    }

    public sealed class DoubanPic
    {
        [JsonPropertyName("large")] public string Large { get; set; } = "";
        [JsonPropertyName("normal")] public string Normal { get; set; } = "";
    }

    public sealed class DoubanRating
    {
        [JsonPropertyName("value")] public double Value { get; set; }
    }

    public sealed class DoubanItem
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("pic")] public DoubanPic Pic { get; set; }
        [JsonPropertyName("rating")] public DoubanRating Rating { get; set; }
    }

    public sealed class DoubanPage
    {
        [JsonPropertyName("items")] public List<DoubanItem> Items { get; set; } = new();
        [JsonPropertyName("total")] public int Total { get; set; }
    }

    public sealed class OmniHome
    {
        [JsonPropertyName("homeMode")] public string HomeMode { get; set; } = "";
        [JsonPropertyName("libraries")] public List<OmniLibrary> Libraries { get; set; } = new();
        [JsonPropertyName("categories")] public List<OmniCategory> Categories { get; set; } = new();
        [JsonPropertyName("resume")] public List<OmniResumeItem> Resume { get; set; } = new();
        [JsonPropertyName("recommend")] public List<OmniRecommendItem> Recommend { get; set; } = new();
    }

    public sealed class OmniEpisode
    {
        [JsonPropertyName("index")] public int Index { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("playId")] public string PlayId { get; set; } = "";
    }

    public sealed class OmniPlaySource
    {
        [JsonPropertyName("index")] public int Index { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("episodes")] public List<OmniEpisode> Episodes { get; set; } = new();
    }

    public sealed class OmniDetail
    {
        [JsonPropertyName("libraryId")] public string LibraryId { get; set; } = "";
        [JsonPropertyName("libraryName")] public string LibraryName { get; set; } = "";
        [JsonPropertyName("videoId")] public string VideoId { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("poster")] public string Poster { get; set; } = "";
        [JsonPropertyName("content")] public string Content { get; set; } = "";
        [JsonPropertyName("year")] public string Year { get; set; } = "";
        [JsonPropertyName("area")] public string Area { get; set; } = "";
        [JsonPropertyName("actors")] public string Actors { get; set; } = "";
        [JsonPropertyName("director")] public string Director { get; set; } = "";
        [JsonPropertyName("remark")] public string Remark { get; set; } = "";
        [JsonPropertyName("score")] public string Score { get; set; } = "";
        [JsonPropertyName("playSources")] public List<OmniPlaySource> PlaySources { get; set; } = new();
    }

    public sealed class OmniPlayUrl
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
    }

    public sealed class OmniPlayInfo
    {
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("urls")] public List<OmniPlayUrl> Urls { get; set; } = new();
        [JsonPropertyName("header")] public Dictionary<string, string> Header { get; set; } = new();
        [JsonPropertyName("flag")] public string Flag { get; set; } = "";
        [JsonPropertyName("parse")] public int Parse { get; set; }
        [JsonPropertyName("danmaku")] public List<OmniDanmaku> Danmaku { get; set; } = new();
    }

    public sealed class OmniDanmaku
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("url")] public string Url { get; set; } = "";
    }

    public sealed class OmniLocalSite
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("api")] public string Api { get; set; } = "";
        [JsonPropertyName("type")] public int Type { get; set; }
        [JsonPropertyName("priority")] public int Priority { get; set; }
        [JsonPropertyName("isHomeSearch")] public bool IsHomeSearch { get; set; } = true;
        [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    }

    public sealed class OmniLocalSpider
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("language")] public string Language { get; set; } = "";
        [JsonPropertyName("scriptUrl")] public string ScriptUrl { get; set; } = "";
        [JsonPropertyName("scriptMd5")] public string ScriptMd5 { get; set; } = "";
        [JsonPropertyName("priority")] public int Priority { get; set; }
        [JsonPropertyName("isHomeSearch")] public bool IsHomeSearch { get; set; } = true;
        [JsonPropertyName("clientCompatible")] public bool ClientCompatible { get; set; } = true;
    }

    public sealed class OmniLocalSources
    {
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("concurrency")] public int Concurrency { get; set; } = 4;
        [JsonPropertyName("sites")] public List<OmniLocalSite> Sites { get; set; } = new();
        [JsonPropertyName("spiders")] public List<OmniLocalSpider> Spiders { get; set; } = new();
    }

    public sealed class OmniLocalSyncResult
    {
        public bool NotModified { get; set; }
        public string Etag { get; set; } = "";
        public OmniLocalSources Sources { get; set; }
    }

    public sealed class IptvSource
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("is_default")] public int IsDefault { get; set; }
        [JsonPropertyName("header")] public string Header { get; set; } = "";
    }

    public sealed class IptvPlayUrl
    {
        [JsonPropertyName("url")] public string Url { get; set; } = "";
        [JsonPropertyName("line")] public string Line { get; set; } = "";
        [JsonPropertyName("ua")] public string Ua { get; set; } = "";
    }

    public sealed class IptvChannel
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("logo")] public string Logo { get; set; } = "";
        [JsonPropertyName("format")] public string Format { get; set; } = "";
        [JsonPropertyName("playUrls")] public List<IptvPlayUrl> PlayUrls { get; set; } = new();
    }

    public sealed class IptvGroup
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("channels")] public List<IptvChannel> Channels { get; set; } = new();
    }

    public sealed class IptvChannels
    {
        [JsonPropertyName("groups")] public List<IptvGroup> Groups { get; set; } = new();
    }

    public sealed class FavoriteItem
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("vodId")] public string VodId { get; set; } = "";
        [JsonPropertyName("sourceId")] public string SourceId { get; set; } = "";
        [JsonPropertyName("sourceName")] public string SourceName { get; set; } = "";
        [JsonPropertyName("coverImage")] public string CoverImage { get; set; } = "";
        [JsonPropertyName("remark")] public string Remark { get; set; } = "";
        [JsonPropertyName("rating")] public double Rating { get; set; }
    }

    public sealed class FavoritePage
    {
        public List<FavoriteItem> Items { get; set; } = new();
        public int Total { get; set; }
    }

    public sealed class FollowItem
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("posterPath")] public string PosterPath { get; set; } = "";
        [JsonPropertyName("backdropPath")] public string BackdropPath { get; set; } = "";
        [JsonPropertyName("mediaType")] public string MediaType { get; set; } = "";
        [JsonPropertyName("tmdbId")] public int TmdbId { get; set; }
        [JsonPropertyName("currentEpisodeNumber")] public int? CurrentEpisodeNumber { get; set; }
        [JsonPropertyName("nextEpisodeAirDate")] public string NextEpisodeAirDate { get; set; } = "";
        [JsonPropertyName("seasonNumber")] public int SeasonNumber { get; set; }
    }

    // ---------------------------------------------------------------- 基础请求

    static Dictionary<string, string> Headers(bool auth)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "application/json",
        };
        if (auth)
        {
            if (!OmniConfig.IsLoggedIn) throw new OmniException(401, "登录已失效，请重新登录");
            headers["Authorization"] = "Bearer " + OmniConfig.Token;
        }
        return headers;
    }

    static async Task<OkResponse> ExecuteAbsoluteAsync(string method, string url, Dictionary<string, string> query, object body, bool auth, int timeoutMs, Dictionary<string, string> extraHeaders = null)
    {
        var payload = body == null ? null : Encoding.UTF8.GetBytes(JsonUtil.Serialize(body));
        var headers = Headers(auth);
        if (extraHeaders != null)
            foreach (var kv in extraHeaders) headers[kv.Key] = kv.Value;
        try
        {
            return await HttpUtil.Execute(method, url, headers, query, payload,
                body == null ? null : "application/json; charset=utf-8", true, timeoutMs);
        }
        catch (Exception e) when (e is not OmniException)
        {
            throw new OmniException(-1, "网络连接失败：" + e.Message);
        }
    }

    static string ParseMessage(string text, int code)
    {
        if (!string.IsNullOrWhiteSpace(text))
        {
            var node = JsonUtil.Parse(text);
            var message = JsonUtil.SafeString(node, "message");
            if (!string.IsNullOrEmpty(message)) return message;
            var error = JsonUtil.SafeString(node, "error");
            if (!string.IsNullOrEmpty(error)) return error;
        }
        if (code == 401 || code == 403) return "登录已失效，请重新登录";
        if (code == 404) return "接口不存在（服务端版本过旧？）";
        return "请求失败（HTTP " + code + "）";
    }

    /// <summary>发送请求并取 data 节点；失败抛 OmniException。</summary>
    static async Task<JsonNode> DataAsync(string method, string path, Dictionary<string, string> query = null,
        object body = null, bool auth = true, int timeoutMs = ApiTimeoutMs)
    {
        var response = await ExecuteAbsoluteAsync(method, OmniConfig.BaseUrl + path, query, body, auth, timeoutMs);
        var text = response.Text();
        if (response.Code is 401 or 403) NotifyAuthExpired();
        if (response.Code < 200 || response.Code >= 300)
            throw new OmniException(response.Code, ParseMessage(text, response.Code));
        var root = JsonUtil.Parse(text) ?? throw new OmniException(-2, "服务器返回了无法解析的数据");
        var success = root["success"];
        if (success is JsonValue value && value.TryGetValue<bool>(out var ok) && !ok)
        {
            var code = -2;
            if (root["code"] is JsonValue codeValue && codeValue.TryGetValue<int>(out var parsedCode)) code = parsedCode;
            var message = JsonUtil.SafeString(root, "message");
            throw new OmniException(code, string.IsNullOrEmpty(message) ? "请求失败" : message);
        }
        return root["data"];
    }

    static T ObjectOf<T>(JsonNode data) where T : class =>
        data == null ? null : ModelJson.Parse<T>(data);

    static List<T> ListOf<T>(JsonNode data)
    {
        try { return data == null ? new() : data.Deserialize<List<T>>(ModelJson.Options) ?? new(); }
        catch { return new(); }
    }

    // ---------------------------------------------------------------- 登录 / 探活

    public static async Task<LoginResult> LoginAsync(string baseUrl, string username, string password)
    {
        var normalized = OmniConfig.Normalize(baseUrl);
        if (string.IsNullOrEmpty(normalized)) throw new OmniException(-2, "请输入服务器地址");
        var url = normalized + "/api/app/v1/auth/login";
        var payload = Encoding.UTF8.GetBytes(JsonUtil.Serialize(new { username, password }));
        OkResponse response;
        try
        {
            response = await HttpUtil.Execute("POST", url,
                new Dictionary<string, string> { ["Accept"] = "application/json" },
                null, payload, "application/json; charset=utf-8", true, LoginTimeoutMs);
        }
        catch (Exception e)
        {
            throw new OmniException(-1, "网络连接失败：" + e.Message);
        }
        var text = response.Text();
        if (response.Code < 200 || response.Code >= 300)
            throw new OmniException(response.Code, ParseMessage(text, response.Code));
        var root = JsonUtil.Parse(text) ?? throw new OmniException(-2, "服务器返回了无法解析的数据");
        var data = ObjectOf<LoginData>(root["data"]);
        if (data == null || string.IsNullOrEmpty(data.Token))
            throw new OmniException(-2, "服务端未返回有效令牌");
        return new LoginResult
        {
            Token = data.Token,
            UserId = data.User?.Id ?? "",
            UserName = string.IsNullOrEmpty(data.User?.Username) ? username : data.User.Username,
        };
    }

    public static async Task HealthAsync(string baseUrl)
    {
        var normalized = OmniConfig.Normalize(baseUrl);
        if (string.IsNullOrEmpty(normalized)) throw new OmniException(-2, "请输入服务器地址");
        var response = await ExecuteAbsoluteAsync("GET", normalized + "/health", null, null, false, LoginTimeoutMs);
        if (response.Code < 200 || response.Code >= 300)
            throw new OmniException(response.Code, ParseMessage(response.Text(), response.Code));
    }

    // ---------------------------------------------------------------- 首页 / 分类

    public static async Task<OmniHome> HomeAsync() =>
        ObjectOf<OmniHome>(await DataAsync("GET", "/api/app/v1/home"));

    public static async Task<OmniRecommendHome> RecommendAsync()
    {
        var home = ObjectOf<OmniRecommendHome>(await DataAsync("GET", "/api/app/v1/recommend"));
        if (home != null) home.Banners ??= new();
        return home;
    }

    public static async Task<OmniItemPage> CategoryAsync(string libraryId, string categoryId, int page)
    {
        var query = new Dictionary<string, string>
        {
            ["libraryId"] = libraryId ?? "",
            ["categoryId"] = categoryId ?? "",
            ["page"] = Math.Max(1, page).ToString(),
        };
        return ObjectOf<OmniItemPage>(await DataAsync("GET", "/api/app/v1/category", query)) ?? new OmniItemPage();
    }

    public static async Task<DoubanPage> DoubanCategoryAsync(string primary, string secondary, string type, int start)
    {
        var query = new Dictionary<string, string>
        {
            ["primaryCategory"] = primary ?? "",
            ["secondaryCategory"] = secondary ?? "",
            ["tertiaryType"] = type ?? "",
            ["start"] = Math.Max(0, start).ToString(),
        };
        return ObjectOf<DoubanPage>(await DataAsync("GET", "/api/douban-recommendation", query)) ?? new DoubanPage();
    }

    // ---------------------------------------------------------------- 详情 / 播放

    public static async Task<OmniDetail> DetailAsync(string sourceType, string libraryId, string videoId)
    {
        var query = new Dictionary<string, string>
        {
            ["sourceType"] = sourceType ?? "",
            ["libraryId"] = libraryId ?? "",
            ["videoId"] = videoId ?? "",
        };
        return ObjectOf<OmniDetail>(await DataAsync("GET", "/api/app/v1/media", query));
    }

    public static async Task<OmniPlayInfo> PlayAsync(string sourceType, string libraryId, string videoId, int line, int episode)
    {
        var query = new Dictionary<string, string>
        {
            ["sourceType"] = sourceType ?? "",
            ["libraryId"] = libraryId ?? "",
            ["videoId"] = videoId ?? "",
            ["line"] = line.ToString(),
            ["episode"] = episode.ToString(),
        };
        return ObjectOf<OmniPlayInfo>(await DataAsync("GET", "/api/app/v1/play", query));
    }

    public static async Task<List<OmniDanmaku>> DanmakuAsync(string fileName)
    {
        var data = await DataAsync("POST", "/api/app/v1/danmaku", null, new { fileName = fileName ?? "" });
        return ListOf<OmniDanmaku>(data).Where(item => !string.IsNullOrEmpty(item?.Url)).ToList();
    }

    // ---------------------------------------------------------------- 搜索 / 继续观看

    public static async Task<OmniItemPage> SearchAsync(string keyword, int page, bool includeSites)
    {
        var query = new Dictionary<string, string>
        {
            ["q"] = keyword ?? "",
            ["page"] = Math.Max(1, page).ToString(),
        };
        if (includeSites) query["includeSites"] = "1";
        return ObjectOf<OmniItemPage>(await DataAsync("GET", "/api/app/v1/search", query)) ?? new OmniItemPage();
    }

    public static async Task<List<OmniResumeItem>> ResumeAsync(int limit, int timeoutMs = ApiTimeoutMs) =>
        ListOf<OmniResumeItem>(await DataAsync("GET", "/api/app/v1/resume",
            new Dictionary<string, string> { ["limit"] = Math.Max(1, limit).ToString() }, null, true, timeoutMs));

    // ---------------------------------------------------------------- 直播 IPTV

    public static async Task<List<IptvSource>> IptvSourcesAsync() =>
        ListOf<IptvSource>(await DataAsync("GET", "/api/iptv/enabled"));

    public static async Task<List<IptvGroup>> IptvChannelsAsync(string sourceId) =>
        ObjectOf<IptvChannels>(await DataAsync("GET", "/api/iptv/" + Uri.EscapeDataString(sourceId ?? "") + "/channels"))?.Groups
        ?? new List<IptvGroup>();

    // ---------------------------------------------------------------- 收藏 / 追剧

    public static async Task<FavoritePage> FavoritesAsync(int page, int pageSize)
    {
        var data = await DataAsync("GET", "/api/favorite", new Dictionary<string, string>
        {
            ["page"] = Math.Max(1, page).ToString(),
            ["pageSize"] = Math.Max(1, pageSize).ToString(),
        });
        var result = new FavoritePage();
        if (data?["list"] is JsonArray list) result.Items = ListOf<FavoriteItem>(list);
        if (data?["total"] is JsonValue total && total.TryGetValue<int>(out var count)) result.Total = count;
        return result;
    }

    public static async Task<string> FavoriteIdAsync(string libraryId, string videoId)
    {
        var data = await DataAsync("GET", "/api/favorite/check", new Dictionary<string, string>
        {
            ["vodId"] = videoId ?? "",
            ["sourceId"] = libraryId ?? "",
        });
        if (data?["isFavorited"] is JsonValue value && value.TryGetValue<bool>(out var favorited) && favorited)
            return data["favorite"]?["id"]?.ToString() ?? "";
        return "";
    }

    public static async Task AddFavoriteAsync(string libraryId, string videoId, string title,
        string poster, string remark, double rating)
    {
        await DataAsync("POST", "/api/favorite", null, new
        {
            title = title ?? "",
            vodId = videoId ?? "",
            sourceId = libraryId ?? "",
            coverImage = poster ?? "",
            remark = remark ?? "",
            rating,
            extraInfo = "windows",
        });
    }

    public static async Task RemoveFavoriteAsync(string favoriteId)
    {
        if (string.IsNullOrEmpty(favoriteId)) return;
        await DataAsync("DELETE", "/api/favorite/" + Uri.EscapeDataString(favoriteId));
    }

    public static async Task<List<FollowItem>> FollowsAsync(int limit)
    {
        var data = await DataAsync("GET", "/api/watch-follow", new Dictionary<string, string>
        {
            ["page"] = "1",
            ["size"] = Math.Max(1, limit).ToString(),
        });
        return data?["list"] is JsonArray list ? ListOf<FollowItem>(list) : new();
    }

    // ---------------------------------------------------------------- 本地抓取配置

    /// <summary>带 ETag 条件请求；304 时 NotModified=true，直接复用本地缓存。</summary>
    public static async Task<OmniLocalSyncResult> LocalSourcesAsync(string etag)
    {
        var headers = Headers(true);
        if (!string.IsNullOrEmpty(etag)) headers["If-None-Match"] = etag;
        OkResponse response;
        try
        {
            response = await HttpUtil.Execute("GET", OmniConfig.BaseUrl + "/api/app/v1/local-sources",
                headers, null, null, null, true, ConfigTimeoutMs);
        }
        catch (Exception e)
        {
            throw new OmniException(-1, "网络连接失败：" + e.Message);
        }
        var result = new OmniLocalSyncResult();
        if (response.Headers.TryGetValue("ETag", out var etags) && etags.Count > 0)
            result.Etag = etags[0];
        if (response.Code == 304)
        {
            result.NotModified = true;
            return result;
        }
        var text = response.Text();
        if (response.Code is 401 or 403) NotifyAuthExpired();
        if (response.Code < 200 || response.Code >= 300)
            throw new OmniException(response.Code, ParseMessage(text, response.Code));
        var root = JsonUtil.Parse(text) ?? throw new OmniException(-2, "服务器返回了无法解析的数据");
        result.Sources = ObjectOf<OmniLocalSources>(root["data"]);
        return result;
    }

    /// <summary>下载爬虫脚本：相对路径走当前服务器的 Bearer 鉴权，外链不带令牌；按原始字节下载以保持 MD5 一致。</summary>
    public static async Task<byte[]> DownloadSpiderScriptAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new OmniException(-2, "脚本地址为空");
        var external = path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                       path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        var url = external ? path : OmniConfig.BaseUrl + (path.StartsWith('/') ? path : "/" + path);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "application/javascript, text/plain, */*",
        };
        if (!external)
        {
            if (!OmniConfig.IsLoggedIn) throw new OmniException(401, "登录已失效，请重新登录");
            headers["Authorization"] = "Bearer " + OmniConfig.Token;
        }
        var response = await ExecuteAbsoluteAsync("GET", url, null, null, false, ApiTimeoutMs, headers);
        if (response.Code is 401 or 403) NotifyAuthExpired();
        if (response.Code < 200 || response.Code >= 300)
            throw new OmniException(response.Code, ParseMessage(response.Text(), response.Code));
        return response.Body;
    }

    // ---------------------------------------------------------------- 进度

    public static async Task ReportProgressAsync(string libraryId, string videoId, string playId, string title,
        string poster, string episodeName, long positionMs, long durationMs)
    {
        if (string.IsNullOrEmpty(libraryId) || string.IsNullOrEmpty(videoId) || string.IsNullOrEmpty(playId)) return;
        try
        {
            await DataAsync("POST", "/api/app/v1/progress", null, new
            {
                libraryId,
                videoId,
                playId,
                title = title ?? "",
                poster = poster ?? "",
                episodeName = episodeName ?? "",
                positionMs = Math.Max(0, positionMs),
                durationMs = Math.Max(0, durationMs),
            });
        }
        catch (Exception e)
        {
            Logger.E("OmniApi.progress", e.Message);
        }
    }
}
