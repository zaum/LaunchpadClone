using LaunchpadClone.Core.Cache;
using LaunchpadClone.Core.Discovery;
using LaunchpadClone.Core.Icons;

var iconCacheDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "LaunchpadClone", "icons", "hi");

var uwpProvider = new UwpPackageProvider(iconCacheDir);
var executableProvider = new ExecutableAppProvider();
var discovery = new AppDiscoveryService(uwpProvider, executableProvider);
var iconService = new IconExtractionService(iconCacheDir);
var appCache = AppCache.Default();

Console.WriteLine("Discovering apps...");

// Instant paint from disk cache, then refresh in background.
var cached = await appCache.LoadAsync();
if (cached.Count > 0)
    Console.WriteLine($"{cached.Count} apps loaded from cache (instant).");

var sw = System.Diagnostics.Stopwatch.StartNew();
var apps = await discovery.ScanAllAsync();
await appCache.SaveAsync(apps);

Console.WriteLine($"{apps.Count} apps found in {sw.ElapsedMilliseconds} ms.");
foreach (var kindGroup in apps.GroupBy(a => a.Kind).OrderBy(g => g.Key))
    Console.WriteLine($"  {kindGroup.Key,-12} {kindGroup.Count()}");
Console.WriteLine();

foreach (var app in apps.OrderBy(a => a.DisplayName))
{
    var iconPath = app.IconCachePath ?? await iconService.ExtractAndCacheAsync(app);
    var iconStatus = iconPath is not null ? "[icon OK]" : "[no icon]";
    Console.WriteLine($"{iconStatus,-14} {app.DisplayName,-40} ({app.Kind})");
}

