using System;
using System.Windows.Threading;
using Beautiful.App.Controls;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Controls;

namespace Beautiful.App.Services;

/// <summary>
/// 音量 HUD 的接线层（Phase 4-T7）：媒体键 → 改系统音量 → 弹 HUD。
/// <para>
/// 【为什么要吞掉媒体键】不吞的话 Windows 自己的音量浮层会同时在左上角出现，
/// 屏幕上就有两个音量提示，macOS 还原当场破功。所以这里用 WH_KEYBOARD_LL 识别
/// <c>VK_VOLUME_UP/DOWN/MUTE</c>，**吞掉按下与抬起**，改由 <see cref="SystemVolumeService"/>
/// 直接写端点音量，再画自家 HUD。副作用是：一旦本进程钩子失效，音量键会立刻回到系统行为——
/// 这正是我们要的降级方向（宁可多个系统浮层，也不能让音量键失灵）。
/// </para>
/// <para>
/// 【回调纪律】钩子回调里只判断 vk 与投递，不碰 COM、不碰 UI、不写日志（见
/// <see cref="LowLevelKeyboardHook"/> 的 300ms 摘钩说明）；读取/写入音量全部在 UI 线程完成。
/// </para>
/// <para>
/// 【幂等与安全】老版本不存在这个开关，故新增设置 <c>VolumeOsdEnabled</c> 默认开；
/// 关掉时控制器连钩子都不装，媒体键行为与系统默认完全一致。
/// </para>
/// </summary>
public sealed class VolumeOsdController : IDisposable
{
    private const int VkVolumeMute = 0xAD;
    private const int VkVolumeDown = 0xAE;
    private const int VkVolumeUp = 0xAF;
    private const uint LlkhfInjected = 0x10;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;

    private readonly Dispatcher _dispatcher;
    private readonly SystemVolumeService? _volume;
    private readonly LowLevelKeyboardHook? _hook;
    private VolumeOsdWindow? _window;
    private bool _disposed;

    public VolumeOsdController(bool enabled, Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;

        if (!enabled)
        {
            Logger.Info("音量 HUD：设置里已关闭，不接管媒体键");
            return;
        }

        if (SystemVolumeService.DisabledByEnvironment)
        {
            Logger.Info("音量 HUD：BEAUTIFUL_NOAUDIO 生效，不接管媒体键");
            return;
        }

        var volume = new SystemVolumeService();
        if (!volume.IsAvailable)
        {
            Logger.Warn($"音量 HUD：系统音量不可用（{volume.UnavailableReason}），媒体键保持系统行为");
            volume.Dispose();
            return;
        }

        _volume = volume;

        var hook = new LowLevelKeyboardHook(OnKey);
        if (!hook.Install())
        {
            Logger.Warn("音量 HUD：媒体键钩子安装失败，媒体键保持系统行为");
            hook.Dispose();
            return;
        }

        _hook = hook;
        Logger.Info("音量 HUD：已接管媒体键（音量 + / − / 静音）");
    }

    /// <summary>诊断入口：直接显示一次 HUD（不改音量，不依赖按键）。</summary>
    public void ShowDiagnostic(int percent, bool muted, TimeSpan? hold = null)
    {
        ShowOnUi(percent, muted, hold ?? TimeSpan.FromSeconds(30));
    }

    private bool OnKey(int msg, uint vkCode, uint flags)
    {
        if (_disposed || _hook is null)
        {
            return false;
        }

        // 注入键（输入法/自动化/密码管理器）一律放行，绝不吞别人的合成按键
        if ((flags & LlkhfInjected) != 0)
        {
            return false;
        }

        if (vkCode is not (VkVolumeUp or VkVolumeDown or VkVolumeMute))
        {
            return false;
        }

        var isDown = msg is WmKeyDown or WmSysKeyDown;
        if (isDown)
        {
            var vk = vkCode;
            _dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => HandleKey((int)vk)));
        }

        // 按下与抬起都吞：只吞一半会让系统收到"抬起而没按下"的怪序列
        return true;
    }

    private void HandleKey(int vk)
    {
        if (_disposed || _volume is null)
        {
            return;
        }

        if (!_volume.TryGetVolume(out var scalar, out var muted))
        {
            Logger.Warn("音量 HUD：读取系统音量失败，本次按键忽略");
            return;
        }

        var percent = VolumeMath.ToPercent(scalar);
        try
        {
            if (vk == VkVolumeMute)
            {
                var nowMuted = _volume.ToggleMute();
                if (nowMuted is null)
                {
                    return;
                }

                muted = nowMuted.Value;
            }
            else
            {
                percent = VolumeOsd.Step(percent, up: vk == VkVolumeUp);
                if (!_volume.TrySetPercent(percent))
                {
                    return;
                }

                // 调音量即解除静音（macOS 语义：用户按音量键就是在要求"我能听见"）
                if (muted)
                {
                    _volume.TrySetMuted(false);
                    muted = false;
                }
            }

            // 以系统真实值为准重读一遍：不显示"我们以为的"音量
            if (_volume.TryGetVolume(out var actual, out var actualMuted))
            {
                percent = VolumeMath.ToPercent(actual);
                muted = actualMuted;
            }

            ShowOnUi(percent, muted);
            Logger.Info($"音量 HUD：{VolumeOsd.Describe(percent, muted)}（key=0x{vk:X2}）");
        }
        catch (Exception ex)
        {
            Logger.Warn($"音量 HUD：按键处理失败 {ex.GetType().Name} {ex.Message}");
        }
    }

    private void ShowOnUi(int percent, bool muted, TimeSpan? hold = null)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(() => ShowOnUi(percent, muted, hold)));
            return;
        }

        try
        {
            // 上一次淡出后窗口已 Close，不能复用（WPF 关闭的 Window 不可再 Show）：
            // 检测到已关闭就重建一个。这是"第一次能用、第二次按音量键没反应"的根因。
            if (_window is null || !_window.IsLoaded)
            {
                _window = new VolumeOsdWindow();
            }

            _window.ShowState(percent, muted, hold);
        }
        catch (Exception ex)
        {
            Logger.Warn($"音量 HUD：显示失败 {ex.GetType().Name} {ex.Message}");
        }
    }

    /// <summary>当前 HUD 窗口句柄（截图诊断用；没有则 Zero）。</summary>
    public IntPtr DiagnosticsHandle => VolumeOsdWindow.HandleOf(_window);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hook?.Dispose();
        _volume?.Dispose();

        try
        {
            _window?.Close();
        }
        catch (InvalidOperationException)
        {
            // 窗口已关：忽略
        }

        _window = null;
    }
}
