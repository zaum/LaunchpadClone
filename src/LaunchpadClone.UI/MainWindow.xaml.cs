using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LaunchpadClone.Core.Cache;
using LaunchpadClone.Core.Discovery;
using LaunchpadClone.Core.Groups;
using LaunchpadClone.Core.Icons;
using LaunchpadClone.Core.Launch;
using LaunchpadClone.Core.Layout;
using LaunchpadClone.Core.Models;
using LaunchpadClone.Core.Search;
using LaunchpadClone.Core.Settings;
using LaunchpadClone.UI.Native;

namespace LaunchpadClone.UI;

public partial class MainWindow : INotifyPropertyChanged
{
    /// <summary>Anything that can appear as a tile: an app or a group.</summary>
    public interface ITileRow
    {
        string DisplayName { get; }
    }

    // One row in the grid — wraps an AppItem and adds UI-only state: the
    // decoded icon plus the badge flags (INPC so async updates repaint).
    public sealed class AppRow : INotifyPropertyChanged, ITileRow
    {
        private AppItem _app;
        private ImageSource? _icon;
        private bool _showRemoveBadge;
        private bool _showUninstallBadge;
        private bool _showAsPlaceholder;

        public AppRow(AppItem app) => _app = app;

        public AppItem App => _app;

        // Watcher upserts replace the AppItem in place and repaint the row.
        public void Update(AppItem app)
        {
            _app = app;
            var pc = PropertyChanged;
            pc?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
        }

        // During a live reorder drag the cell is drawn as an empty slot the
        // size of a real tile, so the other icons visually "flow around" it
        // (macOS Launchpad rearrange behavior). The ghost follows the cursor.
        public bool ShowAsPlaceholder
        {
            get => _showAsPlaceholder;
            set
            {
                if (_showAsPlaceholder == value)
                    return;
                _showAsPlaceholder = value;
                var pc = PropertyChanged;
                pc?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            }
        }

        public string DisplayName => _showAsPlaceholder ? "" : _app.DisplayName;

        public ImageSource? Icon
        {
            get => _showAsPlaceholder ? null : _icon;
            set
            {
                _icon = value;
                var pc = PropertyChanged;
                pc?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            }
        }

        // ✕ badge inside an open group view (remove from group).
        public bool ShowRemoveBadge
        {
            get => _showRemoveBadge;
            set => SetField(ref _showRemoveBadge, value);
        }

        // Red ✕ badge in jiggle mode (uninstall).
        public bool ShowUninstallBadge
        {
            get => _showUninstallBadge;
            set => SetField(ref _showUninstallBadge, value);
        }

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    // A user-created group rendered as a "folder" tile with a 3x3 mini
    // preview of up to nine member icons.
    public sealed class GroupRow : INotifyPropertyChanged, ITileRow
    {
        private bool _showRemoveBadge;

        public GroupRow(AppGroup group) => Group = group;

        public AppGroup Group { get; }

        public string DisplayName => Group.Name;

        // Indexer bindings refresh together via the "Item[]" notification.
        public ImageSource?[] Previews { get; } = new ImageSource?[9];

        public void SetPreviews(IReadOnlyList<ImageSource?> icons)
        {
            for (var i = 0; i < Previews.Length; i++)
                Previews[i] = i < icons.Count ? icons[i] : null;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }

        // ✕ badge in jiggle mode (delete group).
        public bool ShowRemoveBadge
        {
            get => _showRemoveBadge;
            set
            {
                _showRemoveBadge = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowRemoveBadge)));
            }
        }

        public void NotifyNameChanged() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));

        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private static readonly Comparer<AppItem> DisplayNameComparer =
        Comparer<AppItem>.Create((a, b) => string.Compare(
            a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));

    private readonly AppCache _cache = AppCache.Default();
    private readonly AppGroupStore _groupStore = AppGroupStore.Default();
    private readonly TileLayoutStore _layoutStore = TileLayoutStore.Default();
    private readonly UwpPackageProvider _uwpProvider;
    private readonly AppDiscoveryService _discovery;
    private readonly IconExtractionService _icons;
    private readonly AppWatcher _watcher;
    private readonly ObservableCollection<AppRow> _rows = new();
    private readonly Dictionary<string, AppRow> _rowsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImageSource> _iconMemoryCache = new(StringComparer.Ordinal);
    private readonly object _iconLock = new();
    private List<AppItem> _allApps = new();

    private List<AppGroup> _groups = new();
    private readonly List<GroupRow> _groupRows = new();
    private GroupRow? _openGroup;
    private List<string> _orderIds = new(); // saved drag order (layout store)

    // Paging state: one page slice at a time, flipped by wheel, drag, keys.
    private List<ITileRow> _filtered = new();
    private int _pageIndex;
    private int _pageSize = 24;
    private System.Windows.Point? _dragOrigin;
    private bool _dragConsumed;

    // Tile-drag state (group create/add, reorder, drag-out).
    private AppRow? _dragTile;
    private bool _tileDragArmed;
    private bool _tileDragFromGroup;
    private System.Windows.Point _tileDragStart;

    // Live reorder preview: a reordered full list + where the dragged tile
    // would land, so a drag re-renders the page with "others flow around".
    private List<ITileRow>? _reorderPreview;
    private int _dragTargetIndex = -1;

    // Jiggle (uninstall) mode + the long-press that arms it.
    private bool _jiggleMode;
    private DispatcherTimer? _holdTimer;

    // Global hotkey ("open" shortcut from settings) — polled via user32.
    private DispatcherTimer? _hotKeyTimer;
    private int _hotKeyVk;
    private int _hotKeyMods;
    private const int VkControl = 0x11;
    private const int VkAlt = 0x12;
    private const int VkShift = 0x10;
    private const int VkWin = 0x5B;
    private const int VkSpace = 0x20;

    public MainWindow()
    {
        var iconCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LaunchpadClone", "icons", "hi");

        _uwpProvider = new UwpPackageProvider(iconCacheDir);
        _discovery = new AppDiscoveryService(_uwpProvider, new ExecutableAppProvider());
        _icons = new IconExtractionService(iconCacheDir);
        _watcher = new AppWatcher(_discovery);

        InitializeComponent();

        // Gestures and overlay controls.
        AppsList.MouseDoubleClick += (_, _) => LaunchSelected();
        AppsList.SizeChanged += (_, _) => RecomputeLayout();
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        GroupMembers.MouseDoubleClick += (_, _) => LaunchSelected();
        GroupNameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitGroupName();
                e.Handled = true;
            }
        };
        GroupNameBox.LostFocus += (_, _) => CommitGroupName();

        SearchBox.TextChanged += (_, _) => ApplyFilter();
        Loaded += OnLoaded;
        Closed += (_, _) => App.SettingsChanged -= OnAppSettingsChanged;

        App.SettingsChanged += OnAppSettingsChanged;
        ApplySettings(App.Settings);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Blur the desktop behind the scrim (graceful no-op if unsupported).
        WindowAccentBlur.EnableBlurBehind(this);

        // Global hotkey: native poll (no window hook needed).
        StartHotKeyPoller();
    }

    protected override void OnClosed(EventArgs e)
    {
        _hotKeyTimer?.Stop();
        _hotKeyTimer = null;
        _holdTimer?.Stop();
        base.OnClosed(e);
    }

    // Live grid size (settings): the templates bind to these.
    private double _tileWidth = 168;
    public double TileWidth
    {
        get => _tileWidth;
        private set => SetField(ref _tileWidth, value);
    }

    private double _tileIconSize = 112;
    public double TileIconSize
    {
        get => _tileIconSize;
        private set => SetField(ref _tileIconSize, value);
    }

    private double _labelMaxWidth = 156;
    public double LabelMaxWidth
    {
        get => _labelMaxWidth;
        private set => SetField(ref _labelMaxWidth, value);
    }

    private bool _applyingSettings;

    private void OnAppSettingsChanged()
    {
        Dispatcher.Invoke(() => ApplySettings(App.Settings));
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        ApplySettings(App.Settings);
        UpdateGridSizeLabel(App.Settings.TileScale);
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private void ApplySettings(AppSettings settings)
    {
        // Legacy files stored the slider raw value (70-140) instead of 0.7-1.4.
        var scale = settings.TileScale > 2 ? settings.TileScale / 100.0 : settings.TileScale;
        scale = Math.Clamp(scale, 0.7, 1.4);
        TileWidth = Math.Round(168 * scale);
        TileIconSize = Math.Round(112 * scale);
        LabelMaxWidth = TileWidth - 12;
        if (AppsList is not null)
            RecomputeLayout();

        // Sync the settings panel without re-firing change handlers.
        // During InitializeComponent some controls may still be null.
        _applyingSettings = true;
        try
        {
            if (HotCornerToggle is not null)
                HotCornerToggle.IsChecked = settings.HotCornerEnabled;
            if (TileScaleSlider is not null)
                TileScaleSlider.Value = scale * 100.0;
            if (HotKeyBox is not null)
                HotKeyBox.Text = settings.HotKey;
        }
        finally
        {
            _applyingSettings = false;
        }
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();

        try
        {
            // Order, groups and cache first so the first paint is complete.
            _orderIds = await _layoutStore.LoadAsync();
            _groups = await _groupStore.LoadAsync();
            RebuildGroupRows();

            var cached = await _cache.LoadAsync();
            if (cached.Count > 0)
            {
                RenderApps(cached);
                StatusText.Text = $"{cached.Count} apps";
                _ = ExtractMissingIconsAsync();
            }

            _watcher.AppsUpserted += OnWatcherUpserted;
            _watcher.AppsRemoved += OnWatcherRemoved;
            _watcher.Start();

            // Full rescan runs in the background so the window paints instantly
            // from cache. Quiet when cache was shown (no "Scanning..." flash).
            _ = RefreshAppsAsync(quiet: cached.Count > 0);

            // UWP and raw-exe installs have no filesystem watcher — a periodic
            // full re-scan keeps them fresh (AGENTS.md convention).
            var rescanTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(10) };
            rescanTimer.Tick += async (_, _) => await RefreshAppsAsync();
            rescanTimer.Start();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Startup failed: " + ex.Message;
        }
    }

    private bool _refreshing;

    private async Task RefreshAppsAsync(bool quiet = false)
    {
        if (_refreshing)
            return;
        _refreshing = true;
        if (!quiet)
            StatusText.Text = "Scanning...";
        try
        {
            var apps = await _discovery.ScanAllAsync().ConfigureAwait(true);
            _allApps = apps.OrderBy(
                a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
            await _cache.SaveAsync(_allApps).ConfigureAwait(true);
            RenderApps(_allApps);
            StatusText.Text = $"{_allApps.Count} apps";
            _ = ExtractMissingIconsAsync();
        }
        catch (Exception ex)
        {
            if (!quiet)
                StatusText.Text = "Scan failed: " + ex.Message;
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RenderApps(IReadOnlyList<AppItem> apps)
    {
        _rows.Clear();
        _rowsById.Clear();
        foreach (var app in apps.DistinctBy(a => a.Id))
        {
            var row = new AppRow(app);
            if (_iconMemoryCache.TryGetValue(app.Id, out var icon))
                row.Icon = icon;
            _rows.Add(row);
            _rowsById[app.Id] = row;
        }
        RefreshAllGroupPreviews();
        ApplyFilter();
    }

    // Stable id of a tile in the saved layout order.
    private static string TileId(ITileRow tile) => tile switch
    {
        GroupRow g => "g:" + g.Group.Id,
        AppRow a => a.App.Id,
        _ => tile.DisplayName
    };
    private void ApplyFilter(bool keepPage = false)
    {
        ClearDragPreview(); // a re-filtered grid must not show a stale drop slot
        var query = SearchBox.Text.Trim();
        var ranked = new List<(ITileRow Tile, double Score)>();

        if (query.Length > 0)
        {
            // Fuzzy search reaches inside groups too: matching members
            // surface as plain tiles alongside matching group names.
            foreach (var row in _rows)
            {
                var score = FuzzySearch.Score(query, row.DisplayName);
                if (score > 0)
                    ranked.Add((row, score));
            }
            foreach (var gr in _groupRows)
            {
                var score = FuzzySearch.Score(query, gr.DisplayName);
                if (score > 0)
                    ranked.Add((gr, score));
            }
            _filtered = ranked
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Tile.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .Select(x => x.Tile)
                .ToList();
        }
        else
        {
            // Ungrouped apps + group tiles, in the user's saved order;
            // unknown tiles append alphabetically at the end.
            var groupedIds = _groups
                .SelectMany(g => g.MemberIds)
                .ToHashSet(StringComparer.Ordinal);

            var byId = new Dictionary<string, ITileRow>(StringComparer.Ordinal);
            foreach (var row in _rows.Where(r => !groupedIds.Contains(r.App.Id)))
                byId[row.App.Id] = row;
            foreach (var gr in _groupRows)
                byId["g:" + gr.Group.Id] = gr;

            var result = new List<ITileRow>(byId.Count);
            var used = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in _orderIds)
            {
                if (byId.TryGetValue(id, out var tile))
                {
                    result.Add(tile);
                    used.Add(id);
                }
            }
            result.AddRange(byId
                .Where(kvp => !used.Contains(kvp.Key))
                .Select(kvp => kvp.Value)
                .OrderBy(t => t.DisplayName, StringComparer.CurrentCultureIgnoreCase));
            _filtered = result;
        }

        if (!keepPage)
            _pageIndex = 0;
        RenderPage(0);
    }
    // Shows only the current page slice; `direction` (+1 next / -1 previous /
    // 0 none) drives the soft slide animation.
    private void RenderPage(int direction)
    {
        if (_pageSize <= 0)
            _pageSize = 24;

        var pageCount = Math.Max(1, (int)Math.Ceiling(_filtered.Count / (double)_pageSize));
        if (_pageIndex >= pageCount)
            _pageIndex = pageCount - 1;

        var source = _reorderPreview ?? _filtered;
        var slice = source
            .Skip(_pageIndex * _pageSize)
            .Take(_pageSize)
            .ToList();

        AppsList.ItemsSource = slice;
        AppsList.SelectedIndex = slice.Count > 0 ? 0 : -1;

        UpdatePageDots(pageCount);
        StatusText.Text = pageCount > 1
            ? $"page {_pageIndex + 1}/{pageCount} of {_filtered.Count} apps — drag, scroll or PgUp/PgDn to flip"
            : $"{_filtered.Count} apps";

        if (direction != 0)
            AnimatePage(direction);
    }

    private void UpdatePageDots(int pageCount)
    {
        PageDots.Children.Clear();
        for (var i = 0; i < pageCount; i++)
        {
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = i == _pageIndex ? 9 : 7,
                Height = 7,
                Margin = new Thickness(3, 0, 3, 0),
                Fill = new SolidColorBrush(i == _pageIndex
                    ? System.Windows.Media.Color.FromRgb(0x6C, 0x8C, 0xFF)
                    : System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF))
            };
            PageDots.Children.Add(dot);
        }
    }

    // Page flip glides like moving within one wide surface: a single soft
    // horizontal slide, no fading — the new page drifts in from the side.
    private void AnimatePage(int direction)
    {
        const double travel = 180;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        PageSlide.BeginAnimation(TranslateTransform.XProperty, null);
        PageSlide.X = direction * travel;
        PageSlide.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(450)) { EasingFunction = ease });
    }

    // Tile cell footprint must match the template (tile width + 8px side
    // padding horizontally; icon + label + 12px vertical padding).
    private void RecomputeLayout()
    {
        if (AppsList is null)
            return;
        var width = AppsList.ActualWidth;
        var height = AppsList.ActualHeight;
        if (width < 100 || height < 100)
            return;

        var cols = Math.Max(1, (int)(width / (TileWidth + 16)));
        var rows = Math.Max(1, (int)(height / (TileIconSize + 52)));
        var newPageSize = cols * rows;
        if (newPageSize == _pageSize)
            return;

        _pageSize = newPageSize;
        RenderPage(0);
    }

    private void NextPage()
    {
        var pageCount = Math.Max(1, (int)Math.Ceiling(_filtered.Count / (double)_pageSize));
        if (_pageIndex >= pageCount - 1)
            return;
        _pageIndex++;
        RenderPage(1);
    }

    private void PreviousPage()
    {
        if (_pageIndex <= 0)
            return;
        _pageIndex--;
        RenderPage(-1);
    }

    // Wheel flips pages like a book: down = right/next, up = left/previous.
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_openGroup is not null || SettingsOverlay.Visibility == Visibility.Visible)
            return; // let inner lists/sliders handle their own scrolling
                    if (e.Delta < 0)
            NextPage();
        else if (e.Delta > 0)
            PreviousPage();
        e.Handled = true;
    }

    // ── Page sizing ─────────────────────────────────────────────────
    private int ComputePageSize()
    {
        // Based on the live tile size: columns across the window / rows / 2 (label space).
        var availWidth = ActualWidth - 80; // margins
        var cols = Math.Max(1, (int)(availWidth / (TileWidth + 24)));
        var availHeight = ActualHeight - 160; // search + status
        var rows = Math.Max(1, (int)(availHeight / (TileWidth + 40)));
        return cols * rows;
    }

    private void StartHoldTimer()
    {
        _holdTimer?.Stop();
        if (_jiggleMode)
            return;
        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _holdTimer.Tick += (_, _) =>
        {
            _holdTimer?.Stop();
            _holdTimer = null;
            EnterJiggle();
        };
        _holdTimer.Start();
    }

    private void StopHoldTimer()
    {
        _holdTimer?.Stop();
        _holdTimer = null;
    }
    private int ComputePageCount(int count) =>
        Math.Max(1, (int)Math.Ceiling(count / (double)(ComputePageSize())));

    // ── Gesture / positioning helpers ─────────────────────────────
    private bool IsWithinSearch(DependencyObject? source)
    {
        for (var node = source; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
            if (node == SearchBox || node == ClearSearchButton)
                return true;
        return false;
    }

    private void OnClearSearch(object sender, MouseButtonEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
    }

    // Walks up the click source to the list-item that carries the tile.
    private ListBoxItem? FindTileContainer(DependencyObject? source)
    {
        for (var node = source; node is not null; node = (node as System.Windows.Controls.Control)?.Parent)
            if (node is ListBoxItem item)
                return item;
        return null;
    }

    private ListBoxItem? FindMemberContainer(DependencyObject? source)
    {
        // Same walk, but only within the group members list.
        for (var node = source; node is not null; node = (node as System.Windows.Controls.Control)?.Parent)
            if (node is ListBoxItem item && GroupMembers.ItemContainerGenerator?.ItemFromContainer(item) is not null)
                return item;
        return null;
    }

    // ── Mouse handling: drag, jiggle, reorder ───────────────────────
    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (SettingsOverlay.Visibility == Visibility.Visible)
            return;

        if (IsWithinSearch(e.OriginalSource as DependencyObject))
            return;

        // Long-press anywhere arms jiggle (uninstall) mode; movement cancels.
        _tileDragStart = e.GetPosition(this);
        StartHoldTimer();
        _dragOrigin = e.GetPosition(this);
        _dragConsumed = false;

        if (_openGroup is not null)
        {
            if (FindMemberContainer(e.OriginalSource as DependencyObject) is { } member
                && member.DataContext is AppRow groupTile)
            {
                // Custom mouse-move/up drag (ghost + placeholder). Do NOT start
                // a blocking Ole DoDragDrop here: it suppresses MouseMove/MouseUp
                // so the live reorder preview and drop handling never run.
                _dragTile = groupTile;
                _tileDragArmed = false;
                _tileDragFromGroup = true;
            }
            return;
        }

        if (_jiggleMode)
            return; // tiles stay put; badges handle clicks

        if (FindTileContainer(e.OriginalSource as DependencyObject) is { } container
            && container.DataContext is AppRow tile)
        {
            _dragTile = tile;
            _tileDragArmed = false;
            _tileDragFromGroup = false;
        }
    }

    private AppRow? FindRowFromSender(object sender)
    {
        for (var node = sender as DependencyObject; node is not null; node = (node as System.Windows.Controls.Control)?.Parent)
            if (node is ListBoxItem item && item.DataContext is AppRow r)
                return r;
        return null;
    }

    private GroupRow? FindGroupFromSender(object sender)
    {
        for (var node = sender as DependencyObject; node is not null; node = (node as System.Windows.Controls.Control)?.Parent)
            if (node is ListBoxItem item && item.DataContext is GroupRow g)
                return g;
        return null;
    }

    // ── Drag / drop: creates groups, adds to group, reorders, drag-out ─
    private void OnDragEnter(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("Tile"))
            return;
        var tile = e.Data.GetData("Tile") as AppRow;
        if (tile is null)
            return;

        if (SettingsOverlay.Visibility == Visibility.Visible)
            return;

        e.Effects = System.Windows.DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (SettingsOverlay.Visibility == Visibility.Visible)
            return;
        if (!e.Data.GetDataPresent("Tile"))
            return;

        e.Effects = System.Windows.DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent("Tile"))
            return;
        var dragged = e.Data.GetData("Tile") as AppRow;
        if (dragged is null)
            return;

        // Group target: drop onto a group tile or onto the open group window.
        if (FindGroupFromSender(sender) is { } targetGroup)
        {
            AddToGroup(targetGroup, dragged);
            return;
        }

        // Open-group window: drop on the members area = add to group.
        if (_openGroup is not null && FindMemberContainer(e.OriginalSource as DependencyObject) is not null)
        {
            AddToGroup(_openGroup, dragged);
            return;
        }

        // Drag-out from a group: drop back on the main grid = remove from group.
        if (_tileDragFromGroup && FindTileContainer(e.OriginalSource as DependencyObject) is { } container)
        {
            if (_openGroup is not null && dragged == _dragTile)
                RemoveFromGroup(dragged);
            else
                TryReorder(dragged, container);
            return;
        }

        // Main-grid drop on a tile: create/add group.
        if (FindTileContainer(e.OriginalSource as DependencyObject) is { } targetContainer
            && targetContainer.DataContext is AppRow targetTile && targetTile != dragged)
        {
            if (_tileDragFromGroup)
            {
                AddToGroup(_openGroup!, dragged);
            }
            else
            {
                CreateGroup(dragged, targetTile);
            }
            return;
        }
    }

    private void CreateGroup(AppRow a, AppRow b)
    {
        var group = new AppGroup
        {
            Name = a.DisplayName + " & " + b.DisplayName,
            MemberIds = [a.App.Id, b.App.Id]
        };
        _groups.Add(group);
        PersistGroups();
        RebuildGroupRows();
        var row = _groupRows.FirstOrDefault(g => g.Group.Id == group.Id);
        if (row is not null)
            OpenGroup(row);
    }

    private void AddToGroup(GroupRow? target, AppRow source)
    {
        if (target is null)
            return;
        if (!target.Group.MemberIds.Contains(source.App.Id))
        {
            target.Group.MemberIds.Add(source.App.Id);
            PersistGroups();
            RefreshAllGroupPreviews();
            if (_openGroup is not null && _openGroup == target)
                OpenGroup(target); // re-render members
        }
    }

    private void RemoveFromGroup(AppRow member)
    {
        if (_openGroup is null)
            return;
        _openGroup.Group.MemberIds.Remove(member.App.Id);
        PersistGroups();
        if (_openGroup.Group.MemberIds.Count == 0)
        {
            _groups.Remove(_openGroup.Group);
            _openGroup = null;
            GroupOverlay.Visibility = Visibility.Collapsed;
            RebuildGroupRows();
            ApplyFilter();
            return;
        }
        OpenGroup(_openGroup);
        RefreshAllGroupPreviews();
    }

    private void TryReorder(AppRow tile, ListBoxItem targetContainer)
    {
        // Live reordering within the current page slice: the dropped tile
        // swaps with the target. For now, swap-with-target (macOS flows all
        // others, but a swap is enough to reorder).
        var targetTile = targetContainer.DataContext as ITileRow;
        if (targetTile is null)
            return;

        if (_reorderPreview is null)
            _reorderPreview = new List<ITileRow>(_filtered);

        var from = _reorderPreview.IndexOf(tile);
        var to = _reorderPreview.IndexOf(targetTile);
        if (from >= 0 && to >= 0 && from != to)
        {
            (_reorderPreview[from], _reorderPreview[to]) = (_reorderPreview[to], _reorderPreview[from]);
            _reorderPreview.ToList(); // force
            RenderPage(0);
            PersistLayout();
        }
    }

    // ── Launching ─────────────────────────────────────────────────
    // Enter / single-click / double-click. Groups open instead of launch.
    private void LaunchSelected()
    {
        var selected = _openGroup is not null
            ? GroupMembers.SelectedItem
            : AppsList.SelectedItem;
        if (selected is GroupRow groupRow)
        {
            OpenGroup(groupRow);
            return;
        }
        if (selected is AppRow row)
        {
            Close(); // overlay dismisses once the app opens (macOS behavior)
            AppLauncher.Launch(row.App);
        }
    }

    // ── Group (folder) operations ─────────────────────────────────
    private void OpenGroup(GroupRow gr)
    {
        _openGroup = gr;
        var memberRows = new List<AppRow>();
        foreach (var id in gr.Group.MemberIds)
            if (_rowsById.TryGetValue(id, out var r))
                memberRows.Add(r);
        GroupMembers.ItemsSource = memberRows;
        GroupMembers.SelectedIndex = memberRows.Count > 0 ? 0 : -1;
        foreach (var row in memberRows)
            row.ShowRemoveBadge = true;
        GroupNameBox.Text = gr.Group.Name;
        GroupOverlay.Visibility = Visibility.Visible;
    }

    private void CloseGroup(bool relaunchFilter = true)
    {
        if (_openGroup is not null && GroupMembers.ItemsSource is List<AppRow> members)
            foreach (var row in members)
                row.ShowRemoveBadge = false;
        _openGroup = null;
        GroupOverlay.Visibility = Visibility.Collapsed;
        if (relaunchFilter)
            ApplyFilter(true);
    }

    private void CommitGroupName()
    {
        if (_openGroup is null)
            return;
        var name = GroupNameBox.Text.Trim();
        if (name.Length > 0)
            _openGroup.Group.Name = name;
        _openGroup.NotifyNameChanged();
                        PersistGroups();
    }

    private void RebuildGroupRows()
    {
        _groupRows.Clear();
        foreach (var g in _groups)
            _groupRows.Add(new GroupRow(g));
        RefreshAllGroupPreviews();
        ApplyFilter(true);
    }

    private void RefreshAllGroupPreviews()
    {
        foreach (var gr in _groupRows)
        {
            var icons = new List<ImageSource?>();
            foreach (var id in gr.Group.MemberIds)
                icons.Add(_rowsById.TryGetValue(id, out var mr) ? mr.Icon : null);
            gr.SetPreviews(icons);
        }
    }

    private void PersistGroups()
    {
        _ = _groupStore.SaveAsync(_groups);
    }

    private void PersistLayout()
    {
        var ordered = _filtered.Select(TileId).ToList();
        _orderIds = ordered;
        _ = _layoutStore.SaveAsync(ordered);
    }

    private void ReorderTiles()
    {
        if (_reorderPreview is not null && _dragTile is not null)
        {
            _dragTile.ShowAsPlaceholder = false;
            _filtered = _reorderPreview.ToList();
        }
        _reorderPreview = null;
        _dragTargetIndex = -1;
        _dragTile = null;
        PersistLayout();
        RenderPage(0);
    }

    // Converts a pointer position inside the icon grid to an absolute index
    // in the full (filtered) tile list, so the dragged tile can be slotted in.
    private int ComputeDragTargetIndex(System.Windows.Point pos)
    {
        if (AppsList.ActualWidth < 100)
            return 0;
        var cellW = TileWidth + 16;
        var rowH = TileIconSize + 52;
        var cols = Math.Max(1, (int)(AppsList.ActualWidth / cellW));
        var usedW = cols * cellW;
        var leftOffset = Math.Max(0.0, (AppsList.ActualWidth - usedW) / 2);
        var col = (int)Math.Floor(((double)pos.X - leftOffset) / cellW);
        var row = (int)Math.Floor((double)pos.Y / rowH);
        col = Math.Max(0, Math.Min(cols - 1, col));
        row = Math.Max(0, row);
                var local = Math.Max(0, Math.Min(ComputePageSize() - 1, row * cols + col));
        return Math.Max(0, Math.Min(_filtered.Count - 1, _pageIndex * ComputePageSize() + local));
    }

    // Reorders the full list around where the cursor is and re-renders, with
    // the dragged tile drawn as an empty slot so the rest "flow around" it.
    private void BuildReorderPreview()
    {
        if (_dragTile is null)
            return;
        var idx = _filtered.IndexOf(_dragTile);
        if (idx < 0)
            return;
        _dragTile.ShowAsPlaceholder = true;
        var preview = _filtered.ToList();
        preview.RemoveAt(idx);
        preview.Insert(Math.Max(0, Math.Min(preview.Count, _dragTargetIndex)), _dragTile);
        _reorderPreview = preview;
        RenderPage(0);
    }

    // Cancels a live reorder preview (drop-onto-group / drop-create / cancel).
    private void ClearDragPreview()
    {
        if (_dragTile is not null)
            _dragTile.ShowAsPlaceholder = false;
        if (_reorderPreview is not null)
            foreach (var tile in _reorderPreview)
                if (tile is AppRow ar)
                    ar.ShowAsPlaceholder = false;
        _reorderPreview = null;
        _dragTargetIndex = -1;
    }

    // ── Badge buttons (clicks) ────────────────────────────────────
    private void OnRemoveFromGroup(object sender, RoutedEventArgs e)
    {
        if (FindRowFromSender(sender) is { } row)
            RemoveFromGroup(row);
    }

    private void OnRemoveGroupClick(object sender, RoutedEventArgs e)
    {
        if (FindGroupFromSender(sender) is { } gr)
        {
            _groups.Remove(gr.Group);
            _groupRows.Remove(gr);
            PersistGroups();
            ApplyFilter();
        }
    }

    private void OnUninstallClick(object sender, RoutedEventArgs e)
    {
        if (FindRowFromSender(sender) is { } row)
        {
            UninstallerService.LaunchUninstall(row.App);
            ExitJiggle();
        }
    }

    // ── Jiggle (uninstall) mode ───────────────────────────────────
    private void EnterJiggle()
    {
        _jiggleMode = true;
        _tileDragArmed = false;
        foreach (var row in _rows)
            row.ShowUninstallBadge = true;
        foreach (var gr in _groupRows)
            gr.ShowRemoveBadge = true;
        AnimateJiggle();
    }

    private void ExitJiggle()
    {
        _jiggleMode = false;
        foreach (var row in _rows)
            row.ShowUninstallBadge = false;
        foreach (var gr in _groupRows)
            gr.ShowRemoveBadge = false;
        AppsList.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private void AnimateJiggle()
    {
        AppsList.BeginAnimation(RotateTransform.AngleProperty, null);
        AppsList.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(500))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            });
    }

    private void LaunchAppsListDoubleClick(object sender, MouseButtonEventArgs e) => LaunchSelected();
    // ── Drag: mouse-move (ghost + swipe flip) ─────────────────────
    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (SettingsOverlay.Visibility == Visibility.Visible)
            return;

        // Movement cancels the jiggle long-press arm.
        var holdPos = e.GetPosition(this);
        if (_holdTimer is not null && (holdPos - _tileDragStart).Length > 8)
            StopHoldTimer();

        // Page flip by horizontal swipe on empty space (not while moving a tile).
        if (_dragTile is null && _dragOrigin is { } og && !_dragConsumed)
        {
            var pos = e.GetPosition(this);
            var dx = pos.X - og.X;
            if (Math.Abs(dx) > 60)
            {
                _dragConsumed = true;
                if (dx < 0)
                    NextPage();
                else
                    PreviousPage();
                return;
            }
        }

        // Tile drag: show the ghost once the pointer moves 14px, follow it,
        // and flip the page when dragged against an edge (macOS rearrange).
        if (_dragTile is not null)
        {
            var pos = e.GetPosition(this);
            if (!_tileDragArmed)
            {
                var dd = Math.Max(Math.Abs((double)(pos.X - _tileDragStart.X)),
                                  Math.Abs((double)(pos.Y - _tileDragStart.Y)));
                if (dd <= 14)
                    return;
                _tileDragArmed = true;
                _tileDragStart = pos;
            }
            if (DragGhost.Visibility != Visibility.Visible)
            {
                DragGhost.Visibility = Visibility.Visible;
                DragGhostImage.Source = _dragTile.Icon;
            }
            DragGhost.Margin = new Thickness(pos.X, pos.Y, 0, 0);
            e.Handled = true;

            // Live reorder: re-render the page so the other icons flow around
            // the placeholder cell that tracks the cursor (macOS behavior).
            // Drag-out of a group reorders nothing on the main grid.
            if (!_tileDragFromGroup && _openGroup is null)
            {
                var gridPos = e.GetPosition(AppsList);
                var target = ComputeDragTargetIndex(gridPos);
                if (target != _dragTargetIndex)
                {
                    _dragTargetIndex = target;
                    BuildReorderPreview();
                }
            }

            var edgePos = e.GetPosition(AppsList);
            if (edgePos.X < 24)
                PreviousPage();
            else if (edgePos.X > AppsList.ActualWidth - 24)
                NextPage();
        }
    }

    // ── Drag: mouse-up (group create / add / remove / drop) ───────
    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var swiped = _dragConsumed;
        _dragConsumed = false;
        _dragOrigin = null;
        StopHoldTimer();

        if (_tileDragArmed && _dragTile is not null)
        {
            var source = _dragTile;
            var fromGroup = _tileDragFromGroup;
            _tileDragArmed = false;
            _tileDragFromGroup = false;
            DragGhost.Visibility = Visibility.Collapsed;

            // Dragged out of an open group → remove that member.
            if (fromGroup && _openGroup is { } og && og.Group.MemberIds.Contains(source.App.Id))
            {
                ClearDragPreview();
                _dragTile = null;
                RemoveFromGroup(source);
                return;
            }

            var targetContainer = FindTileContainer(e.OriginalSource as DependencyObject);
            if (targetContainer?.DataContext is GroupRow targetGroup)
            {
                ClearDragPreview();
                _dragTile = null;
                AddToGroup(targetGroup, source);
            }
            else if (targetContainer?.DataContext is AppRow targetApp && targetApp != source)
            {
                ClearDragPreview();
                _dragTile = null;
                CreateGroup(source, targetApp);
            }
            else
                ReorderTiles(); // commits the live preview (and clears drag state)
            return;
        }

        if (swiped)
            return;

        // Plain click: launch/reopen (single click launches — macOS style).
        if (_jiggleMode || SettingsOverlay.Visibility == Visibility.Visible)
            return;
        if (IsWithinSearch(e.OriginalSource as DependencyObject))
            return;

        if (FindMemberContainer(e.OriginalSource as DependencyObject) is { } member
            && member.DataContext is AppRow groupApp)
        {
            AppLauncher.Launch(groupApp.App);
            Close();
            return;
        }

        if (FindTileContainer(e.OriginalSource as DependencyObject) is { } container)
        {
            if (container.DataContext is GroupRow g)
                OpenGroup(g);
            else if (container.DataContext is AppRow app)
            {
                AppLauncher.Launch(app.App);
                Close();
                return;
            }
        }

        // Click on empty area (outside tiles and search) → close the launcher.
        Close();
    }
    // ── Global hotkey: native polling (robust, no window hook) ────
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    private static bool KeyState(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private void StartHotKeyPoller()
    {
        _hotKeyTimer?.Stop();
        _hotKeyTimer = null;
        if (!ParseHotKey(App.Settings.HotKey, ref _hotKeyMods, ref _hotKeyVk))
            return;
        _hotKeyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _hotKeyTimer.Tick += (_, _) => CheckHotKey();
        _hotKeyTimer.Start();
    }

    private void CheckHotKey()
    {
        if (_hotKeyVk == 0 || App.OpenWindows.Count > 0)
            return;
        if (KeyState(_hotKeyVk) && HotKeyModifiersMatch())
            App.OpenLaunchpad();
    }

    private bool HotKeyModifiersMatch()
    {
        var wantCtrl = (_hotKeyMods & 0x0002) != 0;
        var wantAlt = (_hotKeyMods & 0x0001) != 0;
        var wantShift = (_hotKeyMods & 0x0004) != 0;
        var wantWin = (_hotKeyMods & 0x0008) != 0;
        return KeyState(VkControl) == wantCtrl
            && KeyState(VkAlt) == wantAlt
            && KeyState(VkShift) == wantShift
            && KeyState(VkWin) == wantWin;
    }

    // Parses "Ctrl+Alt+L"/"Alt+Space"... into Windows modal flags + VK.
    private static bool ParseHotKey(string spec, ref int mods, ref int vk)
    {
        mods = 0;
        vk = 0;
        foreach (var part in spec.Split('+'))
        {
            var p = part.Trim().ToLowerInvariant();
            switch (p)
            {
                case "ctrl": mods |= 0x0002; break;
                case "alt": mods |= 0x0001; break;
                case "shift": mods |= 0x0004; break;
                case "win": mods |= 0x0008; break;
                case "space": vk = 0x20; break;
                default:
                    if (p.Length == 1 && char.IsLetterOrDigit(p[0]))
                        vk = char.ToUpperInvariant(p[0]);
                    break;
            }
        }
        return mods != 0 && vk != 0;
    }

    // ── Settings panel handlers ───────────────────────────────────
    private void OnHotCornerToggled(object sender, RoutedEventArgs e)
    {
        if (_applyingSettings)
            return;
        App.Settings.HotCornerEnabled = HotCornerToggle.IsChecked == true;
        CommitSettings();
    }

    private void OnCornerClick(object sender, RoutedEventArgs e)
    {
        var tag = (sender as System.Windows.Controls.Button)?.Tag as string;
        App.Settings.HotCorner = tag switch
        {
            "TopLeft" => AppHotCorner.TopLeft,
            "TopRight" => AppHotCorner.TopRight,
            "BottomLeft" => AppHotCorner.BottomLeft,
            "BottomRight" => AppHotCorner.BottomRight,
            _ => App.Settings.HotCorner
        };
        CommitSettings();
    }

    private void OnTileScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_applyingSettings)
            return;
        App.Settings.TileScale = e.NewValue / 100.0;
        ApplySettings(App.Settings);
        UpdateGridSizeLabel(e.NewValue / 100.0);
        CommitSettings();
    }

    private void OnSliderLoaded(object sender, RoutedEventArgs e)
    {
        UpdateGridSizeLabel(App.Settings.TileScale);
    }

    private void UpdateGridSizeLabel(double scale)
    {
        if (GridSizeLabel is null)
            return;
        var tileW = Math.Round(168 * scale);
        var tileH = Math.Round(112 * scale);
        var cols = Math.Max(1, (int)((ActualWidth - 80) / (tileW + 16)));
        var rows = Math.Max(1, (int)((ActualHeight - 200) / (tileH + 52)));
        GridSizeLabel.Text = $"{cols} × {rows}";
    }

    private void OnOpenAppFolder(object sender, RoutedEventArgs e)
    {
        // ContextMenu is not in the visual tree, so resolve the row via PlacementTarget.
        var menuItem = sender as MenuItem;
        var contextMenu = menuItem?.Parent as ContextMenu;
        var target = contextMenu?.PlacementTarget as FrameworkElement;
        if (target?.DataContext is AppRow row)
        {
            var path = row.App.TargetPath;
            if (row.App.Kind == AppKind.Uwp)
                return;
            var folder = Path.GetDirectoryName(path);
            if (folder is not null && Directory.Exists(folder))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"select,\"{path}\"") { UseShellExecute = true });
                Close(); // close the launcher so the folder is frontmost
            }
        }
    }

    private string _pendingHotKey = "";

    private void OnHotKeyBoxKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;
        var keyLabel = HotKeyLabel(e.Key);
        if (keyLabel.Length == 0)
            return;
        _pendingHotKey = (KeyState(VkControl) ? "Ctrl+" : "") + (KeyState(VkAlt) ? "Alt+" : "") + keyLabel;
        HotKeyBox.Text = _pendingHotKey;
    }

    private void OnHotKeyBoxCommit(object sender, RoutedEventArgs e)
    {
        if (_pendingHotKey.Length > 0)
        {
            App.Settings.HotKey = _pendingHotKey;
            StartHotKeyPoller();
        }
        CommitSettings();
    }

    private void OnCloseSettings(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
        if (_openGroup is not null)
            CloseGroup(false);
        ApplyFilter(true);
    }

    private void CommitSettings() => App.SaveSettings(App.Settings);

    private static string HotKeyLabel(Key key) => key switch
    {
        Key.Enter => "Enter",
        Key.Space => "Space",
        _ when key.ToString().Length == 1 => key.ToString(),
        _ => ""
    };

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // First cancel an in-flight drag; then step out of the overlay stack.
            if (_tileDragArmed || _dragTile is not null)
            {
                StopHoldTimer();
                ClearDragPreview();
                _tileDragArmed = false;
                _tileDragFromGroup = false;
                _dragTile = null;
                DragGhost.Visibility = Visibility.Collapsed;
            }
            else if (SettingsOverlay.Visibility == Visibility.Visible)
                OnCloseSettings(sender, e);
            else if (_openGroup is not null)
                CloseGroup();
            else if (SearchBox.Text.Length > 0)
                SearchBox.Text = "";
            else
                Close();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter)
        {
            LaunchSelected();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.PageDown)
        {
            if (_jiggleMode)
                ExitJiggle();
            else
                NextPage();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.PageUp)
        {
            if (_jiggleMode)
                ExitJiggle();
            else
                PreviousPage();
            e.Handled = true;
            return;
        }
        if ((e.Key == Key.Left || e.Key == Key.Right) && SearchBox.Text.Length == 0)
        {
            if (e.Key == Key.Left)
                PreviousPage();
            else
                NextPage();
            e.Handled = true;
        }
    }

    // ── Icon decoding (async) ────────────────────────────────────
    private async Task ExtractMissingIconsAsync()
    {
        // Snapshot: a background refresh may replace _rows mid-run.
        foreach (var row in _rows.ToList())
        {
            if (row.Icon is not null)
                continue;
            var path = await _icons.ExtractAndCacheAsync(row.App);
            if (path is null)
                continue;
                        var source = new BitmapImage();
            source.BeginInit();
            source.UriSource = new Uri("file:///" + path.Replace("\\", "/"));
            source.DecodePixelWidth = 128;
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.EndInit();
            _iconMemoryCache[row.App.Id] = source;
            if (_rowsById.TryGetValue(row.App.Id, out var current))
                current.Icon = source;
        }
        Dispatcher.Invoke(() => RefreshAllGroupPreviews());
    }

    // ── Watcher deltas (shortcut install/uninstall) ──────────────
    private async Task OnWatcherUpserted(IReadOnlyList<AppItem> items)
    {
        foreach (var app in items)
        {
            if (_rowsById.TryGetValue(app.Id, out var existing))
                existing.Update(app);
            else
            {
                var row = new AppRow(app);
                _rows.Add(row);
                _rowsById[app.Id] = row;
            }
        }
        Dispatcher.Invoke(() => RefreshAllGroupPreviews());
        _ = ExtractMissingIconsAsync();
    }

    private async Task OnWatcherRemoved(IReadOnlyList<string> ids)
    {
        foreach (var id in ids)
        {
            if (_rowsById.TryGetValue(id, out var row))
            {
                _rows.Remove(row);
                _rowsById.Remove(id);
            }
        }
        Dispatcher.Invoke(() => RefreshAllGroupPreviews());
    }
}
