#pragma once
#include "scene_observer.h"

namespace dirt3vr {
// Ground cover (#d20e9cf7): in VR, DiRT 3's roadside grass and bushes fill their whole draw distance instead of
// stopping where the main grass view's budget of 10,240 tufts runs out. Install before the game builds its renderer.
// DIRT3VR_GRASS_BUDGET=0 keeps DiRT 3's own budget.
void InstallGroundCoverBudget(DiagnosticLogger logger) noexcept;
}
