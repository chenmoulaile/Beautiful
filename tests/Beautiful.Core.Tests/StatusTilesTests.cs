using Beautiful.Core.TopBar;
using Xunit;

/// <summary>
/// 状态瓦片文案测试：电量四档/充电、网络三种形态、蓝牙三态、输入法徽标。
/// 这些字符串直接出现在菜单栏与控制中心，长度变化会让整行抖动，所以在纯函数层钉住。
/// </summary>
public class StatusTilesTests
{
    [Theory]
    [InlineData(82, "82%")]
    [InlineData(-1, "--")]
    [InlineData(140, "100%")]
    [InlineData(-9, "--")]
    public void 电量文字钳制且未知给占位(int percent, string expected)
        => Assert.Equal(expected, StatusTiles.BatteryText(percent));

    [Theory]
    [InlineData(5, false, "battery-0")]
    [InlineData(25, false, "battery-1")]
    [InlineData(50, false, "battery-2")]
    [InlineData(80, false, "battery-3")]
    [InlineData(99, false, "battery-4")]
    [InlineData(50, true, "battery-charging")]
    [InlineData(-1, false, "battery-unknown")]
    public void 电量图标键分档(int percent, bool charging, string expected)
        => Assert.Equal(expected, StatusTiles.BatteryGlyphKey(percent, charging));

    [Fact]
    public void 充电时读数带说明()
    {
        Assert.Equal("45% · 充电中", StatusTiles.BatteryDetail(45, charging: true));
        Assert.Equal("45%", StatusTiles.BatteryDetail(45, charging: false));
    }

    [Theory]
    [InlineData(true, false, null, "以太网")]
    [InlineData(false, true, "CMCC-5G", "CMCC-5G")]
    [InlineData(false, true, "  ", "Wi-Fi")]
    [InlineData(false, false, null, "离线")]
    public void 网络文字覆盖四种形态(bool ethernet, bool wifi, string? ssid, string expected)
        => Assert.Equal(expected, StatusTiles.NetworkText(ethernet, wifi, ssid));

    [Theory]
    [InlineData(true, "开")]
    [InlineData(false, "关")]
    [InlineData(null, "不可用")]
    public void 蓝牙三态(bool? on, string expected)
        => Assert.Equal(expected, StatusTiles.BluetoothText(on));

    [Theory]
    [InlineData("zh-Hans-CN", "中")]
    [InlineData("zh-CN", "中")]
    [InlineData("微软拼音", "中")]
    [InlineData("ja-JP", "あ")]
    [InlineData("ko-KR", "한")]
    [InlineData("en-US", "EN")]
    [InlineData("", "EN")]
    [InlineData(null, "EN")]
    public void 输入法徽标按语言与关键字(string? tag, string expected)
        => Assert.Equal(expected, StatusTiles.InputBadge(tag));

    [Theory]
    [InlineData("chenk", 2, "chenk · USB 2 个")]
    [InlineData("chenk", -1, "chenk")]
    [InlineData("", 0, "未登录 · USB 0 个")]
    public void 账户行拼装(string? user, int usb, string expected)
        => Assert.Equal(expected, StatusTiles.AccountLine(user, usb));
}
