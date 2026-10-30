using System;
using System.Windows.Interop;
using Beautiful.App.Interop;
using Beautiful.App.Services;

namespace Beautiful.App.Launcher;

/// <summary>
/// F4 全局热键（v4 Phase 5-T1）：消息窗 + RegisterHotKey；不抢共享低级键盘钩子、不吞键。
/// 注册失败（其它程序占用）只记日志，Launchpad 仍可从 `--launchpad` 打开。
/// </summary>
public sealed class LaunchpadHotkeyService : IDisposable
{
    private const int HotkeyId = 0xBEA7;
    private const int WmHotkey = 0x0312;
    private const uint VkF4 = 0x73;
    private const uint ModNoRepeat = 0x4000;
    private static readonly IntPtr HwndMessage = new(-3);

    private HwndSource? _source;
    private bool _registered;

    public bool IsRegistered => _registered;
    public event Action? Pressed;

    public void Start()
    {
        if (_source is not null) return;
        try
        {
            var parameters = new HwndSourceParameters("Beautiful.LaunchpadHotkey")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
                ExtendedWindowStyle = 0,
                ParentWindow = HwndMessage,
            };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
            _registered = RegisterHotKey(_source.Handle, HotkeyId, ModNoRepeat, VkF4);
            if (_registered) Logger.Info("启动台：F4 全局热键已注册");
            else Logger.Warn($"启动台：F4 注册失败（可能被占用），错误={System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"启动台热键初始化失败: {ex.Message}");
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

    public void Stop()
    {
        if (_source is null) return;
        try
        {
            if (_registered) UnregisterHotKey(_source.Handle, HotkeyId);
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
        catch (Exception ex) { Logger.Warn($"启动台热键停止失败: {ex.Message}"); }
        finally
        {
            _source = null;
            _registered = false;
            Pressed = null;
        }
    }

    public void Dispose() => Stop();

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
