using System.Diagnostics;
using System.Runtime.Versioning;
using LaunchpadClone.Core.Models;

namespace LaunchpadClone.Core.Launch;

/// <summary>
/// Launches a discovered app: .lnk via ShellExecute (keeps working dir and
/// args), UWP via explorer.exe shell:AppsFolder\AUMID, raw Executable via
/// ShellExecute on the exe path (falls into the same branch as .lnk).
/// </summary>
[SupportedOSPlatform("windows")]
public static class AppLauncher
{
    public static void Launch(AppItem app)
    {
        if (app.Kind == AppKind.Uwp)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"shell:AppsFolder\\{app.TargetPath}",
                UseShellExecute = true
            });
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = app.TargetPath,
            UseShellExecute = true
        });
    }
}
