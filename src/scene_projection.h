#pragma once
#include "scene_observer.h"
#include "scene_camera.h"
#include <openxr/openxr.h>
#include <array>

namespace dirt3vr {
// Call only after the production executable SHA-256 guard succeeds.
// Also requires DIRT3VR_XR_PROTOTYPE=1; desktop/replay diagnostics stay unchanged.
bool InstallSceneProjection(DiagnosticLogger logger) noexcept;
// Installation is inert until the caller finishes all presentation guards.
// Disable on XR stop/failure to restore native prepared visibility immediately.
void SetSceneProjectionActive(bool enabled) noexcept;
// Read-only refusal of both native stereo paths; never calls the vendor API.
bool NativeStereoDisabled(void* renderer) noexcept;

class ProjectionScope {
public:
    ProjectionScope(void* renderer, void* context, void* cameraA, void* cameraB, const XrFovf& fov) noexcept;
    ~ProjectionScope();
    ProjectionScope(const ProjectionScope&) = delete;
    ProjectionScope& operator=(const ProjectionScope&) = delete;
    unsigned Uploads() const noexcept { return uploads_; }
    bool Valid() const noexcept { return valid_; }
private:
    friend struct ProjectionHooks;
    ProjectionScope* previous_{};
    void* renderer_{};
    void* context_{};
    void* cameraA_{};
    void* cameraB_{};
    XrFovf fov_{};
    unsigned uploads_{};
    bool valid_{};
};

// Pure operations shared with the native contract tests. Outputs change only
// after all inputs and derived coefficients pass validation.
namespace projection_detail {
using Matrix = std::array<float, 16>;
bool ApplyProjection(Matrix& projection, const Matrix& view, Matrix& combined, const XrFovf& fov) noexcept;
bool VisibilityMatrix(const CameraRecord& a, const CameraRecord& b,
                      const std::array<float, 56>& nativeVolume, Matrix& matrix) noexcept;
// Same guarded reads as the runtime entry, with an explicit image for tests.
bool NativeStereoDisabledAt(void* renderer, uintptr_t imageBase) noexcept;
}
}
