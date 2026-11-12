#include "scene_projection.h"
#include "vr_math.h"
#include <windows.h>
#include <intrin.h>
#include <MinHook.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <iterator>
#include <mutex>

namespace dirt3vr {
namespace {
constexpr uintptr_t SetupRva = 0x57a9b0, UploadRva = 0x4a81f0;
constexpr uintptr_t CopyRva = 0x174c10, BuildRva = 0x3c55d0;
constexpr uintptr_t RendererTableRva = 0xaac84c, CameraTableRva = 0xa85b50;
constexpr uintptr_t ContextTableRva = 0xb075e8, DeviceTableRva = 0xb06ab8;
constexpr uintptr_t StereoGetterRva = 0x4a8ca0;
// The tree system culls with its own frustum, built from the camera's native FOV
// (docs/research/asymmetric-projection.md, "Tree visibility"). Main preparation
// calls it at 0x177509; its three frustum copies return to these addresses.
constexpr uintptr_t TreeCullRva = 0x768ed0, TreeCullReturnRva = 0x17750e;
constexpr uintptr_t TreeCopyReturnRvas[] = {0x7690e7, 0x76939d, 0x7695b5};
constexpr unsigned char AlignedEntry[] = {0x53,0x8b,0xdc,0x83,0xec,0x08,0x83,0xe4,0xf0,0x83,0xc4,0x04,0x55,0x8b,0x6b,0x04};
constexpr unsigned char CopyEntry[] = {0x55,0x8b,0xec,0x8b,0xc1,0x8b,0x4d,0x08,0x0f,0x28,0x01,0x0f,0x29,0x00,0x0f,0x28};
constexpr unsigned char UploadCall[] = {0x8b,0x4f,0x28,0x8b,0x11,0x8b,0x82,0x48,0x01,0x00,0x00,0x56,0xff,0xd0};
// Light builders (#82c61759; docs/research/per-eye-lights.md): thiscall, point (light, context) ret 8,
// spot and projected spot (light, context, material, extra) ret 0x10. Each pushes its shader parameter
// names (lightPosVs 0xf32a48, lightDirVs 0xf32ba8, mViewToLightClipSpace 0xf32bb4 at the preferred base).
constexpr uintptr_t PointLightRva = 0x7d4b30, SpotLightRva = 0x7da0c0, ProjectedLightRva = 0x7da380;
struct LightGuard { uintptr_t rva; uintptr_t nameRva; };
constexpr LightGuard LightNames[] = {{0x7d4bc4, 0xb32a48}, {0x7da143, 0xb32a48}, {0x7da2b2, 0xb32ba8},
                                     {0x7da409, 0xb32a48}, {0x7da579, 0xb32ba8}, {0x7daf8d, 0xb32bb4}};
constexpr unsigned char Ret8[] = {0xc2,0x08,0x00}, Ret16[] = {0xc2,0x10,0x00};
constexpr unsigned char StereoGetterEntry[] = {0x55,0x8b,0xec,0x51,0x83,0xb9,0x60,0x4d,0x00,0x00,0x00,0x7e,0x06,0xb0,0x01,0x8b,0xe5,0x5d,0xc3,0x8b,0x81,0x78,0x4d,0x00,0x00,0x85,0xc0,0x74,0x1a,0x8d,0x4d,0xff,0x51,0x50,0xc6,0x45,0xff,0x00,0xe8,0xa5,0x6d,0x4f,0x00,0x83,0xc4,0x08,0x85,0xc0,0x75,0x05,0x38,0x45,0xff,0x75,0xd6,0x32,0xc0,0x8b,0xe5,0x5d,0xc3};
using SetupFn = void (__thiscall*)(void*, void*, void*, float, unsigned);
using UploadFn = void (__thiscall*)(void*, void*);
using FrustumFn = void* (__thiscall*)(void*, const void*);
using TreeCullFn = void (__thiscall*)(void*, uint32_t, uint32_t, void*, uint32_t, uint32_t, uint32_t, uint32_t);
using PointLightFn = void (__thiscall*)(void*, void*, void*);
using SpotLightFn = void (__thiscall*)(void*, void*, void*, void*, void*);
SetupFn realSetup{};
UploadFn realUpload{};
FrustumFn realCopy{}, buildFrustum{};
TreeCullFn realTreeCull{};
PointLightFn realPointLight{};
SpotLightFn realSpotLight{}, realProjectedLight{};
PresentSequenceReader presentSequence{};
PreparedLights preparedLights;
std::atomic_bool lightsInstalled{};
std::atomic_uint64_t lightReplays{}, lightOverflows{};
std::atomic<DWORD> lightThread{};
uintptr_t base{};
DiagnosticLogger logMessage{};
std::atomic_bool installed{}, active{};
std::atomic_uint64_t visibilityCount{}, visibilityRejected{}, treeCount{}, treeRejected{};
thread_local ProjectionScope* current{};
thread_local ProjectionScope* setupScope{};
// The main renderer whose visibility was last widened on this (render) thread.
// Tree volumes are widened only inside its own preparation's tree cull.
struct PreparedRenderer { void* renderer; void* context; };
thread_local PreparedRenderer prepared{};
thread_local const PreparedRenderer* treeScope{};

void Report(const char* text) noexcept {
    try { if(logMessage) logMessage("%s", text); } catch(...) { }
}
bool Range(const void* pointer, size_t length, bool writing = false) noexcept {
    MEMORY_BASIC_INFORMATION region{};
    if(!pointer || !VirtualQuery(pointer, &region, sizeof(region)) || region.State != MEM_COMMIT ||
       (region.Protect & (PAGE_NOACCESS | PAGE_GUARD))) return false;
    const auto start = reinterpret_cast<uintptr_t>(pointer);
    const auto end = reinterpret_cast<uintptr_t>(region.BaseAddress) + region.RegionSize;
    if(start > end || length > end - start) return false;
    if(writing && !(region.Protect & (PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY))) return false;
    return true;
}
bool Read(const void* source, void* target, size_t length) noexcept {
    if(!Range(source, length)) return false;
    __try { std::memcpy(target, source, length); return true; }
    __except(EXCEPTION_EXECUTE_HANDLER) { return false; }
}
bool IsPointer(const void* source, uintptr_t expected) noexcept {
    uintptr_t found{};
    return Read(source, &found, sizeof(found)) && found == expected;
}
bool BytesAt(uintptr_t address, const unsigned char* expected, size_t length) noexcept {
    unsigned char actual[64]{};
    return length <= sizeof(actual) && Read(reinterpret_cast<void*>(address), actual, length) && !std::memcmp(actual, expected, length);
}
bool Bytes(uintptr_t rva, const unsigned char* expected, size_t length) noexcept {
    return BytesAt(base + rva, expected, length);
}
bool Pushes(uintptr_t rva, uintptr_t nameRva) noexcept {
    const auto name = static_cast<uint32_t>(base + nameRva);
    const unsigned char push[] = {0x68, uint8_t(name), uint8_t(name >> 8), uint8_t(name >> 16), uint8_t(name >> 24)};
    return Bytes(rva, push, sizeof(push));
}
bool Call(uintptr_t rva, uintptr_t target) noexcept {
    unsigned char bytes[5]{};
    int32_t displacement{};
    if(!Read(reinterpret_cast<void*>(base + rva), bytes, sizeof(bytes)) || bytes[0] != 0xe8) return false;
    std::memcpy(&displacement, bytes + 1, sizeof(displacement));
    return rva + 5 + displacement == target;
}
bool Identity(void* renderer, void* context, void* a, void* b) noexcept {
    const auto address = reinterpret_cast<uintptr_t>(renderer);
    return IsPointer(renderer, base + RendererTableRva) &&
        reinterpret_cast<uintptr_t>(a) == address + 0x600 && reinterpret_cast<uintptr_t>(b) == address + 0x690 &&
        IsPointer(a, base + CameraTableRva) && IsPointer(b, base + CameraTableRva) &&
        IsPointer(reinterpret_cast<void*>(address + 0x5f4), reinterpret_cast<uintptr_t>(context)) &&
        IsPointer(context, base + ContextTableRva) && Range(context, 0x1d0, true);
}
struct RestoreSetup {
    ProjectionScope* previous;
    ~RestoreSetup() { setupScope = previous; }
};
struct RestoreTree {
    const PreparedRenderer* previous;
    ~RestoreTree() { treeScope = previous; }
};
bool TreeCopy(uintptr_t caller) noexcept {
    return std::find(std::begin(TreeCopyReturnRvas), std::end(TreeCopyReturnRvas), caller) != std::end(TreeCopyReturnRvas);
}
// The engine owns these matrices on the render thread. Only projection and its
// derived product change; inverse view at +70 and view at +150 stay untouched.
bool WriteMatrices(void* context, const projection_detail::Matrix& projection,
                   const projection_detail::Matrix& combined,
                   const projection_detail::Matrix& oldProjection,
                   const projection_detail::Matrix& oldCombined) noexcept {
    auto* bytes = static_cast<unsigned char*>(context);
    if(!Range(bytes + 0x110, 0xc0, true)) return false;
    __try {
        std::memcpy(bytes + 0x110, projection.data(), 64);
        std::memcpy(bytes + 0x190, combined.data(), 64);
        return true;
    } __except(EXCEPTION_EXECUTE_HANDLER) {
        __try {
            std::memcpy(bytes + 0x110, oldProjection.data(), 64);
            std::memcpy(bytes + 0x190, oldCombined.data(), 64);
        } __except(EXCEPTION_EXECUTE_HANDLER) { }
        return false;
    }
}
// One recorded builder call; a fault leaves that light as it was.
bool CallLight(const LightCall& call, void* context) noexcept {
    __try {
        if(call.kind == LightKind::Point) realPointLight(call.self, call.light, context);
        else if(call.kind == LightKind::Spot) realSpotLight(call.self, call.light, context, call.material, call.extra);
        else realProjectedLight(call.self, call.light, context, call.material, call.extra);
        return true;
    } __except(EXCEPTION_EXECUTE_HANDLER) { return false; }
}
void RememberLight(const LightCall& call) noexcept {
    if(!active.load(std::memory_order_acquire) || !presentSequence) return;
    try {
        preparedLights.Remember(presentSequence(), call);
        DWORD none{};
        if(lightThread.compare_exchange_strong(none, GetCurrentThreadId())) {
            char text[160]{};
            std::snprintf(text, sizeof(text), "XR lights: first builder call kind=%u thread=%lu; each eye reruns them after its camera upload",
                unsigned(call.kind), GetCurrentThreadId());
            Report(text);
        }
    } catch(...) { }
}
bool BuildVolume(void* output, const void* matrix) noexcept {
    __try { return buildFrustum(output, matrix) == output; }
    __except(EXCEPTION_EXECUTE_HANDLER) { return false; }
}
}

namespace projection_detail {
bool NativeStereoDisabledAt(void* renderer, uintptr_t imageBase) noexcept {
    void* device{};
    int32_t stereoCount{};
    uintptr_t vendorHandle{};
    return imageBase && IsPointer(renderer, imageBase + RendererTableRva) &&
        Read(static_cast<unsigned char*>(renderer) + 0x28, &device, sizeof(device)) &&
        IsPointer(device, imageBase + DeviceTableRva) &&
        IsPointer(reinterpret_cast<void*>(imageBase + DeviceTableRva + 0xd0), imageBase + StereoGetterRva) &&
        BytesAt(imageBase + StereoGetterRva, StereoGetterEntry, sizeof(StereoGetterEntry)) &&
        Read(static_cast<unsigned char*>(device) + 0x4d60, &stereoCount, sizeof(stereoCount)) && stereoCount <= 0 &&
        Read(static_cast<unsigned char*>(device) + 0x4d78, &vendorHandle, sizeof(vendorHandle)) && !vendorHandle;
}
bool ApplyProjection(Matrix& projection, const Matrix& view, Matrix& combined, const XrFovf& fov) noexcept {
    for(float value : projection) if(!std::isfinite(value)) return false;
    for(float value : view) if(!std::isfinite(value)) return false;
    auto replacement = projection;
    if(!math::ApplyFov(replacement.data(), fov)) return false;
    Matrix product{};
    for(unsigned row = 0; row < 4; ++row) for(unsigned column = 0; column < 4; ++column) {
        for(unsigned index = 0; index < 4; ++index)
            product[row*4 + column] += view[row*4 + index]*replacement[index*4 + column];
        if(!std::isfinite(product[row*4 + column])) return false;
    }
    projection = replacement; combined = product;
    return true;
}
namespace {
bool VisibilityBox(const CameraRecord& a, const CameraRecord& b, const std::array<float, 56>& nativeVolume,
                   bool cameraFar, Matrix& matrix) noexcept {
    if(!ValidReplayCamera(a) || !ValidReplayCamera(b)) return false;
    const float farA = CameraFloat(a, 0x58), farB = CameraFloat(b, 0x58);
    if(!std::isfinite(farA) || !std::isfinite(farB) || farA < 25 || farA > 5000 || farB < 25 || farB > 5000) return false;
    double range = cameraFar ? std::max(farA, farB) : 0.;
    std::array<double, 3> center{};
    for(unsigned axis = 0; axis < 3; ++axis)
        center[axis] = (double(CameraFloat(a, 0x40 + axis*4)) + CameraFloat(b, 0x40 + axis*4))*.5;
    for(unsigned at = 24; at < 56; at += 4) {
        const double radius = std::hypot(double(nativeVolume[at]) - center[0],
            double(nativeVolume[at + 1]) - center[1], double(nativeVolume[at + 2]) - center[2]);
        if(!std::isfinite(radius) || radius > 100000.) return false;
        range = std::max(range, radius);
    }
    range += 8.;
    Matrix result{};
    for(unsigned axis = 0; axis < 3; ++axis) {
        const double extent = range + std::abs(double(CameraFloat(a, 0x40 + axis*4)) - CameraFloat(b, 0x40 + axis*4))*.5;
        const double scale = (axis == 2 ? -1. : 1.)/extent;
        result[axis*5] = float(scale); result[12 + axis] = float(-center[axis]*scale);
    }
    result[15] = 1;
    for(float value : result) if(!std::isfinite(value)) return false;
    matrix = result;
    return true;
}
}
bool VisibilityMatrix(const CameraRecord& a, const CameraRecord& b,
                      const std::array<float, 56>& nativeVolume, Matrix& matrix) noexcept {
    return VisibilityBox(a, b, nativeVolume, true, matrix);
}
bool TreeVisibilityMatrix(const CameraRecord& a, const CameraRecord& b,
                          const std::array<float, 56>& nativeVolume, Matrix& matrix) noexcept {
    return VisibilityBox(a, b, nativeVolume, false, matrix);
}
}

struct ProjectionHooks {
    static void __fastcall Setup(void* self, void*, void* context, void* camera, float farPlane, unsigned mode) {
        RestoreSetup restore{setupScope};
        setupScope = current && current->valid_ && self == current->renderer_ && context == current->context_ &&
            (camera == current->cameraA_ || camera == current->cameraB_) ? current : nullptr;
        realSetup(self, context, camera, farPlane, mode);
    }
    static void __fastcall Upload(void* self, void*, void* context) {
        const auto caller = reinterpret_cast<uintptr_t>(_ReturnAddress()) - base;
        auto* scope = setupScope;
        if(scope && scope->valid_ && active.load(std::memory_order_acquire) && context == scope->context_ && caller == 0x57b63f) {
            void* expectedDevice{};
            projection_detail::Matrix projection{}, view{}, combined{};
            const auto* bytes = static_cast<const unsigned char*>(context);
            const bool read = Read(static_cast<unsigned char*>(scope->renderer_) + 0x28, &expectedDevice, sizeof(expectedDevice)) &&
                self == expectedDevice && IsPointer(self, base + DeviceTableRva) && IsPointer(context, base + ContextTableRva) &&
                Read(bytes + 0x110, projection.data(), 64) && Read(bytes + 0x150, view.data(), 64) && Read(bytes + 0x190, combined.data(), 64);
            const auto oldProjection = projection, oldCombined = combined;
            if(read && projection_detail::ApplyProjection(projection, view, combined, scope->fov_) &&
               WriteMatrices(context, projection, combined, oldProjection, oldCombined)) ++scope->uploads_;
            else { scope->valid_ = false; Report("XR projection rejected; original camera retained"); }
        }
        realUpload(self, context);
        // The context now holds this eye's view (+150) and inverse view (+70): rebuild the local lights
        // from it once, before the eye draws anything (#82c61759).
        if(scope && scope->valid_ && scope->uploads_ && scope->lights_ && !scope->lightsRefreshed_ && context == scope->context_) {
            scope->lightsRefreshed_ = true;
            ReplaySceneLights(*scope->lights_, context);
        }
    }
    static void __fastcall PointLight(void* self, void*, void* light, void* context) {
        RememberLight({LightKind::Point, self, light, context, nullptr, nullptr});
        realPointLight(self, light, context);
    }
    static void __fastcall SpotLight(void* self, void*, void* light, void* context, void* material, void* extra) {
        RememberLight({LightKind::Spot, self, light, context, material, extra});
        realSpotLight(self, light, context, material, extra);
    }
    static void __fastcall ProjectedLight(void* self, void*, void* light, void* context, void* material, void* extra) {
        RememberLight({LightKind::Projected, self, light, context, material, extra});
        realProjectedLight(self, light, context, material, extra);
    }
    // Main preparation culls trees once per frame against the game camera's own
    // FOV and direction. Mark that call so its tree volumes can be widened too;
    // shadow and reflection tree culls keep their native volumes.
    static void __fastcall TreeCull(void* self, void*, uint32_t a1, uint32_t a2, void* context, uint32_t a4, uint32_t a5, uint32_t a6, uint32_t a7) {
        const auto caller = reinterpret_cast<uintptr_t>(_ReturnAddress()) - base;
        RestoreTree restore{treeScope};
        treeScope = active.load(std::memory_order_acquire) && caller == TreeCullReturnRva &&
            prepared.context && context == prepared.context ? &prepared : nullptr;
        realTreeCull(self, a1, a2, context, a4, a5, a6, a7);
    }
    static void* __fastcall Copy(void* self, void*, const void* source) {
        const auto caller = reinterpret_cast<uintptr_t>(_ReturnAddress()) - base;
        if(active.load(std::memory_order_acquire) && treeScope && TreeCopy(caller)) {
            const auto renderer = reinterpret_cast<uintptr_t>(treeScope->renderer);
            CameraRecord a{}, b{};
            std::array<float, 56> original{};
            alignas(16) std::array<float, 56> volume{};
            alignas(16) projection_detail::Matrix matrix{};
            if(Identity(treeScope->renderer, treeScope->context, reinterpret_cast<void*>(renderer + 0x600), reinterpret_cast<void*>(renderer + 0x690)) &&
               Read(reinterpret_cast<void*>(renderer + 0x600), a.data(), a.size()) &&
               Read(reinterpret_cast<void*>(renderer + 0x690), b.data(), b.size()) && Read(source, original.data(), sizeof(original)) &&
               projection_detail::TreeVisibilityMatrix(a, b, original, matrix) && BuildVolume(volume.data(), matrix.data()) &&
               std::all_of(volume.begin(), volume.end(), [](float value) { return std::isfinite(value); })) {
                if(treeCount.fetch_add(1) == 0) Report("XR tree visibility expanded before preparation; tree cull distances and LOD retained");
                return realCopy(self, volume.data());
            }
            if(treeRejected.fetch_add(1) == 0) Report("XR tree visibility expansion rejected; original tree volume retained");
        }
        if(active.load(std::memory_order_acquire) && caller == 0x18093f) {
            prepared = {};
            const auto renderer = reinterpret_cast<uintptr_t>(self) - 0x350;
            void* context{};
            CameraRecord a{}, b{};
            std::array<float, 56> original{};
            alignas(16) std::array<float, 56> volume{};
            alignas(16) projection_detail::Matrix matrix{};
            if(Read(reinterpret_cast<void*>(renderer + 0x5f4), &context, sizeof(context)) &&
               Identity(reinterpret_cast<void*>(renderer), context, reinterpret_cast<void*>(renderer + 0x600), reinterpret_cast<void*>(renderer + 0x690)) &&
               Read(reinterpret_cast<void*>(renderer + 0x600), a.data(), a.size()) &&
               Read(reinterpret_cast<void*>(renderer + 0x690), b.data(), b.size()) && Read(source, original.data(), sizeof(original)) &&
               projection_detail::VisibilityMatrix(a, b, original, matrix) && BuildVolume(volume.data(), matrix.data()) &&
               std::all_of(volume.begin(), volume.end(), [](float value) { return std::isfinite(value); })) {
                if(visibilityCount.fetch_add(1) == 0) Report("XR main visibility expanded before preparation; native distance and LOD retained");
                prepared = {reinterpret_cast<void*>(renderer), context};
                return realCopy(self, volume.data());
            }
            if(visibilityRejected.fetch_add(1) == 0) Report("XR visibility expansion rejected; original volume retained");
        }
        return realCopy(self, source);
    }
};

ProjectionScope::ProjectionScope(void* renderer, void* context, void* cameraA, void* cameraB, const XrFovf& fov,
                                 const std::vector<LightCall>* lights) noexcept
    : previous_(current), renderer_(renderer), context_(context), cameraA_(cameraA), cameraB_(cameraB), fov_(fov), lights_(lights) {
    valid_ = active.load(std::memory_order_acquire) && !previous_ && math::ValidFov(fov) && Identity(renderer, context, cameraA, cameraB);
    current = this;
}
ProjectionScope::~ProjectionScope() { current = previous_; }

void SetSceneProjectionActive(bool enabled) noexcept {
    active.store(enabled && installed.load(std::memory_order_acquire), std::memory_order_release);
}
bool NativeStereoDisabled(void* renderer) noexcept {
    return installed.load(std::memory_order_acquire) && projection_detail::NativeStereoDisabledAt(renderer, base);
}

bool SnapshotSceneLights(void* context, std::vector<LightCall>& calls) noexcept {
    try {
        calls.clear();
        if(!lightsInstalled.load(std::memory_order_acquire) || !presentSequence) return false;
        if(preparedLights.Snapshot(presentSequence(), context, calls)) return true;
        if(lightOverflows.fetch_add(1) == 0) Report("XR lights: more than 512 builder calls in a frame; those frames keep the original camera's lights");
    } catch(...) { }
    calls.clear();
    return false;
}
void ReplaySceneLights(const std::vector<LightCall>& calls, void* context) noexcept {
    unsigned failed{};
    for(const auto& call : calls) if(!CallLight(call, context)) ++failed;
    const auto replays = lightReplays.fetch_add(1) + 1;
    if(failed || replays == 1 || replays % 1200 == 0) {
        char text[160]{};
        std::snprintf(text, sizeof(text), "XR lights rebuilt replays=%llu calls=%zu failed=%u thread=%lu",
            static_cast<unsigned long long>(replays), calls.size(), failed, GetCurrentThreadId());
        Report(text);
    }
}

bool InstallSceneProjection(DiagnosticLogger logger, PresentSequenceReader presentSequenceReader) noexcept {
    static std::once_flag once;
    try {
        std::call_once(once, [logger, presentSequenceReader] {
            wchar_t flag[8]{};
            if(GetEnvironmentVariableW(L"DIRT3VR_XR_PROTOTYPE", flag, 8) != 1 || flag[0] != L'1') return;
            logMessage = logger;
            base = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
            if(!Bytes(SetupRva, AlignedEntry, 16) || !Bytes(UploadRva, AlignedEntry, 16) ||
               !Bytes(BuildRva, AlignedEntry, 16) || !Bytes(CopyRva, CopyEntry, 16) || !Bytes(0x57b631, UploadCall, sizeof(UploadCall)) ||
               !Bytes(StereoGetterRva, StereoGetterEntry, sizeof(StereoGetterEntry)) ||
               !IsPointer(reinterpret_cast<void*>(base + DeviceTableRva + 0xd0), base + StereoGetterRva) ||
               !IsPointer(reinterpret_cast<void*>(base + DeviceTableRva + 0x148), base + UploadRva) ||
               !IsPointer(reinterpret_cast<void*>(base + ContextTableRva + 0x40), base + 0x4ab360) ||
               !Call(0x584a8b, SetupRva) || !Call(0x584adf, SetupRva) || !Call(0x180914, SetupRva) ||
               !Call(0x180932, BuildRva) || !Call(0x18093a, CopyRva)) {
                Report("XR projection hooks refused: loaded binary contract mismatch"); return;
            }
            // Tree widening is optional: a mismatch keeps native tree culling only.
            const bool trees = Bytes(TreeCullRva, AlignedEntry, 16) && Call(0x177509, TreeCullRva) &&
                Call(0x7690d6, BuildRva) && Call(0x76938c, BuildRva) && Call(0x7695a4, BuildRva) &&
                Call(0x7690e2, CopyRva) && Call(0x769398, CopyRva) && Call(0x7695b0, CopyRva);
            if(!trees) Report("XR tree visibility hook refused: loaded binary contract mismatch; native tree culling retained");
            // Per-eye lights are optional too: a mismatch keeps the original camera's lights.
            // DIRT3VR_LIGHTS_PER_EYE=0 turns them off for a comparison.
            wchar_t lightsFlag[4]{};
            const bool lightsOff = GetEnvironmentVariableW(L"DIRT3VR_LIGHTS_PER_EYE", lightsFlag, 4) == 1 && lightsFlag[0] == L'0';
            if(lightsOff) Report("XR light hooks off (DIRT3VR_LIGHTS_PER_EYE=0); lights keep the original camera");
            bool lights = !lightsOff && presentSequenceReader && Bytes(PointLightRva, AlignedEntry, 16) && Bytes(SpotLightRva, AlignedEntry, 16) &&
                Bytes(ProjectedLightRva, AlignedEntry, 16) && Bytes(0x7d4bd7, Ret8, 3) && Bytes(0x7da36e, Ret16, 3) && Bytes(0x7db04c, Ret16, 3);
            for(const auto& guard : LightNames) lights = lights && Pushes(guard.rva, guard.nameRva);
            if(!lights && !lightsOff) Report("XR light hooks refused: loaded binary contract mismatch; lights keep the original camera");
            const auto initialized = MH_Initialize();
            if(initialized != MH_OK && initialized != MH_ERROR_ALREADY_INITIALIZED) { Report("XR projection hooks unavailable: MinHook initialization"); return; }
            void* addresses[] = {reinterpret_cast<void*>(base + SetupRva), reinterpret_cast<void*>(base + UploadRva), reinterpret_cast<void*>(base + CopyRva), reinterpret_cast<void*>(base + TreeCullRva)};
            void* detours[] = {reinterpret_cast<void*>(&ProjectionHooks::Setup), reinterpret_cast<void*>(&ProjectionHooks::Upload), reinterpret_cast<void*>(&ProjectionHooks::Copy), reinterpret_cast<void*>(&ProjectionHooks::TreeCull)};
            void** originals[] = {reinterpret_cast<void**>(&realSetup), reinterpret_cast<void**>(&realUpload), reinterpret_cast<void**>(&realCopy), reinterpret_cast<void**>(&realTreeCull)};
            const unsigned wanted = trees ? 4 : 3;
            unsigned created{};
            for(; created < wanted; ++created) if(MH_CreateHook(addresses[created], detours[created], originals[created]) != MH_OK) break;
            bool enabled = created == wanted;
            if(enabled) for(unsigned i = 0; i < wanted; ++i) if(MH_QueueEnableHook(addresses[i]) != MH_OK) enabled = false;
            if(enabled) enabled = MH_ApplyQueued() == MH_OK;
            if(!enabled) {
                for(unsigned i = 0; i < created; ++i) { MH_DisableHook(addresses[i]); MH_RemoveHook(addresses[i]); }
                Report("XR projection hook activation failed; native rendering retained"); return;
            }
            buildFrustum = reinterpret_cast<FrustumFn>(base + BuildRva);
            if(lights) {
                presentSequence = presentSequenceReader;
                void* lightAddresses[] = {reinterpret_cast<void*>(base + PointLightRva), reinterpret_cast<void*>(base + SpotLightRva), reinterpret_cast<void*>(base + ProjectedLightRva)};
                void* lightDetours[] = {reinterpret_cast<void*>(&ProjectionHooks::PointLight), reinterpret_cast<void*>(&ProjectionHooks::SpotLight), reinterpret_cast<void*>(&ProjectionHooks::ProjectedLight)};
                void** lightOriginals[] = {reinterpret_cast<void**>(&realPointLight), reinterpret_cast<void**>(&realSpotLight), reinterpret_cast<void**>(&realProjectedLight)};
                unsigned made{};
                for(; made < 3; ++made) if(MH_CreateHook(lightAddresses[made], lightDetours[made], lightOriginals[made]) != MH_OK) break;
                bool on = made == 3;
                if(on) for(auto* address : lightAddresses) if(MH_QueueEnableHook(address) != MH_OK) on = false;
                if(on) on = MH_ApplyQueued() == MH_OK;
                if(!on) {
                    for(unsigned i = 0; i < made; ++i) { MH_DisableHook(lightAddresses[i]); MH_RemoveHook(lightAddresses[i]); }
                    Report("XR light hook activation failed; lights keep the original camera");
                } else {
                    lightsInstalled.store(true, std::memory_order_release);
                    Report("XR light hooks attached: point, spot and projected-spot lights are rebuilt for each eye");
                }
            }
            HMODULE pinned{};
            GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                reinterpret_cast<LPCWSTR>(&ProjectionHooks::Setup), &pinned);
            installed.store(true, std::memory_order_release);
            Report(trees ? "XR projection hooks attached inactive, including tree visibility; activation and ProjectionScope required"
                         : "XR projection hooks attached inactive; activation and ProjectionScope required");
        });
    } catch(...) { Report("XR projection hook initialization failed"); }
    return installed.load(std::memory_order_acquire);
}
}
