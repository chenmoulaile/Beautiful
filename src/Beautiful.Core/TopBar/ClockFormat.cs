using System.Text;
using Beautiful.Core.Settings;

namespace Beautiful.Core.TopBar;

/// <summary>
/// 顶栏时钟的**可配置形态**（v3.1 缺陷 3）：四个用户开关 → 一个令牌模板 + 一个定时器步长。
/// <para>
/// 【为什么单独有这一个类型】在此之前顶栏时钟只会 <c>ClockFormatter.FullPattern</c> 一种写法，
/// 用户"是否显示秒数 / 日期 / 星期 / 12 小时制"没有落点。把"配置 → 模板"的折算收在 Core 的纯函数里，
/// 好处有三：① UI 只负责勾选框，不参与字符串拼装；② 四种开关的 16 种组合都能被单测穷举；
/// ③ **"显示秒数"同时决定刷新节奏**（见 <see cref="TickSeconds"/>），这条耦合也必须放在能被测试的地方，
/// 否则就会出现"勾了秒但每分钟才跳一次"和"没勾秒却每秒醒一次"两种各自的错。
/// </para>
/// <para>
/// 输出与区域设置无关：<see cref="Pattern"/> 只拼 ASCII 花括号令牌，真正的数字/星期/上下午由
/// <see cref="ClockFormatter"/> 用 <c>InvariantCulture</c> 与代码内词表渲染。
/// </para>
/// </summary>
/// <param name="ShowSeconds">是否显示秒（决定模板形状**和**刷新步长）。</param>
/// <param name="ShowDate">是否显示"几月几日"。</param>
/// <param name="ShowWeekday">是否显示"周几"。</param>
/// <param name="Use24Hour">true = 24 小时制（21:27），false = 12 小时制带时段（9:27 下午）。</param>
public readonly record struct ClockFormat(
    bool ShowSeconds, bool ShowDate, bool ShowWeekday, bool Use24Hour)
{
    /// <summary>
    /// 与 v3.0.0 顶栏观感<b>逐字符相同</b>的默认形态（"10月14日 周三 21:27"，无秒、24 小时制）。
    /// 默认值写在这里而不是散在 UI/设置里，保证"新配置默认值 = 旧版本唯一形态"，升级观感不变。
    /// </summary>
    public static ClockFormat Default => new(ShowSeconds: false, ShowDate: true, ShowWeekday: true, Use24Hour: true);

    /// <summary>设置记录 → 时钟形态（唯一的取值口径来源；App 与单测都经此，不各自读字段）。</summary>
    public static ClockFormat From(AppSettings s) =>
        new(s.ClockShowSeconds, s.ClockShowDate, s.ClockShowWeekday, s.ClockUse24Hour);

    /// <summary>
    /// 刷新步长（秒）：显示秒 → 1 秒；否则 60 秒。
    /// <b>这是"不轮询"的约定值</b>：调用方（顶栏）首个 Interval 用"到下一个边界的余量"对齐，
    /// 之后固定本值；因此既不会出现 <c>mm:50</c> 启动却到 <c>mm+1:50</c> 才跳（v2 定时器对齐的既有裁定），
    /// 也不会用 100ms 轮询去假装"秒级"。
    /// </summary>
    public int TickSeconds => ShowSeconds ? 1 : 60;

    /// <summary>日期 + 星期 + 时间拼成的令牌模板（顺序恒为"日期 星期 时间"，与 v3.0.0 一致）。</summary>
    public string Pattern
    {
        get
        {
            var sb = new StringBuilder(32);
            if (ShowDate) sb.Append("{M}月{d}日 ");
            if (ShowWeekday) sb.Append("{w} ");
            sb.Append(TimePattern());
            return sb.ToString();
        }
    }

    /// <summary>
    /// 时间部分。12 小时制把时段（上午/下午）写在<b>分钟之后</b>（"9:27 下午"）：
    /// 若写成"{tt} {h}:{mm}"，中文 locale 下会把时段顶在最前面，与日期/星期连读成"…周三 上午 9:27"，
    /// 反而比 24 小时制更啰嗦；而秒位插在分钟与时段之间，勾选"显示秒"时只是在中间加长，
    /// 右对齐的状态区里不会把左侧其它状态项推来推去（顶栏侧的观感由该排布保证）。
    /// </summary>
    private string TimePattern() => (Use24Hour, ShowSeconds) switch
    {
        (true, false) => "{HH}:{mm}",
        (true, true) => "{HH}:{mm}:{ss}",
        (false, false) => "{h}:{mm} {tt}",
        (false, true) => "{h}:{mm}:{ss} {tt}",
    };
}
