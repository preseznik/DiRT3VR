#include "research_patch.h"
#include <windows.h>
#include <tlhelp32.h>
#include <MinHook.h>
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <mutex>
#include <string>

namespace dirt3vr::research {
namespace {
void (*traceLog)(const char*,...){};
std::atomic<unsigned> traced{};
using CreateFileWFn=HANDLE(WINAPI*)(LPCWSTR,DWORD,DWORD,LPSECURITY_ATTRIBUTES,DWORD,DWORD,HANDLE);
using CreateFileAFn=HANDLE(WINAPI*)(LPCSTR,DWORD,DWORD,LPSECURITY_ATTRIBUTES,DWORD,DWORD,HANDLE);
CreateFileWFn realCreateFileW{};
CreateFileAFn realCreateFileA{};
// Logs each opened path once per call (capped), so a run shows which data files the game reads.
void Trace(const std::string& path,DWORD access) {
    if(path.rfind("\\\\?\\",0)==0 || path.rfind("\\\\.\\",0)==0) return; // Device polling (HID, pipes).
    if(traced.fetch_add(1)>=20000) return;
    traceLog("Research file open access=0x%lx path=%s",access,path.c_str());
}
HANDLE WINAPI TraceCreateFileW(LPCWSTR name,DWORD access,DWORD share,LPSECURITY_ATTRIBUTES security,DWORD disposition,DWORD flags,HANDLE templ) {
    if(name) {
        const int size=WideCharToMultiByte(CP_UTF8,0,name,-1,nullptr,0,nullptr,nullptr);
        if(size>0 && size<4096) { std::string text(size-1,'\0'); WideCharToMultiByte(CP_UTF8,0,name,-1,text.data(),size,nullptr,nullptr); Trace(text,access); }
    }
    return realCreateFileW(name,access,share,security,disposition,flags,templ);
}
HANDLE WINAPI TraceCreateFileA(LPCSTR name,DWORD access,DWORD share,LPSECURITY_ATTRIBUTES security,DWORD disposition,DWORD flags,HANDLE templ) {
    if(name) Trace(name,access);
    return realCreateFileA(name,access,share,security,disposition,flags,templ);
}
void InstallFileTrace(void (*log)(const char*,...)) {
    wchar_t flag[8]{};
    if(GetEnvironmentVariableW(L"DIRT3VR_RESEARCH_FILE_TRACE",flag,8)!=1 || flag[0]!=L'1') return;
    traceLog=log;
    const auto initialized=MH_Initialize();
    if(initialized!=MH_OK && initialized!=MH_ERROR_ALREADY_INITIALIZED) { log("Research file trace unavailable: MinHook=%d",initialized); return; }
    const auto wide=MH_CreateHookApi(L"kernel32",  "CreateFileW",reinterpret_cast<void*>(&TraceCreateFileW),reinterpret_cast<void**>(&realCreateFileW));
    const auto narrow=MH_CreateHookApi(L"kernel32","CreateFileA",reinterpret_cast<void*>(&TraceCreateFileA),reinterpret_cast<void**>(&realCreateFileA));
    const auto enabled=MH_EnableHook(MH_ALL_HOOKS);
    log("Research file trace installed wide=%d narrow=%d enabled=%d",wide,narrow,enabled);
}

// Profile settings trace: 0x5ba530 looks a named setting up in the loaded profile's settings
// (e.g. "difficulty"; -1 when absent). Logs each distinct name=value once.
using SettingFn=int(__thiscall*)(void*,const char*);
SettingFn realSetting{};
std::mutex settingsMutex;
std::string settingsSeen;
int __fastcall TraceSetting(void* self,void*,const char* name) {
    const int value=realSetting(self,name);
    if(name && traceLog) {
        char entry[160];
        std::snprintf(entry,sizeof(entry),"|%.120s=%d|",name,value);
        std::lock_guard lock(settingsMutex);
        if(settingsSeen.size()<64*1024 && settingsSeen.find(entry)==std::string::npos) {
            settingsSeen+=entry;
            traceLog("Research setting %.120s=%d",name,value);
        }
    }
    return value;
}
// Steam Cloud trace: profile saves are read through ISteamRemoteStorage (version 012 in this
// game's steam_api.dll), not ordinary file opens. Logs each name passed to FileRead (slot 1),
// FileExists (10) and GetFileSize (12) once, with the result.
using NameFn=int(__thiscall*)(void*,const char*);
using ReadFn=int(__thiscall*)(void*,const char*,void*,int);
ReadFn realFileRead{};
NameFn realFileExists{},realFileSize{};
std::string cloudSeen;
void CloudLog(const char* call,const char* name,int result) {
    if(!name || !traceLog) return;
    char entry[200];
    std::snprintf(entry,sizeof(entry),"|%s %.150s|",call,name);
    std::lock_guard lock(settingsMutex);
    if(cloudSeen.size()<64*1024 && cloudSeen.find(entry)==std::string::npos) {
        cloudSeen+=entry;
        traceLog("Research cloud %s %.150s -> %d",call,name,result);
    }
}
int __fastcall CloudFileRead(void* self,void*,const char* name,void* data,int size) {
    const int result=realFileRead(self,name,data,size); CloudLog("FileRead",name,result); return result;
}
int __fastcall CloudFileExists(void* self,void*,const char* name) {
    const int result=realFileExists(self,name); CloudLog("FileExists",name,result&0xff); return result;
}
int __fastcall CloudFileSize(void* self,void*,const char* name) {
    const int result=realFileSize(self,name); CloudLog("GetFileSize",name,result); return result;
}
void InstallCloudTrace(void (*log)(const char*,...)) {
    wchar_t flag[8]{};
    if(GetEnvironmentVariableW(L"DIRT3VR_RESEARCH_SETTINGS_TRACE",flag,8)!=1 || flag[0]!=L'1') return;
    const HMODULE steam=GetModuleHandleW(L"steam_api.dll");
    using StorageFn=void*(__cdecl*)();
    const auto storage=steam ? reinterpret_cast<StorageFn>(GetProcAddress(steam,"SteamRemoteStorage")) : nullptr;
    void* remote=storage ? storage() : nullptr;
    if(!remote) { log("Research cloud trace unavailable: no SteamRemoteStorage"); return; }
    traceLog=log;
    auto** table=*reinterpret_cast<void***>(remote);
    const auto read=MH_CreateHook(table[1],reinterpret_cast<void*>(&CloudFileRead),reinterpret_cast<void**>(&realFileRead));
    const auto exists=MH_CreateHook(table[10],reinterpret_cast<void*>(&CloudFileExists),reinterpret_cast<void**>(&realFileExists));
    const auto size=MH_CreateHook(table[12],reinterpret_cast<void*>(&CloudFileSize),reinterpret_cast<void**>(&realFileSize));
    const auto enabled=MH_EnableHook(MH_ALL_HOOKS);
    log("Research cloud trace installed read=%d exists=%d size=%d enabled=%d",read,exists,size,enabled);
}

void InstallSettingsTrace(void (*log)(const char*,...)) {
    wchar_t flag[8]{};
    if(GetEnvironmentVariableW(L"DIRT3VR_RESEARCH_SETTINGS_TRACE",flag,8)!=1 || flag[0]!=L'1') return;
    static const uint8_t entry[]{0x55,0x8b,0xec,0x53,0x56,0x8b,0xf1,0x8b,0x46,0x08,0x83,0xcb,0xff,0x85,0xc0,0x74};
    auto* target=reinterpret_cast<uint8_t*>(GetModuleHandleW(nullptr))+0x1ba530;
    if(std::memcmp(target,entry,sizeof(entry))!=0) { log("Research settings trace refused: lookup instructions differ"); return; }
    traceLog=log;
    const auto initialized=MH_Initialize();
    if(initialized!=MH_OK && initialized!=MH_ERROR_ALREADY_INITIALIZED) { log("Research settings trace unavailable: MinHook=%d",initialized); return; }
    const auto created=MH_CreateHook(target,reinterpret_cast<void*>(&TraceSetting),reinterpret_cast<void**>(&realSetting));
    const auto enabled=created==MH_OK ? MH_EnableHook(target) : created;
    log("Research settings trace installed created=%d enabled=%d",created,enabled);
    InstallCloudTrace(log);
}

// Stack sampler: at each second listed in DIRT3VR_RESEARCH_STACKS (e.g. "40,50"), briefly
// suspends every other thread and logs its EIP and EBP-chain return addresses as module+offset.
void Describe(uintptr_t address,char* out,size_t size) {
    HMODULE module{};
    wchar_t path[MAX_PATH]{};
    if(GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                          reinterpret_cast<LPCWSTR>(address),&module) && GetModuleFileNameW(module,path,MAX_PATH)) {
        const wchar_t* name=wcsrchr(path,L'\\');
        std::snprintf(out,size,"%ls+0x%x",name ? name+1 : path,static_cast<unsigned>(address-reinterpret_cast<uintptr_t>(module)));
    } else std::snprintf(out,size,"0x%x",static_cast<unsigned>(address));
}
bool Readable(const void* address,size_t size) {
    MEMORY_BASIC_INFORMATION info{};
    if(!VirtualQuery(address,&info,sizeof(info)) || info.State!=MEM_COMMIT || (info.Protect&(PAGE_GUARD|PAGE_NOACCESS))) return false;
    return reinterpret_cast<uintptr_t>(address)+size<=reinterpret_cast<uintptr_t>(info.BaseAddress)+info.RegionSize;
}
void SampleThreads(void (*log)(const char*,...),unsigned second) {
    const HANDLE snapshot=CreateToolhelp32Snapshot(TH32CS_SNAPTHREAD,0);
    if(snapshot==INVALID_HANDLE_VALUE) return;
    THREADENTRY32 entry{sizeof(entry)};
    for(BOOL more=Thread32First(snapshot,&entry); more; more=Thread32Next(snapshot,&entry)) {
        if(entry.th32OwnerProcessID!=GetCurrentProcessId() || entry.th32ThreadID==GetCurrentThreadId()) continue;
        const HANDLE thread=OpenThread(THREAD_SUSPEND_RESUME|THREAD_GET_CONTEXT|THREAD_QUERY_INFORMATION,FALSE,entry.th32ThreadID);
        if(!thread) continue;
        uintptr_t frames[18]{};
        unsigned count{};
        if(SuspendThread(thread)!=static_cast<DWORD>(-1)) {
            CONTEXT context{};
            context.ContextFlags=CONTEXT_CONTROL|CONTEXT_INTEGER;
            if(GetThreadContext(thread,&context)) {
                frames[count++]=context.Eip;
                auto ebp=static_cast<uintptr_t>(context.Ebp);
                // Only reads the suspended thread's stack; no allocation or locks while it is stopped.
                while(count<18 && ebp>=context.Esp && ebp-context.Esp<0x100000 && Readable(reinterpret_cast<void*>(ebp),8)) {
                    const auto* frame=reinterpret_cast<const uintptr_t*>(ebp);
                    if(!frame[1] || frame[0]<=ebp) { if(frame[1]) frames[count++]=frame[1]; break; }
                    frames[count++]=frame[1];
                    ebp=frame[0];
                }
            }
            ResumeThread(thread);
        }
        CloseHandle(thread);
        std::string line;
        char text[96];
        for(unsigned i=0;i<count;++i) { Describe(frames[i],text,sizeof(text)); line+=i ? " < " : ""; line+=text; }
        log("Research stack t=%us tid=%lu: %s",second,entry.th32ThreadID,line.c_str());
    }
    CloseHandle(snapshot);
}
// Call counter: DIRT3VR_RESEARCH_COUNT lists up to 8 function addresses (hex, as in the
// disassembly at the preferred base 0x400000). Each gets a thunk that counts calls and jumps on
// unchanged; the counts are logged with every stack sample.
constexpr unsigned CountSlots=8;
volatile long callCounts[CountSlots]{};
void* countTrampolines[CountSlots]{};
unsigned countAddresses[CountSlots]{};
unsigned countInstalled{};
constexpr unsigned CapturedCalls=2,CapturedFrames=14;
void* capturedStacks[CountSlots][CapturedCalls][CapturedFrames]{};
// Keeps the call stacks of each function's first calls (logged with the counts).
// Frame 0 is the target's return address; the rest come from the caller's EBP chain.
void __cdecl CaptureCall(unsigned slot,void* returnAddress) {
    const auto call=static_cast<unsigned>(callCounts[slot]);
    if(slot>=CountSlots || call>=CapturedCalls) return;
    capturedStacks[slot][call][0]=returnAddress;
    RtlCaptureStackBackTrace(2,CapturedFrames-1,capturedStacks[slot][call]+1,nullptr);
}
// Registers and flags are saved around the capture, so the target sees its original entry state.
// After pushad (32 bytes) and pushfd (4) the target's return address is at [esp+36].
#define DIRT3VR_COUNT_THUNK(n) __declspec(naked) void CountThunk##n() { \
    __asm { pushad } __asm { pushfd } __asm { push dword ptr [esp+36] } __asm { push n } \
    __asm { call CaptureCall } __asm { add esp,8 } __asm { popfd } __asm { popad } \
    __asm { lock inc dword ptr [callCounts+n*4] } __asm { jmp dword ptr [countTrampolines+n*4] } }
DIRT3VR_COUNT_THUNK(0) DIRT3VR_COUNT_THUNK(1) DIRT3VR_COUNT_THUNK(2) DIRT3VR_COUNT_THUNK(3)
DIRT3VR_COUNT_THUNK(4) DIRT3VR_COUNT_THUNK(5) DIRT3VR_COUNT_THUNK(6) DIRT3VR_COUNT_THUNK(7)
#undef DIRT3VR_COUNT_THUNK
void (*const countThunks[CountSlots])(){&CountThunk0,&CountThunk1,&CountThunk2,&CountThunk3,
                                         &CountThunk4,&CountThunk5,&CountThunk6,&CountThunk7};
void InstallCallCounter(void (*log)(const char*,...)) {
    char text[160]{};
    const auto length=GetEnvironmentVariableA("DIRT3VR_RESEARCH_COUNT",text,sizeof(text));
    if(!length || length>=sizeof(text)) return;
    const auto initialized=MH_Initialize();
    if(initialized!=MH_OK && initialized!=MH_ERROR_ALREADY_INITIALIZED) { log("Research call counter unavailable: MinHook=%d",initialized); return; }
    auto* image=reinterpret_cast<uint8_t*>(GetModuleHandleW(nullptr));
    for(const char* cursor=text; *cursor && countInstalled<CountSlots;) {
        char* end{};
        const auto address=std::strtoul(cursor,&end,16);
        if(end==cursor || address<0x401000 || address>=0xe00000) { log("Research call counter refused: bad address list"); break; }
        void* target=image+(address-0x400000);
        const auto slot=countInstalled;
        const auto created=MH_CreateHook(target,reinterpret_cast<void*>(countThunks[slot]),&countTrampolines[slot]);
        const auto enabled=created==MH_OK ? MH_EnableHook(target) : created;
        log("Research call counter 0x%lx created=%d enabled=%d",address,created,enabled);
        if(enabled==MH_OK) countAddresses[countInstalled++]=address;
        cursor=*end==',' ? end+1 : end;
    }
}
void LogCallCounts(void (*log)(const char*,...),unsigned second) {
    if(!countInstalled) return;
    std::string line;
    char text[40];
    for(unsigned i=0;i<countInstalled;++i) {
        std::snprintf(text,sizeof(text),"%s0x%x=%ld",i ? " " : "",countAddresses[i],callCounts[i]);
        line+=text;
    }
    log("Research calls t=%us: %s",second,line.c_str());
    static bool stacksLogged[CountSlots][CapturedCalls]{};
    for(unsigned i=0;i<countInstalled;++i)
        for(unsigned call=0;call<CapturedCalls;++call) {
            if(stacksLogged[i][call] || !capturedStacks[i][call][0]) continue;
            stacksLogged[i][call]=true;
            std::string stack;
            char frame[96];
            for(unsigned f=0;f<CapturedFrames && capturedStacks[i][call][f];++f) {
                Describe(reinterpret_cast<uintptr_t>(capturedStacks[i][call][f]),frame,sizeof(frame));
                stack+=f ? " < " : "";
                stack+=frame;
            }
            log("Research call 0x%x #%u from %s",countAddresses[i],call+1,stack.c_str());
        }
}
void InstallStackSampler(void (*log)(const char*,...)) {
    static char seconds[128]{};
    const auto length=GetEnvironmentVariableA("DIRT3VR_RESEARCH_STACKS",seconds,sizeof(seconds));
    if(!length || length>=sizeof(seconds)) return;
    static void (*sampleLog)(const char*,...)=log;
    const HANDLE thread=CreateThread(nullptr,0,[](void*)->DWORD {
        const auto start=GetTickCount64();
        for(const char* cursor=seconds; *cursor;) {
            char* end{};
            const auto second=std::strtoul(cursor,&end,10);
            if(end==cursor || second>600) break;
            const auto due=start+second*1000ull;
            while(GetTickCount64()<due) Sleep(50);
            // With a call counter the counts are the point; stacks only without one.
            if(countInstalled) LogCallCounts(sampleLog,static_cast<unsigned>(second));
            else SampleThreads(sampleLog,static_cast<unsigned>(second));
            cursor=*end==',' ? end+1 : end;
        }
        return 0;
    },nullptr,0,nullptr);
    if(thread) CloseHandle(thread);
    log("Research stack sampler at %s s",seconds);
}
}
void Tick(void (*log)(const char*,...)) {
    static const unsigned laps=[] {
        char text[8]{};
        const auto length=GetEnvironmentVariableA("DIRT3VR_RESEARCH_LAPS",text,sizeof(text));
        const auto value=length && length<sizeof(text) ? std::strtoul(text,nullptr,10) : 0ul;
        return value>=1 && value<=20 ? static_cast<unsigned>(value) : 0u;
    }();
    if(!laps) return;
    auto* image=reinterpret_cast<uint8_t*>(GetModuleHandleW(nullptr));
    const auto benchmarkActive=*reinterpret_cast<volatile uint8_t*>(image+0xb9cef0);
    auto* count=reinterpret_cast<volatile uint32_t*>(image+0xb9dd64);
    static unsigned writes{};
    if(benchmarkActive==1 && *count!=laps) {
        const auto before=*count;
        *count=laps;
        if(++writes<=4) log("Research laps: benchmark count %u -> %u",before,laps);
    }
}

void Apply(void (*log)(const char*,...)) {
    static std::once_flag once;
    std::call_once(once,[log] {
        InstallFileTrace(log);
        InstallSettingsTrace(log);
        InstallCallCounter(log);
        InstallStackSampler(log);
        char text[513]{};
        const auto length=GetEnvironmentVariableA("DIRT3VR_RESEARCH_PATCH",text,sizeof(text));
        if(!length) return;
        std::vector<Patch> patches;
        if(length>=sizeof(text) || !Parse(text,patches)) { log("Research patch refused: malformed DIRT3VR_RESEARCH_PATCH"); return; }
        auto* image=reinterpret_cast<uint8_t*>(GetModuleHandleW(nullptr));
        for(const auto& patch:patches) {
            if(std::memcmp(image+patch.rva,patch.original.data(),patch.original.size())) {
                log("Research patch refused: bytes at rva=0x%x differ; nothing written",static_cast<unsigned>(patch.rva));
                return;
            }
        }
        for(const auto& patch:patches) {
            auto* target=image+patch.rva;
            DWORD protection{},ignored{};
            if(!VirtualProtect(target,patch.replacement.size(),PAGE_EXECUTE_READWRITE,&protection)) { log("Research patch rva=0x%x: VirtualProtect failed",static_cast<unsigned>(patch.rva)); continue; }
            std::memcpy(target,patch.replacement.data(),patch.replacement.size());
            FlushInstructionCache(GetCurrentProcess(),target,patch.replacement.size());
            VirtualProtect(target,patch.replacement.size(),protection,&ignored);
            log("Research patch applied rva=0x%x bytes=%u",static_cast<unsigned>(patch.rva),static_cast<unsigned>(patch.replacement.size()));
        }
    });
}
}
