using System.Text.Json;
using LaunchpadClone.Core.Models;

namespace LaunchpadClone.Core.Cache;

/// <summary>
/// On-disk JSON cache of the app list so startup does not need a full scan.
/// The UI shows this instantly, then Discovery refreshes it in the background.
/// </summary>
public sealed class AppCache
{
    private const int CurrentVersion = 1;

    private readonly string _cacheFilePath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AppCache(string cacheFilePath)
    {
        _cacheFilePath = cacheFilePath;
    }

    public static AppCache Default()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LaunchpadClone");
        Directory.CreateDirectory(dir);
        return new AppCache(Path.Combine(dir, "apps.json"));
    }

    public async Task<(IReadOnlyList<AppItem> Apps, DateTime? SavedAt)> LoadWithAgeAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_cacheFilePath))
            return (Array.Empty<AppItem>(), null);

        try
        {
            await using var stream = File.OpenRead(_cacheFilePath);
            var envelope = await JsonSerializer.DeserializeAsync<CacheEnvelope>(stream, JsonOptions, ct);
            if (envelope?.Version != CurrentVersion || envelope.Apps is null)
                return (Array.Empty<AppItem>(), null);
            return (envelope.Apps, envelope.SavedAtUtc);
        }
        catch (Exception)
        {
            return (Array.Empty<AppItem>(), null);
        }
    }

    /// <summary>Backward-compatible load — discards the save timestamp.</summary>
    public async Task<IReadOnlyList<AppItem>> LoadAsync(CancellationToken ct = default)
    {
        var (apps, _) = await LoadWithAgeAsync(ct);
        return apps;
    }

    /// <summary>
    /// Returns true if the cache file exists and was written within the given age.
    /// </summary>
    public bool IsFresh(TimeSpan maxAge)
    {
        if (!File.Exists(_cacheFilePath))
            return false;
        var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(_cacheFilePath);
        return age <= maxAge;
    }

    public async Task SaveAsync(IReadOnlyList<AppItem> apps, CancellationToken ct = default)
    {
        var envelope = new CacheEnvelope(CurrentVersion, apps.ToList(), DateTime.UtcNow);
        var dir = Path.GetDirectoryName(_cacheFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Write to temp file first, then atomic move — a crash mid-write
        // must not leave a half-written apps.json behind.
        var tmpPath = _cacheFilePath + ".tmp";
        await using (var stream = File.Create(tmpPath))
        {
            await JsonSerializer.SerializeAsync(stream, envelope, JsonOptions, ct);
        }

        File.Move(tmpPath, _cacheFilePath, overwrite: true);
    }

    private sealed record CacheEnvelope(int Version, List<AppItem> Apps, DateTime SavedAtUtc);
}
