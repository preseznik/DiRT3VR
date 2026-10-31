#pragma once
#include <windows.h>
#include <d3d11.h>
#include <cstdint>
#include <filesystem>

// Versioned snapshot for local diagnostics. There is no interface to enable stereo.
struct DiRT3VRStatus {
    uint32_t size = sizeof(DiRT3VRStatus);
    uint32_t version = 1;
    uint32_t diagnosticsEnabled{};
    uint32_t supportedHost{};
    uint32_t syntheticFixture{};
    uint32_t stereoVerified{};
    uint32_t observerAttached{};
    uint32_t deviceCreates{};
    uint32_t swapchains{};
    uint32_t presents{};
    uint32_t resizes{};
    uint32_t failedPresents{};
    uint32_t failedResizes{};
    uint32_t width{};
    uint32_t height{};
};

namespace dirt3vr {
void ObserveCreation(HRESULT result, ID3D11Device* device, IDXGISwapChain* swapchain) noexcept;
void ReadStatus(DiRT3VRStatus& status) noexcept;
// Called only within an observed scene on the game's render thread. Returns an
// owned backbuffer reference; no swapchain reference survives this call.
bool AcquireDiagnosticBackbuffer(ID3D11Texture2D** texture, std::filesystem::path& directory) noexcept;
}
