#include "render_probe.h"
#include "diagnostics.h"
#include <windows.h>
#include <fstream>
#include <vector>

namespace dirt3vr {
using Microsoft::WRL::ComPtr;
bool RenderProbe::Begin() {
    if(!AcquireDiagnosticBackbuffer(&back_, directory_)) return false;
    back_->GetDevice(&device_); device_->GetImmediateContext(&context_);
    const D3D11_QUERY_DESC description{D3D11_QUERY_PIPELINE_STATISTICS, 0};
    if(FAILED(device_->CreateQuery(&description, &query_))) return false;
    context_->Begin(query_.Get()); active_ = true;
    return true;
}
RenderProbe::~RenderProbe() { if(active_) context_->End(query_.Get()); }
bool RenderProbe::Finish(uint64_t sequence, unsigned eye) {
    if(!active_) return false;
    context_->End(query_.Get()); active_ = false;
    context_->Flush();
    const auto deadline = GetTickCount64() + 2000;
    HRESULT ready{};
    while((ready = context_->GetData(query_.Get(), &statistics_, sizeof(statistics_), D3D11_ASYNC_GETDATA_DONOTFLUSH)) == S_FALSE) {
        if(GetTickCount64() >= deadline) return false;
        Sleep(1);
    }
    if(FAILED(ready) || !statistics_.IAPrimitives) return false;
    D3D11_TEXTURE2D_DESC description{}; back_->GetDesc(&description);
    const bool rgba = description.Format == DXGI_FORMAT_R8G8B8A8_UNORM || description.Format == DXGI_FORMAT_R8G8B8A8_UNORM_SRGB;
    const bool bgra = description.Format == DXGI_FORMAT_B8G8R8A8_UNORM || description.Format == DXGI_FORMAT_B8G8R8A8_UNORM_SRGB;
    const bool rgb10 = description.Format == DXGI_FORMAT_R10G10B10A2_UNORM;
    if((!rgba && !bgra && !rgb10) || description.SampleDesc.Count != 1 || !description.Width || !description.Height ||
       description.Width > 4096 || description.Height > 4096) return false;
    description.Usage = D3D11_USAGE_STAGING; description.BindFlags = 0;
    description.CPUAccessFlags = D3D11_CPU_ACCESS_READ; description.MiscFlags = 0;
    ComPtr<ID3D11Texture2D> staging;
    if(FAILED(device_->CreateTexture2D(&description, nullptr, &staging))) return false;
    context_->CopyResource(staging.Get(), back_.Get());
    D3D11_MAPPED_SUBRESOURCE mapped{};
    if(FAILED(context_->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped))) return false;
    std::vector<unsigned char> pixels;
    try {
        pixels.resize(static_cast<size_t>(description.Width) * description.Height * 4);
        for(unsigned y = 0; y < description.Height; ++y) {
            auto* destination = pixels.data() + static_cast<size_t>(y) * description.Width * 4;
            const auto* source = static_cast<const unsigned char*>(mapped.pData) + static_cast<size_t>(y) * mapped.RowPitch;
            for(unsigned x = 0; x < description.Width; ++x) {
                if(rgb10) {
                    uint32_t value{}; memcpy(&value, source + x*4, sizeof(value));
                    destination[x*4] = static_cast<unsigned char>(((value >> 20) & 1023) * 255 / 1023);
                    destination[x*4+1] = static_cast<unsigned char>(((value >> 10) & 1023) * 255 / 1023);
                    destination[x*4+2] = static_cast<unsigned char>((value & 1023) * 255 / 1023);
                } else {
                    destination[x*4] = source[x*4 + (rgba ? 2 : 0)];
                    destination[x*4+1] = source[x*4+1];
                    destination[x*4+2] = source[x*4 + (rgba ? 0 : 2)];
                }
                destination[x*4+3] = 255;
            }
        }
    } catch(...) { context_->Unmap(staging.Get(), 0); throw; }
    context_->Unmap(staging.Get(), 0);
    BITMAPFILEHEADER file{}; BITMAPINFOHEADER info{};
    file.bfType = 0x4d42; file.bfOffBits = sizeof(file) + sizeof(info);
    file.bfSize = file.bfOffBits + static_cast<DWORD>(pixels.size());
    info.biSize = sizeof(info); info.biWidth = description.Width; info.biHeight = -static_cast<LONG>(description.Height);
    info.biPlanes = 1; info.biBitCount = 32; info.biCompression = BI_RGB;
    info.biSizeImage = static_cast<DWORD>(pixels.size());
    const auto name = L"replay-" + std::to_wstring(sequence) + L"-eye" + std::to_wstring(eye) + L".bmp";
    std::ofstream output(directory_ / name, std::ios::binary);
    output.write(reinterpret_cast<const char*>(&file), sizeof(file));
    output.write(reinterpret_cast<const char*>(&info), sizeof(info));
    output.write(reinterpret_cast<const char*>(pixels.data()), pixels.size());
    return output.good();
}
}
