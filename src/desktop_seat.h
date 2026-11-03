#pragma once
#include <dxgi.h>

namespace dirt3vr::desktopseat {
// The in-game seat menu on the monitor (#35826889): in a monitor session's seat channel (offset 216),
// each Present of the game's swapchain runs the menu in the cockpit view, moves the selected head
// camera's eye point and field of view live (src/head_camera.h), and draws the open panel over the
// left of the game image, as DiRT2VR does. VR sessions run their menu in the VR renderer instead.
void BeforePresent(IDXGISwapChain* chain,void (*log)(const char*,...)) noexcept;
}
