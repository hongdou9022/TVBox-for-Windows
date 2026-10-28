using TVBoxForWindows.Core;
using TVBoxForWindows.Models;

namespace TVBoxForWindows.Omni;

/// <summary>客户端本地搜索流（与 Android SEARCH_OPTIMIZATION.md 对齐）：
/// 只搜 isHomeSearch 源，站点/爬虫边到边回调，按 matchRank/tokenHit 排序，2 分钟缓存；不请求服务端搜索。</summary>
public static class OmniSearchService
{
    const long CacheTtlMs = 2 * 60 * 1000;
    const int CacheMax = 100;
    static readonly object Sync = new();
    static readonly Dictionary<string, (List<(string Source, List<Vod> Items)> Batches, long Expires)> Cache = new();

    public static async Task SearchStreamingAsync(string keyword, string pg, Action<Site, List<Vod>> onSiteResult, CancellationToken ct)
    {
        keyword = (keyword ?? "").Trim();
        if (keyword.Length == 0) return;
        var page = int.TryParse(pg, out var parsed) ? Math.Max(1, parsed) : 1;
        var cacheKey = keyword + "|" + page;

        List<(string Source, List<Vod> Items)> cached = null;
        lock (Sync)
        {
            if (Cache.TryGetValue(cacheKey, out var hit) && hit.Expires > Environment.TickCount64)
                cached = hit.Batches.Select(batch => (batch.Source, batch.Items.ToList())).ToList();
        }
        if (cached != null)
        {
            foreach (var batch in cached)
            {
                if (ct.IsCancellationRequested) return;
                App.Post(() =>
                {
                    if (!ct.IsCancellationRequested) onSiteResult?.Invoke(PseudoSite(batch.Source), batch.Items);
                });
            }
            return;
        }

        var batches = new List<(string Source, List<Vod> Items)>();
        void Emit(string source, List<OmniApi.OmniItem> items)
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
            lock (Sync) batches.Add((source, filtered));
            App.Post(() =>
            {
                if (!ct.IsCancellationRequested) onSiteResult?.Invoke(PseudoSite(source), filtered);
            });
        }

        var siteTask = LocalSiteEngine.Instance.SearchStreamingAsync(keyword, page,
            (site, items) => Emit(site.Name, items), ct);
        var spiderTask = LocalSpiderEngine.Instance.SearchStreamingAsync(keyword, page,
            (spider, items) => Emit(spider.Name, items), ct);
        await Task.WhenAll(siteTask, spiderTask);

        lock (Sync)
        {
            Cache[cacheKey] = (batches.Select(batch => (batch.Source, batch.Items.ToList())).ToList(),
                Environment.TickCount64 + CacheTtlMs);
            if (Cache.Count > CacheMax)
            {
                foreach (var pair in Cache.Where(pair => pair.Value.Expires <= Environment.TickCount64).Take(20).ToList())
                    Cache.Remove(pair.Key);
            }
        }
    }

    static Site PseudoSite(string sourceName) => new()
    {
        Key = OmniConfig.SourceKey,
        Name = sourceName,
        Type = 3,
        Api = "omni",
        Searchable = 1,
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
