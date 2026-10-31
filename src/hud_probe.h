#pragma once
#include <d3d11.h>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <array>

namespace dirt3vr {
inline constexpr GUID ProbeLayoutTag={0xaf853328,0x1072,0x4ae9,{0x89,0x1d,0xcf,0x3c,0x67,0x27,0x9a,0x68}};
struct ProbeLayoutElement {
    char semantic[32]{};
    unsigned index{},format{},slot{},offset{},classification{},step{};
};
struct ProbeLayout { unsigned count{}; std::array<ProbeLayoutElement,32> elements{}; };
// Explicit one-frame diagnostic. Never reads GPU data during ordinary play.
// Each process permits three captures, with at most 256 backbuffer draws each.
class HudProbe {
public:
    bool Begin(const std::filesystem::path& directory,uint64_t frame) noexcept;
    void Record(ID3D11DeviceContext* context,ID3D11Texture2D* back,
                uint64_t vertexHash,uint64_t pixelHash,const char* draw,
                unsigned count,unsigned instances,unsigned first,int base,unsigned startInstance) noexcept;
    void Finish(ID3D11Texture2D* image) noexcept;
    bool Active() const noexcept { return output_.is_open(); }
    unsigned Count() const noexcept { return count_; }
private:
    std::ofstream output_;
    std::filesystem::path prefix_;
    unsigned captures_{},count_{};
};
}
