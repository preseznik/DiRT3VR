#include "anisotropic_filter.h"
#include <windows.h>
#include <MinHook.h>
#include <intrin.h>
#include <atomic>
#include <mutex>

namespace dirt3vr::anisotropy {
namespace {
using CreateSamplerFn=HRESULT (STDMETHODCALLTYPE*)(ID3D11Device*,const D3D11_SAMPLER_DESC*,ID3D11SamplerState**);
CreateSamplerFn realCreate{};
unsigned strength{};
uintptr_t imageBegin{},imageEnd{};
void (*logLine)(const char*,...){};
std::atomic<unsigned> reports{};

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
    return FAILED(hr) ? realCreate(device,desc,state) : hr;
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
        auto* target=(*reinterpret_cast<void***>(device))[23]; // ID3D11Device::CreateSamplerState
        if(MH_CreateHook(target,reinterpret_cast<void*>(&CreateSampler),reinterpret_cast<void**>(&realCreate))!=MH_OK) { log("Anisotropic filtering refused: hook not created"); return; }
        if(MH_EnableHook(target)!=MH_OK) { MH_RemoveHook(target); log("Anisotropic filtering refused: hook not enabled"); return; }
        installed=true;
        log("Anisotropic filtering: %ux for the game's mipmapped, repeating samplers",value);
    });
    return installed || !requested;
}

}
