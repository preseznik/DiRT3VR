#pragma once
#include <d3d11_1.h>
#include <wrl/client.h>
#include <array>
#include <cstdint>
#include <functional>

namespace dirt3vr {
using FrontendShadowMatrix = std::array<float,16>;
constexpr bool IsFrontendShadowShaderPair(uint64_t vertex,uint64_t pixel) noexcept {
    return vertex==0x4b2b0f44adfbf5bbull &&
        (pixel==0x28625f0662b53c50ull || pixel==0xa40172fc4329f5f6ull || pixel==0x6299a6c46af41b38ull);
}

// Exact frontend shadow-mask draws only. The caller supplies this scene's
// original producer matrix and the independently derived current eye matrix.
// The GPU copies mismatching native constants through without correction.
class FrontendShadow {
public:
    FrontendShadow() = default;
    FrontendShadow(const FrontendShadow&) = delete;
    FrontendShadow& operator=(const FrontendShadow&) = delete;
    // False means no callback was invoked. True may be an unchanged GPU copy
    // when the producer-matrix guard refused. Callback exceptions propagate
    // after all bindings have been restored.
    bool Draw(ID3D11DeviceContext* context,uint64_t vertex,uint64_t pixel,
              const FrontendShadowMatrix& expected,const FrontendShadowMatrix& corrected,
              const std::function<void()>& draw);
    void Reset() noexcept;
private:
    bool Initialize(ID3D11DeviceContext* context);
    Microsoft::WRL::ComPtr<ID3D11Device> device_;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext1> context_;
    Microsoft::WRL::ComPtr<ID3DDeviceContextState> state_;
    Microsoft::WRL::ComPtr<ID3D11ComputeShader> shader_;
    Microsoft::WRL::ComPtr<ID3D11Buffer> source_,output_,constant_,parameters_;
    Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> sourceView_;
    Microsoft::WRL::ComPtr<ID3D11UnorderedAccessView> outputView_;
    DWORD thread_{};
    bool busy_{};
};
}
