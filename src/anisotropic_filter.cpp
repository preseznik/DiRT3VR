#include "anisotropic_filter.h"
#include <windows.h>
#include <MinHook.h>
#include <intrin.h>
#include <wrl/client.h>
#include <array>
#include <atomic>
#include <mutex>
#include <unordered_map>

namespace dirt3vr::anisotropy {
namespace {
using CreateSamplerFn=HRESULT (STDMETHODCALLTYPE*)(ID3D11Device*,const D3D11_SAMPLER_DESC*,ID3D11SamplerState**);
using CreateViewFn=HRESULT (STDMETHODCALLTYPE*)(ID3D11Device*,ID3D11Resource*,const D3D11_SHADER_RESOURCE_VIEW_DESC*,ID3D11ShaderResourceView**);
using SetViewsFn=void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*,UINT,UINT,ID3D11ShaderResourceView* const*);
using SetSamplersFn=void (STDMETHODCALLTYPE*)(ID3D11DeviceContext*,UINT,UINT,ID3D11SamplerState* const*);
CreateSamplerFn realCreate{};
CreateViewFn realCreateView{};
SetViewsFn realSetViews{};
SetSamplersFn realSetSamplers{};
unsigned strength{};
uintptr_t imageBegin{},imageEnd{};
void (*logLine)(const char*,...){};
std::atomic<unsigned> reports{},paletteReports{};

// The colour lookup (IsPalette): each raised sampler with the game's own one behind it, the palette views, and
// per device context its palette slots. D3D11 uses a context from one thread at a time, so a context's state
// needs no lock; the shared tables take one to change and to look up a sampler.
constexpr unsigned SamplerSlots=D3D11_COMMONSHADER_SAMPLER_SLOT_COUNT;
std::mutex lock;
std::unordered_map<ID3D11SamplerState*,ID3D11SamplerState*> ownOf,raisedOf; // both directions; every sampler held
constexpr unsigned MaxPalettes=64;
std::array<std::atomic<ID3D11ShaderResourceView*>,MaxPalettes> palettes{};
std::atomic<unsigned> paletteCount{};
struct ContextState {
    std::atomic<ID3D11DeviceContext*> context{};
    PaletteSlots slots;
    bool bound{};
};
constexpr unsigned MaxContexts=16;
std::array<ContextState,MaxContexts> contexts{};

bool IsPaletteView(ID3D11ShaderResourceView* view) {
    if(!view) return false;
    const auto count=paletteCount.load(std::memory_order_acquire);
    for(unsigned i=0;i<count;++i) if(palettes[i].load(std::memory_order_relaxed)==view) return true;
    return false;
}

ContextState* StateFor(ID3D11DeviceContext* context) {
    for(auto& state:contexts) if(state.context.load(std::memory_order_acquire)==context) return &state;
    std::lock_guard guard(lock);
    for(auto& state:contexts) if(state.context.load(std::memory_order_relaxed)==context) return &state;
    for(auto& state:contexts) {
        if(state.context.load(std::memory_order_relaxed)) continue;
        state.context.store(context,std::memory_order_release);
        return &state;
    }
    return nullptr;
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
    if(!Adjust(adjusted,strength)) return realCreate(device,desc,state);
    const auto hr=realCreate(device,&adjusted,state);
    if(logLine && reports.fetch_add(1)<16)
        logLine("Anisotropic filtering: sampler filter=0x%x/%u -> 0x%x/%u hr=0x%08lx",unsigned(desc->Filter),desc->MaxAnisotropy,
                unsigned(adjusted.Filter),adjusted.MaxAnisotropy,hr);
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

HRESULT STDMETHODCALLTYPE CreateView(ID3D11Device* device,ID3D11Resource* resource,const D3D11_SHADER_RESOURCE_VIEW_DESC* desc,ID3D11ShaderResourceView** view) {
    const auto hr=realCreateView(device,resource,desc,view);
    if(FAILED(hr) || !resource || !view || !*view) return hr;
    D3D11_RESOURCE_DIMENSION dimension{};
    resource->GetType(&dimension);
    if(dimension!=D3D11_RESOURCE_DIMENSION_TEXTURE2D) return hr;
    D3D11_TEXTURE2D_DESC texture{};
    static_cast<ID3D11Texture2D*>(resource)->GetDesc(&texture);
    if(!IsPalette(texture)) return hr;
    std::lock_guard guard(lock);
    const auto count=paletteCount.load(std::memory_order_relaxed);
    if(count>=MaxPalettes) {
        if(logLine && paletteReports.fetch_add(1)<16) logLine("Anisotropic filtering: more than %u colour palettes; the rest keep the raised samplers",MaxPalettes);
        return hr;
    }
    (*view)->AddRef(); // A palette view keeps its address for the session.
    palettes[count].store(*view,std::memory_order_relaxed);
    paletteCount.store(count+1,std::memory_order_release);
    if(logLine && paletteReports.fetch_add(1)<16) logLine("Anisotropic filtering: colour palette %u (32x256, format %u) read with the game's own samplers",count+1,unsigned(texture.Format));
    return hr;
}

void STDMETHODCALLTYPE SetSamplers(ID3D11DeviceContext* context,UINT start,UINT count,ID3D11SamplerState* const* samplers) {
    auto* state=StateFor(context);
    if(!state || !state->bound || !samplers || start>=SamplerSlots || count>SamplerSlots-start) return realSetSamplers(context,start,count,samplers);
    std::array<ID3D11SamplerState*,SamplerSlots> own{};
    for(UINT i=0;i<count;++i) own[i]=samplers[i];
    Swap(ownOf,own.data(),count);
    realSetSamplers(context,start,count,own.data());
}

void STDMETHODCALLTYPE SetViews(ID3D11DeviceContext* context,UINT start,UINT count,ID3D11ShaderResourceView* const* views) {
    realSetViews(context,start,count,views);
    if(!paletteCount.load(std::memory_order_acquire) || start>=PaletteSlots::Slots) return;
    auto* state=StateFor(context);
    if(!state) return;
    const auto slots=count<PaletteSlots::Slots-start ? count : PaletteSlots::Slots-start;
    std::array<bool,PaletteSlots::Slots> palette{};
    for(UINT i=0;i<slots;++i) palette[i]=views && IsPaletteView(views[i]);
    const auto bound=state->slots.Set(start,slots,palette.data());
    if(bound==state->bound) return;
    state->bound=bound;
    // The samplers actually bound now (also after a command list or ClearState): raised ones swapped for the game's
    // own while a palette is bound, and back afterwards; others stay.
    std::array<ID3D11SamplerState*,SamplerSlots> current{};
    context->PSGetSamplers(0,SamplerSlots,current.data());
    auto chosen=current;
    if(Swap(bound ? ownOf : raisedOf,chosen.data(),SamplerSlots)) realSetSamplers(context,0,SamplerSlots,chosen.data());
    for(auto* sampler:current) if(sampler) sampler->Release();
    if(logLine && paletteReports.fetch_add(1)<16) logLine("Anisotropic filtering: colour palette %s",bound ? "bound, game's own samplers in use" : "unbound, raised samplers back");
}
}

bool Install(ID3D11Device* device,void (*log)(const char*,...)) {
    static std::once_flag once;
    static bool installed{},requested{};
    std::call_once(once,[device,log] {
        wchar_t text[8]{};
        const auto length=GetEnvironmentVariableW(L"DIRT3VR_ANISOTROPY",text,8);
        if(!length) return;
        requested=true;
        unsigned value{};
        if(length>=8 || !Parse(text,value)) { log("Anisotropic filtering refused: DIRT3VR_ANISOTROPY is not 0, 1, 2, 4, 8 or 16"); return; }
        if(!value) { requested=false; return; }
        if(!device) { log("Anisotropic filtering refused: no device"); return; }
        const auto init=MH_Initialize();
        if(init!=MH_OK && init!=MH_ERROR_ALREADY_INITIALIZED) { log("Anisotropic filtering refused: MinHook=%d",int(init)); return; }
        const auto base=reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
        const auto* nt=reinterpret_cast<const IMAGE_NT_HEADERS*>(base+reinterpret_cast<const IMAGE_DOS_HEADER*>(base)->e_lfanew);
        imageBegin=base;
        imageEnd=base+nt->OptionalHeader.SizeOfImage;
        strength=value;
        logLine=log;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext> context;
        device->GetImmediateContext(&context);
        if(!context) { log("Anisotropic filtering refused: no device context"); return; }
        auto** d=*reinterpret_cast<void***>(device);
        auto** c=*reinterpret_cast<void***>(context.Get());
        // The colour lookup's guard comes with the raised samplers or not at all: without it the palettes go green.
        struct Hook { void* target; void* replacement; void** original; };
        const std::array<Hook,4> hooks{{
            {d[23],reinterpret_cast<void*>(&CreateSampler),reinterpret_cast<void**>(&realCreate)},           // CreateSamplerState
            {d[7],reinterpret_cast<void*>(&CreateView),reinterpret_cast<void**>(&realCreateView)},           // CreateShaderResourceView
            {c[8],reinterpret_cast<void*>(&SetViews),reinterpret_cast<void**>(&realSetViews)},              // PSSetShaderResources
            {c[10],reinterpret_cast<void*>(&SetSamplers),reinterpret_cast<void**>(&realSetSamplers)}}};     // PSSetSamplers
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
        log("Anisotropic filtering: %ux for the game's mipmapped, repeating samplers; colour palettes keep the game's own",value);
    });
    return installed || !requested;
}

}
