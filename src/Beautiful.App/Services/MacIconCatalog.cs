using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Beautiful.App.Dock;
using Beautiful.Core.Dock;
using Beautiful.Core.Icons;
using Beautiful.Core.Themes;

namespace Beautiful.App.Services;

/// <summary>
/// Dock 图标优先取 macOS Tahoe 26 图标集，取不到才回退 Windows 系统图标提取。
/// <para>
/// 【为什么整张画】图标集（现 110 张）多数是 MyDockFinder 的 256×256，图形内容只占画布约
/// 84%、四周留透明边距，这是 macOS 官方图标 canvas 规范；按 ink 裁切会把它放大成"顶格贴边"
/// 的 Windows 图标比例。r4 起混入的真 macOS 26 原版是 1024×1024（同留白比例），由
/// <see cref="TryLoad"/> 在加载端统一缩到 256。所以这里不做任何裁切，直接把整张冻结位图交给
/// 模板缩放。
/// </para>
/// <para>【为什么不猜】别名表只映射确实存在 PNG 的键；查不到、资源打不开、PNG 损坏一律返回 null，
/// 由 <see cref="IconBakeContext"/> 走原来的 exe 图标提取。环境变量
/// <c>BEAUTIFUL_NO_MAC_ICONS=1</c> 可一键退回纯系统图标，便于 A/B 对照。</para>
/// </summary>
internal static class MacIconCatalog
{
    private static readonly ConcurrentDictionary<string, BitmapSource?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly bool Disabled =
        Environment.GetEnvironmentVariable("BEAUTIFUL_NO_MAC_ICONS") == "1";

    /// <summary>图标主题覆盖位图在缓存里的键前缀（$ 开头与 Tahoe 键不可能撞）。</summary>
    private const string OverrideCachePrefix = "$ovr:";

    /// <summary>
    /// 回收站槽位当前是否满（alpha.29 残项清零）：<see cref="RecycleBinMonitor"/> 轮询写、
    /// <see cref="KeysFor"/> 的废纸篓分支读。必须先改状态再替换条目实例——绑定重求值发生在
    /// Replace 事件里，顺序反了就永远换不出满桶图。默认 false（空桶 = 历史行为，
    /// 轮询首拍即校正）。图标主题覆盖不受它影响：用户显式指定的覆盖图永远优先于满/空自动切换。
    /// </summary>
    internal static bool TrashFull { get; set; }

    /// <summary>清空缓存（图标主题覆盖变更时由 IconThemeService 调用；烘焙缓存由 IconBakeContext.ClearCache 同链清）。</summary>
    public static void ClearCache() => Cache.Clear();

    /// <summary>按条目的图标来源解析候选键，返回第一张真实存在的 macOS 图标；没有则 null。</summary>
    public static BitmapSource? TryGet(DockItem item)
    {
        if (Disabled) return null;
        // 图标主题覆盖层（图标主题工作包）：用户按应用键指定的图集键 / 本地图片优先于内置映射表。
        // 候选键与 SourceFor 同一优先级（访达/启动台只认 IconSource），规则在 Core IconThemeKey.CandidatesFor。
        if (TryOverride(IconThemeKey.CandidatesFor(item.Kind, item.LaunchPath, item.IconSource)) is { } overridden)
            return overridden;
        // 用户裁定：项目 DockBuiltinIcons 那两枚（笑脸 / 火箭）是手绘仿制品，不是真 macOS 图标，
        // 一律不用。访达与启动台同样走图标集，且用的是从真实 macOS 26 系统导出的
        // Finder.png / Apps.png（见 Assets/TahoeIcons/finder.png 与 pad.png）。
        // 废纸篓的满/空随条目快照走（item.TrashFull，round5 缺陷 2）：键链不读全局静态，
        // 就没有"先改静态再换实例"的顺序耦合——绑定无论何时重求值都拿到与条目一致的那张图。
        return FirstFor(SourceFor(item), item.TrashFull);
    }

    /// <summary>
    /// 面板侧入口（macOS 化 r4）：给一条启动路径 / exe 路径，返回图标集里第一张真实存在的
    /// macOS 图标；没有则 null，调用方一字不改地走原来的提取链。
    /// <para>
    /// 启动台 / 聚焦启动器（<c>LauncherService</c>）、切换器（<c>SwitcherService</c>）、控制中心
    /// 混音器（<c>ControlCenterPanel</c>）、Dock 右键菜单（<c>DockContextMenu</c>）共用此口，
    /// 键解析（<see cref="MacIconNames"/> 别名表 + <see cref="MacDisplayName"/> 显示名表）与缓存
    /// 都和 <see cref="TryGet"/> 同一套——同一个应用在任何面板都是同一张脸。
    /// </para>
    /// <para>
    /// <c>shell:</c> / <c>builtin:</c> 源与空值直接不命中：它们没有可信的应用身份，
    /// 硬猜会复现「把不相干条目统统画成访达」的老毛病。线程安全：启动台图标泵在后台线程逐个调用，
    /// 缓存是 ConcurrentDictionary，位图 OnLoad + Freeze 后跨线程可用（与 IconExtractor 的
    /// 后台提取同一模式）。
    /// </para>
    /// </summary>
    public static BitmapSource? TryGetFor(string? pathOrName)
    {
        if (Disabled || string.IsNullOrWhiteSpace(pathOrName)) return null;
        var text = pathOrName.Trim();
        if (text.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        // 覆盖层与本表同键语义：启动台/切换器等面板同样先吃图标主题覆盖（与 TryGet 同一张脸）。
        if (TryOverride(IconThemeKey.CandidatesFor(DockItemKind.Pinned, text, null)) is { } overridden)
            return overridden;
        return FirstFor(text, TrashFull);   // 面板路径没有条目快照：跟随全局满/空（废纸篓分支才会读它）
    }

    /// <summary>候选键逐个试读（带缓存）；这是 <see cref="TryGet"/> 与 <see cref="TryGetFor"/> 的公共尾巴。
    /// <paramref name="trashFull"/> 只影响废纸篓键链（其余来源忽略）。</summary>
    private static BitmapSource? FirstFor(string? source, bool trashFull)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        foreach (var key in KeysFor(source, trashFull))
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                if (cached is not null) return cached;
                continue;
            }

            var bitmap = TryLoad(key);
            Cache[key] = bitmap;
            if (bitmap is not null)
            {
                Logger.Info($"macOS 图标集: 键={key} 尺寸={bitmap.PixelWidth}×{bitmap.PixelHeight}");
                return bitmap;
            }
        }
        return null;
    }

    /// <summary>
    /// 取键的源：固定项优先用 <c>dock.json</c> 里的原始写法（LaunchPath），而不是解析后的
    /// IconSource。本机大量 .lnk 的 IconLocation 都指向 <c>explorer.exe</c>（Windows 默认图标），
    /// 按 IconSource 取图会把一堆不相干的快捷方式统统画成访达 —— 那是错画，比不换图标更糟。
    /// 运行窗口没有 LaunchPath，才用进程 exe 路径；回收站的 LaunchPath 是 shell: 形式，改用图标源。
    /// </summary>
    private static string? SourceFor(DockItem item)
    {
        // 内置功能格（访达 / 启动台）没有可执行文件，LaunchPath 对访达还被设成了用户目录；
        // 它们必须按 IconSource 的 builtin: 身份取真实 macOS 图标，不能拿路径去猜应用名。
        if (item.Kind is DockItemKind.Finder or DockItemKind.Launchpad)
        {
            return item.IconSource;
        }
        if (!string.IsNullOrWhiteSpace(item.LaunchPath)
            && !item.LaunchPath.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
        {
            return item.LaunchPath;
        }
        return item.IconSource ?? item.LaunchPath;
    }

    private static IEnumerable<string> KeysFor(string? source, bool trashFull)
    {
        if (string.IsNullOrWhiteSpace(source)) return Array.Empty<string>();
        var text = source.Trim();

        // 内置功能格同样用真实 macOS 26 图标（网站下载后直接替换进 Assets/TahoeIcons）。
        // finder.png = 官方 Finder.app 图标，pad.png = 官方 Apps/Launchpad 图标（用户认可的彩色网格）。
        if (text.Equals("builtin:finder", StringComparison.OrdinalIgnoreCase))
            return new[] { "finder", "explorer" };
        if (text.Equals("builtin:launchpad", StringComparison.OrdinalIgnoreCase))
            return new[] { "pad", "app_store" };
        if (text.StartsWith("::{645FF040", StringComparison.OrdinalIgnoreCase))
            return RecycleBinSlot.IconKeyCandidates(trashFull);   // 废纸篓：按满/空给键（Core 候选链；状态随条目快照，round5 缺陷 2）

        var keys = new List<string>(MacIconNames.CandidateKeys(text));
        // 开始菜单导入的 .lnk 是显示名（"Visual Studio Code.lnk"）而不是 exe 基名，
        // 走 Core 纯函数补一张「显示名 → 确实存在的 Tahoe PNG」映射；没有对应图就返回 null，
        // 交回系统图标（绝不把 Chrome 画成 Safari）。
        var displayKey = MacDisplayName.KeyFor(text);
        if (displayKey is not null) keys.Add(displayKey);
        var lower = text.ToLowerInvariant();
        if (lower.Contains("file explorer") || lower.Contains("资源管理器")) keys.Add("explorer");
        if (lower.Contains("recycle") || lower.Contains("回收站"))
        {
            // 显示名含"回收站/recycle"的条目：同样跟随满/空（满态 full 缺图时由候选链后续键兜底）。
            if (trashFull) keys.Add(RecycleBinSlot.IconKeyFull);
            keys.Add(RecycleBinSlot.IconKeyEmpty);
        }
        if (lower.Contains("启动台") || lower.Contains("launchpad")) keys.Add("pad");
        return keys;
    }

    /// <summary>
    /// 图标主题覆盖层：候选覆盖键逐个查 <see cref="IconThemeService"/> 的覆盖表，命中即加载该图
    /// （缓存键带前缀，与 Tahoe 键共用一个字典）。覆盖加载失败返回 null → 调用方继续走默认链，
    /// **绝不空白上屏**（坏文件 / 被删文件同此路径）。
    /// </summary>
    private static BitmapSource? TryOverride(IReadOnlyList<string> candidateKeys)
    {
        if (candidateKeys.Count == 0) return null;
        if (IconThemeService.FindRef(candidateKeys) is not { } hit) return null;

        var cacheKey = OverrideCachePrefix + hit.AppKey;
        if (Cache.TryGetValue(cacheKey, out var cached)) return cached;

        var bitmap = hit.Kind == IconThemeRefKind.Asset ? TryLoad(hit.Value) : TryLoadFile(hit.Value);
        Cache[cacheKey] = bitmap;   // null 也进缓存：坏文件不反复重读（与 FirstFor 同一策略）
        if (bitmap is not null)
        {
            Logger.Info($"图标主题: 键={hit.AppKey} 命中覆盖 " +
                        $"来源={(hit.Kind == IconThemeRefKind.Asset ? "图集" : "文件")}={hit.Value} " +
                        $"尺寸={bitmap.PixelWidth}×{bitmap.PixelHeight}");
        }
        return bitmap;
    }

    /// <summary>
    /// 本地图片文件 → 位图（覆盖层专用）：任何 WIC 可解码格式（png/ico/jpg/bmp）都收，
    /// 与 <see cref="TryLoad"/> 同一套后处理（Pbgra32 + ≤256 缩放 + Freeze，跨线程可用）。
    /// </summary>
    private static BitmapSource? TryLoadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;
            BitmapSource frame = decoder.Frames[0];
            if (frame.Format != PixelFormats.Pbgra32)
                frame = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
            if (frame.PixelWidth > 256 || frame.PixelHeight > 256)
            {
                var scale = 256.0 / Math.Max(frame.PixelWidth, frame.PixelHeight);
                frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
            }
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException
                                       or FileFormatException or InvalidOperationException
                                       or ArgumentException)
        {
            return null;   // 读不到或解不出 → 覆盖失效，回默认链
        }
    }

    private static BitmapSource? TryLoad(string key)
    {
        try
        {
            var uri = new Uri($"pack://application:,,,/Assets/TahoeIcons/{key}.png", UriKind.Absolute);
            var info = System.Windows.Application.GetResourceStream(uri);
            if (info is null) return null;
            using var stream = info.Stream;
            var decoder = new PngBitmapDecoder(stream,
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;
            BitmapSource frame = decoder.Frames[0];
            if (frame.Format != PixelFormats.Pbgra32)
                frame = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
            // 网站下载的是 macOS 官方 1024×1024 原图；Dock 在 200% DPI 下最大只画到约 115px，
            // 全尺寸解码 40 多张会白占上百 MB。这里只做显示尺寸适配（图案像素完全一致），
            // 放大场开到 1.2× 也仍有 2 倍余量。
            if (frame.PixelWidth > 256 || frame.PixelHeight > 256)
            {
                var scale = 256.0 / Math.Max(frame.PixelWidth, frame.PixelHeight);
                frame = new TransformedBitmap(frame,
                    new ScaleTransform(scale, scale));
            }
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException
                                       or FileFormatException or InvalidOperationException
                                       or ArgumentException or UriFormatException)
        {
            return null;   // 资源不存在或不是合法 PNG → 回退系统图标，不猜
        }
    }
}
