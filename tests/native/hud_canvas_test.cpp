#include "hud_canvas.h"
#include "hud_capture.h"
#include <d3dcompiler.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <limits>
#include <stdexcept>
#include <string_view>

using namespace dirt3vr;
using Microsoft::WRL::ComPtr;
namespace {
constexpr uint64_t Colour=0xe057d253ecd4296eull,Image=0x5c571e5a5daa0ce4ull,Text=0xad7b09c4361f558dull;
constexpr uint64_t Solid=0xcf2fe246cd20d277ull;
constexpr unsigned Width=320,Height=340;
void Check(bool value,const char* message) { if(!value) throw std::runtime_error(message); }
std::array<uint8_t,320> Camera() {
    // Captured camera from hud-probe-32868-1396 draw2; its actual lens aspect is
    // 3072/3264 == 320/340. All non-lens bytes belong to DiRT3, not GRID2.
    constexpr std::string_view hex=
        "7cb3873e00000000000000000000000000000000f76f7f3e0000000000000000"
        "000000000000000017b751b80000000000000080000000800000003f0000803f"
        "7cb3873e00000000000000000000000000000000f76f7f3e0000000000000000"
        "000000000000000017b751b8000000000000000000000000be32003f0000803f"
        "0000803f000000000000000000000000000000000000803f0000000000000000"
        "00000000000000000000803f90c277c1ffff7f3f000000000000000000000000"
        "000000000000803f00000000000000000000000000000000ffff7f3f8fc27741"
        "00000000000000008fc277410000803fcb787140000000800000008000000080"
        "000000802c48804000000080000000800000008000000080ff3f9cc600000080"
        "0000008000000080ff3f1c46feff7f3f01000000000000000000000000000000";
    static_assert(hex.size()==640);
    std::array<uint8_t,320> camera{};
    const auto digit=[](char v) { return v<='9' ? v-'0' : v-'a'+10; };
    for(size_t i=0;i<camera.size();++i) camera[i]=uint8_t(digit(hex[2*i])*16+digit(hex[2*i+1]));
    return camera;
}
void Put(std::array<uint8_t,320>& camera,size_t offset,float value) { std::memcpy(camera.data()+offset,&value,4); }
void TestIdentity() {
    const auto observed=Camera(); float scale=-1;
    Check(hud_canvas_detail::CameraScale(observed,3072,3264,scale) && std::abs(scale-9.f/17.f)<1e-6,"Observed DiRT3 camera not recognized");
    Check(hud_canvas_detail::CameraScale(observed,Width,Height,scale),"Resolution-relative camera not recognized");
    for(size_t i=0;i<308;++i) {
        if(i<4 || (i>=64 && i<68) || (i>=240 && i<244)) continue;
        auto altered=observed; altered[i]^=1; scale=-1;
        Check(!hud_canvas_detail::CameraScale(altered,Width,Height,scale) && scale==-1,"Unknown camera field accepted or failure changed output");
    }
    auto padding=observed; for(size_t i=308;i<320;++i) padding[i]^=0xff;
    Check(hud_canvas_detail::CameraScale(padding,Width,Height,scale),"Uninitialized padding became camera identity");
    auto wide=observed; const float px=.24945054948329926f*9.f/16.f;
    Put(wide,0,px); Put(wide,64,px); Put(wide,240,1.f/px);
    Check(hud_canvas_detail::CameraScale(wide,640,360,scale) && std::abs(scale-1)<1e-6,"Native widescreen lens rejected");
    Check(!hud_canvas_detail::CameraScale(observed,640,360,scale),"Viewport/aspect mismatch accepted");
    Put(wide,240,0); Check(!hud_canvas_detail::CameraScale(wide,640,360,scale),"Wrong inverse lens accepted");
    wide=observed; Put(wide,0,std::numeric_limits<float>::quiet_NaN());
    Check(!hud_canvas_detail::CameraScale(wide,Width,Height,scale),"NaN lens accepted");
    Check(!hud_canvas_detail::CameraScale(std::span(observed).first(308),Width,Height,scale),"Truncated camera accepted");
}
ComPtr<ID3D11Device> device;
ComPtr<ID3D11DeviceContext> context;
struct Vertex { float position[4],colour[4]; };
constexpr char Shader[]=R"(
struct Vertex {
 float4 position:SV_Position;
 float4 t0:TEXCOORD0; float4 t1:TEXCOORD1; float4 t2:TEXCOORD2;
#ifdef TEXT_VERTEX
 float4 t3:TEXCOORD3;
#endif
 float4 t4:TEXCOORD4;
};
Vertex vs(float4 position:POSITION,float4 colour:COLOR0) {
 Vertex v; v.position=position; v.t0=float4(.2,.3,.4,.5);
 v.t1=float4(.3,.4,.5,.6); v.t2=colour; v.t4=float4(.4,.5,.6,.7);
#ifdef TEXT_VERTEX
 v.t3=float4(.5,.6,.7,.8);
#endif
 return v;
}
float4 colour(Vertex v):SV_Target { return v.t2; }
float4 varyings(Vertex v):SV_Target {
#ifdef TEXT_VERTEX
 return float4(v.t0.x,v.t1.y,v.t3.z,1);
#else
 return float4(v.t0.x,v.t1.y,v.t4.z,1);
#endif
}
[maxvertexcount(3)]
void gs(triangle Vertex input[3],inout TriangleStream<Vertex> output) {
 [unroll]for(uint i=0;i<3;++i)output.Append(input[i]); output.RestartStrip();
}
)";
ComPtr<ID3DBlob> Compile(const char* entry,const char* target,bool text=false) {
    const D3D_SHADER_MACRO macros[]={{"TEXT_VERTEX","1"},{nullptr,nullptr}};
    ComPtr<ID3DBlob> code,error;
    const auto result=D3DCompile(Shader,sizeof(Shader)-1,nullptr,text ? macros : nullptr,nullptr,entry,target,0,0,&code,&error);
    if(FAILED(result) && error) std::puts(static_cast<const char*>(error->GetBufferPointer()));
    Check(SUCCEEDED(result),"Fixture shader compilation"); return code;
}
std::array<uint8_t,4> Pixel(ID3D11Texture2D* texture,unsigned x,unsigned y) {
    D3D11_TEXTURE2D_DESC desc{}; texture->GetDesc(&desc); desc.BindFlags=0;
    desc.Usage=D3D11_USAGE_STAGING; desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> copy; Check(SUCCEEDED(device->CreateTexture2D(&desc,nullptr,&copy)),"Readback texture");
    context->CopyResource(copy.Get(),texture); D3D11_MAPPED_SUBRESOURCE map{};
    Check(SUCCEEDED(context->Map(copy.Get(),0,D3D11_MAP_READ,0,&map)),"Readback map");
    std::array<uint8_t,4> pixel{}; std::memcpy(pixel.data(),static_cast<uint8_t*>(map.pData)+size_t(y)*map.RowPitch+x*4,4);
    context->Unmap(copy.Get(),0); return pixel;
}
void Quad(float left,float right,float bottom,float top,float alpha=1,float blue=0,float w=1) {
    const std::array<Vertex,6> vertices{{
        {{left,bottom,0,w},{0,0,blue,alpha}},{{left,top,0,w},{0,0,blue,alpha}},{{right,top,0,w},{0,0,blue,alpha}},
        {{left,bottom,0,w},{0,0,blue,alpha}},{{right,top,0,w},{0,0,blue,alpha}},{{right,bottom,0,w},{0,0,blue,alpha}}
    }};
    D3D11_BUFFER_DESC desc{}; desc.ByteWidth=sizeof(vertices); desc.BindFlags=D3D11_BIND_VERTEX_BUFFER;
    const D3D11_SUBRESOURCE_DATA data{vertices.data(),0,0}; ComPtr<ID3D11Buffer> buffer;
    Check(SUCCEEDED(device->CreateBuffer(&desc,&data,&buffer)),"Vertex buffer");
    auto* raw=buffer.Get(); UINT stride=sizeof(Vertex),offset=0;
    context->IASetVertexBuffers(0,1,&raw,&stride,&offset);
}
void TestGraphics() {
    Check(SUCCEEDED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context)),"WARP unavailable");
    ComPtr<ID3D11DeviceContext1> context1; Check(SUCCEEDED(context.As(&context1)),"D3D11.1 unavailable");
    auto vs=Compile("vs","vs_5_0"),textVs=Compile("vs","vs_5_0",true);
    auto ps=Compile("colour","ps_5_0"),varying=Compile("varyings","ps_5_0"),textPs=Compile("varyings","ps_5_0",true);
    ComPtr<ID3D11VertexShader> vertex,textVertex; ComPtr<ID3D11PixelShader> pixel,varyingPixel,textPixel;
    Check(SUCCEEDED(device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&vertex)) &&
          SUCCEEDED(device->CreateVertexShader(textVs->GetBufferPointer(),textVs->GetBufferSize(),nullptr,&textVertex)) &&
          SUCCEEDED(device->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&pixel)) &&
          SUCCEEDED(device->CreatePixelShader(varying->GetBufferPointer(),varying->GetBufferSize(),nullptr,&varyingPixel)) &&
          SUCCEEDED(device->CreatePixelShader(textPs->GetBufferPointer(),textPs->GetBufferSize(),nullptr,&textPixel)),"Fixture shaders");
    const D3D11_INPUT_ELEMENT_DESC elements[]={{"POSITION",0,DXGI_FORMAT_R32G32B32A32_FLOAT,0,0,D3D11_INPUT_PER_VERTEX_DATA,0},
                                             {"COLOR",0,DXGI_FORMAT_R32G32B32A32_FLOAT,0,16,D3D11_INPUT_PER_VERTEX_DATA,0}};
    ComPtr<ID3D11InputLayout> layout; Check(SUCCEEDED(device->CreateInputLayout(elements,2,vs->GetBufferPointer(),vs->GetBufferSize(),&layout)),"Fixture layout");
    D3D11_TEXTURE2D_DESC td{}; td.Width=Width; td.Height=Height; td.MipLevels=td.ArraySize=td.SampleDesc.Count=1;
    td.Format=DXGI_FORMAT_R8G8B8A8_UNORM; td.BindFlags=D3D11_BIND_RENDER_TARGET;
    ComPtr<ID3D11Texture2D> image; ComPtr<ID3D11RenderTargetView> target;
    Check(SUCCEEDED(device->CreateTexture2D(&td,nullptr,&image)) && SUCCEEDED(device->CreateRenderTargetView(image.Get(),nullptr,&target)),"Fixture target");
    auto* raw=target.Get(); context->OMSetRenderTargets(1,&raw,nullptr);
    context->IASetInputLayout(layout.Get()); context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    context->VSSetShader(vertex.Get(),nullptr,0); context->PSSetShader(pixel.Get(),nullptr,0);
    D3D11_RASTERIZER_DESC rd{}; rd.FillMode=D3D11_FILL_SOLID; rd.CullMode=D3D11_CULL_NONE; rd.DepthClipEnable=TRUE;
    ComPtr<ID3D11RasterizerState> raster; Check(SUCCEEDED(device->CreateRasterizerState(&rd,&raster)),"Fixture raster"); context->RSSetState(raster.Get());
    const D3D11_VIEWPORT viewport{0,0,float(Width),float(Height),0,1}; context->RSSetViewports(1,&viewport);
    const float clear[]{.1f,.8f,.3f,1}; const auto reset=[&] { context->ClearRenderTargetView(target.Get(),clear); };
    const auto camera=Camera(); HudCanvas canvas;
    const auto draw=[&](uint64_t hash=Colour,uint64_t pixelHash=Solid) {
        Check(canvas.Apply(context.Get(),hash,pixelHash,camera,Width,Height)==HudCanvas::Result::Applied,"Canvas correction not applied");
        context->Draw(6,0); canvas.Restore(context.Get());
    };
    // Every source vertex is outside the right clip plane; only post-VS X
    // scaling before clipping can recover this menu element.
    reset(); Quad(1.1f,1.6f,-.2f,.2f); context->Draw(6,0);
    Check(Pixel(image.Get(),272,170)[1]>200,"Control geometry was not fully clipped");
    draw(); Check(Pixel(image.Get(),272,170)[1]==0,"Formerly clipped menu geometry not recovered");
    for(bool text:{false,true}) {
        reset(); Quad(-.5f,.5f,-.5f,.5f);
        context->VSSetShader(text ? textVertex.Get() : vertex.Get(),nullptr,0);
        context->PSSetShader(text ? textPixel.Get() : varyingPixel.Get(),nullptr,0);
        draw(text ? Text : Image,0); const auto sampled=Pixel(image.Get(),160,170);
        Check(sampled[0]==51 && sampled[1]==102 && std::abs(int(sampled[2])-(text ? 179 : 153))<=1,"Artwork varying changed across GS");
    }
    context->VSSetShader(vertex.Get(),nullptr,0); context->PSSetShader(pixel.Get(),nullptr,0);
    for(bool top:{false,true}) {
        reset(); Quad(-10.6016f,10.6016f,top ? .7902f : -3.7836f,top ? 3.7723f : -.7902f);
        draw(); Check(Pixel(image.Get(),160,top ? 10 : 330)[1]>200,"Witnessed letterbox was not culled");
        // #7d2ac4d9: the screen-wide bars of transitions, at the letterbox line and sliding in.
        reset(); Quad(-1,1,top ? .7902f : -1.f,top ? 1.f : -.7902f);
        draw(); Check(Pixel(image.Get(),160,top ? 10 : 330)[1]>200,"Screen-wide letterbox was not culled");
        reset(); Quad(-1,1,top ? .9f : -1.f,top ? 1.f : -.9f);
        draw(); Check(Pixel(image.Get(),160,top ? 5 : 335)[1]>200,"Sliding letterbox was not culled");
    }
    // Black bars narrower than the screen, or reaching below the letterbox line, are not letterbox.
    reset(); Quad(-.9f,.9f,.7902f,1); draw(); Check(Pixel(image.Get(),160,10)[1]==0,"Narrow black top bar culled");
    reset(); Quad(-1,1,.5f,1); draw(); Check(Pixel(image.Get(),160,10)[1]==0,"Tall black top panel culled");
    // Preserve ordinary black menu panels, fullscreen fades, translucent and
    // coloured bars, other pixel shaders, and unverified homogeneous W.
    reset(); Quad(-.5f,.5f,-.4f,.4f); draw(); Check(Pixel(image.Get(),160,170)[1]==0,"Black menu panel culled");
    reset(); Quad(-10.6f,10.6f,-3.8f,3.8f); draw(); Check(Pixel(image.Get(),160,10)[1]==0,"Fullscreen black fade culled");
    reset(); Quad(-10.6f,10.6f,.7902f,3.8f,.5f); draw(); Check(Pixel(image.Get(),160,10)[3]>=127 && Pixel(image.Get(),160,10)[3]<=128,"Translucent black culled");
    reset(); Quad(-10.6f,10.6f,.7902f,3.8f,1,1); draw(); Check(Pixel(image.Get(),160,10)[2]==255,"Coloured border culled");
    reset(); Quad(-10.6f,10.6f,.7902f,3.8f); draw(Colour,0); Check(Pixel(image.Get(),160,10)[1]==0,"Unknown pixel shader culled");
    reset(); Quad(-21.2f,21.2f,1.5804f,7.6f,1,0,2); draw(); Check(Pixel(image.Get(),160,10)[1]==0,"Unwitnessed W geometry culled");
    // Preserve both the original CB object and its D3D11.1 range.
    D3D11_BUFFER_DESC bd{}; bd.ByteWidth=1024; bd.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
    ComPtr<ID3D11Buffer> original; Check(SUCCEEDED(device->CreateBuffer(&bd,nullptr,&original)),"Original GS CB");
    auto* originalRaw=original.Get(); UINT first=16,count=16; context1->GSSetConstantBuffers1(0,1,&originalRaw,&first,&count);
    Check(canvas.Apply(context.Get(),Colour,Solid,camera,Width,Height)==HudCanvas::Result::Applied,"State fixture apply");
    Check(canvas.Apply(context.Get(),Colour,Solid,camera,Width,Height)==HudCanvas::Result::Failed,"Nested canvas overwrote saved bindings");
    canvas.Reset(); ComPtr<ID3D11Buffer> restored; UINT restoredFirst{},restoredCount{};
    context1->GSGetConstantBuffers1(0,1,&restored,&restoredFirst,&restoredCount);
    ComPtr<ID3D11GeometryShader> geometry; context->GSGetShader(&geometry,nullptr,nullptr);
    Check(!geometry && restored.Get()==original.Get() && restoredFirst==first && restoredCount==count,"GS/CB range not restored");
    Check(canvas.Apply(context.Get(),1,Solid,camera,Width,Height)==HudCanvas::Result::Unchanged,"Unknown shader modified");
    auto altered=camera; altered[176]^=1; Check(canvas.Apply(context.Get(),Colour,Solid,altered,Width,Height)==HudCanvas::Result::Unchanged,"World camera modified");
    auto gs=Compile("gs","gs_5_0"); Check(SUCCEEDED(device->CreateGeometryShader(gs->GetBufferPointer(),gs->GetBufferSize(),nullptr,&geometry)),"Existing GS");
    context->GSSetShader(geometry.Get(),nullptr,0);
    Check(canvas.Apply(context.Get(),Colour,Solid,camera,Width,Height)==HudCanvas::Result::Unchanged,"Active native GS replaced");
    context->GSSetShader(nullptr,nullptr,0);
    bd.ByteWidth=64; bd.BindFlags=D3D11_BIND_STREAM_OUTPUT; ComPtr<ID3D11Buffer> stream;
    Check(SUCCEEDED(device->CreateBuffer(&bd,nullptr,&stream)),"SO buffer"); auto* streamRaw=stream.Get(); UINT streamOffset=0;
    context->SOSetTargets(1,&streamRaw,&streamOffset);
    Check(canvas.Apply(context.Get(),Colour,Solid,camera,Width,Height)==HudCanvas::Result::Unchanged,"Active SO pipeline changed");
    context->SOSetTargets(0,nullptr,nullptr);
    td.Format=DXGI_FORMAT_D24_UNORM_S8_UINT; td.BindFlags=D3D11_BIND_DEPTH_STENCIL;
    ComPtr<ID3D11Texture2D> depth; ComPtr<ID3D11DepthStencilView> depthView;
    Check(SUCCEEDED(device->CreateTexture2D(&td,nullptr,&depth)) && SUCCEEDED(device->CreateDepthStencilView(depth.Get(),nullptr,&depthView)),"Depth fixture");
    context->OMSetRenderTargets(1,&raw,depthView.Get());
    Check(canvas.Apply(context.Get(),Colour,Solid,camera,Width,Height)==HudCanvas::Result::Unchanged,"Effective depth pipeline changed");
    context->OMSetRenderTargets(1,&raw,nullptr);
    auto shifted=viewport; shifted.TopLeftX=1; context->RSSetViewports(1,&shifted);
    Check(canvas.Apply(context.Get(),Colour,Solid,camera,Width,Height)==HudCanvas::Result::Unchanged,"Shifted viewport changed");
    // Exercise the actual HudCapture caller: CPU camera metadata selects the
    // corrected canvas, then all draw exits restore GS and output state.
    context->RSSetViewports(1,&viewport);
    bd.ByteWidth=320; bd.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
    const D3D11_SUBRESOURCE_DATA cameraData{camera.data(),0,0}; ComPtr<ID3D11Buffer> cameraBuffer;
    HudCameraShadow cameraShadow; cameraShadow.generation=HudCameraGeneration.load(); cameraShadow.bytes=camera;
    Check(SUCCEEDED(device->CreateBuffer(&bd,&cameraData,&cameraBuffer)) &&
          SUCCEEDED(cameraBuffer->SetPrivateData(HudCameraTag,sizeof(cameraShadow),&cameraShadow)),"Tagged camera fixture");
    auto* cameraRaw=cameraBuffer.Get(); context->VSSetConstantBuffers(3,1,&cameraRaw);
    D3D11_BLEND_DESC blendDesc{}; auto& blendRt=blendDesc.RenderTarget[0]; blendRt.BlendEnable=TRUE;
    blendRt.SrcBlend=D3D11_BLEND_SRC_ALPHA; blendRt.DestBlend=D3D11_BLEND_INV_SRC_ALPHA; blendRt.BlendOp=D3D11_BLEND_OP_ADD;
    blendRt.SrcBlendAlpha=D3D11_BLEND_ONE; blendRt.DestBlendAlpha=D3D11_BLEND_ZERO; blendRt.BlendOpAlpha=D3D11_BLEND_OP_ADD;
    blendRt.RenderTargetWriteMask=D3D11_COLOR_WRITE_ENABLE_ALL;
    ComPtr<ID3D11BlendState> originalBlend; Check(SUCCEEDED(device->CreateBlendState(&blendDesc,&originalBlend)),"Capture blend fixture");
    const float factors[]{.1f,.2f,.3f,.4f}; context->OMSetBlendState(originalBlend.Get(),factors,0xffffffff);
    context->VSSetShader(vertex.Get(),nullptr,0); context->PSSetShader(pixel.Get(),nullptr,0);
    reset(); Quad(1.1f,1.6f,-.2f,.2f,1,1); HudCapture hud; unsigned captured{};
    const auto capturedDraw=[&] {
        ComPtr<ID3D11GeometryShader> actual; context->GSGetShader(&actual,nullptr,nullptr);
        Check(bool(actual),"Capture draw did not bind canvas GS"); ++captured; context->Draw(6,0);
    };
    Check(hud.Begin(image.Get(),30) && hud.Draw(context.Get(),capturedDraw,HudShaderKind::Shared,true,Colour,Solid),"Qualified HUD draw not captured");
    hud.End(); Check(captured==1 && hud.CanvasDraws()==1 && std::abs(hud.Aspect()-16.f/9.f)<1e-6f,"Capture did not report widescreen canvas");
    float correctedScale{}; Check(hud_canvas_detail::CameraScale(camera,Width,Height,correctedScale),"Physical scale fixture");
    const float sourceAspect=float(Width)/Height,oldEffectiveWidth=std::min(1.6f,1.8f*sourceAspect);
    Check(std::abs(hud.PanelWidth()*correctedScale-oldEffectiveWidth)<1e-6f &&
          std::abs(hud.PanelWidth()/hud.Aspect()-oldEffectiveWidth/sourceAspect)<1e-6f,"Canvas changed accepted physical HUD size/placement");
    Check(hud.Current(30) && Pixel(hud.Current(30),272,170)[2]==255 && Pixel(image.Get(),272,170)[1]>200,"HUD canvas did not separate corrected UI from world");
    Check(hud.Begin(image.Get(),31),"Exception capture begin"); bool caught{};
    try { hud.Draw(context.Get(),[]{throw std::runtime_error("fixture draw failure");},HudShaderKind::Shared,true,Colour,Solid); }
    catch(const std::runtime_error&) { caught=true; }
    ComPtr<ID3D11RenderTargetView> restoredTarget; ComPtr<ID3D11BlendState> restoredBlend; float restoredFactors[4]{}; UINT restoredMask{};
    context->OMGetRenderTargets(1,&restoredTarget,nullptr); context->OMGetBlendState(&restoredBlend,restoredFactors,&restoredMask);
    geometry.Reset(); context->GSGetShader(&geometry,nullptr,nullptr);
    Check(caught && hud.Failed() && !geometry && restoredTarget.Get()==target.Get() && restoredBlend.Get()==originalBlend.Get() &&
          !std::memcmp(factors,restoredFactors,sizeof(factors)) && restoredMask==0xffffffff,"HUD exception leaked GS/target/blend state");
    hud.End(); Check(!hud.Current(31),"Failed canvas exposed partial capture");
    Check(SUCCEEDED(cameraBuffer->SetPrivateData(HudCameraTag,0,nullptr)),"Remove camera metadata");
    Check(hud.Begin(image.Get(),32),"Unknown-camera capture begin");
    Check(hud.Draw(context.Get(),[&] {
        ComPtr<ID3D11GeometryShader> actual; context->GSGetShader(&actual,nullptr,nullptr);
        Check(!actual,"Unknown camera inherited previous GS"); context->Draw(6,0);
    },HudShaderKind::Shared,true,Colour,Solid),"Unknown camera lost ordinary HUD capture");
    hud.End(); Check(hud.CanvasDraws()==0 && std::abs(hud.Aspect()-16.f/9.f)<1e-6f,"Native HUD did not use shared canvas");
    Check(std::abs(hud.PanelWidth()*correctedScale-oldEffectiveWidth)<1e-6f,"Fitted native HUD changed physical width");
    Check(SUCCEEDED(cameraBuffer->SetPrivateData(HudCameraTag,sizeof(cameraShadow),&cameraShadow)),"Restore camera metadata");
    HudCameraGeneration.fetch_add(1);
    Check(hud.Begin(image.Get(),33),"Stale-generation capture begin");
    Check(hud.Draw(context.Get(),[&] {
        ComPtr<ID3D11GeometryShader> actual; context->GSGetShader(&actual,nullptr,nullptr);
        Check(!actual,"Stale camera generation selected canvas GS"); context->Draw(6,0);
    },HudShaderKind::Shared,true,Colour,Solid),"Stale shadow lost ordinary HUD capture");
    hud.End(); Check(hud.CanvasDraws()==0 && std::abs(hud.PanelWidth()*correctedScale-oldEffectiveWidth)<1e-6f,"Stale shadow changed native HUD physical scale");
    // A native 40x40-pixel instrument and a corrected menu element coexist in
    // one frame. The instrument must remain square after the shared XR/mirror
    // canvas is displayed at its declared aspect, regardless of draw ordering.
    cameraShadow.generation=HudCameraGeneration.load();
    Check(SUCCEEDED(cameraBuffer->SetPrivateData(HudCameraTag,sizeof(cameraShadow),&cameraShadow)),"Refresh camera metadata");
    const D3D11_RECT originalScissor{0,0,LONG(Width),LONG(Height)}; context->RSSetScissorRects(1,&originalScissor);
    for(bool menuFirst:{false,true}) {
        Check(hud.Begin(image.Get(),35),"Mixed canvas begin");
        const auto native=[&] {
            Quad(-40.f/Width,40.f/Width,-40.f/Height,40.f/Height,1,1);
            Check(hud.Draw(context.Get(),[&] {
                D3D11_VIEWPORT fitted{}; UINT n=1; context->RSGetViewports(&n,&fitted);
                Check(std::abs(fitted.Width-float(Width)*correctedScale)<.001f,"Native HUD viewport not fitted");
                context->Draw(6,0);
            },HudShaderKind::TwoDimensional,true),"Native HUD capture in mixed frame");
        };
        const auto menu=[&] {
            Quad(1.1f,1.6f,-.2f,.2f,1,1);
            Check(hud.Draw(context.Get(),capturedDraw,HudShaderKind::Shared,true,Colour,Solid),"Menu capture in mixed frame");
        };
        if(menuFirst) { menu(); native(); } else { native(); menu(); }
        hud.End(); auto* mixed=hud.Current(35);
        Check(mixed && hud.Draws()==2 && hud.CanvasDraws()==1,"Mixed capture lost either coordinate system");
        Check(Pixel(mixed,149,170)[3]==255 && Pixel(mixed,170,170)[3]==255 && Pixel(mixed,148,170)[3]==0 &&
            Pixel(mixed,171,170)[3]==0 && Pixel(mixed,160,150)[3]==255 && Pixel(mixed,160,189)[3]==255 &&
            Pixel(mixed,160,149)[3]==0 && Pixel(mixed,160,190)[3]==0,"Native square stretched in declared canvas");
        Check(Pixel(mixed,272,170)[2]==255,"Native fit changed corrected menu placement");
        D3D11_VIEWPORT restoredViewport{}; UINT n=1; context->RSGetViewports(&n,&restoredViewport);
        D3D11_RECT scissor{}; UINT m=1; context->RSGetScissorRects(&m,&scissor);
        Check(n==1 && !std::memcmp(&restoredViewport,&viewport,sizeof(viewport)) && m==1 &&
            !std::memcmp(&scissor,&originalScissor,sizeof(scissor)),"Native fit leaked viewport/scissor state");
    }
    Check(hud.Begin(image.Get(),36),"Native fit exception begin"); caught=false;
    try { hud.Draw(context.Get(),[]{throw std::runtime_error("native failure");},HudShaderKind::TwoDimensional,true); }
    catch(const std::runtime_error&) { caught=true; }
    D3D11_VIEWPORT afterFailure{}; UINT afterCount=1; context->RSGetViewports(&afterCount,&afterFailure);
    Check(caught && !std::memcmp(&afterFailure,&viewport,sizeof(viewport)),"Native fit exception leaked viewport");
    hud.End();
    Quad(1.1f,1.6f,-.2f,.2f,1,1);
    cameraShadow.generation=HudCameraGeneration.load();
    Check(SUCCEEDED(cameraBuffer->SetPrivateData(HudCameraTag,sizeof(cameraShadow),&cameraShadow)),"Refresh camera metadata");
    // A mask must be corrected in the native target/DSV, not redirected to the
    // capture texture or silently discarded on the second eye.
    D3D11_DEPTH_STENCIL_DESC ds{}; ds.DepthFunc=D3D11_COMPARISON_ALWAYS;
    ds.StencilEnable=TRUE; ds.StencilReadMask=ds.StencilWriteMask=255;
    ds.FrontFace=ds.BackFace={D3D11_STENCIL_OP_KEEP,D3D11_STENCIL_OP_KEEP,D3D11_STENCIL_OP_REPLACE,D3D11_COMPARISON_ALWAYS};
    ComPtr<ID3D11DepthStencilState> stencilState; Check(SUCCEEDED(device->CreateDepthStencilState(&ds,&stencilState)),"Mask depth state");
    context->OMSetDepthStencilState(stencilState.Get(),7); context->OMSetRenderTargets(1,&raw,depthView.Get());
    blendRt.RenderTargetWriteMask=0; ComPtr<ID3D11BlendState> maskBlend;
    Check(SUCCEEDED(device->CreateBlendState(&blendDesc,&maskBlend)),"Mask blend state"); context->OMSetBlendState(maskBlend.Get(),nullptr,0xffffffff);
    Check(hud.Begin(image.Get(),34),"Mask capture begin");
    for(bool firstEye:{true,false}) {
        context->ClearDepthStencilView(depthView.Get(),D3D11_CLEAR_STENCIL,1,0);
        Check(hud.Draw(context.Get(),capturedDraw,HudShaderKind::Shared,firstEye,Colour,Solid),"Corrected native mask not routed");
        Check(Pixel(depth.Get(),272,170)[3]==7 && Pixel(image.Get(),272,170)[1]>200,"Corrected mask lost native stencil or changed color");
    }
    geometry.Reset(); context->GSGetShader(&geometry,nullptr,nullptr); hud.End();
    Check(!geometry && hud.Draws()==0 && !hud.Current(34),"Mask-only frame leaked GS or exposed an empty panel");
    hud.Reset();
    context->ClearState(); canvas.Reset();
}
}
int main() {
    try { TestIdentity(); TestGraphics(); std::puts("HUD canvas: exact camera, clipping recovery, artwork varyings, narrow bar removal, pipeline guards and GS state restoration passed"); return 0; }
    catch(const std::exception& error) { std::puts(error.what()); return 1; }
}
