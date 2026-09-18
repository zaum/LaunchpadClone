using System.Text.Json;

namespace LaunchpadClone.Core.Settings;

public enum AppHotCorner
{
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight
}

/// <summary>
/// User settings for the launcher shell: hot corner, grid size, opening
/// hotkey. Persisted as JSON next to the other caches.
/// </summary>
public sealed class AppSettings
{
    public bool HotCornerEnabled { get; set; } = false;

    public AppHotCorner HotCorner { get; set; } = AppHotCorner.TopRight;

    /// <summary>Tile size multiplier (0.7 compact .. 1.4 roomy) — the grid
    /// size slider. Applied live; page capacity recomputes from it.
    /// Default is roomy: fewer, larger icons per page (macOS-like).</summary>
    public double TileScale { get; set; } = 1.2;

    /// <summary>Global hotkey that opens the launcher, e.g. "Ctrl+Alt+L".</summary>
    public string HotKey { get; set; } = "Ctrl+Alt+L";
}

/// <summary>Atomic JSON persistence for AppSettings (temp file + move).</summary>
public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;

    public AppSettingsStore(string filePath) => _filePath = filePath;

    public static AppSettingsStore Default()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LaunchpadClone");
        Directory.CreateDirectory(dir);
        return new AppSettingsStore(Path.Combine(dir, "launchpad-settings.json"));
    }

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath))
            return new AppSettings();

        try
        {
            await using var stream = File.OpenRead(_filePath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(
                stream, JsonOptions, ct);
            return settings ?? new AppSettings();
        }
        catch (Exception)
        {
            return new AppSettings(); // corrupt file — defaults win
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        var tmpPath = _filePath + ".tmp";
        await using (var stream = File.Create(tmpPath))
        {
            await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, ct);
        }
        File.Move(tmpPath, _filePath, overwrite: true);
    }
}
