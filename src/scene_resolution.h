#pragma once
#include "headset_resolution.h"
#include "scene_observer.h"
#include <d3d11.h>
#include <cstddef>
#include <cstdint>

namespace dirt3vr::sceneresolution {
struct Size {
    unsigned width{},height{};
    bool operator==(const Size&) const = default;
};
// DiRT 3 Complete Edition only; see docs/research/scene-resolution.md.
struct Renderer {
    uint8_t prefix[0xc],fullscreen,beforeSize[0x30-0xd];
    unsigned width,height;
    uint8_t beforeWindow[0x78-0x38];
    HWND window;
    uint8_t beforeDevice[0xf0-0x7c];
    IUnknown* device;
    IDXGISwapChain* chain;
    ID3D11DeviceContext* context;
    DXGI_SWAP_CHAIN_DESC swap;
    uint8_t beforeWindowSize[0x164-0x138];
    unsigned windowWidth,windowHeight;
};
static_assert(offsetof(Renderer,width)==0x30);
static_assert(offsetof(Renderer,window)==0x78);
static_assert(offsetof(Renderer,device)==0xf0);
static_assert(offsetof(Renderer,chain)==0xf4);
static_assert(offsetof(Renderer,swap)==0xfc);
static_assert(offsetof(Renderer,windowWidth)==0x164);
static_assert(sizeof(Renderer)==0x16c);

struct Change {
    Renderer* renderer{};
    Size engine{},window{},descriptor{},effective{};
    bool recreation{};
    explicit operator bool() const noexcept { return renderer!=nullptr; }
};
// A binding is used only after the executable SHA and interactive-VR opt-in
// succeed. It owns no COM references and never modifies desktop launches.
class Binding {
public:
    Change PrepareCreate(uintptr_t base,uintptr_t caller,IUnknown* device,DXGI_SWAP_CHAIN_DESC* desc,
                         IDXGISwapChain** output,const HeadsetResolutionRequest& request,DiagnosticLogger log=nullptr) noexcept;
    void CompleteCreate(const Change& change,HRESULT result,IDXGISwapChain* chain) noexcept;
    Change PrepareResize(uintptr_t base,uintptr_t caller,IDXGISwapChain* chain,
                         UINT& width,UINT& height,DXGI_FORMAT format) noexcept;
    void CompleteResize(const Change& change,HRESULT result) noexcept;
    void Forget(IDXGISwapChain* chain) noexcept;
private:
    Renderer* renderer_{};
    IUnknown* device_{};
    HWND window_{};
    IDXGISwapChain* chain_{};
    uintptr_t base_{};
    DWORD owner_{};
    Size requested_{};
};
}
