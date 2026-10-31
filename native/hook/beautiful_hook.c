// ============================================================
// beautiful_hook.c — Beautiful.Hook.dll（v4.0.0-alpha.16）
// 目的：真替换 Windows 原生最小化动画（MyDockFinder 同款思路）。
//
// 原理：
//   主程序（Beautiful.exe，C#）调用 BH_Install 装一条全局 WH_CBT 钩子。
//   Windows 会把本 DLL 注入所有 GUI 进程；目标窗口最小化的一瞬间，
//   HCBT_MINIMIZE 在【原生缩小动画开始之前】在目标进程内触发，此时窗口还
//   画着最后一帧 —— 我们就地 PrintWindow(PW_RENDERFULLCONTENT) 抓进共享
//   内存，PostMessage 通知主程序，等确认（≤40ms）后返回 1 阻断原生动画。
//   主程序拿帧播 genie 替身，播完自己静默完成最小化（DWM 过渡临时关闭）。
//
// 安全红线：
//   1. 本 DLL 跑在别人进程里 —— 钩子回调全程 __try/__except，任何一步
//      失败都走 CallNextHookEx 放行，绝不让宿主进程崩。
//   2. 确认等待只有 40ms：主程序死了/忙了/拒绝，原生最小化照常进行。
//   3. 自家进程、被抑制（程序化最小化）的窗口一律放行，防递归。
//   4. 本文件不许引用 CRT 堆以外的东西都行，但绝不创建线程、不弹窗、
//      不写文件、不动注册表 —— 注入组件的克制是第一设计约束。
//
// 构建（Linux 交叉编译，无需 MSVC；clang 提供 MS SEH，gcc 只做链接）：
//   clang --target=x86_64-w64-mingw32 --sysroot=<prefix>/usr/x86_64-w64-mingw32
//         -isystem <prefix>/usr/share/mingw-w64/include -fms-extensions -c -O2
//   x86_64-w64-mingw32-gcc -shared -o Beautiful.Hook.dll beautiful_hook.o -static
// ============================================================

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <excpt.h>

/* HCBT_MINMAX 的 show 码（wParam=窗口，lParam 低字=SW_*）：只拦最小化方向 */
static int IsMinimizeShow(LPARAM lParam)
{
    const int show = (int)(short)LOWORD(lParam);
    return show == 2 /*SW_SHOWMINIMIZED*/ || show == 6 /*SW_MINIMIZE*/ ||
           show == 7 /*SW_SHOWMINNOACTIVE*/;
}

// ---- 与 C# 侧（Animation/MinimizeHookClient.cs）逐字对齐的常量 ----
#define BH_SHARED_NAME   L"Local\\Beautiful.Hook.Shared.v1"
#define BH_ACK_NAME      L"Local\\Beautiful.Hook.Ack.v1"
#define BH_MAGIC         0x31484B42u   /* "BHK1" */
#define BH_VERSION       1u
#define BH_MAX_WIDTH     2560
#define BH_MAX_HEIGHT    1440
#define BH_MIN_WIDTH     120
#define BH_MIN_HEIGHT    120
#define BH_ACK_TIMEOUT_MS 40

/* 共享内存头（每份注入副本 OpenFileMapping 得到同一块） */
typedef struct _BH_SHARED {
    UINT32 Magic;               /* BH_MAGIC，主程序安装时写入 */
    UINT32 Version;             /* BH_VERSION */
    UINT64 HostHwnd;            /* 主程序通知窗口 */
    UINT32 NotifyMsg;           /* RegisterWindowMessage 的消息号 */
    volatile UINT32 Flags;      /* bit0 = 钩子应放行一切（主程序退出中） */
    volatile UINT64 SuppressHwnd; /* 主程序正在程序化最小化的窗口（0=无） */
    volatile UINT32 Seq;        /* 最近一次捕获的序号（递增） */
    UINT32 SlotBytes;           /* 单槽像素字节数 */
    UINT32 SlotCount;           /* 恒为 2 */
} BH_SHARED;

/* 槽头：紧跟在 BH_SHARED 之后；像素再随后 */
typedef struct _BH_SLOT {
    volatile UINT32 ReadySeq;   /* 本槽数据对应的 Seq；0 = 空 */
    UINT32 Width;
    UINT32 Height;
    UINT32 Stride;              /* 字节/行 = Width*4（BGRA，自上而下） */
    UINT64 Hwnd;
} BH_SLOT;

static HMODULE  g_self        = NULL;
static HHOOK    g_hook        = NULL;   /* 只在主程序进程里有意义 */
static HWND     g_host        = NULL;   /* 主程序通知窗口（每副本都有） */
static UINT     g_msg         = 0;
static HANDLE   g_section     = NULL;   /* 本副本的节句柄 */
static BH_SHARED* g_shared    = NULL;
static HANDLE   g_ack         = NULL;   /* 本副本的事件句柄 */
static UINT32   g_localSeq    = 0;      /* 本副本的捕获计数 */
static DWORD    g_hostPid     = 0;      /* 主程序进程：自家窗口一律放行 */
static CRITICAL_SECTION g_cs;           /* 同进程并发最小化的槽保护 */
static BOOL     g_csOk        = FALSE;

/* ---- 惰性资源：注入副本第一次用到 HCBT_MINIMIZE 时才打开共享节/事件 ---- */
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

/* ---- 画面是否"值得播"：扫一遍像素，纯色（抓到白板/黑板）判废 ---- */
static BOOL FrameHasContent(const BYTE* pixels, UINT32 stride, UINT32 width, UINT32 height)
{
    const UINT32 step = 24;                       /* 抽样即可 */
    BYTE r0 = pixels[2], g0 = pixels[1], b0 = pixels[0];
    UINT32 x, y;
    for (y = 0; y < height; y += step)
    {
        const BYTE* row = pixels + (SIZE_T)y * stride;
        for (x = 0; x < width; x += step)
        {
            const BYTE* px = row + (SIZE_T)x * 4;
            if (px[0] != b0 || px[1] != g0 || px[2] != r0) return TRUE;
        }
    }
    return FALSE;
}

/* ---- 就地抓帧到第 slot 个槽。成功返回 TRUE 并已写好槽头（调用方已持 g_cs） ---- */
static BOOL CaptureIntoSlot(HWND hwnd, int slot)
{
    BH_SHARED* s = g_shared;
    if (s == NULL || hwnd == NULL) return FALSE;

    RECT r;
    if (!GetWindowRect(hwnd, &r)) return FALSE;
    const int w = r.right - r.left;
    const int h = r.bottom - r.top;
    if (w < BH_MIN_WIDTH || h < BH_MIN_HEIGHT) return FALSE;
    if (w > BH_MAX_WIDTH || h > BH_MAX_HEIGHT) return FALSE;

    const UINT32 stride = (UINT32)w * 4;
    const UINT32 next = g_localSeq + 1;   /* 本次捕获的序号（槽号 = next&1） */
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

    BOOL ok = FALSE;
    if (bits != NULL)
    {
        /* PW_RENDERFULLCONTENT = 2：DirectComposition 内容也参与重绘（Win 8.1+） */
        if (PrintWindow(hwnd, mdc, 2) || PrintWindow(hwnd, mdc, 0))
        {
            if (FrameHasContent((const BYTE*)bits, stride, (UINT32)w, (UINT32)h))
            {
                BYTE* dst = (BYTE*)s + sizeof(BH_SHARED)
                          + (SIZE_T)slot * (sizeof(BH_SLOT) + (SIZE_T)s->SlotBytes);
                BH_SLOT* sh = (BH_SLOT*)dst;
                BYTE* px = dst + sizeof(BH_SLOT);
                memcpy(px, bits, (SIZE_T)stride * (SIZE_T)h);
                /* 先写数据后发号：ReadySeq 非零即本槽可读。
                   槽号 = 发号奇偶（调用方按 seq&1 取槽，两侧一致）。 */
                sh->Width    = (UINT32)w;
                sh->Height   = (UINT32)h;
                sh->Stride   = stride;
                sh->Hwnd     = (UINT64)(UINT_PTR)hwnd;
                sh->ReadySeq = next;
                s->Seq       = next;
                g_localSeq   = next;
                ok = TRUE;
            }
        }
    }

    if (old != NULL) SelectObject(mdc, old);
    if (bmp != NULL) DeleteObject(bmp);
    if (mdc != NULL) DeleteDC(mdc);
    ReleaseDC(hwnd, wdc);
    return ok;
}

/* ---- CBT 钩子本体（在目标进程内执行） ---- */
static LRESULT CALLBACK CbtProc(int code, WPARAM wParam, LPARAM lParam)
{
    if (code == HCBT_MINMAX && IsMinimizeShow(lParam))
    {
        HWND hwnd = (HWND)wParam;
        int blocked = 0;
        __try
        {
            /* 资源惰性初始化：任何失败都放行（原生路径兜底） */
            if ((g_shared == NULL && !EnsureLocalResources()) || (g_shared->Flags & 1u))
            {
                /* 放行 */
            }
            else
            {
                DWORD pid = 0;
                GetWindowThreadProcessId(hwnd, &pid);
                HWND root = GetAncestor(hwnd, GA_ROOT);
                if (root == hwnd && pid != 0 && pid != g_hostPid &&
                    !IsIconic(hwnd) && IsWindowVisible(hwnd) &&
                    (UINT64)(UINT_PTR)hwnd != g_shared->SuppressHwnd &&
                    g_csOk && TryEnterCriticalSection(&g_cs))
                {
                    const int slot = (int)((g_localSeq + 1) & 1);
                    if (CaptureIntoSlot(hwnd, slot))
                    {
                        /* 数据就绪 → 通知主程序 → 等确认 */
                        g_msg  = g_shared->NotifyMsg;   /* 每次同步，防主程序换窗口 */
                        g_host = (HWND)(UINT_PTR)g_shared->HostHwnd;
                        if (g_host != NULL && g_msg != 0 &&
                            PostMessageW(g_host, g_msg, (WPARAM)g_shared->Seq, (LPARAM)(UINT_PTR)hwnd) &&
                            g_ack != NULL &&
                            WaitForSingleObject(g_ack, BH_ACK_TIMEOUT_MS) == WAIT_OBJECT_0)
                        {
                            blocked = 1;   /* 主程序接管：阻断原生最小化 */
                        }
                    }
                    LeaveCriticalSection(&g_cs);
                }
            }
        }
        __except (EXCEPTION_EXECUTE_HANDLER)
        {
            /* 注入组件的第一铁律：绝不把异常抛给宿主进程 */
        }
        if (blocked) return 1;   /* 阻断：原生动画不会发生 */
    }
    return CallNextHookEx(NULL, code, wParam, lParam);
}

/* ---- 主程序 API ---- */

/* 安装：host=通知窗口，msg=RegisterWindowMessage("Beautiful.Hook.Notify.v1") 的值。
   返回 TRUE = 钩子已装上（注入由系统在目标进程下次 relevant 调用时完成）。 */
__declspec(dllexport) BOOL WINAPI BH_Install(HWND host, UINT msg)
{
    if (g_hook != NULL) return TRUE;   /* 幂等 */
    if (host == NULL || msg == 0) return FALSE;

    g_self = GetModuleHandleW(L"Beautiful.Hook.dll");
    if (g_self == NULL) return FALSE;

    /* 共享节：头（一页）+ 双槽 */
    const UINT32 slotBytes = (UINT32)BH_MAX_WIDTH * BH_MAX_HEIGHT * 4;
    const SIZE_T sectionSize = sizeof(BH_SHARED) + 2 * ((SIZE_T)sizeof(BH_SLOT) + slotBytes);
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
    g_hook = SetWindowsHookExW(WH_CBT, CbtProc, g_self, 0);
    return g_hook != NULL;
}

/* 卸载：主程序退出前调用；共享区 Flags bit0 让所有副本立即进入放行模式。 */
__declspec(dllexport) BOOL WINAPI BH_Uninstall(void)
{
    if (g_shared != NULL) g_shared->Flags |= 1u;
    BOOL ok = TRUE;
    if (g_hook != NULL) { ok = UnhookWindowsHookEx(g_hook); g_hook = NULL; }
    return ok;
}

/* 抑制开关：主程序自己 ShowWindow(SW_MINIMIZE) 前置位，防止钩子拦自己的动作。
   hwnd=0 清除。 */
__declspec(dllexport) void WINAPI BH_Suppress(HWND hwnd)
{
    if (g_shared != NULL) InterlockedExchange64((volatile INT64*)&g_shared->SuppressHwnd,
                                                (INT64)(UINT_PTR)hwnd);
}

/* 探针：供 NativeSmoke / CI 自检（不依赖宿主，返回编译版本）。 */
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
