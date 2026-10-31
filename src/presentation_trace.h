#pragma once
#include <d3d11.h>
#include <array>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <atomic>

namespace dirt3vr {
// Opt-in CPU metadata only. Private resource tags contain values, never COM
// references; the trace cannot keep a swapchain backbuffer alive across resize.
class PresentationTrace {
public:
    bool Configure(const std::filesystem::path& directory) noexcept;
    void BindBackbuffer(ID3D11Texture2D* texture) noexcept;
    void BeginEye(unsigned eye) noexcept;
    void EndEye() noexcept;
    void BeforePresent() noexcept;
    void Record(ID3D11DeviceContext* context,uint64_t vertexHash,uint64_t pixelHash,
                const char* draw,unsigned count,unsigned instances,unsigned first,
                int base,unsigned startInstance) noexcept;
    bool Active() const noexcept;
    bool Enabled() const noexcept;
    bool Wants(uint64_t vertexHash,unsigned count=0,unsigned instances=0) const noexcept;
    unsigned Records() const noexcept { return records_; }
    unsigned Pipelines() const noexcept { return pipelines_; }
private:
    struct Pipeline {
        std::array<uint64_t,11> key{};
        uint64_t occurrences{};
        unsigned samples{};
    };
    bool Write(const std::string& line) noexcept;
    std::ofstream output_;
    std::array<Pipeline,256> pipeline_{};
    uint64_t started_{},backbuffer_{},sequence_{},signature_{},previousSignature_{};
    unsigned records_{},pipelines_{},summaries_{},movieDraws_{},tailDraws_{},screenDraws_{};
    unsigned previousMovieDraws_{},previousTailDraws_{},previousScreenDraws_{};
    int eye_{-1};
    bool configured_{},previousSummary_{};
    std::atomic<DWORD> ownerThread_{};
};
}
