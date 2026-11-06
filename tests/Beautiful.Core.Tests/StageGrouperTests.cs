using System;
using System.Collections.Generic;
using System.Linq;
using Beautiful.Core.StageManager;
using Xunit;

/// <summary>
/// 台前调度分组纯函数（wp-macos-parity r5）。
/// 约定：入参序列已是 MRU 序（首位 = 最近使用）；Build 只归并 + 前台组置顶，不重排其余；
/// PickThumbnail 选"最能代表组画面"的窗口；Limit 截断保前缀。
/// </summary>
public class StageGrouperTests
{
    private static StageWindow W(int handle, string appId, string appName, string title = "", bool minimized = false)
        => new(new IntPtr(handle), appId, appName, title, minimized);

    // ---------- Build：分组 ----------

    // 按 AppId 归并，组序 = 首现序（MRU），组内窗口保持 MRU 序。
    [Fact]
    public void 按首现应用分组保序且组内保持MRU()
    {
        var input = new[]
        {
            W(1, "edge.exe", "Edge", "标签 A"),
            W(2, "notepad.exe", "记事本", "a.txt"),
            W(3, "edge.exe", "Edge", "标签 B"),
            W(4, "edge.exe", "Edge", "标签 C"),
        };
        var groups = StageGrouper.Build(input, IntPtr.Zero);

        Assert.Equal(2, groups.Count);
        Assert.Equal("edge.exe", groups[0].AppId);
        Assert.Equal(new[] { 1, 3, 4 }, groups[0].Windows.Select(w => w.Handle.ToInt32()));
        Assert.Equal("notepad.exe", groups[1].AppId);
        Assert.Single(groups[1].Windows);
    }

    // 组名取首次出现项（后续窗口即便同名不同也不覆盖）；AppId 保留首现原文。
    [Fact]
    public void 组名与应用标识取首次出现项()
    {
        var input = new[]
        {
            W(1, "A.exe", "首名"),
            W(2, "a.EXE", "别名"),
        };
        var groups = StageGrouper.Build(input, IntPtr.Zero);

        var g = Assert.Single(groups);
        Assert.Equal("A.exe", g.AppId);      // 大小写不敏感归并，但保留首现原文
        Assert.Equal("首名", g.AppName);
        Assert.Equal(2, g.Windows.Count);
    }

    // 句柄去重：同一窗口重复出现只保留首次（防御 EnumWindows 与前台事件重报）。
    [Fact]
    public void 重复句柄只保留首次()
    {
        var input = new[]
        {
            W(1, "edge.exe", "Edge", "第一次"),
            W(2, "notepad.exe", "记事本"),
            W(1, "edge.exe", "Edge", "重报"),
        };
        var groups = StageGrouper.Build(input, IntPtr.Zero);

        var edge = groups.Single(g => g.AppId == "edge.exe");
        Assert.Single(edge.Windows);
        Assert.Equal("第一次", edge.Windows[0].Title);
    }

    // null AppId 防御：折叠为空串组，不抛异常。
    [Fact]
    public void 空应用标识聚合为一组()
    {
        var input = new[]
        {
            W(1, "", "无名一"),
            W(2, "", "无名二"),
        };
        var groups = StageGrouper.Build(input, IntPtr.Zero);

        var g = Assert.Single(groups);
        Assert.Equal(2, g.Windows.Count);
    }

    // ---------- Build：前台组置顶 ----------

    // 前台所在组移到首位并标 IsCurrent，其余保持 MRU 相对顺序。
    [Fact]
    public void 前台组置顶并标记当前()
    {
        var input = new[]
        {
            W(1, "edge.exe", "Edge"),
            W(2, "notepad.exe", "记事本"),
            W(3, "shell.exe", "终端"),
        };
        var groups = StageGrouper.Build(input, new IntPtr(2));

        Assert.Equal("notepad.exe", groups[0].AppId);
        Assert.True(groups[0].IsCurrent);
        Assert.False(groups[1].IsCurrent);
        Assert.False(groups[2].IsCurrent);
        Assert.Equal(new[] { "notepad.exe", "edge.exe", "shell.exe" }, groups.Select(g => g.AppId));
    }

    // 前台已是首位：位置不动，仍标 IsCurrent。
    [Fact]
    public void 前台组已在首位不重复移动()
    {
        var input = new[]
        {
            W(1, "edge.exe", "Edge"),
            W(2, "notepad.exe", "记事本"),
        };
        var groups = StageGrouper.Build(input, new IntPtr(1));

        Assert.Equal("edge.exe", groups[0].AppId);
        Assert.True(groups[0].IsCurrent);
    }

    // 前台为 Zero / 不在列表（前台是我们的面板或桌面）：无当前组，保持 MRU 序。
    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    public void 前台不在列表时无当前组(int foreground)
    {
        var input = new[]
        {
            W(1, "edge.exe", "Edge"),
            W(2, "notepad.exe", "记事本"),
        };
        var groups = StageGrouper.Build(input, new IntPtr(foreground));

        Assert.Equal(new[] { "edge.exe", "notepad.exe" }, groups.Select(g => g.AppId));
        Assert.All(groups, g => Assert.False(g.IsCurrent));
    }

    // 空输入 → 空列表；null 输入 → 抛 ArgumentNullException。
    [Fact]
    public void 空输入返回空列表_null抛异常()
    {
        Assert.Empty(StageGrouper.Build(Array.Empty<StageWindow>(), IntPtr.Zero));
        Assert.Throws<ArgumentNullException>(() => StageGrouper.Build(null!, IntPtr.Zero));
    }

    // ---------- PickThumbnail ----------

    // 首个非最小化窗口优先（MRU 序里第一个"看得见画面"的）。
    [Fact]
    public void 缩略候选选首个非最小化窗口()
    {
        var windows = new[]
        {
            W(1, "edge.exe", "Edge", minimized: true),
            W(2, "edge.exe", "Edge", minimized: false),
            W(3, "edge.exe", "Edge", minimized: false),
        };
        Assert.Equal(new IntPtr(2), StageGrouper.PickThumbnail(windows));
    }

    // 全最小化回退首窗（点击时还原它）。
    [Fact]
    public void 缩略候选全最小化回退首窗()
    {
        var windows = new[]
        {
            W(1, "edge.exe", "Edge", minimized: true),
            W(2, "edge.exe", "Edge", minimized: true),
        };
        Assert.Equal(new IntPtr(1), StageGrouper.PickThumbnail(windows));
    }

    [Fact]
    public void 缩略候选空组返回零句柄()
    {
        Assert.Equal(IntPtr.Zero, StageGrouper.PickThumbnail(Array.Empty<StageWindow>()));
        Assert.Throws<ArgumentNullException>(() => StageGrouper.PickThumbnail(null!));
    }

    // StageGroup.ThumbnailHandle 走同一纯函数。
    [Fact]
    public void 组缩略候选与纯函数一致()
    {
        var g = new StageGroup("edge.exe", "Edge",
        [
            W(1, "edge.exe", "Edge", minimized: true),
            W(2, "edge.exe", "Edge"),
        ], IsCurrent: false);
        Assert.Equal(new IntPtr(2), g.ThumbnailHandle);
    }

    // ---------- Limit ----------

    // 截断保前缀；Build 的当前组在首位，因此截断不丢当前组。
    [Fact]
    public void 截断保前缀且当前组不丢()
    {
        var input = new[]
        {
            W(1, "a.exe", "A"),
            W(2, "b.exe", "B"),
            W(3, "c.exe", "C"),
            W(4, "d.exe", "D"),
            W(5, "e.exe", "E"),
        };
        var groups = StageGrouper.Build(input, new IntPtr(4));   // D 置顶
        var limited = StageGrouper.Limit(groups, 3);

        Assert.Equal(3, limited.Count);
        Assert.Equal("d.exe", limited[0].AppId);
        Assert.True(limited[0].IsCurrent);
        Assert.Equal(new[] { "d.exe", "a.exe", "b.exe" }, limited.Select(g => g.AppId));
    }

    // 上限 0 / 负数 → 空列表；上限大于总数 → 原样（新列表）。
    [Fact]
    public void 截断上限的边界()
    {
        var groups = new List<StageGroup>
        {
            new("a.exe", "A", new List<StageWindow> { W(1, "a.exe", "A") }, false),
        };
        Assert.Empty(StageGrouper.Limit(groups, 0));
        Assert.Empty(StageGrouper.Limit(groups, -1));
        var kept = StageGrouper.Limit(groups, 5);
        Assert.Single(kept);
        Assert.NotSame(groups, kept);   // 返回新列表，不修改入参
        Assert.Single(groups);          // 入参未被修改
        Assert.Throws<ArgumentNullException>(() => StageGrouper.Limit(null!, 3));
    }
}
