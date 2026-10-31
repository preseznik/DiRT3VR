#pragma once
#include "scene_observer.h"
#include <array>
#include <cstddef>
#include <cstdint>

namespace dirt3vr {
enum class CameraKind { Unknown, Cockpit, External };
enum class StateKnowledge { Unknown, No, Yes };
enum class CameraReason {
    GuardUnavailable, RendererMismatch, MissingOwner, AlternateController,
    InvalidSelection, InvalidCamera, DisplayMismatch, IdentityChanged,
    UnsupportedView, CockpitIdentity, ExternalIdentity
};
enum class CameraComparisonLink { OwnerToSelected, SelectedToGlobal, GlobalToMainA, GlobalToMainB };
struct CameraFloatDifference {
    uint32_t offset{}, leftBits{}, rightBits{};
    float leftValue{}, rightValue{};
};
struct CameraComparison {
    CameraComparisonLink link{};
    unsigned count{};
    std::array<CameraFloatDifference, 15> differences{};
};
struct CameraClassification {
    CameraKind kind{CameraKind::Unknown};
    // Stereo follows the validated displayed 3D scene, independent of whether
    // its optional owner/name can be classified (replays and transitions too).
    bool stereoEligible{};
    CameraReason reason{CameraReason::GuardUnavailable};
    uintptr_t controller{}, manager{}, owner{};
    std::array<char, 64> name{};
    // Filled on DisplayMismatch, using the same snapshots as the unchanged
    // acceptance checks. Each link retains every differing pose/lens float.
    std::array<CameraComparison, 4> comparisons{{
        {CameraComparisonLink::OwnerToSelected}, {CameraComparisonLink::SelectedToGlobal},
        {CameraComparisonLink::GlobalToMainA}, {CameraComparisonLink::GlobalToMainB}}};
    // Camera identity does not establish gameplay state. These remain Unknown
    // until a separate, verified context observer exists.
    StateKnowledge gameplay{StateKnowledge::Unknown};
    StateKnowledge paused{StateKnowledge::Unknown};
    StateKnowledge replay{StateKnowledge::Unknown};
};
// Parent must first pass the production executable SHA-256 guard. This adds
// loaded-layout guards, never calls game functions and never installs hooks.
bool InitializeCameraClassifier(DiagnosticLogger logger) noexcept;
// Call at the main inner boundary, BEFORE applying eye poses. A Cockpit result
// proves a displayed Head identity only; replay/pause acceptance is still open.
CameraClassification ClassifyMainCamera(void* renderer, void* cameraA, void* cameraB) noexcept;
const char* CameraReasonName(CameraReason reason) noexcept;

namespace classifier_detail {
struct MismatchLogPermit { unsigned first{}, count{}; };
// One group per second, at most twelve detail lines for the process. The caller
// serializes access; ineligible cameras never reserve this diagnostic budget.
class MismatchLogBudget {
public:
    MismatchLogPermit Reserve(uint64_t now, unsigned requested) noexcept {
        if(!requested || used_ >= 12 || (sampled_ && (now < last_ || now - last_ < 1000))) return {};
        const auto count = requested < 12 - used_ ? requested : 12 - used_;
        const auto first = used_ + 1;
        used_ += count; last_ = now; sampled_ = true;
        return {first, count};
    }
private:
    uint64_t last_{};
    unsigned used_{};
    bool sampled_{};
};
// Test seam for bounded, read-only synthetic address spaces.
using ReadMemory = bool (*)(uintptr_t address, void* output, size_t length, void* context) noexcept;
bool VerifyLayouts(uintptr_t imageBase, ReadMemory read, void* context) noexcept;
CameraClassification Inspect(uintptr_t imageBase, uintptr_t renderer, uintptr_t cameraA,
                             uintptr_t cameraB, ReadMemory read, void* context) noexcept;
}
}
