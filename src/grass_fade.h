#pragma once
#include "scene_observer.h"

namespace dirt3vr {
// Ground cover (#d20e9cf7): in VR, roadside grass fades out by 70 % of DiRT 3's grass distance instead of at its end,
// so grass that is filled in late is already faded instead of popping in. DIRT3VR_GRASS_FADE=0 turns it off.
void InstallGrassFade(DiagnosticLogger logger) noexcept;
}
