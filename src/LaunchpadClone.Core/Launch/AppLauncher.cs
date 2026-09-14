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

        // Raw exes need their own folder as the working directory —
        // otherwise relative resource/DLL loads break. Shortcuts keep
        // their embedded working dir/args by launching the .lnk itself.
        string? workingDir = null;
        try
        {
            workingDir = Path.GetDirectoryName(app.TargetPath);
        }
        catch
        {
            workingDir = null;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = app.TargetPath,
            WorkingDirectory = workingDir is not null && Directory.Exists(workingDir) ? workingDir : string.Empty,
            UseShellExecute = true
        });
    }
}
