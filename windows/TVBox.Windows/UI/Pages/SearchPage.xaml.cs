using System.Collections.ObjectModel;
using System.ComponentModel;
using TVBoxForWindows.Core;
using TVBoxForWindows.Engine;
using TVBoxForWindows.Omni;
using Microsoft.UI.Dispatching;
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
    readonly ObservableCollection<SiteGroup> _sourceGroups = new();
    readonly Dictionary<string, SiteGroup> _groupByName = new(StringComparer.Ordinal);
    readonly List<VodItem> _allItems = new();
    readonly HashSet<string> _resultSources = new(StringComparer.Ordinal);
    readonly DispatcherQueueTimer _rebuildTimer;
    SiteGroup _resultGroup;
    CancellationTokenSource _cts;
    string _lastKeyword = "";
    int _page = 1;
    bool _loadingMore;
    bool _end;
    bool _suppressScopeChange;
    OmniSearchStatus _lastStatus;

    public SearchPage()
    {
        InitializeComponent();
        SiteList.ItemsSource = _groups;
        GroupedResultList.ItemsSource = _sourceGroups;
        _rebuildTimer = DispatcherQueue.CreateTimer();
        _rebuildTimer.Interval = TimeSpan.FromMilliseconds(180);
        _rebuildTimer.IsRepeating = false;
        _rebuildTimer.Tick += (s, e) => RebuildGroups();
        RefreshDisplayStyle();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        BackButton.Visibility = Frame.CanGoBack ? Visibility.Visible : Visibility.Collapsed;
        RefreshDisplayStyle();
        UpdateScope();
        _ = LoadScopeOptionsAsync();
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

    async Task LoadScopeOptionsAsync()
    {
        try
        {
            // 沿用 10 分钟配置缓存，避免每次进入搜索页都多发一次 local-sources 请求。
            if (OmniConfig.IsLoggedIn) await LocalSourceStore.Instance.SyncAsync();
        }
        catch (Exception e) { Logger.E("SearchPage.scope", e.Message); }

        var options = OmniSearchService.ScopeOptions();
        var current = options.FirstOrDefault(option =>
                          option.Type == OmniConfig.SearchScopeType && option.Id == OmniConfig.SearchScopeId)
                      ?? options[0];
        if (current.Type != OmniConfig.SearchScopeType || current.Id != OmniConfig.SearchScopeId)
            OmniConfig.SetSearchScope(current.Type, current.Id, current.Name);

        _suppressScopeChange = true;
        ScopeCombo.ItemsSource = options;
        ScopeCombo.SelectedItem = current;
        _suppressScopeChange = false;
        UpdateScope();
    }

    void OnScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressScopeChange || ScopeCombo.SelectedItem is not OmniScopeOption option) return;
        OmniConfig.SetSearchScope(option.Type, option.Id, option.Name);
        UpdateScope();
        var keyword = string.IsNullOrWhiteSpace(SearchBox.Text) ? _lastKeyword : SearchBox.Text;
        if (!string.IsNullOrWhiteSpace(keyword)) StartSearch(keyword);
    }

    void UpdateScope()
    {
        if (!string.IsNullOrEmpty(OmniConfig.SearchScopeType))
        {
            ScopeText.Text = $"仅搜索 {OmniConfig.SearchScopeName}";
            return;
        }
        var sites = LocalSiteEngine.Instance.ScopedSites().Count;
        var spiders = LocalSpiderEngine.Instance.ScopedSpiders().Count;
        ScopeText.Text = sites + spiders > 0 ? $"将搜索 {sites} 站点 + {spiders} 爬虫" : "暂无可搜索源";
    }

    void OnQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) => StartSearch(args.QueryText);

    void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var text = (sender.Text ?? "").Trim();
        sender.ItemsSource = text.Length >= 2 ? OmniTitleIndex.Suggest(text) : null;
    }

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
        _resultSources.Clear();
        _groups.Clear();
        _sourceGroups.Clear();
        _groupByName.Clear();
        _resultGroup = null;
        ResultGrid.ItemsSource = null;
        SiteList.Visibility = Visibility.Collapsed;
        EmptyText.Visibility = Visibility.Collapsed;
        SearchProgress.Visibility = Visibility.Visible;
        SearchStatusText.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;
        _lastStatus = null;
        FailureButton.Visibility = Visibility.Collapsed;
        try
        {
            // 每源完成即回调（回调已切 UI 线程，空结果不回调）
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
                        SourcePriority = site.SourcePriority,
                        SiteKey = site.Key,
                        VodId = v.Id,
                        FromCache = site.FromCache,
                    })
                    .ToList();
                _allItems.AddRange(items);
                foreach (var item in items) _resultSources.Add(item.SourceName ?? "");
                if (SearchProgress.Visibility == Visibility.Visible)
                {
                    SearchProgress.Visibility = Visibility.Collapsed;
                    SearchStatusText.Visibility = Visibility.Visible;
                }
                SearchStatusText.Text = $"已找到 {_resultSources.Count} 个来源，仍在搜索其他站点…";
                ScheduleRebuild();
            }, cts.Token, status =>
            {
                if (cts.IsCancellationRequested) return;
                _lastStatus = status;
                UpdateFailureButton();
            });
        }
        catch (Exception e) { Logger.E("SearchPage", e.Message); }
        finally
        {
            if (cts == _cts)
            {
                _cts = null;
                SearchProgress.Visibility = Visibility.Collapsed;
                SearchStatusText.Visibility = Visibility.Collapsed;
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
                        SourcePriority = site.SourcePriority,
                        SiteKey = site.Key,
                        VodId = v.Id,
                        FromCache = site.FromCache,
                    })
                    .Where(item => !_allItems.Any(existing =>
                        (existing.SiteKey == item.SiteKey && existing.VodId == item.VodId) ||
                        string.Equals(existing.Title, item.Title, StringComparison.Ordinal)))
                    .ToList();
                if (items.Count == 0) return;
                added += items.Count;
                _allItems.AddRange(items);
                ScheduleRebuild();
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

    /// <summary>结果批次到达很快时合并成一次重建，避免每来一个源都重排整页导致卡顿。</summary>
    void ScheduleRebuild()
    {
        if (!_rebuildTimer.IsRunning) _rebuildTimer.Start();
    }

    /// <summary>原地增量更新分组：不重置 ItemsSource，避免流式结果到达时整页闪烁和滚动跳回顶部。</summary>
    void RebuildGroups()
    {
        if (_allItems.Count == 0) return;
        var ordered = _allItems
            .OrderBy(item => OmniSearchRank.MatchRank(_lastKeyword, item.Title))
            .ThenBy(item => OmniSearchRank.TokenHit(_lastKeyword, item.Title))
            .ThenByDescending(item => item.SourcePriority)
            .ThenBy(item => item.SourceName)
            .ThenBy(item => item.Title)
            .ToList();
        var grouped = ordered
            .GroupBy(item => SourceGroupName(string.IsNullOrEmpty(item.SourceName) ? "CloudTV" : item.SourceName))
            .Select(group => (Name: group.Key, Items: group.ToList()))
            .ToList();

        var desiredNames = grouped.Select(group => group.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var stale in _sourceGroups.Where(group => !desiredNames.Contains(group.SiteName)).ToList())
        {
            _sourceGroups.Remove(stale);
            _groups.Remove(stale);
            _groupByName.Remove(stale.SiteName);
            if (ReferenceEquals(_resultGroup, stale)) _resultGroup = null;
        }
        foreach (var (name, items) in grouped)
        {
            if (!_groupByName.TryGetValue(name, out var group))
            {
                group = new SiteGroup(name);
                _groupByName[name] = group;
                _sourceGroups.Add(group);
            }
            group.SetItems(items);
        }

        var tabs = new List<SiteGroup>();
        if (_sourceGroups.Count > 1)
        {
            if (!_groupByName.TryGetValue("全部", out var all))
            {
                all = new SiteGroup("全部");
                _groupByName["全部"] = all;
            }
            all.SetItems(ordered);
            tabs.Add(all);
        }
        tabs.AddRange(_sourceGroups);
        SyncGroups(_groups, tabs);

        RefreshDisplayStyle();
        if (_groups.Count == 0) return;
        var selectedName = (SiteList.SelectedItem as SiteGroup)?.SiteName;
        var index = string.IsNullOrEmpty(selectedName)
            ? 0
            : Math.Max(0, _groups.ToList().FindIndex(group => group.SiteName == selectedName));
        if (SiteList.SelectedIndex != index) SiteList.SelectedIndex = index;
        ShowGroup(_groups[index]);
    }

    static void SyncGroups(ObservableCollection<SiteGroup> target, List<SiteGroup> desired)
    {
        var desiredNames = desired.Select(group => group.SiteName).ToHashSet(StringComparer.Ordinal);
        for (var i = target.Count - 1; i >= 0; i--)
            if (!desiredNames.Contains(target[i].SiteName)) target.RemoveAt(i);
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i])) continue;
            var existing = -1;
            for (var j = i + 1; j < target.Count; j++)
                if (ReferenceEquals(target[j], desired[i])) { existing = j; break; }
            if (existing >= 0) target.Move(existing, i);
            else target.Insert(i, desired[i]);
        }
    }

    void ShowGroup(SiteGroup group)
    {
        if (group == null || ReferenceEquals(_resultGroup, group)) return;
        _resultGroup = group;
        ResultGrid.ItemsSource = group.Items;
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

    void UpdateFailureButton()
    {
        var count = _lastStatus?.Failures.Count ?? 0;
        if (count == 0)
        {
            FailureButton.Visibility = Visibility.Collapsed;
            return;
        }
        FailureButton.Content = $"{count} 个源失败，查看详情";
        FailureButton.Visibility = Visibility.Visible;
    }

    async void OnShowFailures(object sender, RoutedEventArgs e)
    {
        var failures = _lastStatus?.Failures;
        if (failures == null || failures.Count == 0) return;
        var text = string.Join("\n", failures
            .OrderBy(failure => failure.SourceType)
            .ThenBy(failure => failure.Name)
            .Select(failure =>
                $"{failure.Name}（{(failure.SourceType == "spider" ? "爬虫" : "站点")}）：{failure.Reason}"));
        var dialog = new ContentDialog
        {
            Title = $"失败的源（{failures.Count}/{_lastStatus.SearchedSources}）",
            Content = new ScrollViewer
            {
                MaxHeight = 420,
                Content = new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                },
            },
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
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
        {
            _cts?.Cancel();
            Frame.Navigate(typeof(DetailPage), new DetailArgs { SiteKey = item.SiteKey, VodId = item.VodId, Name = item.Title });
        }
    }

    void OnGroupedResultClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is VodItem item)
        {
            _cts?.Cancel();
            Frame.Navigate(typeof(DetailPage), new DetailArgs { SiteKey = item.SiteKey, VodId = item.VodId, Name = item.Title });
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        // 离开搜索页就停止剩余站点请求，避免搜索流量与海报下载拖慢播放。
        _cts?.Cancel();
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
        if (SiteList.SelectedItem is not SiteGroup group) return;
        ShowGroup(group);
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

/// <summary>按站点分组的结果组（Expander DataTemplate Binding 用）；Items 原地增量更新，避免整组重建。</summary>
public class SiteGroup : INotifyPropertyChanged
{
    public SiteGroup(string siteName)
    {
        SiteName = siteName;
    }

    public string SiteName { get; }
    public ObservableCollection<VodItem> Items { get; } = new();
    public string Header => $"{SiteName}（{Items.Count}）";
    public event PropertyChangedEventHandler PropertyChanged;

    public void SetItems(IReadOnlyList<VodItem> items)
    {
        var desiredKeys = new HashSet<string>(items.Select(item => item.Key), StringComparer.Ordinal);
        for (var i = Items.Count - 1; i >= 0; i--)
            if (!desiredKeys.Contains(Items[i].Key)) Items.RemoveAt(i);
        for (var i = 0; i < items.Count; i++)
        {
            if (i < Items.Count && Items[i].Key == items[i].Key) continue;
            var existing = -1;
            for (var j = i + 1; j < Items.Count; j++)
                if (Items[j].Key == items[i].Key) { existing = j; break; }
            if (existing >= 0) Items.Move(existing, i);
            else Items.Insert(i, items[i]);
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Header)));
    }
}

/// <summary>搜索结果卡片视图项。</summary>
public class VodItem
{
    public string Pic { get; set; }
    public string Title { get; set; }
    public string Remark { get; set; }
    public string SourceName { get; set; }
    public int SourcePriority { get; set; }
    public string SiteKey { get; set; }
    public string VodId { get; set; }
    public string Action { get; set; }
    public bool FromCache { get; set; }
    public string Key => (SiteKey ?? "") + "|" + (VodId ?? "");
    public string SourceText => FromCache && !string.IsNullOrEmpty(SourceName)
        ? SourceName + " · 缓存"
        : SourceName;
}
