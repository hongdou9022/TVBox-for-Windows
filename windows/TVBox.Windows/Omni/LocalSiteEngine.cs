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
    const long StreamCacheTtlMs = 2 * 60 * 1000;
    const int StreamCacheMax = 200;

    public static LocalSiteEngine Instance { get; } = new();

    readonly Dictionary<string, (OmniApi.OmniDetail Detail, long Expires)> _details = new();
    readonly Dictionary<string, long> _cooldown = new();
    readonly Dictionary<string, (List<OmniApi.OmniItem> Items, long Expires)> _streamCache = new();

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
            return (await SearchSiteCoreAsync(site, keyword, page)).Items ?? new();
        }
        finally
        {
            gate.Release();
        }
    }

    async Task<(List<OmniApi.OmniItem> Items, string Error, long LatencyMs)> SearchSiteCoreAsync(
        OmniApi.OmniLocalSite site, string keyword, int page)
    {
        lock (_cooldown)
        {
            if (_cooldown.TryGetValue(site.Id, out var until) && until > Environment.TickCount64)
                return (new(), "", 0);
        }
        // 三种 MacCMS 搜索接口并行，单站总预算仍是 3.5s；
        // 任一接口先给出非空结果就立即返回，避免被慢接口拖住，死站也不再占用 3 倍时间。
        var tasks = SearchVariants(keyword, page)
            .Select(variant => SearchVariantAsync(site, variant))
            .ToList();
        var pending = new List<Task<(bool Responded, List<OmniApi.OmniItem> Items, string Error, long LatencyMs)>>(tasks);
        while (pending.Count > 0)
        {
            var done = await Task.WhenAny(pending);
            pending.Remove(done);
            var outcome = done.Result;
            if (!outcome.Responded || outcome.Items.Count == 0) continue;
            lock (_cooldown) _cooldown.Remove(site.Id);
            return (outcome.Items, "", outcome.LatencyMs);
        }
        var outcomes = tasks.Select(task => task.Result).ToList();
        var valid = outcomes.Where(outcome => outcome.Responded).ToList();
        if (valid.Count > 0)
        {
            lock (_cooldown) _cooldown.Remove(site.Id);
            return (new(), "", valid.Min(outcome => outcome.LatencyMs));
        }
        var error = outcomes.Select(outcome => outcome.Error)
            .FirstOrDefault(message => !string.IsNullOrEmpty(message)) ?? "请求失败";
        lock (_cooldown) _cooldown[site.Id] = Environment.TickCount64 + CooldownMs;
        return (new(), error, 0);
    }

    async Task<(bool Responded, List<OmniApi.OmniItem> Items, string Error, long LatencyMs)> SearchVariantAsync(
        OmniApi.OmniLocalSite site, Dictionary<string, string> variant)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var json = await RequestAsync(site, variant, SearchSiteTimeoutMs);
            watch.Stop();
            if (json == null) return (false, new(), "接口无响应", 0);
            if (json["list"] is not JsonArray list) return (false, new(), "返回格式异常", 0);
            return (true, ParseItems(site, list), "", watch.ElapsedMilliseconds);
        }
        catch (Exception e)
        {
            watch.Stop();
            Logger.E(Tag, "search " + site.Name + ": " + e.Message);
            return (false, new(), DescribeError(e), 0);
        }
    }

    static string DescribeError(Exception e) => e switch
    {
        TimeoutException or TaskCanceledException => "超时",
        HttpRequestException http => http.Message.StartsWith("HTTP ", StringComparison.Ordinal) ? http.Message : "连接失败",
        _ => "请求失败",
    };

    /// <summary>当前搜索范围下的站点（全站/指定站点）；范围菜单只影响这里，不影响默认全站。</summary>
    public List<OmniApi.OmniLocalSite> ScopedSites()
    {
        var sources = LocalSourceStore.Instance.Cached;
        var sites = (sources?.Sites ?? new())
            .Where(site => !string.IsNullOrEmpty(site?.Id) && !string.IsNullOrEmpty(site.Api) && !IsCooling(site.Id))
            .ToList();
        var scopeType = OmniConfig.SearchScopeType;
        if (scopeType == "site" && !string.IsNullOrEmpty(OmniConfig.SearchScopeId))
            sites = sites.Where(site => site.Id == OmniConfig.SearchScopeId).ToList();
        else if (scopeType == "spider")
            sites = new();
        return sites;
    }

    /// <summary>全站搜索：全部启用站点，按动态评分排序 + 缓存命中优先 + 滑动窗口逐源回调。
    /// 回调第三参为缓存命中标记；失败源通过 onFailure 上报名称与原因。</summary>
    public async Task SearchStreamingAsync(string keyword, int page,
        Action<OmniApi.OmniLocalSite, List<OmniApi.OmniItem>, bool> onSite,
        Action<string, string> onFailure = null, CancellationToken ct = default)
    {
        var ordered = ScopedSites()
            .OrderByDescending(site => OmniSourceScoreStore.EffectiveScore(site.Id, false))
            .ThenByDescending(site => site.Priority)
            .ThenBy(site => OmniSourceScoreStore.Latency(site.Id, false))
            .ThenBy(site => site.Name)
            .ToList();
        if (ordered.Count == 0) return;

        var queue = new Queue<OmniApi.OmniLocalSite>();
        foreach (var site in ordered)
        {
            if (TryGetStreamCache(StreamCacheKey(site, keyword, page), out var cached) && cached.Count > 0)
                onSite(site, cached, true);
            else
                queue.Enqueue(site);
        }
        if (queue.Count == 0) return;

        var userConcurrency = Setting.GetInt("omni_concurrency");
        var serverConcurrency = LocalSourceStore.Instance.Cached?.Concurrency ?? 8;
        var autoConcurrency = Math.Min(serverConcurrency > 0 ? serverConcurrency : 8, OmniConcurrencyTuner.Current);
        var concurrency = Math.Clamp(userConcurrency > 0 ? userConcurrency : autoConcurrency, 1, 16);

        using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        windowCts.CancelAfter(SearchStreamDeadlineMs);
        var stats = new RunStats();
        var running = new HashSet<Task>();
        while ((queue.Count > 0 || running.Count > 0) && !windowCts.IsCancellationRequested)
        {
            while (running.Count < concurrency && queue.Count > 0 && !windowCts.IsCancellationRequested)
            {
                var site = queue.Dequeue();
                running.Add(RunSiteAsync(site, keyword, page, onSite, onFailure, stats, windowCts.Token));
            }
            if (running.Count == 0) break;
            var completed = await Task.WhenAny(running);
            running.Remove(completed);
        }
        windowCts.Cancel();
        stats.Report();
    }

    sealed class RunStats
    {
        int _attempted;
        int _succeeded;
        long _latencySum;

        public void Add(bool success, long latencyMs)
        {
            Interlocked.Increment(ref _attempted);
            if (success) Interlocked.Increment(ref _succeeded);
            Interlocked.Add(ref _latencySum, Math.Max(0, latencyMs));
        }

        public void Report()
        {
            var attempted = Volatile.Read(ref _attempted);
            if (attempted == 0) return;
            var succeeded = Volatile.Read(ref _succeeded);
            var avgLatency = Volatile.Read(ref _latencySum) / (double)attempted;
            OmniConcurrencyTuner.Record(attempted, succeeded, avgLatency);
        }
    }

    async Task RunSiteAsync(OmniApi.OmniLocalSite site, string keyword, int page,
        Action<OmniApi.OmniLocalSite, List<OmniApi.OmniItem>, bool> onSite,
        Action<string, string> onFailure, RunStats stats, CancellationToken token)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var (items, error, latencyMs) = await SearchSiteCoreAsync(site, keyword, page);
            watch.Stop();
            var elapsed = string.IsNullOrEmpty(error) && latencyMs > 0
                ? Math.Min(watch.ElapsedMilliseconds, latencyMs)
                : watch.ElapsedMilliseconds;
            stats.Add(string.IsNullOrEmpty(error), elapsed);
            if (!string.IsNullOrEmpty(error))
            {
                OmniSourceScoreStore.Record(site.Id, site.Name, false, watch.ElapsedMilliseconds, 0, false);
                if (!token.IsCancellationRequested) onFailure?.Invoke(site.Name, error);
                return;
            }
            OmniSourceScoreStore.Record(site.Id, site.Name, false, elapsed, items.Count, true);
            if (items.Count > 0)
            {
                PutStreamCache(StreamCacheKey(site, keyword, page), items);
                if (!token.IsCancellationRequested) onSite(site, items, false);
            }
        }
        catch (Exception e)
        {
            watch.Stop();
            stats.Add(false, watch.ElapsedMilliseconds);
            OmniSourceScoreStore.Record(site.Id, site.Name, false, watch.ElapsedMilliseconds, 0, false);
            Logger.E(Tag, "stream " + site.Name + ": " + e.Message);
            if (!token.IsCancellationRequested) onFailure?.Invoke(site.Name, DescribeError(e));
        }
    }

    bool IsCooling(string id)
    {
        lock (_cooldown)
            return _cooldown.TryGetValue(id, out var until) && until > Environment.TickCount64;
    }

    static string StreamCacheKey(OmniApi.OmniLocalSite site, string keyword, int page) =>
        (LocalSourceStore.Instance.Cached?.Version ?? "") + "|" + site.Id + "|" + keyword + "|" + page;

    bool TryGetStreamCache(string key, out List<OmniApi.OmniItem> items)
    {
        lock (_streamCache)
        {
            if (_streamCache.TryGetValue(key, out var cached) && cached.Expires > Environment.TickCount64)
            {
                items = cached.Items;
                return true;
            }
            if (_streamCache.ContainsKey(key)) _streamCache.Remove(key);
        }
        items = null;
        return false;
    }

    void PutStreamCache(string key, List<OmniApi.OmniItem> items)
    {
        lock (_streamCache)
        {
            _streamCache[key] = (items, Environment.TickCount64 + StreamCacheTtlMs);
            if (_streamCache.Count > StreamCacheMax)
            {
                foreach (var pair in _streamCache.Where(pair => pair.Value.Expires <= Environment.TickCount64).Take(20).ToList())
                    _streamCache.Remove(pair.Key);
            }
        }
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
        if (response.Code < 200 || response.Code >= 300)
            throw new HttpRequestException("HTTP " + response.Code);
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
