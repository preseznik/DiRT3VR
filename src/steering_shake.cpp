#include "steering_shake.h"
#include <windows.h>
#include <MinHook.h>
#include <intrin.h>
#include <atomic>
#include <mutex>

namespace dirt3vr::steeringshake {
namespace {
using UpdateFn=void (__thiscall*)(void*,float);
UpdateFn original{};
uintptr_t imageBase{};
void (*logger)(const char*,...){};
std::atomic<unsigned> samples{};
void __fastcall Update(void* component,void*,float dt) {
    const auto returnAddress=reinterpret_cast<uintptr_t>(_ReturnAddress());
    original(component,dt);
    if(returnAddress!=imageBase+callerRva+sizeof(caller)) return;
    float generated{},raw{};
    std::memcpy(&generated,static_cast<uint8_t*>(component)+visualOffset,sizeof(generated));
    if(!ClearVisualOffset(component)) return;
    if(logger && generated!=0.f && samples.load()<8) {
        const auto sample=samples.fetch_add(1);
        if(sample<8) {
            std::memcpy(&raw,static_cast<uint8_t*>(component)+0x264,sizeof(raw));
            logger("Steering shake: sample=%u generated=%.6f applied=0 raw=%.6f",sample+1,generated,raw);
        }
    }
}
}
bool Requested() {
    wchar_t value[8]{};
    return GetEnvironmentVariableW(L"DIRT3VR_STEERING_SHAKE_OFF",value,8)==1 && value[0]==L'1';
}
bool Install(void (*log)(const char*,...)) {
    static std::once_flag once;
    static bool installed{};
    std::call_once(once,[log] {
        auto* image=reinterpret_cast<uint8_t*>(GetModuleHandleW(nullptr));
        if(!SitesMatch(image)) { log("Steering shake refused: animation instructions differ; original behaviour retained"); return; }
        auto status=MH_Initialize();
        if(status!=MH_OK && status!=MH_ERROR_ALREADY_INITIALIZED) {
            log("Steering shake refused: MinHook initialization failed (%s)",MH_StatusToString(status)); return;
        }
        auto* target=image+updateRva;
        imageBase=reinterpret_cast<uintptr_t>(image); logger=log;
        status=MH_CreateHook(target,reinterpret_cast<void*>(Update),reinterpret_cast<void**>(&original));
        if(status!=MH_OK) { log("Steering shake refused: hook creation failed (%s)",MH_StatusToString(status)); return; }
        status=MH_EnableHook(target);
        if(status!=MH_OK) {
            MH_RemoveHook(target); original=nullptr;
            log("Steering shake refused: hook enable failed (%s); original behaviour retained",MH_StatusToString(status)); return;
        }
        installed=true;
        log("Steering shake: artificial cockpit animation offset off; original timer, random sequence and raw steering retained");
    });
    return installed;
}
}
