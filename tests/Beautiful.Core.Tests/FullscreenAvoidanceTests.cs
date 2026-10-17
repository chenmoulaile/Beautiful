using System;
using Beautiful.Core.Layout;
using Xunit;

/// <summary>
/// 全屏避让纯函数测试（v3.8，算法移植自 TaskbarLyrics 的 SmartTopmostPolicy，MIT）。
/// <para>
/// 这条线只能在这里锁：真机上"起一个全屏游戏再看装饰层掉不掉"要么扰动用户正在用的机器，
/// 要么根本没有 GPU 直通。而让位/浮着的每个分支都取决于矩形与标志位的组合，纯函数能穷举。
/// </para>
/// </summary>
public class FullscreenAvoidanceTests
{
    private static readonly PhysicalRect Primary = new(0, 0, 1920, 1080);
    private static readonly PhysicalRect Secondary = new(-2560, 0, 0, 1440);

    private static ForegroundWindow Window(
        PhysicalRect bounds,
        nint handle = 1,
        bool visible = true,
        bool minimized = false,
        bool cloaked = false,
        bool own = false,
        bool shell = false)
        => new(new IntPtr(handle), bounds, visible, minimized, cloaked, own, shell);

    // ---------- CoversMonitor：只有真全屏才成立 ----------

    [Fact]
    public void 恰好铺满显示器_判为盖住()
        => Assert.True(FullscreenAvoidance.CoversMonitor(Primary, Primary));

    [Fact]
    public void 外溢一像素的全屏_仍判为盖住()
        => Assert.True(FullscreenAvoidance.CoversMonitor(new PhysicalRect(-1, -1, 1921, 1081), Primary));

    [Theory]
    [InlineData(1, 0, 1920, 1080)]     // 左缘差 1px
    [InlineData(0, 1, 1920, 1080)]     // 上缘差 1px
    [InlineData(0, 0, 1919, 1080)]     // 右缘差 1px
    [InlineData(0, 0, 1920, 1079)]     // 下缘差 1px
    public void 差一条边就判为没盖住(int l, int t, int r, int b)
        => Assert.False(FullscreenAvoidance.CoversMonitor(new PhysicalRect(l, t, r, b), Primary));

    [Fact]
    public void 最大化窗口停在任务栏上沿_判为没盖住()
    {
        // 1080 高、40px 任务栏：SC_MAXIMIZE 只会铺满工作区，Bottom 到不了 1080。
        // 这一条是本策略不需要"是不是游戏"启发式的全部理由。
        Assert.False(FullscreenAvoidance.CoversMonitor(new PhysicalRect(0, 0, 1920, 1040), Primary));
    }

    [Fact]
    public void 副屏负坐标_按该屏自己的矩形判()
    {
        Assert.True(FullscreenAvoidance.CoversMonitor(Secondary, Secondary));
        // 主屏全屏不该把副屏的装饰层拖下去
        Assert.False(FullscreenAvoidance.CoversMonitor(Primary, Secondary));
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(0, 0, 1920, 0)]
    public void 显示器矩形非法_一律判为没盖住_不让位(int l, int t, int r, int b)
        => Assert.False(FullscreenAvoidance.CoversMonitor(Primary, new PhysicalRect(l, t, r, b)));

    // ---------- IsEligible：让位对象的资格 ----------

    [Fact]
    public void 零句柄_不合格()
        => Assert.False(new ForegroundWindow(IntPtr.Zero, Primary, true, false, false, false, false).IsEligible);

    [Theory]
    [InlineData(false, false, false, false, false)]   // 不可见
    [InlineData(true, true, false, false, false)]     // 最小化：根本没盖住屏幕
    [InlineData(true, false, true, false, false)]     // 被 DWM cloak：一个像素都没画（本机 TextInputHost 实测形态）
    [InlineData(true, false, false, true, false)]     // 本进程自己的窗口：不能让位给自己
    [InlineData(true, false, false, false, true)]     // 桌面/任务栏：Progman 天生铺满且 cloak=0，只能按类名挡
    public void 任一资格条件不成立_就不作为让位对象(
        bool visible, bool minimized, bool cloaked, bool own, bool shell)
        => Assert.False(Window(Primary, visible: visible, minimized: minimized,
            cloaked: cloaked, own: own, shell: shell).IsEligible);

    [Fact]
    public void 五个条件都成立_才作为让位对象()
        => Assert.True(Window(Primary).IsEligible);

    [Fact]
    public void 零尺寸窗口_不合格()
        => Assert.False(Window(new PhysicalRect(0, 0, 0, 1080)).IsEligible);

    // ---------- Evaluate：裁定 ----------

    [Fact]
    public void 全屏前台_让位并把句柄交出去()
    {
        var d = FullscreenAvoidance.Evaluate(Primary, Window(Primary, handle: 42));
        Assert.False(d.KeepTopmost);
        Assert.Equal(new IntPtr(42), d.PlaceAfter);
    }

    [Fact]
    public void 没有前台窗口_浮着()
    {
        var d = FullscreenAvoidance.Evaluate(Primary, null);
        Assert.True(d.KeepTopmost);
        Assert.Equal(IntPtr.Zero, d.PlaceAfter);
    }

    [Fact]
    public void 前台是桌面_浮着()
    {
        // 这是最常见的常态（桌面本身铺满屏幕）：若把桌面当让位对象，装饰层会永久消失
        var d = FullscreenAvoidance.Evaluate(Primary, Window(Primary, shell: true));
        Assert.True(d.KeepTopmost);
    }

    [Fact]
    public void 前台窗口没盖住这块屏_浮着()
    {
        var d = FullscreenAvoidance.Evaluate(Primary, Window(new PhysicalRect(100, 100, 900, 700)));
        Assert.True(d.KeepTopmost);
        Assert.Equal(IntPtr.Zero, d.PlaceAfter);
    }

    [Fact]
    public void 输入法宿主的铺满窗口_不让位()
    {
        // 本机 2026-10-17 取证到的真实形态：TextInputHost.exe 的 Windows.UI.Core.CoreWindow
        // IsWindowVisible=true、矩形 0,0,2880,1920 正好铺满、DWMWA_CLOAKED=2。
        // 它一旦抢到前台又没有 cloak 判据，玻璃壳会凭空消失且再也回不来。
        var d = FullscreenAvoidance.Evaluate(
            new PhysicalRect(0, 0, 2880, 1920),
            Window(new PhysicalRect(0, 0, 2880, 1920), cloaked: true));
        Assert.True(d.KeepTopmost);
    }

    [Fact]
    public void 副屏全屏_不影响主屏覆盖层()
    {
        var d = FullscreenAvoidance.Evaluate(Primary, Window(Secondary));
        Assert.True(d.KeepTopmost);
    }

    // ---------- NeedsReassert：什么时候真的调 SetWindowPos ----------

    [Fact]
    public void 该浮着时_每轮都重施_否则会被别的置顶窗口抢走()
    {
        var keep = new OverlayZOrder(true, IntPtr.Zero);
        Assert.True(FullscreenAvoidance.NeedsReassert(keep, keep));
        Assert.True(FullscreenAvoidance.NeedsReassert(null, keep));
    }

    [Fact]
    public void 该让位时_只做一次_不与全屏窗口抢Z序()
    {
        var yield = new OverlayZOrder(false, new IntPtr(42));
        Assert.False(FullscreenAvoidance.NeedsReassert(yield, yield));
        Assert.True(FullscreenAvoidance.NeedsReassert(null, yield));
    }

    [Fact]
    public void 状态翻转_必须下发()
    {
        var keep = new OverlayZOrder(true, IntPtr.Zero);
        var yield = new OverlayZOrder(false, new IntPtr(42));
        Assert.True(FullscreenAvoidance.NeedsReassert(keep, yield));
        Assert.True(FullscreenAvoidance.NeedsReassert(yield, keep));
    }

    [Fact]
    public void 让位对象换了窗口_必须下发()
    {
        // 全屏窗口 A 关掉后系统把前台给 B，两轮的 PlaceAfter 不同 → 得跟着换插位目标
        var a = new OverlayZOrder(false, new IntPtr(1));
        var b = new OverlayZOrder(false, new IntPtr(2));
        Assert.True(FullscreenAvoidance.NeedsReassert(a, b));
    }
}
