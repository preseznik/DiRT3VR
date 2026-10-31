#pragma once
#include "hud_capture.h"
#include "scene_observer.h"

namespace dirt3vr {
// Called only after executable fingerprint and interactive-VR checks.
bool InstallHudHooks(ID3D11Device* device,DiagnosticLogger log) noexcept;
bool HudHooksReady() noexcept;
HudShaderKind QueryHudShader(ID3D11VertexShader* shader) noexcept;
void BindPresentationBackbuffer(ID3D11Texture2D* back) noexcept;
void TraceBeforePresent() noexcept;
bool IntroVideoDrawn() noexcept;
unsigned TransitionDrawsSuppressed() noexcept;
void EndPresentationFrame() noexcept;
void ResetFrontendShadowResources() noexcept;
class HudDrawScope {
public:
    HudDrawScope(HudCapture& capture,unsigned eye) noexcept;
    ~HudDrawScope();
    HudDrawScope(const HudDrawScope&)=delete;
    HudDrawScope& operator=(const HudDrawScope&)=delete;
private:
    HudCapture* previous_{};
    unsigned previousEye_{};
};
}
