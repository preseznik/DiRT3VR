#include "grass_fade.h"
#include <windows.h>
#include <MinHook.h>
#include <atomic>
#include <cstdint>
#include <cstring>

namespace dirt3vr {
namespace {
// GroundCoverPlugin (vtable 0xf2a894), the roadside grass and bushes. Slot 5 0xb81030 is its per-frame update; its
// views are at +0xf90 (main), +0xf94/+0xf98 (splits) and +0xf9c (rear view). Slot 7 0xb646d0 calls 0xb64530 per view,
// which uploads cameraPositionAndDrawDistance = (view +0xa0 xyz, view +0xd0) at 0xb645f3. The selection (0xb778e8)
// picks cells within max(1.1 x view +0x50, 70) of the camera. The vertex shader fades each tuft out between
// (fadeParams.x + jitter) x D and 0.2 x D further (Finland rally: fadeParams 0.6/0.2/0.2, so 40-80 % of D, gone 20 %
// later; +0x50 = +0xd0 = 210 m). In VR new grass reaches full strength before it is filled in, so it pops in.
// Here the shader distance of the main view is 70 % of the game's (147 m on Finland): grass is still selected and
// filled in as far as before, but fades out by 70 %, so grass that arrives late is already faded.
constexpr uintptr_t ViewConstantsRva = 0x764530, UpdateRva = 0x781030;
constexpr unsigned char AlignedEntry[] = {0x53,0x8b,0xdc,0x83,0xec,0x08,0x83,0xe4,0xf0,0x83,0xc4,0x04,0x55};
constexpr size_t MainView = 0xf90, Distance = 0x50, ShaderDistance = 0xd0;
constexpr float ShaderShare = 0.70f;
using ViewConstantsFn = void (__thiscall*)(void*, void*);
using UpdateFn = void (__thiscall*)(void*, void*, void*, void*);
uintptr_t base{};
DiagnosticLogger report{};
ViewConstantsFn realViewConstants{};
UpdateFn realUpdate{};
std::atomic<void*> mainView{};
std::atomic_uint32_t faded{};

void __fastcall ViewConstants(void* view, void*, void* arg) noexcept {
    if(view && view == mainView.load(std::memory_order_relaxed)) {
        __try {
            auto* bytes = static_cast<unsigned char*>(view);
            const float distance = *reinterpret_cast<const float*>(bytes + Distance);
            if(distance > 0.f) {
                *reinterpret_cast<float*>(bytes + ShaderDistance) = distance * ShaderShare;
                if(faded.fetch_add(1, std::memory_order_relaxed) == 0 && report)
                    report("Grass fade first view distance=%.1f shader_distance=%.1f", distance, distance * ShaderShare);
            }
        } __except(EXCEPTION_EXECUTE_HANDLER) {}
    }
    realViewConstants(view, arg);
}

void __fastcall Update(void* system, void*, void* a, void* settings, void* c) noexcept {
    realUpdate(system, a, settings, c);
    void* view{};
    __try { std::memcpy(&view, static_cast<const unsigned char*>(system) + MainView, sizeof(view)); }
    __except(EXCEPTION_EXECUTE_HANDLER) { view = nullptr; }
    mainView.store(view, std::memory_order_relaxed);
}

bool Entry(uintptr_t rva) noexcept {
    __try { return std::memcmp(reinterpret_cast<const void*>(base + rva), AlignedEntry, sizeof(AlignedEntry)) == 0; }
    __except(EXCEPTION_EXECUTE_HANDLER) { return false; }
}
}

void InstallGrassFade(DiagnosticLogger logger) noexcept {
    report = logger;
    wchar_t flag[4]{};
    if(GetEnvironmentVariableW(L"DIRT3VR_GRASS_FADE", flag, 4) == 1 && flag[0] == L'0') {
        if(report) report("Grass fade off (DIRT3VR_GRASS_FADE=0)");
        return;
    }
    base = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    if(!Entry(ViewConstantsRva) || !Entry(UpdateRva)) { if(report) report("Grass fade refused: entry mismatch"); return; }
    const auto init = MH_Initialize();
    if(init != MH_OK && init != MH_ERROR_ALREADY_INITIALIZED) return;
    auto* view = reinterpret_cast<void*>(base + ViewConstantsRva);
    auto* update = reinterpret_cast<void*>(base + UpdateRva);
    if(MH_CreateHook(view, reinterpret_cast<void*>(&ViewConstants), reinterpret_cast<void**>(&realViewConstants)) != MH_OK ||
       MH_CreateHook(update, reinterpret_cast<void*>(&Update), reinterpret_cast<void**>(&realUpdate)) != MH_OK ||
       MH_QueueEnableHook(view) != MH_OK || MH_QueueEnableHook(update) != MH_OK || MH_ApplyQueued() != MH_OK) {
        if(report) report("Grass fade refused: hooks failed");
        return;
    }
    if(report) report("Grass fade attached shader_share=%.2f", ShaderShare);
}
}
