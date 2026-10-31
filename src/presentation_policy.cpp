#include "presentation_policy.h"
#include <d3d11_1.h>
#include <wrl/client.h>
#include <array>

namespace dirt3vr {
namespace {
using Microsoft::WRL::ComPtr;
struct Pipeline {
    D3D11_TEXTURE2D_DESC target{};
    D3D11_DEPTH_STENCIL_DESC depth{};
    D3D11_BLEND_DESC blend{};
    UINT stencil{};
    bool hasDepth{};
};
bool Output(ID3D11DeviceContext* context,ID3D11Texture2D* backbuffer,Pipeline& state) noexcept {
    if(!context || !backbuffer || context->GetType()!=D3D11_DEVICE_CONTEXT_IMMEDIATE) return false;
    std::array<ID3D11RenderTargetView*,8> raw{};
    std::array<ComPtr<ID3D11RenderTargetView>,8> targets;
    ComPtr<ID3D11DepthStencilView> depth;
    context->OMGetRenderTargets(UINT(raw.size()),raw.data(),&depth);
    for(size_t i=0;i<raw.size();++i) targets[i].Attach(raw[i]);
    if(!targets[0]) return false;
    for(size_t i=1;i<targets.size();++i) if(targets[i]) return false;
    ComPtr<ID3D11Resource> resource; targets[0]->GetResource(&resource);
    // A stale borrowed address is safe to compare. Inspect only the live
    // resource returned by the bound RTV after it has matched that identity.
    if(resource.Get()!=static_cast<ID3D11Resource*>(backbuffer)) return false;
    ComPtr<ID3D11Texture2D> texture;
    if(FAILED(resource.As(&texture))) return false;
    texture->GetDesc(&state.target);
    if(!state.target.Width || !state.target.Height || state.target.ArraySize!=1 || state.target.MipLevels!=1 ||
        state.target.SampleDesc.Count!=1) return false;
    D3D11_RENDER_TARGET_VIEW_DESC targetView{}; targets[0]->GetDesc(&targetView);
    if(targetView.ViewDimension!=D3D11_RTV_DIMENSION_TEXTURE2D || targetView.Texture2D.MipSlice || targetView.Format!=state.target.Format) return false;
    ComPtr<ID3D11Device> device; context->GetDevice(&device);
    std::array<ID3D11UnorderedAccessView*,D3D11_1_UAV_SLOT_COUNT> unordered{};
    const auto slots=device->GetFeatureLevel()>=D3D_FEATURE_LEVEL_11_1 ? D3D11_1_UAV_SLOT_COUNT : D3D11_PS_CS_UAV_REGISTER_COUNT;
    context->OMGetRenderTargetsAndUnorderedAccessViews(0,nullptr,nullptr,0,slots,unordered.data());
    bool writes{}; for(auto* view:unordered) if(view) { writes=true; view->Release(); }
    if(writes) return false;
    ComPtr<ID3D11VertexShader> vertex; ComPtr<ID3D11PixelShader> pixel;
    ComPtr<ID3D11GeometryShader> geometry; ComPtr<ID3D11HullShader> hull; ComPtr<ID3D11DomainShader> domain;
    context->VSGetShader(&vertex,nullptr,nullptr); context->PSGetShader(&pixel,nullptr,nullptr);
    context->GSGetShader(&geometry,nullptr,nullptr); context->HSGetShader(&hull,nullptr,nullptr); context->DSGetShader(&domain,nullptr,nullptr);
    if(!vertex || !pixel || geometry || hull || domain) return false;
    std::array<ID3D11Buffer*,D3D11_SO_BUFFER_SLOT_COUNT> streams{};
    context->SOGetTargets(UINT(streams.size()),streams.data());
    for(auto* stream:streams) if(stream) { writes=true; stream->Release(); }
    if(writes) return false;
    D3D11_PRIMITIVE_TOPOLOGY topology{}; context->IAGetPrimitiveTopology(&topology);
    if(topology!=D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST && topology!=D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP) return false;
    UINT count=1; D3D11_VIEWPORT viewport{}; context->RSGetViewports(&count,&viewport);
    if(count!=1 || viewport.TopLeftX!=0 || viewport.TopLeftY!=0 || viewport.Width!=float(state.target.Width) ||
        viewport.Height!=float(state.target.Height) || viewport.MinDepth!=0 || viewport.MaxDepth!=1) return false;
    ComPtr<ID3D11RasterizerState> raster; context->RSGetState(&raster);
    if(!raster) return false; // Native measured state is explicitly solid/no-cull.
    D3D11_RASTERIZER_DESC rasterDesc{}; raster->GetDesc(&rasterDesc);
    if(rasterDesc.FillMode!=D3D11_FILL_SOLID || rasterDesc.CullMode!=D3D11_CULL_NONE ||
        !rasterDesc.DepthClipEnable || rasterDesc.ScissorEnable) return false;
    ComPtr<ID3D11BlendState> blend; FLOAT factors[4]{}; UINT sampleMask{};
    context->OMGetBlendState(&blend,factors,&sampleMask);
    if(blend) blend->GetDesc(&state.blend);
    else {
        auto& t=state.blend.RenderTarget[0]; t.SrcBlend=t.SrcBlendAlpha=D3D11_BLEND_ONE;
        t.DestBlend=t.DestBlendAlpha=D3D11_BLEND_ZERO; t.BlendOp=t.BlendOpAlpha=D3D11_BLEND_OP_ADD;
        t.RenderTargetWriteMask=D3D11_COLOR_WRITE_ENABLE_ALL;
    }
    if(sampleMask!=0xffffffffu || state.blend.AlphaToCoverageEnable || state.blend.IndependentBlendEnable) return false;
    ComPtr<ID3D11DepthStencilState> depthState; context->OMGetDepthStencilState(&depthState,&state.stencil);
    if(depthState) depthState->GetDesc(&state.depth);
    else { state.depth.DepthEnable=TRUE; state.depth.DepthWriteMask=D3D11_DEPTH_WRITE_MASK_ALL; state.depth.DepthFunc=D3D11_COMPARISON_LESS; }
    state.hasDepth=depth!=nullptr;
    return true;
}
bool Opaque(const D3D11_RENDER_TARGET_BLEND_DESC& blend) noexcept {
    return !blend.BlendEnable && blend.RenderTargetWriteMask==D3D11_COLOR_WRITE_ENABLE_ALL;
}
bool Alpha(const D3D11_RENDER_TARGET_BLEND_DESC& blend) noexcept {
    return blend.BlendEnable && blend.RenderTargetWriteMask==D3D11_COLOR_WRITE_ENABLE_ALL &&
        blend.SrcBlend==D3D11_BLEND_SRC_ALPHA && blend.DestBlend==D3D11_BLEND_INV_SRC_ALPHA && blend.BlendOp==D3D11_BLEND_OP_ADD &&
        blend.SrcBlendAlpha==D3D11_BLEND_SRC_ALPHA && blend.DestBlendAlpha==D3D11_BLEND_INV_SRC_ALPHA && blend.BlendOpAlpha==D3D11_BLEND_OP_ADD;
}
bool Source(ID3D11ShaderResourceView* view,D3D11_TEXTURE2D_DESC& desc,ComPtr<ID3D11Resource>& resource) noexcept {
    if(!view) return false;
    D3D11_SHADER_RESOURCE_VIEW_DESC srv{}; view->GetDesc(&srv);
    if(srv.ViewDimension!=D3D11_SRV_DIMENSION_TEXTURE2D || srv.Texture2D.MostDetailedMip || srv.Texture2D.MipLevels!=1) return false;
    view->GetResource(&resource); ComPtr<ID3D11Texture2D> texture;
    if(FAILED(resource.As(&texture))) return false;
    texture->GetDesc(&desc);
    return desc.Width && desc.Height && desc.MipLevels==1 && desc.ArraySize==1 && desc.SampleDesc.Count==1 && srv.Format==desc.Format;
}
bool EqualStencil(const Pipeline& state) noexcept {
    const auto keep=[](const D3D11_DEPTH_STENCILOP_DESC& face) {
        return face.StencilFunc==D3D11_COMPARISON_EQUAL && face.StencilFailOp==D3D11_STENCIL_OP_KEEP &&
            face.StencilDepthFailOp==D3D11_STENCIL_OP_KEEP && face.StencilPassOp==D3D11_STENCIL_OP_KEEP;
    };
    return state.hasDepth && state.depth.StencilEnable && state.stencil==1 &&
        state.depth.StencilReadMask==255 && state.depth.StencilWriteMask==255 && keep(state.depth.FrontFace) && keep(state.depth.BackFace);
}
}

bool IsIntroVideoDraw(ID3D11DeviceContext* context,ID3D11Texture2D* backbuffer,
    uint64_t vertexHash,uint64_t pixelHash,unsigned count,unsigned instances) noexcept {
    if(vertexHash!=IntroVideoVertexHash || pixelHash!=IntroVideoPixelHash || count!=4 || instances!=1) return false;
    Pipeline state;
    if(!Output(context,backbuffer,state) || state.depth.StencilEnable) return false;
    // The first measured Bink draw uses GE depth/opaque; later frames use
    // disabled depth/source alpha. Do not assume the first movie frame is 2D.
    const bool first=state.hasDepth && state.depth.DepthEnable && state.depth.DepthFunc==D3D11_COMPARISON_GREATER_EQUAL &&
        state.depth.DepthWriteMask==D3D11_DEPTH_WRITE_MASK_ALL && Opaque(state.blend.RenderTarget[0]);
    const bool later=!state.depth.DepthEnable && Alpha(state.blend.RenderTarget[0]);
    if(!first && !later) return false;
    std::array<ID3D11ShaderResourceView*,3> raw{}; std::array<ComPtr<ID3D11ShaderResourceView>,3> views;
    context->PSGetShaderResources(0,UINT(raw.size()),raw.data());
    for(size_t i=0;i<raw.size();++i) views[i].Attach(raw[i]);
    std::array<D3D11_TEXTURE2D_DESC,3> desc{}; std::array<ComPtr<ID3D11Resource>,3> source;
    for(size_t i=0;i<views.size();++i) if(!Source(views[i].Get(),desc[i],source[i]) || desc[i].Format!=DXGI_FORMAT_R8_UNORM) return false;
    return source[0]!=source[1] && source[0]!=source[2] && source[1]!=source[2] && !(desc[0].Width%2) && !(desc[0].Height%2) &&
        desc[1].Width==desc[0].Width/2 && desc[1].Height==desc[0].Height/2 && desc[2].Width==desc[1].Width && desc[2].Height==desc[1].Height;
}
bool IsStereoTransitionDraw(ID3D11DeviceContext* context,ID3D11Texture2D* backbuffer,
    uint64_t vertexHash,uint64_t pixelHash,unsigned count,unsigned instances) noexcept {
    if(vertexHash!=StereoTransitionVertexHash || pixelHash!=StereoTransitionPixelHash || count!=4 || instances!=1) return false;
    Pipeline state;
    if(!Output(context,backbuffer,state) || state.depth.DepthEnable) return false;
    // 0.1.12 trace32504: source-alpha/no-stencil at sequence812 onward;
    // opaque/EQUAL-KEEP stencil(ref1) at sequences4164-4228. Both select
    // the same TScreenGrab in both eyes. Every stencil-writing variant stays native.
    const bool fade=!state.depth.StencilEnable && Alpha(state.blend.RenderTarget[0]);
    const bool masked=EqualStencil(state) && Opaque(state.blend.RenderTarget[0]);
    if(!fade && !masked) return false;
    ComPtr<ID3D11ShaderResourceView> view; context->PSGetShaderResources(0,1,&view);
    D3D11_TEXTURE2D_DESC desc{}; ComPtr<ID3D11Resource> source;
    return Source(view.Get(),desc,source) && source.Get()!=static_cast<ID3D11Resource*>(backbuffer) &&
        desc.Width==state.target.Width && desc.Height==state.target.Height && desc.Format==state.target.Format;
}
}
