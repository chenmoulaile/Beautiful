namespace Beautiful.Core.Icons;

/// <summary>
/// macOS 图标集（Tahoe 26）的**文件名解析规则**（v4.0-⑧）。
/// <para>
/// 【为什么需要这一层】图标集来自 MyDockFinder 的 <c>themes/Tahoe 26 Light/icons</c>（约 95 张 256×256 PNG，
/// 内容占画布约 84%，四周留透明边距——这是 macOS 官方图标 canvas 规范，不能按 ink 裁切）；
/// macOS 化 r4 起混入 macosicons 下载的真 macOS 26 原版（1024×1024，加载端缩到 256，见
/// Assets/TahoeIcons 现共 110 张），键名口径不变。
/// 这些文件的命名是**人工起的**：<c>qq.png</c> / <c>wps.png</c> / <c>cloudmusic.png</c> 这类能直接对上
/// 可执行文件名，但 <c>weixin.exe</c> 与 <c>wechat.png</c>、<c>obs64.exe</c> 与 <c>obs.png</c> 就对不上。
/// 本机注册表实测：61 个已安装程序的 exe 名里只有 6 个能直接同名命中 —— 所以必须有一张别名表。
/// </para>
/// <para>
/// 【规则顺序】先查别名表（同一个应用的不同发行名/主程序名），再退回 exe 原名本身。
/// 返回的是**候选键序列**，而不是"答案"：图标集里到底有没有这张图由 App 层试读决定
/// （Core 不碰 WPF 资源，也就没法知道 pack URI 能不能打开）。
/// </para>
/// </summary>
public static class MacIconNames
{
    /// <summary>
    /// 可执行文件名（小写、去扩展名）→ 图标集键。只收**确实存在对应 PNG** 的映射：
    /// 编错一个键只是多一次失败的资源查找（不崩、不挡回退），但会让"为什么这个应用没换图标"难查，
    /// 所以这张表按图标集实际内容逐条核对过。
    /// </summary>
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // ---------- 通讯 ----------
        ["wechat"] = "wechat",
        ["weixin"] = "wechat",
        ["wechatappex"] = "wechat",
        ["wecom"] = "wechat",
        ["wxwork"] = "wechat",
        ["qq"] = "qq",
        ["qqnt"] = "qq",
        ["tim"] = "qq",
        ["telegram"] = "telegram",
        ["telegramdesktop"] = "telegram",
        ["whatsapp"] = "whatsapp",
        ["whatsappdesktop"] = "whatsapp",
        ["ms-teams"] = "ms-teams",
        ["teams"] = "ms-teams",
        ["ms-teamsupdate"] = "ms-teams",

        // ---------- 浏览器 ----------
        ["msedge"] = "msedge",
        ["firefox"] = "firefox",
        ["safari"] = "safari",

        // ---------- 媒体 ----------
        ["cloudmusic"] = "cloudmusic",
        ["neteasecloudmusic"] = "cloudmusic",
        ["qqmusic"] = "applemusic",
        ["spotify"] = "spotify",
        ["applemusic"] = "applemusic",
        ["itunes"] = "itunes",
        ["microsoft.media.player"] = "microsoft.media.player",
        ["wmplayer"] = "microsoft.media.player",
        ["obs"] = "obs",
        ["obs64"] = "obs",
        ["obs32"] = "obs",
        ["potplayermini64"] = "quicktime_player",
        ["vlc"] = "quicktime_player",
        ["bilibili"] = "bilibili",
        ["哔哩哔哩"] = "bilibili",

        // ---------- 生产力 ----------
        ["wps"] = "wps",
        ["et"] = "wps",
        ["wpp"] = "wps",
        ["winword"] = "pages",
        ["excel"] = "numbers",
        ["powerpnt"] = "keynote",
        ["olk"] = "olk",
        ["outlook"] = "olk",
        ["notepad"] = "notepad",
        ["notepad++"] = "textedit",
        ["textedit"] = "textedit",
        ["code"] = "code",
        ["code - insiders"] = "code",
        ["cursor"] = "code",
        ["windowsterminal"] = "windowsterminal",
        ["wt"] = "windowsterminal",
        ["openconsole"] = "terminal",
        ["cmd"] = "terminal",
        ["powershell"] = "terminal",
        ["pwsh"] = "terminal",
        ["windowspowershell"] = "terminal",
        ["conhost"] = "terminal",
        ["explorer"] = "explorer",
        ["files"] = "files",
        ["todo"] = "todo",
        ["notes"] = "notes",
        ["onenote"] = "notes",
        ["jishisheji"] = "jishisheji",
        ["即时设计"] = "jishisheji",
        ["lyricify"] = "lyricify_for_spotify",
        ["lyricify_for_spotify"] = "lyricify_for_spotify",

        // ---------- 系统 / 工具 ----------
        ["systemsettings"] = "systemsettings",
        ["system_settings"] = "systemsettings",
        ["control"] = "system_settings",
        ["calculatorapp"] = "calculator",
        ["calculator"] = "calculator",
        ["calc"] = "calculator",
        ["calendar"] = "calendar",
        ["camera"] = "camera",
        ["clock"] = "clock",
        ["weather"] = "weather",
        ["maps"] = "maps",
        ["mail"] = "mail",
        ["photos"] = "photos",
        ["mspaint"] = "photo_booth",
        ["taskmgr"] = "activity_monitor",          // macOS 化 r4：图标集补齐真实 Activity Monitor（下载自 macOS-26-Icons/Utilities）
        ["trash_empty"] = "trash_empty",
        ["trash_full"] = "trash_full",

        // ---------- 云 / 网盘 / 游戏 ----------
        ["baidunetdisk"] = "baidunetdisk",
        ["baidunetdiskunite"] = "baidunetdiskunite",
        ["onedrive"] = "airdrop",                  // 云同步语义最接近的现成图形
        ["steam"] = "steam",
        ["steam++"] = "steamplus",
        ["steamplus"] = "steamplus",
        ["clash-verge"] = "clash-verge",
        ["clash"] = "clash-verge",
        ["xboxpcapp"] = "xboxpcapp",
        ["githubdesktop"] = "githubdesktop",
        ["xcode"] = "xcode",
        ["imovie"] = "imovie",
        ["finalcutpro"] = "final_cut_pro",
        ["final cut pro"] = "final_cut_pro",
    };

    /// <summary>
    /// 图标集里**确实不存在**的键（占位用）：映射到这些值等于"明确不命中"，让别名表能表达
    /// "这个应用我知道是谁，但图标集没它的图"，而不是被迫留空看不出意图。
    /// 目前为空（r4 补齐 Activity Monitor 后最后一条已转正）；再遇到图标集没有的应用时在此登记。
    /// </summary>
    private static readonly HashSet<string> Absent = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 给定可执行文件路径（或裸文件名）→ 候选图标键（按优先级）。空/无效输入返回空数组。
    /// 返回的第一项是别名表结果，第二项是 exe 原名（人工命名的图标集里两种都可能命中）。
    /// </summary>
    public static IReadOnlyList<string> CandidateKeys(string? pathOrName)
    {
        if (string.IsNullOrWhiteSpace(pathOrName)) return Array.Empty<string>();
        var name = BaseName(pathOrName!);
        if (name.Length == 0) return Array.Empty<string>();

        var keys = new List<string>(2);
        if (Map.TryGetValue(name, out var mapped) && !Absent.Contains(mapped)) keys.Add(mapped);
        if (!string.Equals(mapped, name, StringComparison.OrdinalIgnoreCase) && !Absent.Contains(name))
            keys.Add(name);
        return keys;
    }

    /// <summary>路径或裸名 → 小写、去扩展名的基名（"C:\x\Code.exe" → "code"）。</summary>
    private static string BaseName(string pathOrName)
    {
        var text = pathOrName.Trim();
        var cut = Math.Max(text.LastIndexOf('\\'), text.LastIndexOf('/'));
        if (cut >= 0) text = text[(cut + 1)..];
        var dot = text.LastIndexOf('.');
        if (dot > 0) text = text[..dot];
        return text.Trim().ToLowerInvariant();
    }
}
