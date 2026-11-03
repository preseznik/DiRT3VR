#include "cockpit_start.h"
#include <windows.h>
#include <mutex>

namespace dirt3vr::cockpitstart {

bool Requested() {
    wchar_t value[8]{};
    return GetEnvironmentVariableW(L"DIRT3VR_START_IN_COCKPIT",value,8)==1 && value[0]==L'1';
}

bool Install(void (*log)(const char*,...)) {
    static std::once_flag once;
    static bool installed{},requested{};
    std::call_once(once,[log] {
        requested=Requested();
        if(!requested) return;
        auto* image=reinterpret_cast<uint8_t*>(GetModuleHandleW(nullptr));
        if(!SiteMatches(image,reinterpret_cast<uintptr_t>(image))) { log("Cockpit start refused: start-camera instructions differ; races start in the chase view"); return; }
        uint8_t patched[5]; Patched(patched);
        auto* target=image+switchRva;
        DWORD protection{},ignored{};
        if(!VirtualProtect(target,sizeof(patched),PAGE_EXECUTE_READWRITE,&protection)) { log("Cockpit start refused: VirtualProtect failed"); return; }
        uint8_t original[sizeof(patched)];
        std::memcpy(original,target,sizeof(original));
        std::memcpy(target,patched,sizeof(patched));
        const bool flushed=FlushInstructionCache(GetCurrentProcess(),target,sizeof(patched))!=FALSE;
        const bool restored=VirtualProtect(target,sizeof(patched),protection,&ignored)!=FALSE;
        if(!flushed || !restored) {
            // Never leave a half-made change: put the original back.
            if(VirtualProtect(target,sizeof(original),PAGE_EXECUTE_READWRITE,&protection)) {
                std::memcpy(target,original,sizeof(original));
                FlushInstructionCache(GetCurrentProcess(),target,sizeof(original));
                VirtualProtect(target,sizeof(original),protection,&ignored);
            }
            log("Cockpit start refused: the code change could not be completed");
            return;
        }
        installed=true;
        log("Cockpit start: the race-start switch to the chase view is skipped (0x53bcc9); the car keeps its first view");
    });
    return installed || !requested;
}

}
