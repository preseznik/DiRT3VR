#include "seat_panel.h"
#include <cstdio>
#include <stdexcept>
#include <vector>

namespace {
using Microsoft::WRL::ComPtr;
using namespace dirt3vr;
void Require(bool value,const char* message) { if(!value) throw std::runtime_error(message); }
bool NoKeys(unsigned) { return false; }
struct Pixels { std::vector<unsigned char> bytes; unsigned pitch{}; const unsigned char* At(unsigned x,unsigned y) const { return bytes.data()+y*pitch+x*4; } };
Pixels Download(ID3D11Device* device,ID3D11Texture2D* texture) {
    D3D11_TEXTURE2D_DESC desc{}; texture->GetDesc(&desc);
    desc.Usage=D3D11_USAGE_STAGING; desc.BindFlags=0; desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> staging; ComPtr<ID3D11DeviceContext> context; device->GetImmediateContext(&context);
    Require(SUCCEEDED(device->CreateTexture2D(&desc,nullptr,&staging)),"Staging texture unavailable");
    context->CopyResource(staging.Get(),texture);
    D3D11_MAPPED_SUBRESOURCE mapped{}; Require(SUCCEEDED(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped)),"Panel readback failed");
    Pixels pixels; pixels.pitch=mapped.RowPitch;
    pixels.bytes.assign(static_cast<unsigned char*>(mapped.pData),static_cast<unsigned char*>(mapped.pData)+size_t(mapped.RowPitch)*desc.Height);
    context->Unmap(staging.Get(),0);
    return pixels;
}
unsigned Bright(const Pixels& pixels,unsigned top,unsigned bottom) {
    unsigned count{};
    for(unsigned y=top;y<bottom;++y) for(unsigned x=0;x<SeatPanelImage::Width;++x) if(pixels.At(x,y)[1]>200) ++count;
    return count;
}
}
int main() {
    try {
        ComPtr<ID3D11Device> device;
        Require(SUCCEEDED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,nullptr)),"WARP unavailable");
        std::vector<unsigned char> bytes(seat::Channel::Bytes);
        seat::Channel channel(bytes.data());
        channel.Write(0,seat::Channel::Magic); channel.Write(4,seat::Channel::Version); channel.Write(8,seat::Channel::Bytes);
        channel.Write(128,unsigned(VK_F11)); channel.Write(184,1u);
        std::memcpy(bytes.data()+seat::Channel::Table,"ford_fiesta",11);
        std::memcpy(bytes.data()+seat::Channel::Table+16,"Ford Fiesta",11);
        Require(seat::Channel::Acceptable(bytes.data()),"Panel test channel refused");
        seat::Menu menu(channel,true,NoKeys); menu.Select(0); menu.Update(1000,0,true);

        SeatPanelImage image;
        Require(!image.Render(nullptr,menu),"Panel drawn without a device");
        auto* texture=image.Render(device.Get(),menu);
        Require(texture,"Panel not drawn");
        D3D11_TEXTURE2D_DESC desc{}; texture->GetDesc(&desc);
        Require(desc.Width==SeatPanelImage::Width && desc.Height==SeatPanelImage::Height && desc.Format==DXGI_FORMAT_B8G8R8A8_UNORM,"Panel texture size or format differs");
        const auto first=Download(device.Get(),texture);
        const auto* corner=first.At(4,4);
        Require(corner[3]==238 && corner[0]<40 && corner[1]<40 && corner[2]<40,"Panel background is not DiRT2VR's translucent dark grey");
        Require(Bright(first,30,95)>500,"Panel title missing");
        Require(Bright(first,670,815)>500,"Seat values missing");
        Require(image.Render(device.Get(),menu)==texture,"Unchanged panel replaced its texture");

        channel.Write(seat::Channel::Table+64,seat::Position{0,.2f,0});
        channel.Write(seat::Channel::Table+76,seat::Position{0,.2f,0});
        seat::Menu moved(channel,true,NoKeys); moved.Select(0); moved.Update(1000,0,true);
        const auto second=Download(device.Get(),image.Render(device.Get(),moved));
        bool changed{};
        for(unsigned y=670;y<815 && !changed;++y) for(unsigned x=0;x<SeatPanelImage::Width && !changed;++x) changed=std::memcmp(first.At(x,y),second.At(x,y),4)!=0;
        Require(changed,"Panel not redrawn for a new seat position");
        std::puts("seat panel: drawn, translucent, redrawn on change");
        return 0;
    } catch(const std::exception& error) { std::fprintf(stderr,"%s\n",error.what()); return 1; }
}
