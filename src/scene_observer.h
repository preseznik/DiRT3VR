#pragma once
#include <cstdint>
namespace dirt3vr {
using DiagnosticLogger = void (*)(const char*, ...);
using PresentSequenceReader = uint32_t (*)();
// Called only after the exact executable SHA-256 guard succeeds. The observer
// also requires its own opt-in and verified loaded instruction bytes.
void InstallSceneObserver(DiagnosticLogger log, PresentSequenceReader presentSequence) noexcept;
}
