using System;
using System.Collections.Generic;
using System.Linq;
using Beautiful.Core.Dock;
using Xunit;

/// <summary>
/// Dock 右键菜单的决策内核（<see cref="DockContextMenuPlan"/>）。
/// 用户的不满原话是"右键只有固定到 dock 栏的选项，没有其他 windows 自带或 mac os 自带右键菜单选项，这不还原"——
/// 这些用例钉的就是"还原度"：macOS Dock + Windows 跳转列表该出现的条目、顺序、以及**灰项必须带可见原因**。
/// </summary>
public class DockContextMenuPlanTests
{
    private static readonly IntPtr H1 = new(0x1001);
    private static readonly IntPtr H2 = new(0x1002);
    private static readonly IntPtr H3 = new(0x1003);

    private static DockWindowSnap Win(IntPtr h, string title, uint pid = 42, string name = "notepad",
                                      bool min = false, bool max = false)
        => new(h, title, pid, name, min, max);

    private static DockContextMenuInput Pinned(IReadOnlyList<DockWindowSnap>? windows = null,
                                               bool hasExe = true, bool exists = true, IntPtr fg = default,
                                               bool props = true)
        => new()
        {
            Kind = DockItemKind.Pinned,
            LaunchPath = @"C:\Windows\notepad.exe",
            LaunchPathExists = exists,
            HasExecutableTarget = hasExe,
            IsPinned = true,
            Windows = windows ?? Array.Empty<DockWindowSnap>(),
            ForegroundHandle = fg,
            PropertiesPageAvailable = props
        };

    private static DockContextMenuInput WindowItem(IntPtr self, IReadOnlyList<DockWindowSnap> siblings,
                                                   IntPtr fg = default, bool pinned = false)
        => new()
        {
            Kind = DockItemKind.Window,
            LaunchPath = @"C:\Windows\notepad.exe",
            LaunchPathExists = true,
            HasExecutableTarget = true,
            IsPinned = pinned,
            Windows = siblings,
            ThisWindowHandle = self,
            ForegroundHandle = fg
        };

    private static string[] Labels(IReadOnlyList<DockContextMenuEntry> entries) =>
        entries.Select(e => e.IsSeparator ? "─" : e.Label).ToArray();

    // ---------- 不弹菜单的种类 ----------

    [Fact]
    public void 分隔符与媒体胶囊不产生任何条目()
    {
        foreach (var kind in new[] { DockItemKind.Separator, DockItemKind.Media })
            Assert.Empty(DockContextMenuPlan.Build(new DockContextMenuInput { Kind = kind }));
    }

    // ---------- 固定应用：未运行 ----------

    [Fact]
    public void 固定未运行的条目顺序_且没有窗口段与结束任务()
    {
        var plan = DockContextMenuPlan.Build(Pinned());
        Assert.Equal(new[] { "打开", "打开新窗口", "─", "取消固定", "─",
                             "以管理员身份运行", "打开文件位置", "属性…" }, Labels(plan));
        Assert.DoesNotContain(plan, e => e.Action == DockContextAction.ForceKill);
        Assert.DoesNotContain(plan, e => e.Action == DockContextAction.WindowList);
    }

    [Fact]
    public void 没有窗口就没有窗口子菜单()
    {
        var plan = DockContextMenuPlan.Build(Pinned(windows: Array.Empty<DockWindowSnap>()));
        Assert.Null(plan.SingleOrDefault(e => e.Action == DockContextAction.WindowList));
    }

    [Fact]
    public void 目标不是可执行文件时以管理员身份运行置灰并给出原因()
    {
        var plan = DockContextMenuPlan.Build(Pinned(hasExe: false));
        var runas = plan.Single(e => e.Action == DockContextAction.RunAsAdmin);
        Assert.False(runas.IsEnabled);
        Assert.Equal("目标不是可执行文件", runas.DisabledReason);
    }

    [Fact]
    public void 启动目标文件不存在时文件位置与属性都置灰带原因()
    {
        var plan = DockContextMenuPlan.Build(Pinned(exists: false));
        foreach (var a in new[] { DockContextAction.OpenFileLocation, DockContextAction.Properties })
        {
            var e = plan.Single(x => x.Action == a);
            Assert.False(e.IsEnabled);
            Assert.Equal("启动目标文件不存在", e.DisabledReason);
        }
    }

    [Fact]
    public void 本机打不开属性页时整格不给而不是留一个死入口()
    {
        // Win11 24H2 对裸 .exe 的 properties 动词返回 1155：这一格做不到就**不出现**，
        // 而不是灰着占位、更不是点了没反应（用户原话要的是"还原"，不是"看起来多"）。
        var plan = DockContextMenuPlan.Build(Pinned(props: false));
        Assert.DoesNotContain(plan, e => e.Action == DockContextAction.Properties);
        Assert.Contains(plan, e => e.Action == DockContextAction.OpenFileLocation);
    }

    // ---------- 固定应用：运行中 ----------

    [Fact]
    public void 固定运行中追加窗口段与末尾的结束任务()
    {
        var plan = DockContextMenuPlan.Build(Pinned(new[] { Win(H1, "a.txt — 记事本"), Win(H2, "b.txt — 记事本") }));
        Assert.Equal(new[] { "打开", "打开新窗口", "─", "窗口", "显示所有窗口", "关闭窗口", "退出", "─",
                             "取消固定", "─", "以管理员身份运行", "打开文件位置", "属性…", "─", "结束任务" },
                     Labels(plan));
        Assert.True(plan.Single(e => e.Action == DockContextAction.ForceKill).RequiresConfirmation);
    }

    [Fact]
    public void 多窗口应用的窗口子菜单每窗一行且按顺序()
    {
        var plan = DockContextMenuPlan.Build(Pinned(new[] { Win(H1, "第一扇窗"), Win(H2, "第二扇窗"), Win(H3, "第三扇窗") }));
        var submenu = plan.Single(e => e.Action == DockContextAction.WindowList).Children!;
        Assert.Equal(new[] { "第一扇窗", "第二扇窗", "第三扇窗" }, submenu.Select(e => e.Label));
        Assert.All(submenu, e => Assert.Equal(DockContextAction.SwitchToWindow, e.Action));
        Assert.Equal(new[] { H1, H2, H3 }, submenu.Select(e => e.WindowHandle));
    }

    [Fact]
    public void 窗口子菜单里前台那一格打勾并置灰()
    {
        var plan = DockContextMenuPlan.Build(Pinned(new[] { Win(H1, "甲"), Win(H2, "乙") }, fg: H2));
        var submenu = plan.Single(e => e.Action == DockContextAction.WindowList).Children!;
        Assert.False(submenu[0].IsChecked);
        Assert.True(submenu[1].IsChecked);
        Assert.False(submenu[1].IsEnabled);
        Assert.Equal("已在前台", submenu[1].DisabledReason);
    }

    [Fact]
    public void 结束任务优先点名前台窗口的进程而不是第一个()
    {
        var plan = DockContextMenuPlan.Build(Pinned(
            new[] { Win(H1, "甲", 11, "edge"), Win(H2, "乙", 22, "code") }, fg: H2));
        var kill = plan.Single(e => e.Action == DockContextAction.ForceKill);
        Assert.Equal(22u, kill.ProcessId);
        Assert.Equal("code", kill.ProcessName);
        Assert.Contains("code", kill.ConfirmLabel);
    }

    // ---------- 运行窗口条目 ----------

    [Fact]
    public void 窗口条目顺序_单窗口时关闭所有窗口置灰()
    {
        var plan = DockContextMenuPlan.Build(WindowItem(H1, new[] { Win(H1, "文档") }));
        Assert.Equal(new[] { "切到该窗口", "最小化", "最大化", "─", "关闭窗口", "关闭所有窗口", "─",
                             "固定到 Dock", "─", "结束任务" }, Labels(plan));
        var closeAll = plan.Single(e => e.Action == DockContextAction.CloseAllWindows);
        Assert.False(closeAll.IsEnabled);
        Assert.Equal("该应用只有一个窗口", closeAll.DisabledReason);
    }

    [Fact]
    public void 窗口条目多窗口时关闭所有窗口可用且已固定则改为取消固定()
    {
        var plan = DockContextMenuPlan.Build(WindowItem(H1, new[] { Win(H1, "甲"), Win(H2, "乙") }, pinned: true));
        Assert.True(plan.Single(e => e.Action == DockContextAction.CloseAllWindows).IsEnabled);
        Assert.Equal("取消固定", plan.Single(e => e.Action == DockContextAction.Unpin).Label);
        Assert.DoesNotContain(plan, e => e.Action == DockContextAction.Pin);
    }

    [Fact]
    public void 最小化随状态置灰_最大化标签随状态取反()
    {
        var minimized = DockContextMenuPlan.Build(WindowItem(H1, new[] { Win(H1, "甲", min: true) }));
        var min = minimized.Single(e => e.Action == DockContextAction.Minimize);
        Assert.False(min.IsEnabled);
        Assert.Equal("该窗口已最小化", min.DisabledReason);
        // 已最小化的窗口谈不上"再最大化"，但那一格仍在，标签按当前状态给"最大化"
        Assert.Equal("最大化", minimized.Single(e => e.Action == DockContextAction.MaximizeOrRestore).Label);

        var maximized = DockContextMenuPlan.Build(WindowItem(H1, new[] { Win(H1, "甲", max: true) }));
        Assert.Equal("还原", maximized.Single(e => e.Action == DockContextAction.MaximizeOrRestore).Label);
    }

    [Fact]
    public void 已在前台的窗口_切到该窗口置灰带原因()
    {
        var plan = DockContextMenuPlan.Build(WindowItem(H1, new[] { Win(H1, "甲" ) }, fg: H1));
        var sw = plan.Single(e => e.Action == DockContextAction.SwitchToWindow);
        Assert.False(sw.IsEnabled);
        Assert.Equal("该窗口已在前台", sw.DisabledReason);
    }

    [Fact]
    public void 前台未知时不把切到该窗口误置灰()
    {
        var plan = DockContextMenuPlan.Build(WindowItem(H1, new[] { Win(H1, "甲") }, fg: IntPtr.Zero));
        Assert.True(plan.Single(e => e.Action == DockContextAction.SwitchToWindow).IsEnabled);
    }

    // ---------- 结束任务的安全裁定 ----------

    [Theory]
    [InlineData("csrss")]
    [InlineData("wininit")]
    [InlineData("winlogon")]
    [InlineData("services")]
    [InlineData("lsass")]
    [InlineData("svchost")]
    [InlineData("dwm")]
    [InlineData("explorer")]
    [InlineData("System")]
    [InlineData("Memory Compression")]
    [InlineData("EXPLORER.EXE")]
    public void 系统关键进程一律禁止结束(string name)
    {
        Assert.True(DockContextMenuPlan.IsKillProtected(name));
        var plan = DockContextMenuPlan.Build(WindowItem(H1, new[] { Win(H1, "此电脑", 4, name) }));
        var kill = plan.Single(e => e.Action == DockContextAction.ForceKill);
        Assert.False(kill.IsEnabled);
        // 原因里点的是归一化后的进程名（EXPLORER.EXE 与 explorer 是同一个进程，文案不该带扩展名）
        var expected = (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name).ToLowerInvariant();
        Assert.Contains(expected, kill.DisabledReason!.ToLowerInvariant());
    }

    [Fact]
    public void 取不到进程时不给一把刀_置灰说明原因()
    {
        var plan = DockContextMenuPlan.Build(WindowItem(H1, new[] { Win(H1, "甲", 0, "") }));
        var kill = plan.Single(e => e.Action == DockContextAction.ForceKill);
        Assert.False(kill.IsEnabled);
        Assert.Equal("无法确定所属进程", kill.DisabledReason);
    }

    [Fact]
    public void 普通进程可结束但必须二次确认且名单外的进程不被误伤()
    {
        Assert.False(DockContextMenuPlan.IsKillProtected("notepad"));
        Assert.False(DockContextMenuPlan.IsKillProtected(null));
        var plan = DockContextMenuPlan.Build(WindowItem(H1, new[] { Win(H1, "甲", 7, "notepad") }));
        var kill = plan.Single(e => e.Action == DockContextAction.ForceKill);
        Assert.True(kill.IsEnabled);
        Assert.True(kill.RequiresConfirmation);
        Assert.Contains("notepad", kill.ConfirmLabel);
    }

    [Fact]
    public void 所有置灰项都必须带可见原因()
    {
        var inputs = new[]
        {
            Pinned(new[] { Win(H1, "甲", 0, "") }),
            Pinned(hasExe: false, exists: false),
            WindowItem(H1, new[] { Win(H1, "甲", min: true, max: true) }, fg: H1),
            new DockContextMenuInput { Kind = DockItemKind.RecycleBin }
        };
        foreach (var input in inputs)
            foreach (var e in Flatten(DockContextMenuPlan.Build(input)))
                if (!e.IsSeparator && !e.IsEnabled)
                    Assert.False(string.IsNullOrWhiteSpace(e.DisabledReason), $"“{e.Label}”置灰却没写原因");
    }

    // ---------- 回收站 ----------

    [Fact]
    public void 回收站只有打开与需确认的清空()
    {
        var plan = DockContextMenuPlan.Build(new DockContextMenuInput
        {
            Kind = DockItemKind.RecycleBin,
            RecycleBinHasItems = true
        });
        Assert.Equal(new[] { "打开", "清空回收站" }, Labels(plan));
        var empty = plan.Single(e => e.Action == DockContextAction.EmptyRecycleBin);
        Assert.True(empty.RequiresConfirmation);
        Assert.NotNull(empty.ConfirmLabel);
    }

    [Fact]
    public void 回收站已空时清空置灰说明原因()
    {
        var plan = DockContextMenuPlan.Build(new DockContextMenuInput { Kind = DockItemKind.RecycleBin });
        var empty = plan.Single(e => e.Action == DockContextAction.EmptyRecycleBin);
        Assert.False(empty.IsEnabled);
        Assert.Equal("回收站已是空的", empty.DisabledReason);
    }

    private static IEnumerable<DockContextMenuEntry> Flatten(IReadOnlyList<DockContextMenuEntry> entries)
    {
        foreach (var e in entries)
        {
            yield return e;
            if (e.Children is not null)
                foreach (var c in Flatten(e.Children)) yield return c;
        }
    }
}
