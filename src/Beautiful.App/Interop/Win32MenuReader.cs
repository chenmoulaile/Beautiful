using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Beautiful.App.Services;
using Beautiful.Core.TopBar;

namespace Beautiful.App.Interop;

/// <summary>
/// 只读镜像前台应用的 Win32 菜单（v4 Phase 4-T3）：GetMenu 递归读取标签/命令 ID/勾选/禁用/分隔线；
/// 点击时按命令 ID PostMessage(WM_COMMAND) 回发。自绘菜单（Chrome/Office/UWP）GetMenu 为空 → 返回空表，
/// 调用方给置灰说明，绝不伪造。
/// </summary>
internal static class Win32MenuReader
{
    private const uint MfByPosition = 0x00000400;
    private const uint MfSeparator = 0x00000800;
    private const uint MfChecked = 0x00000008;
    private const uint MfGrayed = 0x00000001;
    private const uint MfDisabled = 0x00000002;
    private const uint MfPopup = 0x00000010;
    private const int MaxDepth = 3;
    private const int MaxItemsPerLevel = 64;

    public static IReadOnlyList<MirrorMenuItem> Read(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return Array.Empty<MirrorMenuItem>();
        try
        {
            var menu = GetMenu(hwnd);
            return menu == IntPtr.Zero ? Array.Empty<MirrorMenuItem>() : ReadMenu(menu, 0);
        }
        catch (Exception ex)
        {
            Logger.Warn($"真菜单读取失败：{ex.GetType().Name} {ex.Message}");
            return Array.Empty<MirrorMenuItem>();
        }
    }

    /// <summary>把命令 ID 回发给前台窗口（WM_COMMAND）；窗口已换/句柄失效则 no-op。</summary>
    public static bool Invoke(IntPtr hwnd, uint commandId)
    {
        if (hwnd == IntPtr.Zero || commandId == 0) return false;
        try
        {
            var ok = PostMessage(hwnd, WmCommand, new IntPtr(commandId), IntPtr.Zero);
            Logger.Info($"真菜单命令回发：hwnd=0x{hwnd.ToInt64():X} id={commandId} ok={ok}");
            return ok;
        }
        catch (Exception ex)
        {
            Logger.Warn($"真菜单命令回发失败：{ex.Message}");
            return false;
        }
    }

    private static List<MirrorMenuItem> ReadMenu(IntPtr menu, int depth)
    {
        var result = new List<MirrorMenuItem>();
        if (depth > MaxDepth) return result;
        var count = Math.Min(GetMenuItemCount(menu), MaxItemsPerLevel);
        for (var i = 0; i < count; i++)
        {
            var raw = new StringBuilder(512);
            var len = GetMenuStringW(menu, (uint)i, raw, raw.Capacity, MfByPosition);
            var state = GetMenuState(menu, (uint)i, MfByPosition);
            var id = (uint)GetMenuItemIdNative(menu, i);
            var (label, shortcut) = MenuMirrorText.Split(len > 0 ? raw.ToString() : string.Empty);
            var isSeparator = (state & MfSeparator) != 0 || (id == 0 && label.Length == 0);
            var sub = GetSubMenu(menu, i);
            IReadOnlyList<MirrorMenuItem> children = sub == IntPtr.Zero ? Array.Empty<MirrorMenuItem>() : ReadMenu(sub, depth + 1);
            if (isSeparator)
            {
                result.Add(new MirrorMenuItem(string.Empty, null, 0, false, false, true, Array.Empty<MirrorMenuItem>()));
                continue;
            }
            if ((state & MfPopup) != 0 && children.Count == 0 && label.Length == 0) continue;
            result.Add(new MirrorMenuItem(
                label,
                shortcut,
                id,
                (state & (MfGrayed | MfDisabled)) == 0,
                (state & MfChecked) != 0,
                false,
                children));
        }
        return result;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetMenu(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetSubMenu(IntPtr menu, int pos);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern int GetMenuItemIdNative(IntPtr menu, int pos);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMenuStringW(IntPtr menu, uint item, StringBuilder text, int maxCount, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetMenuState(IntPtr menu, uint item, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint WmCommand = 0x0111;
}
