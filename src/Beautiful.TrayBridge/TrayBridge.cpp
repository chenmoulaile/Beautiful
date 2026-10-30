// Beautiful.TrayBridge —— 注入 explorer.exe 的极小只读 payload（v4 Phase 2 骨架）。
// Phase 2 只做：版本闸门、心跳、卸载闭环、自卸载；不做任何 hook、不读任何数据。
// 纪律：不创建窗口、不做 UI、不在 DllMain 里做文件 IO；所有等待都在独立线程里。

#include <windows.h>
#include <cstdio>
#include <string>

namespace
{
    constexpr wchar_t kHeartbeat[] = L"Local\\Beautiful.TrayBridge.Heartbeat.v1";
    constexpr wchar_t kUnload[] = L"Local\\Beautiful.TrayBridge.Unload.v1";
    constexpr wchar_t kHost[] = L"Local\\Beautiful.TrayBridge.Host.v1";
    constexpr DWORD kMinBuild = 22000;
    constexpr DWORD kMaxBuild = 26999;

    HMODULE g_module = nullptr;
    HANDLE g_thread = nullptr;
    HANDLE g_stop = nullptr;
    volatile LONG g_stopFlag = 0;

    std::wstring TempLogPath()
    {
        wchar_t temp[32768]{};
        const DWORD n = GetTempPathW(32768, temp);
        std::wstring path = n ? std::wstring(temp, n) : L".\\";
        path += L"Beautiful.TrayBridge.log";
        return path;
    }

    void Log(const wchar_t* message)
    {
        SYSTEMTIME st{};
        GetLocalTime(&st);
        wchar_t line[1024]{};
        swprintf_s(line, L"[%04u-%02u-%02u %02u:%02u:%02u] %s\r\n",
                   st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, message);
        const auto path = TempLogPath();
        HANDLE file = CreateFileW(path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                  nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return;
        DWORD written = 0;
        WriteFile(file, line, static_cast<DWORD>(wcslen(line) * sizeof(wchar_t)), &written, nullptr);
        CloseHandle(file);
    }

    DWORD OsBuild()
    {
        using RtlGetVersionFn = LONG(WINAPI*)(PRTL_OSVERSIONINFOW);
        const HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
        const auto fn = ntdll ? reinterpret_cast<RtlGetVersionFn>(GetProcAddress(ntdll, "RtlGetVersion")) : nullptr;
        if (!fn) return 0;
        RTL_OSVERSIONINFOW info{};
        info.dwOSVersionInfoSize = sizeof(info);
        return fn(&info) == 0 ? info.dwBuildNumber : 0;
    }

    DWORD WINAPI BridgeThread(LPVOID)
    {
        const DWORD build = OsBuild();
        if (build < kMinBuild || build > kMaxBuild)
        {
            Log(L"版本闸门拒绝：build 不在 22000..26999");
            return 0;
        }

        HANDLE heartbeat = CreateEventW(nullptr, FALSE, FALSE, kHeartbeat);
        HANDLE unload = CreateEventW(nullptr, TRUE, FALSE, kUnload);
        if (!heartbeat || !unload)
        {
            Log(L"创建心跳/卸载事件失败");
            if (heartbeat) CloseHandle(heartbeat);
            if (unload) CloseHandle(unload);
            return 0;
        }

        HANDLE host = OpenMutexW(SYNCHRONIZE, FALSE, kHost);
        if (!host)
        {
            Log(L"宿主 Mutex 不存在：主程序已退出，拒绝装载");
            if (heartbeat) CloseHandle(heartbeat);
            if (unload) CloseHandle(unload);
            return 0;
        }

        Log(L"TrayBridge 已装载（空 payload：仅心跳/卸载）");
        SetEvent(heartbeat);
        while (InterlockedCompareExchange(&g_stopFlag, 0, 0) == 0)
        {
            const DWORD wait = WaitForSingleObject(unload, 500);
            SetEvent(heartbeat);
            if (wait == WAIT_OBJECT_0)
            {
                Log(L"收到卸载命令：FreeLibraryAndExitThread");
                CloseHandle(host);
                CloseHandle(unload);
                CloseHandle(heartbeat);
                FreeLibraryAndExitThread(g_module, 0);
            }
            // 主程序崩溃/退出：宿主 Mutex 进入 abandoned；payload 自卸载兜底（心跳断 500ms 内）
            const DWORD hostState = WaitForSingleObject(host, 0);
            if (hostState == WAIT_ABANDONED || hostState == WAIT_OBJECT_0)
            {
                if (hostState == WAIT_OBJECT_0) ReleaseMutex(host);
                Log(L"宿主已退出（Mutex abandoned）：自卸载兜底");
                CloseHandle(host);
                CloseHandle(unload);
                CloseHandle(heartbeat);
                FreeLibraryAndExitThread(g_module, 0);
            }
        }

        Log(L"进程卸载/停止信号：线程退出");
        CloseHandle(host);
        CloseHandle(unload);
        CloseHandle(heartbeat);
        return 0;
    }
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        DisableThreadLibraryCalls(module);
        g_module = module;
        g_stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        g_thread = CreateThread(nullptr, 0, BridgeThread, nullptr, 0, nullptr);
    }
    else if (reason == DLL_PROCESS_DETACH)
    {
        InterlockedExchange(&g_stopFlag, 1);
        if (g_stop) SetEvent(g_stop);
        if (g_thread)
        {
            WaitForSingleObject(g_thread, 1000);
            CloseHandle(g_thread);
            g_thread = nullptr;
        }
        if (g_stop)
        {
            CloseHandle(g_stop);
            g_stop = nullptr;
        }
    }
    return TRUE;
}
