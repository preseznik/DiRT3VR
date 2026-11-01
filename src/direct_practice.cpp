#include "direct_practice.h"
#include <windows.h>
#include <MinHook.h>
#include <atomic>
#include <mutex>

namespace dirt3vr::directpractice {
namespace {
bool Flag(const wchar_t* name) {
    wchar_t value[8]{};
    return GetEnvironmentVariableW(name,value,8)==1 && value[0]==L'1';
}

std::atomic<bool> installed{};
std::atomic<bool> returnInstalled{};

// Writes size bytes over original code. A failed write must not leave a partial behaviour
// change behind, so any failure puts the original bytes back.
bool WriteCode(uint8_t* target,const uint8_t* bytes,const uint8_t* original,size_t size,const char* what,void (*log)(const char*,...)) {
    DWORD protection{},ignored{};
    if(!VirtualProtect(target,size,PAGE_EXECUTE_READWRITE,&protection)) { log("%s refused: VirtualProtect failed",what); return false; }
    std::memcpy(target,bytes,size);
    const bool flushed=FlushInstructionCache(GetCurrentProcess(),target,size)!=FALSE;
    const bool restored=VirtualProtect(target,size,protection,&ignored)!=FALSE;
    if(flushed && restored) return true;
    if(VirtualProtect(target,size,PAGE_EXECUTE_READWRITE,&ignored)) {
        std::memcpy(target,original,size);
        FlushInstructionCache(GetCurrentProcess(),target,size);
        VirtualProtect(target,size,protection,&ignored);
    }
    log("%s refused: flush=%u protection_restored=%u; original code kept",what,flushed,restored);
    return false;
}
}

bool Requested() { return Flag(L"DIRT3VR_DIRECT_PRACTICE"); }
bool ReturnRequested() { return Flag(L"DIRT3VR_DIRECT_RETURN"); }

unsigned RequestedLaps() {
    wchar_t text[8]{};
    const auto length=GetEnvironmentVariableW(L"DIRT3VR_DIRECT_LAPS",text,8);
    if(length<1 || length>2) return 0;
    unsigned value{};
    for(DWORD i=0;i<length;++i) {
        if(text[i]<L'0' || text[i]>L'9') return 0;
        value=value*10+static_cast<unsigned>(text[i]-L'0');
    }
    return value>=1 && value<=maxLaps ? value : 0;
}

void Tick(void (*log)(const char*,...)) {
    static const unsigned laps=RequestedLaps();
    const bool control=installed.load(std::memory_order_acquire);
    const bool back=returnInstalled.load(std::memory_order_acquire);
    if(!(laps && control) && !back) return;
    auto* image=reinterpret_cast<uint8_t*>(GetModuleHandleW(nullptr));
    auto* active=reinterpret_cast<volatile uint8_t*>(image+benchmarkFlagRva);
    if(laps && control) {
        auto* count=reinterpret_cast<volatile uint32_t*>(image+lapsRva);
        uint32_t value=*count;
        const auto before=value;
        if(KeepLaps(*active,value,laps)) {
            *count=value;
            static unsigned writes{};
            if(++writes<=4) log("Direct practice laps: benchmark count %u -> %u",before,value);
        }
    }
    if(back) {
        auto* commandLine=reinterpret_cast<volatile uint8_t*>(image+commandLineFlagRva);
        uint8_t a=*active,c=*commandLine;
        const uint32_t requested=*reinterpret_cast<volatile uint32_t*>(image+requestedTracksRva);
        const uint32_t completed=*reinterpret_cast<volatile uint32_t*>(image+completedTracksRva);
        if(EndBenchmark(a,c,requested,completed)) {
            *active=a; *commandLine=c;
            log("Direct practice return: result written (%u/%u tracks); benchmark mode off, heading to DiRT 3's menus",completed,requested);
        }
    }
}

bool Install(void (*log)(const char*,...)) {
    static std::once_flag once;
    std::call_once(once,[log] {
        auto* image=reinterpret_cast<uint8_t*>(GetModuleHandleW(nullptr));
        const auto base=reinterpret_cast<uintptr_t>(image);
        if(!SitesMatch(image,base)) { log("Direct practice refused: controller update instructions differ; the benchmark AI keeps the car"); return; }
        const uint8_t jump=0xeb;
        if(!WriteCode(image+forceAiRva,&jump,forceAi,1,"Direct practice",log)) return;
        installed.store(true,std::memory_order_release);
        log("Direct practice: benchmark start keeps the player's car under player control (base=0x%x)",static_cast<unsigned>(base));
        if(const auto laps=RequestedLaps()) log("Direct practice laps: %u requested",laps);
        // The pause menu is separate: if only its site differs, the player still drives.
        if(!PauseSiteMatches(image,base)) log("Direct practice pause refused: pause check instructions differ; Esc stays without a menu");
        else {
            const uint8_t pauseOriginal=0x74,pauseJump=0xeb;
            if(WriteCode(image+pauseBranchRva,&pauseJump,&pauseOriginal,1,"Direct practice pause",log))
                log("Direct practice pause: Esc opens DiRT 3's pause menu");
        }
    });
    return installed.load(std::memory_order_acquire);
}

int RequestedDifficulty() {
    wchar_t text[4]{};
    const auto length=GetEnvironmentVariableW(L"DIRT3VR_DIRECT_DIFFICULTY",text,4);
    return length==1 && text[0]>=L'0' && text[0]<=L'0'+maxDifficulty ? int(text[0]-L'0') : -1;
}

namespace {
using DifficultyFn=int (__thiscall*)(void*);
DifficultyFn realDifficulty{};
void (*difficultyLog)(const char*,...){};
int requestedDifficulty=-1;
std::atomic<unsigned> difficultyReports{};

int __fastcall Difficulty(void* self,void*) {
    const int original=realDifficulty(self);
    const auto* image=reinterpret_cast<const uint8_t*>(GetModuleHandleW(nullptr));
    const auto active=*reinterpret_cast<const volatile uint8_t*>(image+benchmarkFlagRva);
    const int answer=DifficultyAnswer(active,requestedDifficulty,original);
    if(answer!=original && difficultyReports.fetch_add(1)<4)
        difficultyLog("Direct practice difficulty: AI level %d instead of the profile's %d",answer,original);
    return answer;
}
}

bool InstallDifficulty(void (*log)(const char*,...)) {
    static std::once_flag once;
    static std::atomic<bool> hooked{};
    std::call_once(once,[log] {
        requestedDifficulty=RequestedDifficulty();
        if(requestedDifficulty<0) return;
        auto* image=reinterpret_cast<uint8_t*>(GetModuleHandleW(nullptr));
        if(!DifficultySiteMatches(image)) { log("Direct practice difficulty refused: difficulty lookup instructions differ; the profile's setting applies"); return; }
        const auto init=MH_Initialize();
        if(init!=MH_OK && init!=MH_ERROR_ALREADY_INITIALIZED) { log("Direct practice difficulty refused: MinHook=%d",int(init)); return; }
        difficultyLog=log;
        auto* target=image+difficultyRva;
        if(MH_CreateHook(target,reinterpret_cast<void*>(&Difficulty),reinterpret_cast<void**>(&realDifficulty))!=MH_OK) { log("Direct practice difficulty refused: hook not created"); return; }
        if(MH_EnableHook(target)!=MH_OK) { MH_RemoveHook(target); log("Direct practice difficulty refused: hook not enabled"); return; }
        HMODULE pinned{};
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,reinterpret_cast<LPCWSTR>(&Difficulty),&pinned);
        hooked.store(true,std::memory_order_release);
        log("Direct practice difficulty: AI level %d during the benchmark start",requestedDifficulty);
    });
    return hooked.load(std::memory_order_acquire);
}

bool InstallReturn(void (*log)(const char*,...)) {
    static std::once_flag once;
    std::call_once(once,[log] {
        auto* image=reinterpret_cast<uint8_t*>(GetModuleHandleW(nullptr));
        const auto base=reinterpret_cast<uintptr_t>(image);
        if(!ExitSiteMatches(image,base)) { log("Direct practice return refused: exit instructions differ; the game quits after the finish"); return; }
        if(!WriteCode(image+exitBranchRva,skipExit,exitBranch,sizeof(skipExit),"Direct practice return",log)) return;
        returnInstalled.store(true,std::memory_order_release);
        log("Direct practice return: the finish leads back to DiRT 3's menus instead of quitting");
    });
    return returnInstalled.load(std::memory_order_acquire);
}
}
