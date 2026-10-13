using System.Windows.Media.Imaging;

namespace Beautiful.App.Dock;

public enum DockItemKind
{
    Pinned,
    Window,
    RecycleBin,
    Media
}

/// <summary>
/// Dock 单个条目（record，不可变；运行窗口标题变化时由 DockViewModel 以新实例原位替换）。
/// </summary>
/// <param name="Id">稳定标识：固定项用路径/固定 id，窗口项用句柄字符串。</param>
/// <param name="Label">显示名（exe 名/窗口标题/回收站等）。</param>
/// <param name="Icon">图标；null 时视图回退占位字形。</param>
/// <param name="Kind">条目种类。</param>
/// <param name="LaunchPath">启动目标：exe 路径或 shell: 路径。</param>
/// <param name="WindowHandle">Kind=Window 时的顶层窗口句柄。</param>
public record DockItem(
    string Id,
    string Label,
    BitmapSource? Icon,
    DockItemKind Kind,
    string LaunchPath,
    IntPtr? WindowHandle = null);
