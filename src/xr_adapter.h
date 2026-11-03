#pragma once
#include <windows.h>

namespace dirt3vr {
// OpenXR names the graphics adapter a session must use. SteamVR's virtual test headset
// (its null driver) names none: an all-zero LUID. Accept any adapter only then; every
// runtime that names one still needs that exact adapter (DIRT2VR 08e99fd, #0ea87e54).
inline bool AnyAdapter(const LUID& required) noexcept { return required.LowPart==0 && required.HighPart==0; }
inline bool RequiredAdapter(const LUID& required,const LUID& adapter) noexcept {
    return AnyAdapter(required) || (required.LowPart==adapter.LowPart && required.HighPart==adapter.HighPart);
}
}
