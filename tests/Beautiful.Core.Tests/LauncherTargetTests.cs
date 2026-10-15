using System;
using Beautiful.Core.Launcher;
using Xunit;

/// <summary>
/// 目标检索键推导取证（v3 T7c）：这一层决定"用户敲 exe 名时搜得到什么"，所以每个分支都断言到具体字符串，
/// 而不只是"非空"。全部为纯函数，不涉及磁盘与 shell。
/// </summary>
public class LauncherTargetTests
{
    // ---------- 普通可执行文件 ----------

    [Theory]
    [InlineData(@"C:\Windows\System32\charmap.exe", "charmap")]
    [InlineData(@"E:\Program Files\Bandizip\Bandizip.EXE", "bandizip")]
    [InlineData(@"C:\Program Files\Everything\Everything.lnk", "everything")]   // 解析失败回交 .lnk 本身
    [InlineData(@"C:\Windows\System32\cmd.exe ", "cmd")]                        // 尾随空白先剥
    [InlineData(@"""C:\Windows\notepad.exe""", "notepad")]                      // 带引号的目标
    [InlineData(@"C:\Apps\Git CMD.exe", "git cmd")]
    [InlineData(@"\\nas\tools\tool", "tool")]                                   // 无扩展名（网络路径）
    public void 目标键取文件名并剥扩展名小写不变(string path, string expected)
        => Assert.Equal(expected, LauncherTarget.SearchKeyOf(path));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空目标给空键而不是抛(string? path)
        => Assert.Equal(string.Empty, LauncherTarget.SearchKeyOf(path));

    [Fact]
    public void 目录目标取最后一段()
        => Assert.Equal("startup", LauncherTarget.SearchKeyOf(@"C:\Users\x\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\"));

    // ---------- 协议/动词目标 ----------

    [Theory]
    [InlineData("ms-settings:", "ms-settings")]
    [InlineData("ms-settings:network", "ms-settings")]
    [InlineData("msteams:", "msteams")]
    public void 协议目标用动词名而不是整串(string path, string expected)
        // 冒号后的参数与整个 URI 都不该进检索键：GetFileName 会把 "ms-settings:network" 原样当文件名，
        // 那个串里带冒号，用户永远敲不中。
        => Assert.Equal(expected, LauncherTarget.SearchKeyOf(path));

    // ---------- AppsFolder（MSIX/UWP 启动形态） ----------

    private const string NotepadAumid = @"Microsoft.WindowsNotepad_8wekyb3d8bbwe!App";

    [Theory]
    [InlineData(@"C:\Windows\explorer.exe", @"shell:AppsFolder\" + NotepadAumid, "notepad")]
    [InlineData(@"C:\Windows\explorer.exe", @"""shell:AppsFolder\" + NotepadAumid + @"""", "notepad")]
    [InlineData(@"C:\Windows\explorer.exe", @" SHELL:APPSFOLDER\" + NotepadAumid + " ", "notepad")]  // 大小写/空白/引号都不影响身份
    [InlineData(@"shell:AppsFolder\" + NotepadAumid, null, "notepad")]                               // 动词直接当路径
    [InlineData("explorer.exe", @"shell:AppsFolder\Microsoft.Paint_8wekyb3d8bbwe!App", "paint")]
    [InlineData("explorer.exe", @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "calculator")]
    public void AppsFolder条目的键是包名词而不是explorer(string path, string? args, string expected)
        // 这是 T7c 的关键分支：若按"文件名去扩展"处理，每条 MSIX 的键都会是 "explorer"——
        // 用户敲 notepad 依然搜不到，而敲 explorer 会命中几十个不相干条目。
        => Assert.Equal(expected, LauncherTarget.SearchKeyOf(path, args));

    [Fact]
    public void 非AppsFolder的参数位不会被误认成MSIX()
    {
        Assert.Null(LauncherTarget.AppsFolderAumidOf(@"C:\a.exe", "-n 1"));
        Assert.Null(LauncherTarget.AppsFolderAumidOf(@"C:\a.exe", null));
        // 恰好等于前缀长度（后面没有 AUMID）也不算一条记录
        Assert.Null(LauncherTarget.AppsFolderAumidOf("explorer.exe", @"shell:AppsFolder\"));
        Assert.Equal("a", LauncherTarget.SearchKeyOf(@"C:\a.exe", "-n 1"));
    }

    [Theory]
    [InlineData(@"Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", "notepad")]
    [InlineData(@"Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "calculator")]
    [InlineData(@"Microsoft.Paint_8wekyb3d8bbwe!App", "paint")]
    [InlineData(@"Microsoft.WindowsTerminal_8wekyb3d8bbwe!App", "terminal")]
    [InlineData(@"Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", "zunemusic")]
    [InlineData(@"Microsoft.Getstarted_8wekyb3d8bbwe!App", "getstarted")]
    [InlineData(@"io.github.clash-verge-rev.clash-verge-rev", "clash-verge-rev")]
    [InlineData(@"Microsoft.Windows.Phone", "phone")]
    [InlineData(@"Microsoft", "microsoft")]          // 只剩发布者词时原样给回，绝不给空串
    [InlineData("notepad", "notepad")]              // 没有点号的裸名
    [InlineData(null, "")]
    [InlineData("", "")]
    public void AumID取词规则(string? aumid, string expected)
        => Assert.Equal(expected ?? string.Empty, LauncherTarget.NameOfAppId(aumid));

    // ---------- 去重身份 ----------

    [Fact]
    public void 同一AUMID的不同写法是同一条()
    {
        // 开始菜单那条 .lnk（解析出 explorer.exe + 参数）与 MSIX 枚举直接拼出来的那条必须合成一条，
        // 否则搜"记事本"会出两行。路径写法、引号、大小写都不该把它们分开。
        var fromLnk = LauncherTarget.IdentityOf(@"C:\Windows\explorer.exe", @"shell:AppsFolder\" + NotepadAumid);
        var fromMsix = LauncherTarget.IdentityOf("explorer.exe", @"""SHELL:APPSFOLDER\microsoft.windowsnotepad_8wekyb3d8bbwe!app""");
        Assert.True(string.Equals(fromLnk, fromMsix, StringComparison.OrdinalIgnoreCase),
                    $"两条写法应合成同一条身份键：{fromLnk} / {fromMsix}");
    }

    [Fact]
    public void 普通条目的身份是路径加参数()
    {
        Assert.NotEqual(
            LauncherTarget.IdentityOf(@"C:\a\tool.exe", ""),
            LauncherTarget.IdentityOf(@"C:\b\tool.exe", ""));
        Assert.NotEqual(                                   // 同 exe 不同参数 = 两个入口，不合并
            LauncherTarget.IdentityOf(@"C:\a\tool.exe", "--app=x"),
            LauncherTarget.IdentityOf(@"C:\a\tool.exe", "--app=y"));
        Assert.Equal(                                      // 引号只是写法差异
            LauncherTarget.IdentityOf(@"""C:\a\tool.exe""", "\" --app=x \""),
            LauncherTarget.IdentityOf(@"C:\a\tool.exe", "--app=x"));
    }
}
