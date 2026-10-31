#include "presentation_trace.h"
#include "hud_canvas.h"
#include <wrl/client.h>
#include <atomic>
#include <algorithm>
#include <iomanip>
#include <sstream>
#include <cmath>

namespace dirt3vr {
namespace {
using Microsoft::WRL::ComPtr;
constexpr GUID ResourceTag={0x06e3ab09,0xac1f,0x487b,{0x91,0x76,0x66,0xd0,0xe2,0x6a,0x0e,0x9c}};
std::atomic_uint64_t nextResource{1};
constexpr uint64_t Offset=14695981039346656037ull;
uint64_t Mix(uint64_t hash,uint64_t value) noexcept {
    for(unsigned i=0;i<8;++i) { hash^=(value>>(i*8))&255; hash*=1099511628211ull; }
    return hash;
}
uint64_t Identity(ID3D11Resource* resource) noexcept {
    if(!resource) return 0;
    uint64_t id{}; UINT size=sizeof(id);
    if(SUCCEEDED(resource->GetPrivateData(ResourceTag,&size,&id)) && size==sizeof(id) && id) return id;
    id=nextResource.fetch_add(1);
    return SUCCEEDED(resource->SetPrivateData(ResourceTag,sizeof(id),&id)) ? id : 0;
}
bool Movie(uint64_t hash) noexcept {
    return hash==0xf3c97950c737695dull || hash==0xbc345cfbdda49603ull || hash==0x8a868f7f6dfba787ull;
}
bool Small(unsigned count,unsigned instances) noexcept { return count>0 && count<=6 && instances==1; }
bool Screen(uint64_t hash,unsigned count,unsigned instances) noexcept {
    return hash==0x361f7beff48b284dull || Small(count,instances);
}
const char* Phase(int eye) noexcept { return eye==0 ? "left" : eye==1 ? "right" : "outside"; }
void Number(std::ostream& out,float value) { if(std::isfinite(value)) out<<value; else out<<"null"; }
void Hex(std::ostream& out,uint64_t value) { out<<'"'<<std::hex<<std::setw(16)<<std::setfill('0')<<value<<std::dec<<'"'; }
void Text(std::ostream& out,const char* value) {
    out<<'"';
    if(value) for(unsigned i=0;value[i] && i<64;++i) {
        const auto c=static_cast<unsigned char>(value[i]);
        if(c=='"' || c=='\\') out<<'\\'<<char(c);
        else if(c>=32 && c<127) out<<char(c);
        else out<<'?';
    }
    out<<'"';
}
void Resource(std::ostream& out,ID3D11Resource* resource) {
    if(!resource) { out<<"null"; return; }
    D3D11_RESOURCE_DIMENSION dimension{}; resource->GetType(&dimension);
    out<<"{\"id\":"<<Identity(resource)<<",\"dimension\":"<<unsigned(dimension);
    if(dimension==D3D11_RESOURCE_DIMENSION_TEXTURE2D) {
        ComPtr<ID3D11Texture2D> texture;
        if(SUCCEEDED(resource->QueryInterface(IID_PPV_ARGS(&texture)))) {
            D3D11_TEXTURE2D_DESC d{}; texture->GetDesc(&d);
            out<<",\"width\":"<<d.Width<<",\"height\":"<<d.Height<<",\"format\":"<<unsigned(d.Format)
               <<",\"mips\":"<<d.MipLevels<<",\"array\":"<<d.ArraySize<<",\"samples\":"<<d.SampleDesc.Count
               <<",\"usage\":"<<unsigned(d.Usage)<<",\"bind\":"<<d.BindFlags<<",\"cpu\":"<<d.CPUAccessFlags;
        }
    } else if(dimension==D3D11_RESOURCE_DIMENSION_BUFFER) {
        ComPtr<ID3D11Buffer> buffer;
        if(SUCCEEDED(resource->QueryInterface(IID_PPV_ARGS(&buffer)))) {
            D3D11_BUFFER_DESC d{}; buffer->GetDesc(&d);
            out<<",\"bytes\":"<<d.ByteWidth<<",\"usage\":"<<unsigned(d.Usage)<<",\"bind\":"<<d.BindFlags;
        }
    }
    out<<'}';
}
uint64_t StateKey(const D3D11_BLEND_DESC& blend,const D3D11_RASTERIZER_DESC& raster) noexcept {
    auto hash=Mix(Offset,blend.AlphaToCoverageEnable); hash=Mix(hash,blend.IndependentBlendEnable);
    for(const auto& t:blend.RenderTarget) {
        for(auto value:{unsigned(t.BlendEnable),unsigned(t.SrcBlend),unsigned(t.DestBlend),unsigned(t.BlendOp),
            unsigned(t.SrcBlendAlpha),unsigned(t.DestBlendAlpha),unsigned(t.BlendOpAlpha),unsigned(t.RenderTargetWriteMask)}) hash=Mix(hash,value);
    }
    for(auto value:{unsigned(raster.FillMode),unsigned(raster.CullMode),unsigned(raster.FrontCounterClockwise),
        unsigned(raster.DepthClipEnable),unsigned(raster.ScissorEnable),unsigned(raster.MultisampleEnable)}) hash=Mix(hash,value);
    return hash;
}
}

bool PresentationTrace::Configure(const std::filesystem::path& directory) noexcept {
    // Configuration is one-shot, so repeated launch/configuration callbacks
    // cannot reset the diagnostic budget during a process lifetime.
    if(configured_) return Active();
    try {
        if(!directory.is_absolute() || !std::filesystem::is_directory(directory)) return false;
        const auto file=directory/("presentation-"+std::to_string(GetCurrentProcessId())+".jsonl");
        const auto handle=CreateFileW(file.c_str(),GENERIC_WRITE,FILE_SHARE_READ,nullptr,CREATE_NEW,FILE_ATTRIBUTE_NORMAL,nullptr);
        if(handle==INVALID_HANDLE_VALUE) return false;
        CloseHandle(handle);
        output_.open(file,std::ios::out|std::ios::app);
        if(!output_) return false;
        configured_=true; started_=GetTickCount64(); signature_=Offset;
        return true;
    } catch(...) { return false; }
}
bool PresentationTrace::Active() const noexcept {
    return output_.is_open() && output_.good() && records_<512 && GetTickCount64()-started_<180000;
}
bool PresentationTrace::Enabled() const noexcept {
    const auto owner=ownerThread_.load(std::memory_order_acquire);
    return owner && owner==GetCurrentThreadId() && Active();
}
bool PresentationTrace::Wants(uint64_t vertexHash,unsigned count,unsigned instances) const noexcept {
    return Enabled() && (eye_==-1 || Movie(vertexHash) || Screen(vertexHash,count,instances));
}
bool PresentationTrace::Write(const std::string& line) noexcept {
    if(!Active()) return false;
    try { output_<<line<<'\n'; output_.flush(); if(output_) { ++records_; return true; } }
    catch(...) { }
    return false;
}
void PresentationTrace::BindBackbuffer(ID3D11Texture2D* texture) noexcept {
    const auto current=GetCurrentThreadId();
    auto owner=ownerThread_.load(std::memory_order_acquire);
    if(owner && owner!=current) return;
    if(!Active()) return;
    if(texture && !owner) ownerThread_.compare_exchange_strong(owner,current,std::memory_order_acq_rel);
    if(ownerThread_.load(std::memory_order_acquire)==current) backbuffer_=Identity(texture);
}
void PresentationTrace::BeginEye(unsigned eye) noexcept { if(Enabled()) eye_=eye<2 ? int(eye) : -1; }
void PresentationTrace::EndEye() noexcept { if(Enabled()) eye_=-1; }
void PresentationTrace::BeforePresent() noexcept {
    if(!Enabled()) return;
    try {
        if(Active() && summaries_<128 && (!previousSummary_ || signature_!=previousSignature_ ||
            movieDraws_!=previousMovieDraws_ || tailDraws_!=previousTailDraws_ || screenDraws_!=previousScreenDraws_)) {
            std::ostringstream out;
            out<<"{\"type\":\"present\",\"sequence\":"<<sequence_<<",\"ms\":"<<GetTickCount64()-started_
               <<",\"movie_draws\":"<<movieDraws_<<",\"tail_draws\":"<<tailDraws_<<",\"screen_draws\":"<<screenDraws_<<",\"signature\":";
            Hex(out,signature_); out<<'}';
            if(Write(out.str())) ++summaries_;
        }
    } catch(...) { }
    previousSummary_=true; previousSignature_=signature_;
    previousMovieDraws_=movieDraws_; previousTailDraws_=tailDraws_; previousScreenDraws_=screenDraws_;
    signature_=Offset; movieDraws_=tailDraws_=screenDraws_=0; ++sequence_; eye_=-1;
}
void PresentationTrace::Record(ID3D11DeviceContext* context,uint64_t vertexHash,uint64_t pixelHash,
    const char* draw,unsigned count,unsigned instances,unsigned first,int base,unsigned startInstance) noexcept {
    if(!Enabled() || !context || context->GetType()!=D3D11_DEVICE_CONTEXT_IMMEDIATE) return;
    const bool movie=Movie(vertexHash);
    if(!movie && eye_!=-1 && !Screen(vertexHash,count,instances)) return;
    try {
        std::array<ComPtr<ID3D11RenderTargetView>,8> rt;
        std::array<ID3D11RenderTargetView*,8> raw{};
        ComPtr<ID3D11DepthStencilView> depth;
        context->OMGetRenderTargets(UINT(raw.size()),raw.data(),&depth);
        for(size_t i=0;i<raw.size();++i) rt[i].Attach(raw[i]);
        std::array<ComPtr<ID3D11Resource>,8> targets;
        unsigned targetCount{}; bool back{};
        for(size_t i=0;i<rt.size();++i) if(rt[i]) {
            rt[i]->GetResource(&targets[i]); ++targetCount;
            back|=backbuffer_ && Identity(targets[i].Get())==backbuffer_;
        }
        if(!movie && !back) return;
        std::array<ComPtr<ID3D11ShaderResourceView>,4> srv;
        std::array<ID3D11ShaderResourceView*,4> rawSrv{};
        std::array<ComPtr<ID3D11Resource>,4> sources;
        context->PSGetShaderResources(0,UINT(rawSrv.size()),rawSrv.data());
        for(size_t i=0;i<srv.size();++i) { srv[i].Attach(rawSrv[i]); if(srv[i]) srv[i]->GetResource(&sources[i]); }
        ComPtr<ID3D11BlendState> blend; FLOAT factor[4]{}; UINT mask{};
        context->OMGetBlendState(&blend,factor,&mask);
        D3D11_BLEND_DESC blendDesc{};
        if(blend) blend->GetDesc(&blendDesc);
        else for(auto& t:blendDesc.RenderTarget) {
            t.SrcBlend=t.SrcBlendAlpha=D3D11_BLEND_ONE; t.DestBlend=t.DestBlendAlpha=D3D11_BLEND_ZERO;
            t.BlendOp=t.BlendOpAlpha=D3D11_BLEND_OP_ADD; t.RenderTargetWriteMask=D3D11_COLOR_WRITE_ENABLE_ALL;
        }
        ComPtr<ID3D11RasterizerState> raster; context->RSGetState(&raster);
        D3D11_RASTERIZER_DESC rasterDesc{};
        if(raster) raster->GetDesc(&rasterDesc);
        else { rasterDesc.FillMode=D3D11_FILL_SOLID; rasterDesc.CullMode=D3D11_CULL_BACK; rasterDesc.DepthClipEnable=TRUE; }
        ComPtr<ID3D11DepthStencilState> depthState; UINT stencilReference{};
        context->OMGetDepthStencilState(&depthState,&stencilReference);
        D3D11_DEPTH_STENCIL_DESC depthDesc{};
        if(depthState) depthState->GetDesc(&depthDesc);
        else {
            depthDesc.DepthEnable=TRUE; depthDesc.DepthWriteMask=D3D11_DEPTH_WRITE_MASK_ALL; depthDesc.DepthFunc=D3D11_COMPARISON_LESS;
            depthDesc.StencilReadMask=D3D11_DEFAULT_STENCIL_READ_MASK; depthDesc.StencilWriteMask=D3D11_DEFAULT_STENCIL_WRITE_MASK;
            depthDesc.FrontFace={D3D11_STENCIL_OP_KEEP,D3D11_STENCIL_OP_KEEP,D3D11_STENCIL_OP_KEEP,D3D11_COMPARISON_ALWAYS};
            depthDesc.BackFace=depthDesc.FrontFace;
        }
        auto stateKey=StateKey(blendDesc,rasterDesc);
        for(auto value:{unsigned(depthDesc.DepthEnable),unsigned(depthDesc.DepthWriteMask),unsigned(depthDesc.DepthFunc),
            unsigned(depthDesc.StencilEnable),unsigned(depthDesc.StencilReadMask),unsigned(depthDesc.StencilWriteMask),stencilReference}) stateKey=Mix(stateKey,value);
        for(const auto face:{depthDesc.FrontFace,depthDesc.BackFace}) for(auto value:{unsigned(face.StencilFailOp),unsigned(face.StencilDepthFailOp),
            unsigned(face.StencilPassOp),unsigned(face.StencilFunc)}) stateKey=Mix(stateKey,value);
        std::array<uint64_t,11> key{vertexHash,pixelHash,uint64_t(eye_+1),Identity(targets[0].Get()),
            Identity(sources[0].Get()),Identity(sources[1].Get()),Identity(sources[2].Get()),Identity(sources[3].Get()),
            stateKey,mask,targetCount};
        const bool screen=!movie && eye_!=-1;
        if(movie) ++movieDraws_; else if(screen) ++screenDraws_; else ++tailDraws_;
        for(const auto value:key) signature_=Mix(signature_,value);
        Pipeline* pipeline{};
        for(unsigned i=0;i<pipelines_;++i) if(pipeline_[i].key==key) { pipeline=&pipeline_[i]; break; }
        if(!pipeline) {
            if(pipelines_==pipeline_.size()) return;
            pipeline=&pipeline_[pipelines_++]; pipeline->key=key;
        }
        const auto occurrence=++pipeline->occurrences;
        // Early samples plus exponentially spaced samples retain later evidence
        // without spending all twelve slots on consecutive startup frames.
        if(pipeline->samples>=12 || (occurrence>2 && (occurrence&(occurrence-1)))) return;
        std::ostringstream out;
        out<<"{\"type\":\"draw\",\"sequence\":"<<sequence_<<",\"ms\":"<<GetTickCount64()-started_
           <<",\"phase\":\""<<Phase(eye_)<<"\",\"movie\":"<<unsigned(movie)<<",\"backbuffer\":"<<unsigned(back)
           <<",\"screen_draw\":"<<unsigned(screen)<<",\"sample\":"<<pipeline->samples+1<<",\"occurrence\":"<<occurrence<<",\"vs\":";
        Hex(out,vertexHash); out<<",\"ps\":"; Hex(out,pixelHash); out<<",\"draw\":"; Text(out,draw);
        out<<",\"count\":"<<count<<",\"instances\":"<<instances<<",\"first\":"<<first<<",\"base\":"<<base
           <<",\"start_instance\":"<<startInstance<<",\"mrt_count\":"<<targetCount<<",\"rt\":[";
        for(size_t i=0;i<targets.size();++i) { if(i) out<<','; Resource(out,targets[i].Get()); }
        out<<"],\"rt_views\":[";
        for(size_t i=0;i<rt.size();++i) {
            if(i) out<<',';
            if(!rt[i]) { out<<"null"; continue; }
            D3D11_RENDER_TARGET_VIEW_DESC d{}; rt[i]->GetDesc(&d);
            out<<"{\"format\":"<<unsigned(d.Format)<<",\"dimension\":"<<unsigned(d.ViewDimension)<<'}';
        }
        out<<"],\"depth\":"; ComPtr<ID3D11Resource> depthResource;
        if(depth) depth->GetResource(&depthResource); Resource(out,depthResource.Get());
        out<<",\"depth_state\":{\"enable\":"<<depthDesc.DepthEnable<<",\"write\":"<<unsigned(depthDesc.DepthWriteMask)
           <<",\"func\":"<<unsigned(depthDesc.DepthFunc)<<",\"stencil\":"<<depthDesc.StencilEnable
           <<",\"read_mask\":"<<unsigned(depthDesc.StencilReadMask)<<",\"write_mask\":"<<unsigned(depthDesc.StencilWriteMask)
           <<",\"reference\":"<<stencilReference<<",\"faces\":[";
        unsigned faceIndex{};
        for(const auto& face:{depthDesc.FrontFace,depthDesc.BackFace}) {
            if(faceIndex++) out<<',';
            out<<'['<<unsigned(face.StencilFailOp)<<','<<unsigned(face.StencilDepthFailOp)<<','<<unsigned(face.StencilPassOp)<<','<<unsigned(face.StencilFunc)<<']';
        }
        out<<"]}";
        out<<",\"srv\":[";
        for(size_t i=0;i<srv.size();++i) {
            if(i) out<<',';
            if(!srv[i]) { out<<"null"; continue; }
            D3D11_SHADER_RESOURCE_VIEW_DESC d{}; srv[i]->GetDesc(&d);
            out<<"{\"format\":"<<unsigned(d.Format)<<",\"dimension\":"<<unsigned(d.ViewDimension)<<",\"resource\":";
            Resource(out,sources[i].Get()); out<<'}';
        }
        out<<"],\"camera\":"; ComPtr<ID3D11Buffer> camera;
        context->VSGetConstantBuffers(3,1,&camera);
        HudCameraShadow shadow{}; UINT size=sizeof(shadow);
        if(camera && SUCCEEDED(camera->GetPrivateData(HudCameraTag,&size,&shadow)) && size==sizeof(shadow) &&
            shadow.generation==HudCameraGeneration.load()) {
            out<<'"'<<std::hex<<std::setfill('0'); for(auto byte:shadow.bytes) out<<std::setw(2)<<unsigned(byte); out<<std::dec<<'"';
        } else out<<"null";
        out<<",\"blend\":{\"alpha_to_coverage\":"<<blendDesc.AlphaToCoverageEnable<<",\"independent\":"<<blendDesc.IndependentBlendEnable
           <<",\"sample_mask\":"<<mask<<",\"factor\":[";
        for(unsigned i=0;i<4;++i) { if(i) out<<','; Number(out,factor[i]); }
        out<<"],\"targets\":[";
        for(size_t i=0;i<8;++i) {
            if(i) out<<','; const auto& b=blendDesc.RenderTarget[i];
            out<<'['<<b.BlendEnable<<','<<unsigned(b.SrcBlend)<<','<<unsigned(b.DestBlend)<<','<<unsigned(b.BlendOp)<<','
               <<unsigned(b.SrcBlendAlpha)<<','<<unsigned(b.DestBlendAlpha)<<','<<unsigned(b.BlendOpAlpha)<<','<<unsigned(b.RenderTargetWriteMask)<<']';
        }
        out<<"]},\"raster\":{\"fill\":"<<unsigned(rasterDesc.FillMode)<<",\"cull\":"<<unsigned(rasterDesc.CullMode)
           <<",\"front_ccw\":"<<rasterDesc.FrontCounterClockwise<<",\"depth_clip\":"<<rasterDesc.DepthClipEnable
           <<",\"scissor\":"<<rasterDesc.ScissorEnable<<",\"multisample\":"<<rasterDesc.MultisampleEnable<<"},\"viewports\":[";
        std::array<D3D11_VIEWPORT,16> viewports{}; UINT viewportCount=UINT(viewports.size()); context->RSGetViewports(&viewportCount,viewports.data());
        for(UINT i=0;i<viewportCount;++i) {
            if(i) out<<','; const auto& v=viewports[i]; out<<'[';
            unsigned item{}; for(auto value:{v.TopLeftX,v.TopLeftY,v.Width,v.Height,v.MinDepth,v.MaxDepth}) {
                if(item++) out<<','; Number(out,value);
            }
            out<<']';
        }
        out<<"]}";
        if(Write(out.str())) ++pipeline->samples;
    } catch(...) { }
}
}
