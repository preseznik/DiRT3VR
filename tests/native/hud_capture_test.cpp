#include "hud_capture.h"
#include "hud_hooks.h"
#include "hud_shaders.h"
#include <d3dcompiler.h>
#include <array>
#include <cstdio>
#include <stdexcept>
#include <fstream>
#include <iterator>
#include <vector>

using Microsoft::WRL::ComPtr;
using namespace dirt3vr;
namespace {
void Check(bool value,const char* message) { if(!value) throw std::runtime_error(message); }
void Log(const char*,...) {}
ComPtr<ID3D11Device> device;
ComPtr<ID3D11DeviceContext> context;
void CameraShadow() {
    // Exercise the actual installed D3D11 hooks, including invalidation when
    // writes are GPU-side or deferred and therefore not a current CPU snapshot.
    std::array<uint8_t,320> bytes{}; bytes.fill(0x31);
    D3D11_BUFFER_DESC desc{}; desc.ByteWidth=320; desc.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
    D3D11_SUBRESOURCE_DATA data{bytes.data(),0,0};
    ComPtr<ID3D11Buffer> buffer,source;
    Check(SUCCEEDED(device->CreateBuffer(&desc,&data,&buffer)) &&
          SUCCEEDED(device->CreateBuffer(&desc,&data,&source)),"camera shadow buffers");
    unsigned snapshotIndex{};
    const auto snapshot=[&](ID3D11Buffer* b,bool expected) {
        ++snapshotIndex;
        HudCameraShadow actual{}; UINT size=sizeof(actual);
        const bool valid=SUCCEEDED(b->GetPrivateData(HudCameraTag,&size,&actual)) && size==sizeof(actual) &&
            actual.generation==HudCameraGeneration.load();
        if(valid!=expected || (valid && actual.bytes!=bytes)) {
            printf("Camera shadow check %u: valid=%u expected=%u\n",snapshotIndex,valid,expected);
            Check(false,"stale or missing camera CPU shadow");
        }
    };
    snapshot(buffer.Get(),true);
    bytes.fill(0x52); context->UpdateSubresource(buffer.Get(),0,nullptr,bytes.data(),0,0); snapshot(buffer.Get(),true);
    context->CopyResource(buffer.Get(),source.Get()); snapshot(buffer.Get(),false);
    context->UpdateSubresource(buffer.Get(),0,nullptr,bytes.data(),0,0);
    context->CopySubresourceRegion(buffer.Get(),0,0,0,0,source.Get(),0,nullptr); snapshot(buffer.Get(),false);
    ComPtr<ID3D11DeviceContext> deferred;
    Check(SUCCEEDED(device->CreateDeferredContext(0,&deferred)),"deferred camera context");
    context->UpdateSubresource(buffer.Get(),0,nullptr,bytes.data(),0,0);
    deferred->UpdateSubresource(buffer.Get(),0,nullptr,bytes.data(),0,0);
    ComPtr<ID3D11CommandList> commands;
    Check(SUCCEEDED(deferred->FinishCommandList(FALSE,&commands)),"camera command list");
    context->ExecuteCommandList(commands.Get(),TRUE); snapshot(buffer.Get(),false);
    context->UpdateSubresource(buffer.Get(),0,nullptr,bytes.data(),0,0); snapshot(buffer.Get(),true);
    desc.Usage=D3D11_USAGE_DYNAMIC; desc.CPUAccessFlags=D3D11_CPU_ACCESS_WRITE;
    ComPtr<ID3D11Buffer> dynamic;
    Check(SUCCEEDED(device->CreateBuffer(&desc,&data,&dynamic)),"dynamic camera buffer");
    D3D11_MAPPED_SUBRESOURCE mapped{};
    Check(SUCCEEDED(context->Map(dynamic.Get(),0,D3D11_MAP_WRITE_DISCARD,0,&mapped)),"camera write map");
    snapshot(dynamic.Get(),false);
    bytes.fill(0x73); memcpy(mapped.pData,bytes.data(),bytes.size());
    context->Unmap(dynamic.Get(),0); snapshot(dynamic.Get(),true);
    context->UpdateSubresource(source.Get(),0,nullptr,bytes.data(),0,0);
    // Private data is owned by the COM object, not a registry of reused pointers.
    buffer.Reset(); desc.Usage=D3D11_USAGE_DEFAULT; desc.CPUAccessFlags=0;
    Check(SUCCEEDED(device->CreateBuffer(&desc,nullptr,&buffer)),"replacement camera buffer"); snapshot(buffer.Get(),false);
    desc.ByteWidth=336; ComPtr<ID3D11Buffer> other;
    Check(SUCCEEDED(device->CreateBuffer(&desc,nullptr,&other)),"other constant buffer"); snapshot(other.Get(),false);
}
ComPtr<ID3D11Texture2D> Texture(DXGI_FORMAT format,UINT binds) {
    D3D11_TEXTURE2D_DESC d{}; d.Width=320; d.Height=180;
    d.ArraySize=d.MipLevels=d.SampleDesc.Count=1; d.Format=format; d.BindFlags=binds;
    ComPtr<ID3D11Texture2D> value; Check(SUCCEEDED(device->CreateTexture2D(&d,nullptr,&value)),"texture allocation"); return value;
}
std::array<unsigned char,4> Pixel(ID3D11Texture2D* texture) {
    D3D11_TEXTURE2D_DESC d{}; texture->GetDesc(&d); d.BindFlags=0; d.Usage=D3D11_USAGE_STAGING; d.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> copy; Check(SUCCEEDED(device->CreateTexture2D(&d,nullptr,&copy)),"readback allocation");
    context->CopyResource(copy.Get(),texture); D3D11_MAPPED_SUBRESOURCE mapped{};
    Check(SUCCEEDED(context->Map(copy.Get(),0,D3D11_MAP_READ,0,&mapped)),"readback mapping");
    std::array<unsigned char,4> result{}; memcpy(result.data(),mapped.pData,4); context->Unmap(copy.Get(),0); return result;
}
}
int main(int argc,char** argv) {
    try {
        Check(SUCCEEDED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context)),"WARP unavailable");
        Check(InstallHudHooks(device.Get(),Log) && HudHooksReady(),"draw hook installation");
        CameraShadow();
        const char shader[]=R"(
float4 vs(uint id:SV_VertexID):SV_Position { float2 uv=float2((id<<1)&2,id&2); return float4(uv*float2(2,-2)+float2(-1,1),0,1); }
float4 ps():SV_Target { return float4(1,0,0,.5); }
)";
        ComPtr<ID3DBlob> vs,ps,error; ComPtr<ID3D11VertexShader> vertex; ComPtr<ID3D11PixelShader> pixel;
        Check(SUCCEEDED(D3DCompile(shader,sizeof(shader)-1,nullptr,nullptr,nullptr,"vs","vs_5_0",0,0,&vs,&error)) &&
              SUCCEEDED(D3DCompile(shader,sizeof(shader)-1,nullptr,nullptr,nullptr,"ps","ps_5_0",0,0,&ps,&error)),"compile fixture shaders");
        Check(SUCCEEDED(device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&vertex)) &&
              SUCCEEDED(device->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&pixel)),"create fixture shaders");
        auto back=Texture(DXGI_FORMAT_R8G8B8A8_UNORM,D3D11_BIND_RENDER_TARGET);
        ComPtr<ID3D11RenderTargetView> target; Check(SUCCEEDED(device->CreateRenderTargetView(back.Get(),nullptr,&target)),"backbuffer target");
        auto* raw=target.Get(); context->OMSetRenderTargets(1,&raw,nullptr);
        D3D11_VIEWPORT viewport{0,0,320,180,0,1}; context->RSSetViewports(1,&viewport);
        context->VSSetShader(vertex.Get(),nullptr,0); context->PSSetShader(pixel.Get(),nullptr,0);
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        D3D11_BLEND_DESC desc{}; auto& rt=desc.RenderTarget[0]; rt.BlendEnable=TRUE;
        rt.SrcBlend=D3D11_BLEND_SRC_ALPHA; rt.DestBlend=D3D11_BLEND_INV_SRC_ALPHA; rt.BlendOp=D3D11_BLEND_OP_ADD;
        rt.SrcBlendAlpha=D3D11_BLEND_ONE; rt.DestBlendAlpha=D3D11_BLEND_ZERO; rt.BlendOpAlpha=D3D11_BLEND_OP_ADD; rt.RenderTargetWriteMask=15;
        ComPtr<ID3D11BlendState> blend; Check(SUCCEEDED(device->CreateBlendState(&desc,&blend)),"fixture blend");
        context->OMSetBlendState(blend.Get(),nullptr,0xffffffff);
        const float blue[4]{0,0,1,1}; context->ClearRenderTargetView(target.Get(),blue);
        HudCapture hud; unsigned draws{};
        auto draw=[&] { ++draws; context->Draw(3,0); };
        Check(!hud.Draw(context.Get(),draw,HudShaderKind::TwoDimensional,true),"inactive capture routed draw");
        Check(hud.Begin(back.Get(),1),"begin HUD");
        Check(!hud.Draw(context.Get(),draw,HudShaderKind::None,true),"world shader captured");
        Check(hud.Draw(context.Get(),draw,HudShaderKind::TwoDimensional,true) && draws==1,"first eye capture");
        Check(hud.Draw(context.Get(),draw,HudShaderKind::TwoDimensional,false) && draws==1 && hud.Suppressed()==1,"second eye was drawn twice");
        ComPtr<ID3D11RenderTargetView> actual; ComPtr<ID3D11BlendState> actualBlend;
        float factors[4]{}; UINT mask{}; context->OMGetRenderTargets(1,&actual,nullptr); context->OMGetBlendState(&actualBlend,factors,&mask);
        Check(actual.Get()==target.Get() && actualBlend.Get()==blend.Get() && mask==0xffffffff,"capture did not restore graphics state");
        hud.End(); auto* image=hud.Current(1); Check(image && !hud.Current(2),"stale HUD frame exposed");
        auto color=Pixel(image); Check(color[0]>=127 && color[0]<=128 && color[1]==0 && color[2]==0 && color[3]>=127 && color[3]<=128,"captured alpha/RGB wrong");
        Check(Pixel(back.Get())[2]==255 && Pixel(back.Get())[0]==0,"HUD contaminated world image");
        Check(hud.Begin(back.Get(),2),"begin empty frame"); hud.End(); Check(!hud.Current(2),"empty frame reused old HUD");
        Check(hud.Begin(back.Get(),3),"begin exception frame");
        try { hud.Draw(context.Get(),[]{throw std::runtime_error("draw failure");},HudShaderKind::TwoDimensional,true); Check(false,"exception lost"); }
        catch(const std::runtime_error&) {}
        context->OMGetRenderTargets(1,&actual,nullptr); context->OMGetBlendState(&actualBlend,factors,&mask);
        Check(hud.Failed() && actual.Get()==target.Get() && actualBlend.Get()==blend.Get(),"failed draw did not restore state");
        hud.End(); Check(!hud.Current(3),"failed capture was exposed");
        auto depth=Texture(DXGI_FORMAT_D24_UNORM_S8_UINT,D3D11_BIND_DEPTH_STENCIL);
        ComPtr<ID3D11DepthStencilView> depthView; Check(SUCCEEDED(device->CreateDepthStencilView(depth.Get(),nullptr,&depthView)),"DSV");
        D3D11_DEPTH_STENCIL_DESC ds{}; ds.DepthEnable=TRUE; ds.DepthWriteMask=D3D11_DEPTH_WRITE_MASK_ZERO; ds.DepthFunc=D3D11_COMPARISON_ALWAYS;
        ComPtr<ID3D11DepthStencilState> state; Check(SUCCEEDED(device->CreateDepthStencilState(&ds,&state)),"depth state");
        context->OMSetRenderTargets(1,&raw,depthView.Get()); context->OMSetDepthStencilState(state.Get(),0);
        Check(hud.Begin(back.Get(),4) && !hud.Draw(context.Get(),draw,HudShaderKind::Shared,true),"shared world-depth shader captured");
        ds.DepthEnable=FALSE; ds.StencilEnable=TRUE; ds.StencilReadMask=ds.StencilWriteMask=255;
        ds.FrontFace=ds.BackFace={D3D11_STENCIL_OP_KEEP,D3D11_STENCIL_OP_KEEP,D3D11_STENCIL_OP_REPLACE,D3D11_COMPARISON_ALWAYS};
        state.Reset(); Check(SUCCEEDED(device->CreateDepthStencilState(&ds,&state)),"stencil state"); context->OMSetDepthStencilState(state.Get(),7);
        context->ClearDepthStencilView(depthView.Get(),D3D11_CLEAR_STENCIL,1,0);
        Check(hud.Draw(context.Get(),draw,HudShaderKind::Shared,true) && Pixel(depth.Get())[3]==7,"first eye lost native stencil write");
        context->ClearDepthStencilView(depthView.Get(),D3D11_CLEAR_STENCIL,1,0);
        Check(hud.Draw(context.Get(),draw,HudShaderKind::Shared,false) && Pixel(depth.Get())[3]==7,"second eye lost native stencil write");
        Check(Pixel(back.Get())[0]==0 && Pixel(back.Get())[2]==255,"stencil-only second eye changed color");
        hud.End();
        // The opt-in probe preserves state and bytes, refuses foreign targets,
        // stops after 256 draws and never overwrites an existing capture.
        const auto folder=std::filesystem::temp_directory_path()/(
            L"DiRT3VR-HudProbe-test-"+std::to_wstring(GetCurrentProcessId()));
        Check(std::filesystem::create_directory(folder),"probe test directory already exists");
        const auto prefix=folder/(L"hud-probe-"+std::to_wstring(GetCurrentProcessId())+L"-20");
        D3D11_BUFFER_DESC constantDesc{}; constantDesc.ByteWidth=512;
        constantDesc.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
        std::array<unsigned char,512> constantBytes{}; constantBytes.fill(0x42);
        D3D11_SUBRESOURCE_DATA constantData{constantBytes.data(),0,0};
        ComPtr<ID3D11Buffer> constants;
        Check(SUCCEEDED(device->CreateBuffer(&constantDesc,&constantData,&constants)),"probe constants");
        auto* constantRaw=constants.Get(); context->VSSetConstantBuffers(3,1,&constantRaw);
        const char inputShader[]="float4 main(float3 position:POSITION,float4 row:TANGENT):SV_Position { return float4(position,1)+row; }";
        ComPtr<ID3DBlob> inputCode;
        Check(SUCCEEDED(D3DCompile(inputShader,sizeof(inputShader)-1,nullptr,nullptr,nullptr,"main","vs_5_0",0,0,&inputCode,&error)),"probe input shader");
        const D3D11_INPUT_ELEMENT_DESC elements[]={{"POSITION",0,DXGI_FORMAT_R32G32B32_FLOAT,0,0,D3D11_INPUT_PER_VERTEX_DATA,0},
                                                 {"TANGENT",0,DXGI_FORMAT_R32G32B32A32_FLOAT,1,0,D3D11_INPUT_PER_INSTANCE_DATA,1}};
        ComPtr<ID3D11InputLayout> input;
        Check(SUCCEEDED(device->CreateInputLayout(elements,2,inputCode->GetBufferPointer(),inputCode->GetBufferSize(),&input)),"probe tagged input layout");
        context->IASetInputLayout(input.Get());
        std::array<unsigned short,600> indexValues{};
        for(unsigned i=301;i<307;++i) indexValues[i]=20;
        D3D11_BUFFER_DESC indexDesc{}; indexDesc.ByteWidth=sizeof(indexValues); indexDesc.BindFlags=D3D11_BIND_INDEX_BUFFER;
        D3D11_SUBRESOURCE_DATA indexInitial{indexValues.data(),0,0}; ComPtr<ID3D11Buffer> indexBuffer;
        Check(SUCCEEDED(device->CreateBuffer(&indexDesc,&indexInitial,&indexBuffer)),"probe index buffer");
        context->IASetIndexBuffer(indexBuffer.Get(),DXGI_FORMAT_R16_UINT,2);
        std::array<unsigned char,1024> vertexBytes{}; vertexBytes[268]=0xcd; vertexBytes[48]=0xef;
        D3D11_BUFFER_DESC vertexDesc{}; vertexDesc.ByteWidth=sizeof(vertexBytes); vertexDesc.BindFlags=D3D11_BIND_VERTEX_BUFFER;
        D3D11_SUBRESOURCE_DATA vertexInitial{vertexBytes.data(),0,0}; ComPtr<ID3D11Buffer> vertexBuffer;
        Check(SUCCEEDED(device->CreateBuffer(&vertexDesc,&vertexInitial,&vertexBuffer)),"probe vertex buffer");
        ID3D11Buffer* vertexBuffers[]={vertexBuffer.Get(),vertexBuffer.Get()}; UINT strides[]={12,16},offsets[]={4,0};
        context->IASetVertexBuffers(0,2,vertexBuffers,strides,offsets);
        context->OMSetBlendState(nullptr,nullptr,0xffffffff);
        HudProbe probe;
        Check(!probe.Begin("relative",20) && probe.Begin(folder,20),"probe path guards");
        probe.Record(context.Get(),image,1,2,"draw",3,1,0,0,0);
        Check(probe.Count()==0,"probe captured a foreign target");
        for(unsigned i=0;i<260;++i) probe.Record(context.Get(),back.Get(),1,2,"indexed-instanced",6,1,300,2,3);
        Check(probe.Count()==256,"unbounded diagnostic draws");
        context->OMGetRenderTargets(1,&actual,nullptr);
        ComPtr<ID3D11Buffer> actualConstants; context->VSGetConstantBuffers(3,1,&actualConstants);
        Check(actual.Get()==target.Get() && actualConstants.Get()==constants.Get(),"probe changed game state");
        probe.Finish(hud.Current(4)); Check(!probe.Active(),"probe survived frame end");
        std::ifstream trace(prefix.wstring()+L".jsonl",std::ios::binary);
        std::string contents{std::istreambuf_iterator<char>(trace),{}}; trace.close();
        Check(contents.find("4242424242424242")!=std::string::npos &&
              contents.find("\"limitReached\":1")!=std::string::npos,"probe lost buffer bytes/limit marker");
        Check(contents.find("\"offset\":602,\"data\":\"14001400")!=std::string::npos &&
              contents.find("\"offset\":268,\"data\":\"cd")!=std::string::npos &&
              contents.find("\"offset\":48,\"data\":\"ef")!=std::string::npos,"probe read unrelated geometry for nonzero draw offsets");
        Check(contents.find("\"blend\":null")!=std::string::npos,"probe misreported default blend");
        Check(std::filesystem::file_size(prefix.wstring()+L".rgba")==320*180*4,"probe image dimensions/alpha bytes");
        Check(!probe.Begin(folder,20),"probe overwrote evidence");
        for(uint64_t frame=21;frame<=22;++frame) { Check(probe.Begin(folder,frame),"bounded probe rejected"); probe.Finish(nullptr); }
        Check(!probe.Begin(folder,23),"unbounded diagnostic capture count");
        for(uint64_t frame=20;frame<=22;++frame)
            std::filesystem::remove(folder/(L"hud-probe-"+std::to_wstring(GetCurrentProcessId())+L"-"+std::to_wstring(frame)+L".jsonl"));
        std::filesystem::remove(prefix.wstring()+L".rgba"); std::filesystem::remove(folder);
        constantRaw=nullptr; context->VSSetConstantBuffers(3,1,&constantRaw);
        ID3D11Buffer* nullBuffers[]={nullptr,nullptr}; UINT zero[]={0,0};
        context->IASetVertexBuffers(0,2,nullBuffers,zero,zero); context->IASetIndexBuffer(nullptr,DXGI_FORMAT_UNKNOWN,0);
        context->IASetInputLayout(nullptr); context->OMSetBlendState(blend.Get(),nullptr,0xffffffff);
        hud.Reset();
        Check(ClassifyHudShader(0xbef999561247f2d5ull)==HudShaderKind::None && ClassifyHudShader(0x5c571e5a5daa0ce4ull)==HudShaderKind::Shared,"wrong-game/shared classification");
        // ui_osd_2d_* race display programs, not only ui_2d_osd_*, belong on the HUD panel.
        Check(ClassifyHudShader(0x15472d78cb54f392ull)==HudShaderKind::TwoDimensional && ClassifyHudShader(0xf89762721daadb0bull)==HudShaderKind::TwoDimensional &&
              ClassifyHudShader(0x1d3d792d462f6733ull)==HudShaderKind::TwoDimensional,"race display (OSD) classification");
        // Unknown shaders and draws outside a capture scope must remain normal.
        context->OMSetRenderTargets(1,&raw,nullptr); context->Draw(3,0);
        Check(Pixel(back.Get())[0]>=127,"hook changed ordinary game drawing");
        if(argc==2) {
            std::ifstream file(argv[1],std::ios::binary);
            const std::vector<unsigned char> bytes{std::istreambuf_iterator<char>(file),{}};
            bool found{};
            for(size_t offset=0;offset+32<bytes.size();++offset) {
                if(memcmp(bytes.data()+offset,"DXBC",4)) continue;
                uint32_t size{}; memcpy(&size,bytes.data()+offset+24,4);
                if(size<32 || size>bytes.size()-offset) continue;
                uint64_t hash=14695981039346656037ull;
                for(size_t i=0;i<size;++i) hash=(hash^bytes[offset+i])*1099511628211ull;
                const auto kind=ClassifyHudShader(hash);
                if(kind==HudShaderKind::None) continue;
                ComPtr<ID3D11VertexShader> gameShader;
                Check(SUCCEEDED(device->CreateVertexShader(bytes.data()+offset,size,nullptr,&gameShader)),"shipped UI shader creation");
                Check(QueryHudShader(gameShader.Get())==kind,"actual device hook did not tag shipped shader");
                context->VSSetShader(gameShader.Get(),nullptr,0);
                Check(hud.Begin(back.Get(),5),"begin routed frame");
                { HudDrawScope scope(hud,0); context->Draw(0,0); }
                { HudDrawScope scope(hud,1); context->Draw(0,0); }
                Check(hud.Draws()==1 && hud.Suppressed()==1,"actual draw hook did not route first/second eye");
                context->Draw(0,0); Check(hud.Draws()==1,"draw scope escaped its lifetime");
                hud.End(); found=true; break;
            }
            Check(found,"No classified shader found in provided pack");
        }
        puts("HUD capture WARP: alpha, first/second eye, ownership, empty frames, failure restoration, stencil and world exclusion passed");
        return 0;
    } catch(const std::exception& e) { puts(e.what()); return 1; }
}
