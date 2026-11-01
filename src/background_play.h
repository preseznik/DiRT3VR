#pragma once
#include <cstddef>
#include <cstdint>
#include <cstring>

// Keeps DiRT 3 running when its window loses focus. The game's focus-loss handler
// (RVA 0x296800, logged by the game as "APP focus lost") requests the pause menu and
// pauses a running replay. Only those two instructions are replaced with NOPs; the
// handler's other notifications and the real Windows focus state stay unchanged.
// Same approach as DiRT2VR and GRID2VR (background_play).
namespace dirt3vr::backgroundplay {
inline constexpr uintptr_t pauseMenuRva=0x296812;   // mov byte ptr [esi+0x1dd5],1
inline constexpr uintptr_t pauseReplayRva=0x29685e; // push 2; call 0xae95b0
inline constexpr uint8_t pauseMenu[]{0xc6,0x86,0xd5,0x1d,0x00,0x00,0x01};
inline constexpr uint8_t pauseReplay[]{0x6a,0x02,0xe8,0x4b,0x2d,0x45,0x00};
inline constexpr size_t patchLength=pauseReplayRva+sizeof(pauseReplay)-pauseMenuRva;

inline bool SitesMatch(const uint8_t* image) {
    return std::memcmp(image+pauseMenuRva,pauseMenu,sizeof(pauseMenu))==0 &&
           std::memcmp(image+pauseReplayRva,pauseReplay,sizeof(pauseReplay))==0;
}
inline bool SitesSuppressed(const uint8_t* image) {
    for(size_t i=0;i<sizeof(pauseMenu);++i) if(image[pauseMenuRva+i]!=0x90) return false;
    for(size_t i=0;i<sizeof(pauseReplay);++i) if(image[pauseReplayRva+i]!=0x90) return false;
    return true;
}
// Writes the NOPs into already-writable memory (the caller handles page protection).
inline bool Suppress(uint8_t* image) {
    if(!SitesMatch(image)) return false;
    std::memset(image+pauseMenuRva,0x90,sizeof(pauseMenu));
    std::memset(image+pauseReplayRva,0x90,sizeof(pauseReplay));
    return true;
}
inline void Restore(uint8_t* image) {
    std::memcpy(image+pauseMenuRva,pauseMenu,sizeof(pauseMenu));
    std::memcpy(image+pauseReplayRva,pauseReplay,sizeof(pauseReplay));
}

bool Requested();
// Applies the change to the running supported game once. Returns false and leaves the
// code unchanged when the bytes differ or memory protection cannot be handled.
bool Install(void (*log)(const char*,...));
}
