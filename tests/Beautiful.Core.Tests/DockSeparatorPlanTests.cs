using System;
using Beautiful.Core.Dock;
using Xunit;

/// <summary>
/// 分隔符插位与放大场资格（<see cref="DockSeparatorPlan"/>）。
/// macOS 原生 Dock 的结构是「应用区（固定+运行混排，不分界） | 最小化窗口区 | 废纸篓」：
/// 分隔符只出现在语义块边界上——运行窗口与固定应用之间**不**画线（alpha.24 改为 mac 真实语义），
/// 而且**一条也不会出现在媒体胶囊旁边**（胶囊无会话时宽度归零，细线会浮在面板末端）。
/// </summary>
public class DockSeparatorPlanTests
{
    private const bool On = true;
    private const bool Off = false;

    private static readonly DockItemKind[] Native =
    {
        DockItemKind.Pinned, DockItemKind.Pinned,
        DockItemKind.Window, DockItemKind.Window,
        DockItemKind.MinimizedWindow,
        DockItemKind.RecycleBin, DockItemKind.Media
    };

    [Fact]
    public void 分隔符不放大也不可交互_其余条目照常()
    {
        Assert.False(DockSeparatorPlan.IsMagnifiable(DockItemKind.Separator));
        Assert.False(DockSeparatorPlan.IsInteractive(DockItemKind.Separator));
        foreach (var kind in new[] { DockItemKind.Pinned, DockItemKind.Window, DockItemKind.MinimizedWindow, DockItemKind.RecycleBin, DockItemKind.Media })
        {
            Assert.True(DockSeparatorPlan.IsMagnifiable(kind), $"{kind} 应参与放大场");
            Assert.True(DockSeparatorPlan.IsInteractive(kind), $"{kind} 应可命中");
        }
    }

    [Fact]
    public void 主题关闭时一条分隔符都不生成()
    {
        // GNOME dash / 鸿蒙 dock 是连续列表：原生没有分隔符，App 侧不需要 if (theme == mac)
        Assert.Empty(DockSeparatorPlan.Slots(Native, Off));
        Assert.Empty(DockSeparatorPlan.Slots(new[] { DockItemKind.Pinned }, Off));
    }

    [Fact]
    public void macos结构_最小化区前与废纸篓前各一道_固定与运行之间不画线()
    {
        var slots = DockSeparatorPlan.Slots(Native, On);
        Assert.Equal(2, slots.Count);
        Assert.Equal(4, slots[0].Index);                       // 第一个 MinimizedWindow 之前
        Assert.Equal(DockSeparatorRole.AppsBoundary, slots[0].Role);
        Assert.Equal(5, slots[1].Index);                       // 回收站之前
        Assert.Equal(DockSeparatorRole.TrashBoundary, slots[1].Role);
    }

    [Fact]
    public void 插入后正是原生三段_应用区_最小化区_废纸篓()
    {
        var slots = DockSeparatorPlan.Slots(Native, On);
        var drawn = Compose(Native, slots);
        Assert.Equal(
            "Pinned,Pinned,Window,Window,SEP,MinimizedWindow,SEP,RecycleBin,Media",
            string.Join(",", drawn));
    }

    [Fact]
    public void 运行窗口与固定应用之间不留线_无论谁在前()
    {
        // 固定→运行：应用区内部，macOS 不分界
        Assert.Empty(DockSeparatorPlan.Slots(new[] { DockItemKind.Pinned, DockItemKind.Window }, On));
        // 运行→固定：同上（alpha.23 之前这里的旧规则也不画线，行为保持）
        Assert.Empty(DockSeparatorPlan.Slots(new[] { DockItemKind.Window, DockItemKind.Pinned }, On));
    }

    [Fact]
    public void 没有最小化窗口时应用区与废纸篓之间只有一道()
    {
        var kinds = new[] { DockItemKind.Finder, DockItemKind.Launchpad, DockItemKind.Pinned, DockItemKind.Window, DockItemKind.RecycleBin, DockItemKind.Media };
        var slots = DockSeparatorPlan.Slots(kinds, On);
        var only = Assert.Single(slots);
        Assert.Equal(4, only.Index);
        Assert.Equal(DockSeparatorRole.TrashBoundary, only.Role);
    }

    [Fact]
    public void 只有固定区时不在其前后画线()
    {
        // 没有最小化窗口也没有回收站在首位：固定块右端不留悬空细线
        Assert.Empty(DockSeparatorPlan.Slots(new[] { DockItemKind.Pinned, DockItemKind.Pinned }, On));
    }

    [Fact]
    public void 无固定项时最小化区前与回收站前各留一道()
    {
        var kinds = new[] { DockItemKind.Window, DockItemKind.MinimizedWindow, DockItemKind.RecycleBin, DockItemKind.Media };
        var slots = DockSeparatorPlan.Slots(kinds, On);
        Assert.Equal(2, slots.Count);
        Assert.Equal(1, slots[0].Index);
        Assert.Equal(DockSeparatorRole.AppsBoundary, slots[0].Role);
        Assert.Equal(2, slots[1].Index);
        Assert.Equal(DockSeparatorRole.TrashBoundary, slots[1].Role);
    }

    [Fact]
    public void 只有回收站与胶囊时首尾都不插线()
    {
        // 序列首不插（否则面板左端一道孤立细线）、尾不插、胶囊前不插
        Assert.Empty(DockSeparatorPlan.Slots(new[] { DockItemKind.RecycleBin, DockItemKind.Media }, On));
        Assert.Empty(DockSeparatorPlan.Slots(new[] { DockItemKind.Media }, On));
        Assert.Empty(DockSeparatorPlan.Slots(Array.Empty<DockItemKind>(), On));
    }

    [Fact]
    public void 固定项直接接回收站时那道线插在回收站之前()
    {
        var kinds = new[] { DockItemKind.Pinned, DockItemKind.Pinned, DockItemKind.RecycleBin, DockItemKind.Media };
        var slot = Assert.Single(DockSeparatorPlan.Slots(kinds, On));
        Assert.Equal(2, slot.Index);
        Assert.Equal(DockSeparatorRole.TrashBoundary, slot.Role);
    }

    [Fact]
    public void 媒体胶囊之前永不插线()
    {
        // 有最小化窗口时细线在 MinimizedWindow 之前，而不是 Media 之前
        var kinds = new[] { DockItemKind.Pinned, DockItemKind.Window, DockItemKind.MinimizedWindow, DockItemKind.Media };
        var slots = DockSeparatorPlan.Slots(kinds, On);
        var slot = Assert.Single(slots);
        Assert.Equal(2, slot.Index);   // MinimizedWindow 之前（回收站也不在场，无 TrashBoundary）
        Assert.Equal(DockSeparatorRole.AppsBoundary, slot.Role);
        Assert.DoesNotContain(kinds.Length - 1, Array.ConvertAll(slots.ToArray(), s => s.Index));
    }

    [Fact]
    public void 同一角色不重复插线且槽位升序()
    {
        // 语义序列不该含分隔符；混进来了也只当作"分隔符两侧不再是边界"，不会叠出两道线
        var kinds = new[]
        {
            DockItemKind.Pinned, DockItemKind.Separator, DockItemKind.Pinned,
            DockItemKind.Window, DockItemKind.MinimizedWindow, DockItemKind.RecycleBin, DockItemKind.Media
        };
        var slots = DockSeparatorPlan.Slots(kinds, On);
        Assert.Equal(2, slots.Count);
        Assert.Equal(4, slots[0].Index);   // MinimizedWindow 之前（与 SEP 相邻的那个边界被跳过）
        Assert.Equal(5, slots[1].Index);   // 回收站之前
        Assert.True(slots[0].Index < slots[1].Index, "槽位必须升序，倒序插入会把语义下标算歪");
    }

    [Fact]
    public void null输入按无分隔符处理()
    {
        Assert.Empty(DockSeparatorPlan.Slots(null!, On));
    }

    /// <summary>按"在指定语义下标之前插入"的规则把分隔符写进序列（与 DockViewModel.Reconcile 同一口径）。</summary>
    private static string[] Compose(DockItemKind[] kinds, System.Collections.Generic.IReadOnlyList<DockSeparatorSlot> slots)
    {
        var list = new System.Collections.Generic.List<string>(kinds.Length + slots.Count);
        var at = 0;
        foreach (var s in slots)
        {
            while (at < s.Index) list.Add(kinds[at++].ToString());
            list.Add("SEP");
        }
        while (at < kinds.Length) list.Add(kinds[at++].ToString());
        return list.ToArray();
    }
}
