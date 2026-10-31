#include "camera_classifier.h"
#include "scene_camera.h"
#include <cstdio>
#include <bit>
#include <cstring>
#include <limits>
#include <stdexcept>
#include <vector>

namespace {
void Require(bool value, const char* message) { if(!value) throw std::runtime_error(message); }
constexpr uint32_t Base = 0x400000, Renderer = 0x2000000, Root = 0x2010000, Game = 0x2020000;
constexpr uint32_t Controller = 0x2030000, Manager = 0x2040000, Entries = 0x2050000, Owner = 0x2060000, Rear = 0x2070000;
struct Region { uint32_t address; std::vector<unsigned char> data; };
struct Memory {
    std::vector<Region> regions;
    unsigned sourceReads{};
    bool changeOwner{};
    void Add(uint32_t address, size_t size) { regions.push_back({address, std::vector<unsigned char>(size)}); }
    void* At(uint32_t address, size_t length) {
        for(auto& region : regions) if(address >= region.address && address - region.address <= region.data.size() &&
            length <= region.data.size() - (address - region.address)) return region.data.data() + address - region.address;
        throw std::runtime_error("Test fixture address missing");
    }
    template<typename T> void Set(uint32_t address, T value) { std::memcpy(At(address, sizeof(value)), &value, sizeof(value)); }
    static bool Read(uintptr_t address, void* output, size_t length, void* context) noexcept {
        auto& memory = *static_cast<Memory*>(context);
        try {
            if(address == Owner && length == 0x90 && ++memory.sourceReads == 2 && memory.changeOwner)
                memory.Set(Owner + 0x40, 100.f);
            std::memcpy(output, memory.At(uint32_t(address), length), length); return true;
        } catch(...) { return false; }
    }
    void Name(uint32_t object, const char* name) {
        std::memset(At(object + 0xa1, 64), 0, 64);
        std::memcpy(At(object + 0xa1, std::strlen(name)), name, std::strlen(name));
    }
    Memory() {
        Add(Base + 0xbe7404, 4); Add(Base + 0xbf7e50, 0x90);
        Add(Renderer, 0x800); Add(Root, 0x10); Add(Game, 0x1200);
        Add(Controller, 0x300); Add(Manager, 0x600); Add(Entries, 2*0x280);
        Add(Owner, 0x1a60); Add(Rear, 0x1a60);
        Set(Base + 0xbe7404, Root); Set(Root + 8, Game); Set(Game + 0x11b4, Controller);
        Set(Controller + 0x24, Manager); Set(Manager, Entries); Set(Manager + 4, Entries + 2*0x280);
        Set(Entries, Owner); Set(Entries + 0x280, Rear);
        Set(Renderer, Base + 0xaac84c);
        dirt3vr::CameraRecord camera{};
        const auto Float = [&](size_t offset, float value) { std::memcpy(camera.data() + offset, &value, 4); };
        Float(0x10, 1); Float(0x24, 1); Float(0x38, 1); Float(0x40, 5); Float(0x44, 2);
        Float(0x50, 50); Float(0x54, .075f); Float(0x58, 1000);
        for(auto address : {Owner, Rear, Controller + 0x150, Base + 0xbf7e50, Renderer + 0x600, Renderer + 0x690})
            std::memcpy(At(address, camera.size()), camera.data(), camera.size());
        Set(Renderer + 0x600, Base + 0xa85b50); Set(Renderer + 0x690, Base + 0xa85b50);
        Set(Owner, Base + 0xb28530); Set(Owner + 0x9c, .075f);
        Set<unsigned char>(Owner + 0x1a4f, 1); Set<unsigned char>(Owner + 0x1a50, 1);
        Name(Owner, "head-cam"); Name(Rear, "head-cam_reverse");
    }
    dirt3vr::CameraClassification Inspect() {
        sourceReads = 0;
        return dirt3vr::classifier_detail::Inspect(Base, Renderer, Renderer + 0x600, Renderer + 0x690, Read, this);
    }
};
}
int main() {
    try {
        using namespace dirt3vr;
        classifier_detail::MismatchLogBudget budget;
        Require(!budget.Reserve(0, 0).count, "Empty sample consumed a detail line");
        const auto first = budget.Reserve(0, 4);
        Require(first.first == 1 && first.count == 4, "First mismatch group was delayed");
        Require(!budget.Reserve(999, 4).count, "Persistent mismatch sampled before one second");
        const auto settled = budget.Reserve(1000, 4);
        Require(settled.first == 5 && settled.count == 4, "Settled mismatch was not resampled after one second");
        Require(!budget.Reserve(999, 4).count && !budget.Reserve(1999, 4).count,
                "Backward clock or early sample consumed the remaining budget");
        const auto third = budget.Reserve(2000, 3);
        const auto finalLine = budget.Reserve(3000, 4);
        Require(third.first == 9 && third.count == 3 && finalLine.first == 12 && finalLine.count == 1,
                "Diagnostic group exceeded the twelve-line budget");
        Require(!budget.Reserve(4000, 4).count && !budget.Reserve(UINT64_MAX, 1).count,
                "Exhausted diagnostic budget restarted");
        Memory good;
        const auto cockpit = good.Inspect();
        Require(cockpit.stereoEligible && cockpit.kind == CameraKind::Cockpit && cockpit.owner == Owner, "Matching displayed Head rejected");
        Memory clipped;
        clipped.Set(Owner + 0x58, 2000.f); // Exact difference captured in PID 31784.
        const auto clippedCockpit = clipped.Inspect();
        Require(clippedCockpit.stereoEligible && clippedCockpit.kind == CameraKind::Cockpit,
                "Native 2000-to-1000 far clipping still blocks the cockpit");
        Require(!ClassifyMainCamera(nullptr, nullptr, nullptr).stereoEligible, "Stereo eligible before layout guards");
        Memory wrongRenderer; wrongRenderer.Set(Renderer, Base + 0xa85b50);
        Require(!wrongRenderer.Inspect().stereoEligible, "Foreign renderer accepted for stereo");
        Memory wrongRecord; wrongRecord.Set(Renderer + 0x600, Base + 0xb28530);
        Require(!wrongRecord.Inspect().stereoEligible, "Foreign inline camera record accepted for stereo");
        for(auto address : {Base + 0xbf7e50, Renderer + 0x600, Renderer + 0x690}) {
            Memory nonfinite; nonfinite.Set(address + 0x40, std::numeric_limits<float>::quiet_NaN());
            Require(!nonfinite.Inspect().stereoEligible, "Non-finite displayed pose accepted for stereo");
            Memory invalidClip; invalidClip.Set(address + 0x58, .01f);
            Require(!invalidClip.Inspect().stereoEligible, "Invalid displayed clip range accepted for stereo");
        }
        Require(cockpit.gameplay == StateKnowledge::Unknown && cockpit.paused == StateKnowledge::Unknown &&
                cockpit.replay == StateKnowledge::Unknown, "Unverified gameplay state asserted");
        Require(!classifier_detail::VerifyLayouts(Base, Memory::Read, &good), "Missing instruction guards accepted");
        Require(ClassifyMainCamera(nullptr, nullptr, nullptr).reason == CameraReason::GuardUnavailable,
                "Classifier active before guards");
        Memory wrongType; wrongType.Set(Owner, Base + 0xa85b50);
        Require(wrongType.Inspect().kind == CameraKind::Unknown, "Near and name alone accepted without Head type");
        Memory wrongPose; wrongPose.Set(Renderer + 0x600 + 0x40, 5.00001f);
        const auto poseMismatch = wrongPose.Inspect();
        Require(!poseMismatch.stereoEligible && poseMismatch.reason == CameraReason::DisplayMismatch, "Stale displayed pose accepted with guessed tolerance");
        const auto& poseDetail = poseMismatch.comparisons[2];
        Require(poseDetail.link == CameraComparisonLink::GlobalToMainA && poseDetail.count && poseDetail.differences[0].offset == 0x40 &&
                poseDetail.differences[0].leftBits == std::bit_cast<uint32_t>(5.f) && poseDetail.differences[0].rightBits == std::bit_cast<uint32_t>(5.00001f) &&
                poseDetail.differences[0].leftValue == 5.f && poseDetail.differences[0].rightValue == 5.00001f &&
                !poseMismatch.comparisons[0].count && !poseMismatch.comparisons[1].count && !poseMismatch.comparisons[3].count,
                "Pose diagnostic did not isolate the global-to-mainA copy link");
        Memory wrongLens; wrongLens.Set(Base + 0xbf7e50 + 0x50, 60.f);
        const auto lensMismatch = wrongLens.Inspect();
        Require(!lensMismatch.stereoEligible && lensMismatch.reason == CameraReason::DisplayMismatch, "Different displayed FOV accepted");
        Require(!lensMismatch.comparisons[0].count && lensMismatch.comparisons[1].link == CameraComparisonLink::SelectedToGlobal &&
                lensMismatch.comparisons[1].differences[0].offset == 0x50 && lensMismatch.comparisons[1].differences[0].leftValue == 50.f && lensMismatch.comparisons[1].differences[0].rightValue == 60.f &&
                lensMismatch.comparisons[2].differences[0].offset == 0x50 && lensMismatch.comparisons[2].differences[0].leftValue == 60.f && lensMismatch.comparisons[2].differences[0].rightValue == 50.f &&
                lensMismatch.comparisons[3].differences[0].offset == 0x50,
                "FOV diagnostic lost propagation direction between selected, global and renderer snapshots");
        Memory wrongFar; wrongFar.Set(Renderer + 0x690 + 0x58, 2000.f);
        const auto farMismatch = wrongFar.Inspect();
        Require(!farMismatch.stereoEligible && farMismatch.reason == CameraReason::DisplayMismatch && farMismatch.comparisons[3].link == CameraComparisonLink::GlobalToMainB &&
                farMismatch.comparisons[3].count && farMismatch.comparisons[3].differences[0].offset == 0x58 &&
                farMismatch.comparisons[3].differences[0].leftValue == 1000.f && farMismatch.comparisons[3].differences[0].rightValue == 2000.f,
                "Far-plane mismatch was relaxed or not identified");
        Memory wrongSelected; wrongSelected.Set(Controller + 0x150 + 0x44, 3.f);
        const auto selectedMismatch = wrongSelected.Inspect();
        Require(selectedMismatch.reason == CameraReason::DisplayMismatch && selectedMismatch.comparisons[0].link == CameraComparisonLink::OwnerToSelected &&
                selectedMismatch.comparisons[0].count && selectedMismatch.comparisons[0].differences[0].offset == 0x44 &&
                selectedMismatch.comparisons[0].differences[0].leftValue == 2.f && selectedMismatch.comparisons[0].differences[0].rightValue == 3.f &&
                selectedMismatch.comparisons[1].count && !selectedMismatch.comparisons[2].count && !selectedMismatch.comparisons[3].count,
                "Owner-to-selected mismatch was attributed to renderer records");
        Memory signedZero; signedZero.Set(Renderer + 0x600 + 0x14, -0.f);
        const auto zeroMismatch = signedZero.Inspect();
        Require(zeroMismatch.reason == CameraReason::DisplayMismatch && zeroMismatch.comparisons[2].differences[0].offset == 0x14 &&
                zeroMismatch.comparisons[2].differences[0].leftBits == 0 && zeroMismatch.comparisons[2].differences[0].rightBits == 0x80000000u &&
                zeroMismatch.comparisons[2].differences[0].leftValue == zeroMismatch.comparisons[2].differences[0].rightValue,
                "Diagnostics changed bit-exact signed-zero acceptance or lost exact bits");
        Memory allFields;
        constexpr uint32_t comparedOffsets[] = {0x10,0x14,0x18,0x20,0x24,0x28,0x30,0x34,0x38,0x40,0x44,0x48,0x50,0x54,0x58};
        for(const auto offset : comparedOffsets) {
            float value{}; std::memcpy(&value, allFields.At(Renderer + 0x600 + offset, 4), 4);
            allFields.Set(Renderer + 0x600 + offset, value + .01f);
        }
        // Padding must remain excluded even when every compared float differs.
        allFields.Set(Renderer + 0x600 + 0x1c, std::numeric_limits<float>::quiet_NaN());
        const auto allMismatch = allFields.Inspect();
        const auto& allDetail = allMismatch.comparisons[2];
        Require(!allMismatch.stereoEligible && allMismatch.reason == CameraReason::DisplayMismatch && allDetail.count == 15 &&
                !allMismatch.comparisons[0].count && !allMismatch.comparisons[1].count && !allMismatch.comparisons[3].count,
                "Complete mismatch details were truncated or attributed to another link");
        for(unsigned i = 0; i < 15; ++i) {
            const auto& difference = allDetail.differences[i];
            float original{}, changed{};
            std::memcpy(&original, allFields.At(Base + 0xbf7e50 + comparedOffsets[i], 4), 4);
            std::memcpy(&changed, allFields.At(Renderer + 0x600 + comparedOffsets[i], 4), 4);
            Require(difference.offset == comparedOffsets[i] && difference.leftBits == std::bit_cast<uint32_t>(original) &&
                    difference.rightBits == std::bit_cast<uint32_t>(changed) && difference.leftValue == original && difference.rightValue == changed,
                    "Complete mismatch detail lost a pose/lens offset, exact bits or value");
        }
        Memory invalid; invalid.Set(Owner + 0x10, std::numeric_limits<float>::quiet_NaN());
        Require(invalid.Inspect().reason == CameraReason::InvalidCamera, "Non-finite source accepted");
        Memory reverse; reverse.Set<unsigned char>(Manager + 0x139, 1); reverse.Set<uint32_t>(Owner + 0x118, 1);
        reverse.Set<uint32_t>(Owner + 0x13c, 1);
        Require(reverse.Inspect().stereoEligible && reverse.Inspect().kind == CameraKind::Unknown && reverse.Inspect().owner == Rear,
                "Reverse view blocked from stereo or misclassified as cockpit");
        reverse.Set<uint32_t>(Owner + 0x13c, 2);
        Require(reverse.Inspect().reason == CameraReason::InvalidSelection, "Out-of-range reverse entry read");
        Memory fallback; fallback.Set<uint32_t>(Manager + 0x140, 2);
        Require(fallback.Inspect().reason == CameraReason::InvalidSelection, "Unknown fallback accepted");
        Memory malformed; malformed.Set<uint32_t>(Manager + 4, Entries + 0x281);
        Require(malformed.Inspect().reason == CameraReason::InvalidSelection, "Misaligned manager entries accepted");
        Memory alternate; alternate.Set<unsigned char>(Controller + 0x11, 1);
        Require(alternate.Inspect().stereoEligible && alternate.Inspect().reason == CameraReason::AlternateController,
                "Alternate displayed view lost stereo or was mislabeled");
        alternate.Set<unsigned char>(Controller + 0x11, 0); alternate.Set<unsigned char>(Controller + 0x12, 1);
        Require(alternate.Inspect().reason == CameraReason::AlternateController, "Second alternate camera branch accepted");
        Memory changing; changing.changeOwner = true;
        Require(changing.Inspect().reason == CameraReason::IdentityChanged, "Changing source retained a positive classification");
        Memory external; external.Name(Owner, "chase_close");
        Require(external.Inspect().kind == CameraKind::External, "Matched named external view rejected");
        external.Name(Owner, "replay_driver1");
        Require(external.Inspect().stereoEligible && external.Inspect().kind == CameraKind::Unknown,
                "Replay view blocked from stereo or mislabeled as external driving camera");
        for(const auto* name : {"head-cam", "bonnet", "bumper", "chase_close", "chase_far", "head-cam_reverse",
                              "replay_driver1", "replay_wheel1", "initial_camera_r0", "custom-camera"}) {
            Memory view; view.Name(Owner, name);
            view.Set(Owner + 0x58, 2000.f);
            Require(view.Inspect().stereoEligible, "Camera name restricted a valid displayed 3D scene");
        }
        Memory noOwner; noOwner.Set<uint32_t>(Controller + 0x24, 0);
        Require(noOwner.Inspect().stereoEligible && noOwner.Inspect().reason == CameraReason::MissingOwner,
                "A valid displayed scene requires optional vehicle owner metadata");
        noOwner.Set(Renderer + 0x600 + 0x40, 6.f);
        Require(!noOwner.Inspect().stereoEligible, "Missing owner bypassed displayed-camera matching");
        Memory padding; padding.Set(Renderer + 0x600 + 0x1c, std::numeric_limits<float>::quiet_NaN());
        Require(padding.Inspect().kind == CameraKind::Cockpit, "Uninterpreted padding treated as pose");
        Memory unterminated; std::memset(unterminated.At(Owner + 0xa1, 64), 'x', 64);
        Require(unterminated.Inspect().reason == CameraReason::InvalidSelection, "Unbounded camera name accepted");
        puts("Camera classifier: observed far clipping, all-view stereo eligibility, displayed-state refusal and bounded diagnostics passed");
        return 0;
    } catch(const std::exception& error) { puts(error.what()); return 1; }
}
