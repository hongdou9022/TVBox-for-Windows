using TVBoxForWindows.Core;
using TVBoxForWindows.Models;

namespace TVBoxForWindows.Omni;

/// <summary>客户端本地搜索流（与 Android SEARCH_OPTIMIZATION.md v1.5 对齐）：
/// 全站搜索覆盖全部启用站点/兼容爬虫，尊重范围设置，逐源回调（带缓存标记），
/// 按 matchRank/tokenHit/优先级排序，2 分钟查询缓存；失败源聚合上报；
/// 服务端仅在本地源不可用或翻页无本地结果时兜底。</summary>
public static class OmniSearchService
{
    const long CacheTtlMs = 2 * 60 * 1000;
    const int CacheMax = 100;
    static readonly object Sync = new();
    static readonly Dictionary<string, (List<SearchBatch> Batches, int Searched, long Expires)> Cache = new();

    sealed class SearchBatch
    {
        public string Source { get; init; } = "";
        public int Priority { get; init; }
        public List<Vod> Items { get; init; } = new();
    }

    /// <summary>范围菜单候选：全站 + 勾选首页搜索的站点/兼容爬虫（爬虫优先，按优先级降序）。</summary>
    public static List<OmniScopeOption> ScopeOptions()
    {
        var sources = LocalSourceStore.Instance.Cached;
        var options = new List<OmniScopeOption> { new() { Type = "", Id = "", Name = "全站" } };
        var spiders = (sources?.Spiders ?? new())
            .Where(spider => spider != null && !string.IsNullOrEmpty(spider.Id) &&
                             spider.IsHomeSearch && spider.ClientCompatible)
            .OrderByDescending(spider => spider.Priority)
            .ThenBy(spider => spider.Name)
            .Select(spider => new OmniScopeOption { Type = "spider", Id = spider.Id, Name = spider.Name });
        var sites = (sources?.Sites ?? new())
            .Where(site => site != null && !string.IsNullOrEmpty(site.Id) &&
                           !string.IsNullOrEmpty(site.Api) && site.IsHomeSearch)
            .OrderByDescending(site => site.Priority)
            .ThenBy(site => site.Name)
            .Select(site => new OmniScopeOption { Type = "site", Id = site.Id, Name = site.Name });
        options.AddRange(spiders);
        options.AddRange(sites);
        return options;
    }

    public static async Task SearchStreamingAsync(string keyword, string pg,
        Action<Site, List<Vod>> onSiteResult, CancellationToken ct,
        Action<OmniSearchStatus> onStatus = null)
    {
        keyword = (keyword ?? "").Trim();
        if (keyword.Length == 0) return;
        var page = int.TryParse(pg, out var parsed) ? Math.Max(1, parsed) : 1;
        var cacheKey = (LocalSourceStore.Instance.Cached?.Version ?? "") + "|" +
                       OmniConfig.SearchScopeType + "|" + OmniConfig.SearchScopeId + "|" + keyword + "|" + page;

        (List<SearchBatch> Batches, int Searched) cached = default;
        var hit = false;
        lock (Sync)
        {
            if (Cache.TryGetValue(cacheKey, out var entry) && entry.Expires > Environment.TickCount64)
            {
                cached = (entry.Batches, entry.Searched);
                hit = true;
            }
        }
        if (hit)
        {
            foreach (var batch in cached.Batches)
            {
                if (ct.IsCancellationRequested) return;
                App.Post(() =>
                {
                    if (!ct.IsCancellationRequested)
                        onSiteResult?.Invoke(PseudoSite(batch.Source, batch.Priority, true), batch.Items);
                });
            }
            App.Post(() =>
            {
                if (!ct.IsCancellationRequested)
                    onStatus?.Invoke(new OmniSearchStatus { SearchedSources = cached.Searched });
            });
            return;
        }

        var batches = new List<SearchBatch>();
        var failures = new List<OmniSearchFailure>();
        void Emit(string source, int priority, List<OmniApi.OmniItem> items, bool fromCache)
        {
            var filtered = items
                .Where(item => item != null && !string.IsNullOrEmpty(item.VideoId) &&
                               OmniSearchRank.MatchesKeyword(keyword, item.Title))
                .GroupBy(item => item.LibraryId + "|" + item.VideoId)
                .Select(group => group.First())
                .OrderBy(item => OmniSearchRank.MatchRank(keyword, item.Title))
                .ThenBy(item => OmniSearchRank.TokenHit(keyword, item.Title))
                .ThenBy(item => item.Title)
                .Select(item => ToVod(item, source))
                .ToList();
            if (filtered.Count == 0) return;
            OmniTitleIndex.Record(filtered.Select(item => item.Name));
            lock (Sync) batches.Add(new SearchBatch { Source = source, Priority = priority, Items = filtered });
            App.Post(() =>
            {
                if (!ct.IsCancellationRequested)
                    onSiteResult?.Invoke(PseudoSite(source, priority, fromCache), filtered);
            });
        }
        void Fail(string type, string name, string reason)
        {
            lock (Sync) failures.Add(new OmniSearchFailure { SourceType = type, Name = name, Reason = reason });
        }

        var scopedSites = LocalSiteEngine.Instance.ScopedSites();
        var scopedSpiders = LocalSpiderEngine.Instance.ScopedSpiders();
        var searched = scopedSites.Count + scopedSpiders.Count;

        var siteTask = scopedSites.Count > 0
            ? LocalSiteEngine.Instance.SearchStreamingAsync(keyword, page,
                (site, items, fromCache) => Emit(site.Name, site.Priority, items, fromCache),
                (name, reason) => Fail("site", name, reason), ct)
            : Task.CompletedTask;
        var spiderTask = scopedSpiders.Count > 0
            ? LocalSpiderEngine.Instance.SearchStreamingAsync(keyword, page,
                (spider, items, fromCache) => Emit(spider.Name, spider.Priority, items, fromCache),
                (name, reason) => Fail("spider", name, reason), ct)
            : Task.CompletedTask;
        await Task.WhenAll(siteTask, spiderTask);

        // 服务端只在本地源不可用，或翻页本地没有结果时兜底；指定范围时不越过用户选择。
        bool HasResults()
        {
            lock (Sync) return batches.Count > 0;
        }
        var scoped = !string.IsNullOrEmpty(OmniConfig.SearchScopeType);
        if (!HasResults() && !scoped && (searched == 0 || page >= 2))
        {
            try
            {
                var remote = await OmniApi.SearchAsync(keyword, page, true);
                foreach (var group in (remote.Items ?? new()).GroupBy(item => item.LibraryName ?? "CloudTV"))
                    Emit(group.Key, 0, group.ToList(), false);
            }
            catch (Exception e)
            {
                Logger.E("OmniSearchService.fallback", e.Message);
            }
        }

        OmniTitleIndex.Flush();
        List<OmniSearchFailure> failureSnapshot;
        lock (Sync)
        {
            var batchSnapshot = batches.ToList();
            failureSnapshot = failures.ToList();
            if (batchSnapshot.Count > 0)
                Cache[cacheKey] = (batchSnapshot, searched, Environment.TickCount64 + CacheTtlMs);
            if (Cache.Count > CacheMax)
            {
                foreach (var pair in Cache.Where(pair => pair.Value.Expires <= Environment.TickCount64).Take(20).ToList())
                    Cache.Remove(pair.Key);
            }
        }
        App.Post(() =>
        {
            if (!ct.IsCancellationRequested)
                onStatus?.Invoke(new OmniSearchStatus
                {
                    SearchedSources = searched,
                    Failures = failureSnapshot,
                });
        });
    }

    static Site PseudoSite(string sourceName, int priority, bool fromCache) => new()
    {
        Key = OmniConfig.SourceKey,
        Name = sourceName,
        Type = 3,
        Api = "omni",
        Searchable = 1,
        SourcePriority = priority,
        FromCache = fromCache,
    };

    static Vod ToVod(OmniApi.OmniItem item, string sourceName) => new()
    {
        Id = OmniIds.EncodeVod(string.IsNullOrEmpty(item.SourceType) ? "spider" : item.SourceType,
            item.LibraryId, item.VideoId),
        Name = item.Title ?? "",
        Pic = item.Poster ?? "",
        Remarks = item.Remark ?? "",
        Year = item.Year ?? "",
        SourceName = string.IsNullOrEmpty(item.LibraryName) ? sourceName : item.LibraryName,
    };
}
