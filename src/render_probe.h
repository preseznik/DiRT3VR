#pragma once
#include <d3d11.h>
#include <wrl/client.h>
#include <cstdint>
#include <filesystem>

namespace dirt3vr {
// A bounded diagnostic, never used on the regular rendering path. Pipeline
// statistics and readback prove what the GPU drew for each inner invocation.
class RenderProbe {
public:
    bool Begin();
    bool Finish(uint64_t sequence, unsigned eye);
    uint64_t Vertices() const { return statistics_.IAVertices; }
    uint64_t Primitives() const { return statistics_.IAPrimitives; }
    uint64_t Pixels() const { return statistics_.PSInvocations; }
    ~RenderProbe();
private:
    Microsoft::WRL::ComPtr<ID3D11Device> device_;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> context_;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> back_;
    Microsoft::WRL::ComPtr<ID3D11Query> query_;
    std::filesystem::path directory_;
    D3D11_QUERY_DATA_PIPELINE_STATISTICS statistics_{};
    bool active_{};
};
}
