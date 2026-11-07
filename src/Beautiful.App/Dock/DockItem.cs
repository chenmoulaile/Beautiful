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
/// <param name="Detail">
/// 第二行小字（目前只有小组件用：温度 + 天气描述）。其它条目为 null。
/// 单独一个字段而不是塞进 <see cref="Label"/>：Label 是工具提示/无障碍名称，
/// 把温度拼进去会让读屏把"14:05 23° 晴"当成应用名念一遍。
/// </param>
/// <param name="IsRunning">
/// 固定项背后有在跑的窗口时置 true（「一格 = 一个应用」合并后，窗口格被并入固定格）。
/// 单独一个布尔而不改 <see cref="Kind"/>：Kind 决定右键菜单与点击语义，改成 Window 会让固定项
/// 失去「取消固定/以管理员运行」并随窗口关闭而消失；运行指示只该多亮一颗点。
/// </param>
/// <param name="TrashFull">
/// 回收站槽的满/空快照（round5 缺陷 2）：只有回收站槽写它，其余条目恒 false。
/// 它不只是"状态可读"——本类型是 record（值相等），满/空翻转时旧新实例的其余字段全部相同
/// （Icon 是同一份提取缓存的位图引用），没有这一字段两条记录就<b>相等</b>；DockSurface 的
/// Replace 快路径给容器换 DataContext 时，WPF 对等值的新值不做失效处理，图标绑定不重求值，
/// 垃圾桶图标就永远停在旧状态（真机取证：日志已翻转、屏幕上满桶不变，重启后首绘才正确）。
/// 状态进记录 → 翻转时记录必然不等 → DataContext 真的变更 → 绑定重求值 → 图标跟着翻。
/// </param>
public record DockItem(
    string Id,
    string Label,
    BitmapSource? Icon,
    DockItemKind Kind,
    string LaunchPath,
    IntPtr? WindowHandle = null,
    string? IconSource = null,
    string? Detail = null,
    bool IsRunning = false,
    bool TrashFull = false);
