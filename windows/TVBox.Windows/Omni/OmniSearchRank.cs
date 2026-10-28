using System.Text;

namespace TVBoxForWindows.Omni;

/// <summary>搜索匹配规则（与 Android SEARCH_OPTIMIZATION.md 一致）：归一化、分词、5 级 matchRank、tokenHit。</summary>
public static class OmniSearchRank
{
    static readonly HashSet<char> Separators = new(
        new[] { ' ', '\t', '\u3000', '·', ':', '：', ',', '，', '.', '。', '!', '！', '?', '？', '-', '—', '_', '/', '|', '(', ')', '（', '）', '[', ']', '【', '】' });

    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var lower = text.ToLowerInvariant();
        var builder = new StringBuilder(lower.Length);
        foreach (var ch in lower)
        {
            if (char.IsWhiteSpace(ch) || Separators.Contains(ch)) continue;
            builder.Append(ch);
        }
        return builder.ToString();
    }

    public static List<string> Tokens(string keyword) =>
        (keyword ?? "")
            .Split(Separators.ToArray(), StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .Where(token => token.Length >= 2)
            .Distinct()
            .ToList();

    /// <summary>粗筛：标题与关键词无任何交集时丢弃（太短的词不判）。</summary>
    public static bool MatchesKeyword(string keyword, string title)
    {
        var query = Normalize(keyword);
        var name = Normalize(title);
        if (query.Length < 2 || name.Length < 2) return true;
        if (name.Contains(query, StringComparison.Ordinal) || query.Contains(name, StringComparison.Ordinal)) return true;
        return Tokens(keyword).Any(token => name.Contains(token, StringComparison.Ordinal));
    }

    /// <summary>0 完全 / 1 前缀 / 2 包含 / 3 分词命中 / 4 其他。</summary>
    public static int MatchRank(string keyword, string title)
    {
        var query = Normalize(keyword);
        var name = Normalize(title);
        if (query.Length == 0 || name.Length == 0) return 4;
        if (name == query) return 0;
        if (name.StartsWith(query, StringComparison.Ordinal)) return 1;
        if (name.Contains(query, StringComparison.Ordinal)) return 2;
        return Tokens(keyword).Any(token => name.Contains(token, StringComparison.Ordinal)) ? 3 : 4;
    }

    /// <summary>命中的关键词词序；没有命中返回 int.MaxValue。</summary>
    public static int TokenHit(string keyword, string title)
    {
        var name = Normalize(title);
        var tokens = Tokens(keyword);
        for (var i = 0; i < tokens.Count; i++)
            if (name.Contains(tokens[i], StringComparison.Ordinal)) return i;
        return int.MaxValue;
    }
}
