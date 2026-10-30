#pragma once

#include <windows.h>
#include "beautiful_native.h"

namespace bn
{
    void* CaptureCreate();
    int CaptureWindow(void* handle, HWND hwnd, uint8_t* pixels, int capBytes, BnCaptureResult* result);
    int CaptureScreen(void* handle, const BnRect& rect, uint8_t* pixels, int capBytes, BnCaptureResult* result);
    void CaptureDestroy(void* handle);
}
