#include "direct_practice.h"
#include <windows.h>
#include <mutex>

namespace dirt3vr::directpractice {
bool Requested() {
    wchar_t value[8]{};
    return GetEnvironmentVariableW(L"DIRT3VR_DIRECT_PRACTICE",value,8)==1 && value[0]==L'1';
}

bool Install(void (*log)(const char*,...)) {
    static std::once_flag once;
    static bool installed{};
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
        installed=true;
        log("Direct practice: benchmark start keeps the player's car under player control (base=0x%x)",static_cast<unsigned>(base));
    });
    return installed;
}
}
