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
        private bool _isKeyboardSelected;

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

        public bool IsKeyboardSelected
        {
            get => _isKeyboardSelected;
            set => SetField(ref _isKeyboardSelected, value);
        }

        private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public sealed class GroupPageDot
    {
        public GroupPageDot(int index, bool isCurrent)
        {
            Index = index;
            IsCurrent = isCurrent;
        }

        public int Index { get; }
        public bool IsCurrent { get; }
    }

    // A user-created group rendered as a "folder" tile with a 3x3 mini
    // preview of up to nine member icons.
    public sealed class GroupRow : INotifyPropertyChanged, ITileRow
    {
        private bool _showRemoveBadge;
        private bool _isExpanded;
        private double _expandedWidth;
        private double _expandedHeight;
        private int _expandedSlotCount = 4;
        private int _memberPageIndex;
        private bool _isDropTarget;
        private double _lastTileWidth;
        private double _lastIconSize;

        public GroupRow(AppGroup group) => Group = group;

        public AppGroup Group { get; }

        public string DisplayName => Group.Name;

        public ObservableCollection<AppRow> Members { get; } = new();
        public ObservableCollection<AppRow> VisibleMembers { get; } = new();
        public ObservableCollection<GroupPageDot> MemberPages { get; } = new();

        public const int MembersPerPage = 6;

        public int MemberPageCount => Math.Max(1,
            (int)Math.Ceiling(Members.Count / (double)MembersPerPage));

        public int MemberPageIndex => _memberPageIndex;

        public bool HasMultipleMemberPages => MemberPageCount > 1;

        public AppRow? SelectedMember { get; private set; }

        public bool IsDropTarget
        {
            get => _isDropTarget;
            set
            {
                if (_isDropTarget == value)
                    return;
                _isDropTarget = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDropTarget)));
            }
        }

        public double ExpandedWidth
        {
            get => _expandedWidth;
            private set
            {
                _expandedWidth = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandedWidth)));
            }
        }

        public double ExpandedHeight
        {
            get => _expandedHeight;
            private set
            {
                _expandedHeight = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandedHeight)));
            }
        }

        public int ExpandedSlotCount
        {
            get => _expandedSlotCount;
            private set => _expandedSlotCount = value;
        }

        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value)
                    return;
                _isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }

        // Indexer bindings refresh together via the "Item[]" notification.
        public ImageSource?[] Previews { get; } = new ImageSource?[9];

        public void SetPreviews(IReadOnlyList<ImageSource?> icons)
        {
            for (var i = 0; i < Previews.Length; i++)
                Previews[i] = i < icons.Count ? icons[i] : null;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }

        public void SetMembers(IReadOnlyList<AppRow> members)
        {
            var selectedId = SelectedMember?.App.Id;
            Members.Clear();
            foreach (var member in members)
                Members.Add(member);
            _memberPageIndex = Math.Clamp(_memberPageIndex, 0, MemberPageCount - 1);
            RebuildMemberPage();
            if (selectedId is not null)
            {
                var selected = Members.FirstOrDefault(m => m.App.Id == selectedId);
                if (selected is not null)
                    SelectMember(selected);
            }
        }

        public bool SetMemberPage(int pageIndex)
        {
            var next = Math.Clamp(pageIndex, 0, MemberPageCount - 1);
            if (next == _memberPageIndex)
                return false;
            ClearMemberSelection();
            _memberPageIndex = next;
            RebuildMemberPage();
            SelectVisibleMember(0);
            return true;
        }

        public void ShowLastMemberPage()
        {
            _memberPageIndex = MemberPageCount - 1;
            RebuildMemberPage();
            SelectVisibleMember(Math.Max(0, VisibleMembers.Count - 1));
        }

        public void SelectVisibleMember(int index)
        {
            if (VisibleMembers.Count == 0)
            {
                ClearMemberSelection();
                return;
            }
            index = Math.Clamp(index, 0, VisibleMembers.Count - 1);
            SelectMember(VisibleMembers[index]);
        }

        public int SelectedVisibleMemberIndex() =>
            SelectedMember is null ? -1 : VisibleMembers.IndexOf(SelectedMember);

        public void ClearMemberSelection()
        {
            foreach (var member in Members)
                member.IsKeyboardSelected = false;
            SelectedMember = null;
        }

        private void SelectMember(AppRow member)
        {
            foreach (var item in Members)
                item.IsKeyboardSelected = ReferenceEquals(item, member);
            SelectedMember = member;
        }

        private void RebuildMemberPage()
        {
            VisibleMembers.Clear();
            foreach (var member in Members
                .Skip(_memberPageIndex * MembersPerPage)
                .Take(MembersPerPage))
            {
                VisibleMembers.Add(member);
            }

            MemberPages.Clear();
            for (var i = 0; i < MemberPageCount; i++)
                MemberPages.Add(new GroupPageDot(i, i == _memberPageIndex));

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MemberPageCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MemberPageIndex)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasMultipleMemberPages)));
            if (_lastTileWidth > 0 && _lastIconSize > 0)
                UpdateExpandedLayout(_lastTileWidth, _lastIconSize);
        }

        public void UpdateExpandedLayout(double tileWidth, double iconSize)
        {
            _lastTileWidth = tileWidth;
            _lastIconSize = iconSize;
            var count = Math.Max(1, Math.Min(Members.Count, MembersPerPage));
            var columns = count <= 4
                ? 2
                : 3;
            var memberRows = (int)Math.Ceiling(count / (double)columns);
            var occupiedRows = Math.Max(2, memberRows + 1); // title + full-size icon rows
            var cellHeight = iconSize + 52;
            // Include the folder's horizontal padding and every member's
            // margin so two full-size icons never wrap into one column.
            // Border padding is 36px in total, then another 2px is consumed
            // by its border. A little spare width also absorbs DPI rounding,
            // preventing the final member from wrapping into a clipped row.
            ExpandedWidth = columns * (tileWidth + 8) + 42;
            ExpandedHeight = occupiedRows * cellHeight - 24
                + (HasMultipleMemberPages ? 24 : 0);
            ExpandedSlotCount = columns * Math.Max(2,
                (int)Math.Ceiling(ExpandedHeight / cellHeight));
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
    private CancellationTokenSource? _uninstallEligibilityCts;

    private List<AppGroup> _groups = new();
    private readonly List<GroupRow> _groupRows = new();
    private GroupRow? _openGroup;
    private List<string> _orderIds = new(); // saved drag order (layout store)

    // Paging state: one page slice at a time, flipped by wheel, drag, keys.
    private List<ITileRow> _filtered = new();
    // Kept as one stable ItemsSource so a reorder can move the existing WPF
    // containers instead of rebuilding every tile template on every step.
    private readonly ObservableCollection<ITileRow> _visibleTiles = new();
    private int _pageIndex;
    private int _pageSize = 24;
    private bool _pageAnimationRunning;
    private ITileRow? _keyboardSelectedTile;
    private System.Windows.Point? _dragOrigin;
    private bool _dragConsumed;
    private HwndSource? _windowSource;
    private int _horizontalWheelDelta;

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

    // The tile currently offered as a drop target. Group creation itself only
    // happens on mouse-up; hovering is visual feedback, never an action.
    private ITileRow? _hoverGroupTarget;
    // The tile currently pressed by the hovering ghost (drop feedback).
    private ListBoxItem? _hoverHighlightContainer;
    // Dragging to a page edge arms one deliberate page flip. The pointer
    // must remain there briefly, then leave the edge before another flip can
    // be armed. This prevents a single drag from racing across every page.
    private DispatcherTimer? _edgePageTimer;
    private int _edgePageDirection;
    private bool _edgePageTriggered;
    private const double EdgePageDelayMs = 600;

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
        AppsList.ItemsSource = _visibleTiles;

        // Gestures and overlay controls.
        // Single click launches (macOS style) via OnMouseLeftButtonUp —
        // no DoubleClick handler on purpose: it would fire a second launch.
        AppsList.SizeChanged += (_, _) => RecomputeLayout();

        SearchBox.TextChanged += (_, _) => ApplyFilter();
        Loaded += OnLoaded;
        Closed += (_, _) => App.SettingsChanged -= OnAppSettingsChanged;
        // Normal focus loss dismisses the launcher. In uninstall mode an
        // external uninstaller may take focus; keep the launcher alive and
        // move it behind that window so the user can return to the same view.
        Deactivated += (_, _) =>
        {
            if (_jiggleMode)
            {
                Topmost = false;
                return;
            }
            Dismiss();
        };

        App.SettingsChanged += OnAppSettingsChanged;
        ApplySettings(App.Settings);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        _windowSource?.AddHook(WindowMessageHook);

        // Capture the desktop BEFORE this window paints over it. The tiny
        // snapshot upscaled behind the scrim replaces the DWM blur accent —
        // it is a one-shot static image, so nothing per-frame remains.
        var shot = DesktopSnapshot.Capture();
        if (shot is not null)
            BackdropImage.Source = shot;
        else
            WindowAccentBlur.EnableBlurBehind(this); // graceful fallback

        // Global hotkey: native poll (no window hook needed).
        StartHotKeyPoller();
    }

    // After the window becomes active (first show, hot-corner, hotkey), the
    // search box takes the keyboard — typing starts filtering immediately.
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        Topmost = true;
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
        _watcher.Dispose();
        StopEdgePageFlip(resetTrigger: true);
        _iconCts?.Cancel();
        _iconCts?.Dispose();
        _iconCts = null;
        _uninstallEligibilityCts?.Cancel();
        _uninstallEligibilityCts?.Dispose();
        _uninstallEligibilityCts = null;
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
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
        foreach (var groupRow in _groupRows)
            groupRow.UpdateExpandedLayout(TileWidth, TileIconSize);
        if (AppsList is not null)
            RecomputeLayout();

        // Sync the settings panel without re-firing change handlers.
        // During InitializeComponent some controls may still be null.
        _applyingSettings = true;
        try
        {
            if (HotCornerToggle is not null)
                HotCornerToggle.IsChecked = settings.HotCornerEnabled;
            if (TopLeftCornerButton is not null)
                UpdateCornerSelection(settings.HotCorner);
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
                _allApps = cached.ToList();
                RenderApps(cached);
                StatusText.Text = $"{cached.Count} apps";
                _ = ExtractVisibleIconsAsync(); // only current page, not all
            }

            _watcher.AppsUpserted += OnWatcherUpserted;
            _watcher.AppsRemoved += OnWatcherRemoved;
            _watcher.Start();

            // A non-empty cache is the startup source of truth. Filesystem
            // watcher deltas apply immediately, while UWP/raw-exe changes are
            // picked up by the periodic scan. This avoids rebuilding every
            // tile and reloading every icon whenever the launcher restarts.
            if (cached.Count > 0)
            {
                StatusText.Text = $"{cached.Count} apps (cached)";
            }
            else
            {
                // Full rescan runs in the background so the window paints instantly
                // from cache. Quiet when cache was shown (no "Scanning..." flash).
                _ = RefreshAppsAsync(quiet: cached.Count > 0);
            }

            // Full rescans are owned by App while the overlay is closed, so
            // they run in tray-idle time without keeping closed windows alive.
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
            var knownIconPaths = _allApps
                .Where(a => !string.IsNullOrWhiteSpace(a.IconCachePath))
                .GroupBy(a => a.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().IconCachePath!, StringComparer.Ordinal);
            _allApps = apps
                .Select(a => knownIconPaths.TryGetValue(a.Id, out var path)
                    ? a.WithIconCachePath(path)
                    : a)
                .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
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
        => DecodeIconFile(path, CancellationToken.None);

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
        {
            _pageIndex = 0;
            _keyboardSelectedTile = null;
        }
        if (_keyboardSelectedTile is not null && !_filtered.Contains(_keyboardSelectedTile))
            _keyboardSelectedTile = null;
        RenderPage(0);
    }
    // Shows only the current page slice; `direction` (+1 next / -1 previous /
    // 0 none) drives the soft slide animation.
    private void RenderPage(int direction)
    {
        if (_pageSize <= 0)
            _pageSize = 24;

        var extraGroupSlots = _openGroup is null ? 0 : _openGroup.ExpandedSlotCount - 1;
        var occupiedSlots = _filtered.Count + extraGroupSlots;
        var pageCount = Math.Max(1, (int)Math.Ceiling(occupiedSlots / (double)_pageSize));
        if (_pageIndex >= pageCount)
            _pageIndex = pageCount - 1;

        var slice = BuildCurrentPageSlice(_reorderPreview ?? _filtered);

        SyncVisibleTiles(slice);
        AppsList.SelectedItem = _openGroup is null
            && _keyboardSelectedTile is not null
            && slice.Contains(_keyboardSelectedTile)
                ? _keyboardSelectedTile
                : null;

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

    private List<ITileRow> BuildCurrentPageSlice(IReadOnlyList<ITileRow> source)
    {
        var extraGroupSlots = _openGroup is null ? 0 : _openGroup.ExpandedSlotCount - 1;
        var pageStart = _pageIndex * Math.Max(1, _pageSize);
        var slice = source.Skip(pageStart).Take(_pageSize).ToList();
        if (_openGroup is not null && slice.Contains(_openGroup))
        {
            // Keep the expanded folder at its original list position and push
            // ordinary tiles off the page to pay for its additional cells.
            for (var i = 0; i < extraGroupSlots && slice.Count > 1; i++)
            {
                var removeAt = slice.Count - 1;
                if (ReferenceEquals(slice[removeAt], _openGroup))
                    removeAt--;
                if (removeAt >= 0)
                    slice.RemoveAt(removeAt);
            }
        }
        return slice;
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
            var dotShape = new System.Windows.Shapes.Ellipse
            {
                Width = i == _pageIndex ? 9 : 7,
                Height = 7,
                Fill = brush
            };
            var dot = new System.Windows.Controls.Button
            {
                Width = 18,
                Height = 16,
                Padding = new Thickness(4),
                Margin = new Thickness(1, 0, 1, 0),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Content = dotShape,
                Cursor = System.Windows.Input.Cursors.Hand,
                Tag = i,
                ToolTip = $"Page {i + 1}"
            };
            dot.Click += OnPageDotClick;
            PageDots.Children.Add(dot);
        }
    }

    private void OnPageDotClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int page } || page == _pageIndex)
            return;
        if (_openGroup is not null)
            CloseGroup(false);
        GoToPage(page);
        e.Handled = true;
    }

    private void GoToPage(int page)
    {
        var pageCount = Math.Max(1, (int)Math.Ceiling(_filtered.Count / (double)Math.Max(1, _pageSize)));
        page = Math.Clamp(page, 0, pageCount - 1);
        if (page == _pageIndex || _pageAnimationRunning)
            return;
        CaptureOutgoingPage();
        var direction = page > _pageIndex ? 1 : -1;
        _pageIndex = page;
        _keyboardSelectedTile = _filtered.Skip(_pageIndex * _pageSize).FirstOrDefault();
        RenderPage(direction);
    }

    // Page flip glides like a camera across one continuous icon surface.
    private void AnimatePage(int direction)
    {
        var travel = Math.Max(1, AppsList.ActualWidth);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        _pageAnimationRunning = true;
        PageSlide.BeginAnimation(TranslateTransform.XProperty, null);
        PageSlide.X = direction * travel;
        // The page is static during a flip. Rasterizing it once lets the
        // compositor move one texture instead of repainting every tile.
        AppsList.CacheMode = new BitmapCache { RenderAtScale = 1.0 };
        var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(440))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            PageSlide.BeginAnimation(TranslateTransform.XProperty, null);
            PageSlide.X = 0;
            AppsList.CacheMode = null;
            OutgoingPageSlide.BeginAnimation(TranslateTransform.XProperty, null);
            OutgoingPageSlide.X = 0;
            OutgoingPageSnapshot.Visibility = Visibility.Collapsed;
            OutgoingPageSnapshot.Source = null;
            _pageAnimationRunning = false;
            _ = ExtractVisibleIconsAsync();
        };

        if (OutgoingPageSnapshot.Source is not null)
        {
            OutgoingPageSnapshot.Visibility = Visibility.Visible;
            OutgoingPageSlide.BeginAnimation(TranslateTransform.XProperty, null);
            OutgoingPageSlide.X = 0;
            OutgoingPageSlide.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation(-direction * travel, TimeSpan.FromMilliseconds(440))
                {
                    EasingFunction = ease,
                    FillBehavior = FillBehavior.HoldEnd
                },
                HandoffBehavior.SnapshotAndReplace);
        }

        // Let the newly selected page finish layout before the animation is
        // clocked. This prevents its first frame from doing layout and paint
        // work while it is already moving.
        _ = Dispatcher.BeginInvoke(new Action(() =>
        {
            AppsList.UpdateLayout();
            PageSlide.BeginAnimation(
                TranslateTransform.XProperty,
                animation,
                HandoffBehavior.SnapshotAndReplace);
        }), DispatcherPriority.Render);
    }

    private void CaptureOutgoingPage()
    {
        if (AppsList.ActualWidth < 1 || AppsList.ActualHeight < 1)
            return;
        try
        {
            AppsList.UpdateLayout();
            var dpi = VisualTreeHelper.GetDpi(AppsList);
            var pixelWidth = Math.Max(1, (int)Math.Ceiling(AppsList.ActualWidth * dpi.DpiScaleX));
            var pixelHeight = Math.Max(1, (int)Math.Ceiling(AppsList.ActualHeight * dpi.DpiScaleY));
            var snapshot = new RenderTargetBitmap(
                pixelWidth, pixelHeight,
                dpi.PixelsPerInchX, dpi.PixelsPerInchY,
                PixelFormats.Pbgra32);
            snapshot.Render(AppsList);
            snapshot.Freeze();
            OutgoingPageSnapshot.Source = snapshot;
            OutgoingPageSnapshot.Visibility = Visibility.Visible;
        }
        catch
        {
            OutgoingPageSnapshot.Source = null;
            OutgoingPageSnapshot.Visibility = Visibility.Collapsed;
        }
    }

    private void SyncVisibleTiles(IReadOnlyList<ITileRow> desired)
    {
        // Preserve containers whenever the same items remain visible. In the
        // common drag-reorder case this reduces a full page rebuild to moves.
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < _visibleTiles.Count && ReferenceEquals(_visibleTiles[i], desired[i]))
                continue;

            var existing = -1;
            for (var j = i + 1; j < _visibleTiles.Count; j++)
            {
                if (ReferenceEquals(_visibleTiles[j], desired[i]))
                {
                    existing = j;
                    break;
                }
            }

            if (existing >= 0)
                _visibleTiles.Move(existing, i);
            else if (i < _visibleTiles.Count)
                _visibleTiles[i] = desired[i];
            else
                _visibleTiles.Add(desired[i]);
        }

        while (_visibleTiles.Count > desired.Count)
            _visibleTiles.RemoveAt(_visibleTiles.Count - 1);
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
        // Keep a small bottom safe area for DPI rounding and the WrapPanel's
        // arranged extent. Without it, exact-fit grid sizes could clip the
        // label baseline of the final row by a few pixels.
        var rows = Math.Max(1, (int)(Math.Max(0, height - 12) / cellH));
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
        if (_pageAnimationRunning)
            return;
        var pageCount = Math.Max(1, (int)Math.Ceiling(_filtered.Count / (double)_pageSize));
        if (_pageIndex >= pageCount - 1)
            return;
        CaptureOutgoingPage();
        _pageIndex++;
        RenderPage(1);
    }

    private void PreviousPage()
    {
        if (_pageAnimationRunning)
            return;
        if (_pageIndex <= 0)
            return;
        CaptureOutgoingPage();
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

    private const int WmMouseHWheel = 0x020E;

    private IntPtr WindowMessageHook(
        IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmMouseHWheel || SettingsOverlay.Visibility == Visibility.Visible)
            return IntPtr.Zero;

        var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
        _horizontalWheelDelta += delta;
        if (Math.Abs(_horizontalWheelDelta) < 120)
            return IntPtr.Zero;

        var direction = Math.Sign(_horizontalWheelDelta);
        _horizontalWheelDelta = 0;
        if (_openGroup is not null)
            ChangeOpenGroupPage(_openGroup.MemberPageIndex + direction);
        else if (direction > 0)
            NextPage();
        else
            PreviousPage();
        handled = true;
        return IntPtr.Zero;
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

    private static AppRow? FindInlineGroupMember(DependencyObject? source)
    {
        for (var node = source; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { Tag: "InlineGroupMember", DataContext: AppRow row })
                return row;
        }
        return null;
    }

    // ── Mouse handling: drag, jiggle, reorder ───────────────────────
    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        StopEdgePageFlip(resetTrigger: true);
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
            var source = e.OriginalSource as DependencyObject;
            if (FindInlineGroupMember(source) is { } groupTile)
            {
                // Custom mouse-move/up drag (ghost + placeholder). Do NOT start
                // a blocking Ole DoDragDrop here: it suppresses MouseMove/MouseUp
                // so the live reorder preview and drop handling never run.
                _dragTile = groupTile;
                _tileDragArmed = false;
                _tileDragFromGroup = true;
            }
            else if (FindTileContainer(source) is { DataContext: AppRow outsideApp })
            {
                // Ordinary tiles remain draggable while a folder is open so
                // they can be dropped directly into the expanded folder.
                _dragTile = outsideApp;
                _tileDragArmed = false;
                _tileDragFromGroup = false;
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
            {
                target.ShowLastMemberPage();
                OpenGroup(target); // re-render members and reveal the new app
            }
        }
    }

    private void RemoveFromGroup(
        AppRow member,
        ITileRow? insertBefore = null,
        bool appendWhenNoAnchor = false)
    {
        if (_openGroup is null)
            return;
        var groupRow = _openGroup;
        var group = groupRow.Group;
        var groupKey = "g:" + group.Id;

        group.MemberIds.Remove(member.App.Id);
        _orderIds.Remove(member.App.Id);
        var groupIndex = _orderIds.IndexOf(groupKey);

        // A one-item folder is no longer a group. Replace the folder tile at
        // its exact slot with the remaining member, then place the dragged-out
        // member beside it so the surrounding icons reflow naturally.
        if (group.MemberIds.Count <= 1)
        {
            if (groupIndex < 0)
                groupIndex = _orderIds.Count;
            else
                _orderIds.RemoveAt(groupIndex);

            var returnedIds = group.MemberIds
                .Concat([member.App.Id])
                .Distinct(StringComparer.Ordinal)
                .ToList();
            foreach (var id in returnedIds)
                _orderIds.Remove(id);
            foreach (var id in returnedIds)
                _orderIds.Insert(Math.Min(groupIndex++, _orderIds.Count), id);

            groupRow.IsExpanded = false;
            _groups.Remove(group);
            _openGroup = null;
            _ = _layoutStore.SaveAsync(_orderIds);
            PersistGroups();
            RebuildGroupRows();
            return;
        }

        // Larger folders stay open. A live drag preview provides the tile
        // that should follow the extracted app, so the persisted order agrees
        // with the moving placeholder and the main grid keeps flowing around
        // it. A simple remove-button click still returns it beside the folder.
        var insertAt = appendWhenNoAnchor
            ? _orderIds.Count
            : groupIndex >= 0
                ? groupIndex + 1
                : _orderIds.Count;
        if (insertBefore is not null)
        {
            var anchorIndex = _orderIds.IndexOf(TileId(insertBefore));
            if (anchorIndex >= 0)
                insertAt = anchorIndex;
        }
        _orderIds.Insert(Math.Min(insertAt, _orderIds.Count), member.App.Id);
        _ = _layoutStore.SaveAsync(_orderIds);
        PersistGroups();
        RefreshAllGroupPreviews();
        ApplyFilter(true);
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

    // Fallback used before keyboard navigation has selected a visible tile.
    private ITileRow? FirstVisibleTile()
    {
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
        var selected = _keyboardSelectedTile is not null
            && IsOnCurrentPage(_keyboardSelectedTile)
                ? _keyboardSelectedTile
                : FirstVisibleTile();
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

    private void MoveKeyboardSelection(Key key)
    {
        if (_openGroup is not null)
            MoveOpenGroupKeyboardSelection(key);
        else
            MoveMainKeyboardSelection(key);
    }

    private void MoveMainKeyboardSelection(Key key)
    {
        if (_filtered.Count == 0)
            return;

        var current = _keyboardSelectedTile is null
            ? -1
            : _filtered.IndexOf(_keyboardSelectedTile);
        if (current < 0)
        {
            SelectMainTileAt(Math.Min(_filtered.Count - 1, _pageIndex * Math.Max(1, _pageSize)));
            return;
        }

        var width = AppsList.ActualWidth >= 100 ? AppsList.ActualWidth : Math.Max(100, ActualWidth - 80);
        var height = AppsList.ActualHeight >= 100 ? AppsList.ActualHeight : Math.Max(100, ActualHeight - 160);
        var (columns, _) = GridDimsFor(width, height, GridCellWidth, GridCellHeight);
        var delta = key switch
        {
            Key.Left => -1,
            Key.Right => 1,
            Key.Up => -columns,
            Key.Down => columns,
            _ => 0
        };
        SelectMainTileAt(Math.Clamp(current + delta, 0, _filtered.Count - 1));
    }

    private void SelectMainTileAt(int absoluteIndex)
    {
        if (_filtered.Count == 0)
            return;
        absoluteIndex = Math.Clamp(absoluteIndex, 0, _filtered.Count - 1);
        var targetPage = absoluteIndex / Math.Max(1, _pageSize);
        var direction = Math.Sign(targetPage - _pageIndex);
        if (direction != 0)
        {
            CaptureOutgoingPage();
            _pageIndex = targetPage;
        }

        _keyboardSelectedTile = _filtered[absoluteIndex];
        RenderPage(direction);
    }

    private void MoveOpenGroupKeyboardSelection(Key key)
    {
        if (_openGroup is not { Members.Count: > 0 } group)
            return;

        var selectedOnPage = group.SelectedVisibleMemberIndex();
        var current = selectedOnPage < 0
            ? group.MemberPageIndex * GroupRow.MembersPerPage
            : group.MemberPageIndex * GroupRow.MembersPerPage + selectedOnPage;
        var visibleCapacity = Math.Min(group.Members.Count, GroupRow.MembersPerPage);
        var columns = visibleCapacity <= 4 ? 2 : 3;
        var delta = key switch
        {
            Key.Left => -1,
            Key.Right => 1,
            Key.Up => -columns,
            Key.Down => columns,
            _ => 0
        };
        var target = Math.Clamp(current + delta, 0, group.Members.Count - 1);
        var targetPage = target / GroupRow.MembersPerPage;
        if (targetPage != group.MemberPageIndex)
            group.SetMemberPage(targetPage);
        group.SelectVisibleMember(target % GroupRow.MembersPerPage);
        RenderPage(0);
    }

    // ── Group (folder) operations ─────────────────────────────────
    // A folder expands in place inside the main WrapPanel. Its larger desired
    // size participates in layout, so later tiles naturally move around it.
    private void OpenGroup(GroupRow gr, System.Windows.Point? preferredCenter = null)
    {
        if (_openGroup is not null && !ReferenceEquals(_openGroup, gr))
        {
            _openGroup.ClearMemberSelection();
            _openGroup.IsExpanded = false;
        }
        _openGroup = gr;
        _keyboardSelectedTile = gr;
        gr.IsExpanded = true;
        gr.SelectVisibleMember(0);
        GroupOverlay.Visibility = Visibility.Collapsed;
        RenderPage(0);
    }

    private void OnGroupMemberPageClick(object sender, RoutedEventArgs e)
    {
        if (_openGroup is null || sender is not FrameworkElement { Tag: int page })
            return;
        ChangeOpenGroupPage(page);
        e.Handled = true;
    }

    private void ChangeOpenGroupPage(int page)
    {
        if (_openGroup is null || !_openGroup.SetMemberPage(page))
            return;
        RenderPage(0);
    }

    // Places the folder card near its tile: measured tile center in window
    // coordinates, clamped so the card (measured after layout) stays on screen.
    private void PositionGroupCard(GroupRow gr, System.Windows.Point? preferredCenter = null)
    {
        GroupCard.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        var cardW = Math.Min(GroupCard.MaxWidth, GroupCard.DesiredSize.Width);
        var cardH = GroupCard.DesiredSize.Height;
        if (cardW <= 0 || cardH <= 0)
            return;

        double x, y;
        if (preferredCenter is { } anchor)
        {
            x = anchor.X - cardW / 2;
            y = anchor.Y - cardH / 2;
        }
        else if (AppsList.ItemContainerGenerator.ContainerFromItem(gr) is ListBoxItem c)
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
        if (_openGroup is not null)
        {
            _openGroup.ClearMemberSelection();
            _openGroup.IsDropTarget = false;
            _openGroup.IsExpanded = false;
        }
        _openGroup = null;
        GroupOverlay.Visibility = Visibility.Collapsed;
        ScrimDim.Opacity = 0;
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

    private void OnInlineGroupNameMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox editor || editor.IsKeyboardFocusWithin)
            return;
        editor.Focus();
        editor.SelectAll();
        e.Handled = true;
    }

    private void OnInlineGroupNameLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is System.Windows.Controls.TextBox editor)
            CommitInlineGroupName(editor);
    }

    private void OnInlineGroupNameKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox editor)
            return;
        if (e.Key == Key.Enter)
        {
            CommitInlineGroupName(editor);
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && editor.DataContext is GroupRow row)
        {
            editor.Text = row.DisplayName;
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void CommitInlineGroupName(System.Windows.Controls.TextBox editor)
    {
        if (editor.DataContext is not GroupRow row)
            return;
        var name = editor.Text.Trim();
        if (name.Length == 0)
        {
            editor.Text = row.DisplayName;
            return;
        }
        if (name == row.Group.Name)
            return;
        row.Group.Name = name;
        row.NotifyNameChanged();
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
            var members = new List<AppRow>();
            var icons = new List<ImageSource?>();
            foreach (var id in gr.Group.MemberIds)
            {
                if (_rowsById.TryGetValue(id, out var mr))
                {
                    members.Add(mr);
                    icons.Add(mr.Icon);
                }
            }
            gr.SetMembers(members);
            gr.UpdateExpandedLayout(TileWidth, TileIconSize);
            gr.SetPreviews(icons);
        }
    }

    private void PersistGroups()
    {
        _ = _groupStore.SaveAsync(_groups);
    }

    private void PersistLayout()
    {
        // Never persist a SEARCH-RESULT slice: that would overwrite the full
        // saved order with a filtered subset and lose tile positions.
        if (SearchBox.Text.Length > 0)
            return;
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
        if (AppsList.ActualWidth < 100 || AppsList.Items.Count == 0)
            return 0;

        // Use the real arranged containers rather than theoretical grid math.
        // Folder margins and expanded tiles can change actual positions, and
        // the placeholder must open exactly beside the visual under the cursor.
        ITileRow? closest = null;
        var bestScore = double.PositiveInfinity;
        foreach (var item in AppsList.Items.OfType<ITileRow>())
        {
            if (ReferenceEquals(item, AnyDragTile))
                continue;
            if (AppsList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container)
                continue;
            System.Windows.Point center;
            try
            {
                center = container.TranslatePoint(
                    new System.Windows.Point(container.ActualWidth / 2, container.ActualHeight / 2),
                    AppsList);
            }
            catch
            {
                continue;
            }

            var normalizedX = (pos.X - center.X) / Math.Max(1, container.ActualWidth);
            var normalizedY = (pos.Y - center.Y) / Math.Max(1, container.ActualHeight);
            var score = normalizedX * normalizedX + normalizedY * normalizedY;
            if (score < bestScore)
            {
                bestScore = score;
                closest = item;
            }
        }

        if (closest is not null)
        {
            var index = _filtered.IndexOf(closest);
            if (index >= 0)
                return index;
        }

        return Math.Max(0, Math.Min(_filtered.Count - 1, _pageIndex * Math.Max(1, _pageSize)));
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

    // A member being pulled out of an expanded folder is not part of the
    // main-grid source yet. Insert a temporary placeholder at the pointer's
    // target so the surrounding main tiles visibly make room before release.
    private void BuildGroupMemberDragOutPreview()
    {
        if (_dragTile is not { } dragged)
            return;
        var preview = _filtered.ToList();
        preview.Insert(Math.Clamp(_dragTargetIndex, 0, preview.Count), dragged);
        dragged.ShowAsPlaceholder = true;
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
        var before = new Dictionary<object, System.Windows.Point>();
        foreach (var item in AppsList.Items)
        {
            if (AppsList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem c)
            {
                try { before[item] = c.TranslatePoint(new System.Windows.Point(c.ActualWidth / 2, c.ActualHeight / 2), AppsList); }
                catch { /* container not yet laid out */ }
                // Capture the currently displayed (possibly mid-animation)
                // location first, then cancel the old clock. The next FLIP
                // continues from that exact visual position without a snap.
                c.RenderTransform = null;
            }
        }
        var desired = BuildCurrentPageSlice(_reorderPreview ?? _filtered);

        // A drag normally changes one item's index. ObservableCollection.Move
        // keeps all generated containers and their cached tile visuals alive.
        var dragged = AnyDragTile;
        var oldIndex = dragged is null ? -1 : _visibleTiles.IndexOf(dragged);
        var newIndex = dragged is null ? -1 : desired.IndexOf(dragged);
        if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
            _visibleTiles.Move(oldIndex, newIndex);
        else
            SyncVisibleTiles(desired);

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
        StopEdgePageFlip(resetTrigger: true);
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
        if (FindRowFromSender(sender) is { } row
            && UninstallerService.CanUninstall(row.App))
        {
            UninstallerService.LaunchUninstall(row.App);
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
            row.ShowUninstallBadge = false;
        foreach (var gr in _groupRows)
            gr.ShowRemoveBadge = true;
        _ = RefreshUninstallBadgesAsync();
    }

    private void ExitJiggle()
    {
        _jiggleMode = false;
        _uninstallEligibilityCts?.Cancel();
        _uninstallEligibilityCts?.Dispose();
        _uninstallEligibilityCts = null;
        foreach (var row in _rows)
            row.ShowUninstallBadge = false;
        foreach (var gr in _groupRows)
            gr.ShowRemoveBadge = false;
    }

    private async Task RefreshUninstallBadgesAsync()
    {
        _uninstallEligibilityCts?.Cancel();
        _uninstallEligibilityCts?.Dispose();
        _uninstallEligibilityCts = new CancellationTokenSource();
        var ct = _uninstallEligibilityCts.Token;
        var snapshot = _rows.ToList();

        try
        {
            var removableIds = await Task.Run(() =>
            {
                var result = new HashSet<string>(StringComparer.Ordinal);
                foreach (var row in snapshot)
                {
                    ct.ThrowIfCancellationRequested();
                    if (UninstallerService.CanUninstall(row.App))
                        result.Add(row.App.Id);
                }
                return result;
            }, ct);

            if (!_jiggleMode || ct.IsCancellationRequested)
                return;
            foreach (var row in _rows)
                row.ShowUninstallBadge = removableIds.Contains(row.App.Id);
        }
        catch (OperationCanceledException)
        {
            // Leaving jiggle mode or closing the window cancels the scan.
        }
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
                var dragThreshold = _dragGroupTile is not null
                    ? 26
                    : _tileDragFromGroup ? 6 : 14;
                if (dd <= dragThreshold)
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
                // Show the potential folder target, but wait for mouse-up
                // before creating or modifying a group.
                if (_dragTile is not null)
                    UpdateGroupDropPreview(gridPos);
            }
            else if (_tileDragFromGroup && _dragTile is not null && _openGroup is { } openGroup)
            {
                var gridPos = e.GetPosition(AppsList);
                if (!IsPointOverTile(openGroup, gridPos, 0))
                {
                    var target = ComputeDragTargetIndex(gridPos);
                    var now = Environment.TickCount64;
                    if (target != _dragTargetIndex && now - _lastReorderMs >= 50)
                    {
                        _lastReorderMs = now;
                        _dragTargetIndex = target;
                        BuildGroupMemberDragOutPreview();
                    }
                }
                else if (_reorderPreview is not null)
                {
                    ClearDragPreview();
                    RenderPage(0);
                }
            }
            else if (!_tileDragFromGroup && _dragTile is not null && _openGroup is not null)
            {
                UpdateOpenGroupDropPreview(e.GetPosition(AppsList));
            }

            if (_openGroup is null)
                UpdateEdgePageFlip(e.GetPosition(AppsList));
        }
    }

    private void UpdateEdgePageFlip(System.Windows.Point position)
    {
        const double edgeWidth = 36;
        var direction = position.X < edgeWidth
            ? -1
            : position.X > AppsList.ActualWidth - edgeWidth
                ? 1
                : 0;

        if (direction == 0)
        {
            StopEdgePageFlip(resetTrigger: true);
            return;
        }

        if (_edgePageTriggered || (_edgePageTimer is not null && _edgePageDirection == direction))
            return;

        StopEdgePageFlip(resetTrigger: false);
        _edgePageDirection = direction;
        _edgePageTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(EdgePageDelayMs)
        };
        _edgePageTimer.Tick += (_, _) =>
        {
            var flipDirection = _edgePageDirection;
            StopEdgePageFlip(resetTrigger: false);
            _edgePageTriggered = true;
            if (!_tileDragArmed || AnyDragTile is null)
                return;
            if (flipDirection < 0)
                PreviousPage();
            else
                NextPage();
        };
        _edgePageTimer.Start();
    }

    private void StopEdgePageFlip(bool resetTrigger)
    {
        _edgePageTimer?.Stop();
        _edgePageTimer = null;
        _edgePageDirection = 0;
        if (resetTrigger)
            _edgePageTriggered = false;
    }

    // True while the cursor sits in the middle ~62% of a tile cell: the user
    // is aiming AT that tile (group intent), not at a gap (reorder intent).
    // Freezing the reorder preview here stops the target tile from sliding
    // away under the cursor, so a drop / hover can actually land on it.
    // The wider dead-zone makes grouping easy (macOS never slides the target
    // away from under you).
    private bool IsHoveringTileCenter(System.Windows.Point gridPos)
    {
        const double edge = 0.15;
        foreach (var item in AppsList.Items.OfType<ITileRow>())
        {
            if (AppsList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container)
                continue;
            System.Windows.Point topLeft;
            try { topLeft = container.TranslatePoint(new System.Windows.Point(), AppsList); }
            catch { continue; }
            var x = gridPos.X - topLeft.X;
            var y = gridPos.Y - topLeft.Y;
            if (x < 0 || y < 0 || x > container.ActualWidth || y > container.ActualHeight)
                continue;
            return x > container.ActualWidth * edge && x < container.ActualWidth * (1 - edge)
                && y > container.ActualHeight * edge && y < container.ActualHeight * (1 - edge);
        }
        return false;
    }

    // Hover only previews the potential group target. The actual create/add
    // operation is performed by OnMouseLeftButtonUp after a deliberate drop.
    private void UpdateGroupDropPreview(System.Windows.Point gridPos)
    {
        if (_dragTile is null || _tileDragFromGroup || _openGroup is not null)
        {
            ClearGroupDropPreview();
            return;
        }
        // Once a tile accepted the drag, keep it selected across its whole
        // transformed bounds. Scaling moves its visual edges; recomputing the
        // narrow center zone on every frame made edge hovering oscillate.
        if (_hoverGroupTarget is not null && IsPointOverTile(_hoverGroupTarget, gridPos, 6))
            return;
        var over = IsHoveringTileCenter(gridPos) ? TileAtGridPoint(gridPos) : null;
        if (over is null)
        {
            ClearGroupDropPreview();
            return;
        }
        if (ReferenceEquals(over, _hoverGroupTarget))
            return;
        ClearGroupDropPreview();
        _hoverGroupTarget = over;
        HighlightHoverTarget(over, true);
    }

    private void UpdateOpenGroupDropPreview(System.Windows.Point gridPos)
    {
        if (_openGroup is null)
            return;
        var isOver = IsPointOverTile(_openGroup, gridPos, 0);
        _openGroup.IsDropTarget = isOver;
        _hoverGroupTarget = isOver ? _openGroup : null;
    }

    private bool IsPointOverTile(ITileRow tile, System.Windows.Point point, double tolerance)
    {
        if (AppsList.ItemContainerGenerator.ContainerFromItem(tile) is not ListBoxItem container)
            return false;
        try
        {
            var topLeft = container.TranslatePoint(new System.Windows.Point(), AppsList);
            var bottomRight = container.TranslatePoint(
                new System.Windows.Point(container.ActualWidth, container.ActualHeight), AppsList);
            var left = Math.Min(topLeft.X, bottomRight.X) - tolerance;
            var top = Math.Min(topLeft.Y, bottomRight.Y) - tolerance;
            var right = Math.Max(topLeft.X, bottomRight.X) + tolerance;
            var bottom = Math.Max(topLeft.Y, bottomRight.Y) + tolerance;
            return point.X >= left && point.X <= right && point.Y >= top && point.Y <= bottom;
        }
        catch
        {
            return false;
        }
    }

    // The tile being dragged, whether it is an app or a whole folder.
    private ITileRow? AnyDragTile => (ITileRow?)_dragTile ?? _dragGroupTile;

    // Folders have no single icon: their drag ghost shows the first preview
    // icon (the same one the tile shows top-left).
    private static ImageSource? MakeGroupGhostImage(GroupRow? group)
        => group?.Previews.FirstOrDefault(p => p is not null);

    private void ClearGroupDropPreview()
    {
        if (_hoverGroupTarget is GroupRow group)
            group.IsDropTarget = false;
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
            AnimateTileScale(container, 1.24);
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
        foreach (var item in AppsList.Items.OfType<ITileRow>())
        {
            if (ReferenceEquals(item, AnyDragTile)
                || AppsList.ItemContainerGenerator.ContainerFromItem(item) is not ListBoxItem container)
                continue;
            System.Windows.Point topLeft;
            try { topLeft = container.TranslatePoint(new System.Windows.Point(), AppsList); }
            catch { continue; }
            if (gridPos.X >= topLeft.X && gridPos.X <= topLeft.X + container.ActualWidth
                && gridPos.Y >= topLeft.Y && gridPos.Y <= topLeft.Y + container.ActualHeight)
                return item;
        }
        return null;
    }

    // ── Drag: mouse-up (group create / add / remove / drop) ───────
    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        StopEdgePageFlip(resetTrigger: true);
        var swiped = _dragConsumed;
        var previewGroupTarget = _hoverGroupTarget as GroupRow;
        _dragConsumed = false;
        _dragOrigin = null;
        StopHoldTimer();
        ClearGroupDropPreview();

        // Deletion mode is modal: clicking a tile never launches it, closes
        // the window, or leaves stale drag state behind. Badge buttons still
        // receive their Click event after this preview handler returns.
        if (_jiggleMode)
        {
            var jiggleClickSource = e.OriginalSource as DependencyObject;
            var position = e.GetPosition(this);
            double appsTop;
            try { appsTop = AppsList.TranslatePoint(new System.Windows.Point(), this).Y; }
            catch { appsTop = 110; }
            if (position.Y < appsTop
                && !IsWithinSearch(jiggleClickSource)
                && !IsWithinSettingsGear(jiggleClickSource)
                && !IsWithinControl(jiggleClickSource)
                && FindTileContainer(jiggleClickSource) is null)
            {
                ExitJiggle();
            }
            ResetDragState();
            return;
        }

        // A just-closed ContextMenu mirrors a mouse-up onto the tile — never
        // treat that as a launch/open click (Open-folder bug).
        if (_ignoreNextClick)
        {
            _ignoreNextClick = false;
            ResetDragState();
            return;
        }

        // Some nested controls can coalesce intermediate MouseMove events.
        // Re-evaluate displacement on release so an intended member drag is
        // never mistaken for a click that launches the application.
        if (!_tileDragArmed && _tileDragFromGroup && AnyDragTile is not null)
        {
            var releasePosition = e.GetPosition(this);
            if ((releasePosition - _tileDragStart).Length > 6)
                _tileDragArmed = true;
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
                if (IsPointOverTile(og, e.GetPosition(AppsList), 0))
                {
                    ResetDragState();
                    return;
                }
                var previewIndex = _reorderPreview?.IndexOf(source) ?? -1;
                var insertBefore = previewIndex >= 0 && _reorderPreview is not null
                    ? _reorderPreview.Skip(previewIndex + 1).FirstOrDefault()
                    : null;
                var appendWhenNoAnchor = previewIndex >= 0
                    && previewIndex == _reorderPreview!.Count - 1;
                ResetDragState();
                RemoveFromGroup(source, insertBefore, appendWhenNoAnchor);
                return;
            }

            var targetContainer = FindTileContainer(e.OriginalSource as DependencyObject);
            var targetGroup = targetContainer?.DataContext as GroupRow ?? previewGroupTarget;
            if (source is not null && targetGroup is not null)
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
        {
            ResetDragState();
            return;
        }

        // A click candidate stores its tile on mouse-down. Once mouse-up has
        // confirmed that no drag happened, clear that candidate before the
        // click opens a folder. Otherwise the next tiny mouse movement would
        // pick up the folder ghost (which looks like its first member icon).
        ResetDragState();

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

        // Inside an expanded inline folder, member icons launch normally;
        // clicking anywhere else collapses the folder in place.
        if (_openGroup is not null)
        {
            if (FindInlineGroupMember(clickSource) is { } groupApp)
                TryLaunch(groupApp);
            else
                CloseGroup();
            return;
        }

        if (FindTileContainer(clickSource) is { } container)
        {
            if (container.DataContext is GroupRow g)
            {
                if (ReferenceEquals(_openGroup, g))
                    CloseGroup();
                else
                    OpenGroup(g);
                return;
            }
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
        UpdateCornerSelection(App.Settings.HotCorner);
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
        UpdateCornerSelection(App.Settings.HotCorner);
        CommitSettings();
    }

    private void UpdateCornerSelection(AppHotCorner selected)
    {
        if (TopLeftCornerButton is null)
            return;

        var selectedBackground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x38, 0x6C, 0x8C, 0xFF));
        var selectedBorder = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xA0, 0x7C, 0x9C, 0xFF));
        var normalBackground = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF));
        var normalBorder = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        selectedBackground.Freeze();
        selectedBorder.Freeze();
        normalBackground.Freeze();
        normalBorder.Freeze();

        var choices = new (System.Windows.Controls.Button Button, AppHotCorner Corner)[]
        {
            (TopLeftCornerButton, AppHotCorner.TopLeft),
            (TopRightCornerButton, AppHotCorner.TopRight),
            (BottomLeftCornerButton, AppHotCorner.BottomLeft),
            (BottomRightCornerButton, AppHotCorner.BottomRight)
        };
        var enabled = HotCornerToggle.IsChecked == true;
        foreach (var (button, corner) in choices)
        {
            var isSelected = corner == selected;
            button.Background = isSelected ? selectedBackground : normalBackground;
            button.BorderBrush = isSelected ? selectedBorder : normalBorder;
            button.Opacity = enabled ? 1.0 : 0.46;
            button.IsEnabled = enabled;
        }
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

    private void OnAppContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        StopHoldTimer();
        ResetDragState();
        // Arm suppression before the ContextMenu popup receives its click;
        // setting this only in the MenuItem.Click handler is too late.
        _ignoreNextClick = true;
    }

    private void OnAppContextMenuClosing(object sender, ContextMenuEventArgs e)
    {
        // Clear only after the popup's current input route has completed.
        _ = Dispatcher.BeginInvoke(
            new Action(() => _ignoreNextClick = false),
            DispatcherPriority.ContextIdle);
    }

    private void OnOpenAppFolder(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
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
        CommitSettings();
        SettingsOverlay.Visibility = Visibility.Collapsed;
        if (_openGroup is not null)
            CloseGroup(false);
        ApplyFilter(true);
    }

    private void CommitSettings() => App.SaveSettings(App.Settings);

    private void OnSaveGroupName(object sender, RoutedEventArgs e)
    {
        CommitGroupName();
        GroupMembers.Focus();
    }

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
            if (Keyboard.FocusedElement is System.Windows.Controls.TextBox { DataContext: GroupRow row } inlineEditor)
            {
                inlineEditor.Text = row.DisplayName;
                Keyboard.ClearFocus();
                e.Handled = true;
                return;
            }
            // First cancel an in-flight drag; then step out of the overlay stack.
            if (_tileDragArmed || AnyDragTile is not null)
            {
                StopHoldTimer();
                ClearGroupDropPreview();
                ResetDragState();
            }
            else if (SettingsOverlay.Visibility == Visibility.Visible)
                OnCloseSettings(sender, e);
            else if (_openGroup is not null)
                CloseGroup();
            else if (_jiggleMode)
                ExitJiggle();
            else if (SearchBox.Text.Length > 0)
                SearchBox.Text = "";
            else
                Dismiss();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Enter)
        {
            if (Keyboard.FocusedElement is System.Windows.Controls.TextBox { DataContext: GroupRow } inlineEditor)
            {
                CommitInlineGroupName(inlineEditor);
                Keyboard.ClearFocus();
                e.Handled = true;
                return;
            }
            // The window-level preview event runs before TextBox.KeyDown, so
            // commit here when Enter is pressed in the editable group title.
            if (_openGroup is not null && GroupNameBox.IsKeyboardFocusWithin)
            {
                CommitGroupName();
                Keyboard.ClearFocus();
            }
            else if (_openGroup?.SelectedMember is { } member)
                TryLaunch(member);
            else
                LaunchSelected();
            e.Handled = true;
            return;
        }
        if (SettingsOverlay.Visibility == Visibility.Visible)
            return;
        if (e.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            MoveKeyboardSelection(e.Key);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.PageDown)
        {
            if (_jiggleMode)
                ExitJiggle();
            else if (_openGroup is not null)
                ChangeOpenGroupPage(_openGroup.MemberPageIndex + 1);
            else
                NextPage();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.PageUp)
        {
            if (_jiggleMode)
                ExitJiggle();
            else if (_openGroup is not null)
                ChangeOpenGroupPage(_openGroup.MemberPageIndex - 1);
            else
                PreviousPage();
            e.Handled = true;
            return;
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

            // Decode OFF the UI thread (CPU work: PNG decode + scale) and only
            // hop to the UI thread for the row update — keeps paging and
            // startup snappy even with a full page of cold icons.
            var decoded = await Task.Run(() => DecodeIconFile(path, ct));
            if (decoded is null)
                continue;
            var source = decoded;

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

    // Off-thread icon decode: create, scale down, freeze (free-threaded) so
    // the UI thread only assigns the finished bitmap. Returns null on error.
    private static ImageSource? DecodeIconFile(string path, CancellationToken ct)
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
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            return null;
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
