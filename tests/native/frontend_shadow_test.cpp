#include "frontend_shadow.h"
#include <d3dcompiler.h>
#include <d3d11sdklayers.h>
#include <array>
#include <cstdio>
#include <cstring>
#include <limits>
#include <stdexcept>
#include <vector>

using namespace dirt3vr;
using Microsoft::WRL::ComPtr;
namespace {
bool hardware{};
constexpr uint64_t Vertex=0x4b2b0f44adfbf5bbull;
constexpr std::array<uint64_t,3> Pixels{0x28625f0662b53c50ull,0xa40172fc4329f5f6ull,0x6299a6c46af41b38ull};
void Check(bool value,const char* message) { if(!value) throw std::runtime_error(message); }
ComPtr<ID3DBlob> Compile(const char* source,const char* target) {
    ComPtr<ID3DBlob> code,error;
    const auto result=D3DCompile(source,strlen(source),nullptr,nullptr,nullptr,"main",target,0,0,&code,&error);
    if(FAILED(result) && error) std::puts(static_cast<const char*>(error->GetBufferPointer()));
    Check(SUCCEEDED(result),"shader compilation"); return code;
}
struct Gpu {
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID3D11DeviceContext1> context1;
    ComPtr<ID3D11VertexShader> vertex;
    ComPtr<ID3D11PixelShader> pixel;
    Gpu() {
        Check(SUCCEEDED(D3D11CreateDevice(nullptr,hardware ? D3D_DRIVER_TYPE_HARDWARE : D3D_DRIVER_TYPE_WARP,
            nullptr,hardware ? D3D11_CREATE_DEVICE_DEBUG : 0,nullptr,0,D3D11_SDK_VERSION,
            &device,nullptr,&context)),"D3D11 test device (hardware mode requires debug layer)");
        Check(SUCCEEDED(context.As(&context1)),"D3D11.1 context");
        auto vs=Compile("float4 main(float4 p:POSITION):SV_Position{return p;}","vs_5_0");
        auto ps=Compile("float4 main():SV_Target{return 1;}","ps_5_0");
        Check(SUCCEEDED(device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&vertex)),"vertex shader");
        Check(SUCCEEDED(device->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&pixel)),"pixel shader");
        context->VSSetShader(vertex.Get(),nullptr,0); context->PSSetShader(pixel.Get(),nullptr,0);
    }
    ComPtr<ID3D11Buffer> Buffer(const std::vector<uint8_t>& bytes,UINT binds=D3D11_BIND_CONSTANT_BUFFER) {
        D3D11_BUFFER_DESC desc{}; desc.ByteWidth=UINT(bytes.size()); desc.BindFlags=binds;
        D3D11_SUBRESOURCE_DATA data{bytes.data(),0,0}; ComPtr<ID3D11Buffer> result;
        Check(SUCCEEDED(device->CreateBuffer(&desc,&data,&result)),"buffer"); return result;
    }
    std::vector<uint8_t> Read(ID3D11Buffer* buffer) {
        D3D11_BUFFER_DESC desc{}; buffer->GetDesc(&desc); desc.BindFlags=desc.MiscFlags=desc.StructureByteStride=0;
        desc.Usage=D3D11_USAGE_STAGING; desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
        ComPtr<ID3D11Buffer> copy; Check(SUCCEEDED(device->CreateBuffer(&desc,nullptr,&copy)),"readback buffer");
        context->CopyResource(copy.Get(),buffer); D3D11_MAPPED_SUBRESOURCE map{};
        Check(SUCCEEDED(context->Map(copy.Get(),0,D3D11_MAP_READ,0,&map)),"readback map");
        std::vector<uint8_t> result(desc.ByteWidth); memcpy(result.data(),map.pData,result.size());
        context->Unmap(copy.Get(),0); return result;
    }
    void Bind(ID3D11Buffer* buffer,UINT first=0,UINT count=16) {
        context1->PSSetConstantBuffers1(0,1,&buffer,&first,&count);
    }
    void Original(ID3D11Buffer* buffer,UINT first=0,UINT count=16) {
        ComPtr<ID3D11Buffer> actual; UINT a{},b{};
        context1->PSGetConstantBuffers1(0,1,&actual,&a,&b);
        Check(actual.Get()==buffer && a==first && b==count,"PS buffer/range restoration");
    }
    std::vector<uint8_t> BoundBytes(ID3D11Buffer* native) {
        ComPtr<ID3D11Buffer> actual; context->PSGetConstantBuffers(0,1,&actual);
        Check(actual && actual.Get()!=native,"owned PS buffer not bound"); return Read(actual.Get());
    }
};
FrontendShadowMatrix Matrix(float translation) {
    return {.7f,.1f,0,0, -.2f,.6f,0,0, .1f,-.3f,1,0, translation,.25f,0,1};
}
std::vector<uint8_t> Constants(const FrontendShadowMatrix& matrix) {
    std::vector<uint8_t> bytes(240);
    for(size_t i=0;i<bytes.size();++i) bytes[i]=uint8_t(i*71+19);
    memcpy(bytes.data()+48,matrix.data(),64); return bytes;
}
void CorrectionAndGuards() {
    Gpu gpu; FrontendShadow helper;
    const auto expected=Matrix(.4f),eye=Matrix(.8f);
    const auto source=Constants(expected); auto wanted=source; memcpy(wanted.data()+48,eye.data(),64);
    auto native=gpu.Buffer(source); gpu.Bind(native.Get()); unsigned calls{};
    for(auto ps:Pixels) {
        Check(IsFrontendShadowShaderPair(Vertex,ps),"known shader pair");
        Check(helper.Draw(gpu.context.Get(),Vertex,ps,expected,eye,[&] {
            ++calls; Check(gpu.BoundBytes(native.Get())==wanted,"corrected FE matrix or unrelated bytes");
            Check(!helper.Draw(gpu.context.Get(),Vertex,ps,expected,eye,[]{}),"recursive draw admitted");
        }),"known draw refused");
        gpu.Original(native.Get()); Check(gpu.Read(native.Get())==source,"native resource changed");
    }
    Check(calls==3,"callback count");
    auto stale=expected; stale[0]+=.01f;
    Check(helper.Draw(gpu.context.Get(),Vertex,Pixels[0],stale,eye,[&] {
        Check(gpu.BoundBytes(native.Get())==source,"stale producer changed native constants");
    }),"stale draw should copy through"); gpu.Original(native.Get());
    // Reusing an already adjusted native matrix must not apply another eye delta.
    gpu.context->UpdateSubresource(native.Get(),0,nullptr,wanted.data(),0,0);
    Check(helper.Draw(gpu.context.Get(),Vertex,Pixels[0],expected,eye,[&] {
        Check(gpu.BoundBytes(native.Get())==wanted,"already adjusted input changed");
    }),"already adjusted input refused");
    gpu.context->UpdateSubresource(native.Get(),0,nullptr,source.data(),0,0);
    bool threw=false;
    try { helper.Draw(gpu.context.Get(),Vertex,Pixels[0],expected,eye,[] {throw std::runtime_error("callback");}); }
    catch(const std::runtime_error&) { threw=true; }
    Check(threw,"callback exception swallowed"); gpu.Original(native.Get());
    auto refuse=[&](ID3D11DeviceContext* context,uint64_t vs,uint64_t ps,const FrontendShadowMatrix& a,const FrontendShadowMatrix& b) {
        bool called=false; Check(!helper.Draw(context,vs,ps,a,b,[&]{called=true;}) && !called,"unsupported draw admitted");
    };
    refuse(gpu.context.Get(),Vertex^1,Pixels[0],expected,eye);
    refuse(gpu.context.Get(),Vertex,Pixels[0]^1,expected,eye);
    auto invalid=eye; invalid[5]=std::numeric_limits<float>::quiet_NaN();
    refuse(gpu.context.Get(),Vertex,Pixels[0],expected,invalid);
    refuse(gpu.context.Get(),Vertex,Pixels[0],invalid,eye);
    gpu.context->VSSetShader(nullptr,nullptr,0); refuse(gpu.context.Get(),Vertex,Pixels[0],expected,eye);
    gpu.context->VSSetShader(gpu.vertex.Get(),nullptr,0);
    gpu.context->PSSetShader(nullptr,nullptr,0); refuse(gpu.context.Get(),Vertex,Pixels[0],expected,eye);
    gpu.context->PSSetShader(gpu.pixel.Get(),nullptr,0);
    auto gsCode=Compile("struct V{float4 p:SV_Position;};[maxvertexcount(3)]void main(triangle V p[3],inout TriangleStream<V> s){s.Append(p[0]);s.Append(p[1]);s.Append(p[2]);}","gs_5_0");
    ComPtr<ID3D11GeometryShader> gs;
    Check(SUCCEEDED(gpu.device->CreateGeometryShader(gsCode->GetBufferPointer(),gsCode->GetBufferSize(),nullptr,&gs)),"geometry shader");
    gpu.context->GSSetShader(gs.Get(),nullptr,0); refuse(gpu.context.Get(),Vertex,Pixels[0],expected,eye);
    gpu.context->GSSetShader(nullptr,nullptr,0);
    auto oversized=gpu.Buffer(std::vector<uint8_t>(512)); gpu.Bind(oversized.Get());
    refuse(gpu.context.Get(),Vertex,Pixels[0],expected,eye); gpu.Original(oversized.Get());
    gpu.Bind(native.Get(),16,16); refuse(gpu.context.Get(),Vertex,Pixels[0],expected,eye); gpu.Original(native.Get(),16,16);
    gpu.Bind(native.Get());
    ComPtr<ID3D11DeviceContext> deferred; Check(SUCCEEDED(gpu.device->CreateDeferredContext(0,&deferred)),"deferred context");
    refuse(deferred.Get(),Vertex,Pixels[0],expected,eye);
    helper.Reset();
    Check(helper.Draw(gpu.context.Get(),Vertex,Pixels[0],expected,eye,[]{}),"reset recreation"); gpu.Original(native.Get());
    Gpu other; auto otherNative=other.Buffer(source); other.Bind(otherNative.Get());
    Check(helper.Draw(other.context.Get(),Vertex,Pixels[0],expected,eye,[&] {
        Check(other.BoundBytes(otherNative.Get())==wanted,"second-device output");
    }),"second-device recreation"); other.Original(otherNative.Get());
}
void StateRestoration() {
    Gpu gpu; FrontendShadow helper;
    const auto expected=Matrix(.4f),eye=Matrix(.8f); auto native=gpu.Buffer(Constants(expected)); gpu.Bind(native.Get());
    constexpr char csSource[]=R"(
interface Operation{float Value();};
class NativeOperation:Operation{float stored;float Value(){return stored;}};
class OtherOperation:Operation{float stored;float Value(){return stored*2;}};
Operation selected; RWStructuredBuffer<float> data:register(u0);
[numthreads(1,1,1)]void main(){data[0]=selected.Value();}
)";
    auto code=Compile(csSource,"cs_5_0"); ComPtr<ID3D11ClassLinkage> linkage;
    Check(SUCCEEDED(gpu.device->CreateClassLinkage(&linkage)),"class linkage");
    ComPtr<ID3D11ComputeShader> compute;
    Check(SUCCEEDED(gpu.device->CreateComputeShader(code->GetBufferPointer(),code->GetBufferSize(),linkage.Get(),&compute)),"class compute");
    ComPtr<ID3D11ClassInstance> instance;
    Check(SUCCEEDED(linkage->CreateClassInstance("NativeOperation",0,0,0,0,&instance)),"class instance");
    auto* classBinding=instance.Get(); gpu.context->CSSetShader(compute.Get(),&classBinding,1);
    auto constants=gpu.Buffer(std::vector<uint8_t>(512)); auto* cb=constants.Get(); UINT first=16,count=16;
    gpu.context1->CSSetConstantBuffers1(0,1,&cb,&first,&count);
    D3D11_BUFFER_DESC desc{}; desc.ByteWidth=64; desc.BindFlags=D3D11_BIND_UNORDERED_ACCESS|D3D11_BIND_SHADER_RESOURCE;
    desc.MiscFlags=D3D11_RESOURCE_MISC_BUFFER_STRUCTURED; desc.StructureByteStride=4;
    ComPtr<ID3D11Buffer> raw,readOnly;
    Check(SUCCEEDED(gpu.device->CreateBuffer(&desc,nullptr,&raw)) && SUCCEEDED(gpu.device->CreateBuffer(&desc,nullptr,&readOnly)),"structured buffers");
    D3D11_UNORDERED_ACCESS_VIEW_DESC ud{}; ud.ViewDimension=D3D11_UAV_DIMENSION_BUFFER;
    ud.Buffer.NumElements=16; ud.Buffer.Flags=D3D11_BUFFER_UAV_FLAG_COUNTER;
    ComPtr<ID3D11UnorderedAccessView> uav; Check(SUCCEEDED(gpu.device->CreateUnorderedAccessView(raw.Get(),&ud,&uav)),"counter UAV");
    auto* uv=uav.Get(); UINT counter=7; gpu.context->CSSetUnorderedAccessViews(0,1,&uv,&counter);
    ComPtr<ID3D11ShaderResourceView> srv; Check(SUCCEEDED(gpu.device->CreateShaderResourceView(readOnly.Get(),nullptr,&srv)),"CS source");
    auto* sv=srv.Get(); gpu.context->CSSetShaderResources(0,1,&sv);
    D3D11_QUERY_DESC qd{D3D11_QUERY_OCCLUSION_PREDICATE,0}; ComPtr<ID3D11Predicate> predicate;
    Check(SUCCEEDED(gpu.device->CreatePredicate(&qd,&predicate)),"predicate");
    gpu.context->Begin(predicate.Get()); gpu.context->End(predicate.Get()); gpu.context->SetPredication(predicate.Get(),TRUE);
    const D3D11_VIEWPORT view{3,4,31,29,.2f,.8f}; gpu.context->RSSetViewports(1,&view);
    auto verify=[&] {
        ComPtr<ID3D11ComputeShader> actual; ID3D11ClassInstance* classes[256]{}; UINT n=256;
        gpu.context->CSGetShader(&actual,classes,&n);
        const bool same=n==1 && classes[0]==instance.Get(); for(UINT i=0;i<n;++i) if(classes[i]) classes[i]->Release();
        Check(actual==compute && same,"compute shader/classes changed");
        ComPtr<ID3D11Buffer> b; UINT f{},c{}; gpu.context1->CSGetConstantBuffers1(0,1,&b,&f,&c);
        Check(b==constants && f==first && c==count,"compute CB/range changed");
        ComPtr<ID3D11ShaderResourceView> s; gpu.context->CSGetShaderResources(0,1,&s); Check(s==srv,"compute SRV changed");
        ComPtr<ID3D11UnorderedAccessView> u; gpu.context->CSGetUnorderedAccessViews(0,1,&u); Check(u==uav,"compute UAV changed");
        ComPtr<ID3D11Predicate> p; BOOL value{}; gpu.context->GetPredication(&p,&value); Check(p==predicate && value,"predicate changed");
        D3D11_VIEWPORT v{}; n=1; gpu.context->RSGetViewports(&n,&v); Check(n==1 && !memcmp(&v,&view,sizeof(v)),"viewport changed");
    };
    verify();
    Check(helper.Draw(gpu.context.Get(),Vertex,Pixels[0],expected,eye,verify),"state fixture draw");
    verify(); gpu.Original(native.Get());
    bool threw=false;
    try { helper.Draw(gpu.context.Get(),Vertex,Pixels[0],expected,eye,[&] {verify();throw std::runtime_error("state");}); }
    catch(const std::runtime_error&) {threw=true;}
    Check(threw,"state callback exception"); verify(); gpu.Original(native.Get());
    gpu.context->SetPredication(nullptr,FALSE);
    auto counterBuffer=gpu.Buffer(std::vector<uint8_t>(16),0); gpu.context->CopyStructureCount(counterBuffer.Get(),0,uav.Get());
    auto countBytes=gpu.Read(counterBuffer.Get()); UINT after{}; memcpy(&after,countBytes.data(),4);
    Check(after==7,"UAV counter changed");
}
void RepeatedRealDraws() {
    Gpu gpu; FrontendShadow helper;
    ComPtr<ID3D11InfoQueue> messages; gpu.device.As(&messages);
    auto vs=Compile("float4 main(uint i:SV_VertexID):SV_Position {return float4(i==1?3:-1,i==2?3:-1,0,1);}","vs_5_0");
    auto ps=Compile("cbuffer C:register(b0){float4 rows[15];};float4 main():SV_Target {return float4(rows[6].xy,0,1);}","ps_5_0");
    Check(SUCCEEDED(gpu.device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&gpu.vertex)),"draw VS");
    Check(SUCCEEDED(gpu.device->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&gpu.pixel)),"draw PS");
    gpu.context->VSSetShader(gpu.vertex.Get(),nullptr,0); gpu.context->PSSetShader(gpu.pixel.Get(),nullptr,0);
    D3D11_TEXTURE2D_DESC td{}; td.Width=td.Height=16; td.MipLevels=td.ArraySize=1;
    td.Format=DXGI_FORMAT_R32G32B32A32_FLOAT; td.SampleDesc.Count=1; td.BindFlags=D3D11_BIND_RENDER_TARGET;
    ComPtr<ID3D11Texture2D> target,read;
    Check(SUCCEEDED(gpu.device->CreateTexture2D(&td,nullptr,&target)),"draw texture");
    td.BindFlags=0; td.Usage=D3D11_USAGE_STAGING; td.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
    Check(SUCCEEDED(gpu.device->CreateTexture2D(&td,nullptr,&read)),"draw readback");
    ComPtr<ID3D11RenderTargetView> rtv; Check(SUCCEEDED(gpu.device->CreateRenderTargetView(target.Get(),nullptr,&rtv)),"draw RTV");
    auto* output=rtv.Get(); gpu.context->OMSetRenderTargets(1,&output,nullptr);
    const D3D11_VIEWPORT viewport{0,0,16,16,0,1}; gpu.context->RSSetViewports(1,&viewport);
    D3D11_RASTERIZER_DESC rd{}; rd.FillMode=D3D11_FILL_SOLID; rd.CullMode=D3D11_CULL_NONE; rd.DepthClipEnable=TRUE;
    ComPtr<ID3D11RasterizerState> raster; Check(SUCCEEDED(gpu.device->CreateRasterizerState(&rd,&raster)),"draw rasterizer");
    gpu.context->RSSetState(raster.Get());
    gpu.context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    auto native=gpu.Buffer(Constants(Matrix(.4f))); auto* cb=native.Get(); gpu.context->PSSetConstantBuffers(0,1,&cb);
    if(messages) messages->ClearStoredMessages();
    for(unsigned frame=0;frame<600;++frame) {
        const auto baseline=Matrix(float(frame%13)/20),eye=Matrix(float(frame%19)/20);
        const auto bytes=Constants(baseline); gpu.context->UpdateSubresource(native.Get(),0,nullptr,bytes.data(),0,0);
        Check(helper.Draw(gpu.context.Get(),Vertex,Pixels[frame%3],baseline,eye,[&]{gpu.context->Draw(3,0);}),"repeated draw refused");
        if(frame%30==29) {
            gpu.context->CopyResource(read.Get(),target.Get()); D3D11_MAPPED_SUBRESOURCE map{};
            const HRESULT hr=gpu.context->Map(read.Get(),0,D3D11_MAP_READ,0,&map);
            if(FAILED(hr)) std::printf("readback hr=%08lx removal=%08lx\n",hr,gpu.device->GetDeviceRemovedReason());
            Check(SUCCEEDED(hr),"real draw readback failed");
            float pixel[4]{}; memcpy(pixel,map.pData,sizeof(pixel)); gpu.context->Unmap(read.Get(),0);
            if(std::abs(pixel[0]-eye[12])>=1e-6f || std::abs(pixel[1]-eye[13])>=1e-6f)
                std::printf("draw pixel=%f,%f,%f,%f expected=%f,%f frame=%u\n",pixel[0],pixel[1],pixel[2],pixel[3],eye[12],eye[13],frame);
            Check(std::abs(pixel[0]-eye[12])<1e-6f && std::abs(pixel[1]-eye[13])<1e-6f,"real draw received stale matrix");
        }
    }
    if(messages) {
        unsigned errors{};
        for(UINT64 i=0;i<messages->GetNumStoredMessagesAllowedByRetrievalFilter();++i) {
            SIZE_T size{}; messages->GetMessage(i,nullptr,&size); std::vector<uint8_t> storage(size);
            auto* message=reinterpret_cast<D3D11_MESSAGE*>(storage.data()); messages->GetMessage(i,message,&size);
            if(message->Severity<=D3D11_MESSAGE_SEVERITY_WARNING) {
                std::printf("D3D11 severity=%u id=%u: %s\n",message->Severity,message->ID,message->pDescription);
                if(message->Severity<=D3D11_MESSAGE_SEVERITY_ERROR) ++errors;
            }
        }
        Check(errors==0,"D3D11 debug validation errors");
    }
    Check(SUCCEEDED(gpu.device->GetDeviceRemovedReason()),"device removed after repeated draws");
}
}
int main(int argc,char** argv) {
    hardware=argc==2 && !std::strcmp(argv[1],"--hardware");
    try { CorrectionAndGuards(); StateRestoration(); RepeatedRealDraws();
        std::puts("Frontend shadow GPU matrix guard, native byte preservation, scoped bindings, exception and device lifecycle pass."); return 0;
    } catch(const std::exception& error) { std::fprintf(stderr,"FAIL: %s\n",error.what()); return 1; }
}
