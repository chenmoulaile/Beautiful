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

namespace Beautiful.App.Services;

/// <summary>
/// Dock 图标优先取 macOS Tahoe 26 图标集，取不到才回退 Windows 系统图标提取。
/// <para>
/// 【为什么整张画】这 95 张 PNG 都是 256×256，图形内容只占画布约 84%、四周留透明边距，
/// 这是 macOS 官方图标 canvas 规范；按 ink 裁切会把它放大成"顶格贴边"的 Windows 图标比例。
/// 所以这里不做任何裁切，直接把整张冻结位图交给模板缩放。
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

    /// <summary>按条目的图标来源解析候选键，返回第一张真实存在的 macOS 图标；没有则 null。</summary>
    public static BitmapSource? TryGet(DockItem item)
    {
        if (Disabled) return null;
        // 用户裁定：项目 DockBuiltinIcons 那两枚（笑脸 / 火箭）是手绘仿制品，不是真 macOS 图标，
        // 一律不用。访达与启动台同样走图标集，且用的是从真实 macOS 26 系统导出的
        // Finder.png / Apps.png（见 Assets/TahoeIcons/finder.png 与 pad.png）。
        foreach (var key in KeysFor(SourceFor(item)))
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

    private static IEnumerable<string> KeysFor(string? source)
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
            return new[] { "trash_empty" };                   // 废纸篓

        var keys = new List<string>(MacIconNames.CandidateKeys(text));
        // 开始菜单导入的 .lnk 是显示名（"Visual Studio Code.lnk"）而不是 exe 基名，
        // 走 Core 纯函数补一张「显示名 → 确实存在的 Tahoe PNG」映射；没有对应图就返回 null，
        // 交回系统图标（绝不把 Chrome 画成 Safari）。
        var displayKey = MacDisplayName.KeyFor(text);
        if (displayKey is not null) keys.Add(displayKey);
        var lower = text.ToLowerInvariant();
        if (lower.Contains("file explorer") || lower.Contains("资源管理器")) keys.Add("explorer");
        if (lower.Contains("recycle") || lower.Contains("回收站")) keys.Add("trash_empty");
        if (lower.Contains("启动台") || lower.Contains("launchpad")) keys.Add("pad");
        return keys;
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
