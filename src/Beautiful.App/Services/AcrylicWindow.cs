using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Beautiful.App.Interop;
using static Beautiful.App.Interop.NativeMethods;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;

namespace Beautiful.App.Services;

/// <summary>
/// 亚克力/毛玻璃窗口基类。实现模式参考 ModernFlyouts (MIT) 的 SetWindowCompositionAttribute 用法。
/// </summary>
public class AcrylicWindow : Window
{
    public AcrylicWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
    }

    internal void ApplyAcrylic(Color tint, AccentState state = AccentState.AcrylicBlurBehind)
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var accent = new ACCENT_POLICY
        {
            AccentState = state,
            AccentFlags = 2,
            GradientColor = NativeMethods.ToAbgr(tint),
            AnimationId = 0
        };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<ACCENT_POLICY>());
        try
        {
            Marshal.StructureToPtr(accent, ptr, false);
            var data = new WINDOWCOMPOSITIONATTRIBDATA
            {
                Attribute = NativeMethods.WcaAccentPolicy,
                Data = ptr,
                SizeOfData = Marshal.SizeOf<ACCENT_POLICY>()
            };
            NativeMethods.SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public void DisableAcrylic() => ApplyAcrylic(Colors.Transparent, AccentState.Disabled);
}
