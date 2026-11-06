using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using static Beautiful.App.Interop.NativeMethods;
// UseWindowsForms 把 System.Drawing 引入全局命名空间，与 WPF 的 Rect 同名 → 显式取 WPF 版本
// （与 ThumbnailPreview / DockSurface 同一手法）。
using Rect = System.Windows.Rect;
using Application = System.Windows.Application;   // 同上，System.Windows.Forms.Application

namespace Beautiful.App.StageManager;

/// <summary>
/// 舞台条上的 DWM 实况缩略图登记表（wp-macos-parity r5）：一张宿主窗口上同时挂 N 张
/// <c>DwmRegisterThumbnail</c> 实时缩略图，每张落在一张卡片的预留矩形里。
/// <para>
/// 【与 ThumbnailPreview / DwmLiveGhost 的关系】同一套画面通道与同一套句柄纪律，差异只在
/// "一次几张"：悬停预览（ThumbnailPreview）与 genie 动画（DwmLiveGhost）各自只保留 1..N 张
/// 短命句柄播完即注销；舞台条是<b>常驻多张</b>（每卡片一张），所以对账语义放在这里——
/// <see cref="Sync"/> 以期望集合为准做增/改/删，调用方（StageBarWindow）每次快照重建后调一次。
/// 目标宿主是舞台条窗口本体（非分层 WPF 窗口 + OnSourceInitialized 里延伸整客户区玻璃，
/// 与 ThumbnailPreview 缺陷 2 的结论一致：DWM 缩略图只在带玻璃通道的宿主上显形）。
/// </para>
/// <para>
/// 【泄漏红线】缩略图句柄不是随进程回收的普通引用：每张注册过的句柄要么在后续 Sync 里继续
/// 持有，要么被注销——不变量"注册次数 == 注销次数"与 ThumbnailPreview 相同。
/// 收口路径全覆盖：① Sync 移除不再期望的源；② 源窗口已销毁时注销；
/// ③ <see cref="Clear"/>（卡片清空 / 窗口收起 / 关闭）全数注销；④ <see cref="Dispose"/> 转发 Clear。
/// </para>
/// <para>
/// 【诚实边界】注册 HRESULT 全 0 ≠ 屏幕上真有像素：仓库在 24H2 VM 上实测过"三步全 0 却不合成"
/// 的环境（ThumbnailPreview 缺陷 2 / StillPreviewWindow 的由来）。本类不做抓帧兜底——台前调度的
/// 画面必须走实况通道，兜底静帧不是它的语义；那种环境里卡片退化为"图标 + 标题"占位，真机目视
/// 确认为准。卡片预留矩形自带底色，最坏情况是一块与卡同色的空位，绝无黑框。
/// </para>
/// <para>
/// 【线程模型】仅限 WPF UI 线程（舞台条窗口的消息泵上）。窗口销毁后调用 Sync/Clear 为 no-op。
/// </para>
/// </summary>
internal sealed class StageThumbStrip : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IntPtr _host;                                    // 舞台条窗口句柄
    private readonly Dictionary<IntPtr, IntPtr> _thumbs = new();      // 源窗口 → 缩略图句柄
    private bool _unavailableLogged;

    public StageThumbStrip(IntPtr hostHwnd)
    {
        _host = hostHwnd;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    /// <summary>当前持有的缩略图句柄数（泄漏断言用：应恒等于屏上卡片数）。</summary>
    public int RegisteredCount => _thumbs.Count;

    /// <summary>
    /// 按期望集合对账：期望内已有 → 只更新目标矩形；缺 → 注册；多余/源已销毁 → 注销。
    /// <paramref name="items"/> 为卡片缩略矩形（<b>宿主客户区坐标、物理像素</b>）。
    /// 返回"期望项是否全部注册成功"（个别失败不抛异常，只记日志；该卡片退化为占位底色）。
    /// </summary>
    public bool Sync(IReadOnlyList<(IntPtr Source, Rect DestClientPx)> items)
    {
        _dispatcher.VerifyAccess();
        if (_host == IntPtr.Zero || !IsWindow(_host))
        {
            Clear();
            return false;
        }

        var allOk = true;
        var wanted = new HashSet<IntPtr>();

        foreach (var (source, dest) in items)
        {
            if (source == IntPtr.Zero || !IsWindow(source)) continue;   // 源没了：不再期望，落到下面统一清理
            wanted.Add(source);

            if (_thumbs.TryGetValue(source, out var thumb))
            {
                if (UpdateDest(thumb, dest)) continue;
                // 属性更新失败（源刚刚销毁等）：注销后按"未注册"处理，下一拍 Sync 重新注册。
                Unregister(source);
                allOk = false;
                continue;
            }

            var hr = DwmRegisterThumbnail(_host, source, out var created);
            if (hr != 0 || created == IntPtr.Zero)
            {
                LogUnavailableOnce($"DwmRegisterThumbnail 失败 hr=0x{hr:X8} src=0x{source.ToInt64():X}");
                allOk = false;
                continue;
            }
            _thumbs[source] = created;
            if (!UpdateDest(created, dest))
            {
                Unregister(source);   // 注册了但首帧属性写不进去：不留悬空句柄
                allOk = false;
            }
        }

        // 期望之外的（卡片被对账移除 / 源已销毁）全部注销。
        if (_thumbs.Count > 0)
        {
            List<IntPtr>? stale = null;
            foreach (var source in _thumbs.Keys)
                if (!wanted.Contains(source)) (stale ??= new List<IntPtr>()).Add(source);
            if (stale is not null)
                foreach (var source in stale)
                    Unregister(source);
        }
        return allOk;
    }

    /// <summary>全数注销（收起 / 关闭 / 宿主失效时调用；可安全重复调用）。</summary>
    public void Clear()
    {
        _dispatcher.VerifyAccess();
        if (_thumbs.Count == 0) return;
        foreach (var thumb in _thumbs.Values)
            DwmUnregisterThumbnail(thumb);
        _thumbs.Clear();
    }

    public void Dispose() => Clear();

    private bool UpdateDest(IntPtr thumb, Rect dest)
    {
        var props = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = DwmtRectDestination | DwmtVisible | DwmtOpacity | DwmtSourceClientAreaOnly,
            rcDestination = new RECT
            {
                Left = (int)Math.Round(dest.Left),
                Top = (int)Math.Round(dest.Top),
                Right = (int)Math.Round(dest.Right),
                Bottom = (int)Math.Round(dest.Bottom)
            },
            rcSource = default,
            opacity = 255,
            fVisible = true,
            fSourceClientAreaOnly = true
        };
        var hr = DwmUpdateThumbnailProperties(thumb, ref props);
        if (hr != 0) Logger.Warn($"DwmUpdateThumbnailProperties 失败 hr=0x{hr:X8}（舞台条卡片）");
        return hr == 0;
    }

    private void Unregister(IntPtr source)
    {
        if (!_thumbs.Remove(source, out var thumb)) return;
        var hr = DwmUnregisterThumbnail(thumb);
        if (hr != 0) Logger.Warn($"DwmUnregisterThumbnail 返回 hr=0x{hr:X8}（句柄仍视为已释放）");
    }

    private void LogUnavailableOnce(string reason)
    {
        if (_unavailableLogged) return;
        _unavailableLogged = true;
        Logger.Warn($"台前调度：实况缩略图注册不可用（卡片退化为图标占位，同类原因后续不再逐次记录）：{reason}");
    }
}
