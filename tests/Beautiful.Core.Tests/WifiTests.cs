using System;
using System.Linq;
using System.Xml.Linq;
using Beautiful.Core.Network;
using Xunit;

/// <summary>
/// WiFi 面板的纯函数层：信号档位、列表排序、加密方式标签、可直连判定、profile XML 生成。
/// <para>
/// 这层能完全脱离网卡单测——真机有没有 WiFi 适配器、连得上连不上，都不影响这些规则被锁住。
/// 尤其 profile XML：SSID 是外部输入，转义漏了就是把结构交给网络名决定；而 <c>keyMaterial</c>
/// 含明文口令，所以生成逻辑必须集中在一处、可核对。
/// </para>
/// </summary>
public class WifiTests
{
    private static WifiNetwork N(string ssid, int quality, bool connected = false)
        => new(ssid, quality, connected, "WPA2-AES", true, null);

    // ---------- 信号档位 ----------

    [Theory]
    [InlineData(0, 0)]
    [InlineData(24, 0)]
    [InlineData(25, 1)]
    [InlineData(49, 1)]
    [InlineData(50, 2)]
    [InlineData(74, 2)]
    [InlineData(75, 3)]
    [InlineData(99, 3)]
    [InlineData(100, 4)]
    public void 信号百分比折成四格(int quality, int expectedBars)
        => Assert.Equal(expectedBars, WifiSignal.Bars(quality));

    // ---------- 列表排序 ----------

    [Fact]
    public void 已连接置顶_其次信号强_再按名字忽略大小写()
    {
        var ordered = WifiSignal.Order(new[]
        {
            N("zzz", 90),
            N("Home", 40),
            N("work", 80),
            N("Home", 80, connected: true),
        });

        Assert.Equal(new[] { "Home", "zzz", "work", "Home" }, ordered.Select(n => n.Ssid).ToArray());
        Assert.True(ordered[0].Connected);
    }

    [Fact]
    public void 空列表排序不抛() => Assert.Empty(WifiSignal.Order(Array.Empty<WifiNetwork>()));

    // ---------- 加密方式标签与可直连判定 ----------

    [Theory]
    [InlineData(WifiSecurity.Auth80211Open, WifiSecurity.EncCcmp128, "开放")]
    [InlineData(WifiSecurity.Auth80211Open, 0, "开放")]
    [InlineData(WifiSecurity.AuthWpa2, WifiSecurity.EncCcmp128, "WPA2-AES")]
    [InlineData(WifiSecurity.AuthWpa2, WifiSecurity.EncTkip, "WPA2-TKIP")]
    [InlineData(WifiSecurity.AuthWpa, WifiSecurity.EncTkip, "WPA-TKIP")]
    [InlineData(WifiSecurity.AuthWpa3, WifiSecurity.EncCcmp128, "WPA3-AES")]
    [InlineData(WifiSecurity.AuthWpa3Enterprise, WifiSecurity.EncCcmp128, "WPA3 企业级-AES")]
    [InlineData(9999, 0, "未知")]
    public void 加密方式给人看的标签(int auth, int enc, string expected)
        => Assert.Equal(expected, WifiSecurity.Describe(auth, enc));

    [Theory]
    [InlineData(WifiSecurity.Auth80211Open, true)]
    [InlineData(WifiSecurity.AuthWpa2, true)]
    [InlineData(WifiSecurity.AuthWpa3, true)]
    [InlineData(WifiSecurity.AuthWpa3Enterprise, false)]
    [InlineData(WifiSecurity.AuthFcuWpaWpa2, false)]
    [InlineData(9999, false)]
    public void 企业级与未知加密一律不给直连(int auth, bool joinable)
    {
        Assert.Equal(joinable, WifiSecurity.CanJoinDirectly(auth, out var reason));
        // 不可直连必须带原因：置灰项要点得出"为什么"，不留死控件
        Assert.Equal(joinable, string.IsNullOrEmpty(reason));
    }

    [Fact]
    public void 开放网络不要口令()
    {
        Assert.False(WifiSecurity.NeedsPassphrase(WifiSecurity.Auth80211Open));
        Assert.True(WifiSecurity.NeedsPassphrase(WifiSecurity.AuthWpa2));
    }

    // ---------- profile XML ----------

    [Fact]
    public void 开放网络_profile_不含口令节点()
    {
        var xml = WifiProfileXml.Build("CafeGuest", WifiSecurity.Auth80211Open, null);

        Assert.Contains("<authentication>open</authentication>", xml);
        Assert.Contains("<encryption>none</encryption>", xml);
        Assert.DoesNotContain("keyMaterial", xml);
        Assert.NotNull(XDocument.Parse(xml));   // 结构合法
    }

    [Fact]
    public void WPA2_profile_是个人版写法且带口令节点()
    {
        var xml = WifiProfileXml.Build("Home", WifiSecurity.AuthWpa2, "s3cret");

        Assert.Contains("<authentication>WPA2</authentication>", xml);
        Assert.Contains("<encryption>AES</encryption>", xml);
        Assert.Contains("<useOneX>false</useOneX>", xml);   // true 就是企业级了
        Assert.Contains("<keyMaterial>s3cret</keyMaterial>", xml);
        Assert.NotNull(XDocument.Parse(xml));
    }

    [Fact]
    public void SSID_含XML元字符_必须转义且不破坏结构()
    {
        const string nasty = "A&B<x>\"c\"'d";
        var xml = WifiProfileXml.Build(nasty, WifiSecurity.Auth80211Open, null);

        Assert.Contains("A&amp;B&lt;x&gt;&quot;c&quot;&apos;d", xml);
        // 真解析一遍：名字取回来必须与原文完全一致，才算没被结构吃掉
        var doc = XDocument.Parse(xml);
        var ns = doc.Root!.GetDefaultNamespace();
        var name = (string?)doc.Root.Element(ns + "name");
        Assert.Equal(nasty, name);
    }

    [Fact]
    public void 口令里的元字符同样转义()
    {
        var xml = WifiProfileXml.Build("Net", WifiSecurity.AuthWpa2, "p&ss<w>rd");
        var doc = XDocument.Parse(xml);
        var ns = doc.Root!.GetDefaultNamespace();
        var key = doc.Descendants(ns + "keyMaterial").Single().Value;
        Assert.Equal("p&ss<w>rd", key);
    }

    [Fact]
    public void 隐藏网络_带上广播标记()
    {
        var xml = WifiProfileXml.Build("Hidden", WifiSecurity.AuthWpa2, "pw", hidden: true);

        Assert.Contains("<hidden>true</hidden>", xml);
        Assert.Contains("<nonBroadcast>true</nonBroadcast>", xml);
        Assert.NotNull(XDocument.Parse(xml));
    }

    [Fact]
    public void 连接模式是manual_不给系统留下开机自动重连的副作用()
    {
        var xml = WifiProfileXml.Build("Net", WifiSecurity.AuthWpa2, "pw");
        Assert.Contains("<connectionMode>manual</connectionMode>", xml);
    }

    [Theory]
    [InlineData(WifiSecurity.AuthWpa2)]
    [InlineData(WifiSecurity.AuthWpa3)]
    [InlineData(WifiSecurity.AuthWpa)]
    public void 需要口令的加密方式_没给口令就拒(int auth)
        => Assert.Throws<ArgumentException>(() => WifiProfileXml.Build("Net", auth, null));

    [Theory]
    [InlineData("")]
    [InlineData("bad\name")]
    [InlineData("tab\there")]
    public void 非法SSID直接拒(string ssid)
        => Assert.Throws<ArgumentException>(() => WifiProfileXml.Build(ssid, WifiSecurity.Auth80211Open, null));

    [Fact]
    public void 企业级认证不生成profile()
        => Assert.Throws<ArgumentException>(() =>
            WifiProfileXml.Build("Corp", WifiSecurity.AuthWpa3Enterprise, "pw"));
}
