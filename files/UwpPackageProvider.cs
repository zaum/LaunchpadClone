using System.Runtime.Versioning;
using LaunchpadClone.Core.Models;
using Windows.Management.Deployment;

namespace LaunchpadClone.Core.Discovery;

public interface IUwpPackageProvider
{
    Task<List<AppItem>> GetInstalledAppsAsync(CancellationToken ct = default);
}

/// <summary>
/// Lists installed UWP/Store apps via the PackageManager API and immediately
/// saves their logo into the given icon-cache folder.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class UwpPackageProvider : IUwpPackageProvider
{
    private readonly string _iconCacheDirectory;

    public UwpPackageProvider(string iconCacheDirectory)
    {
        _iconCacheDirectory = iconCacheDirectory;
        Directory.CreateDirectory(_iconCacheDirectory);
    }

    public async Task<List<AppItem>> GetInstalledAppsAsync(CancellationToken ct = default)
    {
        var results = new List<AppItem>();

        IEnumerable<Windows.ApplicationModel.Package> packages;
        try
        {
            // "" = packages of the currently signed-in user.
            // Framework, resource and system packages are skipped — they are
            // not standalone, launchable apps.
            using var manager = new PackageManager();
            packages = manager.FindPackagesForUser(string.Empty)
                .Where(p => !p.IsFramework && !p.IsResourcePackage
                            && p.SignatureKind != PackageSignatureKind.System)
                .ToList();
        }
        catch (Exception)
        {
            // PackageManager unavailable (e.g. service disabled, unexpected
            // Windows SKU) — return empty rather than failing the whole scan.
            return results;
        }

        foreach (var package in packages)
        {
            ct.ThrowIfCancellationRequested();

            IReadOnlyList<Windows.ApplicationModel.Core.AppListEntry> entries;
            try
            {
                entries = await package.GetAppListEntriesAsync();
            }
            catch (Exception)
            {
                continue; // unqueryable/corrupt package — skip, don't stop
            }

            foreach (var entry in entries)
            {
                var aumid = entry.AppUserModelId;
                if (string.IsNullOrWhiteSpace(aumid))
                    continue;

                var id = AppItem.ComputeId(aumid);

                await TrySaveLogoAsync(entry, id, ct);

                var displayName = entry.DisplayInfo.DisplayName;
                if (string.IsNullOrWhiteSpace(displayName))
                    displayName = package.DisplayName;

                results.Add(new AppItem(
                    Id: id,
                    DisplayName: displayName,
                    TargetPath: aumid,
                    Kind: AppKind.Uwp,
                    IconCachePath: Path.Combine(_iconCacheDirectory, $"{id}.png"),
                    LastSeenUtc: DateTime.UtcNow));
            }
        }

        return results;
    }

    private async Task TrySaveLogoAsync(Windows.ApplicationModel.Core.AppListEntry entry, string id, CancellationToken ct)
    {
        var cachePath = Path.Combine(_iconCacheDirectory, $"{id}.png");
        if (File.Exists(cachePath))
            return;

        try
        {
            var logoRef = entry.DisplayInfo.GetLogo(new Windows.Foundation.Size(150, 150));
            using var stream = await logoRef.OpenReadAsync();
            await using var fileStream = File.Create(cachePath);
            await stream.AsStreamForRead().CopyToAsync(fileStream, ct);
        }
        catch (Exception)
        {
            // No logo or not accessible — the app still shows with a placeholder.
            // Remove any half-written file so Exists() stays trustworthy.
            try { if (File.Exists(cachePath)) File.Delete(cachePath); } catch { }
        }
    }
}
