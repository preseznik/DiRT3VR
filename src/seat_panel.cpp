#include "seat_panel.h"
#include <algorithm>
#include <objidl.h>
namespace Gdiplus { using std::min; using std::max; } // gdiplus.h expects min/max; the build defines NOMINMAX.
#include <gdiplus.h>
#include <cwchar>
#include <stdexcept>

namespace dirt3vr {
struct SeatPanelImage::Gdi {
    ULONG_PTR token{};
    std::unique_ptr<Gdiplus::Bitmap> bitmap;
    ~Gdi() { bitmap.reset(); if(token) Gdiplus::GdiplusShutdown(token); }
};
namespace {
std::wstring Wide(const std::string& text) {
    const int length=MultiByteToWideChar(CP_UTF8,0,text.data(),int(text.size()),nullptr,0);
    std::wstring value(size_t(std::max(length,0)),L'\0');
    if(length>0) MultiByteToWideChar(CP_UTF8,0,text.data(),int(text.size()),value.data(),length);
    return value;
}
std::wstring KeyName(seat::Channel::Key binding) {
    wchar_t name[64]{};
    if(!binding.key || !GetKeyNameTextW(LONG(MapVirtualKeyW(binding.key,MAPVK_VK_TO_VSC)<<16),name,64)) swprintf_s(name,L"Key %u",binding.key);
    return std::wstring(binding.modifiers&1 ? L"Ctrl+" : L"")+(binding.modifiers&2 ? L"Alt+" : L"")+(binding.modifiers&4 ? L"Shift+" : L"")+name;
}
}
SeatPanelImage::SeatPanelImage()=default;
SeatPanelImage::~SeatPanelImage()=default;
ID3D11Texture2D* SeatPanelImage::Render(ID3D11Device* device,const seat::Menu& menu) noexcept {
    try {
        if(!device) return nullptr;
        if(!gdi_) {
            auto gdi=std::make_unique<Gdi>();
            Gdiplus::GdiplusStartupInput startup;
            if(Gdiplus::GdiplusStartup(&gdi->token,&startup,nullptr)!=Gdiplus::Ok) { gdi->token=0; return nullptr; }
            gdi->bitmap=std::make_unique<Gdiplus::Bitmap>(INT(Width),INT(Height),PixelFormat32bppPARGB);
            gdi_=std::move(gdi);
        }
        if(!texture_) {
            D3D11_TEXTURE2D_DESC desc{}; desc.Width=Width; desc.Height=Height; desc.MipLevels=desc.ArraySize=1;
            desc.Format=DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count=1; desc.Usage=D3D11_USAGE_DEFAULT; desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
            if(FAILED(device->CreateTexture2D(&desc,nullptr,&texture_))) return nullptr;
            last_.clear();
        }
        const auto& channel=menu.View();
        const std::wstring name=channel.Universal() ? L"All cars" : !menu.CanAdjust() ? L"Car not identified" : Wide(channel.CarName(menu.Active()));
        const auto p=menu.Current();
        wchar_t values[160]{};
        swprintf_s(values,L"Height     %+.1f cm\nDepth      %+.1f cm\nSideways  %+.1f cm",p.y*100,-p.z*100,p.x*100);
        const std::wstring close=KeyName(channel.Binding(0))+L": save & close",hint=channel.HintText();
        const bool sideways=menu.SidewaysHeld();
        const wchar_t* status=!menu.InputReady() ? L"Input filtering unavailable" : !menu.CanAdjust() ? L"Adjustment unavailable for this car" : L"Arrows / D-pad: move";
        const auto signature=name+L'\n'+values+L'\n'+close+L'\n'+hint+L'\n'+status+(sideways ? L"\nside" : L"\ndepth");
        if(signature==last_) return texture_.Get();

        auto& bitmap=*gdi_->bitmap;
        Gdiplus::Graphics g(&bitmap);
        g.SetSmoothingMode(Gdiplus::SmoothingModeAntiAlias);
        g.SetTextRenderingHint(Gdiplus::TextRenderingHintAntiAliasGridFit);
        g.Clear(Gdiplus::Color(238,25,27,29));
        Gdiplus::SolidBrush white(Gdiplus::Color(255,245,245,245)),green(Gdiplus::Color(255,183,235,25));
        Gdiplus::Font title(L"Segoe UI",43,Gdiplus::FontStyleBold,Gdiplus::UnitPixel),normal(L"Segoe UI",29,Gdiplus::FontStyleRegular,Gdiplus::UnitPixel),
            bold(L"Segoe UI",32,Gdiplus::FontStyleBold,Gdiplus::UnitPixel);
        const auto label=[&](const wchar_t* text,float x,float y,float width,float height,Gdiplus::Font& font,Gdiplus::Brush& brush) {
            g.DrawString(text,-1,&font,Gdiplus::RectF(x,y,width,height),nullptr,&brush);
        };
        label(L"SEAT POSITION",40,30,690,65,title,white);
        label(name.c_str(),40,98,690,80,normal,white);
        label(close.c_str(),40,174,690,45,normal,green);
        // Seat pictogram, vector drawn rather than a font glyph.
        Gdiplus::Pen seatPen(Gdiplus::Color(255,245,245,245),24);
        const Gdiplus::PointF seatPoints[]={{330,315},{347,426},{364,480},{445,480},{480,520}};
        g.DrawLines(&seatPen,seatPoints,5);
        const auto arrow=[&](float x,float y,float dx,float dy) {
            Gdiplus::Pen pen(Gdiplus::Color(255,183,235,25),9); g.DrawLine(&pen,x-dx*23,y-dy*23,x+dx*23,y+dy*23);
            const Gdiplus::PointF triangle[]={{x+dx*40,y+dy*40},{x+dx*12-dy*20,y+dy*12+dx*20},{x+dx*12+dy*20,y+dy*12-dx*20}};
            g.FillPolygon(&green,triangle,3);
        };
        arrow(384,254,0,-1); arrow(384,555,0,1); arrow(126,406,-1,0); arrow(642,406,1,0);
        label(L"UP",356,286,90,45,bold,white); label(L"DOWN",335,597,130,45,bold,white);
        label(sideways ? L"LEFT" : L"BACK",55,451,180,45,bold,white); label(sideways ? L"RIGHT" : L"FORWARD",550,451,205,45,bold,white);
        label(values,175,670,550,145,bold,white);
        label(status,40,838,690,45,normal,white);
        label(hint.c_str(),40,886,690,120,normal,white);

        Gdiplus::BitmapData data{}; Gdiplus::Rect rect(0,0,INT(Width),INT(Height));
        if(bitmap.LockBits(&rect,Gdiplus::ImageLockModeRead,PixelFormat32bppPARGB,&data)!=Gdiplus::Ok) return nullptr;
        Microsoft::WRL::ComPtr<ID3D11DeviceContext> context; device->GetImmediateContext(&context);
        context->UpdateSubresource(texture_.Get(),0,nullptr,data.Scan0,UINT(data.Stride),0);
        bitmap.UnlockBits(&data);
        last_=signature;
        return texture_.Get();
    } catch(...) { return nullptr; }
}
}
