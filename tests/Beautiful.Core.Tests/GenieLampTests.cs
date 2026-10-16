using System;
using Beautiful.Core.Animation;
using Xunit;

/// <summary>
/// macOS 灯罩曲线的顶点映射测试。
/// <para>
/// 这套数学是整个 genie 的全部形状来源（渲染侧只把顶点连成三角网），所以端点、单调、
/// "脖子"这三条必须钉住：端点错 = 动画起止各闪一下；单调错 = 窗口会先胀大再缩；
/// 脖子错 = 它就只是一块在缩放的矩形，也就是用户说"不像 mac"的那个原因。
/// </para>
/// </summary>
public class GenieLampTests
{
    // 一个 1200×800 的窗口，图标在下方条带上（物理像素，200% 屏的量级）
    private static readonly LampGeometry Win = new(400, 200, 1200, 800);
    private static readonly LampGeometry Icon = new(1500, 1830, 96, 0);

    [Fact]
    public void 起点每个顶点精确等于源窗口内的原位置()
    {
        for (var ty = 0.0; ty <= 1.0; ty += 0.25)
        {
            for (var tx = 0.0; tx <= 1.0; tx += 0.25)
            {
                var (x, y) = GenieLamp.Vertex(tx, ty, 0.0, Win, Icon);
                Assert.Equal(Win.X + tx * Win.Width, x, 6);
                Assert.Equal(Win.Y + ty * Win.Height, y, 6);
            }
        }
    }

    [Fact]
    public void 终点所有顶点塌到图标那一行()
    {
        for (var ty = 0.0; ty <= 1.0; ty += 0.2)
        {
            for (var tx = 0.0; tx <= 1.0; tx += 0.2)
            {
                var (x, y) = GenieLamp.Vertex(tx, ty, 1.0, Win, Icon);
                Assert.Equal(Icon.X + tx * Icon.Width, x, 6);
                Assert.Equal(Icon.Y, y, 6);
            }
        }
    }

    [Fact]
    public void 下缘比上缘先收窄_这就是脖子()
    {
        // 第二阶段中途：靠近图标的底行已经快收到图标宽，顶行还接近原宽
        const double p = 0.65;
        var top = GenieLamp.RowWidth(0.0, p, Win, Icon);
        var bottom = GenieLamp.RowWidth(1.0, p, Win, Icon);

        Assert.True(bottom < top, $"底行 {bottom} 应当窄于顶行 {top}，否则就只是一块缩放的矩形");
        Assert.True(top > Icon.Width, "顶行此时应当还没收到图标宽");
        Assert.Equal(Icon.Width, bottom, 6);
    }

    [Fact]
    public void 同一进度下行越靠下越窄_全程单调()
    {
        foreach (var p in new[] { 0.05, 0.3, 0.45, 0.65, 0.85, 0.99 })
        {
            double previous = double.MaxValue;
            for (var ty = 0.0; ty <= 1.0001; ty += 0.1)
            {
                var width = GenieLamp.RowWidth(ty, p, Win, Icon);
                Assert.True(width <= previous + 1e-6, $"p={p} ty={ty} 处行宽反弹了：{previous} → {width}");
                previous = width;
            }
        }
    }

    [Fact]
    public void 第一阶段把窗口拉长到图标_第二阶段才收拢()
    {
        // 分界点上：下缘刚好触到图标那一行，而整体高度还没开始塌
        var bottomAtSplit = GenieLamp.Vertex(0.5, 1.0, GenieLamp.Split, Win, Icon).Y;
        Assert.Equal(Icon.Y, bottomAtSplit, 6);

        // 分界点之前：下缘仍在往下走（拉长），不是收缩
        var earlier = GenieLamp.Vertex(0.5, 1.0, GenieLamp.Split * 0.5, Win, Icon).Y;
        Assert.True(earlier > Win.Y + Win.Height && earlier < Icon.Y,
            $"半程时下缘应落在窗口与图标之间，实际 {earlier}");
    }

    [Fact]
    public void 摆动项在两端为零_不会让落点偏掉()
    {
        // p=0 时 k=0，p=1 时 sin(π)=0：两处摆动都必须恰好消失，否则起点/终点会闪
        foreach (var tx in new[] { 0.0, 0.37, 1.0 })
        {
            var atStart = GenieLamp.Vertex(tx, 0.6, 0.0, Win, Icon);
            Assert.Equal(Win.X + tx * Win.Width, atStart.X, 6);

            var atEnd = GenieLamp.Vertex(tx, 0.6, 1.0, Win, Icon);
            Assert.Equal(Icon.X + tx * Icon.Width, atEnd.X, 6);
        }
    }

    [Fact]
    public void 图标在窗口左侧时方向照样正确()
    {
        var left = new LampGeometry(120, 1830, 96, 0);
        var (x, y) = GenieLamp.Vertex(0.0, 1.0, 1.0, Win, left);
        Assert.Equal(left.X, x, 6);
        Assert.Equal(left.Y, y, 6);
        Assert.True(GenieLamp.RowWidth(1.0, 0.65, Win, left) < GenieLamp.RowWidth(0.0, 0.65, Win, left));
    }

    [Fact]
    public void 图标与窗口同高时不退化()
    {
        // expandHeight = 0 的退化情形（图标就在窗口下缘上）：仍然要有起点与终点的正确形状
        var flush = new LampGeometry(900, 1000, 96, 0);
        var start = GenieLamp.Vertex(1.0, 1.0, 0.0, Win, flush);
        Assert.Equal(Win.X + Win.Width, start.X, 6);
        Assert.Equal(Win.Y + Win.Height, start.Y, 6);

        var end = GenieLamp.Vertex(1.0, 1.0, 1.0, Win, flush);
        Assert.Equal(flush.X + flush.Width, end.X, 6);
        Assert.Equal(flush.Y, end.Y, 6);
    }

    [Theory]
    [InlineData(-3.0)]
    [InlineData(-0.01)]
    public void 负进度等于起点(double p)
    {
        var a = GenieLamp.Vertex(0.3, 0.7, p, Win, Icon);
        var b = GenieLamp.Vertex(0.3, 0.7, 0.0, Win, Icon);
        Assert.Equal(b.X, a.X, 9);
        Assert.Equal(b.Y, a.Y, 9);
    }

    [Theory]
    [InlineData(1.01)]
    [InlineData(7.0)]
    public void 越上界等于终点(double p)
    {
        var a = GenieLamp.Vertex(0.3, 0.7, p, Win, Icon);
        var b = GenieLamp.Vertex(0.3, 0.7, 1.0, Win, Icon);
        Assert.Equal(b.X, a.X, 9);
        Assert.Equal(b.Y, a.Y, 9);
    }

    [Fact]
    public void 钳制函数只认零一区间()
    {
        Assert.Equal(0.0, GenieLamp.Clamp01(-1));
        Assert.Equal(0.25, GenieLamp.Clamp01(0.25));
        Assert.Equal(1.0, GenieLamp.Clamp01(2));
    }

    [Fact]
    public void 同一输入重复映射结果一致_说明没有藏状态()
    {
        for (var p = 0.0; p <= 1.0; p += 0.1)
        {
            var a = GenieLamp.Vertex(0.42, 0.77, p, Win, Icon);
            var b = GenieLamp.Vertex(0.42, 0.77, p, Win, Icon);
            Assert.Equal(a.X, b.X, 12);
            Assert.Equal(a.Y, b.Y, 12);
        }
    }
}
