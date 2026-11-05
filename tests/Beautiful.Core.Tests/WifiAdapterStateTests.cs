using System;
using System.Linq;
using Beautiful.Core.Network;
using Xunit;

/// <summary>
/// 无线网卡在场性裁定（<c>WlanEnumInterfaces</c> 的逐卡 state 折成一个结论）。
/// <para>
/// 这层必须能<b>完全脱离网卡</b>跑：真机上"这块驱动不给你 opcode"这种事，恰恰只有把它当成
/// 一条普通输入来测才测得到（下面几个用例就是本机那台 Intel AX201 的读数形状 + 各种坏情况 +
/// 未知档位）。判定的价值在于：射频关着时界面还能不能给用户一条"把它打开"的出路
/// （ChosenIndex 必须有效），以及"没网卡"和"网卡关着"这两句话不能互相冒充。
/// </para>
/// </summary>
public class WifiAdapterStateTests
{
    // ---------- 四个基本案：没网卡 / 全 not_ready / connected / 多卡取第一块可用 ----------

    [Fact]
    public void 一块网卡都没有_裁定为缺席且没有可用下标()
    {
        var v = WifiAdapterState.Judge(0, Array.Empty<int>());
        Assert.Equal(WifiAdapterVerdict.Absent, v.Verdict);
        Assert.Equal(-1, v.ChosenIndex);
        Assert.Equal("这台机器没有无线网卡", v.Reason);
    }

    [Fact]
    public void 全都不就绪_裁定为射频关闭_但仍要给出下标()
    {
        var v = WifiAdapterState.Judge(2, new[] { 0, 0 });
        Assert.Equal(WifiAdapterVerdict.RadioOff, v.Verdict);
        // 【这条断言是整份裁定的要点】射频关着时用户唯一能做的事就是把它打开，
        // 而那需要一块网卡的 GUID 去 WlanSetInterface——返回 -1 就等于把这条出路也断了。
        Assert.Equal(0, v.ChosenIndex);
    }

    [Fact]
    public void 本机那台Intel的形状_一块已连接_裁定为已连接()
    {
        // 真机实测：count=1、state=1（connected），这就是控制中心原本"读不到开关状态"的那台机器。
        var v = WifiAdapterState.Judge(1, new[] { 1 });
        Assert.Equal(WifiAdapterVerdict.Connected, v.Verdict);
        Assert.Equal(0, v.ChosenIndex);
        Assert.Equal("无线网卡已连接", v.Reason);
    }

    [Fact]
    public void 多卡取第一块可用_跳过前面那块不就绪的()
    {
        var v = WifiAdapterState.Judge(3, new[] { 0, 4, 1 });
        Assert.Equal(WifiAdapterVerdict.Connected, v.Verdict);
        // 已连接的那块（下标 2）优先于"在场未连"的下标 1：那块才是正在干活的卡。
        Assert.Equal(2, v.ChosenIndex);
    }

    [Fact]
    public void 多卡没有已连接时_取第一块在场未连的()
    {
        var v = WifiAdapterState.Judge(3, new[] { 0, 3, 4 });
        Assert.Equal(WifiAdapterVerdict.Ready, v.Verdict);
        Assert.Equal(1, v.ChosenIndex);
    }

    // ---------- 档位口径：0-7 全档 ----------

    [Theory]
    [InlineData(2, WifiAdapterVerdict.Ready)]   // ad_hoc_joined：在场
    [InlineData(3, WifiAdapterVerdict.Ready)]   // disconnecting：过渡态也算在场
    [InlineData(4, WifiAdapterVerdict.Ready)]   // disconnected
    [InlineData(5, WifiAdapterVerdict.Ready)]   // associating
    [InlineData(6, WifiAdapterVerdict.Ready)]   // discovering
    [InlineData(7, WifiAdapterVerdict.Ready)]   // authenticating
    public void 除不就绪和已连接外的档位都算在场未连(int state, WifiAdapterVerdict expected)
        => Assert.Equal(expected, WifiAdapterState.Judge(1, new[] { state }).Verdict);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(4, true)]
    public void 只有不就绪那一档算不可用(int state, bool expectedUsable)
        => Assert.Equal(expectedUsable, WifiAdapterState.IsUsable(state));

    // ---------- 未知档位：驱动给了 0-7 之外的数，也不能谎报"关" ----------

    [Theory]
    [InlineData(8)]
    [InlineData(999)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void 未知档位按在场处理_不谎报关闭(int unknownState)
    {
        var v = WifiAdapterState.Judge(1, new[] { unknownState });
        Assert.Equal(WifiAdapterVerdict.Ready, v.Verdict);
        Assert.Equal(0, v.ChosenIndex);
        Assert.True(WifiAdapterState.IsUsable(unknownState));
    }

    [Fact]
    public void 未知档位与已连接并存_仍以已连接为准()
    {
        var v = WifiAdapterState.Judge(2, new[] { 999, 1 });
        Assert.Equal(WifiAdapterVerdict.Connected, v.Verdict);
        Assert.Equal(1, v.ChosenIndex);
    }

    [Fact]
    public void 全块都是未知档位_裁定为在场未连且给首卡下标()
    {
        var v = WifiAdapterState.Judge(2, new[] { -1, 999 });
        Assert.Equal(WifiAdapterVerdict.Ready, v.Verdict);
        Assert.Equal(0, v.ChosenIndex);
    }

    // ---------- 坏输入必须保守，不能越界 ----------

    [Fact]
    public void 条目数比实际解出的多_按小的算_不越界()
    {
        // 链表读到一半失败是真实存在的形状（count 说 3 块、只解出 1 块）。
        // 按 count 硬读会 IndexOutOfRange，而这个错发生在顶栏的每分钟定时器里就是整个顶栏遭殃。
        var v = WifiAdapterState.Judge(3, new[] { 0 });
        Assert.Equal(WifiAdapterVerdict.RadioOff, v.Verdict);
        Assert.Equal(0, v.ChosenIndex);
    }

    [Fact]
    public void 状态表为空指针_裁定为缺席而不是崩溃()
    {
        Assert.Equal(WifiAdapterVerdict.Absent, WifiAdapterState.Judge(5, null).Verdict);
        Assert.Equal(WifiAdapterVerdict.Absent, WifiAdapterState.Judge(-1, new[] { 1 }).Verdict);
        Assert.Equal(-1, WifiAdapterState.Prefer(null));
    }

    [Fact]
    public void 计数为零但给了状态_仍裁定为缺席()
        => Assert.Equal(WifiAdapterVerdict.Absent, WifiAdapterState.Judge(0, new[] { 1 }).Verdict);

    // ---------- Prefer 与 Judge 必须同一条规则（否则两处各选一块卡） ----------

    [Theory]
    [InlineData(new int[] { 1 }, 0)]
    [InlineData(new int[] { 0, 1 }, 1)]
    [InlineData(new int[] { 0, 4, 1 }, 2)]
    [InlineData(new int[] { 4, 1 }, 1)]
    [InlineData(new int[] { 0, 4 }, 1)]
    [InlineData(new int[] { 0, 0 }, 0)]
    public void Prefer与Judge给出的下标必须一致(int[] states, int expected)
    {
        Assert.Equal(expected, WifiAdapterState.Prefer(states));
        Assert.Equal(expected, WifiAdapterState.Judge(states.Length, states).ChosenIndex);
    }

    [Fact]
    public void 三态短语各自不同_界面不会拿同一句话顶替另一件事()
    {
        var texts = new[]
        {
            WifiAdapterState.ShortText(WifiAdapterVerdict.Connected),
            WifiAdapterState.ShortText(WifiAdapterVerdict.Ready),
            WifiAdapterState.ShortText(WifiAdapterVerdict.RadioOff),
            WifiAdapterState.ShortText(WifiAdapterVerdict.Absent),
        };
        Assert.Equal(4, texts.Distinct().Count());
        Assert.Equal(new[] { "已连接", "已开启，未连接", "已关闭", "无无线网卡" }, texts);
    }
}
