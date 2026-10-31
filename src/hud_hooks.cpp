#include "hud_hooks.h"
#include "hud_shaders.h"
#include "hud_canvas.h"
#include "presentation_trace.h"
#include "presentation_trace_scope.h"
#include "presentation_policy.h"
#include "frontend_shadow_state.h"
#include <MinHook.h>
#include <array>
#include <atomic>
#include <mutex>
#include <cstring>

namespace dirt3vr {
namespace {
constexpr GUID HudTag={0xef9cffb4,0x4fac,0x4d53,{0x94,0x19,0xb2,0x24,0x93,0x8c,0x33,0xa6}};
constexpr GUID ShaderHashTag={0xa38f25d3,0x622e,0x4c97,{0xa9,0x06,0x33,0x2b,0x7f,0x36,0x8e,0xd1}};
thread_local HudCapture* current{};
thread_local unsigned currentEye{};
thread_local bool executingCommands{};
// Borrowed identity only, cleared at Present/stop. Policy checks the live RTV
// before inspecting the backbuffer; no swapchain resource is retained.
thread_local ID3D11Texture2D* presentationBack{};
thread_local bool introVideo{};
thread_local unsigned transitionDraws{};
thread_local FrontendShadow frontendShadow;
thread_local unsigned shadowDraws{},shadowRefusals{};
std::atomic_bool installed{};
DiagnosticLogger logger{};
PresentationTrace presentation;
using CreateVSFn=HRESULT (STDMETHODCALLTYPE*)(ID3D11Device*,const void*,SIZE_T,ID3D11ClassLinkage*,ID3D11VertexShader**);
using CreatePSFn=HRESULT (STDMETHODCALLTYPE*)(ID3D11Device*,const void*,SIZE_T,ID3D11ClassLinkage*,ID3D11PixelShader**);
using CreateLayoutFn=HRESULT (STDMETHODCALLTYPE*)(ID3D11Device*,const D3D11_INPUT_ELEMENT_DESC*,UINT,const void*,SIZE_T,ID3D11InputLayout**);
using CreateBufferFn=HRESULT (STDMETHODCALLTYPE*)(ID3D11Device*,const D3D11_BUFFER_DESC*,const D3D11_SUBRESOURCE_DATA*,ID3D11Buffer**);
using MapFn=HRESULT (STDMETHODCALLTYPE*)(ID3D11DeviceContext*,ID3D11Resource*,UINT,D3D11_MAP,UINT,D3D11_MAPPED_SUBRESOURCE*);
using UnmapFn=void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*,ID3D11Resource*,UINT);
using UpdateFn=void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*,ID3D11Resource*,UINT,const D3D11_BOX*,const void*,UINT,UINT);
using CopyFn=void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*,ID3D11Resource*,ID3D11Resource*);
using CopyRegionFn=void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*,ID3D11Resource*,UINT,UINT,UINT,UINT,ID3D11Resource*,UINT,const D3D11_BOX*);
using ExecuteFn=void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*,ID3D11CommandList*,BOOL);
using DrawFn=void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*,UINT,UINT);
using IndexedFn=void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*,UINT,UINT,INT);
using InstancedFn=void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*,UINT,UINT,UINT,UINT);
using IndexedInstancedFn=void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*,UINT,UINT,UINT,INT,UINT);
using AutoFn=void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*);
using IndirectFn=void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*,ID3D11Buffer*,UINT);
CreateVSFn realVS{};
CreatePSFn realPS{};
CreateLayoutFn realLayout{};
CreateBufferFn realBuffer{};
MapFn realMap{}; UnmapFn realUnmap{}; UpdateFn realUpdate{}; CopyFn realCopy{}; CopyRegionFn realCopyRegion{};
ExecuteFn realExecute{};
struct MappedCamera { ID3D11DeviceContext* context{}; ID3D11Resource* resource{}; const void* bytes{}; };
thread_local std::array<MappedCamera,8> mappedCameras{};
DrawFn realDraw{}; IndexedFn realIndexed{};
InstancedFn realInstanced{}; IndexedInstancedFn realIndexedInstanced{};
AutoFn realAuto{}; IndirectFn realIndirect{},realIndexedIndirect{};
void* vertexTarget{};
uint64_t ShaderHash(ID3D11DeviceChild* shader) {
    uint64_t hash{}; UINT size=sizeof(hash);
    return shader && SUCCEEDED(shader->GetPrivateData(ShaderHashTag,&size,&hash)) && size==sizeof(hash) ? hash : 0;
}
template<class F> void Route(ID3D11DeviceContext* context,const char* name,UINT count,UINT instances,UINT first,INT base,UINT startInstance,const F& draw) {
    const bool trace=presentation.Enabled() && !PresentationTraceSuppressed && !executingCommands;
    const bool nativePresentation=presentationBack && !PresentationTraceSuppressed && !executingCommands;
    if((current || trace || nativePresentation) && !executingCommands) {
        Microsoft::WRL::ComPtr<ID3D11VertexShader> shader;
        context->VSGetShader(&shader,nullptr,nullptr);
        const auto kind=QueryHudShader(shader.Get());
        const auto vs=ShaderHash(shader.Get());
        const bool presentationCandidate=nativePresentation && count==4 && instances==1 && !first && !base && !startInstance &&
            std::strcmp(name,"draw")==0 && (current ? vs==StereoTransitionVertexHash : vs==IntroVideoVertexHash);
        const bool traceDraw=trace && (!current || kind==HudShaderKind::None) && presentation.Wants(vs,count,instances);
        const bool probe=current && currentEye==0 && current->Probe().Active();
        const bool shadowCandidate=current && vs==0x4b2b0f44adfbf5bbull && count==4 && instances==1 &&
            !first && !base && !startInstance && std::strcmp(name,"draw")==0;
        Microsoft::WRL::ComPtr<ID3D11PixelShader> pixel;
        if(traceDraw || probe || kind!=HudShaderKind::None || presentationCandidate || shadowCandidate) context->PSGetShader(&pixel,nullptr,nullptr);
        const auto ps=ShaderHash(pixel.Get());
        if(traceDraw) presentation.Record(context,vs,ps,name,count,instances,first,base,startInstance);
        if(probe) current->Probe().Record(context,current->Backbuffer(),vs,ps,name,count,instances,first,base,startInstance);
        if(shadowCandidate && IsFrontendShadowShaderPair(vs,ps)) {
            FrontendShadowMatrix expected{},corrected{};
            if(FrontendShadowMatrices(expected,corrected) && frontendShadow.Draw(context,vs,ps,expected,corrected,draw)) {
                if(++shadowDraws<=4 && logger) logger("XR frontend shadow guarded draw eye=%u ps=%016llx; GPU matrix-match guard active",currentEye,ps);
                return;
            }
            if(++shadowRefusals<=4 && logger) logger("XR frontend shadow draw retained native constants eye=%u ps=%016llx",currentEye,ps);
        }
        if(presentationCandidate && current && IsStereoTransitionDraw(context,presentationBack,vs,ps,count,instances)) {
            ++transitionDraws; return;
        }
        if(presentationCandidate && !current && IsIntroVideoDraw(context,presentationBack,vs,ps,count,instances)) {
            draw(); introVideo=true; return;
        }
        if(current && kind!=HudShaderKind::None && current->Draw(context,draw,kind,currentEye==0,vs,ps)) return;
    }
    draw();
}
bool CameraBuffer(ID3D11Resource* resource) {
    if(!resource) return false;
    D3D11_RESOURCE_DIMENSION type{}; resource->GetType(&type);
    if(type!=D3D11_RESOURCE_DIMENSION_BUFFER) return false;
    D3D11_BUFFER_DESC desc{}; static_cast<ID3D11Buffer*>(resource)->GetDesc(&desc);
    return desc.ByteWidth==320 && desc.BindFlags==D3D11_BIND_CONSTANT_BUFFER;
}
void InvalidateCamera(ID3D11Resource* resource) {
    if(CameraBuffer(resource)) resource->SetPrivateData(HudCameraTag,0,nullptr);
}
void SaveCamera(ID3D11Resource* resource,const void* bytes) {
    if(executingCommands) { InvalidateCamera(resource); return; }
    HudCameraShadow shadow; shadow.generation=HudCameraGeneration.load(std::memory_order_relaxed);
    std::memcpy(shadow.bytes.data(),bytes,shadow.bytes.size());
    resource->SetPrivateData(HudCameraTag,sizeof(shadow),&shadow);
}
HRESULT STDMETHODCALLTYPE CreateBuffer(ID3D11Device* device,const D3D11_BUFFER_DESC* desc,const D3D11_SUBRESOURCE_DATA* data,ID3D11Buffer** buffer) {
    const auto result=realBuffer(device,desc,data,buffer);
    if(SUCCEEDED(result) && buffer && *buffer && data && data->pSysMem && CameraBuffer(*buffer))
        SaveCamera(*buffer,data->pSysMem);
    return result;
}
HRESULT STDMETHODCALLTYPE Map(ID3D11DeviceContext* context,ID3D11Resource* resource,UINT sub,D3D11_MAP mode,UINT flags,D3D11_MAPPED_SUBRESOURCE* mapped) {
    const auto result=realMap(context,resource,sub,mode,flags,mapped);
    if(SUCCEEDED(result) && sub==0 && mode!=D3D11_MAP_READ && mapped && mapped->pData && CameraBuffer(resource)) {
        InvalidateCamera(resource);
        if(context->GetType()==D3D11_DEVICE_CONTEXT_IMMEDIATE) for(auto& entry:mappedCameras) if(!entry.resource) {
            entry={context,resource,mapped->pData}; break;
        }
    }
    return result;
}
void STDMETHODCALLTYPE Unmap(ID3D11DeviceContext* context,ID3D11Resource* resource,UINT sub) {
    if(sub==0) for(auto& entry:mappedCameras) if(entry.context==context && entry.resource==resource) {
        SaveCamera(resource,entry.bytes); entry={}; break;
    }
    realUnmap(context,resource,sub);
}
void STDMETHODCALLTYPE Update(ID3D11DeviceContext* context,ID3D11Resource* resource,UINT sub,const D3D11_BOX* box,const void* data,UINT row,UINT depth) {
    realUpdate(context,resource,sub,box,data,row,depth);
    if(CameraBuffer(resource)) {
        InvalidateCamera(resource);
        if(!sub && !box && data && context->GetType()==D3D11_DEVICE_CONTEXT_IMMEDIATE) SaveCamera(resource,data);
    }
}
void STDMETHODCALLTYPE Copy(ID3D11DeviceContext* context,ID3D11Resource* to,ID3D11Resource* from) {
    InvalidateCamera(to); realCopy(context,to,from);
}
void STDMETHODCALLTYPE CopyRegion(ID3D11DeviceContext* context,ID3D11Resource* to,UINT sub,UINT x,UINT y,UINT z,ID3D11Resource* from,UINT fromSub,const D3D11_BOX* box) {
    InvalidateCamera(to); realCopyRegion(context,to,sub,x,y,z,from,fromSub,box);
}
void STDMETHODCALLTYPE Execute(ID3D11DeviceContext* context,ID3D11CommandList* commands,BOOL restore) {
    HudCameraGeneration.fetch_add(1,std::memory_order_relaxed);
    // WARP can replay deferred writes through the immediate upload entry point.
    // Such replay is not an independently observed CPU update or UI draw.
    struct RestoreExecution { bool previous; ~RestoreExecution() { executingCommands=previous; } } scope{executingCommands};
    executingCommands=true;
    realExecute(context,commands,restore);
}
HRESULT STDMETHODCALLTYPE CreateVS(ID3D11Device* device,const void* bytes,SIZE_T size,ID3D11ClassLinkage* link,ID3D11VertexShader** shader) {
    const auto result=realVS(device,bytes,size,link,shader);
    if(SUCCEEDED(result) && shader && *shader && bytes && size<=1024*1024) {
        uint64_t hash=14695981039346656037ull;
        for(SIZE_T i=0;i<size;++i) hash=(hash^static_cast<const unsigned char*>(bytes)[i])*1099511628211ull;
        (*shader)->SetPrivateData(ShaderHashTag,sizeof(hash),&hash);
        const auto kind=ClassifyHudShader(hash);
        if(kind!=HudShaderKind::None) {
            const auto tagged=(*shader)->SetPrivateData(HudTag,sizeof(kind),&kind);
            if(logger) logger("HUD shader hash=%016llx kind=%u tagged=%u",hash,unsigned(kind),SUCCEEDED(tagged));
        }
    }
    return result;
}
HRESULT STDMETHODCALLTYPE CreatePS(ID3D11Device* device,const void* bytes,SIZE_T size,ID3D11ClassLinkage* link,ID3D11PixelShader** shader) {
    const auto result=realPS(device,bytes,size,link,shader);
    if(SUCCEEDED(result) && shader && *shader && bytes && size<=1024*1024) {
        uint64_t hash=14695981039346656037ull;
        for(SIZE_T i=0;i<size;++i) hash=(hash^static_cast<const unsigned char*>(bytes)[i])*1099511628211ull;
        (*shader)->SetPrivateData(ShaderHashTag,sizeof(hash),&hash);
    }
    return result;
}
HRESULT STDMETHODCALLTYPE CreateLayout(ID3D11Device* device,const D3D11_INPUT_ELEMENT_DESC* elements,UINT count,const void* bytes,SIZE_T size,ID3D11InputLayout** layout) {
    const auto result=realLayout(device,elements,count,bytes,size,layout);
    if(SUCCEEDED(result) && layout && *layout && elements && count<=32) {
        ProbeLayout metadata; metadata.count=count;
        for(UINT i=0;i<count;++i) {
            const auto& e=elements[i]; auto& p=metadata.elements[i];
            if(e.SemanticName) strncpy_s(p.semantic,e.SemanticName,_TRUNCATE);
            p.index=e.SemanticIndex; p.format=e.Format; p.slot=e.InputSlot; p.offset=e.AlignedByteOffset;
            p.classification=e.InputSlotClass; p.step=e.InstanceDataStepRate;
        }
        (*layout)->SetPrivateData(ProbeLayoutTag,sizeof(metadata),&metadata);
    }
    return result;
}
void STDMETHODCALLTYPE Draw(ID3D11DeviceContext* c,UINT n,UINT first) { Route(c,"draw",n,1,first,0,0,[&]{realDraw(c,n,first);}); }
void STDMETHODCALLTYPE Indexed(ID3D11DeviceContext* c,UINT n,UINT first,INT base) { Route(c,"indexed",n,1,first,base,0,[&]{realIndexed(c,n,first,base);}); }
void STDMETHODCALLTYPE Instanced(ID3D11DeviceContext* c,UINT n,UINT instances,UINT first,UINT start) { Route(c,"instanced",n,instances,first,0,start,[&]{realInstanced(c,n,instances,first,start);}); }
void STDMETHODCALLTYPE IndexedInstanced(ID3D11DeviceContext* c,UINT n,UINT instances,UINT first,INT base,UINT start) { Route(c,"indexed-instanced",n,instances,first,base,start,[&]{realIndexedInstanced(c,n,instances,first,base,start);}); }
void STDMETHODCALLTYPE Auto(ID3D11DeviceContext* c) { Route(c,"auto",0,0,0,0,0,[&]{realAuto(c);}); }
void STDMETHODCALLTYPE Indirect(ID3D11DeviceContext* c,ID3D11Buffer* b,UINT offset) { Route(c,"indirect",0,0,offset,0,0,[&]{realIndirect(c,b,offset);}); }
void STDMETHODCALLTYPE IndexedIndirect(ID3D11DeviceContext* c,ID3D11Buffer* b,UINT offset) { Route(c,"indexed-indirect",0,0,offset,0,0,[&]{realIndexedIndirect(c,b,offset);}); }
}
bool HudHooksReady() noexcept { return installed.load(std::memory_order_acquire); }
void BindPresentationBackbuffer(ID3D11Texture2D* back) noexcept { presentationBack=back; presentation.BindBackbuffer(back); }
void TraceBeforePresent() noexcept { presentation.BeforePresent(); }
bool IntroVideoDrawn() noexcept { return introVideo; }
unsigned TransitionDrawsSuppressed() noexcept { return transitionDraws; }
void EndPresentationFrame() noexcept { introVideo=false; transitionDraws=0; presentationBack=nullptr; EndFrontendShadowFrame(); }
void ResetFrontendShadowResources() noexcept { frontendShadow.Reset(); }
HudShaderKind QueryHudShader(ID3D11VertexShader* shader) noexcept {
    HudShaderKind kind{}; UINT size=sizeof(kind);
    return shader && SUCCEEDED(shader->GetPrivateData(HudTag,&size,&kind)) && size==sizeof(kind) &&
        (kind==HudShaderKind::TwoDimensional || kind==HudShaderKind::Shared) ? kind : HudShaderKind::None;
}
bool InstallHudHooks(ID3D11Device* device,DiagnosticLogger log) noexcept {
    if(!device || !log) return false;
    static std::mutex mutex;
    try {
        std::lock_guard lock(mutex);
        auto** v=*reinterpret_cast<void***>(device);
        if(HudHooksReady()) return vertexTarget==v[12];
        Microsoft::WRL::ComPtr<ID3D11DeviceContext> context; device->GetImmediateContext(&context);
        auto** c=*reinterpret_cast<void***>(context.Get());
        struct Hook { void* target; void* replacement; void** original; };
        const std::array<Hook,17> hooks{{
            {v[3],reinterpret_cast<void*>(&CreateBuffer),reinterpret_cast<void**>(&realBuffer)},
            {v[11],reinterpret_cast<void*>(&CreateLayout),reinterpret_cast<void**>(&realLayout)},
            {v[12],reinterpret_cast<void*>(&CreateVS),reinterpret_cast<void**>(&realVS)},
            {v[15],reinterpret_cast<void*>(&CreatePS),reinterpret_cast<void**>(&realPS)},
            {c[12],reinterpret_cast<void*>(&Indexed),reinterpret_cast<void**>(&realIndexed)},
            {c[13],reinterpret_cast<void*>(&Draw),reinterpret_cast<void**>(&realDraw)},
            {c[14],reinterpret_cast<void*>(&Map),reinterpret_cast<void**>(&realMap)},
            {c[15],reinterpret_cast<void*>(&Unmap),reinterpret_cast<void**>(&realUnmap)},
            {c[20],reinterpret_cast<void*>(&IndexedInstanced),reinterpret_cast<void**>(&realIndexedInstanced)},
            {c[21],reinterpret_cast<void*>(&Instanced),reinterpret_cast<void**>(&realInstanced)},
            {c[38],reinterpret_cast<void*>(&Auto),reinterpret_cast<void**>(&realAuto)},
            {c[39],reinterpret_cast<void*>(&IndexedIndirect),reinterpret_cast<void**>(&realIndexedIndirect)},
            {c[40],reinterpret_cast<void*>(&Indirect),reinterpret_cast<void**>(&realIndirect)},
            {c[46],reinterpret_cast<void*>(&CopyRegion),reinterpret_cast<void**>(&realCopyRegion)},
            {c[47],reinterpret_cast<void*>(&Copy),reinterpret_cast<void**>(&realCopy)},
            {c[48],reinterpret_cast<void*>(&Update),reinterpret_cast<void**>(&realUpdate)},
            {c[58],reinterpret_cast<void*>(&Execute),reinterpret_cast<void**>(&realExecute)}
        }};
        const auto init=MH_Initialize();
        if(init!=MH_OK && init!=MH_ERROR_ALREADY_INITIALIZED) return false;
        size_t created{};
        for(const auto& hook:hooks) {
            if(MH_CreateHook(hook.target,hook.replacement,hook.original)!=MH_OK) break;
            ++created;
        }
        bool ready=created==hooks.size();
        for(size_t i=0;ready && i<created;++i) ready=MH_QueueEnableHook(hooks[i].target)==MH_OK;
        ready=ready && MH_ApplyQueued()==MH_OK;
        if(!ready) {
            for(size_t i=0;i<created;++i) { MH_DisableHook(hooks[i].target); MH_RemoveHook(hooks[i].target); }
            log("HUD draw hooks unavailable; VR initialization refused"); return false;
        }
        logger=log; vertexTarget=v[12]; installed.store(true,std::memory_order_release);
        wchar_t traceFlag[8]{},directory[32768]{};
        if(GetEnvironmentVariableW(L"DIRT3VR_PRESENTATION_TRACE",traceFlag,8)==1 && traceFlag[0]==L'1') {
            const auto length=GetEnvironmentVariableW(L"DIRT3VR_LOG_DIR",directory,32768);
            if(length && length<32768) presentation.Configure(directory);
            log("Presentation trace requested configured=%u; automatic metadata only, no GPU readback",presentation.Active());
        }
        log("HUD shader observation and draw routing installed; inactive outside stereo eye scopes");
        return true;
    } catch(...) { return false; }
}
HudDrawScope::HudDrawScope(HudCapture& capture,unsigned eye) noexcept : previous_(current),previousEye_(currentEye) {
    current=&capture; currentEye=eye;
    presentation.BeginEye(eye);
}
HudDrawScope::~HudDrawScope() {
    current=previous_; currentEye=previousEye_;
    if(current) presentation.BeginEye(currentEye); else presentation.EndEye();
}
}
