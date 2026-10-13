using System.Runtime.InteropServices;
using Beautiful.App.Interop;
using static Beautiful.App.Interop.NativeMethods;

namespace Beautiful.App.Services;

public enum TaskbarPolicy { Keep, AutoHide, Hide }

/// <summary>
/// 原生任务栏策略管理。Hide=移出屏幕（可还原）；AutoHide=系统级自动隐藏。
/// Win11 XAML 任务栏会把"移出屏幕"的程序性移动在 WM_WINDOWPOSCHANGING 里夹回屏幕内
/// （QA 实测：SetWindowPos 返回成功但矩形原位不动），故 HideAll 先以 SWP_NOSENDCHANGING
/// 绕过该回调尝试，仍被还原时降级 ShowWindow(SW_HIDE) 整窗隐藏；两种路径都记录还原凭据
/// （_saved 原始矩形 + _swHidden 可见性），Restore/策略切换时完整还原。
/// shell 重布局（Dock AppBar 重注册、explorer 重启）可能把任务栏摆回屏幕内，经 Reassert 重申。
/// </summary>
public sealed class TaskbarManager
{
    private readonly Dictionary<IntPtr, RECT> _saved = new(); // 还原基准：首次隐藏时的屏幕内矩形
    private readonly HashSet<IntPtr> _swHidden = new();       // 经 SW_HIDE 隐藏的窗口（还原需 SW_SHOWNA）
    private TaskbarPolicy _policy = TaskbarPolicy.Keep;

    public event Action? PolicyApplied;
    public TaskbarPolicy Current => _policy;

    public void Apply(TaskbarPolicy policy)
    {
        Logger.Info($"任务栏策略: {_policy} → {policy}");
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

    /// <summary>
    /// shell 重布局（Dock AppBar 注册/重注册、explorer 重启等）后重申 Hide 策略：
    /// 任务栏被 shell 摆回屏幕内时重新隐藏。幂等（已移出的窗口跳过）；非 Hide 策略为 no-op。
    /// </summary>
    public void Reassert()
    {
        if (_policy == TaskbarPolicy.Hide) HideAll();
    }

    /// <summary>退出/策略切换前调用：SW_SHOWNA（恢复可见且不抢焦点）恢复被整窗隐藏的任务栏 + 复位矩形 + 关自动隐藏。</summary>
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
        {
            if (_swHidden.Remove(hwnd))
                NativeMethods.ShowWindow(hwnd, NativeMethods.SwShowna); // SW_SHOWNA 先恢复可见（不抢焦点）再复位矩形
            Logger.Info($"Restore: hwnd=0x{hwnd.ToInt64():X} → ({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})");
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, rect.Left, rect.Top,
                rect.Right - rect.Left, rect.Bottom - rect.Top,
                NativeMethods.SwpNozorder | NativeMethods.SwpNoactivate);
        }
        _swHidden.IntersectWith(_saved.Keys); // 残留的死亡句柄（explorer 重启）清理，须在 _saved.Clear() 前：保持 _swHidden ⊆ _saved
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
            if (rect.Top >= screenH)
            {
                Logger.Info($"HideAll: hwnd=0x{hwnd.ToInt64():X} 已在屏幕外 (top={rect.Top})，跳过");
                continue;
            }
            if (!_saved.ContainsKey(hwnd))
                _saved[hwnd] = rect; // 只记首次屏幕内矩形：重复 HideAll（Reassert）不覆盖还原基准
            // 尝试 1：SWP_NOSENDCHANGING 绕过任务栏的 WM_WINDOWPOSCHANGING 回夹直接落位
            var ok = NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, rect.Left, screenH,
                rect.Right - rect.Left, rect.Bottom - rect.Top,
                NativeMethods.SwpNozorder | NativeMethods.SwpNoactivate | NativeMethods.SwpNoSendChanging);
            NativeMethods.GetWindowRect(hwnd, out var after);
            if (after.Top >= screenH)
            {
                Logger.Info($"HideAll: hwnd=0x{hwnd.ToInt64():X} 移出屏幕成功 after=({after.Left},{after.Top})-({after.Right},{after.Bottom})");
                continue;
            }
            Logger.Info($"HideAll: hwnd=0x{hwnd.ToInt64():X} 移动被 shell 还原 (ok={ok} after=({after.Left},{after.Top})-({after.Right},{after.Bottom}))，降级 SW_HIDE");
            // 尝试 2：整窗隐藏（Win11 XAML 任务栏对移动有回夹，对可见性没有）——还原时 SW_SHOWNA
            NativeMethods.ShowWindow(hwnd, NativeMethods.SwHide);
            _swHidden.Add(hwnd);
            Logger.Info($"HideAll: hwnd=0x{hwnd.ToInt64():X} SW_HIDE 完成");
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
