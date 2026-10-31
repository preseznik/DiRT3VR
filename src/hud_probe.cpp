#include "hud_probe.h"
#include <wrl/client.h>
#include <array>
#include <algorithm>
#include <iomanip>
#include <vector>
#include <cstring>
#include <limits>

namespace dirt3vr {
using Microsoft::WRL::ComPtr;
namespace {
struct Unmap {
    ID3D11DeviceContext* context;
    ID3D11Resource* resource;
    ~Unmap() { context->Unmap(resource,0); }
};
void Hex(std::ostream& out,const void* data,size_t size) {
    constexpr char digits[]="0123456789abcdef";
    const auto* bytes=static_cast<const unsigned char*>(data);
    out << '"';
    for(size_t i=0;i<size;++i) out << digits[bytes[i]>>4] << digits[bytes[i]&15];
    out << '"';
}
std::vector<unsigned char> Buffer(std::ostream& out,ID3D11DeviceContext* context,ID3D11Buffer* source,uint64_t offset,unsigned limit) {
    if(!source) { out << "null"; return {}; }
    D3D11_BUFFER_DESC desc{}; source->GetDesc(&desc);
    out << "{\"size\":" << desc.ByteWidth << ",\"offset\":" << offset << ",\"data\":";
    // Copy full buffers: D3D11 constant-buffer copies cannot be partial.
    // Large streaming pools are excluded; no unbounded staging allocation.
    if(!desc.ByteWidth || desc.ByteWidth>4*1024*1024 || offset>=desc.ByteWidth) { out << "null}"; return {}; }
    ComPtr<ID3D11Device> device; context->GetDevice(&device);
    D3D11_BUFFER_DESC staging{}; staging.ByteWidth=desc.ByteWidth;
    staging.Usage=D3D11_USAGE_STAGING; staging.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Buffer> copy;
    if(FAILED(device->CreateBuffer(&staging,nullptr,&copy))) { out << "null}"; return {}; }
    context->CopyResource(copy.Get(),source);
    D3D11_MAPPED_SUBRESOURCE mapped{};
    if(FAILED(context->Map(copy.Get(),0,D3D11_MAP_READ,0,&mapped))) { out << "null}"; return {}; }
    Unmap unmap{context,copy.Get()};
    // Hex output preserves non-finite floats and exact packed vertex bytes.
    const auto* start=static_cast<const unsigned char*>(mapped.pData)+size_t(offset);
    std::vector<unsigned char> bytes(start,start+std::min(limit,desc.ByteWidth-unsigned(offset)));
    Hex(out,bytes.data(),bytes.size());
    out << '}';
    return bytes;
}
void Image(const std::filesystem::path& prefix,ID3D11Texture2D* image) {
    if(!image) return;
    D3D11_TEXTURE2D_DESC desc{}; image->GetDesc(&desc);
    if(desc.Format!=DXGI_FORMAT_R8G8B8A8_UNORM || !desc.Width || !desc.Height ||
       desc.Width>4096 || desc.Height>4096 || desc.ArraySize!=1 || desc.MipLevels!=1 || desc.SampleDesc.Count!=1) return;
    ComPtr<ID3D11Device> device; image->GetDevice(&device);
    ComPtr<ID3D11DeviceContext> context; device->GetImmediateContext(&context);
    desc.Usage=D3D11_USAGE_STAGING; desc.BindFlags=desc.MiscFlags=0; desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> copy;
    if(FAILED(device->CreateTexture2D(&desc,nullptr,&copy))) return;
    context->CopyResource(copy.Get(),image);
    D3D11_MAPPED_SUBRESOURCE mapped{};
    if(FAILED(context->Map(copy.Get(),0,D3D11_MAP_READ,0,&mapped))) return;
    Unmap unmap{context.Get(),copy.Get()};
    // Raw RGBA + dimensions in the JSONL: lossless, including alpha, with no
    // image codec or runtime dependency. Offline tools can convert for review.
    std::ofstream output(prefix.wstring()+L".rgba",std::ios::binary);
    for(unsigned y=0;y<desc.Height;++y)
        output.write(static_cast<const char*>(mapped.pData)+size_t(y)*mapped.RowPitch,size_t(desc.Width)*4);
}
}
bool HudProbe::Begin(const std::filesystem::path& directory,uint64_t frame) noexcept {
    Finish(nullptr);
    if(captures_>=3 || !directory.is_absolute()) return false;
    try {
        if(!std::filesystem::is_directory(directory)) return false;
        prefix_=directory/(L"hud-probe-"+std::to_wstring(GetCurrentProcessId())+L"-"+std::to_wstring(frame));
        if(std::filesystem::exists(prefix_.wstring()+L".jsonl") || std::filesystem::exists(prefix_.wstring()+L".rgba")) return false;
        output_.open(prefix_.wstring()+L".jsonl",std::ios::binary);
        if(!output_) { output_.close(); return false; }
        ++captures_; count_=0;
        output_ << "{\"schema\":1,\"frame\":" << frame << ",\"diagnosticReadback\":true}\n";
        return true;
    } catch(...) { output_.close(); return false; }
}
void HudProbe::Record(ID3D11DeviceContext* context,ID3D11Texture2D* back,uint64_t vertexHash,uint64_t pixelHash,
                      const char* draw,unsigned count,unsigned instances,unsigned first,int base,unsigned startInstance) noexcept {
    if(!Active() || !context || !back || count_>=256 || context->GetType()!=D3D11_DEVICE_CONTEXT_IMMEDIATE) return;
    try {
        ComPtr<ID3D11RenderTargetView> target; ComPtr<ID3D11DepthStencilView> depth;
        context->OMGetRenderTargets(1,&target,&depth);
        ComPtr<ID3D11Resource> resource; if(target) target->GetResource(&resource);
        if(resource.Get()!=back) return;
        ++count_;
        output_ << "{\"draw\":" << count_ << ",\"call\":\"" << draw << "\",\"count\":" << count
                << ",\"instances\":" << instances << ",\"first\":" << first << ",\"base\":" << base << ",\"startInstance\":" << startInstance
                << ",\"vs\":\"" << std::hex << std::setw(16) << std::setfill('0') << vertexHash
                << "\",\"ps\":\"" << std::setw(16) << pixelHash << "\"" << std::dec;
        D3D11_TEXTURE2D_DESC image{}; back->GetDesc(&image);
        output_ << ",\"width\":" << image.Width << ",\"height\":" << image.Height << ",\"depthTarget\":" << bool(depth);
        ComPtr<ID3D11DepthStencilState> ds; UINT stencil{}; context->OMGetDepthStencilState(&ds,&stencil);
        D3D11_DEPTH_STENCIL_DESC d{}; if(ds) ds->GetDesc(&d); else { d.DepthEnable=TRUE; d.DepthWriteMask=D3D11_DEPTH_WRITE_MASK_ALL; }
        output_ << ",\"depthEnabled\":" << bool(d.DepthEnable) << ",\"depthWrite\":" << d.DepthWriteMask << ",\"stencilRef\":" << stencil;
        std::array<D3D11_VIEWPORT,16> viewports{}; UINT views=16; context->RSGetViewports(&views,viewports.data());
        output_ << ",\"viewports\":[";
        for(UINT i=0;i<views;++i) { if(i) output_ << ','; const auto& v=viewports[i]; output_ << '[' << v.TopLeftX << ',' << v.TopLeftY << ',' << v.Width << ',' << v.Height << ']'; }
        output_ << "],\"scissors\":[";
        std::array<D3D11_RECT,16> rects{}; UINT rectCount=16; context->RSGetScissorRects(&rectCount,rects.data());
        for(UINT i=0;i<rectCount;++i) { if(i) output_ << ','; const auto& r=rects[i]; output_ << '[' << r.left << ',' << r.top << ',' << r.right << ',' << r.bottom << ']'; }
        ComPtr<ID3D11RasterizerState> raster; context->RSGetState(&raster); D3D11_RASTERIZER_DESC rd{}; if(raster) raster->GetDesc(&rd);
        output_ << "],\"scissorEnabled\":" << bool(rd.ScissorEnable);
        ComPtr<ID3D11BlendState> blend; float factors[4]{}; UINT mask{}; context->OMGetBlendState(&blend,factors,&mask);
        D3D11_BLEND_DESC bd{}; if(blend) blend->GetDesc(&bd);
        output_ << ",\"blend\":"; if(blend) Hex(output_,&bd,sizeof(bd)); else output_ << "null";
        D3D11_PRIMITIVE_TOPOLOGY topology{}; context->IAGetPrimitiveTopology(&topology); output_ << ",\"topology\":" << topology;
        ComPtr<ID3D11Buffer> cb; context->VSGetConstantBuffers(3,1,&cb); output_ << ",\"vs3\":"; Buffer(output_,context,cb.Get(),0,512);
        cb.Reset(); context->VSGetConstantBuffers(0,1,&cb); output_ << ",\"vs0\":"; Buffer(output_,context,cb.Get(),0,512);
        cb.Reset(); context->PSGetConstantBuffers(0,1,&cb); output_ << ",\"ps0\":"; Buffer(output_,context,cb.Get(),0,512);
        ComPtr<ID3D11InputLayout> layout; context->IAGetInputLayout(&layout);
        ProbeLayout metadata; UINT metadataSize=sizeof(metadata);
        const bool layoutKnown=layout && SUCCEEDED(layout->GetPrivateData(ProbeLayoutTag,&metadataSize,&metadata)) &&
            metadataSize==sizeof(metadata) && metadata.count<=32;
        output_ << ",\"layout\":";
        if(!layoutKnown) output_ << "null";
        else {
            output_ << '[';
            for(unsigned i=0;i<metadata.count;++i) {
                const auto& e=metadata.elements[i]; if(i) output_ << ',';
                output_ << "{\"semanticHex\":"; Hex(output_,e.semantic,sizeof(e.semantic));
                output_ << ",\"index\":" << e.index << ",\"format\":" << e.format << ",\"slot\":" << e.slot
                        << ",\"offset\":" << e.offset << ",\"classification\":" << e.classification << ",\"step\":" << e.step << '}';
            }
            output_ << ']';
        }
        const bool indexed=!strcmp(draw,"indexed") || !strcmp(draw,"indexed-instanced");
        const bool direct=indexed || !strcmp(draw,"draw") || !strcmp(draw,"instanced");
        ComPtr<ID3D11Buffer> indices; DXGI_FORMAT format{}; UINT indexOffset{}; context->IAGetIndexBuffer(&indices,&format,&indexOffset);
        const unsigned indexBytes=format==DXGI_FORMAT_R16_UINT ? 2 : format==DXGI_FORMAT_R32_UINT ? 4 : 0;
        output_ << ",\"indices\":";
        const auto indexData=Buffer(output_,context,indexed ? indices.Get() : nullptr,uint64_t(indexOffset)+uint64_t(first)*indexBytes,512);
        output_ << ",\"indexFormat\":" << format;
        int64_t vertexStart=first;
        if(indexed) {
            vertexStart=-1;
            if(indexBytes && count && count<=indexData.size()/indexBytes) {
                unsigned smallest=UINT_MAX;
                for(unsigned i=0;i<count;++i) { unsigned index{}; memcpy(&index,indexData.data()+i*indexBytes,indexBytes); smallest=std::min(smallest,index); }
                vertexStart=int64_t(smallest)+base;
            }
        }
        output_ << ",\"vertices\":[";
        for(unsigned slot=0;slot<4;++slot) {
            if(slot) output_ << ',';
            ComPtr<ID3D11Buffer> buffer; UINT stride{},offset{}; context->IAGetVertexBuffers(slot,1,&buffer,&stride,&offset);
            int64_t element=-1;
            if(direct && layoutKnown) for(unsigned i=0;i<metadata.count;++i) if(metadata.elements[i].slot==slot) {
                const auto& e=metadata.elements[i];
                element=e.classification==D3D11_INPUT_PER_VERTEX_DATA ? vertexStart : e.step==1 ? int64_t(startInstance) : -1;
                break;
            }
            const uint64_t byteOffset=uint64_t(offset)+(element>=0 ? uint64_t(element)*stride : 0);
            output_ << "{\"stride\":" << stride << ",\"drawReferenced\":" << (element>=0) << ",\"element\":" << element << ",\"buffer\":";
            Buffer(output_,context,buffer.Get(),byteOffset,4096); output_ << '}';
        }
        output_ << "]}\n";
    } catch(...) { Finish(nullptr); }
}
void HudProbe::Finish(ID3D11Texture2D* image) noexcept {
    if(!Active()) return;
    try {
        D3D11_TEXTURE2D_DESC desc{}; if(image) image->GetDesc(&desc);
        output_ << "{\"end\":true,\"draws\":" << count_ << ",\"limitReached\":" << (count_==256)
                << ",\"imageWidth\":" << desc.Width << ",\"imageHeight\":" << desc.Height << "}\n";
        Image(prefix_,image);
    } catch(...) { }
    output_.close();
}
}
