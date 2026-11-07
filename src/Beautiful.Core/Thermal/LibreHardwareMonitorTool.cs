using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.Versioning;

namespace Beautiful.Core.Thermal;

/// <summary>获取便携包的失败分类（<see cref="LibreHardwareMonitorTool.FetchPortableAsync"/> 的结果细目）。</summary>
public enum FetchFailure
{
    None,
    DownloadError,      // 网络不通 / GitHub 不可达 / 超时
    SizeMismatch,       // 下载字节数与官方发布清单不符（中断或被篡改）
    NotAnArchive,       // 不是可解压的 zip
    MissingExecutable,  // 包里没有根级 LibreHardwareMonitor.exe（不是官方便携包的布局）
    UnsafeEntry,        // 包内出现逃逸固定目录的条目（zip-slip，拒绝安装）
    ExtractFailed,      // 解压写盘失败（典型：目标 exe 正在运行被锁）
}

/// <summary>一次获取的结果（<see cref="FetchFailure"/> 为 <see cref="FetchFailure.None"/> 即成功；Message 是可直接上 UI 的中文说明）。</summary>
public sealed record FetchResult(bool Success, FetchFailure Failure, string Message, string? ExePath = null);

/// <summary>启动失败分类（<see cref="LibreHardwareMonitorTool.LaunchElevated"/> 的结果细目）。</summary>
public enum LaunchFailure
{
    None,
    NotInstalled,       // 固定目录没有 exe
    UserDeclinedUac,    // 用户在 UAC 弹窗点了"否"——不是错误，不装也能继续用其他数据源
    Error,              // 其他启动失败
}

/// <summary>一次启动的结果（Message 可直接上 UI）。</summary>
public sealed record LaunchResult(bool Success, LaunchFailure Failure, string Message);

/// <summary>
/// LibreHardwareMonitor 便携包的获取 / 安装 / 启动（应用内引导数据源，温度设置卡调用）。
/// <para>
/// 【为什么钉死 v0.9.4】LibreHardwareMonitor 自 v0.9.5 起删除了 WMI 提供方（其源码树已无 WMI/*.cs，
/// 最新 v0.9.6 亦无）——v0.9.4 是最后一个把传感器发布到 root\LibreHardwareMonitor 命名空间的官方发布，
/// 也是温度探测链第一级认的来源。因此本类不查 releases/latest（会拿到没有 WMI 桥的新版，流程必死），
/// 而是钉死 v0.9.4 的官方下载直链与官方发布清单的字节数，升级版本需要换
/// <see cref="PinnedVersion"/> / <see cref="PortableZipUrl"/> / <see cref="PortableZipExpectedBytes"/> 三常量并重验。
/// </para>
/// <para>
/// 【不静默装任何东西】本类只做"下载 zip → 校验 → 解压到固定目录"。以管理员运行 LibreHardwareMonitor
/// 时它**自己**会装载自带的 WinRing0 内核驱动读硬件寄存器——那是它在 UAC 之后的行为，本应用不代装、
/// 不预装、不解包任何驱动。用户在 UAC 拒绝启动，温度芯片仍可走其他数据源（OHM / ACPI 热区）。
/// </para>
/// <para>
/// 【zip-slip 防护】解压逐条目走 <see cref="SafeEntryTarget"/>：归一化后逃出固定目录的条目一律拒绝，
/// 绝不让包内路径写到固定目录之外。
/// </para>
/// </summary>
public static class LibreHardwareMonitorTool
{
    /// <summary>钉死的官方发布版本（最后一个带 WMI 提供方的版本，见类注释）。</summary>
    public const string PinnedVersion = "v0.9.4";

    /// <summary>官方 GitHub releases 的便携包下载直链（.NET Framework 4.7.2 版，Win10 1803+ 自带运行时）。</summary>
    public const string PortableZipUrl =
        "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases/download/v0.9.4/LibreHardwareMonitor-net472.zip";

    /// <summary>官方发布清单声明的 zip 字节数（2026-11-07 实测下载校验一致；用于完整性比对）。</summary>
    public const long PortableZipExpectedBytes = 1_930_741;

    /// <summary>发布页（打开给用户看来源，下载失败时的手工兜底）。</summary>
    public const string ReleasePageUrl = "https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases/tag/v0.9.4";

    /// <summary>便携包根级主程序条目名（官方包布局：exe 在根级，2026-11-07 实测 24 个条目）。</summary>
    public const string ExecutableEntryName = "LibreHardwareMonitor.exe";

    /// <summary>官方发布直链下载 HttpClient（便携包约 1.9 MB；2026-11-07 本机实测慢网需 2-3 分钟，超时留足余量）。</summary>
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(300) };

    // ---------- 路径收敛（纯函数；Default 用真实 LOCALAPPDATA） ----------

    /// <summary>固定目录三件套：工具根 / 安装目录 / 主程序。全部由 LOCALAPPDATA 根收敛而来。</summary>
    /// <param name="ToolsRoot">%LOCALAPPDATA%/Beautiful/tools/（Beautiful 自有工具的统一根）。</param>
    /// <param name="InstallDir">LibreHardwareMonitor 的解压固定目录。</param>
    /// <param name="ExePath">主程序完整路径（安装态判定与启动目标）。</param>
    public sealed record LhmToolPaths(string ToolsRoot, string InstallDir, string ExePath);

    /// <summary>从 LOCALAPPDATA 根收敛出固定目录（纯函数，单测钉死路径拼接）。</summary>
    public static LhmToolPaths Resolve(string localAppDataRoot)
    {
        var toolsRoot = Path.Combine(localAppDataRoot, "Beautiful", "tools");
        var installDir = Path.Combine(toolsRoot, "LibreHardwareMonitor");
        return new LhmToolPaths(toolsRoot, installDir, Path.Combine(installDir, ExecutableEntryName));
    }

    /// <summary>真实环境的固定目录（%LOCALAPPDATA%\Beautiful\tools\LibreHardwareMonitor）。</summary>
    public static LhmToolPaths DefaultPaths { get; } =
        Resolve(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>便携包是否已就位（固定目录有主程序即视为已获取）。</summary>
    public static bool IsInstalled(LhmToolPaths paths) => File.Exists(paths.ExePath);

    // ---------- zip 校验（纯函数部分） ----------

    /// <summary>下载字节数是否与官方发布清单一致（完整性第一道闸：多一字节少一字节都算被篡改/中断）。</summary>
    public static bool DeclaredSizeMatches(long actualSize) => actualSize == PortableZipExpectedBytes;

    /// <summary>
    /// zip 条目名是否安全（<b>纯函数</b>）：拒绝绝对路径、盘符、".." 逃逸段与空段——
    /// 只放行"相对 + 逐段普通名"的条目（官方包全是这类，实测 24 条目）。
    /// </summary>
    public static bool EntryPathIsSafe(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName)) return false;
        var normalized = entryName.Replace('\\', '/');
        if (normalized.StartsWith('/')) return false;                    // 绝对路径
        var segments = normalized.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0) return false;                       // 空段（// 或结尾 / 由调用方按目录条目另行处理）
            if (segment == "." || segment == "..") return false;         // 相对跳段
            if (segment.EndsWith(':')) return false;                     // 盘符（"C:"）
            if (segment.IndexOf(':') >= 0) return false;                 // NTFS 备用数据流（"name:stream"）
        }
        return true;
    }

    /// <summary>
    /// 条目 → 固定目录内的安全目标路径（<b>纯函数</b>）：先过 <see cref="EntryPathIsSafe"/>，
    /// 再逐段拼进安装目录并做最终归一化前缀校验——归一化后逃出目录的返回 null（调用方拒绝安装）。
    /// 目录条目（以 / 结尾）返回其目录路径；文件条目返回完整文件路径。
    /// </summary>
    public static string? SafeEntryTarget(string installDir, string entryName)
    {
        var normalized = entryName.Replace('\\', '/').TrimEnd('/');
        if (!EntryPathIsSafe(normalized)) return null;
        var target = Path.GetFullPath(Path.Combine(installDir, normalized.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDir));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(target, root, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return target;
    }

    /// <summary>在 zip 条目清单里找根级主程序（官方包布局已知，只认精确名；找不到 = 不是官方便携包）。</summary>
    public static string? FindExecutableEntry(IEnumerable<string> entryNames)
    {
        foreach (var name in entryNames)
            if (string.Equals(name, ExecutableEntryName, StringComparison.OrdinalIgnoreCase))
                return name;
        return null;
    }

    // ---------- 获取（下载 → 校验 → 解压；不打网络的部分独立成 ValidateAndInstall 供单测） ----------

    /// <summary>
    /// 从官方发布直链下载便携包到<b>全新临时目录</b>，校验完整性（字节数 / 可解压 / 含主程序条目），
    /// 解压到固定目录，收尾删临时目录。全程不外抛（失败都进 <see cref="FetchResult"/>），
    /// 也全程不装任何驱动——只放文件。
    /// </summary>
    public static async Task<FetchResult> FetchPortableAsync(LhmToolPaths paths, CancellationToken cancellationToken = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Beautiful-LhmFetch-" + Guid.NewGuid().ToString("N"));
        var tempZip = Path.Combine(tempDir, "LibreHardwareMonitor-net472.zip");
        try
        {
            Directory.CreateDirectory(tempDir);   // 全新临时目录（Guid 名，冲突概率可忽略）
            byte[] zipBytes;
            try
            {
                zipBytes = await Http.GetByteArrayAsync(PortableZipUrl, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException oce) when (oce.InnerException is TimeoutException)
            {
                // HttpClient 超时以 TaskCanceledException（内含 TimeoutException）现形：给用户的是"超时"不是"取消"。
                return new FetchResult(false, FetchFailure.DownloadError,
                    $"下载超时：从 GitHub 官方发布获取 {PinnedVersion} 便携包未能在限定时间内完成，请检查网络后重试。");
            }
            catch (OperationCanceledException)
            {
                return new FetchResult(false, FetchFailure.DownloadError, "下载已取消。");
            }
            catch (Exception ex)
            {
                return new FetchResult(false, FetchFailure.DownloadError,
                    $"下载失败（{ex.GetType().Name}）：无法从 GitHub 官方发布获取 {PinnedVersion} 便携包，请检查网络后重试。");
            }

            File.WriteAllBytes(tempZip, zipBytes);
            return ValidateAndInstall(tempZip, paths);
        }
        finally
        {
            // 临时目录尽力删除：Windows 上句柄释放有延迟，删不掉的残留留在 %TEMP% 不影响结果。
            try { Directory.Delete(tempDir, recursive: true); } catch (Exception) { /* 尽力而为 */ }
        }
    }

    /// <summary>
    /// 校验已落盘的 zip 并安装到固定目录（I/O 但不打网络——单测用本地构造的 zip 走同一条路）：
    /// ①字节数与官方清单一致 ②可解开 ③含根级主程序条目 ④逐条目 zip-slip 校验后覆盖解压
    /// ⑤终验主程序就位。任何一步不过都返回失败原因，不外抛。
    /// <paramref name="enforceDeclaredSize"/> 仅测试关得掉（样本 zip 字节数与官方清单对不上，
    /// 要测的是后面的布局/安全级）；真实下载路径（<see cref="FetchPortableAsync"/>）恒为开。
    /// </summary>
    internal static FetchResult ValidateAndInstall(string zipPath, LhmToolPaths paths, bool enforceDeclaredSize = true)
    {
        long actualSize;
        try
        {
            actualSize = new FileInfo(zipPath).Length;
        }
        catch (Exception ex)
        {
            return new FetchResult(false, FetchFailure.NotAnArchive, $"读取下载文件失败（{ex.GetType().Name}）。");
        }

        if (enforceDeclaredSize && !DeclaredSizeMatches(actualSize))
        {
            return new FetchResult(false, FetchFailure.SizeMismatch,
                $"下载不完整或被篡改：实际 {actualSize.ToString("N0", CultureInfo.InvariantCulture)} 字节，官方发布应为 {PortableZipExpectedBytes.ToString("N0", CultureInfo.InvariantCulture)} 字节。");
        }

        ZipArchive? archive = null;
        try
        {
            try
            {
                archive = ZipFile.OpenRead(zipPath);
            }
            catch (Exception ex)
            {
                return new FetchResult(false, FetchFailure.NotAnArchive, $"下载文件不是有效的 zip（{ex.GetType().Name}）。");
            }

            var entryNames = archive.Entries.Select(e => e.FullName).ToArray();
            if (FindExecutableEntry(entryNames) is null)
            {
                return new FetchResult(false, FetchFailure.MissingExecutable,
                    $"压缩包里没有 {ExecutableEntryName}：不是官方便携包布局，已拒绝安装。");
            }

            try
            {
                Directory.CreateDirectory(paths.InstallDir);
                foreach (var entry in archive.Entries)
                {
                    // 空目录条目（FullName 以 / 结尾）：建目录即可。
                    if (entry.FullName.EndsWith('/'))
                    {
                        if (SafeEntryTarget(paths.InstallDir, entry.FullName) is not { } dir)
                            return UnsafeEntryResult(entry.FullName, paths);
                        Directory.CreateDirectory(dir);
                        continue;
                    }

                    if (SafeEntryTarget(paths.InstallDir, entry.FullName) is not { } target)
                        return UnsafeEntryResult(entry.FullName, paths);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                return new FetchResult(false, FetchFailure.ExtractFailed,
                    $"解压失败（{ex.GetType().Name}）：LibreHardwareMonitor 可能正在运行，请先退出它再重新获取。");
            }

            if (!File.Exists(paths.ExePath))
            {
                return new FetchResult(false, FetchFailure.ExtractFailed, "解压完成但未找到主程序，安装未生效。");
            }

            return new FetchResult(true, FetchFailure.None, $"已解压到 {paths.InstallDir}", paths.ExePath);
        }
        finally
        {
            archive?.Dispose();
        }
    }

    private static FetchResult UnsafeEntryResult(string entryName, LhmToolPaths paths)
        => new(false, FetchFailure.UnsafeEntry,
            $"压缩包含逃逸固定目录的条目「{entryName}」（zip-slip），已拒绝安装：{paths.InstallDir}");

    // ---------- 启动 / 打开目录（Windows 专属：UAC 由用户在系统弹窗确认） ----------

    /// <summary>
    /// 以管理员权限启动已安装的 LibreHardwareMonitor（<c>Verb=runas</c> 弹系统 UAC，由用户确认）。
    /// 用户拒绝不是错误——不装管理员运行也能继续用其他数据源；LibreHardwareMonitor 管理员运行时
    /// 装载自带的 WinRing0 内核驱动是它自己的行为，本方法只负责拉起它。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static LaunchResult LaunchElevated(LhmToolPaths paths)
    {
        if (!File.Exists(paths.ExePath))
            return new LaunchResult(false, LaunchFailure.NotInstalled, "还没有获取 LibreHardwareMonitor，请先点「获取温度数据源」。");

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = paths.ExePath,
                UseShellExecute = true,
                Verb = "runas",                       // 系统弹 UAC；用户拒绝抛 Win32Exception(1223)
                WorkingDirectory = paths.InstallDir,
            };
            using var process = Process.Start(start);
            return new LaunchResult(true, LaunchFailure.None,
                "LibreHardwareMonitor 已启动：它的传感器几秒内经 WMI 发布，状态行稍候刷新为「已连接」。");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return new LaunchResult(false, LaunchFailure.UserDeclinedUac,
                "已在 UAC 拒绝管理员启动：没有关系，温度芯片仍可使用其他数据源（如已装工具的 WMI / ACPI 热区）。");
        }
        catch (Exception ex)
        {
            return new LaunchResult(false, LaunchFailure.Error, $"启动失败（{ex.GetType().Name}）：{ex.Message}");
        }
    }

    /// <summary>在资源管理器里打开固定目录（目录不存在返回 false，由调用方提示先获取）。</summary>
    [SupportedOSPlatform("windows")]
    public static bool OpenInstallDirectory(LhmToolPaths paths)
    {
        if (!Directory.Exists(paths.InstallDir)) return false;
        try
        {
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{paths.InstallDir}\"",
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
