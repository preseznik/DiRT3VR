#include "performance_display.h"
#include <cstdio>
#include <cstring>
#include <stdexcept>

namespace {
using Microsoft::WRL::ComPtr;
using dirt3vr::PerformanceStrip;
void Require(bool value,const char* message) { if(!value) throw std::runtime_error(message); }
unsigned TextPixels(const std::vector<uint32_t>& pixels) {
    unsigned count{};
    for(const auto pixel:pixels) count+=pixel==0xffeeeeee;
    return count;
}
std::vector<uint32_t> Download(ID3D11Device* device,ID3D11Texture2D* texture) {
    D3D11_TEXTURE2D_DESC desc{}; texture->GetDesc(&desc);
    desc.Usage=D3D11_USAGE_STAGING; desc.BindFlags=0; desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> staging; ComPtr<ID3D11DeviceContext> context; device->GetImmediateContext(&context);
    Require(SUCCEEDED(device->CreateTexture2D(&desc,nullptr,&staging)),"Staging texture unavailable");
    context->CopyResource(staging.Get(),texture);
    D3D11_MAPPED_SUBRESOURCE mapped{}; Require(SUCCEEDED(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped)),"Strip readback failed");
    std::vector<uint32_t> pixels(PerformanceStrip::Width*PerformanceStrip::Height);
    for(unsigned y=0;y<PerformanceStrip::Height;++y)
        std::memcpy(pixels.data()+y*PerformanceStrip::Width,static_cast<unsigned char*>(mapped.pData)+y*mapped.RowPitch,PerformanceStrip::Width*4);
    context->Unmap(staging.Get(),0);
    return pixels;
}
}
int main() {
    try {
        const auto known=PerformanceStrip::Pixels(89.94,72),unknown=PerformanceStrip::Pixels(-1,-1);
        Require(known.size()==PerformanceStrip::Width*PerformanceStrip::Height && known[0]==0xff3030df && known[10]==0xff181818,"Strip background or accent bar differs");
        Require(TextPixels(known)>500 && TextPixels(unknown)>300 && known!=unknown,"Strip text missing, or N/A not shown");
        Require(PerformanceStrip::Pixels(89.94,72)==known && PerformanceStrip::Pixels(60,72)!=known,"Strip text not drawn from its values");

        ComPtr<ID3D11Device> device;
        Require(SUCCEEDED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,nullptr)),"WARP unavailable");
        PerformanceStrip strip;
        Require(!strip.Render(nullptr,1000,50),"Strip drawn without a device");
        auto* texture=strip.Render(device.Get(),1000,50);
        Require(texture && Download(device.Get(),texture)==PerformanceStrip::Pixels(-1,50),"First strip not uploaded");
        for(uint64_t now=1000;now<=2100;now+=11) strip.Submitted(now);
        Require(strip.Fps()>85 && strip.Fps()<95,"Submitted frames not counted as FPS");
        Require(strip.Render(device.Get(),1500,80)==texture && Download(device.Get(),texture)==PerformanceStrip::Pixels(-1,50),"Strip redrawn within a second");
        strip.Render(device.Get(),2100,80);
        Require(Download(device.Get(),texture)==PerformanceStrip::Pixels(strip.Fps(),80),"Strip not redrawn after a second");
        std::puts("FPS strip: pixels, N/A, frame rate and once-per-second upload passed");
        return 0;
    } catch(const std::exception& error) { std::fprintf(stderr,"%s\n",error.what()); return 1; }
}
