#pragma once
#include <cstddef>
#include <cstdint>
#include <vector>

struct IDXGISwapChain;

// Diagnostic screenshots: with DIRT3VR_SCREENSHOT_SECONDS="30,90,150" (and DIRT3VR_LOG_DIR),
// the frame the game is about to present is saved as screenshot-<n>-<seconds>s.bmp once each
// listed time (seconds since the first observed Present) is reached. Reads the back buffer
// only; works fullscreen and with the window in the background. Never set by the launcher.
namespace dirt3vr::screenshot {
inline constexpr size_t maxShots=16;
inline constexpr unsigned maxSeconds=3600;

// Strictly ascending whole seconds, 1-3600, at most 16; empty for anything else.
std::vector<unsigned> ParseSchedule(const wchar_t* text);
// 32-bit top-down BMP. rows holds height rows of width BGRA pixels, pitch bytes apart;
// swapRedBlue converts RGBA rows. Alpha is written as opaque.
std::vector<uint8_t> Bmp(uint32_t width,uint32_t height,const uint8_t* rows,size_t pitch,bool swapRedBlue);
// The same from R10G10B10A2 rows (DiRT 3's usual back buffer), keeping the top 8 bits.
std::vector<uint8_t> Bmp10(uint32_t width,uint32_t height,const uint8_t* rows,size_t pitch);
// Call before the real Present on the game's render thread.
void BeforePresent(IDXGISwapChain* chain,void (*log)(const char*,...));
}
