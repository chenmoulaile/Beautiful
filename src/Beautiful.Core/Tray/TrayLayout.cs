using Beautiful.Core.Layout;

namespace Beautiful.Core.Tray;

/// <summary>图标条目的来源路（面板要把"哪一路读到的"如实标在提示里，两路的可点击能力不同）。</summary>
public enum TrayIconOrigin
{
    /// <summary>经典路：<c>ToolbarWindow32</c>（跨进程读 explorer 的按钮数组）。</summary>
    ClassicToolbar,

    /// <summary>UIA 路：<c>System.Windows.Automation</c> 里的 Button 元素（Win11 24H2 的 XAML 岛）。</summary>
    UiaElement
}

/// <summary>条目归类。判定只用于"要不要把 shell 自己的控件也当托盘图标列出来"，见 <see cref="TrayLayout.Classify"/>。</summary>
public enum TrayIconCategory
{
    /// <summary>
    /// 第三方应用的通知区域图标：本功能的目标。<b>只有经典路能给出这个确信</b>
    /// （<c>User Promoted Notification Area</c> 工具栏按构造只放应用注册的图标，见
    /// <see cref="TrayLayout.Classify"/>）。
    /// </summary>
    ThirdParty,

    /// <summary>系统自己的通知区域控件（网络/音量/电池/"显示隐藏的图标"/通知中心）：不进平铺区。
    /// 它们是 shell 的部件，列出来只会让用户点到一个不像托盘图标的东西；面板底部另有"系统通知设置…"这条出路。</summary>
    SystemOwned,

    /// <summary>认不准的条目。<b>按第三方一样显示</b>（宁可多列一个真图标，也不要因为一张过期的名单把用户的图标藏起来）。</summary>
    Unknown
}

/// <summary>用户在一格图标上做的动作。<see cref="TrayLayout.ClickMessage"/> 负责把它折成要转发给应用的消息号。</summary>
public enum TrayClickAction
{
    /// <summary>左键单击（多数应用在这里弹自己的菜单或直接激活窗口）。</summary>
    LeftClick,

    /// <summary>右键（应用的上下文菜单）。</summary>
    RightClick,

    /// <summary>双击。</summary>
    DoubleClick
}

/// <summary>
/// 托盘图标区的<b>纯算术层</b>：消息号映射、按钮状态位判定、分页切片、两路去重键、回调消息可信度判定、
/// 系统控件归类、屏幕取像的避让判定。全部无 Win32/WPF 依赖，因此这些规则能被单测锁住
/// （真机上"这台机器的托盘长什么样"本 VM 证不了，但"读回来的东西怎么切页、什么消息号能发、哪些条目该丢"完全可以）。
/// <para>
/// 【为什么判定必须在 Core 而不是写在遍历循环里】<c>ToolbarWindow32</c> 的跨进程读取只有一条容错策略：
/// 任何字段都可能返回垃圾（explorer 版本不同、提权差异、24H2 上那个窗口可能根本不是工具栏）。
/// "垃圾要不要丢弃、丢了之后界面说什么"这类判断如果散在 IO 代码里，就既不能单测也不能在两路之间复用；
/// 收口到这里之后两条枚举路共用同一套取舍口径，界面与算术层对"什么算一个可信条目"永远不会有两种解释。
/// </para>
/// <para>
/// 【宁可空框也不错画（本层的裁定口径）】读不到位图、认不出 owner 窗口、消息号可疑时，这里一律回
/// "不行 + 判据"，由界面把该格标成只读并写明原因。猜一个图标画上去、或朝一个认不出主人的窗口发消息，
/// 代价分别是"用户以为那是它的图标"和"把点击发给别人的窗口"——两者都比空框严重。
/// </para>
/// </summary>
public static class TrayLayout
{
    // ---------- 消息号（与 winuser.h 一致） ----------

    /// <summary><c>WM_LBUTTONUP</c>。</summary>
    public const uint WmLButtonUp = 0x0202;

    /// <summary><c>WM_RBUTTONUP</c>。</summary>
    public const uint WmRButtonUp = 0x0205;

    /// <summary><c>WM_LBUTTONDBLCLK</c>。</summary>
    public const uint WmLButtonDblClk = 0x0203;

    /// <summary>托盘回调消息的下界：应用自定义消息从 <c>WM_USER</c>(0x400) 起。</summary>
    public const uint WmUser = 0x0400;

    /// <summary><c>RegisterWindowMessage</c> 返回值下界（0xC000–0xFFFF）。绝大多数托盘应用用的是这一档。</summary>
    public const uint RegisteredMessageMin = 0xC000;

    /// <summary><c>WM_USER</c> 段里认为"私有但合理"的上界：再高就撞上系统保留段，宁可判可疑。</summary>
    public const uint WmPrivateMax = 0x08FF;

    /// <summary>
    /// 动作 → 转发给 owner 窗口的 <c>lParam</c> 消息号。
    /// <para>【为什么只发 <c>*BUTTONUP</c>，不发 DOWN】通知区域的系统语义是"shell 把按钮消息经应用的回调
    /// 窗口转达"。应用普遍在 <c>UP</c> 上做事；先发 <c>DOWN</c> 会让部分应用进入鼠标跟踪状态
    /// （有些自绘菜单等不到后续 MOVE 就永远不收），我们只发一次 UP 是"按下并松开这一次"的最小可信表达。</para>
    /// <para>【为什么不发 <c>WM_CONTEXTMENU</c>(0x7B)】确实有少数应用只在那里做动作，但同一个右键既发
    /// <c>WM_RBUTTONUP</c> 又发 <c>WM_CONTEXTMENU</c> 会让"两个分支都实现了"的常见应用弹<b>两层</b>菜单。
    /// 系统只会发一次按钮消息，我们就照系统的样子只发一次。</para>
    /// </summary>
    public static uint ClickMessage(TrayClickAction action) => action switch
    {
        TrayClickAction.LeftClick => WmLButtonUp,
        TrayClickAction.RightClick => WmRButtonUp,
        TrayClickAction.DoubleClick => WmLButtonDblClk,
        _ => WmLButtonUp,
    };

    /// <summary>
    /// 从 explorer 内存里读回来的"回调消息号"是否可信。
    /// <para>可信只有两档：应用自己 <c>#define</c> 的 <c>WM_USER+n</c>（限到 <see cref="WmPrivateMax"/>）
    /// 和 <c>RegisterWindowMessage</c> 的 0xC000–0xFFFF。其余（0、中间的系统消息段、垃圾）判不可信：
    /// 拿它去 <c>PostMessage</c> 就是朝一个来路不明的窗口发自定义消息，可能触发目标程序的未知分支。</para>
    /// </summary>
    public static bool IsPlausibleCallback(uint message)
        => (message >= WmUser && message <= WmPrivateMax) || (message >= RegisteredMessageMin && message <= 0xFFFF);

    // ---------- 工具栏按钮状态位 ----------

    /// <summary><c>BTNS_SEP</c>（工具栏分隔符）：没有图标也没有 owner，必须跳过——列出来就是一格永远点不动的东西。</summary>
    public const byte BtnsSeparator = 0x01;

    /// <summary><c>TBSTATE_HIDDEN</c>：explorer 标记为不显示的按钮（进程已退但条目未回收的常见形态）。</summary>
    public const byte TbStateHidden = 0x08;

    /// <summary>样式位是不是分隔符。</summary>
    public static bool IsSeparator(byte fsStyle) => (fsStyle & BtnsSeparator) != 0;

    /// <summary>状态位是不是"已隐藏"。</summary>
    public static bool IsHidden(byte fsState) => (fsState & TbStateHidden) != 0;

    // ---------- 垃圾与越界防护 ----------

    /// <summary>
    /// 单个工具栏一次读取的按钮数上限。
    /// <para>【为什么必须有上界】按钮数来自跨进程 <c>SendMessageTimeout</c>，24H2 上那个窗口可能根本
    /// 不是工具栏、返回值就是垃圾。没有上界的话一次读取会去循环几万次跨进程 send 加远程读写
    /// （驻留进程里的自杀式抖动）。超限即判"这一路读不通"，界面写原因。</para>
    /// </summary>
    public const int MaxButtonsPerToolbar = 128;

    /// <summary>把工具栏自报的按钮数钳到 <c>[0, <see cref="MaxButtonsPerToolbar"/>]</c>；负数与超限都只取边界值。</summary>
    public static int ClampButtonCount(int reported)
        => Math.Clamp(reported, 0, MaxButtonsPerToolbar);

    /// <summary>一页平铺多少格（卡片宽 330 DIP、一格 40 DIP → 一行 7 格；24 = 约三行半，再多就分页）。</summary>
    public const int DefaultPageSize = 24;

    // ---------- 几何有效性 ----------

    /// <summary>
    /// 矩形是否可信（右&gt;左、下&gt;上，且四个边都在 int 的安全范围内）。
    /// UIA 的 <c>BoundingRectangle</c> 在元素不可见/已脱离树时返回 <c>Empty</c>（分量是 ±∞），
    /// <c>TB_GETITEMRECT</c> 失败时返回全 0 —— 两者都必须在这里挡下，否则要么拿 ±∞ 转 int 炸掉，
    /// 要么在屏幕原点裁出一块不属于任何图标的像素。
    /// </summary>
    public static bool IsUsableRect(in PhysicalRect rect)
    {
        const int safe = int.MaxValue / 2;   // 32 位 int 的一半：±10 亿，真实屏幕坐标永远到不了，±∞ 转来的值一定越界
        return rect.Right > rect.Left && rect.Bottom > rect.Top
               && rect.Left > -safe && rect.Right < safe
               && rect.Top > -safe && rect.Bottom < safe;
    }

    /// <summary>两矩形是否相交（共享一条边不算相交）。</summary>
    public static bool IntersectsWith(in PhysicalRect a, in PhysicalRect b)
        => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    /// <summary>
    /// 这个图标矩形的屏幕像素能不能用作它的图。
    /// <para>【为什么需要避让清单】本项目的取图做法是<b>把真实托盘那一格的屏幕像素裁下来</b>
    /// （跨进程取 <c>HIMAGELIST</c> 在 Win11 24H2 上做不到，理由见 App 侧注释）。裁屏唯一的风险是
    /// "裁到的其实是自家表面"：常驻 Dock 就贴在屏幕底边、经常盖住真实任务栏，那一刻那块像素是我们的
    /// 玻璃条而不是那个应用的图标——那正是"画错图标"。相交时宁可不用，退回占位字形。</para>
    /// </summary>
    /// <param name="iconPx">图标在屏幕上的物理矩形。</param>
    /// <param name="avoidPx">自家可见表面矩形清单（null/空 = 不检查）。</param>
    public static bool CanCropFromScreen(in PhysicalRect iconPx, IReadOnlyList<PhysicalRect>? avoidPx)
    {
        if (!IsUsableRect(iconPx)) return false;
        if (avoidPx is null) return true;
        for (var i = 0; i < avoidPx.Count; i++)
            if (IntersectsWith(iconPx, avoidPx[i])) return false;
        return true;
    }

    // ---------- 分页 ----------

    /// <summary>页数：向上取整；<paramref name="total"/> 为 0 时仍给 1 页（空态也要有一页可画，分页按钮才不会撞上除零）。</summary>
    public static int PageCount(int total, int pageSize)
    {
        if (total <= 0) return 1;
        var size = pageSize <= 0 ? DefaultPageSize : pageSize;
        return (total + size - 1) / size;
    }

    /// <summary>页号钳到 <c>[0, PageCount-1]</c>。重读后条目变少、当前页越界是最常见的竞态，界面不该白屏。</summary>
    public static int ClampPage(int page, int total, int pageSize)
        => Math.Clamp(page, 0, PageCount(total, pageSize) - 1);

    /// <summary>当前页要画的起始下标与条数。<paramref name="total"/> &lt;= 0 回 <c>(0, 0)</c>，绝不回负数。</summary>
    public static (int Start, int Take) Slice(int total, int page, int pageSize)
    {
        if (total <= 0) return (0, 0);
        var size = pageSize <= 0 ? DefaultPageSize : pageSize;
        var start = ClampPage(page, total, size) * size;
        if (start >= total) return (0, 0);
        return (start, Math.Min(size, total - start));
    }

    // ---------- 两路去重 ----------

    /// <summary>
    /// 去重键：同一条目会被两路各读到一次（23H2 及以下两条路同时成立），不去重就是用户看到两个一样的格子。
    /// <para>取法刻意保守：优先用"tooltip/Name 文本归一化"（两路都能拿到同一份应用自己写的文字），
    /// 完全没有文本时退回 <c>uID</c>。<b>不</b>用 owner 窗口句柄做键——UIA 路根本没有句柄，
    /// 混用两种键会让同一图标既可能合并也可能不合并（不可预期的合并比不合并更难查）。</para>
    /// </summary>
    public static string MergeKey(string? label, uint uid)
    {
        var text = (label ?? "").Trim();
        return text.Length == 0 ? $"uid:{uid}" : $"txt:{text.ToLowerInvariant()}";
    }

    // ---------- 归类 ----------

    /// <summary>
    /// shell 自己的"部件"（不是某个应用的图标）：<b>整名相等</b>才认（忽略大小写与首尾空白）。
    /// 【为什么用整名相等而不是子串】子串会吃掉真实应用的图标名——"PowerToys 托盘"里有 <c>power</c>、
    /// "百度网盘"里有"网"、"XX 通知助手"里有"通知"。把这些格子藏起来的故障是"用户的图标不见了、
    /// 界面与日志都不说原因"，比多显示一格系统状态项严重得多。
    /// </summary>
    private static readonly HashSet<string> ShellPartNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "显示隐藏的图标", "隐藏图标", "show hidden icons", "hidden icons", "notification chevron",
        "通知区域", "notification area", "taskbar button list",
    };

    /// <summary>
    /// 系统自己的状态项（网络/音量/电池一类）。同样<b>整名相等</b>才认（名单随版本与语言过期是常态）。
    /// <para>【认错方向的代价其实很小】这些格子即使显示出来也不是"假控件"：点它走的是同一条转发链路，
    /// 等于点真实的那一个（真实音量图标被 Invoke 就打开真实音量浮层）。所以宁可漏认——漏认只是多一格，
    /// 误认才是把第三方图标藏起来。</para>
    /// </summary>
    private static readonly HashSet<string> SystemStatusNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "网络", "internet 访问", "internet access", "network",
        "音量", "volume", "音量混合器", "volume mixer", "扬声器", "speakers",
        "电池", "battery", "电源", "power",
        "时钟", "日期和时间", "date and time", "clock",
        "windows 安全中心", "windows security center", "security center",
        "操作中心", "action center", "通知", "notifications", "焦点", "focus",
        "one drive", "onedrive", "触摸键盘", "touch keyboard", "笔操作", "pen operation",
        "windows 语音识别", "windows voice access",
    };

    /// <summary>
    /// 归类一个条目。
    /// <para>【为什么默认判 <see cref="TrayIconCategory.Unknown"/>（=照常列出）】这个功能的目标是
    /// "把第三方托盘图标平铺出来"，而任何一张"系统控件名单"都会随 Windows 版本与界面语言过期。
    /// 判成系统项等于<b>悄悄把用户的图标藏起来</b>；判成第三方最多多出一格，那一格的 ToolTip 会写明
    /// 来源与能力，代价小得多。名单见 <see cref="ShellPartNames"/> 与 <see cref="SystemStatusNames"/>。</para>
    /// <para>【两条路能给的确信度不同，这个差别值得写进类型】<paramref name="origin"/> 是经典路时判
    /// <see cref="TrayIconCategory.ThirdParty"/>：<c>User Promoted Notification Area</c> 那个工具栏
    /// 按构造只放<b>应用</b>自己注册的图标，shell 的状态项不在里面，所以"没落到系统名单上"在这里
    /// 是真的能确定。UIA 路只能判 <see cref="TrayIconCategory.Unknown"/>——那棵树把状态项和第三方图标
    /// 混在同一层里，光看 Name 分不出来。</para>
    /// </summary>
    /// <param name="label">UIA Name 或工具栏 tooltip 文本（可为空）。</param>
    /// <param name="automationId">UIA AutomationId（经典路没有，传 null）。只用它认"展开隐藏区"那一个按钮。</param>
    /// <param name="origin">哪一路读到的（决定"没命中名单"能推到哪一步）。</param>
    public static TrayIconCategory Classify(string? label, string? automationId, TrayIconOrigin origin)
    {
        var name = (label ?? "").Trim();
        if (name.Length > 0 && (ShellPartNames.Contains(name) || SystemStatusNames.Contains(name)))
            return TrayIconCategory.SystemOwned;
        // AutomationId 是宿主自己写的英文常量，比本地化名字稳定；只认这一个明确的 token，
        // 不认包含 "overflow"/"tray" 之类的宽泛子串——隐藏区里的第三方图标也可能带这类前缀。
        var id = (automationId ?? "").Trim();
        if (id.Length > 0 && id.Contains("ShowHiddenIcons", StringComparison.OrdinalIgnoreCase))
            return TrayIconCategory.SystemOwned;
        return origin == TrayIconOrigin.ClassicToolbar ? TrayIconCategory.ThirdParty : TrayIconCategory.Unknown;
    }

    /// <summary>
    /// 这一路读到的条目该不该出现在平铺区。<see cref="TrayIconCategory.SystemOwned"/> 不进，
    /// 其余都进（含 <see cref="TrayIconCategory.Unknown"/>，理由见 <see cref="Classify"/>）。
    /// </summary>
    public static bool ShouldDisplay(TrayIconCategory category) => category != TrayIconCategory.SystemOwned;

    /// <summary>
    /// 排序键（显示名小写；无名字的排到最后）。
    /// 【为什么要稳定而不是"读到的顺序"】工具栏顺序由 explorer 内部维护、UIA 树顺序同样不保证，
    /// 两次读取可能不同。用户认格子靠的是位置记忆——顺序乱跳比顺序不"正确"更难受。
    /// 前缀 <c>"1 "</c> 让无名项排在所有有名项之后（<c>'1' &gt; '0'</c>），比 <c>char.MaxValue</c> 好读也好写断言。
    /// </summary>
    public static string SortKey(string? label)
    {
        var text = (label ?? "").Trim();
        return text.Length == 0 ? "1 " : "0 " + text.ToLowerInvariant();
    }

    /// <summary>
    /// 一格到底能不能把点击转发回它自己的应用。返回 null = 可以，否则是不可以的原因（直接进 ToolTip）。
    /// <para>【为什么判定放在这里而不是 try/catch 里猜】<c>PostMessage</c> 给一个可疑句柄不会报错——
    /// 它把消息投给"某个窗口"就返回成功，我们永远收不到"点错了人"的失败。所以能不能点必须在发之前决定，
    /// 这一层就是这个不可逆动作的唯一闸门。</para>
    /// </summary>
    /// <param name="hasOwner">是否认出了 owner 窗口（经典路从 dwData 恢复 + <c>IsWindow</c> 校验的结果）。</param>
    /// <param name="callbackMessage">恢复出的回调消息号（<see cref="IsPlausibleCallback"/> 的入参）。</param>
    /// <param name="uidMatches"><c>dwData</c> 里的 uID 与工具栏 <c>idCommand</c> 是否一致（交叉校验）。</param>
    /// <param name="ownerHung">owner 窗口是否已停止处理消息（<c>IsHungAppWindow</c>）。</param>
    /// <param name="origin">哪一路读到的（UIA 路走 Invoke 模式，不需要句柄）。</param>
    public static string? ForwardBlockReason(
        bool hasOwner, uint callbackMessage, bool uidMatches, bool ownerHung, TrayIconOrigin origin)
    {
        if (origin == TrayIconOrigin.UiaElement) return null;   // UIA 元素自带可调用模式，句柄闸门不适用
        if (!hasOwner) return "读不到这个图标属于哪个窗口，不能把点击发给来路不明的窗口";
        if (!IsPlausibleCallback(callbackMessage)) return $"回调消息号 0x{callbackMessage:X} 不在可信范围，不能转发";
        if (!uidMatches) return "图标编号与工具栏条目不一致（系统结构可能已变），不能转发";
        if (ownerHung) return "这个应用当前未响应，点击不会有任何效果";
        return null;
    }
}
