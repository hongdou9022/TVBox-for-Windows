using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TVBoxForWindows.Core;

namespace TVBoxForWindows.Omni;

/// <summary>OmniBox 爬虫执行器（移植自 omni/local/LocalSpiderEngine.java）：脚本下载/MD5 校验/运行时缓存与结果映射。</summary>
public sealed class LocalSpiderEngine
{
    const string Tag = "LocalSpiderEngine";
    const int SearchDeadlineMs = 8000;
    const long DetailTtlMs = 2 * 60 * 1000;
    const long CooldownMs = 5 * 60 * 1000;
    const long ScriptRetryMs = 5 * 60 * 1000;
    const long StreamCacheTtlMs = 2 * 60 * 1000;
    const int StreamCacheMax = 200;

    public static LocalSpiderEngine Instance { get; } = new();

    readonly ConcurrentDictionary<string, Lazy<Task<OmniSpiderScript>>> _runtimes = new();
    readonly Dictionary<string, (OmniApi.OmniDetail Detail, long Expires)> _details = new();
    readonly Dictionary<string, long> _cooldown = new();
    readonly Dictionary<string, long> _scriptAttempts = new();
    readonly Dictionary<string, (List<OmniApi.OmniItem> Items, long Expires)> _streamCache = new();

    static string ScriptDir => Path.Combine(AppPaths.Root, "omni", "spiders");

    public List<OmniApi.OmniLocalSpider> EnabledSpiders()
    {
        var sources = LocalSourceStore.Instance.Cached;
        if (sources?.Spiders == null) return new();
        return sources.Spiders
            .Where(spider => spider != null && !string.IsNullOrEmpty(spider.Id) && IsJs(spider) && spider.ClientCompatible)
            .ToList();
    }

    static bool IsJs(OmniApi.OmniLocalSpider spider) =>
        string.IsNullOrEmpty(spider.Type) ||
        spider.Type.Equals("js", StringComparison.OrdinalIgnoreCase) ||
        spider.Type.Equals("javascript", StringComparison.OrdinalIgnoreCase);

    public bool IsLocalSpider(string id) =>
        !string.IsNullOrEmpty(id) && EnabledSpiders().Any(spider => spider.Id == id);

    /// <summary>配置重载：销毁全部运行时并清空详情缓存。</summary>
    public void Reset()
    {
        foreach (var lazy in _runtimes.Values)
        {
            try
            {
                if (lazy.IsValueCreated && lazy.Value.IsCompletedSuccessfully) lazy.Value.Result?.Dispose();
            }
            catch { }
        }
        _runtimes.Clear();
        lock (_details) _details.Clear();
        lock (_cooldown) _cooldown.Clear();
    }

    /// <summary>登录后预热：下载脚本并在后台编译运行时，避免首次搜索吃掉超时窗口。</summary>
    public async Task WarmupAsync()
    {
        var spiders = EnabledSpiders();
        await Task.WhenAll(spiders.Select(async spider =>
        {
            try { await RuntimeAsync(spider); }
            catch (Exception e) { Logger.E(Tag, "warmup " + spider.Name + ": " + e.Message); }
        }));
    }

    public async Task<List<OmniApi.OmniItem>> SearchAsync(string keyword, int page)
    {
        var spiders = EnabledSpiders();
        if (spiders.Count == 0) return new();
        var tasks = spiders.Select(spider => SearchOneAsync(spider, keyword, page)).ToList();
        var waitMs = Math.Clamp(Setting.OmniSpiderWait, 5, 20) * 1000;
        await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(waitMs));
        var items = tasks.Where(task => task.IsCompletedSuccessfully).SelectMany(task => task.Result).ToList();
        return items
            .GroupBy(item => item.LibraryId + "|" + item.VideoId)
            .Select(group => group.First())
            .OrderBy(item => MatchRank(keyword, item.Title))
            .ToList();
    }

    /// <summary>全站爬虫搜索：按动态评分排序 + 缓存命中优先 + 滑动窗口逐源回调。</summary>
    public async Task SearchStreamingAsync(string keyword, int page, Action<OmniApi.OmniLocalSpider, List<OmniApi.OmniItem>> onSpider, CancellationToken ct = default)
    {
        var ordered = EnabledSpiders()
            .Where(spider => !IsCooling(spider.Id))
            .OrderByDescending(spider => OmniSourceScoreStore.EffectiveScore(spider.Id, true))
            .ThenByDescending(spider => spider.Priority)
            .ThenBy(spider => OmniSourceScoreStore.Latency(spider.Id, true))
            .ThenBy(spider => spider.Name)
            .ToList();
        if (ordered.Count == 0) return;

        var queue = new Queue<OmniApi.OmniLocalSpider>();
        foreach (var spider in ordered)
        {
            if (TryGetStreamCache(StreamCacheKey(spider, keyword, page), out var cached) && cached.Count > 0)
                onSpider(spider, cached);
            else
                queue.Enqueue(spider);
        }
        if (queue.Count == 0) return;

        var configured = Setting.OmniConcurrency;
        var concurrency = Math.Clamp(configured > 0 ? configured : 6, 1, 6);
        var waitMs = Math.Clamp(Setting.OmniSpiderWait, 5, 20) * 1000;
        using var windowCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        windowCts.CancelAfter(waitMs);
        var running = new HashSet<Task>();
        while ((queue.Count > 0 || running.Count > 0) && !windowCts.IsCancellationRequested)
        {
            while (running.Count < concurrency && queue.Count > 0 && !windowCts.IsCancellationRequested)
            {
                var spider = queue.Dequeue();
                running.Add(RunSpiderAsync(spider, keyword, page, onSpider, windowCts.Token));
            }
            if (running.Count == 0) break;
            var completed = await Task.WhenAny(running);
            running.Remove(completed);
        }
        windowCts.Cancel();
    }

    async Task RunSpiderAsync(OmniApi.OmniLocalSpider spider, string keyword, int page,
        Action<OmniApi.OmniLocalSpider, List<OmniApi.OmniItem>> onSpider, CancellationToken token)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var items = await SearchOneAsync(spider, keyword, page);
            watch.Stop();
            OmniSourceScoreStore.Record(spider.Id, spider.Name, true, watch.ElapsedMilliseconds, items.Count, true);
            if (items.Count > 0)
            {
                PutStreamCache(StreamCacheKey(spider, keyword, page), items);
                if (!token.IsCancellationRequested) onSpider(spider, items);
            }
        }
        catch (Exception e)
        {
            watch.Stop();
            OmniSourceScoreStore.Record(spider.Id, spider.Name, true, watch.ElapsedMilliseconds, 0, false);
            Logger.E(Tag, "stream " + spider.Name + ": " + e.Message);
        }
    }

    bool IsCooling(string id)
    {
        lock (_cooldown)
            return _cooldown.TryGetValue(id, out var until) && until > Environment.TickCount64;
    }

    static string StreamCacheKey(OmniApi.OmniLocalSpider spider, string keyword, int page) =>
        (LocalSourceStore.Instance.Cached?.Version ?? "") + "|" + spider.Id + "|" + keyword + "|" + page;

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

    async Task<List<OmniApi.OmniItem>> SearchOneAsync(OmniApi.OmniLocalSpider spider, string keyword, int page)
    {
        lock (_cooldown)
        {
            if (_cooldown.TryGetValue(spider.Id, out var until) && until > Environment.TickCount64)
                return new();
        }
        try
        {
            var runtime = await RuntimeAsync(spider);
            if (runtime == null) return new();
            var call = runtime.CallAsync("search", new Dictionary<string, string>
            {
                ["keyword"] = keyword,
                ["wd"] = keyword,
                ["page"] = Math.Max(1, page).ToString(),
            });
            var json = await call.WaitAsync(TimeSpan.FromSeconds(6));
            if (string.IsNullOrEmpty(json)) return new();
            if (JsonUtil.Parse(json) is not JsonObject root || root["list"] is not JsonArray list) return new();
            var items = ParseItems(spider, list);
            lock (_cooldown) _cooldown.Remove(spider.Id);
            return items;
        }
        catch (Exception e)
        {
            Logger.E(Tag, "search " + spider.Name + ": " + e.Message);
            lock (_cooldown) _cooldown[spider.Id] = Environment.TickCount64 + CooldownMs;
            return new();
        }
    }

    public async Task<OmniApi.OmniDetail> DetailAsync(string spiderId, string videoId)
    {
        if (string.IsNullOrEmpty(spiderId) || string.IsNullOrEmpty(videoId)) return null;
        var spider = EnabledSpiders().FirstOrDefault(item => item.Id == spiderId);
        if (spider == null) return null;
        var key = spiderId + "|" + videoId;
        lock (_details)
        {
            if (_details.TryGetValue(key, out var cached) && cached.Expires > Environment.TickCount64)
                return cached.Detail;
        }
        try
        {
            var runtime = await RuntimeAsync(spider);
            if (runtime == null) return null;
            var json = await runtime.CallAsync("detail", new Dictionary<string, string> { ["videoId"] = videoId })
                .WaitAsync(TimeSpan.FromSeconds(10));
            if (string.IsNullOrEmpty(json)) return null;
            if (JsonUtil.Parse(json) is not JsonObject root || root["list"] is not JsonArray list) return null;
            if (list.FirstOrDefault() is not JsonObject vod) return null;

            var detail = new OmniApi.OmniDetail
            {
                LibraryId = spider.Id,
                LibraryName = spider.Name,
                VideoId = videoId,
                Title = First(vod, "vod_name", "title", "name"),
                Poster = First(vod, "vod_pic", "poster", "pic"),
                Content = First(vod, "vod_content", "content", "description"),
                Year = First(vod, "vod_year", "year"),
                Area = First(vod, "vod_area", "area"),
                Actors = First(vod, "vod_actor", "actors"),
                Director = First(vod, "vod_director", "director"),
                Remark = First(vod, "vod_remarks", "remarks"),
                Score = First(vod, "vod_douban_score", "score"),
                PlaySources = ParsePlaySources(vod),
            };
            lock (_details) _details[key] = (detail, Environment.TickCount64 + DetailTtlMs);
            return detail;
        }
        catch (Exception e)
        {
            Logger.E(Tag, "detail " + spider.Name + ": " + e.Message);
            return null;
        }
    }

    public async Task<OmniApi.OmniPlayInfo> PlayAsync(string spiderId, string videoId, int line, int episode)
    {
        var detail = await DetailAsync(spiderId, videoId);
        if (detail?.PlaySources == null || line < 0 || line >= detail.PlaySources.Count) return null;
        var source = detail.PlaySources[line];
        if (source?.Episodes == null || episode < 0 || episode >= source.Episodes.Count) return null;
        var target = source.Episodes[episode];
        if (string.IsNullOrEmpty(target.PlayId)) return null;

        var spider = EnabledSpiders().FirstOrDefault(item => item.Id == spiderId);
        if (spider == null) return null;
        try
        {
            var runtime = await RuntimeAsync(spider);
            if (runtime == null) return null;
            var json = await runtime.CallAsync("play", new Dictionary<string, string>
            {
                ["playId"] = target.PlayId,
                ["vodId"] = videoId,
                ["flag"] = source.Name ?? "",
            }).WaitAsync(TimeSpan.FromSeconds(10));
            if (string.IsNullOrEmpty(json)) return null;
            if (JsonUtil.Parse(json) is not JsonObject root) return null;

            var url = "";
            if (root["urls"] is JsonArray urls && urls.Count > 0 && urls[0] is JsonObject first)
                url = First(first, "url");
            if (string.IsNullOrEmpty(url)) url = First(root, "url");
            if (string.IsNullOrEmpty(url)) return null;

            var info = new OmniApi.OmniPlayInfo
            {
                Url = url,
                Flag = string.IsNullOrEmpty(source.Name) ? spider.Name : source.Name,
                Parse = root["parse"] is JsonValue parseValue && parseValue.TryGetValue<int>(out var parse) ? parse : 0,
                Header = ParseHeader(root["header"]),
            };
            info.Danmaku = ParseDanmaku(root["danmaku"] ?? root["danmakuList"]);
            return info;
        }
        catch (Exception e)
        {
            Logger.E(Tag, "play " + spider.Name + ": " + e.Message);
            return null;
        }
    }

    // ---------------------------------------------------------------- 脚本缓存

    async Task<OmniSpiderScript> RuntimeAsync(OmniApi.OmniLocalSpider spider)
    {
        var lazy = _runtimes.GetOrAdd(spider.Id, _ => new Lazy<Task<OmniSpiderScript>>(() => CreateRuntimeAsync(spider)));
        try
        {
            var runtime = await lazy.Value;
            if (runtime == null) _runtimes.TryRemove(spider.Id, out _);
            return runtime;
        }
        catch
        {
            _runtimes.TryRemove(spider.Id, out _);
            throw;
        }
    }

    async Task<OmniSpiderScript> CreateRuntimeAsync(OmniApi.OmniLocalSpider spider)
    {
        var script = await EnsureScriptAsync(spider);
        if (string.IsNullOrEmpty(script)) return null;
        return await Task.Run(() => new OmniSpiderScript(spider.Id, script));
    }

    async Task<string> EnsureScriptAsync(OmniApi.OmniLocalSpider spider)
    {
        var file = Path.Combine(ScriptDir, spider.Id + ".js");
        var expected = spider.ScriptMd5?.Trim() ?? "";
        string text = null;
        if (File.Exists(file))
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(file);
                if (string.IsNullOrEmpty(expected) || Md5Hex(bytes).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    text = Encoding.UTF8.GetString(bytes);
            }
            catch (Exception e)
            {
                Logger.E(Tag, "read script " + spider.Id + ": " + e.Message);
            }
        }
        if (text == null)
        {
            if (string.IsNullOrEmpty(spider.ScriptUrl)) return "";
            lock (_scriptAttempts)
            {
                if (_scriptAttempts.TryGetValue(spider.Id, out var last) &&
                    Environment.TickCount64 - last < ScriptRetryMs)
                    return "";
            }
            try
            {
                var bytes = await OmniApi.DownloadSpiderScriptAsync(spider.ScriptUrl);
                if (bytes.Length == 0) throw new InvalidOperationException("脚本为空");
                var actual = Md5Hex(bytes);
                if (!string.IsNullOrEmpty(expected) && !actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.E(Tag, "script md5 mismatch " + spider.Id + ": " + actual + " != " + expected);
                    MarkAttempt(spider.Id);
                    return "";
                }
                Directory.CreateDirectory(ScriptDir);
                var tmp = file + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes);
                File.Move(tmp, file, true);
                text = Encoding.UTF8.GetString(bytes);
                lock (_scriptAttempts) _scriptAttempts.Remove(spider.Id);
                Logger.D(Tag, "downloaded " + spider.Id + " md5=" + actual);
            }
            catch (Exception e)
            {
                Logger.E(Tag, "download " + spider.Id + ": " + e.Message);
                MarkAttempt(spider.Id);
                return "";
            }
        }
        if (!string.IsNullOrEmpty(text) && text[0] == '\uFEFF') text = text[1..];
        return text ?? "";
    }

    void MarkAttempt(string id)
    {
        lock (_scriptAttempts) _scriptAttempts[id] = Environment.TickCount64;
    }

    static string Md5Hex(byte[] data) => Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant();

    // ---------------------------------------------------------------- 映射

    static List<OmniApi.OmniItem> ParseItems(OmniApi.OmniLocalSpider spider, JsonArray list)
    {
        var items = new List<OmniApi.OmniItem>();
        foreach (var node in list)
        {
            if (node is not JsonObject vod) continue;
            var videoId = First(vod, "vod_id", "id", "videoId");
            var title = First(vod, "vod_name", "name", "title");
            if (string.IsNullOrEmpty(videoId) || string.IsNullOrEmpty(title)) continue;
            items.Add(new OmniApi.OmniItem
            {
                SourceType = "spider",
                LibraryId = spider.Id,
                LibraryName = spider.Name,
                VideoId = videoId,
                Title = title,
                Poster = First(vod, "vod_pic", "pic", "poster"),
                Remark = First(vod, "vod_remarks", "remarks", "remark"),
                Year = First(vod, "vod_year", "year"),
                Score = First(vod, "vod_douban_score", "score"),
                TypeName = First(vod, "type_name", "typeName"),
            });
        }
        return items;
    }

    static List<OmniApi.OmniPlaySource> ParsePlaySources(JsonObject vod)
    {
        var sources = vod["vod_play_sources"] as JsonArray ?? vod["playSources"] as JsonArray;
        if (sources == null) return new();
        var result = new List<OmniApi.OmniPlaySource>();
        foreach (var node in sources)
        {
            if (node is not JsonObject source) continue;
            var playSource = new OmniApi.OmniPlaySource
            {
                Index = result.Count,
                Name = First(source, "name", "source_name"),
                Episodes = new(),
            };
            var episodes = source["episodes"] as JsonArray ?? source["source_list"] as JsonArray;
            if (episodes != null)
            {
                foreach (var episodeNode in episodes)
                {
                    if (episodeNode is not JsonObject episode) continue;
                    var playId = First(episode, "playId", "url", "play_id");
                    if (string.IsNullOrEmpty(playId)) continue;
                    playSource.Episodes.Add(new OmniApi.OmniEpisode
                    {
                        Index = playSource.Episodes.Count,
                        Name = First(episode, "name", "source_name", "episodeName"),
                        PlayId = playId,
                    });
                }
            }
            if (playSource.Episodes.Count > 0) result.Add(playSource);
        }
        return result;
    }

    static Dictionary<string, string> ParseHeader(JsonNode node)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node == null) return headers;
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
            node = JsonUtil.Parse(text) ?? node;
        if (node is JsonObject obj)
            foreach (var kv in obj)
                headers[kv.Key] = kv.Value is JsonValue headerValue ? headerValue.ToString() : "";
        return headers;
    }

    static List<OmniApi.OmniDanmaku> ParseDanmaku(JsonNode node)
    {
        var result = new List<OmniApi.OmniDanmaku>();
        if (node is not JsonArray array) return result;
        foreach (var item in array)
        {
            if (item is not JsonObject obj) continue;
            var url = First(obj, "url");
            if (string.IsNullOrEmpty(url)) continue;
            result.Add(new OmniApi.OmniDanmaku { Name = First(obj, "name"), Url = url });
        }
        return result;
    }

    static string First(JsonNode node, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = JsonUtil.SafeString(node, key);
            if (!string.IsNullOrEmpty(value) && value != "null") return value;
        }
        return "";
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
}
