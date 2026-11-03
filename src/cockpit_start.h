#pragma once
#include <cstddef>
#include <cstdint>
#include <cstring>

// Start races in the cockpit without hiding any view (#cd56379b). DiRT 3 puts the car in the view
// named "chase_close" at a race start in two places, and hiding that view, the launcher's first
// cockpit start, defeated both but also took the view away:
// - The race setup (vtable method 0x5539e0) looks the view up on the car's camera set
//   ([ebx+0x24e4], 0x975aa0) and selects it (0x980620) at 0x553c03-0x553c18. Run 20261103-220716
//   showed this one alone still starts the race in the chase view.
// - The start-sequence camera event (vtable 0xe9dea0 slot 9, 0x53bc10) stores the current view
//   (0x53bcc4), then switches to chase_close twice (0x53bcc9-0x53bd62); the next event
//   (vtable 0xe9ded8 slot 2, 0x53bd70) selects the stored view again.
// With DIRT3VR_START_IN_COCKPIT=1 the first instruction of each (push "chase_close") becomes a jump
// past it (the stack is balanced at both targets), so the car keeps its first view: the cockpit
// when head-cam is first in its camera file (seat_cameras.cockpit_first, nothing hidden). Change
// View still cycles through every view.
namespace dirt3vr::cockpitstart {

inline constexpr uintptr_t chaseCloseRva=0xa85538; // 0xe85538 "chase_close" at the preferred base 0x400000

struct Site {
    uintptr_t rva;              // push offset "chase_close"
    uint8_t after[5];           // the instruction after the push (position-independent)
    size_t afterSize;
    uintptr_t endRva;           // the jump target
    uint8_t end[9];             // its first instructions
    size_t endSize;
};
inline constexpr Site sites[]{
    // Race setup: push "chase_close"; call 0x975aa0 ... -> lea ecx,[esi+0xc4]
    {0x153c03,{0xe8,0x93,0x1e,0x42,0x00},5,0x153c19,{0x8d,0x8e,0xc4,0x00,0x00,0x00},6},
    // Start sequence: push "chase_close"; mov ecx,edi ... -> pop ebx; pop edi; pop esi; mov esp,ebp; pop ebp; ret 4
    {0x13bcc9,{0x8b,0xcf},2,0x13bd67,{0x5b,0x5f,0x5e,0x8b,0xe5,0x5d,0xc2,0x04,0x00},9},
};

// The push carries the string's absolute address, which Windows relocates when the game doesn't
// load at 0x400000; loadedBase is the base the bytes were relocated for.
inline void Push(uintptr_t loadedBase,uint8_t (&bytes)[5]) {
    const uint32_t name=static_cast<uint32_t>(loadedBase+chaseCloseRva);
    const uint8_t pattern[5]{0x68,uint8_t(name),uint8_t(name>>8),uint8_t(name>>16),uint8_t(name>>24)};
    std::memcpy(bytes,pattern,sizeof(pattern));
}
// jmp rel32 to the site's end; position-independent.
inline void Patched(const Site& site,uint8_t (&bytes)[5]) {
    const int32_t relative=static_cast<int32_t>(site.endRva-(site.rva+5));
    bytes[0]=0xe9;
    std::memcpy(bytes+1,&relative,sizeof(relative));
}
inline bool EndMatches(const uint8_t* image,const Site& site) {
    return std::memcmp(image+site.endRva,site.end,site.endSize)==0;
}
inline bool SiteMatches(const uint8_t* image,uintptr_t loadedBase,const Site& site) {
    uint8_t push[5]; Push(loadedBase,push);
    return std::memcmp(image+site.rva,push,sizeof(push))==0 &&
           std::memcmp(image+site.rva+5,site.after,site.afterSize)==0 && EndMatches(image,site);
}
inline bool Applied(const uint8_t* image,const Site& site) {
    uint8_t patched[5]; Patched(site,patched);
    return std::memcmp(image+site.rva,patched,sizeof(patched))==0 &&
           std::memcmp(image+site.rva+5,site.after,site.afterSize)==0 && EndMatches(image,site);
}
inline bool SitesMatch(const uint8_t* image,uintptr_t loadedBase) {
    for(const auto& site:sites) if(!SiteMatches(image,loadedBase,site)) return false;
    return true;
}

// DIRT3VR_START_IN_COCKPIT=1.
bool Requested();
// Patches both sites once, or neither; false when requested but the instructions differ or a
// write failed.
bool Install(void (*log)(const char*,...));

}
