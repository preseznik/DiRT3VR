#pragma once
#include "scene_observer.h"
#include <atomic>
#include <cstddef>
#include <cstdint>

namespace dirt3vr {
namespace intro_detail {
// One startup sequence per process. Native completion is permanent; a later
// attract/tutorial movie still selects a screen only for its actual draw.
class IntroLatch {
public:
    bool Active(bool video) noexcept {
        if(video) Enter();
        return video || state_.load(std::memory_order_acquire)==Held;
    }
    void Enter() noexcept {
        uint8_t expected=Idle;
        state_.compare_exchange_strong(expected,Held,std::memory_order_acq_rel);
    }
    void Leave() noexcept { state_.store(Finished,std::memory_order_release); }
private:
    enum : uint8_t { Idle, Held, Finished };
    std::atomic<uint8_t> state_{Idle};
};
using ReadMemory=bool (*)(uintptr_t address,void* output,size_t length,void* context) noexcept;
// Shared by installation and read-only file/negative fixtures.
bool ValidateContract(uintptr_t imageBase,ReadMemory read,void* context) noexcept;
}

// Called only after the production executable SHA-256 guard succeeds.
// Hook installation has additional loaded-byte, vtable and type guards.
bool InitializeIntroState(DiagnosticLogger logger) noexcept;
// False unless installation succeeded. No time threshold or GPU work.
bool IntroScreenActive(bool currentVideoDrawn) noexcept;
}
