using System;
using System.Linq;
using Beautiful.Core.Layout;
using Beautiful.Core.Tray;
using Xunit;

/// <summary>
/// 顶栏"系统托盘图标区"的纯算术层：转发消息号、回调消息可信度、按钮状态位、分页切片、
/// 两路去重键、系统控件归类、屏幕取像避让、可点击闸门。
/// <para>
/// 这一层能完全脱离 explorer 单测——真机上有没有 <c>ToolbarWindow32</c>、24H2 是不是已经换成 XAML 岛，
/// 都不影响"读回来的垃圾必须被丢掉、可疑的消息号绝不能发"这些规则被锁住。
/// 尤其 <see cref="TrayLayout.ForwardBlockReason"/>：<c>PostMessage</c> 给错窗口<b>不会报错</b>，
/// 发出去就收不回来，所以闸门只能在发之前，而这道闸门的全部判据都在这里。
/// </para>
/// </summary>
public class TrayLayoutTests
{
    private static PhysicalRect R(int l, int t, int r, int b) => new(l, t, r, b);

    // ---------- 动作 → 消息号 ----------

    [Theory]
    [InlineData(TrayClickAction.LeftClick, TrayLayout.WmLButtonUp)]
    [InlineData(TrayClickAction.RightClick, TrayLayout.WmRButtonUp)]
    [InlineData(TrayClickAction.DoubleClick, TrayLayout.WmLButtonDblClk)]
    public void 动作折成按钮释放消息_不发按下(TrayClickAction action, uint expected)
        => Assert.Equal(expected, TrayLayout.ClickMessage(action));

    [Fact]
    public void 未知动作退回左键释放_不返回零()
    {
        // 返回 0 意味着"给应用发一个 0 号消息"= 未定义行为；越界枚举只能落回最安全的一档
        Assert.Equal(TrayLayout.WmLButtonUp, TrayLayout.ClickMessage((TrayClickAction)(-1)));
    }

    // ---------- 回调消息可信度 ----------

    [Theory]
    [InlineData(0x0400u, true)]   // WM_USER 起点：应用自定义 #define
    [InlineData(0x0800u, true)]
    [InlineData(0x08FFu, true)]   // 私有段上界
    [InlineData(0x0900u, false)]  // 越界即可疑
    [InlineData(0x0202u, false)]  // 系统消息号：拿它当回调消息发就是发错语义
    [InlineData(0x007Bu, false)]  // WM_CONTEXTMENU 同理
    [InlineData(0xC000u, true)]   // RegisterWindowMessage 段
    [InlineData(0xFFFFu, true)]
    [InlineData(0x1_0000u, false)]
    [InlineData(0u, false)]      // 没读出来
    public void 只有两档消息号允许转发(uint message, bool plausible)
        => Assert.Equal(plausible, TrayLayout.IsPlausibleCallback(message));

    // ---------- 按钮状态位 ----------

    [Theory]
    [InlineData((byte)0x01, true)]   // BTNS_SEP
    [InlineData((byte)0x05, true)]   // 分隔符 + 其他位
    [InlineData((byte)0x00, false)]
    [InlineData((byte)0x04, false)]  // BTNS_CHECK 不是分隔符
    public void 分隔符不占一格(byte fsStyle, bool isSep)
        => Assert.Equal(isSep, TrayLayout.IsSeparator(fsStyle));

    [Theory]
    [InlineData((byte)0x08, true)]   // TBSTATE_HIDDEN
    [InlineData((byte)0x0C, true)]   // HIDDEN + PRESSED
    [InlineData((byte)0x04, false)]
    public void 已隐藏的条目不列出来(byte fsState, bool hidden)
        => Assert.Equal(hidden, TrayLayout.IsHidden(fsState));

    // ---------- 垃圾返回值钳制 ----------

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(7, 7)]
    [InlineData(TrayLayout.MaxButtonsPerToolbar, TrayLayout.MaxButtonsPerToolbar)]
    [InlineData(9999, TrayLayout.MaxButtonsPerToolbar)]
    [InlineData(int.MaxValue, TrayLayout.MaxButtonsPerToolbar)]
    public void 自报按钮数一律钳进上限(int reported, int expected)
        => Assert.Equal(expected, TrayLayout.ClampButtonCount(reported));

    // ---------- 矩形有效性 ----------

    [Theory]
    [InlineData(0, 0, 24, 24, true)]
    [InlineData(-1920, 0, -1896, 24, true)]     // 左置副屏：Left 为负是合法的
    [InlineData(0, 0, 0, 0, false)]             // TB_GETITEMRECT 失败 = 全零
    [InlineData(10, 10, 10, 20, false)]         // 零宽
    [InlineData(10, 10, 20, 10, false)]         // 零高
    [InlineData(10, 20, 10, 10, false)]         // 倒挂（宽高为负）
    [InlineData(int.MinValue, int.MinValue, int.MaxValue, int.MaxValue, false)]  // Rect.Empty 转出来的极值
    public void 只信面积有效且没爆边的矩形(
        int l, int t, int r, int b, bool usable)
        => Assert.Equal(usable, TrayLayout.IsUsableRect(R(l, t, r, b)));

    [Fact]
    public void 贴边不算相交()
    {
        Assert.False(TrayLayout.IntersectsWith(R(0, 0, 10, 10), R(10, 0, 20, 10)));  // 共享右/左缘
        Assert.True(TrayLayout.IntersectsWith(R(0, 0, 10, 10), R(9, 9, 20, 20)));
    }

    [Fact]
    public void 自家表面压住图标时不允许裁屏取图()
    {
        var icon = R(100, 40, 124, 64);
        Assert.True(TrayLayout.CanCropFromScreen(icon, null));                       // 不检查
        Assert.True(TrayLayout.CanCropFromScreen(icon, System.Array.Empty<PhysicalRect>()));
        Assert.True(TrayLayout.CanCropFromScreen(icon, [R(0, 0, 50, 20)]));           // 自家条带在别处
        Assert.False(TrayLayout.CanCropFromScreen(icon, [R(80, 30, 400, 70)]));       // 被 Dock 盖住 → 裁到的是我们的玻璃
        Assert.False(TrayLayout.CanCropFromScreen(icon, [R(0, 0, 10, 10), R(80, 30, 400, 70)]));  // 清单里任一块压住就不裁
        Assert.False(TrayLayout.CanCropFromScreen(R(0, 0, 0, 0), null));              // 矩形本身不可信
    }

    // ---------- 分页 ----------

    [Theory]
    [InlineData(0, 24, 1)]      // 空态也有一页可画
    [InlineData(1, 24, 1)]
    [InlineData(24, 24, 1)]
    [InlineData(25, 24, 2)]
    [InlineData(50, 24, 3)]
    [InlineData(50, 0, 3)]      // pageSize<=0 退回默认 24
    public void 页数向上取整(int total, int pageSize, int expected)
        => Assert.Equal(expected, TrayLayout.PageCount(total, pageSize));

    [Theory]
    [InlineData(50, 24, -5, 0)]
    [InlineData(50, 24, 0, 0)]
    [InlineData(50, 24, 2, 2)]
    [InlineData(50, 24, 9, 2)]   // 重读后条目变少：越界页号拉回最后一页，不要白屏
    [InlineData(0, 24, 3, 0)]
    public void 页号钳进合法区间(int total, int pageSize, int page, int expected)
        => Assert.Equal(expected, TrayLayout.ClampPage(page, total, pageSize));

    [Fact]
    public void 切片给出起始与条数_末页短一页()
    {
        Assert.Equal((0, 24), TrayLayout.Slice(50, 0, 24));
        Assert.Equal((24, 24), TrayLayout.Slice(50, 1, 24));
        Assert.Equal((48, 2), TrayLayout.Slice(50, 2, 24));    // 末页只有 2 条
        Assert.Equal((0, 0), TrayLayout.Slice(0, 0, 24));
    }

    [Fact]
    public void 切片加起来就是全部条目_不重不漏()
    {
        const int total = 61;
        const int size = 24;
        var pages = TrayLayout.PageCount(total, size);
        var seen = Enumerable.Range(0, pages)
                             .SelectMany(p => { var (start, take) = TrayLayout.Slice(total, p, size);
                                                return Enumerable.Range(start, take); })
                             .ToArray();
        Assert.Equal(total, seen.Length);
        Assert.Equal(Enumerable.Range(0, total), seen);   // 顺序连续、无重复
    }

    // ---------- 两路去重 ----------

    [Theory]
    [InlineData("Snipaste", 1u, "Snipaste ", 1u)]
    [InlineData("  Foo  ", 7u, "foo", 7u)]
    [InlineData("中文名字", 3u, "中文名字", 3u)]
    public void 同名的两条目合并成一条(string a, uint uidA, string b, uint uidB)
        => Assert.Equal(TrayLayout.MergeKey(a, uidA), TrayLayout.MergeKey(b, uidB));

    [Fact]
    public void 不同名不合并_都没名时按编号分()
    {
        Assert.NotEqual(TrayLayout.MergeKey("foo", 1u), TrayLayout.MergeKey("bar", 1u));
        Assert.Equal(TrayLayout.MergeKey(null, 5u), TrayLayout.MergeKey("   ", 5u));
        Assert.NotEqual(TrayLayout.MergeKey("", 5u), TrayLayout.MergeKey("", 6u));
    }

    // ---------- 归类 ----------

    /// <summary>
    /// 只有<b>整名相等</b>才允许判成系统项：子串匹配会把真实应用的图标藏起来
    /// （"PowerToys 托盘"里有 power、"百度网盘"、"XX 通知助手"里有"通知"）——
    /// 而这些格子根本不该被藏，界面和日志都不会说原因，是最难反馈的故障形态。
    /// </summary>
    [Theory]
    [InlineData("Discord", null, TrayIconCategory.Unknown)]
    [InlineData("网易云音乐", null, TrayIconCategory.Unknown)]
    [InlineData("PowerToys 托盘", null, TrayIconCategory.Unknown)]     // 含 power，但不是整名 → 必须留下
    [InlineData("百度网盘", null, TrayIconCategory.Unknown)]             // 含"网" → 必须留下
    [InlineData("XX 通知助手", null, TrayIconCategory.Unknown)]          // 含"通知" → 必须留下
    [InlineData("Volume 混合器", null, TrayIconCategory.Unknown)]        // 名单随版本过期：认不准就照常列出
    [InlineData(null, "AppExceptionDialog", TrayIconCategory.Unknown)]
    [InlineData("显示隐藏的图标", null, TrayIconCategory.SystemOwned)]
    [InlineData("Show hidden icons", null, TrayIconCategory.SystemOwned)]
    [InlineData("Notification Chevron", null, TrayIconCategory.SystemOwned)]
    [InlineData("音量", null, TrayIconCategory.SystemOwned)]
    [InlineData("Internet 访问", null, TrayIconCategory.SystemOwned)]
    [InlineData(" battery ", null, TrayIconCategory.SystemOwned)]       // 首尾空白与大小写不影响整名相等
    [InlineData(null, "SystemTray.NotifyIconShowHiddenIcons", TrayIconCategory.SystemOwned)]
    public void 只把有把握的系统项剔掉(
        string? label, string? automationId, TrayIconCategory expected)
        => Assert.Equal(expected, TrayLayout.Classify(label, automationId, TrayIconOrigin.UiaElement));

    [Theory]
    [InlineData("Discord", TrayIconCategory.ThirdParty)]
    [InlineData("音量", TrayIconCategory.SystemOwned)]     // 系统名单优先：整名命中就剔
    [InlineData("", TrayIconCategory.ThirdParty)]          // 没名字也确信：那个工具栏按构造只放应用图标
    public void 经典路给得出第三方这个确信(string label, TrayIconCategory expected)
        => Assert.Equal(expected, TrayLayout.Classify(label, null, TrayIconOrigin.ClassicToolbar));

    [Fact]
    public void 认不准的条目照样显示_不藏起来()
    {
        // 藏起来是"用户看不到、日志里也没有"的故障；多列一格会带 ToolTip 说明来源，代价小得多
        Assert.Equal(TrayIconCategory.Unknown, TrayLayout.Classify(null, null, TrayIconOrigin.UiaElement));
        Assert.True(TrayLayout.ShouldDisplay(TrayIconCategory.Unknown));
        Assert.True(TrayLayout.ShouldDisplay(TrayIconCategory.ThirdParty));
        Assert.False(TrayLayout.ShouldDisplay(TrayIconCategory.SystemOwned));
    }

    // ---------- 排序 ----------

    [Fact]
    public void 无名字的条目排在最后()
    {
        var names = new[] { "Zed", null, "apple", "  " };
        var ordered = names.OrderBy(TrayLayout.SortKey, StringComparer.Ordinal).ToArray();
        Assert.Equal("apple", ordered[0]);
        Assert.Equal("Zed", ordered[1]);
        Assert.All(ordered.Skip(2), n => Assert.True(string.IsNullOrWhiteSpace(n)));
    }

    [Fact]
    public void 排序忽略大小写_不随区域设置摆动()
        => Assert.Equal(TrayLayout.SortKey("ABC"), TrayLayout.SortKey("abc"));

    // ---------- 转发闸门 ----------

    [Fact]
    public void 句柄_消息号_编号_三者齐全才允许转发()
        => Assert.Null(TrayLayout.ForwardBlockReason(
            hasOwner: true, callbackMessage: 0xD3C2u, uidMatches: true, ownerHung: false,
            origin: TrayIconOrigin.ClassicToolbar));

    [Theory]
    [InlineData(false, 0xD3C2u, true, false)]    // 认不出主人
    [InlineData(true, 0x0900u, true, false)]     // 消息号可疑
    [InlineData(true, 0xD3C2u, false, false)]    // 编号交叉校验失败
    [InlineData(true, 0xD3C2u, true, true)]      // 应用未响应
    public void 经典路四种情况各自被挡并给出原因(
        bool hasOwner, uint message, bool uidMatches, bool hung)
    {
        var reason = TrayLayout.ForwardBlockReason(hasOwner, message, uidMatches, hung,
                                                   TrayIconOrigin.ClassicToolbar);
        // 被挡必须带中文原因：界面靠这一行说明"为什么这格不能点"，不留死控件
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public void uia_路的条目不走句柄闸门()
        => Assert.Null(TrayLayout.ForwardBlockReason(
            hasOwner: false, callbackMessage: 0, uidMatches: false, ownerHung: false,
            origin: TrayIconOrigin.UiaElement));
}
