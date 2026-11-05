using System;
using System.Linq;
using Beautiful.Core.Dock;
using Xunit;

/// <summary>
/// Dock「一格 = 一个应用」合并（<see cref="DockAppGrouper"/>）。
/// 钉住的都是"会画歪"的规则：同 exe 多窗口合一格、固定与运行同 exe 只留一格（explorer 病根）、
/// 最小化仍算运行中、组内保序（最近使用在前）、解析不出 exe 的条目绝不乱并。
/// </summary>
public class DockAppGrouperTests
{
    private static readonly IntPtr H1 = new(0x1001);
    private static readonly IntPtr H2 = new(0x1002);
    private static readonly IntPtr H3 = new(0x1003);
    private static readonly IntPtr H4 = new(0x1004);

    private static DockWindowEntry W(string key, IntPtr h, string title = "", bool min = false)
        => new(key, new DockAppWindow(h, title, min));

    private static DockPinnedEntry P(string key, string path) => new(key, path);

    // ---------- 同 exe 多窗口 ----------

    [Fact]
    public void 同一应用的三扇窗合并成一格()
    {
        var cells = DockAppGrouper.Group(
            Array.Empty<DockPinnedEntry>(),
            new[] { W("chrome", H1, "甲"), W("chrome", H2, "乙"), W("chrome", H3, "丙") });
        var cell = Assert.Single(cells);
        Assert.Equal(3, cell.Windows.Count);
        Assert.True(cell.IsRunning);
    }

    [Fact]
    public void 组内窗口顺序保持入参顺序_首元素即代表窗()
    {
        // 调用方契约 = "最近使用在前"；Core 只保序不重排（没有真 MRU 数据时重排是编故事）
        var cells = DockAppGrouper.Group(null,
            new[] { W("code", H3, "丙"), W("code", H1, "甲"), W("code", H2, "乙") });
        var cell = Assert.Single(cells);
        Assert.Equal(new[] { H3, H1, H2 }, cell.Windows.Select(w => w.Handle));
        Assert.Equal(H3, cell.Head!.Value.Handle);
    }

    [Fact]
    public void 交错的不同应用各自成格且按首窗出现序排()
    {
        var cells = DockAppGrouper.Group(null,
            new[] { W("chrome", H1), W("code", H2), W("chrome", H3), W("typora", H4) });
        Assert.Equal(new[] { "chrome", "code", "typora" }, cells.Select(c => c.AppKey));
        Assert.Equal(2, cells[0].Windows.Count);
        Assert.Equal(new[] { H1, H3 }, cells[0].Windows.Select(w => w.Handle));
    }

    // ---------- 最小化与运行指示 ----------

    [Fact]
    public void 全最小化的应用仍算运行中且代表窗就是那扇最小化窗()
    {
        var cells = DockAppGrouper.Group(null, new[] { W("notepad", H1, "无题", min: true) });
        var cell = Assert.Single(cells);
        Assert.True(cell.IsRunning);   // 点一颗亮着却永不亮的"已最小化指示"才是假指示——最小化也占一格一颗点
        Assert.True(cell.Head!.Value.IsMinimized);
    }

    // ---------- pinned 与 running 同 exe ----------

    [Fact]
    public void 固定与运行同一程序时只留固定那一格()
    {
        // 病根一的最终形态：explorer.exe 固定着、文件管理器又开着 → 真 macOS 是一格带一颗点，不是两格
        var cells = DockAppGrouper.Group(
            new[] { P("explorer", @"C:\Windows\explorer.exe") },
            new[] { W("explorer", H1, "此电脑"), W("explorer", H2, "下载") });
        var cell = Assert.Single(cells);
        Assert.Equal(@"C:\Windows\explorer.exe", cell.PinnedPath);
        Assert.True(cell.IsRunning);
        Assert.Equal(2, cell.Windows.Count);
        Assert.Equal(H1, cell.Head!.Value.Handle);
    }

    [Fact]
    public void 合并键忽略大小写()
    {
        var cells = DockAppGrouper.Group(
            new[] { P("Chrome", @"c:\program files\chrome.exe") },
            new[] { W("chrome", H1) });
        var cell = Assert.Single(cells);
        Assert.NotNull(cell.PinnedPath);
        Assert.True(cell.IsRunning);
    }

    [Fact]
    public void 未固定的运行应用排在固定块之后()
    {
        var cells = DockAppGrouper.Group(
            new[] { P("explorer", "explorer.exe"), P("notepad", "notepad.exe") },
            new[] { W("chrome", H1), W("notepad", H2) });
        Assert.Equal(3, cells.Count);
        Assert.Equal(new[] { "explorer", "notepad", "chrome" }, cells.Select(c => c.AppKey));
        Assert.False(cells[0].IsRunning);        // 纯固定未运行：有格无点
        Assert.True(cells[1].IsRunning);         // 固定且运行：一格一颗点
        Assert.Null(cells[2].PinnedPath);        // 纯运行未固定
    }

    [Fact]
    public void 纯固定未运行的格子没有窗但有路径()
    {
        var cells = DockAppGrouper.Group(new[] { P("code", "code.exe") }, null);
        var cell = Assert.Single(cells);
        Assert.False(cell.IsRunning);
        Assert.Null(cell.Head);
        Assert.Equal("code.exe", cell.PinnedPath);
    }

    // ---------- 不可合并的防御 ----------

    [Fact]
    public void 解析不出exe的窗口各自成格绝不互并()
    {
        // 权限受限进程读不到映像路径 → 空键。误并 = 点 A 的图标切到 B 的窗，比多一格严重得多。
        var cells = DockAppGrouper.Group(null, new[] { W("", H1, "甲"), W("", H2, "乙") });
        Assert.Equal(2, cells.Count);
        Assert.All(cells, c => Assert.True(c.IsRunning));
    }

    [Fact]
    public void 空键固定项不与空键窗口合并也不互并()
    {
        var cells = DockAppGrouper.Group(
            new[] { P("", "ms-its:X.chm"), P("", "ms-its:Y.chm") },
            new[] { W("", H1) });
        Assert.Equal(3, cells.Count);
        Assert.DoesNotContain(cells, c => c.PinnedPath is not null && c.IsRunning);
    }

    [Fact]
    public void 同一键的两个固定格窗口只归第一格()
    {
        // dock.json 历史残留（同程序两种写法都钉了）：加载去重修新账，这里防旧账——
        // 两个固定格都保留（绝不静默删用户条目），但窗只归第一格，点哪格都切得到。
        var cells = DockAppGrouper.Group(
            new[] { P("explorer", "explorer.exe"), P("explorer", "File Explorer.lnk") },
            new[] { W("explorer", H1) });
        Assert.Equal(2, cells.Count);
        Assert.True(cells[0].IsRunning);
        Assert.False(cells[1].IsRunning);
    }

    [Fact]
    public void 重复句柄只认首次()
    {
        // EnumWindows 对账与 ShellHook 增报可能同一帧到两次（与切换器 WindowListMerger 同一纪律）
        var cells = DockAppGrouper.Group(null, new[] { W("chrome", H1, "甲"), W("chrome", H1, "甲重报"), W("chrome", H2, "乙") });
        var cell = Assert.Single(cells);
        Assert.Equal(2, cell.Windows.Count);
    }

    [Fact]
    public void null输入按空格子表处理()
    {
        Assert.Empty(DockAppGrouper.Group(null, null));
        Assert.Empty(DockAppGrouper.Group(Array.Empty<DockPinnedEntry>(), Array.Empty<DockWindowEntry>()));
    }

    // ---------- 点击选窗裁定 ----------

    [Fact]
    public void 应用未激活时点击切到代表窗()
    {
        var wins = new[] { new DockAppWindow(H1, "最近", false), new DockAppWindow(H2, "较早", false) };
        Assert.Equal(H1, DockAppGrouper.PickWindowForActivate(wins, H3)!.Value.Handle);
    }

    [Fact]
    public void 前台是同组别的窗时也切回代表窗()
    {
        var wins = new[] { new DockAppWindow(H1, "最近", false), new DockAppWindow(H2, "在前台", false) };
        Assert.Equal(H1, DockAppGrouper.PickWindowForActivate(wins, H2)!.Value.Handle);
    }

    [Fact]
    public void 前台正是代表窗且多窗时切到组内下一扇非最小化窗()
    {
        // macOS 在这里开 Exposé——我们没有那个能力，绝不假装；"切到下一扇"是真实存在的动作。
        var wins = new[]
        {
            new DockAppWindow(H1, "在前台", false),
            new DockAppWindow(H2, "全最小化", true),
            new DockAppWindow(H3, "可见的下一扇", false),
        };
        Assert.Equal(H3, DockAppGrouper.PickWindowForActivate(wins, H1)!.Value.Handle);
    }

    [Fact]
    public void 前台正是代表窗且其余全最小化时还原最近的最小化窗()
    {
        var wins = new[] { new DockAppWindow(H1, "在前台", false), new DockAppWindow(H2, "最小化", true) };
        Assert.Equal(H2, DockAppGrouper.PickWindowForActivate(wins, H1)!.Value.Handle);
    }

    [Fact]
    public void 单窗应用在前台时点它即隐藏_裁定返回它自己()
    {
        // 调用方（ToggleWindowItem）据此走"点前台图标 = 最小化"的既有语义（v3.9 修的行为，不许退化）
        var wins = new[] { new DockAppWindow(H1, "唯一", false) };
        Assert.Equal(H1, DockAppGrouper.PickWindowForActivate(wins, H1)!.Value.Handle);
    }

    [Fact]
    public void 空窗口列表裁定返回null_纯固定走启动()
    {
        Assert.Null(DockAppGrouper.PickWindowForActivate(Array.Empty<DockAppWindow>(), H1));
        Assert.Null(DockAppGrouper.PickWindowForActivate(null, H1));
    }
}
