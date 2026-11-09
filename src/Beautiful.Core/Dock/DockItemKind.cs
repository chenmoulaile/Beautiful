namespace Beautiful.Core.Dock;

/// <summary>
/// Dock 条目种类。放在 Core（而非 App 的 Dock 命名空间）是为了让"分隔符插在哪、谁参与放大场"
/// 这类**行结构规则**成为可单测的纯数据——分隔符是 macOS 原生 Dock 的结构语义（固定区 | 运行区 | 废纸篓），
/// 不该由 App 侧散落的 <c>if (theme == mac)</c> 分支决定。
/// </summary>
public enum DockItemKind
{
    /// <summary>固定应用（dock.json）。</summary>
    Pinned,

    /// <summary>正在运行且未固定的窗口。</summary>
    Window,

    /// <summary>
    /// 已最小化的窗口（alpha.24）：驻留在**分隔符右侧**的最小化区（macOS 语义——最小化窗口不是
    /// 消失了，而是缩成图标停在分隔符右边、废纸篓左边）。渲染上与 Window 同模板，但：
    /// 运行指示点不亮（macOS 最小化缩略图没有圆点）、不参与拖拽排序、悬停无实时预览
    /// （iconic 窗口没有可抓的画面）。点击 = 还原。
    /// </summary>
    MinimizedWindow,

    /// <summary>回收站 / macOS 废纸篓。</summary>
    RecycleBin,

    /// <summary>媒体胶囊（后缀槽，无会话时宽度归零）。</summary>
    Media,

    /// <summary>
    /// 分隔符：一条竖细线。**不放大**（固定宽、缩放恒 1）、**不悬停**、**不可点击**，
    /// 只向行贡献宽度；中心不进放大场，于是邻居图标长大时它不会被当成可拉伸图标推挤（macOS 原生行为）。
    /// </summary>
    Separator,

    /// <summary>
    /// Dock 小组件（Phase 7-T3）：时钟 + 天气。它不是应用、不参与固定/拖拽/窗口跟踪，
    /// 只作为行尾的一个信息块存在；点击打开日历浮层。
    /// </summary>
    Widget,

    /// <summary>访达常驻格（macOS Dock 首格）：点击打开自家访达窗口；不参与固定/拖拽/窗口跟踪。</summary>
    Finder,

    /// <summary>启动台常驻格（macOS Dock 第二格）：点击打开 Launchpad；同上不参与固定/拖拽。</summary>
    Launchpad,

    /// <summary>
    /// 托盘收纳列 applet（5.0.0 未闭合项④）：行尾的一个功能格，把 Windows 通知区域（系统托盘）的
    /// 第三方图标"收进"Dock——格子上只有一个自绘的收纳箭头（<b>不显示</b>托盘位图： Dock 常驻表面
    /// 若为它读托盘就要与顶栏常驻条叠加扫描/抓屏，违背"不重复扫描"的裁定），点击展开弹层才读
    /// （TrayPanel.xaml.cs 的"展开才读"模式）并转发真实托盘图标的点击。默认关（设置项
    /// <c>DockTrayEnabled</c>），挂点由 <see cref="DockTrayPlacement"/> 裁定。
    /// 不参与固定/拖拽/窗口跟踪，右键无菜单（<see cref="DockContextMenuPlan"/> 的 default 空表）。
    /// </summary>
    Tray
}
