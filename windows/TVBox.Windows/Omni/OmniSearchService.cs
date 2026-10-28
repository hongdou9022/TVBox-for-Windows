using TVBoxForWindows.Core;
using TVBoxForWindows.Models;

namespace TVBoxForWindows.Omni;

/// <summary>客户端本地搜索流（与 Android SEARCH_OPTIMIZATION.md v1.5 对齐）：
/// 全站搜索覆盖全部启用站点/兼容爬虫，逐源回调，按 matchRank/tokenHit/优先级排序，2 分钟缓存；
/// 服务端仅在本地源不可用或翻页无本地结果时兜底。</summary>
public static class OmniSearchService
{
    const long CacheTtlMs = 2 * 60 * 1000;
    const int CacheMax = 100;
    static readonly object Sync = new();
    static readonly Dictionary<string, (List<(string Source, int Priority, List<Vod> Items)> Batches, long Expires)> Cache = new();

    public static async Task SearchStreamingAsync(string keyword, string pg, Action<Site, List<Vod>> onSiteResult, CancellationToken ct)
    {
        keyword = (keyword ?? "").Trim();
        if (keyword.Length == 0) return;
        var page = int.TryParse(pg, out var parsed) ? Math.Max(1, parsed) : 1;
        var cacheKey = keyword + "|" + page;

        List<(string Source, int Priority, List<Vod> Items)> cached = null;
        lock (Sync)
        {
            if (Cache.TryGetValue(cacheKey, out var hit) && hit.Expires > Environment.TickCount64)
                cached = hit.Batches.Select(batch => (batch.Source, batch.Priority, batch.Items.ToList())).ToList();
        }
        if (cached != null)
        {
            foreach (var batch in cached)
            {
                if (ct.IsCancellationRequested) return;
                App.Post(() =>
                {
                    if (!ct.IsCancellationRequested)
                        onSiteResult?.Invoke(PseudoSite(batch.Source, batch.Priority), batch.Items);
                });
            }
            return;
        }

        var batches = new List<(string Source, int Priority, List<Vod> Items)>();
        void Emit(string source, int priority, List<OmniApi.OmniItem> items)
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
            lock (Sync) batches.Add((source, priority, filtered));
            App.Post(() =>
            {
                if (!ct.IsCancellationRequested)
                    onSiteResult?.Invoke(PseudoSite(source, priority), filtered);
            });
        }

        var sources = LocalSourceStore.Instance.Cached;
        var hasSites = (sources?.Sites ?? new()).Any(site => !string.IsNullOrEmpty(site?.Api));
        var hasSpiders = LocalSpiderEngine.Instance.EnabledSpiders().Count > 0;

        var siteTask = hasSites
            ? LocalSiteEngine.Instance.SearchStreamingAsync(keyword, page,
                (site, items) => Emit(site.Name, site.Priority, items), ct)
            : Task.CompletedTask;
        var spiderTask = hasSpiders
            ? LocalSpiderEngine.Instance.SearchStreamingAsync(keyword, page,
                (spider, items) => Emit(spider.Name, spider.Priority, items), ct)
            : Task.CompletedTask;
        await Task.WhenAll(siteTask, spiderTask);

        // 服务端只在本地源不可用，或翻页本地没有结果时兜底；正常首页搜索全程本地。
        if (batches.Count == 0 && (!hasSites && !hasSpiders || page >= 2))
        {
            try
            {
                var remote = await OmniApi.SearchAsync(keyword, page, true);
                foreach (var group in (remote.Items ?? new()).GroupBy(item => item.LibraryName ?? "CloudTV"))
                    Emit(group.Key, 0, group.ToList());
            }
            catch (Exception e)
            {
                Logger.E("OmniSearchService.fallback", e.Message);
            }
        }

        lock (Sync)
        {
            Cache[cacheKey] = (batches.Select(batch => (batch.Source, batch.Priority, batch.Items.ToList())).ToList(),
                Environment.TickCount64 + CacheTtlMs);
            if (Cache.Count > CacheMax)
            {
                foreach (var pair in Cache.Where(pair => pair.Value.Expires <= Environment.TickCount64).Take(20).ToList())
                    Cache.Remove(pair.Key);
            }
        }
    }

    static Site PseudoSite(string sourceName, int priority) => new()
    {
        Key = OmniConfig.SourceKey,
        Name = sourceName,
        Type = 3,
        Api = "omni",
        Searchable = 1,
        SourcePriority = priority,
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
