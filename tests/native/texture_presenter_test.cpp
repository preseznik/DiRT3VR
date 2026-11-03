#include "texture_presenter.h"
#include <d3dcompiler.h>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <stdexcept>

namespace {
using Microsoft::WRL::ComPtr;
ComPtr<ID3D11Device> device;
ComPtr<ID3D11DeviceContext> context;
void Require(bool value,const char* message) { if(!value) throw std::runtime_error(message); }
ComPtr<ID3D11Texture2D> Texture(unsigned width,unsigned height,DXGI_FORMAT format,UINT binds,const void* pixels=nullptr,UINT samples=1) {
    D3D11_TEXTURE2D_DESC desc{}; desc.Width=width; desc.Height=height; desc.Format=format;
    desc.MipLevels=desc.ArraySize=1; desc.SampleDesc.Count=samples; desc.BindFlags=binds;
    D3D11_SUBRESOURCE_DATA data{pixels,width*4,0};
    ComPtr<ID3D11Texture2D> texture;
    Require(SUCCEEDED(device->CreateTexture2D(&desc,pixels ? &data : nullptr,&texture)),"Texture creation failed"); return texture;
}
ComPtr<ID3D11RenderTargetView> Target(ID3D11Texture2D* texture) {
    ComPtr<ID3D11RenderTargetView> target; Require(SUCCEEDED(device->CreateRenderTargetView(texture,nullptr,&target)),"RTV creation failed"); return target;
}
std::array<unsigned char,4> ReadPixel(ID3D11Texture2D* source,unsigned x,unsigned y) {
    D3D11_TEXTURE2D_DESC desc{}; source->GetDesc(&desc); desc.BindFlags=0; desc.Usage=D3D11_USAGE_STAGING; desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
    ComPtr<ID3D11Texture2D> staging; Require(SUCCEEDED(device->CreateTexture2D(&desc,nullptr,&staging)),"Readback allocation failed");
    context->CopyResource(staging.Get(),source); D3D11_MAPPED_SUBRESOURCE mapped{};
    Require(SUCCEEDED(context->Map(staging.Get(),0,D3D11_MAP_READ,0,&mapped)),"Readback failed");
    std::array<unsigned char,4> pixel{}; memcpy(pixel.data(),static_cast<const unsigned char*>(mapped.pData)+y*mapped.RowPitch+x*4,4);
    context->Unmap(staging.Get(),0); return pixel;
}
void Color(std::array<unsigned char,4> actual,std::array<int,4> expected) {
    for(unsigned i=0;i<4;++i) Require(std::abs(int(actual[i])-expected[i])<=2,"Color conversion, channel ordering or scaling differs");
}
struct GameState {
    ComPtr<ID3D11RenderTargetView> target;
    ComPtr<ID3D11RasterizerState> raster;
    ComPtr<ID3D11DepthStencilState> depth;
    ComPtr<ID3D11BlendState> blend;
    ComPtr<ID3D11SamplerState> sampler;
    ComPtr<ID3D11ShaderResourceView> resource;
    ComPtr<ID3D11Buffer> vertices,constant;
    ComPtr<ID3D11VertexShader> vertex;
    ComPtr<ID3D11PixelShader> pixel;
    std::array<D3D11_VIEWPORT,2> viewports{{{3,4,120,80,.2f,.8f},{9,10,40,30,.1f,.9f}}};
    std::array<D3D11_RECT,2> scissors{{{2,3,100,70},{4,5,30,20}}};
    float blendFactors[4]{.2f,.3f,.4f,.5f};
    GameState() {
        auto color=Texture(128,96,DXGI_FORMAT_R8G8B8A8_UNORM,D3D11_BIND_RENDER_TARGET); target=Target(color.Get());
        D3D11_RASTERIZER_DESC rs{}; rs.FillMode=D3D11_FILL_SOLID; rs.CullMode=D3D11_CULL_FRONT; rs.ScissorEnable=TRUE; rs.DepthClipEnable=TRUE;
        Require(SUCCEEDED(device->CreateRasterizerState(&rs,&raster)),"Game raster state failed");
        D3D11_DEPTH_STENCIL_DESC ds{}; ds.DepthEnable=TRUE; ds.DepthWriteMask=D3D11_DEPTH_WRITE_MASK_ALL; ds.DepthFunc=D3D11_COMPARISON_GREATER;
        Require(SUCCEEDED(device->CreateDepthStencilState(&ds,&depth)),"Game depth state failed");
        D3D11_BLEND_DESC bs{}; auto& rt=bs.RenderTarget[0]; rt.BlendEnable=TRUE; rt.SrcBlend=D3D11_BLEND_BLEND_FACTOR;
        rt.DestBlend=D3D11_BLEND_INV_BLEND_FACTOR; rt.BlendOp=rt.BlendOpAlpha=D3D11_BLEND_OP_ADD;
        rt.SrcBlendAlpha=D3D11_BLEND_ONE; rt.DestBlendAlpha=D3D11_BLEND_ZERO; rt.RenderTargetWriteMask=15;
        Require(SUCCEEDED(device->CreateBlendState(&bs,&blend)),"Game blend state failed");
        D3D11_SAMPLER_DESC ss{}; ss.Filter=D3D11_FILTER_MIN_MAG_MIP_POINT; ss.AddressU=ss.AddressV=ss.AddressW=D3D11_TEXTURE_ADDRESS_WRAP;
        ss.MaxAnisotropy=1; ss.ComparisonFunc=D3D11_COMPARISON_NEVER; ss.MaxLOD=1;
        Require(SUCCEEDED(device->CreateSamplerState(&ss,&sampler)),"Game sampler state failed");
        auto image=Texture(2,2,DXGI_FORMAT_R8G8B8A8_UNORM,D3D11_BIND_SHADER_RESOURCE);
        Require(SUCCEEDED(device->CreateShaderResourceView(image.Get(),nullptr,&resource)),"Game SRV failed");
        D3D11_BUFFER_DESC buffer{}; buffer.ByteWidth=64; buffer.BindFlags=D3D11_BIND_VERTEX_BUFFER|D3D11_BIND_INDEX_BUFFER;
        Require(SUCCEEDED(device->CreateBuffer(&buffer,nullptr,&vertices)),"Game IA buffer failed");
        buffer.BindFlags=D3D11_BIND_CONSTANT_BUFFER;
        Require(SUCCEEDED(device->CreateBuffer(&buffer,nullptr,&constant)),"Game constant buffer failed");
        const char shader[]="float4 vs(uint id:SV_VertexID):SV_Position{return float4(id,0,0,1);} float4 ps():SV_Target{return float4(1,0,0,1);}";
        ComPtr<ID3DBlob> vs,ps,error;
        Require(SUCCEEDED(D3DCompile(shader,sizeof(shader)-1,nullptr,nullptr,nullptr,"vs","vs_5_0",0,0,&vs,&error)) &&
                SUCCEEDED(D3DCompile(shader,sizeof(shader)-1,nullptr,nullptr,nullptr,"ps","ps_5_0",0,0,&ps,&error)),"Game shader compile failed");
        Require(SUCCEEDED(device->CreateVertexShader(vs->GetBufferPointer(),vs->GetBufferSize(),nullptr,&vertex)) &&
                SUCCEEDED(device->CreatePixelShader(ps->GetBufferPointer(),ps->GetBufferSize(),nullptr,&pixel)),"Game shader creation failed");
        auto* rtv=target.Get(); context->OMSetRenderTargets(1,&rtv,nullptr);
        context->OMSetDepthStencilState(depth.Get(),37); context->OMSetBlendState(blend.Get(),blendFactors,0x12345678);
        context->RSSetState(raster.Get()); context->RSSetViewports(2,viewports.data()); context->RSSetScissorRects(2,scissors.data());
        auto* vb=vertices.Get(); UINT stride=16,offset=8;
        context->IASetVertexBuffers(0,1,&vb,&stride,&offset); context->IASetIndexBuffer(vertices.Get(),DXGI_FORMAT_R16_UINT,2);
        context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_LINELIST);
        context->VSSetShader(vertex.Get(),nullptr,0); context->PSSetShader(pixel.Get(),nullptr,0);
        auto* cb=constant.Get(); context->VSSetConstantBuffers(5,1,&cb);
        auto* srv=resource.Get(); auto* sam=sampler.Get(); context->PSSetShaderResources(3,1,&srv); context->PSSetSamplers(4,1,&sam);
    }
    void Verify() const {
        ComPtr<ID3D11RenderTargetView> rtv; context->OMGetRenderTargets(1,&rtv,nullptr); Require(rtv==target,"Game render target changed");
        ComPtr<ID3D11DepthStencilState> ds; UINT ref{}; context->OMGetDepthStencilState(&ds,&ref); Require(ds==depth && ref==37,"Game depth/stencil state changed");
        ComPtr<ID3D11BlendState> bs; float factors[4]{}; UINT mask{}; context->OMGetBlendState(&bs,factors,&mask);
        Require(bs==blend && mask==0x12345678 && !memcmp(factors,blendFactors,sizeof(factors)),"Game blending changed");
        ComPtr<ID3D11RasterizerState> rs; context->RSGetState(&rs); Require(rs==raster,"Game raster state changed");
        UINT count=2; std::array<D3D11_VIEWPORT,2> vp{}; context->RSGetViewports(&count,vp.data());
        Require(count==2 && !memcmp(vp.data(),viewports.data(),sizeof(vp)),"Game viewports changed");
        count=2; std::array<D3D11_RECT,2> rects{}; context->RSGetScissorRects(&count,rects.data());
        Require(count==2 && !memcmp(rects.data(),scissors.data(),sizeof(rects)),"Game scissors changed");
        ComPtr<ID3D11Buffer> vb,ib,cb; UINT stride{},offset{}; DXGI_FORMAT format{};
        context->IAGetVertexBuffers(0,1,&vb,&stride,&offset); Require(vb==vertices && stride==16 && offset==8,"Game vertex binding changed");
        context->IAGetIndexBuffer(&ib,&format,&offset); Require(ib==vertices && format==DXGI_FORMAT_R16_UINT && offset==2,"Game index binding changed");
        D3D11_PRIMITIVE_TOPOLOGY topology{}; context->IAGetPrimitiveTopology(&topology); Require(topology==D3D11_PRIMITIVE_TOPOLOGY_LINELIST,"Game primitive topology changed");
        ComPtr<ID3D11VertexShader> vs; ComPtr<ID3D11PixelShader> ps; context->VSGetShader(&vs,nullptr,nullptr); context->PSGetShader(&ps,nullptr,nullptr);
        Require(vs==vertex && ps==pixel,"Game shaders changed"); context->VSGetConstantBuffers(5,1,&cb); Require(cb==constant,"Untouched-stage game constant buffer changed");
        ComPtr<ID3D11ShaderResourceView> srv; ComPtr<ID3D11SamplerState> sam; context->PSGetShaderResources(3,1,&srv); context->PSGetSamplers(4,1,&sam);
        Require(srv==resource && sam==sampler,"Game sampler/resource bindings changed");
    }
};
}
int main() {
    try {
        Require(SUCCEEDED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_WARP,nullptr,0,nullptr,0,D3D11_SDK_VERSION,&device,nullptr,&context)),"WARP unavailable");
        dirt3vr::TexturePresenter presenter; Require(presenter.Initialize(device.Get()),"Presenter initialization failed");
        GameState game;
        const std::array<unsigned char,16> gray{128,128,128,255,128,128,128,255,128,128,128,255,128,128,128,255};
        for(auto sourceFormat:{DXGI_FORMAT_R8G8B8A8_UNORM,DXGI_FORMAT_R8G8B8A8_UNORM_SRGB,DXGI_FORMAT_B8G8R8A8_UNORM}) {
            auto source=Texture(2,2,sourceFormat,D3D11_BIND_RENDER_TARGET,gray.data()); // Deliberately no SRV bind flag.
            for(auto outputFormat:{DXGI_FORMAT_R8G8B8A8_UNORM_SRGB,DXGI_FORMAT_R8G8B8A8_UNORM}) {
                auto output=Texture(4,4,outputFormat,D3D11_BIND_RENDER_TARGET); auto target=Target(output.Get());
                Require(presenter.Draw(source.Get(),target.Get(),4,4),"Backbuffer presentation failed"); game.Verify();
                const int encoded=outputFormat==DXGI_FORMAT_R8G8B8A8_UNORM_SRGB ? 128 : 55;
                Color(ReadPixel(output.Get(),1,1),{encoded,encoded,encoded,255});
                Require(!presenter.Draw(source.Get(),target.Get(),3,4),"Mismatched eye dimensions accepted"); game.Verify();
            }
        }
        const std::array<unsigned char,16> palette{255,0,0,255,0,255,0,255,0,0,255,255,255,255,255,255};
        auto source=Texture(2,2,DXGI_FORMAT_R8G8B8A8_UNORM,D3D11_BIND_RENDER_TARGET,palette.data());
        auto output=Texture(4,4,DXGI_FORMAT_R8G8B8A8_UNORM_SRGB,D3D11_BIND_RENDER_TARGET); auto target=Target(output.Get());
        Require(presenter.Draw(source.Get(),target.Get(),4,4),"Scaled palette failed"); game.Verify();
        Color(ReadPixel(output.Get(),1,1),{159,64,64,255}); Color(ReadPixel(output.Get(),0,0),{255,0,0,255});
        const std::array<unsigned char,4> bgraRed{0,0,255,255}; auto bgra=Texture(1,1,DXGI_FORMAT_B8G8R8A8_UNORM,D3D11_BIND_RENDER_TARGET,bgraRed.data());
        Require(presenter.Draw(bgra.Get(),target.Get(),4,4),"BGRA presentation failed"); Color(ReadPixel(output.Get(),2,2),{255,0,0,255}); game.Verify();
        // DiRT 3's measured backbuffer is format24: packed10-bit RGB, gamma encoded.
        const uint32_t packedGray=512u|(512u<<10)|(512u<<20)|(3u<<30);
        auto tenBit=Texture(1,1,DXGI_FORMAT_R10G10B10A2_UNORM,D3D11_BIND_RENDER_TARGET,&packedGray);
        Require(presenter.Draw(tenBit.Get(),target.Get(),4,4),"10-bit game backbuffer presentation failed");
        Color(ReadPixel(output.Get(),1,1),{128,128,128,255}); game.Verify();
        auto linearOutput=Texture(4,4,DXGI_FORMAT_R8G8B8A8_UNORM,D3D11_BIND_RENDER_TARGET); auto linearTarget=Target(linearOutput.Get());
        Require(presenter.Draw(tenBit.Get(),linearTarget.Get(),4,4),"10-bit linear XR presentation failed");
        Color(ReadPixel(linearOutput.Get(),1,1),{55,55,55,255}); game.Verify();
        const uint32_t packedRed=1023u|(3u<<30);
        auto tenBitRed=Texture(1,1,DXGI_FORMAT_R10G10B10A2_UNORM,D3D11_BIND_RENDER_TARGET,&packedRed);
        Require(presenter.Draw(tenBitRed.Get(),target.Get(),4,4),"10-bit color presentation failed");
        Color(ReadPixel(output.Get(),1,1),{255,0,0,255}); game.Verify();
        UINT quality{}; Require(SUCCEEDED(device->CheckMultisampleQualityLevels(DXGI_FORMAT_R8G8B8A8_UNORM,4,&quality)) && quality,"WARP MSAA unavailable");
        auto msaa=Texture(2,2,DXGI_FORMAT_R8G8B8A8_UNORM,D3D11_BIND_RENDER_TARGET,nullptr,4); auto msaaTarget=Target(msaa.Get());
        const float color[]={128.f/255,128.f/255,128.f/255,1}; context->ClearRenderTargetView(msaaTarget.Get(),color);
        Require(presenter.Draw(msaa.Get(),target.Get(),4,4),"MSAA resolve presentation failed"); Color(ReadPixel(output.Get(),1,1),{128,128,128,255}); game.Verify();
        auto unsupported=Texture(2,2,DXGI_FORMAT_R16G16B16A16_FLOAT,D3D11_BIND_RENDER_TARGET);
        Require(!presenter.Draw(unsupported.Get(),target.Get(),4,4),"Unsupported HDR source silently converted"); game.Verify();
        Require(!presenter.Draw(output.Get(),target.Get(),4,4),"Aliased source/target accepted"); game.Verify();
        const std::array<unsigned char,4> halfRed{128,0,0,128};
        auto hud=Texture(1,1,DXGI_FORMAT_R8G8B8A8_UNORM,D3D11_BIND_RENDER_TARGET,halfRed.data());
        Require(presenter.Draw(hud.Get(),linearTarget.Get(),4,4,dirt3vr::TexturePresenter::Mode::StraightAlpha),"HUD alpha conversion failed");
        Color(ReadPixel(linearOutput.Get(),1,1),{255,0,0,128}); game.Verify();
        const float blue[4]{0,0,1,1}; context->ClearRenderTargetView(linearTarget.Get(),blue);
        Require(presenter.Draw(hud.Get(),linearTarget.Get(),4,4,dirt3vr::TexturePresenter::Mode::Composite),"Desktop HUD composite failed");
        Color(ReadPixel(linearOutput.Get(),1,1),{128,0,127,255}); game.Verify();
        Require(!presenter.Draw(hud.Get(),target.Get(),4,4,dirt3vr::TexturePresenter::Mode::Composite),"Gamma-encoded HUD composed into sRGB target"); game.Verify();
        const std::array<unsigned char,4> transparent{0,0,0,0};
        auto emptyHud=Texture(1,1,DXGI_FORMAT_R8G8B8A8_UNORM,D3D11_BIND_RENDER_TARGET,transparent.data());
        Require(presenter.Draw(emptyHud.Get(),linearTarget.Get(),4,4,dirt3vr::TexturePresenter::Mode::StraightAlpha),"Transparent HUD conversion failed");
        Color(ReadPixel(linearOutput.Get(),1,1),{0,0,0,0}); game.Verify();
        // A panel over part of the monitor image: only its rectangle changes, and it must fit.
        context->ClearRenderTargetView(linearTarget.Get(),blue);
        Require(presenter.DrawRegion(hud.Get(),linearTarget.Get(),2,1,2,2,dirt3vr::TexturePresenter::Mode::Composite),"Panel region composite failed");
        Color(ReadPixel(linearOutput.Get(),3,2),{128,0,127,255}); Color(ReadPixel(linearOutput.Get(),1,1),{0,0,255,255}); Color(ReadPixel(linearOutput.Get(),3,0),{0,0,255,255});
        game.Verify();
        Require(!presenter.DrawRegion(hud.Get(),linearTarget.Get(),3,0,2,2,dirt3vr::TexturePresenter::Mode::Composite) &&
                !presenter.DrawRegion(hud.Get(),linearTarget.Get(),4,0,1,1,dirt3vr::TexturePresenter::Mode::Composite),"Panel region outside the target accepted");
        presenter.Reset(); game.Verify();
        puts("Texture presenter WARP: resampling, UNORM/sRGB, RGBA/BGRA/10-bit RGB, MSAA and game state preservation passed"); return 0;
    } catch(const std::exception& error) { puts(error.what()); return 1; }
}
