#pragma once
#include <d3d11.h>
#include <cstdint>

namespace dirt3vr {
inline constexpr uint64_t IntroVideoVertexHash=0x65de59dd5b27d083ull;
inline constexpr uint64_t IntroVideoPixelHash=0x443b53fbfc6b08fbull;
inline constexpr uint64_t StereoTransitionVertexHash=0xac773a7adea2a83bull;
inline constexpr uint64_t StereoTransitionPixelHash=0xd0a420252e5afa9full;

// Read-only classification of the two captured presentation paths. The caller
// supplies hashes from the currently bound shaders and owns phase/eye routing.
// backbuffer is a borrowed identity only; it is never dereferenced or retained.
bool IsIntroVideoDraw(ID3D11DeviceContext* context,ID3D11Texture2D* backbuffer,
    uint64_t vertexHash,uint64_t pixelHash,unsigned count,unsigned instances) noexcept;
bool IsStereoTransitionDraw(ID3D11DeviceContext* context,ID3D11Texture2D* backbuffer,
    uint64_t vertexHash,uint64_t pixelHash,unsigned count,unsigned instances) noexcept;
}
