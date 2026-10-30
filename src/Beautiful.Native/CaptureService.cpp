#include "CaptureService.h"
#include "NativeCommon.h"
#include "WgcCapture.h"

#include <algorithm>
#include <cstring>
#include <vector>

namespace
{
    struct Capture
    {
        int lastMode{};
    };

    // 5×4 取样判纯色（与 C# 侧既有判据一致：全同 = 不可用帧）
    bool IsUniform(const uint8_t* bgra, int width, int height, int stride)
    {
        if (!bgra || width < 5 || height < 4) return false;
        uint32_t first = 0;
        bool firstSet = false;
        for (int gy = 0; gy < 4; ++gy)
        {
            const int y = std::min(height - 1, (height - 1) * gy / 3);
            for (int gx = 0; gx < 5; ++gx)
            {
                const int x = std::min(width - 1, (width - 1) * gx / 4);
                const uint8_t* p = bgra + static_cast<size_t>(y) * stride + static_cast<size_t>(x) * 4;
                const uint32_t value = p[0] | (p[1] << 8) | (p[2] << 16) | (p[3] << 24);
                if (!firstSet)
                {
                    first = value;
                    firstSet = true;
                }
                else if (value != first)
                {
                    return false;
                }
            }
        }
        return true;
    }

    // DIB 是 bottom-up：转成 top-down BGRA 给 C#（WPF WriteableBitmap 约定）。
    void CopyBottomUp(const uint8_t* src, int width, int height, int srcStride, uint8_t* dst)
    {
        const int dstStride = width * 4;
        for (int y = 0; y < height; ++y)
        {
            const uint8_t* row = src + static_cast<size_t>(height - 1 - y) * srcStride;
            std::memcpy(dst + static_cast<size_t>(y) * dstStride, row, static_cast<size_t>(dstStride));
        }
    }

    int Finish(const uint8_t* dibBits, int width, int height, int srcStride, int mode,
               uint8_t* pixels, int capBytes, BnCaptureResult* result)
    {
        if (!result) return BN_E_INVALID_ARG;
        const int required = width * height * 4;
        *result = { width, height, width * 4, 0, required, mode };
        if (!pixels || capBytes < required)
            return BN_E_INVALID_ARG;

        CopyBottomUp(dibBits, width, height, srcStride, pixels);
        if (IsUniform(pixels, width, height, width * 4))
        {
            result->rejected = 1;
            std::memset(pixels, 0, static_cast<size_t>(required));
            bn::Log(L"WARN", L"Capture mode=%d rect=%dx%d 判废：5x4 取样全同（纯色帧）", mode, width, height);
        }
        return BN_OK;
    }

    int CaptureWithGdi(HWND hwnd, const BnRect& rect, uint8_t* pixels, int capBytes, BnCaptureResult* result)
    {
        const int width = rect.right - rect.left;
        const int height = rect.bottom - rect.top;
        if (width <= 0 || height <= 0) return BN_E_INVALID_ARG;

        HDC screenDc = GetDC(nullptr);
        HDC memDc = CreateCompatibleDC(screenDc);
        BITMAPINFO bmi{};
        bmi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        bmi.bmiHeader.biWidth = width;
        bmi.bmiHeader.biHeight = height; // bottom-up
        bmi.bmiHeader.biPlanes = 1;
        bmi.bmiHeader.biBitCount = 32;
        bmi.bmiHeader.biCompression = BI_RGB;
        void* bits = nullptr;
        HBITMAP bitmap = CreateDIBSection(screenDc, &bmi, DIB_RGB_COLORS, &bits, nullptr, 0);
        if (!bitmap || !bits)
        {
            DeleteDC(memDc);
            ReleaseDC(nullptr, screenDc);
            return BN_E_CAPTURE;
        }
        HGDIOBJ old = SelectObject(memDc, bitmap);

        int mode = 0;
        bool ok = false;
        if (hwnd)
        {
            // PrintWindow(PW_RENDERFULLCONTENT)：对 DComp 窗口可能整幅纯色，交由判废逻辑处理
            ok = PrintWindow(hwnd, memDc, 2 /* PW_RENDERFULLCONTENT */) != FALSE;
            if (ok) mode = 3;
        }
        if (!ok)
        {
            ok = BitBlt(memDc, 0, 0, width, height, screenDc, rect.left, rect.top, SRCCOPY | CAPTUREBLT) != FALSE;
            if (ok) mode = 4;
        }

        int status = BN_E_CAPTURE;
        if (ok && bits)
            status = Finish(static_cast<const uint8_t*>(bits), width, height, width * 4, mode, pixels, capBytes, result);

        SelectObject(memDc, old);
        DeleteObject(bitmap);
        DeleteDC(memDc);
        ReleaseDC(nullptr, screenDc);
        return status;
    }
}

namespace bn
{
    void* CaptureCreate()
    {
        return new Capture();
    }

    int CaptureWindow(void* handle, HWND hwnd, uint8_t* pixels, int capBytes, BnCaptureResult* result)
    {
        if (!handle || !hwnd || !result) return BN_E_INVALID_ARG;
        RECT rect{};
        if (!GetWindowRect(hwnd, &rect)) return BN_E_INVALID_ARG;
        BnRect r{ rect.left, rect.top, rect.right, rect.bottom };

        // v4 Phase 2：WGC 优先（GPU/DComp 窗口可捕，解决 PrintWindow 纯色），失败回落 GDI。
        const int wgc = TryCaptureWgc(hwnd, pixels, capBytes, result);
        if (wgc == BN_OK) return BN_OK;
        if (wgc == BN_E_INVALID_ARG) return wgc;   // 缓冲区不足：requiredBytes 已写，别再覆盖
        return CaptureWithGdi(hwnd, r, pixels, capBytes, result);
    }

    int CaptureScreen(void* handle, const BnRect& rect, uint8_t* pixels, int capBytes, BnCaptureResult* result)
    {
        if (!handle || !result) return BN_E_INVALID_ARG;
        return CaptureWithGdi(nullptr, rect, pixels, capBytes, result);
    }

    void CaptureDestroy(void* handle)
    {
        delete static_cast<Capture*>(handle);
    }
}
