#include "xr_runtime.h"
#include "vr_math.h"
#include "headset_resolution.h"
#include "xr_adapter.h"
#include <dxgi.h>
#include <algorithm>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <stdexcept>

namespace dirt3vr {
using Microsoft::WRL::ComPtr;
namespace {
constexpr uint32_t MaxImages=8, MaxFormats=64, MaxDimension=4096;
constexpr uint64_t MaxEyePixels=8ull*1024*1024, MaxTextureBytes=512ull*1024*1024;
}
void XrRuntime::Report(const char* format,...) noexcept {
    if(!logger_) return;
    char message[512]{};
    va_list args; va_start(args,format); vsnprintf(message,sizeof(message),format,args); va_end(args);
    try { logger_(message); } catch(...) { }
}
bool XrRuntime::Check(XrResult result,const char* operation) noexcept {
    if(XR_FAILED(result)) { Report("%s=%d",operation,result); failed_=true; return false; }
    return true;
}
void XrRuntime::ReportOrientation(const char* reason) noexcept {
    if(orientationReports_>=8) return;
    try {
        // Runtime-space center before our recenter/game-basis transforms. A
        // quaternion sign change alone is not a change in physical orientation.
        const auto center=math::CenterPose(views_);
        const auto relative=math::RelativePose(reference_,views_[0].pose);
        const auto& c=center.orientation; const auto& r=reference_.orientation; const auto& e=relative.orientation;
        const auto& f=views_[0].fov;
        const auto centerUp=math::Rotate(c,{0,1,0}),relativeUp=math::Rotate(e,{0,1,0});
        Report("XR orientation sample=%u reason=%s mode=%s center_q=(%.6f,%.6f,%.6f,%.6f) reference_q=(%.6f,%.6f,%.6f,%.6f) eye0_relative_q=(%.6f,%.6f,%.6f,%.6f) eye0_fov=(%.6f,%.6f,%.6f,%.6f) up_y=%.6f/%.6f",
            ++orientationReports_,reason,frameMode_==FrameMode::Screen ? "screen" : "stereo",
            c.x,c.y,c.z,c.w,r.x,r.y,r.z,r.w,e.x,e.y,e.z,e.w,
            f.angleLeft,f.angleRight,f.angleUp,f.angleDown,centerUp.y,relativeUp.y);
    } catch(...) { } // Diagnostics never change frame eligibility.
}
// Diagnostics only: a missing extension or a failed query never affects the session.
void XrRuntime::ReportRefreshRate(bool enabled) noexcept {
    if(!enabled) { Report("XR display refresh rate unavailable: runtime lacks %s",XR_FB_DISPLAY_REFRESH_RATE_EXTENSION_NAME); return; }
    PFN_xrGetDisplayRefreshRateFB query{};
    float rate{};
    if(XR_SUCCEEDED(xrGetInstanceProcAddr(instance_,"xrGetDisplayRefreshRateFB",reinterpret_cast<PFN_xrVoidFunction*>(&query))) && query &&
       XR_SUCCEEDED(query(session_,&rate)) && std::isfinite(rate) && rate>0.f && rate<1000.f)
        Report("XR display refresh rate=%.2f Hz",double(rate));
    else
        Report("XR display refresh rate unavailable: query failed");
}
XrRuntime::~XrRuntime() { Shutdown(); }
bool XrRuntime::Initialize(ID3D11Device* device,float scale,uint32_t expectedWidth,uint32_t expectedHeight,bool overlay,bool seatPanel,bool perfPanel) noexcept {
    if((expectedWidth || expectedHeight) && (scale!=1.f || !ValidHeadsetSize(expectedWidth,expectedHeight))) return false;
    if(instance_ || !device || !std::isfinite(scale) || scale<.25f || scale>1.f) return false;
    ownerThread_=GetCurrentThreadId(); failed_=exiting_=false;
    overlayEnabled_=overlay; seatPanelEnabled_=seatPanel; perfPanelEnabled_=perfPanel;
    try {
        if(InitializeResources(device,scale,expectedWidth,expectedHeight)) return true;
    } catch(const std::exception& error) { Report("XR initialization exception: %s",error.what()); }
      catch(...) { Report("XR initialization exception"); }
    Shutdown(); failed_=true; return false;
}
bool XrRuntime::InitializeResources(ID3D11Device* device,float scale,uint32_t expectedWidth,uint32_t expectedHeight) {
    // XR_FB_display_refresh_rate is optional and only reported for the launcher's Graphics tab.
    bool refreshRate{};
    uint32_t available{};
    if(XR_SUCCEEDED(xrEnumerateInstanceExtensionProperties(nullptr,0,&available,nullptr)) && available && available<=1024) {
        std::vector<XrExtensionProperties> properties(available,{XR_TYPE_EXTENSION_PROPERTIES});
        if(XR_SUCCEEDED(xrEnumerateInstanceExtensionProperties(nullptr,available,&available,properties.data())))
            for(uint32_t i=0;i<available && i<properties.size();++i)
                refreshRate|=strcmp(properties[i].extensionName,XR_FB_DISPLAY_REFRESH_RATE_EXTENSION_NAME)==0;
    }
    const char* extensions[]={XR_KHR_D3D11_ENABLE_EXTENSION_NAME,XR_FB_DISPLAY_REFRESH_RATE_EXTENSION_NAME};
    XrInstanceCreateInfo create{XR_TYPE_INSTANCE_CREATE_INFO};
    strcpy_s(create.applicationInfo.applicationName,"DiRT3VR");
    create.applicationInfo.apiVersion=XR_MAKE_VERSION(1,0,0);
    create.enabledExtensionCount=refreshRate ? 2 : 1; create.enabledExtensionNames=extensions;
    if(!Check(xrCreateInstance(&create,&instance_),"create instance")) return false;
    // Diagnostics only: names the runtime (VDXR, SteamVR...) in the session log.
    XrInstanceProperties properties{XR_TYPE_INSTANCE_PROPERTIES};
    if(XR_SUCCEEDED(xrGetInstanceProperties(instance_,&properties))) {
        properties.runtimeName[XR_MAX_RUNTIME_NAME_SIZE-1]='\0';
        Report("XR runtime=%s %u.%u.%u",properties.runtimeName,unsigned(XR_VERSION_MAJOR(properties.runtimeVersion)),
               unsigned(XR_VERSION_MINOR(properties.runtimeVersion)),unsigned(XR_VERSION_PATCH(properties.runtimeVersion)));
    }
    XrSystemGetInfo get{XR_TYPE_SYSTEM_GET_INFO}; get.formFactor=XR_FORM_FACTOR_HEAD_MOUNTED_DISPLAY;
    if(!Check(xrGetSystem(instance_,&get,&system_),"get system")) return false;
    PFN_xrGetD3D11GraphicsRequirementsKHR requirementsFn{};
    if(!Check(xrGetInstanceProcAddr(instance_,"xrGetD3D11GraphicsRequirementsKHR",reinterpret_cast<PFN_xrVoidFunction*>(&requirementsFn)),"graphics requirements function") || !requirementsFn) return false;
    XrGraphicsRequirementsD3D11KHR requirements{XR_TYPE_GRAPHICS_REQUIREMENTS_D3D11_KHR};
    if(!Check(requirementsFn(instance_,system_,&requirements),"graphics requirements")) return false;
    ComPtr<IDXGIDevice> dxgi; ComPtr<IDXGIAdapter> adapter; DXGI_ADAPTER_DESC desc{};
    if(FAILED(device->QueryInterface(IID_PPV_ARGS(&dxgi))) || FAILED(dxgi->GetAdapter(&adapter)) ||
       FAILED(adapter->GetDesc(&desc)) || !RequiredAdapter(requirements.adapterLuid,desc.AdapterLuid) ||
       device->GetFeatureLevel()<requirements.minFeatureLevel) {
        Report("XR rejected game D3D11 adapter or feature level"); return false;
    }
    if(AnyAdapter(requirements.adapterLuid)) Report("XR runtime names no graphics adapter; using the game's");
    device_=device;
    XrGraphicsBindingD3D11KHR binding{XR_TYPE_GRAPHICS_BINDING_D3D11_KHR}; binding.device=device;
    XrSessionCreateInfo session{XR_TYPE_SESSION_CREATE_INFO}; session.next=&binding; session.systemId=system_;
    if(!Check(xrCreateSession(instance_,&session,&session_),"create session")) return false;
    ReportRefreshRate(refreshRate);
    uint32_t count{};
    if(!Check(xrEnumerateEnvironmentBlendModes(instance_,system_,XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO,0,&count,nullptr),"blend modes") || !count || count>16) return false;
    std::vector<XrEnvironmentBlendMode> blends(count);
    if(!Check(xrEnumerateEnvironmentBlendModes(instance_,system_,XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO,count,&count,blends.data()),"blend modes") ||
       std::find(blends.begin(),blends.end(),XR_ENVIRONMENT_BLEND_MODE_OPAQUE)==blends.end()) {
        Report("XR requires opaque HMD composition"); return false;
    }
    count=0;
    if(!Check(xrEnumerateViewConfigurationViews(instance_,system_,XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO,0,&count,nullptr),"view count") || count!=2) return false;
    std::array<XrViewConfigurationView,2> views{{{XR_TYPE_VIEW_CONFIGURATION_VIEW},{XR_TYPE_VIEW_CONFIGURATION_VIEW}}};
    if(!Check(xrEnumerateViewConfigurationViews(instance_,system_,XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO,2,&count,views.data()),"view properties") || count!=2) return false;
    if(expectedWidth) {
        for(const auto& view:views) {
            if(view.recommendedImageRectWidth!=expectedWidth || view.recommendedImageRectHeight!=expectedHeight ||
               view.maxImageRectWidth<expectedWidth || view.maxImageRectHeight<expectedHeight) {
                Report("XR headset resolution changed since launch query; quit and launch again"); return false;
            }
        }
    }
    if(!Check(xrEnumerateSwapchainFormats(session_,0,&count,nullptr),"format count") || !count || count>MaxFormats) return false;
    std::vector<int64_t> formats(count);
    if(!Check(xrEnumerateSwapchainFormats(session_,count,&count,formats.data()),"formats") || count>formats.size()) return false;
    formats.resize(count);
    for(auto candidate:{DXGI_FORMAT_R8G8B8A8_UNORM_SRGB,DXGI_FORMAT_B8G8R8A8_UNORM_SRGB,DXGI_FORMAT_R8G8B8A8_UNORM,DXGI_FORMAT_B8G8R8A8_UNORM})
        if(std::find(formats.begin(),formats.end(),candidate)!=formats.end()) { format_=candidate; break; }
    if(format_==DXGI_FORMAT_UNKNOWN) { Report("No supported XR RGBA/BGRA swapchain format"); return false; }
    XrReferenceSpaceCreateInfo reference{XR_TYPE_REFERENCE_SPACE_CREATE_INFO};
    reference.referenceSpaceType=XR_REFERENCE_SPACE_TYPE_LOCAL; reference.poseInReferenceSpace.orientation.w=1;
    if(!Check(xrCreateReferenceSpace(session_,&reference,&space_),"create LOCAL space")) return false;
    uint64_t textureBytes{};
    for(unsigned i=0;i<(overlayEnabled_ ? 3u : 2u);++i) {
        const auto& view=views[i==2 ? 0 : i]; auto& eye=eyes_[i];
        if(!view.recommendedImageRectWidth || !view.recommendedImageRectHeight || !view.maxImageRectWidth || !view.maxImageRectHeight) return false;
        const double width=view.recommendedImageRectWidth,height=view.recommendedImageRectHeight;
        const double factor=std::min({double(scale),double(MaxDimension)/width,double(MaxDimension)/height,
            double(view.maxImageRectWidth)/width,double(view.maxImageRectHeight)/height,std::sqrt(double(MaxEyePixels)/(width*height))});
        eye.width=expectedWidth ? expectedWidth : std::max(1u,uint32_t(width*factor));
        eye.height=expectedHeight ? expectedHeight : std::max(1u,uint32_t(height*factor));
        if(i==2) {
            const auto panelScale=std::min({1.,1536./width,1536./height});
            eye.width=std::max(1u,uint32_t(width*panelScale));
            eye.height=std::max(1u,uint32_t(height*panelScale));
        }
        if(!CreateSwapchain(device,i,textureBytes,expectedWidth || i==2)) return false;
    }
    if(seatPanelEnabled_) {
        eyes_[3].width=SeatPanelWidth; eyes_[3].height=SeatPanelHeight;
        if(!CreateSwapchain(device,3,textureBytes,true)) return false;
    }
    if(perfPanelEnabled_) {
        eyes_[4].width=PerfPanelWidth; eyes_[4].height=PerfPanelHeight;
        if(!CreateSwapchain(device,4,textureBytes,true)) return false;
    }
    Report("XR initialized with game D3D11 device; awaiting session focus and tracked recenter");
    return true;
}
bool XrRuntime::CreateSwapchain(ID3D11Device* device,unsigned index,uint64_t& textureBytes,bool limitImages) {
    auto& eye=eyes_[index]; uint32_t count{};
    XrSwapchainCreateInfo info{XR_TYPE_SWAPCHAIN_CREATE_INFO};
    info.usageFlags=XR_SWAPCHAIN_USAGE_COLOR_ATTACHMENT_BIT|XR_SWAPCHAIN_USAGE_TRANSFER_DST_BIT;
    info.format=format_; info.sampleCount=1; info.width=eye.width; info.height=eye.height;
    info.faceCount=info.arraySize=info.mipCount=1;
    if(!Check(xrCreateSwapchain(session_,&info,&eye.handle),"create eye swapchain")) return false;
    if(!Check(xrEnumerateSwapchainImages(eye.handle,0,&count,nullptr),"image count") || !count || count>MaxImages) return false;
    if(limitImages && count>4) { Report("XR recommended-size output requires at most four images per layer"); return false; }
    textureBytes+=uint64_t(eye.width)*eye.height*4*count;
    if(textureBytes>MaxTextureBytes) { Report("XR color resource budget exceeded"); return false; }
    eye.images.resize(count,{XR_TYPE_SWAPCHAIN_IMAGE_D3D11_KHR});
    if(!Check(xrEnumerateSwapchainImages(eye.handle,count,&count,reinterpret_cast<XrSwapchainImageBaseHeader*>(eye.images.data())),"images") || count!=eye.images.size()) return false;
    eye.targets.resize(count);
    for(unsigned image=0;image<count;++image) {
        D3D11_RENDER_TARGET_VIEW_DESC target{}; target.Format=format_; target.ViewDimension=D3D11_RTV_DIMENSION_TEXTURE2D;
        if(!eye.images[image].texture || FAILED(device->CreateRenderTargetView(eye.images[image].texture,&target,&eye.targets[image]))) {
            Report("XR image RTV allocation failed"); return false;
        }
    }
    Report("XR eye=%u dimensions=%ux%u images=%u format=%u",index,eye.width,eye.height,count,unsigned(format_));
    return true;
}
bool XrRuntime::PollEvents() noexcept {
    if(!instance_ || GetCurrentThreadId()!=ownerThread_ || failed_ || exiting_) return false;
    XrEventDataBuffer event{XR_TYPE_EVENT_DATA_BUFFER};
    for(unsigned events=0;events<128;++events) {
        const auto result=xrPollEvent(instance_,&event);
        if(result==XR_EVENT_UNAVAILABLE) return !failed_ && !exiting_;
        if(!Check(result,"poll event")) return false;
        if(event.type==XR_TYPE_EVENT_DATA_SESSION_STATE_CHANGED) {
            const auto& changed=*reinterpret_cast<XrEventDataSessionStateChanged*>(&event);
            if(changed.session==session_) {
                Report("XR session state=%d",changed.state);
                visible_=changed.state==XR_SESSION_STATE_VISIBLE || changed.state==XR_SESSION_STATE_FOCUSED;
                focused_=changed.state==XR_SESSION_STATE_FOCUSED;
                if(!focused_) canRecenter_=false;
                if(changed.state==XR_SESSION_STATE_READY && !running_) {
                    XrSessionBeginInfo begin{XR_TYPE_SESSION_BEGIN_INFO}; begin.primaryViewConfigurationType=XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
                    running_=Check(xrBeginSession(session_,&begin),"begin session");
                } else if(changed.state==XR_SESSION_STATE_STOPPING) {
                    if(frameOpen_) EndFrame(generation_,false);
                    if(running_) Check(xrEndSession(session_),"end session");
                    running_=false; canRecenter_=false;
                } else if(changed.state==XR_SESSION_STATE_EXITING || changed.state==XR_SESSION_STATE_LOSS_PENDING) {
                    exiting_=true; canRecenter_=false;
                }
            }
        } else if(event.type==XR_TYPE_EVENT_DATA_INSTANCE_LOSS_PENDING) { exiting_=true; canRecenter_=false; }
        else if(event.type==XR_TYPE_EVENT_DATA_REFERENCE_SPACE_CHANGE_PENDING) {
            const auto& changed=*reinterpret_cast<XrEventDataReferenceSpaceChangePending*>(&event);
            if(changed.session==session_ && changed.referenceSpaceType==XR_REFERENCE_SPACE_TYPE_LOCAL)
                spaceChangeTime_=changed.changeTime;
        }
        event={XR_TYPE_EVENT_DATA_BUFFER};
    }
    Report("XR event queue limit reached; retry next game frame"); return false;
}
bool XrRuntime::Current(uint64_t generation) const noexcept {
    return GetCurrentThreadId()==ownerThread_ && frameOpen_ && generation==generation_;
}
bool XrRuntime::EyeCurrent(uint64_t generation,unsigned eye) const noexcept {
    return eye<eyes_.size() && Current(generation) && !frameFailed_ && !failed_ && !exiting_ && eyes_[eye].acquired && eyes_[eye].waited;
}
bool XrRuntime::StartFrame(uint64_t sequence,FrameMode mode) noexcept {
    if(GetCurrentThreadId()!=ownerThread_ || frameOpen_ || failed_ || exiting_ ||
       (haveGameSequence_ && sequence<=lastGameSequence_) || !PollEvents() || !running_) return false;
    canRecenter_=false; haveGameSequence_=true; lastGameSequence_=sequence;
    XrFrameWaitInfo wait{XR_TYPE_FRAME_WAIT_INFO}; XrFrameState state{XR_TYPE_FRAME_STATE};
    if(!Check(xrWaitFrame(session_,&wait,&state),"wait frame")) return false;
    XrFrameBeginInfo begin{XR_TYPE_FRAME_BEGIN_INFO};
    if(!Check(xrBeginFrame(session_,&begin),"begin frame")) return false;
    frameOpen_=true; frameFailed_=frameRenderable_=seatShown_=perfShown_=false; frameMode_=mode; ++generation_; displayTime_=state.predictedDisplayTime;
    if(state.predictedDisplayPeriod>0) displayPeriod_=state.predictedDisplayPeriod;
    for(auto& eye:eyes_) { eye.completed=false; eye.acquired=eye.waited=false; }
    if(!state.shouldRender) { EndFrame(generation_,false); return false; }
    if(spaceChangeTime_ && displayTime_>=spaceChangeTime_) {
        spaceChangeTime_=0; centered_=false; recenterPending_=true; recenterTilt_=false;
    }
    XrViewLocateInfo locate{XR_TYPE_VIEW_LOCATE_INFO}; locate.viewConfigurationType=XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO;
    locate.displayTime=displayTime_; locate.space=space_;
    XrViewState tracking{XR_TYPE_VIEW_STATE}; uint32_t count{};
    constexpr auto valid=XR_VIEW_STATE_ORIENTATION_VALID_BIT|XR_VIEW_STATE_POSITION_VALID_BIT;
    constexpr auto tracked=XR_VIEW_STATE_ORIENTATION_TRACKED_BIT|XR_VIEW_STATE_POSITION_TRACKED_BIT;
    if(!Check(xrLocateViews(session_,&locate,&tracking,2,&count,views_.data()),"locate views") || count!=2 || (tracking.viewStateFlags&valid)!=valid) {
        EndFrame(generation_,false); return false;
    }
    try {
        for(auto& view:views_) {
            if(!math::Finite(view.pose.position) || !math::ValidFov(view.fov)) throw std::invalid_argument("Invalid XR view");
            view.pose.orientation=math::Normalize(view.pose.orientation);
        }
        if(frameMode_==FrameMode::Stereo && (fovWidth_<1.f || fovHeight_<1.f))
            for(auto& view:views_) view.fov=math::CropFov(view.fov,fovWidth_,fovHeight_);
        canRecenter_=focused_ && (tracking.viewStateFlags&tracked)==tracked;
        bool recentered=false;
        if(recenterPending_ && canRecenter_) {
            const auto center=math::CenterPose(views_);
            reference_=recenterTilt_ ? math::RecenterPoseWithTilt(center) : math::RecenterPose(center,reference_);
            ++referenceRevision_;
            Report(recenterTilt_ ? "XR recentered from focused tracked pose including tilt" : "XR recentered from focused tracked pose");
            recenterPending_=recenterTilt_=false; centered_=true;
            recentered=true;
        }
        if(!centered_) { EndFrame(generation_,false); return false; }
        const unsigned modeBit=frameMode_==FrameMode::Screen ? 2u : 1u;
        if(recentered || !(orientationModes_&modeBit)) ReportOrientation(recentered ? "recenter" : "first-renderable");
        orientationModes_|=modeBit;
    } catch(const std::exception& error) { Report("XR pose rejected: %s",error.what()); EndFrame(generation_,false); return false; }
    frameRenderable_=true; return true;
}
bool XrRuntime::Acquire(uint64_t generation,unsigned index) noexcept {
    if(!Current(generation) || !frameRenderable_ || frameFailed_ || failed_ || exiting_ || index>=eyes_.size()) return false;
    if(frameMode_==FrameMode::Screen && index!=0) { frameFailed_=true; return false; }
    if(index==2 && (!overlayEnabled_ || !eyes_[0].completed || !eyes_[1].completed)) { frameFailed_=true; return false; }
    if(index==3 && (!seatPanelEnabled_ || frameMode_!=FrameMode::Stereo || !eyes_[0].completed || !eyes_[1].completed)) { frameFailed_=true; return false; }
    if(index==4 && (!perfPanelEnabled_ || frameMode_!=FrameMode::Stereo || !eyes_[0].completed || !eyes_[1].completed)) { frameFailed_=true; return false; }
    auto& eye=eyes_[index];
    if(eye.completed || std::any_of(eyes_.begin(),eyes_.end(),[](const auto& image){return image.acquired;})) { frameFailed_=true; return false; }
    XrSwapchainImageAcquireInfo acquire{XR_TYPE_SWAPCHAIN_IMAGE_ACQUIRE_INFO};
    if(!Check(xrAcquireSwapchainImage(eye.handle,&acquire,&eye.index),"acquire eye")) { frameFailed_=true; return false; }
    eye.acquired=true;
    XrSwapchainImageWaitInfo wait{XR_TYPE_SWAPCHAIN_IMAGE_WAIT_INFO}; wait.timeout=100000000; // 100 ms, at most five waits.
    XrResult result=XR_TIMEOUT_EXPIRED;
    for(unsigned attempt=0;attempt<5 && result==XR_TIMEOUT_EXPIRED;++attempt) result=xrWaitSwapchainImage(eye.handle,&wait);
    if(result==XR_TIMEOUT_EXPIRED || !Check(result,"wait eye")) {
        Report("XR image wait did not complete; session requires shutdown"); failed_=frameFailed_=true; return false;
    }
    eye.waited=true;
    if(eye.index>=eye.images.size() || eye.index>=eye.targets.size()) {
        Report("XR returned invalid image index"); failed_=frameFailed_=true; Release(generation,index,false); return false;
    }
    return true;
}
bool XrRuntime::Release(uint64_t generation,unsigned index,bool completed) noexcept {
    if(!Current(generation) || index>=eyes_.size()) return false;
    auto& eye=eyes_[index];
    if(!eye.acquired) return false;
    // OpenXR forbids release before a successful wait. A failed wait makes the
    // session terminal; Shutdown destroys its acquired swapchain instead.
    if(!eye.waited) { failed_=frameFailed_=true; return false; }
    XrSwapchainImageReleaseInfo release{XR_TYPE_SWAPCHAIN_IMAGE_RELEASE_INFO};
    const bool released=Check(xrReleaseSwapchainImage(eye.handle,&release),"release eye");
    eye.acquired=eye.waited=false; eye.completed=released && completed;
    if(!eye.completed) frameFailed_=true;
    return eye.completed;
}
bool XrRuntime::EndFrame(uint64_t generation,bool submit,FrameMode mode,float aspect,float distance,float width,bool overlay,bool video,bool follow) noexcept {
    if(!Current(generation)) return false;
    for(unsigned i=0;i<eyes_.size();++i) if(eyes_[i].acquired) { Release(generation,i,false); submit=false; }
    bool complete=submit && mode==frameMode_ && frameRenderable_ && !frameFailed_ && !failed_ && !exiting_ && eyes_[0].completed;
    complete&=mode==FrameMode::Stereo ? eyes_[1].completed : !eyes_[1].completed;
    complete&=overlay ? (overlayEnabled_ && mode==FrameMode::Stereo && eyes_[2].completed) : !eyes_[2].completed;
    complete&=!video || (overlay && mode==FrameMode::Stereo);
    complete&=!eyes_[3].completed || (seatShown_ && mode==FrameMode::Stereo && !video);
    complete&=!eyes_[4].completed || (perfShown_ && mode==FrameMode::Stereo && !video);
    std::array<XrCompositionLayerProjectionView,2> views{{{XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW},{XR_TYPE_COMPOSITION_LAYER_PROJECTION_VIEW}}};
    for(unsigned i=0;i<2;++i) {
        views[i].pose=views_[i].pose; views[i].fov=views_[i].fov;
        views[i].subImage.swapchain=eyes_[i].handle;
        views[i].subImage.imageRect.extent={int32_t(eyes_[i].width),int32_t(eyes_[i].height)};
    }
    XrCompositionLayerProjection projection{XR_TYPE_COMPOSITION_LAYER_PROJECTION};
    projection.space=space_; projection.viewCount=2; projection.views=views.data();
    XrCompositionLayerQuad screen{XR_TYPE_COMPOSITION_LAYER_QUAD};
    screen.space=space_; screen.eyeVisibility=XR_EYE_VISIBILITY_BOTH;
    const auto& panel=eyes_[overlay ? 2 : 0];
    screen.subImage.swapchain=panel.handle;
    screen.subImage.imageRect.extent={int32_t(panel.width),int32_t(panel.height)};
    if(overlay && !video) screen.layerFlags=XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT|XR_COMPOSITION_LAYER_UNPREMULTIPLIED_ALPHA_BIT;
    if(complete && (mode==FrameMode::Screen || overlay)) {
        complete=std::isfinite(aspect) && aspect>0 && std::isfinite(distance) && distance>0 &&
                 std::isfinite(width) && width>0;
        // Angular bounds: the 0.5–4 m wide, at most 1.8 m tall panel at 2 m, at any distance.
        if(overlay) complete&=distance>=1.f && distance<=5.f && width>=.25f*distance && width<=2.f*distance;
        if(complete) {
            screen.size={width,width/aspect};
            if(overlay && screen.size.height>.9f*distance) screen.size={.9f*distance*aspect,.9f*distance};
            complete=std::isfinite(screen.size.height) && screen.size.height>0;
            try {
                // Fixed in LOCAL space until recenter: head turns do not drag
                // the screen. RecenterPose retains an upright yaw reference.
                // A following HUD panel stays ahead of the current view instead.
                screen.pose=math::OffsetPose(overlay && follow && mode==FrameMode::Stereo && !video ? math::CenterPose(views_) : reference_,{0,0,-distance});
                complete&=math::Finite(screen.pose.position);
            } catch(...) { complete=false; }
        }
    }
    const XrCompositionLayerBaseHeader* layer=mode==FrameMode::Stereo && !video ?
        reinterpret_cast<const XrCompositionLayerBaseHeader*>(&projection) :
        reinterpret_cast<const XrCompositionLayerBaseHeader*>(&screen);
    // The seat menu panel stays where it opened in LOCAL space, in front of the HUD.
    XrCompositionLayerQuad seat{XR_TYPE_COMPOSITION_LAYER_QUAD};
    seat.space=space_; seat.eyeVisibility=XR_EYE_VISIBILITY_BOTH; seat.pose=seatPose_; seat.size=seatSize_;
    seat.layerFlags=XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT|XR_COMPOSITION_LAYER_UNPREMULTIPLIED_ALPHA_BIT;
    seat.subImage.swapchain=eyes_[3].handle;
    seat.subImage.imageRect.extent={int32_t(eyes_[3].width),int32_t(eyes_[3].height)};
    // GRID2VR's FPS / GPU strip: 1.5 m ahead and 45 cm below the view, or 60 cm below the
    // recentered HUD position at 2 m. It stays in front of the HUD panel, never in its plane:
    // SteamVR flickered between the two where they overlapped (default 2 m HUD). Nearer, it
    // keeps its angular size.
    const float perfDistance=perfFollow_ ? 1.5f : 2.f;
    const float perfScale=overlay && std::isfinite(distance) && distance>0 ? std::min(1.f,.95f*distance/perfDistance) : 1.f;
    XrCompositionLayerQuad perf{XR_TYPE_COMPOSITION_LAYER_QUAD};
    perf.space=space_; perf.eyeVisibility=XR_EYE_VISIBILITY_BOTH;
    perf.subImage.swapchain=eyes_[4].handle;
    perf.subImage.imageRect.extent={int32_t(eyes_[4].width),int32_t(eyes_[4].height)};
    perf.size=perfFollow_ ? XrExtent2Df{.6f*perfScale,.075f*perfScale} : XrExtent2Df{.8f*perfScale,.1f*perfScale};
    if(complete && eyes_[4].completed) {
        try {
            perf.pose=perfFollow_ ? math::OffsetPose(math::CenterPose(views_),{0,-.45f*perfScale,-1.5f*perfScale}) :
                                    math::OffsetPose(reference_,{0,-.6f*perfScale,-2.f*perfScale});
            complete&=math::Finite(perf.pose.position);
        } catch(...) { complete=false; }
    }
    XrFrameEndInfo end{XR_TYPE_FRAME_END_INFO}; end.displayTime=displayTime_; end.environmentBlendMode=XR_ENVIRONMENT_BLEND_MODE_OPAQUE;
    std::array<const XrCompositionLayerBaseHeader*,4> layers{layer};
    uint32_t layerCount=1;
    if(overlay && !video) layers[layerCount++]=reinterpret_cast<const XrCompositionLayerBaseHeader*>(&screen);
    if(eyes_[4].completed) layers[layerCount++]=reinterpret_cast<const XrCompositionLayerBaseHeader*>(&perf);
    if(eyes_[3].completed) layers[layerCount++]=reinterpret_cast<const XrCompositionLayerBaseHeader*>(&seat);
    if(complete) { end.layerCount=layerCount; end.layers=layers.data(); }
    const bool ended=Check(xrEndFrame(session_,&end),"end frame");
    frameOpen_=frameRenderable_=seatShown_=perfShown_=false; canRecenter_=false;
    if(complete && ended) {
        if(mode==FrameMode::Stereo && !video) ++submitted_;
        else ++screenSubmitted_;
    }
    return complete && ended;
}
bool XrRuntime::CropFieldOfView(float width,float height) noexcept {
    if(!std::isfinite(width) || !std::isfinite(height) || width<.7f || width>1.f || height<.7f || height>1.f) return false;
    fovWidth_=width; fovHeight_=height;
    if(width<1.f || height<1.f) Report("XR field of view cropped to %.0f%% wide, %.0f%% high",width*100.f,height*100.f);
    return true;
}
void XrRuntime::Shutdown() noexcept {
    if(ownerThread_ && GetCurrentThreadId()!=ownerThread_) { Report("XR Shutdown requires the owning render thread"); return; }
    if(frameOpen_) EndFrame(generation_,false);
    for(auto& eye:eyes_) {
        eye.targets.clear(); eye.images.clear();
        if(eye.handle) xrDestroySwapchain(eye.handle);
        eye={};
    }
    if(space_) xrDestroySpace(space_);
    if(session_) xrDestroySession(session_);
    if(instance_) xrDestroyInstance(instance_);
    device_.Reset();
    space_={}; session_={}; instance_={}; system_={}; format_=DXGI_FORMAT_UNKNOWN; displayPeriod_=0;
    running_=visible_=focused_=frameOpen_=frameRenderable_=canRecenter_=centered_=false;
    recenterPending_=true; recenterTilt_=false; haveGameSequence_=false; spaceChangeTime_=0;
    orientationModes_=0;
    referenceRevision_=0;
    overlayEnabled_=seatPanelEnabled_=seatShown_=perfPanelEnabled_=perfShown_=perfFollow_=false;
    reference_={{0,0,0,1},{0,0,0}}; ++generation_;
}
std::array<uint32_t,4> XrRuntime::EyeDimensions() const noexcept {
    return {eyes_[0].width,eyes_[0].height,eyes_[1].width,eyes_[1].height};
}
XrRuntime::Frame::Frame(XrRuntime& owner,uint64_t sequence,FrameMode mode) noexcept : mode_(mode) {
    if(owner.StartFrame(sequence,mode)) { owner_=&owner; generation_=owner.generation_; }
}
XrRuntime::Frame::Frame(Frame&& other) noexcept : owner_(other.owner_),generation_(other.generation_),mode_(other.mode_) {
    other.owner_=nullptr;
}
XrRuntime::Frame::~Frame() { Abort(); }
XrRuntime::Frame::operator bool() const noexcept { return owner_ && owner_->Current(generation_) && owner_->frameRenderable_ && !owner_->frameFailed_ && !owner_->failed_ && !owner_->exiting_; }
const std::array<XrView,2>& XrRuntime::Frame::Views() const {
    if(!*this || mode_!=FrameMode::Stereo) throw std::logic_error("XR stereo frame is not active"); return owner_->views_;
}
XrPosef XrRuntime::Frame::RelativeEyePose(unsigned eye) const {
    if(!*this || mode_!=FrameMode::Stereo || eye>1) throw std::logic_error("XR stereo eye pose is not active");
    return math::RelativePose(owner_->reference_,owner_->views_[eye].pose);
}
XrRuntime::Eye XrRuntime::Frame::AcquireEye(unsigned eye) {
    if(!owner_) throw std::logic_error("XR frame is not active");
    return Eye(*owner_,generation_,eye<2 ? eye : NoImage);
}
XrRuntime::Eye XrRuntime::Frame::AcquireOverlay() {
    if(!owner_) throw std::logic_error("XR frame is not active");
    return Eye(*owner_,generation_,2);
}
XrRuntime::Eye XrRuntime::Frame::AcquireSeatPanel() {
    if(!owner_) throw std::logic_error("XR frame is not active");
    return Eye(*owner_,generation_,3);
}
XrRuntime::Eye XrRuntime::Frame::AcquirePerfPanel() {
    if(!owner_) throw std::logic_error("XR frame is not active");
    return Eye(*owner_,generation_,4);
}
bool XrRuntime::Frame::ShowPerfPanel(bool follow) noexcept { return owner_ && owner_->ShowPerf(generation_,follow); }
bool XrRuntime::ShowPerf(uint64_t generation,bool follow) noexcept {
    if(!Current(generation) || !eyes_[4].completed) return false;
    perfShown_=true; perfFollow_=follow;
    return true;
}
bool XrRuntime::Frame::ShowSeatPanel(const XrPosef& pose,float width,float height) noexcept {
    return owner_ && owner_->ShowSeat(generation_,pose,width,height);
}
bool XrRuntime::ShowSeat(uint64_t generation,const XrPosef& pose,float width,float height) noexcept {
    if(!Current(generation) || !eyes_[3].completed || !math::Finite(pose.position) ||
       !std::isfinite(width) || !std::isfinite(height) || width<=0 || height<=0 || width>2.f || height>2.f) return false;
    try { seatPose_={math::Normalize(pose.orientation),pose.position}; } catch(...) { return false; }
    seatSize_={width,height}; seatShown_=true;
    return true;
}
bool XrRuntime::Frame::SubmitOverlay(float aspect,float distance,float width,bool follow) noexcept {
    if(!owner_) return false;
    const bool submitted=owner_->EndFrame(generation_,mode_==FrameMode::Stereo,FrameMode::Stereo,aspect,distance,width,true,false,follow);
    owner_=nullptr; return submitted;
}
bool XrRuntime::Frame::SubmitVideo(float aspect,float distance,float width) noexcept {
    if(!owner_) return false;
    const bool submitted=owner_->EndFrame(generation_,mode_==FrameMode::Stereo,FrameMode::Stereo,aspect,distance,width,true,true);
    owner_=nullptr; return submitted;
}
bool XrRuntime::Frame::Submit() noexcept {
    if(!owner_) return false;
    const bool submitted=owner_->EndFrame(generation_,mode_==FrameMode::Stereo);
    owner_=nullptr; return submitted;
}
bool XrRuntime::Frame::SubmitScreen(float aspect,float distance,float width) noexcept {
    if(!owner_) return false;
    const bool submitted=owner_->EndFrame(generation_,mode_==FrameMode::Screen,FrameMode::Screen,aspect,distance,width);
    owner_=nullptr; return submitted;
}
void XrRuntime::Frame::Abort() noexcept { if(owner_) { owner_->EndFrame(generation_,false); owner_=nullptr; } }
XrRuntime::Eye::Eye(XrRuntime& owner,uint64_t generation,unsigned eye) noexcept : owner_(&owner),generation_(generation),eye_(eye) {
    acquired_=owner_->Acquire(generation_,eye_);
}
XrRuntime::Eye::~Eye() { if(acquired_ && owner_) owner_->Release(generation_,eye_,false); }
XrRuntime::Eye::operator bool() const noexcept { return acquired_ && owner_ && owner_->EyeCurrent(generation_,eye_); }
ID3D11Texture2D* XrRuntime::Eye::Texture() const noexcept { return *this ? owner_->eyes_[eye_].images[owner_->eyes_[eye_].index].texture : nullptr; }
ID3D11RenderTargetView* XrRuntime::Eye::Target() const noexcept { return *this ? owner_->eyes_[eye_].targets[owner_->eyes_[eye_].index].Get() : nullptr; }
uint32_t XrRuntime::Eye::Width() const noexcept { return *this ? owner_->eyes_[eye_].width : 0; }
uint32_t XrRuntime::Eye::Height() const noexcept { return *this ? owner_->eyes_[eye_].height : 0; }
bool XrRuntime::Eye::Complete() noexcept {
    if(!acquired_ || !owner_) return false; acquired_=false; return owner_->Release(generation_,eye_,true);
}
}
