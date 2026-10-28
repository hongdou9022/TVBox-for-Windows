using TVBoxForWindows.Player;

namespace TVBoxForWindows.Omni;

/// <summary>CloudTV 播放进度上报（移植自 omni/OmniProgressReporter.java）：同一 token 8 秒节流。</summary>
public static class OmniProgress
{
    static readonly object Sync = new();
    static string _lastKey = "";
    static long _lastTicks;

    public static void Report(PlayItem item, long positionMs, long durationMs)
    {
        if (item == null || string.IsNullOrEmpty(item.Key) || positionMs <= 0) return;
        if (!OmniSpider.Instance.TryGetContext(item.Key, out var context)) return;
        if (context == null || string.IsNullOrEmpty(context.PlayId)) return;
        lock (Sync)
        {
            var now = Environment.TickCount64;
            if (_lastKey == item.Key && now - _lastTicks < 8000) return;
            _lastKey = item.Key;
            _lastTicks = now;
        }
        Core.Logger.D("OmniProgress", $"{context.Title} {context.EpisodeName} {positionMs}/{durationMs}");
        _ = OmniApi.ReportProgressAsync(context.LibraryId, context.VideoId, context.PlayId,
            context.Title, context.Poster, context.EpisodeName, positionMs, durationMs);
    }
}
