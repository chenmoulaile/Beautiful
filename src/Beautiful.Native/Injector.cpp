#include "Injector.h"
#include "NativeCommon.h"

#include <string>
#include <vector>

namespace
{
    constexpr wchar_t kHeartbeat[] = L"Local\\Beautiful.TrayBridge.Heartbeat.v1";
    constexpr wchar_t kUnload[] = L"Local\\Beautiful.TrayBridge.Unload.v1";
    constexpr wchar_t kHost[] = L"Local\\Beautiful.TrayBridge.Host.v1";
    constexpr DWORD kMinBuild = 22000;   // Windows 11 21H2 起；逐版本探针确认后再扩
    constexpr DWORD kMaxBuild = 26999;
    HANDLE g_hostMutex = nullptr;        // 持有到进程退出：explorer 侧据此判断"宿主还活着"

    bool IsExplorer(HANDLE process)
    {
        wchar_t path[32768]{};
        DWORD size = static_cast<DWORD>(std::size(path));
        if (!QueryFullProcessImageNameW(process, 0, path, &size)) return false;
        std::wstring value(path, size);
        const auto pos = value.find_last_of(L"\\/");
        const std::wstring name = pos == std::wstring::npos ? value : value.substr(pos + 1);
        return _wcsicmp(name.c_str(), L"explorer.exe") == 0;
    }

    HANDLE OpenExplorer()
    {
        const HWND shell = GetShellWindow();
        if (!shell) return nullptr;
        DWORD pid = 0;
        GetWindowThreadProcessId(shell, &pid);
        if (!pid) return nullptr;
        HANDLE process = OpenProcess(
            PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION |
            PROCESS_VM_WRITE | PROCESS_VM_READ, FALSE, pid);
        if (!process) return nullptr;
        if (!IsExplorer(process))
        {
            CloseHandle(process);
            return nullptr;
        }
        return process;
    }
}

namespace bn
{
    int InjectProbe()
    {
        const auto build = OsBuild();
        if (build < kMinBuild || build > kMaxBuild)
        {
            SetLastError(L"当前 Windows build 不在 TrayBridge 白名单（22000..26999）");
            Log(L"WARN", L"InjectProbe: build=%u 不在白名单", build);
            return BN_E_OS_UNSUPPORTED;
        }
        return BN_OK;
    }

    int InjectExplorer(const wchar_t* dllPath)
    {
        if (!dllPath || !FileExists(dllPath))
        {
            SetLastError(L"TrayBridge DLL 不存在");
            return BN_E_INVALID_ARG;
        }
        const int probe = InjectProbe();
        if (probe != BN_OK) return probe;

        // 宿主存活信号：payload 用 WaitForSingleObject(host,0) 判 WAIT_ABANDONED（主程序崩溃/退出）。
        // 必须在 CreateRemoteThread 之前创建，否则 payload 可能先启动、看不到 mutex 而立即退出。
        if (!g_hostMutex)
        {
            g_hostMutex = CreateMutexW(nullptr, TRUE, kHost);
            if (!g_hostMutex)
            {
                SetLastError(L"创建 TrayBridge 宿主 Mutex 失败");
                return BN_E_INJECT;
            }
        }

        // 已有心跳 = 已经注入：不重复 LoadLibrary（避免引用计数叠高）
        HANDLE existing = OpenEventW(SYNCHRONIZE, FALSE, kHeartbeat);
        if (existing)
        {
            CloseHandle(existing);
            Log(L"INFO", L"InjectExplorer: 心跳已存在，视为已注入");
            return BN_OK;
        }

        HANDLE process = OpenExplorer();
        if (!process)
        {
            SetLastError(L"打不开 explorer.exe（可能不是交互桌面/权限不足）");
            return BN_E_INJECT;
        }

        const size_t bytes = (wcslen(dllPath) + 1) * sizeof(wchar_t);
        void* remote = VirtualAllocEx(process, nullptr, bytes, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (!remote)
        {
            CloseHandle(process);
            SetLastError(L"VirtualAllocEx 失败");
            return BN_E_INJECT;
        }
        if (!WriteProcessMemory(process, remote, dllPath, bytes, nullptr))
        {
            VirtualFreeEx(process, remote, 0, MEM_RELEASE);
            CloseHandle(process);
            SetLastError(L"WriteProcessMemory 失败");
            return BN_E_INJECT;
        }

        const HMODULE kernel = GetModuleHandleW(L"kernel32.dll");
        const auto loadLibrary = reinterpret_cast<LPTHREAD_START_ROUTINE>(
            GetProcAddress(kernel, "LoadLibraryW"));
        if (!loadLibrary)
        {
            VirtualFreeEx(process, remote, 0, MEM_RELEASE);
            CloseHandle(process);
            return BN_E_INJECT;
        }

        HANDLE thread = CreateRemoteThread(process, nullptr, 0, loadLibrary, remote, 0, nullptr);
        if (!thread)
        {
            VirtualFreeEx(process, remote, 0, MEM_RELEASE);
            CloseHandle(process);
            SetLastError(L"CreateRemoteThread 失败（杀软/权限？）");
            return BN_E_INJECT;
        }

        WaitForSingleObject(thread, 5000);
        DWORD moduleBase = 0;
        GetExitCodeThread(thread, &moduleBase);
        CloseHandle(thread);
        VirtualFreeEx(process, remote, 0, MEM_RELEASE);
        CloseHandle(process);

        if (!moduleBase)
        {
            SetLastError(L"LoadLibraryW 返回 0（payload 加载失败）");
            Log(L"WARN", L"InjectExplorer: LoadLibraryW=0");
            return BN_E_INJECT;
        }

        // 等心跳：payload 在 explorer 里创建心跳事件即视为装载成功
        HANDLE heartbeat = OpenEventW(SYNCHRONIZE, FALSE, kHeartbeat);
        if (heartbeat)
        {
            WaitForSingleObject(heartbeat, 2000);
            CloseHandle(heartbeat);
            Log(L"INFO", L"InjectExplorer: 注入成功 module=0x%p", reinterpret_cast<void*>(static_cast<uintptr_t>(moduleBase)));
            return BN_OK;
        }

        SetLastError(L"payload 已加载但 2s 内没有心跳（版本闸门/线程失败）");
        Log(L"WARN", L"InjectExplorer: 无心跳");
        return BN_E_INJECT;
    }

    int InjectUnload()
    {
        HANDLE unload = OpenEventW(EVENT_MODIFY_STATE, FALSE, kUnload);
        if (!unload)
        {
            // 没有 payload 在跑：视为已卸载
            return BN_OK;
        }
        SetEvent(unload);
        CloseHandle(unload);
        Sleep(300); // 给 FreeLibraryAndExitThread 一点时间；主程序另有 2s 超时闸门
        Log(L"INFO", L"InjectUnload: 已发送卸载命令");
        return BN_OK;
    }
}
