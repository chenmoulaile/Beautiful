using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Beautiful.Core.Tray;
using Microsoft.Win32;

namespace Beautiful.App.Services;

/// <summary>
/// 顶栏托盘条的第三条取图来源：Windows 自己存下来的图标快照 PNG。
/// <para>
/// 【为什么必须补这一条】Win11 24H2 把通知区域换成 XAML 岛，经典 <c>ToolbarWindow32</c> 消失，
/// 岛又不参与 <c>PrintWindow</c>/屏幕裁图；<c>TrayIconSource</c> 两路都只能拿到名字拿不到位图，
/// 顶栏于是画成一排应用名首字（"华 华"）——那不是托盘图标。而系统在
/// <c>HKCU\Control Panel\NotifyIconSettings\*</c> 给每个注册过托盘图标的应用留了
/// <c>IconSnapshot</c>：Windows 自己画出来的那个图标的完整 PNG 像素，不是替代品也不是猜测。
/// </para>
/// <para>
/// 【诚实边界】存单是历史清单（应用退出后仍在），所以只用来给 UIA 当前确实读到的那一格回填；
/// 匹配不上、并列歧义、PNG 损坏时保持原样（中性占位 + 原因），绝不拿 exe 主程序图标冒充托盘图标。
/// </para>
/// </summary>
internal static class TrayIconSnapshot
{
    private const string KeyPath = @"Control Panel\NotifyIconSettings";
    private static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47 };

    private sealed record Entry(string Tooltip, string ExePath, byte[] Png);

    /// <summary>一轮回填的结果：条目、成功回填数、存单条数（都进日志，便于真机对账）。</summary>
    public readonly record struct BackfillResult(
        IReadOnlyList<TrayIconItem> Items, int Filled, int StoreCount);

    /// <summary>
    /// 读一次注册表存单 → 用 Core 纯函数给每个条目找唯一最佳 PNG → 解码成冻结位图回填。
    /// 整个方法在后台线程跑（远程读 + PNG 解码），产出的 BitmapSource 已 Freeze，可安全交回 UI 线程。
    /// </summary>
    public static BackfillResult BackfillAll(IReadOnlyList<TrayIconItem> items)
    {
        var entries = Load();
        if (entries.Count == 0 || items.Count == 0)
            return new BackfillResult(items, 0, entries.Count);

        // Core 的 MatchIndex 在"同分多候选"时会弃权，所以这里先按应用把历史存单压成一条：
        // 同一 (tooltip, exe 基名) 只留 PNG 字节数最大的那张（通常是最清晰的一张）。
        var deduped = Dedupe(entries);
        var candidates = deduped
            .Select(e => new TraySnapshotCandidate(e.Tooltip, e.ExePath))
            .ToList();
        var pngByIndex = deduped.Select(e => e.Png).ToList();

        var filled = 0;
        var result = new List<TrayIconItem>(items.Count);
        foreach (var item in items)
        {
            if (item.Image is not null)
            {
                result.Add(item);   // 裁屏拿到的真像素优先，不覆盖
                continue;
            }

            var index = -1;
            foreach (var candidateLabel in LabelCandidates(BestLabel(item)))
            {
                index = TraySnapshotMatch.MatchIndex(candidates, candidateLabel);
                if (index >= 0) break;
            }
            if (index < 0 || !TryDecode(pngByIndex[index], out var image))
            {
                // 只在这一格确实没拿到像素时留痕：名字是排障唯一线索（用户能给的只有一张截图，
                // 截图里看不到 UIA 读到的名字），45s 一拍不会刷屏。
                Logger.Info($"托盘快照未回填：名字=[{BestLabel(item)}] 候选 {candidates.Count} 条 → 保持中性占位");
                result.Add(item);
                continue;
            }

            result.Add(item with
            {
                Image = image,
                ImageNote = "Windows 图标快照",
            });
            filled++;
        }

        Logger.Info($"顶栏托盘条：存单 {entries.Count} 条 → 去重 {candidates.Count} 条 → 回填真 PNG {filled}/{items.Count} 格");
        return new BackfillResult(result, filled, entries.Count);
    }

    /// <summary>UIA 的 Name 优先（应用自己写的），AutomationId 只作名字为空时的兜底。</summary>
    private static string BestLabel(TrayIconItem item)
        => string.IsNullOrWhiteSpace(item.Label) ? item.UiaAutomationId ?? item.UiaName ?? "" : item.Label;

    /// <summary>
    /// UIA 的名字常带动态后缀（"华为电脑管家 (已连接)"、"音量 本机扬声器: 已静音"），
    /// 存单里存的却是初次注册的稳定名。逐个剥掉后缀再问一次匹配，每多命中一格就少一个汉字占位。
    /// 所有候选仍过 Core 的「唯一最佳 + 同分弃权」闸门，不会因为放宽名字而错画。
    /// </summary>
    private static IEnumerable<string> LabelCandidates(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) yield break;
        yield return label;
        foreach (var sep in new[] { "(", "（", ":", "：", " - ", "·" })
        {
            var at = label.IndexOf(sep, StringComparison.Ordinal);
            if (at > 0)
            {
                var head = label[..at].Trim();
                if (head.Length > 0) yield return head;
            }
        }
        if (label.Length > 6) yield return label[..6].Trim();
    }

    private static List<Entry> Load()
    {
        var list = new List<Entry>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (root is null) return list;
            foreach (var name in root.GetSubKeyNames())
            {
                try
                {
                    using var sub = root.OpenSubKey(name);
                    if (sub is null) continue;
                    var exe = (sub.GetValue("ExecutablePath") as string)?.Trim() ?? "";
                    var tooltip = (sub.GetValue("InitialTooltip") as string)?.Trim() ?? "";
                    if (sub.GetValue("IconSnapshot") is not byte[] png || png.Length < 8) continue;
                    if (!png.AsSpan(0, 4).SequenceEqual(PngMagic)) continue;
                    // 本程序自己占了好几份存单（Debug/Release/dist/安装版/dotnet 托管），顶栏不该自指。
                    if (tooltip.Contains("Beautiful", StringComparison.OrdinalIgnoreCase)) continue;
                    if (tooltip.Length == 0 && exe.Length == 0) continue;
                    list.Add(new Entry(tooltip, exe, png));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                               or System.Security.SecurityException or ArgumentException)
                {
                    // 单键读不出不影响其它键：存单是尽力而为的事实清单
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException or ArgumentException)
        {
            Logger.Warn($"托盘图标快照：读不到 {KeyPath}（{ex.Message}）——保持中性占位");
        }
        return list;
    }

    /// <summary>
    /// 同一应用换图标会在注册表里留下新旧多条存单，32/64 位副本还会共用同一个 tooltip。
    /// 只按 tooltip（tooltip 为空才按 exe 基名）压成一条，Core 的「同分并列弃权」才不会被
    /// 自己制造的历史副本全部挡掉；同一组保留 PNG 字节数最大的那张（通常最清晰）。
    /// </summary>
    private static List<Entry> Dedupe(List<Entry> entries)
        => entries
            .GroupBy(e =>
            {
                var tooltip = TraySnapshotMatch.Normalize(e.Tooltip);
                return tooltip.Length > 0
                    ? "t:" + tooltip
                    : "e:" + TraySnapshotMatch.Normalize(ExeBaseName(e.ExePath));
            })
            .Select(g => g.OrderByDescending(e => e.Png.Length).First())
            .ToList();

    private static string ExeBaseName(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        var name = path.Replace('/', '\\');
        var slash = name.LastIndexOf('\\');
        if (slash >= 0) name = name[(slash + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    private static bool TryDecode(byte[] png, out BitmapSource image)
    {
        image = null!;
        try
        {
            var decoder = new PngBitmapDecoder(new MemoryStream(png),
                BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return false;
            BitmapSource frame = decoder.Frames[0];
            if (frame.Format != PixelFormats.Pbgra32)
                frame = new FormatConvertedBitmap(frame, PixelFormats.Pbgra32, null, 0);
            frame.Freeze();     // 后台线程解码 → UI 线程使用，必须冻结
            image = frame;
            return true;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException
                                       or FileFormatException or InvalidOperationException or ArgumentException)
        {
            return false;       // 存单不是合法 PNG → 保持占位，不猜
        }
    }
}
