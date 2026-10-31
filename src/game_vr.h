#pragma once
#include "scene_observer.h"
#include <cstdint>
struct IDXGISwapChain;

namespace dirt3vr {
using VrInnerFn = void (__thiscall*)(void*, void*, void*, void*, void*, void*, void*);
// Caller has verified the production executable, instruction guards and the
// non-nested main renderer's first inner call. True means an engine call was
// entered: the caller must never fall back to an additional native pass.
bool RenderVrScene(void* renderer, void* lists, void* cameraA, void* cameraB,
                   void* context, void* scene, void* flags, uint64_t sequence,
                   VrInnerFn realInner, DiagnosticLogger logger) noexcept;
// Invoke before DXGI Present, on the same render thread as RenderVrScene.
// The runner creates DIRT3VR_XR_STOP_FILE to request explicit XR teardown.
void VrBeforePresent(IDXGISwapChain* chain) noexcept;
// Called only after the supported executable and all scene hook guards pass.
bool ConfigureVrPresentation(DiagnosticLogger logger) noexcept;
bool InteractiveVrEnabled() noexcept;
}
