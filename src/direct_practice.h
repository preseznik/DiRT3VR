#pragma once
#include <cstddef>
#include <cstdint>
#include <cstring>

// Keeps the player's car under player control in a -benchmark start (direct practice).
// DiRT 3's controller update (RVA 0x729f80) sets the controller's force-AI byte
// [controller+0x2c] on every update while the benchmark flag (0xf9cef0) is set; the same
// code as DiRT 2's 0x72a580 that DiRT2VR changes. Making its je unconditional keeps the
// human/AI choice made when the participants were created. Outside a benchmark start the
// game takes that branch anyway, so nothing else changes.
namespace dirt3vr::directpractice {
inline constexpr uintptr_t benchmarkCheckRva=0x729fab; // cmp byte ptr [benchmark flag],0; mov eax,[ecx+0xc]; mov [ebp-0x2c],eax
inline constexpr uintptr_t benchmarkFlagRva=0xb9cef0;  // 0xf9cef0 at the preferred base 0x400000
inline constexpr uintptr_t forceAiRva=0x729fb8;        // je +6; mov byte [ecx+0x2c],1; jmp +4; mov byte [ecx+0x2c],0
inline constexpr uint8_t forceAi[]{0x74,0x06,0xc6,0x41,0x2c,0x01,0xeb,0x04,0xc6,0x41,0x2c,0x00};

// The compare carries the flag's absolute address, which Windows relocates when the game
// doesn't load at 0x400000; loadedBase is the base the bytes were relocated for.
inline void BenchmarkCheck(uintptr_t loadedBase,uint8_t (&bytes)[13]) {
    const uint32_t flag=static_cast<uint32_t>(loadedBase+benchmarkFlagRva);
    const uint8_t pattern[13]{0x80,0x3d,uint8_t(flag),uint8_t(flag>>8),uint8_t(flag>>16),uint8_t(flag>>24),0x00,0x8b,0x41,0x0c,0x89,0x45,0xd4};
    std::memcpy(bytes,pattern,sizeof(pattern));
}
inline bool CheckMatches(const uint8_t* image,uintptr_t loadedBase) {
    uint8_t expected[13]; BenchmarkCheck(loadedBase,expected);
    return std::memcmp(image+benchmarkCheckRva,expected,sizeof(expected))==0;
}
inline bool SitesMatch(const uint8_t* image,uintptr_t loadedBase) {
    return CheckMatches(image,loadedBase) && std::memcmp(image+forceAiRva,forceAi,sizeof(forceAi))==0;
}
inline bool Applied(const uint8_t* image,uintptr_t loadedBase) {
    return CheckMatches(image,loadedBase) && image[forceAiRva]==0xeb &&
           std::memcmp(image+forceAiRva+1,forceAi+1,sizeof(forceAi)-1)==0;
}
// Writes the one byte into already-writable memory (the caller handles page protection).
inline bool Keep(uint8_t* image,uintptr_t loadedBase) {
    if(!SitesMatch(image,loadedBase)) return false;
    image[forceAiRva]=0xeb;
    return true;
}

// Laps: the race setup (0x608fbe) takes the lap count from the benchmark config's count
// (+0xef4, 0xf9dd64 at the preferred base) as max(1, count); the benchmark XML has no
// attribute for it and the parser leaves it 0. Writing it before the race loads gives the
// player that many laps (count 3 made the AI race 3 laps: ~213 s against ~93 s for 1).
inline constexpr uintptr_t lapsRva=0xb9dd64;
inline constexpr unsigned maxLaps=20;
// One Present's worth: writes laps into the count while the benchmark flag is set.
// Returns true when it changed the value.
inline bool KeepLaps(uint8_t benchmarkActive,uint32_t& count,unsigned laps) {
    if(benchmarkActive!=1 || laps<1 || laps>maxLaps || count==laps) return false;
    count=laps;
    return true;
}

bool Requested();
// DIRT3VR_DIRECT_LAPS=N (1-20); 0 when unset or invalid.
unsigned RequestedLaps();
// Called on every Present: keeps the requested lap count once direct practice is installed.
void Tick(void (*log)(const char*,...));
// Applies the change to the running supported game once; false leaves the code unchanged.
bool Install(void (*log)(const char*,...));
}
