using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Beautiful.App.Dock;
using Beautiful.App.Interop;
using Beautiful.Core.Dock;

namespace Beautiful.App.Services;

/// <summary>
/// 回收站满/空轮询（alpha.29 残项清零）：每 5 秒用 <c>SHQueryRecycleBin(null, …)</c> 读一次
/// 全部卷的条目数，满/空翻转时经 <see cref="DockViewModel.UpdateRecycleBin"/> 换回收站槽实例 →
/// 垃圾桶图标 trash_empty ↔ trash_full 实时切换（Assets/TahoeIcons 的两枚真 macOS 26 废纸篓），
/// 与右键菜单"清空回收站"可用性同源（同一次 shell 读数的两条口径，判定在 Core
/// <see cref="RecycleBinSlot"/>，有单测）。
/// <para>
/// 【为什么轮询而不是 shell 事件】SHChangeNotifyRegister 要挂消息窗 + COM 事件 cookie，
/// 而 SHQueryRecycleBin 是一次查表式 shell 调用（读各卷 $Recycle.Bin 元数据，右键菜单打开时
/// 已经在 UI 线程调过它）；5s 一拍对 CPU 无感知。轮询与菜单的取数是同一个 API，两边永远读
/// 同一份事实。
/// </para>
/// <para>
/// 【失败语义】hr ≠ 0（个别卷被组策略挡住、网络卷暂不可达等）时<b>保持上一次状态</b>——
/// 图标不能因为一次读数失败来回抖；右键菜单另有保守口径（失败当作"有可清"，Core
/// <see cref="RecycleBinSlot.MenuAssumesItems"/>），两条口径为什么方向相反见该文件注释。
/// </para>
/// </summary>
internal sealed class RecycleBinMonitor : IDisposable
{
    /// <summary>轮询间隔（秒）：删文件/清空回收站是低频事件，5s 足够"实时"观感且无感知开销。</summary>
    public const int PollSeconds = 5;

    private readonly DockViewModel _vm;
    private readonly DispatcherTimer _timer;
    private bool _lastFull;
    private bool _disposed;

    public RecycleBinMonitor(DockViewModel viewModel, Dispatcher dispatcher)
    {
        _vm = viewModel;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(PollSeconds), DispatcherPriority.Background,
            (_, _) => Refresh(), dispatcher);
        _timer.Start();
        Refresh();   // 首帧就校正：垃圾桶实际是满的，别让它空着 5 秒
    }

    /// <summary>读一次回收站读数并落地（满/空翻转才动 Items；引用与状态不变时零事件）。</summary>
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var info = new NativeMethods.SHQUERYRBINFO
            {
                cbSize = Marshal.SizeOf<NativeMethods.SHQUERYRBINFO>()
            };
            var hr = NativeMethods.SHQueryRecycleBin(null, ref info);
            if (hr != 0) return;   // 读数失败：保持现状，图标不抖（见类型注释的失败语义）
            var full = RecycleBinSlot.IsFull(info.i64NumItems);
            if (full == _lastFull) return;
            _lastFull = full;
            _vm.UpdateRecycleBin(full);
            Logger.Info($"回收站状态：{(full ? "满（trash_full）" : "空（trash_empty）")}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"回收站状态刷新失败（保持现状）：{ex.GetType().Name} {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
    }
}
