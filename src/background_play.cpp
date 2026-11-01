#include "background_play.h"
#include <windows.h>
#include <mutex>

namespace dirt3vr::backgroundplay {
bool Requested() {
    wchar_t value[8]{};
    return GetEnvironmentVariableW(L"DIRT3VR_BACKGROUND_PLAY",value,8)==1 && value[0]==L'1';
}

bool Install(void (*log)(const char*,...)) {
    static std::once_flag once;
    static bool installed{};
    std::call_once(once,[log] {
        auto* image=reinterpret_cast<uint8_t*>(GetModuleHandleW(nullptr));
        if(!SitesMatch(image)) { log("Background play refused: focus-loss instructions differ; game pauses on focus loss as usual"); return; }
        auto* first=image+pauseMenuRva;
        MEMORY_BASIC_INFORMATION region{};
        if(!VirtualQuery(first,&region,sizeof(region)) || region.State!=MEM_COMMIT ||
           reinterpret_cast<uintptr_t>(first)+patchLength>reinterpret_cast<uintptr_t>(region.BaseAddress)+region.RegionSize) {
            log("Background play refused: unexpected code region");
            return;
        }
        DWORD protection{};
        if(!VirtualProtect(first,patchLength,PAGE_EXECUTE_READWRITE,&protection)) { log("Background play refused: VirtualProtect failed"); return; }
        Suppress(image);
        DWORD ignored{};
        const bool flushed=FlushInstructionCache(GetCurrentProcess(),first,patchLength)!=FALSE;
        const bool restored=VirtualProtect(first,patchLength,protection,&ignored)!=FALSE;
        if(!flushed || !restored) {
            // A failed install must not leave a partial behaviour change behind.
            if(VirtualProtect(first,patchLength,PAGE_EXECUTE_READWRITE,&ignored)) {
                Restore(image);
                FlushInstructionCache(GetCurrentProcess(),first,patchLength);
                VirtualProtect(first,patchLength,protection,&ignored);
            }
            log("Background play refused: flush=%u protection_restored=%u; original code kept",flushed,restored);
            return;
        }
        installed=true;
        log("Background play: focus-loss pause request and replay pause suppressed; native focus and input notifications retained");
    });
    return installed;
}
}
