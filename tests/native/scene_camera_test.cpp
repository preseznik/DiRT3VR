#include "scene_camera.h"
#include <limits>
#include <stdexcept>
#include <cstdio>

using namespace dirt3vr;
void Require(bool value) { if(!value) throw std::runtime_error("camera transaction check failed"); }
void Set(CameraRecord& camera, size_t offset, float value) { std::memcpy(camera.data() + offset, &value, sizeof(value)); }
int main() {
    try {
        CameraRecord a{}, b{};
        Set(a, 0x14, 1); Set(a, 0x20, 1); Set(a, 0x38, 1);
        Set(a, 0x40, 100); Set(a, 0x44, 200); Set(a, 0x48, 300);
        a[0x8c] = 1; a[0x4c] = 0x71; a[0x8f] = 0xa3; b = a;
        const auto original = a;
        Require(ValidReplayCamera(a));
        {
            ScopedReplayCamera eye(a.data(), b.data(), original, original, .032f, false);
            Require(std::abs(CameraFloat(a, 0x40) - 99.968f) < .00001f);
            Require(a[0x8c] == 1 && b[0x8c] == 1 && a[0x4c] == 0x71 && a[0x8f] == 0xa3);
        }
        Require(a == original && b == original);
        try {
            ScopedReplayCamera eye(a.data(), b.data(), original, original, -.032f, true);
            Require(std::abs(CameraFloat(b, 0x40) - 100.032f) < .00001f);
            Require(a[0x8c] == 1 && b[0x8c] == 0);
            b[0x80] = 0x42; // Renderer-owned change must survive our rollback.
            throw 1;
        } catch(int) { }
        auto expected = original; expected[0x80] = 0x42;
        Require(a == original && b == expected);
        Set(a, 0x20, std::numeric_limits<float>::quiet_NaN()); Require(!ValidReplayCamera(a));
        a = original; Set(a, 0x40, std::numeric_limits<float>::infinity()); Require(!ValidReplayCamera(a));
        a = {}; Require(!ValidReplayCamera(a));
        std::puts("Camera translation, temporal flag, exception restoration and ownership checks passed");
        return 0;
    } catch(const std::exception& error) { std::puts(error.what()); return 1; }
}
