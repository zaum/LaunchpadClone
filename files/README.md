# LaunchpadClone — Core réteg (Fázis 1: App discovery + ikon-kinyerés)

## Tartalom
- **LaunchpadClone.Core** — a felderítés, ikon-kinyerés és modellek, UI-mentesen
- **LaunchpadClone.Poc** — konzolos teszt-harness, ami kilistázza a talált appokat és kinyeri az ikonjaikat

## Futtatás (Windows-on, Visual Studio 2022 / .NET 8 SDK)
```
cd LaunchpadClone.Poc
dotnet run
```
Elsőre pár másodpercig tarthat (COM + fájlrendszer hívások), utána minden app mellett `[ikon OK]` vagy `[nincs ikon]` jelzés látszik. Az ikonok ide kerülnek: `%LOCALAPPDATA%\LaunchpadClone\icons\*.png`.

## Amit tudni érdemes / ellenőrizendő build közben
- **UWP-projekció**: a `Windows.Management.Deployment` és `Windows.ApplicationModel.Core` névterek a `net8.0-windows10.0.19041.0` TFM-mel automatikusan elérhetők. Ha a fordító nem találja őket, add hozzá a `Microsoft.Windows.SDK.Contracts` NuGet csomagot.
- **PackageManager jogosultság**: `FindPackagesForUser("")` unpackaged desktop appból a jelenlegi felhasználó csomagjaira eddig manifest-megszorítás nélkül működött a tapasztalat szerint — de ezt érdemes az első futtatáskor leellenőrizni a célgépen, mert Windows-verziónként lehet eltérés.
- **System.Drawing.Common**: csak Windows-on fut (ez itt nem gond, mert a teljes projekt is Windows-only), de érdemes tudni, hogy .NET 6+ óta explicit Windows-only csomagként van jelölve.
- Ez a kód **nem lett lefordítva/tesztelve** ebben a környezetben (Linux sandbox, nincs Windows/WinRT) — logikailag és API-szinten átgondolt, de az első Windows-os build után várhatók apró fordítási hibák (pl. using-hiány, névtér-eltérés a Windows SDK verziójától függően).

## Mi hiányzik még a tervhez képest (következő lépések)
- **FileSystemWatcher + delta-frissítés** (2. fázis) — az `IAppDiscoveryService.ScanShortcutAsync` már elő van készítve erre, csak a Watcher-osztály és a debounce-logika hiányzik
- **Lemezes app-lista cache** (JSON), hogy induláskor ne kelljen újra teljes scan
- **`AppKind.Executable`** ág, ha közvetlenül a Program Files-t is be akarjátok vonni parancsikon nélkül
