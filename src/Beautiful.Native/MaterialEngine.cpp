#include "MaterialEngine.h"
#include "NativeCommon.h"

#include <mutex>
#include <dispatcherqueue.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.System.h>
#include <winrt/Windows.UI.h>
#include <winrt/Windows.UI.Composition.h>
#include <winrt/Windows.UI.Composition.Desktop.h>
#include <windows.ui.composition.interop.h>

using namespace winrt::Windows::UI::Composition;

namespace
{
    constexpr wchar_t kClassName[] = L"Beautiful.Native.Material";
    std::once_flag g_classOnce;
    bool g_classRegistered = false;
    std::once_flag g_queueOnce;
    winrt::com_ptr<ABI::Windows::System::IDispatcherQueueController> g_queueController;

    /// <summary>
    /// Windows.UI.Composition 在 Win11 上要求创建线程有 DispatcherQueue；WPF 的 UI 线程没有，
    /// 直接 `Compositor()` 会得到 0x80070005 "caller must initialize DispatcherQueue"。
    /// 在本线程创建一个（保持 controller 存活到进程退出），这是 HostBackdrop 能真正工作的前提。
    /// </summary>
    void EnsureDispatcherQueue()
    {
        std::call_once(g_queueOnce, [] {
            try
            {
                DispatcherQueueOptions options{};
                options.dwSize = sizeof(options);
                options.threadType = DQTYPE_THREAD_CURRENT;
                options.apartmentType = DQTAT_COM_NONE;
                winrt::com_ptr<ABI::Windows::System::IDispatcherQueueController> controller;
                const HRESULT hr = CreateDispatcherQueueController(options, controller.put());
                if (SUCCEEDED(hr))
                {
                    g_queueController = controller;
                    bn::Log(L"INFO", L"DispatcherQueue 已创建（Compositor 前置）");
                }
                else
                {
                    bn::Log(L"WARN", L"CreateDispatcherQueueController hr=0x%08X", static_cast<unsigned>(hr));
                }
            }
            catch (...) { bn::Log(L"WARN", L"DispatcherQueue 创建异常"); }
        });
    }

    LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wParam, LPARAM lParam)
    {
        if (msg == WM_NCHITTEST) return HTTRANSPARENT; // 背板只做材质，不抢鼠标
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    bool EnsureClass()
    {
        std::call_once(g_classOnce, [] {
            WNDCLASSEXW wc{};
            wc.cbSize = sizeof(wc);
            wc.lpfnWndProc = WndProc;
            wc.hInstance = GetModuleHandleW(nullptr);
            wc.hCursor = LoadCursorW(nullptr, IDC_ARROW);
            wc.lpszClassName = kClassName;
            g_classRegistered = RegisterClassExW(&wc) != 0 || GetLastError() == ERROR_CLASS_ALREADY_EXISTS;
        });
        return g_classRegistered;
    }

    winrt::Windows::UI::Color ColorFromArgb(uint32_t argb)
    {
        return winrt::Windows::UI::ColorHelper::FromArgb(
            static_cast<uint8_t>((argb >> 24) & 0xFF),
            static_cast<uint8_t>((argb >> 16) & 0xFF),
            static_cast<uint8_t>((argb >> 8) & 0xFF),
            static_cast<uint8_t>(argb & 0xFF));
    }

    struct Material
    {
        HWND hwnd{};
        HWND owner{};
        Compositor compositor{ nullptr };
        SpriteVisual root{ nullptr };
        SpriteVisual tintVisual{ nullptr };
        CompositionColorBrush tintBrush{ nullptr };
        CompositionRoundedRectangleGeometry rounded{ nullptr };
        float opacity{ 0.5f };
        int dark{ 1 };
        uint32_t tint{ 0x4D202020 };
        int width{};
        int height{};
        float radius{ 18.0f };

        void ApplySize()
        {
            if (!root) return;
            root.Size({ static_cast<float>(width), static_cast<float>(height) });
            tintVisual.Size({ static_cast<float>(width), static_cast<float>(height) });
            if (rounded)
            {
                rounded.Size({ static_cast<float>(width), static_cast<float>(height) });
                rounded.CornerRadius({ radius, radius });
            }
        }

        void ApplyTint()
        {
            if (!tintBrush) return;
            const auto color = ColorFromArgb(tint);
            tintBrush.Color(color);
            tintVisual.Opacity(opacity);
        }
    };
}

namespace bn
{
    int MaterialProbe()
    {
        try
        {
            EnsureDispatcherQueue();
            // 探针只创建 Composition 对象 + HostBackdrop 画刷，不需要窗口：
            // 失败（旧系统/无 DComp）返回明确错误，由 C# 走二级/三级降级。
            Compositor compositor;
            auto brush = compositor.CreateHostBackdropBrush();
            if (!brush) return BN_E_NOT_AVAILABLE;
            return BN_OK;
        }
        catch (const winrt::hresult_error& ex)
        {
            SetLastError(L"HostBackdrop 探针失败");
            Log(L"WARN", L"MaterialProbe: hr=0x%08X %s", static_cast<unsigned>(ex.code()), ex.message().c_str());
            return BN_E_NOT_AVAILABLE;
        }
        catch (...)
        {
            SetLastError(L"HostBackdrop 探针未知异常");
            return BN_E_UNKNOWN;
        }
    }

    void* MaterialCreate(HWND owner, const BnMaterialDesc& desc)
    {
        try
        {
            EnsureDispatcherQueue();
            if (!EnsureClass()) return nullptr;
            auto* material = new Material();
            material->owner = owner;
            material->width = desc.rect.right - desc.rect.left;
            material->height = desc.rect.bottom - desc.rect.top;
            material->radius = desc.cornerRadius > 0 ? desc.cornerRadius : 18.0f;
            material->tint = desc.tint;
            material->opacity = desc.opacity;
            material->dark = desc.dark;

            material->hwnd = CreateWindowExW(
                WS_EX_NOREDIRECTIONBITMAP | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT,
                kClassName, L"", WS_POPUP,
                desc.rect.left, desc.rect.top, material->width, material->height,
                nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
            if (!material->hwnd)
            {
                delete material;
                return nullptr;
            }

            material->compositor = Compositor();
            auto interop = material->compositor.as<ABI::Windows::UI::Composition::Desktop::ICompositorDesktopInterop>();
            winrt::Windows::UI::Composition::Desktop::DesktopWindowTarget target{ nullptr };
            winrt::check_hresult(interop->CreateDesktopWindowTarget(
                material->hwnd, TRUE,
                reinterpret_cast<ABI::Windows::UI::Composition::Desktop::IDesktopWindowTarget**>(winrt::put_abi(target))));

            material->root = material->compositor.CreateSpriteVisual();
            auto backdrop = material->compositor.CreateHostBackdropBrush();
            material->root.Brush(backdrop);
            material->tintBrush = material->compositor.CreateColorBrush(ColorFromArgb(material->tint));
            material->tintVisual = material->compositor.CreateSpriteVisual();
            material->tintVisual.Brush(material->tintBrush);
            material->root.Children().InsertAtTop(material->tintVisual);

            material->rounded = material->compositor.CreateRoundedRectangleGeometry();
            material->root.Clip(material->compositor.CreateGeometricClip(material->rounded));
            material->ApplySize();
            material->ApplyTint();
            target.Root(material->root);

            ShowWindow(material->hwnd, SW_SHOWNOACTIVATE);
            SetWindowPos(material->hwnd, owner, desc.rect.left, desc.rect.top, material->width, material->height,
                         SWP_NOACTIVATE | SWP_SHOWWINDOW);
            Log(L"INFO", L"Material created hwnd=0x%p owner=0x%p rect=(%d,%d)-(%d,%d) dark=%d tint=0x%08X",
                material->hwnd, owner, desc.rect.left, desc.rect.top, desc.rect.right, desc.rect.bottom,
                desc.dark, desc.tint);
            return material;
        }
        catch (const winrt::hresult_error& ex)
        {
            SetLastError(L"材质引擎创建失败（HostBackdrop/DComp）");
            Log(L"WARN", L"MaterialCreate: hr=0x%08X %s", static_cast<unsigned>(ex.code()), ex.message().c_str());
            return nullptr;
        }
        catch (...)
        {
            SetLastError(L"材质引擎创建未知异常");
            return nullptr;
        }
    }

    int MaterialSetRect(void* handle, const BnRect& rect)
    {
        auto* material = static_cast<Material*>(handle);
        if (!material) return BN_E_INVALID_ARG;
        try
        {
            material->width = rect.right - rect.left;
            material->height = rect.bottom - rect.top;
            material->ApplySize();
            SetWindowPos(material->hwnd, material->owner, rect.left, rect.top, material->width, material->height,
                         SWP_NOACTIVATE | SWP_SHOWWINDOW);
            return BN_OK;
        }
        catch (...)
        {
            return BN_E_UNKNOWN;
        }
    }

    int MaterialSetTheme(void* handle, uint32_t tint, float opacity, int dark)
    {
        auto* material = static_cast<Material*>(handle);
        if (!material) return BN_E_INVALID_ARG;
        material->tint = tint;
        material->opacity = opacity;
        material->dark = dark;
        material->ApplyTint();
        return BN_OK;
    }

    int MaterialShow(void* handle, bool show)
    {
        auto* material = static_cast<Material*>(handle);
        if (!material) return BN_E_INVALID_ARG;
        ShowWindow(material->hwnd, show ? SW_SHOWNOACTIVATE : SW_HIDE);
        if (show)
            SetWindowPos(material->hwnd, material->owner, 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        return BN_OK;
    }

    void MaterialDestroy(void* handle)
    {
        auto* material = static_cast<Material*>(handle);
        if (!material) return;
        try
        {
            if (material->hwnd) DestroyWindow(material->hwnd);
            material->root = nullptr;
            material->tintVisual = nullptr;
            material->tintBrush = nullptr;
            material->rounded = nullptr;
            material->compositor = nullptr;
        }
        catch (...) { }
        delete material;
        Log(L"INFO", L"Material destroyed");
    }
}
