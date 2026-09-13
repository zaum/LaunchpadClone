using System.Text.Json;

namespace LaunchpadClone.Core.Groups;

/// <summary>
/// JSON persistence of user-created groups, on disk next to the app cache.
/// Same write discipline as AppCache: temp file first, then atomic move, so
/// a crash mid-write never leaves a half-written groups.json behind.
/// </summary>
public sealed class AppGroupStore
{
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;

    public AppGroupStore(string filePath) => _filePath = filePath;

    public static AppGroupStore Default()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LaunchpadClone");
        Directory.CreateDirectory(dir);
        return new AppGroupStore(Path.Combine(dir, "groups.json"));
    }

    public async Task<List<AppGroup>> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath))
            return [];

        try
        {
            await using var stream = File.OpenRead(_filePath);
            var envelope = await JsonSerializer.DeserializeAsync<GroupEnvelope>(
                stream, JsonOptions, ct);
            if (envelope?.Version != CurrentVersion || envelope.Groups is null)
                return [];
            return envelope.Groups;
        }
        catch (Exception)
        {
            // Corrupt or unreadable file — the user loses their folders but
            // the launcher still works; a fresh save rewrites it.
            return [];
        }
    }

    public async Task SaveAsync(IReadOnlyList<AppGroup> groups, CancellationToken ct = default)
    {
        var envelope = new GroupEnvelope(CurrentVersion, groups.ToList(), DateTime.UtcNow);
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmpPath = _filePath + ".tmp";
        await using (var stream = File.Create(tmpPath))
        {
            await JsonSerializer.SerializeAsync(stream, envelope, JsonOptions, ct);
        }

        File.Move(tmpPath, _filePath, overwrite: true);
    }

    private sealed record GroupEnvelope(int Version, List<AppGroup> Groups, DateTime SavedAtUtc);
}
