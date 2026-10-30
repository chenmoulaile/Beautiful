using System;
using System.Windows;
using System.Windows.Interop;
using Beautiful.App.Interop;
using Beautiful.App.Themes;
using Beautiful.Core.Native;
using Beautiful.Core.Settings;

namespace Beautiful.App.Services;

/// <summary>
/// 把菜单栏/Dock 的 WPF 分层窗口与 native 背板（DComp HostBackdrop）绑定（v4 Phase 2 T3）。
/// <para>
/// 形态：native 背板是独立 WS_EX_NOREDIRECTIONBITMAP 窗口，插在 owner 窗口的 Z 序下方；
/// owner 保持真透明区域，两者同一矩形、同一 topmost 带。移动/缩放/DPI 变化时按
/// <c>GetWindowRect</c> 的物理像素同步，不做 DIP 换算。
/// </para>
/// <para>
/// 降级：Native 不可用、总开关/材质开关关闭、MaterialQuality=static、HostBackdrop 探针失败，
/// 一律不创建背板，owner 保留原有静态 tint（<see cref="IsActive"/> 为 false）。
/// 失败只记日志，绝不拖垮主程序。
/// </para>
/// </summary>
public sealed class NativeMaterialHost : IDisposable
{
    private readonly Window _owner;
    private readonly bool _dark;
    private readonly string _surface; // "menubar" / "dock"，只用于日志
    private uint _tint;
    private float _opacity;
    private float _radius;
    private IntPtr _handle;
    private bool _disposed;
    private bool _probeTried;
    private bool _probeOk;

    public NativeMaterialHost(Window owner, uint tint, float opacity, float radius, bool dark, string surface)
    {
        _owner = owner;
        _tint = tint;
        _opacity = opacity;
        _radius = radius;
        _dark = dark;
        _surface = surface;

        _owner.SourceInitialized += (_, _) => Ensure();
        _owner.LocationChanged += (_, _) => Sync();
        _owner.SizeChanged += (_, _) => Sync();
        _owner.IsVisibleChanged += (_, _) => SetVisible(_owner.IsVisible);
        _owner.Closed += (_, _) => Dispose();
        if (new WindowInteropHelper(_owner).Handle != IntPtr.Zero) Ensure();
    }

    public bool IsActive => _handle != IntPtr.Zero;

    /// <summary>外观切换（亮/暗/强调色）：只更新 tint，不重建背板。</summary>
    public void SetTheme(uint tint, float opacity, bool dark)
    {
        _tint = tint;
        _opacity = opacity;
        if (_handle != IntPtr.Zero)
            NativeLoader.MaterialSetTheme(_handle, _tint, _opacity, dark ? 1 : 0);
    }

    private bool ShouldUse()
    {
        var s = SettingsService.Current;
        if (!s.NativeModules || !s.NativeBlur) return false;
        if (string.Equals(s.MaterialQuality, "static", StringComparison.OrdinalIgnoreCase)) return false;
        if (!NativeLoader.Available) return false;
        if (!_probeTried)
        {
            _probeTried = true;
            _probeOk = NativeLoader.MaterialProbe() == NativeProtocol.Ok;
            Logger.Info($"native 材质探针（{_surface}）：{(_probeOk ? "HostBackdrop 可用" : "不可用，回落静态 tint")}");
        }
        return _probeOk;
    }

    private void Ensure()
    {
        if (_disposed || _handle != IntPtr.Zero || !ShouldUse()) return;
        var owner = new WindowInteropHelper(_owner).Handle;
        if (owner == IntPtr.Zero) return;
        if (!GetWindowRect(owner, out var rect)) return;

        var desc = new NativeMaterialDesc
        {
            Rect = new NativeRect { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom },
            Tint = _tint,
            Opacity = _opacity,
            CornerRadius = _radius,
            Dark = _dark ? 1 : 0,
        };
        _handle = NativeLoader.MaterialCreate(owner, ref desc);
        if (_handle == IntPtr.Zero)
        {
            Logger.Warn($"native 材质创建失败（{_surface}）：{NativeLoader.LastError()}");
            return;
        }
        Logger.Info($"native 材质已启用（{_surface}）");
        SetVisible(_owner.IsVisible);
    }

    private void Sync()
    {
        if (_disposed) return;
        if (_handle == IntPtr.Zero)
        {
            Ensure();
            return;
        }
        var owner = new WindowInteropHelper(_owner).Handle;
        if (owner == IntPtr.Zero || !GetWindowRect(owner, out var rect)) return;
        var r = new NativeRect { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom };
        NativeLoader.MaterialSetRect(_handle, ref r);
    }

    private void SetVisible(bool visible)
    {
        if (_handle != IntPtr.Zero)
            NativeLoader.MaterialShow(_handle, visible);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            NativeLoader.MaterialDestroy(_handle);
            _handle = IntPtr.Zero;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
