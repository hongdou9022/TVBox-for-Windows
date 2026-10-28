using System.Text;

namespace TVBoxForWindows.Omni;

/// <summary>CatVod 协议单字符串 ID 编解码（移植自 omni/OmniIds.java）：把库/视频/线路/集压进 URL-safe token。</summary>
public static class OmniIds
{
    const char Sep = '\u0001';

    public static string EncodeType(string libraryId, string categoryId) => Encode(libraryId, categoryId);

    public static string[] DecodeType(string value) => Decode(value, 2);

    public static string EncodeVod(string libraryId, string videoId) => Encode(libraryId, videoId);

    public static string EncodeVod(string sourceType, string libraryId, string videoId) =>
        Encode(sourceType, libraryId, videoId);

    public static VodRef DecodeVodRef(string value)
    {
        var parts = Decode(value, 3);
        if (parts != null) return new VodRef(parts[0], parts[1], parts[2]);
        parts = Decode(value, 2);
        return parts == null ? null : new VodRef("spider", parts[0], parts[1]);
    }

    public static string EncodePlay(string sourceType, string libraryId, string videoId, int line, int episode) =>
        Encode(sourceType, libraryId, videoId, line.ToString(), episode.ToString());

    public static PlayRef DecodePlay(string value)
    {
        var parts = Decode(value, 5);
        if (parts != null && int.TryParse(parts[3], out var line) && int.TryParse(parts[4], out var episode))
            return new PlayRef(parts[0], parts[1], parts[2], line, episode);
        parts = Decode(value, 4);
        if (parts == null) return null;
        return int.TryParse(parts[2], out line) && int.TryParse(parts[3], out episode)
            ? new PlayRef("spider", parts[0], parts[1], line, episode)
            : null;
    }

    static string Encode(params string[] parts)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) builder.Append(Sep);
            builder.Append(parts[i] ?? "");
        }
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(builder.ToString()))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    static string[] Decode(string value, int count)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try
        {
            var clean = value.Replace('-', '+').Replace('_', '/');
            if (clean.Length % 4 != 0) clean = clean.PadRight(clean.Length + (4 - clean.Length % 4), '=');
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(clean)).Split(Sep);
            return parts.Length == count ? parts : null;
        }
        catch
        {
            return null;
        }
    }

    public sealed class VodRef
    {
        public string SourceType { get; }
        public string LibraryId { get; }
        public string VideoId { get; }

        public VodRef(string sourceType, string libraryId, string videoId)
        {
            SourceType = sourceType;
            LibraryId = libraryId;
            VideoId = videoId;
        }
    }

    public sealed class PlayRef
    {
        public string SourceType { get; }
        public string LibraryId { get; }
        public string VideoId { get; }
        public int Line { get; }
        public int Episode { get; }

        public PlayRef(string sourceType, string libraryId, string videoId, int line, int episode)
        {
            SourceType = sourceType;
            LibraryId = libraryId;
            VideoId = videoId;
            Line = line;
            Episode = episode;
        }
    }
}
