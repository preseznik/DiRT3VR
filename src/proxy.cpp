#include "diagnostics.h"
#include <intrin.h>
#include <filesystem>

// Resolve only the Windows DLL by absolute path; never search the game folder.
// This occurs on the caller's normal thread, not under DllMain's loader lock.
static FARPROC RealProc(const char* name) {
    static HMODULE real = [] {
        wchar_t system[MAX_PATH]{};
        const auto length = GetSystemDirectoryW(system, MAX_PATH);
        if(!length || length >= MAX_PATH) return HMODULE{};
        return LoadLibraryExW((std::filesystem::path(system) / L"d3d11.dll").c_str(),
                              nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32);
    }();
    FARPROC proc = real ? GetProcAddress(real, name) : nullptr;
    if(!proc) __fastfail(FAST_FAIL_FATAL_APP_EXIT);
    return proc;
}

extern "C" HRESULT WINAPI ProxyCreateDevice(IDXGIAdapter* adapter, D3D_DRIVER_TYPE type,
    HMODULE software, UINT flags, const D3D_FEATURE_LEVEL* levels, UINT count, UINT sdk,
    ID3D11Device** device, D3D_FEATURE_LEVEL* level, ID3D11DeviceContext** context) {
    const auto fn = reinterpret_cast<decltype(&D3D11CreateDevice)>(RealProc("D3D11CreateDevice"));
    const HRESULT result = fn(adapter, type, software, flags, levels, count, sdk, device, level, context);
    dirt3vr::ObserveCreation(result, SUCCEEDED(result) && device ? *device : nullptr, nullptr);
    return result;
}

extern "C" HRESULT WINAPI ProxyCreateDeviceAndSwapChain(IDXGIAdapter* adapter, D3D_DRIVER_TYPE type,
    HMODULE software, UINT flags, const D3D_FEATURE_LEVEL* levels, UINT count, UINT sdk,
    const DXGI_SWAP_CHAIN_DESC* desc, IDXGISwapChain** swapchain, ID3D11Device** device,
    D3D_FEATURE_LEVEL* level, ID3D11DeviceContext** context) {
    const auto fn = reinterpret_cast<decltype(&D3D11CreateDeviceAndSwapChain)>(RealProc("D3D11CreateDeviceAndSwapChain"));
    const HRESULT result = fn(adapter, type, software, flags, levels, count, sdk, desc, swapchain, device, level, context);
    dirt3vr::ObserveCreation(result, SUCCEEDED(result) && device ? *device : nullptr,
                            SUCCEEDED(result) && swapchain ? *swapchain : nullptr);
    return result;
}

extern "C" BOOL __cdecl DiRT3VRGetStatus(DiRT3VRStatus* status, UINT size) {
    if(!status || size != sizeof(DiRT3VRStatus)) return FALSE;
    dirt3vr::ReadStatus(*status);
    return TRUE;
}

#include "forwarders.inc"
