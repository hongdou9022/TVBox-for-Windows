namespace TVBoxForWindows.Omni;

/// <summary>单个未返回的搜索源。</summary>
public sealed class OmniSearchFailure
{
    public string Name { get; init; } = "";
    /// <summary>site 或 spider。</summary>
    public string SourceType { get; init; } = "";
    public string Reason { get; init; } = "";
}

/// <summary>一轮搜索的汇总状态：实际参与源数与失败明细。</summary>
public sealed class OmniSearchStatus
{
    public int SearchedSources { get; init; }
    public bool FromCache { get; init; }
    public List<OmniSearchFailure> Failures { get; init; } = new();
}

/// <summary>搜索范围菜单项：全站或指定站点/爬虫。</summary>
public sealed class OmniScopeOption
{
    public string Type { get; init; } = "";
    public string Id { get; init; } = "";
    public string Name { get; init; } = "全站";
    public string Display => string.IsNullOrEmpty(Type)
        ? "全站"
        : Name + (Type == "spider" ? " · 爬虫" : "");

    public override string ToString() => Display;
}
