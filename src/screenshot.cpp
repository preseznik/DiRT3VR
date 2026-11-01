#include "screenshot.h"
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>
#include <wrl/client.h>
#include <cstring>
#include <fstream>
#include <string>

using Microsoft::WRL::ComPtr;

namespace dirt3vr::screenshot {
std::vector<unsigned> ParseSchedule(const wchar_t* text) {
    std::vector<unsigned> result;
    if(!text || !*text) return {};
    unsigned value{}; bool digits{};
    for(const wchar_t* p=text;;++p) {
        if(*p>=L'0' && *p<=L'9') {
            value=value*10+static_cast<unsigned>(*p-L'0');
            if(value>maxSeconds) return {};
            digits=true;
        } else if(*p==L',' || *p==0) {
            if(!digits || value<1 || (!result.empty() && value<=result.back()) || result.size()==maxShots) return {};
            result.push_back(value);
            value=0; digits=false;
            if(*p==0) break;
        } else return {};
    }
    return result;
}

std::vector<uint8_t> Bmp(uint32_t width,uint32_t height,const uint8_t* rows,size_t pitch,bool swapRedBlue) {
    const uint32_t imageBytes=width*height*4;
    std::vector<uint8_t> file(54+static_cast<size_t>(imageBytes));
    auto put32=[&](size_t at,uint32_t v) { for(int i=0;i<4;++i) file[at+i]=static_cast<uint8_t>(v>>(8*i)); };
    auto put16=[&](size_t at,uint16_t v) { file[at]=static_cast<uint8_t>(v); file[at+1]=static_cast<uint8_t>(v>>8); };
    file[0]='B'; file[1]='M';
    put32(2,static_cast<uint32_t>(file.size())); put32(10,54);
    put32(14,40); put32(18,width); put32(22,static_cast<uint32_t>(-static_cast<int32_t>(height)));
    put16(26,1); put16(28,32); put32(34,imageBytes);
    for(uint32_t y=0;y<height;++y) {
        const uint8_t* source=rows+y*pitch;
        uint8_t* target=file.data()+54+static_cast<size_t>(y)*width*4;
        for(uint32_t x=0;x<width;++x) {
            target[4*x+0]=source[4*x+(swapRedBlue?2:0)];
            target[4*x+1]=source[4*x+1];
            target[4*x+2]=source[4*x+(swapRedBlue?0:2)];
            target[4*x+3]=0xff;
        }
    }
    return file;
}

std::vector<uint8_t> Bmp10(uint32_t width,uint32_t height,const uint8_t* rows,size_t pitch) {
    std::vector<uint8_t> bgra(static_cast<size_t>(width)*height*4);
    for(uint32_t y=0;y<height;++y)
        for(uint32_t x=0;x<width;++x) {
            uint32_t v; std::memcpy(&v,rows+y*pitch+4*x,4);
            uint8_t* p=bgra.data()+(static_cast<size_t>(y)*width+x)*4;
            p[0]=static_cast<uint8_t>((v>>22)&0xff); p[1]=static_cast<uint8_t>((v>>12)&0xff);
            p[2]=static_cast<uint8_t>((v>>2)&0xff); p[3]=0xff;
        }
    return Bmp(width,height,bgra.data(),static_cast<size_t>(width)*4,false);
}

namespace {
bool Capture(IDXGISwapChain* chain,const std::wstring& path,void (*log)(const char*,...),unsigned seconds) {
    ComPtr<ID3D11Texture2D> back;
    if(FAILED(chain->GetBuffer(0,IID_PPV_ARGS(&back)))) { log("Screenshot at %us: no D3D11 back buffer",seconds); return false; }
    D3D11_TEXTURE2D_DESC desc{}; back->GetDesc(&desc);
    bool swap{},tenBit{};
    switch(desc.Format) {
    case DXGI_FORMAT_R10G10B10A2_UNORM: tenBit=true; break;
    case DXGI_FORMAT_B8G8R8A8_UNORM: case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB: case DXGI_FORMAT_B8G8R8X8_UNORM: case DXGI_FORMAT_B8G8R8X8_UNORM_SRGB: break;
    case DXGI_FORMAT_R8G8B8A8_UNORM: case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB: swap=true; break;
    default: log("Screenshot at %us: back buffer format %u not supported",seconds,unsigned(desc.Format)); return false;
    }
    ComPtr<ID3D11Device> device; back->GetDevice(&device);
    ComPtr<ID3D11DeviceContext> context; device->GetImmediateContext(&context);
    ComPtr<ID3D11Texture2D> source=back;
    if(desc.SampleDesc.Count>1) {
        auto resolvedDesc=desc;
        resolvedDesc.SampleDesc={1,0}; resolvedDesc.Usage=D3D11_USAGE_DEFAULT;
        resolvedDesc.BindFlags=0; resolvedDesc.CPUAccessFlags=0; resolvedDesc.MiscFlags=0;
        resolvedDesc.MipLevels=1; resolvedDesc.ArraySize=1;
        ComPtr<ID3D11Texture2D> resolved;
        if(FAILED(device->CreateTexture2D(&resolvedDesc,nullptr,&resolved))) { log("Screenshot at %us: resolve texture failed",seconds); return false; }
        context->ResolveSubresource(resolved.Get(),0,back.Get(),0,desc.Format);
        source=resolved;
    }
    auto stagingDesc=desc;
    stagingDesc.SampleDesc={1,0}; stagingDesc.Usage=D3D11_USAGE_STAGING;
    stagingDesc.BindFlags=0; stagingDesc.CPUAccessFlags=D3D11_CPU_ACCESS_READ; stagingDesc.MiscFlags=0;
    stagingDesc.MipLevels=1; stagingDesc.ArraySize=1;
    ComPtr<ID3D11Texture2D> staging;
    if(FAILED(device->CreateTexture2D(&stagingDesc,nullptr,&staging))) { log("Screenshot at %us: staging texture failed",seconds); return false; }
    context->CopyResource(staging.Get(),source.Get());
    D3D11_MAPPED_SUBRESOURCE mapped{};
    if(FAILED(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped))) { log("Screenshot at %us: map failed",seconds); return false; }
    const auto* rows=static_cast<const uint8_t*>(mapped.pData);
    const auto file=tenBit ? Bmp10(desc.Width,desc.Height,rows,mapped.RowPitch) : Bmp(desc.Width,desc.Height,rows,mapped.RowPitch,swap);
    context->Unmap(staging.Get(),0);
    std::ofstream output(path,std::ios::binary|std::ios::trunc);
    output.write(reinterpret_cast<const char*>(file.data()),static_cast<std::streamsize>(file.size()));
    if(!output) { log("Screenshot at %us: write failed",seconds); return false; }
    log("Screenshot at %us: %ux%u format=%u samples=%u saved",seconds,desc.Width,desc.Height,unsigned(desc.Format),desc.SampleDesc.Count);
    return true;
}
}

void BeforePresent(IDXGISwapChain* chain,void (*log)(const char*,...)) {
    static const auto schedule=[] {
        wchar_t text[256]{};
        const auto length=GetEnvironmentVariableW(L"DIRT3VR_SCREENSHOT_SECONDS",text,256);
        return length && length<256 ? ParseSchedule(text) : std::vector<unsigned>{};
    }();
    static const std::wstring directory=[] {
        wchar_t text[MAX_PATH]{};
        const auto length=GetEnvironmentVariableW(L"DIRT3VR_LOG_DIR",text,MAX_PATH);
        return length && length<MAX_PATH ? std::wstring(text,length) : std::wstring();
    }();
    static LARGE_INTEGER frequency{},start{};
    static size_t next{};
    if(schedule.empty() || directory.empty() || next>=schedule.size() || !chain) return;
    LARGE_INTEGER now{}; QueryPerformanceCounter(&now);
    if(!start.QuadPart) {
        QueryPerformanceFrequency(&frequency); start=now;
        log("Screenshots scheduled: %u, first at %us",static_cast<unsigned>(schedule.size()),schedule.front());
    }
    const auto elapsed=static_cast<double>(now.QuadPart-start.QuadPart)/static_cast<double>(frequency.QuadPart);
    if(elapsed<schedule[next]) return;
    const auto index=next++;
    const auto seconds=schedule[index];
    Capture(chain,directory+L"\\screenshot-"+std::to_wstring(index+1)+L"-"+std::to_wstring(seconds)+L"s.bmp",log,seconds);
}
}
