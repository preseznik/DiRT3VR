#include "research_patch.h"
#include <windows.h>
#include <cstring>
#include <mutex>

namespace dirt3vr::research {
void Apply(void (*log)(const char*,...)) {
    static std::once_flag once;
    std::call_once(once,[log] {
        char text[513]{};
        const auto length=GetEnvironmentVariableA("DIRT3VR_RESEARCH_PATCH",text,sizeof(text));
        if(!length) return;
        std::vector<Patch> patches;
        if(length>=sizeof(text) || !Parse(text,patches)) { log("Research patch refused: malformed DIRT3VR_RESEARCH_PATCH"); return; }
        auto* image=reinterpret_cast<uint8_t*>(GetModuleHandleW(nullptr));
        for(const auto& patch:patches) {
            if(std::memcmp(image+patch.rva,patch.original.data(),patch.original.size())) {
                log("Research patch refused: bytes at rva=0x%x differ; nothing written",static_cast<unsigned>(patch.rva));
                return;
            }
        }
        for(const auto& patch:patches) {
            auto* target=image+patch.rva;
            DWORD protection{},ignored{};
            if(!VirtualProtect(target,patch.replacement.size(),PAGE_EXECUTE_READWRITE,&protection)) { log("Research patch rva=0x%x: VirtualProtect failed",static_cast<unsigned>(patch.rva)); continue; }
            std::memcpy(target,patch.replacement.data(),patch.replacement.size());
            FlushInstructionCache(GetCurrentProcess(),target,patch.replacement.size());
            VirtualProtect(target,patch.replacement.size(),protection,&ignored);
            log("Research patch applied rva=0x%x bytes=%u",static_cast<unsigned>(patch.rva),static_cast<unsigned>(patch.replacement.size()));
        }
    });
}
}
