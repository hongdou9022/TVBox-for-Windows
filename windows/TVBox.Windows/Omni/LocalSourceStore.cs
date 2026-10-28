using System.Text.Json.Serialization;
using TVBoxForWindows.Core;

namespace TVBoxForWindows.Omni;

/// <summary>本地抓取配置缓存（移植自 omni/local/LocalSourceStore.java）：服务端 ETag 未变化时返回 304，复用本地站点列表。</summary>
public sealed class LocalSourceStore
{
    const string Tag = "LocalSourceStore";
    const long SyncTtlMs = 10 * 60 * 1000;
    const int CacheSchema = 2;

    public static LocalSourceStore Instance { get; } = new();

    readonly SemaphoreSlim _lock = new(1, 1);
    OmniApi.OmniLocalSources _sources;
    string _etag = "";
    long _syncedAt;
    bool _loaded;

    sealed class CacheFile
    {
        [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
        [JsonPropertyName("etag")] public string Etag { get; set; } = "";
        [JsonPropertyName("syncedAt")] public long SyncedAt { get; set; }
        [JsonPropertyName("sources")] public OmniApi.OmniLocalSources Sources { get; set; }
    }

    static string FilePath => Path.Combine(AppPaths.Root, "omni", "local-sources.json");

    public OmniApi.OmniLocalSources Cached
    {
        get { Load(); return _sources; }
    }

    /// <summary>退出登录/切换服务器：清空本地站点配置。</summary>
    public void Clear()
    {
        _sources = null;
        _etag = "";
        _syncedAt = 0;
        _loaded = true;
        try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
    }

    public async Task<OmniApi.OmniLocalSources> SyncAsync(bool force = false)    {
        if (!OmniConfig.IsLoggedIn) { Load(); return _sources; }
        await _lock.WaitAsync();
        try
        {
            Load();
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!force && _sources != null && now - _syncedAt < SyncTtlMs) return _sources;
            try
            {
                var result = await OmniApi.LocalSourcesAsync(_etag);
                if (result.NotModified)
                {
                    _syncedAt = now;
                    Save();
                    return _sources;
                }
                if (result.Sources?.Sites != null)
                {
                    _sources = result.Sources;
                    _etag = result.Etag ?? "";
                    _syncedAt = now;
                    Save();
                }
            }
            catch (Exception e)
            {
                Logger.E(Tag, "sync: " + e.Message);
            }
            return _sources;
        }
        finally
        {
            _lock.Release();
        }
    }

    void Load()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(FilePath)) return;
            var cache = JsonUtil.Deserialize<CacheFile>(File.ReadAllText(FilePath));
            if (cache == null) return;
            _sources = cache.Sources;
            _etag = cache.Etag ?? "";
            _syncedAt = cache.SyncedAt;
            if (cache.SchemaVersion != CacheSchema)
            {
                // 旧缓存没有 spiders，升级后强制重新拉一次配置。
                _syncedAt = 0;
            }
        }
        catch (Exception e)
        {
            Logger.E(Tag, "load: " + e.Message);
        }
    }

    void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var cache = new CacheFile
            {
                SchemaVersion = CacheSchema,
                Etag = _etag,
                SyncedAt = _syncedAt,
                Sources = _sources,
            };
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonUtil.Serialize(cache));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception e)
        {
            Logger.E(Tag, "save: " + e.Message);
        }
    }
}
