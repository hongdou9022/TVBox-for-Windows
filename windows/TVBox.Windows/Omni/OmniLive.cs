using TVBoxForWindows.Core;
using TVBoxForWindows.Live;
using TVBoxForWindows.Models;

namespace TVBoxForWindows.Omni;

/// <summary>CloudTV IPTV 直播源（移植自 Box 的 ApiConfig.loadOmniLiveChannels）：把后端频道转成 TVBox Live 模型。</summary>
public static class OmniLive
{
    const string Tag = "OmniLive";

    public static async Task LoadAsync()
    {
        if (!OmniConfig.IsLoggedIn) return;
        try
        {
            var sources = await OmniApi.IptvSourcesAsync();
            var lives = new List<Models.Live>();
            foreach (var source in sources)
            {
                if (source == null || string.IsNullOrEmpty(source.Id)) continue;
                List<OmniApi.IptvGroup> groups;
                try
                {
                    groups = await OmniApi.IptvChannelsAsync(source.Id);
                }
                catch (Exception e)
                {
                    Logger.E(Tag, "channels " + source.Name + ": " + e.Message);
                    continue;
                }
                var live = new Models.Live
                {
                    Name = string.IsNullOrEmpty(source.Name) ? "CloudTV 直播" : source.Name,
                    Header = JsonUtil.ToMap(source.Header),
                };
                foreach (var group in groups)
                {
                    if (group?.Channels == null) continue;
                    var liveGroup = new LiveGroup { Name = group.Name ?? "" };
                    foreach (var channel in group.Channels)
                    {
                        if (channel == null) continue;
                        var urls = (channel.PlayUrls ?? new())
                            .Where(play => play != null && !string.IsNullOrEmpty(play.Url))
                            .Select(play => play.Url)
                            .ToList();
                        if (urls.Count == 0) continue;
                        liveGroup.Channel.Add(new LiveChannel
                        {
                            Name = channel.Name ?? "",
                            Urls = urls,
                            Logo = channel.Logo ?? "",
                            Format = channel.Format ?? "",
                            Ua = (channel.PlayUrls ?? new()).FirstOrDefault(play => !string.IsNullOrEmpty(play?.Ua))?.Ua ?? "",
                            Epg = channel.Name ?? "",
                        });
                    }
                    if (liveGroup.Channel.Count > 0) live.Groups.Add(liveGroup);
                }
                if (live.Groups.Count > 0) lives.Add(live);
            }
            LiveConfigService.Instance.ApplyOmni(lives);
        }
        catch (Exception e)
        {
            Logger.E(Tag, "load: " + e.Message);
        }
    }
}
