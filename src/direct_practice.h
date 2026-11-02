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

// AI difficulty: the race setup (0x69ca05) asks 0x5ba5c0 for the profile's "difficulty"
// setting (star level 0-5, -1 when absent) and turns it into the opponents' pace
// (difficulty.ai_ratio, 0x5c9560). During a benchmark start the hook answers with the
// requested level instead; outside one the player's own setting is returned unchanged.
inline constexpr uintptr_t difficultyRva=0x1ba5c0;
inline constexpr int maxDifficulty=5;
inline constexpr uint8_t difficultyEntry[]{0x8b,0x41,0x08,0x8b,0x90,0xf0,0x1c,0x00,0x00,0x83,0xba,0x84,0x0a,0x00,0x00,0x07};
inline constexpr uint8_t difficultyBranch[]{0x74,0x05};                          // at +0x15
inline constexpr uint8_t difficultyTail[]{0x50,0xe8,0x4e,0xff,0xff,0xff,0xc3};   // at +0x1c: push; call 0x5ba530; ret
inline bool DifficultySiteMatches(const uint8_t* image) {
    const uint8_t* entry=image+difficultyRva;
    return std::memcmp(entry,difficultyEntry,sizeof(difficultyEntry))==0 &&
           std::memcmp(entry+0x15,difficultyBranch,sizeof(difficultyBranch))==0 &&
           std::memcmp(entry+0x1c,difficultyTail,sizeof(difficultyTail))==0;
}
inline int DifficultyAnswer(uint8_t benchmarkActive,int requested,int original) {
    return benchmarkActive==1 && requested>=0 && requested<=maxDifficulty ? requested : original;
}

// Assists and gearbox: the race setup (0x5c0a00) reads each assist from the profile's settings
// store through 0x9dfad0, which returns the named entry's value (0x88 bytes, the number at
// +0x8) or null. During a benchmark start the hook answers allowlisted names from a private
// copy carrying the requested number; the store itself is never written, and outside a
// benchmark start the player's own values come back unchanged. Meanings (states.bin menus):
// on/off assists 0/1; racing line 0 off, 1 braking only, 2 full; gearbox 0 automatic,
// 1 manual sequential, 2 manual H-pattern, 3 H-pattern with clutch.
inline constexpr uintptr_t settingLookupRva=0x5dfad0;
inline constexpr uint8_t settingLookupEntry[]{0x55,0x8b,0xec,0x53,0x56,0x57,0x8b,0xf9,0x8b,0x77,0x7c,0x3b,0xb7,0x80,0x00,0x00};
inline constexpr size_t settingValueBytes=0x88;
inline constexpr size_t settingNumberOffset=0x8;
struct AssistSetting { const char* name; int maximum; };
inline constexpr AssistSetting assistSettings[]{
    {"assists_abs",1},{"assists_stability",1},{"assists_dyn_race_line",2},{"assists_race_line",1},
    {"assists_corner_braking",1},{"assists_throttle_mngt",1},{"assists_auto_steer",1},{"transmission_type",3}};
inline constexpr size_t assistCount=sizeof(assistSettings)/sizeof(assistSettings[0]);
inline bool SettingLookupMatches(const uint8_t* image) {
    return std::memcmp(image+settingLookupRva,settingLookupEntry,sizeof(settingLookupEntry))==0;
}
// Index of an allowlisted setting name (the store compares at most 64 characters), or -1.
inline int AssistIndex(const char* name) {
    if(!name) return -1;
    for(size_t i=0;i<assistCount;++i)
        if(std::strncmp(name,assistSettings[i].name,64)==0) return static_cast<int>(i);
    return -1;
}
// Parses "name=value,name=value"; values[i] is -1 where nothing was requested. Any unknown
// name, out-of-range or malformed value, or repeated name rejects the whole request.
inline bool ParseAssists(const char* text,int (&values)[assistCount]) {
    for(auto& value:values) value=-1;
    if(!text || !*text) return false;
    int parsed[assistCount];
    for(auto& value:parsed) value=-1;
    const char* cursor=text;
    while(*cursor) {
        const char* equals=std::strchr(cursor,'=');
        if(!equals || equals==cursor || equals-cursor>=64) return false;
        char name[64]{};
        std::memcpy(name,cursor,static_cast<size_t>(equals-cursor));
        const int index=AssistIndex(name);
        if(index<0 || parsed[index]>=0) return false;
        const char digit=equals[1];
        if(digit<'0' || digit>'9' || (equals[2] && equals[2]!=',')) return false;
        const int number=digit-'0';
        if(number>assistSettings[index].maximum) return false;
        parsed[index]=number;
        cursor=equals+2;
        if(*cursor==',') { ++cursor; if(!*cursor) return false; }
    }
    std::memcpy(values,parsed,sizeof(parsed));
    return true;
}
inline int AssistAnswer(uint8_t benchmarkActive,int requested,int original) {
    return benchmarkActive==1 && requested>=0 ? requested : original;
}

bool Requested();
bool ReturnRequested();
// DIRT3VR_DIRECT_ASSISTS="name=value,...": true when a valid request is set.
bool AssistsRequested();
// Hooks the settings lookup once; false leaves the game's own values in charge.
bool InstallAssists(void (*log)(const char*,...));
// DIRT3VR_DIRECT_DIFFICULTY=N (0-5); -1 when unset or invalid.
int RequestedDifficulty();
// Hooks the difficulty lookup once; false leaves the game's own setting in charge.
bool InstallDifficulty(void (*log)(const char*,...));
// Applies the return-to-menus change once; false leaves the code unchanged.
bool InstallReturn(void (*log)(const char*,...));
// DIRT3VR_DIRECT_LAPS=N (1-20); 0 when unset or invalid.
unsigned RequestedLaps();
// Called on every Present: keeps the requested lap count once direct practice is installed.
void Tick(void (*log)(const char*,...));
// Applies the change to the running supported game once; false leaves the code unchanged.
bool Install(void (*log)(const char*,...));
}
