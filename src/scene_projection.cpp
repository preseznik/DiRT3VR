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
// Water reflection (#dd027e16; docs/research/water-reflection.md). The reflection renderer (vtable 0xeb2bf0)
// keeps the main camera (main renderer +0x600) at +0x970. Once per frame 0x5adb90 mirrors that camera about
// the water into its context (+0x8d4) and camera (+0x8e0), culls with +0x380, then initialises its prepared
// lists at 0x5ae207; draw method 0x5ada70 calls the scene outer and releases the lists. Each eye reruns
// 0x5adb90 only up to 0x5ae207, leaving through the function's own epilogue at 0x5ae270, and draws the
// recorded lists again.
constexpr uintptr_t OuterRva = 0x58a270, ReflectionTableRva = 0xab2bf0, ReflectionDrawRva = 0x1ada70;
constexpr uintptr_t ReflectionCameraRva = 0x1adb90, ReflectionTailRva = 0x1ae207, ReflectionExitRva = 0x1ae270;
constexpr uintptr_t ReflectionCopyReturnRva = 0x1ae1d5, ReflectionCameraCopyRva = 0x7c260;
constexpr size_t ReflectionHeaderBytes = 0x108;
// fld [edx+50]; lea ecx,[esi+998]; fstp [esi+470]
constexpr unsigned char ReflectionTailBytes[] = {0xd9,0x42,0x50,0x8d,0x8e,0x98,0x09,0x00,0x00,0xd9,0x9e,0x70,0x04,0x00,0x00};
// pop edi; mov byte [esi+e08],1; pop esi; mov esp,ebp; pop ebp; mov esp,ebx; pop ebx; ret
constexpr unsigned char ReflectionExitBytes[] = {0x5f,0xc6,0x86,0x08,0x0e,0x00,0x00,0x01,0x5e,0x8b,0xe5,0x5d,0x8b,0xe3,0x5b,0xc3};
// The draw method's outer call: lea edx,[esi+8e0]; push edx; push eax; mov ecx,esi; call outer
constexpr unsigned char ReflectionDrawCall[] = {0x8d,0x96,0xe0,0x08,0x00,0x00,0x52,0x50,0x8b,0xce};
// Vegetation pop-in (#d20e9cf7; docs/research/vegetation-culling.md). Main preparation (0x576fd0) selects
// ground cover through 0xb74650 (renderer +0xd8, called at 0x577640): a perspective frustum from the
// context's desktop FOV and its own draw distance (+0x168), built at 0xb7482b. Ground clutter (tufts and
// bushes, GroundClutterPlugin 0xfa0774) gets the camera position, its forward direction and a cone
// half-angle through 0xb70a90 (from 0xb3d5f4, called at 0x577229) and keeps only cells inside that cone.
constexpr uintptr_t GroundCoverRva = 0x774650, GroundCoverReturnRva = 0x177645, GroundBuildReturnRva = 0x774830;
constexpr uintptr_t ClutterViewRva = 0x770a90, ClutterViewReturnRva = 0x73d5f9, ClutterPluginRva = 0x73d5b0;
// push ebp; mov ebp,esp; push -1; push <SEH handler 0xdaafb8> (relocated); mov eax,fs:[0]
constexpr unsigned char ClutterEntryHead[] = {0x55,0x8b,0xec,0x6a,0xff,0x68};
constexpr unsigned char ClutterEntryTail[] = {0x64,0xa1,0x00,0x00,0x00,0x00};
constexpr uintptr_t ClutterHandlerRva = 0x9aafb8;
constexpr unsigned char Ret18[] = {0xc2,0x18,0x00}, Ret1c[] = {0xc2,0x1c,0x00};
// Just under a right angle: the cone test then keeps every cell in front of the plane through the camera.
constexpr float ClutterHalfAngle = 1.5698f;
constexpr unsigned char StereoGetterEntry[] = {0x55,0x8b,0xec,0x51,0x83,0xb9,0x60,0x4d,0x00,0x00,0x00,0x7e,0x06,0xb0,0x01,0x8b,0xe5,0x5d,0xc3,0x8b,0x81,0x78,0x4d,0x00,0x00,0x85,0xc0,0x74,0x1a,0x8d,0x4d,0xff,0x51,0x50,0xc6,0x45,0xff,0x00,0xe8,0xa5,0x6d,0x4f,0x00,0x83,0xc4,0x08,0x85,0xc0,0x75,0x05,0x38,0x45,0xff,0x75,0xd6,0x32,0xc0,0x8b,0xe5,0x5d,0xc3};
using SetupFn = void (__thiscall*)(void*, void*, void*, float, unsigned);
using UploadFn = void (__thiscall*)(void*, void*);
using FrustumFn = void* (__thiscall*)(void*, const void*);
using TreeCullFn = void (__thiscall*)(void*, uint32_t, uint32_t, void*, uint32_t, uint32_t, uint32_t, uint32_t);
using GroundCoverFn = void (__thiscall*)(void*, void*, float, void*, void*, void*, uint32_t);
using ClutterViewFn = void (__thiscall*)(void*, void*, void*, const float*, const float*, float, float, void*);
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
// The main renderer whose visibility was last widened. Tree volumes and ground cover are widened only inside
// its own preparation. That preparation runs on the render thread (call at 0x580a5d) or, when renderer +0x160
// is set, as a job on a worker thread (0x577750 -> 0x576fd0, queued at 0x580a45 after the copy at 0x58093a).
// So the pair is shared, packed into one atomic (32-bit pointers), never thread-local (#d20e9cf7).
struct PreparedRenderer { void* renderer; void* context; };
std::atomic_uint64_t preparedShared{};
void PublishPrepared(void* renderer, void* context) noexcept {
    preparedShared.store(uint64_t(reinterpret_cast<uintptr_t>(renderer)) | uint64_t(reinterpret_cast<uintptr_t>(context)) << 32,
                         std::memory_order_release);
}
PreparedRenderer LoadPrepared() noexcept {
    const auto packed = preparedShared.load(std::memory_order_acquire);
    return {reinterpret_cast<void*>(uintptr_t(uint32_t(packed))), reinterpret_cast<void*>(uintptr_t(uint32_t(packed >> 32)))};
}
static_assert(sizeof(void*) == 4, "DiRT 3 is a 32-bit game");
// Each thread's copy of the pair while its tree cull or ground cover selection runs.
thread_local PreparedRenderer prepared{};
thread_local const PreparedRenderer* treeScope{};
// Main preparation's ground cover selection, while its frustum is built.
thread_local const PreparedRenderer* groundScope{};
FrustumFn realBuild{};
GroundCoverFn realGroundCover{};
ClutterViewFn realClutterView{};
std::atomic_bool vegetationInstalled{};
std::atomic_uint64_t groundWidened{}, groundRejected{}, clutterAimed{};
// Where the eyes looked in the last VR frame, in world space, for the clutter cone. Written by the render
// thread; read by the preparation, which may run as a worker job, so both copy it under the lock.
struct HeadDirection { float forward[3]{}; uint32_t frame{}; bool valid{}; };
HeadDirection headDirectionShared;
SRWLOCK headDirectionLock = SRWLOCK_INIT;
HeadDirection LoadHeadDirection() noexcept {
    AcquireSRWLockShared(&headDirectionLock);
    const auto copy = headDirectionShared;
    ReleaseSRWLockShared(&headDirectionLock);
    return copy;
}

// The water reflection's last draw, recorded and replayed on the render thread only.
struct ReflectionRecord {
    void* renderer{};
    void *a{}, *b{}, *c{}, *d{}, *e{};
    std::array<unsigned char, ReflectionHeaderBytes> lists{};
    uint32_t used[2]{}; // The prepared lists' two allocators (+0x97c, +0x98c), released after the draw.
    uint32_t frame{};
    DWORD thread{};
    bool valid{};
};
ReflectionRecord reflection;
std::atomic_bool reflectionInstalled{}, reflectionStopped{};
std::atomic_uint64_t reflectionDraws{}, reflectionWidened{}, reflectionRejected{};
// Read by ReflectionTail from inline assembly, so plain globals.
void* reflectionTrampoline{};
void* reflectionExit{};
void* reflectionCameraRenderer{};
DWORD reflectionCameraThread{};
unsigned reflectionCameraExits{};
bool reflectionCameraOnly{};
// The menus' floor reflection in VR (#8817e533) is drawn once from the game camera, so both eyes saw it in the
// wrong place (a second car beside the menu car). Mirroring each eye's camera, as for water, did not line it up
// either (run 20261113-2214), so in VR it is left out; DIRT3VR_MENU_REFLECTION=game keeps DiRT 3's own.
enum class MenuReflection { Game, Off };
MenuReflection menuReflection{MenuReflection::Off};
std::atomic_uint64_t menuReflectionSkips{};

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
struct RestoreGround {
    const PreparedRenderer* previous;
    ~RestoreGround() { groundScope = previous; }
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
bool Write(void* target, const void* source, size_t length) noexcept {
    if(!Range(target, length, true)) return false;
    __try { std::memcpy(target, source, length); return true; }
    __except(EXCEPTION_EXECUTE_HANDLER) { return false; }
}

// Only the eye's own reflection camera update leaves early; the game's own update runs to the end.
bool __stdcall ReflectionCameraOnly(void* renderer) noexcept {
    if(!reflectionCameraOnly || renderer != reflectionCameraRenderer || GetCurrentThreadId() != reflectionCameraThread) return false;
    ++reflectionCameraExits;
    return true;
}
// Replaces lea ecx,[esi+998] at 0x5ae207: the mirrored camera, context and culling volume are complete and
// only the prepared lists' initialisation remains. The eye's call stores the pending fld as the original
// does, then leaves through the function's own epilogue at 0x5ae270.
__declspec(naked) void ReflectionTail() {
    __asm {
        pushfd
        pushad
        push esi
        call ReflectionCameraOnly
        test al, al
        popad
        jz original
        popfd
        fstp dword ptr [esi+0x470]
        jmp dword ptr [reflectionExit]
    original:
        popfd
        jmp dword ptr [reflectionTrampoline]
    }
}
using ReflectionCameraFn = void (__thiscall*)(void*);
bool CallReflectionCamera(void* renderer) noexcept {
    __try { reinterpret_cast<ReflectionCameraFn>(base + ReflectionCameraRva)(renderer); return true; }
    __except(EXCEPTION_EXECUTE_HANDLER) { return false; }
}
bool CallReflectionInner(ReflectionInnerFn inner, void* renderer, void* lists, const ReflectionRecord& record) noexcept {
    __try { inner(renderer, lists, record.a, record.b, record.c, record.d, record.e); return true; }
    __except(EXCEPTION_EXECUTE_HANDLER) { return false; }
}
// The reflection draw's arguments: its own vtable, the main camera it mirrors, its mirrored camera and context.
bool ReflectionIdentity(void* renderer, void* context, void* a, void* b) noexcept {
    const auto* bytes = static_cast<const unsigned char*>(renderer);
    void *source{}, *own{};
    return IsPointer(renderer, base + ReflectionTableRva) && Read(bytes + 0x970, &source, sizeof(source)) && source == a &&
        IsPointer(a, base + CameraTableRva) && IsPointer(static_cast<const unsigned char*>(a) - 0x600, base + RendererTableRva) &&
        b == bytes + 0x8e0 && Read(bytes + 0x8d4, &own, sizeof(own)) && own == context &&
        IsPointer(context, base + ContextTableRva) && Range(context, 0x1d0, true);
}
void StopReflection(const char* reason) noexcept {
    if(reflectionStopped.exchange(true)) return;
    char text[200]{};
    std::snprintf(text, sizeof(text), "XR water reflection per eye stopped (%s); the original reflection is kept", reason);
    Report(text);
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
        if(caller == TreeCullReturnRva) prepared = LoadPrepared();
        treeScope = active.load(std::memory_order_acquire) && caller == TreeCullReturnRva &&
            prepared.context && context == prepared.context ? &prepared : nullptr;
        realTreeCull(self, a1, a2, context, a4, a5, a6, a7);
    }
    // Ground cover (#d20e9cf7): main preparation's selection, for the renderer whose visibility was widened.
    static void __fastcall GroundCover(void* self, void*, void* local, float value, void* context, void* list, void* device, uint32_t flags) {
        const auto caller = reinterpret_cast<uintptr_t>(_ReturnAddress()) - base;
        RestoreGround restore{groundScope};
        if(caller == GroundCoverReturnRva) prepared = LoadPrepared();
        groundScope = active.load(std::memory_order_acquire) && caller == GroundCoverReturnRva &&
            prepared.context && context == prepared.context ? &prepared : nullptr;
        realGroundCover(self, local, value, context, list, device, flags);
    }
    // Its frustum: the context's desktop FOV and the ground cover's own distance. In VR it becomes a box
    // around the camera sized by that frustum's own corners, like the tree volumes.
    static void* __fastcall Build(void* self, void*, const void* matrix) {
        const auto caller = reinterpret_cast<uintptr_t>(_ReturnAddress()) - base;
        void* result = realBuild(self, matrix);
        if(groundScope && caller == GroundBuildReturnRva && result == self && active.load(std::memory_order_acquire)) {
            const auto renderer = reinterpret_cast<uintptr_t>(groundScope->renderer);
            CameraRecord a{}, b{};
            std::array<float, 56> original{};
            alignas(16) std::array<float, 64> volume{};
            alignas(16) projection_detail::Matrix box{};
            if(Identity(groundScope->renderer, groundScope->context, reinterpret_cast<void*>(renderer + 0x600), reinterpret_cast<void*>(renderer + 0x690)) &&
               Read(reinterpret_cast<void*>(renderer + 0x600), a.data(), a.size()) &&
               Read(reinterpret_cast<void*>(renderer + 0x690), b.data(), b.size()) && Read(self, original.data(), sizeof(original)) &&
               projection_detail::TreeVisibilityMatrix(a, b, original, box) && BuildVolume(volume.data(), box.data()) &&
               std::all_of(volume.begin(), volume.begin() + 56, [](float value) { return std::isfinite(value); })) {
                if(groundWidened.fetch_add(1) == 0) Report("XR ground cover visibility expanded around the camera; its draw distance retained");
                return realBuild(self, box.data());
            }
            if(groundRejected.fetch_add(1) == 0) Report("XR ground cover visibility expansion rejected; its original frustum retained");
        }
        return result;
    }
    // Ground clutter (#d20e9cf7): keep its cone, but aim it where the eyes looked in the last VR frame and
    // open it to a hemisphere, which covers both eyes' views and a head turn before the next frame.
    static void __fastcall ClutterView(void* self, void*, void* a1, void* a2, const float* position, const float* direction,
                                       float angle, float scale, void* a7) {
        const auto caller = reinterpret_cast<uintptr_t>(_ReturnAddress()) - base;
        alignas(16) float aimed[4]{};
        const bool main = caller == ClutterViewReturnRva && active.load(std::memory_order_acquire);
        const auto headDirection = main ? LoadHeadDirection() : HeadDirection{};
        if(main && headDirection.valid && presentSequence &&
           presentSequence() - headDirection.frame <= 2 && Read(direction, aimed, sizeof(aimed)) && std::isfinite(angle) && angle < ClutterHalfAngle) {
            aimed[0] = headDirection.forward[0]; aimed[1] = headDirection.forward[1]; aimed[2] = headDirection.forward[2];
            if(clutterAimed.fetch_add(1) == 0) Report("XR ground clutter cone aimed at the headset's view and opened to a hemisphere; its distance retained");
            realClutterView(self, a1, a2, position, aimed, ClutterHalfAngle, scale, a7);
            return;
        }
        realClutterView(self, a1, a2, position, direction, angle, scale, a7);
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
        // The water reflection culls with the main camera's own frustum (#dd027e16): in the headset that left
        // the water beside and behind the game's view without reflections. Widen it to a box around the camera,
        // sized by its own corners (the reflection's draw distance), like the tree volumes.
        if(active.load(std::memory_order_acquire) && caller == ReflectionCopyReturnRva && reflectionInstalled.load(std::memory_order_acquire) &&
           !reflectionStopped.load()) {
            const auto* renderer = static_cast<unsigned char*>(self) - 0x380;
            unsigned char* cameraA{};
            void* mainContext{};
            CameraRecord a{}, b{};
            std::array<float, 56> original{};
            alignas(16) std::array<float, 56> volume{};
            alignas(16) projection_detail::Matrix matrix{};
            if(IsPointer(renderer, base + ReflectionTableRva) && Read(renderer + 0x970, &cameraA, sizeof(cameraA)) && cameraA &&
               Read(cameraA - 0x600 + 0x5f4, &mainContext, sizeof(mainContext)) &&
               Identity(cameraA - 0x600, mainContext, cameraA, cameraA + 0x90) &&
               Read(cameraA, a.data(), a.size()) && Read(cameraA + 0x90, b.data(), b.size()) &&
               Read(source, original.data(), sizeof(original)) &&
               projection_detail::TreeVisibilityMatrix(a, b, original, matrix) && BuildVolume(volume.data(), matrix.data()) &&
               std::all_of(volume.begin(), volume.end(), [](float value) { return std::isfinite(value); })) {
                if(reflectionWidened.fetch_add(1) == 0) Report("XR water reflection visibility expanded around the camera; its draw distance retained");
                return realCopy(self, volume.data());
            }
            if(reflectionRejected.fetch_add(1) == 0) Report("XR water reflection visibility expansion rejected; its original volume retained");
        }
        if(active.load(std::memory_order_acquire) && caller == 0x18093f) {
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
                PublishPrepared(reinterpret_cast<void*>(renderer), context);
                return realCopy(self, volume.data());
            }
            PublishPrepared(nullptr, nullptr);
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
ProjectionScope::ProjectionScope(Reflection, void* renderer, void* context, void* cameraA, void* cameraB, const XrFovf& fov) noexcept
    : previous_(current), renderer_(renderer), context_(context), cameraA_(cameraA), cameraB_(cameraB), fov_(fov) {
    valid_ = active.load(std::memory_order_acquire) && !previous_ && math::ValidFov(fov) && ReflectionIdentity(renderer, context, cameraA, cameraB);
    current = this;
}
ProjectionScope::~ProjectionScope() { current = previous_; }

void RememberEyeDirection(const void* camera) noexcept {
    if(!vegetationInstalled.load(std::memory_order_acquire) || !active.load(std::memory_order_acquire) || !presentSequence) return;
    try {
        float forward[3]{};
        if(!Read(static_cast<const unsigned char*>(camera) + 0x30, forward, sizeof(forward))) return;
        const double length = std::sqrt(double(forward[0])*forward[0] + double(forward[1])*forward[1] + double(forward[2])*forward[2]);
        if(!std::isfinite(length) || length < .5 || length > 2.) return;
        HeadDirection next{};
        for(unsigned axis = 0; axis < 3; ++axis) next.forward[axis] = float(forward[axis]/length);
        next.frame = presentSequence();
        next.valid = true;
        AcquireSRWLockExclusive(&headDirectionLock);
        headDirectionShared = next;
        ReleaseSRWLockExclusive(&headDirectionLock);
    } catch(...) { }
}

void RememberReflectionDraw(void* renderer, const void* lists, void* a, void* b, void* c, void* d, void* e) noexcept {
    reflection.valid = false;
    if(!reflectionInstalled.load(std::memory_order_acquire) || reflectionStopped.load() || !active.load(std::memory_order_acquire) || !presentSequence) return;
    try {
        const auto* bytes = static_cast<const unsigned char*>(renderer);
        unsigned char frontend{}, history{};
        // Water only (the menus' floor reflection is +0x1770), and without motion history, so that an extra
        // draw cannot move the main renderer's history checks.
        if(!ReflectionIdentity(renderer, c, a, b) || !Read(bytes + 0x1770, &frontend, 1) || frontend ||
           !Read(bytes + 0x50, &history, 1) || history || !Read(bytes + 0x97c, &reflection.used[0], 4) ||
           !Read(bytes + 0x98c, &reflection.used[1], 4) || !Read(lists, reflection.lists.data(), reflection.lists.size())) return;
        reflection.renderer = renderer;
        reflection.a = a; reflection.b = b; reflection.c = c; reflection.d = d; reflection.e = e;
        reflection.frame = presentSequence();
        reflection.thread = GetCurrentThreadId();
        reflection.valid = true;
    } catch(...) { reflection.valid = false; }
}

bool SkipMenuReflection(void* renderer) noexcept {
    if(menuReflection != MenuReflection::Off || !active.load(std::memory_order_acquire)) return false;
    unsigned char frontend{};
    if(!IsPointer(renderer, base + ReflectionTableRva) || !Read(static_cast<const unsigned char*>(renderer) + 0x1770, &frontend, 1) ||
       !frontend) return false;
    if(menuReflectionSkips.fetch_add(1) == 0) Report("XR menus' floor reflection left out in VR; it cannot line up with both eyes");
    return true;
}

bool RenderEyeReflection(void* mainRenderer, const XrFovf& fov, ReflectionInnerFn inner) noexcept {
    if(!inner || !reflection.valid || reflectionStopped.load() || !active.load(std::memory_order_acquire) || !presentSequence) return false;
    try {
        auto* bytes = static_cast<unsigned char*>(reflection.renderer);
        void* pending{};
        // Same frame and thread, the same main camera, and no newer preparation since the draw released its lists.
        if(reflection.frame != presentSequence() || reflection.thread != GetCurrentThreadId() ||
           reflection.a != static_cast<unsigned char*>(mainRenderer) + 0x600 ||
           !ReflectionIdentity(reflection.renderer, reflection.c, reflection.a, reflection.b) ||
           !Read(bytes + 0x994, &pending, sizeof(pending)) || pending) return false;
        // The lists' allocators and prepared object: put back if the early exit were ever missed.
        constexpr size_t PreparedFirst = 0x974, PreparedBytes = 0xbf8 - PreparedFirst;
        std::array<unsigned char, PreparedBytes> saved{};
        uint32_t used[2]{};
        if(!Read(bytes + PreparedFirst, saved.data(), saved.size()) || !Read(bytes + 0x97c, &used[0], 4) || !Read(bytes + 0x98c, &used[1], 4)) return false;
        reflectionCameraRenderer = bytes; reflectionCameraThread = GetCurrentThreadId();
        const auto exits = reflectionCameraExits;
        reflectionCameraOnly = true;
        const bool updated = CallReflectionCamera(bytes);
        reflectionCameraOnly = false;
        if(!updated || reflectionCameraExits != exits + 1) {
            Write(bytes + PreparedFirst, saved.data(), saved.size());
            StopReflection(updated ? "camera update missed its early exit" : "camera update fault");
            return false;
        }
        bool drawn{}, projected{};
        {
            ProjectionScope projection(ProjectionScope::Reflection{}, reflection.renderer, reflection.c, reflection.a, reflection.b, fov);
            if(!projection.Valid()) { StopReflection("identity or projection refused"); return false; }
            // While it draws, the lists' allocators hold what they held during the game's own draw.
            auto lists = reflection.lists;
            if(Write(bytes + 0x97c, &reflection.used[0], 4) && Write(bytes + 0x98c, &reflection.used[1], 4))
                drawn = CallReflectionInner(inner, bytes, lists.data(), reflection);
            Write(bytes + 0x97c, &used[0], 4); Write(bytes + 0x98c, &used[1], 4);
            projected = projection.Valid() && projection.Uploads() > 0;
        }
        if(!drawn || !projected) { StopReflection(drawn ? "eye projection not applied" : "draw fault"); return false; }
        const auto draws = reflectionDraws.fetch_add(1) + 1;
        if(draws == 1 || draws % 1200 == 0) {
            char text[160]{};
            std::snprintf(text, sizeof(text), "XR water reflection drawn for each eye draws=%llu widened=%llu thread=%lu",
                static_cast<unsigned long long>(draws), static_cast<unsigned long long>(reflectionWidened.load()), GetCurrentThreadId());
            Report(text);
        }
        return true;
    } catch(...) { StopReflection("exception"); return false; }
}

void SetSceneProjectionActive(bool enabled) noexcept {
    active.store(enabled && installed.load(std::memory_order_acquire), std::memory_order_release);
    if(!enabled) PublishPrepared(nullptr, nullptr);
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
            wchar_t menuFlag[8]{};
            if(GetEnvironmentVariableW(L"DIRT3VR_MENU_REFLECTION", menuFlag, 8) == 4 && !wcscmp(menuFlag, L"game")) {
                menuReflection = MenuReflection::Game;
                Report("XR menus' floor reflection kept (DIRT3VR_MENU_REFLECTION=game)");
            }
            // Per-eye water reflection is optional as well (#dd027e16); DIRT3VR_WATER_PER_EYE=0 turns it off.
            wchar_t waterFlag[4]{};
            const bool waterOff = GetEnvironmentVariableW(L"DIRT3VR_WATER_PER_EYE", waterFlag, 4) == 1 && waterFlag[0] == L'0';
            if(waterOff) Report("XR water reflection per eye off (DIRT3VR_WATER_PER_EYE=0); the reflection keeps the original camera");
            const bool water = !waterOff && presentSequenceReader && Bytes(ReflectionCameraRva, AlignedEntry, 16) &&
                Bytes(ReflectionTailRva - 3, ReflectionTailBytes, sizeof(ReflectionTailBytes)) &&
                Bytes(ReflectionExitRva, ReflectionExitBytes, sizeof(ReflectionExitBytes)) &&
                Bytes(ReflectionOuterReturnRva - 15, ReflectionDrawCall, sizeof(ReflectionDrawCall)) && Call(ReflectionOuterReturnRva - 5, OuterRva) &&
                IsPointer(reinterpret_cast<void*>(base + ReflectionTableRva + 13*4), base + ReflectionDrawRva) &&
                Call(0x1adbdd, SetupRva) && Call(0x1ae1ad, SetupRva) && Call(0x1ae1d0, CopyRva) && Call(0x1ae123, ReflectionCameraCopyRva);
            if(!water && !waterOff) Report("XR water reflection hook refused: loaded binary contract mismatch; the reflection keeps the original camera");
            if(water) {
                presentSequence = presentSequenceReader;
                auto* tail = reinterpret_cast<void*>(base + ReflectionTailRva);
                reflectionExit = reinterpret_cast<void*>(base + ReflectionExitRva);
                bool on = MH_CreateHook(tail, reinterpret_cast<void*>(&ReflectionTail), &reflectionTrampoline) == MH_OK;
                if(on && MH_EnableHook(tail) != MH_OK) { MH_RemoveHook(tail); on = false; }
                if(on) {
                    reflectionInstalled.store(true, std::memory_order_release);
                    Report("XR water reflection hook attached: each eye mirrors its own camera and draws the reflection again");
                } else Report("XR water reflection hook activation failed; the reflection keeps the original camera");
            }
            // Vegetation in VR (#d20e9cf7): ground cover's frustum and ground clutter's cone. Optional;
            // DIRT3VR_VEGETATION_WIDE=0 keeps both on the game camera's view.
            wchar_t vegetationFlag[4]{};
            const bool vegetationOff = GetEnvironmentVariableW(L"DIRT3VR_VEGETATION_WIDE", vegetationFlag, 4) == 1 && vegetationFlag[0] == L'0';
            if(vegetationOff) Report("XR vegetation widening off (DIRT3VR_VEGETATION_WIDE=0); ground cover and clutter keep the game camera's view");
            const bool vegetation = !vegetationOff && presentSequenceReader && Bytes(GroundCoverRva, AlignedEntry, 16) && Bytes(0x774ad1, Ret18, 3) &&
                Call(0x177640, GroundCoverRva) && Call(0x77482b, BuildRva) &&
                Bytes(ClutterViewRva, ClutterEntryHead, sizeof(ClutterEntryHead)) &&
                IsPointer(reinterpret_cast<void*>(base + ClutterViewRva + 6), base + ClutterHandlerRva) &&
                Bytes(ClutterViewRva + 10, ClutterEntryTail, sizeof(ClutterEntryTail)) && Bytes(0x770b56, Ret1c, 3) &&
                Call(ClutterViewReturnRva - 5, ClutterViewRva) && Call(0x177229, ClutterPluginRva);
            if(!vegetation && !vegetationOff) Report("XR vegetation hooks refused: loaded binary contract mismatch; ground cover and clutter keep the game camera's view");
            if(vegetation) {
                presentSequence = presentSequenceReader;
                void* plantAddresses[] = {reinterpret_cast<void*>(base + GroundCoverRva), reinterpret_cast<void*>(base + BuildRva), reinterpret_cast<void*>(base + ClutterViewRva)};
                void* plantDetours[] = {reinterpret_cast<void*>(&ProjectionHooks::GroundCover), reinterpret_cast<void*>(&ProjectionHooks::Build), reinterpret_cast<void*>(&ProjectionHooks::ClutterView)};
                void** plantOriginals[] = {reinterpret_cast<void**>(&realGroundCover), reinterpret_cast<void**>(&realBuild), reinterpret_cast<void**>(&realClutterView)};
                unsigned made{};
                for(; made < 3; ++made) if(MH_CreateHook(plantAddresses[made], plantDetours[made], plantOriginals[made]) != MH_OK) break;
                bool on = made == 3;
                if(on) for(auto* address : plantAddresses) if(MH_QueueEnableHook(address) != MH_OK) on = false;
                if(on) on = MH_ApplyQueued() == MH_OK;
                if(!on) {
                    for(unsigned i = 0; i < made; ++i) { MH_DisableHook(plantAddresses[i]); MH_RemoveHook(plantAddresses[i]); }
                    Report("XR vegetation hook activation failed; ground cover and clutter keep the game camera's view");
                } else {
                    buildFrustum = realBuild; // Our own volume builds skip the detour.
                    vegetationInstalled.store(true, std::memory_order_release);
                    Report("XR vegetation hooks attached: ground cover around the camera, clutter aimed at the headset's view");
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
