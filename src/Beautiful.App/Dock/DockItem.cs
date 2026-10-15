using System.Windows.Media.Imaging;
using Beautiful.Core.Dock;   // DockItemKind 已迁到 Core（行结构规则要可单测），此处只留 WPF 图标类型

namespace Beautiful.App.Dock;

/// <summary>
/// Dock 单个条目（record，不可变；运行窗口标题变化时由 DockViewModel 以新实例原位替换）。
/// </summary>
/// <param name="Id">稳定标识：固定项用路径/固定 id，窗口项用句柄字符串，分隔符用槽位标识。</param>
/// <param name="Label">显示名（exe 名/窗口标题/回收站等）。</param>
/// <param name="Icon">图标；null 时视图回退占位字形。</param>
/// <param name="Kind">条目种类（<see cref="DockItemKind.Separator"/> 表示分隔符槽：不放大、不可点击）。</param>
/// <param name="LaunchPath">启动目标：exe 路径或 shell: 路径。</param>
/// <param name="WindowHandle">Kind=Window 时的顶层窗口句柄。</param>
/// <param name="IconSource">
/// **实际用于提取图标的源**（v3 T6）：固定项是解析过 .lnk 之后的文件路径、运行窗口是进程 exe 路径、
/// 回收站是 shell 命名空间 id。烘焙缓存按它取键，因此它必须稳定且能区分不同图标；
/// 为 null 时转换器退回用原位图实例的引用身份。
/// </param>
public record DockItem(
    string Id,
    string Label,
    BitmapSource? Icon,
    DockItemKind Kind,
    string LaunchPath,
    IntPtr? WindowHandle = null,
    string? IconSource = null);
