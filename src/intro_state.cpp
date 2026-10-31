#include "intro_state.h"
#include <windows.h>
#include <MinHook.h>
#include <array>
#include <cstring>
#include <mutex>

namespace dirt3vr {
namespace {
constexpr uintptr_t EnterRva=0x0e0820,LeaveRva=0x0e09d0,TableRva=0xa95370;
constexpr uintptr_t TypeRva=0xa9a9f4,ConstructorTableRva=0x0e06fa;
constexpr unsigned char EnterEntry[]={0x55,0x8b,0xec,0x83,0xec,0x28,0x56,0x8b,0xf1,0xe8,0x42,0x90,0x82,0x00,0x8b,0x4e};
constexpr unsigned char LeaveEntry[]={0x55,0x8b,0xec,0x83,0xec,0x08,0x56,0x8b,0xf1,0xe8,0xe2,0x8f,0x82,0x00,0x8b,0x4e};
constexpr char TypeName[]="ScreenInitialLegal";
using StateFn=void (__thiscall*)(void*);
StateFn realEnter{},realLeave{};
intro_detail::IntroLatch latch;
std::atomic_bool installed{};
uintptr_t base{};
DiagnosticLogger logMessage{};

bool SafeRead(uintptr_t address,void* output,size_t length,void*) noexcept {
    MEMORY_BASIC_INFORMATION region{};
    if(!address || !output || !length || !VirtualQuery(reinterpret_cast<void*>(address),&region,sizeof(region)) ||
        region.State!=MEM_COMMIT || (region.Protect&(PAGE_NOACCESS|PAGE_GUARD))) return false;
    const auto begin=reinterpret_cast<uintptr_t>(region.BaseAddress);
    if(address<begin || address-begin>region.RegionSize || length>region.RegionSize-(address-begin)) return false;
    __try { std::memcpy(output,reinterpret_cast<void*>(address),length); return true; }
    __except(EXCEPTION_EXECUTE_HANDLER) { return false; }
}
bool LegalObject(void* self) noexcept {
    uintptr_t table{};
    return installed.load(std::memory_order_acquire) && SafeRead(reinterpret_cast<uintptr_t>(self),&table,sizeof(table),nullptr) && table==base+TableRva;
}
void __fastcall Enter(void* self,void*) {
    const bool legal=LegalObject(self);
    realEnter(self);
    if(legal) latch.Enter();
}
void __fastcall Leave(void* self,void*) {
    const bool legal=LegalObject(self);
    realLeave(self);
    if(legal) latch.Leave();
}
void Report(const char* message) noexcept { try { if(logMessage) logMessage("%s",message); } catch(...) { } }
}

bool intro_detail::ValidateContract(uintptr_t imageBase,ReadMemory read,void* context) noexcept {
    if(!imageBase || imageBase>UINT32_MAX-0xb00000 || !read) return false;
    const auto matches=[&](uintptr_t rva,const void* expected,size_t length) {
        std::array<unsigned char,32> actual{};
        return length<=actual.size() && read(imageBase+rva,actual.data(),length,context) && !std::memcmp(actual.data(),expected,length);
    };
    const auto pointer=[&](uintptr_t rva,uintptr_t expected) {
        uint32_t actual{}; return read(imageBase+rva,&actual,sizeof(actual),context) && actual==expected;
    };
    return matches(EnterRva,EnterEntry,sizeof(EnterEntry)) && matches(LeaveRva,LeaveEntry,sizeof(LeaveEntry)) &&
        pointer(TableRva+0x38,imageBase+EnterRva) && pointer(TableRva+0x44,imageBase+LeaveRva) &&
        pointer(ConstructorTableRva,imageBase+TableRva) && matches(TypeRva,TypeName,sizeof(TypeName));
}
bool InitializeIntroState(DiagnosticLogger logger) noexcept {
    if(!logger) return false;
    static std::once_flag once;
    try {
        std::call_once(once,[logger] {
            logMessage=logger;
            base=reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
            if(!intro_detail::ValidateContract(base,SafeRead,nullptr)) { Report("XR intro state refused: loaded binary contract mismatch"); return; }
            const auto initialized=MH_Initialize();
            if(initialized!=MH_OK && initialized!=MH_ERROR_ALREADY_INITIALIZED) { Report("XR intro state unavailable: MinHook initialization"); return; }
            void* targets[]={reinterpret_cast<void*>(base+EnterRva),reinterpret_cast<void*>(base+LeaveRva)};
            void* detours[]={reinterpret_cast<void*>(&Enter),reinterpret_cast<void*>(&Leave)};
            void** originals[]={reinterpret_cast<void**>(&realEnter),reinterpret_cast<void**>(&realLeave)};
            unsigned created{};
            for(;created<2;++created) if(MH_CreateHook(targets[created],detours[created],originals[created])!=MH_OK) break;
            bool enabled=created==2;
            if(enabled) for(auto* target:targets) if(MH_QueueEnableHook(target)!=MH_OK) enabled=false;
            if(enabled) enabled=MH_ApplyQueued()==MH_OK;
            if(!enabled) {
                for(unsigned i=0;i<created;++i) { MH_DisableHook(targets[i]); MH_RemoveHook(targets[i]); }
                Report("XR intro state activation failed; native callbacks retained"); return;
            }
            HMODULE pinned{};
            GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,reinterpret_cast<LPCWSTR>(&Enter),&pinned);
            installed.store(true,std::memory_order_release);
            Report("XR intro state attached: native initial-legal enter/leave; no timer");
        });
    } catch(...) { Report("XR intro state initialization failed"); }
    return installed.load(std::memory_order_acquire);
}
bool IntroScreenActive(bool currentVideoDrawn) noexcept {
    return installed.load(std::memory_order_acquire) && latch.Active(currentVideoDrawn);
}
}
