using System.IO;
using System.Xml.Linq;
using Beautiful.App.Interop;
using Beautiful.App.Services;
using Beautiful.Core.Launcher;
using Windows.Management.Deployment;

namespace Beautiful.App.Launcher;

/// <summary>
/// MSIX / UWP 应用枚举（v3 T7c）：Win11 把记事本、计算器、画图、Store 这类系统应用改成了 MSIX 包，
/// 它们的条目不再落在 Programs 目录里——<b>不枚举它们，启动器在现代机器上就是"搜不到记事本"</b>。
/// 本机实测：139 个包 / 41 条应用条目 / 0 包级失败，记事本·计算器·画图全部只存在于这一路。
/// <para>
/// 【失败形态：只可能"少一批条目"】整个函数体被一层 catch 兜住，失败时写一行日志并返回<b>已经拿到的
/// 那部分</b>（崩得早就是空列表），<b>绝不向上抛</b>；调用方（<see cref="LauncherIndexSource.ScanAllAsync"/>）
/// 照常交回桌面条目。本仓库有一条花血换来的规矩——这台 VM 上 shell/WinRT 调用可能抛
/// <c>AccessViolationException</c>（.NET 8 抓不住，见 THIRD-PARTY-NOTICES.md 与 IconExtractor ④ 层的注释），
/// 所以：① 这里<b>只能在后台线程调用</b>（由 <see cref="LauncherService.PrewarmIndex"/> 的 <c>Task.Run</c>
/// 保证，续接也落回线程池，UI 线程一次 COM/WinRT 都不碰，与 <c>SmtcService</c> 同一套线程纪律）；
/// ② 逐包、逐条目各自 try/catch，坏一个跳过一层而不是废一片；③ 与 <c>IShellItemImageFactory</c> 那条
/// 被证伪的路不同，<see cref="PackageManager"/> 是部署 API 而非 shell 命名空间
/// （<c>Get-AppxPackage</c> 走的就是它），风险等级不同。
/// </para>
/// <para>
/// 【启动方式】<c>explorer.exe shell:AppsFolder\&lt;AUMID&gt;</c>：MSIX 应用没有可直接执行的 exe
/// （WindowsApps 目录对普通用户不可读），AppsFolder 动词是唯一正道；仍走 <c>UseShellExecute</c>
/// 交给 shell，绝不 <c>cmd /c</c>（<see cref="LauncherService.Launch"/> 已统一这一点）。
/// Core 侧认得这个形态，会把检索键取成包名词（<see cref="LauncherTarget.NameOfAppId"/>）而不是 "explorer"。
/// </para>
/// </summary>
public static class MsixIndexSource
{
    /// <summary>
    /// 条目上限：本机 41 条；真出现上千条时宁可截断，也不许把一次索引构建拖成长任务
    /// （上限只在<b>包边界</b>生效，因此最多越界一个包的条目数，代价是可忽略的几条）。
    /// </summary>
    private const int MaxEntries = 512;

    /// <summary>
    /// 枚举当前用户的全部 MSIX 应用条目。<b>本方法不抛</b>：任何异常都只写成一行警告 + 返回已得部分。
    /// </summary>
    public static async Task<IReadOnlyList<LauncherApp>> ScanAsync()
    {
        var apps = new List<LauncherApp>();
        try
        {
            // 空 userSid = "当前用户"（FindPackagesForUser 的文档语义）。这里刻意不去取 SID：
            // 取 SID 要再引一条 Windows.Security / P/Invoke 路径，多一个可能崩的地方，换不到任何好处。
            var packages = new PackageManager().FindPackagesForUser(string.Empty);
            foreach (var package in packages)
            {
                if (apps.Count >= MaxEntries) break;
                apps.AddRange(await ScanPackageAsync(package));
            }
            Logger.Info($"启动器：MSIX 枚举完成，收录 {apps.Count} 条");
            return apps;
        }
        catch (Exception ex)
        {
            // 一行日志 + 已得部分：这是"降级"而不是"故障"，桌面条目不受影响。
            Logger.Warn($"启动器：MSIX 枚举中断（已收录 {apps.Count} 条），其余 UWP 条目本次缺席: {ex.Message}");
            return apps;
        }
    }

    /// <summary>单个包的条目：包本身读不动（安装位置被策略挡、包处于待移除态）就整包跳过。</summary>
    private static async Task<IReadOnlyList<LauncherApp>> ScanPackageAsync(Windows.ApplicationModel.Package package)
    {
        var list = new List<LauncherApp>();
        try
        {
            // 【图标补源（v5 ⑤ 未闭合项）】19041 投影的 AppListEntry 本身只给 AppUserModelId +
            // DisplayInfo（Description/DisplayName），但**包目录本身可读**：WindowsApps 根目录的 ACL
            // 只挡"列根目录"，逐包 InstallLocation 及其中的 AppxManifest.xml / targetsize-*.png
            // 本机实测均可读（2026-11-09 探针：179 包中 111 包命中，微软 Family / 记事本 / 计算器 /
            // 画图 / Store / Terminal 全部拿到 targetsize-256 unplated 资产）。于是读包清单、按 Core
            // 的 AppxLogoResolver 展开变体候选、逐个 File.Exists 取第一个真实存在的资产填进 IconPath
            // ——启动器图标链第②级由此拿到"包内 logo"这一级（见 LauncherService.IconOf）；找不到
            // 维持 null，交回显示名表/首字母色块兜底。任何一步失败都只少一张图标，绝不向上抛。
            // 19041 投影没有 Package.InstallPath（更高 SDK 才进投影），EffectivePath 又是 19041 专属
            // （SupportedOSPlatformVersion=17763 会触发 CA1416）；改用 InstalledLocation.Path——17763 起
            // 就有、与 2026-11-09 探针的 InstallLocation 同源。包读不动时为 null——ResolveLogoPath
            // 对空白 installRoot 一律返回 null，条目照常收录、只少一张图标。
            var installRoot = package.InstalledLocation?.Path;
            var logos = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in await package.GetAppListEntriesAsync())
            {
                try
                {
                    var aumid = entry.AppUserModelId;
                    var name = entry.DisplayInfo?.DisplayName;
                    // AUMID 是启动凭据、名字是显示与检索的主体，缺任何一个这条都没意义。
                    if (string.IsNullOrWhiteSpace(aumid) || string.IsNullOrWhiteSpace(name)) continue;
                    // 同一包的多条 Application 各自解析（AUMID '!' 后段是 Application Id），
                    // 但清单只读一次：结果按 AUMID 记忆，别把磁盘 IO 塞进逐条目循环。
                    if (!logos.TryGetValue(aumid, out var logo))
                    {
                        logo = ResolveLogoPath(installRoot, aumid);
                        logos[aumid] = logo;
                    }
                    list.Add(new LauncherApp(name!.Trim(), ExplorerPath(), AppsFolderArguments(aumid), logo));
                }
                catch { /* 单条目失效（包正在被卸载）：跳过这一条 */ }
            }
            return list;
        }
        catch (Exception ex)
        {
            // 逐包吞掉：一个坏包不许让整批 UWP 条目消失。这里不写 Logger——包级异常在这台 VM 上是常态，
            // 逐包记一行会把日志刷满（SmtcService 的"会话失效是常态竞态而非故障"同一条判断）。
            System.Diagnostics.Debug.WriteLine($"MsixIndexSource: 跳过包: {ex.Message}");
            return [];
        }
    }

    /// <summary>请求的 logo 像素：与启动台的最大请求尺寸对齐（<see cref="IconExtractor.PreferredSize"/> = 256）。</summary>
    private const int LogoRequestPx = IconExtractor.PreferredSize;

    /// <summary>
    /// 解析包内 logo 资产的全路径；读不动/清单缺失/资产不存在<b>一律 null</b>（调用方维持旧兜底链，
    /// 绝不抛上 UI——与 <see cref="ScanAsync"/> 的"只可能少一批条目"同一纪律，这里只是"少一张图标"）。
    /// 顺序：清单 → <see cref="AppxLogoResolver.Candidates"/> 展开变体候选 → 逐个 File.Exists 取第一个。
    /// </summary>
    private static string? ResolveLogoPath(string? installRoot, string aumid)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(installRoot)) return null;
            var manifestPath = Path.Combine(installRoot, "AppxManifest.xml");
            if (!File.Exists(manifestPath)) return null;
            XDocument manifest;
            try
            {
                manifest = XDocument.Load(manifestPath, LoadOptions.None);
            }
            catch
            {
                return null;   // 清单损坏/编码异常/被锁：静默降级
            }
            // AUMID = 包族名!应用 Id：取 '!' 后的段去清单里挑对应 Application（对不上交给
            // 解析器回落到第一个带 logo 声明的 Application，单应用包即全量包的绝大多数）。
            var bang = aumid.IndexOf('!');
            var appId = bang >= 0 ? aumid[(bang + 1)..] : aumid;
            foreach (var candidate in AppxLogoResolver.Candidates(manifest, LogoRequestPx, appId))
            {
                var full = Path.Combine(installRoot, candidate.RelativePath);
                if (File.Exists(full)) return full;
            }
            return null;
        }
        catch
        {
            // installRoot 含非法字符（Path.Combine 抛 ArgumentException）这类极端输入也接住：
            // 一个包的 logo 解析失败不许影响别的条目。
            return null;
        }
    }

    /// <summary>AppsFolder 启动参数（Core 的 <see cref="LauncherTarget"/> 按这个形态认 MSIX 条目并合条）。</summary>
    private static string AppsFolderArguments(string aumid) => $@"shell:AppsFolder\{aumid}";

    /// <summary>
    /// explorer 的路径用 SystemRoot 拼而不是硬写 <c>C:\Windows</c>：Windows 装在别处时也要能启动。
    /// 取不到特殊文件夹时交回裸 <c>explorer.exe</c>——shell 会自己在 PATH 里找到它。
    /// </summary>
    private static string ExplorerPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return string.IsNullOrWhiteSpace(root) ? "explorer.exe" : Path.Combine(root, "explorer.exe");
    }
}
