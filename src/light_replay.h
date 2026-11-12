#pragma once
#include <cstdint>
#include <mutex>
#include <vector>

// Per-eye local lights (#82c61759, DiRT2VR's light_replay.h). DiRT 3 builds each point, spot and
// projected-spot light's shader values (lightPosVs, lightDirVs, mViewToLightClipSpace) on a preparation
// worker, once per frame, from the original camera; both eyes then reused them, so headlights followed
// the head. The renderer records those builder calls and runs them again after each eye's camera upload.
namespace dirt3vr {
enum class LightKind : unsigned { Point, Spot, Projected };
struct LightCall { LightKind kind; void* self; void* light; void* context; void* material; void* extra; };

// Borrowed engine objects live only through the current prepared scene. A frame's calls are kept until
// the next frame's first call; a snapshot takes only the exact frame's calls for one context.
class PreparedLights {
    std::mutex mutex_;
    uint64_t frame_{};
    bool started_{},overflow_{};
    std::vector<LightCall> calls_;
public:
    static constexpr size_t Capacity=512;
    void Remember(uint64_t frame,const LightCall& call) {
        std::lock_guard lock(mutex_);
        if(started_ && frame<frame_) return;
        if(!started_ || frame!=frame_) { frame_=frame; started_=true; overflow_=false; calls_.clear(); }
        if(calls_.size()==Capacity) { overflow_=true; return; }
        calls_.push_back(call);
    }
    // False when this frame's set overflowed: never refresh half a set.
    bool Snapshot(uint64_t frame,void* context,std::vector<LightCall>& result) {
        std::lock_guard lock(mutex_);
        result.clear();
        if(!started_ || frame!=frame_) return true;
        if(overflow_) return false;
        for(const auto& call:calls_) if(call.context==context) result.push_back(call);
        return true;
    }
};
}
