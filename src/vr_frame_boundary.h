#pragma once
#include <cstdint>

namespace dirt3vr {
// One XR attempt per desktop frame, including invisible/failed XR frames.
// A failed stereo attempt cannot become a screen submission at Present.
class VrFrameBoundary {
public:
    bool Claim() noexcept { if(claimed_) return false; claimed_=true; return true; }
    uint64_t Sequence() const noexcept { return sequence_; }
    void Presented() noexcept { ++sequence_; claimed_=false; }
private:
    uint64_t sequence_=1;
    bool claimed_{};
};

// Temporal matrices belong to a continuous camera/reference and a completed
// pair. A discontinuity must prime both selected eye slots, not just the right.
class VrHistoryContinuity {
public:
    bool NeedsPrime(const void* plugin,uint64_t sequence,uint64_t reference) const noexcept {
        return !valid_ || plugin!=plugin_ || sequence!=sequence_+1 || reference!=reference_;
    }
    void Completed(const void* plugin,uint64_t sequence,uint64_t reference) noexcept {
        plugin_=plugin; sequence_=sequence; reference_=reference; valid_=true;
    }
    void Reset() noexcept { valid_=false; }
private:
    const void* plugin_{};
    uint64_t sequence_{},reference_{};
    bool valid_{};
};
}
