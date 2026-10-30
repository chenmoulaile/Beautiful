#include "WgcCapture.h"
#include "NativeCommon.h"

#include <algorithm>
#include <cstring>
#include <d3d11.h>
#include <dxgi.h>
#include <atomic>
#include <memory>
#include <mutex>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;
using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;

namespace
{
    struct WgcContext
    {
        ComPtr<ID3D11Device> d3d;
        ComPtr<ID3D11DeviceContext> context;
        Direct3D11CaptureFramePool pool{ nullptr };
        GraphicsCaptureSession session{ nullptr };
        winrt::event_token frameToken{};
        HANDLE frameEvent{};
        ComPtr<ID3D11Texture2D> captured;
        bool arrived{ false };
    };

    bool CreateDevice(ComPtr<ID3D11Device>& device, ComPtr<ID3D11DeviceContext>& context)
    {
        D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
        D3D_FEATURE_LEVEL got{};
        HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                                       levels, _countof(levels), D3D11_SDK_VERSION, &device, &got, &context);
        if (FAILED(hr))
            hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                                   levels, _countof(levels), D3D11_SDK_VERSION, &device, &got, &context);
        return SUCCEEDED(hr) && device && context;
    }

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
                if (!firstSet) { first = value; firstSet = true; }
                else if (value != first) return false;
            }
        }
        return true;
    }
}

namespace bn
{
    int TryCaptureWgc(HWND hwnd, uint8_t* pixels, int capBytes, BnCaptureResult* result)
    {
        if (!hwnd || !result) return BN_E_INVALID_ARG;
        try
        {
            auto interop = winrt::get_activation_factory<GraphicsCaptureItem, ::IGraphicsCaptureItemInterop>();
            GraphicsCaptureItem item{ nullptr };
            winrt::check_hresult(interop->CreateForWindow(
                hwnd, winrt::guid_of<GraphicsCaptureItem>(), winrt::put_abi(item)));
            if (!item) return BN_E_NOT_AVAILABLE;

            auto ctx = std::make_shared<WgcContext>();
            if (!CreateDevice(ctx->d3d, ctx->context)) return BN_E_D3D;

            ComPtr<IDXGIDevice> dxgiDevice;
            ctx->d3d.As(&dxgiDevice);
            winrt::com_ptr<::IInspectable> inspectable;
            winrt::check_hresult(::CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.Get(), inspectable.put()));
            auto device = inspectable.as<IDirect3DDevice>();

            const auto size = item.Size();
            if (size.Width <= 0 || size.Height <= 0) return BN_E_CAPTURE;
            ctx->pool = Direct3D11CaptureFramePool::CreateFreeThreaded(
                device, DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size);
            ctx->session = ctx->pool.CreateCaptureSession(item);
            ctx->frameEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            if (!ctx->frameEvent) return BN_E_CAPTURE;

            ctx->frameToken = ctx->pool.FrameArrived(
                [ctx](Direct3D11CaptureFramePool const& pool, winrt::Windows::Foundation::IInspectable const&)
                {
                    try
                    {
                        // WGC 的 FrameArrived 第二参在 SDK 投影里是 IInspectable；帧要从 pool 取。
                        auto frame = pool.TryGetNextFrame();
                        if (!frame) { SetEvent(ctx->frameEvent); return; }
                        auto surface = frame.Surface();
                        auto unknown = winrt::get_unknown(surface);
                        winrt::com_ptr<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess> access;
                        winrt::check_hresult(unknown->QueryInterface(
                            __uuidof(::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess),
                            access.put_void()));
                        ComPtr<ID3D11Texture2D> texture;
                        winrt::check_hresult(access->GetInterface(IID_PPV_ARGS(&texture)));
                        if (texture)
                        {
                            ctx->captured = texture;
                            ctx->arrived = true;
                            SetEvent(ctx->frameEvent);
                        }
                        frame.Close();
                    }
                    catch (...) { SetEvent(ctx->frameEvent); }
                });

            ctx->session.StartCapture();
            const DWORD wait = WaitForSingleObject(ctx->frameEvent, 2000);
            ctx->pool.FrameArrived(ctx->frameToken);
            ctx->session.Close();
            ctx->pool.Close();
            CloseHandle(ctx->frameEvent);

            if (wait != WAIT_OBJECT_0 || !ctx->arrived || !ctx->captured) return BN_E_NOT_AVAILABLE;

            D3D11_TEXTURE2D_DESC desc{};
            ctx->captured->GetDesc(&desc);
            const int width = static_cast<int>(desc.Width);
            const int height = static_cast<int>(desc.Height);
            const int required = width * height * 4;
            *result = { width, height, width * 4, 0, required, 1 };
            if (!pixels || capBytes < required) return BN_E_INVALID_ARG;

            D3D11_TEXTURE2D_DESC stagingDesc = desc;
            stagingDesc.Usage = D3D11_USAGE_STAGING;
            stagingDesc.BindFlags = 0;
            stagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            stagingDesc.MiscFlags = 0;
            ComPtr<ID3D11Texture2D> staging;
            if (FAILED(ctx->d3d->CreateTexture2D(&stagingDesc, nullptr, &staging))) return BN_E_D3D;
            ctx->context->CopyResource(staging.Get(), ctx->captured.Get());
            D3D11_MAPPED_SUBRESOURCE mapped{};
            if (FAILED(ctx->context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped))) return BN_E_D3D;
            for (int y = 0; y < height; ++y)
            {
                const auto* row = static_cast<const uint8_t*>(mapped.pData) + static_cast<size_t>(y) * mapped.RowPitch;
                std::memcpy(pixels + static_cast<size_t>(y) * width * 4, row, static_cast<size_t>(width * 4));
            }
            ctx->context->Unmap(staging.Get(), 0);
            if (IsUniform(pixels, width, height, width * 4))
            {
                result->rejected = 1;
                std::memset(pixels, 0, static_cast<size_t>(required));
            }
            Log(L"INFO", L"WGC capture ok %dx%d rejected=%d", width, height, result->rejected);
            return BN_OK;
        }
        catch (const winrt::hresult_error& ex)
        {
            Log(L"WARN", L"WGC capture failed hr=0x%08X %s", static_cast<unsigned>(ex.code()), ex.message().c_str());
            return BN_E_NOT_AVAILABLE;
        }
        catch (...)
        {
            return BN_E_NOT_AVAILABLE;
        }
    }
}
