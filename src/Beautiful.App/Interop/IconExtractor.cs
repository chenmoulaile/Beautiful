using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;          // PixelFormats（BitmapSource.Create 的像素格式）
using System.Windows.Media.Imaging;
using Beautiful.App.Services;

namespace Beautiful.App.Interop;

/// <summary>
/// 图标提取（v3 T6 重写为"大来源 + 降级链"）。
/// <para>
/// 【根因（本机实测数据，探针脚本量化过）】旧实现只请求 48px：<c>SHDefExtractIconW(…, MAKELONG(48,16))</c>
/// → <c>CreateBitmapSourceFromHIcon</c> 得到 48×48 的 Bgra32。Dock 的条目却是 <b>DIP</b> 方框：
/// IconSize=44 DIP 在 100%/150%/200% 分别是 44/66/88 **物理像素**，macOS 主题悬停再乘 1.8× 放大
/// （44 DIP@150% 的悬停峰值 ≈ 119px）。设置项上限 72 DIP@200% 更是 144px（悬停 259px）。
/// 也就是说：缓存里最大的资产 48px，而屏幕上要 66~259px —— 全程线性放大，就是"图标看着劣质"。
/// 同一台机器把请求改成 256：<c>SHDefExtractIcon(…, MAKELONG(256,16))</c> 对 explorer.exe /
/// msedge.exe / notepad.exe / cmd.exe / shell32.dll / WindowsApps 下的 Notepad.exe 全部返回
/// 256×256 Bgra32（0=成功）；而 <c>SHGetFileInfo(PIDL)</c> 只有 32px、<c>SHGFI_SMALLICON</c> 只有 16px。
/// </para>
/// <para>
/// 【降级链（取最大的成功结果，不是取第一个不为空）】
/// ① <see cref="NativeMethods.PrivateExtractIconsW"/> 256×256 —— 能取到资源里最大的帧（含 PNG 帧）；
/// ② <see cref="NativeMethods.SHDefExtractIcon"/> 请求 256（旧调用只请求 48）；
/// ③ .lnk：解析目标（<see cref="ShellLink"/>）后把 ①② 在目标/IconLocation 上重跑；
/// ④ shell 命名空间 / <c>shell:AppsFolder</c> AUMID 的 <c>IShellItemImageFactory::GetImage</c> ——
///    **默认关闭**，只由 <c>BEAUTIFUL_ICON_SHELLIMAGE=1</c> 显式开启，原因见 <see cref="TryShellItemImage"/>；
/// ⑤ <c>SHGetFileInfoW(PIDL)</c> 系统大图标（本机 32px）—— 末位兜底，永不为空（视图还有字形回退）。
/// 每层实际用了哪一层、拿到多大，**每个图标源只落一行日志**（<see cref="LogOnce"/>），不逐帧刷。
/// </para>
/// <para>
/// 【句柄】每个 HICON/HBITMAP 都在创建它的同一个 finally 里销毁（本仓库把句柄泄漏定级 P0）；
/// 转换出的 BitmapSource 一律 Freeze，之后可在渲染线程共享。
/// </para>
/// </summary>
internal static class IconExtractor
{
    /// <summary>
    /// 目标提取尺寸：256 = 图标资源里常见的最大帧，也覆盖 72 DIP·200% 的渲染需求。
    /// 再大没有意义（资源里没有），所以它是"请求"而不是"上限"。
    /// </summary>
    public const int PreferredSize = 256;

    /// <summary>提取层标识（落日志用；顺序即降级顺序）。</summary>
    public enum Tier
    {
        /// <summary>什么都没拿到。</summary>
        None,
        /// <summary>① PrivateExtractIconsW。</summary>
        PrivateExtract,
        /// <summary>② SHDefExtractIconW。</summary>
        ShellDef,
        /// <summary>③ .lnk 解析后的目标上重跑 ①②。</summary>
        LinkTarget,
        /// <summary>④ IShellItemImageFactory（显式开启才可能出现在这一行）。</summary>
        ShellItemImage,
        /// <summary>⑤ SHGetFileInfo(PIDL) 系统大图标。</summary>
        SystemIcon
    }

    /// <summary>一次提取的结果：位图 + 产出它的层 + 真正用于提取的路径（日志与缓存键用）。</summary>
    public sealed record Result(BitmapSource? Icon, Tier Tier, string UsedPath)
    {
        public static readonly Result Empty = new(null, Tier.None, string.Empty);
        public int Px => Icon?.PixelWidth ?? 0;
    }

    /// <summary>④ 层的开关：环境变量显式开启（见类型注释与 <see cref="TryShellItemImage"/>）。</summary>
    private static readonly bool ShellImageEnabled =
        Environment.GetEnvironmentVariable("BEAUTIFUL_ICON_SHELLIMAGE") == "1";

    /// <summary>源路径 → 结果（有界；提取发生在条目构建期，不在逐帧路径）。</summary>
    private static readonly LruCache<string, Result> Cache = new(128);

    /// <summary>
    /// ① 层可用性：本机 shell32 按名解析不到 PrivateExtractIconsW（探针实测 EntryPointNotFoundException），
    /// 一旦解析失败就永久停用并降级到 ②，绝不改成"猜序号"调用（错序号 = 用错签名调陌生函数 = 进程级风险）。
    /// </summary>
    private static bool _privateExtractUsable = true;

    /// <summary>已落过日志的图标源（每源一行，见 <see cref="LogOnce"/>）。</summary>
    private static readonly HashSet<string> Logged = new(StringComparer.OrdinalIgnoreCase);
    private const int LogLimit = 256;   // 上限只为防"无限增长的日志集合"，正常运行远达不到

    /// <summary>公开签名沿用 Task 8 契约（WindowTracker / DockViewModel 无感知），默认尺寸从 48 抬到 256。</summary>
    public static BitmapSource? GetIcon(string path, int size = PreferredSize) => GetIconDetailed(path, size).Icon;

    /// <summary>带层的提取（缓存命中也返回同一 Result 实例，因此日志同样只出现一次）。</summary>
    public static Result GetIconDetailed(string path, int size = PreferredSize)
    {
        if (string.IsNullOrWhiteSpace(path)) return Result.Empty;
        var key = path.Trim().ToLowerInvariant() + '|' + size;
        if (Cache.TryGet(key, out var hit)) return hit;
        var result = Extract(path, size);
        Cache.Set(key, result);
        LogOnce(result);
        return result;
    }

    /// <summary>缓存整体作废（主题/尺寸/DPI 变更时由 IconBake 一侧调用其自有缓存；这里留给测试与"图标源变了"）。</summary>
    public static void ClearCache() => Cache.Clear();

    // ---------- 降级链 ----------

    private static Result Extract(string path, int size)
    {
        var best = TryFileTiers(path, size, Tier.PrivateExtract, Tier.ShellDef);
        if (best.Icon is not null) return best;

        // ③ .lnk：图标在目标 exe（或快捷方式自定义的 IconLocation）里，.lnk 本身按路径取图是拿不到的
        //    （本机实测 SHDefExtractIconW 对 .lnk 返回 hr=1 且句柄为空）。
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var candidate in LinkCandidates(path))
            {
                var viaLink = TryFileTiers(candidate, size, Tier.PrivateExtract, Tier.ShellDef);
                if (viaLink.Icon is not null)
                    return viaLink with { Tier = Tier.LinkTarget };
            }
        }

        // ④ 只在前面全失败时才碰：正常文件路径 ② 就能拿到 256，没必要走一条本机证伪过的 COM 路径
        if (ShellImageEnabled)
        {
            var viaFactory = TryShellItemImage(path, size);
            if (viaFactory.Icon is not null) return viaFactory;
        }

        // ⑤ 末位兜底：shell 命名空间（::{clsid} / shell:...）的系统大图标，永不为空
        return TrySystemIcon(path);
    }

    /// <summary>快捷方式的图标候选：先 IconLocation（剥掉尾随 ",index"，且必须真存在），再 TargetPath。</summary>
    private static IEnumerable<string> LinkCandidates(string lnkPath)
    {
        ShellLink.Resolved resolved;
        try { resolved = ShellLink.Resolve(lnkPath); }
        catch { yield break; }   // 解析失败不阻断降级链（后面还有 ⑤）

        foreach (var raw in new[] { StripIconIndex(resolved.IconLocation), resolved.TargetPath })
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (!File.Exists(raw)) continue;
            yield return raw;
        }
    }

    private static string StripIconIndex(string loc)
    {
        var comma = loc.LastIndexOf(',');
        return comma > 0 ? loc[..comma] : loc;
    }

    /// <summary>
    /// ①② 两层**都**跑一遍取更大的那个（两层通常给同一个 256 帧，差别只在个别资源：
    /// PrivateExtractIcons 认得某些资源的最大帧，SHDefExtractIcon 在别的组合上更全）。
    /// 这里刻意不"拿到 256 就短路"：短路会依赖①层是否可用（本机①层按名解析不到，见
    /// <see cref="_privateExtractUsable"/>），于是同一台机器上"用了哪一层"随资源顺序漂移；
    /// 两次调用的代价落在条目构建期（每个源一次，结果进 <see cref="Cache"/>），不在逐帧路径。
    /// </summary>
    private static Result TryFileTiers(string path, int size, Tier first, Tier second)
    {
        Result? best = null;

        if (_privateExtractUsable)
        {
            var r = TryPrivateExtract(path, size, first);
            if (r.Icon is not null) best = r;
        }

        var def = TryShellDef(path, size, second);
        if (def.Icon is not null && (best is null || def.Px > best.Px)) best = def;

        return best ?? Result.Empty with { UsedPath = path };
    }

    /// <summary>① PrivateExtractIconsW：请求 w×h，shell 返回资源中最接近的帧（含 256 PNG 帧）。</summary>
    private static Result TryPrivateExtract(string path, int size, Tier tier)
    {
        var handles = new IntPtr[1];
        var ids = new int[1];
        try
        {
            var n = NativeMethods.PrivateExtractIconsW(path, 0, size, size, handles, ids, 1, 0);
            if (n <= 0 || handles[0] == IntPtr.Zero) return Result.Empty with { UsedPath = path };
            var img = FromHIcon(handles[0]);
            return img is null ? Result.Empty with { UsedPath = path } : new Result(img, tier, path);
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // 本机就是这一支：按名解析不到。永久停用 ① 层并降到 ②，只留一行日志（不逐次刷）。
            _privateExtractUsable = false;
            Logger.Info($"图标层① PrivateExtractIconsW 在此系统不可用（{ex.GetType().Name}）→ 永久降级到 SHDefExtractIcon");
            return Result.Empty with { UsedPath = path };
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException
                                      or ArgumentException or InvalidOperationException)
        {
            return Result.Empty with { UsedPath = path };
        }
        finally
        {
            // 句柄纪律：成功/失败/异常都要销毁（n>0 但转换抛异常时这里就是唯一出口）
            if (handles[0] != IntPtr.Zero) NativeMethods.DestroyIcon(handles[0]);
        }
    }

    /// <summary>② SHDefExtractIconW：老路径，只是把请求尺寸从 48 抬到 256（nSize 低字=大图，高字=小图）。</summary>
    private static Result TryShellDef(string path, int size, Tier tier)
    {
        var nSize = (uint)Math.Clamp(size, 16, 256) | (16u << 16);
        IntPtr hLarge = IntPtr.Zero, hSmall = IntPtr.Zero;
        try
        {
            var hr = NativeMethods.SHDefExtractIcon(path, 0, 0, out hLarge, out hSmall, nSize);
            if (hr != 0 || hLarge == IntPtr.Zero) return Result.Empty with { UsedPath = path };
            var img = FromHIcon(hLarge);
            return img is null ? Result.Empty with { UsedPath = path } : new Result(img, tier, path);
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException
                                      or ArgumentException or InvalidOperationException)
        {
            return Result.Empty with { UsedPath = path };
        }
        finally
        {
            // nSize 高字请求的 16px 小图也会被 shell 写出来：out 参数 marshaling 产生的是真实指针，
            // 一并销毁（旧实现同样如此，这里是链路上第二个必须成对释放的句柄）。
            if (hLarge != IntPtr.Zero) NativeMethods.DestroyIcon(hLarge);
            if (hSmall != IntPtr.Zero) NativeMethods.DestroyIcon(hSmall);
        }
    }

    /// <summary>
    /// ④ <c>IShellItemImageFactory::GetImage</c>：唯一能给 shell 命名空间/AUMID 真实大 logo 的层，
    /// 但**本仓库已在本机证伪它**：任意路径/尺寸/标志组合都触发 AccessViolationException
    /// （见 THIRD-PARTY-NOTICES 与仓库历史）。.NET 8 里 AV 属于损坏状态异常，<b>try/catch 抓不住</b>，
    /// 所以这一层做不到"进程内绝对安全"，只能：默认关闭 + <c>BEAUTIFUL_ICON_SHELLIMAGE=1</c> 显式开启 +
    /// 只在 ①②③⑤ 全失败时才试。开启者自负其责，报告里也如实写明"本 VM 无法验证"。
    /// </summary>
    private static Result TryShellItemImage(string path, int size)
    {
        IntPtr hbmp = IntPtr.Zero;
        try
        {
            var factory = NativeMethods.BindShellItemImageFactory(path);
            if (factory is null) return Result.Empty with { UsedPath = path };
            factory.GetImage(new NativeMethods.PSIZE { cx = size, cy = size },
                             NativeMethods.SiigbfBiggerSizeOk, out hbmp);
            if (hbmp == IntPtr.Zero) return Result.Empty with { UsedPath = path };
            var img = FromHBitmapPreservingAlpha(hbmp);
            return img is null ? Result.Empty with { UsedPath = path } : new Result(img, Tier.ShellItemImage, path);
        }
        catch (Exception ex) when (ex is COMException or EntryPointNotFoundException or DllNotFoundException
                                      or InvalidOperationException or ArgumentException)
        {
            return Result.Empty with { UsedPath = path };
        }
        finally
        {
            if (hbmp != IntPtr.Zero) NativeMethods.DeleteObject(hbmp);   // GetImage 交回来的 DIB 段归我们释放
        }
    }

    /// <summary>⑤ SHGetFileInfoW(PIDL)：shell 命名空间的系统图标（本机 32px）。失败返回空结果（视图回退字形）。</summary>
    private static Result TrySystemIcon(string path)
    {
        IntPtr pidl = IntPtr.Zero;
        try
        {
            NativeMethods.SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _);
            if (pidl == IntPtr.Zero) return Result.Empty with { UsedPath = path };
            var shfi = new NativeMethods.SHFILEINFOW();
            var ok = NativeMethods.SHGetFileInfo(pidl, 0, ref shfi,
                (uint)Marshal.SizeOf<NativeMethods.SHFILEINFOW>(),
                NativeMethods.ShgfiIcon | NativeMethods.ShgfiPidl | NativeMethods.ShgfiLargeIcon);
            if (ok == IntPtr.Zero || shfi.hIcon == IntPtr.Zero) return Result.Empty with { UsedPath = path };
            try
            {
                var img = FromHIcon(shfi.hIcon);
                return img is null ? Result.Empty with { UsedPath = path }
                                   : new Result(img, Tier.SystemIcon, path);
            }
            finally
            {
                NativeMethods.DestroyIcon(shfi.hIcon);
            }
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException
                                      or ArgumentException or InvalidOperationException)
        {
            return Result.Empty with { UsedPath = path };
        }
        finally
        {
            if (pidl != IntPtr.Zero) NativeMethods.CoTaskMemFree(pidl);
        }
    }

    // ---------- 位图转换 ----------

    /// <summary>HICON → 冻结的 BitmapSource（保留 32bpp alpha；CreateBitmapSourceFromHIcon 取的是图标 DIB）。</summary>
    private static BitmapSource? FromHIcon(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            var bs = Imaging.CreateBitmapSourceFromHIcon(hIcon, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bs.Freeze();
            return bs;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// HBITMAP（DIB 段）→ 冻结的 BitmapSource，**alpha 原样保留**：
    /// <c>CreateBitmapSourceFromHBitmap</c> 会把掩码当不透明处理并输出 Bgr32，图标透明区会变成黑底方块，
    /// 于是这里直接按 BITMAP 结构把像素拷进 Bgra32。bottom-up/top-down 由 biHeight 符号决定。
    /// 全 0 alpha 的 DIB（shell 常给不透明缩略图）钉成 255，否则画出来是空的。
    /// </summary>
    private static BitmapSource? FromHBitmapPreservingAlpha(IntPtr hbmp)
    {
        if (hbmp == IntPtr.Zero) return null;
        var bm = new NativeMethods.BITMAP();
        if (NativeMethods.GetObjectW(hbmp, Marshal.SizeOf<NativeMethods.BITMAP>(), ref bm) == 0) return null;
        var w = bm.bmWidth;
        var h = Math.Abs(bm.bmHeight);
        if (w <= 0 || h <= 0 || bm.bmBits == IntPtr.Zero || bm.bmBitsPixel != 32) return null;

        var stride = w * 4;
        var pixels = new byte[stride * h];
        if (bm.bmHeight > 0)  // bottom-up：逐行翻过来
            for (var y = 0; y < h; y++)
                Marshal.Copy(bm.bmBits + (h - 1 - y) * stride, pixels, y * stride, stride);
        else
            Marshal.Copy(bm.bmBits, pixels, 0, pixels.Length);

        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) { hasAlpha = true; break; }
        if (!hasAlpha)
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 0xFF;

        var bs = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bs.Freeze();
        return bs;
    }

    // ---------- 诊断 ----------

    /// <summary>
    /// 每个图标源一行日志（"哪个层产出的 + 多大"）：真机反馈"图标糊"时先看这一行，
    /// 判的是"资源里到底有没有大帧"，不必再靠截图目测。上限 <see cref="LogLimit"/> 条防集合无界增长。
    /// </summary>
    private static void LogOnce(Result result)
    {
        if (result.Icon is null) return;
        var path = result.UsedPath;
        if (path.Length == 0 || Logged.Count >= LogLimit) return;
        if (!Logged.Add(path)) return;
        var name = Path.GetFileName(path);
        Logger.Info($"图标提取 {name}: 层={result.Tier} 尺寸={result.Icon.PixelWidth}×{result.Icon.PixelHeight} " +
                    $"格式={result.Icon.Format} 请求={PreferredSize}");
    }
}
