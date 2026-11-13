#include "cockpit_start.h"
#include <windows.h>
#include <mutex>

namespace dirt3vr::cockpitstart {
namespace {
// Writes a few bytes of code; on any failure the original bytes go back.
template<size_t N> bool Write(uint8_t* target,const uint8_t (&bytes)[N]) {
    DWORD protection{},ignored{};
    if(!VirtualProtect(target,N,PAGE_EXECUTE_READWRITE,&protection)) return false;
    uint8_t original[N];
    std::memcpy(original,target,N);
    std::memcpy(target,bytes,N);
    const bool flushed=FlushInstructionCache(GetCurrentProcess(),target,N)!=FALSE;
    const bool restored=VirtualProtect(target,N,protection,&ignored)!=FALSE;
    if(flushed && restored) return true;
    if(VirtualProtect(target,N,PAGE_EXECUTE_READWRITE,&protection)) {
        std::memcpy(target,original,N);
        FlushInstructionCache(GetCurrentProcess(),target,N);
        VirtualProtect(target,N,protection,&ignored);
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
        uint8_t push[5],headCam[5],jump[5],stored[6];
        Push(base,push); Push(base,headCam,headCamRva); Jump(sequence,jump); Stored(base,stored);
        // All or none: put back what was already changed.
        if(!Write(image+storedRva,stored)) { log("Cockpit start refused: the code change could not be completed"); return; }
        if(!Write(image+sequence.rva,jump)) {
            Write(image+storedRva,storedOriginal);
            log("Cockpit start refused: the code change could not be completed");
            return;
        }
        if(!Write(image+defaultRva,headCam)) {
            Write(image+sequence.rva,push); Write(image+storedRva,storedOriginal);
            log("Cockpit start refused: the code change could not be completed");
            return;
        }
        installed=true;
        log("Cockpit start: the start sequence stores head-cam as the view to return to (0x53bcbd) and skips its chase-view switches (0x53bcc9), and current_camera defaults to head-cam (0x59fb31); the race setup and the menus' car keep DiRT 3's chase view");
    });
    return installed || !requested;
}

}
