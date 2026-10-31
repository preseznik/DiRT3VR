#pragma once
namespace dirt3vr {
// Exclude the mod's own texture copies/composites from native draw evidence.
inline thread_local bool PresentationTraceSuppressed{};
struct PausePresentationTrace {
    bool previous=PresentationTraceSuppressed;
    PausePresentationTrace() { PresentationTraceSuppressed=true; }
    ~PausePresentationTrace() { PresentationTraceSuppressed=previous; }
    PausePresentationTrace(const PausePresentationTrace&)=delete;
};
}
