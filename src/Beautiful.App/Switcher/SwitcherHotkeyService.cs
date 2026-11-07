using System.Runtime.InteropServices;
using System.Windows.Interop;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Launcher;

namespace Beautiful.App.Switcher;

/// <summary>
/// 切换器<b>呼出</b>热键（alpha.29 残项清零）：消息窗 + RegisterHotKey；与
/// <see cref="Launcher.LaunchpadHotkeyService"/> 同一模板、同一纪律——不抢共享低级键盘钩子、
/// 不吞键、不参与切换会话。注册失败（组合被占用）只记日志，面板仍可经 --switcher / Alt+Tab 到达。
/// <para>
/// 【为什么独立成服务而不是接进 AltTabHook】低级钩子对注入键（脚本 keybd_event / 远程桌面 /
/// 屏幕键盘）一律放行且不碰状态位（AltTabHook ⓪/① 的既有裁定：切换器分支要跟踪 Alt 会话，
/// 注入的 Alt 会开出幽灵会话）——于是"用脚本/远程唤出切换器"在钩子路径上永远打不通。
/// 消息窗热键是 OS 层的注册表：WM_HOTKEY 是一次<b>无状态边沿</b>，只发 OpenRequested，
/// 与钩子的状态机零接触（同启动器 ⓪ 分支"无状态打开面板"的理由），也就不会被注入键检查挡住。
/// </para>
/// <para>
/// 【默认键位与占用】Ctrl + `（<see cref="LauncherHotkey.DefaultSwitcherOpen"/>，用户可改）。
/// 刻意避开：启动台 F4（HotkeyId=0xBEA7）、启动器 Ctrl+Alt+Space、系统 IME/布局的
/// Ctrl/Win/Alt+Space。HotkeyId 取相邻的 0xBEA8，两个服务互不冲突。
/// </para>
/// </summary>
public sealed class SwitcherHotkeyService : IDisposable
{
    private const int HotkeyId = 0xBEA8;
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const uint ModAlt = 0x0001, ModControl = 0x0002, ModShift = 0x0004, ModWin = 0x0008;
    private static readonly IntPtr HwndMessage = new(-3);

    private HwndSource? _source;
    private bool _registered;

    public bool IsRegistered => _registered;
    public event Action? Pressed;

    /// <summary>
    /// 按设置串（如 <c>ctrl+grave</c>）注册呼出热键（须在 UI 线程；重注册前先注销旧键，幂等）。
    /// 解析走 <see cref="LauncherHotkey"/>（与设置侧规范化同一套规则），主键码走
    /// <see cref="AltTabHook.VirtualKeyOf"/>（一张映射表服务两个消费者，不复制第二份）。
    /// 解析失败/主键无键码：注销旧键并只记日志（面板不因一颗坏热键失联）。
    /// </summary>
    public void Configure(string? hotkey)
    {
        UnregisterCurrent();
        if (_source is null) Start();
        if (_source is null) return;

        if (!LauncherHotkey.TryParse(hotkey, out var gesture))
        {
            Logger.Warn($"切换器：呼出热键 \"{hotkey}\" 无法解析 → 本次不注册（面板仍可经 Alt+Tab/--switcher 到达）");
            return;
        }
        var vk = AltTabHook.VirtualKeyOf(gesture.Key);
        if (vk == 0)
        {
            Logger.Warn($"切换器：呼出热键 \"{hotkey}\" 的主键 {gesture.Key} 没有键码映射 → 本次不注册");
            return;
        }

        var modifiers = ModNoRepeat
            | (gesture.Ctrl ? ModControl : 0)
            | (gesture.Alt ? ModAlt : 0)
            | (gesture.Win ? ModWin : 0)
            | (gesture.Shift ? ModShift : 0);
        _registered = RegisterHotKey(_source.Handle, HotkeyId, modifiers, (uint)vk);
        if (_registered)
            Logger.Info($"切换器：呼出热键已注册 {gesture.ToText()}（VK=0x{vk:X2}）");
        else
            Logger.Warn($"切换器：呼出热键 {gesture.ToText()} 注册失败（可能被其它程序占用），错误={Marshal.GetLastWin32Error()}");
    }

    /// <summary>按当前设置重新注册（设置页改键的即时生效入口，幂等）。</summary>
    public void Reconfigure() => Configure(SettingsService.Current.SwitcherOpenHotkey);

    private void Start()
    {
        try
        {
            var parameters = new HwndSourceParameters("Beautiful.SwitcherHotkey")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                ExtendedWindowStyle = 0,
                ParentWindow = HwndMessage,
            };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
        }
        catch (Exception ex)
        {
            Logger.Warn($"切换器呼出热键初始化失败: {ex.Message}");
            Stop();
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    private void UnregisterCurrent()
    {
        if (_source is null || !_registered) return;
        try { UnregisterHotKey(_source.Handle, HotkeyId); }
        catch (Exception ex) { Logger.Warn($"切换器呼出热键注销失败: {ex.Message}"); }
        _registered = false;
    }

    public void Stop()
    {
        if (_source is null) return;
        try
        {
            UnregisterCurrent();
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
        catch (Exception ex) { Logger.Warn($"切换器呼出热键停止失败: {ex.Message}"); }
        finally
        {
            _source = null;
            _registered = false;
            Pressed = null;
        }
    }

    public void Dispose() => Stop();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
