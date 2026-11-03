#pragma once
#include <charconv>
#include <cmath>
#include <string_view>
#include <system_error>

namespace dirt3vr {
// The VR HUD panel's placement the launcher passes (#3652b9aa), as in DiRT2VR: DIRT3VR_HUD_DISTANCE
// in metres ("2.5", invariant format) and DIRT3VR_HUD_FOLLOW "1" (the panel follows the view) or
// "0" (fixed where the player recentered). The panel keeps its angular size at any distance, so the
// distance changes only how deep it sits in the scene.
struct HudPlacement {
    static constexpr float DefaultDistance=2.f,MinDistance=1.f,MaxDistance=5.f;
    float distance=DefaultDistance;
    bool follow{};
    // Anything but a plain number within 1–5 m keeps the default. from_chars ignores the game's locale.
    static float ParseDistance(std::string_view text) {
        float value{};
        const auto result=std::from_chars(text.data(),text.data()+text.size(),value,std::chars_format::fixed);
        return !text.empty() && result.ec==std::errc{} && result.ptr==text.data()+text.size() && std::isfinite(value) &&
               value>=MinDistance && value<=MaxDistance ? value : DefaultDistance;
    }
    static bool ParseFollow(std::string_view text) { return text=="1"; }
    // A panel width chosen for the default distance, at this distance.
    float Width(float widthAtDefault) const { return widthAtDefault*distance/DefaultDistance; }
};
}
