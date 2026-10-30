#include "NativeCommon.h"

#include <cstdarg>
#include <cstdio>
#include <mutex>
#include <vector>

namespace
{
    std::mutex g_logMutex;
    thread_local wchar_t g_lastError[512] = L"";

    std::wstring Env(const wchar_t* name)
    {
        std::vector<wchar_t> buffer(32768);
        const DWORD n = GetEnvironmentVariableW(name, buffer.data(), static_cast<DWORD>(buffer.size()));
        if (n == 0 || n >= buffer.size()) return {};
        return std::wstring(buffer.data(), n);
    }

    std::wstring DirectoryOf(const std::wstring& path)
    {
        const auto pos = path.find_last_of(L"\\/");
        return pos == std::wstring::npos ? std::wstring() : path.substr(0, pos);
    }
}

namespace bn
{
    std::wstring ExeDirectory()
    {
        std::vector<wchar_t> buffer(32768);
        const DWORD n = GetModuleFileNameW(nullptr, buffer.data(), static_cast<DWORD>(buffer.size()));
        if (n == 0) return {};
        return DirectoryOf(std::wstring(buffer.data(), n));
    }

    std::wstring TempDirectory()
    {
        std::vector<wchar_t> buffer(32768);
        const DWORD n = GetTempPathW(static_cast<DWORD>(buffer.size()), buffer.data());
        return n == 0 ? ExeDirectory() : std::wstring(buffer.data(), n);
    }

    std::wstring LogPath()
    {
        // 主程序可设 BEAUTIFUL_NATIVE_LOG_DIR 指向 <exe>\logs；注入 payload 不设，落 %TEMP%（由主程序收编）
        auto dir = Env(L"BEAUTIFUL_NATIVE_LOG_DIR");
        if (!dir.empty())
        {
            const DWORD attr = GetFileAttributesW(dir.c_str());
            if (attr != INVALID_FILE_ATTRIBUTES && (attr & FILE_ATTRIBUTE_DIRECTORY)) return dir + L"\\native.log";
        }
        return TempDirectory() + L"beautiful-native.log";
    }

    void Log(const wchar_t* level, const wchar_t* format, ...)
    {
        wchar_t message[2048]{};
        va_list args;
        va_start(args, format);
        _vsnwprintf_s(message, _TRUNCATE, format, args);
        va_end(args);

        SYSTEMTIME st{};
        GetLocalTime(&st);
        wchar_t line[2600]{};
        swprintf_s(line, L"[%04u-%02u-%02u %02u:%02u:%02u.%03u] [%s] %s\r\n",
                   st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
                   level ? level : L"INFO", message);

        std::lock_guard<std::mutex> lock(g_logMutex);
        const auto path = LogPath();
        HANDLE file = CreateFileW(path.c_str(), FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE,
                                  nullptr, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) return;
        DWORD written = 0;
        WriteFile(file, line, static_cast<DWORD>(wcslen(line) * sizeof(wchar_t)), &written, nullptr);
        CloseHandle(file);
    }

    void SetLastError(const wchar_t* text)
    {
        wcsncpy_s(g_lastError, text ? text : L"", _TRUNCATE);
    }

    const wchar_t* LastError()
    {
        return g_lastError;
    }

    uint32_t OsBuild()
    {
        using RtlGetVersionFn = LONG(WINAPI*)(PRTL_OSVERSIONINFOW);
        const HMODULE ntdll = GetModuleHandleW(L"ntdll.dll");
        if (!ntdll) return 0;
        const auto fn = reinterpret_cast<RtlGetVersionFn>(GetProcAddress(ntdll, "RtlGetVersion"));
        if (!fn) return 0;
        RTL_OSVERSIONINFOW info{};
        info.dwOSVersionInfoSize = sizeof(info);
        return fn(&info) == 0 ? info.dwBuildNumber : 0;
    }

    bool FileExists(const std::wstring& path)
    {
        const DWORD attr = GetFileAttributesW(path.c_str());
        return attr != INVALID_FILE_ATTRIBUTES && !(attr & FILE_ATTRIBUTE_DIRECTORY);
    }

    bool IsProcessElevated()
    {
        HANDLE token = nullptr;
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return false;
        TOKEN_ELEVATION elevation{};
        DWORD size = sizeof(elevation);
        const bool ok = GetTokenInformation(token, TokenElevation, &elevation, size, &size) != FALSE;
        CloseHandle(token);
        return ok && elevation.TokenIsElevated != 0;
    }
}
