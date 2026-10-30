#pragma once

#include <windows.h>
#include "beautiful_native.h"

namespace bn
{
    int InjectProbe();
    int InjectExplorer(const wchar_t* dllPath);
    int InjectUnload();
}
