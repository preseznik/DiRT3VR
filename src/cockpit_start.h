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
// - The start-sequence camera event (vtable 0xe9dea0 slot 9, 0x53bc10) stores the current view's
//   name (0x53bcbd-0x53bcc4, into its +0x40), then switches to chase_close twice (0x53bcc9-0x53bd62);
//   its end (vtable 0xe9ded8 slot 2, 0x53bd70) selects the stored view by name again (0x53bdea).
// - The view itself comes from the data store's "current_camera" name (read at 0x557e88, matched
//   against the car's views). Its default, set at 0x59fb31 in the data defaults (0x59f730, next to
//   the default player names), is "chase_close"; run 20261103-230530 showed skipping both switches
//   alone still starts in the chase view.
// With DIRT3VR_START_IN_COCKPIT=1 the start sequence stores "head-cam" as the view to go back to and
// jumps past its switches (the stack is balanced at the jump's target), so its end selects the
// cockpit; the default pushes "head-cam" too. The race setup keeps DiRT 3's own chase_close: it also
// runs for the menus' car (the frontend is a track too), and selecting head-cam there (0.1.29,
// #5886cb28) crashed DiRT 3 on the way back to the menus after a race (#5ef4eff5). Nothing is hidden
// and no camera file changes: Change View still cycles through every view.
namespace dirt3vr::cockpitstart {

inline constexpr uintptr_t chaseCloseRva=0xa85538; // 0xe85538 "chase_close" at the preferred base 0x400000
inline constexpr uintptr_t headCamRva=0xa8552c;    // 0xe8552c "head-cam"
inline constexpr uintptr_t defaultRva=0x19fb31;    // 0x59fb31: push "chase_close"; lea eax,[ebp-0x80]
inline constexpr uint8_t defaultAfter[]{0x8d,0x45,0x80};

// The start sequence's stored view: mov ecx,[ebp+8]; lea ebx,[eax+0xa1] (the current view's name);
// push ebx; call 0xd1c520 (store it). The lea becomes mov ebx,"head-cam"; nop.
inline constexpr uintptr_t storedRva=0x13bcbd;
inline constexpr uint8_t storedBefore[]{0x8b,0x4d,0x08};
inline constexpr uint8_t storedOriginal[]{0x8d,0x98,0xa1,0x00,0x00,0x00};
inline constexpr uint8_t storedAfter[]{0x53,0xe8,0x57,0x08,0x7e,0x00};

// The start sequence's switches: push "chase_close"; mov ecx,edi ... -> pop ebx; pop edi; pop esi;
// mov esp,ebp; pop ebp; ret 4. Becomes a jump to that end.
struct Site {
    uintptr_t rva;              // push offset "chase_close"
    uint8_t after[5];           // the instruction after the push (position-independent)
    size_t afterSize;
    uintptr_t endRva;           // the jump target
    uint8_t end[9];             // its first instructions
    size_t endSize;
};
inline constexpr Site sequence{0x13bcc9,{0x8b,0xcf},2,0x13bd67,{0x5b,0x5f,0x5e,0x8b,0xe5,0x5d,0xc2,0x04,0x00},9};

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
// mov ebx,"head-cam" (relocated for loadedBase); nop.
inline void Stored(uintptr_t loadedBase,uint8_t (&bytes)[6]) {
    const uint32_t name=static_cast<uint32_t>(loadedBase+headCamRva);
    const uint8_t pattern[6]{0xbb,uint8_t(name),uint8_t(name>>8),uint8_t(name>>16),uint8_t(name>>24),0x90};
    std::memcpy(bytes,pattern,sizeof(pattern));
}
inline bool StoredContext(const uint8_t* image) {
    return std::memcmp(image+storedRva-sizeof(storedBefore),storedBefore,sizeof(storedBefore))==0 &&
           std::memcmp(image+storedRva+sizeof(storedOriginal),storedAfter,sizeof(storedAfter))==0;
}
inline bool StoredMatches(const uint8_t* image) {
    return StoredContext(image) && std::memcmp(image+storedRva,storedOriginal,sizeof(storedOriginal))==0;
}
inline bool StoredApplied(const uint8_t* image,uintptr_t loadedBase) {
    uint8_t patched[6]; Stored(loadedBase,patched);
    return StoredContext(image) && std::memcmp(image+storedRva,patched,sizeof(patched))==0;
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
    uint8_t patched[5]; Jump(site,patched);
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
    return SiteMatches(image,loadedBase,sequence) && StoredMatches(image) && DefaultMatches(image,loadedBase,chaseCloseRva);
}

// DIRT3VR_START_IN_COCKPIT=1.
bool Requested();
// Patches all three sites once, or none; false when requested but the instructions differ or a
// write failed.
bool Install(void (*log)(const char*,...));

}
