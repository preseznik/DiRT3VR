#pragma once
#include <d3d11_1.h>
#include <wrl/client.h>
#include <cstdint>
#include <span>
#include <array>
#include <atomic>

namespace dirt3vr {
// CPU shadow of the native 320-byte camera constant buffer; never GPU readback.
inline constexpr GUID HudCameraTag={0x8d11851b,0x7244,0x4e88,{0x83,0x95,0x46,0xa2,0x70,0x9c,0xd5,0x32}};
// Deferred command lists can write buffers without passing immediate-context
// upload hooks. Their execution invalidates old shadows conservatively.
inline std::atomic_uint64_t HudCameraGeneration{};
struct HudCameraShadow {
    uint64_t generation{};
    std::array<uint8_t,320> bytes{};
};

namespace hud_canvas_detail {
inline float Aspect(unsigned width,unsigned height) noexcept {
    const float native=height ? float(width)/height : 1.f;
    return native>16.f/9.f ? native : 16.f/9.f;
}
// DiRT3's menu camera identity, every field within float rounding, with only the
// aspect-dependent X lens and its inverse normalized. On refusal, scale is unchanged.
bool CameraScale(std::span<const uint8_t> camera,unsigned width,unsigned height,float& scale) noexcept;
}

// Scoped canvas mapping for already-admitted screen-space backbuffer draws.
// Only the fingerprinted menu camera receives a post-VS projection correction;
// other captured HUD draws retain their native clip bounds and proportions.
class HudCanvas {
public:
    enum class Result { Unchanged, Applied, Failed };
    HudCanvas() = default;
    ~HudCanvas() { Reset(); }
    HudCanvas(const HudCanvas&) = delete;
    HudCanvas& operator=(const HudCanvas&) = delete;
    Result Apply(ID3D11DeviceContext* context,uint64_t vertexHash,uint64_t pixelHash,
                 std::span<const uint8_t> camera,unsigned width,unsigned height) noexcept;
    // Already-admitted UI without the menu-camera correction keeps its native
    // aspect inside the shared canvas. This preserves its original clip bounds.
    Result FitNative(ID3D11DeviceContext* context,unsigned width,unsigned height) noexcept;
    void Restore(ID3D11DeviceContext* context) noexcept;
    void Reset() noexcept;
private:
    bool Initialize(ID3D11Device* device);
    Microsoft::WRL::ComPtr<ID3D11Device> device_;
    Microsoft::WRL::ComPtr<ID3D11GeometryShader> simple_,text_;
    Microsoft::WRL::ComPtr<ID3D11Buffer> parameters_,previousBuffer_;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext1> activeContext_;
    ID3D11DeviceContext* owner_{};
    UINT previousFirst_{},previousCount_{};
    std::array<D3D11_VIEWPORT,D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE> previousViews_{};
    std::array<D3D11_RECT,D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE> previousScissors_{};
    UINT previousViewCount_{},previousScissorCount_{};
    bool viewportFit_{};
};
}
