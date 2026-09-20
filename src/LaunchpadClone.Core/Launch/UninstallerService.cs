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
    private sealed record UninstallEntry(string DisplayName, string Command);

    private static readonly Lazy<IReadOnlyList<UninstallEntry>> RegistryEntries =
        new(LoadRegistryEntries, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool>
        EligibilityCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns true only when Launchpad can identify a real uninstall route.
    /// Store apps exposed by discovery are non-system packages; desktop apps
    /// need either a matching registry entry or a nearby uninstaller.
    /// </summary>
    public static bool CanUninstall(AppItem app) =>
        EligibilityCache.GetOrAdd(app.Id, _ => DetectCanUninstall(app));

    private static bool DetectCanUninstall(AppItem app)
    {
        if (app.Kind == AppKind.Uwp)
            return true;

        var target = app.Kind == AppKind.Shortcut
            ? ResolveShortcutTarget(app.TargetPath)
            : app.TargetPath;
        return FindRegistryUninstallString(app.DisplayName, target) is not null
            || FindNearbyUninstaller(target) is not null;
    }

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

        var nearbyUninstaller = FindNearbyUninstaller(target);
        if (nearbyUninstaller is not null)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = nearbyUninstaller,
                UseShellExecute = true
            });
            return;
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
        var needle = displayName.Trim().ToLowerInvariant();
        if (needle.Length == 0)
            return null;
        // Strip the extension: registry DisplayNames never contain ".exe",
        // so "code.exe" would never match "Visual Studio Code" otherwise.
        var targetFile = targetPath is null
            ? null
            : Path.GetFileNameWithoutExtension(targetPath).ToLowerInvariant();

        foreach (var entry in RegistryEntries.Value)
        {
            var nameLower = entry.DisplayName.ToLowerInvariant();
            // Exact match wins. Substring matches require a meaningful needle
            // so a short name cannot select an unrelated uninstall command.
            var matches = nameLower.Equals(needle, StringComparison.Ordinal)
                || (needle.Length >= 4 && nameLower.Contains(needle, StringComparison.Ordinal))
                || (targetFile is not null && targetFile.Length >= 4
                    && nameLower.Contains(targetFile, StringComparison.Ordinal));
            if (matches)
                return entry.Command;
        }

        return null;
    }

    private static IReadOnlyList<UninstallEntry> LoadRegistryEntries()
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

        var result = new List<UninstallEntry>();

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

                            if (sub!.GetValue("UninstallString") is string str && str.Trim().Length > 0)
                                result.Add(new UninstallEntry(name, str));
                            else if (sub.GetValue("QuietUninstallString") is string quiet && quiet.Trim().Length > 0)
                                result.Add(new UninstallEntry(name, quiet));
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

        return result;
    }

    private static string? FindNearbyUninstaller(string? targetPath)
    {
        var directory = targetPath is null ? null : Path.GetDirectoryName(targetPath);
        if (directory is null || !Directory.Exists(directory))
            return null;

        try
        {
            return Directory.EnumerateFiles(directory, "*.exe")
                .FirstOrDefault(path => Path.GetFileName(path)
                    .Contains("unins", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception)
        {
            return null;
        }
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
