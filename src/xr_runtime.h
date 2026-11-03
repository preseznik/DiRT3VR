#pragma once
#include <windows.h>
#include <d3d11.h>
#include <openxr/openxr.h>
#include <openxr/openxr_platform.h>
#include <wrl/client.h>
#include <array>
#include <cstdint>
#include <vector>

namespace dirt3vr {
// Render-thread-owned. Initialize only after the caller's explicit VR opt-in.
// Runtime selection comes from this process's existing XR_RUNTIME_JSON value.
class XrRuntime {
public:
    using Logger = void (*)(const char*);
    enum class FrameMode { Stereo, Screen };
    class Eye;
    class Frame {
    public:
        Frame(const Frame&) = delete;
        Frame& operator=(const Frame&) = delete;
        Frame(Frame&& other) noexcept;
        Frame& operator=(Frame&&) = delete;
        ~Frame();
        explicit operator bool() const noexcept;
        FrameMode Mode() const noexcept { return mode_; }
        const std::array<XrView,2>& Views() const;
        XrPosef RelativeEyePose(unsigned eye) const;
        Eye AcquireEye(unsigned eye);
        Eye AcquireOverlay();
        // The seat menu's panel (SeatPanelWidth × SeatPanelHeight), after a complete stereo pair.
        // A completed panel needs ShowSeatPanel before Submit / SubmitOverlay, or nothing submits.
        Eye AcquireSeatPanel();
        bool ShowSeatPanel(const XrPosef& pose,float width,float height) noexcept;
        // The FPS / GPU strip (PerfPanelWidth × PerfPanelHeight, opaque), after a complete stereo
        // pair; ShowPerfPanel places it below the view (follow) or below the recentered HUD.
        Eye AcquirePerfPanel();
        bool ShowPerfPanel(bool follow) noexcept;
        bool Submit() noexcept;
        // follow: the panel stays ahead of the current view instead of the recentered reference.
        // Its angular size is bounded: width 0.25–2 × distance, height at most 0.9 × distance.
        bool SubmitOverlay(float aspect,float distance=2.f,float width=1.6f,bool follow=false) noexcept;
        // A recognized movie/startup screen replaces a COMPLETE stereo pair with the
        // current overlay image. Never a fallback for a failed/partial pair.
        bool SubmitVideo(float aspect,float distance=3.f,float width=3.f) noexcept;
        // Screen mode owns only image zero. Aspect is the original game's
        // width/height; the physical quad restores it after texture scaling.
        bool SubmitScreen(float aspect,float distance=3.f,float width=3.f) noexcept;
        void Abort() noexcept;
    private:
        friend class XrRuntime;
        Frame(XrRuntime& owner,uint64_t gameSequence,FrameMode mode) noexcept;
        XrRuntime* owner_{};
        uint64_t generation_{};
        FrameMode mode_;
    };
    class Eye {
    public:
        Eye(const Eye&) = delete;
        Eye& operator=(const Eye&) = delete;
        Eye(Eye&&) = delete;
        Eye& operator=(Eye&&) = delete;
        ~Eye();
        explicit operator bool() const noexcept;
        ID3D11Texture2D* Texture() const noexcept;
        ID3D11RenderTargetView* Target() const noexcept;
        uint32_t Width() const noexcept;
        uint32_t Height() const noexcept;
        // Call after successful rendering/copy and restoring all game bindings.
        // Omitting Complete invalidates the frame; a partial pair never submits.
        bool Complete() noexcept;
    private:
        friend class Frame;
        Eye(XrRuntime& owner,uint64_t generation,unsigned eye) noexcept;
        XrRuntime* owner_{};
        uint64_t generation_{};
        unsigned eye_{};
        bool acquired_{};
    };

    static constexpr uint32_t SeatPanelWidth=768,SeatPanelHeight=1024,PerfPanelWidth=512,PerfPanelHeight=64;
    explicit XrRuntime(Logger logger=nullptr) noexcept : logger_(logger) {}
    ~XrRuntime();
    XrRuntime(const XrRuntime&) = delete;
    XrRuntime& operator=(const XrRuntime&) = delete;
    bool Initialize(ID3D11Device* device,float outputScale=1.f,uint32_t expectedWidth=0,uint32_t expectedHeight=0,bool overlay=false,bool seatPanel=false,bool perfPanel=false) noexcept;
    bool PollEvents() noexcept;
    Frame BeginFrame(uint64_t monotonicGameSequence) noexcept { return Frame(*this,monotonicGameSequence,FrameMode::Stereo); }
    Frame BeginScreenFrame(uint64_t monotonicGameSequence) noexcept { return Frame(*this,monotonicGameSequence,FrameMode::Screen); }
    void Shutdown() noexcept;
    // Narrows each stereo eye's field of view to width / height (0.7-1) of the runtime's, in tangent
    // space: the game renders and the runtime shows the same narrower view (#16a86c75, #47d16d40).
    bool CropFieldOfView(float width,float height) noexcept;
    // includeTilt also resets pitch and roll (the player's choice); automatic centering never does.
    void RequestRecenter(bool includeTilt=false) noexcept { recenterPending_=true; recenterTilt_=includeTilt; }
    bool Initialized() const noexcept { return session_!=XR_NULL_HANDLE && !failed_; }
    bool Running() const noexcept { return running_ && !failed_; }
    bool Exiting() const noexcept { return failed_ || exiting_; }
    bool CanRecenter() const noexcept { return canRecenter_; }
    bool Centered() const noexcept { return centered_; }
    // Advances only when the tracked reference is actually established.
    uint64_t ReferenceRevision() const noexcept { return referenceRevision_; }
    bool Visible() const noexcept { return visible_; }
    // Existing counter retains stereo-only semantics.
    uint64_t Submitted() const noexcept { return submitted_; }
    uint64_t ScreenSubmitted() const noexcept { return screenSubmitted_; }
    DXGI_FORMAT Format() const noexcept { return format_; }
    // The headset's frame period from the last xrWaitFrame, 0 before the first frame.
    XrDuration DisplayPeriod() const noexcept { return displayPeriod_; }
    std::array<uint32_t,4> EyeDimensions() const noexcept;
private:
    friend struct XrRuntimeTestAccess;
    struct Swapchain {
        XrSwapchain handle{};
        uint32_t width{},height{},index{};
        std::vector<XrSwapchainImageD3D11KHR> images;
        std::vector<Microsoft::WRL::ComPtr<ID3D11RenderTargetView>> targets;
        bool acquired{},waited{},completed{};
    };
    static constexpr unsigned NoImage=99; // AcquireEye's refusal for an index outside the pair.
    bool InitializeResources(ID3D11Device*,float,uint32_t,uint32_t);
    bool CreateSwapchain(ID3D11Device*,unsigned index,uint64_t& textureBytes,bool limitImages);
    bool ShowSeat(uint64_t,const XrPosef&,float,float) noexcept;
    bool ShowPerf(uint64_t,bool) noexcept;
    bool StartFrame(uint64_t,FrameMode) noexcept;
    bool Acquire(uint64_t,unsigned) noexcept;
    bool Release(uint64_t,unsigned,bool completed) noexcept;
    bool EndFrame(uint64_t,bool submit,FrameMode mode=FrameMode::Stereo,
                  float aspect=0,float distance=3.f,float width=3.f,bool overlay=false,bool video=false,bool follow=false) noexcept;
    bool Current(uint64_t) const noexcept;
    bool EyeCurrent(uint64_t,unsigned) const noexcept;
    bool Check(XrResult,const char*) noexcept;
    void Report(const char*,...) noexcept;
    void ReportRefreshRate(bool enabled) noexcept;
    void ReportOrientation(const char* reason) noexcept;
    Logger logger_{};
    DWORD ownerThread_{};
    XrInstance instance_{};
    XrSystemId system_{};
    XrSession session_{};
    XrSpace space_{};
    Microsoft::WRL::ComPtr<ID3D11Device> device_;
    std::array<Swapchain,5> eyes_; // [2]: optional shared UI panel; [3]: seat menu panel; [4]: FPS / GPU strip.
    bool overlayEnabled_{},seatPanelEnabled_{},seatShown_{},perfPanelEnabled_{},perfShown_{},perfFollow_{};
    XrPosef seatPose_{{0,0,0,1},{0,0,0}};
    XrExtent2Df seatSize_{};
    std::array<XrView,2> views_{{{XR_TYPE_VIEW},{XR_TYPE_VIEW}}};
    float fovWidth_=1.f,fovHeight_=1.f;
    XrPosef reference_{{0,0,0,1},{0,0,0}};
    XrTime displayTime_{};
    XrDuration displayPeriod_{};
    XrTime spaceChangeTime_{};
    DXGI_FORMAT format_=DXGI_FORMAT_UNKNOWN;
    uint64_t generation_{},lastGameSequence_{},submitted_{},screenSubmitted_{},referenceRevision_{};
    unsigned orientationReports_{},orientationModes_{};
    FrameMode frameMode_=FrameMode::Stereo;
    bool haveGameSequence_{},running_{},visible_{},focused_{},failed_{},exiting_{};
    bool frameOpen_{},frameRenderable_{},frameFailed_{},canRecenter_{},centered_{},recenterPending_=true,recenterTilt_{};
};
}
