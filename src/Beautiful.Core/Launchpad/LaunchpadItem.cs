namespace Beautiful.Core.Launchpad;

/// <summary>
/// Launchpad 中的一个条目：要么是可启动的应用图标，要么是包含多应用的文件夹。
/// <para>
/// 【为什么 Core 只认 ID】图标位图、名称、快捷方式解析都是 App 层的活
/// —— Core 只推导"这个位置上是什么东西、应该怎么排"。夹上图标就是 UI 胶水事，
/// 决不能混进纯逻辑，否则单测就要构造位图/磁盘了。
/// </para>
/// <para>
/// 【为什么文件夹不能再套文件夹】macOS 启动台不允许嵌套文件夹，
/// 所以 <see cref="Folder"/> 的成员始终是扁平的应用 ID 列表 —— 删减/合并都简单到"数组增删"。
/// </para>
/// </summary>
public abstract record LaunchpadItem
{
    /// <summary>
    /// 应用图标：一个可启动的应用。
    /// <param name="Id">身份键：应用在 <see cref="Core.Launcher.LauncherApp"/> 层的去重键
    /// （目标路径+参数），用于跨文件夹去重。</param>
    /// </summary>
    public sealed record App(string Id) : LaunchpadItem;

    /// <summary>
    /// 文件夹：包含一组成员应用。成员用 ID 列表（有序）表示，顺序即拖入顺序 ——
    /// 第一个成员同时作为文件夹命名依据（macOS 行为）。
    /// </summary>
    public sealed record Folder(string Name, IReadOnlyList<string> MemberIds) : LaunchpadItem;
}
