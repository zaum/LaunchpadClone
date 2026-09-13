using System.Security.Cryptography;
using System.Text;

namespace LaunchpadClone.Core.Models;

public enum AppKind
{
    /// <summary>Start Menu .lnk shortcut — TargetPath is the .lnk itself.</summary>
    Shortcut,

    /// <summary>UWP/Store app — TargetPath is the AUMID.</summary>
    Uwp,

    /// <summary>Program Files exe with no Start Menu shortcut — TargetPath is the exe path.</summary>
    Executable
}

/// <summary>
/// Describes a discovered, launchable application — whether it originates
/// from a Start Menu shortcut, a UWP package, or a raw Program Files exe.
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
    /// Stable, path/AUMID/exe-path based identifier — stays the same across
    /// restarts and re-scans, so the disk cache can remain consistent.
    /// </summary>
    public static string ComputeId(string uniqueKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(uniqueKey.Trim().ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..16];
    }

    public AppItem WithIconCachePath(string path) => this with { IconCachePath = path };
}
