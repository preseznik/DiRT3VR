#include "hud_capture.h"
#include "headset_resolution.h"
#include <array>
#include <cstring>

namespace dirt3vr {
using Microsoft::WRL::ComPtr;
namespace {
// UI routing changes only targets and blend state. Depth/stencil is left live:
// native stencil mask writes must still happen once in each native eye pass.
struct RestoreOutput {
    ID3D11DeviceContext* context;
    std::array<ComPtr<ID3D11RenderTargetView>,8> targets;
    ID3D11RenderTargetView* raw[8]{};
    ComPtr<ID3D11DepthStencilView> depth;
    ComPtr<ID3D11BlendState> blend;
    float factors[4]{};
    UINT mask{};
    bool changed{};
    explicit RestoreOutput(ID3D11DeviceContext* c) : context(c) {
        c->OMGetRenderTargets(8,raw,&depth);
        for(unsigned i=0;i<8;++i) targets[i].Attach(raw[i]);
        c->OMGetBlendState(&blend,factors,&mask);
    }
    ~RestoreOutput() {
        if(!changed) return;
        context->OMSetRenderTargets(8,raw,depth.Get());
        context->OMSetBlendState(blend.Get(),factors,mask);
    }
};
struct RestoreCanvas {
    HudCanvas& canvas;
    ID3D11DeviceContext* context;
    ~RestoreCanvas() { canvas.Restore(context); }
};
bool WritesStencil(const D3D11_DEPTH_STENCIL_DESC& d) {
    const auto writes=[](const D3D11_DEPTH_STENCILOP_DESC& face) {
        return face.StencilFailOp!=D3D11_STENCIL_OP_KEEP || face.StencilDepthFailOp!=D3D11_STENCIL_OP_KEEP ||
               face.StencilPassOp!=D3D11_STENCIL_OP_KEEP;
    };
    return d.StencilEnable && d.StencilWriteMask && (writes(d.FrontFace) || writes(d.BackFace));
}
}
bool HudCapture::Begin(ID3D11Texture2D* back,uint64_t frame) noexcept {
    End(); ready_=failed_=false; draws_=suppressed_=canvasDraws_=0;
    if(!back) return false;
    D3D11_TEXTURE2D_DESC desc{}; back->GetDesc(&desc);
    if(!ValidHeadsetSize(desc.Width,desc.Height) || desc.SampleDesc.Count!=1 || desc.ArraySize!=1 || desc.MipLevels!=1) return false;
    ComPtr<ID3D11Device> device,oldDevice; back->GetDevice(&device);
    if(image_) image_->GetDevice(&oldDevice);
    if(!image_ || device.Get()!=oldDevice.Get() || width_!=desc.Width || height_!=desc.Height) {
        // Eight-bit alpha avoids quantizing the interface to the game's 2-bit alpha.
        desc.Format=DXGI_FORMAT_R8G8B8A8_UNORM;
        desc.Usage=D3D11_USAGE_DEFAULT; desc.CPUAccessFlags=desc.MiscFlags=0;
        desc.BindFlags=D3D11_BIND_RENDER_TARGET|D3D11_BIND_SHADER_RESOURCE;
        ComPtr<ID3D11Texture2D> image; ComPtr<ID3D11RenderTargetView> target;
        if(FAILED(device->CreateTexture2D(&desc,nullptr,&image)) || FAILED(device->CreateRenderTargetView(image.Get(),nullptr,&target))) return false;
        image_=image; target_=target; width_=desc.Width; height_=desc.Height;
        blend_.Reset(); maskBlend_.Reset();
    }
    ComPtr<ID3D11DeviceContext> context; device->GetImmediateContext(&context);
    const float clear[4]{}; context->ClearRenderTargetView(target_.Get(),clear);
    back_=back; frame_=frame; active_=true;
    DWORD process{}; GetWindowThreadProcessId(GetForegroundWindow(),&process);
    const bool pressed=process==GetCurrentProcessId() && (GetAsyncKeyState(VK_F8)&0x8000);
    if(pressed && !probeHeld_) {
        try {
            wchar_t directory[32768]{};
            const auto length=GetEnvironmentVariableW(L"DIRT3VR_LOG_DIR",directory,32768);
            if(length && length<32768) probe_.Begin(directory,frame);
        } catch(...) { } // A diagnostic path/allocation failure cannot stop VR.
    }
    probeHeld_=pressed;
    return true;
}
void HudCapture::End() noexcept {
    if(active_) ready_=!failed_ && draws_!=0;
    probe_.Finish(active_ && ready_ ? image_.Get() : nullptr);
    active_=false; back_.Reset();
}
void HudCapture::Reset() noexcept {
    End(); canvas_.Reset(); image_.Reset(); target_.Reset(); blend_.Reset(); maskBlend_.Reset();
    width_=height_=draws_=suppressed_=canvasDraws_=0; ready_=failed_=false;
}
ID3D11Texture2D* HudCapture::Current(uint64_t frame) const noexcept {
    return ready_ && !active_ && !failed_ && frame==frame_ ? image_.Get() : nullptr;
}
bool HudCapture::Draw(ID3D11DeviceContext* context,const std::function<void()>& draw,HudShaderKind kind,bool firstEye,uint64_t vertexHash,uint64_t pixelHash) {
    if(!active_ || failed_ || kind==HudShaderKind::None || !context ||
       context->GetType()!=D3D11_DEVICE_CONTEXT_IMMEDIATE) return false;
    RestoreOutput restore(context);
    if(!restore.targets[0]) return false;
    for(unsigned i=1;i<8;++i) if(restore.targets[i]) return false;
    ComPtr<ID3D11Resource> resource; restore.targets[0]->GetResource(&resource);
    if(resource.Get()!=back_.Get()) return false;
    std::array<ID3D11UnorderedAccessView*,D3D11_1_UAV_SLOT_COUNT> unordered{};
    ComPtr<ID3D11Device> device; context->GetDevice(&device);
    const UINT slots=device->GetFeatureLevel()>=D3D_FEATURE_LEVEL_11_1 ? D3D11_1_UAV_SLOT_COUNT : D3D11_PS_CS_UAV_REGISTER_COUNT;
    context->OMGetRenderTargetsAndUnorderedAccessViews(0,nullptr,nullptr,0,slots,unordered.data());
    bool unorderedBound{};
    for(auto* view:unordered) if(view) { unorderedBound=true; view->Release(); }
    if(unorderedBound) return false;
    ComPtr<ID3D11DepthStencilState> state; UINT stencil{};
    context->OMGetDepthStencilState(&state,&stencil);
    D3D11_DEPTH_STENCIL_DESC depth{};
    if(state) state->GetDesc(&depth);
    else { depth.DepthEnable=TRUE; depth.DepthWriteMask=D3D11_DEPTH_WRITE_MASK_ALL; }
    if(restore.depth && depth.DepthEnable &&
       (kind==HudShaderKind::Shared || depth.DepthWriteMask!=D3D11_DEPTH_WRITE_MASK_ZERO)) return false;
    D3D11_BLEND_DESC blend{};
    if(restore.blend) restore.blend->GetDesc(&blend);
    else {
        auto& rt=blend.RenderTarget[0]; rt.SrcBlend=rt.SrcBlendAlpha=D3D11_BLEND_ONE;
        rt.DestBlend=rt.DestBlendAlpha=D3D11_BLEND_ZERO; rt.BlendOp=rt.BlendOpAlpha=D3D11_BLEND_OP_ADD;
        rt.RenderTargetWriteMask=D3D11_COLOR_WRITE_ENABLE_ALL;
    }
    auto& rt=blend.RenderTarget[0];
    const bool maskOnly=!(rt.RenderTargetWriteMask&7);
    // Only ordinary source-over/opaque UI is compositable independently.
    // Destination-colour and additive effects remain in the world for now.
    if(!maskOnly && rt.BlendEnable && (rt.BlendOp!=D3D11_BLEND_OP_ADD ||
       (rt.SrcBlend!=D3D11_BLEND_SRC_ALPHA && rt.SrcBlend!=D3D11_BLEND_ONE) ||
       rt.DestBlend!=D3D11_BLEND_INV_SRC_ALPHA)) return false;
    HudCameraShadow camera{}; UINT cameraSize=sizeof(camera);
    ComPtr<ID3D11Buffer> cameraBuffer;
    if(vertexHash) context->VSGetConstantBuffers(3,1,&cameraBuffer);
    auto correction=HudCanvas::Result::Unchanged;
    if(cameraBuffer && SUCCEEDED(cameraBuffer->GetPrivateData(HudCameraTag,&cameraSize,&camera)) && cameraSize==sizeof(camera) &&
       camera.generation==HudCameraGeneration.load(std::memory_order_relaxed))
        correction=canvas_.Apply(context,vertexHash,pixelHash,camera.bytes,width_,height_);
    RestoreCanvas canvasRestore{canvas_,context};
    if(correction==HudCanvas::Result::Failed) { failed_=true; return false; }
    // A frame can contain menu-camera draws and native HUD instruments. Fit
    // uncorrected draws to the same canvas rather than stretching their pixels
    // when any menu draw causes the shared panel to become widescreen.
    bool nativeFit{};
    if(correction==HudCanvas::Result::Unchanged && (!maskOnly || kind==HudShaderKind::TwoDimensional)) {
        const auto fit=canvas_.FitNative(context,width_,height_);
        if(fit==HudCanvas::Result::Failed) { failed_=true; return false; }
        if(fit!=HudCanvas::Result::Applied) return false;
        nativeFit=true;
    }
    if(maskOnly) {
        if(correction!=HudCanvas::Result::Applied && !nativeFit) return false;
        // Native stencil masks must use the same canvas as the visible UI.
        try { draw(); } catch(...) { failed_=true; throw; }
        return true;
    }
    if(!firstEye) {
        if(restore.depth && WritesStencil(depth)) {
            if(!maskBlend_) {
                D3D11_BLEND_DESC mask{}; mask.RenderTarget[0].RenderTargetWriteMask=0;
                if(FAILED(device->CreateBlendState(&mask,&maskBlend_))) { failed_=true; return false; }
            }
            restore.changed=true;
            context->OMSetBlendState(maskBlend_.Get(),restore.factors,restore.mask);
            try { draw(); } catch(...) { failed_=true; throw; }
        }
        ++suppressed_; return true;
    }
    if(!rt.BlendEnable) {
        rt.SrcBlend=D3D11_BLEND_ONE; rt.DestBlend=D3D11_BLEND_ZERO; rt.BlendOp=D3D11_BLEND_OP_ADD;
        rt.DestBlendAlpha=D3D11_BLEND_ZERO;
    } else rt.DestBlendAlpha=D3D11_BLEND_INV_SRC_ALPHA;
    rt.BlendEnable=TRUE; rt.SrcBlendAlpha=D3D11_BLEND_ONE; rt.BlendOpAlpha=D3D11_BLEND_OP_ADD;
    rt.RenderTargetWriteMask|=D3D11_COLOR_WRITE_ENABLE_ALPHA;
    if(!blend_ || std::memcmp(&blend,&blendDesc_,sizeof(blend))) {
        ComPtr<ID3D11BlendState> replacement;
        if(FAILED(device->CreateBlendState(&blend,&replacement))) { failed_=true; return false; }
        blend_=replacement; blendDesc_=blend;
    }
    auto* target=target_.Get();
    restore.changed=true;
    context->OMSetRenderTargets(1,&target,restore.depth.Get());
    context->OMSetBlendState(blend_.Get(),restore.factors,restore.mask);
    try {
        draw(); ++draws_;
        if(correction==HudCanvas::Result::Applied) ++canvasDraws_;
    } catch(...) { failed_=true; throw; }
    return true;
}
}
