using System.Runtime.InteropServices;
using System.Text;
using static Beautiful.App.Interop.NativeMethods;

namespace Beautiful.App.Interop;

/// <summary>
/// 通知区域（系统托盘）的 Win32 互操：找窗口、跨进程读 explorer 的按钮数组、把点击转回图标自己的窗口。
/// 独立编写声明，不含第三方代码（借鉴的是 shell 公开 API 的**调用序列**知识）。
/// <para>
/// 【为什么单独一个文件而不并进 <see cref="NativeMethods"/>】这里几乎所有入口都是"跨进程 + 可能永久阻塞"的
/// 组合（<c>SendMessage</c> 给 explorer、<c>ReadProcessMemory</c> 给提权进程）。把它们和通用声明分开，
/// 是为了让"这一层的每个调用都必须带超时或早退"这条纪律能被一眼看到，而不是埋在一堆无害声明中间。
/// </para>
/// <para>
/// 【为什么不 <c>Shell_NotifyIcon</c> 重放 <c>NIM_ADD</c>（WM_USER+0/1/2）】这是网上常见教程的做法，但它不可信：
/// 我们是往 explorer 的托盘里加**一个假图标**，uID 还可能与真实应用撞车（撞上了就把别人的图标改掉或删除）。
/// 更关键的是它根本达不到目的——<c>NIM_ADD</c> 需要我们自己持有那个应用的 <c>HICON</c>，而托盘图标的位图
/// 在添加那一刻就被 explorer 复制进它自己的进程，之后**没有任何公开 API** 能把某个 uID 对应的位图再取回来
/// （<c>TB_GETIMAGELIST</c> 交回的是 explorer 进程里的指针，在我们进程里解引用就是随机崩溃或垃圾内存）。
/// 因此本文件不声明 <c>Shell_NotifyIcon</c>，也不声明 <c>LoadImage</c>——没有可安全使用的场景，
/// 留一个没人调用的 P/Invoke 只会让下一个人以为这条路可行。取图的替代方案见
/// <see cref="Services.TrayIconSource"/>（裁真实屏幕像素）。
/// </para>
/// </summary>
internal static class TrayInterop
{
    // ---------- 窗口类名（shell 的内部结构，微软未公开承诺，全部按"找不到就降级"处理） ----------
    public const string ShellTrayWnd = "Shell_TrayWnd";
    public const string TrayNotifyWnd = "TrayNotifyWnd";
    public const string SysPager = "SysPager";
    public const string ToolbarWindow32 = "ToolbarWindow32";

    /// <summary>Win11 21H2–23H2 的"隐藏图标"溢出窗口（里面仍是经典 ToolbarWindow32）。</summary>
    public const string NotifyIconOverflowWindow = "NotifyIconOverflowWindow";

    /// <summary>Win11 24H2 把溢出区换成 XAML 岛后的顶层窗口类名（此时 ToolbarWindow32 已不存在，只有 UIA 能读）。</summary>
    public const string XamlIslandOverflowWindow = "TopLevelWindowForOverflowXamlIsland";

    /// <summary>
    /// Win11 24H2 任务栏内容的 XAML 岛宿主（<b>Shell_TrayWnd 的子窗口</b>，不是顶层窗口）。
    /// <para>
    /// 这里曾经写过一句"UIA 路不需要它，从 Shell_TrayWnd 进树按 ControlType/Name 命中就够了"——
    /// 本机（26200）实测是错的：从 Shell_TrayWnd 的 HWND 做 <c>FindAll(Descendants, Button)</c> 得到
    /// <b>0 个</b>，而从这个子窗口的 HWND 进同一棵树得到 <b>23 个</b>。XAML 岛自带独立的 UIA
    /// 片段根，Win32 宿主的后代遍历不会跨过去，所以必须按 HWND 分别进树。
    /// 取证脚本：<c>.tools/trayprobe.ps1</c> / <c>trayprobe2.ps1</c>（只读，不建窗口不发输入）。
    /// </para>
    /// </summary>
    public const string TaskbarXamlIslandBridge = "Windows.UI.Composition.DesktopWindowContentBridge";

    /// <summary>岛里第三方托盘图标的唯一 AutomationId（实测：华为电脑管家/华为控制中心/更新提示都是它）。</summary>
    public const string IslandNotifyItemId = "NotifyItemIcon";

    /// <summary>岛里 shell 自己的部件（音量/电源/时钟/显示桌面/输入指示/显示隐藏的图标）的类名前缀。</summary>
    public const string IslandSystemTrayClassPrefix = "SystemTray.";

    // ---------- 工具栏消息（commctrl.h 的 WM_USER 偏移） ----------
    // 【为什么按偏移写而不是抄字面量】任务里给的 0x417/0x41B/0x419 与 commctrl.h 对不上
    // （0x417=WM_USER+23=TB_CUSTOMIZE、0x418=TB_ADDSTRINGW），抄错偏移的后果是"读到结构里的空洞"
    // 而不是编译失败，所以这里全部写成 WM_USER+n 的形式，n 直接对着 commctrl.h 的宏名核对：
    // TB_GETBUTTON=+19、TB_BUTTONCOUNT=+20、TB_GETITEMRECT=+29、TB_GETBUTTONINFOW=+63。
    // 消息号认错的最终表现是"这一路读不到条目"，界面写原因，不会画错东西（见 Services/TrayIconSource）。
    private const int WmUser = 0x0400;

    /// <summary><c>TB_GETBUTTON</c>（WM_USER+19）：按索引取 <see cref="TBBUTTON"/>，lParam 必须是**目标进程里**的指针。</summary>
    public const uint TbGetButton = WmUser + 19;

    /// <summary><c>TB_BUTTONCOUNT</c>（WM_USER+20）：按钮数（含分隔符）。</summary>
    public const uint TbButtonCount = WmUser + 20;

    /// <summary><c>TB_GETITEMRECT</c>（WM_USER+29）：按钮在工具栏**客户区**里的矩形（要再 ClientToScreen）。</summary>
    public const uint TbGetItemRect = WmUser + 29;

    /// <summary><c>TB_GETBUTTONINFOW</c>（WM_USER+63）：按 dwMask 选择性取回状态/样式/lParam(dwData)/命令号/提示文本。</summary>
    public const uint TbGetButtonInfoW = WmUser + 63;

    /// <summary>
    /// <c>TBBUTTONINFO.dwMask</c> 里本层用到的两个位（外加 <see cref="TbifByIndex"/>）。
    /// 置哪个位，目标代码才写哪个字段——<b>没置位的字段它不会写</b>，于是我们从远程缓冲区读回来的是
    /// 自己预填的值，所以预填必须是 0（见 <see cref="Services.TrayIconSource"/> 里构造 <see cref="TBBUTTONINFOW"/> 的地方）。
    /// 其余位（<c>TBIF_IMAGE</c>=0x1、<c>TBIF_STATE</c>=0x4、<c>TBIF_STYLE</c>=0x8、<c>TBIF_LPARAM</c>=0x10、
    /// <c>TBIF_SIZE</c>=0x40）这里刻意不要：状态位、样式位与 <c>dwData</c> 已经由 <see cref="TbGetButton"/>
    /// 一次性拿到（那次读的是整条 <see cref="TBBUTTON"/>），在这里重复索取只会多一个"两处数值不一致时该信谁"
    /// 的问题，而 <see cref="TbifCommand"/> 是唯一必须重复的——它用来交叉校验我们要的确实是同一个按钮。
    /// </summary>
    public const uint TbifText = 0x0002, TbifCommand = 0x0020;

    /// <summary><c>TBIF_BYINDEX</c>：置位后 wParam 解释为**索引**，否则解释为**命令号**（TB_GETBUTTONINFO 的文档行为）。</summary>
    public const uint TbifByIndex = 0x8000_0000;

    // 按钮样式/状态位（BTNS_SEP=0x01 / TBSTATE_HIDDEN=0x08）与鼠标消息号
    // （WM_LBUTTONUP=0x202 / WM_RBUTTONUP=0x205 / WM_LBUTTONDBLCLK=0x203 / WM_CONTEXTMENU=0x7B）
    // **不在这里声明**：它们回答的是"读到什么算分隔符、哪个动作该发什么消息"，属于判断而不是互操，
    // 唯一来源放在 Beautiful.Core.Tray.TrayLayout（纯算术、可单测）。同一个数在两个项目里各写一遍，
    // 就是给了未来一次"只改一处"的机会。
    //
    // 同理不声明 Shell_NotifyIcon 与它的 NIM_ADD/NIM_MODIFY/NIM_DELETE（WM_USER+0/1/2）：
    // 重放 NIM_ADD 需要自己持有那个应用的 HICON（取不到，见类型注释），而且会往 explorer 的托盘里
    // 塞一个假图标、可能与真实应用撞 uID。留着这三个常量的唯一效果是让下一个人以为这条路可走。

    // ---------- 进程访问权限 ----------
    /// <summary><c>PROCESS_VM_READ</c>：读 explorer 的按钮数组与提示文本。</summary>
    public const uint ProcessVmRead = 0x0010;

    /// <summary><c>PROCESS_VM_OPERATION</c>：VirtualAllocEx/FreeEx（给跨进程 SendMessage 准备参数缓冲区）。</summary>
    public const uint ProcessVmOperation = 0x0008;

    public const uint MemCommit = 0x1000, MemRelease = 0x10000;
    public const uint PageReadwrite = 0x04;

    /// <summary>
    /// <c>SendMessageTimeout</c> 的超时（毫秒）。
    /// 【为什么不是 INFINITE】explorer 挂了的时候"读托盘"这个动作绝不能把我们的 UI/后台线程一起拖住；
    /// 200ms 足够一次正常的跨进程 send（本机内同进程 send 是微秒级），超了就当这一路不可用。
    /// </summary>
    public const uint SendTimeoutMs = 200;

    // ---------- 结构体 ----------

    /// <summary>
    /// <c>TBBUTTON</c>（x64 布局，32 字节）：<c>iBitmap(4) idCommand(4) fsState(1) fsStyle(1) bReserved(2)
    /// [填充 4] dwData(8) iString(8)</c>。
    /// 【为什么手写填充而不是 Pack=1】bReserved 之后原生对齐要把 <c>dwData</c> 抬到 8 的倍数；
    /// <c>Pack=8</c>（默认）让封送器算出同一个空洞，而 <c>Pack=1</c> 会少 4 字节 → 读回来的 dwData
    /// 落在别人的 iString 上，于是"每个图标都属于同一个窗口"这种难查故障。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct TBBUTTON
    {
        public int iBitmap;
        public int idCommand;
        public byte fsState;
        public byte fsStyle;
        public byte bReserved0;
        public byte bReserved1;
        public IntPtr dwData;
        public IntPtr iString;
    }

    /// <summary>
    /// <c>TBBUTTONINFOW</c>（x64 布局，40 字节）：<c>cbSize(4) dwMask(4) iBitmap(4) idCommand(4)
    /// fsState(1) fsStyle(1) wReserved(2) [填充 4] dwData(8) iString(8)</c>。
    /// <c>iString</c> 这里放的是**目标进程里的文本缓冲区地址**（Remote 模式下由调用方分配），
    /// 目标代码把 UTF-16 提示文本写进去，我们再读回来。
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    public struct TBBUTTONINFOW
    {
        public int cbSize;
        public int dwMask;
        public int iBitmap;
        public int idCommand;
        public byte fsState;
        public byte fsStyle;
        public short wReserved;
        public IntPtr dwData;
        public IntPtr iString;
    }

    // ---------- P/Invoke ----------

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    /// <summary>
    /// 带超时的 SendMessage。<b>本层不使用不带超时的 SendMessage</b>：托盘在 explorer 进程里，
    /// explorer 一旦挂住，普通 SendMessage 会永久阻塞调用线程（项目承重墙：<c>IsHungAppWindow</c> 只是
    /// 第一道早退，第二道必须是超时——检查与发消息之间 explorer 随时可能变挂）。
    /// </summary>
    /// <param name="msg">消息号。</param>
    /// <param name="wParam">wParam（工具栏消息里通常是索引或命令号）。</param>
    /// <param name="lParam">lParam（跨进程时必须指向**目标进程**的内存）。</param>
    /// <param name="result">目标窗口的返回值（失败时无意义，调用方看返回值是否 false）。</param>
    /// <returns>false = 窗口挂死、超时或出错（<c>Marshal.GetLastWin32Error()</c> 可细化）。</returns>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        uint flags, uint timeoutMs, out IntPtr result);

    public const uint SmtoAbortIfHung = 0x0002;      // SMTO_ABORTIFHUNG：目标挂死立即返回，不排队
    public const uint SmtoTimeoutIfNotHung = 0x0008; // SMTO_NOTIMEOUTIFNOTHUNG：窗口活着就多等一会儿，别把慢当成挂

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr baseAddress,
        byte[] buffer, int size, out int read);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr baseAddress,
        byte[] buffer, int size, out int written);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr VirtualAllocEx(IntPtr hProcess, IntPtr address, int size,
        uint allocType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool VirtualFreeEx(IntPtr hProcess, IntPtr address, int size, uint freeType);

    [DllImport("user32.dll")]
    public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);

    /// <summary><c>SetCursorPos</c>：UIA 路右键前把光标放到真实图标上（UIA 没有"右键"模式，见 Services 侧注释）。
    /// 取回/还原光标位置用全库唯一定义的 <see cref="NativeMethods.GetCursorPos"/>，这里不再重复声明。</summary>
    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    /// <summary>
    /// <c>mouse_event</c>：只用来在已经 <see cref="SetCursorPos"/> 定位之后补一次按下/抬起（左键或右键）。
    /// 【为什么需要它】托管 UIA 客户端<b>没有</b>"右键/上下文"模式（<c>ContextMenuManager</c> 在服务端一侧），
    /// 而托盘应用的菜单是它在 <c>WM_RBUTTONUP</c> 里自己 <c>TrackPopupMenu</c> 出来的——
    /// 让真实的系统输入落到真实的图标坐标上，是唯一不需要句柄与 uID 也能"照常弹出它自己的菜单"的做法。
    /// 左键那一对只在 <c>Invoke</c> 模式不可用时用（同一理由：位置是两路都可能拿到的唯一事实）。
    /// </summary>
    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

    public const uint MouseEventfLeftdown = 0x0002, MouseEventfLeftup = 0x0004,
                     MouseEventfRightdown = 0x0008, MouseEventfRightup = 0x0010;

    // ---------- 便捷封装 ----------

    /// <summary>
    /// 带超时的 send 包装：flags 固定 <c>SMTO_ABORTIFHUNG</c>（+ <c>SMTO_NOTIMEOUTIFNOTHUNG</c>），
    /// 所有跨进程读都走这里。返回 false 的三种原因（挂死/超时/窗口无效）在这里不区分——调用方只需要
    /// "这一条读不到"，区分它们不会改变任何行为，只会长出一堆没人读的 out 参数。
    /// </summary>
    public static bool Send(IntPtr hwnd, uint msg, long wParam, long lParam, out long result)
    {
        var ok = SendMessageTimeout(hwnd, msg, new IntPtr(wParam), new IntPtr(lParam),
            SmtoAbortIfHung | SmtoTimeoutIfNotHung, SendTimeoutMs, out var raw);
        result = ok ? raw.ToInt64() : 0;
        return ok;
    }

    /// <summary>
    /// <c>FindWindowEx</c> 的"按类名找第一个子窗口"包装。<paramref name="className"/> 为 null 时匹配任意类名。
    /// 【为什么不用 <c>EnumChildWindows</c>】托盘那条链是**唯一路径**（TrayNotifyWnd→SysPager→ToolbarWindow32），
    /// 逐级 FindWindowEx 比枚举全部子窗口再筛更便宜，也不会意外把第三方挂进任务栏的自定义子窗口当成托盘工具栏。
    /// </summary>
    public static IntPtr FindChildByClass(IntPtr parent, string className)
        => FindWindowEx(parent, IntPtr.Zero, className, null);

    /// <summary>
    /// 打开目标进程用于远程读写。<b>只读 + 可分配</b>：<c>PROCESS_VM_READ|PROCESS_VM_OPERATION|PROCESS_QUERY_LIMITED_INFORMATION</c>。
    /// 失败（目标提权、被保护进程）返回 <see cref="IntPtr.Zero"/>，由调用方降级——不去试更高的权限或注入代码。
    /// </summary>
    public static IntPtr OpenForRead(uint pid)
        => OpenProcess(ProcessVmRead | ProcessVmOperation | ProcessQueryLimitedInformation, false, pid);

    /// <summary>把结构体序列化成字节（仅用于 blittable 结构：字段全是数值/IntPtr，无 string）。</summary>
    public static byte[] ToBytes<T>(T value) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var bytes = new byte[size];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { Marshal.StructureToPtr(value, handle.AddrOfPinnedObject(), false); }
        finally { handle.Free(); }
        return bytes;
    }

    /// <summary>把远程读回的字节还原成结构体；长度不足返回 false（绝不猜缺的字段是什么）。</summary>
    public static bool TryFromBytes<T>(byte[] bytes, out T value) where T : struct
    {
        value = default;
        if (bytes.Length < Marshal.SizeOf<T>()) return false;
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            value = Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject());
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or MarshalDirectiveException)
        {
            // 结构里混进非 blittable 字段（string/数组）时走这条：宁可回 false 也不返回半截值
            return false;
        }
        finally { handle.Free(); }
    }

    /// <summary>
    /// 读远程指针指向的 UTF-16 字符串（提示文本走的是"我们自己分配 + 目标写回"的缓冲区，
    /// 这里只做长度钳制：超过 512 个字符的 tooltip 在托盘里显示不出来，读回来也是垃圾）。
    /// </summary>
    public static string DecodeUtf16(byte[] bytes)
    {
        var usable = Math.Min(bytes.Length, 512 * 2) & ~1;   // 向下取偶数字节（半个字符不要）
        if (usable <= 0) return "";
        var text = Encoding.Unicode.GetString(bytes, 0, usable);
        var end = text.IndexOf('\0');
        return (end >= 0 ? text[..end] : text).Trim();
    }
}
