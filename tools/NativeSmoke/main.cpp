// NativeSmoke —— Beautiful.Native 的冒烟 CLI（无桌面部分进 CI）。
// 只读：不发输入、不改系统、不注入。材质/捕获在无桌面环境会 SKIP。

#include "../../src/Beautiful.Native/beautiful_native.h"

#include <windows.h>
#include <cstdio>
#include <fcntl.h>
#include <io.h>
#include <string>
#include <vector>

namespace
{
    using AbiVersionFn = uint32_t(__cdecl*)();
    using GetHealthFn = int32_t(__cdecl*)(BnHealth*);
    using LastErrorFn = const wchar_t*(__cdecl*)();
    using MaterialProbeFn = int32_t(__cdecl*)();
    using SceneProbeFn = int32_t(__cdecl*)();
    using SceneCreateFn = void*(__cdecl*)(void*, int32_t, int32_t);
    using SceneRenderFn = int32_t(__cdecl*)(void*, uint8_t*, int32_t, BnCaptureResult*);
    using SceneDestroyFn = void(__cdecl*)(void*);
    using CaptureCreateFn = void*(__cdecl*)();
    using CaptureWindowFn = int32_t(__cdecl*)(void*, void*, uint8_t*, int32_t, BnCaptureResult*);
    using CaptureScreenFn = int32_t(__cdecl*)(void*, const BnRect*, uint8_t*, int32_t, BnCaptureResult*);
    using CaptureDestroyFn = void(__cdecl*)(void*);
    using InjectProbeFn = int32_t(__cdecl*)();

    int g_pass = 0;
    int g_skip = 0;
    int g_fail = 0;

    void Pass(const wchar_t* name) { ++g_pass; wprintf(L"PASS  %s\n", name); }
    void Skip(const wchar_t* name, int code) { ++g_skip; wprintf(L"SKIP  %s (code=%d)\n", name, code); }
    void Fail(const wchar_t* name, int code) { ++g_fail; wprintf(L"FAIL  %s (code=%d)\n", name, code); }

    std::wstring DirOf(const std::wstring& path)
    {
        const auto pos = path.find_last_of(L"\\/");
        return pos == std::wstring::npos ? std::wstring(L".") : path.substr(0, pos);
    }
}

int wmain(int argc, wchar_t** argv)
{
    SetConsoleOutputCP(CP_UTF8);
    _setmode(_fileno(stdout), _O_U8TEXT);
    std::wstring dll = argc > 1 ? argv[1] : L"Beautiful.Native.dll";
    wprintf(L"NativeSmoke v1\nDLL: %s\n", dll.c_str());

    HMODULE module = LoadLibraryW(dll.c_str());
    if (!module)
    {
        wprintf(L"FAIL  LoadLibrary err=%lu\n", GetLastError());
        return 2;
    }

    const auto abi = reinterpret_cast<AbiVersionFn>(GetProcAddress(module, "Bn_AbiVersion"));
    const auto healthFn = reinterpret_cast<GetHealthFn>(GetProcAddress(module, "Bn_GetHealth"));
    const auto lastError = reinterpret_cast<LastErrorFn>(GetProcAddress(module, "Bn_LastError"));
    if (!abi || !healthFn)
    {
        wprintf(L"FAIL  缺少导出 Bn_AbiVersion/Bn_GetHealth\n");
        FreeLibrary(module);
        return 2;
    }

    (abi() == BN_ABI_VERSION) ? Pass(L"ABI version") : Fail(L"ABI version", static_cast<int>(abi()));

    BnHealth health{};
    const int healthStatus = healthFn(&health);
    if (healthStatus == BN_OK)
    {
        Pass(L"health");
        wprintf(L"      version=%s modules=0x%X lastError=%s\n", health.version, health.modules, health.lastError);
    }
    else
    {
        Fail(L"health", healthStatus);
    }

    wprintf(L"      struct sizes: BnHealth=%zu BnMaterialDesc=%zu BnCaptureResult=%zu\n",
            sizeof(BnHealth), sizeof(BnMaterialDesc), sizeof(BnCaptureResult));

    const auto materialProbe = reinterpret_cast<MaterialProbeFn>(GetProcAddress(module, "Bn_Material_Probe"));
    if (materialProbe)
    {
        const int code = materialProbe();
        if (code == BN_OK) Pass(L"material host-backdrop probe");
        else Skip(L"material host-backdrop probe (headless/unsupported)", code);
    }

    const auto sceneProbe = reinterpret_cast<SceneProbeFn>(GetProcAddress(module, "Bn_Scene_Probe"));
    const auto sceneCreate = reinterpret_cast<SceneCreateFn>(GetProcAddress(module, "Bn_Scene_Create"));
    const auto sceneRender = reinterpret_cast<SceneRenderFn>(GetProcAddress(module, "Bn_Scene_RenderTest"));
    const auto sceneDestroy = reinterpret_cast<SceneDestroyFn>(GetProcAddress(module, "Bn_Scene_Destroy"));
    if (sceneProbe && sceneCreate && sceneRender && sceneDestroy)
    {
        const int probe = sceneProbe();
        if (probe != BN_OK)
        {
            Skip(L"scene D3D11 probe", probe);
        }
        else if (void* scene = sceneCreate(nullptr, 64, 48))
        {
            std::vector<uint8_t> pixels(64 * 48 * 4);
            BnCaptureResult result{};
            const int code = sceneRender(scene, pixels.data(), static_cast<int32_t>(pixels.size()), &result);
            if (code == BN_OK && result.width == 64 && result.height == 48)
            {
                const bool varies = pixels[0] != pixels[1234] || pixels[0] != pixels[8000];
                varies ? Pass(L"scene offscreen render") : Fail(L"scene offscreen render (uniform)", -1);
            }
            else
            {
                Fail(L"scene offscreen render", code);
            }
            sceneDestroy(scene);
        }
        else
        {
            Fail(L"scene create", -1);
        }
    }

    const auto captureCreate = reinterpret_cast<CaptureCreateFn>(GetProcAddress(module, "Bn_Capture_Create"));
    const auto captureWindow = reinterpret_cast<CaptureWindowFn>(GetProcAddress(module, "Bn_Capture_Window"));
    const auto captureScreen = reinterpret_cast<CaptureScreenFn>(GetProcAddress(module, "Bn_Capture_Screen"));
    const auto captureDestroy = reinterpret_cast<CaptureDestroyFn>(GetProcAddress(module, "Bn_Capture_Destroy"));
    if (captureCreate && captureScreen && captureDestroy)
    {
        if (void* capture = captureCreate())
        {
            std::vector<uint8_t> pixels(100 * 100 * 4);
            BnCaptureResult result{};
            const BnRect rect{ 0, 0, 100, 100 };
            const int code = captureScreen(capture, &rect, pixels.data(), static_cast<int32_t>(pixels.size()), &result);
            if (code == BN_OK) (result.rejected ? Skip(L"screen capture (uniform frame)", code) : Pass(L"screen capture"));
            else Skip(L"screen capture (headless)", code);

            // 自身控制台窗口：优先验证 WGC 路（mode=1）；失败回落 PrintWindow/BitBlt（3/4）。
            if (captureWindow)
            {
                HWND console = GetConsoleWindow();
                if (console)
                {
                    std::vector<uint8_t> windowPixels(4096 * 2160 * 4);
                    BnCaptureResult windowResult{};
                    const int wcode = captureWindow(capture, console, windowPixels.data(),
                                                    static_cast<int32_t>(windowPixels.size()), &windowResult);
                    if (wcode == BN_OK)
                    {
                        wprintf(L"INFO  window capture mode=%d %dx%d rejected=%d\n",
                                windowResult.mode, windowResult.width, windowResult.height, windowResult.rejected);
                        if (windowResult.mode == 1) Pass(L"WGC window capture");
                        else Skip(L"WGC unavailable; GDI fallback ok", windowResult.mode);
                    }
                    else
                    {
                        Skip(L"window capture (headless/unsupported)", wcode);
                    }
                }
            }
            captureDestroy(capture);
        }
    }

    const auto injectProbe = reinterpret_cast<InjectProbeFn>(GetProcAddress(module, "Bn_Inject_Probe"));
    if (injectProbe)
    {
        const int code = injectProbe();
        if (code == BN_OK) Pass(L"inject OS-build gate");
        else Skip(L"inject OS-build gate", code);
    }

    if (lastError) wprintf(L"lastError: %s\n", lastError());
    wprintf(L"\nresult: %d passed, %d skipped, %d failed\n", g_pass, g_skip, g_fail);
    FreeLibrary(module);
    return g_fail == 0 ? 0 : 1;
}
