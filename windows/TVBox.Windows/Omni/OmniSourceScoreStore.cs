using System.Text.Json.Serialization;
using TVBoxForWindows.Core;

namespace TVBoxForWindows.Omni;

/// <summary>搜索源动态评分（与 Android SEARCH_OPTIMIZATION 2.9 一致）：延迟 + 结果数，指数衰减，本地持久化。</summary>
public sealed class OmniSourceScore
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("spider")] public bool Spider { get; set; }
    [JsonPropertyName("score")] public double Score { get; set; } = 50;
    [JsonPropertyName("samples")] public int Samples { get; set; }
    [JsonPropertyName("successes")] public int Successes { get; set; }
    [JsonPropertyName("failures")] public int Failures { get; set; }
    [JsonPropertyName("lastLatencyMs")] public long LastLatencyMs { get; set; }
    [JsonPropertyName("lastResults")] public int LastResults { get; set; }
    [JsonPropertyName("updatedAt")] public long UpdatedAt { get; set; }

    [JsonIgnore] public double EffectiveScore { get; set; }
}

public static class OmniSourceScoreStore
{
    const int MinSamples = 3;
    static readonly object Sync = new();
    static Dictionary<string, OmniSourceScore> _scores;
    static bool _loaded;

    static string FilePath => Path.Combine(AppPaths.Root, "omni", "search-scores.json");

    public static double EffectiveScore(string id, bool spider)
    {
        lock (Sync)
        {
            Load();
            return _scores.TryGetValue(Key(id, spider), out var score) ? Effective(score) : 50;
        }
    }

    public static long Latency(string id, bool spider)
    {
        lock (Sync)
        {
            Load();
            return _scores.TryGetValue(Key(id, spider), out var score) && score.LastLatencyMs > 0
                ? score.LastLatencyMs
                : 10_000;
        }
    }

    public static void Record(string id, string name, bool spider, long latencyMs, int results, bool success)
    {
        if (string.IsNullOrEmpty(id)) return;
        lock (Sync)
        {
            Load();
            var key = Key(id, spider);
            if (!_scores.TryGetValue(key, out var score))
            {
                score = new OmniSourceScore { Id = id, Name = name ?? "", Spider = spider, Score = 50 };
                _scores[key] = score;
            }
            if (success)
            {
                var latencyScore = LatencyScore(latencyMs);
                var outcomeScore = results > 0 ? 100 : 60;
                var sample = latencyScore * 0.55 + outcomeScore * 0.45;
                score.Score = score.Score * 0.85 + sample * 0.15;
                score.Successes++;
            }
            else
            {
                score.Score *= 0.85;
                score.Failures++;
            }
            score.Samples++;
            score.LastLatencyMs = Math.Max(0, latencyMs);
            score.LastResults = Math.Max(0, results);
            score.UpdatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Save();
        }
    }

    public static List<OmniSourceScore> Snapshot()
    {
        lock (Sync)
        {
            Load();
            return _scores.Values
                .Select(score => new OmniSourceScore
                {
                    Id = score.Id,
                    Name = score.Name,
                    Spider = score.Spider,
                    Score = score.Score,
                    Samples = score.Samples,
                    Successes = score.Successes,
                    Failures = score.Failures,
                    LastLatencyMs = score.LastLatencyMs,
                    LastResults = score.LastResults,
                    UpdatedAt = score.UpdatedAt,
                    EffectiveScore = Effective(score),
                })
                .OrderByDescending(score => score.EffectiveScore)
                .ThenBy(score => score.Name)
                .ToList();
        }
    }

    public static void Clear()
    {
        lock (Sync)
        {
            Load();
            _scores.Clear();
            Save();
        }
    }

    static double Effective(OmniSourceScore score)
    {
        if (score.Samples >= MinSamples) return score.Score;
        var c = Math.Clamp(score.Samples / (double)MinSamples, 0, 1);
        return 50 * (1 - c) + score.Score * c;
    }

    static double LatencyScore(long latencyMs)
    {
        if (latencyMs <= 300) return 100;
        if (latencyMs >= 8000) return 0;
        var ratio = Math.Log(latencyMs / 300.0) / Math.Log(8000.0 / 300.0);
        return Math.Clamp(100 * (1 - ratio), 0, 100);
    }

    static string Key(string id, bool spider) => (spider ? "spider:" : "site:") + (id ?? "");

    static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath))
            {
                _scores = new Dictionary<string, OmniSourceScore>(StringComparer.Ordinal);
                return;
            }
            var list = JsonUtil.Deserialize<List<OmniSourceScore>>(File.ReadAllText(FilePath)) ?? new();
            _scores = list
                .Where(score => score != null && !string.IsNullOrEmpty(score.Id))
                .GroupBy(score => Key(score.Id, score.Spider))
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
        }
        catch (Exception e)
        {
            Logger.E("OmniSourceScore.load", e.Message);
            _scores = new Dictionary<string, OmniSourceScore>(StringComparer.Ordinal);
        }
    }

    static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonUtil.Serialize(_scores.Values.ToList()));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception e)
        {
            Logger.E("OmniSourceScore.save", e.Message);
        }
    }
}
