using System;
using System.Collections.Generic;
using System.Linq;
using Beautiful.Core.Launcher;
using Xunit;

/// <summary>
/// 启动器检索层取证（v3 T7）：去重键、档次排序、并列次序、空查询语义、健壮性。
/// 这一层的契约是"同样的输入同样的次序"，所以既断言**谁在第一**，也断言**分数为什么是这一档**。
/// </summary>
public class LauncherIndexTests
{
    private static LauncherApp App(string name, string? path = null, string args = "")
        => new(name, path ?? $@"C:\Apps\{name}.exe", args);

    // ---------- Build：规范化与去重 ----------

    [Fact]
    public void 名称末尾的lnk被剥掉而启动路径保持完整()
    {
        var index = LauncherIndex.Build([App("计算器.lnk", @"C:\Windows\System32\calc.exe")]);
        var hit = Assert.Single(index.Search("计算器", 10));
        Assert.Equal("计算器", hit.App.Name);
        Assert.Equal(@"C:\Windows\System32\calc.exe", hit.App.LaunchPath);
    }

    [Fact]
    public void 空名称与空路径的条目被丢弃()
    {
        var index = LauncherIndex.Build(
        [
            App("  "),                       // 名称为空（剥完 .lnk 也空）
            new("记事本", "   "),            // 启动路径为空 → 无法启动
            App("画图"),
        ]);
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void 同一目标的重复快捷方式只留一条()
    {
        var index = LauncherIndex.Build(
        [
            App("Edge", @"C:\Edge\msedge.exe"),
            App("Microsoft Edge", @"c:\edge\MSEDGE.EXE"),   // 路径大小写不同 = 同一个目标
        ]);
        Assert.Equal(1, index.Count);
        // 留的是"第一条"：开始菜单扫描的用户区在前、机器区在后，用户自己的命名优先
        Assert.Equal("Edge", index.Search("edge", 10)[0].App.Name);
    }

    [Fact]
    public void 同目标不同参数不算重复()
    {
        // "Edge 应用模式"这类快捷方式靠参数区分身份，合并成一个就等于丢掉一个入口
        var index = LauncherIndex.Build(
        [
            App("Edge", @"C:\Edge\msedge.exe"),
            App("Edge（网页应用）", @"C:\Edge\msedge.exe", "--app=https://x"),
        ]);
        Assert.Equal(2, index.Count);
    }

    [Fact]
    public void 入表次序按名称且与区域无关()
    {
        var names = new[] { "浏览器", "Alpha", "计算器", "alpha2" };
        var index = LauncherIndex.Build(names.Select(n => App(n)));
        var listed = index.All.Select(a => a.Name).ToArray();
        // OrdinalIgnoreCase 序：ASCII 先于汉字，"Alpha" 在 "alpha2" 前；两个汉字按码位排（浏 U+6D4F < 计 U+8BA1），
        // 关键是这条序**不随 ICU/区域设置变**——中文系统上"按拼音排"会让同一个索引排出两种次序。
        Assert.Equal(["Alpha", "alpha2", "浏览器", "计算器"], listed);
    }

    // ---------- Search：档次与次序 ----------

    [Fact]
    public void 整名前缀优于首字母命中()
    {
        var index = LauncherIndex.Build(
        [
            App("计算器", @"C:\Windows\System32\calc.exe"),   // 首字母 jsq
            App("jsq", @"C:\jsq.exe"),                        // 名称就以查询开头
        ]);
        var hits = index.Search("js", 10);
        Assert.Equal("jsq", hits[0].App.Name);
        Assert.Equal(LauncherIndex.ScorePrefix, hits[0].Score);
        Assert.Equal(LauncherIndex.ScoreInitialsPrefix, hits[1].Score);
    }

    [Fact]
    public void 完全相等排最前()
    {
        var index = LauncherIndex.Build([App("cmd"), App("命令提示符 cmd")]);
        var hits = index.Search("cmd", 10);
        Assert.Equal("cmd", hits[0].App.Name);
        Assert.Equal(LauncherIndex.ScoreExact, hits[0].Score);
        Assert.Equal(LauncherIndex.ScoreWordPrefix, hits[1].Score);
    }

    [Fact]
    public void 拼音首字母能命中中文应用名()
    {
        var index = LauncherIndex.Build([App("计算器", @"C:\Windows\System32\calc.exe")]);
        var hit = Assert.Single(index.Search("jsq", 10));
        Assert.Equal(LauncherIndex.ScoreInitialsPrefix, hit.Score);
        // 带空格同样命中（用户从 Spotlight 抄来的习惯）
        Assert.NotEmpty(index.Search("j sq", 10));
        // 只打两个字母也命中前缀档
        Assert.Equal(LauncherIndex.ScoreInitialsPrefix, Assert.Single(index.Search("js", 10)).Score);
    }

    [Fact]
    public void 整名子串命中低于词首与前缀()
    {
        var index = LauncherIndex.Build([App("Code - text editor"), App("vscode")]);
        var hits = index.Search("code", 10);
        Assert.Equal("Code - text editor", hits[0].App.Name);        // 整名前缀
        Assert.Equal(LauncherIndex.ScorePrefix, hits[0].Score);
        Assert.Equal("vscode", hits[1].App.Name);
        Assert.Equal(LauncherIndex.ScoreContains, hits[1].Score);    // 只在中间出现
    }

    [Fact]
    public void 词首命中拿词首档()
    {
        var index = LauncherIndex.Build([App("Task Manager"), App("Notepad")]);
        var hit = Assert.Single(index.Search("manager", 10));
        Assert.Equal("Task Manager", hit.App.Name);
        Assert.Equal(LauncherIndex.ScoreWordPrefix, hit.Score);
    }

    [Fact]
    public void 首字母前缀档高于首字母子串档()
    {
        var index = LauncherIndex.Build([App("任务管理器"), App("管理器")]);
        var hits = index.Search("gl", 10);
        Assert.Equal(2, hits.Count);
        Assert.Equal("管理器", hits[0].App.Name);                    // gl 就是它的首字母开头
        Assert.Equal(LauncherIndex.ScoreInitialsPrefix, hits[0].Score);
        Assert.Equal(LauncherIndex.ScoreInitialsContains, hits[1].Score);   // rwglq 里 gl 在中间
    }

    [Fact]
    public void 同分时名称短的在前()
    {
        var index = LauncherIndex.Build([App("命令提示符（管理员）"), App("命令提示符")]);
        var hits = index.Search("命令", 10);
        Assert.Equal("命令提示符", hits[0].App.Name);
    }

    [Fact]
    public void 同分同长时按名称与路径的Ordinal序()
    {
        var index = LauncherIndex.Build(
        [
            App("ab", @"C:\z\ab.exe"),
            App("ab", @"C:\a\ab.exe"),
            App("AB", @"C:\b\ab.exe"),
        ]);
        var hits = index.Search("ab", 10);
        // 完全相等（忽略大小写）后比 Ordinal：大写 ASCII 码位在小写之前 → "AB"，再比路径 → C:\a 先
        Assert.Equal([@"C:\b\ab.exe", @"C:\a\ab.exe", @"C:\z\ab.exe"], hits.Select(h => h.App.LaunchPath).ToArray());
    }

    [Fact]
    public void 次序可重复()
    {
        var apps = new[] { "计算器", "记事本", "画图", "cmd", "Edge", "资源管理器" }
            .Select(n => App(n)).ToArray();
        var first = LauncherIndex.Build(apps);
        foreach (var query in new[] { "", "j", "js", "e", "资源", "q" })
        {
            var a = first.Search(query, 10).Select(h => h.App.Name).ToArray();
            var b = LauncherIndex.Build(apps).Search(query, 10).Select(h => h.App.Name).ToArray();
            Assert.Equal(a, b);
        }
    }

    // ---------- 目标字段（v3 T7c：用户敲的是 exe 名，不是显示名） ----------

    [Fact]
    public void 显示名不含查询而目标含时依然搜得到()
    {
        var index = LauncherIndex.Build([App("Character Map", @"C:\Windows\System32\charmap.exe")]);
        var hit = Assert.Single(index.Search("charmap", 10));   // "character map" 里没有 "charmap" 一个字
        Assert.Equal("Character Map", hit.App.Name);
        Assert.Equal(LauncherIndex.ScoreTargetExact, hit.Score);
    }

    [Fact]
    public void 敲cmd时cmdexe排第一其余按档次在后()
    {
        var index = LauncherIndex.Build(
        [
            App("命令提示符", @"C:\Windows\System32\cmd.exe"),      // 目标完全相等
            App("Git CMD", @"E:\Git\git-cmd.exe"),                 // 名称词首 600（目标词首 570 取高者）
            App("Command Prompt", @"C:\Windows\System32\cmd.exe"), // 同目标 → 与"命令提示符"合成一条
        ]);
        var hits = index.Search("cmd", 10);
        Assert.Equal(2, hits.Count);                              // 同一 cmd.exe 的两条快捷方式只留一条
        Assert.Equal("命令提示符", hits[0].App.Name);              // 用户区/第一条命名优先
        Assert.Equal(LauncherIndex.ScoreTargetExact, hits[0].Score);
        Assert.Equal("Git CMD", hits[1].App.Name);
        Assert.Equal(LauncherIndex.ScoreWordPrefix, hits[1].Score);
    }

    [Fact]
    public void 名称完全相等仍压过目标完全相等()
    {
        var index = LauncherIndex.Build(
        [
            App("命令提示符", @"C:\Windows\System32\cmd.exe"),      // 目标完全相等 900
            App("cmd", @"C:\Tools\cmd.exe"),                       // 名称完全相等 1000
        ]);
        var hits = index.Search("cmd", 10);
        Assert.Equal(["cmd", "命令提示符"], hits.Select(h => h.App.Name).ToArray());
        Assert.Equal(LauncherIndex.ScoreExact, hits[0].Score);
        Assert.Equal(LauncherIndex.ScoreTargetExact, hits[1].Score);
    }

    [Fact]
    public void 名称前缀压过目标前缀()
    {
        var index = LauncherIndex.Build(
        [
            App("cmdx", @"C:\Tools\other.exe"),   // 名称前缀 700
            App("备用", @"C:\Tools\cmdkit.exe"),   // 目标前缀 680
        ]);
        var hits = index.Search("cmd", 10);
        Assert.Equal(["cmdx", "备用"], hits.Select(h => h.App.Name).ToArray());
        Assert.Equal(LauncherIndex.ScorePrefix, hits[0].Score);
        Assert.Equal(LauncherIndex.ScoreTargetPrefix, hits[1].Score);
    }

    [Fact]
    public void 目标中间子串拿目标子串档()
    {
        var index = LauncherIndex.Build([App("画图", @"C:\Windows\System32\mspaint.exe")]);
        var hit = Assert.Single(index.Search("paint", 10));   // mspaint 里 paint 在中间：不是前缀
        Assert.Equal(LauncherIndex.ScoreTargetContains, hit.Score);
    }

    [Fact]
    public void 拼音与目标同时命中同一条时取最高档()
    {
        var index = LauncherIndex.Build([App("计算器", @"C:\Windows\System32\calc.exe")]);
        Assert.Equal(LauncherIndex.ScoreInitialsPrefix, Assert.Single(index.Search("jsq", 10)).Score);
        Assert.Equal(LauncherIndex.ScoreTargetExact, Assert.Single(index.Search("calc", 10)).Score);
        // 两条不同的路都到同一条记录：这正是"中文名 + 英文 exe 名"两种输入习惯都要顾到的目的
        Assert.Equal("计算器", Assert.Single(index.Search("calc", 10)).App.Name);
    }

    [Fact]
    public void MSIX条目按包名词可搜并压过名称前缀()
    {
        var notepadMsix = new LauncherApp("记事本", @"C:\Windows\explorer.exe",
            @"shell:AppsFolder\Microsoft.WindowsNotepad_8wekyb3d8bbwe!App");
        var index = LauncherIndex.Build([notepadMsix, App("Notepad++", @"C:\Notepad++\notepad++.exe")]);
        var hits = index.Search("notepad", 10);
        // 记事本（目标完全相等 900）在 Notepad++（名称前缀 700）之前：敲 notepad 要的是系统记事本，
        // 而"名字里以 notepad 开头"这件事并不比"它的 exe 就叫 notepad"更直接。
        Assert.Equal("记事本", hits[0].App.Name);
        Assert.Equal(LauncherIndex.ScoreTargetExact, hits[0].Score);
        Assert.Equal(LauncherIndex.ScorePrefix, hits[1].Score);
        // 中文显示名同样可搜（目标字段没有把名称那一路挤掉）
        Assert.Equal("记事本", Assert.Single(index.Search("记事本", 10)).App.Name);
    }

    [Fact]
    public void 快捷方式与MSIX枚举的同一AppsFolder条目只留一条()
    {
        // 开始菜单里那条 .lnk 解析出来就是 explorer.exe + AppsFolder 参数；MSIX 枚举又给一条。
        // 两条都留着 = 搜"记事本"出现两行，且第二行图标不同。身份键按 AUMID 收敛（LauncherTarget.IdentityOf）。
        var fromLnk = new LauncherApp("记事本", @"C:\Windows\explorer.exe",
            @"shell:AppsFolder\Microsoft.WindowsNotepad_8wekyb3d8bbwe!App");
        var fromMsix = new LauncherApp("Notepad", "explorer.exe",
            @"""shell:AppsFolder\MICROSOFT.WINDOWSNOTEPAD_8WEKYB3D8BBWE!APP""");
        var index = LauncherIndex.Build([fromLnk, fromMsix]);
        var hit = Assert.Single(index.All);
        Assert.Equal("记事本", hit.Name);   // 留先出现的那条（扫描顺序：桌面在前）
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    public void 空白查询不打分也不抛(string? query)
    {
        var index = LauncherIndex.Build([App("Command Prompt", @"C:\System32\cmd.exe")]);
        // 检索键里可以合法含空格（"command prompt"），所以纯空白查询必须是"不检索"而不是"全都子串命中"
        Assert.All(index.Search(query, 10), h => Assert.Equal(0, h.Score));
        if (query is not null) Assert.Equal(0, LauncherIndex.Score(query, App("Command Prompt")));
    }

    // ---------- 空查询与边界 ----------

    [Fact]
    public void 空查询给索引前若干条而不是空()
    {
        var index = LauncherIndex.Build([App("Alpha"), App("Beta"), App("Gamma")]);
        var hits = index.Search(null, 2);
        Assert.Equal(["Alpha", "Beta"], hits.Select(h => h.App.Name).ToArray());
        Assert.All(hits, h => Assert.Equal(0, h.Score));   // 空查询不打分：它不是"命中"
    }

    [Fact]
    public void limit被尊重()
    {
        var index = LauncherIndex.Build(Enumerable.Range(1, 50).Select(i => App("app" + i)));
        Assert.Equal(8, index.Search("app", 8).Count);
        Assert.Empty(index.Search("app", 0));
    }

    [Fact]
    public void 空索引与查不到都是空结果不抛()
    {
        Assert.Empty(LauncherIndex.Empty.Search("计算器", 10));
        var index = LauncherIndex.Build([App("记事本")]);
        Assert.Empty(index.Search("zzzzz", 10));
    }

    [Fact]
    public void 单条打分可被外部复用()
        => Assert.Equal(LauncherIndex.ScoreExact, LauncherIndex.Score("记事本", App("记事本")));
}
