using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Beautiful.App.Interop;

// 高清图标提取：IShellItemImageFactory（缩略图工厂，比 SHGetFileInfo 的 32px 更清晰）。
internal static class IconExtractor
{
    public static BitmapSource? GetIcon(string path, int size = 48)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var factory = NativeMethods.CreateItemFactory(path);
        if (factory is null) return null;
        try
        {
            var hr = factory.GetImage((uint)size, (uint)size,
                NativeMethods.SiigbfIcononly | NativeMethods.SiigbfBiggersizeok, out var hbm);
            if (hr != 0 || hbm == IntPtr.Zero) return null;
            try
            {
                var bs = Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero,
                    Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bs.Freeze();
                return bs;
            }
            finally { NativeMethods.DeleteObject(hbm); }
        }
        catch { return null; }
        finally { Marshal.ReleaseComObject(factory); }
    }
}
