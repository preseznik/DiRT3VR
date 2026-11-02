#pragma once
#include "seat_menu.h"
#include <d3d11.h>
#include <wrl/client.h>
#include <memory>
#include <string>

namespace dirt3vr {
// The seat menu's panel as DiRT2VR draws it (src/seat_adjustment.cpp): GDI+ into a
// premultiplied BGRA texture of XrRuntime's seat panel size, for TexturePresenter's
// StraightAlpha mode. Redrawn only when its text changes. Render thread only.
class SeatPanelImage {
public:
    static constexpr unsigned Width=768,Height=1024;
    SeatPanelImage();
    ~SeatPanelImage();
    SeatPanelImage(const SeatPanelImage&)=delete;
    SeatPanelImage& operator=(const SeatPanelImage&)=delete;
    // The panel for the menu's current state, or nullptr if it can't be drawn.
    ID3D11Texture2D* Render(ID3D11Device* device,const seat::Menu& menu) noexcept;
private:
    struct Gdi;
    std::unique_ptr<Gdi> gdi_;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> texture_;
    std::wstring last_;
};
}
