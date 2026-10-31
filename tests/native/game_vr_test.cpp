#include "../../src/game_vr.cpp"
namespace dirt3vr { bool AcquireDiagnosticBackbuffer(ID3D11Texture2D**,std::filesystem::path&) noexcept { return false; } }
#include <cstdio>
#include <limits>
#include <thread>
namespace {
void Require(bool value,const char* text) { if(!value) throw std::runtime_error(text); }
void Put(dirt3vr::CameraRecord& c,unsigned at,float v) { std::memcpy(c.data()+at,&v,4); }
dirt3vr::CameraRecord Camera() {
    dirt3vr::CameraRecord c; c.fill(0xa5);
    for(unsigned at:{0x10u,0x20u,0x30u,0x40u}) for(unsigned axis=0;axis<3;++axis) Put(c,at+4*axis,0);
    Put(c,0x14,1); Put(c,0x20,-1); Put(c,0x38,-1); Put(c,0x40,10); Put(c,0x44,20); Put(c,0x48,30); c[0x8c]=1; return c;
}
void Near(float a,float b) { Require(std::abs(a-b)<.0001f,"Camera basis or position differs"); }
unsigned exitCode{}, closeCalls{};
void WINAPI FakeExit(UINT code) { exitCode=code; }
LRESULT CALLBACK FixtureWindow(HWND window,UINT message,WPARAM wparam,LPARAM lparam) {
    if(message==WM_CLOSE) { ++closeCalls; return 123; }
    return DefWindowProcW(window,message,wparam,lparam);
}
void CheckShutdown() {
    dirt3vr::PrototypeState state;
    dirt3vr::prototype.store(&state);
    state.rendering=true;
    dirt3vr::StopForExit("fixture-busy");
    Require(!state.stopped,"Exit destroyed an active frame");
    const auto boundary=state.boundary.Sequence();
    dirt3vr::VrBeforePresent(nullptr);
    Require(state.rendering && state.boundary.Sequence()==boundary,"Nested Present changed outer render ownership");
    { dirt3vr::RenderingScope nested(state); }
    Require(state.rendering,"Nested scope cleared outer render ownership");
    state.rendering=false;
    std::thread foreign([] { dirt3vr::StopForExit("fixture-foreign"); }); foreign.join();
    Require(!state.stopped,"Foreign thread destroyed render-owned runtime");
    WNDCLASSW definition{}; definition.lpfnWndProc=FixtureWindow;
    definition.hInstance=GetModuleHandleW(nullptr); definition.lpszClassName=L"DiRT3VR.ShutdownFixture";
    Require(RegisterClassW(&definition)!=0,"Fixture class failed");
    const auto window=CreateWindowW(definition.lpszClassName,L"",0,0,0,64,64,nullptr,nullptr,definition.hInstance,nullptr);
    Require(window!=nullptr,"Fixture window failed");
    state.window=window; state.previousWindowProc=FixtureWindow;
    SetWindowLongPtrW(window,GWLP_WNDPROC,reinterpret_cast<LONG_PTR>(&dirt3vr::GameWindowProc));
    Require(SendMessageW(window,WM_CLOSE,0,0)==123 && closeCalls==1 && state.stopped,"Close did not clean up and forward exactly once");
    DestroyWindow(window);
    Require(!state.window,"Destroyed HWND was retained");
    UnregisterClassW(definition.lpszClassName,definition.hInstance);
    dirt3vr::realExitProcess=FakeExit;
    dirt3vr::ExitGame(42);
    Require(exitCode==42,"Exit code was not preserved");
    dirt3vr::prototype.store(nullptr);
    dirt3vr::realExitProcess=nullptr;
}
}
int main() {
    try {
        dirt3vr::HeadsetResolutionRequest request;
        Require(request.Configure(2400,2600,50) && request.sceneWidth==1200 && request.sceneHeight==1300,"Half scale is not relative to headset dimensions");
        Require(request.Configure(2400,2600,100) && request.sceneWidth==2400 && request.sceneHeight==2600,"100 percent lost recommended scene size");
        Require(request.Configure(2400,2600,150) && request.sceneWidth==3600 && request.sceneHeight==3900,"Supersampling dimensions differ");
        Require(!request.Configure(2400,2600,200) && !request.width,"Unsafe scene dimensions accepted or stale plan retained");
        Require(!request.Configure(UINT32_MAX,2600,100),"Headset dimensions overflowed");
        Require(!request.Configure(2400,2600,49) && !request.Configure(2400,2600,301),"Invalid render scale accepted");
        SetEnvironmentVariableW(L"DIRT3VR_HEADSET_WIDTH",L"2400");
        SetEnvironmentVariableW(L"DIRT3VR_HEADSET_HEIGHT",L"2600");
        for(unsigned value : {50u,75u,100u,150u}) {
            const auto text=std::to_wstring(value);
            SetEnvironmentVariableW(L"DIRT3VR_VR_RENDER_SCALE",text.c_str());
            Require(dirt3vr::ReadResolution(request) && request.scale==value,"Valid headset request was not read");
        }
        for(const auto* text : {L"49",L"301",L"-25",L"75.0",L"75,0",L"75junk",L" 75",L"nan",L"999999999999999999999999999999999",L""}) {
            SetEnvironmentVariableW(L"DIRT3VR_VR_RENDER_SCALE",text);
            Require(!dirt3vr::ReadResolution(request),"Invalid resolution request accepted");
        }
        SetEnvironmentVariableW(L"DIRT3VR_VR_RENDER_SCALE",L"100");
        SetEnvironmentVariableW(L"DIRT3VR_HEADSET_WIDTH",nullptr);
        Require(!dirt3vr::ReadResolution(request),"Partial resolution request accepted");
        SetEnvironmentVariableW(L"DIRT3VR_HEADSET_HEIGHT",nullptr);
        SetEnvironmentVariableW(L"DIRT3VR_VR_RENDER_SCALE",nullptr);
        auto original=Camera();
        auto identity=dirt3vr::EyeCamera(original,{{0,0,0,1},{0,0,0}}); for(unsigned at:{0x10u,0x20u,0x30u,0x40u}) for(unsigned axis=0;axis<3;++axis) Near(dirt3vr::CameraFloat(identity,at+axis*4),dirt3vr::CameraFloat(original,at+axis*4));
        auto moved=dirt3vr::EyeCamera(original,{{0,0,0,1},{.1f,.2f,.3f}});
        Near(dirt3vr::CameraFloat(moved,0x40),10.1f); Near(dirt3vr::CameraFloat(moved,0x44),20.2f); Near(dirt3vr::CameraFloat(moved,0x48),30.3f);
        const auto rotated=dirt3vr::EyeCamera(original,{{0,std::sqrt(.5f),0,std::sqrt(.5f)},{0,0,0}});
        Near(dirt3vr::CameraFloat(rotated,0x20),0); Near(dirt3vr::CameraFloat(rotated,0x28),1); Near(dirt3vr::CameraFloat(rotated,0x30),-1);
        for(unsigned i=0;i<original.size();++i) {
            bool pose=false; for(unsigned at:{0x10u,0x20u,0x30u,0x40u}) pose|=i>=at && i<at+12;
            if(!pose) Require(rotated[i]==original[i],"Pose touched W, padding or lens field");
        }
        auto a=original,b=original;
        {
            dirt3vr::ScopedEyeCameras scope(a.data(),b.data(),original,original,true);
            Require(scope.Apply({{0,0,0,1},{.032f,0,0}}),"Pose application failed"); Require(b[0x8c]==0,"Second eye advanced history");
            a[0x80]=0x2a; Require(scope.Restore(),"Explicit camera restoration failed");
        }
        auto changed=original; changed[0x80]=0x2a; Require(a==changed && b==original,"Restoration overwrote engine-owned fields");
        a=original;
        {
            dirt3vr::ScopedEyeCameras scope(a.data(),b.data(),original,original,false);
            Require(scope.Apply({{0,0,0,1},{0,0,.1f}}),"Pose application failed");
        }
        Require(a==original && b==original,"Destructor did not restore pose");
        {
            dirt3vr::ScopedEyeCameras scope(a.data(),nullptr,original,original,true);
            Require(!scope.Apply({{0,0,0,1},{.032f,0,0}}),"Invalid second camera accepted");
        }
        Require(a==original,"Partial camera write was not rolled back");
        bool rejected=false;
        try { dirt3vr::EyeCamera(original,{{0,0,0,1},{std::numeric_limits<float>::quiet_NaN(),0,0}}); } catch(const std::invalid_argument&) { rejected=true; }
        Require(rejected,"NaN eye position accepted");
        std::array<unsigned char,0x80> renderer{};
        float nativeNear=.075f,foregroundNear=.01f;
        std::memcpy(renderer.data()+0x40,&nativeNear,sizeof(float));
        try {
            dirt3vr::ScopedRendererNear scope(renderer.data());
            Require(scope.Valid(),"Valid native near plane refused");
            std::memcpy(renderer.data()+0x40,&foregroundNear,sizeof(float));
            renderer[0x60]=42;
            throw 1;
        } catch(int) { }
        float restoredNear{}; std::memcpy(&restoredNear,renderer.data()+0x40,sizeof(float));
        Require(restoredNear==nativeNear && renderer[0x60]==42,"Foreground near plane or unrelated state not restored correctly");
        CheckShutdown();
        puts("Game VR camera transaction: identity, translation, yaw, selective restore, partial-write rollback and invalid pose passed"); return 0;
    } catch(const std::exception& e) { puts(e.what()); return 1; }
}
