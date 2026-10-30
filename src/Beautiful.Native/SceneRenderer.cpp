#include "SceneRenderer.h"
#include "NativeCommon.h"

#include <d3d11.h>
#include <vector>
#include <wrl/client.h>

using Microsoft::WRL::ComPtr;

namespace
{
    struct Scene
    {
        ComPtr<ID3D11Device> device;
        ComPtr<ID3D11DeviceContext> context;
        int width{};
        int height{};
    };

    bool CreateDevice(ComPtr<ID3D11Device>& device, ComPtr<ID3D11DeviceContext>& context)
    {
        D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
        D3D_FEATURE_LEVEL got{};
        HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, 0, levels,
                                       _countof(levels), D3D11_SDK_VERSION, &device, &got, &context);
        if (FAILED(hr))
            hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, levels,
                                   _countof(levels), D3D11_SDK_VERSION, &device, &got, &context);
        return SUCCEEDED(hr) && device && context;
    }
}

namespace bn
{
    int SceneProbe()
    {
        ComPtr<ID3D11Device> device;
        ComPtr<ID3D11DeviceContext> context;
        if (!CreateDevice(device, context)) return BN_E_D3D;
        return BN_OK;
    }

    void* SceneCreate(HWND owner, int width, int height)
    {
        (void)owner; // Phase 2 骨架只做离屏；Phase 3-T7 再接幽灵窗口/合成目标
        if (width <= 0 || height <= 0 || width > 16384 || height > 16384) return nullptr;
        auto* scene = new Scene();
        scene->width = width;
        scene->height = height;
        if (!CreateDevice(scene->device, scene->context))
        {
            delete scene;
            return nullptr;
        }
        Log(L"INFO", L"Scene created (offscreen) %dx%d", width, height);
        return scene;
    }

    int SceneRenderTest(void* handle, uint8_t* pixels, int capBytes, BnCaptureResult* result)
    {
        auto* scene = static_cast<Scene*>(handle);
        if (!scene || !result) return BN_E_INVALID_ARG;
        const int w = scene->width;
        const int h = scene->height;
        const int required = w * h * 4;
        *result = { w, h, w * 4, 0, required, 1 };
        if (!pixels || capBytes < required) return BN_E_INVALID_ARG;

        // 基准图：渐变 + 32px 网格线（CPU 生成 → D3D11 texture → staging 回读）。
        // 这条链证明设备/纹理/回读可用；shader 网格形变在 Phase 3-T7 接入。
        std::vector<uint8_t> source(static_cast<size_t>(required));
        for (int y = 0; y < h; ++y)
        {
            for (int x = 0; x < w; ++x)
            {
                const bool grid = (x % 32 == 0) || (y % 32 == 0);
                uint8_t b = static_cast<uint8_t>(20 + 120 * x / (w > 1 ? w - 1 : 1));
                uint8_t g = static_cast<uint8_t>(20 + 120 * y / (h > 1 ? h - 1 : 1));
                uint8_t r = 40;
                if (grid) { b = 235; g = 235; r = 235; }
                const size_t offset = static_cast<size_t>(y) * w * 4 + static_cast<size_t>(x) * 4;
                source[offset + 0] = b;
                source[offset + 1] = g;
                source[offset + 2] = r;
                source[offset + 3] = 255;
            }
        }

        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = w;
        desc.Height = h;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;

        D3D11_SUBRESOURCE_DATA data{};
        data.pSysMem = source.data();
        data.SysMemPitch = w * 4;
        ComPtr<ID3D11Texture2D> texture;
        if (FAILED(scene->device->CreateTexture2D(&desc, &data, &texture)) || !texture)
            return BN_E_D3D;

        D3D11_TEXTURE2D_DESC stagingDesc = desc;
        stagingDesc.Usage = D3D11_USAGE_STAGING;
        stagingDesc.BindFlags = 0;
        stagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        ComPtr<ID3D11Texture2D> staging;
        if (FAILED(scene->device->CreateTexture2D(&stagingDesc, nullptr, &staging)) || !staging)
            return BN_E_D3D;

        scene->context->CopyResource(staging.Get(), texture.Get());
        D3D11_MAPPED_SUBRESOURCE mapped{};
        if (FAILED(scene->context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped)))
            return BN_E_D3D;

        // D3D11 B8G8R8A8 行是 top-down；C# 也是 top-down，直接逐行拷贝。
        for (int y = 0; y < h; ++y)
        {
            const auto* row = static_cast<const uint8_t*>(mapped.pData) + static_cast<size_t>(y) * mapped.RowPitch;
            std::memcpy(pixels + static_cast<size_t>(y) * w * 4, row, static_cast<size_t>(w * 4));
        }
        scene->context->Unmap(staging.Get(), 0);
        Log(L"INFO", L"SceneRenderTest ok %dx%d", w, h);
        return BN_OK;
    }

    void SceneDestroy(void* handle)
    {
        delete static_cast<Scene*>(handle);
    }
}
