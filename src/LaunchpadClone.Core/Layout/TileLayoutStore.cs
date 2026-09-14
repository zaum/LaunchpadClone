using System.Text.Json;

namespace LaunchpadClone.Core.Layout;

/// <summary>
/// Persisted tile order: the user's drag-arranged order of app and group
/// tiles (macOS Launchpad remembers positions). Ids are AppItem.Id for apps
/// and "g:&lt;groupId&gt;" for groups; ids that no longer exist are skipped,
/// unknown new apps are appended alphabetically at the end.
/// </summary>
public sealed class TileLayoutStore
{
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;

    public TileLayoutStore(string filePath) => _filePath = filePath;

    public static TileLayoutStore Default()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LaunchpadClone");
        Directory.CreateDirectory(dir);
        return new TileLayoutStore(Path.Combine(dir, "launchpad-layout.json"));
    }

    public async Task<List<string>> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath))
            return [];

        try
        {
            await using var stream = File.OpenRead(_filePath);
            var envelope = await JsonSerializer.DeserializeAsync<LayoutEnvelope>(
                stream, JsonOptions, ct);
            if (envelope?.Version != CurrentVersion || envelope.OrderedIds is null)
                return [];
            return envelope.OrderedIds;
        }
        catch (Exception)
        {
            return []; // corrupt layout — alphabetical fallback
        }
    }

    public async Task SaveAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default)
    {
        var envelope = new LayoutEnvelope(CurrentVersion, orderedIds.ToList(), DateTime.UtcNow);
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

    private sealed record LayoutEnvelope(int Version, List<string> OrderedIds, DateTime SavedAtUtc);
}
