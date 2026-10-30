#pragma once

#include <windows.h>
#include "beautiful_native.h"

namespace bn
{
    int SceneProbe();
    void* SceneCreate(HWND owner, int width, int height);
    int SceneRenderTest(void* handle, uint8_t* pixels, int capBytes, BnCaptureResult* result);
    void SceneDestroy(void* handle);
}
