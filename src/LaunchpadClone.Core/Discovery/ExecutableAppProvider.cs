using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using LaunchpadClone.Core.Models;

namespace LaunchpadClone.Core.Discovery;

public interface IExecutableAppProvider
{
    /// <summary>
    /// Scans the configured roots for launchable .exe files — apps installed
    /// without a Start Menu shortcut of their own.
    /// </summary>
    Task<List<AppItem>> GetInstalledAppsAsync(CancellationToken ct = default);
}

/// <summary>
/// Finds installed apps by walking Program Files (and the per-user
/// %LocalAppData%\Programs folder) for .exe files. Heuristics filter noise
/// (installers, uninstallers, crash handlers): name fragments, required
/// FileDescription version info, and a "largest exe per display name" dedupe.
/// Over-inclusion is preferred over missing a real app — ranking/filtering is
/// a later phase.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ExecutableAppProvider : IExecutableAppProvider
{
    private static readonly string[] NoiseNameFragments =
    [
        "unins",   // uninstallers
        "setup",   // installers / bootstrappers
        "update",  // auto-update agents (Squirrel "Update.exe" etc.)
        "updater",
        "crash",   // crash handlers
        "helper",  // browser/app helpers
        "elevat",  // elevation helpers
        "handler",
        "broker",
        "agent"
    ];

    /// <summary>
    /// Checked against the FileDescription too — many background services and
    /// SDK tools have a neutral exe name but a descriptive display name.
    /// </summary>
    private static readonly string[] NoiseDisplayNameFragments =
    [
        "tool",        // SDK/developer tools ("...Compiler Tool", "Xml Schema Tool")
        "compiler",
        "service",
        "monitor",
        "licens",
        "command line",
        "console",
        "installer",
        "plugin",
        "plug-in",
        "extension",
        "library",
        "runtime",
        "distribut",
        "sdk"
    ];

    private static readonly string[] NoiseDirectoryFragments =
    [
        "common files",       // shared components, not end-user apps
        "windows defender",   // OS security components
        "node_modules",
        "microsoft sdks",     // developer toolchains
        "windows kits",
        "reference assemblies",
        "msbuild",
        "dotnet",             // .NET runtime/SDK host binaries
        "shared",             // product-shared components
        "plugins",
        "addins",
        "modules"
    ];

    /// <summary>
    /// End-user app binaries are almost always bigger than this; smaller exes
    /// are overwhelmingly helpers/tools. Deliberately aggressive — a missing
    /// tiny utility is less painful than hundreds of SDK binaries.
    /// </summary>
    private const long MinExeSizeBytes = 512 * 1024;

    private readonly List<string> _roots;

    public ExecutableAppProvider(IReadOnlyList<string>? roots = null)
    {
        _roots = (roots ?? DefaultRoots())
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<List<AppItem>> GetInstalledAppsAsync(CancellationToken ct = default)
    {
        var existingRoots = _roots.Where(Directory.Exists).ToList();
        if (existingRoots.Count == 0)
            return [];

        var exeFiles = existingRoots
            .SelectMany(SafeEnumerateExeFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var candidates = new ConcurrentBag<(string Path, string DisplayName)>();

        await Parallel.ForEachAsync(exeFiles,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
            (path, _) =>
            {
                var displayName = TryBuildDisplayName(path);
                if (displayName is not null)
                    candidates.Add((path, displayName));
                return ValueTask.CompletedTask;
            });

        return BuildFinalItems(candidates);
    }

    /// <summary>
    /// Default scan roots: machine-wide Program Files (both views, via the
    /// ProgramW6432 variable too) plus the per-user Programs folder where
    /// user-scope installers (Chrome, VS Code user setup, ...) put apps.
    /// </summary>
    public static List<string> DefaultRoots() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetEnvironmentVariable("ProgramW6432") ?? string.Empty,
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs")
    ];

    // Multiple exes of the same product often carry the same FileDescription
    // (e.g. "Google Chrome" in stable and beta folders, or the app exe plus a
    // same-described helper). Keep exactly one app per display name — the
    // largest exe, which is almost always the main binary, not a helper.
    private static List<AppItem> BuildFinalItems(ConcurrentBag<(string Path, string DisplayName)> candidates) =>
        candidates
            .GroupBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderByDescending(c => GetFileSizeSafe(c.Path))
                .Select(c => new AppItem(
                    Id: AppItem.ComputeId(c.Path),
                    DisplayName: c.DisplayName,
                    TargetPath: c.Path,
                    Kind: AppKind.Executable,
                    IconCachePath: null,
                    LastSeenUtc: DateTime.UtcNow))
                .First())
            .OrderBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static long GetFileSizeSafe(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string? TryBuildDisplayName(string exePath)
    {
        var fileName = Path.GetFileName(exePath);
        var directory = Path.GetDirectoryName(exePath) ?? string.Empty;

        // Cheap size check first — avoids a version-info read on the bulk of
        // small helper/tool exes.
        if (GetFileSizeSafe(exePath) < MinExeSizeBytes)
            return null;

        if (NoiseNameFragments.Any(fragment => fileName.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            return null;
        if (NoiseDirectoryFragments.Any(fragment => directory.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            return null;

        FileVersionInfo versionInfo;
        try
        {
            versionInfo = FileVersionInfo.GetVersionInfo(exePath);
        }
        catch (Exception)
        {
            return null; // locked/corrupt file — skip, don't stop the scan
        }

        // Heuristic: end-user app binaries almost always carry a
        // FileDescription ("Google Chrome"); helper/system exes often don't.
        var displayName = versionInfo.FileDescription?.Trim();
        if (string.IsNullOrEmpty(displayName))
            return null;
        if (NoiseDisplayNameFragments.Any(fragment => displayName.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            return null;

        return displayName;
    }

    private static IEnumerable<string> SafeEnumerateExeFiles(string root)
    {
        // Same defensive pattern as the .lnk walk in AppDiscoveryService:
        // IgnoreInaccessible skips denied subfolders instead of aborting the
        // whole walk, and the result is materialized so any remaining error
        // surfaces here rather than lazily at the caller's Distinct().
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        try
        {
            return Directory.EnumerateFiles(root, "*.exe", options).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (IOException)
        {
            return [];
        }
    }
}