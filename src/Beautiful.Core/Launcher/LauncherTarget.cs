using System.IO;

namespace Beautiful.Core.Launcher;

/// <summary>
/// 启动目标的"第二可搜索字段"推导（v3 T7c）。
/// <para>
/// 【为什么需要它】用户敲的是 <b>exe 名</b>，不是显示名：开始菜单里躺着的是
/// "Character Map → charmap.exe"、"Command Prompt → cmd.exe"、"记事本 → WindowsNotepad"，
/// 而 "charmap"/"cmd"/"notepad" 这些串一个都不出现在显示名里。只按显示名打分就等于
/// "列表里明明有，搜索说没有"。本类把这些目标折算成一个稳定的小写检索键，
/// <see cref="LauncherIndex"/> 拿它当第一等字段打分。
/// </para>
/// <para>
/// 【为什么放 Core】键的推导规则（剥扩展名 / 剥 .lnk / AppsFolder 的 AUMID 取词）直接决定
/// "搜得到什么、谁排前面"，是启动器可测契约的一部分；放在 App 侧就只剩手工验收能发现了。
/// 这里全是纯字符串函数，不碰磁盘也不碰 shell。
/// </para>
/// </summary>
public static class LauncherTarget
{
    /// <summary>AppsFolder 动词前缀（<c>explorer.exe shell:AppsFolder\&lt;AUMID&gt;</c> 是启动 MSIX 应用的唯一正道）。</summary>
    private const string AppsFolderPrefix = "shell:appsfolder\\";

    /// <summary>
    /// 目标检索键：文件名去掉扩展名（<c>charmap.exe</c> → <c>charmap</c>，<c>Everything.lnk</c> → <c>everything</c>），
    /// 小写不变（Invariant：与区域设置无关，跨机器同键）。三条特殊形：
    /// <list type="bullet">
    /// <item><description>AppsFolder 启动（explorer.exe + <c>shell:AppsFolder\AUMID</c>，或直接以该动词为路径）
    /// → 用 <see cref="NameOfAppId"/> 取出的包名词，<b>绝不是 "explorer"</b>——否则每个 MSIX 应用都会被
    /// "explorer" 这个词命中，而用户敲 "notepad" 依然一无所获。</description></item>
    /// <item><description>URI 动词目标（<c>ms-settings:</c>）→ 冒号前的动词名，扩展名规则对它无意义。</description></item>
    /// <item><description>.lnk 解析失败回交 .lnk 本身 → 剥掉 .lnk 后得到快捷方式自己的名字（"everything"），
    /// 仍然可搜。</description></item>
    /// </list>
    /// </summary>
    public static string SearchKeyOf(string? launchPath, string? arguments = null)
    {
        var appsFolder = AppsFolderAumidOf(launchPath, arguments);
        if (appsFolder is not null) return NameOfAppId(appsFolder);

        var path = Unquote(launchPath);
        if (path.Length == 0) return string.Empty;

        var colon = path.IndexOf(':');
        if (colon > 0 && !HasDirectorySeparator(path))
        {
            // "ms-settings:" / "ms-quick-assist:" 这类协议目标：GetFileName 会把整串当成文件名，
            // 于是检索键里带个冒号，用户永远敲不中。取冒号前的动词名即可。
            var verb = path[..colon];
            return verb.Length == 0 ? string.Empty : verb.ToLowerInvariant();
        }

        var name = Path.GetFileName(path.TrimEnd('/', '\\'));
        // 大小写不敏感地剥扩展名：扫描器只在"名称"上剥过 .lnk，这里的目标可能是 .lnk/.exe/.msi 任意一种。
        var dot = name.LastIndexOf('.');
        if (dot > 0) name = name[..dot];
        return name.ToLowerInvariant();
    }

    /// <summary>
    /// 这条启动命令是不是"经 explorer.exe 打开 AppsFolder 里的某个 AUMID"；是则返回 AUMID，否则 null。
    /// 引号与多余空格都先剥掉——同一个应用，.lnk 解析出来的参数与 MSIX 枚举拼出来的参数只差在引号上，
    /// 不能让这点差别变成两条索引记录。
    /// </summary>
    public static string? AppsFolderAumidOf(string? launchPath, string? arguments)
    {
        // 两种写法都要认：直接拿动词当路径（Process.Start("shell:AppsFolder\\AUMID")）与
        // 经 explorer 中转（args 位是动词）。这个前缀本身足够独特，不必再核对可执行文件名。
        var fromPath = TakeAfter(AppsFolderPrefix, launchPath);
        return fromPath ?? TakeAfter(AppsFolderPrefix, arguments);
    }

    /// <summary>
    /// 键的规范化：先剥首尾空白，再剥包裹的引号，再剥一次空白——shell 与 .lnk 两边给的东西只差在
    /// <c>"--app=x"</c> 与 <c>--app=x</c> 这种写法上，不能让写法把同一条目拆成两条。
    /// </summary>
    private static string Unquote(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length > 1 && value[0] == '"' && value[^1] == '"') value = value[1..^1].Trim();
        return value;
    }

    /// <summary>文本以该前缀开头时返回前缀之后的部分（剥引号/空白），否则 null。</summary>
    private static string? TakeAfter(string prefix, string? text)
    {
        var value = Unquote(text);
        return value.Length > prefix.Length && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? Unquote(value[prefix.Length..])
            : null;
    }

    /// <summary>
    /// AUMID → 用户会敲的那个词：<c>Microsoft.WindowsNotepad_8wekyb3d8bbwe!App</c> → <c>notepad</c>。
    /// <para>
    /// 【规则】取 <c>!</c> 之前、<c>_</c>（版本-发布者后缀分隔符）之前的包名，按 <c>.</c> 切段，
    /// 拿最后一段，再剥掉开头的 <c>Windows</c> 产品前缀（仅当剥完还剩 ≥3 个字母）。
    /// </para>
    /// <para>
    /// 【为什么敢剥】留下的词永远是包名的<b>子串</b>，所以最坏情况是"敲被剥掉的前缀词时档次变低"，
    /// 不会出现"条目消失"；而不剥的话 <c>notepad</c>/<c>calc</c>/<c>paint</c> 这些真查询只能落到
    /// 子串档甚至匹配不上（<c>calc</c> 是 <c>windowscalculator</c> 的前缀吗？不是——它是中间子串）。
    /// </para>
    /// </summary>
    public static string NameOfAppId(string? aumid)
    {
        var name = (aumid ?? string.Empty).Trim();
        if (name.Length == 0) return string.Empty;
        var bang = name.IndexOf('!');
        if (bang > 0) name = name[..bang];
        var underscore = name.IndexOf('_');
        if (underscore > 0) name = name[..underscore];

        var segments = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var tail = segments.Length == 0 ? name : segments[^1];
        if (tail.Length > 10 && tail.StartsWith("Windows", StringComparison.OrdinalIgnoreCase))
            tail = tail[7..];
        return tail.ToLowerInvariant();
    }

    /// <summary>
    /// 去重身份键：<b>同一个 AppsFolder AUMID 只该有一条</b>——UWP/MSIX 应用在开始菜单里往往另有一条
    /// 指向 <c>explorer.exe shell:AppsFolder\AUMID</c> 的 .lnk，两条都留着就是"搜记事本出来两个记事本"。
    /// 其余条目沿用"路径 + 参数"键（同 exe 不同参数不算重复，见 <see cref="LauncherIndex.Build"/>）。
    /// </summary>
    public static string IdentityOf(string launchPath, string? arguments)
    {
        var appsFolder = AppsFolderAumidOf(launchPath, arguments);
        if (appsFolder is not null) return "appsfolder|" + appsFolder;
        return Unquote(launchPath) + "|" + Unquote(arguments);
    }

    private static bool HasDirectorySeparator(string path) => path.Contains('\\') || path.Contains('/');
}
