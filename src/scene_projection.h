#pragma once
#include "scene_observer.h"
#include "scene_camera.h"
#include "light_replay.h"
#include <openxr/openxr.h>
#include <array>

namespace dirt3vr {
// Call only after the production executable SHA-256 guard succeeds.
// Also requires DIRT3VR_XR_PROTOTYPE=1; desktop/replay diagnostics stay unchanged.
// presentSequence keys each frame's recorded light builder calls (#82c61759).
bool InstallSceneProjection(DiagnosticLogger logger, PresentSequenceReader presentSequence) noexcept;
// Installation is inert until the caller finishes all presentation guards.
// Disable on XR stop/failure to restore native prepared visibility immediately.
void SetSceneProjectionActive(bool enabled) noexcept;
// Read-only refusal of both native stereo paths; never calls the vendor API.
bool NativeStereoDisabled(void* renderer) noexcept;

// Per-eye local lights (#82c61759): this frame's light builder calls for the main context, or false when
// the hooks are off or the set overflowed (the lights then keep the original camera's values).
bool SnapshotSceneLights(void* context, std::vector<LightCall>& calls) noexcept;
// The context bytes the builders read: inverse view at +70 and view at +150.
constexpr size_t LightContextBytes = 0x1d0;
// Runs the recorded builders again with this context (after both eyes, the original camera's copy).
void ReplaySceneLights(const std::vector<LightCall>& calls, void* context) noexcept;

class ProjectionScope {
public:
    // lights: rerun once, right after this eye's first camera upload into the context.
    ProjectionScope(void* renderer, void* context, void* cameraA, void* cameraB, const XrFovf& fov,
                    const std::vector<LightCall>* lights = nullptr) noexcept;
    ~ProjectionScope();
    ProjectionScope(const ProjectionScope&) = delete;
    ProjectionScope& operator=(const ProjectionScope&) = delete;
    unsigned Uploads() const noexcept { return uploads_; }
    bool LightsRefreshed() const noexcept { return lightsRefreshed_; }
    bool Valid() const noexcept { return valid_; }
private:
    friend struct ProjectionHooks;
    ProjectionScope* previous_{};
    void* renderer_{};
    void* context_{};
    void* cameraA_{};
    void* cameraB_{};
    XrFovf fov_{};
    const std::vector<LightCall>* lights_{};
    unsigned uploads_{};
    bool valid_{};
    bool lightsRefreshed_{};
};

// Pure operations shared with the native contract tests. Outputs change only
// after all inputs and derived coefficients pass validation.
namespace projection_detail {
using Matrix = std::array<float, 16>;
bool ApplyProjection(Matrix& projection, const Matrix& view, Matrix& combined, const XrFovf& fov) noexcept;
bool VisibilityMatrix(const CameraRecord& a, const CameraRecord& b,
                      const std::array<float, 56>& nativeVolume, Matrix& matrix) noexcept;
// Tree volumes use each tree type's shorter cull distance as their far plane;
// only their own corners set the box range, never the camera far value.
bool TreeVisibilityMatrix(const CameraRecord& a, const CameraRecord& b,
                          const std::array<float, 56>& nativeVolume, Matrix& matrix) noexcept;
// Same guarded reads as the runtime entry, with an explicit image for tests.
bool NativeStereoDisabledAt(void* renderer, uintptr_t imageBase) noexcept;
}
}
