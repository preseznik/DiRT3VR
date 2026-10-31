#include "presentation_trace.h"
#include "hud_canvas.h"
#include <wrl/client.h>
#include <cstdio>
#include <fstream>
#include <stdexcept>
#include <thread>
#include <vector>

using namespace dirt3vr;
using Microsoft::WRL::ComPtr;
namespace {
void Check(bool value,const char* message) { if(!value) throw std::runtime_error(message); }
ComPtr<ID3D11Device> device;
ComPtr<ID3D11DeviceContext> context;
constexpr uint64_t Movie=0xf3c97950c737695dull;
ComPtr<ID3D11Texture2D> Texture() {
    D3D11_TEXTURE2D_DESC d{}; d.Width=320; d.Height=340; d.ArraySize=d.MipLevels=d.SampleDesc.Count=1;
    d.Format=DXGI_FORMAT_R8G8B8A8_UNORM; d.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
    ComPtr<ID3D11Texture2D> result; Check(SUCCEEDED(device->CreateTexture2D(&d,nullptr,&result)),"texture"); return result;
}
ComPtr<ID3D11RenderTargetView> Target(ID3D11Texture2D* texture) {
    ComPtr<ID3D11RenderTargetView> result; Check(SUCCEEDED(device->CreateRenderTargetView(texture,nullptr,&result)),"target"); return result;
}
std::vector<std::string> Lines(const std::filesystem::path& folder) {
    std::ifstream input(folder/("presentation-"+std::to_string(GetCurrentProcessId())+".jsonl"));
    std::vector<std::string> lines; std::string line; while(std::getline(input,line)) lines.push_back(line); return lines;
}
void Draw(PresentationTrace& trace,uint64_t vs=Movie,uint64_t ps=1) {
    trace.Record(context.Get(),vs,ps,"DrawIndexed",6,1,3,-2,0);
}
ULONG References(IUnknown* object) { auto count=object->AddRef(); object->Release(); return count; }
void AdmissionAndState(const std::filesystem::path& folder) {
    PresentationTrace trace;
    Check(!trace.Configure("relative") && !trace.Configure(folder/"missing"),"must require existing absolute output directory");
    Check(trace.Configure(folder) && !trace.Enabled(),"render owner must not bind to configuring thread");
    auto back=Texture(),foreign=Texture(),source=Texture(); auto target=Target(back.Get()),foreignTarget=Target(foreign.Get());
    const auto before=References(back.Get()); trace.BindBackbuffer(back.Get());
    Check(trace.Enabled() && References(back.Get())==before,"binding retains no backbuffer reference");
    context->OMSetRenderTargets(1,foreignTarget.GetAddressOf(),nullptr);
    Draw(trace,123); Check(trace.Records()==0,"nonmovie foreign target refused");
    Draw(trace); Check(trace.Records()==1,"movie on foreign target retained");
    trace.BeginEye(0); Draw(trace,123); Check(trace.Records()==1,"ordinary engine eye draws excluded");
    Check(!trace.Wants(123) && !trace.Wants(123,7,1) && !trace.Wants(123,3,2) && trace.Wants(123,3,1) && trace.Wants(Movie),"cheap draw admission");
    Check(trace.Wants(0x361f7beff48b284dull,2400,1),"measured final composite admitted despite high vertex count");
    Draw(trace); trace.BeginEye(1); Draw(trace); trace.EndEye();
    Check(trace.Records()==3,"movie phases retained");
    context->OMSetRenderTargets(1,target.GetAddressOf(),nullptr);
    ComPtr<ID3D11ShaderResourceView> srv; Check(SUCCEEDED(device->CreateShaderResourceView(source.Get(),nullptr,&srv)),"srv");
    context->PSSetShaderResources(2,1,srv.GetAddressOf());
    D3D11_BUFFER_DESC bd{}; bd.ByteWidth=320; bd.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
    ComPtr<ID3D11Buffer> camera; Check(SUCCEEDED(device->CreateBuffer(&bd,nullptr,&camera)),"camera");
    HudCameraShadow shadow{}; shadow.generation=HudCameraGeneration.load(); shadow.bytes.fill(0x5a);
    camera->SetPrivateData(HudCameraTag,sizeof(shadow),&shadow); context->VSSetConstantBuffers(3,1,camera.GetAddressOf());
    D3D11_BLEND_DESC blendDesc{}; blendDesc.RenderTarget[0].BlendEnable=TRUE;
    blendDesc.RenderTarget[0].SrcBlend=D3D11_BLEND_SRC_ALPHA; blendDesc.RenderTarget[0].DestBlend=D3D11_BLEND_INV_SRC_ALPHA;
    blendDesc.RenderTarget[0].SrcBlendAlpha=D3D11_BLEND_ONE; blendDesc.RenderTarget[0].DestBlendAlpha=D3D11_BLEND_ZERO;
    blendDesc.RenderTarget[0].BlendOp=blendDesc.RenderTarget[0].BlendOpAlpha=D3D11_BLEND_OP_ADD;
    blendDesc.RenderTarget[0].RenderTargetWriteMask=15;
    ComPtr<ID3D11BlendState> blend; Check(SUCCEEDED(device->CreateBlendState(&blendDesc,&blend)),"blend");
    const float factors[]={.25f,.5f,.75f,1.f}; context->OMSetBlendState(blend.Get(),factors,0x12345678);
    D3D11_RASTERIZER_DESC rd{}; rd.FillMode=D3D11_FILL_SOLID; rd.CullMode=D3D11_CULL_NONE; rd.DepthClipEnable=TRUE;
    ComPtr<ID3D11RasterizerState> raster; Check(SUCCEEDED(device->CreateRasterizerState(&rd,&raster)),"raster"); context->RSSetState(raster.Get());
    D3D11_VIEWPORT viewport{0,0,320,340,0,1}; context->RSSetViewports(1,&viewport);
    const auto backRefs=References(back.Get()),sourceRefs=References(source.Get());
    trace.Record(context.Get(),0,0,"Draw\"\\Indexed",6,1,3,-2,0);
    Check(trace.Records()==4,"unknown-hash tail draw retained");
    Check(References(back.Get())==backRefs && References(source.Get())==sourceRefs,"record retains no target or source references");
    ComPtr<ID3D11RenderTargetView> gotTarget; context->OMGetRenderTargets(1,&gotTarget,nullptr);
    ComPtr<ID3D11BlendState> gotBlend; float gotFactors[4]{}; UINT gotMask{}; context->OMGetBlendState(&gotBlend,gotFactors,&gotMask);
    ComPtr<ID3D11RasterizerState> gotRaster; context->RSGetState(&gotRaster);
    ComPtr<ID3D11ShaderResourceView> gotSrv; context->PSGetShaderResources(2,1,&gotSrv);
    ComPtr<ID3D11Buffer> gotCamera; context->VSGetConstantBuffers(3,1,&gotCamera);
    Check(gotTarget==target && gotBlend==blend && gotRaster==raster && gotSrv==srv && gotCamera==camera && gotMask==0x12345678,
          "record must preserve graphics bindings");
    for(unsigned i=0;i<4;++i) Check(gotFactors[i]==factors[i],"blend factors preserved");
    auto lines=Lines(folder);
    Check(lines.size()==4 && lines[0].find("\"backbuffer\":0")!=std::string::npos && lines[1].find("\"phase\":\"left\"")!=std::string::npos &&
          lines[2].find("\"phase\":\"right\"")!=std::string::npos,"phase and target provenance");
    Check(lines[3].find("\"camera\":\"5a5a5a5a")!=std::string::npos && lines[3].find("\"width\":320,\"height\":340")!=std::string::npos &&
          lines[3].find("Draw\\\"\\\\Indexed")!=std::string::npos,"camera, dimensions, escaped draw name");
    ++HudCameraGeneration; Draw(trace,0,0); lines=Lines(folder);
    Check(lines.back().find("\"camera\":null")!=std::string::npos,"stale CPU camera excluded");
    trace.BeginEye(0); Draw(trace,123); trace.BeginEye(1); Draw(trace,123); trace.EndEye();
    lines=Lines(folder);
    Check(lines[lines.size()-2].find("\"screen_draw\":1")!=std::string::npos && lines.back().find("\"phase\":\"right\"")!=std::string::npos,
          "small in-eye backbuffer composites captured for both eyes");
    const auto recorded=trace.Records();
    bool foreignDisabled{};
    std::thread foreignThread([&] {
        foreignDisabled=!trace.Enabled(); trace.BindBackbuffer(foreign.Get());
        trace.BeginEye(0); Draw(trace); trace.EndEye(); trace.BeforePresent();
    }); foreignThread.join();
    Check(foreignDisabled && trace.Records()==recorded,"foreign thread cannot advance or write trace");
    trace.BeforePresent(); auto summary=Lines(folder).back();
    Check(summary.find("\"type\":\"present\"")!=std::string::npos && summary.find("\"sequence\":0")!=std::string::npos &&
          summary.find("\"screen_draws\":2")!=std::string::npos,"present summary distinguishes in-eye composites");
    trace.BindBackbuffer(foreign.Get()); Draw(trace,9,9); Check(trace.Records()==recorded+1,"old backbuffer no longer admitted");
    context->OMSetRenderTargets(1,foreignTarget.GetAddressOf(),nullptr); Draw(trace,9,9); Check(trace.Records()==recorded+2,"replacement backbuffer admitted");
    context->ClearState();
}
void Limits(const std::filesystem::path& folder) {
    PresentationTrace trace; Check(trace.Configure(folder),"limits configure"); auto back=Texture(); auto target=Target(back.Get());
    trace.BindBackbuffer(back.Get()); context->OMSetRenderTargets(1,target.GetAddressOf(),nullptr);
    for(unsigned i=0;i<4096;++i) Draw(trace);
    Check(trace.Records()==12 && trace.Pipelines()==1,"per pipeline twelve sample maximum");
    Check(trace.Configure(folder) && trace.Records()==12,"reconfigure cannot reset limits");
    for(unsigned i=0;i<300;++i) Draw(trace,Movie,100+i);
    Check(trace.Pipelines()==256 && trace.Records()==267,"unique pipeline limit");
    for(unsigned i=0;i<300;++i) Draw(trace,Movie,100+i);
    Check(trace.Records()==512 && !trace.Enabled(),"all record types share total512 limit");
    trace.BeforePresent(); Draw(trace); Check(Lines(folder).size()==512,"hard output limit");
    context->ClearState();
}
void SummaryChanges(const std::filesystem::path& folder) {
    PresentationTrace trace; Check(trace.Configure(folder),"summary configure"); auto back=Texture(); auto target=Target(back.Get());
    trace.BindBackbuffer(back.Get()); context->OMSetRenderTargets(1,target.GetAddressOf(),nullptr);
    Draw(trace); trace.BeforePresent(); Draw(trace); trace.BeforePresent();
    auto lines=Lines(folder); unsigned summaries{};
    for(const auto& line:lines) summaries+=line.find("\"type\":\"present\"")!=std::string::npos;
    Check(summaries==1,"unchanged present signature not repeated");
    trace.BeforePresent(); lines=Lines(folder); Check(lines.back().find("\"movie_draws\":0")!=std::string::npos,"disappearing movie recorded");
    for(unsigned i=0;i<300;++i) { Draw(trace,Movie,i%2); trace.BeforePresent(); }
    summaries=0; for(const auto& line:Lines(folder)) summaries+=line.find("\"type\":\"present\"")!=std::string::npos;
    Check(summaries==128,"summary budget"); context->ClearState();
}
}
int main() {
    const auto folder=std::filesystem::current_path()/("presentation-trace-test-"+std::to_string(GetCurrentProcessId()));
    try {
        Check(std::filesystem::create_directory(folder),"exclusive test directory");
        for(const auto* child:{"admission","limits","summary"}) std::filesystem::create_directory(folder/child);
        Check(SUCCEEDED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context)),"WARP device");
        AdmissionAndState(folder/"admission");
        PresentationTrace existing; Check(!existing.Configure(folder/"admission"),"existing evidence must not be overwritten");
        Limits(folder/"limits"); SummaryChanges(folder/"summary");
        context.Reset(); device.Reset(); std::filesystem::remove_all(folder);
        puts("presentation trace ownership, filtering, state, CPU metadata and bounds passed"); return 0;
    } catch(const std::exception& error) { fprintf(stderr,"presentation trace test: %s\n",error.what()); return 1; }
}
