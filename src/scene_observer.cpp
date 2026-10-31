#include "scene_observer.h"
#include "scene_camera.h"
#include "render_probe.h"
#include "game_vr.h"
#include "scene_projection.h"
#include <windows.h>
#include <intrin.h>
#include <MinHook.h>
#include <array>
#include <atomic>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <mutex>

namespace dirt3vr {
namespace {
// Verified against the one executable allowed by diagnostics.cpp. These are
// observational boundaries, not permission to replay the renderer for stereo.
constexpr uintptr_t OuterRva = 0x58a270, InnerRva = 0x584a50;
constexpr uintptr_t MainReturnRva = 0x180650;
constexpr uintptr_t FirstInnerReturnRva = 0x58a388, SecondInnerReturnRva = 0x58a41d;
constexpr uintptr_t RendererTableRva = 0xaac84c, CameraTableRva = 0xa85b50;
constexpr size_t CameraBytes = 0x90, HeaderBytes = 0x108;
constexpr uintptr_t VelocityTableRva = 0xaca3a8, HistorySelectRva = 0x332d90, HistoryInvalidateRva = 0x332c50;
constexpr unsigned char HistorySelectEntry[] = {0x55,0x8b,0xec,0x8b,0x45,0x08,0x89,0x81,0xa0,0x00,0x00,0x00,0x5d,0xc2,0x04,0x00};
constexpr unsigned char HistoryInvalidateEntry[] = {0xc6,0x81,0x90,0x00,0x00,0x00,0x00,0xc3};
constexpr unsigned char OuterEntry[] = {0x53,0x8b,0xdc,0x83,0xec,0x08,0x83,0xe4,0xf0,0x83,0xc4,0x04,0x55,0x8b,0x6b,0x04};
constexpr unsigned char InnerEntry[] = {0x55,0x8b,0xec,0x56,0x8b,0xf1,0x80,0x7e,0x55,0x00,0x74,0x16,0x8b,0x4e,0x38,0x85};
using OuterFn = void (__thiscall*)(void*, void*, void*, void*, void*, void*);
using InnerFn = void (__thiscall*)(void*, void*, void*, void*, void*, void*, void*);
OuterFn realOuter{};
InnerFn realInner{};
DiagnosticLogger logger{};
PresentSequenceReader presentSequence{};
uintptr_t base{};
std::atomic_uint64_t outerCount{}, mainCount{}, otherCount{}, mismatchCount{}, innerCount{};
std::atomic_uint32_t changedHeaderSamples{};
bool replayEnabled{};
bool xrPrototypeEnabled{};
bool replayStopped{}; // Accessed only inside the serial main render scope.
void* replayHistoryPlugin{};
uint64_t replayPreviousSequence{};
uint64_t firstMainTick{}, replayStartSequence{};

// These reads are observations only and never traverse any list pointee. A bad
// or changing pointer drops a sample instead of faulting inside diagnostics.
bool Read(const void* source, void* target, size_t length) noexcept {
    MEMORY_BASIC_INFORMATION region{};
    if(!source || !VirtualQuery(source, &region, sizeof(region)) || region.State != MEM_COMMIT ||
       (region.Protect & (PAGE_NOACCESS | PAGE_GUARD))) return false;
    const auto begin = reinterpret_cast<uintptr_t>(source);
    const auto end = reinterpret_cast<uintptr_t>(region.BaseAddress) + region.RegionSize;
    if(begin > end || length > end - begin) return false;
    __try { memcpy(target, source, length); return true; }
    __except(EXCEPTION_EXECUTE_HANDLER) { return false; }
}
bool PointerIs(const void* address, uintptr_t expected) {
    uintptr_t value{}; return Read(address, &value, sizeof(value)) && value == expected;
}
uint64_t Hash(const unsigned char* data, size_t length) {
    uint64_t value = 14695981039346656037ull;
    for(size_t i = 0; i < length; ++i) { value ^= data[i]; value *= 1099511628211ull; }
    return value;
}
struct OuterSample {
    OuterSample* previous{};
    void* renderer{};
    void* cameraA{};
    void* cameraB{};
    uint64_t mainSequence{};
    uint32_t presentBefore{};
    bool sample{}, valid{};
    unsigned innerCalls{}, firstCalls{}, secondCalls{}, unexpectedCalls{};
    std::array<unsigned char, CameraBytes> beforeA{}, beforeB{};
    bool camerasRead{};
    unsigned serialBefore{};
    bool serialRead{};
};
thread_local OuterSample* current{};
struct RestoreCurrent {
    OuterSample* previous;
    ~RestoreCurrent() { current = previous; }
};
using HistorySelectFn = void (__thiscall*)(void*, unsigned);
using HistoryInvalidateFn = void (__thiscall*)(void*);
struct RestoreHistorySelector {
    void* plugin;
    ~RestoreHistorySelector() { reinterpret_cast<HistorySelectFn>(base + HistorySelectRva)(plugin, 0); }
};

bool ReplayPair(OuterSample& scope, void* lists, void* a, void* b, void* c, void* d, void* e) {
    // This is a bounded experiment, not the headset renderer. Keep the native
    // outer/preparation lifetime, consume its exact lists twice, then stop.
    if(!replayEnabled || replayStopped || GetTickCount64() - firstMainTick < 45000) return false;
    if(!replayStartSequence) replayStartSequence = scope.mainSequence;
    if(scope.mainSequence >= replayStartSequence + 120) return false;
    unsigned rendered{};
    try {
        CameraRecord originalA{}, originalB{};
        void* plugin{}; unsigned selector{}, historyBefore{}, historyFirst{}, historySecond{};
        const auto renderer = static_cast<unsigned char*>(scope.renderer);
        if(a != scope.cameraA || b != scope.cameraB || scope.previous || scope.firstCalls != 1 || scope.secondCalls || scope.unexpectedCalls ||
           !Read(a, originalA.data(), originalA.size()) || !Read(b, originalB.data(), originalB.size()) ||
           !ValidReplayCamera(originalA) || !ValidReplayCamera(originalB) ||
           !Read(renderer + 0x2c, &plugin, sizeof(plugin)) || !PointerIs(plugin, base + VelocityTableRva) ||
           !Read(static_cast<unsigned char*>(plugin) + 0xa0, &selector, sizeof(selector)) || selector != 0 ||
           !Read(static_cast<unsigned char*>(plugin) + 0x94, &historyBefore, sizeof(historyBefore))) {
            uintptr_t pluginTable{};
            Read(plugin, &pluginTable, sizeof(pluginTable));
            logger("Replay refused main=%llu reason=scene-or-history-identity cameras=%u/%u valid=%u/%u nested=%u first=%u second=%u unexpected=%u plugin_table_rva=0x%zx selector=%u",
                scope.mainSequence, a == scope.cameraA, b == scope.cameraB, ValidReplayCamera(originalA), ValidReplayCamera(originalB),
                scope.previous != nullptr, scope.firstCalls, scope.secondCalls, scope.unexpectedCalls, pluginTable ? pluginTable-base : 0, selector);
            replayStopped = true; return false;
        }
        const auto pair = scope.mainSequence - replayStartSequence;
        const bool sampled = pair == 0 || pair == 60 || pair == 119;
        const bool prime = replayHistoryPlugin != plugin || replayPreviousSequence + 1 != scope.mainSequence;
        const auto presentBefore = presentSequence();
        bool ready = true, restored = true;
        std::array<uint64_t, 2> vertices{}, primitives{}, pixels{};
        RestoreHistorySelector restoreSelector{plugin};
        for(unsigned eye = 0; eye < 2; ++eye) {
            RenderProbe probe;
            if(sampled && !probe.Begin()) { ready = false; break; }
            {
                ScopedReplayCamera camera(a, b, originalA, originalB, eye ? .032f : -.032f, eye == 1);
                reinterpret_cast<HistorySelectFn>(base + HistorySelectRva)(plugin, eye);
                if(eye == 1 && prime) reinterpret_cast<HistoryInvalidateFn>(base + HistoryInvalidateRva)(plugin);
                realInner(scope.renderer, lists, a, b, c, d, e);
                ++rendered;
            }
            CameraRecord afterA{}, afterB{};
            restored &= Read(a, afterA.data(), afterA.size()) && Read(b, afterB.data(), afterB.size()) &&
                originalA == afterA && originalB == afterB;
            unsigned history{};
            if(!Read(static_cast<unsigned char*>(plugin) + 0x94, &history, sizeof(history))) { ready = false; break; }
            (eye ? historySecond : historyFirst) = history;
            if(sampled) {
                ready &= probe.Finish(scope.mainSequence, eye);
                vertices[eye] = probe.Vertices(); primitives[eye] = probe.Primitives(); pixels[eye] = probe.Pixels();
            }
            if(!ready || !restored) break;
        }
        const bool sameFrame = presentBefore == presentSequence() && historyFirst == historySecond;
        ready &= rendered == 2 && sameFrame;
        logger("Replay pair main=%llu ready=%u restored=%u calls=%u sampled=%u history_before=%u history_first=%u history_second=%u same_frame=%u vertices=%llu/%llu primitives=%llu/%llu pixels=%llu/%llu present_enter=%u present_exit=%u stereo_verified=false",
            scope.mainSequence, ready, restored, rendered, sampled, historyBefore, historyFirst, historySecond, sameFrame,
            vertices[0], vertices[1], primitives[0], primitives[1], pixels[0], pixels[1], presentBefore, presentSequence());
        replayHistoryPlugin = plugin; replayPreviousSequence = scope.mainSequence;
        if(!ready || !restored) { replayStopped = true; logger("Replay failed main=%llu; further replay disabled", scope.mainSequence); }
    } catch(...) { replayStopped = true; logger("Replay failed main=%llu reason=exception calls=%u", scope.mainSequence, rendered); }
    return rendered != 0; // Never draw a third time after a partial pair.
}

void __fastcall Outer(void* self, void*, void* a, void* b, void* c, void* d, void* e) {
    const auto caller = reinterpret_cast<uintptr_t>(_ReturnAddress()) - base;
    ++outerCount;
    OuterSample scope;
    scope.previous = current; scope.renderer = self; scope.cameraA = a; scope.cameraB = b;
    const bool mainCaller = caller == MainReturnRva;
    scope.valid = mainCaller && PointerIs(self, base + RendererTableRva) &&
        reinterpret_cast<uintptr_t>(a) == reinterpret_cast<uintptr_t>(self) + 0x600 &&
        reinterpret_cast<uintptr_t>(b) == reinterpret_cast<uintptr_t>(self) + 0x690 &&
        PointerIs(a, base + CameraTableRva) && PointerIs(b, base + CameraTableRva);
    if(scope.valid) {
        scope.mainSequence = ++mainCount;
        if(scope.mainSequence == 1) firstMainTick = GetTickCount64();
        scope.sample = scope.mainSequence == 1 || scope.mainSequence % 120 == 0;
        if(scope.sample) {
            scope.presentBefore = presentSequence();
            scope.camerasRead = Read(a, scope.beforeA.data(), CameraBytes) && Read(b, scope.beforeB.data(), CameraBytes);
            scope.serialRead = Read(reinterpret_cast<const unsigned char*>(self) + 0xc4, &scope.serialBefore, sizeof(scope.serialBefore));
        }
    } else if(mainCaller) {
        const auto mismatches = ++mismatchCount;
        if(mismatches == 1 || mismatches % 120 == 0)
            logger("Scene main identity mismatch count=%llu; original call retained; no camera reads", mismatches);
    } else ++otherCount;
    current = &scope;
    {
        RestoreCurrent restore{scope.previous};
        realOuter(self, a, b, c, d, e); // Exactly one original call; no changed argument.
    }
    if(scope.sample) {
        std::array<unsigned char, CameraBytes> afterA{}, afterB{};
        const bool read = scope.camerasRead && Read(a, afterA.data(), CameraBytes) && Read(b, afterB.data(), CameraBytes);
        unsigned serialAfter{};
        const bool serial = scope.serialRead && Read(reinterpret_cast<const unsigned char*>(self) + 0xc4, &serialAfter, sizeof(serialAfter));
        logger("Scene outer main=%llu total=%llu auxiliary=%llu identity_mismatches=%llu thread=%lu nested=%u inner_calls=%u first_calls=%u second_calls=%u unexpected_calls=%u cameras_read=%u camera_a_changed=%u camera_b_changed=%u serial_read=%u serial_before=%u serial_after=%u present_enter=%u present_exit=%u stereo=false",
            scope.mainSequence, outerCount.load(), otherCount.load(), mismatchCount.load(), GetCurrentThreadId(), scope.previous != nullptr,
            scope.innerCalls, scope.firstCalls, scope.secondCalls, scope.unexpectedCalls, read,
            read && scope.beforeA != afterA, read && scope.beforeB != afterB, serial, scope.serialBefore, serialAfter,
            scope.presentBefore, presentSequence());
    }
}
void __fastcall Inner(void* self, void*, void* lists, void* a, void* b, void* c, void* d, void* e) {
    const auto caller = reinterpret_cast<uintptr_t>(_ReturnAddress()) - base;
    ++innerCount;
    auto* scope = current;
    if(scope && scope->valid && scope->renderer == self) {
        ++scope->innerCalls;
        if(caller == FirstInnerReturnRva) ++scope->firstCalls;
        else if(caller == SecondInnerReturnRva) ++scope->secondCalls;
        else ++scope->unexpectedCalls;
    }
    const bool sample = scope && scope->sample && scope->renderer == self;
    const uint32_t presentBefore = sample ? presentSequence() : 0;
    std::array<unsigned char, HeaderBytes> before{}, after{};
    const bool beforeRead = sample && Read(lists, before.data(), before.size());
    const bool replayCandidate = scope && scope->valid && scope->renderer == self && caller == FirstInnerReturnRva;
    bool consumed{};
    if(replayCandidate) {
        if(xrPrototypeEnabled && !scope->previous && (InteractiveVrEnabled() || GetTickCount64() - firstMainTick >= 45000))
            consumed = RenderVrScene(self, lists, a, b, c, d, e, scope->mainSequence, realInner, logger);
        else consumed = ReplayPair(*scope, lists, a, b, c, d, e);
    }
    if(!consumed)
        realInner(self, lists, a, b, c, d, e); // Opaque arguments are passed through unchanged.
    if(sample) {
        const bool read = beforeRead && Read(lists, after.data(), after.size());
        logger("Scene inner main=%llu call=%u return_rva=0x%zx thread=%lu camera_a_original=%u camera_b_original=%u list_header_read=%u list_header_changed=%u list_header_before=%016llx list_header_after=%016llx present_enter=%u present_exit=%u",
            scope->mainSequence, scope->innerCalls, caller, GetCurrentThreadId(), a == scope->cameraA, b == scope->cameraB,
            read, read && before != after, read ? Hash(before.data(), before.size()) : 0ull, read ? Hash(after.data(), after.size()) : 0ull,
            presentBefore, presentSequence());
        if(read && before != after) {
            const auto report = ++changedHeaderSamples;
            if(report <= 4) {
                // Offset-only evidence: never print header contents, pointers or
                // pointees. Bound both the number and length of extra records.
                char offsets[16 * 6 + 1]{};
                unsigned changed{}, listed{};
                size_t used{};
                for(size_t i = 0; i < before.size(); ++i) {
                    if(before[i] == after[i]) continue;
                    ++changed;
                    if(listed == 16) continue;
                    const int length = std::snprintf(offsets + used, sizeof(offsets) - used,
                                                     "%s0x%03zx", listed ? "," : "", i);
                    if(length > 0 && static_cast<size_t>(length) < sizeof(offsets) - used) {
                        used += static_cast<size_t>(length); ++listed;
                    }
                }
                logger("Scene header change detail=%u main=%llu call=%u changed_bytes=%u listed_offsets=%u offsets=[%s] truncated=%u",
                    report, scope->mainSequence, scope->innerCalls, changed, listed, offsets, changed > listed);
            }
        }
    }
}
}

void InstallSceneObserver(DiagnosticLogger log, PresentSequenceReader readPresentSequence) noexcept {
    static std::once_flag once;
    try {
        std::call_once(once, [log, readPresentSequence] {
            wchar_t enabled[8]{};
            if(GetEnvironmentVariableW(L"DIRT3VR_SCENE_DIAGNOSTICS", enabled, 8) != 1 || enabled[0] != L'1') return;
            logger = log;
            presentSequence = readPresentSequence;
            base = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
            wchar_t replay[8]{};
            replayEnabled = GetEnvironmentVariableW(L"DIRT3VR_REPLAY_DIAGNOSTICS", replay, 8) == 1 && replay[0] == L'1';
            wchar_t prototype[8]{};
            xrPrototypeEnabled = GetEnvironmentVariableW(L"DIRT3VR_XR_PROTOTYPE", prototype, 8) == 1 && prototype[0] == L'1';
            if(replayEnabled && xrPrototypeEnabled) {
                replayEnabled = xrPrototypeEnabled = false;
                logger("XR prototype refused: desktop replay and headset prototype cannot share a frame");
            }
            if(replayEnabled || xrPrototypeEnabled) {
                std::array<unsigned char, 16> select{};
                std::array<unsigned char, 8> invalidate{};
                if(!Read(reinterpret_cast<void*>(base + HistorySelectRva), select.data(), select.size()) ||
                   !Read(reinterpret_cast<void*>(base + HistoryInvalidateRva), invalidate.data(), invalidate.size()) ||
                   memcmp(select.data(), HistorySelectEntry, select.size()) || memcmp(invalidate.data(), HistoryInvalidateEntry, invalidate.size()) ||
                   !PointerIs(reinterpret_cast<void*>(base + VelocityTableRva + 0x68), base + HistorySelectRva) ||
                   !PointerIs(reinterpret_cast<void*>(base + VelocityTableRva + 0x60), base + HistoryInvalidateRva)) {
                    replayEnabled = xrPrototypeEnabled = false; logger("Replay refused reason=history-instruction-guards");
                }
            }
            std::array<unsigned char, 16> outer{}, inner{};
            if(!Read(reinterpret_cast<void*>(base + OuterRva), outer.data(), outer.size()) ||
               !Read(reinterpret_cast<void*>(base + InnerRva), inner.data(), inner.size()) ||
               memcmp(outer.data(), OuterEntry, sizeof(OuterEntry)) || memcmp(inner.data(), InnerEntry, sizeof(InnerEntry))) {
                logger("Scene observer refused: loaded instruction guard mismatch; stereo=false"); return;
            }
            const auto initialized = MH_Initialize();
            if(initialized != MH_OK && initialized != MH_ERROR_ALREADY_INITIALIZED) {
                logger("Scene observer unavailable: MinHook=%d", initialized); return;
            }
            auto* outerAddress = reinterpret_cast<void*>(base + OuterRva);
            auto* innerAddress = reinterpret_cast<void*>(base + InnerRva);
            const auto first = MH_CreateHook(outerAddress, reinterpret_cast<void*>(&Outer), reinterpret_cast<void**>(&realOuter));
            if(first != MH_OK) { logger("Scene outer observer unavailable: MinHook=%d", first); return; }
            const auto second = MH_CreateHook(innerAddress, reinterpret_cast<void*>(&Inner), reinterpret_cast<void**>(&realInner));
            if(second != MH_OK) { MH_RemoveHook(outerAddress); logger("Scene inner observer unavailable: MinHook=%d", second); return; }
            if(MH_QueueEnableHook(outerAddress) != MH_OK || MH_QueueEnableHook(innerAddress) != MH_OK || MH_ApplyQueued() != MH_OK) {
                MH_DisableHook(outerAddress); MH_DisableHook(innerAddress);
                MH_RemoveHook(outerAddress); MH_RemoveHook(innerAddress);
                logger("Scene observer activation failed; original rendering retained"); return;
            }
            HMODULE pinned{};
            GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                              reinterpret_cast<LPCWSTR>(&Outer), &pinned);
            if(xrPrototypeEnabled && !InstallSceneProjection(logger)) {
                xrPrototypeEnabled = false; logger("XR prototype refused: projection hooks unavailable");
            }
            if(xrPrototypeEnabled && !ConfigureVrPresentation(logger)) {
                xrPrototypeEnabled = false; logger("XR prototype refused: presentation guards unavailable");
            }
            logger("Scene observers attached outer_rva=0x%zx inner_rva=0x%zx passive=%u replay=%u xr_prototype=%u stereo=false", OuterRva, InnerRva, !replayEnabled && !xrPrototypeEnabled, replayEnabled, xrPrototypeEnabled);
        });
    } catch(...) { if(log) log("Scene observer initialization failed; original rendering retained"); }
}
}
