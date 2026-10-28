using Jint;
using Jint.Native;
using Jint.Native.Object;
using TVBoxForWindows.Core;
using TVBoxForWindows.Engine.Js;
using JintEngine = Jint.Engine;

namespace TVBoxForWindows.Omni;

/// <summary>在 Jint 中运行 OmniBox SDK 爬虫脚本（移植自 omni/local/OmniJsRuntime.java）：
/// 注入 OmniNative → 执行 omnibox_sdk shim（可选 cheerio）→ CommonJS 包装脚本 → 调用 handlers。</summary>
public sealed class OmniSpiderScript : IDisposable
{
    const string Tag = "OmniSpiderScript";
    const int CallTimeoutMs = 60_000;

    readonly string _spiderId;
    readonly SemaphoreSlim _sem = new(1, 1);
    readonly JintEngine _engine;
    readonly JsGlobals _globals;
    readonly ObjectInstance _handlers;

    public OmniSpiderScript(string spiderId, string script)
    {
        _spiderId = spiderId ?? "";
        if (string.IsNullOrWhiteSpace(script)) throw new ArgumentException("spider script is empty");

        _engine = new JintEngine(options => options.TimeoutInterval(TimeSpan.FromSeconds(60)));
        _globals = new JsGlobals(_engine, _spiderId);

        var native = new OmniNative(_spiderId);
        _engine.SetValue("__omniRequest", new Func<string, string, string, string, string>(native.Request));
        _engine.SetValue("__omniLog", new Action<string, string>(native.Log));
        _engine.SetValue("__omniB64Encode", new Func<string, string>(native.Base64Encode));
        _engine.SetValue("__omniB64Decode", new Func<string, string>(native.Base64Decode));
        _engine.SetValue("__omniRandomBytes", new Func<int, string>(native.RandomBytes));
        _engine.SetValue("__omniB64ToHex", new Func<string, string>(native.Base64ToHex));
        _engine.SetValue("__omniHexToB64", new Func<string, string>(native.HexToBase64));
        _engine.SetValue("__omniHash", new Func<string, string, string, string>(native.Hash));
        _engine.SetValue("__omniHmac", new Func<string, string, string, string, string>(native.Hmac));
        _engine.SetValue("__omniAes", new Func<int, string, string, string, string, string>(native.Aes));
        _engine.SetValue("__omniDanmaku", new Func<string, string>(native.DanmakuByFileName));
        _engine.SetValue("__omniCacheGet", new Func<string, string>(native.CacheGet));
        _engine.SetValue("__omniCacheSet", new Action<string, string, int>(native.CacheSet));
        _engine.SetValue("__omniCacheDelete", new Func<string, int>(native.CacheDelete));
        _engine.Execute(@"
globalThis.OmniNative = {
  request: __omniRequest, log: __omniLog,
  base64Encode: __omniB64Encode, base64Decode: __omniB64Decode,
  randomBytes: __omniRandomBytes, base64ToHex: __omniB64ToHex, hexToBase64: __omniHexToB64,
  hash: __omniHash, hmac: __omniHmac, aes: __omniAes,
  danmakuByFileName: __omniDanmaku,
  cacheGet: __omniCacheGet, cacheSet: __omniCacheSet, cacheDelete: __omniCacheDelete
};
globalThis.__omni_settle = function (value, ok, err) {
  if (value && typeof value.then === 'function') { Promise.resolve(value).then(ok, err); return true; }
  return false;
};");

        var shim = AppPaths.ReadAsset("js/omni/omnibox_sdk_shim.js");
        if (string.IsNullOrWhiteSpace(shim)) throw new FileNotFoundException("内置 omnibox_sdk shim 缺失");
        _engine.Execute(shim);

        if (UsesCheerio(script))
        {
            try
            {
                var cheerio = AppPaths.ReadAsset("js/omni/cheerio_bundle.js");
                if (!string.IsNullOrWhiteSpace(cheerio)) _engine.Execute(cheerio);
                _engine.Execute("if (!globalThis.__omni_modules.cheerio) { globalThis.__omni_modules.cheerio = globalThis.Cheerio || globalThis.cheerio; }");
            }
            catch (Exception e)
            {
                Logger.E(Tag + "[" + _spiderId + "].cheerio", e.Message);
            }
        }

        var wrapped = "(function(module, exports, require, process, Buffer, URLSearchParams) {\n"
            + script
            + "\n})(globalThis.__omni_module, globalThis.__omni_module.exports, globalThis.require,"
            + " globalThis.process, globalThis.Buffer, globalThis.URLSearchParams);";
        _engine.Execute(wrapped);

        var handlers = _engine.Evaluate("globalThis.__omni_handlers");
        _handlers = handlers as ObjectInstance
            ?? throw new InvalidOperationException("script did not export handlers");
    }

    public bool IsReady => _handlers != null;

    /// <summary>调用 handlers 中的方法，返回 JSON 字符串；脚本没有该方法时返回 null。</summary>
    public async Task<string> CallAsync(string method, Dictionary<string, string> parameters)
    {
        await _sem.WaitAsync();
        try
        {
            return await Task.Run(() => Call(method, parameters));
        }
        finally
        {
            _sem.Release();
        }
    }

    string Call(string method, Dictionary<string, string> parameters)
    {
        var fn = _handlers.Get(method);
        if (fn == null || fn.IsUndefined() || fn.IsNull()) return null;
        var paramsValue = _engine.Evaluate("(" + JsonUtil.Serialize(parameters ?? new()) + ")");
        var contextValue = _engine.Evaluate("(" + JsonUtil.Serialize(new
        {
            from = "tvbox",
            sourceId = _spiderId,
            baseURL = OmniConfig.BaseUrl,
        }) + ")");
        var result = _engine.Invoke(fn, _handlers, new object[] { paramsValue, contextValue });
        var resolved = WaitForResult(result);
        if (resolved == null || resolved.IsUndefined() || resolved.IsNull()) return null;
        return resolved.IsString() ? resolved.AsString() : _globals.Stringify(resolved);
    }

    JsValue WaitForResult(JsValue value)
    {
        if (value == null) return null;
        JsValue resolved = null;
        string error = null;
        var done = false;
        var thenable = _engine.Invoke("__omni_settle", value,
            (Action<JsValue>)(item => { resolved = item; done = true; }),
            (Action<JsValue>)(item => { error = item?.ToString() ?? "unknown"; done = true; }));
        if (!JsGlobals.Truthy(thenable)) return value;

        _engine.Advanced.ProcessTasks();
        var deadline = Environment.TickCount64 + CallTimeoutMs;
        while (!done && Environment.TickCount64 < deadline)
        {
            var pumped = _globals.PumpNextTimer();
            _engine.Advanced.ProcessTasks();
            if (!done && !pumped && !_globals.HasTimer) break;
        }
        if (!done) throw new TimeoutException("脚本执行超时");
        if (error != null) throw new InvalidOperationException(error);
        return resolved;
    }

    static bool UsesCheerio(string script) =>
        script.Contains("require(\"cheerio\")", StringComparison.Ordinal) ||
        script.Contains("require('cheerio')", StringComparison.Ordinal);

    public void Dispose()
    {
        try { _globals?.Destroy(); } catch { }
        try { _engine?.Dispose(); } catch { }
        _sem.Dispose();
    }
}
