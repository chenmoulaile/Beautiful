#include "beautiful_native.h"
#include "CaptureService.h"
#include "Injector.h"
#include "MaterialEngine.h"
#include "NativeCommon.h"
#include "SceneRenderer.h"

#include <winrt/base.h>
#include <mutex>

namespace
{
    std::once_flag g_comOnce;

    void EnsureCom()
    {
        std::call_once(g_comOnce, [] {
            try
            {
                // WPF 主线程已是 STA；返回 RPC_E_CHANGED_MODE 表示已初始化，继续用现有套间。
                winrt::init_apartment(winrt::apartment_type::single_threaded);
            }
            catch (const winrt::hresult_error&) { }
            catch (...) { }
        });
    }

    BnHealth Health()
    {
        BnHealth health{};
        health.abiVersion = BN_ABI_VERSION;
        wcsncpy_s(health.version, BEAUTIFUL_NATIVE_VERSION, _TRUNCATE);
        if (bn::MaterialProbe() == BN_OK) health.modules |= BN_MODULE_MATERIAL;
        health.modules |= BN_MODULE_CAPTURE; // GDI 捕获始终可用（WGC/DXGI 是增强路）
        if (bn::SceneProbe() == BN_OK) health.modules |= BN_MODULE_SCENE;
        if (bn::InjectProbe() == BN_OK) health.modules |= BN_MODULE_INJECT;
        wcsncpy_s(health.lastError, bn::LastError(), _TRUNCATE);
        return health;
    }
}

extern "C"
{
    uint32_t __cdecl Bn_AbiVersion(void)
    {
        return BN_ABI_VERSION;
    }

    int32_t __cdecl Bn_GetHealth(BnHealth* out)
    {
        if (!out) return BN_E_INVALID_ARG;
        try
        {
            EnsureCom();
            *out = Health();
            return BN_OK;
        }
        catch (...) { return BN_E_UNKNOWN; }
    }

    const wchar_t* __cdecl Bn_LastError(void)
    {
        return bn::LastError();
    }

    int32_t __cdecl Bn_Material_Probe(void)
    {
        EnsureCom();
        try { return bn::MaterialProbe(); }
        catch (...) { return BN_E_UNKNOWN; }
    }

    void* __cdecl Bn_Material_Create(void* owner, const BnMaterialDesc* desc)
    {
        EnsureCom();
        if (!desc) return nullptr;
        try { return bn::MaterialCreate(static_cast<HWND>(owner), *desc); }
        catch (...) { return nullptr; }
    }

    int32_t __cdecl Bn_Material_SetRect(void* handle, const BnRect* rect)
    {
        if (!rect) return BN_E_INVALID_ARG;
        try { return bn::MaterialSetRect(handle, *rect); }
        catch (...) { return BN_E_UNKNOWN; }
    }

    int32_t __cdecl Bn_Material_SetTheme(void* handle, uint32_t tint, float opacity, int32_t dark)
    {
        try { return bn::MaterialSetTheme(handle, tint, opacity, dark); }
        catch (...) { return BN_E_UNKNOWN; }
    }

    int32_t __cdecl Bn_Material_Show(void* handle, int32_t show)
    {
        try { return bn::MaterialShow(handle, show != 0); }
        catch (...) { return BN_E_UNKNOWN; }
    }

    void __cdecl Bn_Material_Destroy(void* handle)
    {
        try { bn::MaterialDestroy(handle); }
        catch (...) { }
    }

    void* __cdecl Bn_Capture_Create(void)
    {
        try { return bn::CaptureCreate(); }
        catch (...) { return nullptr; }
    }

    int32_t __cdecl Bn_Capture_Window(void* handle, void* hwnd, uint8_t* pixels, int32_t capBytes, BnCaptureResult* result)
    {
        try { return bn::CaptureWindow(handle, static_cast<HWND>(hwnd), pixels, capBytes, result); }
        catch (...) { return BN_E_UNKNOWN; }
    }

    int32_t __cdecl Bn_Capture_Screen(void* handle, const BnRect* rect, uint8_t* pixels, int32_t capBytes, BnCaptureResult* result)
    {
        if (!rect) return BN_E_INVALID_ARG;
        try { return bn::CaptureScreen(handle, *rect, pixels, capBytes, result); }
        catch (...) { return BN_E_UNKNOWN; }
    }

    void __cdecl Bn_Capture_Destroy(void* handle)
    {
        try { bn::CaptureDestroy(handle); }
        catch (...) { }
    }

    int32_t __cdecl Bn_Scene_Probe(void)
    {
        try { return bn::SceneProbe(); }
        catch (...) { return BN_E_UNKNOWN; }
    }

    void* __cdecl Bn_Scene_Create(void* owner, int32_t width, int32_t height)
    {
        try { return bn::SceneCreate(static_cast<HWND>(owner), width, height); }
        catch (...) { return nullptr; }
    }

    int32_t __cdecl Bn_Scene_RenderTest(void* handle, uint8_t* pixels, int32_t capBytes, BnCaptureResult* result)
    {
        try { return bn::SceneRenderTest(handle, pixels, capBytes, result); }
        catch (...) { return BN_E_UNKNOWN; }
    }

    void __cdecl Bn_Scene_Destroy(void* handle)
    {
        try { bn::SceneDestroy(handle); }
        catch (...) { }
    }

    int32_t __cdecl Bn_Inject_Probe(void)
    {
        try { return bn::InjectProbe(); }
        catch (...) { return BN_E_UNKNOWN; }
    }

    int32_t __cdecl Bn_Inject_Explorer(const wchar_t* dllPath)
    {
        try { return bn::InjectExplorer(dllPath); }
        catch (...) { return BN_E_UNKNOWN; }
    }

    int32_t __cdecl Bn_Inject_Unload(void)
    {
        try { return bn::InjectUnload(); }
        catch (...) { return BN_E_UNKNOWN; }
    }
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(module);
    return TRUE;
}
