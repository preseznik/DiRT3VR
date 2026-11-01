#include "research_patch.h"
#include <windows.h>
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
