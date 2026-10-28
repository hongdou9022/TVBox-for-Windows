using System.Text.Json.Nodes;
using TVBoxForWindows.Core;
using TVBoxForWindows.Net;

namespace TVBoxForWindows.Omni;

/// <summary>影视站本地抓取引擎（移植自 omni/local/LocalSiteEngine.java）：直连采集站 MacCMS JSON 接口，单站失败时调用方回退服务端。</summary>
public sealed class LocalSiteEngine
{
    const string Tag = "LocalSiteEngine";
    const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36";
    const int SearchDeadlineMs = 8000;
    const int SearchStreamDeadlineMs = 20000;
    const int SearchSiteTimeoutMs = 3500;
    const int DetailTimeoutMs = 6000;
    const long DetailTtlMs = 2 * 60 * 1000;
    const long CooldownMs = 5 * 60 * 1000;

    public static LocalSiteEngine Instance { get; } = new();

    readonly Dictionary<string, (OmniApi.OmniDetail Detail, long Expires)> _details = new();
    readonly Dictionary<string, long> _cooldown = new();

    public async Task<List<OmniApi.OmniItem>> SearchAsync(string keyword, int page)
    {
        var sources = LocalSourceStore.Instance.Cached;
        var sites = (sources?.Sites ?? new()).Where(site => !string.IsNullOrEmpty(site?.Api)).ToList();
        if (sites.Count == 0) return new();
        var userConcurrency = Setting.GetInt("omni_concurrency");
        var concurrency = userConcurrency > 0 ? userConcurrency : Math.Max(12, sources.Concurrency);
        concurrency = Math.Clamp(concurrency, 1, 16);
        using var gate = new SemaphoreSlim(concurrency);
        var tasks = sites.Select(site => SearchSiteAsync(site, keyword, page, gate)).ToList();
        await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(SearchDeadlineMs));
        var items = tasks.Where(task => task.IsCompletedSuccessfully).SelectMany(task => task.Result).ToList();
        return items
            .GroupBy(item => item.LibraryId + "|" + item.VideoId)
            .Select(group => group.First())
            .OrderBy(item => MatchRank(keyword, item.Title))
            .ToList();
    }

    async Task<List<OmniApi.OmniItem>> SearchSiteAsync(OmniApi.OmniLocalSite site, string keyword, int page, SemaphoreSlim gate)
    {
        await gate.WaitAsync();
        try
        {
            return await SearchSiteCoreAsync(site, keyword, page);
        }
        finally
        {
            gate.Release();
        }
    }

    async Task<List<OmniApi.OmniItem>> SearchSiteCoreAsync(OmniApi.OmniLocalSite site, string keyword, int page)
    {
        lock (_cooldown)
        {
            if (_cooldown.TryGetValue(site.Id, out var until) && until > Environment.TickCount64)
                return new();
        }
        foreach (var variant in SearchVariants(keyword, page))
        {
            try
            {
                var json = await RequestAsync(site, variant, SearchSiteTimeoutMs);
                if (json?["list"] is not JsonArray list || list.Count == 0) continue;
                lock (_cooldown) _cooldown.Remove(site.Id);
                return ParseItems(site, list);
            }
            catch (Exception e)
            {
                Logger.E(Tag, "search " + site.Name + ": " + e.Message);
            }
        }
        lock (_cooldown) _cooldown[site.Id] = Environment.TickCount64 + CooldownMs;
        return new();
    }

    /// <summary>全站搜索：搜索全部启用站点，逐源回调；isHomeSearch 只用于范围菜单，不限制搜索。</summary>
    public async Task SearchStreamingAsync(string keyword, int page, Action<OmniApi.OmniLocalSite, List<OmniApi.OmniItem>> onSite, CancellationToken ct = default)
    {
        var sources = LocalSourceStore.Instance.Cached;
        var sites = (sources?.Sites ?? new())
            .Where(site => !string.IsNullOrEmpty(site?.Api))
            .OrderByDescending(site => site.Priority)
            .ThenBy(site => site.Name)
            .ToList();
        if (sites.Count == 0) return;

        var userConcurrency = Setting.GetInt("omni_concurrency");
        var concurrency = userConcurrency > 0 ? userConcurrency : Math.Max(12, sources.Concurrency);
        concurrency = Math.Clamp(concurrency, 1, 16);
        using var gate = new SemaphoreSlim(concurrency);
        var tasks = sites.Select(async site =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (ct.IsCancellationRequested) return;
                var items = await SearchSiteCoreAsync(site, keyword, page);
                if (items.Count > 0 && !ct.IsCancellationRequested) onSite(site, items);
            }
            catch (Exception e)
            {
                Logger.E(Tag, "stream " + site.Name + ": " + e.Message);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(SearchStreamDeadlineMs, ct));
    }

    static IEnumerable<Dictionary<string, string>> SearchVariants(string keyword, int page)
    {
        yield return new() { ["ac"] = "detail", ["wd"] = keyword, ["pg"] = page.ToString() };
        yield return new() { ["ac"] = "list", ["wd"] = keyword, ["pg"] = page.ToString() };
        yield return new() { ["ac"] = "videolist", ["wd"] = keyword, ["pg"] = page.ToString() };
    }

    public async Task<OmniApi.OmniDetail> DetailAsync(string sourceType, string libraryId, string videoId)
    {
        if (sourceType != "site" || string.IsNullOrEmpty(libraryId) || string.IsNullOrEmpty(videoId)) return null;
        var site = FindSite(libraryId);
        if (site == null) return null;
        var key = libraryId + "|" + videoId;
        lock (_details)
        {
            if (_details.TryGetValue(key, out var cached) && cached.Expires > Environment.TickCount64)
                return cached.Detail;
        }
        try
        {
            var json = await RequestAsync(site, new() { ["ac"] = "detail", ["ids"] = videoId }, DetailTimeoutMs);
            var vod = (json?["list"] as JsonArray)?.FirstOrDefault() as JsonObject;
            if (vod == null) return null;
            var detail = new OmniApi.OmniDetail
            {
                LibraryId = site.Id,
                LibraryName = site.Name,
                VideoId = JsonUtil.SafeString(vod, "vod_id"),
                Title = JsonUtil.SafeString(vod, "vod_name"),
                Poster = ImageUrl(site.Api, JsonUtil.SafeString(vod, "vod_pic")),
                Content = JsonUtil.SafeString(vod, "vod_content"),
                Year = JsonUtil.SafeString(vod, "vod_year"),
                Area = JsonUtil.SafeString(vod, "vod_area"),
                Actors = JsonUtil.SafeString(vod, "vod_actor"),
                Director = JsonUtil.SafeString(vod, "vod_director"),
                Remark = NormalizeRemark(JsonUtil.SafeString(vod, "vod_remarks")),
                Score = ScoreOf(vod),
                PlaySources = ParsePlaySources(vod),
            };
            lock (_details) _details[key] = (detail, Environment.TickCount64 + DetailTtlMs);
            return detail;
        }
        catch (Exception e)
        {
            Logger.E(Tag, "detail " + site.Name + ": " + e.Message);
            return null;
        }
    }

    public async Task<OmniApi.OmniPlayInfo> PlayAsync(string sourceType, string libraryId, string videoId, int line, int episode)
    {
        var detail = await DetailAsync(sourceType, libraryId, videoId);
        if (detail?.PlaySources == null || line < 0 || line >= detail.PlaySources.Count) return null;
        var source = detail.PlaySources[line];
        if (source?.Episodes == null || episode < 0 || episode >= source.Episodes.Count) return null;
        var target = source.Episodes[episode];
        if (string.IsNullOrEmpty(target.PlayId)) return null;

        var header = new Dictionary<string, string> { ["User-Agent"] = Ua };
        var site = FindSite(libraryId);
        if (site != null) header["Referer"] = OriginOf(site.Api);
        return new OmniApi.OmniPlayInfo
        {
            Url = target.PlayId,
            Flag = source.Name,
            Parse = 0,
            Header = header,
        };
    }

    OmniApi.OmniLocalSite FindSite(string id) =>
        LocalSourceStore.Instance.Cached?.Sites?.FirstOrDefault(site => site?.Id == id);

    static async Task<JsonObject> RequestAsync(OmniApi.OmniLocalSite site, Dictionary<string, string> query, int timeoutMs)
    {
        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = Ua,
            ["Accept"] = "application/json, text/plain, */*",
        };
        var response = await HttpUtil.Get(site.Api, headers, query, timeoutMs);
        if (response.Code < 200 || response.Code >= 300) return null;
        var text = response.Text(CharsetOf(response)).Trim().TrimStart('\uFEFF');
        if (!text.StartsWith('{')) return null;
        return JsonUtil.Parse(text) as JsonObject;
    }

    static string CharsetOf(OkResponse response)
    {
        if (response.Headers.TryGetValue("Content-Type", out var values))
        {
            var contentType = values.FirstOrDefault() ?? "";
            var index = contentType.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
            if (index >= 0) return contentType[(index + 8)..].Trim(' ', '"', ';');
        }
        return "UTF-8";
    }

    static List<OmniApi.OmniItem> ParseItems(OmniApi.OmniLocalSite site, JsonArray list)
    {
        var items = new List<OmniApi.OmniItem>();
        foreach (var node in list)
        {
            if (node is not JsonObject vod) continue;
            var videoId = JsonUtil.SafeString(vod, "vod_id");
            var title = JsonUtil.SafeString(vod, "vod_name");
            if (string.IsNullOrEmpty(videoId) || string.IsNullOrEmpty(title)) continue;
            items.Add(new OmniApi.OmniItem
            {
                SourceType = "site",
                LibraryId = site.Id,
                LibraryName = site.Name,
                VideoId = videoId,
                Title = title,
                Poster = ImageUrl(site.Api, JsonUtil.SafeString(vod, "vod_pic")),
                Remark = NormalizeRemark(JsonUtil.SafeString(vod, "vod_remarks")),
                Year = JsonUtil.SafeString(vod, "vod_year"),
                Score = ScoreOf(vod),
                TypeName = JsonUtil.SafeString(vod, "type_name"),
            });
        }
        return items;
    }

    static List<OmniApi.OmniPlaySource> ParsePlaySources(JsonObject vod)
    {
        var from = JsonUtil.SafeString(vod, "vod_play_from");
        var urls = JsonUtil.SafeString(vod, "vod_play_url");
        if (string.IsNullOrEmpty(urls)) return new();

        var froms = from.Split("$$$");
        var groups = urls.Split("$$$");
        var sources = new List<OmniApi.OmniPlaySource>();
        for (var i = 0; i < groups.Length; i++)
        {
            var lineName = i < froms.Length && !string.IsNullOrWhiteSpace(froms[i]) ? froms[i].Trim() : "线路" + (i + 1);
            var episodes = new List<OmniApi.OmniEpisode>();
            foreach (var entry in groups[i].Split('#'))
            {
                var cut = entry.IndexOf('$');
                if (cut < 0) continue;
                var name = entry[..cut].Trim();
                var url = entry[(cut + 1)..].Trim();
                if (!IsDirectPlay(url)) continue;
                episodes.Add(new OmniApi.OmniEpisode
                {
                    Index = episodes.Count,
                    Name = string.IsNullOrEmpty(name) ? "第" + (episodes.Count + 1) + "集" : name,
                    PlayId = url,
                });
            }
            if (episodes.Count == 0) continue;
            sources.Add(new OmniApi.OmniPlaySource
            {
                Index = sources.Count,
                Name = lineName,
                Episodes = episodes,
            });
        }
        return sources;
    }

    static bool IsDirectPlay(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        var path = url;
        var query = path.IndexOf('?');
        if (query >= 0) path = path[..query];
        var fragment = path.IndexOf('#');
        if (fragment >= 0) path = path[..fragment];
        path = path.ToLowerInvariant();
        return path.EndsWith(".m3u8") || path.EndsWith(".mp4");
    }

    static string ImageUrl(string api, string pic)
    {
        if (string.IsNullOrWhiteSpace(pic)) return "";
        if (pic.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            pic.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return pic;
        if (pic.StartsWith("//")) return "https:" + pic;
        return UrlUtil.Resolve(api, pic);
    }

    static string OriginOf(string api)
    {
        try
        {
            var uri = new Uri(api);
            return uri.Scheme + "://" + uri.Authority;
        }
        catch
        {
            return "";
        }
    }

    static int MatchRank(string keyword, string title)
    {
        if (string.IsNullOrEmpty(title)) return 3;
        var key = (keyword ?? "").Trim().ToLowerInvariant();
        var value = title.Trim().ToLowerInvariant();
        if (value == key) return 0;
        if (value.StartsWith(key)) return 1;
        if (value.Contains(key)) return 2;
        return 3;
    }

    static string ScoreOf(JsonObject vod)
    {
        var score = JsonUtil.SafeString(vod, "vod_douban_score");
        return string.IsNullOrEmpty(score) ? JsonUtil.SafeString(vod, "vod_score") : score;
    }

    static string NormalizeRemark(string remark)
    {
        if (string.IsNullOrWhiteSpace(remark)) return "";
        var text = remark.Trim();
        if (text.Contains("完结") || text.Contains("全集")) return "已完结";
        var match = System.Text.RegularExpressions.Regex.Match(text, @"第\s*(\d+)\s*集");
        if (match.Success) return "第" + match.Groups[1].Value + "集";
        match = System.Text.RegularExpressions.Regex.Match(text, @"更新(?:至|到)\s*(\d+)\s*集?");
        if (match.Success) return "第" + match.Groups[1].Value + "集";
        return text;
    }
}
