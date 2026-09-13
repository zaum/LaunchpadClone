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
    /// The caller must Dispose the returned Icon.
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

    private const uint SHGFI_SYSICONINDEX = 0x4000;
    private const int SHIL_JUMBO = 0x4;      // 256x256 system image list
    private const int ILD_TRANSPARENT = 0x1;

    private static readonly Guid IidIImageList = new("46EB5926-582E-4017-9FDF-E8998D80B5F5");

    // SHGetImageList is exported by ordinal only — hence EntryPoint "#727".
    [DllImport("shell32.dll", EntryPoint = "#727")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

    // Only the first vtable slots of IUnknown-derived IImageList are declared;
    // GetIcon (the 8th method) is the only one we call, but COM marshaling
    // requires the preceding slots to be present in order.
    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998D80B5F5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Overlay(int iImage1, int iImage2, ref int pi);
        [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, ref int pi);
        [PreserveSig] int Draw(IntPtr pimldp);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, ref IntPtr picon);
    }

    /// <summary>
    /// Extracts the largest available icon (up to 256x256 via the "jumbo"
    /// system image list) for a file or .lnk. Returns null when the jumbo
    /// list is unavailable on this system. Caller must Dispose the Icon.
    /// </summary>
    public static Icon? ExtractJumboIcon(string path)
    {
        var info = new SHFILEINFO();

        // SYSICONINDEX resolves .lnk targets too and yields the index into
        // the system image lists; the jumbo list stores a matching hi-res
        // entry under the same index.
        var listHandle = SHGetFileInfo(path, 0, ref info,
            (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_SYSICONINDEX);
        if (listHandle == IntPtr.Zero)
            return null;

        // A static readonly field cannot be passed by ref — copy to a local.
        var iid = IidIImageList;
        if (SHGetImageList(SHIL_JUMBO, ref iid, out var imageList) != 0 || imageList is null)
            return null;

        try
        {
            var hIcon = IntPtr.Zero;
            if (imageList.GetIcon(info.iIcon, ILD_TRANSPARENT, ref hIcon) != 0 || hIcon == IntPtr.Zero)
                return null;

            try
            {
                using var fromHandle = Icon.FromHandle(hIcon);
                return (Icon)fromHandle.Clone();
            }
            finally
            {
                DestroyIcon(hIcon);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(imageList);
        }
    }
}

