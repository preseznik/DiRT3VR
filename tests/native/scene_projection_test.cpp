#include "scene_projection.h"
#include <cmath>
#include <cstdio>
#include <cstring>
#include <limits>
#include <stdexcept>
#include <vector>

namespace {
void Require(bool condition, const char* message) { if(!condition) throw std::runtime_error(message); }
bool Near(float a, float b) { return std::abs(a-b) < .00001f; }
void Set(dirt3vr::CameraRecord& camera, size_t offset, float value) { std::memcpy(camera.data() + offset, &value, sizeof(value)); }
dirt3vr::CameraRecord Camera(float x) {
    dirt3vr::CameraRecord result{};
    Set(result, 0x14, 1); Set(result, 0x20, 1); Set(result, 0x38, 1);
    Set(result, 0x40, x); Set(result, 0x58, 1000);
    return result;
}
}
int main() {
    try {
        using namespace dirt3vr;
        using namespace projection_detail;
        Matrix projection{}, view{}, combined{};
        projection[0] = 2; projection[5] = 3; projection[10] = -1.0004f; projection[11] = -1; projection[14] = -.40008f;
        view[0] = view[5] = view[10] = view[15] = 1;
        view[12] = -7; view[13] = 2; view[14] = -3;
        const auto nativeProjection = projection;
        const XrFovf fov{-.9f, .7f, .8f, -.6f};
        Require(ApplyProjection(projection, view, combined, fov), "Valid projection rejected");
        // A translated world point on a specified eye ray must land at the
        // same edge through combined VP as through view then projection.
        const float world[4] = {std::tan(fov.angleLeft) + 7, -2, 2, 1};
        float clip[4]{};
        for(unsigned column = 0; column < 4; ++column)
            for(unsigned row = 0; row < 4; ++row) clip[column] += world[row]*combined[row*4 + column];
        Require(Near(clip[0]/clip[3], -1), "Combined projection has wrong order or stale eye coefficients");
        Require(projection[10] == nativeProjection[10] && projection[14] == nativeProjection[14], "Native depth mapping changed");
        const auto accepted = projection, acceptedCombined = combined;
        view[0] = std::numeric_limits<float>::infinity();
        Require(!ApplyProjection(projection, view, combined, fov) && projection == accepted && combined == acceptedCombined,
                "Invalid view partially changed outputs");
        view[0] = 1;
        projection[11] = 0; projection[15] = 1;
        const auto orthographic = projection;
        Require(!ApplyProjection(projection, view, combined, fov) && projection == orthographic && combined == acceptedCombined,
                "Orthographic shadow projection changed");
        auto a = Camera(8), b = Camera(12);
        std::array<float, 56> volume{};
        // A stock volume extending beyond the default far range must remain
        // enclosed when head orientation changes. Corner W is native padding.
        for(unsigned i = 24; i < 56; i += 4) {
            volume[i] = 2010; volume[i + 1] = 0; volume[i + 2] = 0;
            volume[i + 3] = std::numeric_limits<float>::quiet_NaN();
        }
        Matrix box{};
        Require(VisibilityMatrix(a, b, volume, box), "Valid native corners rejected because of padding");
        Require(box[0] < 1.f/2000 && box[10] < 0 && box[15] == 1, "Visibility radius or axis convention lost");
        for(float x : {-1990.f, 2010.f}) Require(std::abs(x*box[0] + box[12]) <= 1, "Original visibility corner clipped");
        const auto validBox = box;
        volume[24] = 1000010;
        Require(!VisibilityMatrix(a, b, volume, box) && box == validBox, "Unbounded visibility accepted or changed output");
        // Tree volumes end at each tree type's own cull distance (here 200 of a
        // 1000 camera far): the box must enclose them without growing to the
        // camera far, so tree types keep their distance culling.
        std::array<float, 56> trees{};
        for(unsigned i = 24; i < 56; i += 4) {
            trees[i] = (i/4) % 2 ? 210.f : -190.f; trees[i + 1] = (i/8) % 2 ? 40.f : -40.f; trees[i + 2] = 0;
            trees[i + 3] = std::numeric_limits<float>::quiet_NaN();
        }
        Matrix treeBox{};
        Require(TreeVisibilityMatrix(a, b, trees, treeBox), "Valid tree volume rejected");
        Require(treeBox[0] > 1.f/1000 && treeBox[0] < 1.f/200 && treeBox[10] < 0 && treeBox[15] == 1,
                "Tree visibility box grew to the camera far range or lost its axis convention");
        for(unsigned i = 24; i < 56; i += 4)
            Require(std::abs(trees[i]*treeBox[0] + treeBox[12]) <= 1 && std::abs(trees[i + 1]*treeBox[5] + treeBox[13]) <= 1,
                    "Tree volume corner clipped by its widened box");
        Require(VisibilityMatrix(a, b, trees, box) && box[0] < 1.f/1000, "Main visibility no longer starts at the camera far range");
        box = validBox;
        const auto validTrees = treeBox;
        trees[24] = 1000010;
        Require(!TreeVisibilityMatrix(a, b, trees, treeBox) && treeBox == validTrees, "Unbounded tree volume accepted or changed output");
        volume[24] = 2010; Set(a, 0x58, 0);
        Require(!VisibilityMatrix(a, b, volume, box) && box == validBox, "Invalid camera far plane changed visibility");
        trees[24] = 210;
        Require(!TreeVisibilityMatrix(a, b, trees, treeBox) && treeBox == validTrees, "Invalid camera accepted for tree visibility");
        ProjectionScope inactive(nullptr, nullptr, nullptr, nullptr, fov);
        Require(!inactive.Valid() && inactive.Uploads() == 0, "Projection scope active without guarded installation");
        SetSceneProjectionActive(true);
        ProjectionScope notInstalled(nullptr, nullptr, nullptr, nullptr, fov);
        Require(!notInstalled.Valid() && !NativeStereoDisabled(nullptr), "Activation bypassed installation guard");
        SetSceneProjectionActive(false);
        // A synthetic loaded image exercises the actual read-only device guard,
        // including rejection of vendor handles without invoking their API.
        std::vector<unsigned char> image(0xb07000), renderer(0x30), device(0x4d80);
        const auto imageBase = reinterpret_cast<uintptr_t>(image.data());
        const auto Put = [](void* destination, uintptr_t value) { std::memcpy(destination, &value, sizeof(value)); };
        Put(renderer.data(), imageBase + 0xaac84c);
        Put(renderer.data() + 0x28, reinterpret_cast<uintptr_t>(device.data()));
        Put(device.data(), imageBase + 0xb06ab8);
        Put(image.data() + 0xb06ab8 + 0xd0, imageBase + 0x4a8ca0);
        constexpr unsigned char getter[] = {0x55,0x8b,0xec,0x51,0x83,0xb9,0x60,0x4d,0x00,0x00,0x00,0x7e,0x06,0xb0,0x01,0x8b,0xe5,0x5d,0xc3,0x8b,0x81,0x78,0x4d,0x00,0x00,0x85,0xc0,0x74,0x1a,0x8d,0x4d,0xff,0x51,0x50,0xc6,0x45,0xff,0x00,0xe8,0xa5,0x6d,0x4f,0x00,0x83,0xc4,0x08,0x85,0xc0,0x75,0x05,0x38,0x45,0xff,0x75,0xd6,0x32,0xc0,0x8b,0xe5,0x5d,0xc3};
        std::memcpy(image.data() + 0x4a8ca0, getter, sizeof(getter));
        Require(NativeStereoDisabledAt(renderer.data(), imageBase), "Known disabled native stereo refused");
        Put(device.data() + 0x4d60, 1);
        Require(!NativeStereoDisabledAt(renderer.data(), imageBase), "Native stereo count accepted");
        Put(device.data() + 0x4d60, 0); Put(device.data() + 0x4d78, 1);
        Require(!NativeStereoDisabledAt(renderer.data(), imageBase), "Vendor stereo handle accepted without a proven disabled state");
        Put(device.data() + 0x4d78, 0); Put(device.data() + 0x4d60, UINT32_MAX);
        Require(NativeStereoDisabledAt(renderer.data(), imageBase), "Signed native stereo count interpreted as unsigned");
        image[0x4a8ca0 + 20] ^= 1;
        Require(!NativeStereoDisabledAt(renderer.data(), imageBase), "Changed getter body accepted");
        image[0x4a8ca0 + 20] ^= 1; Put(device.data(), imageBase + 0xb06abc);
        Require(!NativeStereoDisabledAt(renderer.data(), imageBase), "Wrong device vtable accepted");
        Require(!NativeStereoDisabledAt(nullptr, imageBase), "Missing renderer accepted");
        puts("Scene projection: eye rays, native depth, bounded main and tree visibility, activation and guarded native stereo refusal passed");
        return 0;
    } catch(const std::exception& error) { puts(error.what()); return 1; }
}
