using System.Collections.Concurrent;
using TVBoxForWindows.Core;
using TVBoxForWindows.Engine;
using TVBoxForWindows.Models;

namespace TVBoxForWindows.Omni;

/// <summary>把 CloudTV App API 翻译成 CatVod Spider 协议（移植自 omni/OmniSpider.java）。</summary>
public sealed class OmniSpider : Spider
{
    const string PlayFlag = "CloudTV";

    public static OmniSpider Instance { get; } = new();

    readonly ConcurrentDictionary<string, PlayContext> _contexts = new();

    public sealed class PlayContext
    {
        public string LibraryId { get; init; } = "";
        public string VideoId { get; init; } = "";
        public string PlayId { get; init; } = "";
        public string Title { get; init; } = "";
        public string Poster { get; init; } = "";
        public string EpisodeName { get; init; } = "";
    }

    public bool TryGetContext(string token, out PlayContext context) => _contexts.TryGetValue(token ?? "", out context);

    // ---------------------------------------------------------------- 首页

    static readonly string[][] DoubanClasses =
    {
        new[] { "douban:movie:hot", "热门电影" },
        new[] { "douban:tv:hot", "热播剧集" },
        new[] { "douban:tv:variety", "热播综艺" },
        new[] { "douban:movie:filter", "电影筛选" },
        new[] { "douban:tv:filter", "电视筛选" },
        new[] { "douban:movie:rank", "电影榜单" },
        new[] { "douban:tv:rank", "电视剧榜单" },
    };

    static List<OmniApi.OmniResumeItem> _homeResume = new();

    public override async Task<string> HomeContent(bool filter)
    {
        var result = new Result();
        foreach (var def in DoubanClasses)
            result.Types.Add(new VodClass { TypeId = def[0], TypeName = def[1] });
        try
        {
            var resumeTask = LoadResumeSafeAsync();
            var recommendTask = LoadRecommendations();
            await Task.WhenAll(resumeTask, recommendTask);
            _homeResume = resumeTask.Result;
            if (_homeResume.Count > 0)
                result.Types.Insert(0, new VodClass { TypeId = "omni:resume", TypeName = "继续观看" });
            var recommendations = recommendTask.Result;
            var resumeTitles = new HashSet<string>(_homeResume
                .Select(item => Normalize(item.Title))
                .Where(title => title.Length > 0));
            var limit = 48;
            foreach (var item in recommendations)
            {
                if (result.List.Count >= limit) break;
                if (item == null || string.IsNullOrEmpty(item.Title)) continue;
                if (resumeTitles.Contains(Normalize(item.Title))) continue;
                var vodId = !string.IsNullOrEmpty(item.LibraryId) && !string.IsNullOrEmpty(item.VideoId)
                    ? OmniIds.EncodeVod(string.IsNullOrEmpty(item.SourceType) ? "spider" : item.SourceType,
                        item.LibraryId, item.VideoId)
                    : "msearch:" + item.Title;
                result.List.Add(new Vod
                {
                    Id = vodId,
                    Name = item.Title,
                    Pic = item.Poster ?? "",
                    Remarks = item.Subtitle ?? "",
                });
            }
        }
        catch (Exception e)
        {
            Logger.E("OmniSpider.home", e.Message);
            return JsonUtil.Serialize(Result.Error(e.Message));
        }
        return JsonUtil.Serialize(result);
    }

    static async Task<List<OmniApi.OmniResumeItem>> LoadResumeSafeAsync()
    {
        try
        {
            var items = await OmniApi.ResumeAsync(8, 4000);
            return items
                .Where(item => item != null && !string.IsNullOrEmpty(item.LibraryId) && !string.IsNullOrEmpty(item.VideoId))
                .GroupBy(item => item.LibraryId + "|" + item.VideoId)
                .Select(group => group.OrderByDescending(item => item.UpdatedAt).First())
                .OrderByDescending(item => item.UpdatedAt)
                .ToList();
        }
        catch (Exception e)
        {
            Logger.E("OmniSpider.resumeHome", e.Message);
            return new();
        }
    }

    async Task<List<OmniApi.OmniRecommendItem>> LoadRecommendations()
    {
        var items = new List<OmniApi.OmniRecommendItem>();
        try
        {
            var recommend = await OmniApi.RecommendAsync();
            if (recommend?.Sections is { Count: > 0 })
            {
                foreach (var section in recommend.Sections.Where(s => s?.Items != null && s.Type == "tv"))
                    items.AddRange(section.Items);
                foreach (var section in recommend.Sections.Where(s => s?.Items != null && s.Type != "tv"))
                    items.AddRange(section.Items);
            }
        }
        catch (Exception e)
        {
            Logger.E("OmniSpider.recommend", e.Message);
        }
        if (items.Count == 0)
        {
            try
            {
                var home = await OmniApi.HomeAsync();
                if (home?.Recommend != null) items.AddRange(home.Recommend);
            }
            catch (Exception e)
            {
                Logger.E("OmniSpider.homeFallback", e.Message);
            }
        }
        return items;
    }

    // ---------------------------------------------------------------- 分类

    public override async Task<string> CategoryContent(string tid, string pg, bool filter, Dictionary<string, string> extend)
    {
        var result = new Result();
        var page = int.TryParse(pg, out var parsed) ? Math.Max(1, parsed) : 1;
        if (tid == "omni:resume")
        {
            var resume = _homeResume.Count > 0 ? _homeResume : await LoadResumeSafeAsync();
            var list = resume
                .Where(item => item != null && !string.IsNullOrEmpty(item.LibraryId) && !string.IsNullOrEmpty(item.VideoId))
                .Select(ToVodJson)
                .ToList();
            return JsonUtil.Serialize(new Dictionary<string, object>
            {
                ["list"] = list,
                ["page"] = 1,
                ["pagecount"] = 1,
                ["total"] = list.Count,
            });
        }
        if (tid != null && tid.StartsWith("douban:", StringComparison.Ordinal))
            return await DoubanCategoryContent(tid["douban:".Length..], page);
        var type = OmniIds.DecodeType(tid);
        if (type == null) return JsonUtil.Serialize(result);
        try
        {
            var items = await OmniApi.CategoryAsync(type[0], type[1], page);
            foreach (var item in items.Items)
            {
                if (item == null || string.IsNullOrEmpty(item.VideoId)) continue;
                result.List.Add(ToVod(item));
            }
            var payload = new Dictionary<string, object>
            {
                ["list"] = result.List.Select(ToVodJson).ToList(),
                ["page"] = page,
                ["pagecount"] = Math.Max(page, items.PageCount),
                ["total"] = items.Total,
            };
            return JsonUtil.Serialize(payload);
        }
        catch (Exception e)
        {
            Logger.E("OmniSpider.category", e.Message);
            return JsonUtil.Serialize(Result.Error(e.Message));
        }
    }

    async Task<string> DoubanCategoryContent(string spec, int page)
    {
        var parts = spec.Split(':');
        var primary = parts.Length > 0 ? parts[0] : "movie";
        var kind = parts.Length > 1 ? parts[1] : "hot";
        string secondary;
        string type;
        if (kind == "variety")
        {
            primary = "tv";
            secondary = "综艺";
            type = "综艺";
        }
        else if (primary == "movie")
        {
            secondary = "热门";
            type = "全部";
        }
        else
        {
            secondary = "tv";
            type = "tv";
        }
        try
        {
            var data = await OmniApi.DoubanCategoryAsync(primary, secondary, type, (page - 1) * 18);
            var source = (data.Items ?? new())
                .Where(item => item != null && !string.IsNullOrEmpty(item.Title))
                .ToList();
            if (kind == "rank") source = source.OrderByDescending(RatingOf).ToList();
            var list = source.Select(item => new Dictionary<string, object>
            {
                ["vod_id"] = "msearch:" + item.Title,
                ["vod_name"] = item.Title,
                ["vod_pic"] = !string.IsNullOrEmpty(item.Pic?.Large) ? item.Pic.Large : item.Pic?.Normal ?? "",
                ["vod_remarks"] = RatingOf(item) > 0 ? "★ " + RatingOf(item).ToString("0.0") : "",
            }).ToList();
            return JsonUtil.Serialize(new Dictionary<string, object>
            {
                ["list"] = list,
                ["page"] = page,
                ["pagecount"] = 5,
                ["total"] = source.Count,
            });
        }
        catch (Exception e)
        {
            Logger.E("OmniSpider.douban", e.Message);
            return JsonUtil.Serialize(Result.Error(e.Message));
        }
    }

    static double RatingOf(OmniApi.DoubanItem item) => item?.Rating?.Value ?? 0;

    // ---------------------------------------------------------------- 详情

    public override async Task<string> DetailContent(List<string> ids)
    {
        var result = new Result();
        var encodedId = ids is { Count: > 0 } ? ids[0] : "";
        OmniIds.VodRef reference;
        if (encodedId.StartsWith("msearch:", StringComparison.Ordinal))
        {
            reference = await ResolveBySearch(encodedId["msearch:".Length..]);
            if (reference == null) return JsonUtil.Serialize(Result.Error("没有找到匹配的资源"));
            encodedId = OmniIds.EncodeVod(reference.SourceType, reference.LibraryId, reference.VideoId);
        }
        else
        {
            reference = OmniIds.DecodeVodRef(encodedId);
        }
        if (reference == null) return JsonUtil.Serialize(result);
        try
        {
            var detail = reference.SourceType == "site"
                ? await LocalSiteEngine.Instance.DetailAsync(reference.SourceType, reference.LibraryId, reference.VideoId)
                : reference.SourceType == "spider" && LocalSpiderEngine.Instance.IsLocalSpider(reference.LibraryId)
                    ? await LocalSpiderEngine.Instance.DetailAsync(reference.LibraryId, reference.VideoId)
                    : null;
            detail ??= await OmniApi.DetailAsync(reference.SourceType, reference.LibraryId, reference.VideoId);
            // 旧版/续播的两段 ID 默认按 spider 处理，服务端按库回退一次。
            if (detail == null && reference.SourceType != "site")
                detail = await OmniApi.DetailAsync("site", reference.LibraryId, reference.VideoId);
            if (detail == null || string.IsNullOrEmpty(detail.Title)) return JsonUtil.Serialize(result);

            var vod = new Vod
            {
                Id = encodedId,
                Name = detail.Title,
                Pic = detail.Poster ?? "",
                Content = detail.Content ?? "",
                Year = detail.Year ?? "",
                Area = detail.Area ?? "",
                Actor = detail.Actors ?? "",
                Director = detail.Director ?? "",
                Remarks = detail.Remark ?? "",
                SourceName = detail.LibraryName ?? "",
            };

            var related = GetAggregate(reference.LibraryId, reference.VideoId);
            var detailSources = new List<(OmniApi.OmniDetail Detail, OmniApi.OmniItem Item)>
            {
                (detail, new OmniApi.OmniItem
                {
                    SourceType = reference.SourceType,
                    LibraryId = reference.LibraryId,
                    VideoId = reference.VideoId,
                    LibraryName = detail.LibraryName ?? "",
                }),
            };
            if (related != null)
            {
                var others = related
                    .Where(item => item != null && !(item.LibraryId == reference.LibraryId && item.VideoId == reference.VideoId))
                    .Take(3)
                    .ToList();
                if (others.Count > 0)
                {
                    var tasks = others.Select(LoadRelatedDetailAsync).ToList();
                    await Task.WhenAny(Task.WhenAll(tasks), Task.Delay(6000));
                    for (var i = 0; i < tasks.Count; i++)
                        if (tasks[i].IsCompletedSuccessfully && tasks[i].Result != null)
                            detailSources.Add((tasks[i].Result, others[i]));
                }
            }

            var multiSource = detailSources.Count > 1;
            var froms = new List<string>();
            var groups = new List<string>();
            var lineCount = 0;
            var episodeCount = 0;
            foreach (var (sourceDetail, item) in detailSources)
            {
                if (sourceDetail?.PlaySources == null) continue;
                var sourceLabel = CleanName(sourceDetail.LibraryName ?? item.LibraryName, "来源");
                foreach (var source in sourceDetail.PlaySources.Take(4))
                {
                    if (source?.Episodes == null || source.Episodes.Count == 0) continue;
                    if (lineCount >= 12) break;
                    var lineName = CleanName(source.Name, "线路" + (lineCount + 1));
                    if (multiSource) lineName = CleanName(sourceLabel + " · " + lineName, lineName);
                    var episodes = new List<string>();
                    for (var i = 0; i < source.Episodes.Count; i++)
                    {
                        var episode = source.Episodes[i];
                        if (episode == null) continue;
                        var episodeName = CleanName(episode.Name, "第" + (i + 1) + "集");
                        var token = OmniIds.EncodePlay(item.SourceType, item.LibraryId, item.VideoId,
                            source.Index, episode.Index);
                        episodes.Add(episodeName + "$" + token);
                        _contexts[token] = new PlayContext
                        {
                            LibraryId = item.LibraryId,
                            VideoId = item.VideoId,
                            PlayId = episode.PlayId ?? "",
                            Title = sourceDetail.Title,
                            Poster = sourceDetail.Poster ?? "",
                            EpisodeName = episodeName,
                        };
                        episodeCount++;
                    }
                    if (episodes.Count == 0) continue;
                    froms.Add(lineName);
                    groups.Add(string.Join("#", episodes));
                    lineCount++;
                }
                if (lineCount >= 12) break;
            }
            vod.PlayFrom = string.Join("$$$", froms);
            vod.PlayUrl = string.Join("$$$", groups);
            vod.Remarks = lineCount > 1 ? lineCount + " 条线路" : "共 " + episodeCount + " 集";
            if (multiSource) vod.SourceName = (vod.SourceName ?? "") + " 等" + detailSources.Count + "源";
            result.List.Add(vod);
        }
        catch (Exception e)
        {
            Logger.E("OmniSpider.detail", e.Message);
            return JsonUtil.Serialize(Result.Error(e.Message));
        }
        return JsonUtil.Serialize(result);
    }

    // ---------------------------------------------------------------- 播放

    public override async Task<string> PlayerContent(string flag, string id, List<string> vipFlags)
    {
        var reference = OmniIds.DecodePlay(id);
        if (reference == null) throw new InvalidOperationException("无效的播放地址");
        var info = reference.SourceType == "site"
            ? await LocalSiteEngine.Instance.PlayAsync(reference.SourceType, reference.LibraryId, reference.VideoId,
                reference.Line, reference.Episode)
            : reference.SourceType == "spider" && LocalSpiderEngine.Instance.IsLocalSpider(reference.LibraryId)
                ? await LocalSpiderEngine.Instance.PlayAsync(reference.LibraryId, reference.VideoId,
                    reference.Line, reference.Episode)
                : null;
        info ??= await OmniApi.PlayAsync(reference.SourceType, reference.LibraryId, reference.VideoId,
            reference.Line, reference.Episode);
        if (string.IsNullOrEmpty(info?.Url) && reference.SourceType != "site")
            info = await OmniApi.PlayAsync("site", reference.LibraryId, reference.VideoId,
                reference.Line, reference.Episode);
        if (info == null || string.IsNullOrEmpty(info.Url)) throw new InvalidOperationException("没有可播放的地址");

        if (!_contexts.ContainsKey(id ?? ""))
        {
            var detail = await OmniApi.DetailAsync(reference.SourceType, reference.LibraryId, reference.VideoId);
            if (detail?.PlaySources != null && reference.Line < detail.PlaySources.Count)
            {
                var source = detail.PlaySources[reference.Line];
                if (source?.Episodes != null && reference.Episode < source.Episodes.Count)
                {
                    var episode = source.Episodes[reference.Episode];
                    _contexts[id] = new PlayContext
                    {
                        LibraryId = reference.LibraryId,
                        VideoId = reference.VideoId,
                        PlayId = episode.PlayId ?? "",
                        Title = detail.Title,
                        Poster = detail.Poster ?? "",
                        EpisodeName = episode.Name ?? "",
                    };
                }
            }
        }

        var parse = info.Parse;
        if (parse != 0 && IsDirectMedia(info.Url)) parse = 0;
        var payload = new Dictionary<string, object>
        {
            ["parse"] = parse,
            ["playUrl"] = "",
            ["url"] = info.Url,
            ["header"] = info.Header ?? new Dictionary<string, string>(),
            ["flag"] = string.IsNullOrEmpty(flag) ? PlayFlag : flag,
            ["key"] = id ?? "",
        };
        _contexts.TryGetValue(id ?? "", out var context);
        var danmaku = (info.Danmaku ?? new()).Where(d => d != null && !string.IsNullOrEmpty(d.Url)).ToList();
        if (danmaku.Count == 0 && context != null)
        {
            try
            {
                danmaku = (await OmniApi.DanmakuAsync((context.Title + " " + context.EpisodeName).Trim()))
                    .Where(d => !string.IsNullOrEmpty(d.Url)).ToList();
            }
            catch (Exception e)
            {
                Logger.E("OmniSpider.danmaku", e.Message);
            }
        }
        if (danmaku.Count > 0)
            payload["danmaku"] = danmaku.Select(d => new { name = d.Name, url = d.Url }).ToList();
        if (context != null)
        {
            var position = await ResumePositionAsync(context);
            if (position > 0) payload["position"] = position;
        }
        return JsonUtil.Serialize(payload);
    }

    static readonly object ResumeSync = new();
    static List<OmniApi.OmniResumeItem> _resumeItems = new();
    static long _resumeLoadedAt;

    async Task<long> ResumePositionAsync(PlayContext context)
    {
        try
        {
            List<OmniApi.OmniResumeItem> items;
            lock (ResumeSync) items = _resumeItems;
            if (items.Count == 0 || Environment.TickCount64 - Interlocked.Read(ref _resumeLoadedAt) > 60_000)
            {
                items = await OmniApi.ResumeAsync(50);
                lock (ResumeSync)
                {
                    _resumeItems = items;
                    Interlocked.Exchange(ref _resumeLoadedAt, Environment.TickCount64);
                }
            }
            var match = items.FirstOrDefault(item => item != null &&
                item.LibraryId == context.LibraryId && item.VideoId == context.VideoId &&
                ((!string.IsNullOrEmpty(context.PlayId) && item.PlayId == context.PlayId) ||
                 (!string.IsNullOrEmpty(context.EpisodeName) && item.EpisodeName == context.EpisodeName)));
            return match?.PositionMs ?? 0;
        }
        catch (Exception e)
        {
            Logger.E("OmniSpider.resume", e.Message);
            return 0;
        }
    }

    // ---------------------------------------------------------------- 搜索

    public override Task<string> SearchContent(string key, bool quick) => SearchContent(key, quick, "1");

    public override async Task<string> SearchContent(string key, bool quick, string pg)
    {
        var page = int.TryParse(pg, out var parsed) ? Math.Max(1, parsed) : 1;
        try
        {
            var siteTask = LocalSiteEngine.Instance.SearchAsync(key, page);
            var spiderTask = LocalSpiderEngine.Instance.SearchAsync(key, page);
            await Task.WhenAll(siteTask, spiderTask);
            var merged = siteTask.Result.Concat(spiderTask.Result)
                .GroupBy(item => item.LibraryId + "|" + item.VideoId)
                .Select(group => group.First())
                .ToList();
            var items = AggregateItems(RankByRelevance(key, merged));
            var pageCount = Math.Max(page, 1);
            var total = items.Count;
            if (items.Count == 0)
            {
                var remote = await OmniApi.SearchAsync(key, page, true);
                items = AggregateItems(RankByRelevance(key, remote.Items ?? new()));
                pageCount = Math.Max(page, remote.PageCount);
                total = remote.Total;
            }
            var payload = new Dictionary<string, object>
            {
                ["list"] = items
                    .Where(item => item != null && !string.IsNullOrEmpty(item.VideoId))
                    .Select(ToVodJson).ToList(),
                ["page"] = page,
                ["pagecount"] = pageCount,
                ["total"] = total,
            };
            return JsonUtil.Serialize(payload);
        }
        catch (Exception e)
        {
            Logger.E("OmniSpider.search", e.Message);
            return JsonUtil.Serialize(Result.Error(e.Message));
        }
    }

    // ---------------------------------------------------------------- 多来源聚合

    static readonly ConcurrentDictionary<string, (List<OmniApi.OmniItem> Items, long Expires)> _aggregatesByVod = new();

    /// <summary>按规范化片名聚合搜索结果：每组只出一张卡，来源名标注 +N；组内来源缓存给详情合并线路。</summary>
    static List<OmniApi.OmniItem> AggregateItems(List<OmniApi.OmniItem> items)
    {
        var result = new List<OmniApi.OmniItem>();
        var groups = items
            .Where(item => item != null && !string.IsNullOrEmpty(item.VideoId) && !string.IsNullOrEmpty(item.LibraryId))
            .GroupBy(item =>
            {
                var key = Normalize(item.Title);
                return string.IsNullOrEmpty(key) ? item.LibraryId + "|" + item.VideoId : key;
            })
            .ToList();
        foreach (var group in groups)
        {
            var list = group.Take(8).ToList();
            var main = CloneItem(list[0]);
            if (list.Count > 1)
            {
                main.LibraryName = (string.IsNullOrEmpty(main.LibraryName) ? "来源" : main.LibraryName) + " 等 " + list.Count + " 源";
                _aggregatesByVod[main.LibraryId + "|" + main.VideoId] =
                    (list, Environment.TickCount64 + 10 * 60 * 1000);
            }
            result.Add(main);
        }
        if (_aggregatesByVod.Count > 160)
        {
            foreach (var pair in _aggregatesByVod.Where(pair => pair.Value.Expires <= Environment.TickCount64))
                _aggregatesByVod.TryRemove(pair.Key, out _);
        }
        return result;
    }

    static OmniApi.OmniItem CloneItem(OmniApi.OmniItem item) => new()
    {
        SourceType = item.SourceType,
        LibraryId = item.LibraryId,
        LibraryName = item.LibraryName,
        VideoId = item.VideoId,
        Title = item.Title,
        Poster = item.Poster,
        Remark = item.Remark,
        Year = item.Year,
        Score = item.Score,
        TypeName = item.TypeName,
    };

    static List<OmniApi.OmniItem> GetAggregate(string libraryId, string videoId)
    {
        var key = (libraryId ?? "") + "|" + (videoId ?? "");
        if (!_aggregatesByVod.TryGetValue(key, out var entry)) return null;
        if (entry.Expires <= Environment.TickCount64)
        {
            _aggregatesByVod.TryRemove(key, out _);
            return null;
        }
        return entry.Items;
    }

    async Task<OmniApi.OmniDetail> LoadRelatedDetailAsync(OmniApi.OmniItem item)
    {
        try
        {
            OmniApi.OmniDetail detail = item.SourceType == "site"
                ? await LocalSiteEngine.Instance.DetailAsync("site", item.LibraryId, item.VideoId)
                : item.SourceType == "spider" && LocalSpiderEngine.Instance.IsLocalSpider(item.LibraryId)
                    ? await LocalSpiderEngine.Instance.DetailAsync(item.LibraryId, item.VideoId)
                    : null;
            detail ??= await OmniApi.DetailAsync(item.SourceType, item.LibraryId, item.VideoId);
            return detail;
        }
        catch (Exception e)
        {
            Logger.E("OmniSpider.related", e.Message);
            return null;
        }
    }

    // ---------------------------------------------------------------- 工具

    async Task<OmniIds.VodRef> ResolveBySearch(string title)
    {
        var items = (await OmniApi.SearchAsync(title, 1, true)).Items ?? new();
        var match = items.FirstOrDefault(item => item != null && !string.IsNullOrEmpty(item.VideoId) &&
                    string.Equals((item.Title ?? "").Trim(), (title ?? "").Trim(), StringComparison.Ordinal))
                 ?? items.FirstOrDefault(item => item != null && !string.IsNullOrEmpty(item.VideoId));
        if (match == null) return null;
        return new OmniIds.VodRef(string.IsNullOrEmpty(match.SourceType) ? "spider" : match.SourceType,
            match.LibraryId, match.VideoId);
    }

    static Vod ToVod(OmniApi.OmniItem item) => new()
    {
        Id = OmniIds.EncodeVod(string.IsNullOrEmpty(item.SourceType) ? "spider" : item.SourceType,
            item.LibraryId, item.VideoId),
        Name = item.Title ?? "",
        Pic = item.Poster ?? "",
        Remarks = item.Remark ?? "",
        Year = item.Year ?? "",
        TypeName = item.TypeName ?? "",
        SourceName = item.LibraryName ?? "",
    };

    static Dictionary<string, object> ToVodJson(OmniApi.OmniItem item) => new()
    {
        ["vod_id"] = OmniIds.EncodeVod(string.IsNullOrEmpty(item.SourceType) ? "spider" : item.SourceType,
            item.LibraryId, item.VideoId),
        ["vod_name"] = item.Title ?? "",
        ["vod_pic"] = item.Poster ?? "",
        ["vod_remarks"] = item.Remark ?? "",
        ["vod_year"] = item.Year ?? "",
        ["vod_score"] = item.Score ?? "",
        ["vod_source_name"] = item.LibraryName ?? "",
    };

    static Dictionary<string, object> ToVodJson(OmniApi.OmniResumeItem item) => new()
    {
        ["vod_id"] = OmniIds.EncodeVod(item.LibraryId, item.VideoId),
        ["vod_name"] = item.Title ?? "",
        ["vod_pic"] = item.Poster ?? "",
        ["vod_remarks"] = item.DurationMs > 0 && item.PositionMs > 0
            ? "已看 " + Math.Min(99, (int)(item.PositionMs * 100 / item.DurationMs)) + "%"
            : item.EpisodeName ?? "",
        ["vod_source_name"] = "继续观看",
    };

    static Dictionary<string, object> ToVodJson(Vod vod) => new()
    {
        ["vod_id"] = vod.Id,
        ["vod_name"] = vod.Name,
        ["vod_pic"] = vod.Pic,
        ["vod_remarks"] = vod.Remarks,
        ["vod_year"] = vod.Year,
        ["vod_source_name"] = vod.SourceName ?? "",
    };

    /// <summary>按关键词相关度排序：精确 > 前缀 > 包含 > 子序列 > 字符覆盖；无关结果只在相关结果不足时保留。</summary>
    static List<OmniApi.OmniItem> RankByRelevance(string keyword, List<OmniApi.OmniItem> items)
    {
        var ranked = items
            .Select(item => (Item: item, Rank: Relevance(keyword, item?.Title)))
            .OrderBy(pair => pair.Rank)
            .ThenBy(pair => (pair.Item?.Title ?? "").Length)
            .ThenByDescending(pair => ParseScore(pair.Item?.Score))
            .ToList();
        var relevant = ranked.Where(pair => pair.Rank <= 4).ToList();
        if (relevant.Count >= 6) ranked = relevant;
        return ranked.Select(pair => pair.Item).ToList();
    }

    static int Relevance(string keyword, string title)
    {
        var key = Normalize(keyword);
        var value = Normalize(title);
        if (key.Length == 0 || value.Length == 0) return 5;
        if (value == key) return 0;
        if (value.StartsWith(key, StringComparison.Ordinal)) return 1;
        if (value.Contains(key, StringComparison.Ordinal)) return 2;
        if (key.Length >= 2 && IsSubsequence(key, value)) return 3;
        return Coverage(key, value) >= 0.6 ? 4 : 5;
    }

    static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var value = Trans.T2S(text).ToLowerInvariant();
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch)) builder.Append(ch);
        }
        return builder.ToString();
    }

    static bool IsSubsequence(string key, string value)
    {
        var index = 0;
        foreach (var ch in value)
        {
            if (index < key.Length && key[index] == ch) index++;
            if (index == key.Length) return true;
        }
        return false;
    }

    static double Coverage(string key, string value)
    {
        var chars = key.Distinct().ToList();
        if (chars.Count == 0) return 0;
        return (double)chars.Count(ch => value.Contains(ch)) / chars.Count;
    }

    /// <summary>直链媒体强制 parse=0；网页地址保留 parse=1 交给嗅探/换源兜底。</summary>
    static bool IsDirectMedia(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        var path = url.Split('?')[0].Split('#')[0].ToLowerInvariant();
        return path.EndsWith(".m3u8") || path.EndsWith(".mp4") || path.EndsWith(".mkv") ||
               path.EndsWith(".flv") || path.EndsWith(".ts") || path.EndsWith(".m4a") || path.EndsWith(".mp3");
    }

    static double ParseScore(string score) =>
        double.TryParse(score, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : 0;

    /// <summary>CatVod 用 $ # $$$ 做分隔符，名字里出现会把结构拆坏。</summary>
    static string CleanName(string raw, string fallback)
    {
        var value = (raw ?? "").Replace("$", " ").Replace("#", " ").Trim();
        return string.IsNullOrEmpty(value) ? fallback : value;
    }
}
