#include "desktop_seat.h"
#include "head_camera.h"
#include "seat_panel.h"
#include "seat_session.h"
#include "texture_presenter.h"
#include <d3d11.h>
#include <wrl/client.h>
#include <string>

namespace dirt3vr::desktopseat {
namespace {
using Microsoft::WRL::ComPtr;
struct State {
    DWORD thread{};
    seat::Menu* menu{};
    SeatPanelImage image;
    TexturePresenter presenter;
    ComPtr<ID3D11Device> device;
    // The selected head camera and its own values, which hold the launch seat and FOV.
    uintptr_t owner{};
    float ownEye[3]{},ownFov{};
    uint64_t carChecked{};
    std::string car;
    bool active{},drawFailed{};
};
// Deliberately leaked: D3D objects must not be released from DllMain or a global destructor.
State* state{};
bool ReadSelf(uintptr_t address,void* output,size_t bytes) {
    SIZE_T copied{};
    return address && ReadProcessMemory(GetCurrentProcess(),reinterpret_cast<const void*>(address),output,bytes,&copied) && copied==bytes;
}
bool WriteSelf(uintptr_t address,const void* input,size_t bytes) {
    SIZE_T copied{};
    return WriteProcessMemory(GetCurrentProcess(),reinterpret_cast<void*>(address),input,bytes,&copied) && copied==bytes;
}
bool GameInFront() {
    DWORD process{};
    GetWindowThreadProcessId(GetForegroundWindow(),&process);
    return process==GetCurrentProcessId();
}
// The menu's seat and FOV on the selected head camera; a new camera's own values are read first.
void Apply(State& s,const HeadCamera& head) {
    if(head.owner!=s.owner) {
        s.owner=head.owner;
        std::memcpy(s.ownEye,head.eye,sizeof(s.ownEye)); s.ownFov=head.fov;
    }
    float eye[3]{},fov{};
    HeadCamera::Seated(s.ownEye,s.ownFov,s.menu->Offset(),s.menu->FovChange(),eye,fov);
    if(std::memcmp(eye,head.eye,sizeof(eye))) WriteSelf(head.owner+HeadCamera::Eye,eye,sizeof(eye));
    if(fov!=head.fov && fov>=10 && fov<=120) WriteSelf(head.owner+HeadCamera::Fov,&fov,sizeof(fov));
}
// DiRT2VR's placement: 60% of the frame height, 3:4, at the left with a margin.
void DrawPanel(State& s,IDXGISwapChain* chain,void (*log)(const char*,...)) {
    ComPtr<ID3D11Texture2D> back;
    if(FAILED(chain->GetBuffer(0,IID_PPV_ARGS(&back)))) return;
    ComPtr<ID3D11Device> device; back->GetDevice(&device);
    if(device!=s.device) {
        s.presenter.Reset();
        if(!s.presenter.Initialize(device.Get())) { s.drawFailed=true; if(log) log("Desktop seat panel unavailable: presenter"); return; }
        s.device=device;
    }
    D3D11_TEXTURE2D_DESC desc{}; back->GetDesc(&desc);
    const unsigned height=desc.Height*3/5,width=height*3/4;
    ComPtr<ID3D11RenderTargetView> target;
    auto* panel=s.image.Render(device.Get(),*s.menu);
    if(!panel || FAILED(device->CreateRenderTargetView(back.Get(),nullptr,&target)) ||
       !s.presenter.DrawRegion(panel,target.Get(),desc.Height/20,(desc.Height-height)/2,width,height,TexturePresenter::Mode::Composite)) {
        s.drawFailed=true;
        if(log) log("Desktop seat panel draw failed format=%u",unsigned(desc.Format));
    }
}
}
void BeforePresent(IDXGISwapChain* chain,void (*log)(const char*,...)) noexcept {
    try {
        static State* const created=[log]() -> State* {
            auto* menu=OpenSeatMenu(log);
            if(!menu || !menu->Desktop()) return nullptr;
            state=new State;
            state->thread=GetCurrentThreadId(); state->menu=menu;
            if(log) log("Desktop seat menu on: monitor session");
            return state;
        }();
        if(!created || !chain || created->thread!=GetCurrentThreadId()) return;
        auto& s=*created;
        const auto now=GetTickCount64();
        if(now-s.carChecked>=1000) { // Once a second: a new race can change the car.
            s.carChecked=now;
            auto code=PlayerCarCode();
            if(code!=s.car) {
                const int index=s.menu->View().FindCar(code);
                s.menu->Select(index);
                if(log) log("Desktop seat car=%s index=%d",code.empty() ? "unknown" : code.c_str(),index);
                s.car=std::move(code);
            }
        }
        HeadCamera head;
        if(!HeadCamera::Find(reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr)),ReadSelf,head)) {
            if(s.active) { s.menu->Inactive(); s.active=false; } // Not the cockpit view: the menu closes.
            return;
        }
        s.menu->Update(now,GetTickCount(),GameInFront());
        s.active=true;
        Apply(s,head);
        if(s.menu->PanelOpen() && !s.drawFailed) DrawPanel(s,chain,log);
    } catch(...) { }
}
}
