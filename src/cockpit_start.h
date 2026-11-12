#pragma once
#include <cstddef>
#include <cstdint>
#include <cstring>

// Start races in the cockpit without hiding any view (#cd56379b). DiRT 3 puts the car in the view
// named "chase_close" at a race start in three places, and hiding that view, the launcher's first
// cockpit start, defeated all of them but also took the view away:
// - The race setup (vtable method 0x5539e0) looks the view up on the car's camera set
//   ([ebx+0x24e4], 0x975aa0) and selects it (0x980620) at 0x553c03-0x553c18. Run 20261103-220716
//   showed this one alone still starts the race in the chase view.
// - The start-sequence camera event (vtable 0xe9dea0 slot 9, 0x53bc10) stores the current view
//   (0x53bcc4), then switches to chase_close twice (0x53bcc9-0x53bd62); the next event
//   (vtable 0xe9ded8 slot 2, 0x53bd70) selects the stored view again.
// - The view itself comes from the data store's "current_camera" name (read at 0x557e88, matched
//   against the car's views). Its default, set at 0x59fb31 in the data defaults (0x59f730, next to
//   the default player names), is "chase_close"; run 20261103-230530 showed skipping both switches
//   alone still starts in the chase view.
// With DIRT3VR_START_IN_COCKPIT=1 the race setup looks up and selects "head-cam" instead of
// "chase_close" (#5886cb28: iztok's normal launch, with his profile, otherwise starts in the car's first
// view, and moving head-cam first in each camera file made DiRT 3 draw the menu car as its in-car
// model). The start sequence's switches become a jump past them (the stack is balanced at its
// target), and the default pushes "head-cam" instead. Nothing is hidden and no camera file changes:
// Change View still cycles through every view.
namespace dirt3vr::cockpitstart {

inline constexpr uintptr_t chaseCloseRva=0xa85538; // 0xe85538 "chase_close" at the preferred base 0x400000
inline constexpr uintptr_t headCamRva=0xa8552c;    // 0xe8552c "head-cam"
inline constexpr uintptr_t defaultRva=0x19fb31;    // 0x59fb31: push "chase_close"; lea eax,[ebp-0x80]
inline constexpr uint8_t defaultAfter[]{0x8d,0x45,0x80};

struct Site {
    uintptr_t rva;              // push offset "chase_close"
    bool headCam;               // true: push "head-cam" instead; false: jump to endRva
    uint8_t after[5];           // the instruction after the push (position-independent)
    size_t afterSize;
    uintptr_t endRva;           // the jump target
    uint8_t end[9];             // its first instructions
    size_t endSize;
};
inline constexpr Site sites[]{
    // Race setup: push "chase_close"; call 0x975aa0 (the view's index by name) ... select it
    // (0x980620) -> lea ecx,[esi+0xc4]. Selects head-cam instead.
    {0x153c03,true,{0xe8,0x93,0x1e,0x42,0x00},5,0x153c19,{0x8d,0x8e,0xc4,0x00,0x00,0x00},6},
    // Start sequence: push "chase_close"; mov ecx,edi ... -> pop ebx; pop edi; pop esi; mov esp,ebp; pop ebp; ret 4
    {0x13bcc9,false,{0x8b,0xcf},2,0x13bd67,{0x5b,0x5f,0x5e,0x8b,0xe5,0x5d,0xc2,0x04,0x00},9},
};

// The push carries the string's absolute address, which Windows relocates when the game doesn't
// load at 0x400000; loadedBase is the base the bytes were relocated for.
inline void Push(uintptr_t loadedBase,uint8_t (&bytes)[5],uintptr_t nameRva=chaseCloseRva) {
    const uint32_t name=static_cast<uint32_t>(loadedBase+nameRva);
    const uint8_t pattern[5]{0x68,uint8_t(name),uint8_t(name>>8),uint8_t(name>>16),uint8_t(name>>24)};
    std::memcpy(bytes,pattern,sizeof(pattern));
}
// jmp rel32 to the site's end; position-independent.
inline void Jump(const Site& site,uint8_t (&bytes)[5]) {
    const int32_t relative=static_cast<int32_t>(site.endRva-(site.rva+5));
    bytes[0]=0xe9;
    std::memcpy(bytes+1,&relative,sizeof(relative));
}
// The site's new instruction: push "head-cam" (relocated for loadedBase) or the jump.
inline void Patched(uintptr_t loadedBase,const Site& site,uint8_t (&bytes)[5]) {
    if(site.headCam) Push(loadedBase,bytes,headCamRva);
    else Jump(site,bytes);
}
inline bool EndMatches(const uint8_t* image,const Site& site) {
    return std::memcmp(image+site.endRva,site.end,site.endSize)==0;
}
inline bool SiteMatches(const uint8_t* image,uintptr_t loadedBase,const Site& site) {
    uint8_t push[5]; Push(loadedBase,push);
    return std::memcmp(image+site.rva,push,sizeof(push))==0 &&
           std::memcmp(image+site.rva+5,site.after,site.afterSize)==0 && EndMatches(image,site);
}
inline bool Applied(const uint8_t* image,uintptr_t loadedBase,const Site& site) {
    uint8_t patched[5]; Patched(loadedBase,site,patched);
    return std::memcmp(image+site.rva,patched,sizeof(patched))==0 &&
           std::memcmp(image+site.rva+5,site.after,site.afterSize)==0 && EndMatches(image,site);
}
// The current_camera default: push "chase_close" (original) or "head-cam" (patched).
inline bool DefaultMatches(const uint8_t* image,uintptr_t loadedBase,uintptr_t nameRva) {
    uint8_t push[5]; Push(loadedBase,push,nameRva);
    return std::memcmp(image+defaultRva,push,sizeof(push))==0 &&
           std::memcmp(image+defaultRva+5,defaultAfter,sizeof(defaultAfter))==0;
}
inline bool SitesMatch(const uint8_t* image,uintptr_t loadedBase) {
    for(const auto& site:sites) if(!SiteMatches(image,loadedBase,site)) return false;
    return DefaultMatches(image,loadedBase,chaseCloseRva);
}

// DIRT3VR_START_IN_COCKPIT=1.
bool Requested();
// Patches all three sites once, or none; false when requested but the instructions differ or a
// write failed.
bool Install(void (*log)(const char*,...));

}
