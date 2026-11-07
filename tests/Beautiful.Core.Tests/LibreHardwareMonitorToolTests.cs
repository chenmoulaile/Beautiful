using System.IO.Compression;
using Beautiful.Core.Thermal;
using Xunit;

/// <summary>
/// LibreHardwareMonitor 便携包工具测试：固定目录收敛、zip 条目安全（zip-slip 防护）、
/// 官方布局校验与安装。网络下载不打（FetchPortableAsync 的下载段需要真网），
/// 「校验 → 解压 → 终验」整段走 internal ValidateAndInstall——与真下载共用同一条路径，
/// zip 用本地构造的官方布局样本（对照 2026-11-07 实测官方包：根级 exe + 子目录资源条目）。
/// </summary>
public class LibreHardwareMonitorToolTests : IDisposable
{
    private readonly string _tempRoot;

    public LibreHardwareMonitorToolTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "BeautifulLhmToolTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch (Exception) { /* 尽力清理 */ }
    }

    private LibreHardwareMonitorTool.LhmToolPaths Paths
        => LibreHardwareMonitorTool.Resolve(_tempRoot);

    /// <summary>在临时目录里构造一个 zip 并返回其路径（entryBytes：条目名 → 内容）。</summary>
    private string MakeZip(string zipName, params (string Name, byte[] Bytes)[] entries)
    {
        var zipPath = Path.Combine(_tempRoot, zipName);
        using (var file = File.Create(zipPath))
        using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var stream = entry.Open();
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        return zipPath;
    }

    private static byte[] Bytes(string content) => System.Text.Encoding.UTF8.GetBytes(content);

    // ---------- Resolve：路径收敛 ----------

    [Fact]
    public void 固定目录从LOCALAPPDATA根三层收敛()
    {
        var root = Path.Combine(Path.GetTempPath(), "someLocalAppData");
        var paths = LibreHardwareMonitorTool.Resolve(root);
        Assert.Equal(Path.Combine(root, "Beautiful", "tools"), paths.ToolsRoot);
        Assert.Equal(Path.Combine(paths.ToolsRoot, "LibreHardwareMonitor"), paths.InstallDir);
        Assert.Equal(Path.Combine(paths.InstallDir, "LibreHardwareMonitor.exe"), paths.ExePath);
    }

    [Fact]
    public void 默认路径落在用户LOCALAPPDATA下的Beautiful固定目录()
    {
        var expectedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Beautiful", "tools");
        Assert.Equal(Path.Combine(expectedRoot, "LibreHardwareMonitor"),
            LibreHardwareMonitorTool.DefaultPaths.InstallDir);
    }

    [Fact]
    public void 未安装时IsInstalled为假_主程序落位后为真()
    {
        var paths = Paths;
        Assert.False(LibreHardwareMonitorTool.IsInstalled(paths));
        Directory.CreateDirectory(paths.InstallDir);
        File.WriteAllText(paths.ExePath, "fake exe");
        Assert.True(LibreHardwareMonitorTool.IsInstalled(paths));
    }

    // ---------- EntryPathIsSafe：条目名安全（zip-slip 第一道闸，纯函数） ----------

    [Theory]
    [InlineData("LibreHardwareMonitor.exe", true)]
    [InlineData("de/Microsoft.Win32.TaskScheduler.resources.dll", true)]
    [InlineData("sub/file name (1).dll", true)]          // 官方包外的正常相对名：放行
    [InlineData("../escape.txt", false)]                 // .. 逃逸
    [InlineData("a/../../escape.txt", false)]
    [InlineData("/absolute/path.txt", false)]            // 绝对路径
    [InlineData("\\server/share.txt", false)]            // UNC / 反斜杠绝对
    [InlineData("C:", false)]                            // 盘符
    [InlineData("C:/Windows/evil.dll", false)]
    [InlineData("name:stream.txt", false)]               // NTFS 备用数据流
    [InlineData("", false)]
    [InlineData("a//b.txt", false)]                      // 空段
    [InlineData("./x.txt", false)]                       // 相对当前目录段
    [InlineData("..", false)]
    public void 条目名安全判定(string entryName, bool expected)
        => Assert.Equal(expected, LibreHardwareMonitorTool.EntryPathIsSafe(entryName));

    // ---------- SafeEntryTarget：目标路径收敛（zip-slip 第二道闸，纯函数） ----------

    [Fact]
    public void 安全条目收敛到安装目录内()
    {
        var target = LibreHardwareMonitorTool.SafeEntryTarget(Paths.InstallDir, "de/resources.dll");
        Assert.NotNull(target);
        Assert.StartsWith(Path.GetFullPath(Paths.InstallDir), Path.GetFullPath(target!), StringComparison.Ordinal);
    }

    [Fact]
    public void 目录条目收敛为目录路径()
    {
        var target = LibreHardwareMonitorTool.SafeEntryTarget(Paths.InstallDir, "de/");
        Assert.Equal(Path.Combine(Path.GetFullPath(Paths.InstallDir), "de"), Path.GetFullPath(target!));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("")]
    public void 逃逸条目目标为null(string entryName)
        => Assert.Null(LibreHardwareMonitorTool.SafeEntryTarget(Paths.InstallDir, entryName));

    [Fact]
    public void 安装目录带尾分隔符时安全条目仍收敛()
    {
        // 容错：调用方传带尾分隔符的目录（路径工具的常见形态），不得误拒。
        var withSeparator = Paths.InstallDir + Path.DirectorySeparatorChar;
        Assert.NotNull(LibreHardwareMonitorTool.SafeEntryTarget(withSeparator, "x.txt"));
    }

    // ---------- FindExecutableEntry / DeclaredSizeMatches：官方布局与完整性（纯函数） ----------

    [Fact]
    public void 根级主程序条目被识别_大小写不敏感()
    {
        Assert.Equal("LibreHardwareMonitor.exe",
            LibreHardwareMonitorTool.FindExecutableEntry(["readme.txt", "LibreHardwareMonitor.exe", "de/x.dll"]));
        Assert.Equal("librehardwaremonitor.exe",
            LibreHardwareMonitorTool.FindExecutableEntry(["librehardwaremonitor.exe"]));
        Assert.Null(LibreHardwareMonitorTool.FindExecutableEntry(["sub/LibreHardwareMonitor.exe"]));  // 非根级：不认
        Assert.Null(LibreHardwareMonitorTool.FindExecutableEntry(["LibreHardwareMonitor.exe.config"]));
        Assert.Null(LibreHardwareMonitorTool.FindExecutableEntry([]));
    }

    [Fact]
    public void 字节数与官方清单精确比对()
    {
        Assert.Equal(1_930_741, LibreHardwareMonitorTool.PortableZipExpectedBytes);
        Assert.True(LibreHardwareMonitorTool.DeclaredSizeMatches(1_930_741));
        Assert.False(LibreHardwareMonitorTool.DeclaredSizeMatches(1_930_740));   // 少一字节：中断
        Assert.False(LibreHardwareMonitorTool.DeclaredSizeMatches(1_930_742));   // 多一字节：被篡改
        Assert.False(LibreHardwareMonitorTool.DeclaredSizeMatches(0));
    }

    [Fact]
    public void 钉死版本与官方直链指向v094()
    {
        // 版本钉死是本类存在的理由（v0.9.5+ 官方已删 WMI 提供方）：直链与发布页必须指向 v0.9.4。
        Assert.Equal("v0.9.4", LibreHardwareMonitorTool.PinnedVersion);
        Assert.Contains("/v0.9.4/LibreHardwareMonitor-net472.zip", LibreHardwareMonitorTool.PortableZipUrl);
        Assert.StartsWith("https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/releases/",
            LibreHardwareMonitorTool.PortableZipUrl);
        Assert.Contains("tag/v0.9.4", LibreHardwareMonitorTool.ReleasePageUrl);
    }

    // ---------- ValidateAndInstall：校验 → 解压 → 终验（I/O，不打网络） ----------
    // 样本 zip 字节数与官方清单对不上：布局/安全级测试一律 enforceDeclaredSize: false
    //（真实下载路径恒走大小闸，另测）；大小闸单独由「字节数不符拒绝安装」钉死。

    [Fact]
    public void 官方布局样本安装成功且主程序落位()
    {
        // 对照实测官方包布局：根级 exe + exe.config + 子目录资源 dll。
        var zipPath = MakeZip("portable.zip",
            ("LibreHardwareMonitor.exe", Bytes("fake lhm exe")),
            ("LibreHardwareMonitor.exe.config", Bytes("<configuration/>")),
            ("de/Microsoft.Win32.TaskScheduler.resources.dll", Bytes("satellite")));
        var paths = Paths;

        var result = LibreHardwareMonitorTool.ValidateAndInstall(zipPath, paths, enforceDeclaredSize: false);

        Assert.True(result.Success, result.Message);
        Assert.Equal(FetchFailure.None, result.Failure);
        Assert.True(LibreHardwareMonitorTool.IsInstalled(paths));
        Assert.Equal("fake lhm exe", File.ReadAllText(paths.ExePath));
        Assert.Equal("satellite",
            File.ReadAllText(Path.Combine(paths.InstallDir, "de", "Microsoft.Win32.TaskScheduler.resources.dll")));
    }

    [Fact]
    public void 重装覆盖旧文件()
    {
        var paths = Paths;
        Directory.CreateDirectory(paths.InstallDir);
        File.WriteAllText(paths.ExePath, "old version");
        var zipPath = MakeZip("portable.zip", ("LibreHardwareMonitor.exe", Bytes("new version")));

        var result = LibreHardwareMonitorTool.ValidateAndInstall(zipPath, paths, enforceDeclaredSize: false);

        Assert.True(result.Success, result.Message);
        Assert.Equal("new version", File.ReadAllText(paths.ExePath));
    }

    [Fact]
    public void 字节数不符拒绝安装()
    {
        var zipPath = MakeZip("wrong-size.zip", ("LibreHardwareMonitor.exe", Bytes("x")));   // 大小必然不等于官方清单
        var result = LibreHardwareMonitorTool.ValidateAndInstall(zipPath, Paths);
        Assert.False(result.Success);
        Assert.Equal(FetchFailure.SizeMismatch, result.Failure);
    }

    [Fact]
    public void 不是zip拒绝安装()
    {
        var notZip = Path.Combine(_tempRoot, "not-a-zip.bin");
        File.WriteAllText(notZip, "this is definitely not a zip archive");
        var result = LibreHardwareMonitorTool.ValidateAndInstall(notZip, Paths, enforceDeclaredSize: false);
        Assert.False(result.Success);
        Assert.Equal(FetchFailure.NotAnArchive, result.Failure);
    }

    [Fact]
    public void 缺根级主程序拒绝安装()
    {
        var zipPath = MakeZip("no-exe.zip", ("readme.txt", Bytes("hello")));
        var result = LibreHardwareMonitorTool.ValidateAndInstall(zipPath, Paths, enforceDeclaredSize: false);
        Assert.False(result.Success);
        Assert.Equal(FetchFailure.MissingExecutable, result.Failure);
    }

    [Fact]
    public void zip逃逸条目拒绝安装且目录外无落盘()
    {
        // zip-slip 实包演练：CreateEntry 允许创建含 .. 的条目（2026-11-07 本机 PowerShell 实测），
        // 安装必须在解压前拒绝它，绝不让条目写到固定目录之外。
        var outsideFile = Path.Combine(Path.GetFullPath(Paths.InstallDir), "..", "escape.txt");
        var zipPath = MakeZip("zipslip.zip",
            ("../escape.txt", Bytes("evil")),
            ("LibreHardwareMonitor.exe", Bytes("fake lhm exe")));

        var result = LibreHardwareMonitorTool.ValidateAndInstall(zipPath, Paths, enforceDeclaredSize: false);

        Assert.False(result.Success);
        Assert.Equal(FetchFailure.UnsafeEntry, result.Failure);
        Assert.False(File.Exists(outsideFile), "逃逸条目不得写到固定目录之外");
    }

    [Fact]
    public void 失败后目标目录不残留半成品exe()
    {
        var zipPath = MakeZip("bad-layout.zip", ("readme.txt", Bytes("hello")));
        var result = LibreHardwareMonitorTool.ValidateAndInstall(zipPath, Paths, enforceDeclaredSize: false);
        Assert.False(result.Success);
        Assert.False(LibreHardwareMonitorTool.IsInstalled(Paths));
    }
}
