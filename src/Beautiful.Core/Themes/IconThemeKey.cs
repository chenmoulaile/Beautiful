using Beautiful.Core.Dock;

namespace Beautiful.Core.Themes;

/// <summary>
/// 图标主题覆盖键（纯函数，可单测）：把 Dock / 启动台的一个条目源折成「应用键」。
/// 覆盖表（<c>config/icon-theme.json</c>）与主题包 manifest 的 <c>iconOverrides</c> 都按这个键命名。
/// <para>
/// 【口径对齐】普通应用复用 <see cref="DockAppKey"/>（Dock「一格 = 一个应用」分组的同一把尺子：
/// 小写、去扩展名、别名归一）——Dock 合并这一格用的键是什么，覆盖键就是什么，不会出现
/// "Dock 把两个条目并成了一格，主题却要分别覆盖两次"的错位。内置功能格用 MacIconCatalog 的
/// 内置身份折成固定键：访达 <c>finder</c>、启动台 <c>launchpad</c>、废纸篓 <c>trash</c>。
/// </para>
/// <para>
/// 【拿不准就空键】<c>shell:</c> 命名空间、其余 <c>builtin:</c>、<c>::{</c>、<c>ms-</c> 与解析不出
/// 应用名的伪路径一律返回空串；空键不进覆盖表（App 层丢弃），宁可让这一格保持原样，
/// 也不能把不相干条目画成同一张脸——与 <see cref="DockAppKey"/> 的「宁可不合并」同一纪律。
/// </para>
/// </summary>
public static class IconThemeKey
{
    /// <summary>访达常驻格的覆盖键（对应图集 finder.png）。</summary>
    public const string Finder = "finder";

    /// <summary>启动台常驻格的覆盖键（对应图集 pad.png）。</summary>
    public const string Launchpad = "launchpad";

    /// <summary>废纸篓格的覆盖键（对应图集 trash_empty.png / trash_full.png）。</summary>
    public const string Trash = "trash";

    /// <summary>回收站的 shell 命名空间 id 前缀（与 MacIconCatalog.KeysFor 的废纸篓分支同一常量口径）。</summary>
    private const string RecycleBinClsidPrefix = "::{645FF040";

    /// <summary>把一个图标源（exe 路径 / builtin: / ::{clsid}）折成覆盖键；不可覆盖返回空串。</summary>
    public static string Of(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;
        var text = source.Trim();

        // 内置功能格：固定键（编辑器里显示为「访达 / 启动台 / 回收站」这三格的键）。
        if (text.Equals("builtin:finder", StringComparison.OrdinalIgnoreCase)) return Finder;
        if (text.Equals("builtin:launchpad", StringComparison.OrdinalIgnoreCase)) return Launchpad;
        if (text.StartsWith(RecycleBinClsidPrefix, StringComparison.OrdinalIgnoreCase)) return Trash;

        // 其余非应用身份（其它 builtin / shell 命名空间 / 协议路径）没有可信的应用身份，不参与覆盖。
        if (text.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("::{", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("ms-", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return DockAppKey.Of(text);
    }

    /// <summary>
    /// 一个 Dock 条目的**候选覆盖键序列**（按命中优先级，已去重去空）。
    /// 与 MacIconCatalog 的 <c>SourceFor</c> 同一优先级：访达/启动台只认 IconSource（它们的
    /// LaunchPath 分别被设成了用户目录和空串，拿去折键会折出「以用户名命名的应用」这种假键）；
    /// 其余条目 LaunchPath 优先、IconSource 兜底——.lnk 显示名条目的 IconSource 已被 Dock 解析成
    /// 目标 exe，正是它给出稳定的键。
    /// </summary>
    public static IReadOnlyList<string> CandidatesFor(DockItemKind kind, string? launchPath, string? iconSource)
    {
        if (kind is DockItemKind.Finder or DockItemKind.Launchpad)
        {
            var only = Of(iconSource);
            return only.Length == 0 ? Array.Empty<string>() : new[] { only };
        }

        var keys = new List<string>(2);
        AddIfNew(keys, Of(launchPath));
        AddIfNew(keys, Of(iconSource));
        return keys;
    }

    private static void AddIfNew(List<string> keys, string key)
    {
        if (key.Length > 0 && !keys.Contains(key)) keys.Add(key);
    }
}
