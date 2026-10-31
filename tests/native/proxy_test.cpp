#include "diagnostics.h"
#include <wrl/client.h>
#include <filesystem>
#include <iostream>
#include <stdexcept>
#include <string>

namespace {
using Microsoft::WRL::ComPtr;
void Require(bool value, const char* message) { if(!value) throw std::runtime_error(message); }
template<class T> T Procedure(HMODULE module, const char* name) {
    const auto result = GetProcAddress(module, name);
    Require(result != nullptr, name); return reinterpret_cast<T>(result);
}
struct Window {
    HWND value{};
    Window() {
        WNDCLASSW type{}; type.lpfnWndProc = DefWindowProcW; type.hInstance = GetModuleHandleW(nullptr);
        type.lpszClassName = L"DiRT3VR.NativeFixture";
        Require(RegisterClassW(&type) != 0, "Window class registration failed");
        value = CreateWindowW(type.lpszClassName, L"Synthetic D3D11 fixture", WS_OVERLAPPEDWINDOW,
                              0, 0, 320, 240, nullptr, nullptr, type.hInstance, nullptr);
        Require(value != nullptr, "Hidden fixture window creation failed");
    }
    ~Window() { if(value) DestroyWindow(value); }
};
void CheckExports(HMODULE proxy, HMODULE system) {
    struct Export { const char* name; WORD ordinal; };
    const Export expected[] = {
#include "export_manifest.inc"
    };
    for(const auto& item : expected) {
        const auto named = GetProcAddress(proxy, item.name);
        const auto ordinal = GetProcAddress(proxy, MAKEINTRESOURCEA(item.ordinal));
        Require(named != nullptr && named == ordinal, "Proxy export name/ordinal mismatch");
        Require(GetProcAddress(system, item.name) == GetProcAddress(system, MAKEINTRESOURCEA(item.ordinal)), "System exports changed after generation");
    }
    std::cout << "Verified " << std::size(expected) << " system export names and ordinals\n";
}
}

int wmain(int argc, wchar_t** argv) {
    try {
        Require(argc == 3, "Expected DLL path and mode");
        const bool observe = wcscmp(argv[2], L"observe") == 0;
        const bool disabled = wcscmp(argv[2], L"disabled") == 0;
        const bool unsupported = wcscmp(argv[2], L"unsupported") == 0;
        Require(observe || disabled || unsupported, "Unknown mode");
        SetEnvironmentVariableW(L"DIRT3VR_DIAGNOSTICS", disabled ? L"0" : L"1");
        // This must never override the production binary's fingerprint guard.
        SetEnvironmentVariableW(L"DIRT3VR_TEST_FIXTURE", L"1");
        SetEnvironmentVariableW(L"DIRT3VR_SCENE_DIAGNOSTICS", L"1");
        const auto logs = std::filesystem::temp_directory_path() / (L"DiRT3VR.NativeFixture-" + std::to_wstring(GetCurrentProcessId()));
        SetEnvironmentVariableW(L"DIRT3VR_LOG_DIR", logs.c_str());
        const auto proxy = LoadLibraryExW(std::filesystem::absolute(argv[1]).c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        Require(proxy != nullptr, "Unable to load proxy");
        wchar_t directory[MAX_PATH]{}; Require(GetSystemDirectoryW(directory, MAX_PATH) != 0, "System directory unavailable");
        const auto system = LoadLibraryExW((std::filesystem::path(directory) / L"d3d11.dll").c_str(), nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
        Require(system != nullptr, "Unable to load system D3D11");
        CheckExports(proxy, system);
        const auto create = Procedure<decltype(&D3D11CreateDevice)>(proxy, "D3D11CreateDevice");
        const auto direct = Procedure<decltype(&D3D11CreateDevice)>(system, "D3D11CreateDevice");
        const auto createSwapchain = Procedure<decltype(&D3D11CreateDeviceAndSwapChain)>(proxy, "D3D11CreateDeviceAndSwapChain");
        const auto getStatus = Procedure<BOOL (__cdecl*)(DiRT3VRStatus*, UINT)>(proxy, "DiRT3VRGetStatus");
        const auto invalidDirect = direct(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, nullptr, 0, 0, nullptr, nullptr, nullptr);
        const auto invalidProxy = create(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, nullptr, 0, 0, nullptr, nullptr, nullptr);
        Require(FAILED(invalidDirect) && invalidDirect == invalidProxy, "Failed constructor HRESULT changed");

        ComPtr<ID3D11Device> device; ComPtr<ID3D11DeviceContext> context;
        D3D_FEATURE_LEVEL level{};
        Require(SUCCEEDED(create(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &device, &level, &context)), "WARP device creation failed");
        Require(device && context && level >= D3D_FEATURE_LEVEL_10_0, "WARP constructor output mismatch");
        Window window;
        DXGI_SWAP_CHAIN_DESC desc{};
        desc.BufferDesc.Width = 320; desc.BufferDesc.Height = 240; desc.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
        desc.SampleDesc.Count = 1; desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.BufferCount = 1; desc.OutputWindow = window.value; desc.Windowed = TRUE; desc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;
        // DiRT 3 creates the device first and obtains its chain from DXGI later.
        // Exercise that independently of the combined D3D11 constructor.
        ComPtr<IDXGIDevice> dxgi; ComPtr<IDXGIAdapter> adapter; ComPtr<IDXGIFactory> factory;
        Require(SUCCEEDED(device.As(&dxgi)) && SUCCEEDED(dxgi->GetAdapter(&adapter)) &&
                SUCCEEDED(adapter->GetParent(IID_PPV_ARGS(&factory))), "Device factory lookup failed");
        ComPtr<IDXGISwapChain> splitChain;
        Require(SUCCEEDED(factory->CreateSwapChain(device.Get(), &desc, &splitChain)), "Separate factory swapchain creation failed");
        Require(SUCCEEDED(splitChain->Present(0, 0)), "Separate swapchain Present failed");
        splitChain.Reset(); factory.Reset(); adapter.Reset(); dxgi.Reset(); context.Reset(); device.Reset();

        ComPtr<IDXGISwapChain> chain;
        Require(SUCCEEDED(createSwapchain(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, 0, nullptr, 0, D3D11_SDK_VERSION, &desc, &chain, &device, &level, &context)), "WARP swapchain creation failed");
        ComPtr<ID3D11Texture2D> back;
        Require(SUCCEEDED(chain->GetBuffer(0, IID_PPV_ARGS(&back))), "Backbuffer unavailable");
        ComPtr<ID3D11RenderTargetView> target;
        Require(SUCCEEDED(device->CreateRenderTargetView(back.Get(), nullptr, &target)), "Render target creation failed");
        const float color[] = {.1f, .2f, .3f, 1.f}; context->ClearRenderTargetView(target.Get(), color);
        Require(SUCCEEDED(chain->Present(0, DXGI_PRESENT_TEST)), "Present test failed");
        Require(SUCCEEDED(chain->Present(0, 0)), "Present failed");
        const auto failedResize = chain->ResizeBuffers(1, 400, 300, DXGI_FORMAT_UNKNOWN, 0);
        Require(FAILED(failedResize), "Resize with held backbuffer unexpectedly succeeded");
        target.Reset(); back.Reset(); context->ClearState(); context->Flush();
        Require(SUCCEEDED(chain->ResizeBuffers(1, 400, 300, DXGI_FORMAT_UNKNOWN, 0)), "Resize failed after releasing backbuffer");
        Require(SUCCEEDED(chain->Present(0, 0)), "Present after resize failed");
        DXGI_SWAP_CHAIN_DESC resized{}; Require(SUCCEEDED(chain->GetDesc(&resized)), "Resized description failed");
        Require(resized.BufferDesc.Width == 400 && resized.BufferDesc.Height == 300, "Proxy altered resize output");

        DiRT3VRStatus status;
        Require(!getStatus(nullptr, sizeof(status)) && !getStatus(&status, 0), "Invalid status request accepted");
        Require(getStatus(&status, sizeof(status)), "Status request failed");
        Require(status.version == 1 && status.stereoVerified == 0 && status.supportedHost == 0, "Fixture falsely reports game/stereo support");
        Require(status.syntheticFixture == (unsupported ? 0u : 1u), "Production fixture allowance leaked");
        Require(status.diagnosticsEnabled == (disabled ? 0u : 1u), "Diagnostics setting ignored");
        if(observe) {
            Require(status.observerAttached && status.deviceCreates == 2 && status.swapchains == 2, "Creation observations missing");
            Require(status.presents == 4 && status.resizes == 2 && status.failedResizes == 1, "Present/resize observation counters incorrect");
            Require(status.width == 400 && status.height == 300, "Successful resize dimensions missing");
        } else {
            Require(!status.observerAttached && !status.deviceCreates && !status.presents && !status.resizes, "Inactive/unsupported host was instrumented");
        }
        chain.Reset(); context.Reset(); device.Reset();
        std::cout << "D3D11 forwarding, failure preservation, resize, and guard tests passed; logs=" << logs.string() << '\n';
        return 0;
    } catch(const std::exception& error) {
        std::cerr << error.what() << '\n'; return 1;
    }
}
