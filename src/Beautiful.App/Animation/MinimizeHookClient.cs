using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Beautiful.App.Interop;
using Beautiful.App.Services;

namespace Beautiful.App.Animation;

/// <summary>
/// 与 <c>native/hook/Beautiful.Hook.dll</c>（MinGW 交叉编译的 CBT 前置钩子层）配对的宿主端。
/// <para>
/// 【它改变了什么】alpha.15 及以前，标题栏按钮 / Win+↓ / 任务栏三条最小化路径走
/// <c>EVENT_SYSTEM_MINIMIZESTART</c>——事件到手时窗口已经 iconic，原生"缩向任务栏"
/// 动画已经播完或正在播，我们的 genie 只是事后补播（真机反馈：两动画打架 / 白光 / 像没有）。
/// 现在钩子层在<b>原生动画开始之前</b>抓帧并阻断原生最小化（返回 1），由本端播 genie、
/// 播完静默落位——原生动画被真替换，而不是叠加。
/// </para>
/// <para>
/// 【安全模型】DLL 是注入组件：装不上 / 加载失败 / 任何异常 → 本端如实降级回
/// alpha.15 的事后路径（WinEvent + 前台帧缓存），最小化本身永远是系统的，我们只添动画。
/// </para>
/// <para>
/// 【共享内存布局】与 C 侧 <c>BH_SHARED / BH_SLOT</c> 逐字节对齐（x64：8 字节对齐）：
/// 头 48 字节；随后两个槽，各 24 字节槽头 + <c>SlotBytes</c> 像素（BGRA 自上而下）。
/// </para>
/// </summary>
internal sealed class MinimizeHookClient : IDisposable
{
    private const string SharedName = "Local\\Beautiful.Hook.Shared.v1";
    private const string AckName = "Local\\Beautiful.Hook.Ack.v1";
    private const string NotifyMessageName = "Beautiful.Hook.Notify.v1";

    private const uint Magic = 0x31484B42u;   // "BHK1"
    private const int SharedHeaderBytes = 48; // BH_SHARED（含尾部对齐）
    private const int SlotHeaderBytes = 24;   // BH_SLOT
    private const int MaxWidth = 2560;
    private const int MaxHeight = 1440;
    private const int SlotCount = 2;

    [DllImport("Beautiful.Hook.dll", EntryPoint = "BH_Install")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BH_Install(IntPtr host, uint msg);

    [DllImport("Beautiful.Hook.dll", EntryPoint = "BH_Uninstall")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BH_Uninstall();

    /// <summary>钩子层的可用状态行（诊断块用）。null = 不可用。</summary>
    public string? Status { get; private set; }

    /// <summary>DLL 是否已成功加载（供 HookBridge 快速短路，避免每次调用抛异常）。</summary>
    public bool Available => Status is not null && Status.Contains("已挂载");

    private readonly MinimizeAnimator _animator;
    private readonly Dispatcher _dispatcher;
    private MemoryMappedFile? _shared;
    private MemoryMappedViewAccessor? _view;
    private EventWaitHandle? _ack;
    private HwndSource? _source;
    private IntPtr _hostHwnd;
    private uint _notifyMsg;
    private bool _disposed;

    public MinimizeHookClient(MinimizeAnimator animator)
    {
        _animator = animator;
        _dispatcher = Dispatcher.CurrentDispatcher;

        var dllPath = Path.Combine(AppContext.BaseDirectory, "Beautiful.Hook.dll");
        if (!File.Exists(dllPath))
        {
            Status = "CBT 前置钩子不可用：Beautiful.Hook.dll 不在程序目录（回退事后路径）";
            Logger.Info(Status);
            return;
        }

        try
        {
            var slotBytes = MaxWidth * MaxHeight * 4;
            var sectionSize = SharedHeaderBytes + SlotCount * (SlotHeaderBytes + slotBytes);
            _shared = MemoryMappedFile.CreateOrOpen(SharedName, sectionSize, MemoryMappedFileAccess.ReadWrite);
            _view = _shared.CreateViewAccessor(0, sectionSize, MemoryMappedFileAccess.ReadWrite);
            _view.Write(0, Magic);
            _view.Write(4, 1u);                    // Version
            _view.Write(36, slotBytes);            // SlotBytes
            _view.Write(40, SlotCount);            // SlotCount
            // HostHwnd(8) / NotifyMsg(16) 在拿到消息窗口后补写（BH_Install 之前必须就位）

            _ack = new EventWaitHandle(false, EventResetMode.AutoReset, AckName, out var createdNew);
            if (!createdNew)
                Logger.Info("CBT 钩子：确认事件已存在（上一会话残留，直接复用）");

            // 消息窗口：只收钩子层的"我抓到一帧了"通知，不参与任何 UI
            _notifyMsg = NativeMethods.RegisterWindowMessage(NotifyMessageName);
            _source = new HwndSource(new HwndSourceParameters("Beautiful.Hook.Host")
            {
                ParentWindow = new IntPtr(-3),          // HWND_MESSAGE
                WindowStyle = 0,
                Width = 0,
                Height = 0,
            });
            _source.AddHook(WndProc);
            _hostHwnd = _source.Handle;
            _view.Write(8, _hostHwnd.ToInt64());        // HostHwnd
            _view.Write(16, _notifyMsg);                // NotifyMsg

            if (!BH_Install(_hostHwnd, _notifyMsg))
            {
                Status = "CBT 前置钩子：BH_Install 失败（SetWindowsHookEx 被拒？回退事后路径）";
                Logger.Warn(Status);
                return;
            }

            Status = "CBT 前置钩子已挂载：原生最小化动画将被拦截并由 genie 真替换";
            Logger.Info(Status);
            HookBridge.Available = true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
                                     or IOException or UnauthorizedAccessException
                                     or System.ComponentModel.Win32Exception)
        {
            Status = $"CBT 前置钩子不可用（{ex.GetType().Name}）：回退事后路径";
            Logger.Warn(Status);
            CleanupLocal();
        }
    }

    /// <summary>程序化最小化前的抑制开关（防钩子拦自家动作）。Dock 等路径共用。</summary>
    internal static void Suppress(IntPtr hwnd)
    {
        if (!HookBridge.Available) return;
        try { HookBridge.BH_Suppress(hwnd); }
        catch (DllNotFoundException) { HookBridge.Available = false; }
        catch (Exception ex) { Logger.Warn($"BH_Suppress 异常（已隔离）：{ex.Message}"); }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != (int)_notifyMsg || _disposed) return IntPtr.Zero;
        handled = true;

        var seq = (uint)wParam.ToInt64();
        var target = lParam;

        // 先拷像素再确认：注入侧在 40ms 内等确认，拷帧（≤14MB）远快于超时
        BitmapSource? image = null;
        try { image = TryReadFrame(seq); }
        catch (Exception ex)
        {
            Logger.Warn($"CBT 钩子：读帧失败（{ex.GetType().Name} {ex.Message}），放行原生路径");
        }
        finally
        {
            try { _ack?.Set(); } catch (ObjectDisposedException) { }
        }

        _dispatcher.BeginInvoke(() => HandleNotify(target, image), DispatcherPriority.Send);
        return IntPtr.Zero;
    }

    private void HandleNotify(IntPtr hwnd, BitmapSource? image)
    {
        try
        {
            if (hwnd == IntPtr.Zero || hwnd == _hostHwnd) return;
            if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                _animator.SilentMinimize(hwnd, "CBT 接管但矩形失效");
                return;
            }
            _animator.PlayMinimizeFromHook(hwnd, image, new Rect(rect.Left, rect.Top,
                rect.Right - rect.Left, rect.Bottom - rect.Top));
        }
        catch (Exception ex)
        {
            // 已向注入侧确认接管，异常也必须把最小化做完：交给静默路径兜底
            Logger.Warn($"CBT 钩子：播发异常（{ex.GetType().Name} {ex.Message}），静默落位兜底");
            try { _animator.SilentMinimize(hwnd, "CBT 播发异常兜底"); } catch { /* 兜底再失败只能放弃 */ }
        }
    }

    private BitmapSource? TryReadFrame(uint seq)
    {
        if (_view is null) return null;
        var slotBytes = _view.ReadUInt32(36);
        if (slotBytes == 0) return null;
        var slot = (int)(seq & 1);
        var slotBase = SharedHeaderBytes + slot * (SlotHeaderBytes + (int)slotBytes);

        var ready = _view.ReadUInt32(slotBase);
        if (ready != seq) return null;                      // 槽内容已过期（另一窗口抢先写入）
        var width = (int)_view.ReadUInt32(slotBase + 4);
        var height = (int)_view.ReadUInt32(slotBase + 8);
        var stride = (int)_view.ReadUInt32(slotBase + 12);
        if (width <= 0 || height <= 0 || stride < width * 4) return null;

        using var stream = _shared!.CreateViewStream(slotBase + SlotHeaderBytes, (long)slotBytes,
            MemoryMappedFileAccess.Read);
        var bytes = new byte[(long)stride * height];
        var validPerRow = width * 4;
        for (var y = 0; y < height; y++)
        {
            stream.Seek((long)y * stride, SeekOrigin.Begin);
            var read = stream.Read(bytes, y * stride, validPerRow);
            if (read < validPerRow) return null;
        }

        var bmp = BitmapSource.Create(width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32,
            null, bytes, stride);
        bmp.Freeze();
        return bmp;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { HookBridge.BH_Suppress(IntPtr.Zero); } catch { /* 尽力而为 */ }
        try { BH_Uninstall(); } catch { /* DLL 已不在：等效卸载 */ }
        CleanupLocal();
    }

    private void CleanupLocal()
    {
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
        _view?.Dispose();
        _view = null;
        _shared?.Dispose();
        _shared = null;
        _ack?.Dispose();
        _ack = null;
    }
}

/// <summary>
/// 钩子层 P/Invoke 的静态桥（供动画器在无 <see cref="MinimizeHookClient"/> 实例时也能抑制）。
/// <see cref="Available"/> 由客户端在成功安装后置位；DLL 缺失时保持 false，调用方零开销短路。
/// </summary>
internal static class HookBridge
{
    internal static bool Available { get; set; }

    [DllImport("Beautiful.Hook.dll", EntryPoint = "BH_Suppress")]
    internal static extern void BH_Suppress(IntPtr hwnd);
}
