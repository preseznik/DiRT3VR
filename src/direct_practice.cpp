#include "direct_practice.h"
#include <windows.h>
#include <atomic>
#include <mutex>

namespace dirt3vr::directpractice {
bool Requested() {
    wchar_t value[8]{};
    return GetEnvironmentVariableW(L"DIRT3VR_DIRECT_PRACTICE",value,8)==1 && value[0]==L'1';
}

unsigned RequestedLaps() {
    wchar_t text[8]{};
    const auto length=GetEnvironmentVariableW(L"DIRT3VR_DIRECT_LAPS",text,8);
    if(length<1 || length>2) return 0;
    unsigned value{};
    for(DWORD i=0;i<length;++i) {
        if(text[i]<L'0' || text[i]>L'9') return 0;
        value=value*10+static_cast<unsigned>(text[i]-L'0');
    }
    return value>=1 && value<=maxLaps ? value : 0;
}

namespace {
std::atomic<bool> installed{};
}

void Tick(void (*log)(const char*,...)) {
    static const unsigned laps=RequestedLaps();
    if(!laps || !installed.load(std::memory_order_acquire)) return;
    auto* image=reinterpret_cast<uint8_t*>(GetModuleHandleW(nullptr));
    const auto active=*reinterpret_cast<volatile uint8_t*>(image+benchmarkFlagRva);
    auto* count=reinterpret_cast<volatile uint32_t*>(image+lapsRva);
    uint32_t value=*count;
    const auto before=value;
    if(KeepLaps(active,value,laps)) {
        *count=value;
        static unsigned writes{};
        if(++writes<=4) log("Direct practice laps: benchmark count %u -> %u",before,value);
    }
}

bool Install(void (*log)(const char*,...)) {
    static std::once_flag once;
    std::call_once(once,[log] {
        auto* image=reinterpret_cast<uint8_t*>(GetModuleHandleW(nullptr));
        const auto base=reinterpret_cast<uintptr_t>(image);
        if(!SitesMatch(image,base)) { log("Direct practice refused: controller update instructions differ; the benchmark AI keeps the car"); return; }
        auto* target=image+forceAiRva;
        DWORD protection{},ignored{};
        if(!VirtualProtect(target,1,PAGE_EXECUTE_READWRITE,&protection)) { log("Direct practice refused: VirtualProtect failed"); return; }
        Keep(image,base);
        const bool flushed=FlushInstructionCache(GetCurrentProcess(),target,1)!=FALSE;
        const bool restored=VirtualProtect(target,1,protection,&ignored)!=FALSE;
        if(!flushed || !restored) {
            // A failed install must not leave a partial behaviour change behind.
            if(VirtualProtect(target,1,PAGE_EXECUTE_READWRITE,&ignored)) {
                *target=forceAi[0];
                FlushInstructionCache(GetCurrentProcess(),target,1);
                VirtualProtect(target,1,protection,&ignored);
            }
            log("Direct practice refused: flush=%u protection_restored=%u; original code kept",flushed,restored);
            return;
        }
        installed.store(true,std::memory_order_release);
        log("Direct practice: benchmark start keeps the player's car under player control (base=0x%x)",static_cast<unsigned>(base));
        if(const auto laps=RequestedLaps()) log("Direct practice laps: %u requested",laps);
    });
    return installed.load(std::memory_order_acquire);
}
}
