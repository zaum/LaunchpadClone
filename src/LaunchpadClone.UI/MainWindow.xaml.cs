using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    private CancellationTokenSource? _iconCts;

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
    private GroupRow? _dragGroupTile;
    private bool _tileDragArmed;
    private bool _tileDragFromGroup;
    private System.Windows.Point _tileDragStart;

    // Live reorder preview: a reordered full list + where the dragged tile
    // would land, so a drag re-renders the page with "others flow around".
    private List<ITileRow>? _reorderPreview;
    private int _dragTargetIndex = -1;
    private long _lastReorderMs;

    // Hover-to-group: when the ghost rests over another tile, a timer folds
    // them into a group without needing a drop (macOS hover-create).
    private DispatcherTimer? _hoverGroupTimer;
    private ITileRow? _hoverGroupTarget;
    // The tile currently pressed by the hovering ghost (drop feedback).
    private ListBoxItem? _hoverHighlightContainer;
    // Hover-to-group delay — matches macOS Launchpad's ~0.5 s hold before
    // the target tile "compresses" and the folder forms on release.
    private const double HoverGroupDelayMs = 500;

    // Jiggle (uninstall) mode + the long-press that arms it.
    private bool _jiggleMode;
    private DispatcherTimer? _holdTimer;

    // True once dismissal started — Close() while closing throws
    // InvalidOperationException, and Deactivated fires mid-close (focus moves
    // away as the window tears down), so every dismissal goes through
    // Dismiss() exactly once.
    private bool _dismissing;
    // Set when a menu action closes the launcher: the closing ContextMenu
    // popup mirrors one last MouseLeftButtonUp onto the tile below, which
    // would otherwise launch the app right after "Open folder".
    private bool _ignoreNextClick;

    /// <summary>Closes the overlay once — re-entrant calls are ignored.</summary>
    private void Dismiss()
    {
        if (_dismissing)
            return;
        _dismissing = true;
        Close();
    }

    // Global hotkey ("open" shortcut from settings) — polled via user32.
    private DispatcherTimer? _hotKeyTimer;
    private int _hotKeyVk;
    private int _hotKeyMods;
    private const int VkControl = 0x11;
    private const int VkAlt = 0x12;
    private const int VkShift = 0x10;
    private const int VkWinLeft = 0x5B;
    private const int VkWinRight = 0x5C;

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
        // Single click launches (macOS style) via OnMouseLeftButtonUp —
        // no DoubleClick handler on purpose: it would fire a second launch.
        AppsList.SizeChanged += (_, _) => RecomputeLayout();
        // The folder name commits ONLY on Enter — clicking away just returns
        // to the grid view without renaming (macOS-like explicit commit).
        GroupNameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitGroupName();
                CloseGroup();
                e.Handled = true;
            }
        };

        SearchBox.TextChanged += (_, _) => ApplyFilter();
        Loaded += OnLoaded;
        Closed += (_, _) => App.SettingsChanged -= OnAppSettingsChanged;
        // Alt+Tab away (or any focus loss) dismisses the overlay like a click
        // outside would — the window is fullscreen Topmost, so "outside"
        // clicks land on empty area, but focus loss needs this handler.
        Deactivated += (_, _) => Dismiss();

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

    // After the window becomes active (first show, hot-corner, hotkey), the
    // search box takes the keyboard — typing starts filtering immediately.
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        }), DispatcherPriority.Input);
    }

    protected override void OnClosed(EventArgs e)
    {
        _hotKeyTimer?.Stop();
        _hotKeyTimer = null;
        _holdTimer?.Stop();
        _iconCts?.Cancel();
        _iconCts?.Dispose();
        _iconCts = null;
        base.OnClosed(e);
    }

    // Live grid size (settings): the templates bind to these.
    // Defaults are compact (macOS-like density); the slider scales them.
    private double _tileWidth = 134;
    public double TileWidth
    {
        get => _tileWidth;
        private set => SetField(ref _tileWidth, value);
    }

    private double _tileIconSize = 84;
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
        TileWidth = Math.Round(134 * scale);
        TileIconSize = Math.Round(84 * scale);
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
        // Focus the search box AFTER the first layout pass: a plain Focus()
        // during Loaded is too early for a freshly-shown Topmost window and
        // silently loses to window activation.
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        }), DispatcherPriority.Input);

        try
        {
            // Order, groups and cache are independent files — load them
            // concurrently so the first paint waits only for the slowest.
            var orderTask = _layoutStore.LoadAsync();
            var groupsTask = _groupStore.LoadAsync();
            var cacheTask = _cache.LoadAsync();
            await Task.WhenAll(orderTask, groupsTask, cacheTask);
            _orderIds = await orderTask;
            _groups = await groupsTask;
            RebuildGroupRows();

            var cached = await cacheTask;
            if (cached.Count > 0)
            {
                RenderApps(cached);
                StatusText.Text = $"{cached.Count} apps";
                _ = ExtractVisibleIconsAsync(); // only current page, not all
            }

            _watcher.AppsUpserted += OnWatcherUpserted;
            _watcher.AppsRemoved += OnWatcherRemoved;
            _watcher.Start();

            // Skip the background rescan if the cache is fresh (written < 1 min ago).
            // The 10-minute periodic timer will pick up any changes later.
            if (_cache.IsFresh(TimeSpan.FromMinutes(1)))
            {
                StatusText.Text = $"{cached.Count} apps (cached)";
            }
            else
            {
                // Full rescan runs in the background so the window paints instantly
                // from cache. Quiet when cache was shown (no "Scanning..." flash).
                _ = RefreshAppsAsync(quiet: cached.Count > 0);
            }

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
            // Live counter: each discovery source reports as it finishes.
            var seen = 0;
            var progress = new Progress<ScanProgress>(p =>
            {
                seen += p.Count;
                StatusText.Text = $"Loading... {seen} apps ({p.Stage})";
            });
            var apps = await _discovery.ScanAllAsync(CancellationToken.None, progress).ConfigureAwait(true);
            _allApps = apps.OrderBy(
                a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
            await _cache.SaveAsync(_allApps).ConfigureAwait(true);
            RenderApps(_allApps);
            StatusText.Text = $"{_allApps.Count} apps";
            _ = ExtractVisibleIconsAsync();
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
            // Instant startup: a cached icon on disk is decoded right away so
            // tiles never paint empty while async extraction warms up.
            else if (app.IconCachePath is { Length: > 0 } p && File.Exists(p)
                     && TryLoadCachedIcon(p) is { } cached)
            {
                lock (_iconLock)
                    _iconMemoryCache[app.Id] = cached;
                row.Icon = cached;
            }
            _rows.Add(row);
            _rowsById[app.Id] = row;
        }
        RefreshAllGroupPreviews();
        ApplyFilter();
    }

    // Decodes a cached icon PNG off the UI thread's critical path; a corrupt
    // or deleted file just yields no icon (extraction will rebuild it).
    private static ImageSource? TryLoadCachedIcon(string path)
    {
        try
        {
            var source = new BitmapImage();
            source.BeginInit();
            source.UriSource = new Uri("file:///" + path.Replace("\\", "/"));
            source.DecodePixelWidth = 96;
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.EndInit();
            source.Freeze();
            return source;
        }
        catch
        {
            return null;
        }
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
        AppsList.SelectedIndex = -1;

        UpdatePageDots(pageCount);
        // While a background rescan runs, RefreshAppsAsync owns the status
        // line ("Scanning...") — do not overwrite it from every RenderPage.
        if (!_refreshing)
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
            var brush = new SolidColorBrush(i == _pageIndex
                ? System.Windows.Media.Color.FromRgb(0x6C, 0x8C, 0xFF)
                : System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF));
            brush.Freeze();
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = i == _pageIndex ? 9 : 7,
                Height = 7,
                Margin = new Thickness(3, 0, 3, 0),
                Fill = brush
            };
            PageDots.Children.Add(dot);
        }
    }

    // Page flip glides like moving within one wide surface: a soft
    // horizontal slide with a fade, the new page drifting in from the side.
    private void AnimatePage(int direction)
    {
        var travel = Math.Min(360, Math.Max(160, AppsList.ActualWidth * 0.18));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        PageSlide.BeginAnimation(TranslateTransform.XProperty, null);
        PageSlide.X = direction * travel;
        PageSlide.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });

        AppsList.BeginAnimation(OpacityProperty, null);
        AppsList.Opacity = 0.4;
        AppsList.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
    }

    // Tile cell footprint must match the template (tile width + 8px side
    // padding horizontally; icon + label + 12px vertical padding).
    // Single source of truth for every page-size question: RecomputeLayout,
    // ComputePageSize, drag targeting and the settings preview all flow
    // through GridDimsFor so they can never disagree again.
    private double GridCellWidth => TileWidth + 16;
    private double GridCellHeight => TileIconSize + 52;

    private static (int Cols, int Rows) GridDimsFor(double width, double height, double cellW, double cellH)
    {
        var cols = Math.Max(1, (int)(width / cellW));
        var rows = Math.Max(1, (int)(height / cellH));
        return (cols, rows);
    }

    private void RecomputeLayout()
    {
        if (AppsList is null)
            return;
        var width = AppsList.ActualWidth;
        var height = AppsList.ActualHeight;
        if (width < 100 || height < 100)
            return;

        var (cols, rows) = GridDimsFor(width, height, GridCellWidth, GridCellHeight);
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
        _ = ExtractVisibleIconsAsync();
    }

    private void PreviousPage()
    {
        if (_pageIndex <= 0)
            return;
        _pageIndex--;
        RenderPage(-1);
        _ = ExtractVisibleIconsAsync();
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
    // Unified with RecomputeLayout: measured AppsList size wins, otherwise
    // fall back to the window size minus the known chrome (search + status).
    private int ComputePageSize()
    {
        if (AppsList is not null && AppsList.ActualWidth >= 100 && AppsList.ActualHeight >= 100)
        {
            var (cols, rows) = GridDimsFor(AppsList.ActualWidth, AppsList.ActualHeight, GridCellWidth, GridCellHeight);
            return Math.Max(1, cols * rows);
        }
        var fallbackW = Math.Max(100, ActualWidth - 80); // AppsList side margins
        var fallbackH = Math.Max(100, ActualHeight - 160); // search + status rows
        var (fcols, frows) = GridDimsFor(fallbackW, fallbackH, GridCellWidth, GridCellHeight);
        return Math.Max(1, fcols * frows);
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

    // ── Gesture / positioning helpers ─────────────────────────────
    private bool IsWithinSearch(DependencyObject? source)
    {
        for (var node = source; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
            if (node == SearchBox || node == ClearSearchButton)
                return true;
        return false;
    }

    /// <summary>
    /// Checks whether the click landed on the settings gear button.
    /// </summary>
    private bool IsWithinSettingsGear(DependencyObject? source)
    {
        for (var node = source; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
            if (node == SettingsGearButton)
                return true;
        return false;
    }

    private bool IsWithinSettingsPanel(DependencyObject? source)
    {
        for (var node = source; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
            if (node == SettingsOverlay)
                return true;
        return false;
    }

    /// <summary>
    /// Checks whether the click landed on an interactive control (settings gear,
    /// page dots, context menus, scrollbars, etc.) so we don't pre-emptively
    /// close the launcher before the control's own click handler fires.
    /// </summary>
    private static bool IsWithinControl(DependencyObject? source)
    {
        for (var node = source; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Button || node is System.Windows.Controls.MenuItem
                || node is System.Windows.Controls.Slider
                || node is System.Windows.Controls.CheckBox
                || node is System.Windows.Controls.TextBox
                || node is System.Windows.Controls.ContextMenu
                || node is System.Windows.Controls.Primitives.ScrollBar
                || node is System.Windows.Controls.Primitives.RepeatButton)
                return true;
        }
        return false;
    }

    private void OnClearSearch(object sender, MouseButtonEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
    }

    // Walks up the click source to the list-item that carries the tile.
    // Uses VisualHelper so non-Control elements (Grid, Image, TextBlock)
    // in the DataTemplate do not break the tree walk.
    private static ListBoxItem? FindTileContainer(DependencyObject? source)
    {
        for (var node = source; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
            if (node is ListBoxItem item)
                return item;
        return null;
    }

    private ListBoxItem? FindMemberContainer(DependencyObject? source)
    {
        // Same walk, but only within the group members list.
        for (var node = source; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
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

        // Skip the settings gear button — let its Click handler open the overlay.
        if (IsWithinSettingsGear(e.OriginalSource as DependencyObject))
            return;

        // Let interactive controls (settings gear, buttons, etc.) handle their
        // own clicks — don't start a hold timer or drag tracking for them.
        if (IsWithinControl(e.OriginalSource as DependencyObject))
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

        // Any tile (app OR folder) can be dragged: apps create/enter groups,
        // folders reorder like any other tile (macOS Launchpad behavior).
        if (FindTileContainer(e.OriginalSource as DependencyObject) is { } container)
        {
            if (container.DataContext is AppRow tile)
            {
                _dragTile = tile;
                _tileDragArmed = false;
                _tileDragFromGroup = false;
            }
            else if (container.DataContext is GroupRow groupTile)
            {
                _dragGroupTile = groupTile;
                _tileDragArmed = false;
                _tileDragFromGroup = false;
            }
        }
    }

    private AppRow? FindRowFromSender(object sender)
    {
        for (var node = sender as DependencyObject; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
            if (node is ListBoxItem item && item.DataContext is AppRow r)
                return r;
        return null;
    }

    private GroupRow? FindGroupFromSender(object sender)
    {
        for (var node = sender as DependencyObject; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
            if (node is ListBoxItem item && item.DataContext is GroupRow g)
                return g;
        return null;
    }

    // ── Tile drag (custom mouse ghost + placeholder): creates groups,
    // adds to group, reorders, drag-out. NOTE: there is intentionally no
    // Ole DragDrop here (no DoDragDrop / OnDrop) — the ghost follows the
    // cursor in OnMouseMove and the drop lands in OnMouseLeftButtonUp.
    private void CreateGroup(AppRow a, AppRow b)
    {
        var group = new AppGroup
        {
            Name = a.DisplayName + " & " + b.DisplayName,
            MemberIds = [a.App.Id, b.App.Id]
        };
        _groups.Add(group);
        // macOS behavior: the folder appears where the TARGET tile was (b —
        // the icon you hovered/dropped onto), not where the dragged icon
        // came from. Slot "g:<id>" into the saved order at the target's
        // position and drop the two member ids from it.
        var anchorIndex = _orderIds.IndexOf(b.App.Id);
        if (anchorIndex < 0)
            anchorIndex = _orderIds.IndexOf(a.App.Id);
        if (anchorIndex < 0)
            anchorIndex = _filtered.IndexOf(b);
        if (anchorIndex < 0)
            anchorIndex = _orderIds.Count; // fallback: end
        _orderIds.Remove(a.App.Id);
        _orderIds.Remove(b.App.Id);
        _orderIds.Insert(Math.Min(anchorIndex, _orderIds.Count), "g:" + group.Id);
        _ = _layoutStore.SaveAsync(_orderIds);
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
        // The member returns next to the group tile (macOS drag-out).
        var idx = _orderIds.IndexOf("g:" + _openGroup.Group.Id);
        if (idx >= 0)
            _orderIds.Insert(Math.Min(idx + 1, _orderIds.Count), member.App.Id);
        else
            _orderIds.Add(member.App.Id);
        _ = _layoutStore.SaveAsync(_orderIds);
        PersistGroups();
        if (_openGroup.Group.MemberIds.Count == 0)
        {
            // Empty group self-deletes; its remaining members return at the
            // group's spot (only one member remains here, already inserted).
            _orderIds.Remove("g:" + _openGroup.Group.Id);
            _ = _layoutStore.SaveAsync(_orderIds);
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

    // ── Launching ─────────────────────────────────────────────────
    // Enter / single-click. Groups open instead of launch.
    // Guarded: a fast double-click must not start the app twice.
    private string? _lastLaunchedId;
    private DateTime _lastLaunchUtc = DateTime.MinValue;

    private bool TryLaunch(AppRow row)
    {
        var now = DateTime.UtcNow;
        if (row.App.Id == _lastLaunchedId && (now - _lastLaunchUtc) < TimeSpan.FromSeconds(1.5))
            return false;
        _lastLaunchedId = row.App.Id;
        _lastLaunchUtc = now;
        Dismiss(); // overlay dismisses once the app opens (macOS behavior)
        AppLauncher.Launch(row.App);
        return true;
    }

    // Enter launches the first visible tile (selection itself is never shown).
    private ITileRow? FirstVisibleTile()
    {
        if (_openGroup is not null && GroupMembers.ItemsSource is System.Collections.IEnumerable members)
            foreach (var m in members)
                if (m is ITileRow t)
                    return t;
        return _filtered.FirstOrDefault(t => IsOnCurrentPage(t));
    }

    private bool IsOnCurrentPage(ITileRow tile)
    {
        if (AppsList.ItemsSource is System.Collections.IEnumerable items)
            foreach (var i in items)
                if (ReferenceEquals(i, tile))
                    return true;
        return false;
    }

    private void LaunchSelected()
    {
        var selected = FirstVisibleTile();
        if (selected is GroupRow groupRow)
        {
            OpenGroup(groupRow);
            return;
        }
        if (selected is AppRow row)
        {
            TryLaunch(row);
        }
    }

    // ── Group (folder) operations ─────────────────────────────────
    // macOS behavior: the backdrop stays blurred (an extra dim fades in),
    // the folder is a lighter rounded card that zooms open with a slight
    // overshoot while its member icons slide up into place.
    private void OpenGroup(GroupRow gr)
    {
        var reopen = ReferenceEquals(_openGroup, gr)
            && GroupOverlay.Visibility == Visibility.Visible;
        _openGroup = gr;
        var memberRows = new List<AppRow>();
        foreach (var id in gr.Group.MemberIds)
            if (_rowsById.TryGetValue(id, out var r))
                memberRows.Add(r);
        GroupMembers.ItemsSource = memberRows;
        GroupMembers.SelectedIndex = -1;
        foreach (var row in memberRows)
            row.ShowRemoveBadge = true;
        GroupNameBox.Text = gr.Group.Name;
        // macOS behavior: the folder panel pops up AT the folder tile's spot.
        if (!reopen)
            PositionGroupCard(gr);
        if (reopen)
            return; // content refresh only (add/remove member) — no re-zoom
        GroupOverlay.Opacity = 0;
        GroupOverlay.Visibility = Visibility.Visible;
        AnimateGroupOpen();
        // The name field is editable right away (macOS: rename inline in the
        // open folder), committed on Enter.
        Dispatcher.BeginInvoke(new Action(() => Keyboard.Focus(GroupNameBox)),
            DispatcherPriority.Input);
    }

    // Places the folder card near its tile: measured tile center in window
    // coordinates, clamped so the card (measured after layout) stays on screen.
    private void PositionGroupCard(GroupRow gr)
    {
        GroupCard.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var cardW = Math.Min(GroupCard.MaxWidth, GroupCard.DesiredSize.Width);
        var cardH = GroupCard.DesiredSize.Height;
        if (cardW <= 0 || cardH <= 0)
            return;

        double x, y;
        if (AppsList.ItemContainerGenerator.ContainerFromItem(gr) is ListBoxItem c)
        {
            var p = c.TranslatePoint(
                new System.Windows.Point(c.ActualWidth / 2, c.ActualHeight / 2), this);
            x = p.X - cardW / 2;
            y = p.Y - cardH / 2;
        }
        else
        {
            // Tile not on the current page (e.g. opened right after creation
            // from a different page): fall back to the window center.
            x = (ActualWidth - cardW) / 2;
            y = (ActualHeight - cardH) / 2;
        }

        x = Math.Max(24, Math.Min(ActualWidth - cardW - 24, x));
        y = Math.Max(24, Math.Min(ActualHeight - cardH - 24, y));
        GroupCard.Margin = new Thickness(x, y, 0, 0);
    }

    private void CloseGroup(bool relaunchFilter = true)
    {
        if (_openGroup is not null && GroupMembers.ItemsSource is List<AppRow> members)
            foreach (var row in members)
                row.ShowRemoveBadge = false;
        _openGroup = null;
        if (GroupOverlay.Visibility == Visibility.Visible)
            AnimateGroupClose();
        if (relaunchFilter)
            ApplyFilter(true);
    }

    private void AnimateGroupOpen()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var spring = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };

        // Dim layer behind the card fades in (the blur itself is the window's).
        ScrimDim.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });

        GroupOverlay.BeginAnimation(OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });

        GroupCardScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(320)) { EasingFunction = spring });
        GroupCardScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(320)) { EasingFunction = spring });

        GroupMembersSlide.BeginAnimation(TranslateTransform.YProperty, null);
        GroupMembersSlide.Y = 28;
        GroupMembersSlide.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(360)) { EasingFunction = ease });
    }

    private void AnimateGroupClose()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        ScrimDim.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });

        GroupCardScale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.92, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });
        GroupCardScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.92, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease });

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            GroupOverlay.Visibility = Visibility.Collapsed;
            GroupOverlay.BeginAnimation(OpacityProperty, null);
            GroupOverlay.Opacity = 1;
            GroupCardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            GroupCardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            GroupCardScale.ScaleX = 0.86;
            GroupCardScale.ScaleY = 0.86;
        };
        GroupOverlay.BeginAnimation(OpacityProperty, fade);
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
        if (_reorderPreview is not null && AnyDragTile is not null)
        {
            if (_dragTile is not null)
                _dragTile.ShowAsPlaceholder = false;
            _filtered = _reorderPreview.ToList();
        }
        _reorderPreview = null;
        _dragTargetIndex = -1;
        _dragTile = null;
        _dragGroupTile = null;
        PersistLayout();
        RenderPage(0);
    }

    // Converts a pointer position inside the icon grid to an absolute index
    // in the full (filtered) tile list, so the dragged tile can be slotted in.
    private int ComputeDragTargetIndex(System.Windows.Point pos)
    {
        if (AppsList.ActualWidth < 100)
            return 0;
        var cellW = GridCellWidth;
        var rowH = GridCellHeight;
        var cols = Math.Max(1, (int)(AppsList.ActualWidth / cellW));
        var usedW = cols * cellW;
        var leftOffset = Math.Max(0.0, (AppsList.ActualWidth - usedW) / 2);
        var col = (int)Math.Floor(((double)pos.X - leftOffset) / cellW);
        var row = (int)Math.Floor((double)pos.Y / rowH);
        col = Math.Max(0, Math.Min(cols - 1, col));
        row = Math.Max(0, row);
        var pageSize = Math.Max(1, _pageSize);
        var local = Math.Max(0, Math.Min(pageSize - 1, row * cols + col));
        return Math.Max(0, Math.Min(_filtered.Count - 1, _pageIndex * pageSize + local));
    }

    // Reorders the full list around where the cursor is and re-renders, with
    // the dragged tile drawn as an empty slot so the rest "flow around" it.
    private void BuildReorderPreview()
    {
        if (AnyDragTile is not { } dragged)
            return;
        var idx = _filtered.IndexOf(dragged);
        if (idx < 0)
            return;
        if (dragged is AppRow appRow)
            appRow.ShowAsPlaceholder = true;
        var preview = _filtered.ToList();
        preview.RemoveAt(idx);
        preview.Insert(Math.Max(0, Math.Min(preview.Count, _dragTargetIndex)), dragged);
        _reorderPreview = preview;
        RenderPageGlide();
    }

    // Soft rearrange: FLIP-glide every visible tile from its old position
    // to its new one instead of snapping (macOS Launchpad flow-around).
    // First: record centers, Last: re-render, Invert: offset back, Play:
    // animate the offsets to zero so tiles drift to their new slots.
    // Throttled to ~60fps steps: overlapping glides are killed first so a
    // fast drag cannot stack animations (that stacking was the stutter).
    private void RenderPageGlide()
    {
        foreach (var item in AppsList.Items)
            if (AppsList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem old)
                old.RenderTransform = null; // kill any still-running glide
        var before = new Dictionary<object, System.Windows.Point>();
        foreach (var item in AppsList.Items)
        {
            if (AppsList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem c)
            {
                try { before[item] = c.TranslatePoint(new System.Windows.Point(c.ActualWidth / 2, c.ActualHeight / 2), AppsList); }
                catch { /* container not yet laid out */ }
            }
        }
        RenderPage(0);
        AppsList.UpdateLayout();
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        foreach (var item in AppsList.Items)
        {
            if (!before.TryGetValue(item, out var oldCenter))
                continue;
            if (AppsList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem c)
                continue;
            System.Windows.Point newCenter;
            try { newCenter = c.TranslatePoint(new System.Windows.Point(c.ActualWidth / 2, c.ActualHeight / 2), AppsList); }
            catch { continue; }
            var dx = oldCenter.X - newCenter.X;
            var dy = oldCenter.Y - newCenter.Y;
            if (Math.Abs(dx) < 1 && Math.Abs(dy) < 1)
                continue;
            var slide = new System.Windows.Media.TranslateTransform(dx, dy);
            c.RenderTransform = slide;
            // Slightly longer than the 50ms reorder throttle so the motion
            // chains into a continuous, fluid slide instead of staccato hops.
            var animX = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
            var animY = new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
            slide.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, animX);
            slide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, animY);
        }
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

    // Drops every piece of drag state — used by Esc-cancel and mouse-up paths.
    private void ResetDragState()
    {
        ClearDragPreview();
        _dragTile = null;
        _dragGroupTile = null;
        _tileDragArmed = false;
        _tileDragFromGroup = false;
        DragGhost.Visibility = Visibility.Collapsed;
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
            // Members return to the group's old position (macOS un-group).
            var idx = _orderIds.IndexOf("g:" + gr.Group.Id);
            _orderIds.RemoveAt(idx < 0 ? _orderIds.Count : idx);
            var insertAt = idx < 0 ? _orderIds.Count : idx;
            foreach (var id in gr.Group.MemberIds)
                _orderIds.Insert(Math.Min(insertAt++, _orderIds.Count), id);
            _ = _layoutStore.SaveAsync(_orderIds);
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
    // Badge visibility IS the mode — no tile rotation animation: AppsList
    // carries a TranslateTransform (PageSlide), so animating
    // RotateTransform.AngleProperty on it was a silent no-op.
    private void EnterJiggle()
    {
        _jiggleMode = true;
        _tileDragArmed = false;
        foreach (var row in _rows)
            row.ShowUninstallBadge = true;
        foreach (var gr in _groupRows)
            gr.ShowRemoveBadge = true;
    }

    private void ExitJiggle()
    {
        _jiggleMode = false;
        foreach (var row in _rows)
            row.ShowUninstallBadge = false;
        foreach (var gr in _groupRows)
            gr.ShowRemoveBadge = false;
    }

    // ── Drag: mouse-move (ghost + swipe flip) ─────────────────────
    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (IsWithinSettingsPanel(e.OriginalSource as DependencyObject))
            return;

        if (SettingsOverlay.Visibility == Visibility.Visible)
            return;

        // Movement cancels the jiggle long-press arm.
        var holdPos = e.GetPosition(this);
        if (_holdTimer is not null && (holdPos - _tileDragStart).Length > 8)
            StopHoldTimer();

        // Page flip by horizontal swipe on empty space (not while moving a tile).
        if (_dragTile is null && _dragGroupTile is null && _dragOrigin is { } og && !_dragConsumed)
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
        if (AnyDragTile is not null)
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
                DragGhostImage.Source = _dragTile?.Icon ?? MakeGroupGhostImage(_dragGroupTile);
                DragGhost.Visibility = Visibility.Visible;
                DragGhost.UpdateLayout(); // ActualWidth/Height needed below
                // macOS-style pickup pop: the ghost springs up from 70%.
                // Setting the start value BEFORE BeginAnimation keeps the
                // transform identity at frame zero (no visible stall).
                DragGhostScale.ScaleX = 0.7;
                DragGhostScale.ScaleY = 0.7;
                var pop = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 };
                DragGhostScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                    new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { EasingFunction = pop });
                DragGhostScale.BeginAnimation(ScaleTransform.ScaleYProperty,
                    new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { EasingFunction = pop });
            }
            // Center the ghost under the cursor via its TranslateTransform:
            // render-only, so the per-frame move never triggers a layout pass
            // (the old Margin assignment was the drag jitter source).
            DragGhostSlide.X = pos.X - DragGhost.ActualWidth / 2;
            DragGhostSlide.Y = pos.Y - DragGhost.ActualHeight / 2;
            e.Handled = true;

            // Live reorder: re-render the page so the other icons flow around
            // the placeholder cell that tracks the cursor (macOS behavior).
            // Drag-out of a group reorders nothing on the main grid.
            // While hovering a tile center the reorder preview is frozen so
            // the target stops jumping away from the cursor (group intent).
            if (!_tileDragFromGroup && _openGroup is null)
            {
                var gridPos = e.GetPosition(AppsList);
            // Per-move throttle: the mouse fires ~100+ moves/sec, but a
            // re-render + FLIP pass costs milliseconds — without throttling
            // the queue saturates and the drag feels laggy (input backlog).
            // Hovering a tile center freezes the preview anyway (group aim).
            // The placeholder only replaces the dragged tile once the cursor
            // actually leaves its home cell — before that, the grid jumping
            // while the icon is still picked up felt like a hard stutter.
            if (!IsHoveringTileCenter(gridPos))
            {
                var target = ComputeDragTargetIndex(gridPos);
                var now = Environment.TickCount64;
                var home = _filtered.IndexOf(AnyDragTile);
                if (target != _dragTargetIndex && target != home && now - _lastReorderMs >= 50)
                {
                    _lastReorderMs = now;
                    _dragTargetIndex = target;
                    BuildReorderPreview();
                }
            }
                // Hover-to-group: resting the ghost on another tile for a
                // moment folds them into a group (no drop needed).
                if (_dragTile is not null)
                    UpdateHoverGroupTimer(gridPos);
            }

            var edgePos = e.GetPosition(AppsList);
            if (edgePos.X < 24)
                PreviousPage();
            else if (edgePos.X > AppsList.ActualWidth - 24)
                NextPage();
        }
    }

    // True while the cursor sits in the middle ~62% of a tile cell: the user
    // is aiming AT that tile (group intent), not at a gap (reorder intent).
    // Freezing the reorder preview here stops the target tile from sliding
    // away under the cursor, so a drop / hover can actually land on it.
    // The wider dead-zone makes grouping easy (macOS never slides the target
    // away from under you).
    private bool IsHoveringTileCenter(System.Windows.Point gridPos)
    {
        if (AppsList.ActualWidth < 100)
            return false;
        var cellW = GridCellWidth;
        var cols = Math.Max(1, (int)(AppsList.ActualWidth / cellW));
        var usedW = cols * cellW;
        var leftOffset = Math.Max(0.0, (AppsList.ActualWidth - usedW) / 2);
        var inCellX = (gridPos.X - leftOffset) % cellW;
        if (inCellX < 0)
            inCellX += cellW;
        var inCellY = gridPos.Y % GridCellHeight;
        if (inCellY < 0)
            inCellY += GridCellHeight;
        const double edge = 0.19; // outer 19% on each side = reorder zone
        return inCellX > cellW * edge && inCellX < cellW * (1 - edge)
            && inCellY > GridCellHeight * edge && inCellY < GridCellHeight * (1 - edge);
    }

    // Hover-to-group: (re)arms a short timer while the ghost rests over a
    // *different* tile (app OR existing folder); moving off it disarms the
    // timer. When it elapses, the tiles fold into a group (or the dragged
    // app flies into the hovered folder) immediately — no drop needed.
    // While hovering, the target tile gently scales up ("press to accept"),
    // exactly like macOS Launchpad's folder-create feedback.
    private void UpdateHoverGroupTimer(System.Windows.Point gridPos)
    {
        if (_dragTile is null || _tileDragFromGroup || _openGroup is not null)
        {
            StopHoverGroupTimer();
            return;
        }
        var over = TileAtGridPoint(gridPos);
        if (over is null)
        {
            StopHoverGroupTimer();
            return;
        }
        if (ReferenceEquals(over, _hoverGroupTarget) && _hoverGroupTimer is not null)
            return; // already counting down on this tile
        StopHoverGroupTimer();
        _hoverGroupTarget = over;
        HighlightHoverTarget(over, true);
        _hoverGroupTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(HoverGroupDelayMs)
        };
        var target = over;
        _hoverGroupTimer.Tick += (_, _) => FireHoverGroup(target);
        _hoverGroupTimer.Start();
    }

    // The tile being dragged, whether it is an app or a whole folder.
    private ITileRow? AnyDragTile => (ITileRow?)_dragTile ?? _dragGroupTile;

    // Folders have no single icon: their drag ghost shows the first preview
    // icon (the same one the tile shows top-left).
    private static ImageSource? MakeGroupGhostImage(GroupRow? group)
        => group?.Previews.FirstOrDefault(p => p is not null);

    private void StopHoverGroupTimer()
    {
        _hoverGroupTimer?.Stop();
        _hoverGroupTimer = null;
        _hoverGroupTarget = null;
        HighlightHoverTarget(null, false);
    }

    // Scales the tile under the ghost up (true) or restores it (false) —
    // pure RenderTransform animation, so it costs no layout passes.
    private void HighlightHoverTarget(ITileRow? tile, bool on)
    {
        if (_hoverHighlightContainer is not null)
        {
            var c = _hoverHighlightContainer;
            _hoverHighlightContainer = null;
            AnimateTileScale(c, 1.0);
        }
        if (!on || tile is null)
            return;
        if (AppsList.ItemContainerGenerator.ContainerFromItem(tile) is ListBoxItem container)
        {
            _hoverHighlightContainer = container;
            AnimateTileScale(container, 1.12);
        }
    }

    private static void AnimateTileScale(ListBoxItem container, double to)
    {
        if (container.RenderTransform is not ScaleTransform scale)
        {
            scale = new ScaleTransform(1, 1);
            container.RenderTransform = scale;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
    }

    // The tile visually under the cursor: the reorder target index mapped
    // back onto the current page slice (the dragged tile itself excluded —
    // it still occupies its old slot in ItemsSource during the preview).
    private ITileRow? TileAtGridPoint(System.Windows.Point gridPos)
    {
        var targetIndex = ComputeDragTargetIndex(gridPos);
        var pageStart = _pageIndex * Math.Max(1, _pageSize);
        var local = targetIndex - pageStart;
        if (AppsList.ItemsSource is System.Collections.IEnumerable items)
        {
            var i = 0;
            foreach (var item in items)
            {
                if (i == local && item is ITileRow row && !ReferenceEquals(row, AnyDragTile))
                    return row;
                i++;
            }
        }
        return null;
    }

    private void FireHoverGroup(ITileRow target)
    {
        StopHoverGroupTimer();
        if (_dragTile is null || _tileDragFromGroup || _openGroup is not null)
            return;
        if (ReferenceEquals(target, _dragTile))
            return;
        var source = _dragTile;
        ClearDragPreview();
        _dragTile = null;
        _tileDragArmed = false;
        DragGhost.Visibility = Visibility.Collapsed;
        switch (target)
        {
            case AppRow app:
                CreateGroup(source, app);
                break;
            case GroupRow folder:
                AddToGroup(folder, source);
                ApplyFilter(true); // the dragged tile disappears into the folder
                break;
        }
    }

    // ── Drag: mouse-up (group create / add / remove / drop) ───────
    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        var swiped = _dragConsumed;
        _dragConsumed = false;
        _dragOrigin = null;
        StopHoldTimer();
        StopHoverGroupTimer();

        // A just-closed ContextMenu mirrors a mouse-up onto the tile — never
        // treat that as a launch/open click (Open-folder bug).
        if (_ignoreNextClick)
        {
            _ignoreNextClick = false;
            return;
        }

        if (_tileDragArmed && AnyDragTile is not null)
        {
            var source = _dragTile;
            var sourceGroup = _dragGroupTile;
            var fromGroup = _tileDragFromGroup;
            _tileDragArmed = false;
            _tileDragFromGroup = false;
            DragGhost.Visibility = Visibility.Collapsed;

            // Dragged a whole folder → it only reorders (like macOS: folders
            // never nest and cannot be dropped INTO another folder).
            if (sourceGroup is not null)
            {
                ReorderTiles();
                return;
            }

            // Dragged out of an open group → remove that member.
            if (fromGroup && _openGroup is { } og && source is not null
                && og.Group.MemberIds.Contains(source.App.Id))
            {
                ResetDragState();
                RemoveFromGroup(source);
                return;
            }

            var targetContainer = FindTileContainer(e.OriginalSource as DependencyObject);
            if (source is not null && targetContainer?.DataContext is GroupRow targetGroup)
            {
                ResetDragState();
                AddToGroup(targetGroup, source);
                ApplyFilter(true); // the dropped tile disappears into the folder
            }
            else if (source is not null && targetContainer?.DataContext is AppRow targetApp && targetApp != source)
            {
                ResetDragState();
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

        // Skip the settings gear button — let its Click handler open the overlay.
        var clickSource = e.OriginalSource as DependencyObject;
        if (IsWithinSettingsGear(clickSource))
            return;

        // Let interactive controls (settings gear, context menus, etc.) handle
        // their own clicks — don't pre-emptively close the launcher.
        if (IsWithinControl(clickSource))
            return;

        // Inside the open folder: member icons launch, clicking the empty
        // backdrop beside the card just steps back to the grid view — the
        // launcher itself stays open (dismissal is for the grid, not the
        // folder you are actively managing).
        if (_openGroup is not null)
        {
            if (FindMemberContainer(clickSource) is { } member
                && member.DataContext is AppRow groupApp)
                TryLaunch(groupApp);
            else
                CloseGroup();
            return;
        }

        if (FindTileContainer(clickSource) is { } container)
        {
            if (container.DataContext is GroupRow g)
                OpenGroup(g);
            else if (container.DataContext is AppRow app)
            {
                TryLaunch(app);
                return;
            }
        }

        // Click on empty area (outside tiles, search, and controls) → close.
        Dismiss();
    }
    // ── Global hotkey: native polling (robust, no window hook) ────
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);

    private static bool KeyState(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static bool WinKeyState() => KeyState(VkWinLeft) || KeyState(VkWinRight);

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
            && WinKeyState() == wantWin;
    }

    // Parses "Ctrl+Alt+L"/"Alt+Space"/"Ctrl+Shift+F12"... into modal flags + VK.
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
                case "enter": vk = 0x0D; break;
                default:
                    if (p.Length == 1 && char.IsLetterOrDigit(p[0]))
                    {
                        vk = char.ToUpperInvariant(p[0]);
                    }
                    else if (p.Length >= 2 && p[0] == 'f' && int.TryParse(p[1..], out var f) && f >= 1 && f <= 24)
                    {
                        vk = 0x6F + f; // VK_F1 = 0x70
                    }
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
        PositionGridPopup();
        CommitSettings();
    }

    private void OnSliderLoaded(object sender, RoutedEventArgs e)
    {
        UpdateGridSizeLabel(App.Settings.TileScale);
        Dispatcher.BeginInvoke(new Action(PositionGridPopup));
    }

        // One distinct grid size per slider stop: the preview text is derived
    // from the REAL layout math (GridDimsFor on the live grid size), so the
    // readout always matches the actual grid. The computed value is snapped
    // to a per-stop unique entry so two stops never show the same text.
    private string GridTextFor(double sliderValue)
    {
        var scale = Math.Clamp(sliderValue / 100.0, 0.7, 1.4);
        var cellW = Math.Round(134 * scale) + 16;
        var cellH = Math.Round(84 * scale) + 52;
        double w = AppsList is not null && AppsList.ActualWidth >= 100
            ? AppsList.ActualWidth
            : Math.Max(100, ActualWidth - 80);
        double h = AppsList is not null && AppsList.ActualHeight >= 100
            ? AppsList.ActualHeight
            : Math.Max(100, ActualHeight - 200);
        var (cols, rows) = GridDimsFor(w, h, cellW, cellH);
        return cols + " x " + rows;
    }

    private void UpdateGridSizeLabel(double scale)
    {
        var text = GridTextFor(scale * 100.0);
        if (GridSizePopup is not null)
            GridSizePopup.Text = text;
    }

    // The value readout floats above the thumb: thumb X is approximated
    // from the slider fraction mapped onto the track width (control width
    // minus thumb diameter), then centered under the text.
    private void PositionGridPopup()
    {
        if (GridSizePopup is null || TileScaleSlider is null)
            return;
        var fraction = (TileScaleSlider.Value - TileScaleSlider.Minimum)
            / Math.Max(1, TileScaleSlider.Maximum - TileScaleSlider.Minimum);
        var trackW = Math.Max(0, TileScaleSlider.ActualWidth - 22);
        var thumbX = 11 + fraction * trackW;
        var textW = Math.Max(20, GridSizePopup.ActualWidth);
        GridSizePopup.Margin = new Thickness(Math.Max(0, thumbX - textW / 2), 0, 0, 0);
        GridSizePopup.UpdateLayout();
    }

private void OnOpenAppFolder(object sender, RoutedEventArgs e)
    {
        // ContextMenu is not in the visual tree, so resolve the row via PlacementTarget.
        var menuItem = sender as System.Windows.Controls.MenuItem;
        var contextMenu = menuItem?.Parent as System.Windows.Controls.ContextMenu;
        var target = contextMenu?.PlacementTarget as FrameworkElement;
        if (target?.DataContext is AppRow row)
        {
            if (row.App.Kind == AppKind.Uwp)
                return;
            // Shortcuts: open the resolved exe folder (what the user expects),
            // falling back to the .lnk folder when resolution fails.
            var path = row.App.TargetPath;
            if (row.App.Kind == AppKind.Shortcut)
            {
                var resolved = LaunchpadClone.Core.Native.StaRunner
                    .RunSilent(() => LaunchpadClone.Core.Native.ShellLinkResolver.Resolve(path));
                if (!string.IsNullOrWhiteSpace(resolved?.TargetPath) && File.Exists(resolved.TargetPath))
                    path = resolved.TargetPath;
            }
            var folder = Path.GetDirectoryName(path);
            if (folder is not null && Directory.Exists(folder))
            {
                // The ContextMenu popup mirrors a final mouse-up onto the tile
                // when it closes — swallow it so the app is not launched.
                _ignoreNextClick = true;
                Process.Start(new ProcessStartInfo("explorer.exe", $"select,\"{path}\"") { UseShellExecute = true });
                Dismiss(); // close the launcher so the folder is frontmost
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
        _pendingHotKey = (KeyState(VkControl) ? "Ctrl+" : "") + (KeyState(VkAlt) ? "Alt+" : "")
            + (KeyState(VkShift) ? "Shift+" : "") + (WinKeyState() ? "Win+" : "") + keyLabel;
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
        _ when key >= Key.F1 && key <= Key.F24 => key.ToString(),
        _ when key.ToString().Length == 1 => key.ToString(),
        _ => ""
    };

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // First cancel an in-flight drag; then step out of the overlay stack.
            if (_tileDragArmed || AnyDragTile is not null)
            {
                StopHoldTimer();
                StopHoverGroupTimer();
                ResetDragState();
            }
            else if (SettingsOverlay.Visibility == Visibility.Visible)
                OnCloseSettings(sender, e);
            else if (_openGroup is not null)
                CloseGroup();
            else if (SearchBox.Text.Length > 0)
                SearchBox.Text = "";
            else
                Dismiss();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter)
        {
            // Enter inside the open folder just closes it back to the grid —
            // the first visible tile underneath must never auto-launch.
            if (_openGroup is not null)
                CloseGroup();
            else
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
    /// <summary>
    /// Extracts icons only for the currently visible page — keeps startup fast.
    /// Scrolling to another page triggers extraction for that page via
    /// RenderPage → ExtractVisibleIconsAsync. Overlapping runs are cancelled
    /// so fast page flips cannot corrupt the shared icon cache.
    /// </summary>
    private async Task ExtractVisibleIconsAsync()
    {
        // Cancel any in-flight run started by a previous page flip.
        _iconCts?.Cancel();
        _iconCts?.Dispose();
        _iconCts = new CancellationTokenSource();
        var ct = _iconCts.Token;

        // Snapshot only the rows on the current page (not the entire list).
        // Must run on the UI thread: _filtered / _rows are UI-owned.
        List<AppRow> visibleRows;
        if (!Dispatcher.CheckAccess())
            visibleRows = await Dispatcher.InvokeAsync(() => SnapshotVisibleRows());
        else
            visibleRows = SnapshotVisibleRows();

        var updatedApps = false;
        foreach (var row in visibleRows)
        {
            if (ct.IsCancellationRequested)
                return;
            string? path;
            try
            {
                path = await _icons.ExtractAndCacheAsync(row.App, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            if (path is null)
                continue;

            // BitmapImage decode is the single most expensive UI-thread cost at
            // startup (~ms per icon). Decode small (tiles are ~64-112px) and
            // cache the decode, not the file bytes.
            var source = new BitmapImage();
            source.BeginInit();
            source.UriSource = new Uri("file:///" + path.Replace("\\", "/"));
            source.DecodePixelWidth = 96;
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.EndInit();
            source.Freeze();

            lock (_iconLock)
            {
                _iconMemoryCache[row.App.Id] = source;
            }

            // Row updates must happen on the UI thread.
            await Dispatcher.InvokeAsync(() =>
            {
                if (_rowsById.TryGetValue(row.App.Id, out var current))
                    current.Icon = source;

                // Persist the icon cache path so next startup skips re-extraction.
                if (row.App.IconCachePath != path)
                {
                    row.Update(row.App.WithIconCachePath(path));
                    SyncAllAppsIconPath(row.App.Id, path);
                    updatedApps = true;
                }
            });

            if (ct.IsCancellationRequested)
                return;
        }

        await Dispatcher.InvokeAsync(RefreshAllGroupPreviews);

        // Re-save the app list cache now that icon paths are filled in.
        if (updatedApps && !ct.IsCancellationRequested)
        {
            List<AppItem> snapshot;
            lock (_iconLock)
            {
                snapshot = _allApps.ToList();
            }
            await _cache.SaveAsync(snapshot);
        }
    }

    private List<AppRow> SnapshotVisibleRows()
    {
        var pageSize = Math.Max(1, _pageSize);
        var pageStart = _pageIndex * pageSize;
        return _filtered
            .OfType<AppRow>()
            .Skip(pageStart)
            .Take(pageSize)
            .Where(r => r.Icon is null)
            .ToList();
    }

    private void SyncAllAppsIconPath(string id, string path)
    {
        for (var i = 0; i < _allApps.Count; i++)
        {
            if (_allApps[i].Id == id && _allApps[i].IconCachePath != path)
                _allApps[i] = _allApps[i].WithIconCachePath(path);
        }
    }

    // ── Watcher deltas (shortcut install/uninstall) ──────────────
    private async Task OnWatcherUpserted(IReadOnlyList<AppItem> items)
    {
        // AppWatcher fires on a thread-pool timer thread — marshal all
        // ObservableCollection / Dictionary mutations to the UI thread.
        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(() => OnWatcherUpserted(items));
            return;
        }

        foreach (var app in items)
        {
            if (_rowsById.TryGetValue(app.Id, out var existing))
            {
                existing.Update(app);
                SyncAllAppsItem(app);
            }
            else
            {
                var row = new AppRow(app);
                lock (_iconLock)
                {
                    if (_iconMemoryCache.TryGetValue(app.Id, out var icon))
                        row.Icon = icon;
                }
                _rows.Add(row);
                _rowsById[app.Id] = row;
                _allApps.Add(app);
            }
        }
        RefreshAllGroupPreviews();
        ApplyFilter(true);
        _ = ExtractVisibleIconsAsync();
        await Task.CompletedTask;
    }

    private void SyncAllAppsItem(AppItem app)
    {
        for (var i = 0; i < _allApps.Count; i++)
        {
            if (_allApps[i].Id == app.Id)
            {
                // Keep the cached icon path — watcher items carry none.
                var iconPath = _allApps[i].IconCachePath ?? app.IconCachePath;
                _allApps[i] = iconPath is not null ? app.WithIconCachePath(iconPath) : app;
                return;
            }
        }
    }

    private async Task OnWatcherRemoved(IReadOnlyList<string> ids)
    {
        // Same marshalling as above — _rows is UI-owned.
        if (!Dispatcher.CheckAccess())
        {
            await Dispatcher.InvokeAsync(() => OnWatcherRemoved(ids));
            return;
        }

        foreach (var id in ids)
        {
            if (_rowsById.TryGetValue(id, out var row))
            {
                _rows.Remove(row);
                _rowsById.Remove(id);
            }
            _allApps.RemoveAll(a => a.Id == id);
        }
        RefreshAllGroupPreviews();
        ApplyFilter(true);
        await Task.CompletedTask;
    }
}
