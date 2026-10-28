using System.Text.Json.Serialization;
using TVBoxForWindows.Core;

namespace TVBoxForWindows.Omni;

/// <summary>本地高频片名索引（SEARCH_OPTIMIZATION 4.6）：累计搜索结果片名频次，
/// 输入时提供前缀/包含联想与编辑距离 ≤1 的纠错候选。</summary>
public static class OmniTitleIndex
{
    const int MaxTitles = 2000;
    static readonly object Sync = new();
    static Dictionary<string, Entry> _entries;
    static bool _dirty;

    sealed class Entry
    {
        [JsonPropertyName("title")] public string Title { get; set; } = "";
        [JsonPropertyName("count")] public int Count { get; set; } = 1;
        [JsonPropertyName("updatedAt")] public long UpdatedAt { get; set; }
    }

    static string FilePath => Path.Combine(AppPaths.Root, "omni", "title-index.json");

    /// <summary>累计片名频次；调用方在搜索结束后 Flush 落盘。</summary>
    public static void Record(IEnumerable<string> titles)
    {
        lock (Sync)
        {
            Load();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var raw in titles)
            {
                var display = (raw ?? "").Trim();
                if (display.Length < 2) continue;
                var key = OmniSearchRank.Normalize(display);
                if (key.Length < 2) continue;
                if (_entries.TryGetValue(key, out var entry))
                {
                    entry.Count++;
                    entry.Title = display;
                    entry.UpdatedAt = now;
                }
                else
                {
                    _entries[key] = new Entry { Title = display, UpdatedAt = now };
                }
            }
            _dirty = true;
        }
    }

    public static void Flush()
    {
        lock (Sync)
        {
            if (!_dirty) return;
            Load();
            Trim();
            Save();
            _dirty = false;
        }
    }

    /// <summary>返回联想/纠错候选：前缀优先、包含次之、编辑距离 ≤1 最后，同档按频次降序。</summary>
    public static List<string> Suggest(string input, int limit = 6)
    {
        var query = OmniSearchRank.Normalize(input ?? "");
        if (query.Length < 2) return new();

        var scored = new List<(string Title, int Rank, int Count)>();
        lock (Sync)
        {
            Load();
            foreach (var pair in _entries)
            {
                var key = pair.Key;
                if (key.Length < 2 || key == query) continue;
                int rank;
                if (key.StartsWith(query, StringComparison.Ordinal)) rank = 0;
                else if (key.Contains(query, StringComparison.Ordinal)) rank = 1;
                else if (WithinOneEdit(key, query)) rank = 2;
                else continue;
                scored.Add((pair.Value.Title, rank, pair.Value.Count));
            }
        }
        return scored
            .OrderBy(item => item.Rank)
            .ThenByDescending(item => item.Count)
            .ThenBy(item => item.Title.Length)
            .ThenBy(item => item.Title, StringComparer.CurrentCulture)
            .Select(item => item.Title)
            .Distinct(StringComparer.CurrentCulture)
            .Take(limit)
            .ToList();
    }

    static bool WithinOneEdit(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 1) return false;
        if (a.Length == b.Length)
        {
            var diff = 0;
            for (var i = 0; i < a.Length; i++)
            {
                if (a[i] == b[i]) continue;
                if (++diff > 1) return false;
            }
            return true;
        }
        if (a.Length > b.Length) (a, b) = (b, a);
        int left = 0, right = 0;
        var skipped = false;
        while (left < a.Length && right < b.Length)
        {
            if (a[left] == b[right])
            {
                left++;
                right++;
                continue;
            }
            if (skipped) return false;
            skipped = true;
            right++;
        }
        return true;
    }

    static void Load()
    {
        if (_entries != null) return;
        _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(FilePath)) return;
            var list = JsonUtil.Deserialize<List<Entry>>(File.ReadAllText(FilePath)) ?? new();
            _entries = list
                .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Title))
                .GroupBy(entry => OmniSearchRank.Normalize(entry.Title))
                .Where(group => group.Key.Length >= 2)
                .ToDictionary(group => group.Key,
                    group => group.OrderByDescending(entry => entry.Count).First(),
                    StringComparer.Ordinal);
        }
        catch (Exception e)
        {
            Logger.E("OmniTitleIndex.load", e.Message);
        }
    }

    static void Trim()
    {
        if (_entries.Count <= MaxTitles) return;
        foreach (var pair in _entries
                     .OrderBy(pair => pair.Value.Count)
                     .ThenBy(pair => pair.Value.UpdatedAt)
                     .Take(_entries.Count - MaxTitles)
                     .ToList())
            _entries.Remove(pair.Key);
    }

    static void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonUtil.Serialize(_entries.Values.ToList()));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception e)
        {
            Logger.E("OmniTitleIndex.save", e.Message);
        }
    }
}
