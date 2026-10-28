using TVBoxForWindows.Core;
using TVBoxForWindows.Engine;
using TVBoxForWindows.Models;
using TVBoxForWindows.Omni;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;

namespace TVBoxForWindows.UI.Pages;

/// <summary>收藏页（契约 §5.2）：收藏网格（PosterCard，备注=站点名）；右键取消收藏；点击 → DetailPage。</summary>
public sealed partial class KeepPage : Page
{
    long _revision = -1;
    int _cid = -1;
    KeepItem _contextItem;
    List<OmniApi.FollowItem> _follows = new();

    public KeepPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        RefreshIfChanged();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        HideContextMenu();
        base.OnNavigatedFrom(e);
    }

    public void RefreshIfChanged()
    {
        var cid = VodConfigService.Cid;
        var revision = Stores.KeepRevision;
        if (_cid == cid && _revision == revision) return;
        Reload();
        _cid = cid;
        _revision = revision;
    }

    /// <summary>重新读取本地 + CloudTV 收藏，并按 libraryId/videoId 合并（远程补 FavoriteId）。</summary>
    async void Reload()
    {
        var local = Stores.GetKeeps(VodConfigService.Cid)
            .Select(k => new KeepItem
            {
                Pic = k.VodPic,
                Title = k.VodName,
                Remark = k.SiteName,
                SiteKey = k.SiteKey,
                VodId = k.VodId,
                Key = k.Key,
            })
            .ToList();
        var localByRef = new Dictionary<string, KeepItem>(StringComparer.Ordinal);
        foreach (var item in local)
        {
            var reference = OmniIds.DecodeVodRef(item.VodId);
            if (reference != null) localByRef[reference.LibraryId + "|" + reference.VideoId] = item;
        }

        try
        {
            var favorites = await OmniApi.FavoritesAsync(1, 100);
            foreach (var favorite in favorites.Items)
            {
                if (favorite == null || string.IsNullOrEmpty(favorite.SourceId) || string.IsNullOrEmpty(favorite.VodId)) continue;
                var refKey = favorite.SourceId + "|" + favorite.VodId;
                if (localByRef.TryGetValue(refKey, out var existing))
                {
                    existing.FavoriteId = favorite.Id;
                    continue;
                }
                var vodId = OmniIds.EncodeVod(favorite.SourceId, favorite.VodId);
                var sourceName = favorite.SourceName;
                if (string.IsNullOrEmpty(sourceName))
                {
                    var sources = LocalSourceStore.Instance.Cached;
                    sourceName = sources?.Sites?.FirstOrDefault(site => site.Id == favorite.SourceId)?.Name
                        ?? sources?.Spiders?.FirstOrDefault(spider => spider.Id == favorite.SourceId)?.Name;
                }
                local.Add(new KeepItem
                {
                    Pic = favorite.CoverImage,
                    Title = favorite.Title,
                    Remark = string.IsNullOrEmpty(sourceName) ? "CloudTV" : sourceName,
                    SiteKey = OmniConfig.SourceKey,
                    VodId = vodId,
                    Key = OmniConfig.SourceKey + "@" + vodId,
                    FavoriteId = favorite.Id,
                });
            }
        }
        catch (Exception e)
        {
            Logger.E("KeepPage", "favorites: " + e.Message);
        }

        try
        {
            _follows = await OmniApi.FollowsAsync(30);
        }
        catch (Exception e)
        {
            Logger.E("KeepPage", "follows: " + e.Message);
            _follows = new();
        }

        KeepGrid.ItemsSource = local;
        CountText.Text = local.Count > 0 ? $"共 {local.Count} 条" : "";
        EmptyText.Visibility = local.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        FollowButton.Visibility = _follows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FollowButton.Content = _follows.Count > 0 ? $"关注 ({_follows.Count})" : "关注";
    }

    void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is KeepItem item)
            Frame.Navigate(typeof(DetailPage), new DetailArgs { SiteKey = item.SiteKey, VodId = item.VodId, Name = item.Title });
    }

    void OnCardPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement target || target.DataContext is not KeepItem item) return;
        if (!e.GetCurrentPoint(target).Properties.IsRightButtonPressed) return;
        _contextItem = item;
        ContextLayer.Visibility = Visibility.Visible;
        ContextLayer.UpdateLayout();
        PositionContextMenu(e.GetCurrentPoint(ContextLayer).Position);
        e.Handled = true;
    }

    void PositionContextMenu(Point point)
    {
        ContextMenuPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Math.Max(152, ContextMenuPanel.DesiredSize.Width);
        var height = Math.Max(48, ContextMenuPanel.DesiredSize.Height);
        Canvas.SetLeft(ContextMenuPanel, Math.Clamp(point.X, 8, Math.Max(8, ContextLayer.ActualWidth - width - 8)));
        Canvas.SetTop(ContextMenuPanel, Math.Clamp(point.Y, 8, Math.Max(8, ContextLayer.ActualHeight - height - 8)));
    }

    void OnContextLayerPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, ContextLayer)) return;
        HideContextMenu();
        e.Handled = true;
    }

    void HideContextMenu()
    {
        ContextLayer.Visibility = Visibility.Collapsed;
        _contextItem = null;
    }

    void OnRemove(object sender, RoutedEventArgs e)
    {
        var item = _contextItem;
        HideContextMenu();
        if (item == null) return;
        Stores.DeleteKeep(VodConfigService.Cid, item.Key);
        if (!string.IsNullOrEmpty(item.FavoriteId)) _ = OmniApi.RemoveFavoriteAsync(item.FavoriteId);
        RefreshIfChanged();
    }

    /// <summary>观看关注只读列表：点击按标题进入搜索。</summary>
    async void OnShowFollows(object sender, RoutedEventArgs e)
    {
        if (_follows.Count == 0) return;
        var panel = new StackPanel { Spacing = 6 };
        var dialog = new ContentDialog
        {
            Title = "我的关注",
            CloseButtonText = "关闭",
            XamlRoot = XamlRoot,
        };
        string selected = null;
        foreach (var follow in _follows)
        {
            var remark = !string.IsNullOrEmpty(follow.NextEpisodeAirDate)
                ? "下一集 " + follow.NextEpisodeAirDate
                : follow.CurrentEpisodeNumber is > 0
                    ? "已看第 " + follow.CurrentEpisodeNumber + " 集"
                    : follow.MediaType;
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = follow.Title },
                        new TextBlock { Text = remark, FontSize = 11, Opacity = 0.7 },
                    },
                },
            };
            var keyword = follow.Title;
            button.Click += (s, args) =>
            {
                selected = keyword;
                dialog.Hide();
            };
            panel.Children.Add(button);
        }
        dialog.Content = new ScrollViewer { Content = panel, MaxHeight = 480, Width = 420 };
        await dialog.ShowAsync();
        if (!string.IsNullOrEmpty(selected)) Frame.Navigate(typeof(SearchPage), selected);
    }
}

/// <summary>收藏网格视图项（DataTemplate Binding 用，公共属性 getter）。</summary>
public class KeepItem
{
    public string Pic { get; set; }
    public string Title { get; set; }
    public string Remark { get; set; }
    public string SiteKey { get; set; }
    public string VodId { get; set; }
    public string Key { get; set; }
    public string FavoriteId { get; set; }
}
