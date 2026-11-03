#pragma once
#include "seat_menu.h"
#include <string>

namespace dirt3vr {
// The launcher's seat channel (DIRT3VR_SEAT_CHANNEL, docs/seat-channel.md) and the in-game seat menu
// on it, with the input filters installed (src/seat_input.*). One per process, shared by VR and monitor
// sessions; the first caller opens it. nullptr without a valid channel.
seat::Menu* OpenSeatMenu(void (*log)(const char*,...)) noexcept;
// The player's car code in the race (#88b7f778): entry 0 of DiRT 3's vehicle table; empty if unknown.
std::string PlayerCarCode() noexcept;
}
