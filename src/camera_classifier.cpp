#include "camera_classifier.h"
#include "scene_camera.h"
#include <windows.h>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <limits>

namespace dirt3vr {
namespace {
constexpr uintptr_t RendererTableRva = 0xaac84c, CameraTableRva = 0xa85b50;
constexpr uintptr_t HeadTableRva = 0xb28530, HeadTypeRva = 0x707ad0;
constexpr uintptr_t RootRva = 0xbe7404, GlobalCameraRva = 0xbf7e50;
constexpr uintptr_t GetterRva = 0x5806d0, EntryRva = 0x575b50, SelectorRva = 0x703ba0;
constexpr unsigned char GetterBytes[] = {0x55,0x8b,0xec,0x8b,0x45,0x08,0x50,0xe8,0x74,0x54,0xff,0xff,0x8b,0x00,0x5d,0xc2};
constexpr unsigned char EntryBytes[] = {0x55,0x8b,0xec,0x8b,0x51,0x04,0x56,0x8b,0xb1,0x40,0x01,0x00,0x00,0x57,0x8b,0x39};
constexpr unsigned char SelectorBytes[] = {0x8a,0x41,0x11,0x84,0xc0,0x75,0x16,0x38,0x41,0x12,0x74,0x07,0x8d,0x81,0xc0,0x00};
std::atomic<uintptr_t> initializedBase{};
DiagnosticLogger logMessage{};
SRWLOCK mismatchLock = SRWLOCK_INIT;
classifier_detail::MismatchLogBudget mismatchBudget;

struct Reader {
    classifier_detail::ReadMemory read;
    void* context;
    bool Copy(uintptr_t address, void* output, size_t length) const noexcept {
        return address && length && address <= UINT32_MAX && length - 1 <= UINT32_MAX - address &&
            read && read(address, output, length, context);
    }
    template<typename T> bool Value(uintptr_t address, T& result) const noexcept { return Copy(address, &result, sizeof(result)); }
    bool Pointer(uintptr_t address, uintptr_t& result) const noexcept {
        uint32_t value{};
        if(!Value(address, value) || !value) return false;
        result = value;
        return true;
    }
    bool PointerIs(uintptr_t address, uintptr_t expected) const noexcept {
        uintptr_t actual{}; return Pointer(address, actual) && actual == expected;
    }
    bool Bytes(uintptr_t address, const unsigned char* bytes, size_t length) const noexcept {
        unsigned char actual[16]{};
        return length <= sizeof(actual) && Copy(address, actual, length) && !std::memcmp(actual, bytes, length);
    }
    bool Call(uintptr_t address, uintptr_t target) const noexcept {
        unsigned char code[5]{}; int32_t displacement{};
        if(!Copy(address, code, sizeof(code)) || code[0] != 0xe8) return false;
        std::memcpy(&displacement, code + 1, sizeof(displacement));
        return int64_t(address) + 5 + displacement == int64_t(target);
    }
};
bool SafeRead(uintptr_t address, void* output, size_t length, void*) noexcept {
    MEMORY_BASIC_INFORMATION region{};
    const auto* source = reinterpret_cast<const void*>(address);
    if(!VirtualQuery(source, &region, sizeof(region)) || region.State != MEM_COMMIT ||
       (region.Protect & (PAGE_NOACCESS | PAGE_GUARD))) return false;
    const auto begin = reinterpret_cast<uintptr_t>(region.BaseAddress);
    if(address < begin || address - begin > region.RegionSize || length > region.RegionSize - (address - begin)) return false;
    __try { std::memcpy(output, source, length); return true; }
    __except(EXCEPTION_EXECUTE_HANDLER) { return false; }
}
bool ValidCamera(const CameraRecord& camera) noexcept {
    const float fov = CameraFloat(camera, 0x50), nearPlane = CameraFloat(camera, 0x54), farPlane = CameraFloat(camera, 0x58);
    return ValidReplayCamera(camera) && std::isfinite(fov) && fov > 0 &&
        std::isfinite(nearPlane) && nearPlane > 0 && std::isfinite(farPlane) && farPlane > nearPlane;
}
bool SameCamera(const CameraRecord& a, const CameraRecord& b) noexcept {
    // Padding/W, history flags and vtable are not pose/lens identity. Compare
    // all meaningful copied pose components and the three lens values exactly.
    for(size_t at : {0x10u, 0x20u, 0x30u, 0x40u, 0x50u})
        if(std::memcmp(a.data() + at, b.data() + at, 12)) return false;
    return true;
}
bool SameOwnerView(const CameraRecord& owner, const CameraRecord& selected) noexcept {
    // PID 31784: head-cam's source far=2000 becomes far=1000 in the selected,
    // global and both main records, with every other compared float identical.
    // Far clipping is render policy, not owner identity. Keep displayed copies
    // fully exact; ignore only this source-to-selected field for the label.
    for(size_t at : {0x10u, 0x20u, 0x30u, 0x40u})
        if(std::memcmp(owner.data() + at, selected.data() + at, 12)) return false;
    return !std::memcmp(owner.data() + 0x50, selected.data() + 0x50, 8);
}
CameraComparison CompareCamera(const CameraRecord& left, const CameraRecord& right, CameraComparisonLink link) noexcept {
    CameraComparison result{link};
    for(size_t block : {0x10u, 0x20u, 0x30u, 0x40u, 0x50u}) for(unsigned axis = 0; axis < 3; ++axis) {
        const auto offset = block + axis*4;
        uint32_t leftBits{}, rightBits{};
        std::memcpy(&leftBits, left.data() + offset, sizeof(leftBits));
        std::memcpy(&rightBits, right.data() + offset, sizeof(rightBits));
        if(leftBits == rightBits) continue;
        auto& difference = result.differences[result.count++];
        difference.offset = uint32_t(offset);
        difference.leftBits = leftBits; difference.rightBits = rightBits;
        std::memcpy(&difference.leftValue, &leftBits, sizeof(float));
        std::memcpy(&difference.rightValue, &rightBits, sizeof(float));
    }
    return result;
}
const char* ComparisonName(CameraComparisonLink link) noexcept {
    switch(link) {
    case CameraComparisonLink::OwnerToSelected: return "owner-selected";
    case CameraComparisonLink::SelectedToGlobal: return "selected-global";
    case CameraComparisonLink::GlobalToMainA: return "global-mainA";
    case CameraComparisonLink::GlobalToMainB: return "global-mainB";
    }
    return "unknown";
}
bool ReadName(const Reader& memory, uintptr_t object, std::array<char, 64>& name) noexcept {
    for(size_t i = 0; i < name.size(); ++i) {
        unsigned char value{};
        if(!memory.Value(object + 0xa1 + i, value)) return false;
        if(!value) return i != 0;
        if(value < 0x20 || value > 0x7e || i + 1 == name.size()) return false;
        name[i] = char(value);
    }
    return false;
}
bool EffectiveOwner(const Reader& memory, uintptr_t manager, uintptr_t& owner) noexcept {
    uint32_t begin{}, end{}, index{}; unsigned char reverse{};
    if(!memory.Value(manager, begin) || !memory.Value(manager + 4, end) || !begin || end < begin ||
       (end - begin)%0x280 || (end - begin)/0x280 > 1024 ||
       !memory.Value(manager + 0x140, index) || !memory.Value(manager + 0x139, reverse)) return false;
    const uint32_t count = (end - begin)/0x280;
    // The native getter has an inline fallback. Its provenance is unknown, so
    // an invalid selected index deliberately stays on the screen path.
    if(index >= count || !memory.Pointer(uintptr_t(begin) + index*0x280, owner)) return false;
    if(reverse) {
        uint32_t hasReverse{}, reverseIndex{};
        if(!memory.Value(owner + 0x118, hasReverse)) return false;
        if(hasReverse) {
            if(!memory.Value(owner + 0x13c, reverseIndex) || reverseIndex >= count ||
               !memory.Pointer(uintptr_t(begin) + reverseIndex*0x280, owner)) return false;
        }
    }
    return true;
}
bool OwnerChain(const Reader& memory, uintptr_t base, uintptr_t& controller, uintptr_t& manager) noexcept {
    uintptr_t root{}, game{};
    return memory.Pointer(base + RootRva, root) && memory.Pointer(root + 8, game) &&
        memory.Pointer(game + 0x11b4, controller) && memory.Pointer(controller + 0x24, manager);
}
bool PrimaryController(const Reader& memory, uintptr_t controller) noexcept {
    unsigned char flags[2]{};
    return memory.Copy(controller + 0x11, flags, sizeof(flags)) && !flags[0] && !flags[1];
}
void ReportChanged(const CameraClassification& result) noexcept {
    thread_local bool seen{};
    thread_local CameraClassification last;
    const bool changed = !seen || last.stereoEligible != result.stereoEligible || last.kind != result.kind || last.reason != result.reason || last.owner != result.owner ||
        last.controller != result.controller || last.manager != result.manager || last.name != result.name;
    if(changed) { last = result; seen = true; }
    try {
        if(changed && logMessage) logMessage("Camera identity stereo_eligible=%u kind=%u reason=%s controller=%p manager=%p owner=%p name=%s gameplay=unknown paused=unknown replay=unknown",
            unsigned(result.stereoEligible), unsigned(result.kind), CameraReasonName(result.reason), reinterpret_cast<void*>(result.controller),
            reinterpret_cast<void*>(result.manager), reinterpret_cast<void*>(result.owner), result.name.data());
        // Resample a persistent cockpit refusal so the settled pose is visible.
        // Moving frontend/replay cameras cannot consume the process-wide budget.
        if(logMessage && result.reason == CameraReason::DisplayMismatch && !std::strcmp(result.name.data(), "head-cam")) {
            unsigned requested{};
            for(const auto& comparison : result.comparisons) if(comparison.count) ++requested;
            const auto now = GetTickCount64();
            AcquireSRWLockExclusive(&mismatchLock);
            const auto permit = mismatchBudget.Reserve(now, requested);
            ReleaseSRWLockExclusive(&mismatchLock);
            unsigned emitted{};
            for(const auto& comparison : result.comparisons) {
                if(emitted >= permit.count) break;
                if(!comparison.count) continue;
                char fields[2048]{};
                size_t used{};
                for(unsigned i = 0; i < comparison.count; ++i) {
                    const auto& difference = comparison.differences[i];
                    const int written = std::snprintf(fields + used, sizeof(fields) - used,
                        "%s0x%02x:%08x/%08x:%.9g/%.9g", i ? ";" : "", difference.offset,
                        difference.leftBits, difference.rightBits, double(difference.leftValue), double(difference.rightValue));
                    if(written < 0 || size_t(written) >= sizeof(fields) - used) break;
                    used += size_t(written);
                }
                logMessage("Camera mismatch detail=%u sample_ms=%llu name=head-cam link=%s count=%u fields[offset:left_bits/right_bits:left/right]=[%s]",
                    permit.first + emitted++, now, ComparisonName(comparison.link), comparison.count, fields);
            }
        }
    } catch(...) { }
}
}

namespace classifier_detail {
bool VerifyLayouts(uintptr_t base, ReadMemory read, void* context) noexcept {
    const Reader memory{read, context};
    const unsigned char loadAddress[] = {0x8b,0x0d};
    const unsigned char loadTypePointer[] = {0xa1}, returnInstruction[] = {0xc3};
    return memory.Bytes(base + GetterRva, GetterBytes, sizeof(GetterBytes)) &&
        memory.Bytes(base + EntryRva, EntryBytes, sizeof(EntryBytes)) &&
        memory.Bytes(base + SelectorRva, SelectorBytes, sizeof(SelectorBytes)) &&
        memory.Call(base + 0x5806d7, base + EntryRva) &&
        memory.Call(base + 0x176f4e, base + 0x7c260) && memory.Call(base + 0x176f5e, base + 0x7c260) &&
        memory.PointerIs(base + 0x176f44, base + GlobalCameraRva) && memory.PointerIs(base + 0x176f54, base + GlobalCameraRva) &&
        memory.Bytes(base + 0x1aecec, loadAddress, sizeof(loadAddress)) && memory.PointerIs(base + 0x1aecee, base + RootRva) &&
        memory.Call(base + 0x1aecff, base + SelectorRva) && memory.Call(base + 0x1aed11, base + 0x7c260) &&
        memory.PointerIs(base + HeadTableRva + 0x30, base + HeadTypeRva) &&
        memory.Bytes(base + HeadTypeRva, loadTypePointer, sizeof(loadTypePointer)) &&
        memory.PointerIs(base + HeadTypeRva + 1, base + 0xbd8ad8) &&
        memory.PointerIs(base + 0xbd8ad8, base + 0xb285cc) &&
        memory.Bytes(base + HeadTypeRva + 5, returnInstruction, sizeof(returnInstruction));
}
CameraClassification Inspect(uintptr_t base, uintptr_t renderer, uintptr_t a, uintptr_t b,
                             ReadMemory read, void* context) noexcept {
    CameraClassification result;
    const Reader memory{read, context};
    result.reason = CameraReason::RendererMismatch;
    if(!memory.PointerIs(renderer, base + RendererTableRva) || a != renderer + 0x600 || b != renderer + 0x690 ||
       !memory.PointerIs(a, base + CameraTableRva) || !memory.PointerIs(b, base + CameraTableRva)) return result;
    CameraRecord source{}, selected{}, global{}, cameraA{}, cameraB{};
    result.reason = CameraReason::InvalidCamera;
    if(!memory.Copy(base + GlobalCameraRva, global.data(), global.size()) ||
       !memory.Copy(a, cameraA.data(), cameraA.size()) || !memory.Copy(b, cameraB.data(), cameraB.size()) ||
       !ValidCamera(global) || !ValidCamera(cameraA) || !ValidCamera(cameraB)) return result;
    // Owner metadata is not a rendering prerequisite. The exact main boundary,
    // record identities and finite, matching displayed cameras are prerequisites.
    result.stereoEligible = SameCamera(global, cameraA) && SameCamera(global, cameraB);
    result.reason = CameraReason::MissingOwner;
    if(!OwnerChain(memory, base, result.controller, result.manager)) return result;
    result.reason = CameraReason::AlternateController;
    if(!PrimaryController(memory, result.controller)) return result;
    result.reason = CameraReason::InvalidSelection;
    if(!EffectiveOwner(memory, result.manager, result.owner) || !ReadName(memory, result.owner, result.name)) return result;
    result.reason = CameraReason::InvalidCamera;
    if(!memory.Copy(result.owner, source.data(), source.size()) ||
       !memory.Copy(result.controller + 0x150, selected.data(), selected.size()) ||
       !ValidCamera(source) || !ValidCamera(selected)) return result;
    result.reason = CameraReason::DisplayMismatch;
    if(!SameOwnerView(source, selected) || !SameCamera(selected, global) || !result.stereoEligible) {
        result.comparisons = {{CompareCamera(source, selected, CameraComparisonLink::OwnerToSelected),
            CompareCamera(selected, global, CameraComparisonLink::SelectedToGlobal),
            CompareCamera(global, cameraA, CameraComparisonLink::GlobalToMainA),
            CompareCamera(global, cameraB, CameraComparisonLink::GlobalToMainB)}};
        return result;
    }
    // Reject changed ownership or source data during the snapshot. No cached
    // positive classification survives across a transition or a failed read.
    uintptr_t againController{}, againManager{}, againOwner{};
    CameraRecord againSource{};
    result.reason = CameraReason::IdentityChanged;
    if(!OwnerChain(memory, base, againController, againManager) || againController != result.controller ||
       againManager != result.manager || !PrimaryController(memory, againController) ||
       !EffectiveOwner(memory, againManager, againOwner) || againOwner != result.owner ||
       !memory.Copy(againOwner, againSource.data(), againSource.size()) || !SameCamera(source, againSource)) return result;
    result.reason = CameraReason::UnsupportedView;
    if(!std::strcmp(result.name.data(), "head-cam")) {
        float configuredNear{}; unsigned char interior{}, headDisabled{};
        if(!memory.PointerIs(result.owner, base + HeadTableRva) ||
           !memory.Value(result.owner + 0x9c, configuredNear) || !std::isfinite(configuredNear) ||
           std::abs(configuredNear - .075f) > .000001f || std::abs(CameraFloat(source, 0x54) - .075f) > .000001f ||
           !memory.Value(result.owner + 0x1a4f, interior) || !interior ||
           !memory.Value(result.owner + 0x1a50, headDisabled) || !headDisabled) return result;
        result.kind = CameraKind::Cockpit; result.reason = CameraReason::CockpitIdentity;
    } else if(!std::strcmp(result.name.data(), "chase_close") || !std::strcmp(result.name.data(), "chase_far") ||
              !std::strcmp(result.name.data(), "bonnet") || !std::strcmp(result.name.data(), "bumper")) {
        // This is an optional label, not proof of native class or driving state.
        result.kind = CameraKind::External; result.reason = CameraReason::ExternalIdentity;
    }
    return result;
}
}

bool InitializeCameraClassifier(DiagnosticLogger logger) noexcept {
    logMessage = logger;
    const auto base = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    const bool valid = base && classifier_detail::VerifyLayouts(base, SafeRead, nullptr);
    initializedBase.store(valid ? base : 0, std::memory_order_release);
    try { if(logger) logger("Camera classifier read-only layout guards=%u gameplay/replay/pause=unverified", unsigned(valid)); } catch(...) { }
    return valid;
}
CameraClassification ClassifyMainCamera(void* renderer, void* cameraA, void* cameraB) noexcept {
    CameraClassification result;
    if(const auto base = initializedBase.load(std::memory_order_acquire))
        result = classifier_detail::Inspect(base, reinterpret_cast<uintptr_t>(renderer),
            reinterpret_cast<uintptr_t>(cameraA), reinterpret_cast<uintptr_t>(cameraB), SafeRead, nullptr);
    ReportChanged(result);
    return result;
}
const char* CameraReasonName(CameraReason reason) noexcept {
    switch(reason) {
    case CameraReason::GuardUnavailable: return "guard-unavailable";
    case CameraReason::RendererMismatch: return "renderer-mismatch";
    case CameraReason::MissingOwner: return "missing-owner";
    case CameraReason::AlternateController: return "alternate-controller";
    case CameraReason::InvalidSelection: return "invalid-selection";
    case CameraReason::InvalidCamera: return "invalid-camera";
    case CameraReason::DisplayMismatch: return "display-mismatch";
    case CameraReason::IdentityChanged: return "identity-changed";
    case CameraReason::UnsupportedView: return "unsupported-view";
    case CameraReason::CockpitIdentity: return "cockpit-identity";
    case CameraReason::ExternalIdentity: return "external-identity";
    }
    return "unknown";
}
}
