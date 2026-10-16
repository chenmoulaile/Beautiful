using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Beautiful.App.Interop;
using Beautiful.Core.Layout;
using Beautiful.Core.Tray;
using static Beautiful.App.Interop.TrayInterop;
using static Beautiful.App.Interop.NativeMethods;
using Rect = System.Windows.Rect;   // UIA 的 BoundingRectangle 类型（UseWindowsForms 隐式 using 里只有 System.Drawing.Rectangle，不撞名但写清楚）

namespace Beautiful.App.Services;

/// <summary>一格托盘图标的快照（读到的那一刻的事实，之后不自动更新：面板是"显示一次"的瞬态表面）。</summary>
public sealed record TrayIconItem
{
    /// <summary>两路去重键（<see cref="TrayLayout.MergeKey"/> 产出）。</summary>
    public required string Key { get; init; }

    /// <summary>应用自己写的提示文本（经典路的 tooltip / UIA 路的 Name）。空 = 应用没给。</summary>
    public string? Label { get; init; }

    /// <summary>哪一路读到的（决定了能不能点、怎么点）。</summary>
    public required TrayIconOrigin Origin { get; init; }

    /// <summary>归类结果（第三方 / 系统项 / 认不准）。</summary>
    public required TrayIconCategory Category { get; init; }

    /// <summary>图标 uID（经典路的 <c>idCommand</c>；UIA 路为 0）。</summary>
    public uint Uid { get; init; }

    /// <summary>恢复并校验过的 owner 窗口（经典路）；<see cref="IntPtr.Zero"/> = 认不出来。</summary>
    public IntPtr OwnerHwnd { get; init; }

    /// <summary>回调消息号（经典路）；0 = 读不到。</summary>
    public uint CallbackMessage { get; init; }

    /// <summary>图标在屏幕上的物理矩形（两路都可能没有）。既是取图依据，也是合成点击的落点。</summary>
    public PhysicalRect RectPx { get; init; } = new(0, 0, 0, 0);

    /// <summary><see cref="RectPx"/> 是否可信（<see cref="TrayLayout.IsUsableRect"/> 判过的结果）。</summary>
    public bool HasRect { get; init; }

    /// <summary>真实像素（裁自屏幕上那一格托盘）；null = 界面画占位字形。</summary>
    public BitmapSource? Image { get; init; }

    /// <summary>取不到图时的原因（直接进 ToolTip，不让用户以为"这应用就长一个小方块"）。</summary>
    public string? ImageNote { get; init; }

    /// <summary>不能把点击转发回去的原因（<see cref="TrayLayout.ForwardBlockReason"/>）；null = 可以转发。</summary>
    public string? BlockReason { get; init; }

    /// <summary>UIA 路用来<b>重新定位</b>元素的身份键（元素句柄跨线程与跨次枚举都不稳，所以只存身份）。</summary>
    public string? UiaName { get; init; }

    /// <summary>UIA AutomationId（同上，可能为空串）。</summary>
    public string? UiaAutomationId { get; init; }

    /// <summary>这格能不能点（界面据此决定画普通按钮还是置灰并在同一行写原因）。</summary>
    public bool CanActivate => BlockReason is null;
}

/// <summary>一次枚举的结果：条目 + 两条路各自的诚实状态行。</summary>
public sealed record TrayReadResult
{
    /// <summary>合并去重要平铺的条目（已按 <see cref="TrayLayout.SortKey"/> 排好）。</summary>
    public IReadOnlyList<TrayIconItem> Items { get; init; } = [];

    /// <summary>经典路的状态说明（找到几个 / 为什么读不到）。</summary>
    public string ClassicStatus { get; init; } = "";

    /// <summary>UIA 路的状态说明。</summary>
    public string UiaStatus { get; init; } = "";

    /// <summary>被判定为 shell 自己的控件而跳过的数量（界面写"已跳过 N 个系统项"，不说谎）。</summary>
    public int SkippedSystemOwned { get; init; }

    /// <summary>树里有没有"显示隐藏的图标"按钮（有才有"展开隐藏区"这条真动作，没有就不放这个按钮）。</summary>
    public bool HasShowHiddenButton { get; init; }
}

/// <summary>
/// 通知区域（系统托盘）图标的枚举与点击转发。<b>两路并行</b>：任何一路失败不影响另一路，任何一步失败都不抛到调用方。
/// <para>
/// 【为什么要两路】这两条路对应两代实现，谁都不是另一条的"备胎"：
/// ① <b>经典路</b>（<c>Shell_TrayWnd→TrayNotifyWnd→SysPager→ToolbarWindow32</c> 以及顶层
/// <c>NotifyIconOverflowWindow</c> 里那一条）读的是 explorer 进程里的 Win32 工具栏，能拿到 <c>uID</c>、
/// tooltip 文本，以及（三重交叉校验通过后）这个图标属于哪个窗口、那个窗口的回调消息号——只有拿到这两样，
/// 才能把点击 <c>PostMessage</c> 回那个应用，让它照常弹出它自己的菜单。Win11 21H2–23H2 这条还在。
/// ② <b>UIA 路</b>（<c>System.Windows.Automation</c>）读的是同一片区域的无障碍树。Win11 24H2 把通知区域
/// 重写成 XAML 岛，<c>ToolbarWindow32</c> 整个消失，①无从读起；而 24H2 上②仍然给出 Button 元素、
/// 应用自己写的 Name、屏幕矩形，并支持 <c>Invoke</c>。
/// <b>结论：24H2 上真正能用的是②；23H2 及以下①给的信息更多（有名字也有可转发的窗口）。两路都跑，按文本键合并。</b>
/// </para>
/// <para>
/// 【为什么取不到图就留空框（项目裁定：宁可空框也不错画）】托盘位图在应用调 <c>Shell_NotifyIcon(NIM_ADD)</c>
/// 那一刻就被 explorer 复制进它自己的进程，之后<b>没有任何公开 API</b> 能按 uID 把它取回来：
/// <c>TB_GETIMAGELIST</c> 交回的 <c>HIMAGELIST</c> 是 explorer 进程里的指针，在我们进程里用它 = 未定义行为
/// （不是"读不到"而是"可能崩掉驻留进程"）；重放 <c>NIM_ADD</c> 更是往 explorer 里塞一个假图标、还可能撞掉
/// 真实应用的 uID；用"那个应用主窗口的图标"或"exe 的图标"替代是<b>看起来像它的图标</b>，用户分辨不出来，
/// 正是这条禁令要挡的东西。唯一可靠的做法是<b>裁屏幕上那一格真实像素</b>——它就是 Windows 自己画出来的那个图标。
/// 代价是裁到的可能是我们自己的表面（常驻顶栏/Dock 盖住真实任务栏时），所以裁之前必须过
/// <see cref="TrayLayout.CanCropFromScreen"/>，裁完还要过"整块同色"这一关。两道都不过 → 占位字形 + ToolTip 写原因。
/// </para>
/// <para>
/// 【<c>IsHungAppWindow</c> 为什么是承重的】本类每一条 <c>SendMessage*</c>、每一次远程读、每个 UIA 属性访问，
/// 终点都是<b>别人的进程</b>。explorer 或某个挂死的托盘应用会把"发送"变成"永不返回"，而我们整个产品是
/// 常驻进程 + 一个 UI 线程：一次卡住就是顶栏冻死、Dock 不再响应、连退出都退不掉。所以三道防线：
/// ① 任何远程操作之前先 <c>IsHungAppWindow</c> 早退（这个判据本身不发消息、不阻塞）；
/// ② 真正发消息一律走 <see cref="TrayInterop.Send"/>（<c>SMTO_ABORTIFHUNG</c> + 200ms 超时）——
/// 因为"检查"和"发送"之间目标随时可能变挂，只靠①是不够的；
/// ③ 转发点击用 <c>PostMessage</c>（投递，不等回音），用户点一下绝不会被那个应用的 WndProc 拖住我们的线程。
/// </para>
/// <para>
/// 【线程契约】<b><see cref="Read"/>、<see cref="Activate"/>、<see cref="ShowHiddenIcons"/> 只能在后台线程调用</b>。
/// 远程读写 + 若干次跨进程 send + 整棵 UIA 子树遍历，量级是几十到几百毫秒；UIA 的属性读取还是跨套间 COM 调用。
/// 放在 UI 线程上就是把面板的打开时间交给 explorer 的心情。面板侧一律 <c>Task.Run</c> 之后再 <c>await</c> 回来，
/// 回投时先问 <c>IsLoaded</c>（面板可能已经被用户关掉了）。
/// </para>
/// </summary>
public sealed class TrayIconSource
{
    /// <summary>tooltip 的远程缓冲区字节数：512 个 UTF-16 字符，托盘本来就显示不完更长的文本。</summary>
    private const int TextBufferBytes = 1024;

    private static readonly PropertyCondition ButtonCondition =
        new(AutomationElement.ControlTypeProperty, ControlType.Button);

    /// <summary>
    /// 结构体尺寸闸门：x64 下 <c>TBBUTTON</c>=32、<c>TBBUTTONINFOW</c>=40。别的值说明本进程与 explorer
    /// 的位数/布局不同（AnyCPU 跑在 32 位里就是这种情形），按这个布局读回来的<b>每个字段都是错位的</b>——
    /// 与其读出一堆"每个图标都属于同一个窗口"的假条目，不如整条路不走。
    /// </summary>
    private static readonly bool LayoutPlausible =
        Marshal.SizeOf<TBBUTTON>() == 32 && Marshal.SizeOf<TBBUTTONINFOW>() == 40;

    /// <summary>
    /// 枚举两路并合并（<b>只能在后台线程调用</b>，见类型注释的线程契约）。
    /// </summary>
    public TrayReadResult Read()
    {
        // 自家可见表面清单：取图前先算一次，整轮复用（枚举全部顶层窗口是一次同步回调，逐图标做就是几十次）
        var avoid = OwnSurfaceRects();
        var raw = new List<TrayIconItem>();
        var classic = ReadClassic(raw, avoid);
        var uia = ReadUia(raw);

        var skipped = 0;
        var merged = new Dictionary<string, TrayIconItem>(StringComparer.Ordinal);
        foreach (var item in raw)
        {
            if (!TrayLayout.ShouldDisplay(item.Category))
            {
                skipped++;
                continue;
            }
            // 同一图标被两路各读到一次（23H2 及以下必然发生）：留下"更有用的那条"而不是先到先得——
            // 先到的可能恰好是信息更少的那条（UIA 有名字没句柄，经典有句柄但 tooltip 可能空）。
            if (merged.TryGetValue(item.Key, out var existing))
            {
                if (Usefulness(item) > Usefulness(existing)) merged[item.Key] = item;
                continue;
            }
            merged[item.Key] = item;
        }

        var items = merged.Values
                          .OrderBy(i => TrayLayout.SortKey(i.Label), StringComparer.Ordinal)
                          .ToList();
        Logger.Info($"托盘枚举：经典路[{classic}] UIA[{uia}] 平铺 {items.Count} 格，跳过系统项 {skipped} 个，" +
                    $"有图 {items.Count(i => i.Image is not null)} 格，可转发 {items.Count(i => i.CanActivate)} 格");
        return new TrayReadResult
        {
            Items = items,
            ClassicStatus = classic,
            UiaStatus = uia,
            SkippedSystemOwned = skipped,
            HasShowHiddenButton = raw.Any(IsChevron),
        };
    }

    /// <summary>合并优先级：<c>能转发</c> 是第一判据（点不动的格子对用户没有价值），其次有图，最后有名字。</summary>
    private static int Usefulness(TrayIconItem item)
        => (item.CanActivate ? 4 : 0) + (item.Image is not null ? 2 : 0)
           + (string.IsNullOrWhiteSpace(item.Label) ? 0 : 1);

    /// <summary>"显示隐藏的图标"那一项：它是 shell 的部件，不进平铺区，但界面要能认出它并替用户点开。</summary>
    private static bool IsChevron(TrayIconItem item)
        => item.Origin == TrayIconOrigin.UiaElement
           && (item.UiaName?.Contains("隐藏") == true
               || item.UiaName?.IndexOf("hidden", StringComparison.OrdinalIgnoreCase) >= 0
               || item.UiaAutomationId?.IndexOf("ShowHidden", StringComparison.OrdinalIgnoreCase) >= 0);

    // ---------- ① 经典路：explorer 的工具栏 ----------

    /// <summary>读所有可疑的托盘工具栏。返回给界面看的一句话状态。</summary>
    private string ReadClassic(List<TrayIconItem> sink, IReadOnlyList<PhysicalRect> avoid)
    {
        if (!LayoutPlausible) return "本进程与 explorer 的结构体布局不同（位数不匹配），已放弃经典路";

        var shell = FindWindowClass(TrayInterop.ShellTrayWnd);
        if (shell == IntPtr.Zero) return "找不到 Shell_TrayWnd";
        // 承重墙第一道：explorer 挂住时一条消息都不发（判据本身不发消息、不阻塞）
        if (IsHungAppWindow(shell)) return "explorer 未响应（已早退，避免被一起拖住）";

        var toolbars = FindToolbars();
        if (toolbars.Count == 0)
            return "找不到 ToolbarWindow32（24H2 的通知区域已换成 XAML 岛，这条路本来就没有）";

        var total = 0;
        var bar = 0;
        foreach (var (hwnd, area) in toolbars)
        {
            var before = sink.Count;
            try
            {
                ReadToolbar(hwnd, area, sink, avoid);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // 只剩封送层的异常（结构体尺寸、指针换算）；IO 层已经把失败变成 false 了
                Logger.Warn($"托盘工具栏读取异常（{area}）: {ex.Message}");
                continue;
            }
            bar++;
            total += sink.Count - before;
        }
        return $"工具栏 {bar}/{toolbars.Count} 个可读，读到 {total} 格";
    }

    /// <summary>
    /// 收集托盘工具栏。<b>"经过 SysPager"和"不经过 SysPager"两种形态都试</b>：溢出区在没有滚动需求时
    /// 根本没有 SysPager 这一层，硬要求它存在就会变成"明明有图标却读不到"。
    /// </summary>
    private static List<(IntPtr Hwnd, string Area)> FindToolbars()
    {
        var list = new List<(IntPtr Hwnd, string Area)>();
        var shell = FindWindowClass(TrayInterop.ShellTrayWnd);
        var notify = shell == IntPtr.Zero ? IntPtr.Zero : FindChildByClass(shell, TrayInterop.TrayNotifyWnd);
        AddToolbar(list, notify, "常驻图标区");
        AddToolbar(list, notify == IntPtr.Zero ? IntPtr.Zero : FindChildByClass(notify, TrayInterop.SysPager), "常驻图标区");

        var overflow = FindWindowClass(TrayInterop.NotifyIconOverflowWindow);
        AddToolbar(list, overflow, "隐藏图标区");
        AddToolbar(list, overflow == IntPtr.Zero ? IntPtr.Zero : FindChildByClass(overflow, TrayInterop.SysPager), "隐藏图标区");

        // notify 与 notify/SysPager 可能命中同一个工具栏：按句柄去重，否则每个图标被读两遍，
        // 只能靠合并键补救（多做一次无谓的跨进程读，还会让"可转发格数"在日志里翻倍）。
        return list.DistinctBy(t => t.Hwnd).ToList();
    }

    private static void AddToolbar(List<(IntPtr Hwnd, string Area)> list, IntPtr parent, string area)
    {
        if (parent == IntPtr.Zero) return;
        var bar = FindChildByClass(parent, TrayInterop.ToolbarWindow32);
        if (bar != IntPtr.Zero) list.Add((bar, area));
    }

    private void ReadToolbar(IntPtr toolbar, string area, List<TrayIconItem> sink, IReadOnlyList<PhysicalRect> avoid)
    {
        // 第二道早退：这里查的是工具栏自己（explorer 的托盘子窗口），它挂了就一条都不发
        if (IsHungAppWindow(toolbar))
        {
            Logger.Warn($"托盘工具栏（{area}）未响应，跳过");
            return;
        }
        GetWindowThreadProcessId(toolbar, out var pid);
        if (pid == 0)
        {
            Logger.Warn($"托盘工具栏（{area}）取不到进程 id，跳过");
            return;
        }

        var hProc = OpenForRead(pid);
        if (hProc == IntPtr.Zero)
        {
            // 最常见原因：explorer 以更高完整性运行（提权会话/受保护进程），中等完整性打不开它的内存
            Logger.Warn($"托盘工具栏（{area}）打不开目标进程内存（错误 {Marshal.GetLastWin32Error()}），跳过");
            return;
        }

        // 远程缓冲区一次分配、整轮复用（每图标各分配一次 = 每格 4 次跨进程 alloc，纯浪费）。
        // 尺寸都留了余量：结构体实际 32/40 字节，各给 64；分配失败就不读（不降级成"局部读"）。
        var remoteBtn = VirtualAllocEx(hProc, IntPtr.Zero, 64, MemCommit, PageReadwrite);
        var remoteInfo = VirtualAllocEx(hProc, IntPtr.Zero, 64, MemCommit, PageReadwrite);
        var remoteText = VirtualAllocEx(hProc, IntPtr.Zero, TextBufferBytes, MemCommit, PageReadwrite);
        var remoteRect = VirtualAllocEx(hProc, IntPtr.Zero, 32, MemCommit, PageReadwrite);
        try
        {
            if (remoteBtn == IntPtr.Zero || remoteInfo == IntPtr.Zero || remoteText == IntPtr.Zero
                || remoteRect == IntPtr.Zero)
            {
                Logger.Warn($"托盘工具栏（{area}）远程缓冲区分配失败，跳过");
                return;
            }
            if (!Send(toolbar, TbButtonCount, 0, 0, out var rawCount))
            {
                Logger.Warn($"托盘工具栏（{area}）读按钮数失败（超时或已挂）");
                return;
            }
            // 先饱和再钳：裸 (int) 强转会把 0x1_0000_0005 折成 5，那是"看着完全像合法计数"的垃圾，
            // 比一个明显的负数危险得多。
            var reported = rawCount > int.MaxValue ? int.MaxValue : rawCount < 0 ? -1 : (int)rawCount;
            var count = TrayLayout.ClampButtonCount(reported);
            for (var i = 0; i < count; i++)
            {
                var item = ReadButton(toolbar, hProc, i, remoteBtn, remoteInfo, remoteText, remoteRect, avoid);
                if (item is not null) sink.Add(item);
            }
        }
        finally
        {
            // 句柄纪律（本仓库把泄漏定级 P0）：远程内存与进程句柄都在这里还掉，抛异常也还。
            // 面板每刷新一次就跑一遍这段，漏一次就是 explorer 里一片永不释放的私有内存。
            if (remoteBtn != IntPtr.Zero) VirtualFreeEx(hProc, remoteBtn, 0, MemRelease);
            if (remoteInfo != IntPtr.Zero) VirtualFreeEx(hProc, remoteInfo, 0, MemRelease);
            if (remoteText != IntPtr.Zero) VirtualFreeEx(hProc, remoteText, 0, MemRelease);
            if (remoteRect != IntPtr.Zero) VirtualFreeEx(hProc, remoteRect, 0, MemRelease);
            CloseHandle(hProc);
        }
    }

    /// <summary>
    /// 读第 <paramref name="index"/> 个按钮。<b>任何一步读不到都返回 null</b>（跳过这一格），绝不返回一个
    /// "半截条目"：半截条目会画出一格既没名字又点不动的方块，用户完全无法判断发生了什么，
    /// 而我们连日志都没法解释它为什么存在。
    /// </summary>
    private static TrayIconItem? ReadButton(
        IntPtr toolbar, IntPtr hProc, int index,
        IntPtr remoteBtn, IntPtr remoteInfo, IntPtr remoteText, IntPtr remoteRect, IReadOnlyList<PhysicalRect> avoid)
    {
        // 1) TB_GETBUTTON：idCommand / fsState / fsStyle / dwData / iString。
        //    预填 0 再发：目标代码只写它认识的那些字节，缓冲区里残留的上一轮内容会被当成"读到的值"。
        var btnSize = Marshal.SizeOf<TBBUTTON>();
        if (!WriteRemoteZero(hProc, remoteBtn, btnSize)) return null;
        if (!Send(toolbar, TbGetButton, index, remoteBtn.ToInt64(), out _)) return null;
        if (!TryReadStructure<TBBUTTON>(hProc, remoteBtn, out var btn)) return null;

        // 状态位判在昂贵动作之前：分隔符与被隐藏的条目（进程已退、条目未回收）不值得再发三次跨进程消息
        if (TrayLayout.IsSeparator(btn.fsStyle) || TrayLayout.IsHidden(btn.fsState)) return null;
        if (btn.idCommand == 0) return null;   // 没有 uID 的条目不可能是托盘图标

        // 2) 提示文本（TB_GETBUTTONINFOW + TBIF_TEXT，文本由 explorer 写进我们给的远程缓冲区）
        var label = ReadTooltip(toolbar, hProc, index, btn, remoteInfo, remoteText);

        // 3) 这一格在屏幕上的位置（客户区矩形 → ClientToScreen）：取图依据，也是"它真的还在屏幕上"的证据
        var hasRect = TryItemRect(toolbar, hProc, index, remoteRect, out var rectPx);

        // 4) owner 窗口与回调消息号：从 dwData 恢复 + 三重交叉校验，认不出来就只做展示
        var (owner, callback, uidMatches) = TryRecoverOwner(hProc, btn);
        var block = TrayLayout.ForwardBlockReason(
            owner != IntPtr.Zero, callback, uidMatches,
            owner != IntPtr.Zero && IsHungAppWindow(owner),
            TrayIconOrigin.ClassicToolbar);

        // 取图：有位置才裁屏（没有位置连"该裁哪里"都不知道）。写成两条语句而不是三元表达式，
        // 是因为 `(null, "…")` 与 `(BitmapSource?, string?)` 在 var 目标下推不出公共类型。
        BitmapSource? image = null;
        string? note = "读不到这一格在屏幕上的位置";
        if (hasRect) (image, note) = CaptureGlyph(rectPx, avoid);

        return new TrayIconItem
        {
            Key = TrayLayout.MergeKey(label, (uint)btn.idCommand),
            Label = label,
            Origin = TrayIconOrigin.ClassicToolbar,
            Category = TrayLayout.Classify(label, null, TrayIconOrigin.ClassicToolbar),
            Uid = (uint)btn.idCommand,
            OwnerHwnd = owner,
            CallbackMessage = callback,
            RectPx = rectPx,
            HasRect = hasRect,
            Image = image,
            ImageNote = image is null ? note : null,
            BlockReason = block,
        };
    }

    /// <summary>
    /// 读 tooltip。先按<b>命令号</b>取（<c>TB_GETBUTTONINFO</c> 默认把 wParam 解释成命令号），
    /// 取不到再按<b>索引</b>取（<c>TBIF_BYINDEX</c>）：两种调用写法在真实 explorer 上都见过，
    /// 版本归属没有公开承诺，所以多试一次（代价是一次额外的跨进程 send，且只在文本还没拿到时发生）。
    /// </summary>
    private static string ReadTooltip(IntPtr toolbar, IntPtr hProc, int index, TBBUTTON btn,
                                      IntPtr remoteInfo, IntPtr remoteText)
    {
        var infoSize = Marshal.SizeOf<TBBUTTONINFOW>();
        foreach (var byIndex in new[] { false, true })
        {
            var info = new TBBUTTONINFOW
            {
                cbSize = infoSize,
                // dwMask 是逐字段生效开关：没置位的字段目标代码不会写，因此除了 cbSize 与 iString 全留 0
                dwMask = (int)(TbifText | TbifState | TbifStyle | TbifLparam | TbifCommand
                               | (byIndex ? TbifByIndex : 0u)),
                iString = remoteText,
            };
            var bytes = ToBytes(info);
            if (!WriteProcessMemory(hProc, remoteInfo, bytes, bytes.Length, out _)) return "";
            // 文本缓冲区每次清零：上一轮（上一个按钮）的 tooltip 如果残留着，会被当成"这一个图标的名字"，
            // 于是出现"图标名字整体错位一格"这种最难从截图看出来的故障。
            if (!WriteRemoteZero(hProc, remoteText, TextBufferBytes)) return "";

            var wParam = byIndex ? index : btn.idCommand;
            if (!Send(toolbar, TbGetButtonInfoW, wParam, remoteInfo.ToInt64(), out _)) continue;

            // 交叉校验：回读结构里的 idCommand 必须等于 TB_GETBUTTON 给的那个。
            // 这一步专门挡"名字整体错位一格"那种最难发现的故障——wParam 被按错的口径解释时，
            // explorer 会老老实实把**另一个按钮**的文本写进我们的缓冲区，返回值依然是成功。
            if (!TryReadStructure<TBBUTTONINFOW>(hProc, remoteInfo, out var got) ||
                got.idCommand != btn.idCommand) continue;

            var text = new byte[TextBufferBytes];
            if (ReadProcessMemory(hProc, remoteText, text, text.Length, out _) &&
                DecodeUtf16(text) is { Length: > 0 } tip) return tip;

            // 有的实现把文本指针直接放在 TBBUTTON.iString 里（而不是写进我们给的缓冲区）：再试一次读那个指针
            if (btn.iString.ToInt64() > 0x10000 &&
                ReadProcessMemory(hProc, btn.iString, text, text.Length, out _))
            {
                var direct = DecodeUtf16(text);
                if (direct.Length > 0) return direct;
            }
        }
        return "";
    }

    /// <summary>
    /// <c>TB_GETITEMRECT</c> → 客户区矩形 → <c>ClientToScreen</c> 得物理屏幕矩形。任何一步失败回 false。
    /// 【为什么只做平移不做缩放】工具栏自己已经被 explorer 按 DPI 排好，客户区坐标到屏幕坐标只差一个原点；
    /// 我们再乘一次 DPI 就是"缩放发生了两次"，200% 屏上每格都会跑偏。
    /// </summary>
    private static bool TryItemRect(IntPtr toolbar, IntPtr hProc, int index, IntPtr remoteRect, out PhysicalRect rectPx)
    {
        rectPx = new PhysicalRect(0, 0, 0, 0);
        var size = Marshal.SizeOf<RECT>();
        if (!WriteRemoteZero(hProc, remoteRect, size)) return false;
        if (!Send(toolbar, TbGetItemRect, index, remoteRect.ToInt64(), out _)) return false;
        if (!TryReadStructure<RECT>(hProc, remoteRect, out var r)) return false;
        if (!TrayLayout.IsUsableRect(new PhysicalRect(r.Left, r.Top, r.Right, r.Bottom))) return false;

        var origin = new POINT { X = r.Left, Y = r.Top };
        if (!ClientToScreen(toolbar, ref origin)) return false;
        rectPx = new PhysicalRect(origin.X, origin.Y, origin.X + (r.Right - r.Left), origin.Y + (r.Bottom - r.Top));
        return true;
    }

    /// <summary>
    /// 从 <c>TBBUTTON.dwData</c> 恢复"这个图标属于哪个窗口 + 那个窗口的回调消息号 + 它的 uID"。
    /// <para>【为什么这是猜的，以及为什么猜了还算数】微软没有公开 dwData 的内容；社区里长期观察到的形态是
    /// 它指向 explorer 自己保存的那份 <c>NOTIFYICONDATA</c> 前缀（x64：<c>hWnd(8) message(4) uid(4)</c>）。
    /// 读回来之后<b>不直接信</b>，做三重交叉校验：① <c>IsWindow</c> 证明那是个真窗口；
    /// ② 消息号落在 <see cref="TrayLayout.IsPlausibleCallback"/> 认的两档里；
    /// ③ 结构里的 uID 必须等于工具栏条目的 <c>idCommand</c>——这一条最关键：字段错位读出来的值
    /// 同时让这三样自洽几乎不可能。三条全过才允许转发点击；否则这一格只做展示并写明原因。
    /// 猜错的代价被限制成"这一格点不动"，而不是"把点击发给别人的窗口"。</para>
    /// </summary>
    private static (IntPtr Owner, uint Message, bool UidMatches) TryRecoverOwner(IntPtr hProc, TBBUTTON btn)
    {
        var data = btn.dwData.ToInt64();
        // 指针范围粗筛：0 与小整数（个别版本在这里放的是计数而不是指针）都不可能可读
        if (data < 0x1000 || data > 0x7FFF_FFFF_FFFF) return (IntPtr.Zero, 0, false);
        var bytes = new byte[24];
        if (!ReadProcessMemory(hProc, new IntPtr(data), bytes, bytes.Length, out var read) || read < 16)
            return (IntPtr.Zero, 0, false);

        var owner = new IntPtr(BitConverter.ToInt64(bytes, 0));
        var message = (uint)BitConverter.ToInt32(bytes, 8);
        var uid = (uint)BitConverter.ToInt32(bytes, 12);
        if (owner == IntPtr.Zero || !IsWindow(owner) || !TrayLayout.IsPlausibleCallback(message))
            return (IntPtr.Zero, 0, false);
        return (owner, message, uid == (uint)btn.idCommand);
    }

    /// <summary>把远程缓冲区清零（避免上一轮的残留被当成这一轮读到的值）。</summary>
    private static bool WriteRemoteZero(IntPtr hProc, IntPtr remote, int size)
        => WriteProcessMemory(hProc, remote, new byte[size], size, out _);

    /// <summary>远程读一个 blittable 结构体；长度不足或封送失败都回 false（绝不回一个半截值）。</summary>
    private static bool TryReadStructure<T>(IntPtr hProc, IntPtr remote, out T value) where T : struct
    {
        var bytes = new byte[Marshal.SizeOf<T>()];
        value = default;
        return ReadProcessMemory(hProc, remote, bytes, bytes.Length, out var read)
               && read == bytes.Length
               && TryFromBytes(bytes, out value);
    }

    // ---------- 取图：裁屏幕上真实那一格 ----------

    /// <summary>
    /// 裁出指定屏幕矩形当作这格图标。返回 <c>(null, 原因)</c> 的三种情况：与自家表面相交（裁到的是我们的
    /// 玻璃条）、整块同色（什么都没裁到）、GDI 失败。<b>不用任何其他来源凑图</b>（窗口图标、exe 图标都不试，
    /// 理由见类型注释）。
    /// </summary>
    private static (BitmapSource? Image, string? Note) CaptureGlyph(PhysicalRect rectPx, IReadOnlyList<PhysicalRect> avoid)
    {
        if (!TrayLayout.CanCropFromScreen(rectPx, avoid))
            return (null, "这一格被 Beautiful 自己的界面挡住，裁不到真实图标（没有用别的图凑数）");

        var w = rectPx.Width;
        var h = rectPx.Height;
        if (w is < 1 or > 128 || h is < 1 or > 128) return (null, $"这一格的尺寸异常（{w}×{h}），没有裁它");

        var screen = GetDC(IntPtr.Zero);
        var mem = IntPtr.Zero;
        var dib = IntPtr.Zero;
        var old = IntPtr.Zero;
        try
        {
            if (screen == IntPtr.Zero) return (null, "取不到屏幕 DC");
            mem = CreateCompatibleDC(screen);
            if (mem == IntPtr.Zero) return (null, "建不了兼容 DC");
            var bmi = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h,          // 负高 = top-down，与 BitmapSource 的行主序一致
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BiRgb,
            };
            dib = CreateDIBSection(mem, ref bmi, DibRgbColor, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return (null, "建不了取像缓冲区");
            old = SelectObject(mem, dib);   // 必须先选入再 BitBlt（BitBlt 写的是 DC 当前位图）
            if (!BitBlt(mem, 0, 0, w, h, screen, rectPx.Left, rectPx.Top, Srccopy | CaptureBlt))
                return (null, "取像失败（这个显示可能禁止采集）");

            var px = new byte[w * h * 4];
            Marshal.Copy(bits, px, 0, px.Length);
            // 屏幕取像的 alpha 恒 0，钉成 255 才是"看到的这一块"（真实托盘像素本来就不透明）
            for (var i = 3; i < px.Length; i += 4) px[i] = 0xFF;
            if (IsMonochrome(px)) return (null, "这一格是纯色（什么都没画上来）");

            var img = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
            img.Freeze();   // 冻结后才能被控件树长期引用（也免掉变更跟踪）
            return (img, null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OutOfMemoryException)
        {
            Logger.Warn($"托盘取图异常: {ex.Message}");
            return (null, "取图时出了异常（详见日志）");
        }
        finally
        {
            // 与 ScreenBlurBackdrop 同一套成对归还：驻留进程里每次刷新漏一个 DC 就是句柄数单调上涨
            if (old != IntPtr.Zero && mem != IntPtr.Zero) SelectObject(mem, old);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (mem != IntPtr.Zero) DeleteDC(mem);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>整块是否同一种颜色（采样 64 点足够判定：真图标一定有前景与底色的差别）。</summary>
    private static bool IsMonochrome(byte[] px)
    {
        if (px.Length < 4) return true;
        var step = Math.Max(4, px.Length / 64) & ~3;   // 步长对齐到像素边界（4 字节一格）
        for (var i = 0; i + 2 < px.Length; i += step)
            if (px[i] != px[0] || px[i + 1] != px[1] || px[i + 2] != px[2]) return false;
        return true;
    }

    /// <summary>
    /// 本进程所有<b>可见</b>顶层窗口的物理矩形（顶栏、Dock、媒体胶囊、这张面板本身、任何弹层）。
    /// <para>【为什么要这份清单，为什么要"全部"而不是只问面板自己】取图靠的是裁屏幕像素，而裁到自家
    /// 表面就是"画错图标"。Dock 的窗口矩形按设计是"整屏宽 ×（面板高 + 放大余量）"，它透明的部分
    /// <b>也</b>盖在真实任务栏那一条上——只避开面板自己就会把 Dock 的半透明玻璃当成某个应用的图标画出来。
    /// 所以判定偏保守：与任何一块自家矩形相交就不裁，宁可退回占位字形。</para>
    /// <para>【为什么用 EnumWindows 而不是问 App 的单例引用】托盘面板不该知道 Dock 在哪、顶栏在哪
    /// （那是生命周期编排者的事）；"我这个进程在屏幕上有哪几块"是纯事实问题，一次枚举就够，
    /// 也不会因为新加一个表面而漏掉它。</para>
    /// </summary>
    private static IReadOnlyList<PhysicalRect> OwnSurfaceRects()
    {
        var rects = new List<PhysicalRect>();
        var self = Environment.ProcessId;
        // 回调委托必须是显式局部变量并在调用后 KeepAlive：EnumWindows 是同步回调，但委托若在此期间
        // 被判定不可达就会回收，打到已回收的原生桩上就是随机崩溃（本仓库对回调型 P/Invoke 的统一纪律）。
        EnumWindowsProc probe = (hwnd, _) =>
        {
            try
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid != self || !IsWindowVisible(hwnd)) return true;
                if (!GetWindowRect(hwnd, out var r)) return true;
                var rect = new PhysicalRect(r.Left, r.Top, r.Right, r.Bottom);
                if (TrayLayout.IsUsableRect(rect)) rects.Add(rect);
            }
            catch (Exception)
            {
                // 一个回调里的异常绝不能中断整次枚举（会把后续所有窗口的判定一起废掉）
            }
            return true;   // 恒 true：继续枚举
        };
        try
        {
            EnumWindows(probe, IntPtr.Zero);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Logger.Warn($"托盘取图：自家表面枚举失败（这一轮不裁任何图）: {ex.Message}");
            return [];
        }
        finally
        {
            GC.KeepAlive(probe);
        }
        return rects;
    }

    // ---------- ② UIA 路：无障碍树 ----------

    /// <summary>
    /// 读 <c>Shell_TrayWnd</c>（以及当时存在的溢出窗口）里的 Button 元素。
    /// <para>【为什么这条路在 24H2 上是主力】通知区域换成 XAML 岛之后没有 <c>ToolbarWindow32</c>，
    /// 但无障碍树仍然给出每个图标的 Button 元素、应用自己写的 Name 和屏幕矩形，并且支持 Invoke——
    /// "叫什么"与"能点开它自己的菜单"这两件事都有了，只是拿不到 uID/owner（这条路也不需要）。</para>
    /// <para>【为什么 <c>IsHungAppWindow</c> 之外还要另外两道保险】UIA 调用没有超时参数：provider 不响应时
    /// 客户端会等 UIA 自己的超时（秒级）。处置是 ① 入口先查 <c>IsHungAppWindow</c> 早退，
    /// ② 整个遍历只在后台线程跑（由面板的 <c>Task.Run</c> 保证），③ 全部包在 try/catch 里把异常变成状态行。</para>
    /// </summary>
    private string ReadUia(List<TrayIconItem> sink)
    {
        var roots = TrayRoots();
        if (roots.Count == 0) return "找不到 Shell_TrayWnd";
        if (roots.Keys.Any(IsHungAppWindow)) return "任务栏所在进程未响应（已早退）";

        var found = 0;
        string? error = null;
        foreach (var hwnd in roots.Keys)
        {
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                if (root is null) continue;
                var buttons = root.FindAll(TreeScope.Descendants, ButtonCondition);
                // 上限来自 Core：XAML 岛里"按钮"很多（每个状态项、每一页都是），没有上界就把一次面板打开
                // 变成几百次跨进程属性读取
                for (var i = 0; i < buttons.Count && i < TrayLayout.MaxButtonsPerToolbar; i++)
                {
                    var item = FromElement(buttons[i]);
                    if (item is null) continue;
                    sink.Add(item);
                    found++;
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException
                                          or COMException or NullReferenceException or ArgumentException)
            {
                // ElementNotAvailableException = 这棵树在我们遍历途中被换掉了（explorer 重启 / 24H2 岛重建）
                error = ex.GetType().Name;
            }
        }
        return error is null ? $"读到 {found} 个按钮元素" : $"读到 {found} 个按钮元素后中断（{error}）";
    }

    /// <summary>
    /// UIA 读取的根：<c>Shell_TrayWnd</c> 一定试；两个溢出窗口类名<b>在的时候</b>才试
    /// （那个 flyout 只有展开后才存在——所以"展开隐藏图标"之后刷新一次才会多出隐藏区的条目）。
    /// 用字典去重：同一个 hwnd 被两条路各加一次会让同一图标读两遍。
    /// </summary>
    private static Dictionary<IntPtr, string> TrayRoots()
    {
        var map = new Dictionary<IntPtr, string>();
        void Add(string className, string label)
        {
            var hwnd = FindWindowClass(className);
            if (hwnd != IntPtr.Zero) map[hwnd] = label;
        }
        Add(TrayInterop.ShellTrayWnd, "任务栏");
        Add(TrayInterop.NotifyIconOverflowWindow, "隐藏图标区");
        Add(TrayInterop.XamlIslandOverflowWindow, "隐藏图标区(XAML 岛)");
        return map;
    }

    /// <summary>把一个 UIA Button 元素折成条目。既没名字又没位置的元素丢掉（画出来就是一格无法解释的方块）。</summary>
    private static TrayIconItem? FromElement(AutomationElement element)
    {
        var name = SafeString(() => element.Current.Name);
        var automationId = SafeString(() => element.Current.AutomationId);
        var label = name.Length > 0 ? name : automationId;
        var rectPx = RectOf(element);
        var hasRect = rectPx is not null;

        // 只有位置没有名字的元素（XAML 岛里 Name 常为空）仍然值得画——它就是"托盘里多出来的那个图标"；
        // 两者都没有就什么都不是。
        if (label.Length == 0 && !hasRect) return null;

        var r = hasRect ? rectPx!.Value : new PhysicalRect(0, 0, 0, 0);
        return new TrayIconItem
        {
            Key = TrayLayout.MergeKey(label, 0),
            Label = label,
            Origin = TrayIconOrigin.UiaElement,
            Category = TrayLayout.Classify(name, automationId, TrayIconOrigin.UiaElement),
            UiaName = name,
            UiaAutomationId = automationId,
            HasRect = hasRect,
            RectPx = r,
            // 能不能点要等真去取 Invoke 模式才知道；这里先按"可以尝试"给，
            // 失败原因在 Activate 里回（那时才有"这个元素已经不在树里"这类事实）。
            BlockReason = null,
            ImageNote = hasRect ? null : "这个元素没报出自己在屏幕上的位置",
        };
    }

    /// <summary>元素的屏幕矩形（物理像素）；读不到或不可信返回 null。</summary>
    private static PhysicalRect? RectOf(AutomationElement element)
    {
        var v = SafeRect(() => element.Current.BoundingRectangle);
        if (v.IsEmpty || !double.IsFinite(v.X) || !double.IsFinite(v.Y)
            || !double.IsFinite(v.Width) || !double.IsFinite(v.Height)) return null;
        // UIA 的矩形按屏幕物理像素约定返回，SetCursorPos 也用物理像素，口径直接一致。
        // 万一某个 provider 交回 DIP，最坏结果是"合成点击落偏"，而合成点击只是 Invoke 不可用时的兜底。
        var rect = new PhysicalRect((int)Math.Round(v.X), (int)Math.Round(v.Y),
                                    (int)Math.Round(v.X + v.Width), (int)Math.Round(v.Y + v.Height));
        return TrayLayout.IsUsableRect(rect) ? rect : null;
    }

    /// <summary>UIA 属性读取的护栏：任何异常都变成""（"读不到"），绝不冒到调用方。</summary>
    private static string SafeString(Func<string> reader)
    {
        try { return reader() ?? ""; }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException
                                       or COMException or NullReferenceException or ArgumentException)
        {
            return "";
        }
    }

    /// <summary>同上，值类型版本：失败回 <see cref="Rect.Empty"/>（下游的可信性判定会把它挡掉）。</summary>
    private static Rect SafeRect(Func<Rect> reader)
    {
        try { return reader(); }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException
                                       or COMException or NullReferenceException or ArgumentException)
        {
            return Rect.Empty;
        }
    }

    // ---------- 转发点击 ----------

    /// <summary>
    /// 把一次点击送回图标自己的窗口，让那个应用照常处理（左键 = 它的默认动作，右键 = 它自己的上下文菜单）。
    /// <para>【为什么不在这里检查"面板还开着没开着"】本方法跑在后台线程，跑完界面可能已经关了；
    /// 但点击已经发出去、收不回来，所以这里只把结果回给调用方，界面侧用 <c>IsLoaded</c> 守卫。</para>
    /// </summary>
    public (bool Ok, string? Error) Activate(TrayIconItem item, TrayClickAction action)
    {
        try
        {
            return item.Origin switch
            {
                TrayIconOrigin.ClassicToolbar => PostToOwner(item, action),
                TrayIconOrigin.UiaElement => UiaActivate(item, action),
                _ => (false, "未知的条目来源"),
            };
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or COMException
                                       or ElementNotAvailableException)
        {
            Logger.Warn($"托盘点击转发异常: {ex.Message}");
            return (false, $"转发时出错：{ex.Message}");
        }
    }

    /// <summary>
    /// 经典路：投递回调消息。<b>用 PostMessage 而不是 SendMessage</b>：投递不等回音——那个应用哪怕正在
    /// Full-GC、或者在 WndProc 里弹自己的模态菜单，我们的线程也不会被拖住一秒（面板是瞬态表面，
    /// 但拖住的是常驻进程的线程，代价是整个产品）。
    /// </summary>
    private static (bool Ok, string? Error) PostToOwner(TrayIconItem item, TrayClickAction action)
    {
        if (item.BlockReason is not null) return (false, item.BlockReason);
        var ok = PostMessage(item.OwnerHwnd, item.CallbackMessage,
                             new IntPtr(item.Uid), new IntPtr(TrayLayout.ClickMessage(action)));
        if (!ok)
        {
            var err = Marshal.GetLastWin32Error();
            Logger.Warn($"托盘点击投递失败 uID={item.Uid} hwnd=0x{item.OwnerHwnd.ToInt64():X} 错误 {err}");
            return (false, $"没能把点击交给这个应用（系统错误 {err}）");
        }
        Logger.Info($"托盘点击已转发 uID={item.Uid} 动作={action} hwnd=0x{item.OwnerHwnd.ToInt64():X}");
        return (true, null);
    }

    /// <summary>
    /// UIA 路：重新定位同一个元素 → <c>Invoke</c>；拿不到 Invoke 模式时退化成"在它自己的矩形中心合成一次输入"。
    /// 【为什么每次点击都重新定位】元素句柄既不能跨线程用、也不能跨过"explorer 重建托盘岛"存活，
    /// 存下来再用就是随机的 ElementNotAvailableException；能重放的是<b>身份</b>（Name + AutomationId + 位置）。
    /// </summary>
    private (bool Ok, string? Error) UiaActivate(TrayIconItem item, TrayClickAction action)
    {
        // 右键先走真实输入：托管 UIA 客户端没有"右键/上下文"模式（ContextMenuManager 在服务端一侧），
        // 而托盘应用的菜单是它在 WM_RBUTTONUP 里自己 TrackPopupMenu 出来的——把输入落到真实坐标上才是它认的路。
        if (action == TrayClickAction.RightClick) return SynthesizeMouse(item, right: true);

        var element = FindUiaElement(item);
        if (element is not null)
        {
            try
            {
                var pattern = (InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern);
                pattern.Invoke();
                Logger.Info($"托盘 UIA Invoke：{item.Label}");
                return (true, null);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException or COMException)
            {
                Logger.Warn($"托盘 UIA Invoke 失败（{item.Label}）: {ex.Message}");
            }
        }
        if (item.HasRect)
        {
            var synth = SynthesizeMouse(item, right: false);
            if (synth.Ok) return synth;
            return (false, $"没能触发它自己的动作（Invoke 不可用；{synth.Error}）");
        }
        return (false, "没能触发它自己的动作（该元素不支持 Invoke，也没有位置可以合成点击）");
    }

    /// <summary>在图标自己的矩形中心合成一次左/右键（按下 + 抬起），然后把光标放回它原来的位置。</summary>
    private static (bool Ok, string? Error) SynthesizeMouse(TrayIconItem item, bool right)
    {
        if (!item.HasRect) return (false, "这个图标没有报出位置，不能凭空点它");
        var cx = (item.RectPx.Left + item.RectPx.Right) / 2;
        var cy = (item.RectPx.Top + item.RectPx.Bottom) / 2;
        var hasOld = GetCursorPos(out var before);
        if (!SetCursorPos(cx, cy))
        {
            Logger.Warn($"托盘合成点击定位失败（错误 {Marshal.GetLastWin32Error()}）");
            return (false, "系统不接受把光标移到那个图标上（可能被安全策略拦截）");
        }
        mouse_event(right ? MouseEventfRightdown : MouseEventfLeftdown, 0, 0, 0, UIntPtr.Zero);
        mouse_event(right ? MouseEventfRightup : MouseEventfLeftup, 0, 0, 0, UIntPtr.Zero);
        // 用户的指针不该被我们挪走；放不回去也只是位置差，不影响已经发出去的那次点击
        if (hasOld) SetCursorPos(before.X, before.Y);
        Logger.Info($"托盘合成{(right ? "右" : "左")}键：{item.Label} @({cx},{cy})");
        return (true, null);
    }

    /// <summary>
    /// 按身份在树里重新定位元素：AutomationId 优先，其次 Name；同名多个候选时用"当初读到的矩形"挑最近的。
    /// 【为什么用矩形做决胜】同一个应用常有 2–3 个托盘图标且 tooltip 完全相同（"XX 播放器"），
    /// 只有位置能把"用户点的那一格"和"要点击的那个元素"对上。
    /// </summary>
    private static AutomationElement? FindUiaElement(TrayIconItem item)
    {
        foreach (var hwnd in TrayRoots().Keys)
        {
            if (IsHungAppWindow(hwnd)) continue;
            try
            {
                var root = AutomationElement.FromHandle(hwnd);
                if (root is null) continue;
                var buttons = root.FindAll(TreeScope.Descendants, ButtonCondition);
                AutomationElement? best = null;
                var bestDistance = double.MaxValue;
                for (var i = 0; i < buttons.Count && i < TrayLayout.MaxButtonsPerToolbar; i++)
                {
                    var el = buttons[i];
                    var name = SafeString(() => el.Current.Name);
                    var id = SafeString(() => el.Current.AutomationId);
                    var sameIdentity =
                        (item.UiaAutomationId?.Length > 0 && id == item.UiaAutomationId) ||
                        (item.UiaName?.Length > 0 && name == item.UiaName);
                    if (!sameIdentity) continue;

                    var rect = RectOf(el);
                    if (rect is null) { best ??= el; continue; }
                    var distance = Math.Abs(rect.Value.Left - item.RectPx.Left) + Math.Abs(rect.Value.Top - item.RectPx.Top);
                    if (distance < bestDistance) { bestDistance = distance; best = el; }
                }
                if (best is not null) return best;
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>
    /// 点一下系统自己的"显示隐藏的图标"按钮（隐藏区的条目只有展开后才有 UIA 元素可读）。
    /// 【为什么把这个动作交给用户点，而不是刷新时自动做】它会真的在屏幕上弹出系统 flyout，
    /// 而我们这张卡是<b>激活式弹层</b>：系统 flyout 一出现我们就失去激活 → 面板按设计自关。
    /// 这是期望行为（用户接下来要操作的是系统那个 flyout，不是我们的卡），但必须由用户明确点一次
    /// "展开隐藏图标"才发生——自动 Invoke 会变成"打开面板就把系统的菜单弹出来"，观感像失控。
    /// </summary>
    public (bool Ok, string? Error) ShowHiddenIcons()
    {
        var shell = FindWindowClass(TrayInterop.ShellTrayWnd);
        if (shell == IntPtr.Zero) return (false, "找不到 Shell_TrayWnd");
        if (IsHungAppWindow(shell)) return (false, "任务栏所在进程未响应");
        try
        {
            var root = AutomationElement.FromHandle(shell);
            if (root is null) return (false, "读不到任务栏的无障碍树");
            var buttons = root.FindAll(TreeScope.Descendants, ButtonCondition);
            for (var i = 0; i < buttons.Count && i < TrayLayout.MaxButtonsPerToolbar; i++)
            {
                var el = buttons[i];
                var name = SafeString(() => el.Current.Name);
                var id = SafeString(() => el.Current.AutomationId);
                var looksLikeChevron = name.Contains("隐藏") || name.Contains("溢出")
                                       || name.IndexOf("hidden", StringComparison.OrdinalIgnoreCase) >= 0
                                       || id.IndexOf("ShowHidden", StringComparison.OrdinalIgnoreCase) >= 0
                                       || id.IndexOf("Overflow", StringComparison.OrdinalIgnoreCase) >= 0;
                if (!looksLikeChevron) continue;

                try
                {
                    var pattern = (InvokePattern)el.GetCurrentPattern(InvokePattern.Pattern);
                    pattern.Invoke();
                    Logger.Info($"已展开系统隐藏图标区：{name}");
                    return (true, null);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ElementNotAvailableException or COMException)
                {
                    // 没有 Invoke 模式但有位置：合成一次左键同样能展开（用户看得见的是系统 flyout 出现）
                    var rect = RectOf(el);
                    if (rect is null) return (false, "找到了“显示隐藏的图标”，但它既不支持 Invoke 也没有位置");
                    var proxy = new TrayIconItem { Key = "chevron", Origin = TrayIconOrigin.UiaElement,
                                                  Category = TrayIconCategory.SystemOwned,
                                                  Label = name, HasRect = true, RectPx = rect.Value };
                    return SynthesizeMouse(proxy, right: false);
                }
            }
            return (false, "没找到“显示隐藏的图标”按钮（这个 Windows 版本可能没有隐藏区，或它的名字不在已认识的名单里）");
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or COMException)
        {
            Logger.Warn($"展开隐藏图标失败: {ex.Message}");
            return (false, $"展开失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 释放。<b>本类不驻留任何句柄</b>：远程内存在每次 <see cref="Read"/> 的 finally 里已经还掉，
    /// UIA 元素按身份重新定位（不长期持有）。这个方法存在的唯一理由是与面板的 <c>Shutdown()</c> 契约对称
    /// ——将来真要缓存什么，出口已经在这里。
    /// </summary>
    public void Dispose()
    {
        // 没有非托管资源要还（远程内存已在 Read 的 finally 里释放、UIA 元素不持有），
        // 所以这里既不写 GC.SuppressFinalize（本类没有终结器，写了就是照抄一段无用代码），也不留 TODO。
    }
}
