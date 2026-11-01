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
        bool Submit() noexcept;
        bool SubmitOverlay(float aspect,float distance=2.f,float width=1.6f) noexcept;
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

    explicit XrRuntime(Logger logger=nullptr) noexcept : logger_(logger) {}
    ~XrRuntime();
    XrRuntime(const XrRuntime&) = delete;
    XrRuntime& operator=(const XrRuntime&) = delete;
    bool Initialize(ID3D11Device* device,float outputScale=1.f,uint32_t expectedWidth=0,uint32_t expectedHeight=0,bool overlay=false) noexcept;
    bool PollEvents() noexcept;
    Frame BeginFrame(uint64_t monotonicGameSequence) noexcept { return Frame(*this,monotonicGameSequence,FrameMode::Stereo); }
    Frame BeginScreenFrame(uint64_t monotonicGameSequence) noexcept { return Frame(*this,monotonicGameSequence,FrameMode::Screen); }
    void Shutdown() noexcept;
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
    bool InitializeResources(ID3D11Device*,float,uint32_t,uint32_t);
    bool StartFrame(uint64_t,FrameMode) noexcept;
    bool Acquire(uint64_t,unsigned) noexcept;
    bool Release(uint64_t,unsigned,bool completed) noexcept;
    bool EndFrame(uint64_t,bool submit,FrameMode mode=FrameMode::Stereo,
                  float aspect=0,float distance=3.f,float width=3.f,bool overlay=false,bool video=false) noexcept;
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
    std::array<Swapchain,3> eyes_; // Third image is an optional shared UI panel.
    bool overlayEnabled_{};
    std::array<XrView,2> views_{{{XR_TYPE_VIEW},{XR_TYPE_VIEW}}};
    XrPosef reference_{{0,0,0,1},{0,0,0}};
    XrTime displayTime_{};
    XrTime spaceChangeTime_{};
    DXGI_FORMAT format_=DXGI_FORMAT_UNKNOWN;
    uint64_t generation_{},lastGameSequence_{},submitted_{},screenSubmitted_{},referenceRevision_{};
    unsigned orientationReports_{},orientationModes_{};
    FrameMode frameMode_=FrameMode::Stereo;
    bool haveGameSequence_{},running_{},visible_{},focused_{},failed_{},exiting_{};
    bool frameOpen_{},frameRenderable_{},frameFailed_{},canRecenter_{},centered_{},recenterPending_=true,recenterTilt_{};
};
}
