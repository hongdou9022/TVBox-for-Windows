// OmniBox SDK shim for the app-side QuickJS runner.
// Only the APIs used by app-side spider scripts are exposed.

globalThis.process = { env: {} };

globalThis.Buffer = {
    from: function (value, encoding) {
        if (Array.isArray(value)) {
            var bytes = "";
            for (var i = 0; i < value.length; i++) {
                bytes += String.fromCharCode(value[i] & 0xff);
            }
            return omniBuffer(bytes);
        }
        var text = (value === null || value === undefined) ? "" : String(value);
        var source = encoding ? String(encoding).toLowerCase() : "utf8";
        if (source === "base64") text = OmniNative.base64Decode(text);
        else if (source === "hex") text = omniUtf8FromHex(text);
        return omniBuffer(text);
    },
    isBuffer: function (value) {
        return !!(value && value.__isBuffer);
    },
    concat: function (list) {
        var text = "";
        for (var i = 0; i < list.length; i++) text += String(list[i]);
        return omniBuffer(text);
    },
    byteLength: function (value) {
        return omniUtf8Length(value);
    }
};

globalThis.URLSearchParams = function (init) {
    var pairs = [];
    if (init) {
        if (typeof init === "string") {
            var parts = init.replace(/^\?/, "").split("&");
            for (var i = 0; i < parts.length; i++) {
                if (!parts[i]) continue;
                var kv = parts[i].split("=");
                pairs.push([decodeURIComponent(kv[0] || ""), decodeURIComponent(kv[1] || "")]);
            }
        } else if (typeof init === "object") {
            for (var key in init) {
                if (Object.prototype.hasOwnProperty.call(init, key)) {
                    pairs.push([String(key), String(init[key])]);
                }
            }
        }
    }
    this.append = function (key, value) {
        pairs.push([String(key), String(value)]);
    };
    this.set = function (key, value) {
        for (var i = 0; i < pairs.length; i++) {
            if (pairs[i][0] === String(key)) {
                pairs[i][1] = String(value);
                return;
            }
        }
        pairs.push([String(key), String(value)]);
    };
    this.get = function (key) {
        for (var i = 0; i < pairs.length; i++) {
            if (pairs[i][0] === String(key)) return pairs[i][1];
        }
        return null;
    };
    this.getAll = function (key) {
        var values = [];
        for (var i = 0; i < pairs.length; i++) {
            if (pairs[i][0] === String(key)) values.push(pairs[i][1]);
        }
        return values;
    };
    this.has = function (key) {
        for (var i = 0; i < pairs.length; i++) {
            if (pairs[i][0] === String(key)) return true;
        }
        return false;
    };
    this.forEach = function (fn, thisArg) {
        for (var i = 0; i < pairs.length; i++) {
            fn.call(thisArg, pairs[i][1], pairs[i][0], this);
        }
    };
    this.entries = function () {
        return pairs.slice();
    };
    this.delete = function (key) {
        for (var i = pairs.length - 1; i >= 0; i--) {
            if (pairs[i][0] === String(key)) pairs.splice(i, 1);
        }
    };
    this.toString = function () {
        var result = [];
        for (var i = 0; i < pairs.length; i++) {
            result.push(encodeURIComponent(pairs[i][0]) + "=" + encodeURIComponent(pairs[i][1]));
        }
        return result.join("&");
    };
};

var OmniBox = {
    request: function (url, options) {
        options = options || {};
        var headers = options.headers || {};
        var body = options.body;
        if (body !== undefined && body !== null && typeof body !== "string") {
            body = JSON.stringify(body);
        }
        var raw = OmniNative.request(
            String(url),
            String(options.method || "GET"),
            JSON.stringify(headers),
            body === undefined || body === null ? "" : String(body)
        );
        try {
            return JSON.parse(raw || "{}");
        } catch (e) {
            return { statusCode: 0, headers: {}, body: "" };
        }
    },
    log: function (level, message) {
        try {
            OmniNative.log(String(level || "info"), String(message === null || message === undefined ? "" : message));
        } catch (e) {
        }
    },
    sniffVideo: function () {
        return Promise.resolve(null);
    },
    getScrapeMetadata: function () {
        return Promise.resolve(null);
    },
    processScraping: function () {
        return Promise.resolve(null);
    },
    addPlayHistory: function () {
        return Promise.resolve(false);
    },
    getDanmakuByFileName: function (fileName) {
        try {
            var raw = OmniNative.danmakuByFileName(String(fileName || ""));
            var list = JSON.parse(raw || "[]");
            return Promise.resolve(Array.isArray(list) ? list : []);
        } catch (e) {
            return Promise.resolve([]);
        }
    },
    getCache: function (key) {
        try {
            var raw = OmniNative.cacheGet(String(key));
            return Promise.resolve(raw ? JSON.parse(raw) : null);
        } catch (e) {
            return Promise.resolve(null);
        }
    },
    setCache: function (key, value, exSeconds) {
        try {
            OmniNative.cacheSet(
                String(key),
                JSON.stringify(value === undefined ? null : value),
                exSeconds ? Number(exSeconds) : 0
            );
        } catch (e) {
        }
        return Promise.resolve("OK");
    },
    deleteCache: function (key) {
        try {
            return Promise.resolve(OmniNative.cacheDelete(String(key)));
        } catch (e) {
            return Promise.resolve(0);
        }
    },
    setRequestContext: function (context) {
        globalThis.__omni_context = context || {};
    },
    getRequestContext: function () {
        return globalThis.__omni_context || null;
    }
};

globalThis.OmniBox = OmniBox;

// ---------------------------------------------------------------- Node 模块兼容层
// 只覆盖 OmniBox 脚本常用的子集，网络请求统一走 OkHttp 原生桥。

function omniOwn(obj, key) {
    return Object.prototype.hasOwnProperty.call(obj, key);
}

function omniMerge() {
    var target = {};
    for (var i = 0; i < arguments.length; i++) {
        var src = arguments[i];
        if (!src) continue;
        for (var key in src) {
            if (omniOwn(src, key)) target[key] = src[key];
        }
    }
    return target;
}

function omniFindHeader(headers, name) {
    for (var key in headers) {
        if (omniOwn(headers, key) && key.toLowerCase() === String(name).toLowerCase()) {
            return key;
        }
    }
    return "";
}

function omniMergeHeaders(base, extra) {
    var out = {};
    function add(src) {
        if (!src) return;
        for (var key in src) {
            if (!omniOwn(src, key)) continue;
            var old = omniFindHeader(out, key);
            if (old) delete out[old];
            out[key] = src[key];
        }
    }
    add(base);
    add(extra);
    return out;
}

function omniQueryString(params) {
    if (params === null || params === undefined || params === "") return "";
    if (typeof params === "string") return params.replace(/^\?/, "");
    if (typeof params.toString === "function" && typeof params.append === "function") {
        return params.toString();
    }
    var parts = [];
    function add(key, value) {
        if (value === null || value === undefined) return;
        if (Array.isArray(value)) {
            for (var i = 0; i < value.length; i++) add(key, value[i]);
            return;
        }
        parts.push(encodeURIComponent(String(key)) + "=" + encodeURIComponent(String(value)));
    }
    for (var key in params) {
        if (omniOwn(params, key)) add(key, params[key]);
    }
    return parts.join("&");
}

function omniAppendQuery(url, params) {
    var query = omniQueryString(params);
    if (!query) return url;
    return url + (url.indexOf("?") >= 0 ? "&" : "?") + query;
}

function omniJoinUrl(url, baseURL) {
    var target = String(url || "");
    if (/^[a-z][a-z0-9+.-]*:/i.test(target) || !baseURL) return target;
    var base = String(baseURL || "");
    if (target.indexOf("//") === 0) {
        var scheme = /^([a-z][a-z0-9+.-]*:)/i.exec(base);
        return (scheme ? scheme[1] : "https:") + target;
    }
    if (target.charAt(0) === "/") return base.replace(/\/+$/, "") + target;
    return base.replace(/\/+$/, "") + "/" + target;
}

function omniBuffer(text) {
    var value = String(text === null || text === undefined ? "" : text);
    return {
        __isBuffer: true,
        length: omniUtf8Length(value),
        toString: function (encoding) {
            if (encoding === "base64") return OmniNative.base64Encode(value);
            if (encoding === "hex") return omniHexFromUtf8(value);
            return value;
        }
    };
}

function omniUtf8Bytes(text) {
    var value = String(text === null || text === undefined ? "" : text);
    var bytes = [];
    for (var i = 0; i < value.length; i++) {
        var code = value.charCodeAt(i);
        if (code < 0x80) {
            bytes.push(code);
        } else if (code < 0x800) {
            bytes.push(0xc0 | (code >> 6), 0x80 | (code & 0x3f));
        } else if (code >= 0xd800 && code <= 0xdbff && i + 1 < value.length) {
            var next = value.charCodeAt(++i);
            var pair = 0x10000 + ((code - 0xd800) << 10) + (next - 0xdc00);
            bytes.push(0xf0 | (pair >> 18), 0x80 | ((pair >> 12) & 0x3f),
                    0x80 | ((pair >> 6) & 0x3f), 0x80 | (pair & 0x3f));
        } else {
            bytes.push(0xe0 | (code >> 12), 0x80 | ((code >> 6) & 0x3f),
                    0x80 | (code & 0x3f));
        }
    }
    return bytes;
}

function omniUtf8Length(text) {
    return omniUtf8Bytes(text).length;
}

function omniHexFromUtf8(text) {
    var bytes = omniUtf8Bytes(text);
    var out = "";
    for (var i = 0; i < bytes.length; i++) {
        var code = bytes[i] & 0xff;
        out += (code < 16 ? "0" : "") + code.toString(16);
    }
    return out;
}

function omniUtf8FromHex(hex) {
    var text = String(hex || "").replace(/[^0-9a-f]/gi, "");
    var bytes = [];
    for (var i = 0; i + 1 < text.length; i += 2) {
        bytes.push(parseInt(text.substr(i, 2), 16));
    }
    var out = "";
    for (var j = 0; j < bytes.length; j++) {
        var first = bytes[j];
        if (first < 0x80) {
            out += String.fromCharCode(first);
        } else if ((first & 0xe0) === 0xc0 && j + 1 < bytes.length) {
            out += String.fromCharCode(((first & 0x1f) << 6) | (bytes[++j] & 0x3f));
        } else if ((first & 0xf0) === 0xe0 && j + 2 < bytes.length) {
            out += String.fromCharCode(((first & 0x0f) << 12)
                    | ((bytes[++j] & 0x3f) << 6) | (bytes[++j] & 0x3f));
        } else if ((first & 0xf8) === 0xf0 && j + 3 < bytes.length) {
            var code = ((first & 0x07) << 18) | ((bytes[++j] & 0x3f) << 12)
                    | ((bytes[++j] & 0x3f) << 6) | (bytes[++j] & 0x3f);
            code -= 0x10000;
            out += String.fromCharCode(0xd800 + (code >> 10), 0xdc00 + (code & 0x3ff));
        } else {
            out += String.fromCharCode(first);
        }
    }
    return out;
}

function OmniEventEmitter() {
    this._events = {};
}

OmniEventEmitter.prototype.on = function (name, fn) {
    if (!this._events[name]) this._events[name] = [];
    this._events[name].push(fn);
    return this;
};

OmniEventEmitter.prototype.addListener = OmniEventEmitter.prototype.on;

OmniEventEmitter.prototype.once = function (name, fn) {
    var self = this;
    function wrapped() {
        self.removeListener(name, wrapped);
        fn.apply(self, arguments);
    }
    return this.on(name, wrapped);
};

OmniEventEmitter.prototype.removeListener = function (name, fn) {
    var list = this._events[name];
    if (!list) return this;
    for (var i = list.length - 1; i >= 0; i--) {
        if (list[i] === fn) list.splice(i, 1);
    }
    return this;
};

OmniEventEmitter.prototype.removeAllListeners = function (name) {
    if (name) delete this._events[name];
    else this._events = {};
    return this;
};

OmniEventEmitter.prototype.emit = function (name) {
    var list = this._events[name];
    if (!list || list.length === 0) return false;
    var args = Array.prototype.slice.call(arguments, 1);
    var copy = list.slice();
    for (var i = 0; i < copy.length; i++) copy[i].apply(this, args);
    return true;
};

// querystring
var omniQuerystring = {
    stringify: function (obj) {
        return omniQueryString(obj);
    },
    parse: function (text) {
        var result = {};
        var raw = String(text || "").replace(/^\?/, "");
        if (!raw) return result;
        var parts = raw.split("&");
        for (var i = 0; i < parts.length; i++) {
            if (!parts[i]) continue;
            var idx = parts[i].indexOf("=");
            var key = idx >= 0 ? parts[i].slice(0, idx) : parts[i];
            var value = idx >= 0 ? parts[i].slice(idx + 1) : "";
            try { key = decodeURIComponent(key.replace(/\+/g, " ")); } catch (e) {}
            try { value = decodeURIComponent(value.replace(/\+/g, " ")); } catch (e) {}
            if (!omniOwn(result, key)) {
                result[key] = value;
            } else if (Array.isArray(result[key])) {
                result[key].push(value);
            } else {
                result[key] = [result[key], value];
            }
        }
        return result;
    },
    escape: function (text) {
        return encodeURIComponent(String(text));
    },
    unescape: function (text) {
        try { return decodeURIComponent(String(text)); } catch (e) { return String(text); }
    }
};

// url
function omniParseUrl(input, base) {
    var href = String(input === null || input === undefined ? "" : input);
    var baseParts = null;
    if (base && !/^[a-z][a-z0-9+.-]*:/i.test(href)) {
        baseParts = omniParseUrl(base);
        if (href.indexOf("//") === 0) {
            href = (baseParts.protocol || "https:") + href;
        } else if (href.charAt(0) === "/") {
            href = (baseParts.protocol || "https:") + "//" + baseParts.host + href;
        } else {
            var dir = baseParts.pathname.replace(/[^/]*$/, "");
            href = (baseParts.protocol || "https:") + "//" + baseParts.host + dir + href;
        }
    }
    var match = /^([a-z][a-z0-9+.-]*:)?(?:\/\/([^\/?#]*))?([^?#]*)(\?[^#]*)?(#.*)?$/i.exec(href) || [];
    var protocol = match[1] || "";
    var authority = match[2] || "";
    var pathname = match[3] || "";
    var search = match[4] || "";
    var hash = match[5] || "";
    var auth = "";
    var at = authority.lastIndexOf("@");
    if (at >= 0) {
        auth = authority.slice(0, at);
        authority = authority.slice(at + 1);
    }
    var hostname = authority;
    var port = "";
    if (authority.charAt(0) === "[") {
        var close = authority.indexOf("]");
        if (close >= 0) {
            hostname = authority.slice(0, close + 1);
            if (authority.charAt(close + 1) === ":") port = authority.slice(close + 2);
        }
    } else {
        var colon = authority.lastIndexOf(":");
        if (colon > 0) {
            hostname = authority.slice(0, colon);
            port = authority.slice(colon + 1);
        }
    }
    var query = search ? search.slice(1) : "";
    var path = pathname + search;
    return {
        href: href,
        protocol: protocol,
        slashes: authority ? true : false,
        auth: auth,
        host: authority,
        port: port,
        hostname: hostname,
        hash: hash,
        search: search,
        query: query,
        pathname: pathname,
        path: path,
        queryObject: omniQuerystring.parse(query)
    };
}

var omniUrl = {
    parse: function (input, parseQueryString) {
        var parsed = omniParseUrl(input);
        parsed.query = parseQueryString ? omniQuerystring.parse(parsed.query) : parsed.query;
        delete parsed.queryObject;
        return parsed;
    },
    format: function (obj) {
        if (typeof obj === "string") return obj;
        var protocol = obj.protocol || "";
        if (protocol && protocol.slice(-1) !== ":") protocol += ":";
        var auth = obj.auth ? obj.auth + "@" : "";
        var host = obj.host || (obj.hostname ? obj.hostname + (obj.port ? ":" + obj.port : "") : "");
        var pathname = obj.pathname || "";
        var query = obj.query;
        if (query && typeof query === "object") query = omniQuerystring.stringify(query);
        var search = obj.search || (query ? "?" + String(query).replace(/^\?/, "") : "");
        var hash = obj.hash || "";
        if (hash && hash.charAt(0) !== "#") hash = "#" + hash;
        return protocol + (host ? "//" + auth + host : "") + pathname + search + hash;
    },
    resolve: function (from, to) {
        return omniParseUrl(to, from).href;
    },
    URLSearchParams: globalThis.URLSearchParams
};

if (typeof globalThis.URL === "undefined") {
    globalThis.URL = function (input, base) {
        var parsed = omniParseUrl(input, base);
        this.href = parsed.href;
        this.protocol = parsed.protocol;
        this.host = parsed.host;
        this.hostname = parsed.hostname;
        this.port = parsed.port;
        this.pathname = parsed.pathname;
        this.search = parsed.search;
        this.hash = parsed.hash;
        this.origin = parsed.protocol + "//" + parsed.host;
        this.searchParams = new globalThis.URLSearchParams(parsed.query);
        this.toString = function () { return this.href; };
    };
}

omniUrl.URL = globalThis.URL;

// util
var omniUtil = {
    format: function (template) {
        var args = Array.prototype.slice.call(arguments, 1);
        var index = 0;
        return String(template).replace(/%[sdifjo%]/g, function (token) {
            if (token === "%%") return "%";
            if (index >= args.length) return token;
            var value = args[index++];
            if (token === "%d" || token === "%i") return String(parseInt(value, 10) || 0);
            if (token === "%f") return String(parseFloat(value) || 0);
            if (token === "%j") {
                try { return JSON.stringify(value); } catch (e) { return "[Circular]"; }
            }
            if (token === "%o") {
                try { return JSON.stringify(value); } catch (e) { return String(value); }
            }
            return String(value);
        });
    },
    inspect: function (value) {
        try { return JSON.stringify(value, null, 2); } catch (e) { return String(value); }
    },
    promisify: function (fn) {
        return function () {
            var args = Array.prototype.slice.call(arguments);
            var self = this;
            return new Promise(function (resolve, reject) {
                args.push(function (err, value) {
                    if (err) reject(err);
                    else resolve(value);
                });
                fn.apply(self, args);
            });
        };
    },
    inherits: function (ctor, superCtor) {
        ctor.prototype = Object.create(superCtor.prototype);
        ctor.prototype.constructor = ctor;
    },
    isDeepStrictEqual: function (a, b) {
        try { return JSON.stringify(a) === JSON.stringify(b); } catch (e) { return a === b; }
    }
};

// http / https
function OmniAgent(options) {
    this.options = options || {};
}

function omniHttpUrl(options) {
    if (typeof options === "string") return options;
    if (options.href) return options.href;
    var protocol = options.protocol || "http:";
    if (protocol.slice(-1) !== ":") protocol += ":";
    var host = options.hostname || options.host || "localhost";
    var port = options.port ? ":" + options.port : "";
    var path = options.path || options.pathname || "/";
    return protocol + "//" + host + port + (path.charAt(0) === "/" ? path : "/" + path);
}

function omniHttpRequest(options, callback, defaultProtocol) {
    if (typeof options === "string") options = { href: options };
    options = options || {};
    if (!options.protocol && defaultProtocol) {
        var withProtocol = omniMerge(options);
        withProtocol.protocol = defaultProtocol;
        options = withProtocol;
    }
    var req = new OmniEventEmitter();
    var headers = omniMerge({}, options.headers || {});
    var method = String(options.method || "GET").toUpperCase();
    var body = "";
    if (callback) req.on("response", callback);
    req.setHeader = function (name, value) {
        var old = omniFindHeader(headers, name);
        if (old) delete headers[old];
        headers[name] = value;
    };
    req.getHeader = function (name) {
        var key = omniFindHeader(headers, name);
        return key ? headers[key] : undefined;
    };
    req.removeHeader = function (name) {
        var key = omniFindHeader(headers, name);
        if (key) delete headers[key];
    };
    req.write = function (chunk) {
        if (chunk !== null && chunk !== undefined) body += String(chunk);
    };
    req.abort = function () {};
    req.destroy = function (error) {
        if (error) req.emit("error", error);
    };
    req.end = function (chunk) {
        if (chunk !== null && chunk !== undefined) body += String(chunk);
        var raw;
        try {
            raw = JSON.parse(OmniNative.request(
                omniHttpUrl(options), method, JSON.stringify(headers), body
            ) || "{}");
        } catch (e) {
            raw = { statusCode: 0, headers: {}, body: "" };
        }
        if (!raw.statusCode) {
            var error = new Error("request failed");
            if (req.emit("error", error)) return;
            try { OmniBox.log("error", "http request failed: " + omniHttpUrl(options)); } catch (e) {}
            return;
        }
        var res = new OmniEventEmitter();
        res.statusCode = raw.statusCode;
        res.headers = raw.headers || {};
        res.rawHeaders = [];
        res.setEncoding = function () {};
        req.emit("response", res);
        res.emit("data", raw.body || "");
        res.emit("end");
    };
    return req;
}

function omniHttpGet(options, callback, defaultProtocol) {
    if (typeof options === "function") {
        callback = options;
        options = {};
    }
    var req = omniHttpRequest(options, callback, defaultProtocol);
    req.end();
    return req;
}

var omniHttp = {
    Agent: OmniAgent,
    globalAgent: new OmniAgent({ keepAlive: true }),
    request: function (options, callback) {
        return omniHttpRequest(options, callback, "http:");
    },
    get: function (options, callback) {
        return omniHttpGet(options, callback, "http:");
    },
    STATUS_CODES: { 200: "OK", 301: "Moved Permanently", 302: "Found", 304: "Not Modified", 404: "Not Found", 500: "Internal Server Error" }
};

var omniHttps = {
    Agent: OmniAgent,
    globalAgent: new OmniAgent({ keepAlive: true }),
    request: function (options, callback) {
        return omniHttpRequest(options, callback, "https:");
    },
    get: function (options, callback) {
        return omniHttpGet(options, callback, "https:");
    }
};

// axios
function omniAxiosError(message, config, response) {
    var error = new Error(message);
    error.config = config;
    error.response = response;
    error.isAxiosError = true;
    return error;
}

function omniAxiosParseBody(raw, config) {
    var text = raw.body === null || raw.body === undefined ? "" : String(raw.body);
    var type = config.responseType || "";
    if (type === "text") return text;
    if (type === "json") {
        try { return JSON.parse(text); } catch (e) { return null; }
    }
    var headers = raw.headers || {};
    var contentType = "";
    for (var key in headers) {
        if (omniOwn(headers, key) && key.toLowerCase() === "content-type") {
            contentType = String(headers[key] || "").toLowerCase();
            break;
        }
    }
    var trimmed = text.replace(/^\s+/, "");
    if (contentType.indexOf("json") >= 0 || trimmed.charAt(0) === "{" || trimmed.charAt(0) === "[") {
        try { return JSON.parse(text); } catch (e) {}
    }
    return text;
}

function omniAxiosRequest(config) {
    config = omniMerge(config || {});
    config.method = String(config.method || "GET").toUpperCase();
    config.headers = omniMergeHeaders(config.defaultsHeaders || {}, config.headers || {});
    delete config.defaultsHeaders;

    var url = omniJoinUrl(config.url || "", config.baseURL || "");
    url = omniAppendQuery(url, config.params);

    var data = config.data;
    if (data !== null && data !== undefined && typeof data !== "string") {
        if (typeof data.append === "function" && typeof data.toString === "function") {
            data = data.toString();
        } else {
            var contentType = "";
            var headerKey = omniFindHeader(config.headers, "content-type");
            if (headerKey) contentType = String(config.headers[headerKey] || "").toLowerCase();
            if (contentType.indexOf("application/x-www-form-urlencoded") >= 0) {
                data = omniQuerystring.stringify(data);
            } else {
                data = JSON.stringify(data);
                if (!headerKey) config.headers["Content-Type"] = "application/json";
            }
        }
    }
    if (config.auth && config.auth.username !== undefined) {
        config.headers["Authorization"] = "Basic " + OmniNative.base64Encode(
            String(config.auth.username) + ":" + String(config.auth.password || "")
        );
    }

    var raw;
    try {
        raw = JSON.parse(OmniNative.request(
            url, config.method, JSON.stringify(config.headers), data === null || data === undefined ? "" : String(data)
        ) || "{}");
    } catch (e) {
        raw = { statusCode: 0, headers: {}, body: "" };
    }

    var responseHeaders = {};
    for (var key in (raw.headers || {})) {
        if (!omniOwn(raw.headers, key)) continue;
        responseHeaders[key] = raw.headers[key];
        responseHeaders[String(key).toLowerCase()] = raw.headers[key];
    }
    var response = {
        data: omniAxiosParseBody(raw, config),
        status: raw.statusCode || 0,
        statusText: "",
        headers: responseHeaders,
        config: config,
        request: { responseURL: url }
    };
    var validate = typeof config.validateStatus === "function"
        ? config.validateStatus
        : function (status) { return status >= 200 && status < 300; };
    if (!response.status || !validate(response.status)) {
        throw omniAxiosError(
            "Request failed with status " + response.status + " (" + config.method + " " + url + ")",
            config, response
        );
    }
    return response;
}

// QuickJS 运行时在 Async.get 阻塞期间不会继续驱动微任务；axios 直接返回同步 thenable，
// 让 await/.then 在原生请求完成后立即拿到结果。
function OmniSyncThenable(failed, payload) {
    this.failed = !!failed;
    if (this.failed) this.error = payload;
    else this.value = payload;
}

function omniSyncResolve(value) {
    return new OmniSyncThenable(false, value);
}

function omniSyncReject(error) {
    return new OmniSyncThenable(true, error);
}

function omniSyncFrom(value) {
    return value instanceof OmniSyncThenable ? value : omniSyncResolve(value);
}

OmniSyncThenable.prototype.then = function (onFulfilled, onRejected) {
    if (this.failed) {
        if (!onRejected) return this;
        try {
            return omniSyncFrom(onRejected(this.error));
        } catch (e) {
            return omniSyncReject(e);
        }
    }
    if (!onFulfilled) return this;
    try {
        return omniSyncFrom(onFulfilled(this.value));
    } catch (e) {
        return omniSyncReject(e);
    }
};

OmniSyncThenable.prototype.catch = function (onRejected) {
    return this.then(null, onRejected);
};

OmniSyncThenable.prototype.finally = function (fn) {
    try { fn(); } catch (e) {}
    return this;
};

function omniAxiosCreate(defaults) {
    var base = omniMerge(defaults || {});
    var instance = function (configOrUrl, maybeConfig) {
        var config;
        if (typeof configOrUrl === "string") {
            config = omniMerge(maybeConfig || {}, { url: configOrUrl });
        } else {
            config = omniMerge(configOrUrl || {});
        }
        var merged = omniMerge(base, config);
        merged.headers = omniMergeHeaders(base.headers || {}, config.headers || {});
        merged.baseURL = config.baseURL || base.baseURL;
        if (base.validateStatus && !config.validateStatus) merged.validateStatus = base.validateStatus;
        try {
            return omniSyncResolve(omniAxiosRequest(merged));
        } catch (e) {
            return omniSyncReject(e);
        }
    };
    function method(name) {
        return function (url, dataOrConfig, maybeConfig) {
            var config;
            if (name === "get" || name === "delete" || name === "head" || name === "options") {
                config = omniMerge(dataOrConfig || {});
            } else {
                config = omniMerge(maybeConfig || {});
                config.data = dataOrConfig;
            }
            config.url = url;
            config.method = name;
            return instance(config);
        };
    }
    instance.get = method("get");
    instance.delete = method("delete");
    instance.head = method("head");
    instance.options = method("options");
    instance.post = method("post");
    instance.put = method("put");
    instance.patch = method("patch");
    instance.request = function (config) { return instance(config); };
    instance.defaults = base;
    instance.interceptors = {
        request: { use: function () { return 0; }, eject: function () {} },
        response: { use: function () { return 0; }, eject: function () {} }
    };
    return instance;
}

var omniAxios = omniAxiosCreate({});
omniAxios.create = function (defaults) { return omniAxiosCreate(defaults); };
omniAxios.request = function (config) { return omniAxios(config); };
omniAxios.get = function (url, config) { return omniAxios(url, omniMerge(config || {}, { method: "GET" })); };
omniAxios.delete = function (url, config) { return omniAxios(url, omniMerge(config || {}, { method: "DELETE" })); };
omniAxios.head = function (url, config) { return omniAxios(url, omniMerge(config || {}, { method: "HEAD" })); };
omniAxios.options = function (url, config) { return omniAxios(url, omniMerge(config || {}, { method: "OPTIONS" })); };
omniAxios.post = function (url, data, config) { return omniAxios(url, omniMerge(config || {}, { method: "POST", data: data })); };
omniAxios.put = function (url, data, config) { return omniAxios(url, omniMerge(config || {}, { method: "PUT", data: data })); };
omniAxios.patch = function (url, data, config) { return omniAxios(url, omniMerge(config || {}, { method: "PATCH", data: data })); };
omniAxios.isAxiosError = function (error) { return !!(error && error.isAxiosError); };
omniAxios.all = function (list) {
    var values = [];
    for (var i = 0; i < list.length; i++) {
        var item = list[i];
        if (item instanceof OmniSyncThenable) {
            if (item.failed) return omniSyncReject(item.error);
            values.push(item.value);
        } else {
            values.push(item);
        }
    }
    return omniSyncResolve(values);
};
omniAxios.spread = function (fn) {
    return function (list) { return fn.apply(null, list); };
};

// crypto-js（常用子集：MD5/SHA1/SHA256/HMAC、Base64/Hex/Utf8、AES-CBC/ECB）
function omniWord(kind, value) {
    return {
        __cryptoWord: true,
        kind: kind,
        value: String(value === null || value === undefined ? "" : value),
        toString: function (encoder) {
            if (encoder && encoder.kind) return omniWordToString(this, encoder.kind);
            return omniWordToString(this, "hex");
        }
    };
}

function omniWordToString(word, kind) {
    if (!word || !word.__cryptoWord) return String(word === null || word === undefined ? "" : word);
    var target = kind || "utf8";
    if (word.kind === target) return word.value;
    if (target === "utf8") {
        if (word.kind === "base64") return OmniNative.base64Decode(word.value);
        if (word.kind === "hex") return omniUtf8FromHex(word.value);
        return word.value;
    }
    if (target === "base64") {
        if (word.kind === "utf8") return OmniNative.base64Encode(word.value);
        if (word.kind === "hex") return OmniNative.base64Encode(omniUtf8FromHex(word.value));
        return word.value;
    }
    if (target === "hex") {
        if (word.kind === "utf8") return omniHexFromUtf8(word.value);
        if (word.kind === "base64") return omniHexFromUtf8(OmniNative.base64Decode(word.value));
        return word.value;
    }
    return word.value;
}

var omniEncUtf8 = {
    kind: "utf8",
    parse: function (text) { return omniWord("utf8", text); },
    stringify: function (word) { return omniWordToString(word, "utf8"); }
};
var omniEncBase64 = {
    kind: "base64",
    parse: function (text) { return omniWord("base64", text); },
    stringify: function (word) { return omniWordToString(word, "base64"); }
};
var omniEncHex = {
    kind: "hex",
    parse: function (text) { return omniWord("hex", text); },
    stringify: function (word) { return omniWordToString(word, "hex"); }
};

function omniCryptoHash(algo) {
    return function (text) {
        var input = text && text.__cryptoWord ? omniWordToString(text, "utf8") : String(text === null || text === undefined ? "" : text);
        return omniWord("hex", OmniNative.hash(algo, input, "hex"));
    };
}

function omniCryptoHmac(algo) {
    return function (text, key) {
        var input = text && text.__cryptoWord ? omniWordToString(text, "utf8") : String(text === null || text === undefined ? "" : text);
        var secret = key && key.__cryptoWord ? omniWordToString(key, "utf8") : String(key === null || key === undefined ? "" : key);
        return omniWord("hex", OmniNative.hmac(algo, secret, input, "hex"));
    };
}

function omniCryptoAes(encrypt, data, key, config) {
    var cfg = config || {};
    var mode = cfg.mode && cfg.mode.name ? cfg.mode.name : "CBC";
    var keyWord = key && key.__cryptoWord ? key : omniWord("utf8", key);
    var dataWord;
    if (encrypt) {
        dataWord = data && data.__cryptoWord ? data : omniWord("utf8", data);
    } else if (data && data.ciphertext) {
        dataWord = data.ciphertext;
    } else if (data && data.__cryptoWord) {
        dataWord = data;
    } else {
        dataWord = omniWord("base64", data);
    }
    var iv = cfg.iv ? omniWordToString(cfg.iv, "base64") : "";
    var result = OmniNative.aes(
        encrypt ? 1 : 0,
        mode,
        omniWordToString(keyWord, "base64"),
        iv,
        omniWordToString(dataWord, "base64")
    );
    if (!result) throw new Error("AES " + (encrypt ? "encrypt" : "decrypt") + " failed");
    if (!encrypt) return omniWord("base64", result);
    var ciphertext = omniWord("base64", result);
    return {
        ciphertext: ciphertext,
        toString: function (encoder) {
            return omniWordToString(ciphertext, encoder && encoder.kind ? encoder.kind : "base64");
        }
    };
}

var omniCryptoJs = {
    MD5: omniCryptoHash("md5"),
    SHA1: omniCryptoHash("sha1"),
    SHA256: omniCryptoHash("sha256"),
    SHA512: omniCryptoHash("sha512"),
    HmacMD5: omniCryptoHmac("md5"),
    HmacSHA1: omniCryptoHmac("sha1"),
    HmacSHA256: omniCryptoHmac("sha256"),
    enc: {
        Utf8: omniEncUtf8,
        Base64: omniEncBase64,
        Hex: omniEncHex
    },
    mode: {
        CBC: { name: "CBC" },
        ECB: { name: "ECB" }
    },
    pad: {
        Pkcs7: { name: "Pkcs7" }
    },
    AES: {
        encrypt: function (data, key, config) { return omniCryptoAes(true, data, key, config); },
        decrypt: function (data, key, config) { return omniCryptoAes(false, data, key, config); }
    },
    lib: {
        WordArray: {
            create: function (words, sigBytes) {
                if (Array.isArray(words)) {
                    var text = "";
                    for (var i = 0; i < words.length; i++) text += String.fromCharCode(words[i] & 0xff);
                    return omniWord("utf8", text);
                }
                return omniWord("utf8", words || "");
            }
        }
    }
};

globalThis.CryptoJS = omniCryptoJs;

// Node crypto（常用子集：hash/HMAC/AES/randomBytes）
function omniBinaryBuffer(base64) {
    var value = String(base64 === null || base64 === undefined ? "" : base64);
    var padding = value.charAt(value.length - 2) === "=" ? 2 : (value.charAt(value.length - 1) === "=" ? 1 : 0);
    return {
        __isBuffer: true,
        __base64: value,
        length: Math.max(0, Math.floor(value.length * 3 / 4) - padding),
        toString: function (encoding) {
            if (encoding === "hex") return OmniNative.base64ToHex(value);
            if (encoding === "base64") return value;
            return OmniNative.base64Decode(value);
        }
    };
}

function omniToUtf8(value, encoding) {
    if (value && value.__cryptoWord) return omniWordToString(value, "utf8");
    if (value && value.__isBuffer) {
        if (encoding === "base64" || encoding === "hex") return value.toString(encoding);
        return value.toString("utf8");
    }
    if (encoding === "base64") return OmniNative.base64Decode(String(value));
    if (encoding === "hex") return omniUtf8FromHex(String(value));
    return String(value === null || value === undefined ? "" : value);
}

function omniToBase64(value, encoding) {
    if (value && value.__cryptoWord) return omniWordToString(value, "base64");
    if (value && value.__isBuffer) {
        if (value.__base64 !== undefined && (!encoding || encoding === "base64")) return value.__base64;
        return value.toString("base64");
    }
    var text = String(value === null || value === undefined ? "" : value);
    if (encoding === "base64") return text;
    if (encoding === "hex") return OmniNative.hexToBase64(text);
    return OmniNative.base64Encode(text);
}

function omniCryptoDigestOutput(hex, encoding) {
    if (encoding === "hex") return hex;
    var base64 = OmniNative.hexToBase64(hex);
    if (encoding === "base64") return base64;
    if (!encoding || encoding === "buffer") return omniBinaryBuffer(base64);
    return hex;
}

function omniCryptoHashStream(algo) {
    var data = "";
    return {
        update: function (value, encoding) {
            data += omniToUtf8(value, encoding);
            return this;
        },
        digest: function (encoding) {
            return omniCryptoDigestOutput(OmniNative.hash(algo, data, "hex"), encoding);
        }
    };
}

function omniCryptoHmacStream(algo, key) {
    var data = "";
    var secret = omniToUtf8(key);
    return {
        update: function (value, encoding) {
            data += omniToUtf8(value, encoding);
            return this;
        },
        digest: function (encoding) {
            return omniCryptoDigestOutput(OmniNative.hmac(algo, secret, data, "hex"), encoding);
        }
    };
}

function omniCryptoCipherOutput(base64, encoding) {
    if (encoding === "base64") return base64;
    if (encoding === "hex") return OmniNative.base64ToHex(base64);
    if (encoding === "utf8" || encoding === "utf-8" || encoding === "ascii") {
        return OmniNative.base64Decode(base64);
    }
    return omniBinaryBuffer(base64);
}

function omniCryptoCipher(encrypt, algorithm, key, iv) {
    var mode = String(algorithm || "").toLowerCase().indexOf("ecb") >= 0 ? "ECB" : "CBC";
    var keyBase64 = omniToBase64(key);
    var ivBase64 = iv ? omniToBase64(iv) : "";
    var pendingText = "";
    var pendingCipher = "";
    return {
        setAutoPadding: function () { return this; },
        update: function (data, inputEncoding, outputEncoding) {
            if (encrypt) {
                pendingText += omniToUtf8(data, inputEncoding);
            } else if (inputEncoding === "base64") {
                pendingCipher += data && data.__isBuffer ? data.toString("base64") : String(data);
            } else if (inputEncoding === "hex") {
                pendingCipher += OmniNative.hexToBase64(String(data));
            } else if (data && data.__isBuffer) {
                pendingCipher += data.toString("base64");
            } else {
                pendingCipher += omniToBase64(data, inputEncoding);
            }
            return omniCryptoCipherOutput("", outputEncoding);
        },
        final: function (outputEncoding) {
            var payload = encrypt ? OmniNative.base64Encode(pendingText) : pendingCipher;
            var result = OmniNative.aes(encrypt ? 1 : 0, mode, keyBase64, ivBase64, payload);
            pendingText = "";
            pendingCipher = "";
            if (!result) throw new Error("crypto cipher failed");
            return omniCryptoCipherOutput(result, outputEncoding);
        }
    };
}

var omniCrypto = {
    createHash: function (algo) { return omniCryptoHashStream(algo); },
    createHmac: function (algo, key) { return omniCryptoHmacStream(algo, key); },
    createCipheriv: function (algo, key, iv) { return omniCryptoCipher(true, algo, key, iv); },
    createDecipheriv: function (algo, key, iv) { return omniCryptoCipher(false, algo, key, iv); },
    randomBytes: function (size) { return omniBinaryBuffer(OmniNative.randomBytes(size)); },
    randomUUID: function () {
        var hex = OmniNative.base64ToHex(OmniNative.randomBytes(16));
        return hex.slice(0, 8) + "-" + hex.slice(8, 12) + "-" + hex.slice(12, 16)
            + "-" + hex.slice(16, 20) + "-" + hex.slice(20, 32);
    }
};

globalThis.__omni_module = { exports: {} };
globalThis.__omni_modules = {
    omnibox_sdk: OmniBox,
    spider_runner: {
        run: function (handlers) {
            globalThis.__omni_handlers = handlers;
        }
    },
    axios: omniAxios,
    "http": omniHttp,
    "https": omniHttps,
    "crypto": omniCrypto,
    "crypto-js": omniCryptoJs,
    querystring: omniQuerystring,
    url: omniUrl,
    util: omniUtil
};
globalThis.require = function (name) {
    var mod = globalThis.__omni_modules[name];
    if (mod) return mod;
    throw new Error("module not found: " + name);
};
