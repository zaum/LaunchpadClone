using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LaunchpadClone.UI.Native;

/// <summary>
/// Captures the desktop ONCE as a tiny bitmap (downscaled to ~96px wide).
/// Upscaled with bicubic stretch it reads as a blurred desktop backdrop —
/// the cheapest possible blur: zero per-frame DWM work, zero per-frame CPU.
/// Captured in OnSourceInitialized, BEFORE the overlay window paints over
/// the desktop.
/// </summary>
internal static class DesktopSnapshot
{
    public static ImageSource? Capture()
    {
        try
        {
            var w = (int)SystemParameters.PrimaryScreenWidth;
            var h = (int)SystemParameters.PrimaryScreenHeight;
            if (w < 100 || h < 100)
                return null;

            using var bmp = new System.Drawing.Bitmap(w, h);
            using (var g = System.Drawing.Graphics.FromImage(bmp))
                g.CopyFromScreen(0, 0, 0, 0, new System.Drawing.Size(w, h));

            using var ms = new MemoryStream();
            bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;

            var full = new BitmapImage();
            full.BeginInit();
            full.CacheOption = BitmapCacheOption.OnLoad;
            full.StreamSource = ms;
            full.EndInit();
            full.Freeze();

            // Downscale to 96px wide: stretching it back up gives a free
            // gaussian-ish blur via bilinear sampling in the compositor.
            var small = new TransformedBitmap(full,
                new ScaleTransform(96.0 / w, 96.0 / w));
            small.Freeze();
            return small;
        }
        catch
        {
            return null; // backdrop is cosmetic — scrim alone is fine
        }
    }
}