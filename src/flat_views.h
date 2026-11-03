#pragma once
#include <string_view>

namespace dirt3vr::flatviews {
// Views the player can send to the flat virtual screen instead of stereo (#48208327), by the game
// camera's name: more reliable than the classified kind, which briefly reads unknown even in the
// cockpit during transitions. The cockpit (head-cam, and its look-back head-cam_reverse) is never flat.
enum Group : unsigned { None = 0, Car = 1, Cinematic = 2, Menus = 4 };

inline Group Classify(std::string_view name) noexcept {
    if(name.empty()) return Menus; // DiRT 3's 3D menu backgrounds have no camera name.
    if(name.ends_with("_reverse")) name.remove_suffix(8);
    if(name == "head-cam") return None;
    if(name == "chase_close" || name == "chase_far" || name == "bonnet" || name == "bumper") return Car;
    return Cinematic; // Race intros, establishing shots, montages, the service area, replay cameras.
}
// DIRT3VR_FLAT_VIEWS: a comma-separated subset of car, cinematic, menus. Anything else refuses the
// whole list, so a typo never sends views flat unexpectedly.
inline bool Parse(std::string_view text, unsigned& groups) noexcept {
    unsigned result{};
    while(!text.empty()) {
        const auto comma = text.find(',');
        const auto word = text.substr(0, comma);
        if(word == "car") result |= Car;
        else if(word == "cinematic") result |= Cinematic;
        else if(word == "menus") result |= Menus;
        else return false;
        if(comma == std::string_view::npos) break;
        text.remove_prefix(comma + 1);
        if(text.empty()) return false;
    }
    groups = result;
    return true;
}
}
