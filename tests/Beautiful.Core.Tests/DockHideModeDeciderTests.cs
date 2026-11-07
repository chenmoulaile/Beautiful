using Beautiful.Core.Dock;
using Beautiful.Core.Layout;
using Xunit;

/// <summary>
/// Dock 隐藏模式 9 档的纯判定（对标 MyDockFinder）：串→枚举映射 + 每档的 Evaluate 分支穷举。
/// 判定不读时钟不碰 Win32，单测无需任何环境；事实（DockHideFacts）全部手工构造。
/// </summary>
public class DockHideModeDeciderTests
{
    // 常用几何：一台 1920×1080 的屏，Dock 可见面板带贴底（高 100px）。
    private static readonly PhysicalRect Monitor = new(0, 0, 1920, 1080);
    private static readonly PhysicalRect DockBand = new(0, 980, 1920, 1080);

    private static DockHideFacts Facts(
        DockHideModeKind mode,
        ForegroundWindow? foreground = null,
        bool maximizedOnDockMonitor = false,
        bool desktopOrNone = true,
        bool modifierHeld = false,
        bool edgeToggleVisible = false,
        bool cursorInside = false,
        bool cursorAtEdge = false,
        PhysicalRect? dockBand = null,
        PhysicalRect? monitorRect = null,
        IReadOnlyList<PhysicalRect>? occluding = null)
        => new(mode, dockBand ?? DockBand, monitorRect ?? Monitor, foreground,
               maximizedOnDockMonitor, desktopOrNone, modifierHeld, edgeToggleVisible,
               cursorInside, cursorAtEdge, occluding);

    private static ForegroundWindow Window(int l, int t, int r, int b) => new(
        new IntPtr(42), new PhysicalRect(l, t, r, b),
        IsVisible: true, IsMinimized: false, IsCloaked: false,
        IsOwnProcess: false, IsShellOrDesktop: false);

    // ---------- 串 → 枚举映射 ----------

    [Theory]
    [InlineData(null, DockHideModeKind.Always)]
    [InlineData("", DockHideModeKind.Always)]
    [InlineData("nonsense", DockHideModeKind.Always)]     // 未知值兜 Always = 历史默认
    [InlineData("off", DockHideModeKind.Always)]
    [InlineData("OFF", DockHideModeKind.Always)]
    [InlineData("auto", DockHideModeKind.AutoHide)]
    [InlineData("smart", DockHideModeKind.Smart)]
    [InlineData(" fullscreen ", DockHideModeKind.Fullscreen)]
    [InlineData("maximized", DockHideModeKind.Maximized)]
    [InlineData("edge", DockHideModeKind.EdgeToggle)]
    [InlineData("Modifier", DockHideModeKind.ModifierHold)]
    [InlineData("desktop", DockHideModeKind.DesktopOnly)]
    [InlineData("dim", DockHideModeKind.Dim)]
    public void 模式串映射_未知兜Always(string? raw, DockHideModeKind expected)
        => Assert.Equal(expected, DockHideModes.KindOf(raw));

    [Fact]
    public void 白名单共9档且与枚举一一对应()
    {
        Assert.Equal(9, DockHideModes.Known.Count);
        Assert.Equal(new[] { "off", "auto", "smart", "fullscreen", "maximized", "edge", "modifier", "desktop", "dim" },
            DockHideModes.Known);
        // 每个 Known 值都能映射回它自己的枚举档位，且互不串档
        Assert.Equal(DockHideModeKind.Always, DockHideModes.KindOf("off"));
        Assert.Equal(DockHideModeKind.AutoHide, DockHideModes.KindOf("auto"));
        Assert.Equal(DockHideModeKind.Smart, DockHideModes.KindOf("smart"));
        Assert.Equal(DockHideModeKind.Fullscreen, DockHideModes.KindOf("fullscreen"));
        Assert.Equal(DockHideModeKind.Maximized, DockHideModes.KindOf("maximized"));
        Assert.Equal(DockHideModeKind.EdgeToggle, DockHideModes.KindOf("edge"));
        Assert.Equal(DockHideModeKind.ModifierHold, DockHideModes.KindOf("modifier"));
        Assert.Equal(DockHideModeKind.DesktopOnly, DockHideModes.KindOf("desktop"));
        Assert.Equal(DockHideModeKind.Dim, DockHideModes.KindOf("dim"));
    }

    // ---------- off / dim ----------

    [Fact]
    public void 总是显示_无条件可见()
    {
        Assert.Equal(DockHideDecision.Visible,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.Always, cursorInside: false)));
    }

    [Fact]
    public void 半透明_光标在内可见_在外降透明()
    {
        Assert.Equal(DockHideDecision.Visible,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.Dim, cursorInside: true)));
        Assert.Equal(DockHideDecision.Dimmed,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.Dim, cursorInside: false)));
    }

    // ---------- auto：不经由此判定器（状态机在 AutoHidePlan） ----------

    [Fact]
    public void 自动隐藏档在本判定器只兜可见_真判定走AutoHidePlan()
    {
        // 文档化契约：Evaluate 对 auto 恒 Visible；调用方（DockWindow）先分流到状态机。
        Assert.Equal(DockHideDecision.Visible,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.AutoHide)));
    }

    // ---------- smart：窗口遮挡才让位 ----------

    [Fact]
    public void 智能隐藏_前台窗口盖住Dock面板带才让位()
    {
        // 前台窗口下沿压进面板带 → 藏
        var overlapping = Window(100, 900, 1800, 1000);
        Assert.Equal(DockHideDecision.Hidden,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.Smart, overlapping)));

        // 前台窗口在 Dock 上方留出空隙 → 不藏
        var above = Window(100, 500, 1800, 900);
        Assert.Equal(DockHideDecision.Visible,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.Smart, above)));
    }

    [Fact]
    public void 智能隐藏_不合格前台不当遮挡对象()
    {
        // shell/桌面窗口（桌面本来就铺满整屏）不是"遮挡"
        var desktop = new ForegroundWindow(new IntPtr(7), new PhysicalRect(0, 0, 1920, 1080),
            true, false, false, false, IsShellOrDesktop: true);
        Assert.Equal(DockHideDecision.Visible,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.Smart, desktop)));

        // 读不到前台（null）→ 不藏
        Assert.Equal(DockHideDecision.Visible,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.Smart, foreground: null)));
    }

    [Fact]
    public void 智能隐藏_藏下后光标在Dock上或贴边仍可见()
    {
        var overlapping = Window(0, 900, 1920, 1080);
        Assert.Equal(DockHideDecision.Visible,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.Smart, overlapping, cursorInside: true)));
        Assert.Equal(DockHideDecision.Visible,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.Smart, overlapping, cursorAtEdge: true)));
    }

    // ---------- smart：全量遮挡集合（alpha.29 升级：背景窗口也让位） ----------

    [Fact]
    public void 智能隐藏_背景窗口遮挡集合命中也让位()
    {
        // 前台没有合格窗口（null），但 EnumWindows 采到一扇压进面板带的背景窗口 → 藏
        var facts = Facts(DockHideModeKind.Smart, foreground: null,
                          occluding: new List<PhysicalRect> { new(0, 900, 400, 1000) });
        Assert.Equal(DockHideDecision.Hidden, DockHideModeDecider.Evaluate(facts));
    }

    [Fact]
    public void 智能隐藏_遮挡集合全不相交时不藏()
    {
        // 背景窗口都在面板带上方（底边恰好贴带顶也不算）→ 可见
        var facts = Facts(DockHideModeKind.Smart, foreground: null,
                          occluding: new List<PhysicalRect> { new(0, 500, 1800, 900), new(100, 100, 300, 300) });
        Assert.Equal(DockHideDecision.Visible, DockHideModeDecider.Evaluate(facts));
    }

    [Fact]
    public void 智能隐藏_集合为空或缺失时退回前台判据()
    {
        // null（枚举不可用）与空列表（无合格窗口）都等价于 alpha.28 的"只看前台"：
        var overlapping = Window(100, 900, 1800, 1000);
        Assert.Equal(DockHideDecision.Hidden,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.Smart, overlapping, occluding: null)));
        Assert.Equal(DockHideDecision.Hidden,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.Smart, overlapping,
                                               occluding: Array.Empty<PhysicalRect>())));
        Assert.Equal(DockHideDecision.Visible,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.Smart, foreground: null,
                                               occluding: Array.Empty<PhysicalRect>())));
    }

    [Fact]
    public void 智能隐藏_集合命中且光标贴边仍可唤回()
    {
        // 让位四档共用同一唤出口径：光标在 Dock 上或贴边时保持可见
        var facts = Facts(DockHideModeKind.Smart, foreground: null,
                          occluding: new List<PhysicalRect> { new(0, 900, 400, 1000) },
                          cursorAtEdge: true);
        Assert.Equal(DockHideDecision.Visible, DockHideModeDecider.Evaluate(facts));
    }

    [Fact]
    public void 遮挡判据_边界相触与零面积矩形不算()
    {
        // 集合矩形底边恰好贴带顶边：与前台判据同一几何口径（RectsOverlap，边界相触不算）
        var touching = Facts(DockHideModeKind.Smart, foreground: null,
                             occluding: new List<PhysicalRect> { new(0, 800, 100, 980) });
        Assert.False(DockHideModeDecider.OccluderOverlapsDock(touching));

        // 零面积矩形与"压进上沿"的真遮挡对照
        var zero = Facts(DockHideModeKind.Smart, foreground: null,
                         occluding: new List<PhysicalRect> { default });
        Assert.False(DockHideModeDecider.OccluderOverlapsDock(zero));

        var real = Facts(DockHideModeKind.Smart, foreground: null,
                         occluding: new List<PhysicalRect> { new(0, 900, 100, 1000) });
        Assert.True(DockHideModeDecider.OccluderOverlapsDock(real));
    }

    // ---------- fullscreen：真全屏才让位（复用 FullscreenAvoidance 几何） ----------

    [Fact]
    public void 全屏档_盖住整屏才藏_最大化不误触发()
    {
        // 真全屏：四边都包住显示器
        Assert.Equal(DockHideDecision.Hidden,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.Fullscreen, Window(0, 0, 1920, 1080))));

        // 最大化窗口只铺到工作区（够不到屏底 1080）→ 不藏
        var maximized = Window(-8, -8, 1928, 1072);
        Assert.Equal(DockHideDecision.Visible,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.Fullscreen, maximized)));
    }

    [Fact]
    public void 全屏档_全屏窗口探出边缘1px也算盖住()
    {
        // 全屏窗口常有 1px 外溢（FullscreenAvoidance 的既有口径）
        Assert.Equal(DockHideDecision.Hidden,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.Fullscreen, Window(-1, -1, 1921, 1081))));
    }

    // ---------- maximized：最大化窗口在 Dock 所在屏 ----------

    [Fact]
    public void 最大化档_前台最大化在本屏才藏()
    {
        Assert.Equal(DockHideDecision.Hidden,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.Maximized, maximizedOnDockMonitor: true)));
        Assert.Equal(DockHideDecision.Visible,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.Maximized, maximizedOnDockMonitor: false)));
    }

    [Fact]
    public void 最大化档_藏下后触边仍能唤回()
    {
        Assert.Equal(DockHideDecision.Visible,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.Maximized, maximizedOnDockMonitor: true, cursorAtEdge: true)));
        Assert.Equal(DockHideDecision.Hidden,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.Maximized, maximizedOnDockMonitor: true, cursorAtEdge: false)));
    }

    // ---------- edge：双击边缘切换（状态驱动） ----------

    [Fact]
    public void 双击边缘档_只认切换状态_不认光标()
    {
        Assert.Equal(DockHideDecision.Hidden,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.EdgeToggle, edgeToggleVisible: false, cursorAtEdge: true)));
        Assert.Equal(DockHideDecision.Visible,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.EdgeToggle, edgeToggleVisible: true)));
    }

    // ---------- modifier：按住修饰键临时显示 ----------

    [Fact]
    public void 修饰键档_按住才显示_光标在Dock上保持()
    {
        Assert.Equal(DockHideDecision.Hidden,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.ModifierHold, modifierHeld: false)));
        Assert.Equal(DockHideDecision.Visible,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.ModifierHold, modifierHeld: true)));
        // 松键后光标还在 Dock 上：保持可见，光标离开才藏
        Assert.Equal(DockHideDecision.Visible,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.ModifierHold, modifierHeld: false, cursorInside: true)));
        // 贴边不唤回（否则隐藏名存实亡）：仅边缘命中不算
        Assert.Equal(DockHideDecision.Hidden,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.ModifierHold, modifierHeld: false, cursorAtEdge: true)));
    }

    // ---------- desktop：仅桌面时显示 ----------

    [Fact]
    public void 仅桌面档_前台是桌面或无前台才显示()
    {
        Assert.Equal(DockHideDecision.Visible,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.DesktopOnly, desktopOrNone: true)));
        Assert.Equal(DockHideDecision.Hidden,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.DesktopOnly, desktopOrNone: false)));
        // 前台是应用窗口但光标贴边：仍可唤回
        Assert.Equal(DockHideDecision.Visible,
            DockHideModeDecider.Evaluate(Facts(DockHideModeKind.DesktopOnly, desktopOrNone: false, cursorAtEdge: true)));
    }

    // ---------- 相交几何 ----------

    [Fact]
    public void 矩形相交_边界相触不算_零面积恒否()
    {
        var dock = new PhysicalRect(0, 980, 1920, 1080);
        Assert.True(DockHideModeDecider.RectsOverlap(new PhysicalRect(0, 900, 100, 1000), dock));   // 压进上沿
        Assert.False(DockHideModeDecider.RectsOverlap(new PhysicalRect(0, 900, 100, 980), dock));   // 底边恰好贴 Dock 顶边
        Assert.False(DockHideModeDecider.RectsOverlap(new PhysicalRect(0, 1080, 100, 1200), dock)); // 顶边恰好贴 Dock 底边
        Assert.False(DockHideModeDecider.RectsOverlap(new PhysicalRect(2000, 0, 2100, 100), dock)); // 完全在外
        Assert.False(DockHideModeDecider.RectsOverlap(default, dock));                              // 零矩形
        Assert.False(DockHideModeDecider.RectsOverlap(dock, default));
    }

    [Fact]
    public void 全屏判定_显示器矩形非法时恒不藏()
    {
        // 屏幕矩形取不到（宽高 ≤ 0）时绝不该让位——拿不到边界就不该藏
        var facts = Facts(DockHideModeKind.Fullscreen, Window(0, 0, 1920, 1080),
                          monitorRect: new PhysicalRect(0, 0, 0, 0));
        Assert.Equal(DockHideDecision.Visible, DockHideModeDecider.Evaluate(facts));
    }
}
