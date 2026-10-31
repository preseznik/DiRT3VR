#include "frontend_shadow_state.h"
#include <windows.h>
#include <intrin.h>
#include <MinHook.h>
#include <atomic>
#include <cmath>
#include <cstring>
#include <mutex>

namespace dirt3vr {
namespace {
constexpr uintptr_t ProducerRva=0x17cc20,CallerRva=0x1805f0;
constexpr uintptr_t RendererTableRva=0xaac84c,ContextTableRva=0xb075e8;
constexpr unsigned char ProducerEntry[]={0x53,0x8b,0xdc,0x83,0xec,0x08,0x83,0xe4,0xf0,0x83,0xc4,0x04,0x55,0x8b,0x6b,0x04};
constexpr unsigned char ProducerCall[]={0xe8,0x30,0xc6,0xff,0xff};
constexpr unsigned char MatrixStores[]={0x0f,0x28,0x85,0x60,0xff,0xff,0xff,0x0f,0x29,0x86,0x60,0x07,0x00,0x00,
    0x0f,0x28,0x85,0x70,0xff,0xff,0xff,0x0f,0x29,0x86,0x70,0x07,0x00,0x00,
    0x0f,0x28,0x45,0x80,0x0f,0x29,0x86,0x80,0x07,0x00,0x00,0x0f,0x28,0x45,0x90,0x0f,0x29,0x86,0x90,0x07,0x00,0x00};
using ProducerFn=void (__thiscall*)(void*,void*);
ProducerFn realProducer{};
uintptr_t imageBase{};
std::atomic_bool installed{};
DiagnosticLogger logMessage{};
struct Capture {
    void* owner{};
    void* context{};
    FrontendShadowMatrix inverse{},matrix{};
    bool valid{};
};
thread_local Capture pending{},scene{};
thread_local bool sceneActive{};
thread_local unsigned observations{};

bool Read(const void* address,void* output,size_t length) noexcept {
    MEMORY_BASIC_INFORMATION region{};
    if(!address || !VirtualQuery(address,&region,sizeof(region)) || region.State!=MEM_COMMIT ||
       (region.Protect&(PAGE_NOACCESS|PAGE_GUARD))) return false;
    const auto at=reinterpret_cast<uintptr_t>(address)-reinterpret_cast<uintptr_t>(region.BaseAddress);
    if(at>region.RegionSize || length>region.RegionSize-at) return false;
    __try { std::memcpy(output,address,length); return true; }
    __except(EXCEPTION_EXECUTE_HANDLER) { return false; }
}
bool Pointer(const void* address,uintptr_t expected) noexcept {
    uintptr_t actual{}; return Read(address,&actual,sizeof(actual)) && actual==expected;
}
bool Bytes(uintptr_t rva,const unsigned char* expected,size_t size) noexcept {
    unsigned char actual[64]{};
    return size<=sizeof(actual) && Read(reinterpret_cast<void*>(imageBase+rva),actual,size) && !std::memcmp(actual,expected,size);
}
void Report(const char* message) noexcept { try { if(logMessage) logMessage("%s",message); } catch(...) {} }
void __fastcall Produce(void* self,void*,void* context) {
    const bool main=reinterpret_cast<uintptr_t>(_ReturnAddress())==imageBase+CallerRva;
    if(main) pending={};
    realProducer(self,context);
    if(!main || !self || !installed.load(std::memory_order_acquire) || sceneActive ||
       !Pointer(context,imageBase+ContextTableRva)) return;
    Capture next; next.owner=self; next.context=context;
    uintptr_t texture{},camera{};
    // Native producer returns without writing its matrix if either is absent.
    if(!Read(static_cast<unsigned char*>(self)+0x4d4,&texture,sizeof(texture)) || !texture ||
       !Read(static_cast<unsigned char*>(self)+0x4dc,&camera,sizeof(camera)) || !camera ||
       !Read(static_cast<unsigned char*>(context)+0x70,next.inverse.data(),64) ||
       !Read(static_cast<unsigned char*>(self)+0x760,next.matrix.data(),64)) return;
    FrontendShadowMatrix checked{};
    next.valid=frontend_shadow_detail::Correct(next.inverse,next.inverse,next.matrix,checked);
    if(next.valid) {
        pending=next;
        if(observations++==0) Report("XR frontend shadow producer captured: same-frame native view and texture mapping");
    }
}
using Wide=std::array<double,16>;
bool Finite(const FrontendShadowMatrix& m) { for(float x:m) if(!std::isfinite(x)) return false; return true; }
bool Camera(const FrontendShadowMatrix& m) {
    if(!Finite(m) || std::abs(m[3])>1e-6f || std::abs(m[7])>1e-6f || std::abs(m[11])>1e-6f || std::abs(m[15]-1)>1e-4f) return false;
    for(unsigned row=0;row<3;++row) {
        double length{}; for(unsigned k=0;k<3;++k) length+=double(m[row*4+k])*m[row*4+k];
        if(length<.99 || length>1.01 || std::abs(m[12+row])>1e7f) return false;
        for(unsigned other=0;other<row;++other) {
            double dot{}; for(unsigned k=0;k<3;++k) dot+=double(m[row*4+k])*m[other*4+k];
            if(std::abs(dot)>.01) return false;
        }
    }
    return true;
}
}

bool frontend_shadow_detail::Correct(const FrontendShadowMatrix& a,const FrontendShadowMatrix& eye,
                                    const FrontendShadowMatrix& original,FrontendShadowMatrix& output) noexcept {
    if(!Camera(a) || !Camera(eye) || !Finite(original)) return false;
    const double d=double(a[0])*(double(a[5])*a[10]-double(a[6])*a[9])-double(a[1])*(double(a[4])*a[10]-double(a[6])*a[8])+
        double(a[2])*(double(a[4])*a[9]-double(a[5])*a[8]);
    if(!std::isfinite(d) || std::abs(d)<.99 || std::abs(d)>1.01) return false;
    Wide inv{};
    inv[0]=(double(a[5])*a[10]-double(a[6])*a[9])/d; inv[1]=(double(a[2])*a[9]-double(a[1])*a[10])/d;
    inv[2]=(double(a[1])*a[6]-double(a[2])*a[5])/d; inv[4]=(double(a[6])*a[8]-double(a[4])*a[10])/d;
    inv[5]=(double(a[0])*a[10]-double(a[2])*a[8])/d; inv[6]=(double(a[2])*a[4]-double(a[0])*a[6])/d;
    inv[8]=(double(a[4])*a[9]-double(a[5])*a[8])/d; inv[9]=(double(a[1])*a[8]-double(a[0])*a[9])/d;
    inv[10]=(double(a[0])*a[5]-double(a[1])*a[4])/d; inv[15]=1;
    for(unsigned c=0;c<3;++c) for(unsigned k=0;k<3;++k) inv[12+c]-=double(a[12+k])*inv[k*4+c];
    Wide change{};
    for(unsigned r=0;r<4;++r) for(unsigned c=0;c<4;++c) for(unsigned k=0;k<4;++k) change[r*4+c]+=double(eye[r*4+k])*inv[k*4+c];
    FrontendShadowMatrix result{};
    for(unsigned r=0;r<4;++r) for(unsigned c=0;c<4;++c) {
        double value{}; for(unsigned k=0;k<4;++k) value+=change[r*4+k]*original[k*4+c];
        result[r*4+c]=float(value);
    }
    if(!Finite(result)) return false;
    output=result; return true;
}

bool InstallFrontendShadowState(DiagnosticLogger logger) noexcept {
    if(!logger) return false;
    static std::once_flag once;
    try { std::call_once(once,[logger] {
        logMessage=logger; imageBase=reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
        if(!Bytes(ProducerRva,ProducerEntry,sizeof(ProducerEntry)) || !Bytes(0x1805eb,ProducerCall,sizeof(ProducerCall)) ||
           !Bytes(0x17d0e2,MatrixStores,sizeof(MatrixStores))) { Report("XR frontend shadow hook refused: loaded binary contract mismatch"); return; }
        const auto init=MH_Initialize();
        if(init!=MH_OK && init!=MH_ERROR_ALREADY_INITIALIZED) return;
        auto* target=reinterpret_cast<void*>(imageBase+ProducerRva);
        if(MH_CreateHook(target,reinterpret_cast<void*>(&Produce),reinterpret_cast<void**>(&realProducer))!=MH_OK) return;
        if(MH_EnableHook(target)!=MH_OK) { MH_RemoveHook(target); return; }
        HMODULE pinned{};
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS|GET_MODULE_HANDLE_EX_FLAG_PIN,reinterpret_cast<LPCWSTR>(&Produce),&pinned);
        installed.store(true,std::memory_order_release);
        Report("XR frontend shadow observer attached; correction requires matching per-eye draw constants");
    }); } catch(...) { Report("XR frontend shadow initialization failed"); }
    return installed.load(std::memory_order_acquire);
}
FrontendShadowScene::FrontendShadowScene(void* renderer,void* context) noexcept {
    const auto candidate=pending; pending={};
    if(sceneActive) return;
    sceneActive=owns_=true; scene={};
    if(!candidate.valid || candidate.context!=context || !Pointer(renderer,imageBase+RendererTableRva) ||
       !Pointer(static_cast<unsigned char*>(renderer)+0x5f4,reinterpret_cast<uintptr_t>(context)) ||
       !Pointer(static_cast<unsigned char*>(renderer)+0x9c0,reinterpret_cast<uintptr_t>(candidate.owner))) return;
    scene=candidate;
}
FrontendShadowScene::~FrontendShadowScene() { if(owns_) { scene={}; sceneActive=false; } }
void EndFrontendShadowFrame() noexcept { pending={}; }
bool FrontendShadowMatrices(FrontendShadowMatrix& expected,FrontendShadowMatrix& corrected) noexcept {
    FrontendShadowMatrix inverse{},result{};
    if(!sceneActive || !scene.valid || !Pointer(scene.context,imageBase+ContextTableRva) ||
       !Read(static_cast<unsigned char*>(scene.context)+0x70,inverse.data(),64) ||
       !frontend_shadow_detail::Correct(scene.inverse,inverse,scene.matrix,result)) return false;
    expected=scene.matrix; corrected=result; return true;
}
}
