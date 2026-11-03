#pragma once
#include "performance_stats.h"
#include <d3d11.h>
#include <wrl/client.h>
#include <cstdint>
#include <vector>

namespace dirt3vr {
// GRID2VR's headset FPS / GPU strip (#4896ffd3): "FPS 89.9  GPU 72%" in its small bitmap font,
// so it needs no desktop font services. FPS counts submitted stereo frames; GPU is the adapter's
// busiest engine. Redrawn at most once per second. Render thread only.
class PerformanceStrip {
public:
    static constexpr unsigned Width=512,Height=64;
    // Opaque R8G8B8A8 pixels; a negative value shows N/A.
    static std::vector<uint32_t> Pixels(double fps,double gpuPercent);
    void Submitted(uint64_t milliseconds) { rate_.Frame(milliseconds); }
    double Fps() const { return rate_.Fps(); }
    // The strip for TexturePresenter's Opaque mode, or nullptr.
    ID3D11Texture2D* Render(ID3D11Device* device,uint64_t milliseconds,double gpuPercent) noexcept;
private:
    SubmittedFrameRate rate_;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> texture_;
    uint64_t updated_{};
    bool drawn_{};
};
}
