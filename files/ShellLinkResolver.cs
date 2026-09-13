using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace LaunchpadClone.Core.Native;

[ComImport]
[Guid("00021401-0000-0000-C000-000000000046")]
internal class ShellLinkCoClass
{
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("000214F9-0000-0000-C000-000000000046")]
internal interface IShellLinkW
{
    int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, uint fFlags);
    int GetIDList(out IntPtr ppidl);
    int SetIDList(IntPtr pidl);
    int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
    int SetDescription(string pszName);
    int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
    int SetWorkingDirectory(string pszDir);
    int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
    int SetArguments(string pszArgs);
    int GetHotkey(out short pwHotkey);
    int SetHotkey(short wHotkey);
    int GetShowCmd(out int piShowCmd);
    int SetShowCmd(int iShowCmd);
    int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
    int SetIconLocation(string pszIconPath, int iIcon);
    int SetRelativePath(string pszPathRel, uint dwReserved);
    int Resolve(IntPtr hwnd, uint fFlags);
    int SetPath(string pszFile);
}

public sealed record ResolvedShortcut(string TargetPath, string? IconLocation, int IconIndex, string? Arguments);

/// <summary>
/// Resolves .lnk files through the Shell IShellLinkW COM interface — the same
/// path and icon Explorer would show, including custom icons that differ from
/// the target exe.
/// </summary>
public static class ShellLinkResolver
{
    // Long paths and argument strings can exceed legacy MAX_PATH (260),
    // so we use generous buffers here.
    private const int PathBufferSize = 1024;
    private const int ArgsBufferSize = 4096;

    public static ResolvedShortcut? Resolve(string lnkPath)
    {
        object? comObject = null;
        try
        {
            comObject = new ShellLinkCoClass();
            var link = (IShellLinkW)comObject;
            var file = (IPersistFile)comObject;

            file.Load(lnkPath, 0 /* STGM_READ */);

            var pathBuilder = new StringBuilder(PathBufferSize);
            link.GetPath(pathBuilder, pathBuilder.Capacity, IntPtr.Zero, 0);

            var iconBuilder = new StringBuilder(PathBufferSize);
            link.GetIconLocation(iconBuilder, iconBuilder.Capacity, out int iconIndex);

            var argsBuilder = new StringBuilder(ArgsBufferSize);
            link.GetArguments(argsBuilder, argsBuilder.Capacity);

            var target = pathBuilder.ToString();
            if (string.IsNullOrWhiteSpace(target))
                return null;

            return new ResolvedShortcut(
                TargetPath: target,
                IconLocation: iconBuilder.Length > 0 ? iconBuilder.ToString() : null,
                IconIndex: iconIndex,
                Arguments: argsBuilder.Length > 0 ? argsBuilder.ToString() : null);
        }
        catch (COMException)
        {
            // Corrupt or unreachable .lnk — skip it, don't abort the whole scan.
            return null;
        }
        catch (FileNotFoundException)
        {
            // The .lnk vanished between enumeration and resolve (e.g. uninstall
            // racing the scan) — same handling as corrupt.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (comObject is not null)
#pragma warning disable CA1416 // STA COM release is Windows-only by design here
                Marshal.ReleaseComObject(comObject);
#pragma warning restore CA1416
        }
    }
}

