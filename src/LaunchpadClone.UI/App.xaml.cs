using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using LaunchpadClone.Core.Cache;
using LaunchpadClone.Core.Discovery;
using LaunchpadClone.Core.Models;
using LaunchpadClone.Core.Settings;

namespace LaunchpadClone.UI;

public partial class App : System.Windows.Application
{
    private static readonly AppSettingsStore SettingsStore = AppSettingsStore.Default();
    private static readonly SemaphoreSlim SettingsSaveGate = new(1, 1);
    private static int _settingsRevision;

    internal static AppSettings Settings { get; private set; } = new();

    /// <summary>Raised after settings were saved — open windows re-apply.</summary>
    internal static event Action? SettingsChanged;

    internal static readonly List<MainWindow> OpenWindows = new();

    private NotifyIcon? _trayIcon;
    private DispatcherTimer? _hotCornerTimer;
    private DispatcherTimer? _backgroundRescanTimer;
    private AppDiscoveryService? _backgroundDiscovery;
    private readonly AppCache _appCache = AppCache.Default();
    private bool _backgroundRescanRunning;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The app lives in the tray; windows come and go.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        Settings = await SettingsStore.LoadAsync();

        InitTray();
        StartHotCorner();
        StartBackgroundRescan();
        OpenLaunchpad();
    }

    internal static void OpenLaunchpad()
    {
        if (OpenWindows.Count > 0)
        {
            var existing = OpenWindows[0];
            existing.Activate();
            return;
        }

        var window = new MainWindow();
        OpenWindows.Add(window);
        window.Closed += (_, _) => OpenWindows.Remove(window);
        window.Show();
    }

    internal static async void SaveSettings(AppSettings settings)
    {
        Settings = settings;
        SettingsChanged?.Invoke();
        var snapshot = CopySettings(settings);
        var revision = Interlocked.Increment(ref _settingsRevision);
        try
        {
            await SettingsSaveGate.WaitAsync();
            if (revision == Volatile.Read(ref _settingsRevision))
                await SettingsStore.SaveAsync(snapshot);
        }
        catch
        {
            // persistence is best-effort
        }
        finally
        {
            SettingsSaveGate.Release();
        }
    }

    private static AppSettings CopySettings(AppSettings source) => new()
    {
        HotCornerEnabled = source.HotCornerEnabled,
        HotCorner = source.HotCorner,
        TileScale = source.TileScale,
        HotKey = source.HotKey
    };

    private void InitTray()
    {
        try
        {
            var icon = System.Drawing.SystemIcons.Application;
            try
            {
                var module = Process.GetCurrentProcess().MainModule?.FileName;
                if (module is not null)
                    icon = System.Drawing.Icon.ExtractAssociatedIcon(module) ?? icon;
            }
            catch
            {
                // fall back to the generic application icon
            }

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Open Launchpad", null, (_, _) => OpenLaunchpad());
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => ExitApp());

            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = icon,
                Text = "Launchpad",
                Visible = true,
                ContextMenuStrip = menu
            };
            _trayIcon.MouseDoubleClick += (_, _) => OpenLaunchpad();
        }
        catch
        {
            // tray unavailable (e.g. shell not ready) — launcher still works
        }
    }

    private void ExitApp()
    {
        _backgroundRescanTimer?.Stop();
        _trayIcon?.Dispose();
        Shutdown();
    }

    // Poll cheaply once a minute, but perform a full discovery only when the
    // cache is at least ten minutes old and the launcher overlay is closed.
    // This keeps UWP/raw-exe changes fresh without interrupting interaction.
    private void StartBackgroundRescan()
    {
        var iconCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LaunchpadClone", "icons", "hi");
        _backgroundDiscovery = new AppDiscoveryService(
            new UwpPackageProvider(iconCacheDir), new ExecutableAppProvider());
        _backgroundRescanTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        _backgroundRescanTimer.Tick += async (_, _) => await RefreshCacheWhileIdleAsync();
        _backgroundRescanTimer.Start();
    }

    private async Task RefreshCacheWhileIdleAsync()
    {
        if (_backgroundRescanRunning || OpenWindows.Count > 0
            || _appCache.IsFresh(TimeSpan.FromMinutes(10))
            || _backgroundDiscovery is null)
        {
            return;
        }

        _backgroundRescanRunning = true;
        try
        {
            var cached = await _appCache.LoadAsync();
            var iconPaths = cached
                .Where(a => !string.IsNullOrWhiteSpace(a.IconCachePath))
                .GroupBy(a => a.Id, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().IconCachePath!, StringComparer.Ordinal);
            var discovered = await _backgroundDiscovery.ScanAllAsync();
            var merged = discovered
                .Select(a => iconPaths.TryGetValue(a.Id, out var path)
                    ? a.WithIconCachePath(path)
                    : a)
                .OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            await _appCache.SaveAsync(merged);
        }
        catch
        {
            // Background refresh is best-effort; the existing cache remains.
        }
        finally
        {
            _backgroundRescanRunning = false;
        }
    }

    // Mouse parked in the configured screen corner opens the launcher.
    private void StartHotCorner()
    {
        _hotCornerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _hotCornerTimer.Tick += (_, _) => CheckHotCorner();
        _hotCornerTimer.Start();
    }

    private void CheckHotCorner()
    {
        if (!Settings.HotCornerEnabled || OpenWindows.Count > 0)
            return;

        if (!GetCursorPos(out var point))
            return;

        // GetCursorPos and WinForms screen bounds both use physical desktop
        // pixels. WPF SystemParameters uses DPI-scaled units, which made the
        // right and bottom corners unreachable above 100% display scaling.
        // Resolve the monitor under the pointer so multi-monitor and negative
        // desktop coordinates work as well.
        const int trigger = 10;
        var screen = System.Windows.Forms.Screen.FromPoint(
            new System.Drawing.Point(point.X, point.Y));
        var bounds = screen.Bounds;

        var inCorner = Settings.HotCorner switch
        {
            AppHotCorner.TopLeft => point.X <= bounds.Left + trigger && point.Y <= bounds.Top + trigger,
            AppHotCorner.TopRight => point.X >= bounds.Right - trigger && point.Y <= bounds.Top + trigger,
            AppHotCorner.BottomLeft => point.X <= bounds.Left + trigger && point.Y >= bounds.Bottom - trigger,
            AppHotCorner.BottomRight => point.X >= bounds.Right - trigger && point.Y >= bounds.Bottom - trigger,
            _ => false
        };

        if (inCorner)
            OpenLaunchpad();
    }
}
