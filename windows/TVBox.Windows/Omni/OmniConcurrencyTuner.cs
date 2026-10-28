namespace TVBoxForWindows.Omni;

/// <summary>自动并发调节（SEARCH_OPTIMIZATION 4.5）：按上一轮成功率与平均延迟在 4/6/8 三档间切换。
/// 用户手动指定并发时不参与调节。</summary>
public static class OmniConcurrencyTuner
{
    static readonly object Sync = new();
    static readonly int[] Tiers = { 4, 6, 8 };
    static int _tier = Tiers.Length - 1;

    public static int Current
    {
        get { lock (Sync) return Tiers[_tier]; }
    }

    public static void Record(int attempted, int succeeded, double avgLatencyMs)
    {
        if (attempted < 6) return;
        lock (Sync)
        {
            var successRate = succeeded / (double)attempted;
            if (successRate < 0.6 || avgLatencyMs > 4000)
                _tier = Math.Max(0, _tier - 1);
            else if (successRate >= 0.85 && avgLatencyMs < 1500)
                _tier = Math.Min(Tiers.Length - 1, _tier + 1);
        }
    }
}
