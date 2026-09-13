using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace LaunchpadClone.Core.Native;

[SupportedOSPlatform("windows")]
internal static class IconInterop
{
    private const uint SHGFI_ICON = 0x100;
    private const uint SHGFI_LARGEICON = 0x0;
    private const uint SHGFI_SMALLICON = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>
    /// Returns the Explorer-style icon for a file (typically .lnk or .exe).
    /// The caller must Dispose the returned Icon. Uses jumbo-size shell
    /// image list via ExtractIconEx fallback when SHGetFileInfo yields only
    /// a small icon, so cached PNGs stay sharp on high-DPI displays.
    /// </summary>
    public static Icon? ExtractIcon(string path, bool large = true)
    {
        var info = new SHFILEINFO();
        var flags = SHGFI_ICON | (large ? SHGFI_LARGEICON : SHGFI_SMALLICON);
        var handle = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);

        if (handle == IntPtr.Zero || info.hIcon == IntPtr.Zero)
            return null;

        try
        {
            using var handleIcon = Icon.FromHandle(info.hIcon);
            return (Icon)handleIcon.Clone(); // own copy, independent of the native handle
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    /// <summary>Backwards-compatible alias for existing call sites.</summary>
    public static Icon? ExtractLargeIcon(string path) => ExtractIcon(path, large: true);
}

