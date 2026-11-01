#include "research_patch.h"
#include <windows.h>
#include <MinHook.h>
#include <atomic>
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
}
void Apply(void (*log)(const char*,...)) {
    static std::once_flag once;
    std::call_once(once,[log] {
        InstallFileTrace(log);
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
