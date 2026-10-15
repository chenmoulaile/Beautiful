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

    /// <summary>回收站 / macOS 废纸篓。</summary>
    RecycleBin,

    /// <summary>媒体胶囊（后缀槽，无会话时宽度归零）。</summary>
    Media,

    /// <summary>
    /// 分隔符：一条竖细线。**不放大**（固定宽、缩放恒 1）、**不悬停**、**不可点击**，
    /// 只向行贡献宽度；中心不进放大场，于是邻居图标长大时它不会被当成可拉伸图标推挤（macOS 原生行为）。
    /// </summary>
    Separator
}
