using System.Diagnostics;
using System.Runtime.Versioning;
using LaunchpadClone.Core.Models;
using LaunchpadClone.Core.Native;

namespace LaunchpadClone.Core.Launch;

/// <summary>
/// Launches the Windows uninstaller for an app (the Launchpad "✕" in
/// jiggle mode). Order of attempts:
/// 1. registry Uninstall entry matching the display name,
/// 2. an unins*.exe next to the resolved target,
/// 3. fallback: Windows Settings > Apps &amp; features.
/// </summary>
[SupportedOSPlatform("windows")]
public static class UninstallerService
{
    public static void LaunchUninstall(AppItem app)
    {
        if (app.Kind == AppKind.Uwp)
        {
            OpenAppsSettings();
            return;
        }

        var target = app.Kind == AppKind.Shortcut
            ? ResolveShortcutTarget(app.TargetPath)
            : app.TargetPath;

        var uninstallString = FindRegistryUninstallString(app.DisplayName, target);
        if (uninstallString is not null)
        {
            StartUninstallString(uninstallString);
            return;
        }

        var directory = target is null ? null : Path.GetDirectoryName(target);
        if (directory is not null && Directory.Exists(directory))
        {
            try
            {
                var unins = Directory.EnumerateFiles(directory, "*.exe")
                    .FirstOrDefault(p => Path.GetFileName(p)
                        .Contains("unins", StringComparison.OrdinalIgnoreCase));
                if (unins is not null)
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = unins,
                        UseShellExecute = true
                    });
                    return;
                }
            }
            catch (Exception)
            {
                // unreadable directory — fall through to Settings
            }
        }

        OpenAppsSettings();
    }

    private static string? ResolveShortcutTarget(string lnkPath)
    {
        // IShellLinkW is STA — shared StaRunner helper (see discovery).
        return StaRunner.RunSilent(() => ShellLinkResolver.Resolve(lnkPath))?.TargetPath;
    }
    private static string? FindRegistryUninstallString(string displayName, string? targetPath)
    {
        string[] keyPaths =
        [
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
            @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        ];
        Microsoft.Win32.RegistryKey[] roots =
        [
            Microsoft.Win32.Registry.CurrentUser,
            Microsoft.Win32.Registry.LocalMachine
        ];

        var needle = displayName.Trim().ToLowerInvariant();
        if (needle.Length == 0)
            return null;
        // Strip the extension: registry DisplayNames never contain ".exe",
        // so "code.exe" would never match "Visual Studio Code" otherwise.
        var targetFile = targetPath is null
            ? null
            : Path.GetFileNameWithoutExtension(targetPath).ToLowerInvariant();

        foreach (var root in roots)
        {
            foreach (var keyPath in keyPaths)
            {
                Microsoft.Win32.RegistryKey? uninstallKey = null;
                try
                {
                    uninstallKey = root.OpenSubKey(keyPath);
                    if (uninstallKey is null)
                        continue;

                    foreach (var subName in uninstallKey.GetSubKeyNames())
                    {
                        Microsoft.Win32.RegistryKey? sub = null;
                        try
                        {
                            sub = uninstallKey.OpenSubKey(subName);
                            var name = sub?.GetValue("DisplayName") as string;
                            if (string.IsNullOrWhiteSpace(name))
                                continue;

                            var nameLower = name.ToLowerInvariant();
                            // Exact match wins. Substring matches require a
                            // meaningful needle (>= 4 chars) so "Mail" cannot
                            // match "Gmail Notifier" and uninstall the wrong app.
                            var matches = nameLower.Equals(needle, StringComparison.Ordinal)
                                || (needle.Length >= 4 && nameLower.Contains(needle, StringComparison.Ordinal))
                                || (targetFile is not null && targetFile.Length >= 4
                                    && nameLower.Contains(targetFile, StringComparison.Ordinal));
                            if (!matches)
                                continue;

                            if (sub!.GetValue("UninstallString") is string str && str.Trim().Length > 0)
                                return str;
                            if (sub.GetValue("QuietUninstallString") is string quiet && quiet.Trim().Length > 0)
                                return quiet;
                        }
                        catch (Exception)
                        {
                            // unreadable entry — skip
                        }
                        finally
                        {
                            sub?.Dispose();
                        }
                    }
                }
                catch (Exception)
                {
                    // registry hive not readable — skip
                }
                finally
                {
                    uninstallKey?.Dispose();
                }
            }
        }

        return null;
    }

    // UninstallString values come in shapes like "\"C:\\...\\unins000.exe\""
    // or "MsiExec.exe /X{GUID}" — split the exe from its arguments.
    private static void StartUninstallString(string uninstallString)
    {
        var text = uninstallString.Trim();
        string exe;
        var args = string.Empty;

        if (text.StartsWith('"'))
        {
            var closing = text.IndexOf('"', 1);
            if (closing > 0)
            {
                exe = text[1..closing];
                args = text[(closing + 1)..].Trim();
            }
            else
            {
                exe = text.Trim('"');
            }
        }
        else
        {
            var space = text.IndexOf(' ');
            exe = space > 0 ? text[..space] : text;
            args = space > 0 ? text[(space + 1)..] : string.Empty;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            UseShellExecute = true
        });
    }

    private static void OpenAppsSettings()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "ms-settings:appsfeatures",
            UseShellExecute = true
        });
    }
}
