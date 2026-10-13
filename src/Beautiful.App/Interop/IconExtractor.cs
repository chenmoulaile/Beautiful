using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Beautiful.App.Interop;

// 图标提取。原 IShellItemImageFactory::GetImage 方案在本机环境（Windows 11 VM）对任意
// 路径/尺寸/标志组合均触发 AccessViolationException（进程致命崩溃，已用 PowerShell 探针
// 脚本复现定位），故改用经探针验证稳定的两条经典路径：
//   1) SHDefExtractIconW：文件路径，可取精确尺寸（48px）；
//   2) SHParseDisplayName + SHGetFileInfoW(SHGFI_PIDL)：shell 命名空间路径（::{clsid} 等），
//      固定 32px LARGEICON，由视图端 HighQuality 缩放补偿。
// 公开签名保持 Task 8 契约 GetIcon(string, int=48) 不变（WindowTracker / Dock 消费方无感知）。
internal static class IconExtractor
{
    public static BitmapSource? GetIcon(string path, int size = 48)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return TryExtractIcon(path, size) ?? TryPidlIcon(path);
    }

    /// <summary>路径 1：SHDefExtractIconW，仅适用于文件路径，支持任意尺寸。</summary>
    private static BitmapSource? TryExtractIcon(string path, int size)
    {
        try
        {
            var nSize = (uint)size | (16u << 16);
            // nSize 高 16 位显式请求 16px 小图标：shell 会同时写出一个 HICON，
            // 不能用 out _ 丢弃（out 参数 marshaling 仍产生真实指针），必须在成功路径一并销毁。
            var hr = NativeMethods.SHDefExtractIcon(path, 0, 0, out var hLarge, out var hSmall, nSize);
            if (hr != 0 || hLarge == IntPtr.Zero) return null;
            try { return FromHIcon(hLarge); }
            finally
            {
                NativeMethods.DestroyIcon(hLarge);
                if (hSmall != IntPtr.Zero) NativeMethods.DestroyIcon(hSmall);
            }
        }
        catch { return null; }
    }

    /// <summary>路径 2：SHParseDisplayName → SHGetFileInfoW(SHGFI_PIDL)，适用于 ::{clsid} 等 shell 路径。</summary>
    private static BitmapSource? TryPidlIcon(string path)
    {
        try
        {
            NativeMethods.SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _);
            if (pidl == IntPtr.Zero) return null;
            try
            {
                var shfi = new NativeMethods.SHFILEINFOW();
                var ok = NativeMethods.SHGetFileInfo(pidl, 0, ref shfi,
                    (uint)Marshal.SizeOf<NativeMethods.SHFILEINFOW>(),
                    NativeMethods.ShgfiIcon | NativeMethods.ShgfiPidl);
                if (ok == IntPtr.Zero || shfi.hIcon == IntPtr.Zero) return null;
                try { return FromHIcon(shfi.hIcon); }
                finally { NativeMethods.DestroyIcon(shfi.hIcon); }
            }
            finally { NativeMethods.CoTaskMemFree(pidl); }
        }
        catch { return null; }
    }

    private static BitmapSource FromHIcon(IntPtr hIcon)
    {
        var bs = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        bs.Freeze();
        return bs;
    }
}
