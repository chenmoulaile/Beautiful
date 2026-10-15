using System.IO;
using System.Security;   // SecurityException：开始菜单根目录 ACL 受限时走这里
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Launcher;

namespace Beautiful.App.Launcher;

/// <summary>
/// 启动器索引的**数据来源**（v3 T7）：扫开始菜单的快捷方式，把每一条折算成
/// <see cref="LauncherApp"/>。Core 那侧（<see cref="LauncherIndex"/>）只做规范化/去重/打分，
/// 这里只做磁盘与 shell——两层各自可测，App 侧这一层的失败也只影响"搜得到几条"。
/// <para>
/// 【范围：开始菜单 + MSIX（v3 T7c 补上后者）】只扫 Programs 目录在这台 Win11 VM 上实测<b>漏掉了
/// 记事本/计算器/画图/Store</b>——它们已经变成 MSIX 包，开始菜单里不再有对应的 .lnk，
/// 于是"搜不到记事本"就等于启动器不可用。这部分改由 <see cref="MsixIndexSource"/> 经部署 API 枚举，
/// 并且<b>只会降级</b>（失败时返回空，桌面条目照旧）。
/// 唯一被证伪仍不能用的是 shell 命名空间取图标（<c>IShellItemImageFactory</c> 在本机任意路径都触发
/// AccessViolationException，见 IconExtractor ④ 层），那条路依旧关闭；这里用的是部署 API，不是它。
/// </para>
/// <para>
/// 【为什么解析 .lnk 的目标而不是直接启动 .lnk】brief 明确要求"在解析出的目标上
/// <c>UseShellExecute=true</c>"。解析同时带来两个副产品：① 去重能按真正的 exe 合条（同一 exe 在
/// 用户区/机器区各有一条时不会重复出现）；② 图标降级链的 ③ 层（.lnk → 目标/IconLocation）本来就是
/// 按这条约定写的，交出去的源路径与它期望的一致。解析失败时<b>回交 .lnk 本身</b>：shell 依然认它，
/// 于是"解析不了"最多是少一条去重依据，不会变成"这条搜不到也启动不了"。
/// </para>
/// </summary>
public static class LauncherIndexSource
{
    /// <summary>
    /// 索引的<b>唯一</b>数据来源入口（供 <see cref="LauncherService.PrewarmIndex"/> 在后台线程调用）：
    /// 先桌面开始菜单、后 MSIX——<b>顺序即去重优先级</b>，同一条 AppsFolder 应用两条都有时留下的是
    /// 桌面那条（它的 .lnk 可能带用户自定义图标，Core 的 <see cref="LauncherTarget"/> 认得这个身份）。
    /// MSIX 那一路失败只会少一批条目，绝不会把已经到手的桌面条目一起带走。
    /// </summary>
    public static async Task<IReadOnlyList<LauncherApp>> ScanAllAsync()
    {
        var apps = new List<LauncherApp>(ScanStartMenu());
        apps.AddRange(await MsixIndexSource.ScanAsync());
        return apps;
    }

    /// <summary>
    /// 扫描两个 Programs 根（用户区在前、机器区在后 → 同 exe 时用户的命名优先）。
    /// 目录不存在/无权限逐目录跳过，任何一条快捷方式的意外都不许让整次扫描抛出来（只少一条结果）。
    /// </summary>
    public static IReadOnlyList<LauncherApp> ScanStartMenu()
    {
        var roots = new List<string>();
        AddRoot(roots, () => Environment.GetFolderPath(Environment.SpecialFolder.Programs));
        AddRoot(roots, () => Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms));
        if (roots.Count == 0)
        {
            Logger.Warn("启动器：开始菜单目录一个都取不到，索引为空");
            return [];
        }
        return Scan(roots);
    }

    /// <summary>特殊文件夹在部分会话里会返回空串（域控策略删了已知文件夹键），这里一并挡掉。</summary>
    private static void AddRoot(List<string> roots, Func<string> resolve)
    {
        try
        {
            var path = resolve();
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) roots.Add(path);
        }
        catch (Exception ex) when (ex is ArgumentException or SecurityException or UnauthorizedAccessException)
        {
            Logger.Warn($"启动器：开始菜单根目录不可用: {ex.Message}");
        }
    }

    /// <summary>
    /// 按给定根扫描。<b>可注入根路径</b>是刻意的：单测与"只扫一个目录"的调试都能走同一条代码，
    /// 而不是把 IO 逻辑复制一份到测试里。
    /// </summary>
    public static IReadOnlyList<LauncherApp> Scan(IReadOnlyList<string> roots)
    {
        var apps = new List<LauncherApp>();
        foreach (var root in roots)
        {
            try
            {
                // AllDuplicates: 开始菜单里同名快捷方式常出现在不同子文件夹（"Microsoft Edge" 与
                // "附件\Internet Explorer"），SearchOption.AllDirectories 一次拿全，不去重交给 Core。
                foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                    TryAdd(apps, lnk);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Logger.Warn($"启动器：跳过不可读目录 {root}: {ex.Message}");
            }
        }
        Logger.Info($"启动器：开始菜单扫描完成，收录 {apps.Count} 条（根目录 {roots.Count} 个）");
        return apps;
    }

    /// <summary>
    /// 单条快捷方式 → 一条记录。<para/>
    /// 【与目标检索键的约定】Core 会把每条的启动目标折成一个可搜索键
    /// （<c>LauncherTarget.SearchKeyOf</c>：<c>charmap.exe</c> → <c>charmap</c>；
    /// <c>explorer.exe shell:AppsFolder\AUMID</c> → 包名词），"用户敲的是 exe 名而不是显示名"这条缺陷
    /// 就修在那一层。因此本层的要求是<b>交出去的目标必须就是"用户会敲的那个东西"</b>：解析失败时交回
    /// .lnk 本身，得到的键是快捷方式自己的名字（"everything"），依然可搜。
    /// </summary>
    private static void TryAdd(List<LauncherApp> apps, string lnkPath)
    {
        ShellLink.Resolved resolved;
        try
        {
            // 一次解析取全部字段：ShellLink 每次调用都要实例化一个 WScript.Shell COM 对象，
            // 分开调两次就是把开始菜单那几百条的成本翻倍。
            resolved = ShellLink.Resolve(lnkPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or InvalidOperationException)
        {
            Logger.Warn($"启动器：跳过解析失败的快捷方式 {lnkPath}: {ex.Message}");
            return;
        }

        var name = Path.GetFileNameWithoutExtension(lnkPath);
        // 解析不出目标时回交 .lnk 本身：shell 认得它，最坏是少一条去重依据，不是"这条没法启动"。
        var target = string.IsNullOrWhiteSpace(resolved.TargetPath) ? lnkPath : resolved.TargetPath;
        var arguments = resolved.Arguments;

        // 目标不存在的本地路径 = 卸载残留（开始菜单里相当常见），留着只会让用户搜到一个点了没反应的条目。
        var looksLikeLocalPath = target.Length > 2 && target[1] == ':' && !Uri.IsWellFormedUriString(target, UriKind.Absolute);
        if (looksLikeLocalPath && !File.Exists(target) && !Directory.Exists(target)) return;

        apps.Add(new LauncherApp(name, target, arguments, CustomIconOf(resolved) ?? lnkPath));
    }

    /// <summary>
    /// 快捷方式自定义图标位置（剥掉尾随 ",index"）；没有或文件不存在时返回 null，
    /// 调用方交回 <c>.lnk</c> 本身 —— 图标降级链的 ③ 层会在目标与 IconLocation 上重跑，行为不变。
    /// </summary>
    private static string? CustomIconOf(ShellLink.Resolved resolved)
    {
        var location = resolved.IconLocation;
        var comma = location.LastIndexOf(',');
        var file = comma > 0 ? location[..comma] : location;
        return file.Length > 0 && File.Exists(file) ? file : null;
    }
}
