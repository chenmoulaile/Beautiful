#pragma once

#include <windows.h>
#include "beautiful_native.h"

namespace bn
{
    // WGC 优先捕获窗口；不可用/超时返回负值，调用方回落 PrintWindow/BitBlt。
    // 成功时 result.mode=1，pixels 为 top-down BGRA。
    int TryCaptureWgc(HWND hwnd, uint8_t* pixels, int capBytes, BnCaptureResult* result);
}
