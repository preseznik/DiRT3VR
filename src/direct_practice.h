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

// Pause: the race's next-state check (0x51f3f0) returns "pause" when a pause was requested,
// but drops it while the benchmark flag is set. Making that je unconditional gives a direct
// session DiRT 3's normal Single Race pause menu (Continue, Restart, Options, Retire...).
inline constexpr uintptr_t pauseCheckRva=0x11f44f;  // cmp byte [benchmark flag],0; je +0xf; push 0; lea ecx,[ebp+8]
inline constexpr uintptr_t pauseBranchRva=0x11f456;
inline void PauseCheck(uintptr_t loadedBase,uint8_t (&bytes)[13]) {
    const uint32_t flag=static_cast<uint32_t>(loadedBase+benchmarkFlagRva);
    const uint8_t pattern[13]{0x80,0x3d,uint8_t(flag),uint8_t(flag>>8),uint8_t(flag>>16),uint8_t(flag>>24),0x00,
                              0x74,0x0f,0x6a,0x00,0x8d,0x4d};
    std::memcpy(bytes,pattern,sizeof(pattern));
}
inline bool PauseSiteMatches(const uint8_t* image,uintptr_t loadedBase) {
    uint8_t expected[13]; PauseCheck(loadedBase,expected);
    return std::memcmp(image+pauseCheckRva,expected,sizeof(expected))==0;
}
inline bool AllowPause(uint8_t* image,uintptr_t loadedBase) {
    if(!PauseSiteMatches(image,loadedBase)) return false;
    image[pauseBranchRva]=0xeb;
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

// Return to menus: after the last benchmark track the game writes its result (0x6a87d8)
// and, for a command-line benchmark (config +0x81), calls ExitProcess(0) (0x6a87ec).
// Jumping past that call lets flow.bin's skipfe route take the player to DiRT 3's main
// menu; once the result is written, the benchmark flags are switched off so the menus and
// any race started from them behave like a normal session.
inline constexpr uintptr_t exitCheckRva=0x2a87d8;   // call result writer; cmp byte [esi+0x81],0; je; push 0; call [ExitProcess]
inline constexpr uintptr_t exitBranchRva=0x2a87e4;  // the je (6 bytes)
inline constexpr uintptr_t exitImportRva=0xa0119c;  // ExitProcess import slot (0xe0119c at the preferred base)
inline constexpr uintptr_t commandLineFlagRva=0xb9cef1; // config +0x81
inline constexpr uintptr_t requestedTracksRva=0xb9dd68; // config +0xef8
inline constexpr uintptr_t completedTracksRva=0xb9dd6c; // config +0xefc
inline constexpr uint8_t exitBranch[]{0x0f,0x84,0x6d,0xff,0xff,0xff};
inline constexpr uint8_t skipExit[]{0xe9,0x6e,0xff,0xff,0xff,0x90}; // jmp 0x6a8757 (the je's target); nop

inline void ExitCheck(uintptr_t loadedBase,uint8_t (&bytes)[26]) {
    const uint32_t slot=static_cast<uint32_t>(loadedBase+exitImportRva);
    const uint8_t pattern[26]{0xe8,0xf3,0xeb,0xff,0xff, 0x80,0xbe,0x81,0x00,0x00,0x00,0x00,
                              0x0f,0x84,0x6d,0xff,0xff,0xff, 0x6a,0x00, 0xff,0x15,
                              uint8_t(slot),uint8_t(slot>>8),uint8_t(slot>>16),uint8_t(slot>>24)};
    std::memcpy(bytes,pattern,sizeof(pattern));
}
inline bool ExitSiteMatches(const uint8_t* image,uintptr_t loadedBase) {
    uint8_t expected[26]; ExitCheck(loadedBase,expected);
    return std::memcmp(image+exitCheckRva,expected,sizeof(expected))==0;
}
// Writes the jump into already-writable memory (the caller handles page protection).
inline bool SkipExit(uint8_t* image,uintptr_t loadedBase) {
    if(!ExitSiteMatches(image,loadedBase)) return false;
    std::memcpy(image+exitBranchRva,skipExit,sizeof(skipExit));
    return true;
}
// One Present's worth: once every requested track is complete, ends benchmark mode.
inline bool EndBenchmark(uint8_t& active,uint8_t& commandLine,uint32_t requested,uint32_t completed) {
    if(active!=1 || requested==0 || completed<requested) return false;
    active=0; commandLine=0;
    return true;
}

bool Requested();
bool ReturnRequested();
// Applies the return-to-menus change once; false leaves the code unchanged.
bool InstallReturn(void (*log)(const char*,...));
// DIRT3VR_DIRECT_LAPS=N (1-20); 0 when unset or invalid.
unsigned RequestedLaps();
// Called on every Present: keeps the requested lap count once direct practice is installed.
void Tick(void (*log)(const char*,...));
// Applies the change to the running supported game once; false leaves the code unchanged.
bool Install(void (*log)(const char*,...));
}
