#pragma once
#include <windows.h>
#include <d3d11.h>
#include <memory>

namespace dirt3vr {
struct GpuCounterState;
// GRID2VR's GPU usage: Windows adapter counters sampled once per second on a worker that owns
// no D3D/XR objects; the busiest engine of the game's adapter, -1 while unknown. Stop signals
// the worker and never waits for it.
class GpuUsageSampler {
public:
    GpuUsageSampler()=default;
    GpuUsageSampler(const GpuUsageSampler&)=delete;
    GpuUsageSampler& operator=(const GpuUsageSampler&)=delete;
    ~GpuUsageSampler() {Stop();}
    bool Start(ID3D11Device* device);
    double Percent() const;
    void Stop();
private:
    std::shared_ptr<GpuCounterState> state_;
};
}
