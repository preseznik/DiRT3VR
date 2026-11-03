#include "game_vr.h"
#include "diagnostics.h"
#include "scene_camera.h"
#include "scene_projection.h"
#include "texture_presenter.h"
#include "vr_math.h"
#include "xr_runtime.h"
#include "headset_resolution.h"
#include "camera_classifier.h"
#include "vr_frame_boundary.h"
#include "hud_hooks.h"
#include "intro_state.h"
#include "frontend_shadow_state.h"
#include "recenter_shortcut.h"
#include "seat_menu.h"
#include "seat_panel.h"
#include "seat_session.h"
#include "hud_placement.h"
#include "frame_timing.h"
#include "performance_display.h"
#include "flat_views.h"
#include <windows.h>
#include <MinHook.h>
#include <array>
#include <atomic>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <stdexcept>
#include <string>
#include <optional>

namespace dirt3vr {
namespace {
using Microsoft::WRL::ComPtr;
constexpr uintptr_t VelocityTableRva=0xaca3a8, HistorySelectRva=0x332d90, HistoryInvalidateRva=0x332c50;
using HistorySelectFn=void (__thiscall*)(void*,unsigned);
using HistoryInvalidateFn=void (__thiscall*)(void*);
constexpr std::array<size_t,4> PoseOffsets{0x10,0x20,0x30,0x40};
static_assert(SeatPanelImage::Width==XrRuntime::SeatPanelWidth && SeatPanelImage::Height==XrRuntime::SeatPanelHeight);

void RuntimeLog(const char*) noexcept;
std::wstring EnvironmentPath(const wchar_t* variable);
bool GameInFront();
struct PrototypeState {
    DWORD ownerThread=GetCurrentThreadId();
    DiagnosticLogger log{};
    XrRuntime xr{RuntimeLog};
    TexturePresenter presenter{RuntimeLog};
    TexturePresenter hudPresenter{RuntimeLog};
    HudCapture hud;
    ComPtr<ID3D11Device> device;
    HeadsetResolutionRequest resolution;
    std::wstring stopFile;
    VrHistoryContinuity history;
    uint64_t pairs{},previousSceneTick{};
    unsigned timingReports{};
    unsigned presentationReports{},previousTransitions{};
    bool previousVideo{},previousIntroScreen{};
    VrFrameBoundary boundary;
    std::optional<XrRuntime::Frame> pendingFrame;
    uint64_t pendingScene{};
    void* pendingPlugin{};
    std::array<unsigned,6> pendingHistory{}; // before, first, second, advance, uploads L/R
    CameraKind previousCamera=CameraKind::Unknown;
    uintptr_t previousCameraOwner{};
    bool previousStereoEligible{}, cameraReported{};
    RecenterPoll recenter;
    RecenterPoll toggle;
    bool flat{}; // Toggle VR: the player switched to the flat virtual screen.
    unsigned flatViews{}; // flatviews::Group bits the player sends to the flat screen (#48208327).
    bool basisPending=true;
    unsigned basisSamples{};
    bool rendering{}, windowReported{};
    HWND window{};
    WNDPROC previousWindowProc{};
    bool attempted{}, stopped{}, stopPathChecked{}, waitingReported{};
    // In-game seat menu (#35826889), when the launcher passes DIRT3VR_SEAT_CHANNEL.
    seat::Menu* seat{};
    SeatPanelImage seatImage;
    TexturePresenter seatPresenter{RuntimeLog};
    XrPosef seatPanelPose{{0,0,0,1},{0,0,-1}};
    unsigned seatOpenings{};
    bool seatActive{};
    uint64_t seatCarChecked{};
    std::string seatCar;
    // FPS / GPU strip in the headset (#4896ffd3), when DIRT3VR_PERF_DISPLAY is "1".
    PerformanceStrip perf;
    TexturePresenter perfPresenter{RuntimeLog};
    bool perfDisplay{},perfFollow{};
};
// Explicit render-thread teardown is necessary. A static XR destructor would
// otherwise run under the DLL loader lock during process exit.
std::atomic<PrototypeState*> prototype{};
std::atomic<DiagnosticLogger> configuredLogger{};
using ExitProcessFn=void (WINAPI*)(UINT);
ExitProcessFn realExitProcess{};
void RuntimeLog(const char* message) noexcept {
    const auto* state=prototype.load(std::memory_order_acquire);
    try { if(state && state->log) state->log("%s",message); } catch(...) { }
}
bool Enabled() noexcept {
    wchar_t flag[8]{};
    return GetEnvironmentVariableW(L"DIRT3VR_XR_PROTOTYPE",flag,8)==1 && flag[0]==L'1';
}
PrototypeState* State() noexcept {
    try {
    auto* state=prototype.load(std::memory_order_acquire);
    if(!state && configuredLogger.load(std::memory_order_acquire)) {
        static auto* owned=[] {
            auto* value=new PrototypeState;
            value->log=configuredLogger.load(std::memory_order_acquire);
            prototype.store(value,std::memory_order_release);
            return value;
        }();
        state=owned;
    }
    return state;
    } catch(...) { return nullptr; }
}
bool Range(const void* pointer,size_t length,bool write=false) noexcept {
    MEMORY_BASIC_INFORMATION region{};
    if(!pointer || !VirtualQuery(pointer,&region,sizeof(region)) || region.State!=MEM_COMMIT ||
       (region.Protect&(PAGE_NOACCESS|PAGE_GUARD))) return false;
    const auto start=reinterpret_cast<uintptr_t>(pointer);
    const auto end=reinterpret_cast<uintptr_t>(region.BaseAddress)+region.RegionSize;
    if(start>end || length>end-start) return false;
    return !write || (region.Protect&(PAGE_READWRITE|PAGE_WRITECOPY|PAGE_EXECUTE_READWRITE|PAGE_EXECUTE_WRITECOPY));
}
bool Read(const void* pointer,void* output,size_t length) noexcept {
    if(!Range(pointer,length)) return false;
    __try { std::memcpy(output,pointer,length); return true; }
    __except(EXCEPTION_EXECUTE_HANDLER) { return false; }
}
bool WritePose(void* pointer,const CameraRecord& record,bool historyByte) noexcept {
    if(!Range(pointer,record.size(),true)) return false;
    auto* destination=static_cast<unsigned char*>(pointer);
    __try {
        for(const auto offset:PoseOffsets) std::memcpy(destination+offset,record.data()+offset,12);
        if(historyByte) destination[0x8c]=record[0x8c];
        return true;
    } __except(EXCEPTION_EXECUTE_HANDLER) { return false; }
}
bool WriteNear(void* pointer,float value) noexcept {
    if(!Range(pointer,sizeof(value),true)) return false;
    __try { std::memcpy(pointer,&value,sizeof(value)); return true; }
    __except(EXCEPTION_EXECUTE_HANDLER) { return false; }
}
class ScopedRendererNear {
public:
    explicit ScopedRendererNear(void* renderer):address_(static_cast<unsigned char*>(renderer)+0x40) {
        valid_=Read(address_,&original_,sizeof(original_)) && std::isfinite(original_) && original_>0 && Range(address_,sizeof(original_),true);
    }
    ~ScopedRendererNear() { Restore(); }
    bool Valid() const noexcept { return valid_; }
    bool Restore() noexcept {
        if(!valid_) return false;
        float actual{};
        return WriteNear(address_,original_) && Read(address_,&actual,sizeof(actual)) && actual==original_;
    }
private:
    void* address_{};
    float original_{};
    bool valid_{};
};
bool SamePose(const CameraRecord& before,const CameraRecord& after,bool historyByte) noexcept {
    for(const auto offset:PoseOffsets) if(std::memcmp(before.data()+offset,after.data()+offset,12)) return false;
    return !historyByte || before[0x8c]==after[0x8c];
}
CameraRecord EyeCamera(const CameraRecord& original,const XrPosef& pose) {
    if(!math::Finite(pose.position)) throw std::invalid_argument("Invalid eye position");
    const auto q=math::Normalize(pose.orientation);
    const auto x=math::Rotate(q,{1,0,0}),y=math::Rotate(q,{0,1,0}),z=math::Rotate(q,{0,0,1});
    CameraRecord changed=original;
    // DiRT2VR ApplyEyePose, with the same verified DiRT3 XYZ locations. The
    // prototype uses one game unit per metre; W/padding and lens fields stay native.
    for(unsigned axis=0;axis<3;++axis) {
        const float right=-CameraFloat(original,0x20+axis*4),up=CameraFloat(original,0x10+axis*4),back=-CameraFloat(original,0x30+axis*4);
        const auto world=[&](XrVector3f v) { return right*v.x+up*v.y+back*v.z; };
        const float values[]={world(y),-world(x),-world(z),CameraFloat(original,0x40+axis*4)+world(pose.position)};
        for(unsigned vector=0;vector<4;++vector) {
            if(!std::isfinite(values[vector])) throw std::invalid_argument("Invalid eye camera");
            std::memcpy(changed.data()+PoseOffsets[vector]+axis*4,&values[vector],sizeof(float));
        }
    }
    if(!ValidReplayCamera(changed)) throw std::invalid_argument("Invalid transformed camera");
    return changed;
}
class ScopedEyeCameras {
public:
    ScopedEyeCameras(void* a,void* b,const CameraRecord& originalA,const CameraRecord& originalB,bool second)
        : cameras_{a,b},originals_{originalA,originalB},second_(second) {}
    ~ScopedEyeCameras() { Restore(); }
    bool Apply(const XrPosef& pose) {
        auto a=EyeCamera(originals_[0],pose),b=EyeCamera(originals_[1],pose);
        if(second_) b[0x8c]=0;
        touched_=true;
        return WritePose(cameras_[0],a,false) && WritePose(cameras_[1],b,second_);
    }
    bool Restore() noexcept {
        if(!touched_) return true;
        const bool a=WritePose(cameras_[0],originals_[0],false),b=WritePose(cameras_[1],originals_[1],second_);
        touched_=false;
        CameraRecord afterA{},afterB{};
        return a && b && Read(cameras_[0],afterA.data(),afterA.size()) && Read(cameras_[1],afterB.data(),afterB.size()) &&
            SamePose(originals_[0],afterA,false) && SamePose(originals_[1],afterB,second_);
    }
    ScopedEyeCameras(const ScopedEyeCameras&)=delete;
    ScopedEyeCameras& operator=(const ScopedEyeCameras&)=delete;
private:
    std::array<void*,2> cameras_;
    std::array<CameraRecord,2> originals_;
    bool second_{},touched_{};
};
struct RestoreSelector {
    void* plugin;
    HistorySelectFn select;
    ~RestoreSelector() { select(plugin,0); }
};
void Stop(PrototypeState& state,const char* reason) noexcept {
    if(GetCurrentThreadId()!=state.ownerThread || state.stopped) return;
    state.stopped=true;
    SetSceneProjectionActive(false);
    try {
        if(state.log) {
            state.log("XR prototype stopped reason=%s",reason);
            if(state.device) {
                const HRESULT removed=state.device->GetDeviceRemovedReason();
                if(FAILED(removed)) state.log("XR graphics device removed reason=0x%08lx",static_cast<unsigned long>(removed));
            }
        }
    } catch(...) { }
    state.pendingFrame.reset(); EndPresentationFrame(); ResetFrontendShadowResources();
    try { frametiming::Stop(); } catch(...) { } // Writes the final performance-summary.json.
    if(state.seat) state.seat->Inactive();
    state.seatActive=false; state.seatPresenter.Reset(); state.perfPresenter.Reset();
    state.hud.Reset(); state.hudPresenter.Reset(); state.presenter.Reset(); state.xr.Shutdown(); state.device.Reset();
    RuntimeLog("XR prototype shutdown complete");
}
void StopForExit(const char* reason) noexcept {
    auto* state=prototype.load(std::memory_order_acquire);
    if(!state) return;
    if(GetCurrentThreadId()!=state->ownerThread || state->rendering) {
        RuntimeLog("XR exit cleanup unavailable: render owner is not at a safe boundary");
        return; // Never destroy XR or D3D resources from another thread.
    }
    Stop(*state,reason);
}
void WINAPI ExitGame(UINT code) {
    StopForExit("game-process-exit");
    realExitProcess(code);
}
LRESULT CALLBACK GameWindowProc(HWND window,UINT message,WPARAM wparam,LPARAM lparam) {
    auto* state=prototype.load(std::memory_order_acquire);
    const auto previous=state->previousWindowProc;
    if(message==WM_CLOSE || message==WM_DESTROY || (message==WM_SYSCOMMAND && (wparam&0xfff0)==SC_CLOSE))
        StopForExit("game-window-close");
    if(message==WM_NCDESTROY) {
        if(reinterpret_cast<WNDPROC>(GetWindowLongPtrW(window,GWLP_WNDPROC))==GameWindowProc)
            SetWindowLongPtrW(window,GWLP_WNDPROC,reinterpret_cast<LONG_PTR>(previous));
        state->window=nullptr;
    }
    return CallWindowProcW(previous,window,message,wparam,lparam);
}
void AttachWindow(PrototypeState& state,IDXGISwapChain* chain) {
    if(state.window || state.windowReported || !chain) return;
    DXGI_SWAP_CHAIN_DESC desc{}; DWORD process{};
    if(FAILED(chain->GetDesc(&desc)) || !desc.OutputWindow) return;
    state.windowReported=true;
    if(GetWindowThreadProcessId(desc.OutputWindow,&process)!=state.ownerThread || process!=GetCurrentProcessId()) {
        RuntimeLog("XR window cleanup unavailable: window and render thread differ"); return;
    }
    const auto previous=reinterpret_cast<WNDPROC>(GetWindowLongPtrW(desc.OutputWindow,GWLP_WNDPROC));
    if(!previous || previous==GameWindowProc) return;
    state.previousWindowProc=previous;
    SetLastError(0);
    const auto replaced=SetWindowLongPtrW(desc.OutputWindow,GWLP_WNDPROC,reinterpret_cast<LONG_PTR>(GameWindowProc));
    if(!replaced && GetLastError()) { RuntimeLog("XR window cleanup could not attach"); return; }
    state.previousWindowProc=reinterpret_cast<WNDPROC>(replaced);
    state.window=desc.OutputWindow;
    RuntimeLog("XR window cleanup attached on render owner");
}
struct RenderingScope {
    PrototypeState& state;
    bool previous;
    explicit RenderingScope(PrototypeState& value):state(value),previous(value.rendering) { state.rendering=true; }
    ~RenderingScope() { state.rendering=previous; }
};
bool StopRequested(PrototypeState& state) {
    if(!state.stopPathChecked) {
        state.stopPathChecked=true;
        const DWORD size=GetEnvironmentVariableW(L"DIRT3VR_XR_STOP_FILE",nullptr,0);
        if(!size || size>32768) { Stop(state,"missing-stop-path"); return true; }
        std::wstring value(size,L'\0');
        const DWORD copied=GetEnvironmentVariableW(L"DIRT3VR_XR_STOP_FILE",value.data(),size);
        if(!copied || copied>=size) { Stop(state,"invalid-stop-path"); return true; }
        value.resize(copied);
        if(!std::filesystem::path(value).is_absolute()) { Stop(state,"relative-stop-path"); return true; }
        state.stopFile=std::move(value);
    }
    const DWORD attributes=GetFileAttributesW(state.stopFile.c_str());
    if(attributes!=INVALID_FILE_ATTRIBUTES && !(attributes&FILE_ATTRIBUTE_DIRECTORY)) {
        Stop(state,"runner-stop-file"); return true;
    }
    return state.stopped;
}
bool SceneMatches(const HeadsetResolutionRequest& request,ID3D11Texture2D* back) noexcept {
    if(!request.width) return true; // Bounded legacy benchmark remains independent.
    D3D11_TEXTURE2D_DESC desc{}; back->GetDesc(&desc);
    return desc.Width==request.sceneWidth && desc.Height==request.sceneHeight;
}
// The launcher's VR HUD placement (#3652b9aa), read once.
const HudPlacement& ConfiguredHudPlacement() {
    static const HudPlacement placement=[] {
        HudPlacement value; char text[16]{};
        DWORD length=GetEnvironmentVariableA("DIRT3VR_HUD_DISTANCE",text,sizeof(text));
        if(length && length<sizeof(text)) value.distance=HudPlacement::ParseDistance(std::string_view(text,length));
        length=GetEnvironmentVariableA("DIRT3VR_HUD_FOLLOW",text,sizeof(text));
        value.follow=length && length<sizeof(text) && HudPlacement::ParseFollow(std::string_view(text,length));
        char message[96]{};
        snprintf(message,sizeof(message),"XR HUD distance=%.2f m follow=%u",value.distance,unsigned(value.follow));
        RuntimeLog(message);
        return value;
    }();
    return placement;
}
// The launcher's seat channel (docs/seat-channel.md), shared with monitor sessions (src/seat_session.*).
void OpenSeatChannel(PrototypeState& state,ID3D11Device* device) {
    auto* menu=OpenSeatMenu(state.log);
    if(!menu || menu->Desktop()) return;
    if(!state.seatPresenter.Initialize(device)) { RuntimeLog("Seat menu panel unavailable: presenter"); return; }
    state.seat=menu;
}
// 1 m ahead of the head, upright and facing it, where the menu opened (DiRT2VR's panel placement).
XrPosef SeatPanelPose(const std::array<XrView,2>& views) {
    const auto head=math::RecenterPose(math::CenterPose(views),{{0,0,0,1},{0,0,0}});
    return math::OffsetPose(head,{0,-.1f,-1.f});
}
// One stereo frame's seat menu step: cockpit frames run the menu, others close it once.
seat::Position UpdateSeat(PrototypeState& state,const XrRuntime::Frame& frame,bool cockpit) {
    if(!state.seat) return {};
    if(!cockpit) {
        if(state.seatActive) { state.seat->Inactive(); state.seatActive=false; }
        return {};
    }
    const auto now=GetTickCount64();
    if(now-state.seatCarChecked>=1000) { // Once a second: a new race can change the car.
        state.seatCarChecked=now;
        auto code=PlayerCarCode();
        if(code!=state.seatCar) {
            const int index=state.seat->View().FindCar(code);
            state.seat->Select(index);
            if(state.log) state.log("Seat menu car=%s index=%d",code.empty() ? "unknown" : code.c_str(),index);
            state.seatCar=std::move(code);
        }
    }
    state.seat->Update(now,GetTickCount(),GameInFront());
    state.seatActive=true;
    if(state.seat->Openings()!=state.seatOpenings) {
        state.seatOpenings=state.seat->Openings();
        state.seatPanelPose=SeatPanelPose(frame.Views());
    }
    return state.seat->Offset();
}
// A launcher switch: "1" turns it on, anything else (or nothing) leaves it off.
bool EnvironmentFlag(const char* variable) {
    char text[4]{};
    return GetEnvironmentVariableA(variable,text,sizeof(text))==1 && text[0]=='1';
}
// The FPS / GPU strip below the HUD, refreshed once per second. Only a drawn strip is acquired.
void DrawPerfPanel(PrototypeState& state,XrRuntime::Frame& frame) {
    if(!state.perfDisplay) return;
    auto* strip=state.perf.Render(state.device.Get(),GetTickCount64(),frametiming::GpuPercent());
    if(!strip) return;
    auto image=frame.AcquirePerfPanel();
    if(image && state.perfPresenter.Draw(strip,image.Target(),image.Width(),image.Height()) && image.Complete())
        frame.ShowPerfPanel(state.perfFollow);
}
// The open seat menu's panel, on top of the HUD. Only an image that was drawn is acquired.
void DrawSeatPanel(PrototypeState& state,XrRuntime::Frame& frame) {
    if(!state.seat || !state.seat->PanelOpen()) return;
    auto* panel=state.seatImage.Render(state.device.Get(),*state.seat);
    if(!panel) return;
    auto image=frame.AcquireSeatPanel();
    if(image && state.seatPresenter.Draw(panel,image.Target(),image.Width(),image.Height(),TexturePresenter::Mode::StraightAlpha) && image.Complete())
        frame.ShowSeatPanel(state.seatPanelPose,.6f,.8f);
}
bool Prepare(PrototypeState& state,ID3D11Texture2D* back) {
    if(InteractiveVrEnabled()) BindPresentationBackbuffer(back);
    ComPtr<ID3D11Device> device; back->GetDevice(&device);
    if(state.attempted) {
        if(device.Get()!=state.device.Get()) { Stop(state,"game-device-changed"); return false; }
        if(!SceneMatches(state.resolution,back)) { Stop(state,"scene-resolution-changed"); return false; }
        return !state.stopped;
    }
    state.attempted=true;
    if(InteractiveVrEnabled()) {
        if(!ReadResolution(state.resolution)) { Stop(state,"invalid-headset-resolution-request"); return false; }
        if(!SceneMatches(state.resolution,back)) {
            D3D11_TEXTURE2D_DESC desc{}; back->GetDesc(&desc);
            if(state.log) state.log("XR scene resolution mismatch requested=%ux%u actual=%ux%u",state.resolution.sceneWidth,state.resolution.sceneHeight,desc.Width,desc.Height);
            Stop(state,"scene-resolution-mismatch"); return false;
        }
    }
    if(InteractiveVrEnabled()) {
        OpenSeatChannel(state,device.Get());
        state.perfDisplay=EnvironmentFlag("DIRT3VR_PERF_DISPLAY") && state.perfPresenter.Initialize(device.Get());
        state.perfFollow=EnvironmentFlag("DIRT3VR_PERF_FOLLOW");
        if(state.log) state.log("XR FPS display=%u follow=%u",unsigned(state.perfDisplay),unsigned(state.perfFollow));
        char views[64]{};
        const auto length=GetEnvironmentVariableA("DIRT3VR_FLAT_VIEWS",views,sizeof(views));
        const bool valid=length<sizeof(views) && flatviews::Parse(std::string_view(views,length),state.flatViews);
        if(state.log) state.log("XR flat views=%s car=%u cinematic=%u menus=%u",valid ? "ok" : "refused",
            unsigned((state.flatViews&flatviews::Car)!=0),unsigned((state.flatViews&flatviews::Cinematic)!=0),unsigned((state.flatViews&flatviews::Menus)!=0));
    }
    if(!state.presenter.Initialize(device.Get()) ||
       (state.resolution.width && !state.hudPresenter.Initialize(device.Get())) ||
       !state.xr.Initialize(device.Get(),state.resolution.width ? 1.f : .5f,state.resolution.width,state.resolution.height,state.resolution.width!=0,
                            state.seat!=nullptr,state.perfDisplay) ||
       !state.xr.CropFieldOfView(state.resolution.fovWidth/100.f,state.resolution.fovHeight/100.f)) {
        Stop(state,"initialization"); return false;
    }
    state.device=device;
    // The session's performance summary (#4896ffd3) for the launcher's Last VR session line.
    if(InteractiveVrEnabled()) {
        const auto folder=EnvironmentPath(L"DIRT3VR_LOG_DIR");
        const bool timing=!folder.empty() && frametiming::Start(device.Get(),folder,state.resolution.width,state.resolution.height,state.resolution.scale);
        RuntimeLog(timing ? "XR performance summary on" : "XR performance summary unavailable");
    }
    if(state.log) state.log("XR prototype enabled headset=%ux%u render_scale=%u fov=%u/%u scene=%ux%u game_units_per_metre=1; first tracked focused frame required",
        state.resolution.width,state.resolution.height,state.resolution.scale,state.resolution.fovWidth,state.resolution.fovHeight,
        state.resolution.sceneWidth,state.resolution.sceneHeight);
    return true;
}
RecenterShortcut ConfiguredRecenter() {
    char text[32]{};
    const DWORD length=GetEnvironmentVariableA("DIRT3VR_RECENTER_KEY",text,sizeof(text));
    RecenterShortcut value;
    if(length>0 && (length>=sizeof(text) || !RecenterShortcut::Parse(std::string_view(text,length),value))) {
        RuntimeLog("XR recenter key setting rejected; using F10");
        return RecenterShortcut{};
    }
    RuntimeLog(("XR recenter key="+value.Describe()).c_str());
    return value;
}
// The toggle VR key the launcher passes ("key modifiers", F6 by default there). The launcher leaves
// the variable out for "No key", so a missing or rejected value means no key.
std::optional<RecenterShortcut> ConfiguredToggle() {
    char text[32]{};
    const DWORD length=GetEnvironmentVariableA("DIRT3VR_TOGGLE_KEY",text,sizeof(text));
    RecenterShortcut value;
    if(!length) { RuntimeLog("XR toggle VR key=none"); return std::nullopt; }
    if(length>=sizeof(text) || !RecenterShortcut::Parse(std::string_view(text,length),value)) {
        RuntimeLog("XR toggle VR key setting rejected; no key"); return std::nullopt;
    }
    RuntimeLog(("XR toggle VR key="+value.Describe()).c_str());
    return value;
}
// An absolute path from the launcher, or empty.
std::wstring EnvironmentPath(const wchar_t* variable) {
    const DWORD size=GetEnvironmentVariableW(variable,nullptr,0);
    if(!size || size>32768) return {};
    std::wstring value(size,L'\0');
    const DWORD copied=GetEnvironmentVariableW(variable,value.data(),size);
    if(!copied || copied>=size) return {};
    value.resize(copied);
    return std::filesystem::path(value).is_absolute() ? value : std::wstring{};
}
// The launcher creates such a file when a bound controller/wheel button is pressed with DiRT 3 in
// front; taking the request deletes it.
bool TakeRequest(const std::wstring& path) { return !path.empty() && DeleteFileW(path.c_str())!=FALSE; }
bool DeviceRecenterRequested() {
    static const std::wstring path=EnvironmentPath(L"DIRT3VR_RECENTER_REQUEST_FILE");
    return TakeRequest(path);
}
unsigned HeldModifiers() {
    return (GetAsyncKeyState(VK_CONTROL)<0?1u:0u)|(GetAsyncKeyState(VK_MENU)<0?2u:0u)|(GetAsyncKeyState(VK_SHIFT)<0?4u:0u);
}
bool GameInFront() {
    DWORD process{};
    GetWindowThreadProcessId(GetForegroundWindow(),&process);
    return process==GetCurrentProcessId();
}
// Toggle VR (#288ceec1): the bound key (DIRT3VR_TOGGLE_KEY) or a controller button
// (DIRT3VR_TOGGLE_REQUEST_FILE) switches between stereo and the flat virtual screen while playing.
// Every session starts in stereo.
void ToggleKey(PrototypeState& state) {
    static const auto binding=ConfiguredToggle();
    static const std::wstring request=EnvironmentPath(L"DIRT3VR_TOGGLE_REQUEST_FILE");
    const bool keyPressed=binding && state.toggle.Update(*binding,GetAsyncKeyState(static_cast<int>(binding->key))<0,HeldModifiers(),GameInFront());
    const bool devicePressed=TakeRequest(request);
    if(!keyPressed && !devicePressed) return;
    state.flat=!state.flat;
    state.history.Reset(); // The next stereo pair starts without history from before the switch.
    RuntimeLog(state.flat ? "XR view toggled flat=1 (virtual screen)" : "XR view toggled flat=0 (stereo)");
}
// DIRT3VR_RECENTER_TILT=1: the player chose to include pitch and roll when recentering.
bool ConfiguredRecenterTilt() {
    wchar_t value[4]{};
    return GetEnvironmentVariableW(L"DIRT3VR_RECENTER_TILT",value,4)==1 && value[0]==L'1';
}
void RecenterKey(PrototypeState& state) {
    static const RecenterShortcut binding=ConfiguredRecenter();
    static const bool tilt=ConfiguredRecenterTilt();
    const bool keyDown=GetAsyncKeyState(static_cast<int>(binding.key))<0;
    // Consume a button request every frame, so a key press in the same frame can't leave it for a second recenter.
    const bool keyPressed=state.recenter.Update(binding,keyDown,HeldModifiers(),GameInFront());
    const bool devicePressed=DeviceRecenterRequested();
    if(keyPressed || devicePressed) {
        state.xr.RequestRecenter(tilt);
        RuntimeLog(((keyPressed ? "XR recenter requested key="+binding.Describe() : std::string("XR recenter requested by controller/wheel button")) + (tilt ? " tilt=1" : "")).c_str());
    }
}
}

bool InteractiveVrEnabled() noexcept {
    wchar_t flag[8]{};
    return GetEnvironmentVariableW(L"DIRT3VR_XR_INTERACTIVE",flag,8)==1 && flag[0]==L'1';
}
bool ConfigureVrPresentation(DiagnosticLogger logger) noexcept {
    if(InteractiveVrEnabled() && !HudHooksReady()) return false;
    if(!Enabled() || !logger || (InteractiveVrEnabled() && (!InitializeCameraClassifier(logger) || !InitializeIntroState(logger) ||
        !InstallFrontendShadowState(logger)))) return false;
    if(InteractiveVrEnabled()) {
        auto* address=reinterpret_cast<void*>(GetProcAddress(GetModuleHandleW(L"kernel32.dll"),"ExitProcess"));
        if(!address || MH_CreateHook(address,reinterpret_cast<void*>(&ExitGame),reinterpret_cast<void**>(&realExitProcess))!=MH_OK) return false;
        if(MH_EnableHook(address)!=MH_OK) { MH_RemoveHook(address); return false; }
    }
    configuredLogger.store(logger,std::memory_order_release);
    SetSceneProjectionActive(true);
    return true;
}
void VrBeforePresent(IDXGISwapChain* chain) noexcept {
    // A startup splash or another in-process DXGI surface must not select the
    // render owner or start a headset session before the actual game window.
    if(InteractiveVrEnabled() && !prototype.load(std::memory_order_acquire)) {
        DXGI_SWAP_CHAIN_DESC desc{}; DWORD process{}; wchar_t name[64]{};
        if(!chain || FAILED(chain->GetDesc(&desc)) || !desc.OutputWindow ||
           !GetWindowThreadProcessId(desc.OutputWindow,&process) || process!=GetCurrentProcessId() ||
           !GetClassNameW(desc.OutputWindow,name,64) || wcscmp(name,L"NeonClass_41")) return;
    }
    auto* state=InteractiveVrEnabled() ? State() : prototype.load(std::memory_order_acquire);
    if(!state || GetCurrentThreadId()!=state->ownerThread || state->stopped || state->rendering) return;
    TraceBeforePresent();
    struct Advance { VrFrameBoundary& boundary; ~Advance() { EndPresentationFrame(); boundary.Presented(); } } advance{state->boundary};
    try {
        if(InteractiveVrEnabled()) AttachWindow(*state,chain);
        if(StopRequested(*state)) return;
        RenderingScope rendering(*state);
        RecenterKey(*state);
        if(InteractiveVrEnabled()) ToggleKey(*state);
        if(state->pendingFrame) {
            // Native intro playback draws after RenderVrScene. End exactly the
            // existing XR frame here, after all game draws, never a second frame.
            ComPtr<ID3D11Texture2D> back; std::filesystem::path directory;
            if(!AcquireDiagnosticBackbuffer(&back,directory) || !Prepare(*state,back.Get())) {
                Stop(*state,"pending-backbuffer-unavailable"); return;
            }
            const bool video=IntroVideoDrawn();
            const bool introScreen=IntroScreenActive(video);
            const auto transitions=TransitionDrawsSuppressed();
            auto& frame=*state->pendingFrame;
            if(!introScreen) { DrawPerfPanel(*state,frame); DrawSeatPanel(*state,frame); }
            auto* image=state->hud.Current(state->pendingScene);
            bool successful{};
            if(introScreen || image) {
                auto overlay=frame.AcquireOverlay();
                const bool copied=overlay && state->hudPresenter.Draw(introScreen ? back.Get() : image,
                    overlay.Target(),overlay.Width(),overlay.Height(),introScreen ? TexturePresenter::Mode::Opaque : TexturePresenter::Mode::StraightAlpha) && overlay.Complete();
                const auto& placement=ConfiguredHudPlacement();
                if(copied) successful=introScreen ? frame.SubmitVideo(16.f/9.f) :
                    frame.SubmitOverlay(state->hud.Aspect(),placement.distance,placement.Width(state->hud.PanelWidth()),placement.follow);
                else frame.Abort();
            } else successful=frame.Submit();
            state->pendingFrame.reset();
            if((video!=state->previousVideo || introScreen!=state->previousIntroScreen || transitions!=state->previousTransitions) && state->presentationReports<32) {
                state->log("XR presentation main=%llu late_video=%u intro_screen=%u transition_draws_skipped=%u submitted=%u",state->pendingScene,video,introScreen,transitions,successful);
                ++state->presentationReports;
            }
            state->previousVideo=video; state->previousIntroScreen=introScreen; state->previousTransitions=transitions;
            if(successful) frametiming::Submitted(!introScreen);
            if(successful && !introScreen) state->perf.Submitted(GetTickCount64());
            if(successful) {
                if(introScreen) state->history.Reset();
                else state->history.Completed(state->pendingPlugin,state->boundary.Sequence(),state->xr.ReferenceRevision());
                ++state->pairs;
            }
            if(introScreen && successful) {
                if(state->xr.ScreenSubmitted()<=8 || state->pendingScene%120==0)
                    state->log("XR screen submitted=%llu aspect=16/9 source=%s",state->xr.ScreenSubmitted(),video ? "late-intro-video" : "startup-screen");
            } else if(!successful || state->xr.Submitted()<8 || state->pendingScene%120==0) {
                const auto& h=state->pendingHistory;
                state->log("XR prototype main=%llu submitted=%u total=%llu cameras_restored=1 history_before=%u history_first=%u history_second=%u history_expected_advance=%u projections=%u/%u",
                    state->pendingScene,successful,state->xr.Submitted(),h[0],h[1],h[2],h[3],h[4],h[5]);
            }
            if(!successful) Stop(*state,"pending-frame-incomplete");
            return;
        }
        if(!InteractiveVrEnabled() || !state->boundary.Claim()) return;
        if(state->seat && state->seatActive) { state->seat->Inactive(); state->seatActive=false; } // A virtual-screen frame: no cockpit.
        ComPtr<ID3D11Texture2D> back; std::filesystem::path directory;
        if(!AcquireDiagnosticBackbuffer(&back,directory) || !Prepare(*state,back.Get())) return;
        D3D11_TEXTURE2D_DESC desc{}; back->GetDesc(&desc);
        const auto waitStarted=frametiming::Now();
        auto frame=state->xr.BeginScreenFrame(state->boundary.Sequence());
        frametiming::Wait(waitStarted,state->xr.DisplayPeriod());
        if(!frame) { if(state->xr.Exiting()) Stop(*state,"runtime-unavailable"); return; }
        auto eye=frame.AcquireEye(0);
        const bool ready=eye && state->presenter.Draw(back.Get(),eye.Target(),eye.Width(),eye.Height()) && eye.Complete();
        if(!ready || !frame.SubmitScreen(float(desc.Width)/float(desc.Height))) {
            frame.Abort(); Stop(*state,"screen-frame-incomplete"); return;
        }
        frametiming::Submitted(false);
        if(state->xr.ScreenSubmitted()<=3 || state->xr.ScreenSubmitted()%300==0)
            state->log("XR screen submitted=%llu aspect=%u/%u",state->xr.ScreenSubmitted(),desc.Width,desc.Height);
    } catch(...) { Stop(*state,"present-exception"); }
}
bool RenderVrScene(void* renderer,void* lists,void* a,void* b,void* context,void* scene,void* flags,
                   uint64_t sequence,VrInnerFn realInner,DiagnosticLogger logger) noexcept {
    if(!Enabled() || !realInner) return false;
    unsigned rendered{};
    PrototypeState* state=prototype.load(std::memory_order_acquire);
    try {
        if(!state) state=State();
        if(!state || GetCurrentThreadId()!=state->ownerThread || state->stopped || state->rendering || StopRequested(*state)) return false;
        RenderingScope rendering(*state);
        bool cockpit{};
        if(InteractiveVrEnabled()) {
            const auto camera=ClassifyMainCamera(renderer,a,b);
            if(!state->cameraReported || camera.kind!=state->previousCamera || camera.owner!=state->previousCameraOwner ||
               camera.stereoEligible!=state->previousStereoEligible) {
                logger("XR camera stereo_eligible=%u kind=%u name=%s gameplay=unknown pause=unknown replay=unknown",
                    unsigned(camera.stereoEligible),unsigned(camera.kind),camera.name.data());
                state->cameraReported=true; state->previousCamera=camera.kind;
                state->previousCameraOwner=camera.owner; state->previousStereoEligible=camera.stereoEligible;
                state->basisPending=true;
                state->history.Reset(); // Neither eye may reuse the previous camera.
            }
            if(!camera.stereoEligible) return false;
            cockpit=camera.kind==CameraKind::Cockpit;
            if(state->flat) return false; // Toggle VR: the engine's own pass goes to the virtual screen.
            if(state->flatViews & flatviews::Classify(camera.name.data())) return false; // The player's flat views, the same way.
        }
        if(!NativeStereoDisabled(renderer)) { Stop(*state,"native-driver-stereo-contract"); return false; }
        const auto base=reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
        CameraRecord originalA{},originalB{};
        void* plugin{}; uintptr_t table{}; unsigned selector{},historyBefore{},historyFirst{},historySecond{};
        unsigned char historyEnabled{};
        const auto* self=static_cast<const unsigned char*>(renderer);
        if(!Read(a,originalA.data(),originalA.size()) || !Read(b,originalB.data(),originalB.size()) ||
           !Range(a,originalA.size(),true) || !Range(b,originalB.size(),true) ||
           !ValidReplayCamera(originalA) || !ValidReplayCamera(originalB) ||
           !Read(self+0x50,&historyEnabled,sizeof(historyEnabled)) ||
           !Read(self+0x2c,&plugin,sizeof(plugin)) || !Read(plugin,&table,sizeof(table)) || table!=base+VelocityTableRva ||
           !Read(static_cast<unsigned char*>(plugin)+0xa0,&selector,sizeof(selector)) || selector!=0 ||
           !Read(static_cast<unsigned char*>(plugin)+0x94,&historyBefore,sizeof(historyBefore))) {
            Stop(*state,"camera-or-history-contract"); return false;
        }
        const unsigned historyExpectedAdvance=historyEnabled && originalB[0x8c] ? 1u : 0u;
        if(state->basisPending && state->basisSamples<8) {
            state->basisPending=false; ++state->basisSamples;
            logger("XR native camera up=%.5f,%.5f,%.5f right=%.5f,%.5f,%.5f forward=%.5f,%.5f,%.5f near=%.5f",
                CameraFloat(originalA,0x10),CameraFloat(originalA,0x14),CameraFloat(originalA,0x18),
                CameraFloat(originalA,0x20),CameraFloat(originalA,0x24),CameraFloat(originalA,0x28),
                CameraFloat(originalA,0x30),CameraFloat(originalA,0x34),CameraFloat(originalA,0x38),CameraFloat(originalA,0x54));
        }
        ComPtr<ID3D11Texture2D> back; std::filesystem::path directory;
        if(!AcquireDiagnosticBackbuffer(&back,directory)) return false;
        const bool first=!state->attempted;
        if(!Prepare(*state,back.Get()) || first) return false;
        RecenterKey(*state);
        if(!state->boundary.Claim()) return false;
        FrontendShadowScene frontendShadows(renderer,context);
        bool successful{},restored=true,ready=true;
        unsigned uploads[2]{};
        const auto started=GetTickCount64();
        const auto gap=state->previousSceneTick ? started-state->previousSceneTick : 0;
        state->previousSceneTick=started;
        uint64_t waitMs{},eyeMs[2]{};
        bool prime{};
        {
            if(state->pendingFrame) { Stop(*state,"unpresented-stereo-frame"); return false; }
            const auto waitStarted=frametiming::Now();
            state->pendingFrame.emplace(state->xr.BeginFrame(state->boundary.Sequence()));
            frametiming::Wait(waitStarted,state->xr.DisplayPeriod());
            auto& frame=*state->pendingFrame;
            waitMs=GetTickCount64()-started;
            if(!frame) {
                state->pendingFrame.reset();
                if(state->xr.Exiting()) Stop(*state,"runtime-unavailable");
                else if(!state->waitingReported) { RuntimeLog("XR prototype awaiting renderable frame; original engine pass retained"); state->waitingReported=true; }
                return false;
            }
            // The seat menu's live offset moves the eyes in the car; the camera files hold the launch seat.
            const auto seatOffset=UpdateSeat(*state,frame,cockpit);
            prime=state->history.NeedsPrime(plugin,state->boundary.Sequence(),state->xr.ReferenceRevision());
            const auto select=reinterpret_cast<HistorySelectFn>(base+HistorySelectRva);
            const auto invalidate=reinterpret_cast<HistoryInvalidateFn>(base+HistoryInvalidateRva);
            RestoreSelector restoreSelector{plugin,select};
            const bool captureUi=state->resolution.width!=0;
            struct EndCapture { HudCapture& hud; ~EndCapture() { hud.End(); } } endCapture{state->hud};
            if(captureUi) ready=state->hud.Begin(back.Get(),sequence);
            for(unsigned index=0;ready && index<2;++index) {
                auto eye=frame.AcquireEye(index);
                if(!eye) { ready=false; break; }
                ScopedEyeCameras cameras(a,b,originalA,originalB,index==1);
                ScopedRendererNear nearPlane(renderer);
                if(!nearPlane.Valid()) { ready=false; break; }
                auto eyePose=frame.RelativeEyePose(index);
                eyePose.position.x+=seatOffset.x; eyePose.position.y+=seatOffset.y; eyePose.position.z+=seatOffset.z;
                if(!cameras.Apply(eyePose)) { restored&=cameras.Restore(); ready=false; break; }
                ProjectionScope projection(renderer,context,a,b,frame.Views()[index].fov);
                if(!projection.Valid()) { restored&=cameras.Restore(); ready=false; break; }
                select(plugin,index);
                // +0x90 is a shared validity byte. Each selected slot needs
                // invalidation before its own setup after a camera/reference cut.
                if(prime) invalidate(plugin);
                ++rendered; // An exception after entry must not trigger a third engine call.
                {
                    const auto eyeStarted=GetTickCount64();
                    HudDrawScope capture(state->hud,index);
                    frametiming::Mark(index ? frametiming::RightBegin : frametiming::CenterBegin);
                    realInner(renderer,lists,a,b,context,scene,flags);
                    frametiming::Mark(index ? frametiming::RightEnd : frametiming::CenterEnd);
                    eyeMs[index]=GetTickCount64()-eyeStarted;
                }
                restored&=nearPlane.Restore();
                restored&=cameras.Restore();
                uploads[index]=projection.Uploads();
                unsigned history{};
                ready=projection.Valid() && uploads[index]>0 && restored &&
                    Read(static_cast<unsigned char*>(plugin)+0x94,&history,sizeof(history));
                (index ? historySecond : historyFirst)=history;
                if(!ready || (index==0 && historyFirst!=historyBefore+historyExpectedAdvance) ||
                   (index==1 && historyFirst!=historySecond) ||
                   !state->presenter.Draw(back.Get(),eye.Target(),eye.Width(),eye.Height()) || !eye.Complete()) {
                    ready=false; break;
                }
            }
            state->hud.End();
            ready&=!state->hud.Failed();
            auto* image=captureUi ? state->hud.Current(sequence) : nullptr;
            if(ready && rendered==2 && image) {
                // Restore the captured interface to the desktop mirror after
                // both world images have already been copied to their XR eyes.
                ComPtr<ID3D11RenderTargetView> mirror;
                D3D11_TEXTURE2D_DESC desc{}; back->GetDesc(&desc);
                ready=ready && SUCCEEDED(state->device->CreateRenderTargetView(back.Get(),nullptr,&mirror)) &&
                    state->hudPresenter.Draw(image,mirror.Get(),desc.Width,desc.Height,TexturePresenter::Mode::Composite);
            }
            if(ready && rendered==2) {
                if(captureUi) {
                    state->pendingScene=sequence; state->pendingPlugin=plugin;
                    state->pendingHistory={historyBefore,historyFirst,historySecond,historyExpectedAdvance,uploads[0],uploads[1]};
                    successful=true; // Prepared only; actual submission occurs at Present.
                } else successful=frame.Submit();
            } else frame.Abort();
            if(captureUi && logger && (!successful || state->pairs<8 || sequence%120==0))
                logger("XR HUD main=%llu captured=%u suppressed=%u panel=%u aspect=%.4f failed=%u canvas=%u",sequence,
                    state->hud.Draws(),state->hud.Suppressed(),image!=nullptr,state->hud.Aspect(),state->hud.Failed(),state->hud.CanvasDraws());
        }
        if(!state->resolution.width) {
            state->pendingFrame.reset();
            if(successful) { state->history.Completed(plugin,state->boundary.Sequence(),state->xr.ReferenceRevision()); ++state->pairs; }
        }
        const auto elapsed=GetTickCount64()-started;
        if(logger && state->timingReports<64 && ((prime && !state->previousIntroScreen) || elapsed>=100 || gap>=250)) {
            logger("XR transition main=%llu prime_both=%u reference=%llu gap_ms=%llu xr_wait_ms=%llu eye_cpu_ms=%llu/%llu total_cpu_ms=%llu",
                sequence,prime,state->xr.ReferenceRevision(),gap,waitMs,eyeMs[0],eyeMs[1],elapsed);
            if(++state->timingReports==64) logger("XR transition diagnostic limit reached");
        }
        if(logger && !state->resolution.width && (!successful || state->pairs<=8 || sequence%120==0))
            logger("XR prototype main=%llu submitted=%u total=%llu cameras_restored=%u history_before=%u history_first=%u history_second=%u history_expected_advance=%u projections=%u/%u",
                sequence,successful,state->xr.Submitted(),restored,historyBefore,historyFirst,historySecond,historyExpectedAdvance,uploads[0],uploads[1]);
        if(!successful) Stop(*state,"eye-pair-incomplete");
    } catch(const std::exception& error) {
        try { if(logger) logger("XR prototype failed main=%llu calls=%u reason=%s",sequence,rendered,error.what()); } catch(...) { }
        if(state) Stop(*state,"exception");
    } catch(...) {
        if(state) Stop(*state,"exception");
    }
    return rendered!=0;
}
}
