#pragma once
#include <d3d11.h>
#include <wrl/client.h>
#include <cstdint>
#include <functional>
#include <algorithm>
#include "hud_probe.h"
#include "hud_canvas.h"

namespace dirt3vr {
enum class HudShaderKind : unsigned { None, TwoDimensional, Shared };
// Adapted from DiRT2VR. Captures UI draws without executing game UI logic again.
// A frame never retains the game backbuffer after End or Reset.
class HudCapture {
public:
    bool Begin(ID3D11Texture2D* back,uint64_t frame) noexcept;
    bool Draw(ID3D11DeviceContext* context,const std::function<void()>& draw,
              HudShaderKind kind,bool firstEye,uint64_t vertexHash=0,uint64_t pixelHash=0);
    void End() noexcept;
    void Reset() noexcept;
    bool Failed() const noexcept { return failed_; }
    unsigned Draws() const noexcept { return draws_; }
    unsigned Suppressed() const noexcept { return suppressed_; }
    unsigned CanvasDraws() const noexcept { return canvasDraws_; }
    float Aspect() const noexcept { return hud_canvas_detail::Aspect(width_,height_); }
    float PanelWidth() const noexcept {
        // The native-aspect region inside this canvas retains its previous
        // physical size. Menu draws can use the remaining horizontal space.
        return width_ ? std::min(1.6f*height_/width_,1.8f)*Aspect() : 1.6f;
    }
    ID3D11Texture2D* Current(uint64_t frame) const noexcept;
    HudProbe& Probe() noexcept { return probe_; }
    ID3D11Texture2D* Backbuffer() const noexcept { return back_.Get(); }
private:
    HudProbe probe_;
    HudCanvas canvas_;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> back_,image_;
    Microsoft::WRL::ComPtr<ID3D11RenderTargetView> target_;
    Microsoft::WRL::ComPtr<ID3D11BlendState> blend_,maskBlend_;
    D3D11_BLEND_DESC blendDesc_{};
    uint64_t frame_{};
    unsigned width_{},height_{},draws_{},suppressed_{},canvasDraws_{};
    bool active_{},ready_{},failed_{};
    bool probeHeld_{};
};
}
