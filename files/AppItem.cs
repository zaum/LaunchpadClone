using System.Security.Cryptography;
using System.Text;

namespace LaunchpadClone.Core.Models;

public enum AppKind
{
    Shortcut,
    Uwp
}

/// <summary>
/// Egy felderített, indítható alkalmazást ír le — akár Start Menu parancsikonról,
/// akár UWP csomagból származik.
/// </summary>
public sealed record AppItem(
    string Id,
    string DisplayName,
    string TargetPath,
    AppKind Kind,
    string? IconCachePath,
    DateTime LastSeenUtc)
{
    /// <summary>
    /// Stabil, útvonal/AUMID alapú azonosító — ugyanaz marad újraindítás és
    /// újraszkennelés között is, így a lemezes cache konzisztens tud maradni.
    /// </summary>
    public static string ComputeId(string uniqueKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(uniqueKey.Trim().ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..16];
    }

    public AppItem WithIconCachePath(string path) => this with { IconCachePath = path };
}
