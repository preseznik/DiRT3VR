#pragma once
#include "frontend_shadow.h"
#include "scene_observer.h"

namespace dirt3vr {
// The executable fingerprint is checked by the caller before installation.
bool InstallFrontendShadowState(DiagnosticLogger logger) noexcept;
void EndFrontendShadowFrame() noexcept;

// Consumes only the frontend producer observed earlier in this same frame.
// An absent/refused producer simply leaves native rendering unchanged.
class FrontendShadowScene {
public:
    FrontendShadowScene(void* renderer,void* context) noexcept;
    ~FrontendShadowScene();
    FrontendShadowScene(const FrontendShadowScene&)=delete;
    FrontendShadowScene& operator=(const FrontendShadowScene&)=delete;
private:
    bool owns_{};
};
bool FrontendShadowMatrices(FrontendShadowMatrix& expected,FrontendShadowMatrix& corrected) noexcept;

namespace frontend_shadow_detail {
// Row-vector coordinates: M_eye = inverseView_eye * inverse(inverseView_center) * M_center.
// The matrix is camera-relative texture placement, not an eye projection.
bool Correct(const FrontendShadowMatrix& centerInverse,const FrontendShadowMatrix& eyeInverse,
             const FrontendShadowMatrix& original,FrontendShadowMatrix& output) noexcept;
}
}
