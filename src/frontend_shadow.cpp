#include "frontend_shadow.h"
#include <d3dcompiler.h>
#include <cmath>
#include <cstring>

namespace dirt3vr {
using Microsoft::WRL::ComPtr;
namespace {
constexpr UINT Bytes=240;
constexpr char Shader[]=R"(
ByteAddressBuffer nativeConstants:register(t0);
RWByteAddressBuffer result:register(u0);
cbuffer Parameters:register(b0) { uint4 expected[4]; uint4 corrected[4]; };
[numthreads(1,1,1)] void main() {
    bool matches=true;
    [unroll]for(uint r=0;r<4;++r) {
        matches=matches && all(nativeConstants.Load4(48+r*16)==expected[r]) &&
            all(isfinite(asfloat(corrected[r])));
    }
    [unroll]for(uint row=0;row<15;++row) {
        uint4 value=nativeConstants.Load4(row*16);
        if(matches && row>=3 && row<7)value=corrected[row-3];
        result.Store4(row*16,value);
    }
})";
bool Finite(const FrontendShadowMatrix& matrix) {
    for(float value:matrix) if(!std::isfinite(value)) return false;
    return true;
}
bool Pipeline(ID3D11DeviceContext* context) {
    ComPtr<ID3D11VertexShader> vertex; ComPtr<ID3D11PixelShader> pixel;
    ComPtr<ID3D11GeometryShader> geometry; ComPtr<ID3D11HullShader> hull; ComPtr<ID3D11DomainShader> domain;
    context->VSGetShader(&vertex,nullptr,nullptr); context->PSGetShader(&pixel,nullptr,nullptr);
    context->GSGetShader(&geometry,nullptr,nullptr); context->HSGetShader(&hull,nullptr,nullptr);
    context->DSGetShader(&domain,nullptr,nullptr);
    if(!vertex || !pixel || geometry || hull || domain) return false;
    ID3D11Buffer* streams[D3D11_SO_BUFFER_SLOT_COUNT]{};
    context->SOGetTargets(D3D11_SO_BUFFER_SLOT_COUNT,streams);
    bool streaming=false;
    for(auto* stream:streams) if(stream) { streaming=true; stream->Release(); }
    return !streaming;
}
struct ComputeScope {
    ID3D11DeviceContext1* context;
    ComPtr<ID3DDeviceContextState> previous;
    ComputeScope(ID3D11DeviceContext1* owner,ID3DDeviceContextState* isolated):context(owner) {
        context->SwapDeviceContextState(isolated,&previous);
        context->SetPredication(nullptr,FALSE);
    }
    ~ComputeScope() {
        // Release only this pass's bindings, as in GRID2VR's correction pass.
        // Avoid a broad ClearState in the game's immediate context (including
        // while its intro/video work is active). The previous context-state
        // still restores native class instances, CB ranges and UAV state.
        ID3D11Buffer* buffer=nullptr;
        ID3D11ShaderResourceView* view=nullptr;
        ID3D11UnorderedAccessView* output=nullptr;
        context->CSSetConstantBuffers(0,1,&buffer);
        context->CSSetShaderResources(0,1,&view);
        context->CSSetUnorderedAccessViews(0,1,&output,nullptr);
        context->CSSetShader(nullptr,nullptr,0);
        context->SwapDeviceContextState(previous.Get(),nullptr);
    }
};
struct PixelScope {
    ID3D11DeviceContext1* context;
    ID3D11Buffer* original;
    UINT first,count;
    ~PixelScope() { context->PSSetConstantBuffers1(0,1,&original,&first,&count); }
};
}

bool FrontendShadow::Initialize(ID3D11DeviceContext* context) {
    ComPtr<ID3D11Device> device; context->GetDevice(&device);
    if(device_==device && context_.Get()==context && thread_==GetCurrentThreadId()) return true;
    if(context_ && thread_!=GetCurrentThreadId()) return false;
    Reset();
    ComPtr<ID3D11Device1> device1;
    if(FAILED(device.As(&device1)) || FAILED(context->QueryInterface(IID_PPV_ARGS(&context_)))) return false;
    const auto level=device->GetFeatureLevel();
    if(level<D3D_FEATURE_LEVEL_11_0 || FAILED(device1->CreateDeviceContextState(0,&level,1,D3D11_SDK_VERSION,
        __uuidof(ID3D11Device),nullptr,&state_))) { Reset(); return false; }
    ComPtr<ID3DBlob> code;
    if(FAILED(D3DCompile(Shader,sizeof(Shader)-1,nullptr,nullptr,nullptr,"main","cs_5_0",
        D3DCOMPILE_ENABLE_STRICTNESS,0,&code,nullptr)) ||
       FAILED(device->CreateComputeShader(code->GetBufferPointer(),code->GetBufferSize(),nullptr,&shader_))) { Reset(); return false; }
    D3D11_BUFFER_DESC desc{}; desc.ByteWidth=Bytes; desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
    desc.MiscFlags=D3D11_RESOURCE_MISC_BUFFER_ALLOW_RAW_VIEWS;
    if(FAILED(device->CreateBuffer(&desc,nullptr,&source_))) { Reset(); return false; }
    D3D11_SHADER_RESOURCE_VIEW_DESC srv{}; srv.Format=DXGI_FORMAT_R32_TYPELESS;
    srv.ViewDimension=D3D11_SRV_DIMENSION_BUFFEREX; srv.BufferEx.NumElements=Bytes/4; srv.BufferEx.Flags=D3D11_BUFFEREX_SRV_FLAG_RAW;
    if(FAILED(device->CreateShaderResourceView(source_.Get(),&srv,&sourceView_))) { Reset(); return false; }
    desc.BindFlags=D3D11_BIND_UNORDERED_ACCESS;
    if(FAILED(device->CreateBuffer(&desc,nullptr,&output_))) { Reset(); return false; }
    D3D11_UNORDERED_ACCESS_VIEW_DESC uav{}; uav.Format=DXGI_FORMAT_R32_TYPELESS;
    uav.ViewDimension=D3D11_UAV_DIMENSION_BUFFER; uav.Buffer.NumElements=Bytes/4; uav.Buffer.Flags=D3D11_BUFFER_UAV_FLAG_RAW;
    if(FAILED(device->CreateUnorderedAccessView(output_.Get(),&uav,&outputView_))) { Reset(); return false; }
    desc.BindFlags=D3D11_BIND_CONSTANT_BUFFER; desc.MiscFlags=0;
    if(FAILED(device->CreateBuffer(&desc,nullptr,&constant_))) { Reset(); return false; }
    desc.ByteWidth=sizeof(FrontendShadowMatrix)*2;
    if(FAILED(device->CreateBuffer(&desc,nullptr,&parameters_))) { Reset(); return false; }
    device_=device; thread_=GetCurrentThreadId(); return true;
}

bool FrontendShadow::Draw(ID3D11DeviceContext* context,uint64_t vertex,uint64_t pixel,
    const FrontendShadowMatrix& expected,const FrontendShadowMatrix& corrected,const std::function<void()>& draw) {
    if(busy_ || !context || !draw || context->GetType()!=D3D11_DEVICE_CONTEXT_IMMEDIATE ||
       !IsFrontendShadowShaderPair(vertex,pixel) || !Finite(expected) || !Finite(corrected) || !Pipeline(context) ||
       !Initialize(context)) return false;
    ComPtr<ID3D11Buffer> native; UINT first{},count{};
    context_->PSGetConstantBuffers1(0,1,&native,&first,&count);
    if(!native || first || count<Bytes/16) return false;
    D3D11_BUFFER_DESC desc{}; native->GetDesc(&desc);
    if(desc.ByteWidth!=Bytes || desc.BindFlags!=D3D11_BIND_CONSTANT_BUFFER || desc.MiscFlags || desc.StructureByteStride) return false;
    busy_=true;
    struct ReleaseBusy { bool& flag; ~ReleaseBusy() { flag=false; } } release{busy_};
    {
        ComputeScope state(context_.Get(),state_.Get());
        context_->CopyResource(source_.Get(),native.Get());
        const std::array<FrontendShadowMatrix,2> parameters{expected,corrected};
        context_->UpdateSubresource(parameters_.Get(),0,nullptr,parameters.data(),0,0);
        auto* input=sourceView_.Get(); auto* output=outputView_.Get(); auto* constants=parameters_.Get();
        context_->CSSetShaderResources(0,1,&input); context_->CSSetUnorderedAccessViews(0,1,&output,nullptr);
        context_->CSSetConstantBuffers(0,1,&constants); context_->CSSetShader(shader_.Get(),nullptr,0);
        context_->Dispatch(1,1,1);
        output=nullptr; context_->CSSetUnorderedAccessViews(0,1,&output,nullptr);
        context_->CopyResource(constant_.Get(),output_.Get());
    }
    if(FAILED(device_->GetDeviceRemovedReason())) return false;
    PixelScope restore{context_.Get(),native.Get(),first,count};
    auto* replacement=constant_.Get(); context_->PSSetConstantBuffers(0,1,&replacement);
    ComPtr<ID3D11Buffer> actual; context_->PSGetConstantBuffers(0,1,&actual);
    if(actual!=constant_) return false;
    draw(); return true;
}
void FrontendShadow::Reset() noexcept {
    if(busy_) return;
    state_.Reset(); shader_.Reset(); sourceView_.Reset(); outputView_.Reset();
    source_.Reset(); output_.Reset(); constant_.Reset(); parameters_.Reset();
    context_.Reset(); device_.Reset(); thread_=0;
}
}
