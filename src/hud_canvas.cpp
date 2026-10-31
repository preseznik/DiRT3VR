#include "hud_canvas.h"
#include <d3dcompiler.h>
#include <array>
#include <cmath>
#include <cstring>
#include <limits>
#include <algorithm>

namespace dirt3vr {
using Microsoft::WRL::ComPtr;
namespace {
constexpr uint64_t ColourVertex=0xe057d253ecd4296eull;
constexpr uint64_t ImageVertex=0x5c571e5a5daa0ce4ull;
constexpr uint64_t TextVertex=0xad7b09c4361f558dull;
constexpr uint64_t ColourPixel=0xcf2fe246cd20d277ull;
constexpr char Shader[]=R"(
cbuffer Canvas : register(b0) { float scaleX; float removeBars; float2 reserved; };
struct Vertex {
    float4 position : SV_Position;
    float4 t0 : TEXCOORD0;
    float4 t1 : TEXCOORD1;
    float4 t2 : TEXCOORD2;
#ifdef TEXT_VERTEX
    float4 t3 : TEXCOORD3;
#endif
    float4 t4 : TEXCOORD4;
};
[maxvertexcount(3)]
void main(triangle Vertex input[3],inout TriangleStream<Vertex> output) {
    // Only the witnessed solid-colour overscan letterbox geometry qualifies.
    // Fullscreen fades, translucent black and bounded menu panels survive.
    bool bar=removeBars>0.5;
    float2 lo=float2(1e20,1e20),hi=float2(-1e20,-1e20);
    [unroll] for(uint i=0;i<3;++i) {
        float4 p=input[i].position;
        bar=bar && all(isfinite(p)) && abs(p.w-1.0)<1e-6 &&
            all(input[i].t2.rgb==0.0) && input[i].t2.a==1.0;
        float2 xy=p.xy/p.w;
        lo=min(lo,xy); hi=max(hi,xy);
    }
    bool top=lo.y>=0.7901 && lo.y<=0.7903 && hi.y>3.0;
    bool bottom=hi.y<=-0.7901 && hi.y>=-0.7903 && lo.y<-3.0;
    if(bar && lo.x<-2.0 && hi.x>2.0 && (top || bottom)) return;
    [unroll] for(uint j=0;j<3;++j) {
        Vertex v=input[j]; v.position.x*=scaleX; output.Append(v);
    }
    output.RestartStrip();
}
)";
}
bool hud_canvas_detail::CameraScale(std::span<const uint8_t> camera,unsigned width,unsigned height,float& scale) noexcept {
    if(camera.size()!=320 || !width || !height || width>4096 || height>4096) return false;
    const auto value=[&](size_t offset) { float result; std::memcpy(&result,camera.data()+offset,4); return result; };
    const float px=value(0),py=value(20),inverseX=value(240);
    if(!std::isfinite(px) || !std::isfinite(py) || !std::isfinite(inverseX) || px<=.01f || px>16 || py<=.01f || py>16 ||
       value(64)!=px || std::abs(double(py)/px-double(width)/height)>1e-5 ||
       std::abs(double(inverseX)*px-1.)>2e-6) return false;
    // Captures 32868 frames147/518/1396 have this full normalized fingerprint.
    // The last twelve padding bytes are uninitialized. No other camera fields
    // are ignored, including the native Y lens, view, near/far and leftEye.
    uint64_t hash=14695981039346656037ull;
    for(size_t i=0;i<308;++i) {
        const bool lens=(i<4) || (i>=64 && i<68) || (i>=240 && i<244);
        hash=(hash^(lens ? uint8_t{} : camera[i]))*1099511628211ull;
    }
    if(hash!=0x41be88f2d87c2b12ull) return false;
    const float result=(py/hud_canvas_detail::Aspect(width,height))/px;
    if(!std::isfinite(result) || result<=0) return false;
    scale=result; return true;
}
bool HudCanvas::Initialize(ID3D11Device* device) {
    if(device_.Get()==device) return true;
    Reset();
    ComPtr<ID3DBlob> code,error;
    if(FAILED(D3DCompile(Shader,sizeof(Shader)-1,nullptr,nullptr,nullptr,"main","gs_5_0",0,0,&code,&error)) ||
       FAILED(device->CreateGeometryShader(code->GetBufferPointer(),code->GetBufferSize(),nullptr,&simple_))) return false;
    code.Reset(); error.Reset();
    const D3D_SHADER_MACRO macros[]={{"TEXT_VERTEX","1"},{nullptr,nullptr}};
    if(FAILED(D3DCompile(Shader,sizeof(Shader)-1,nullptr,macros,nullptr,"main","gs_5_0",0,0,&code,&error)) ||
       FAILED(device->CreateGeometryShader(code->GetBufferPointer(),code->GetBufferSize(),nullptr,&text_))) return false;
    D3D11_BUFFER_DESC desc{}; desc.ByteWidth=16; desc.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
    if(FAILED(device->CreateBuffer(&desc,nullptr,&parameters_))) return false;
    device_=device; return true;
}
HudCanvas::Result HudCanvas::Apply(ID3D11DeviceContext* context,uint64_t vertexHash,uint64_t pixelHash,
                                  std::span<const uint8_t> camera,unsigned width,unsigned height) noexcept {
    if(owner_) return Result::Failed;
    float scale{};
    if(!context || context->GetType()!=D3D11_DEVICE_CONTEXT_IMMEDIATE ||
       (vertexHash!=ColourVertex && vertexHash!=ImageVertex && vertexHash!=TextVertex) ||
       !hud_canvas_detail::CameraScale(camera,width,height,scale)) return Result::Unchanged;
    try {
        ComPtr<ID3D11GeometryShader> geometry; context->GSGetShader(&geometry,nullptr,nullptr);
        ComPtr<ID3D11HullShader> hull; context->HSGetShader(&hull,nullptr,nullptr);
        ComPtr<ID3D11DomainShader> domain; context->DSGetShader(&domain,nullptr,nullptr);
        if(geometry || hull || domain) return Result::Unchanged;
        std::array<ID3D11Buffer*,D3D11_SO_BUFFER_SLOT_COUNT> streams{};
        context->SOGetTargets(unsigned(streams.size()),streams.data());
        bool streaming{};
        for(auto* stream:streams) if(stream) { streaming=true; stream->Release(); }
        if(streaming) return Result::Unchanged;
        D3D11_PRIMITIVE_TOPOLOGY topology{}; context->IAGetPrimitiveTopology(&topology);
        if(topology!=D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST && topology!=D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP) return Result::Unchanged;
        std::array<D3D11_VIEWPORT,D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE> views{};
        UINT count=unsigned(views.size()); context->RSGetViewports(&count,views.data());
        if(count!=1 || views[0].TopLeftX!=0 || views[0].TopLeftY!=0 || views[0].Width!=float(width) || views[0].Height!=float(height)) return Result::Unchanged;
        ComPtr<ID3D11DepthStencilView> depth; context->OMGetRenderTargets(0,nullptr,&depth);
        if(depth) {
            ComPtr<ID3D11DepthStencilState> state; UINT stencil{}; context->OMGetDepthStencilState(&state,&stencil);
            D3D11_DEPTH_STENCIL_DESC desc{}; if(state) state->GetDesc(&desc);
            if(!state || desc.DepthEnable) return Result::Unchanged;
        }
        ComPtr<ID3D11DeviceContext1> context1;
        if(FAILED(context->QueryInterface(IID_PPV_ARGS(&context1)))) return Result::Unchanged;
        ComPtr<ID3D11Device> device; context->GetDevice(&device);
        if(!Initialize(device.Get())) { Reset(); return Result::Failed; }
        const float parameters[4]{scale,vertexHash==ColourVertex && pixelHash==ColourPixel ? 1.f : 0.f,0,0};
        context->UpdateSubresource(parameters_.Get(),0,nullptr,parameters,0,0);
        context1->GSGetConstantBuffers1(0,1,&previousBuffer_,&previousFirst_,&previousCount_);
        owner_=context; activeContext_=context1;
        auto* buffer=parameters_.Get(); context->GSSetConstantBuffers(0,1,&buffer);
        context->GSSetShader(vertexHash==TextVertex ? text_.Get() : simple_.Get(),nullptr,0);
        return Result::Applied;
    } catch(...) { Restore(context); return Result::Failed; }
}
void HudCanvas::Restore(ID3D11DeviceContext* context) noexcept {
    if(!owner_ || owner_!=context) return;
    if(viewportFit_) {
        context->RSSetViewports(previousViewCount_,previousViews_.data());
        context->RSSetScissorRects(previousScissorCount_,previousScissors_.data());
        viewportFit_=false; owner_=nullptr; return;
    }
    context->GSSetShader(nullptr,nullptr,0);
    auto* previous=previousBuffer_.Get();
    activeContext_->GSSetConstantBuffers1(0,1,&previous,&previousFirst_,&previousCount_);
    previousBuffer_.Reset(); activeContext_.Reset(); owner_=nullptr;
}
HudCanvas::Result HudCanvas::FitNative(ID3D11DeviceContext* context,unsigned width,unsigned height) noexcept {
    if(owner_) return Result::Failed;
    if(!context || context->GetType()!=D3D11_DEVICE_CONTEXT_IMMEDIATE || !width || !height || width>4096 || height>4096)
        return Result::Unchanged;
    const float factor=float(width)/height/hud_canvas_detail::Aspect(width,height);
    previousViewCount_=UINT(previousViews_.size()); context->RSGetViewports(&previousViewCount_,previousViews_.data());
    previousScissorCount_=UINT(previousScissors_.size()); context->RSGetScissorRects(&previousScissorCount_,previousScissors_.data());
    auto views=previousViews_; auto scissors=previousScissors_;
    const auto x=[&](double value) { return (value-double(width)*.5)*factor+double(width)*.5; };
    for(UINT i=0;i<previousViewCount_;++i) {
        if(!std::isfinite(views[i].TopLeftX) || !std::isfinite(views[i].Width) || views[i].Width<0) return Result::Unchanged;
        views[i].TopLeftX=float(x(views[i].TopLeftX)); views[i].Width*=factor;
    }
    for(UINT i=0;i<previousScissorCount_;++i) {
        scissors[i].left=LONG(std::max(double(std::numeric_limits<LONG>::min()),std::floor(x(scissors[i].left))));
        scissors[i].right=LONG(std::min(double(std::numeric_limits<LONG>::max()),std::ceil(x(scissors[i].right))));
    }
    owner_=context; viewportFit_=true;
    context->RSSetViewports(previousViewCount_,views.data());
    context->RSSetScissorRects(previousScissorCount_,scissors.data());
    return Result::Applied;
}
void HudCanvas::Reset() noexcept {
    Restore(owner_); device_.Reset(); simple_.Reset(); text_.Reset(); parameters_.Reset();
}
}
