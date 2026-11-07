#pragma once
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <cstring>

namespace dirt3vr::steeringshake {
inline constexpr uintptr_t updateRva=0x81ced0, scaleRva=0x81cf60, storeRva=0x81cfda, callerRva=0x85b790;
inline constexpr size_t visualOffset=0x258;
inline constexpr uint8_t entry[]{0x55,0x8b,0xec,0x51,0x56,0x8b,0xf1,0x57,0x8b,0x7e,0x78,0x85,0xff};
inline constexpr uint8_t scale[]{0xf3,0x0f,0x59,0x8f,0x34,0x03,0x00,0x00};
inline constexpr uint8_t store[]{0xf3,0x0f,0x11,0x8e,0x58,0x02,0x00,0x00};
inline constexpr uint8_t caller[]{0xe8,0x3b,0x17,0xfc,0xff};
inline bool SitesMatch(const uint8_t* image) noexcept {
    return image && !std::memcmp(image+updateRva,entry,sizeof(entry)) &&
        !std::memcmp(image+scaleRva,scale,sizeof(scale)) && !std::memcmp(image+storeRva,store,sizeof(store)) &&
        !std::memcmp(image+callerRva,caller,sizeof(caller));
}
// The original routine has already advanced its timer/RNG and computed its target.
// Only its published animation offset changes; timer +25c, target +260 and raw input +264 stay intact.
inline bool ClearVisualOffset(void* component) noexcept {
    if(!component) return false;
    auto* destination=static_cast<uint8_t*>(component)+visualOffset;
    float value{}; std::memcpy(&value,destination,sizeof(value));
    if(!std::isfinite(value)) return false;
    const float zero{}; std::memcpy(destination,&zero,sizeof(zero));
    return true;
}
bool Requested();
// Caller must first verify the supported executable profile, as for the other native features.
bool Install(void (*log)(const char*,...));
}
