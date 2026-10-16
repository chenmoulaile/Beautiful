using System;
using Beautiful.Core.Layout;
using Xunit;

/// <summary>
/// 弹层落位纯函数测试（用户反馈"点顶栏『窗口』，弹出来的菜单和按钮不在同一列上"）。
/// <para>
/// 病根不是锚点算错，而是<b>锚点被解释成了窗口外框</b>：分层窗口的自绘投影只能画在自己的表面上，
/// 所以内容根元素带一圈 <c>Margin</c>（顶栏菜单 20/18/20/24 DIP），可见卡片比窗口外框小这一圈。
/// 旧实现把外框左上角直接贴到锚点 ⇒ 卡片整体右下偏移一整圈投影，且<b>偏移量随 DPI 放大</b>
/// （200% 屏上约 40×36 物理 px），源开发机 100% 缩放时偏移只有一圈 DIP 数值，所以从未暴露。
/// </para>
/// <para>
/// 因此这里锁两条不变量：① 落位后<b>卡片</b>左上角（= 窗口左上角 + 留白）必须精确等于锚点；
/// ② 翻转时贴的是<b>卡片</b>的右/下边，不是外框。留白本身允许溢出屏幕——用户看到的不能溢出。
/// </para>
/// </summary>
public class PopupPlacementTests
{
    // 真机那块内屏的工作区（2880x1920@200%，顶栏 60px、Dock 预留 140px）
    private static readonly PhysicalRect FhdWork = new(0, 60, 2880, 1780);
    private const double Edge = 4;
    private static readonly PopupShadow MenuShadow = new(40, 36, 40, 48);   // 20/18/20/24 DIP @200%

    private static PopupPlacement Place(
        double anchorX, double anchorY,
        double flipX, double flipY,
        double cardW, double cardH,
        PopupShadow shadow,
        PhysicalRect? work = null)
        => PopupPlacer.Place(work ?? FhdWork, anchorX, anchorY, flipX, flipY, cardW, cardH, shadow, Edge);

    // ---------- ① 卡片对齐锚点 ----------

    [Fact]
    public void 有投影留白时_卡片左上角精确落在锚点()
    {
        var p = Place(413, 68, 413, 28, 640, 220, MenuShadow);

        Assert.Equal(413, p.WindowX + MenuShadow.LeftPx);
        Assert.Equal(68, p.WindowY + MenuShadow.TopPx);
        Assert.False(p.FlipX);
        Assert.False(p.FlipY);
    }

    [Fact]
    public void 窗口外框按留白反向偏移_所以偏移量随DPI放大也追得上()
    {
        // 同一锚点，100% 与 200% 两档留白：卡片落点不变，只有外框左上角跟着留白走。
        var at100 = Place(413, 68, 413, 28, 320, 110, new PopupShadow(20, 18, 20, 24));
        var at200 = Place(413, 68, 413, 28, 640, 220, MenuShadow);

        Assert.Equal(at100.WindowX + 20, at200.WindowX + 40);
        Assert.Equal(at100.WindowY + 18, at200.WindowY + 36);
    }

    [Fact]
    public void 零留白时退化为旧行为_窗口即卡片()
    {
        var p = Place(500, 100, 500, 40, 300, 200, PopupShadow.None);

        Assert.Equal(500, p.WindowX);
        Assert.Equal(100, p.WindowY);
    }

    // ---------- ② 翻转贴的是卡片边 ----------

    [Fact]
    public void 向右放不下_卡片右边贴flipX而不是外框右边()
    {
        // 锚点 2600 + 卡片 640 = 3240 > 2880-4 ⇒ 翻转
        var p = Place(2600, 68, 2600, 28, 640, 220, MenuShadow);

        Assert.True(p.FlipX);
        Assert.Equal(2600, p.WindowX + MenuShadow.LeftPx + 640);
    }

    [Fact]
    public void 向下放不下_卡片底边贴flipY()
    {
        // 托盘场景：锚点在条带上方，向下放不下就往上翻
        var p = Place(1200, 1700, 1200, 1640, 400, 300, MenuShadow);

        Assert.True(p.FlipY);
        Assert.Equal(1640, p.WindowY + MenuShadow.TopPx + 300);
    }

    [Fact]
    public void 零留白加翻转_外框右边即卡片右边()
    {
        var p = Place(2700, 68, 2700, 28, 400, 200, PopupShadow.None);

        Assert.True(p.FlipX);
        Assert.Equal(2700, p.WindowX + 400);
    }

    // ---------- ③ 夹取：卡片不许溢出，留白可以 ----------

    [Fact]
    public void 锚点太靠左_卡片被夹回工作区而留白允许溢出屏外()
    {
        var p = Place(0, 68, 0, 28, 640, 220, MenuShadow, new PhysicalRect(0, 0, 2880, 1780));

        Assert.Equal(4, p.WindowX + MenuShadow.LeftPx);   // 卡片左边贴 work.Left + edge
        Assert.True(p.WindowX < 0);                        // 外框（投影圈）探出屏外是可接受的
    }

    [Fact]
    public void 卡片宽到放不下_优先保住左边不越界()
    {
        var work = new PhysicalRect(0, 0, 500, 1780);
        var p = Place(480, 68, 480, 28, 900, 200, MenuShadow, work);

        // 翻转后仍越界 ⇒ 左边夹到 work.Left + edge（左优先，与历史行为一致）
        Assert.Equal(4, p.WindowX + MenuShadow.LeftPx);
    }

    [Fact]
    public void 副屏工作区原点非零_夹取按该屏原点算()
    {
        var second = new PhysicalRect(-1920, 0, -40, 1040);   // 副屏在原点左侧
        var p = Place(-1930, 68, -1930, 28, 300, 200, MenuShadow, second);

        Assert.Equal(-1920 + 4, p.WindowX + MenuShadow.LeftPx);
    }

    [Fact]
    public void 负留白钳到零_不让卡片反向溢出()
    {
        var p = Place(500, 100, 500, 40, 300, 200, new PopupShadow(-50, -50, -50, -50));

        Assert.Equal(500, p.WindowX);
        Assert.Equal(100, p.WindowY);
    }
}
