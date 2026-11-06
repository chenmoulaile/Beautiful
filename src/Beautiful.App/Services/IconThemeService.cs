using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Media.Imaging;
using Beautiful.Core.Dock;
using Beautiful.Core.Themes;
using Application = System.Windows.Application;   // UseWindowsForms 隐式全局 using 与 WinForms.Application 消歧（项目惯例）

namespace Beautiful.App.Services;

/// <summary>覆盖引用的两种来源：<see cref="Asset"/> = 内置 TahoeIcons 图集，<see cref="File"/> = 本地图片文件。</summary>
public enum IconThemeRefKind
{
    Asset,
    File
}

/// <summary>一条生效中的覆盖：应用键（<see cref="IconThemeKey"/>）→ 图集键或文件路径。</summary>
public sealed record IconThemeRef(string AppKey, IconThemeRefKind Kind, string Value);

/// <summary>
/// 图标主题服务（静态门面，对齐 <see cref="SettingsService"/> 的门面惯例）：**按应用键**覆盖
/// Dock / 启动台 / 切换器等所有走 <see cref="MacIconCatalog"/> 的图标，持久化
/// <c>config/icon-theme.json</c>（读写模式与 settings.json / dock.json 同一惯例：缺文件用代码默认
/// 不落盘、坏条目逐条丢弃、落盘尽力而为）。
/// <para>
/// 【键口径】覆盖键 = Core 的 <see cref="IconThemeKey"/>（对齐 DockAppKey 的「一格 = 一个应用」分组键；
/// 访达 finder / 启动台 launchpad / 废纸篓 trash）。Dock 合并这一格用哪把尺子，覆盖键就是哪把。
/// </para>
/// <para>
/// 【热生效】任何变更（<see cref="Set"/>/<see cref="Reset"/>/<see cref="ClearAll"/>/<see cref="ApplyOverrides"/>）
/// 都会：清 <see cref="MacIconCatalog"/> 缓存 → 清 <see cref="IconBakeContext"/> 缓存 → raise
/// <see cref="Changed"/>。DockWindow 订阅后走既有的「重建条目容器」出口（<c>Surface.RebindIconContainers</c>，
/// 与图标尺寸变更同一出口），绑定重新求值 → 新图标立即上屏，不重启。启动台/切换器在下次解析图标时
/// 自然拿到新脸（缓存已清）。
/// </para>
/// <para>
/// 【懒加载】App 启动**不需要**显式调用 <see cref="Load"/>：首次图标解析（可能发生在启动台后台泵线程）
/// 自动读盘一次，失败按空表继续，绝不阻塞/拖垮图标链路。
/// </para>
/// </summary>
public static class IconThemeService
{
    /// <summary>覆盖表变更（任何线程触发；DockWindow 侧自行调度回 UI 线程）。</summary>
    public static event Action? Changed;

    private static readonly object Gate = new();
    private static bool _loaded;
    private static string _directory = Path.Combine(AppContext.BaseDirectory, "config");
    private static IconThemeData _data = IconThemeData.Empty;

    private static string FilePath => Path.Combine(_directory, "icon-theme.json");

    /// <summary>导入主题包时图标文件的转存目录（包内 PNG 解包落这里，覆盖表指向它）。</summary>
    private static string IconDirectory => Path.Combine(_directory, "icon-theme", "icons");

    /// <summary>显式加载（幂等；可传独立目录做隔离）。文件缺失/损坏按空表继续，不删文件。</summary>
    public static void Load(string? directory = null)
    {
        lock (Gate)
        {
            if (!string.IsNullOrWhiteSpace(directory)) _directory = directory;
            _loaded = true;
            ReadFromDisk();
        }
    }

    /// <summary>当前覆盖表（键 → asset:/文件路径；快照副本，外部改不动内存态）。</summary>
    public static IReadOnlyDictionary<string, string> Overrides
    {
        get
        {
            EnsureLoaded();
            lock (Gate) return new Dictionary<string, string>(_data.Overrides);
        }
    }

    /// <summary>覆盖条数（编辑器/诊断用）。</summary>
    public static int Count
    {
        get { EnsureLoaded(); lock (Gate) return _data.Overrides.Count; }
    }

    /// <summary>
    /// 按候选键序找第一条生效覆盖（<see cref="MacIconCatalog"/> 的覆盖层入口；键序由 Core
    /// <see cref="IconThemeKey.CandidatesFor"/> 给出）。未加载则先读盘；空表返回 null。
    /// </summary>
    internal static IconThemeRef? FindRef(IReadOnlyList<string> candidateKeys)
    {
        if (candidateKeys.Count == 0) return null;
        EnsureLoaded();
        lock (Gate)
        {
            foreach (var key in candidateKeys)
            {
                if (!_data.Overrides.TryGetValue(key, out var value)) continue;
                return value.StartsWith(IconThemeFile.AssetPrefix, StringComparison.Ordinal)
                    ? new IconThemeRef(key, IconThemeRefKind.Asset, value[IconThemeFile.AssetPrefix.Length..])
                    : new IconThemeRef(key, IconThemeRefKind.File, value);
            }
        }
        return null;
    }

    /// <summary>
    /// 设置一条覆盖并落盘。<paramref name="iconRef"/> = <c>asset:&lt;图集键&gt;</c> 或图片文件路径
    /// （后者经 Core <see cref="DockIconOverride.Validate"/>：存在 / 格式 / ≤8MB）。
    /// 成功返回 true；失败返回 false 并给出 <paramref name="reason"/>（编辑器直接显示）。
    /// </summary>
    public static bool Set(string appKey, string iconRef, out string reason)
    {
        reason = string.Empty;
        var key = (appKey ?? string.Empty).Trim().ToLowerInvariant();
        if (key.Length == 0 || key.Length > IconThemeFile.MaxKeyLength)
        {
            reason = "应用键为空或超长";
            return false;
        }

        var reference = (iconRef ?? string.Empty).Trim();
        if (reference.StartsWith(IconThemeFile.AssetPrefix, StringComparison.Ordinal))
        {
            if (IconThemeFile.ValidateValueShape(reference) is { } shapeReason)
            {
                reason = shapeReason;
                return false;
            }
            if (!AssetExists(reference[IconThemeFile.AssetPrefix.Length..]))
            {
                reason = "图集里没有这张图标";
                return false;
            }
        }
        else if (DockIconOverride.Validate(reference, out reason) is not { } filePath)
        {
            return false;
        }
        else
        {
            reference = filePath;
        }

        lock (Gate)
        {
            var map = new Dictionary<string, string>(_data.Overrides) { [key] = reference };
            _data = IconThemeFile.Normalize(new IconThemeData(IconThemeFile.CurrentVersion, map));
            Save();
        }
        Logger.Info($"图标主题: 键={key} ← {Describe(reference)}");
        NotifyChanged();
        return true;
    }

    /// <summary>恢复某格默认（删覆盖并落盘；本来就没有时无操作）。</summary>
    public static bool Reset(string appKey)
    {
        var key = (appKey ?? string.Empty).Trim().ToLowerInvariant();
        if (key.Length == 0) return false;
        bool removed;
        lock (Gate)
        {
            var map = new Dictionary<string, string>(_data.Overrides);
            removed = map.Remove(key);
            if (!removed) return false;
            _data = new IconThemeData(IconThemeFile.CurrentVersion, map);
            Save();
        }
        Logger.Info($"图标主题: 键={key} 已恢复默认");
        NotifyChanged();
        return true;
    }

    /// <summary>清空全部覆盖（「全部恢复默认」）。</summary>
    public static void ClearAll()
    {
        lock (Gate)
        {
            if (_data.Overrides.Count == 0) return;
            _data = IconThemeData.Empty;
            Save();
        }
        Logger.Info("图标主题: 已清空全部覆盖");
        NotifyChanged();
    }

    /// <summary>
    /// 主题包导入的整体应用入口（import 已把包内图标转存到本地）：整体替换覆盖表 → 落盘 → 热生效。
    /// </summary>
    public static void ApplyOverrides(IReadOnlyDictionary<string, string> overrides)
    {
        lock (Gate)
        {
            _data = IconThemeFile.Normalize(new IconThemeData(IconThemeFile.CurrentVersion, new Dictionary<string, string>(overrides)));
            Save();
        }
        NotifyChanged();
    }

    // ---------- 主题包（本地导入导出，zip） ----------

    /// <summary>
    /// 导出主题包：manifest（<see cref="ThemePack.BuildManifestJson"/>）+ 图标 PNG 打进 zip。
    /// asset 覆盖直接从内置图集读字节；文件覆盖统一**转存 ≤256px PNG**（ico/jpg/bmp 一并归一，
    /// 包格式只认 PNG）。当前没有任何覆盖时报错（空包没有意义）。
    /// </summary>
    public static bool ExportPack(string zipPath, string name, string author, string description, out string error)
    {
        error = string.Empty;
        var overrides = Overrides;
        if (overrides.Count == 0)
        {
            error = "当前没有任何图标覆盖，没有可导出的内容";
            return false;
        }
        var packName = string.IsNullOrWhiteSpace(name) ? "我的图标主题" : name.Trim();
        var packAuthor = author?.Trim() ?? string.Empty;
        var packDescription = description?.Trim() ?? string.Empty;

        try
        {
            using var stream = File.Create(zipPath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
            var manifestEntry = archive.CreateEntry(ThemePack.ManifestEntryName, CompressionLevel.Optimal);
            using (var writer = new StreamWriter(manifestEntry.Open()))
            {
                writer.Write(ThemePack.BuildManifestJson(packName, packAuthor, packDescription, overrides));
            }

            foreach (var (key, reference) in overrides)
            {
                var bytes = reference.StartsWith(IconThemeFile.AssetPrefix, StringComparison.Ordinal)
                    ? AssetBytes(reference[IconThemeFile.AssetPrefix.Length..])
                    : FileIconBytes(reference);
                if (bytes is null)
                {
                    Logger.Warn($"图标主题导出: 键={key} 的图标读不出（{Describe(reference)}），已跳过该条");
                    continue;
                }
                var entry = archive.CreateEntry(ThemePack.IconEntryName(key), CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                entryStream.Write(bytes, 0, bytes.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = $"导出失败：{ex.Message}";
            Logger.Warn($"图标主题导出失败: {ex.Message}");
            return false;
        }

        Logger.Info($"图标主题导出: {zipPath}（{overrides.Count} 条覆盖）");
        return true;
    }

    /// <summary>
    /// 导入主题包：按 manifest 引用**逐条读取**包内条目（绝不全量解包）→ 校验 → 图标转存本地
    /// （≤256px PNG，落 <see cref="IconDirectory"/>）→ 整体应用覆盖表并热生效。
    /// 校验不通过返回 false 并把原因拼进 <paramref name="report"/>（编辑器直接展示）。
    /// </summary>
    public static bool ImportPack(string zipPath, out string report)
    {
        report = string.Empty;
        if (!File.Exists(zipPath))
        {
            report = "文件不存在";
            return false;
        }

        try
        {
            using var stream = File.OpenRead(zipPath);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

            var manifestEntry = archive.Entries.FirstOrDefault(e =>
                string.Equals(e.FullName, ThemePack.ManifestEntryName, StringComparison.OrdinalIgnoreCase));
            if (manifestEntry is null)
            {
                report = $"包里没有 {ThemePack.ManifestEntryName}，不是图标主题包";
                return false;
            }
            string manifestJson;
            using (var reader = new StreamReader(manifestEntry.Open()))
            {
                manifestJson = reader.ReadToEnd();
            }
            if (!ThemePack.TryParseManifest(manifestJson, out var manifest, out var errors))
            {
                report = "主题包 manifest 校验失败：" + string.Join("；", errors);
                Logger.Warn($"图标主题导入被拒：{report}");
                return false;
            }

            var archiveErrors = ThemePack.ValidateAgainstArchive(manifest!, archive.Entries.Select(e => e.FullName));
            if (archiveErrors.Count > 0)
            {
                report = "主题包内容校验失败：" + string.Join("；", archiveErrors);
                Logger.Warn($"图标主题导入被拒：{report}");
                return false;
            }

            Directory.CreateDirectory(IconDirectory);
            var applied = new Dictionary<string, string>(StringComparer.Ordinal);
            var assetCount = 0;
            long totalBytes = 0;
            foreach (var (key, reference) in manifest!.IconOverrides)
            {
                if (reference.StartsWith(IconThemeFile.AssetPrefix, StringComparison.Ordinal))
                {
                    var assetKey = reference[IconThemeFile.AssetPrefix.Length..];
                    if (!AssetExists(assetKey))
                    {
                        Logger.Warn($"图标主题导入: 键={key} 引用的图集键 {assetKey} 本机图集没有，已跳过");
                        continue;
                    }
                    applied[key] = reference;
                    assetCount++;
                    continue;
                }

                var entry = archive.Entries.FirstOrDefault(e =>
                    string.Equals(e.FullName, reference, StringComparison.OrdinalIgnoreCase));
                if (entry is null || entry.Length > ThemePack.MaxIconBytes)
                {
                    Logger.Warn($"图标主题导入: 键={key} 的图标缺失或超限，已跳过");
                    continue;
                }
                totalBytes += entry.Length;
                if (totalBytes > ThemePack.MaxReadBytes)
                {
                    report = "主题包过大（超过读取上限），已中止";
                    Logger.Warn($"图标主题导入被拒：{report}");
                    return false;
                }

                using var entryStream = entry.Open();
                if (PngBytesFromStream(entryStream) is not { } bytes)
                {
                    Logger.Warn($"图标主题导入: 键={key} 的图标不是可解码 PNG，已跳过");
                    continue;
                }
                var target = Path.Combine(IconDirectory, $"{key}.png");
                File.WriteAllBytes(target, bytes);
                applied[key] = target;
            }

            if (applied.Count == 0)
            {
                report = "主题包里没有一条可用的图标覆盖";
                return false;
            }

            ApplyOverrides(applied);
            report = $"已导入主题包「{manifest!.Name}」：{applied.Count} 条覆盖" +
                     $"（图集 {assetCount} 条，文件 {applied.Count - assetCount} 条），Dock 已即时刷新";
            Logger.Info($"图标主题导入: {report}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                        or NotSupportedException or InvalidDataException)
        {
            report = $"导入失败：{ex.Message}";
            Logger.Warn($"图标主题导入失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>给编辑器显示的引用描述（"图集 finder" / "xxx.png"）。</summary>
    public static string DescribeRef(string iconRef)
        => iconRef.StartsWith(IconThemeFile.AssetPrefix, StringComparison.Ordinal)
            ? "图集 " + iconRef[IconThemeFile.AssetPrefix.Length..]
            : Describe(iconRef);

    private static string Describe(string iconRef)
        => iconRef.StartsWith(IconThemeFile.AssetPrefix, StringComparison.Ordinal)
            ? "图集 " + iconRef[IconThemeFile.AssetPrefix.Length..]
            : "文件 " + iconRef;

    // ---------- 内部：读盘 / 写盘 / 通知 ----------

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;
            _loaded = true;   // 先置位：坏文件不反复重读（下次任何变更 Save 会重建好文件）
            ReadFromDisk();
        }
    }

    private static void ReadFromDisk()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                _data = IconThemeData.Empty;   // 缺文件：代码默认，不落盘（与 dock.json 惯例一致）
                return;
            }
            if (!IconThemeFile.TryParse(File.ReadAllText(FilePath), out var parsed, out var errors))
            {
                foreach (var line in errors) Logger.Warn($"icon-theme.json 读取：{line}");
                Logger.Warn("icon-theme.json 结构损坏，按空覆盖表继续（文件保留，下次保存覆盖）");
                _data = IconThemeData.Empty;
                return;
            }
            foreach (var line in errors) Logger.Warn($"icon-theme.json：{line}");
            _data = IconThemeFile.Normalize(parsed!);
            Logger.Info($"图标主题已载入：{_data.Overrides.Count} 条覆盖（{FilePath}）");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"icon-theme.json 读取失败，按空覆盖表继续: {ex.Message}");
            _data = IconThemeData.Empty;
        }
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(FilePath, IconThemeFile.Serialize(_data));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 尽力而为保存：瞬时文件锁/权限失败不拖垮 UI（下次变更可重试，与 settings.json 同一姿态）
            Logger.Warn($"icon-theme.json 保存失败: {ex.Message}");
        }
    }

    /// <summary>变更通知：两处图标缓存都清掉，再广播（订阅方 UI 线程自行调度）。</summary>
    private static void NotifyChanged()
    {
        MacIconCatalog.ClearCache();
        IconBakeContext.ClearCache();
        try { Changed?.Invoke(); }
        catch (Exception ex) { Logger.Warn($"图标主题变更通知失败: {ex.Message}"); }
    }

    // ---------- 图集字节（导出）与 PNG 转码 ----------

    /// <summary>图集键对应的资源是否存在（编辑器选图 / 导入时校验 asset 引用）。</summary>
    internal static bool AssetExists(string assetKey)
    {
        try
        {
            return Application.GetResourceStream(
                       new Uri($"pack://application:,,,/Assets/TahoeIcons/{assetKey}.png", UriKind.Absolute)) is not null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            return false;   // WPF 对缺失资源抛 IOException("Cannot locate resource")
        }
    }

    private static byte[]? AssetBytes(string assetKey)
    {
        try
        {
            var info = Application.GetResourceStream(
                new Uri($"pack://application:,,,/Assets/TahoeIcons/{assetKey}.png", UriKind.Absolute));
            if (info is null) return null;
            using var stream = info.Stream;
            return PngBytesFromStream(stream);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>文件覆盖 → ≤256px PNG 字节（导出/导入统一口径，任何 WIC 可解码格式都能转）。</summary>
    private static byte[]? FileIconBytes(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = File.OpenRead(path);
            return PngBytesFromStream(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>从流里解码位图并转成 ≤256px PNG 字节；解不出返回 null（调用方跳过该条，绝不让空白上屏）。</summary>
    private static byte[]? PngBytesFromStream(Stream stream)
    {
        try
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return null;
            BitmapSource frame = decoder.Frames[0];
            if (frame.Format != System.Windows.Media.PixelFormats.Pbgra32)
                frame = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Pbgra32, null, 0);
            if (frame.PixelWidth > 256 || frame.PixelHeight > 256)
            {
                var scale = 256.0 / Math.Max(frame.PixelWidth, frame.PixelHeight);
                frame = new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(scale, scale));
            }
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException
                                        or InvalidOperationException)
        {
            return null;
        }
    }
}
