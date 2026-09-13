using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using LaunchpadClone.Core.Settings;

namespace LaunchpadClone.UI;

public partial class App : System.Windows.Application
{
    private static readonly AppSettingsStore SettingsStore = AppSettingsStore.Default();

    internal static AppSettings Settings { get; private set; } = new();

    /// <summary>Raised after settings were saved — open windows re-apply.</summary>
    internal static event Action? SettingsChanged;

    internal static readonly List<MainWindow> OpenWindows = new();

    private NotifyIcon? _trayIcon;
    private DispatcherTimer? _hotCornerTimer;

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
        try
        {
            await SettingsStore.SaveAsync(settings);
        }
        catch
        {
            // persistence is best-effort
        }
    }

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
        _trayIcon?.Dispose();
        Shutdown();
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

        const int trigger = 4;
        var width = (int)SystemParameters.PrimaryScreenWidth;
        var height = (int)SystemParameters.PrimaryScreenHeight;

        var inCorner = Settings.HotCorner switch
        {
            AppHotCorner.TopLeft => point.X <= trigger && point.Y <= trigger,
            AppHotCorner.TopRight => point.X >= width - trigger && point.Y <= trigger,
            AppHotCorner.BottomLeft => point.X <= trigger && point.Y >= height - trigger,
            AppHotCorner.BottomRight => point.X >= width - trigger && point.Y >= height - trigger,
            _ => false
        };

        if (inCorner)
            OpenLaunchpad();
    }
}
