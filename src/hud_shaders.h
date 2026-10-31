// Generated from the fingerprinted DiRT3 UI pack; see docs/research/ui-shaders.json.
#pragma once
#include "hud_capture.h"

namespace dirt3vr {
inline HudShaderKind ClassifyHudShader(uint64_t hash) noexcept {
    switch(hash) {
    case 0x06e9f99f8c7a6013ull:
    case 0x07345ad5abf42b5bull:
    case 0x0ae52f79b03ca080ull:
    case 0x13ae827ac40ce242ull:
    case 0x1406f76ecab9ba8full:
    case 0x15d77135e29b88b4ull:
    case 0x1ba1ddb867c547ebull:
    case 0x1d76a39c30cc7d67ull:
    case 0x23a4703838caa676ull:
    case 0x2422e720984bee76ull:
    case 0x274d942fd3943ea7ull:
    case 0x2821a2e835a41c2cull:
    case 0x2ab8db33b437db4bull:
    case 0x32683589bca8625full:
    case 0x33b5d90e58382297ull:
    case 0x4048b06dbc441301ull:
    case 0x48568cb45bb10104ull:
    case 0x53d9e62b3948014eull:
    case 0x5a53ffa4b48d1019ull:
    case 0x62d80bf11d3e5ab7ull:
    case 0x6fbed0c81e1d9692ull:
    case 0x73bf1794fa3e656aull:
    case 0x81ff2db6a86b455full:
    case 0x875c85f969aceb9aull:
    case 0x97d55f9405d63b47ull:
    case 0x99a265704370c56cull:
    case 0xafe1972df59b098eull:
    case 0xbf7067ded06c98d9ull:
    case 0xc46c74ef18b1baccull:
    case 0xc9bd59e913accfbbull:
    case 0xcabffb92ebab0822ull:
    case 0xcce633c8d550a2c4ull:
    case 0xd7b9c3b372420a58ull:
    case 0xebc341f6acd68fc4ull:
    case 0xfc4a9474f16bffa2ull:
        return HudShaderKind::TwoDimensional;
    case 0x10c642b6890fa454ull:
    case 0x357d1e86c2d39a60ull:
    case 0x5c571e5a5daa0ce4ull:
    case 0xa065b21965a2d4c1ull:
    case 0xad7b09c4361f558dull:
    case 0xe057d253ecd4296eull:
        return HudShaderKind::Shared;
    default: return HudShaderKind::None;
    }
}
}
