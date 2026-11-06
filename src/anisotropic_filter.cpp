#include "anisotropic_filter.h"
#include <windows.h>
#include <MinHook.h>
#include <intrin.h>
#include <wrl/client.h>
#include <array>
#include <atomic>
#include <mutex>
#include <unordered_map>
#include <vector>

namespace dirt3vr::anisotropy {
namespace {
using Microsoft::WRL::ComPtr;
using CreateSamplerFn=HRESULT (STDMETHODCALLTYPE*)(ID3D11Device*,const D3D11_SAMPLER_DESC*,ID3D11SamplerState**);
using SetViewsFn=void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*,UINT,UINT,ID3D11ShaderResourceView* const*);
using SetSamplersFn=void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*,UINT,UINT,ID3D11SamplerState* const*);
CreateSamplerFn realCreate{};
unsigned strength{};
Scope scope{Scope::All};
bool trace{};
uintptr_t imageBegin{},imageEnd{};
void (*logLine)(const char*,...){};
std::atomic<unsigned> reports{},paletteReports{},contextReports{},traceReports{};

// The colour lookup (IsPalette): each raised sampler with the game's own one behind it, and per device context
// its palette slots. D3D11 uses a context from one thread at a time, so a context's state needs no lock; the
// shared tables take one to change and to look up a sampler.
constexpr unsigned SamplerSlots=D3D11_COMMONSHADER_SAMPLER_SLOT_COUNT;
std::mutex lock;
std::unordered_map<ID3D11SamplerState*,ID3D11SamplerState*> ownOf,raisedOf; // both directions; every sampler held
struct ContextState {
    std::atomic<ID3D11DeviceContext*> context{};
    PaletteSlots slots;
    bool bound{};
};
constexpr unsigned MaxContexts=16;
std::array<ContextState,MaxContexts> contexts{};

// Whether a view shows a palette, decided from its texture the first time it's bound and kept on the view as
// private data (so a released view's address can't be mistaken for it).
constexpr GUID PaletteTag={0x3f1c9a52,0x7d24,0x4e8b,{0x91,0x0e,0x5a,0x6b,0x2c,0x47,0xd8,0x13}};
constexpr GUID TracedTag={0x3f1c9a53,0x7d24,0x4e8b,{0x91,0x0e,0x5a,0x6b,0x2c,0x47,0xd8,0x13}};
bool IsPaletteView(ID3D11ShaderResourceView* view) {
    if(!view) return false;
    uint8_t cached{}; UINT size=sizeof(cached);
    if(SUCCEEDED(view->GetPrivateData(PaletteTag,&size,&cached)) && size==sizeof(cached)) return cached!=0;
    bool palette=false;
    ComPtr<ID3D11Resource> resource; view->GetResource(&resource);
    D3D11_RESOURCE_DIMENSION dimension{};
    if(resource) resource->GetType(&dimension);
    if(dimension==D3D11_RESOURCE_DIMENSION_TEXTURE2D) {
        D3D11_TEXTURE2D_DESC desc{};
        static_cast<ID3D11Texture2D*>(resource.Get())->GetDesc(&desc);
        palette=IsPalette(desc);
    }
    cached=palette ? 1 : 0;
    view->SetPrivateData(PaletteTag,sizeof(cached),&cached);
    if(palette && logLine && paletteReports.fetch_add(1)<16) logLine("Anisotropic filtering: a colour palette is bound to a pixel shader");
    return palette;
}

const char* Kind(ID3D11DeviceContext* context) {
    return context->GetType()==D3D11_DEVICE_CONTEXT_IMMEDIATE ? "immediate" : "deferred";
}

ContextState* StateFor(ID3D11DeviceContext* context) {
    for(auto& state:contexts) if(state.context.load(std::memory_order_acquire)==context) return &state;
    std::lock_guard guard(lock);
    for(auto& state:contexts) if(state.context.load(std::memory_order_relaxed)==context) return &state;
    for(auto& state:contexts) {
        if(state.context.load(std::memory_order_relaxed)) continue;
        state.context.store(context,std::memory_order_release);
        if(logLine && contextReports.fetch_add(1)<MaxContexts) logLine("Anisotropic filtering: pixel shader bindings on a %s context",Kind(context));
        return &state;
    }
    return nullptr;
}

// DIRT3VR_ANISOTROPY_TRACE=1: each distinct texture the pixel shader gets, once, so a run shows what the colour
// grading reads (#24f76866: the 32x256 palettes were created but never bound directly).
std::vector<std::array<UINT,8>> traced;
void Trace(ID3D11DeviceContext* context,UINT slot,ID3D11ShaderResourceView* view) {
    uint8_t seen{}; UINT size=sizeof(seen);
    if(SUCCEEDED(view->GetPrivateData(TracedTag,&size,&seen)) && size==sizeof(seen)) return;
    seen=1;
    view->SetPrivateData(TracedTag,sizeof(seen),&seen);
    ComPtr<ID3D11Resource> resource; view->GetResource(&resource);
    if(!resource) return;
    D3D11_RESOURCE_DIMENSION dimension{};
    resource->GetType(&dimension);
    std::array<UINT,8> shape{UINT(dimension),0,0,0,0,0,0,slot};
    if(dimension==D3D11_RESOURCE_DIMENSION_TEXTURE2D) {
        D3D11_TEXTURE2D_DESC d{}; static_cast<ID3D11Texture2D*>(resource.Get())->GetDesc(&d);
        shape={UINT(dimension),d.Width,d.Height,d.ArraySize,d.MipLevels,UINT(d.Format),d.BindFlags,slot};
    } else if(dimension==D3D11_RESOURCE_DIMENSION_TEXTURE3D) {
        D3D11_TEXTURE3D_DESC d{}; static_cast<ID3D11Texture3D*>(resource.Get())->GetDesc(&d);
        shape={UINT(dimension),d.Width,d.Height,d.Depth,d.MipLevels,UINT(d.Format),d.BindFlags,slot};
    } else if(dimension==D3D11_RESOURCE_DIMENSION_TEXTURE1D) {
        D3D11_TEXTURE1D_DESC d{}; static_cast<ID3D11Texture1D*>(resource.Get())->GetDesc(&d);
        shape={UINT(dimension),d.Width,1,d.ArraySize,d.MipLevels,UINT(d.Format),d.BindFlags,slot};
    }
    {
        std::lock_guard guard(lock);
        for(const auto& known:traced) if(known==shape) return;
        if(traced.size()>=256) return;
        traced.push_back(shape);
    }
    if(logLine && traceReports.fetch_add(1)<256)
        logLine("Anisotropy trace: t%u on %s context: dimension %u %ux%ux%u mips %u format %u bind 0x%x",slot,Kind(context),shape[0],shape[1],shape[2],shape[3],shape[4],shape[5],shape[6]);
}

// Each sampler swapped through table (raised -> own or own -> raised); returns whether any changed.
bool Swap(const std::unordered_map<ID3D11SamplerState*,ID3D11SamplerState*>& table,ID3D11SamplerState** samplers,UINT count) {
    bool changed=false;
    std::lock_guard guard(lock);
    for(UINT i=0;i<count;++i) {
        if(!samplers[i]) continue;
        const auto found=table.find(samplers[i]);
        if(found==table.end()) continue;
        samplers[i]=found->second;
        changed=true;
    }
    return changed;
}

HRESULT STDMETHODCALLTYPE CreateSampler(ID3D11Device* device,const D3D11_SAMPLER_DESC* desc,ID3D11SamplerState** state) {
    const auto caller=reinterpret_cast<uintptr_t>(_ReturnAddress());
    if(!desc || caller<imageBegin || caller>=imageEnd) return realCreate(device,desc,state);
    auto adjusted=*desc;
    if(!Adjust(adjusted,strength) || !InScope(*desc,scope)) return realCreate(device,desc,state);
    const auto hr=realCreate(device,&adjusted,state);
    const auto index=reports.fetch_add(1);
    if(logLine && index<64)
        logLine("Anisotropic filtering: sampler %u filter=0x%x/%u -> 0x%x/%u address=%u/%u/%u lod_bias=%.2f lod=%.2f..%.2f hr=0x%08lx",index,
                unsigned(desc->Filter),desc->MaxAnisotropy,unsigned(adjusted.Filter),adjusted.MaxAnisotropy,unsigned(desc->AddressU),unsigned(desc->AddressV),
                unsigned(desc->AddressW),double(desc->MipLODBias),double(desc->MinLOD),double(desc->MaxLOD),hr);
    // A driver that rejects the optional change still gets the game's own sampler.
    if(FAILED(hr)) return realCreate(device,desc,state);
    // The game's own sampler goes behind the raised one, for the colour lookup.
    if(state && *state) {
        ID3D11SamplerState* own{};
        if(SUCCEEDED(realCreate(device,desc,&own)) && own) {
            std::lock_guard guard(lock);
            if(ownOf.emplace(*state,own).second) { (*state)->AddRef(); raisedOf.emplace(own,*state); }
            else own->Release();
        }
    }
    return hr;
}

// PSSetSamplers: while a palette is bound, the game's own samplers go to the device.
void SetSamplersVia(SetSamplersFn real,ID3D11DeviceContext* context,UINT start,UINT count,ID3D11SamplerState* const* samplers) {
    auto* state=StateFor(context);
    if(!state || !state->bound || !samplers || start>=SamplerSlots || count>SamplerSlots-start) return real(context,start,count,samplers);
    std::array<ID3D11SamplerState*,SamplerSlots> own{};
    for(UINT i=0;i<count;++i) own[i]=samplers[i];
    Swap(ownOf,own.data(),count);
    real(context,start,count,own.data());
}

// PSSetShaderResources (after the real call): tracks palette slots; when that changes, swaps the samplers bound now.
void AfterViews(SetSamplersFn setSamplers,ID3D11DeviceContext* context,UINT start,UINT count,ID3D11ShaderResourceView* const* views) {
    if(start>=PaletteSlots::Slots) return;
    auto* state=StateFor(context);
    if(!state) return;
    const auto slots=count<PaletteSlots::Slots-start ? count : PaletteSlots::Slots-start;
    std::array<bool,PaletteSlots::Slots> palette{};
    for(UINT i=0;i<slots;++i) {
        if(!views || !views[i]) continue;
        if(trace) Trace(context,start+i,views[i]);
        palette[i]=IsPaletteView(views[i]);
    }
    const auto bound=state->slots.Set(start,slots,palette.data());
    if(bound==state->bound) return;
    state->bound=bound;
    // The samplers actually bound now (also after a command list or ClearState): raised ones swapped for the game's
    // own while a palette is bound, and back afterwards; others stay.
    std::array<ID3D11SamplerState*,SamplerSlots> current{};
    context->PSGetSamplers(0,SamplerSlots,current.data());
    auto chosen=current;
    if(Swap(bound ? ownOf : raisedOf,chosen.data(),SamplerSlots)) setSamplers(context,0,SamplerSlots,chosen.data());
    for(auto* sampler:current) if(sampler) sampler->Release();
    if(logLine && paletteReports.fetch_add(1)<16) logLine("Anisotropic filtering: colour palette %s",bound ? "bound, game's own samplers in use" : "unbound, raised samplers back");
}

// The context entry points, once for the immediate context and once more for deferred contexts when d3d11.dll gives
// those their own code.
template<int Context> struct ContextHooks {
    static inline SetViewsFn realViews{};
    static inline SetSamplersFn realSamplers{};
    static void STDMETHODCALLTYPE Views(ID3D11DeviceContext* context,UINT start,UINT count,ID3D11ShaderResourceView* const* views) {
        realViews(context,start,count,views);
        AfterViews(realSamplers,context,start,count,views);
    }
    static void STDMETHODCALLTYPE Samplers(ID3D11DeviceContext* context,UINT start,UINT count,ID3D11SamplerState* const* samplers) {
        SetSamplersVia(realSamplers,context,start,count,samplers);
    }
};
}

bool Install(ID3D11Device* device,void (*log)(const char*,...)) {
    static std::once_flag once;
    static bool installed{},requested{};
    std::call_once(once,[device,log] {
        wchar_t text[16]{};
        const auto length=GetEnvironmentVariableW(L"DIRT3VR_ANISOTROPY",text,8);
        if(!length) return;
        requested=true;
        unsigned value{};
        if(length>=8 || !Parse(text,value)) { log("Anisotropic filtering refused: DIRT3VR_ANISOTROPY is not 0, 1, 2, 4, 8 or 16"); return; }
        if(!value) { requested=false; return; }
        if(!device) { log("Anisotropic filtering refused: no device"); return; }
        wchar_t raise[16]{};
        const auto raiseLength=GetEnvironmentVariableW(L"DIRT3VR_ANISOTROPY_RAISE",raise,16);
        if(raiseLength>=16 || !ParseScope(raiseLength ? raise : nullptr,scope)) log("Anisotropic filtering: DIRT3VR_ANISOTROPY_RAISE isn't all, anisotropic or trilinear; all are raised");
        wchar_t traceText[4]{};
        trace=GetEnvironmentVariableW(L"DIRT3VR_ANISOTROPY_TRACE",traceText,4)==1 && traceText[0]==L'1';
        const auto init=MH_Initialize();
        if(init!=MH_OK && init!=MH_ERROR_ALREADY_INITIALIZED) { log("Anisotropic filtering refused: MinHook=%d",int(init)); return; }
        const auto base=reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
        const auto* nt=reinterpret_cast<const IMAGE_NT_HEADERS*>(base+reinterpret_cast<const IMAGE_DOS_HEADER*>(base)->e_lfanew);
        imageBegin=base;
        imageEnd=base+nt->OptionalHeader.SizeOfImage;
        strength=value;
        logLine=log;
        ComPtr<ID3D11DeviceContext> context;
        device->GetImmediateContext(&context);
        if(!context) { log("Anisotropic filtering refused: no device context"); return; }
        auto** d=*reinterpret_cast<void***>(device);
        auto** c=*reinterpret_cast<void***>(context.Get());
        // The colour lookup's guard comes with the raised samplers or not at all: without it the palettes go green.
        struct Hook { void* target; void* replacement; void** original; };
        std::vector<Hook> hooks{
            {d[23],reinterpret_cast<void*>(&CreateSampler),reinterpret_cast<void**>(&realCreate)},                                  // CreateSamplerState
            {c[8],reinterpret_cast<void*>(&ContextHooks<0>::Views),reinterpret_cast<void**>(&ContextHooks<0>::realViews)},         // PSSetShaderResources
            {c[10],reinterpret_cast<void*>(&ContextHooks<0>::Samplers),reinterpret_cast<void**>(&ContextHooks<0>::realSamplers)}};  // PSSetSamplers
        // Deferred contexts may have their own code in d3d11.dll; a single-threaded device has none.
        ComPtr<ID3D11DeviceContext> deferred;
        const auto deferredResult=device->CreateDeferredContext(0,&deferred);
        if(SUCCEEDED(deferredResult) && deferred) {
            auto** dc=*reinterpret_cast<void***>(deferred.Get());
            const bool own=dc[8]!=c[8] || dc[10]!=c[10];
            if(own && dc[8]!=c[8] && dc[10]!=c[10]) {
                hooks.push_back({dc[8],reinterpret_cast<void*>(&ContextHooks<1>::Views),reinterpret_cast<void**>(&ContextHooks<1>::realViews)});
                hooks.push_back({dc[10],reinterpret_cast<void*>(&ContextHooks<1>::Samplers),reinterpret_cast<void**>(&ContextHooks<1>::realSamplers)});
            }
            log("Anisotropic filtering: deferred contexts %s",!own ? "share the immediate context's code" : (dc[8]!=c[8] && dc[10]!=c[10] ? "have their own code (hooked too)" : "have partly their own code (not hooked)"));
        } else {
            log("Anisotropic filtering: no deferred context (hr=0x%08lx)",deferredResult);
        }
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
            log("Anisotropic filtering refused: hooks not installed (%u of %u created)",unsigned(created),unsigned(hooks.size()));
            return;
        }
        installed=true;
        log("Anisotropic filtering: %ux for the game's mipmapped, repeating samplers (%s); colour palettes keep the game's own%s",value,
            scope==Scope::Anisotropic ? "only the game's anisotropic ones" : scope==Scope::Trilinear ? "only the game's trilinear ones" : "all",trace ? "; texture trace on" : "");
    });
    return installed || !requested;
}

}
