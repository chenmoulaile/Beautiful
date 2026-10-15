using Beautiful.Core.Controls;
using Xunit;

/// <summary>
/// 控制中心音量砖块的换算取证（v3 T7）。
/// 界面与 COM 那一侧都只做"读数→滑杆→写值"的搬运，全部规则在这里：越界钳制、非有限值、
/// 静音与拖动的相互作用。COM interop 在本机 CI 上无法测（要真端点），所以它两侧的规则更不能只靠注释。
/// </summary>
public class VolumeMathTests
{
    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.5, 50)]
    [InlineData(0.45, 45)]
    [InlineData(0.9999999, 100)]   // 端点回读常见的"满量程差一点点"，界面必须显示 100 而不是 99
    [InlineData(1.0, 100)]
    [InlineData(1.5, 100)]         // 越界钳上界（某些驱动会报 >1 的标量）
    [InlineData(-0.2, 0)]          // 越界钳下界
    [InlineData(0.004, 0)]         // 四舍五入到 0：0.4% 这种噪声不该显示成"有 1% 音量"
    [InlineData(0.006, 1)]
    public void 标量按百分比读数并钳到合法区间(double scalar, int expected)
        => Assert.Equal(expected, VolumeMath.ToPercent(scalar));

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(double.PositiveInfinity, 0)]
    [InlineData(double.NegativeInfinity, 0)]
    public void 非有限标量读成零_绝不显示一个编出来的音量(double scalar, int expected)
        => Assert.Equal(expected, VolumeMath.ToPercent(scalar));

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(50, 0.5)]
    [InlineData(100, 1.0)]
    [InlineData(-30, 0.0)]   // 负数钳到下限
    [InlineData(240, 1.0)]   // 越界钳到上限
    public void 百分比写回标量钳位且不越界(int percent, double expected)
    {
        var scalar = VolumeMath.ToScalar(percent);
        Assert.Equal(expected, scalar, 6);
        Assert.InRange(scalar, VolumeMath.MinScalar, VolumeMath.MaxScalar);
    }

    [Fact]
    public void 满量程必须恰好是一而不是零点九九九九()
        => Assert.Equal(VolumeMath.MaxScalar, VolumeMath.ToScalar(VolumeMath.MaxPercent));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(64)]
    [InlineData(100)]
    public void 整数百分比往返不丢刻度(int percent)
        => Assert.Equal(percent, VolumeMath.ToPercent(VolumeMath.ToScalar(percent)));

    [Fact]
    public void 拖动到大于零即刻解除静音_拖动到零保留静音()
    {
        var (scalar, muted) = VolumeMath.ApplyDrag(30, muted: true);
        Assert.Equal(0.3, scalar, 6);
        Assert.False(muted);          // 用户拖动就是在要求"我能听见"

        var (_, stillMuted) = VolumeMath.ApplyDrag(0, muted: true);
        Assert.True(stillMuted);      // 0% 已无声，取消静音只会带来一声可闻的咔哒，没有收益
    }

    [Fact]
    public void 静音不改滑杆读数_取消静音即刻回到原音量()
    {
        // 滑杆恒显示真实标量：静音若把滑杆压到 0，用户随手抖一格就会被解释成"设成 1% 并解除静音"，
        // 原来 45% 当场丢失（与系统混音器 sndvol 同规则）。
        Assert.Equal(45, VolumeMath.SliderPercent(0.45));
        Assert.True(VolumeMath.IsAudible(0.45, muted: false));
        Assert.False(VolumeMath.IsAudible(0.45, muted: true));
        Assert.False(VolumeMath.IsAudible(0.0, muted: false)); // 有滑杆读数但没有声音，同样算"听不见"
    }

    [Fact]
    public void 取消静音时有音量就原样留着()
    {
        var (muted, scalar) = VolumeMath.ApplyMuteToggle(0.45, muted: true);
        Assert.False(muted);
        Assert.Equal(0.45, scalar, 6);
    }

    [Fact]
    public void 零点端点上取消静音补一个可闻下限()
    {
        // "已解除静音"却什么都听不见是自相矛盾的状态，所以补到最低可闻刻度
        var (muted, scalar) = VolumeMath.ApplyMuteToggle(0.0, muted: true);
        Assert.False(muted);
        Assert.Equal(VolumeMath.UnmuteFloorPercent, VolumeMath.ToPercent(scalar));
        Assert.True(VolumeMath.IsAudible(scalar, muted));
    }

    [Fact]
    public void 静音状态下的标量在再次点静音时不被改动()
    {
        var (muted, scalar) = VolumeMath.ApplyMuteToggle(0.62, muted: false);
        Assert.True(muted);
        Assert.Equal(0.62, scalar, 6);
    }
}
