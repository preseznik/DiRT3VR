#pragma once
#include <cstdint>
#include <cstring>

// Start races in the cockpit without hiding any view (#cd56379b). At a race start DiRT 3's
// start-sequence camera event (vtable 0xe9dea0 slot 9, 0x53bc10) stores the player's current view
// (0x53bcc4), then switches to the view named "chase_close": once by activating it when that view's
// flag (+0x152) allows, and once by selecting it by name unless the event's own flag (+0x3c) and
// the current view say otherwise (0x53bcc9-0x53bd62). The next event (vtable 0xe9ded8 slot 2,
// 0x53bd70) selects the stored view again. Hiding chase_close, the launcher's first cockpit start,
// defeated both switches but also took the view away.
// With DIRT3VR_START_IN_COCKPIT=1 the first instruction of the switching code (push "chase_close")
// becomes a jump to its end (0x53bd67, pop ebx; the stack is balanced there), so the car keeps the
// view it starts in: the cockpit when head-cam is first in its camera file
// (seat_cameras.cockpit_first, nothing hidden). Change View still cycles through every view.
namespace dirt3vr::cockpitstart {

inline constexpr uintptr_t switchRva=0x13bcc9;     // 0x53bcc9: push offset "chase_close"; mov ecx,edi
inline constexpr uintptr_t switchEndRva=0x13bd67;  // 0x53bd67: pop ebx; pop edi; pop esi; mov esp,ebp; pop ebp; ret 4
inline constexpr uintptr_t chaseCloseRva=0xa85538; // 0xe85538 "chase_close" at the preferred base 0x400000
inline constexpr uint8_t switchEnd[]{0x5b,0x5f,0x5e,0x8b,0xe5,0x5d,0xc2,0x04,0x00};

// The push carries the string's absolute address, which Windows relocates when the game doesn't
// load at 0x400000; loadedBase is the base the bytes were relocated for.
inline void Original(uintptr_t loadedBase,uint8_t (&bytes)[7]) {
    const uint32_t name=static_cast<uint32_t>(loadedBase+chaseCloseRva);
    const uint8_t pattern[7]{0x68,uint8_t(name),uint8_t(name>>8),uint8_t(name>>16),uint8_t(name>>24),0x8b,0xcf};
    std::memcpy(bytes,pattern,sizeof(pattern));
}
// jmp rel32 to switchEndRva; position-independent.
inline void Patched(uint8_t (&bytes)[5]) {
    const int32_t relative=static_cast<int32_t>(switchEndRva-(switchRva+5));
    bytes[0]=0xe9;
    std::memcpy(bytes+1,&relative,sizeof(relative));
}
inline bool SiteMatches(const uint8_t* image,uintptr_t loadedBase) {
    uint8_t original[7]; Original(loadedBase,original);
    return std::memcmp(image+switchRva,original,sizeof(original))==0 &&
           std::memcmp(image+switchEndRva,switchEnd,sizeof(switchEnd))==0;
}
inline bool Applied(const uint8_t* image) {
    uint8_t patched[5]; Patched(patched);
    return std::memcmp(image+switchRva,patched,sizeof(patched))==0 &&
           std::memcmp(image+switchEndRva,switchEnd,sizeof(switchEnd))==0;
}

// DIRT3VR_START_IN_COCKPIT=1.
bool Requested();
// Patches the switch once; false when requested but the instructions differ or the write failed.
bool Install(void (*log)(const char*,...));

}
