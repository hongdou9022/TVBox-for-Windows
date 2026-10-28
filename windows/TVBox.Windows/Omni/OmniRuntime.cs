using TVBoxForWindows.Core;
using TVBoxForWindows.Engine;
using TVBoxForWindows.Models;

namespace TVBoxForWindows.Omni;

/// <summary>CloudTV 运行入口：登录后注入唯一内置源，取代 TVBox 配置体系。</summary>
public sealed class OmniRuntime
{
    public static OmniRuntime Instance { get; } = new();

    public Site Site { get; } = new()
    {
        Key = OmniConfig.SourceKey,
        Name = OmniConfig.SourceName,
        Api = "omni",
        Type = 3,
        Searchable = 1,
        QuickSearch = 1,
        Changeable = 0,
    };

    /// <summary>把内置源写入配置服务；已有登录态即可调用，不发起网络请求。</summary>
    public void ApplyToConfig()
    {
        var cid = AccountCid();
        if (cid != 0) Stores.MigrateCid(0, cid);
        VodConfigService.Instance.ApplyOmniSource(Site, cid);
    }

    /// <summary>按服务器 + 用户生成稳定的本地数据 cid，隔离多账号历史与收藏。</summary>
    public static int AccountCid()
    {
        var text = (OmniConfig.BaseUrl ?? "") + "|" + (OmniConfig.UserId ?? "");
        if (string.IsNullOrEmpty(text) || text == "|") return 0;
        unchecked
        {
            var hash = 2166136261u;
            foreach (var ch in text)
            {
                hash ^= ch;
                hash *= 16777619u;
            }
            var value = (int)(hash & 0x7FFFFFFF);
            return value == 0 ? 1 : value;
        }
    }

    /// <summary>登录成功后启动：注入源并同步本地抓取配置（失败不阻塞进入应用）。</summary>
    public async Task StartAsync()
    {
        if (!OmniConfig.IsLoggedIn) throw new OmniException(401, "请先登录 CloudTV");
        ApplyToConfig();
        await LocalSourceStore.Instance.SyncAsync();
        LocalSpiderEngine.Instance.Reset();
        _ = LocalSpiderEngine.Instance.WarmupAsync();
    }

    /// <summary>重新拉取配置（设置页“重新同步”），并重建爬虫运行时。</summary>
    public async Task ReloadAsync()
    {
        if (!OmniConfig.IsLoggedIn) throw new OmniException(401, "请先登录 CloudTV");
        ApplyToConfig();
        await LocalSourceStore.Instance.SyncAsync(force: true);
        LocalSpiderEngine.Instance.Reset();
        _ = LocalSpiderEngine.Instance.WarmupAsync();
        await OmniLive.LoadAsync();
    }
}
