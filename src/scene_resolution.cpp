#include "scene_resolution.h"
#include <cstring>

namespace dirt3vr::sceneresolution {
namespace {
constexpr uintptr_t CreateReturnRva = 0x3c903f, RecreateReturnRva = 0x3c8503, ResizeReturnRva = 0x3c7e16;
constexpr uintptr_t DimensionsRva = 0x3c8eca, DescriptorRva = 0x3c8eb5, CreateRva = 0x3c902c;
constexpr uintptr_t WindowRva = 0x3c8da2, RecreateRva = 0x3c84e6, ResizeRva = 0x3c7de5;
constexpr unsigned char DimensionsBytes[] = {0x8b,0x4b,0x30,0x89,0x0e,0x8b,0x53,0x34,0x89,0x56,0x04};
constexpr unsigned char DescriptorBytes[] = {0x8d,0xb3,0xfc,0x00,0x00,0x00};
constexpr unsigned char CreateBytes[] = {0x8b,0x10,0x8b,0x52,0x28,0x8d,0x8b,0xf4,0x00,0x00,0x00,0x51,0x8b,0x0f,0x56,0x51,0x50,0xff,0xd2};
constexpr unsigned char WindowBytes[] = {0x89,0x93,0x64,0x01,0x00,0x00,0x8b,0x53,0x34,0x89,0x93,0x68,0x01,0x00,0x00};
constexpr unsigned char RecreateBytes[] = {0x8d,0x55,0xf0,0x52,0x8d,0x96,0xfc,0x00,0x00,0x00,0x52,0x8b,0x96,0xf0,0x00,0x00,0x00,0x89,0x5d,0xf0,0x8b,0x08,0x52,0x50,0x8b,0x41,0x28,0xff,0xd0};
constexpr unsigned char ResizeBytes[] = {0x33,0xc0,0x83,0x7e,0x3c,0x05,0x53,0x8b,0x9e,0x0c,0x01,0x00,0x00,0x53,0x8b,0x9e,0x00,0x01,0x00,0x00,0x0f,0x95,0xc0,0x53,0x8b,0x9e,0xfc,0x00,0x00,0x00,0x53,0x83,0xc0,0x02,0x89,0x86,0x24,0x01,0x00,0x00,0x8b,0x11,0x50,0x8b,0x42,0x34,0x51,0xff,0xd0};

bool Range(const void* address,size_t size,bool write=false) noexcept {
    MEMORY_BASIC_INFORMATION m{};
    if(!address || !VirtualQuery(address,&m,sizeof(m)) || m.State!=MEM_COMMIT ||
       (m.Protect&(PAGE_GUARD|PAGE_NOACCESS))) return false;
    const auto offset=reinterpret_cast<uintptr_t>(address)-reinterpret_cast<uintptr_t>(m.BaseAddress);
    return offset<=m.RegionSize && size<=m.RegionSize-offset && (!write ||
        (m.Protect&(PAGE_READWRITE|PAGE_WRITECOPY|PAGE_EXECUTE_READWRITE|PAGE_EXECUTE_WRITECOPY)));
}
bool Read(const void* source,void* target,size_t size) noexcept {
    if(!Range(source,size)) return false;
    __try { std::memcpy(target,source,size); return true; }
    __except(EXCEPTION_EXECUTE_HANDLER) { return false; }
}
template<size_t N> bool Bytes(uintptr_t address,const unsigned char (&expected)[N]) noexcept {
    unsigned char actual[N]{};
    return Read(reinterpret_cast<void*>(address),actual,N) && !std::memcmp(actual,expected,N);
}
bool FieldsAgree(const Renderer& r) noexcept {
    return !r.fullscreen && r.swap.Windowed && r.window && r.window==r.swap.OutputWindow && r.device &&
        r.width && r.height && r.width==r.windowWidth && r.height==r.windowHeight &&
        r.width==r.swap.BufferDesc.Width && r.height==r.swap.BufferDesc.Height &&
        r.swap.SampleDesc.Count==1 && r.swap.SampleDesc.Quality==0 &&
        r.swap.BufferDesc.Format==DXGI_FORMAT_R10G10B10A2_UNORM;
}
bool GameWindow(HWND window) noexcept {
    DWORD process{}; wchar_t name[64]{};
    return GetWindowThreadProcessId(window,&process) && process==GetCurrentProcessId() &&
        GetClassNameW(window,name,64) && !wcscmp(name,L"NeonClass_41");
}
Change Snapshot(Renderer* r,const Renderer& before,Size size,bool recreation=false) noexcept {
    return {r,{before.width,before.height},{before.windowWidth,before.windowHeight},
        {before.swap.BufferDesc.Width,before.swap.BufferDesc.Height},size,recreation};
}
bool SetSize(Renderer* r,Size engine,Size window,Size descriptor) noexcept {
    if(!Range(r,sizeof(*r),true)) return false;
    __try {
        r->width=engine.width; r->height=engine.height;
        r->windowWidth=window.width; r->windowHeight=window.height;
        r->swap.BufferDesc.Width=descriptor.width; r->swap.BufferDesc.Height=descriptor.height;
        return true;
    } __except(EXCEPTION_EXECUTE_HANDLER) { return false; }
}
void Restore(const Change& change) noexcept {
    Renderer r{};
    // Only roll back our six size words, and only while they are still ours.
    if(Read(change.renderer,&r,sizeof(r)) && Size{r.width,r.height}==change.effective &&
       Size{r.windowWidth,r.windowHeight}==change.effective &&
       Size{r.swap.BufferDesc.Width,r.swap.BufferDesc.Height}==change.effective)
        SetSize(change.renderer,change.engine,change.window,change.descriptor);
}
}
Change Binding::PrepareCreate(uintptr_t base,uintptr_t caller,IUnknown* device,DXGI_SWAP_CHAIN_DESC* desc,
                             IDXGISwapChain** output,const HeadsetResolutionRequest& request,DiagnosticLogger log) noexcept {
    HeadsetResolutionRequest checked;
    if(!base || !checked.Configure(request.width,request.height,request.scale,request.fovWidth,request.fovHeight) ||
       checked.sceneWidth!=request.sceneWidth || checked.sceneHeight!=request.sceneHeight) return {};
    const bool initial=caller==base+CreateReturnRva, recreation=caller==base+RecreateReturnRva;
    if((!initial && !recreation) || (initial && renderer_) ||
       (recreation && (!renderer_ || base!=base_ || owner_!=GetCurrentThreadId()))) return {};
    if(initial && (!Bytes(base+DimensionsRva,DimensionsBytes) || !Bytes(base+DescriptorRva,DescriptorBytes) ||
                  !Bytes(base+CreateRva,CreateBytes) || !Bytes(base+WindowRva,WindowBytes))) return {};
    if(recreation && !Bytes(base+RecreateRva,RecreateBytes)) return {};
    const auto address=reinterpret_cast<uintptr_t>(desc);
    if(address<offsetof(Renderer,swap) || address%alignof(Renderer)) return {};
    auto* r=reinterpret_cast<Renderer*>(address - offsetof(Renderer,swap));
    Renderer before{}; IDXGISwapChain* outputValue{};
    if(!Range(r,sizeof(*r),true) || !Read(r,&before,sizeof(before))) return {};
    if(log) log("Scene resolution candidate renderer=%p recreation=%u engine=%ux%u window=%ux%u descriptor=%ux%u fullscreen=%u windowed=%u format=%u samples=%u device_match=%u output_is_field=%u chain=%p",
        r,recreation,before.width,before.height,before.windowWidth,before.windowHeight,
        before.swap.BufferDesc.Width,before.swap.BufferDesc.Height,before.fullscreen,before.swap.Windowed,
        unsigned(before.swap.BufferDesc.Format),before.swap.SampleDesc.Count,device==before.device,output==&r->chain,before.chain);
    if(!FieldsAgree(before) ||
       !GameWindow(before.window) || device!=before.device || before.chain ||
       !Range(output,sizeof(*output),true) || !Read(output,&outputValue,sizeof(outputValue)) || outputValue) return {};
    if(initial && output!=&r->chain) return {};
    if(recreation && (r!=renderer_ || device!=device_ || before.window!=window_ ||
       Size{checked.sceneWidth,checked.sceneHeight}!=requested_)) return {};
    const Size size{checked.sceneWidth,checked.sceneHeight};
    const auto change=Snapshot(r,before,size,recreation);
    if(!SetSize(r,size,size,size)) return {};
    renderer_=r; device_=device; window_=before.window; requested_=size;
    owner_=GetCurrentThreadId(); base_=base; chain_=nullptr;
    return change;
}
void Binding::CompleteCreate(const Change& change,HRESULT result,IDXGISwapChain* chain) noexcept {
    if(!change || change.renderer!=renderer_ || owner_!=GetCurrentThreadId()) return;
    Renderer r{};
    // Recreation returns through a local variable; the game assigns its field
    // after this callback. Initial creation writes directly into renderer+f4.
    if(FAILED(result) || !chain || !Read(renderer_,&r,sizeof(r)) ||
       (change.recreation ? r.chain!=nullptr : r.chain!=chain)) {
        Restore(change); chain_=nullptr;
        if(!change.recreation) { renderer_=nullptr; device_=nullptr; window_=nullptr; }
        return;
    }
    chain_=chain;
}
Change Binding::PrepareResize(uintptr_t base,uintptr_t caller,IDXGISwapChain* chain,
                             UINT& width,UINT& height,DXGI_FORMAT format) noexcept {
    if(!chain || chain!=chain_ || base!=base_ || owner_!=GetCurrentThreadId() ||
       caller!=base+ResizeReturnRva || !Bytes(base+ResizeRva,ResizeBytes)) return {};
    Renderer before{};
    if(!Range(renderer_,sizeof(*renderer_),true) || !Read(renderer_,&before,sizeof(before)) || !FieldsAgree(before) ||
       before.chain!=chain || before.device!=device_ || before.window!=window_ ||
       (width && width!=before.swap.BufferDesc.Width) || (height && height!=before.swap.BufferDesc.Height) ||
       (format!=DXGI_FORMAT_UNKNOWN && format!=before.swap.BufferDesc.Format)) return {};
    const auto change=Snapshot(renderer_,before,requested_);
    if(!SetSize(renderer_,requested_,requested_,requested_)) return {};
    width=requested_.width; height=requested_.height;
    return change;
}
void Binding::CompleteResize(const Change& change,HRESULT result) noexcept {
    if(change && change.renderer==renderer_ && owner_==GetCurrentThreadId() && FAILED(result)) Restore(change);
}
void Binding::Forget(IDXGISwapChain* chain) noexcept {
    if(owner_==GetCurrentThreadId() && chain==chain_) chain_=nullptr;
}
}
