using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LaunchpadClone.UI.Native;

/// <summary>
/// Enables the DWM blur-behind accent for a WPF window so the desktop shows
/// through the semi-transparent overlay, blurred — the acrylic-launcher
/// look. Graceful no-op when the OS refuses (e.g. some Windows builds): the
/// dark scrim alone still reads as an overlay.
/// </summary>
internal static class WindowAccentBlur
{
    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_DISABLED = 0;
    private const int ACCENT_ENABLE_BLURBEHIND = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public uint AccentFlags;
        public uint GradientColor;
        public uint AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(
        IntPtr hwnd, ref WindowCompositionAttributeData data);

    public static void EnableBlurBehind(Window window, bool enabled = true)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            var policy = new AccentPolicy
            {
                AccentState = enabled ? ACCENT_ENABLE_BLURBEHIND : ACCENT_DISABLED
            };
            var policySize = Marshal.SizeOf<AccentPolicy>();
            var policyPtr = Marshal.AllocHGlobal(policySize);
            try
            {
                Marshal.StructureToPtr(policy, policyPtr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WCA_ACCENT_POLICY,
                    Data = policyPtr,
                    SizeOfData = policySize
                };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(policyPtr);
            }
        }
        catch
        {
            // Blur is an enhancement, never a requirement — swallow errors.
        }
    }
}
