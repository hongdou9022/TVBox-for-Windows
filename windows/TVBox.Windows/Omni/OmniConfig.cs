using TVBoxForWindows.Core;

namespace TVBoxForWindows.Omni;

/// <summary>CloudTV 会话配置（移植自 omni/OmniConfig.java）：只保存服务器地址、账号与令牌，不保存密码。</summary>
public static class OmniConfig
{
    public const string SourceKey = "omni";
    public const string SourceName = "CloudTV";

    const string KBase = "omni_base";
    const string KToken = "omni_token";
    const string KUid = "omni_uid";
    const string KUser = "omni_user";
    const string KScopeType = "omni_search_scope_type";
    const string KScopeId = "omni_search_scope_id";
    const string KScopeName = "omni_search_scope_name";

    /// <summary>规范化地址：补 http://、去尾部斜杠。</summary>
    public static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var value = input.Trim();
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            value = "http://" + value;
        return value.TrimEnd('/');
    }

    public static string BaseUrl
    {
        get => Setting.GetString(KBase);
        set => Setting.Put(KBase, Normalize(value));
    }

    public static string Token
    {
        get => Setting.GetString(KToken);
        set => Setting.Put(KToken, value ?? "");
    }

    public static string UserId
    {
        get => Setting.GetString(KUid);
        set => Setting.Put(KUid, value ?? "");
    }

    public static string UserName
    {
        get => Setting.GetString(KUser);
        set => Setting.Put(KUser, value ?? "");
    }

    public static bool IsLoggedIn =>
        !string.IsNullOrEmpty(BaseUrl) && !string.IsNullOrEmpty(Token);

    /// <summary>搜索范围：空=全站，site=指定站点，spider=指定爬虫。</summary>
    public static string SearchScopeType
    {
        get => Setting.GetString(KScopeType);
        set => Setting.Put(KScopeType, value ?? "");
    }

    public static string SearchScopeId
    {
        get => Setting.GetString(KScopeId);
        set => Setting.Put(KScopeId, value ?? "");
    }

    public static string SearchScopeName
    {
        get => Setting.GetString(KScopeName);
        set => Setting.Put(KScopeName, value ?? "");
    }

    public static void SetSearchScope(string type, string id, string name)
    {
        SearchScopeType = type;
        SearchScopeId = id;
        SearchScopeName = name;
    }

    public static void SaveSession(string baseUrl, string userName, string token, string userId)
    {
        BaseUrl = baseUrl;
        UserName = userName ?? "";
        Token = token ?? "";
        UserId = userId ?? "";
    }

    /// <summary>退出登录：清会话，保留服务器地址与账号，方便再次登录。</summary>
    public static void Logout()
    {
        Token = "";
        UserId = "";
    }

    /// <summary>切换服务器：清空全部会话信息。</summary>
    public static void Reset()
    {
        Logout();
        Setting.Remove(KBase);
        Setting.Remove(KUser);
    }
}
