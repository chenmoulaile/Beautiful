// ============================================================
// beautiful_hook.c — Beautiful.Hook.dll（v4.0.0-alpha.17 · 协议 v2）
// 目的：真替换 Windows 原生的最小化 / 关闭 / 打开动画（MyDockFinder 同款思路）。
//
// 三条拦截路径：
//   [MIN]  WH_CBT / HCBT_MINMAX（最小化方向）：原生缩小动画开跑之前，在目标进程内
//          PrintWindow 抓帧 → PostMessage → 等 ack ≤40ms → 返回 1 阻断原生最小化。
//          宿主播 genie 后静默落位（DWM 过渡临时关闭）。v1 既有路径，协议 v2 不变语义。
//   [DESTROY] WH_CBT / HCBT_DESTROYWND（Flags bit1 = 关闭动画开启时）：窗口销毁前
//          在目标进程内抓下最后一帧 → PostMessage →【不等待、不阻断】——窗口照常销毁，
//          宿主拿这帧播"收拢消失"动画。阻断销毁再重发 WM_CLOSE 会让"未保存提示"弹两次，
//          所以 v2 明确选择：关闭动画 = 销毁前供帧 + 事后替身，不做销毁阻断。
//   [SHOW] SetWinEventHook(EVENT_OBJECT_SHOW, WINEVENT_INCONTEXT)：窗口隐藏→可见的
//          事件在【目标进程内、ShowWindow 返回之前】同步到达（首帧尚未上屏）——
//          就地 DWM Cloak（窗口从合成树摘掉，用户永远看不到原生出现）→ PostMessage →
//          等 ack ≤40ms：ack 到 = 宿主接管（宿主抓帧、播入场替身、演完 Cross-Process
//          Uncloak）；超时 = 就地 Uncloak 放行（原生出现，绝不让窗口隐形卡死）。
//
// 安全红线（注入组件的第一设计约束，v1 全部继承）：
//   1. 本 DLL 跑在别人进程里 —— 每个回调全程 __try/__except，任何一步失败都
//      CallNextHookEx / 直接返回放行，绝不让宿主进程崩。
//   2. 阻断类等待只有 40ms：主程序死了/忙了/拒绝，原生行为照常进行。
//   3. 自家进程、被抑制（程序化动作）的窗口一律放行，防递归。
//   4. 不创建线程、不弹窗、不写文件、不动注册表、不持有 CRT 堆以外的运行时。
//
// 构建（Linux 交叉编译，无需 MSVC；clang 提供 MS SEH + mingw gcc 链接）：
//   bash scripts/build-hook-dll.sh
// 协议布局：src/Beautiful.Core/Animation/HookProtocol.cs（单一图纸，两侧对齐）。
// ============================================================

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <dwmapi.h>
#include <excpt.h>

/* ---- 与 C# 侧（Beautiful.Core/Animation/HookProtocol.cs）逐字对齐的常量 ---- */
#define BH_SHARED_NAME   L"Local\\Beautiful.Hook.Shared.v2"
#define BH_ACK_NAME      L"Local\\Beautiful.Hook.Ack.v2"
#define BH_MAGIC         0x32484B42u   /* "BHK2" */
#define BH_VERSION       2u
#define BH_MAX_WIDTH     2560
#define BH_MAX_HEIGHT    1440
#define BH_MIN_WIDTH     120
#define BH_MIN_HEIGHT    120
#define BH_ACK_TIMEOUT_MS 40

/* 槽头 48 字节：ReadySeq/Width/Height/Stride/Hwnd/Kind/Pid/Rect(L,T,R,B) */
#define BH_SLOT_HEADER   48
#define BH_SLOT_OFF_KIND   24
#define BH_SLOT_OFF_PID    28
#define BH_SLOT_OFF_LEFT   32
#define BH_SLOT_OFF_TOP    36
#define BH_SLOT_OFF_RIGHT  40
#define BH_SLOT_OFF_BOTTOM 44

#define BH_KIND_MINIMIZE 1u
#define BH_KIND_DESTROY  2u
#define BH_KIND_SHOW     3u

#define BH_FLAG_SHUTDOWN      0x1u
#define BH_FLAG_CLOSE_ENABLED 0x2u

/* ---- 共享内存头（每份注入副本 OpenFileMapping 得到同一块） ---- */
typedef struct _BH_SHARED {
    UINT32 Magic;                 /*  0: BH_MAGIC，主程序安装时写入 */
    UINT32 Version;               /*  4 */
    UINT64 HostHwnd;              /*  8: 主程序通知窗口 */
    UINT32 NotifyMsg;             /* 16: RegisterWindowMessage 的消息号 */
    volatile UINT32 Flags;        /* 20: bit0=放行一切（退出中） bit1=关闭动画开 */
    volatile UINT64 SuppressHwnd; /* 24: 主程序正在程序化动作的窗口（0=无） */
    volatile UINT32 Seq;          /* 32: 最近一次捕获的序号（递增） */
    UINT32 SlotBytes;             /* 36: 单槽像素字节数 */
    UINT32 SlotCount;             /* 40: 恒为 2 */
    UINT32 Reserved;              /* 44: 补齐到 48 */
} BH_SHARED;

/* 槽头：紧跟在 BH_SHARED 之后；像素再随后（Kind=SHOW 的槽不含像素） */
typedef struct _BH_SLOT {
    volatile UINT32 ReadySeq;   /*  0: 本槽数据对应的 Seq；0 = 空 */
    UINT32 Width;               /*  4: SHOW 槽为 0（无像素） */
    UINT32 Height;              /*  8 */
    UINT32 Stride;              /* 12: 字节/行 = Width*4（BGRA，自上而下） */
    UINT64 Hwnd;                /* 16 */
    UINT32 Kind;                /* 24: BH_KIND_* */
    UINT32 Pid;                 /* 28: 目标窗口进程 id（销毁后宿主仍可查进程名） */
    INT32  Left;                /* 32: 抓帧瞬间的窗口矩形（物理像素） */
    INT32  Top;                 /* 36 */
    INT32  Right;               /* 40 */
    INT32  Bottom;              /* 44 */
} BH_SLOT;

static HMODULE    g_self     = NULL;
static HHOOK      g_hook     = NULL;   /* WH_CBT，只在主程序进程里有意义 */
static HWINEVENTHOOK g_showHook = NULL;/* EVENT_OBJECT_SHOW（in-context） */
static HWND       g_host     = NULL;   /* 主程序通知窗口（每副本都有） */
static UINT       g_msg      = 0;
static HANDLE     g_section  = NULL;   /* 本副本的节句柄 */
static BH_SHARED* g_shared   = NULL;
static HANDLE     g_ack      = NULL;   /* 本副本的事件句柄 */
static UINT32     g_localSeq = 0;      /* 本副本的捕获计数 */
static DWORD      g_hostPid  = 0;      /* 主程序进程：自家窗口一律放行 */
static CRITICAL_SECTION g_cs;
static BOOL       g_csOk     = FALSE;

/* ---- 注入副本不该演的窗口类（与宿主 WindowEligibility 同源的最小集） ---- */
static BOOL IsExcludedClass(HWND hwnd)
{
    WCHAR cls[64];
    if (GetClassNameW(hwnd, cls, 64) <= 0) return FALSE;
    if (lstrcmpW(cls, L"#32768") == 0) return TRUE;                       /* 菜单弹层 */
    if (lstrcmpW(cls, L"Windows.UI.Core.CoreWindow") == 0) return TRUE;   /* UWP 浮层 */
    if (lstrcmpW(cls, L"Shell_TrayWnd") == 0) return TRUE;                /* 任务栏 */
    if (lstrcmpW(cls, L"Shell_SecondaryTrayWnd") == 0) return TRUE;
    if (lstrcmpW(cls, L"WorkerW") == 0) return TRUE;                      /* 桌面层 */
    if (lstrcmpW(cls, L"Progman") == 0) return TRUE;
    return FALSE;
}

/* ---- 惰性资源：注入副本第一次用到时才打开共享节/事件 ---- */
static BOOL EnsureLocalResources(void)
{
    if (g_shared != NULL) return TRUE;
    /* 竞态安全靠幂等：两个线程同时进来最多各开一份句柄，最后写的生效，
       多开的那份是合法句柄，泄漏一个 HANDLE 换无锁，值。 */
    g_section = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, BH_SHARED_NAME);
    if (g_section == NULL) return FALSE;
    g_shared = (BH_SHARED*)MapViewOfFile(g_section, FILE_MAP_ALL_ACCESS, 0, 0, 0);
    if (g_shared == NULL) return FALSE;
    if (g_shared->Magic != BH_MAGIC) { g_shared = NULL; return FALSE; }
    g_ack = OpenEventW(SYNCHRONIZE, FALSE, BH_ACK_NAME);
    return g_ack != NULL;
}

/* ---- 画面是否"值得播"（alpha.18 修复，真机反馈：原生最小化动画还在）。
   旧版把"整帧同色"判废——空记事本/空文档窗口是纯白的，恰好整帧同色，
   于是最常见的测试窗口永远走不到拦截，用户看到的就是原生动画。
   纯白/纯色是【合法窗口内容】，只有"全零"（BGRA 全 0 = DIB 未写入的失败特征）才判废；
   PrintWindow 的返回值仍是第一道权威信号，这里只是补一层失败兜底。 ---- */
static BOOL FrameHasContent(const BYTE* pixels, UINT32 stride, UINT32 width, UINT32 height)
{
    const UINT32 step = 24;                       /* 抽样即可 */
    UINT32 x, y;
    for (y = 0; y < height; y += step)
    {
        const BYTE* row = pixels + (SIZE_T)y * stride;
        for (x = 0; x < width; x += step)
        {
            const BYTE* px = row + (SIZE_T)x * 4;
            if (px[0] != 0 || px[1] != 0 || px[2] != 0 || px[3] != 0) return TRUE;
        }
    }
    return FALSE;
}

/* ---- 空帧槽（alpha.18）：抓帧失败但拦截仍要生效的信令。
   只写槽头（Width=0）+ 发号，绝不写像素。宿主读到 ReadySeq==Seq 且 Width=0，
   走"缓存帧/静默落位"接管——原生动画照样被吃掉。调用方已持 g_cs。 ---- */
static void MarkEmptySlot(HWND hwnd, int slot, UINT32 kind, DWORD pid, const RECT* rect)
{
    BH_SHARED* s = g_shared;
    if (s == NULL) return;
    const UINT32 next = g_localSeq + 1;
    BYTE* dst = (BYTE*)s + sizeof(BH_SHARED)
              + (SIZE_T)slot * (BH_SLOT_HEADER + (SIZE_T)s->SlotBytes);
    BH_SLOT* sh = (BH_SLOT*)dst;
    sh->ReadySeq = 0;          /* 先清旧号，防上一帧残留被当作新帧读 */
    sh->Width  = 0;
    sh->Height = 0;
    sh->Stride = 0;
    sh->Hwnd   = (UINT64)(UINT_PTR)hwnd;
    sh->Kind   = kind;
    sh->Pid    = pid;
    sh->Left   = rect->left;
    sh->Top    = rect->top;
    sh->Right  = rect->right;
    sh->Bottom = rect->bottom;
    sh->ReadySeq = next;       /* 后发号：先写数据后可见 */
    s->Seq       = next;
    g_localSeq   = next;
}

/* ---- 就地抓帧到第 slot 个槽（调用方已持 g_cs）。show 槽只写头不抓像素 ---- */
static BOOL CaptureIntoSlot(HWND hwnd, int slot, UINT32 kind, DWORD pid, const RECT* rect)
{
    BH_SHARED* s = g_shared;
    if (s == NULL) return FALSE;

    const int w = rect->right - rect->left;
    const int h = rect->bottom - rect->top;
    if (w < BH_MIN_WIDTH || h < BH_MIN_HEIGHT) return FALSE;
    if (w > BH_MAX_WIDTH || h > BH_MAX_HEIGHT) return FALSE;

    const UINT32 stride = (UINT32)w * 4;
    const UINT32 next = g_localSeq + 1;
    BYTE* dst = (BYTE*)s + sizeof(BH_SHARED)
              + (SIZE_T)slot * (BH_SLOT_HEADER + (SIZE_T)s->SlotBytes);
    BH_SLOT* sh = (BH_SLOT*)dst;
    BYTE* px = dst + BH_SLOT_HEADER;
    BOOL ok = FALSE;

    if (kind == BH_KIND_SHOW)
    {
        /* SHOW：不抓像素（窗口还没首帧，PrintWindow 会向宿主进程里还没跑起来的
           窗口过程发 WM_PRINT——重入风险不值得；宿主 Cloak 稳定后自行抓帧）。 */
        ok = TRUE;
    }
    else
    {
        BITMAPINFO bi;
        ZeroMemory(&bi, sizeof(bi));
        bi.bmiHeader.biSize        = sizeof(BITMAPINFOHEADER);
        bi.bmiHeader.biWidth       = w;
        bi.bmiHeader.biHeight      = -h;              /* 自上而下 */
        bi.bmiHeader.biPlanes      = 1;
        bi.bmiHeader.biBitCount    = 32;
        bi.bmiHeader.biCompression = BI_RGB;

        void* bits = NULL;
        HDC wdc = GetWindowDC(hwnd);
        if (wdc == NULL) return FALSE;
        HDC mdc = CreateCompatibleDC(wdc);
        HBITMAP bmp = mdc != NULL ? CreateDIBSection(wdc, &bi, DIB_RGB_COLORS, &bits, NULL, 0) : NULL;
        HBITMAP old = bmp != NULL ? (HBITMAP)SelectObject(mdc, bmp) : NULL;

        if (bits != NULL)
        {
            /* PW_RENDERFULLCONTENT = 2：DirectComposition 内容也参与重绘（Win 8.1+） */
            if (PrintWindow(hwnd, mdc, 2) || PrintWindow(hwnd, mdc, 0))
            {
                if (FrameHasContent((const BYTE*)bits, stride, (UINT32)w, (UINT32)h))
                {
                    memcpy(px, bits, (SIZE_T)stride * (SIZE_T)h);
                    ok = TRUE;
                }
            }
        }

        if (old != NULL) SelectObject(mdc, old);
        if (bmp != NULL) DeleteObject(bmp);
        if (mdc != NULL) DeleteDC(mdc);
        ReleaseDC(hwnd, wdc);
    }

    if (ok)
    {
        /* 先写数据后发号：ReadySeq 非零即本槽可读；槽号 = 发号奇偶（两侧一致） */
        sh->Width    = (kind == BH_KIND_SHOW) ? 0 : (UINT32)w;
        sh->Height   = (kind == BH_KIND_SHOW) ? 0 : (UINT32)h;
        sh->Stride   = (kind == BH_KIND_SHOW) ? 0 : stride;
        sh->Hwnd     = (UINT64)(UINT_PTR)hwnd;
        sh->Kind     = kind;
        sh->Pid      = pid;
        sh->Left     = rect->left;
        sh->Top      = rect->top;
        sh->Right    = rect->right;
        sh->Bottom   = rect->bottom;
        sh->ReadySeq = next;
        s->Seq       = next;
        g_localSeq   = next;
    }
    return ok;
}

/* ---- 目标窗口的基本过滤（MIN/DESTROY/SHOW 共用）。不合法返回 FALSE = 放行 ---- */
static BOOL TargetEligible(HWND hwnd, DWORD* outPid, RECT* outRect)
{
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid == 0 || pid == g_hostPid) return FALSE;
    if (GetAncestor(hwnd, GA_ROOT) != hwnd) return FALSE;   /* 只演顶层 */
    if (GetAncestor(hwnd, GA_PARENT) != NULL) return FALSE; /* 排除 message-only */
    if (GetWindow(hwnd, GW_OWNER) != NULL) return FALSE;    /* 拥有者窗口（对话框/弹层）不演 */
    if (IsExcludedClass(hwnd)) return FALSE;
    if (GetWindowLongPtrW(hwnd, GWL_EXSTYLE) & WS_EX_TOOLWINDOW) return FALSE;

    RECT r;
    if (!GetWindowRect(hwnd, &r)) return FALSE;
    const int w = r.right - r.left;
    const int h = r.bottom - r.top;
    if (w < BH_MIN_WIDTH || h < BH_MIN_HEIGHT) return FALSE;
    if (w > BH_MAX_WIDTH || h > BH_MAX_HEIGHT) return FALSE;

    *outPid = pid;
    *outRect = r;
    return TRUE;
}

/* ---- 通知宿主并等确认。返回 TRUE = 宿主已接管（ack 收到） ---- */
static BOOL NotifyHost(UINT32 kind, DWORD pid, const RECT* rect, BOOL waitAck)
{
    BH_SHARED* s = g_shared;
    if (s == NULL) return FALSE;
    g_msg  = s->NotifyMsg;                       /* 每次同步，防主程序换窗口 */
    g_host = (HWND)(UINT_PTR)s->HostHwnd;
    if (g_host == NULL || g_msg == 0) return FALSE;
    if (!PostMessageW(g_host, g_msg, (WPARAM)s->Seq, 0)) return FALSE;
    if (!waitAck) return FALSE;                  /* DESTROY：投递即走，不等也不阻断 */
    if (g_ack == NULL) return FALSE;
    return WaitForSingleObject(g_ack, BH_ACK_TIMEOUT_MS) == WAIT_OBJECT_0;
}

/* ---- CBT 钩子本体（在目标进程内执行） ---- */
static LRESULT CALLBACK CbtProc(int code, WPARAM wParam, LPARAM lParam)
{
    if (code == HCBT_MINMAX)
    {
        const int show = (int)(short)LOWORD(lParam);
        /* 只拦最小化方向：SW_SHOWMINIMIZED(2) / SW_MINIMIZE(6) / SW_SHOWMINNOACTIVE(7) */
        if (show == 2 || show == 6 || show == 7)
        {
            __try
            {
                if ((g_shared != NULL || EnsureLocalResources())
                    && !(g_shared->Flags & BH_FLAG_SHUTDOWN))
                {
                    HWND hwnd = (HWND)wParam;
                    DWORD pid = 0;
                    RECT rect;
                    if (TargetEligible(hwnd, &pid, &rect) && !IsIconic(hwnd)
                        && IsWindowVisible(hwnd)
                        && (UINT64)(UINT_PTR)hwnd != g_shared->SuppressHwnd
                        && g_csOk && TryEnterCriticalSection(&g_cs))
                    {
                        const int slot = (int)((g_localSeq + 1) & 1);
                        /* alpha.18：抓帧成功与否都必须拦。旧版抓帧失败直接放行，
                           原生动画照播（真机反馈原话）。现在失败时写空帧槽（Width=0）
                           照样通知宿主接管——宿主用缓存帧播 genie 或静默落位，
                           原生动画永远被吃掉；宿主死了 PostMessage 自然失败 → 放行，
                           安全红线不变。 */
                        if (!CaptureIntoSlot(hwnd, slot, BH_KIND_MINIMIZE, pid, &rect))
                            MarkEmptySlot(hwnd, slot, BH_KIND_MINIMIZE, pid, &rect);
                        if (NotifyHost(BH_KIND_MINIMIZE, pid, &rect, TRUE))
                        {
                            LeaveCriticalSection(&g_cs);
                            return 1;   /* 阻断：原生最小化动画不会发生 */
                        }
                        LeaveCriticalSection(&g_cs);
                    }
                }
            }
            __except (EXCEPTION_EXECUTE_HANDLER)
            {
                /* 注入组件的第一铁律：绝不把异常抛给宿主进程 */
            }
        }
    }
    else if (code == HCBT_DESTROYWND)
    {
        /* 关闭动画供帧：只在宿主开了关闭动画（Flags bit1）时花这次抓帧的钱。
           不等待、不阻断——窗口照常销毁，宿主拿帧播收拢替身。 */
        __try
        {
            if ((g_shared != NULL || EnsureLocalResources())
                && !(g_shared->Flags & BH_FLAG_SHUTDOWN)
                && (g_shared->Flags & BH_FLAG_CLOSE_ENABLED))
            {
                HWND hwnd = (HWND)wParam;
                DWORD pid = 0;
                RECT rect;
                if (TargetEligible(hwnd, &pid, &rect) && IsWindowVisible(hwnd)
                    && (UINT64)(UINT_PTR)hwnd != g_shared->SuppressHwnd
                    && g_csOk && TryEnterCriticalSection(&g_cs))
                {
                    const int slot = (int)((g_localSeq + 1) & 1);
                    CaptureIntoSlot(hwnd, slot, BH_KIND_DESTROY, pid, &rect);
                    NotifyHost(BH_KIND_DESTROY, pid, &rect, FALSE);
                    LeaveCriticalSection(&g_cs);
                }
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            /* 同上：异常绝不外抛 */
        }
    }
    return CallNextHookEx(NULL, code, wParam, lParam);
}

/* ---- EVENT_OBJECT_SHOW（in-context：目标进程内、首帧上屏之前同步执行） ----
   就地 Cloak 真窗口 → 通知宿主 → ack 到 = 宿主接管（它负责演完 Uncloak）；
   超时 = 就地 Uncloak（原生出现）。绝不让窗口隐形卡死。 */
static void CALLBACK ShowEventProc(HWINEVENTHOOK hook, DWORD event, HWND hwnd,
                                   LONG idObject, LONG idChild,
                                   DWORD thread, DWORD time)
{
    (void)hook; (void)event; (void)thread; (void)time;
    if (hwnd == NULL || idObject != 0 /*OBJID_WINDOW*/ || idChild != 0) return;

    __try
    {
        if ((g_shared == NULL && !EnsureLocalResources())
            || (g_shared->Flags & BH_FLAG_SHUTDOWN))
        {
            return;
        }

        DWORD pid = 0;
        RECT rect;
        if (!TargetEligible(hwnd, &pid, &rect)) return;
        if ((UINT64)(UINT_PTR)hwnd == g_shared->SuppressHwnd) return;

        /* 已被别人（虚拟桌面/Shell/UWP）Cloak 的窗口不碰 */
        LONG cloaked = 0;
        if (DwmGetWindowAttribute(hwnd, 14 /*DWMWA_CLOAKED*/, &cloaked, sizeof(cloaked)) == 0
            && cloaked != 0)
        {
            return;
        }

        /* 就地 Cloak：ShowWindow 尚未返回、首帧未上屏 —— 原生"出现"被整个吃掉 */
        LONG on = 1;
        if (DwmSetWindowAttribute(hwnd, 13 /*DWMWA_CLOAK*/, &on, sizeof(on)) != 0) return;

        BOOL taken = FALSE;
        if (g_csOk && TryEnterCriticalSection(&g_cs))
        {
            const int slot = (int)((g_localSeq + 1) & 1);
            if (CaptureIntoSlot(hwnd, slot, BH_KIND_SHOW, pid, &rect))
            {
                taken = NotifyHost(BH_KIND_SHOW, pid, &rect, TRUE);
            }
            LeaveCriticalSection(&g_cs);
        }

        if (!taken)
        {
            /* 宿主没接（忙/拒/超时/发送失败）：就地还原，原生显示照常 */
            LONG off = 0;
            DwmSetWindowAttribute(hwnd, 13 /*DWMWA_CLOAK*/, &off, sizeof(off));
        }
    }
    __except (EXCEPTION_EXECUTE_HANDLER)
    {
        /* 注入组件的第一铁律 */
    }
}

/* ---- 主程序 API ---- */

/* 安装：host=通知窗口，msg=RegisterWindowMessage("Beautiful.Hook.Notify.v2") 的值。
   返回 TRUE = 两条钩子（WH_CBT + SHOW 事件）都已装上。 */
__declspec(dllexport) BOOL WINAPI BH_Install(HWND host, UINT msg)
{
    if (g_hook != NULL) return TRUE;   /* 幂等 */
    if (host == NULL || msg == 0) return FALSE;

    g_self = GetModuleHandleW(L"Beautiful.Hook.dll");
    if (g_self == NULL) return FALSE;

    /* 共享节：头（一页）+ 双槽 */
    const UINT32 slotBytes = (UINT32)BH_MAX_WIDTH * BH_MAX_HEIGHT * 4;
    const SIZE_T sectionSize = sizeof(BH_SHARED) + 2 * ((SIZE_T)BH_SLOT_HEADER + slotBytes);
    g_section = CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0,
                                   (DWORD)sectionSize, BH_SHARED_NAME);
    if (g_section == NULL) return FALSE;
    g_shared = (BH_SHARED*)MapViewOfFile(g_section, FILE_MAP_ALL_ACCESS, 0, 0, 0);
    if (g_shared == NULL) return FALSE;

    ZeroMemory(g_shared, sectionSize);
    g_shared->Magic      = BH_MAGIC;
    g_shared->Version    = BH_VERSION;
    g_shared->HostHwnd   = (UINT64)(UINT_PTR)host;
    g_shared->NotifyMsg  = msg;
    g_shared->SlotBytes  = slotBytes;
    g_shared->SlotCount  = 2;

    g_ack = CreateEventW(NULL, FALSE, FALSE, BH_ACK_NAME);   /* 自动复位 */
    if (g_ack == NULL) return FALSE;

    {
        DWORD pid = 0;
        GetWindowThreadProcessId(host, &pid);
        g_hostPid = pid;
    }
    g_host = host;
    g_msg  = msg;

    /* SHOW 路径：in-context 事件钩子（系统把本 DLL 载入产生事件的进程并就地回调）。
       32 位进程装不进 64 位 DLL → 那些进程收不到就地 SHOW，原生出现照旧（如实降级）。 */
    g_showHook = SetWinEventHook(0x8002 /*EVENT_OBJECT_SHOW*/, 0x8002,
                                 g_self, ShowEventProc, 0, 0, 0x4 /*WINEVENT_INCONTEXT*/);

    g_hook = SetWindowsHookExW(WH_CBT, CbtProc, g_self, 0);
    if (g_hook == NULL)
    {
        if (g_showHook != NULL) { UnhookWinEvent(g_showHook); g_showHook = NULL; }
        return FALSE;
    }
    return TRUE;
}

/* 卸载：主程序退出前调用；共享区 Flags bit0 让所有副本立即进入放行模式。 */
__declspec(dllexport) BOOL WINAPI BH_Uninstall(void)
{
    if (g_shared != NULL) g_shared->Flags |= BH_FLAG_SHUTDOWN;
    BOOL ok = TRUE;
    if (g_showHook != NULL) { ok = UnhookWinEvent(g_showHook) && ok; g_showHook = NULL; }
    if (g_hook != NULL) { ok = UnhookWindowsHookEx(g_hook) && ok; g_hook = NULL; }
    return ok;
}

/* 抑制开关：主程序自己程序化最小化/关闭/显示前前置位，防止钩子拦自己的动作。
   hwnd=0 清除。 */
__declspec(dllexport) void WINAPI BH_Suppress(HWND hwnd)
{
    if (g_shared != NULL) InterlockedExchange64((volatile INT64*)&g_shared->SuppressHwnd,
                                                (INT64)(UINT_PTR)hwnd);
}

/* 探针：供 NativeSmoke / CI 自检（不依赖宿主，返回协议版本）。 */
__declspec(dllexport) UINT32 WINAPI BH_Version(void)
{
    return BH_VERSION;
}

BOOL WINAPI DllMain(HINSTANCE self, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_self = self;
        DisableThreadLibraryCalls(self);
        g_csOk = InitializeCriticalSectionAndSpinCount(&g_cs, 0x400) != 0;
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        if (g_csOk) DeleteCriticalSection(&g_cs);
    }
    return TRUE;
}
