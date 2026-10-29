using System.Collections.Concurrent;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using TVBoxForWindows.Core;
using TVBoxForWindows.Engine;
using TVBoxForWindows.Net;

namespace TVBoxForWindows.UI;

/// <summary>海报加载器（契约 §5.4）：解析 @Referer=/@User-Agent= 后缀 → HttpUtil 下载 → 内存 LRU(256) + 磁盘缓存（AppPaths.Cache/img/md5）。失败返回 null，由控件显示占位。</summary>
public static class ImageLoader
{
    const int Capacity = 256;

    static readonly object Gate = new();
    static readonly ConcurrentDictionary<string, LinkedListNode<(string Key, BitmapImage Image)>> Cache = new();
    static readonly LinkedList<(string Key, BitmapImage Image)> Order = new();
    static readonly SemaphoreSlim FetchGate = new(6, 6);
    static readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> Inflight = new();

    /// <summary>加载海报（必须在 UI 线程调用；BitmapImage 是 UI 对象）。失败返回 null。</summary>
    public static async Task<BitmapImage> Load(string pic)
    {
        if (string.IsNullOrWhiteSpace(pic)) return null;
        pic = NormalizeLocalNodeUrl(pic.Trim());
        if (Cache.TryGetValue(pic, out var node)) { Touch(node); return node.Value.Image; }
        var bytes = await FetchShared(pic);
        if (bytes == null || bytes.Length == 0) return null;
        var image = await Decode(bytes);
        if (image != null) Put(pic, image);
        return image;
    }

    // ---------- 下载与磁盘缓存 ----------

    /// <summary>同一张海报只下载一次；全局最多 6 路并发，避免结果页几十张图同时抢占带宽。</summary>
    static async Task<byte[]> FetchShared(string pic)
    {
        var lazy = Inflight.GetOrAdd(pic, key => new Lazy<Task<byte[]>>(() => FetchGated(key)));
        try { return await lazy.Value; }
        finally { Inflight.TryRemove(pic, out _); }
    }

    static async Task<byte[]> FetchGated(string pic)
    {
        await FetchGate.WaitAsync();
        try { return await Fetch(pic); }
        finally { FetchGate.Release(); }
    }

    static async Task<byte[]> Fetch(string pic)
    {
        var file = CacheFile(pic);
        try { if (File.Exists(file)) return await File.ReadAllBytesAsync(file); } catch { }
        var (url, headers) = ParsePic(pic);
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            try { return File.Exists(url) ? await File.ReadAllBytesAsync(url) : null; }
            catch { return null; }
        }

        if (!headers.ContainsKey("User-Agent"))
            headers["User-Agent"] = string.IsNullOrWhiteSpace(Setting.Ua)
                ? "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/122.0.0.0 Safari/537.36"
                : Setting.Ua;
        if (!headers.ContainsKey("Accept"))
            headers["Accept"] = "image/avif,image/webp,image/*,*/*;q=0.8";
        if (!headers.ContainsKey("Referer") && Uri.TryCreate(url, UriKind.Absolute, out var douban) &&
            douban.Host.EndsWith("doubanio.com", StringComparison.OrdinalIgnoreCase))
            headers["Referer"] = "https://movie.douban.com/";

        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var res = await HttpUtil.Get(url, headers, null, 8000);
                if (ShouldRetryWithReferer(res) && !headers.ContainsKey("Referer") &&
                    Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)
                    {
                        ["Referer"] = uri.GetLeftPart(UriPartial.Authority) + "/",
                    };
                    res = await HttpUtil.Get(url, headers, null, 8000);
                }
                if (res.Code >= 400 || res.Body == null || res.Body.Length == 0)
                {
                    if (attempt == 0) continue;
                    return null;
                }
                try { await File.WriteAllBytesAsync(file, res.Body); } catch { }
                return res.Body;
            }
            catch (Exception e)
            {
                Logger.E("ImageLoader", url + " " + e.Message);
                if (attempt > 0) return null;
            }
        }
        return null;
    }

    static bool ShouldRetryWithReferer(OkResponse response)
    {
        if (response == null || response.Code >= 400 || response.Body == null || response.Body.Length == 0) return true;
        if (!response.Headers.TryGetValue("Content-Type", out var values)) return false;
        var type = values.FirstOrDefault() ?? "";
        return type.StartsWith("text/", StringComparison.OrdinalIgnoreCase);
    }

    static string NormalizeLocalNodeUrl(string value)
    {
        var referer = value.IndexOf("@Referer=", StringComparison.OrdinalIgnoreCase);
        var userAgent = value.IndexOf("@User-Agent=", StringComparison.OrdinalIgnoreCase);
        var marker = referer < 0 ? userAgent : userAgent < 0 ? referer : Math.Min(referer, userAgent);
        var raw = marker < 0 ? value : value[..marker];
        var suffix = marker < 0 ? "" : value[marker..];
        if (!Uri.TryCreate(NodeRuntime.BaseUrl, UriKind.Absolute, out var local) ||
            !Uri.TryCreate(raw, UriKind.Absolute, out var source) ||
            !source.IsLoopback ||
            (!source.AbsolutePath.StartsWith("/spider/", StringComparison.OrdinalIgnoreCase) &&
             !source.AbsolutePath.StartsWith("/website", StringComparison.OrdinalIgnoreCase))) return value;
        return NodeRuntime.BaseUrl.TrimEnd('/') + source.PathAndQuery + suffix;
    }

    /// <summary>解析 pic@Referer=xxx@User-Agent=yyy 后缀（顺序任意、大小写不敏感）。</summary>
    public static (string Url, Dictionary<string, string> Headers) ParsePic(string pic)
    {
        var headers = new Dictionary<string, string>();
        var markers = new[] { "@Referer=", "@User-Agent=" };
        int first = int.MaxValue;
        foreach (var m in markers)
        {
            var i = pic.IndexOf(m, StringComparison.OrdinalIgnoreCase);
            if (i >= 0 && i < first) first = i;
        }
        if (first == int.MaxValue) return (pic, headers);
        var url = pic[..first];
        var rest = pic[first..];
        while (rest.Length > 0)
        {
            string marker = null;
            foreach (var m in markers)
                if (rest.StartsWith(m, StringComparison.OrdinalIgnoreCase)) { marker = m; break; }
            if (marker == null) break;
            var value = rest[marker.Length..];
            int next = int.MaxValue;
            foreach (var m in markers)
            {
                var i = value.IndexOf(m, StringComparison.OrdinalIgnoreCase);
                if (i >= 0 && i < next) next = i;
            }
            var v = next == int.MaxValue ? value : value[..next];
            if (v.Length > 0) headers[marker == "@Referer=" ? "Referer" : "User-Agent"] = v;
            rest = next == int.MaxValue ? "" : value[next..];
        }
        return (url, headers);
    }

    static string CacheFile(string pic)
    {
        var dir = Path.Combine(AppPaths.Cache, "img");
        try { Directory.CreateDirectory(dir); } catch { }
        return Path.Combine(dir, Md5(pic));
    }

    static string Md5(string text)
        => Convert.ToHexString(MD5.HashData(System.Text.Encoding.UTF8.GetBytes(text ?? ""))).ToLowerInvariant();

    // ---------- 解码 ----------

    static async Task<BitmapImage> Decode(byte[] bytes)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var image = new BitmapImage
            {
                // 卡片最大显示 150px 宽；按 480px 解码，避免 3000×4000 原图占用几十 MB。
                DecodePixelWidth = 480,
            };
            await image.SetSourceAsync(stream);
            return image;
        }
        catch { return null; }
    }

    // ---------- 内存 LRU ----------

    static void Touch(LinkedListNode<(string Key, BitmapImage Image)> node)
    {
        lock (Gate)
        {
            if (node.List != Order) return;
            Order.Remove(node);
            Order.AddFirst(node);
        }
    }

    static void Put(string key, BitmapImage image)
    {
        lock (Gate)
        {
            if (Cache.TryRemove(key, out var exist) && exist.List == Order) Order.Remove(exist);
            var node = new LinkedListNode<(string Key, BitmapImage Image)>((key, image));
            Order.AddFirst(node);
            Cache[key] = node;
            while (Order.Count > Capacity)
            {
                var last = Order.Last;
                Order.RemoveLast();
                Cache.TryRemove(last.Value.Key, out _);
            }
        }
    }
}
