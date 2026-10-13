using System.Runtime.InteropServices;
using Beautiful.App.Interop;
using static Beautiful.App.Interop.NativeMethods;

namespace Beautiful.App.Services;

public enum TaskbarPolicy { Keep, AutoHide, Hide }

/// <summary>原生任务栏策略管理。Hide=移出屏幕（可还原）；AutoHide=系统级自动隐藏。</summary>
public sealed class TaskbarManager
{
    private readonly Dictionary<IntPtr, RECT> _saved = new();
    private TaskbarPolicy _policy = TaskbarPolicy.Keep;

    public event Action? PolicyApplied;
    public TaskbarPolicy Current => _policy;

    public void Apply(TaskbarPolicy policy)
    {
        RestoreInternal();
        _policy = policy;
        switch (policy)
        {
            case TaskbarPolicy.AutoHide: SetAutoHide(true); break;
            case TaskbarPolicy.Hide: HideAll(); break;
        }
        PolicyApplied?.Invoke();
    }

    /// <summary>explorer 重启（TaskbarCreated 广播）后重新应用当前策略。</summary>
    public void HandleTaskbarCreated() => Apply(_policy);

    /// <summary>退出前调用：完整还原。</summary>
    public void Restore()
    {
        RestoreInternal();
        _policy = TaskbarPolicy.Keep;
        PolicyApplied?.Invoke();
    }

    private void RestoreInternal()
    {
        SetAutoHide(false);
        foreach (var (hwnd, rect) in _saved)
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, rect.Left, rect.Top,
                rect.Right - rect.Left, rect.Bottom - rect.Top,
                NativeMethods.SwpNozorder | NativeMethods.SwpNoactivate);
        _saved.Clear();
    }

    private static void SetAutoHide(bool on)
    {
        var abd = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        abd.lParam = on ? (NativeMethods.AbsAutohide | NativeMethods.AbsAlwaysOnTop) : NativeMethods.AbsAlwaysOnTop;
        NativeMethods.SHAppBarMessage(NativeMethods.AbmSetState, ref abd);
    }

    private void HideAll()
    {
        var screenH = NativeMethods.GetSystemMetrics(1); // SM_CYSCREEN
        foreach (var hwnd in FindTaskbars())
        {
            if (!NativeMethods.GetWindowRect(hwnd, out var rect)) continue;
            _saved[hwnd] = rect;
            // 顶边贴到主屏底边 → 完全移出屏幕；SetWindowPos 会保留还原所需句柄信息于 _saved
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, rect.Left, screenH,
                rect.Right - rect.Left, rect.Bottom - rect.Top,
                NativeMethods.SwpNozorder | NativeMethods.SwpNoactivate);
        }
    }

    private static List<IntPtr> FindTaskbars()
    {
        var list = new List<IntPtr>();
        NativeMethods.EnumWindows((h, _) =>
        {
            var cls = NativeMethods.GetWindowClassName(h);
            if (cls is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
    }
}
