#include "performance_display.h"
#include <array>
#include <cstdio>
#include <cstring>

namespace dirt3vr {
namespace {
// GRID2VR's 5×7 glyphs for the strip's characters.
std::array<unsigned char,7> Glyph(char c) {
    switch(c) {
    case '0':return {14,17,19,21,25,17,14};case '1':return {4,12,4,4,4,4,14};
    case '2':return {14,17,1,2,4,8,31};case '3':return {30,1,1,14,1,1,30};
    case '4':return {2,6,10,18,31,2,2};case '5':return {31,16,16,30,1,1,30};
    case '6':return {14,16,16,30,17,17,14};case '7':return {31,1,2,4,8,8,8};
    case '8':return {14,17,17,14,17,17,14};case '9':return {14,17,17,15,1,1,14};
    case 'F':return {31,16,16,30,16,16,16};case 'P':return {30,17,17,30,16,16,16};
    case 'S':return {15,16,16,14,1,1,30};case 'G':return {14,17,16,23,17,17,14};
    case 'U':return {17,17,17,17,17,17,14};case 'N':return {17,25,25,21,19,19,17};
    case 'A':return {14,17,17,31,17,17,17};case '/':return {1,2,2,4,8,8,16};
    case '.':return {0,0,0,0,0,12,12};case '%':return {25,25,2,4,8,19,19};
    default:return {};
    }
}
}
std::vector<uint32_t> PerformanceStrip::Pixels(double fps,double gpu) {
    std::vector<uint32_t> pixels(Width*Height,0xff181818);
    for(unsigned y=0;y<Height;++y) for(unsigned x=0;x<4;++x) pixels[y*Width+x]=0xff3030df;
    char text[64],a[24],b[24];
    if(!(fps>=0)) strcpy_s(a,"N/A"); else sprintf_s(a,"%.1f",fps);
    if(!(gpu>=0)) strcpy_s(b,"N/A"); else sprintf_s(b,"%.0f%%",gpu);
    sprintf_s(text,"FPS %s  GPU %s",a,b);
    unsigned left=16;
    for(char c:text) {
        if(!c || left+20>=Width) break;
        const auto rows=Glyph(c);
        for(unsigned y=0;y<7;++y) for(unsigned x=0;x<5;++x) if(rows[y]&(1u<<(4-x)))
            for(unsigned dy=0;dy<4;++dy) for(unsigned dx=0;dx<4;++dx) pixels[(18+y*4+dy)*Width+left+x*4+dx]=0xffeeeeee;
        left+=24;
    }
    return pixels;
}
ID3D11Texture2D* PerformanceStrip::Render(ID3D11Device* device,uint64_t now,double gpu) noexcept {
    try {
        if(!device) return nullptr;
        if(!texture_) {
            D3D11_TEXTURE2D_DESC desc{}; desc.Width=Width; desc.Height=Height; desc.MipLevels=desc.ArraySize=1;
            desc.Format=DXGI_FORMAT_R8G8B8A8_UNORM; desc.SampleDesc.Count=1; desc.Usage=D3D11_USAGE_DEFAULT; desc.BindFlags=D3D11_BIND_SHADER_RESOURCE;
            if(FAILED(device->CreateTexture2D(&desc,nullptr,&texture_))) return nullptr;
            drawn_=false;
        }
        if(drawn_ && now-updated_<1000) return texture_.Get();
        const auto pixels=Pixels(rate_.Fps(),gpu);
        Microsoft::WRL::ComPtr<ID3D11DeviceContext> context; device->GetImmediateContext(&context);
        // The game may have predication set; the upload must not be skipped by it.
        Microsoft::WRL::ComPtr<ID3D11Predicate> predicate; BOOL predicateValue{};
        context->GetPredication(&predicate,&predicateValue); context->SetPredication(nullptr,FALSE);
        context->UpdateSubresource(texture_.Get(),0,nullptr,pixels.data(),Width*4,0);
        context->SetPredication(predicate.Get(),predicateValue);
        updated_=now; drawn_=true;
        return texture_.Get();
    } catch(...) { return nullptr; }
}
}
