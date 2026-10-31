#include "presentation_policy.h"
#include <wrl/client.h>
#include <d3dcompiler.h>
#include <array>
#include <cstdio>
#include <cstring>
#include <stdexcept>

using namespace dirt3vr;
using Microsoft::WRL::ComPtr;
namespace {
void Check(bool value,const char* message) { if(!value) throw std::runtime_error(message); }
ComPtr<ID3D11Device> device;
ComPtr<ID3D11DeviceContext> context;
ComPtr<ID3D11Texture2D> Texture(unsigned width,unsigned height,DXGI_FORMAT format,UINT binds) {
    D3D11_TEXTURE2D_DESC desc{}; desc.Width=width; desc.Height=height; desc.Format=format; desc.BindFlags=binds;
    desc.MipLevels=desc.ArraySize=desc.SampleDesc.Count=1;
    ComPtr<ID3D11Texture2D> value; Check(SUCCEEDED(device->CreateTexture2D(&desc,nullptr,&value)),"texture allocation"); return value;
}
ComPtr<ID3D11ShaderResourceView> Source(ID3D11Texture2D* texture) {
    ComPtr<ID3D11ShaderResourceView> value; Check(SUCCEEDED(device->CreateShaderResourceView(texture,nullptr,&value)),"source view"); return value;
}
ComPtr<ID3D11RenderTargetView> Target(ID3D11Texture2D* texture) {
    ComPtr<ID3D11RenderTargetView> value; Check(SUCCEEDED(device->CreateRenderTargetView(texture,nullptr,&value)),"target view"); return value;
}
ComPtr<ID3DBlob> Compile(const char* source,const char* target) {
    ComPtr<ID3DBlob> code,error;
    Check(SUCCEEDED(D3DCompile(source,strlen(source),nullptr,nullptr,nullptr,"main",target,0,0,&code,&error)),"fixture shader compilation"); return code;
}
ULONG References(IUnknown* value) { auto result=value->AddRef(); value->Release(); return result; }
struct Fixture {
    ComPtr<ID3D11Texture2D> back,depthTexture,screen;
    ComPtr<ID3D11RenderTargetView> target;
    ComPtr<ID3D11DepthStencilView> depth;
    ComPtr<ID3D11VertexShader> vertex;
    ComPtr<ID3D11PixelShader> pixel;
    ComPtr<ID3D11RasterizerState> raster;
    ComPtr<ID3D11BlendState> blend;
    ComPtr<ID3D11DepthStencilState> depthState;
    std::array<ComPtr<ID3D11Texture2D>,3> yuv;
    std::array<ComPtr<ID3D11ShaderResourceView>,3> yuvViews;
    ComPtr<ID3D11ShaderResourceView> screenView;
    D3D11_DEPTH_STENCIL_DESC depthDesc{};
    D3D11_BLEND_DESC blendDesc{};
    D3D11_RASTERIZER_DESC rasterDesc{};
    Fixture() {
        back=Texture(320,340,DXGI_FORMAT_R10G10B10A2_UNORM,D3D11_BIND_RENDER_TARGET); target=Target(back.Get());
        depthTexture=Texture(320,340,DXGI_FORMAT_D24_UNORM_S8_UINT,D3D11_BIND_DEPTH_STENCIL);
        Check(SUCCEEDED(device->CreateDepthStencilView(depthTexture.Get(),nullptr,&depth)),"depth view");
        screen=Texture(320,340,DXGI_FORMAT_R10G10B10A2_UNORM,D3D11_BIND_SHADER_RESOURCE); screenView=Source(screen.Get());
        for(unsigned i=0;i<3;++i) { yuv[i]=Texture(i?128:256,i?64:128,DXGI_FORMAT_R8_UNORM,D3D11_BIND_SHADER_RESOURCE); yuvViews[i]=Source(yuv[i].Get()); }
        auto vs=Compile("float4 main(uint id:SV_VertexID):SV_Position { return float4((id&1)?1:-1,(id&2)?1:-1,0,1); }","vs_5_0");
        auto ps=Compile("float4 main():SV_Target { return float4(1,0,0,1); }","ps_5_0");
        Check(SUCCEEDED(device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&vertex)) &&
            SUCCEEDED(device->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&pixel)),"fixture shaders");
        Reset();
    }
    void Depth(UINT reference=0) {
        depthState.Reset(); Check(SUCCEEDED(device->CreateDepthStencilState(&depthDesc,&depthState)),"depth state");
        context->OMSetDepthStencilState(depthState.Get(),reference);
    }
    void Blend() {
        blend.Reset(); Check(SUCCEEDED(device->CreateBlendState(&blendDesc,&blend)),"blend state"); context->OMSetBlendState(blend.Get(),nullptr,0xffffffffu);
    }
    void Raster() {
        raster.Reset(); Check(SUCCEEDED(device->CreateRasterizerState(&rasterDesc,&raster)),"raster state"); context->RSSetState(raster.Get());
    }
    void Reset() {
        context->ClearState(); context->OMSetRenderTargets(1,target.GetAddressOf(),depth.Get());
        context->VSSetShader(vertex.Get(),nullptr,0); context->PSSetShader(pixel.Get(),nullptr,0);
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLESTRIP);
        D3D11_VIEWPORT viewport{0,0,320,340,0,1}; context->RSSetViewports(1,&viewport);
        rasterDesc={}; rasterDesc.FillMode=D3D11_FILL_SOLID; rasterDesc.CullMode=D3D11_CULL_NONE; rasterDesc.DepthClipEnable=TRUE; rasterDesc.MultisampleEnable=TRUE; Raster();
        depthDesc={}; depthDesc.DepthWriteMask=D3D11_DEPTH_WRITE_MASK_ALL; depthDesc.DepthFunc=D3D11_COMPARISON_LESS;
        depthDesc.StencilReadMask=depthDesc.StencilWriteMask=255;
        depthDesc.FrontFace={D3D11_STENCIL_OP_KEEP,D3D11_STENCIL_OP_KEEP,D3D11_STENCIL_OP_KEEP,D3D11_COMPARISON_ALWAYS};
        depthDesc.BackFace=depthDesc.FrontFace; Depth();
        blendDesc={}; auto& b=blendDesc.RenderTarget[0]; b.BlendEnable=TRUE;
        b.SrcBlend=b.SrcBlendAlpha=D3D11_BLEND_SRC_ALPHA; b.DestBlend=b.DestBlendAlpha=D3D11_BLEND_INV_SRC_ALPHA;
        b.BlendOp=b.BlendOpAlpha=D3D11_BLEND_OP_ADD; b.RenderTargetWriteMask=D3D11_COLOR_WRITE_ENABLE_ALL; Blend();
    }
    void Video() {
        ID3D11ShaderResourceView* raw[]{yuvViews[0].Get(),yuvViews[1].Get(),yuvViews[2].Get()}; context->PSSetShaderResources(0,3,raw);
    }
    void Transition() { context->PSSetShaderResources(0,1,screenView.GetAddressOf()); }
    bool IsVideo(uint64_t vs=IntroVideoVertexHash,uint64_t ps=IntroVideoPixelHash,unsigned count=4,unsigned instances=1) {
        return IsIntroVideoDraw(context.Get(),back.Get(),vs,ps,count,instances);
    }
    bool IsTransition(uint64_t vs=StereoTransitionVertexHash,uint64_t ps=StereoTransitionPixelHash,unsigned count=4,unsigned instances=1) {
        return IsStereoTransitionDraw(context.Get(),back.Get(),vs,ps,count,instances);
    }
    void Masked() {
        depthDesc.StencilEnable=TRUE; depthDesc.FrontFace.StencilFunc=depthDesc.BackFace.StencilFunc=D3D11_COMPARISON_EQUAL; Depth(1);
        blendDesc.RenderTarget[0].BlendEnable=FALSE; Blend();
    }
};
void VideoPolicy(Fixture& f) {
    f.Reset(); f.Video(); Check(f.IsVideo(),"captured ordinary intro draw");
    Check(!f.IsVideo(0) && !f.IsVideo(IntroVideoVertexHash,0) && !f.IsVideo(IntroVideoVertexHash,IntroVideoPixelHash,3) &&
        !f.IsVideo(IntroVideoVertexHash,IntroVideoPixelHash,4,2),"video hash and geometry guards");
    f.depthDesc.DepthEnable=TRUE; f.depthDesc.DepthFunc=D3D11_COMPARISON_GREATER_EQUAL; f.Depth();
    f.blendDesc.RenderTarget[0].BlendEnable=FALSE; f.Blend(); Check(f.IsVideo(),"captured first intro frame depth variant");
    f.depthDesc.DepthFunc=D3D11_COMPARISON_LESS; f.Depth(); Check(!f.IsVideo(),"unknown depth test remains native");
    f.Reset(); f.Video();
    auto badTexture=Texture(128,65,DXGI_FORMAT_R8_UNORM,D3D11_BIND_SHADER_RESOURCE); auto badView=Source(badTexture.Get());
    context->PSSetShaderResources(1,1,badView.GetAddressOf()); Check(!f.IsVideo(),"incorrect chroma size refused");
    f.Video(); context->PSSetShaderResources(2,1,f.yuvViews[1].GetAddressOf()); Check(!f.IsVideo(),"aliased chroma planes refused");
    f.Video(); auto wrongTexture=Texture(128,64,DXGI_FORMAT_R8G8_UNORM,D3D11_BIND_SHADER_RESOURCE); auto wrongView=Source(wrongTexture.Get());
    context->PSSetShaderResources(2,1,wrongView.GetAddressOf()); Check(!f.IsVideo(),"wrong chroma format refused");
    f.Video(); ID3D11ShaderResourceView* missing{}; context->PSSetShaderResources(1,1,&missing); Check(!f.IsVideo(),"missing chroma refused");
    f.Video(); f.depthDesc.StencilEnable=TRUE; f.Depth(); Check(!f.IsVideo(),"unverified stencil video refused");
}
void TransitionPolicy(Fixture& f) {
    f.Reset(); f.Transition(); Check(f.IsTransition(),"captured source-alpha transition");
    Check(!f.IsTransition(0) && !f.IsTransition(StereoTransitionVertexHash,0) && !f.IsTransition(StereoTransitionVertexHash,StereoTransitionPixelHash,6) &&
        !f.IsTransition(StereoTransitionVertexHash,StereoTransitionPixelHash,4,2),"transition shader and geometry guards");
    f.depthDesc.DepthEnable=TRUE; f.Depth(); Check(!f.IsTransition(),"depth writing transition refused");
    f.Reset(); f.Transition(); f.blendDesc.RenderTarget[0].DestBlend=D3D11_BLEND_ONE; f.Blend(); Check(!f.IsTransition(),"additive blend refused");
    f.Reset(); f.Transition(); f.Masked(); Check(f.IsTransition(),"captured EQUAL/KEEP masked transition");
    f.depthDesc.FrontFace.StencilPassOp=D3D11_STENCIL_OP_REPLACE; f.Depth(1); Check(!f.IsTransition(),"front stencil write stays native");
    // Depth is disabled, so a driver may canonicalize the unused DepthFailOp.
    // StencilPassOp is active and would mutate the stencil surface on this draw.
    f.depthDesc.FrontFace.StencilPassOp=D3D11_STENCIL_OP_KEEP; f.depthDesc.BackFace.StencilPassOp=D3D11_STENCIL_OP_INCR; f.Depth(1);
    Check(!f.IsTransition(),"back stencil write stays native");
    f.depthDesc.BackFace.StencilPassOp=D3D11_STENCIL_OP_KEEP; f.Depth(2); Check(!f.IsTransition(),"unknown stencil reference refused");
    f.depthDesc.FrontFace.StencilFunc=D3D11_COMPARISON_NOT_EQUAL; f.Depth(1); Check(!f.IsTransition(),"unknown stencil function refused");
    f.Reset(); f.Transition(); f.Masked(); f.blendDesc.RenderTarget[0].BlendEnable=TRUE; f.Blend(); Check(!f.IsTransition(),"unmeasured stencil/alpha combination refused");
    f.Reset(); f.Transition();
    auto reduced=Texture(160,170,DXGI_FORMAT_R10G10B10A2_UNORM,D3D11_BIND_SHADER_RESOURCE); auto view=Source(reduced.Get());
    context->PSSetShaderResources(0,1,view.GetAddressOf()); Check(!f.IsTransition(),"half-size screen texture refused");
    auto wrong=Texture(320,340,DXGI_FORMAT_R8G8B8A8_UNORM,D3D11_BIND_SHADER_RESOURCE); view=Source(wrong.Get());
    context->PSSetShaderResources(0,1,view.GetAddressOf()); Check(!f.IsTransition(),"mismatched screen format refused");
    f.Transition();
}
void SharedGuards(Fixture& f) {
    f.Reset(); f.Video();
    auto foreign=Texture(320,340,DXGI_FORMAT_R10G10B10A2_UNORM,D3D11_BIND_RENDER_TARGET); auto target=Target(foreign.Get());
    Check(!IsIntroVideoDraw(context.Get(),foreign.Get(),IntroVideoVertexHash,IntroVideoPixelHash,4,1),"foreign target identity refused");
    // A stale identity need not point to readable memory: only the live RTV is inspected.
    Check(!IsIntroVideoDraw(context.Get(),reinterpret_cast<ID3D11Texture2D*>(uintptr_t(1)),IntroVideoVertexHash,IntroVideoPixelHash,4,1),"stale borrowed pointer not dereferenced");
    ID3D11RenderTargetView* mrt[]{f.target.Get(),target.Get()}; context->OMSetRenderTargets(2,mrt,f.depth.Get()); Check(!f.IsVideo(),"multiple targets refused");
    f.Reset(); f.Video(); D3D11_VIEWPORT shifted{1,0,320,340,0,1}; context->RSSetViewports(1,&shifted); Check(!f.IsVideo(),"partial viewport refused");
    f.Reset(); f.Video(); f.rasterDesc.ScissorEnable=TRUE; f.Raster(); Check(!f.IsVideo(),"scissored output refused");
    f.Reset(); f.Video(); context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_LINELIST); Check(!f.IsVideo(),"line primitive refused");
    f.Reset(); f.Video(); context->PSSetShader(nullptr,nullptr,0); Check(!f.IsVideo(),"missing pixel shader refused");
    f.Reset(); f.Video();
    D3D11_BUFFER_DESC desc{}; desc.ByteWidth=64; desc.BindFlags=D3D11_BIND_STREAM_OUTPUT;
    ComPtr<ID3D11Buffer> stream; Check(SUCCEEDED(device->CreateBuffer(&desc,nullptr,&stream)),"stream buffer"); UINT offset{};
    context->SOSetTargets(1,stream.GetAddressOf(),&offset); Check(!f.IsVideo(),"stream-output side effects refused");
    f.Reset(); f.Transition();
    auto writable=Texture(16,16,DXGI_FORMAT_R32_UINT,D3D11_BIND_UNORDERED_ACCESS);
    ComPtr<ID3D11UnorderedAccessView> unordered; Check(SUCCEEDED(device->CreateUnorderedAccessView(writable.Get(),nullptr,&unordered)),"unordered view");
    context->OMSetRenderTargetsAndUnorderedAccessViews(1,f.target.GetAddressOf(),f.depth.Get(),1,1,unordered.GetAddressOf(),nullptr);
    Check(!f.IsTransition(),"pixel UAV side effects refused");
    ComPtr<ID3D11DeviceContext> deferred; Check(SUCCEEDED(device->CreateDeferredContext(0,&deferred)),"deferred context");
    Check(!IsIntroVideoDraw(deferred.Get(),f.back.Get(),IntroVideoVertexHash,IntroVideoPixelHash,4,1),"deferred context refused");
    Check(!IsIntroVideoDraw(nullptr,f.back.Get(),IntroVideoVertexHash,IntroVideoPixelHash,4,1),"null context refused");
}
void Preservation(Fixture& f) {
    f.Reset(); f.Transition(); f.Masked();
    const auto backRefs=References(f.back.Get()),sourceRefs=References(f.screen.Get()),depthRefs=References(f.depthTexture.Get());
    for(unsigned i=0;i<50;++i) Check(f.IsTransition(),"repeat classification");
    Check(References(f.back.Get())==backRefs && References(f.screen.Get())==sourceRefs && References(f.depthTexture.Get())==depthRefs,"classifier retains no resources");
    ComPtr<ID3D11RenderTargetView> target; ComPtr<ID3D11DepthStencilView> depth; context->OMGetRenderTargets(1,&target,&depth);
    ComPtr<ID3D11BlendState> blend; FLOAT factors[4]{}; UINT mask{}; context->OMGetBlendState(&blend,factors,&mask);
    ComPtr<ID3D11DepthStencilState> depthState; UINT reference{}; context->OMGetDepthStencilState(&depthState,&reference);
    ComPtr<ID3D11RasterizerState> raster; context->RSGetState(&raster);
    ComPtr<ID3D11VertexShader> vertex; ComPtr<ID3D11PixelShader> pixel; context->VSGetShader(&vertex,nullptr,nullptr); context->PSGetShader(&pixel,nullptr,nullptr);
    ComPtr<ID3D11ShaderResourceView> source; context->PSGetShaderResources(0,1,&source);
    Check(target==f.target && depth==f.depth && blend==f.blend && depthState==f.depthState && raster==f.raster && vertex==f.vertex && pixel==f.pixel &&
        source==f.screenView && mask==0xffffffffu && reference==1,"all graphics bindings preserved");
    D3D11_VIEWPORT viewport{}; UINT count=1; context->RSGetViewports(&count,&viewport);
    Check(count==1 && viewport.Width==320 && viewport.Height==340 && viewport.TopLeftX==0,"viewport preserved");
}
}
int main() {
    try {
        Check(SUCCEEDED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context)),"WARP device");
        Fixture fixture; VideoPolicy(fixture); TransitionPolicy(fixture); SharedGuards(fixture); Preservation(fixture);
        context->ClearState(); puts("presentation shader policy, rejection paths, state and resource lifetime passed"); return 0;
    } catch(const std::exception& error) { fprintf(stderr,"presentation policy test: %s\n",error.what()); return 1; }
}
