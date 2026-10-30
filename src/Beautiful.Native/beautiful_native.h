#pragma once

// Beautiful.Native —— C ABI（v4 Phase 2）
// 线程契约：除特别注明外，全部函数在调用线程同步执行；DComp/窗口对象只在创建线程使用。
// 生命周期：Bn_Material_* / Bn_Capture_* / Bn_Scene_* 返回的 handle 由调用方 Destroy；
//           所有 handle 的 Destroy 幂等，重复调用返回 BN_E_INVALID_ARG。
// 崩溃隔离：所有导出都包 SEH/异常边界，失败返回负错误码，绝不把异常抛回 C#。

#include <stdint.h>
#include <wchar.h>

#ifdef __cplusplus
extern "C" {
#endif

#define BN_ABI_VERSION 1

// 与 C# NativeApi.Status 一一对应（负值 = 失败）
enum BnStatus : int32_t
{
    BN_OK = 0,
    BN_E_INVALID_ARG = -1,
    BN_E_NOT_AVAILABLE = -2,
    BN_E_NOT_IMPLEMENTED = -3,
    BN_E_OS_UNSUPPORTED = -4,
    BN_E_D3D = -5,
    BN_E_CAPTURE = -6,
    BN_E_IO = -7,
    BN_E_INJECT = -8,
    BN_E_UNKNOWN = -99,
};

// 模块位（Bn_GetHealth.modules）
enum BnModule : uint32_t
{
    BN_MODULE_MATERIAL = 1u << 0,
    BN_MODULE_CAPTURE = 1u << 1,
    BN_MODULE_SCENE = 1u << 2,
    BN_MODULE_INJECT = 1u << 3,
};

#pragma pack(push, 8)
typedef struct BnRect
{
    int32_t left;
    int32_t top;
    int32_t right;
    int32_t bottom;
} BnRect;

typedef struct BnHealth
{
    uint32_t abiVersion;
    uint32_t modules;          // 本次进程内已成功初始化的模块位
    wchar_t version[32];       // UTF-16，来自 NativeVersion.h（与 Directory.Build.props 同源）
    wchar_t lastError[256];    // 最近一次失败的中文说明（UTF-16，空串 = 无）
} BnHealth;

// 材质引擎：背板窗口 + Windows.UI.Composition HostBackdrop（D-BLUR 一级路）
typedef struct BnMaterialDesc
{
    BnRect rect;               // 物理像素
    uint32_t tint;             // 0xAARRGGBB 叠加色
    float opacity;             // 0..1（tint 不透明度）
    float cornerRadius;        // 物理像素
    int32_t dark;              // 1=暗色 token，0=亮色
} BnMaterialDesc;
#pragma pack(pop)

// ---------- 基础 ----------
__declspec(dllexport) uint32_t __cdecl Bn_AbiVersion(void);
__declspec(dllexport) int32_t __cdecl Bn_GetHealth(BnHealth* out);
__declspec(dllexport) const wchar_t* __cdecl Bn_LastError(void);

// ---------- 材质 ----------
// 返回 BN_OK 表示当前 OS/GPU 支持 HostBackdrop；不创建持久对象。
__declspec(dllexport) int32_t __cdecl Bn_Material_Probe(void);
// owner = WPF 内容窗口（背板插在它下面，保持同一 topmost 带）。返回 nullptr = 失败。
__declspec(dllexport) void* __cdecl Bn_Material_Create(void* owner, const BnMaterialDesc* desc);
__declspec(dllexport) int32_t __cdecl Bn_Material_SetRect(void* handle, const BnRect* rect);
__declspec(dllexport) int32_t __cdecl Bn_Material_SetTheme(void* handle, uint32_t tint, float opacity, int32_t dark);
__declspec(dllexport) int32_t __cdecl Bn_Material_Show(void* handle, int32_t show);
__declspec(dllexport) void __cdecl Bn_Material_Destroy(void* handle);

// ---------- 捕获 ----------
// 输出 BGRA（top-down，stride = width*4）。capBytes 不足时返回 BN_E_INVALID_ARG 并写 requiredBytes。
typedef struct BnCaptureResult
{
    int32_t width;
    int32_t height;
    int32_t stride;
    int32_t rejected;          // 1=纯色/无效帧（调用方不得使用像素）
    int32_t requiredBytes;
    int32_t mode;              // 1=WGC, 2=DXGI, 3=PrintWindow, 4=BitBlt, 0=失败
} BnCaptureResult;

__declspec(dllexport) void* __cdecl Bn_Capture_Create(void);
__declspec(dllexport) int32_t __cdecl Bn_Capture_Window(void* handle, void* hwnd, uint8_t* pixels, int32_t capBytes, BnCaptureResult* result);
__declspec(dllexport) int32_t __cdecl Bn_Capture_Screen(void* handle, const BnRect* rect, uint8_t* pixels, int32_t capBytes, BnCaptureResult* result);
__declspec(dllexport) void __cdecl Bn_Capture_Destroy(void* handle);

// ---------- 场景渲染（genie/打开动画的 native 地基） ----------
__declspec(dllexport) int32_t __cdecl Bn_Scene_Probe(void);
__declspec(dllexport) void* __cdecl Bn_Scene_Create(void* owner, int32_t width, int32_t height);
// 离屏渲染一张基准图（渐变 + 网格线），用于 NativeSmoke 验证 D3D11 路径；BGRA 输出。
__declspec(dllexport) int32_t __cdecl Bn_Scene_RenderTest(void* handle, uint8_t* pixels, int32_t capBytes, BnCaptureResult* result);
__declspec(dllexport) void __cdecl Bn_Scene_Destroy(void* handle);

// ---------- 注入框架（Phase 2 只做空 payload 的心跳/卸载演练） ----------
// probe：OS build 是否在 TrayBridge 白名单内（22000..26999）。
__declspec(dllexport) int32_t __cdecl Bn_Inject_Probe(void);
// 仅允许 explorer.exe；失败不拖垮主程序。返回前会等待 TrayBridge 心跳（≤2s）。
__declspec(dllexport) int32_t __cdecl Bn_Inject_Explorer(const wchar_t* dllPath);
// 发送卸载命令并等待确认（≤2s）；成功返回 BN_OK。
__declspec(dllexport) int32_t __cdecl Bn_Inject_Unload(void);

#ifdef __cplusplus
}
#endif
