using System.Drawing.Imaging;
using System.Runtime.Versioning;
using LaunchpadClone.Core.Models;
using LaunchpadClone.Core.Native;

namespace LaunchpadClone.Core.Icons;

public interface IIconExtractionService
{
    /// <summary>
    /// Extracts the item's icon and caches it as PNG if not cached yet.
    /// Returns the cache file path, or null on failure.
    /// </summary>
    Task<string?> ExtractAndCacheAsync(AppItem item, CancellationToken ct = default);
}

[SupportedOSPlatform("windows")]
public sealed class IconExtractionService : IIconExtractionService
{
    private readonly string _cacheDirectory;

    public IconExtractionService(string cacheDirectory)
    {
        _cacheDirectory = cacheDirectory;
        Directory.CreateDirectory(_cacheDirectory);
    }

    public Task<string?> ExtractAndCacheAsync(AppItem item, CancellationToken ct = default)
    {
        var cachePath = Path.Combine(_cacheDirectory, $"{item.Id}.png");

        if (File.Exists(cachePath))
            return Task.FromResult<string?>(cachePath);

        if (!string.IsNullOrWhiteSpace(item.IconCachePath) && File.Exists(item.IconCachePath))
            return Task.FromResult<string?>(item.IconCachePath);

        // UWP logos are saved directly from the package DisplayInfo by
        // UwpPackageProvider — only the Shortcut branch belongs here.
        if (item.Kind != AppKind.Shortcut)
            return Task.FromResult<string?>(null);

        return Task.Run(() => ExtractFromShellItem(item.TargetPath, cachePath), ct);
    }

    private static string? ExtractFromShellItem(string path, string cachePath)
    {
        // Prefer the shortcut's own icon (may differ from the target exe),
        // fall back to the large shell icon.
        using var icon = IconInterop.ExtractIcon(path, large: false)
            ?? IconInterop.ExtractIcon(path, large: true);
        if (icon is null)
            return null;

        try
        {
            // ToBitmap() loses the alpha channel on some icon formats, so
            // render explicitly to a 32bpp ARGB bitmap to keep transparency.
            using var bitmap = new System.Drawing.Bitmap(icon.Width, icon.Height,
                System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.Clear(System.Drawing.Color.Transparent);
                graphics.DrawIcon(icon, 0, 0);
            }
            bitmap.Save(cachePath, ImageFormat.Png);
            return cachePath;
        }
        catch (Exception)
        {
            // Corrupt cache write must not leave a half-written PNG behind,
            // otherwise Exists() would treat it as valid next run.
            try { if (File.Exists(cachePath)) File.Delete(cachePath); } catch { }
            return null;
        }
    }
}

