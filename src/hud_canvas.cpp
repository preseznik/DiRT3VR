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
constexpr uint64_t ImagePixel=0x7605129845e746afull;
constexpr char Shader[]=R"(
// replay: the replay camera's near-black bars at its letterbox line (#dda609d6). logo: a clip-space
// box (min xy, max xy); a triangle wholly inside it is the replay's DiRT logo. An empty box matches nothing.
cbuffer Canvas : register(b0) { float scaleX; float removeBars; float replay; float reserved; float4 logo; };
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
    bool bar=removeBars>0.5,finite=true;
    // The replay's bars are near-black (about 4/255, 0, 1/255) and start at 0.788..0.791.
    float black=replay>0.5 ? 0.03 : 0.0,edge=replay>0.5 ? 0.78 : 0.7901;
    float2 lo=float2(1e20,1e20),hi=float2(-1e20,-1e20);
    [unroll] for(uint i=0;i<3;++i) {
        float4 p=input[i].position;
        finite=finite && all(isfinite(p)) && abs(p.w-1.0)<1e-6;
        bar=bar && all(input[i].t2.rgb>=0.0) && all(input[i].t2.rgb<=black) && input[i].t2.a==1.0;
        float2 xy=p.xy/p.w;
        lo=min(lo,xy); hi=max(hi,xy);
    }
    // At least the native width: the witnessed overscan quads, and the screen-wide bars of some
    // transitions (#7d2ac4d9), which X scaling would otherwise leave floating mid-panel. Each runs
    // from the letterbox line (or between it and the edge while sliding in) out to the edge.
    bool wide=lo.x<=-0.999 && hi.x>=0.999;
    bool top=lo.y>=edge && lo.y<1.0 && hi.y>=0.999;
    bool bottom=hi.y<=-edge && hi.y>-1.0 && lo.y<=-0.999;
    if(finite && bar && wide && (top || bottom)) return;
    if(finite && all(lo>=logo.xy) && all(hi<=logo.zw)) return;
    [unroll] for(uint j=0;j<3;++j) {
        Vertex v=input[j]; v.position.x*=scaleX; output.Append(v);
    }
    output.RestartStrip();
}
)";
}
// The menu camera of captures 32868 frames 147/518/1396 (3072x3264) without its X lens (0, 64)
// and inverse (240): each nonzero float up to 304; every other one is zero, and 304 holds the
// integer 1. The last twelve padding bytes are uninitialized.
struct CameraField { uint16_t offset; float value; };
constexpr CameraField MenuCamera[]={
    {20,.24945055f},{40,-5e-5f},{56,.5f},{60,1},{84,.24945055f},{104,-5e-5f},{120,.50077426f},{124,1},
    {128,1},{148,1},{168,1},{172,-15.485f},{176,1},{196,1},{216,1},{220,15.485f},{232,15.485f},{236,1},
    {260,4.00881f},{280,-20000},{296,10000},{300,1}};
// The replay overlay camera of Lens's captures 72628 frames 5564/11724 (1920x1920, #dda609d6),
// identical in both, likewise without its X lens: a wider orthographic view (1/py 12.76 instead
// of 4.01) placed at 106.53 instead of 15.485, with a few tiny view terms of its own.
constexpr CameraField ReplayFields[]={
    {20,.07836175f},{40,-5e-5f},{56,.5f},{60,1},{84,.07836174f},{104,-5e-5f},{112,4.8225229e-7f},{120,.50532645f},
    {124,.99999994f},{128,.99999994f},{140,6.1541791e-6f},{148,.99999994f},{168,.99999994f},{172,-106.52929f},
    {176,1},{188,-6.1541796e-6f},{196,1},{216,1},{220,106.5293f},{224,-6.1541796e-6f},{232,106.5293f},{236,1},
    {260,12.761328f},{280,-20000},{296,10000},{300,.99999994f}};
// One camera's identity: the X lens (0, 64) and its inverse (240) match the viewport aspect, and
// every other field up to 304 agrees with the table within float rounding; 304 holds the integer 1.
template<size_t N>
bool Identified(std::span<const uint8_t> camera,unsigned width,unsigned height,const CameraField (&fields)[N],float& px,float& py) noexcept {
    if(camera.size()!=320 || !width || !height || width>4096 || height>4096) return false;
    const auto value=[&](size_t offset) { float result; std::memcpy(&result,camera.data()+offset,4); return result; };
    const float lensX=value(0),lensY=value(20),inverseX=value(240);
    if(!std::isfinite(lensX) || !std::isfinite(lensY) || !std::isfinite(inverseX) || lensX<=.01f || lensX>16 || lensY<=.01f || lensY>16 ||
       // The replay camera's view-projection copy of the X lens (64) is an ulp off its own (0).
       std::abs(double(value(64))-lensX)>1e-6*lensX || std::abs(double(lensY)/lensX-double(width)/height)>1e-5 ||
       std::abs(double(inverseX)*lensX-1.)>2e-6) return false;
    // No other camera field is ignored, including the native Y lens, view, near/far and leftEye.
    // Other headset sizes round a few of them by an ulp (3024x3360: py 0.2494506, far 10000
    // instead of 9999.999), so the fields agree within 1e-5 rather than byte for byte (#7d2ac4d9).
    const auto field=[&](size_t offset) {
        for(const auto& known:fields) if(known.offset==offset) return known.value;
        return 0.f;
    };
    for(size_t offset=4;offset<304;offset+=4) {
        if(offset==64 || offset==240) continue;
        const float actual=value(offset),expected=field(offset);
        if(!std::isfinite(actual) || std::abs(double(actual)-expected)>(expected ? 1e-5*std::abs(double(expected)) : 1e-6)) return false;
    }
    uint32_t tail{}; std::memcpy(&tail,camera.data()+304,4);
    if(tail!=1) return false;
    px=lensX; py=lensY; return true;
}
bool hud_canvas_detail::ReplayCamera(std::span<const uint8_t> camera,unsigned width,unsigned height,float& lensX,float& lensY) noexcept {
    float px{},py{};
    if(!Identified(camera,width,height,ReplayFields,px,py)) return false;
    lensX=px; lensY=py; return true;
}
bool hud_canvas_detail::CameraScale(std::span<const uint8_t> camera,unsigned width,unsigned height,float& scale) noexcept {
    float px{},py{};
    if(!Identified(camera,width,height,MenuCamera,px,py)) return false;
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
    D3D11_BUFFER_DESC desc{}; desc.ByteWidth=32; desc.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
    if(FAILED(device->CreateBuffer(&desc,nullptr,&parameters_))) return false;
    device_=device; return true;
}
// The pipeline the canvas GS may join: no native GS/HS/DS or stream output, triangles, one
// full viewport and no effective depth. Applied when it qualifies, else Unchanged.
HudCanvas::Result HudCanvas::Guarded(ID3D11DeviceContext* context,unsigned width,unsigned height) noexcept {
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
        return Result::Applied;
    } catch(...) { return Result::Unchanged; }
}
HudCanvas::Result HudCanvas::Apply(ID3D11DeviceContext* context,uint64_t vertexHash,uint64_t pixelHash,
                                  std::span<const uint8_t> camera,unsigned width,unsigned height) noexcept {
    if(owner_) return Result::Failed;
    float scale{},lensX{},lensY{};
    if(!context || context->GetType()!=D3D11_DEVICE_CONTEXT_IMMEDIATE ||
       (vertexHash!=ColourVertex && vertexHash!=ImageVertex && vertexHash!=TextVertex)) return Result::Unchanged;
    const bool menu=hud_canvas_detail::CameraScale(camera,width,height,scale);
    const bool replay=!menu && hud_canvas_detail::ReplayCamera(camera,width,height,lensX,lensY);
    if((!menu && !replay) || Guarded(context,width,height)!=Result::Applied) return Result::Unchanged;
    try {
        ComPtr<ID3D11DeviceContext1> context1;
        if(FAILED(context->QueryInterface(IID_PPV_ARGS(&context1)))) return Result::Unchanged;
        ComPtr<ID3D11Device> device; context->GetDevice(&device);
        if(!Initialize(device.Get())) { Reset(); return Result::Failed; }
        // The menu camera scales X; the replay camera keeps the native fit (scale 1, viewport below).
        // Its logo sits at about x 7.0..10.9, y 7.0..9.5 in camera units (Lens's draw 11); the box
        // around it is in clip space for this lens, and only image draws can match it.
        float parameters[8]{menu ? scale : 1.f,vertexHash==ColourVertex && pixelHash==ColourPixel ? 1.f : 0.f,replay ? 1.f : 0.f,0,1,1,-1,-1};
        if(replay && vertexHash==ImageVertex && pixelHash==ImagePixel) {
            parameters[4]=6.f*lensX; parameters[5]=6.f*lensY; parameters[6]=12.f*lensX; parameters[7]=10.5f*lensY;
        }
        context->UpdateSubresource(parameters_.Get(),0,nullptr,parameters,0,0);
        context1->GSGetConstantBuffers1(0,1,&previousBuffer_,&previousFirst_,&previousCount_);
        owner_=context; activeContext_=context1;
        auto* buffer=parameters_.Get(); context->GSSetConstantBuffers(0,1,&buffer);
        context->GSSetShader(vertexHash==TextVertex ? text_.Get() : simple_.Get(),nullptr,0);
        if(replay) FitViewports(context,width,float(width)/height/hud_canvas_detail::Aspect(width,height));
        return Result::Applied;
    } catch(...) { Restore(context); return Result::Failed; }
}
void HudCanvas::Restore(ID3D11DeviceContext* context) noexcept {
    if(!owner_ || owner_!=context) return;
    if(viewportFit_) {
        context->RSSetViewports(previousViewCount_,previousViews_.data());
        context->RSSetScissorRects(previousScissorCount_,previousScissors_.data());
        viewportFit_=false;
    }
    if(activeContext_) {
        context->GSSetShader(nullptr,nullptr,0);
        auto* previous=previousBuffer_.Get();
        activeContext_->GSSetConstantBuffers1(0,1,&previous,&previousFirst_,&previousCount_);
        previousBuffer_.Reset(); activeContext_.Reset();
    }
    owner_=nullptr;
}
// Narrows the bound viewports and scissors around the target's centre by factor. The caller has
// checked them, and owns the canvas until Restore.
void HudCanvas::FitViewports(ID3D11DeviceContext* context,unsigned width,float factor) {
    previousViewCount_=UINT(previousViews_.size()); context->RSGetViewports(&previousViewCount_,previousViews_.data());
    previousScissorCount_=UINT(previousScissors_.size()); context->RSGetScissorRects(&previousScissorCount_,previousScissors_.data());
    auto views=previousViews_; auto scissors=previousScissors_;
    const auto x=[&](double value) { return (value-double(width)*.5)*factor+double(width)*.5; };
    for(UINT i=0;i<previousViewCount_;++i) { views[i].TopLeftX=float(x(views[i].TopLeftX)); views[i].Width*=factor; }
    for(UINT i=0;i<previousScissorCount_;++i) {
        scissors[i].left=LONG(std::max(double(std::numeric_limits<LONG>::min()),std::floor(x(scissors[i].left))));
        scissors[i].right=LONG(std::min(double(std::numeric_limits<LONG>::max()),std::ceil(x(scissors[i].right))));
    }
    owner_=context; viewportFit_=true;
    context->RSSetViewports(previousViewCount_,views.data());
    context->RSSetScissorRects(previousScissorCount_,scissors.data());
}
HudCanvas::Result HudCanvas::FitNative(ID3D11DeviceContext* context,unsigned width,unsigned height) noexcept {
    if(owner_) return Result::Failed;
    if(!context || context->GetType()!=D3D11_DEVICE_CONTEXT_IMMEDIATE || !width || !height || width>4096 || height>4096)
        return Result::Unchanged;
    std::array<D3D11_VIEWPORT,D3D11_VIEWPORT_AND_SCISSORRECT_OBJECT_COUNT_PER_PIPELINE> views{};
    UINT count=UINT(views.size()); context->RSGetViewports(&count,views.data());
    for(UINT i=0;i<count;++i)
        if(!std::isfinite(views[i].TopLeftX) || !std::isfinite(views[i].Width) || views[i].Width<0) return Result::Unchanged;
    FitViewports(context,width,float(width)/height/hud_canvas_detail::Aspect(width,height));
    return Result::Applied;
}
void HudCanvas::Reset() noexcept {
    Restore(owner_); device_.Reset(); simple_.Reset(); text_.Reset(); parameters_.Reset();
}
}
