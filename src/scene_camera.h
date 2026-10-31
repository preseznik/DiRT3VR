#pragma once
#include <array>
#include <cmath>
#include <cstring>

namespace dirt3vr {
using CameraRecord = std::array<unsigned char, 0x90>;
inline float CameraFloat(const CameraRecord& camera, size_t offset) {
    float result; std::memcpy(&result, camera.data() + offset, sizeof(result)); return result;
}
inline bool ValidReplayCamera(const CameraRecord& camera) {
    for(const size_t offset : {0x10u, 0x20u, 0x30u}) {
        float length{};
        for(unsigned axis = 0; axis < 3; ++axis) {
            const auto value = CameraFloat(camera, offset + axis*4);
            if(!std::isfinite(value)) return false;
            length += value*value;
        }
        if(length < .5f || length > 1.5f) return false;
    }
    for(unsigned axis = 0; axis < 3; ++axis) {
        const auto value = CameraFloat(camera, 0x40 + axis*4);
        if(!std::isfinite(value) || std::abs(value) > 1e7f) return false;
    }
    return true;
}
// Only the three position floats are ours to change. The native second eye
// additionally disables camera B's history advance. Never restore opaque camera
// bytes that the renderer may legitimately update during a pass.
class ScopedReplayCamera {
public:
    ScopedReplayCamera(void* a, void* b, const CameraRecord& originalA, const CameraRecord& originalB,
                       float rightOffset, bool secondEye)
        : cameras_{static_cast<unsigned char*>(a), static_cast<unsigned char*>(b)},
          originals_{originalA, originalB}, second_(secondEye) {
        for(unsigned i = 0; i < 2; ++i) for(unsigned axis = 0; axis < 3; ++axis) {
            const auto position = CameraFloat(originals_[i], 0x40 + axis*4) - rightOffset*CameraFloat(originals_[i], 0x20 + axis*4);
            std::memcpy(cameras_[i] + 0x40 + axis*4, &position, sizeof(position));
        }
        if(second_) cameras_[1][0x8c] = 0;
    }
    ~ScopedReplayCamera() {
        for(unsigned i = 0; i < 2; ++i) std::memcpy(cameras_[i] + 0x40, originals_[i].data() + 0x40, 12);
        if(second_) cameras_[1][0x8c] = originals_[1][0x8c];
    }
    ScopedReplayCamera(const ScopedReplayCamera&) = delete;
    ScopedReplayCamera& operator=(const ScopedReplayCamera&) = delete;
private:
    std::array<unsigned char*, 2> cameras_;
    std::array<CameraRecord, 2> originals_;
    bool second_;
};
}
