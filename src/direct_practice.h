#pragma once
#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

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

// Driving device: a direct start has its own Press Start (0x56e070, only with the benchmark
// flags). It lists the connected devices through the input service's 0xb04cf0 (12-byte
// descriptors) and binds the first valid one to player 1, the next to player 2, through
// 0xb31c70 as ScreenPressStart does. The keyboard is listed first, so a wheel bound in the
// launcher never drives (#16fa2aa3). For that one call (returning to 0x56e150) the hook moves
// the launcher's device to the front, and DiRT 3 binds it to player 1 itself (run
// 20261103-104313: the Xbox pad became player 1, the keyboard player 2).
// DIRT3VR_DIRECT_DEVICES="name|name|..." lists the session action maps' device names
// (xinput_pad or a DirectInput product name), the steering device first.
inline constexpr uintptr_t deviceListRva=0x704cf0;       // input service: list devices (jumps to 0xccf010)
inline constexpr uintptr_t deviceListReturnRva=0x16e150; // after the direct start's call at 0x56e14b
inline constexpr uintptr_t internRva=0x8cfe30;           // 0xccfe30: an id object from a name
inline constexpr uint8_t deviceListEntry[]{0x55,0x8b,0xec,0x83,0xc1,0x78,0x5d,0xe9};
inline constexpr size_t maxDeviceNames=8,maxDeviceNameLength=127,maxListedDevices=32;
struct InputDevice { uint32_t backend,family,index; };
// The list function, and the direct start's call to it (call rel32 ending at the return address).
inline bool DeviceListMatches(const uint8_t* image) {
    if(std::memcmp(image+deviceListRva,deviceListEntry,sizeof(deviceListEntry))!=0 || image[deviceListReturnRva-5]!=0xe8) return false;
    int32_t relative;
    std::memcpy(&relative,image+deviceListReturnRva-4,sizeof(relative));
    return static_cast<uintptr_t>(static_cast<intptr_t>(deviceListReturnRva)+relative)==deviceListRva;
}
// Parses "name|name|..." into names (printable, at most 127 characters, never "keyboard",
// no repeats, at most 8). Anything malformed rejects the whole value.
inline bool ParseDeviceNames(const char* text,std::vector<std::string>& names) {
    names.clear();
    if(!text || !*text) return false;
    std::vector<std::string> parsed;
    for(const char* cursor=text;;) {
        const char* end=std::strchr(cursor,'|');
        const size_t length=end ? static_cast<size_t>(end-cursor) : std::strlen(cursor);
        if(!length || length>maxDeviceNameLength || parsed.size()==maxDeviceNames) return false;
        std::string name(cursor,length);
        for(const char c:name) if(c<0x20 || c>0x7e) return false;
        if(name=="keyboard" || std::find(parsed.begin(),parsed.end(),name)!=parsed.end()) return false;
        parsed.push_back(std::move(name));
        if(!end) break;
        cursor=end+1;
    }
    names=std::move(parsed);
    return true;
}
// Which listed device to bind first: the first wanted family (in the launcher's order) that is
// listed, else the first device that isn't the keyboard; -1 when there is none. Entries without
// a backend or family are skipped, as the game's own validity check (0xcbb7c0) skips them.
inline int PreferredDevice(const InputDevice* devices,size_t count,const uint32_t* wanted,size_t wantedCount,uint32_t keyboard) {
    const auto usable=[&](size_t i) { return devices[i].backend && devices[i].family; };
    for(size_t w=0;w<wantedCount;++w)
        for(size_t i=0;i<count;++i)
            if(usable(i) && wanted[w] && devices[i].family==wanted[w]) return static_cast<int>(i);
    for(size_t i=0;i<count;++i)
        if(usable(i) && devices[i].family!=keyboard) return static_cast<int>(i);
    return -1;
}
// Moves devices[index] to the front, keeping the others in their order.
inline void MoveToFront(InputDevice* devices,size_t index) {
    std::rotate(devices,devices+index,devices+index+1);
}

bool Requested();
bool ReturnRequested();
// DIRT3VR_DIRECT_DEVICES: true when it holds a valid list.
bool DevicesRequested();
// Hooks the device list once; false leaves DiRT 3's own order (keyboard first).
bool InstallDevices(void (*log)(const char*,...));
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
