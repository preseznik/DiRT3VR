#include "xr_runtime.h"
#include <dxgi.h>
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <deque>
#include <limits>
#include <stdexcept>
#include <string>
#include <type_traits>
#include <optional>

// Link this executable against D3D11, but NOT the OpenXR loader. These xr*
// functions implement a strict synthetic runtime; no headset/runtime is opened.
namespace {
using Microsoft::WRL::ComPtr;
using dirt3vr::XrRuntime;
ComPtr<ID3D11Device> device;
LUID adapterLuid{};
std::vector<std::string> calls;
std::vector<std::string> diagnosticMessages;
std::deque<XrSessionState> events;
std::array<ComPtr<ID3D11Texture2D>,3> textures;
std::array<bool,3> acquired{},waited{};
bool shouldRender=true,validTracking=true,fullTracking=true,badLuid=false,badPose=false,timeoutForever=false;
unsigned chainCount{},imageCount=2,timeoutCount{},layers{},endFrames{},sessions{},destroyedSessions{},destroyedInstances{},destroyedChains{};
int failAcquire=-1,failChain=-1;
uint32_t imageIndex{};
uint32_t recommendedWidth=64,recommendedHeight=48;
float headHeight=1.4f;
float headX{},headZ{};
XrQuaternionf headOrientation{0,0,0,1};
XrStructureType lastLayerType=XR_TYPE_UNKNOWN;
XrCompositionLayerQuad lastQuad{XR_TYPE_COMPOSITION_LAYER_QUAD};
void Require(bool value,const char* message) { if(!value) throw std::runtime_error(message); }
bool Near(float a,float b) { return std::abs(a-b)<.0001f; }
void CaptureDiagnostic(const char* text) { diagnosticMessages.emplace_back(text); }
unsigned Eye(XrSwapchain handle) { return unsigned(uint64_t(handle)-10); }
void Reset() {
    calls.clear(); diagnosticMessages.clear(); events={XR_SESSION_STATE_READY,XR_SESSION_STATE_FOCUSED};
    textures={}; acquired={}; waited={};
    shouldRender=validTracking=fullTracking=true; badLuid=badPose=timeoutForever=false;
    chainCount=timeoutCount=layers=endFrames=sessions=destroyedSessions=destroyedInstances=destroyedChains=imageIndex=0;
    imageCount=2; failAcquire=failChain=-1; headHeight=1.4f; headX=headZ=0;
    recommendedWidth=64; recommendedHeight=48;
    headOrientation={0,0,0,1}; lastLayerType=XR_TYPE_UNKNOWN; lastQuad={XR_TYPE_COMPOSITION_LAYER_QUAD};
}
void CompletePair(XrRuntime::Frame& frame) {
    for(unsigned index=0;index<2;++index) {
        auto eye=frame.AcquireEye(index);
        Require(bool(eye) && eye.Texture() && eye.Target() && eye.Width()==64 && eye.Height()==48,"Missing or mismatched owned eye resources");
        Require(eye.Complete(),"Eye completion failed");
        Require(!eye && !eye.Target() && !eye.Texture(),"Released eye exposes stale resources");
    }
}
void CompleteScreen(XrRuntime::Frame& frame) {
    Require(frame.Mode()==XrRuntime::FrameMode::Screen,"Screen frame lost its explicit mode");
    auto image=frame.AcquireEye(0);
    Require(bool(image) && image.Target() && image.Width()==64 && image.Height()==48,"Screen image unavailable");
    Require(image.Complete(),"Screen image completion failed");
}
}
extern "C" {
XRAPI_ATTR XrResult XRAPI_CALL xrCreateInstance(const XrInstanceCreateInfo*,XrInstance* instance) { *instance=XrInstance(1); return XR_SUCCESS; }
XRAPI_ATTR XrResult XRAPI_CALL xrDestroyInstance(XrInstance) { ++destroyedInstances; return XR_SUCCESS; }
XRAPI_ATTR XrResult XRAPI_CALL xrGetSystem(XrInstance,const XrSystemGetInfo*,XrSystemId* system) { *system=1; return XR_SUCCESS; }
XRAPI_ATTR XrResult XRAPI_CALL GetRequirements(XrInstance,XrSystemId,XrGraphicsRequirementsD3D11KHR* requirements) {
    requirements->adapterLuid=adapterLuid; if(badLuid) ++requirements->adapterLuid.HighPart;
    requirements->minFeatureLevel=D3D_FEATURE_LEVEL_11_0; return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL xrGetInstanceProcAddr(XrInstance,const char* name,PFN_xrVoidFunction* function) {
    Require(std::string(name)=="xrGetD3D11GraphicsRequirementsKHR","Unexpected XR extension lookup");
    *function=reinterpret_cast<PFN_xrVoidFunction>(&GetRequirements); return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL xrCreateSession(XrInstance,const XrSessionCreateInfo* info,XrSession* session) {
    const auto binding=static_cast<const XrGraphicsBindingD3D11KHR*>(info->next);
    Require(binding && binding->device==device.Get(),"XR bound a different D3D11 device");
    ++sessions; *session=XrSession(2); return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL xrDestroySession(XrSession) { ++destroyedSessions; return XR_SUCCESS; }
XRAPI_ATTR XrResult XRAPI_CALL xrEnumerateEnvironmentBlendModes(XrInstance,XrSystemId,XrViewConfigurationType,uint32_t capacity,uint32_t* count,XrEnvironmentBlendMode* modes) {
    *count=1; if(capacity) modes[0]=XR_ENVIRONMENT_BLEND_MODE_OPAQUE; return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL xrEnumerateViewConfigurationViews(XrInstance,XrSystemId,XrViewConfigurationType,uint32_t capacity,uint32_t* count,XrViewConfigurationView* views) {
    *count=2;
    for(unsigned i=0;i<capacity;++i) { views[i].recommendedImageRectWidth=recommendedWidth; views[i].recommendedImageRectHeight=recommendedHeight; views[i].maxImageRectWidth=views[i].maxImageRectHeight=4096; }
    return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL xrEnumerateSwapchainFormats(XrSession,uint32_t capacity,uint32_t* count,int64_t* formats) {
    *count=1; if(capacity) formats[0]=DXGI_FORMAT_R8G8B8A8_UNORM; return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL xrCreateReferenceSpace(XrSession,const XrReferenceSpaceCreateInfo* info,XrSpace* space) {
    Require(info->referenceSpaceType==XR_REFERENCE_SPACE_TYPE_LOCAL,"Wrong reference space"); *space=XrSpace(3); return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL xrDestroySpace(XrSpace) { return XR_SUCCESS; }
XRAPI_ATTR XrResult XRAPI_CALL xrCreateSwapchain(XrSession,const XrSwapchainCreateInfo* info,XrSwapchain* chain) {
    if(int(chainCount)==failChain) return XR_ERROR_RUNTIME_FAILURE;
    Require(chainCount<3 && info->arraySize==1 && info->sampleCount==1,"Unexpected eye resource count or layout");
    D3D11_TEXTURE2D_DESC desc{}; desc.Width=info->width; desc.Height=info->height;
    desc.Format=DXGI_FORMAT(info->format); desc.MipLevels=desc.ArraySize=desc.SampleDesc.Count=1;
    desc.BindFlags=D3D11_BIND_RENDER_TARGET;
    Require(SUCCEEDED(device->CreateTexture2D(&desc,nullptr,&textures[chainCount])),"WARP eye texture allocation failed");
    *chain=XrSwapchain(10+chainCount++); return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL xrDestroySwapchain(XrSwapchain chain) { ++destroyedChains; textures[Eye(chain)].Reset(); acquired[Eye(chain)]=waited[Eye(chain)]=false; return XR_SUCCESS; }
XRAPI_ATTR XrResult XRAPI_CALL xrEnumerateSwapchainImages(XrSwapchain chain,uint32_t capacity,uint32_t* count,XrSwapchainImageBaseHeader* images) {
    *count=imageCount;
    for(unsigned i=0;i<capacity;++i) reinterpret_cast<XrSwapchainImageD3D11KHR*>(images)[i].texture=textures[Eye(chain)].Get();
    return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL xrPollEvent(XrInstance,XrEventDataBuffer* event) {
    if(events.empty()) return XR_EVENT_UNAVAILABLE;
    auto* changed=reinterpret_cast<XrEventDataSessionStateChanged*>(event);
    *changed={XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED}; changed->session=XrSession(2); changed->state=events.front(); events.pop_front(); return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL xrBeginSession(XrSession,const XrSessionBeginInfo*) { calls.emplace_back("beginSession"); return XR_SUCCESS; }
XRAPI_ATTR XrResult XRAPI_CALL xrEndSession(XrSession) { calls.emplace_back("endSession"); return XR_SUCCESS; }
XRAPI_ATTR XrResult XRAPI_CALL xrWaitFrame(XrSession,const XrFrameWaitInfo*,XrFrameState* state) {
    calls.emplace_back("waitFrame"); state->shouldRender=shouldRender; state->predictedDisplayTime=12345; return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL xrBeginFrame(XrSession,const XrFrameBeginInfo*) { calls.emplace_back("beginFrame"); return XR_SUCCESS; }
XRAPI_ATTR XrResult XRAPI_CALL xrLocateViews(XrSession,const XrViewLocateInfo* info,XrViewState* tracking,uint32_t capacity,uint32_t* count,XrView* views) {
    Require(info->displayTime==12345 && capacity==2,"Views not located at predicted display time");
    calls.emplace_back("locateViews"); *count=2;
    tracking->viewStateFlags=validTracking ? XR_VIEW_STATE_POSITION_VALID_BIT|XR_VIEW_STATE_ORIENTATION_VALID_BIT : 0;
    if(fullTracking) tracking->viewStateFlags|=XR_VIEW_STATE_POSITION_TRACKED_BIT|XR_VIEW_STATE_ORIENTATION_TRACKED_BIT;
    for(unsigned i=0;i<2;++i) {
        views[i].pose={headOrientation,{headX+(i ? .032f : -.032f),headHeight,headZ}};
        if(badPose) views[i].pose.position.x=std::numeric_limits<float>::quiet_NaN();
        views[i].fov={-.9f,.7f,.8f,-.6f};
    }
    return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL xrAcquireSwapchainImage(XrSwapchain chain,const XrSwapchainImageAcquireInfo*,uint32_t* index) {
    const auto eye=Eye(chain); calls.emplace_back("acquire"+std::to_string(eye));
    if(int(eye)==failAcquire) return XR_ERROR_RUNTIME_FAILURE;
    Require(!acquired[eye],"Image acquired twice"); acquired[eye]=true; *index=imageIndex; return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL xrWaitSwapchainImage(XrSwapchain chain,const XrSwapchainImageWaitInfo* info) {
    const auto eye=Eye(chain); calls.emplace_back("wait"+std::to_string(eye));
    Require(acquired[eye] && info->timeout>0 && info->timeout!=XR_INFINITE_DURATION,"Unacquired or unbounded image wait");
    if(timeoutForever) return XR_TIMEOUT_EXPIRED;
    if(timeoutCount) { --timeoutCount; return XR_TIMEOUT_EXPIRED; }
    waited[eye]=true; return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL xrReleaseSwapchainImage(XrSwapchain chain,const XrSwapchainImageReleaseInfo*) {
    const auto eye=Eye(chain); calls.emplace_back("release"+std::to_string(eye));
    Require(acquired[eye] && waited[eye],"Unwaited image released or double release"); acquired[eye]=waited[eye]=false; return XR_SUCCESS;
}
XRAPI_ATTR XrResult XRAPI_CALL xrEndFrame(XrSession,const XrFrameEndInfo* info) {
    calls.emplace_back("endFrame"); ++endFrames; layers=info->layerCount;
    lastLayerType=layers ? info->layers[0]->type : XR_TYPE_UNKNOWN;
    Require(info->displayTime==12345,"Frame ended with wrong display time");
    if(layers) {
        Require(layers<=2 && !acquired[0] && !acquired[1] && !acquired[2],"Submitted layer retains an acquired image");
        if(layers==2) {
            Require(lastLayerType==XR_TYPE_COMPOSITION_LAYER_PROJECTION && info->layers[1]->type==XR_TYPE_COMPOSITION_LAYER_QUAD,"HUD must follow the stereo world");
            lastQuad=*reinterpret_cast<const XrCompositionLayerQuad*>(info->layers[1]);
            Require(lastQuad.space==XrSpace(3) && lastQuad.eyeVisibility==XR_EYE_VISIBILITY_BOTH &&
                lastQuad.subImage.swapchain==XrSwapchain(12) && lastQuad.subImage.imageRect.extent.width==64 &&
                lastQuad.subImage.imageRect.extent.height==48 && lastQuad.layerFlags==
                (XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT|XR_COMPOSITION_LAYER_UNPREMULTIPLIED_ALPHA_BIT),"HUD transparency, shared image or rectangle invalid");
        }
        if(lastLayerType==XR_TYPE_COMPOSITION_LAYER_QUAD) {
            lastQuad=*reinterpret_cast<const XrCompositionLayerQuad*>(info->layers[0]);
            Require(lastQuad.space==XrSpace(3) && lastQuad.eyeVisibility==XR_EYE_VISIBILITY_BOTH &&
                    (lastQuad.subImage.swapchain==XrSwapchain(10) || lastQuad.subImage.swapchain==XrSwapchain(12)) && lastQuad.layerFlags==0 && lastQuad.subImage.imageArrayIndex==0 &&
                    lastQuad.subImage.imageRect.offset.x==0 && lastQuad.subImage.imageRect.offset.y==0 &&
                    lastQuad.subImage.imageRect.extent.width==64 && lastQuad.subImage.imageRect.extent.height==48,
                    "Screen layer uses wrong space, eye visibility or image rectangle");
        } else {
            Require(lastLayerType==XR_TYPE_COMPOSITION_LAYER_PROJECTION,"Unexpected composition layer type");
            const auto* projection=reinterpret_cast<const XrCompositionLayerProjection*>(info->layers[0]);
            Require(projection->viewCount==2 && projection->space==XrSpace(3),"Projection pair/space incorrect");
            Require(projection->views[0].pose.position.x<0 && projection->views[1].pose.position.x>0,"Submitted eyes lost independent IPD");
            Require(projection->views[0].fov.angleLeft==-.9f && projection->views[1].fov.angleRight==.7f,"Submitted FOV differs from render FOV");
        }
    }
    return XR_SUCCESS;
}
}
int main() {
    static_assert(!std::is_copy_constructible_v<XrRuntime::Frame> && std::is_nothrow_move_constructible_v<XrRuntime::Frame> && !std::is_move_assignable_v<XrRuntime::Frame>);
    static_assert(!std::is_copy_constructible_v<XrRuntime::Eye> && !std::is_move_constructible_v<XrRuntime::Eye>);
    try {
        Require(SUCCEEDED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,nullptr)),"WARP unavailable");
        ComPtr<IDXGIDevice> dxgi; ComPtr<IDXGIAdapter> adapter; DXGI_ADAPTER_DESC desc{};
        Require(SUCCEEDED(device.As(&dxgi)) && SUCCEEDED(dxgi->GetAdapter(&adapter)) && SUCCEEDED(adapter->GetDesc(&desc)),"WARP LUID unavailable"); adapterLuid=desc.AdapterLuid;
        Reset(); {
            XrRuntime runtime; Require(runtime.Initialize(device.Get(),1.f,0,0,true) && chainCount==3,"HUD resources unavailable");
            { auto frame=runtime.BeginFrame(1); CompletePair(frame);
              { auto panel=frame.AcquireOverlay(); Require(panel && panel.Complete(),"HUD acquire/release failed"); }
              Require(frame.SubmitOverlay(4.f/3.f) && layers==2,"Stereo plus HUD did not submit");
              Require(Near(lastQuad.size.width,1.6f) && Near(lastQuad.size.height,1.2f) && Near(lastQuad.pose.position.z,-2.f),"HUD physical size/distance incorrect"); }
            { auto frame=runtime.BeginFrame(2); CompletePair(frame); Require(frame.Submit() && layers==1,"No-HUD frame leaked a stale panel"); }
            { auto frame=runtime.BeginFrame(3); auto panel=frame.AcquireOverlay(); Require(!panel && !frame.SubmitOverlay(1.f) && layers==0,"HUD allowed before complete stereo pair"); }
            { auto frame=runtime.BeginFrame(4); CompletePair(frame); { auto panel=frame.AcquireOverlay(); Require(bool(panel),"HUD acquire failed"); }
              Require(!frame.SubmitOverlay(1.f) && layers==0,"Incomplete HUD submitted"); }
            { auto frame=runtime.BeginFrame(5); CompletePair(frame); { auto panel=frame.AcquireOverlay(); Require(panel.Complete(),"HUD release failed"); }
              Require(!frame.Submit() && layers==0,"Plain submission silently dropped captured HUD"); }
            { auto frame=runtime.BeginFrame(6); CompletePair(frame); { auto panel=frame.AcquireOverlay(); Require(panel.Complete(),"HUD release failed"); }
              Require(!frame.SubmitOverlay(1.f,.1f) && layers==0,"Uncomfortable HUD distance accepted"); }
            { auto frame=runtime.BeginScreenFrame(7); auto panel=frame.AcquireOverlay(); Require(!panel && !frame.SubmitScreen(1.f),"Screen-only mode acquired stereo HUD"); }
        }
        Reset(); failChain=2; {
            XrRuntime runtime; Require(!runtime.Initialize(device.Get(),1.f,0,0,true) && destroyedChains==2 && destroyedSessions==1 && destroyedInstances==1,"HUD allocation failure leaked XR resources");
        }
        Reset(); {
            XrRuntime runtime; Require(runtime.Initialize(device.Get(),1.f,0,0,true),"HUD init failed");
            auto frame=runtime.BeginFrame(1); CompletePair(frame); failAcquire=2;
            auto panel=frame.AcquireOverlay(); Require(!panel && !frame.SubmitOverlay(1.f) && layers==0,"Failed HUD acquisition submitted partial frame");
        }
        for(unsigned percent : {25u,50u,75u,100u}) {
            Reset(); XrRuntime runtime;
            Require(runtime.Initialize(device.Get(),percent/100.f),"Supported output scale rejected");
            auto frame=runtime.BeginFrame(1);
            for(unsigned index=0;index<2;++index) {
                auto eye=frame.AcquireEye(index);
                Require(eye && eye.Width()==64*percent/100 && eye.Height()==48*percent/100,"Scale did not reach the actual eye allocation");
                D3D11_TEXTURE2D_DESC actual{}; eye.Texture()->GetDesc(&actual);
                Require(actual.Width==eye.Width() && actual.Height==eye.Height() && eye.Complete(),"Eye dimensions differ from allocated texture");
            }
            Require(frame.Submit() && runtime.Submitted()==1,"Scaled stereo pair was not submitted");
        }
        Reset(); recommendedWidth=recommendedHeight=4096; {
            XrRuntime runtime; Require(runtime.Initialize(device.Get(),1.f),"Large recommendation failed bounded allocation");
            const auto dims=runtime.EyeDimensions();
            Require(dims[0]<=4096 && dims[1]<=4096 && uint64_t(dims[0])*dims[1]<=8ull*1024*1024 && dims[0]<4096,
                    "Output scale bypassed existing eye pixel budget");
        }
        Reset(); recommendedWidth=3072; recommendedHeight=3264; {
            XrRuntime runtime;
            Require(runtime.Initialize(device.Get(),1.f,3072,3264),"Recommended-size output refused");
            auto frame=runtime.BeginFrame(1); auto eye=frame.AcquireEye(0);
            Require(eye && eye.Width()==3072 && eye.Height()==3264,"Recommended-size output was silently downscaled");
        }
        Reset(); recommendedWidth=960; recommendedHeight=1000; {
            XrRuntime runtime;
            Require(!runtime.Initialize(device.Get(),1.f,960,999) && chainCount==0,"Stale headset query allocated eyes");
        }
        Reset(); {
            XrRuntime runtime;
            Require(!runtime.Initialize(device.Get(),.5f,960,1000) && sessions==0,"Output slider survived in headset-relative mode");
            Require(!runtime.Initialize(device.Get(),1.f,4097,1000) && sessions==0,"Unsafe requested output created XR resources");
            Require(!runtime.Initialize(device.Get(),1.f,960,0) && sessions==0,"Partial output request created XR resources");
        }
        Reset(); recommendedWidth=recommendedHeight=4096; imageCount=8; {
            XrRuntime runtime;
            Require(!runtime.Initialize(device.Get(),1.f,4096,4096),"Recommended output bypassed color resource budget");
        }
        for(float scale : {0.f,.24f,1.01f,std::numeric_limits<float>::quiet_NaN()}) {
            Reset(); XrRuntime runtime;
            Require(!runtime.Initialize(device.Get(),scale) && sessions==0,"Invalid scale created XR resources");
        }
        Reset(); {
            XrRuntime runtime; Require(runtime.Initialize(device.Get()),"XR initialization failed");
            auto frame=runtime.BeginFrame(1); Require(bool(frame),"Tracked focused frame unavailable");
            auto duplicate=runtime.BeginFrame(2); Require(!duplicate && bool(frame),"Second owner stole active frame");
            CompletePair(frame); Require(frame.Submit() && layers==1 && runtime.Submitted()==1,"Completed pair not submitted");
            auto stale=runtime.BeginFrame(1); Require(!stale,"Same game frame submitted twice");
            auto next=runtime.BeginFrame(2); Require(bool(next),"New game frame rejected"); next.Abort(); Require(layers==0,"Abort submitted stale eyes");
        }
        Require(destroyedChains==2 && destroyedSessions==1 && destroyedInstances==1,"Normal resource teardown incomplete");
        Reset(); {
            XrRuntime runtime(CaptureDiagnostic); Require(runtime.Initialize(device.Get()),"Init failed");
            headOrientation={0,0,1,0}; // A real 180-degree roll, unlike merely q -> -q.
            auto frame=runtime.BeginFrame(1); Require(bool(frame),"Diagnostic pose changed frame eligibility"); frame.Abort();
            auto screen=runtime.BeginScreenFrame(2); Require(bool(screen),"First screen frame rejected"); screen.Abort();
            for(uint64_t sequence=3;sequence<23;++sequence) {
                runtime.RequestRecenter(); auto next=runtime.BeginFrame(sequence); Require(bool(next),"Repeated tracked recenter failed"); next.Abort();
            }
            unsigned samples{}; bool inverted{},firstScreen{};
            for(const auto& message:diagnosticMessages) if(message.starts_with("XR orientation ")) {
                ++samples;
                inverted|=message.find("up_y=-1.000000/-1.000000")!=std::string::npos;
                firstScreen|=message.find("reason=first-renderable mode=screen")!=std::string::npos;
                Require(message.size()<511 && message.find("eye0_fov=(")!=std::string::npos &&
                        message.find("reference_q=(")!=std::string::npos,"Orientation diagnostic truncated or incomplete");
            }
            Require(samples==8 && inverted && firstScreen,"Orientation diagnostic lost inversion, first mode or output bound");
        }
        Reset(); {
            XrRuntime runtime; Require(runtime.Initialize(device.Get()),"Init failed");
            auto screen=runtime.BeginScreenFrame(1); Require(bool(screen),"Tracked screen frame unavailable");
            auto collision=runtime.BeginFrame(2); Require(!collision && bool(screen),"Stereo frame stole active screen frame");
            bool rejected=false; try { screen.Views(); } catch(const std::logic_error&) { rejected=true; }
            Require(rejected,"Screen exposed stereo rendering views");
            rejected=false; try { screen.RelativeEyePose(0); } catch(const std::logic_error&) { rejected=true; }
            Require(rejected,"Screen exposed stereo eye transform");
            CompleteScreen(screen); Require(screen.SubmitScreen(16.f/9.f),"Complete screen not submitted");
            Require(lastLayerType==XR_TYPE_COMPOSITION_LAYER_QUAD && runtime.ScreenSubmitted()==1 && runtime.Submitted()==0,
                    "Screen submission changed stereo statistics or layer mode");
            Require(Near(lastQuad.size.width,3) && Near(lastQuad.size.height,27.f/16.f) &&
                    Near(lastQuad.pose.position.x,0) && Near(lastQuad.pose.position.y,1.4f) && Near(lastQuad.pose.position.z,-3) &&
                    Near(lastQuad.pose.orientation.w,1),"Screen aspect, distance or recenter anchor differs");
            Require(std::count(calls.begin(),calls.end(),"acquire0")==1 && std::count(calls.begin(),calls.end(),"acquire1")==0,
                    "Screen acquired more than its single image");
            auto duplicate=runtime.BeginFrame(1); Require(!duplicate,"Screen sequence reused for stereo");
            auto stereo=runtime.BeginFrame(2); Require(stereo.Mode()==XrRuntime::FrameMode::Stereo,"Default frame changed mode");
            CompletePair(stereo); Require(stereo.Submit() && runtime.Submitted()==1 && runtime.ScreenSubmitted()==1,"Screen-to-stereo transition mixed counters");
            auto screenAgain=runtime.BeginScreenFrame(3); CompleteScreen(screenAgain);
            Require(screenAgain.SubmitScreen(16.f/9.f) && runtime.ScreenSubmitted()==2 && runtime.Submitted()==1,
                    "Stereo-to-screen transition retained stale right-eye completion");
        }
        Reset(); {
            XrRuntime runtime; Require(runtime.Initialize(device.Get()),"Init failed");
            auto stereo=runtime.BeginFrame(1); { auto first=stereo.AcquireEye(0); Require(first.Complete(),"First eye failed"); }
            Require(!stereo.SubmitScreen(16.f/9.f) && layers==0 && !stereo.Submit(),"Partial stereo downgraded into a screen");
            auto sameSequence=runtime.BeginScreenFrame(1); Require(!sameSequence,"Rejected stereo reused its sequence for screen fallback");
            auto completeStereo=runtime.BeginFrame(2); CompletePair(completeStereo);
            Require(!completeStereo.SubmitScreen(16.f/9.f) && layers==0,"Complete stereo changed mode during submission");
            auto screen=runtime.BeginScreenFrame(3); CompleteScreen(screen);
            Require(!screen.Submit() && layers==0 && !screen.SubmitScreen(16.f/9.f),"Screen upgraded into a stereo frame");
            Require(runtime.Submitted()==0 && runtime.ScreenSubmitted()==0,"Rejected modes incremented submission counters");
        }
        Reset(); {
            XrRuntime runtime; Require(runtime.Initialize(device.Get()),"Init failed");
            auto screen=runtime.BeginScreenFrame(1); CompleteScreen(screen); auto second=screen.AcquireEye(1);
            Require(!second && !screen.SubmitScreen(16.f/9.f) && layers==0 &&
                    std::count(calls.begin(),calls.end(),"acquire1")==0,"Screen acquired or accepted second eye");
            auto abandoned=runtime.BeginScreenFrame(2); { auto first=abandoned.AcquireEye(0); Require(bool(first),"Acquire failed"); }
            Require(!abandoned.SubmitScreen(16.f/9.f) && layers==0,"Uncompleted screen image submitted");
        }
        Reset(); {
            XrRuntime runtime; Require(runtime.Initialize(device.Get()),"Init failed");
            auto old=runtime.BeginScreenFrame(1); auto oldImage=old.AcquireEye(0);
            events.push_back(XR_SESSION_STATE_STOPPING); Require(runtime.PollEvents() && !runtime.Running(),"Session did not stop");
            events={XR_SESSION_STATE_READY,XR_SESSION_STATE_FOCUSED}; auto fresh=runtime.BeginFrame(2);
            Require(bool(fresh),"New session frame unavailable"); const auto ended=endFrames;
            Require(!oldImage && !oldImage.Complete() && !old.SubmitScreen(16.f/9.f) && endFrames==ended && bool(fresh),
                    "Stale screen owner ended or released a newer stereo generation");
            CompletePair(fresh); Require(fresh.Submit(),"New stereo generation damaged by stale screen");
        }
        Reset(); {
            XrRuntime runtime; Require(runtime.Initialize(device.Get()),"Init failed");
            // Yaw90 combined with pitch60. Screen must retain yaw but stay upright.
            headOrientation={.35355339f,.61237244f,-.35355339f,.61237244f}; headX=5; headZ=3; headHeight=2;
            auto initial=runtime.BeginScreenFrame(1); CompleteScreen(initial); Require(initial.SubmitScreen(2,3,4),"Screen failed");
            Require(Near(lastQuad.pose.position.x,2) && Near(lastQuad.pose.position.y,2) && Near(lastQuad.pose.position.z,3) &&
                    Near(lastQuad.pose.orientation.x,0) && Near(lastQuad.pose.orientation.z,0) &&
                    Near(lastQuad.pose.orientation.y,std::sqrt(.5f)) && Near(lastQuad.size.height,2),"Screen is not upright in recentered heading");
            const auto anchored=lastQuad.pose;
            headOrientation={0,0,0,1}; headX=7; headHeight=2.3f;
            auto moved=runtime.BeginScreenFrame(2); CompleteScreen(moved); Require(moved.SubmitScreen(2,3,4),"Moved screen failed");
            Require(Near(lastQuad.pose.position.x,anchored.position.x) && Near(lastQuad.pose.position.y,anchored.position.y) &&
                    Near(lastQuad.pose.position.z,anchored.position.z) && Near(lastQuad.pose.orientation.y,anchored.orientation.y),
                    "Screen followed head motion without recenter");
            runtime.RequestRecenter(); fullTracking=false;
            auto inferred=runtime.BeginScreenFrame(3); CompleteScreen(inferred); Require(inferred.SubmitScreen(2,3,4),"Inferred screen frame failed");
            Require(Near(lastQuad.pose.position.x,anchored.position.x),"Untracked pose recentered screen");
            fullTracking=true; auto centered=runtime.BeginScreenFrame(4); CompleteScreen(centered); Require(centered.SubmitScreen(2,3,4),"Recentered screen failed");
            Require(Near(lastQuad.pose.position.x,7) && Near(lastQuad.pose.position.y,2.3f) && Near(lastQuad.pose.position.z,0) &&
                    Near(lastQuad.pose.orientation.w,1),"Tracked recenter did not reposition screen");
        }
        Reset(); events={XR_SESSION_STATE_READY,XR_SESSION_STATE_VISIBLE}; {
            XrRuntime runtime; Require(runtime.Initialize(device.Get()),"Init failed"); auto unfocused=runtime.BeginScreenFrame(1);
            Require(!unfocused && layers==0 && !runtime.Centered(),"Unfocused screen established recenter origin");
        }
        Reset(); {
            XrRuntime runtime; Require(runtime.Initialize(device.Get()),"Init failed"); uint64_t sequence{};
            const float nan=std::numeric_limits<float>::quiet_NaN(),infinity=std::numeric_limits<float>::infinity();
            const float invalid[][3]={{0,3,3},{-1,3,3},{nan,3,3},{1,0,3},{1,infinity,3},{1,3,-1},{1,3,nan},{1e-38f,3,1e38f}};
            for(const auto& dimensions:invalid) {
                auto frame=runtime.BeginScreenFrame(++sequence); CompleteScreen(frame);
                Require(!frame.SubmitScreen(dimensions[0],dimensions[1],dimensions[2]) && layers==0,"Invalid screen geometry submitted");
            }
            auto good=runtime.BeginScreenFrame(++sequence); CompleteScreen(good);
            Require(good.SubmitScreen(4.f/3.f) && runtime.ScreenSubmitted()==1 && runtime.Submitted()==0,"Invalid screen geometry poisoned later frames");
        }
        Reset(); {
            XrRuntime runtime; Require(runtime.Initialize(device.Get()),"Init failed"); auto frame=runtime.BeginFrame(1);
            { auto eye=frame.AcquireEye(0); Require(bool(eye),"Acquire failed"); }
            Require(!frame.Submit() && layers==0,"Abandoned eye submitted a partial pair");
            Require(std::count(calls.begin(),calls.end(),"release0")==1,"Abandoned eye did not release exactly once");
        }
        Reset(); {
            XrRuntime runtime; Require(runtime.Initialize(device.Get()),"Init failed"); auto frame=runtime.BeginFrame(1);
            auto eye=frame.AcquireEye(0); frame.Abort(); Require(!eye && !eye.Complete(),"Eye survives its frame owner");
            Require(std::count(calls.begin(),calls.end(),"release0")==1,"Frame abort did not release live eye exactly once");
        }
        Reset(); failAcquire=1; {
            XrRuntime runtime; Require(runtime.Initialize(device.Get()),"Init failed"); auto frame=runtime.BeginFrame(1);
            { auto eye=frame.AcquireEye(0); Require(eye.Complete(),"First eye failed"); }
            auto second=frame.AcquireEye(1); Require(!second,"Acquire failure ignored"); Require(!frame.Submit() && layers==0 && runtime.Exiting(),"Failed second eye submitted");
        }
        Reset(); timeoutCount=1; {
            XrRuntime runtime; Require(runtime.Initialize(device.Get()),"Init failed"); auto frame=runtime.BeginFrame(1); CompletePair(frame);
            Require(frame.Submit() && std::count(calls.begin(),calls.end(),"wait0")==2,"Timeout was not retried before use");
        }
        Reset(); timeoutForever=true; {
            XrRuntime runtime; Require(runtime.Initialize(device.Get()),"Init failed"); auto frame=runtime.BeginFrame(1); auto eye=frame.AcquireEye(0);
            Require(!eye && runtime.Exiting(),"Persistent timeout did not stop session"); frame.Abort();
            Require(std::count(calls.begin(),calls.end(),"wait0")==5 && std::count(calls.begin(),calls.end(),"release0")==0,"Wait timeout was unbounded or released an unwaited image");
        }
        Reset(); imageIndex=99; {
            XrRuntime runtime; Require(runtime.Initialize(device.Get()),"Init failed"); auto frame=runtime.BeginFrame(1); auto eye=frame.AcquireEye(0);
            Require(!eye && runtime.Exiting(),"Invalid image index accepted"); frame.Abort();
            Require(std::count(calls.begin(),calls.end(),"release0")==1,"Invalid index failed to release its waited image");
        }
        Reset(); shouldRender=false; {
            XrRuntime runtime; Require(runtime.Initialize(device.Get()),"Init failed"); auto frame=runtime.BeginFrame(1);
            Require(!frame && endFrames==1 && layers==0 && std::count(calls.begin(),calls.end(),"locateViews")==0,"Hidden frame acquired/tracked or was not ended");
        }
        Reset(); events={XR_SESSION_STATE_READY,XR_SESSION_STATE_VISIBLE}; {
            XrRuntime runtime; Require(runtime.Initialize(device.Get()),"Init failed"); auto initial=runtime.BeginFrame(1); Require(!initial && !runtime.Centered() && runtime.ReferenceRevision()==0,"Unfocused pose established seated origin");
            events.push_back(XR_SESSION_STATE_FOCUSED); fullTracking=false; auto inferred=runtime.BeginFrame(2); Require(!inferred,"Untracked pose established seated origin");
            fullTracking=true; auto tracked=runtime.BeginFrame(3); Require(bool(tracked) && runtime.Centered() && runtime.ReferenceRevision()==1,"Focused tracked origin not established"); tracked.Abort();
            runtime.RequestRecenter(); fullTracking=false; headHeight=1.7f; auto pending=runtime.BeginFrame(4);
            Require(bool(pending) && std::abs(pending.RelativeEyePose(0).position.y-.3f)<.001f && runtime.ReferenceRevision()==1,"Untracked manual recenter consumed pending request"); pending.Abort();
            fullTracking=true; auto recentered=runtime.BeginFrame(5); Require(std::abs(recentered.RelativeEyePose(0).position.y)<.001f && runtime.ReferenceRevision()==2,"Tracked recovery did not satisfy recenter"); recentered.Abort();
            events.push_back(XR_SESSION_STATE_STOPPING); auto stopped=runtime.BeginFrame(6); Require(!stopped && !runtime.Running(),"Stopping session rendered");
            runtime.Shutdown(); Require(runtime.ReferenceRevision()==0,"Reference revision survived runtime shutdown");
        }
        Reset(); badPose=true; {
            XrRuntime runtime; Require(runtime.Initialize(device.Get()),"Init failed"); auto bad=runtime.BeginFrame(1); Require(!bad && layers==0,"NaN view rendered");
            badPose=false; auto recovered=runtime.BeginFrame(2); Require(bool(recovered),"Tracking recovery permanently blocked");
        }
        Reset(); {
            XrRuntime runtime; Require(runtime.Initialize(device.Get()),"Init failed"); auto frame=runtime.BeginFrame(1); auto eye=frame.AcquireEye(0);
            Require(bool(eye),"Initial eye failed"); events.push_back(XR_SESSION_STATE_LOSS_PENDING);
            Require(!runtime.PollEvents() && runtime.Exiting() && !frame && !eye,"Session loss left writable eye resources available");
            frame.Abort(); Require(layers==0 && std::count(calls.begin(),calls.end(),"release0")==1,"Session loss failed to release/abort without a layer");
        }
        Reset(); badLuid=true; { XrRuntime runtime; Require(!runtime.Initialize(device.Get()) && sessions==0,"Mismatched adapter created session"); }
        Reset(); failChain=1; { XrRuntime runtime; Require(!runtime.Initialize(device.Get()),"Partial allocation accepted"); Require(destroyedChains==1 && destroyedSessions==1 && destroyedInstances==1,"Partial allocation leaked XR objects"); }
        Reset(); imageCount=9; { XrRuntime runtime; Require(!runtime.Initialize(device.Get()) && destroyedChains==1,"Unbounded image count accepted/leaked"); }
        Reset(); {
            XrRuntime runtime; Require(runtime.Initialize(device.Get(),1,0,0,true),"Init failed");
            std::optional<XrRuntime::Frame> pending;
            { auto render=runtime.BeginFrame(1); CompletePair(render); pending.emplace(std::move(render));
              Require(!render && !render.Submit() && bool(*pending),"Moved frame retains duplicate ownership"); }
            Require(endFrames==0 && !acquired[0] && !acquired[1],"Prepared pair ended early or retained acquired images");
            { auto image=pending->AcquireOverlay(); Require(image.Complete(),"Late movie image failed"); }
            Require(pending->SubmitVideo(16.f/9.f) && endFrames==1 && layers==1 && lastLayerType==XR_TYPE_COMPOSITION_LAYER_QUAD &&
                lastQuad.subImage.swapchain==XrSwapchain(12) && runtime.ScreenSubmitted()==1 && runtime.Submitted()==0,
                "Late video did not replace the complete pair with one current opaque panel");
            pending.reset(); Require(endFrames==1,"Pending owner ended the frame twice");
            auto next=runtime.BeginFrame(2); CompletePair(next); Require(next.Submit() && runtime.Submitted()==1,"Movie-to-stereo retained overlay completion");
            pending.emplace(runtime.BeginFrame(3)); CompletePair(*pending); pending.reset();
            Require(endFrames==3 && layers==0,"Abandoned pending pair was submitted or leaked");
        }
        Reset(); {
            XrRuntime runtime; Require(runtime.Initialize(device.Get(),1,0,0,true),"Init failed");
            auto partial=runtime.BeginFrame(1); { auto eye=partial.AcquireEye(0); Require(eye.Complete(),"First eye failed"); }
            Require(!partial.SubmitVideo(16.f/9.f) && layers==0,"Partial stereo pair became a video fallback");
            auto missing=runtime.BeginFrame(2); CompletePair(missing);
            Require(!missing.SubmitVideo(16.f/9.f) && layers==0,"Video reused an absent/stale overlay");
            auto failed=runtime.BeginFrame(3); CompletePair(failed); { auto image=failed.AcquireOverlay(); Require(bool(image),"Overlay unavailable"); }
            Require(!failed.SubmitVideo(16.f/9.f) && layers==0,"Abandoned video image submitted");
            auto screen=runtime.BeginScreenFrame(4); CompleteScreen(screen);
            Require(!screen.SubmitVideo(16.f/9.f) && layers==0,"Screen bypassed explicit late-video requirements");
            std::optional<XrRuntime::Frame> pending; pending.emplace(runtime.BeginFrame(5)); CompletePair(*pending);
            runtime.Shutdown(); const auto ended=endFrames; pending.reset();
            Require(endFrames==ended,"Pending owner double-ended after shutdown");
        }
        puts("XR synthetic runtime: stereo/screen ownership, late video, pending frames, recenter, timeout, loss and allocation guards passed"); return 0;
    } catch(const std::exception& error) { puts(error.what()); return 1; }
}
