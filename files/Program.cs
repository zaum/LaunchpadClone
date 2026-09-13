using LaunchpadClone.Core.Discovery;
using LaunchpadClone.Core.Icons;

var iconCacheDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "LaunchpadClone", "icons");

var uwpProvider = new UwpPackageProvider(iconCacheDir);
var discovery = new AppDiscoveryService(uwpProvider);
var iconService = new IconExtractionService(iconCacheDir);

Console.WriteLine("Alkalmazások felderítése...");
var sw = System.Diagnostics.Stopwatch.StartNew();

var apps = await discovery.ScanAllAsync();

Console.WriteLine($"{apps.Count} app találva {sw.ElapsedMilliseconds} ms alatt.\n");

foreach (var app in apps.OrderBy(a => a.DisplayName))
{
    var iconPath = app.IconCachePath ?? await iconService.ExtractAndCacheAsync(app);
    var iconStatus = iconPath is not null ? "[ikon OK]" : "[nincs ikon]";
    Console.WriteLine($"{iconStatus,-14} {app.DisplayName,-40} ({app.Kind})");
}
