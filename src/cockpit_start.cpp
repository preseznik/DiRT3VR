#include "cockpit_start.h"
#include <windows.h>
#include <mutex>

namespace dirt3vr::cockpitstart {
namespace {
// Writes 5 bytes of code; on any failure the original bytes go back.
bool Write(uint8_t* target,const uint8_t (&bytes)[5]) {
    DWORD protection{},ignored{};
    if(!VirtualProtect(target,sizeof(bytes),PAGE_EXECUTE_READWRITE,&protection)) return false;
    uint8_t original[sizeof(bytes)];
    std::memcpy(original,target,sizeof(original));
    std::memcpy(target,bytes,sizeof(bytes));
    const bool flushed=FlushInstructionCache(GetCurrentProcess(),target,sizeof(bytes))!=FALSE;
    const bool restored=VirtualProtect(target,sizeof(bytes),protection,&ignored)!=FALSE;
    if(flushed && restored) return true;
    if(VirtualProtect(target,sizeof(original),PAGE_EXECUTE_READWRITE,&protection)) {
        std::memcpy(target,original,sizeof(original));
        FlushInstructionCache(GetCurrentProcess(),target,sizeof(original));
        VirtualProtect(target,sizeof(original),protection,&ignored);
    }
    return false;
}
}

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
        const auto base=reinterpret_cast<uintptr_t>(image);
        if(!SitesMatch(image,base)) { log("Cockpit start refused: chase-view instructions differ; races start in the chase view"); return; }
        uint8_t push[5]; Push(base,push);
        size_t written=0;
        for(;written<std::size(sites);++written) {
            uint8_t patched[5]; Patched(sites[written],patched);
            if(!Write(image+sites[written].rva,patched)) break;
        }
        if(written<std::size(sites)) {
            // Both or neither: put back the sites already changed.
            for(size_t i=0;i<written;++i) Write(image+sites[i].rva,push);
            log("Cockpit start refused: the code change could not be completed");
            return;
        }
        installed=true;
        log("Cockpit start: DiRT 3's race-start switches to the chase view are skipped (0x553c03, 0x53bcc9); the car keeps its first view");
    });
    return installed || !requested;
}

}
