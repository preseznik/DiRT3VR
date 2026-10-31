#pragma once
#include <d3d11_1.h>
#include <wrl/client.h>

namespace dirt3vr {
// Adapts DiRT2VR EyeBlit's isolated D3D11.1 context-state approach. The source is
// a completed gamma-encoded game backbuffer. Draw never retains that backbuffer.
class TexturePresenter {
public:
    enum class Mode { Opaque, StraightAlpha, Composite };
    using Logger=void (*)(const char*);
    explicit TexturePresenter(Logger logger=nullptr) noexcept : logger_(logger) {}
    TexturePresenter(const TexturePresenter&)=delete;
    TexturePresenter& operator=(const TexturePresenter&)=delete;
    bool Initialize(ID3D11Device* device) noexcept;
    bool Draw(ID3D11Texture2D* source,ID3D11RenderTargetView* target,unsigned width,unsigned height,Mode mode=Mode::Opaque) noexcept;
    void Reset() noexcept;
private:
    bool PrepareSource(ID3D11Texture2D* source,const D3D11_TEXTURE2D_DESC& desc);
    void Report(const char*) noexcept;
    Logger logger_{};
    Microsoft::WRL::ComPtr<ID3D11Device> device_;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext1> context_;
    Microsoft::WRL::ComPtr<ID3DDeviceContextState> state_;
    Microsoft::WRL::ComPtr<ID3D11VertexShader> vertex_;
    Microsoft::WRL::ComPtr<ID3D11PixelShader> pixel_;
    Microsoft::WRL::ComPtr<ID3D11PixelShader> alphaPixel_,compositePixel_;
    Microsoft::WRL::ComPtr<ID3D11BlendState> compositeBlend_;
    Microsoft::WRL::ComPtr<ID3D11SamplerState> sampler_;
    Microsoft::WRL::ComPtr<ID3D11RasterizerState> raster_;
    Microsoft::WRL::ComPtr<ID3D11DepthStencilState> depth_;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> sourceCopy_;
    Microsoft::WRL::ComPtr<ID3D11ShaderResourceView> sourceView_;
    unsigned sourceWidth_{},sourceHeight_{};
    DXGI_FORMAT sourceFormat_=DXGI_FORMAT_UNKNOWN;
};
}
