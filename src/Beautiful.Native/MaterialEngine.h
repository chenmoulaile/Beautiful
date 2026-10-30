#pragma once

#include <windows.h>
#include <cstdint>
#include "beautiful_native.h"

namespace bn
{
    int MaterialProbe();
    void* MaterialCreate(HWND owner, const BnMaterialDesc& desc);
    int MaterialSetRect(void* handle, const BnRect& rect);
    int MaterialSetTheme(void* handle, uint32_t tint, float opacity, int dark);
    int MaterialShow(void* handle, bool show);
    void MaterialDestroy(void* handle);
}
