#pragma once

#include <windows.h>
#include <string>

// 与 Directory.Build.props 同源：vcxproj 的 GenerateNativeVersion 目标在编译前重写此文件。
#include "NativeVersion.h"

namespace bn
{
    void Log(const wchar_t* level, const wchar_t* format, ...);
    void SetLastError(const wchar_t* text);
    const wchar_t* LastError();
    std::wstring ExeDirectory();
    std::wstring TempDirectory();
    std::wstring LogPath();
    uint32_t OsBuild();
    bool FileExists(const std::wstring& path);
    bool IsProcessElevated();
}
