using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using TVBoxForWindows.Core;
using TVBoxForWindows.Net;

namespace TVBoxForWindows.Omni;

/// <summary>OmniBox 脚本原生桥（移植自 omni/local/OmniNativeBridge.java），由 shim 以 OmniNative.* 调用。</summary>
public sealed class OmniNative
{
    const string Tag = "OmniNative";

    readonly string _spiderId;
    readonly ConcurrentDictionary<string, (string Value, long Expires)> _cache = new();

    public OmniNative(string spiderId) => _spiderId = spiderId ?? "";

    /// <summary>同步 HTTP：返回 {statusCode, headers, body} JSON，供 shim 的 OmniBox.request/axios 使用。</summary>
    public string Request(string url, string method, string headersJson, string body)
    {
        try
        {
            var verb = string.IsNullOrEmpty(method) ? "GET" : method.ToUpperInvariant();
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var contentType = "application/json; charset=utf-8";
            if (!string.IsNullOrEmpty(headersJson) && JsonUtil.Parse(headersJson) is JsonObject headerObject)
            {
                foreach (var kv in headerObject)
                {
                    var value = kv.Value?.ToString() ?? "";
                    if (kv.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                    {
                        contentType = value;
                        continue;
                    }
                    headers[kv.Key] = value;
                }
            }
            var hasBody = verb is "POST" or "PUT" or "PATCH" or "DELETE";
            var payload = hasBody ? Encoding.UTF8.GetBytes(body ?? "") : null;
            var response = HttpUtil.Execute(verb, url, headers, null, payload,
                hasBody ? contentType : null, true, 10000).GetAwaiter().GetResult();
            var headerMap = new Dictionary<string, string>();
            foreach (var kv in response.Headers) headerMap[kv.Key] = kv.Value.FirstOrDefault() ?? "";
            return JsonUtil.Serialize(new Dictionary<string, object>
            {
                ["statusCode"] = response.Code,
                ["headers"] = headerMap,
                ["body"] = response.Text(CharsetOf(response)),
            });
        }
        catch (Exception e)
        {
            Logger.E(Tag + "[" + _spiderId + "].request", e.Message);
            return "{\"statusCode\":0,\"headers\":{},\"body\":\"\"}";
        }
    }

    public void Log(string level, string message)
    {
        var text = "[" + _spiderId + "] " + (message ?? "");
        if (string.Equals(level, "error", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(level, "warn", StringComparison.OrdinalIgnoreCase))
            Logger.E("OmniSpider", text);
        else
            Logger.D("OmniSpider", text);
    }

    public string Base64Encode(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? ""));

    public string Base64Decode(string text)
    {
        try { return Encoding.UTF8.GetString(FromBase64Lenient(text)); }
        catch { return ""; }
    }

    public string RandomBytes(int size)
    {
        try
        {
            var data = new byte[Math.Clamp(size, 0, 64 * 1024)];
            RandomNumberGenerator.Fill(data);
            return Convert.ToBase64String(data);
        }
        catch { return ""; }
    }

    public string Base64ToHex(string text)
    {
        try { return Convert.ToHexString(FromBase64Lenient(text)).ToLowerInvariant(); }
        catch { return ""; }
    }

    public string HexToBase64(string hex)
    {
        try
        {
            var clean = System.Text.RegularExpressions.Regex.Replace(hex ?? "", "[^0-9a-fA-F]", "");
            if (clean.Length == 0) return "";
            return Convert.ToBase64String(Convert.FromHexString(clean.Length % 2 == 0 ? clean : clean[..^1]));
        }
        catch { return ""; }
    }

    public string Hash(string algo, string text, string encoding)
    {
        try
        {
            var data = Encoding.UTF8.GetBytes(text ?? "");
            byte[] result = Normalized(algo) switch
            {
                "SHA1" => SHA1.HashData(data),
                "SHA256" => SHA256.HashData(data),
                "SHA512" => SHA512.HashData(data),
                _ => MD5.HashData(data),
            };
            return EncodeBytes(result, encoding);
        }
        catch (Exception e)
        {
            Logger.E(Tag + "[" + _spiderId + "].hash", e.Message);
            return "";
        }
    }

    public string Hmac(string algo, string key, string text, string encoding)
    {
        try
        {
            var keyBytes = Encoding.UTF8.GetBytes(key ?? "");
            var data = Encoding.UTF8.GetBytes(text ?? "");
            byte[] result = Normalized(algo) switch
            {
                "SHA1" => HMACSHA1.HashData(keyBytes, data),
                "SHA256" => HMACSHA256.HashData(keyBytes, data),
                "SHA512" => HMACSHA512.HashData(keyBytes, data),
                _ => HMACMD5.HashData(keyBytes, data),
            };
            return EncodeBytes(result, encoding);
        }
        catch (Exception e)
        {
            Logger.E(Tag + "[" + _spiderId + "].hmac", e.Message);
            return "";
        }
    }

    public string Aes(int encrypt, string mode, string keyBase64, string ivBase64, string dataBase64)
    {
        try
        {
            using var aes = System.Security.Cryptography.Aes.Create();
            aes.Key = FromBase64Lenient(keyBase64);
            aes.Mode = string.Equals(mode, "ECB", StringComparison.OrdinalIgnoreCase) ? CipherMode.ECB : CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.IV = aes.Mode == CipherMode.CBC
                ? (string.IsNullOrEmpty(ivBase64) ? new byte[16] : FromBase64Lenient(ivBase64))
                : new byte[16];
            var input = FromBase64Lenient(dataBase64);
            var output = encrypt == 1 ? aes.CreateEncryptor().TransformFinalBlock(input, 0, input.Length)
                                      : aes.CreateDecryptor().TransformFinalBlock(input, 0, input.Length);
            return Convert.ToBase64String(output);
        }
        catch (Exception e)
        {
            Logger.E(Tag + "[" + _spiderId + "].aes", e.Message);
            return "";
        }
    }

    public string DanmakuByFileName(string fileName)
    {
        try
        {
            if (!OmniConfig.IsLoggedIn || string.IsNullOrEmpty(fileName)) return "[]";
            var list = OmniApi.DanmakuAsync(fileName).GetAwaiter().GetResult();
            return JsonUtil.Serialize(list.Select(item => new { name = item.Name, url = item.Url }));
        }
        catch (Exception e)
        {
            Logger.E(Tag + "[" + _spiderId + "].danmaku", e.Message);
            return "[]";
        }
    }

    public string CacheGet(string key)
    {
        if (string.IsNullOrEmpty(key)) return "";
        if (!_cache.TryGetValue(key, out var entry)) return "";
        if (entry.Expires > 0 && entry.Expires <= DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            _cache.TryRemove(key, out _);
            return "";
        }
        return entry.Value;
    }

    public void CacheSet(string key, string value, int exSeconds)
    {
        if (string.IsNullOrEmpty(key)) return;
        var expires = exSeconds > 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + exSeconds * 1000L : 0;
        _cache[key] = (value ?? "null", expires);
    }

    public int CacheDelete(string key)
    {
        if (string.IsNullOrEmpty(key)) return 0;
        return _cache.TryRemove(key, out _) ? 1 : 0;
    }

    static string Normalized(string algo) =>
        (algo ?? "").Replace("-", "").Trim().ToUpperInvariant();

    static string EncodeBytes(byte[] data, string encoding) =>
        string.Equals(encoding, "base64", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToBase64String(data)
            : Convert.ToHexString(data).ToLowerInvariant();

    static byte[] FromBase64Lenient(string text)
    {
        try
        {
            var clean = System.Text.RegularExpressions.Regex.Replace(text ?? "", @"\s", "")
                .Replace('_', '/').Replace('-', '+');
            if (clean.Length % 4 != 0) clean = clean.PadRight(clean.Length + (4 - clean.Length % 4), '=');
            return Convert.FromBase64String(clean);
        }
        catch { return Array.Empty<byte>(); }
    }

    static string CharsetOf(OkResponse response)
    {
        if (response.Headers.TryGetValue("Content-Type", out var values))
        {
            var contentType = values.FirstOrDefault() ?? "";
            var index = contentType.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
            if (index >= 0) return contentType[(index + 8)..].Trim(' ', '"', ';');
        }
        return "UTF-8";
    }
}
