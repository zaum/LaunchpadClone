using LaunchpadClone.Core.Models;

namespace LaunchpadClone.Core.Discovery;

/// <summary>
/// Watches the Start Menu folders for .lnk changes and emits delta updates
/// (upsert / removed) with debounce, so the UI stays live without
/// re-scanning everything. UWP installs and raw Executable items are NOT
/// covered — those arrive via a periodic full re-scan (PackageManager has no
/// watcher API, and raw exe installs are too rare to justify watching
/// Program Files).
/// </summary>
public sealed class AppWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly IAppDiscoveryService _discovery;
    private readonly TimeSpan _debounce;
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private readonly Timer _timer;
    private bool _disposed;

    /// <summary>Created or modified shortcuts, re-resolved and ready to upsert.</summary>
    public event Func<IReadOnlyList<AppItem>, Task>? AppsUpserted;

    /// <summary>Stable <see cref="AppItem.Id"/> values whose .lnk disappeared.</summary>
    public event Func<IReadOnlyList<string>, Task>? AppsRemoved;

    public AppWatcher(IAppDiscoveryService discovery, TimeSpan? debounce = null)
    {
        _discovery = discovery;
        _debounce = debounce ?? TimeSpan.FromMilliseconds(750);
        _timer = new Timer(OnDebounceElapsed, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Start()
    {
        foreach (var folder in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
        })
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                continue;

            var watcher = new FileSystemWatcher(folder, "*.lnk")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime
            };
            watcher.Created += (_, e) => Schedule(e.FullPath);
            watcher.Changed += (_, e) => Schedule(e.FullPath);
            watcher.Deleted += (_, e) => Schedule(e.FullPath);
            watcher.Renamed += (_, e) =>
            {
                Schedule(e.OldFullPath);
                Schedule(e.FullPath);
            };
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    private void Schedule(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        lock (_lock)
        {
            if (_disposed)
                return;
            _pending.Add(path);
            // Restart the debounce window on every burst of events.
            try
            {
                _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Dispose raced Start/schedule — safe to ignore.
            }
        }
    }

    private void OnDebounceElapsed(object? _)
    {
        List<string> batch;
        lock (_lock)
        {
            batch = _pending.ToList();
            _pending.Clear();
        }

        if (batch.Count == 0)
            return;

        _ = ProcessBatchAsync(batch);
    }

    private async Task ProcessBatchAsync(List<string> batch)
    {
        var upserted = new List<AppItem>();
        var removedIds = new List<string>();

        foreach (var path in batch.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path))
            {
                // Deleted .lnk — the stable Id is hash(lnkPath), computable
                // without resolving anything.
                removedIds.Add(AppItem.ComputeId(path));
                continue;
            }

            try
            {
                var item = await _discovery.ScanShortcutAsync(path);
                if (item is not null)
                    upserted.Add(item);
            }
            catch (Exception)
            {
                // One bad shortcut must not kill the watcher loop.
            }
        }

        // Subscriber exceptions must not surface as unobserved task failures
        // on the timer thread — one bad UI handler must not kill the watcher.
        try
        {
            if (upserted.Count > 0 && AppsUpserted is not null)
                await AppsUpserted.Invoke(upserted);
        }
        catch (Exception)
        {
        }

        try
        {
            if (removedIds.Count > 0 && AppsRemoved is not null)
                await AppsRemoved.Invoke(removedIds);
        }
        catch (Exception)
        {
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _timer.Dispose();
        foreach (var watcher in _watchers)
            watcher.Dispose();
        _watchers.Clear();
    }
}


