using System.Collections.ObjectModel;
using TVBoxForWindows.Core;
using TVBoxForWindows.Engine;
using TVBoxForWindows.Omni;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

namespace TVBoxForWindows.UI.Pages;

/// <summary>搜索页：SearchService.SearchAll 流式结果按站点分组（顶部横向站点 + 纵向卡片）、
/// 搜索历史 chips（Setting 存 JSON list）、进行中的 ProgressBar 与取消。导航参数可带初始关键词（string）。</summary>
public sealed partial class SearchPage : Page
{
    const string HistoryKey = "keyword_history";
    const int HistoryMax = 16;

    readonly ObservableCollection<SiteGroup> _groups = new();
    readonly List<VodItem> _allItems = new();
    CancellationTokenSource _cts;
    string _lastKeyword = "";
    int _page = 1;
    bool _loadingMore;
    bool _end;

    public SearchPage()
    {
        InitializeComponent();
        SiteList.ItemsSource = _groups;
        GroupedResultList.ItemsSource = _groups;
        RefreshDisplayStyle();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        BackButton.Visibility = Frame.CanGoBack ? Visibility.Visible : Visibility.Collapsed;
        RefreshDisplayStyle();
        UpdateScope();
        LoadChips();
        if (e.Parameter is string keyword && !string.IsNullOrWhiteSpace(keyword))
        {
            SearchBox.Text = keyword;
            // 从详情/播放页返回时复用已有结果，不重复搜索。
            if (e.NavigationMode == NavigationMode.Back &&
                string.Equals(keyword, _lastKeyword, StringComparison.Ordinal) &&
                _allItems.Count > 0)
                return;
            StartSearch(keyword);
        }
    }

    void UpdateScope()
    {
        var count = VodConfigService.Instance.Sites.Count(s => s.IsSearchable && !s.IsHidden);
        ScopeText.Text = count > 0 ? $"将搜索 {count} 个站点" : "暂无可搜索站点";
    }

    void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) => StartSearch(args.QueryText);

    async void StartSearch(string keyword)
    {
        keyword = (keyword ?? "").Trim();
        if (keyword.Length == 0) return;
        _lastKeyword = keyword;
        _page = 1;
        _end = false;
        _loadingMore = false;
        LoadMoreButton.Visibility = Visibility.Collapsed;
        LoadMoreButton.Content = "加载更多";
        LoadMoreButton.IsEnabled = true;
        SaveKeyword(keyword);
        LoadChips();
        UpdateScope();
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        _allItems.Clear();
        _groups.Clear();
        GroupedResultList.ItemsSource = null;
        ResultGrid.ItemsSource = null;
        SiteList.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Collapsed;
        SearchProgress.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Visible;
        try
        {
            // 每站完成即回调（契约 §2.5：回调已切 UI 线程，空结果不回调）
            await SearchService.SearchAll(keyword, false, (site, list) =>
            {
                if (cts.IsCancellationRequested) return;
                var items = list
                    .Select(v => new VodItem
                    {
                        Pic = v.Pic,
                        Title = v.CleanName,
                        Remark = v.Remarks,
                        SourceName = string.IsNullOrEmpty(v.SourceName) ? site.Name : v.SourceName,
                        SiteKey = site.Key,
                        VodId = v.Id,
                    })
                    .ToList();
                _allItems.AddRange(items);
                RebuildGroups();
            }, cts.Token);
        }
        catch (Exception e) { Logger.E("SearchPage", e.Message); }
        finally
        {
            if (cts == _cts)
            {
                _cts = null;
                SearchProgress.Visibility = Visibility.Collapsed;
                CancelButton.Visibility = Visibility.Collapsed;
                EmptyText.Visibility = _allItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                LoadMoreButton.Visibility = _allItems.Count > 0 && !_end ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    /// <summary>加载下一页：追加结果并重建来源分组；返回 0 条视为结束。</summary>
    async void OnLoadMore(object sender, RoutedEventArgs e)
    {
        if (_loadingMore || _cts != null || _end || string.IsNullOrEmpty(_lastKeyword)) return;
        _loadingMore = true;
        LoadMoreButton.IsEnabled = false;
        LoadMoreButton.Content = "加载中…";
        var page = _page + 1;
        var added = 0;
        try
        {
            using var cts = new CancellationTokenSource();
            await SearchService.SearchPage(_lastKeyword, false, page.ToString(), (site, list) =>
            {
                if (cts.IsCancellationRequested) return;
                var items = list
                    .Select(v => new VodItem
                    {
                        Pic = v.Pic,
                        Title = v.CleanName,
                        Remark = v.Remarks,
                        SourceName = string.IsNullOrEmpty(v.SourceName) ? site.Name : v.SourceName,
                        SiteKey = site.Key,
                        VodId = v.Id,
                    })
                    .Where(item => !_allItems.Any(existing =>
                        (existing.SiteKey == item.SiteKey && existing.VodId == item.VodId) ||
                        string.Equals(existing.Title, item.Title, StringComparison.Ordinal)))
                    .ToList();
                if (items.Count == 0) return;
                added += items.Count;
                _allItems.AddRange(items);
                RebuildGroups();
            }, cts.Token);
            _page = page;
            if (added == 0)
            {
                _end = true;
                LoadMoreButton.Content = "没有更多结果";
            }
            else LoadMoreButton.Content = "加载更多";
        }
        catch (Exception ex)
        {
            Logger.E("SearchPage", "load more: " + ex.Message);
            LoadMoreButton.Content = "加载更多";
        }
        finally
        {
            _loadingMore = false;
            LoadMoreButton.IsEnabled = !_end;
            LoadMoreButton.Visibility = _allItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>按来源重建分组；多来源时额外提供“全部”（保持跨来源相关度顺序）。</summary>
    void RebuildGroups()
    {
        var selected = (SiteList.SelectedItem as SiteGroup)?.SiteName;
        var ordered = _allItems
            .OrderBy(item => OmniSearchRank.MatchRank(_lastKeyword, item.Title))
            .ThenBy(item => OmniSearchRank.TokenHit(_lastKeyword, item.Title))
            .ThenBy(item => item.SourceName)
            .ThenBy(item => item.Title)
            .ToList();
        var sourceGroups = ordered
            .GroupBy(item => SourceGroupName(string.IsNullOrEmpty(item.SourceName) ? "CloudTV" : item.SourceName))
            .Select(group => new SiteGroup(group.Key, group.ToList()))
            .ToList();
        _groups.Clear();
        if (sourceGroups.Count > 1) _groups.Add(new SiteGroup("全部", ordered));
        foreach (var group in sourceGroups) _groups.Add(group);
        GroupedResultList.ItemsSource = sourceGroups;
        RefreshDisplayStyle();
        if (_groups.Count == 0) return;
        var index = 0;
        if (!string.IsNullOrEmpty(selected))
        {
            var found = _groups.ToList().FindIndex(group => group.SiteName == selected);
            if (found >= 0) index = found;
        }
        SiteList.SelectedIndex = index;
        ResultGrid.ItemsSource = _groups[index].Items;
    }

    /// <summary>聚合卡片来源形如“豪华资源 等 6 源”，分组时按主源名归并。</summary>
    static string SourceGroupName(string name)
    {
        var value = name ?? "";
        var index = value.IndexOf(" 等", StringComparison.Ordinal);
        if (index > 0) return value[..index];
        index = value.IndexOf(" +", StringComparison.Ordinal);
        return index > 0 ? value[..index] : value;
    }

    void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    void OnBack(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        if (Frame.CanGoBack) Frame.GoBack();
    }

    void OnResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is VodItem item)
            Frame.Navigate(typeof(DetailPage), new DetailArgs { SiteKey = item.SiteKey, VodId = item.VodId, Name = item.Title });
    }

    void OnGroupedResultClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is VodItem item)
            Frame.Navigate(typeof(DetailPage), new DetailArgs { SiteKey = item.SiteKey, VodId = item.VodId, Name = item.Title });
    }

    public void RefreshDisplayStyle()
    {
        var grouped = Setting.SearchDisplay == 1;
        GroupedResultList.Visibility = grouped ? Visibility.Visible : Visibility.Collapsed;
        SiteList.Visibility = !grouped && _groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultGrid.Visibility = grouped ? Visibility.Collapsed : Visibility.Visible;
    }

    void OnSiteSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ResultGrid.ItemsSource = (SiteList.SelectedItem as SiteGroup)?.Items;
        DispatcherQueue.TryEnqueue(() => FindScrollViewer(ResultGrid)?.ChangeView(null, 0, null, true));
    }

    void OnSitePointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var viewer = FindScrollViewer(SiteList);
        if (viewer == null || viewer.ScrollableWidth <= 0) return;
        var delta = e.GetCurrentPoint(SiteList).Properties.MouseWheelDelta;
        viewer.ChangeView(Math.Clamp(viewer.HorizontalOffset - delta, 0, viewer.ScrollableWidth), null, null, true);
        e.Handled = true;
    }

    static ScrollViewer FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer) return viewer;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }

    // ---------- 搜索历史（Setting 存 JSON list） ----------

    List<string> GetKeywords() => JsonUtil.Deserialize<List<string>>(Setting.GetString(HistoryKey)) ?? new();

    void SaveKeyword(string keyword)
    {
        var list = GetKeywords();
        list.Remove(keyword);
        list.Insert(0, keyword);
        if (list.Count > HistoryMax) list = list.Take(HistoryMax).ToList();
        Setting.Put(HistoryKey, JsonUtil.Serialize(list));
    }

    void LoadChips()
    {
        ChipsPanel.Children.Clear();
        var list = GetKeywords();
        HistoryPanel.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var word in list)
        {
            var chip = new Button { Content = word };
            chip.Click += (s, e) => { SearchBox.Text = word; StartSearch(word); };
            ChipsPanel.Children.Add(chip);
        }
    }

    void OnClearHistory(object sender, RoutedEventArgs e)
    {
        Setting.Remove(HistoryKey);
        LoadChips();
    }
}

/// <summary>按站点分组的结果组（Expander DataTemplate Binding 用）。</summary>
public class SiteGroup
{
    public SiteGroup(string siteName, List<VodItem> items)
    {
        SiteName = siteName;
        Items = items;
    }

    public string SiteName { get; }
    public List<VodItem> Items { get; }
    public string Header => $"{SiteName}（{Items.Count}）";
}

/// <summary>搜索结果卡片视图项。</summary>
public class VodItem
{
    public string Pic { get; set; }
    public string Title { get; set; }
    public string Remark { get; set; }
    public string SourceName { get; set; }
    public string SiteKey { get; set; }
    public string VodId { get; set; }
    public string Action { get; set; }
}
