using System.Collections.Concurrent;
using LaunchpadClone.Core.Models;
using LaunchpadClone.Core.Native;

namespace LaunchpadClone.Core.Discovery;

public interface IAppDiscoveryService
{
    /// <summary>Full discovery — called at startup or on manual refresh.</summary>
    Task<IReadOnlyList<AppItem>> ScanAllAsync(CancellationToken ct = default);

    /// <summary>Re-analyze a single .lnk — for watcher delta refresh (phase 2).</summary>
    Task<AppItem?> ScanShortcutAsync(string lnkPath, CancellationToken ct = default);
}

public sealed class AppDiscoveryService : IAppDiscoveryService
{
    private static readonly string[] StartMenuFolders =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
    ];

    private readonly IUwpPackageProvider _uwpProvider;
    private readonly IExecutableAppProvider? _executableProvider;

    public AppDiscoveryService(IUwpPackageProvider uwpProvider, IExecutableAppProvider? executableProvider = null)
    {
        _uwpProvider = uwpProvider;
        _executableProvider = executableProvider;
    }

    public async Task<IReadOnlyList<AppItem>> ScanAllAsync(CancellationToken ct = default)
    {
        var shortcutsTask = ScanShortcutsCoreAsync(ct);
        var uwpTask = _uwpProvider.GetInstalledAppsAsync(ct);
        var executablesTask = _executableProvider is null
            ? Task.FromResult(new List<AppItem>())
            : _executableProvider.GetInstalledAppsAsync(ct);
        await Task.WhenAll(shortcutsTask, uwpTask, executablesTask);

        var all = new List<AppItem>(
            shortcutsTask.Result.Items.Count + uwpTask.Result.Count + executablesTask.Result.Count);
        all.AddRange(shortcutsTask.Result.Items);
        all.AddRange(uwpTask.Result);

        // Raw-exe items whose exe is ALREADY launched via a resolved Start
        // Menu shortcut would appear twice — filter them by the exe paths
        // that shortcut resolution actually landed on.
        all.AddRange(executablesTask.Result.Where(exe =>
            !shortcutsTask.Result.ResolvedExePaths.Contains(exe.TargetPath)));
        return all;
    }

    /// <summary>
    /// Shortcut scan outcome: the app items plus every exe path their .lnk
    /// targets resolved to (used to dedupe the raw-exe scan).
    /// </summary>
    private sealed record ShortcutScanOutcome(List<AppItem> Items, IReadOnlySet<string> ResolvedExePaths);

    public Task<AppItem?> ScanShortcutAsync(string lnkPath, CancellationToken ct = default)
        => Task.Run(() => ResolveShortcutAppItem(lnkPath).Item, ct);

    private async Task<ShortcutScanOutcome> ScanShortcutsCoreAsync(CancellationToken ct)
    {
        // Materialized up-front: enumeration itself can throw on protected
        // subfolders, so it must not stay lazy (see SafeEnumerateLnkFiles).
        var lnkFiles = StartMenuFolders
            .Where(s => !string.IsNullOrWhiteSpace(s) && Directory.Exists(s))
            .SelectMany(SafeEnumerateLnkFiles)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var results = new ConcurrentBag<AppItem>();
        var resolvedExePaths = new ConcurrentBag<string>();

        // NOTE: ShellLinkResolver uses STA COM (IShellLinkW). Parallel.ForEachAsync
        // runs on MTA pool threads, so each resolve is marshalled onto a
        // dedicated STA thread inside ResolveShortcutAppItem.
        await Parallel.ForEachAsync(lnkFiles,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct },
            (path, _) =>
            {
                var (item, resolvedExe) = ResolveShortcutAppItem(path);
                if (item is not null)
                    results.Add(item);
                if (resolvedExe is not null)
                    resolvedExePaths.Add(resolvedExe);
                return ValueTask.CompletedTask;
            });

        return new ShortcutScanOutcome(
            results.ToList(),
            resolvedExePaths.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> SafeEnumerateLnkFiles(string root)
    {
        // EnumerationOptions.IgnoreInaccessible skips denied subfolders instead of
        // aborting the whole walk. We still materialize to a List so any
        // remaining error surfaces here (where we can swallow it) rather than
        // lazily at the caller's Distinct()/ToList() site.
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        try
        {
            return Directory.EnumerateFiles(root, "*.lnk", options).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            return Enumerable.Empty<string>();
        }
        catch (DirectoryNotFoundException)
        {
            return Enumerable.Empty<string>();
        }
        catch (IOException)
        {
            return Enumerable.Empty<string>();
        }
    }

    /// <summary>
    /// Resolves a single .lnk into an AppItem. Also returns the exe path the
    /// shortcut target landed on, so ScanAllAsync can dedupe raw-exe scan
    /// results against shortcut-based apps.
    /// </summary>
    private static (AppItem? Item, string? ResolvedExePath) ResolveShortcutAppItem(string lnkPath)
    {
        // IShellLinkW is an STA COM object — resolve it on a dedicated STA
        // thread so MTA pool threads (Parallel.ForEachAsync, Task.Run) work.
        ResolvedShortcut? resolved = null;
        var thread = new Thread(() =>
        {
            try
            {
                resolved = ShellLinkResolver.Resolve(lnkPath);
            }
            catch
            {
                resolved = null;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();

        if (resolved is null || string.IsNullOrWhiteSpace(resolved.TargetPath))
            return (null, null);

        // Simple heuristic to filter obvious noise (uninstallers, help files).
        if (resolved.TargetPath.Contains("unins", StringComparison.OrdinalIgnoreCase))
            return (null, null);

        return (
            new AppItem(
                Id: AppItem.ComputeId(lnkPath),
                DisplayName: Path.GetFileNameWithoutExtension(lnkPath),
                TargetPath: lnkPath, // we launch the .lnk itself, not the resolved exe — keeps working dir/args
                Kind: AppKind.Shortcut,
                IconCachePath: null,
                LastSeenUtc: DateTime.UtcNow),
            resolved.TargetPath);
    }
}

