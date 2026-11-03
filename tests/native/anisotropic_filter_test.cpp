#include "anisotropic_filter.h"
#include <cstdio>
#include <stdexcept>

namespace {
void Require(bool value,const char* message) { if(!value) throw std::runtime_error(message); }
namespace af=dirt3vr::anisotropy;

D3D11_SAMPLER_DESC Surface() {
    D3D11_SAMPLER_DESC desc{};
    desc.Filter=D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    desc.AddressU=desc.AddressV=desc.AddressW=D3D11_TEXTURE_ADDRESS_WRAP;
    desc.MinLOD=0; desc.MaxLOD=D3D11_FLOAT32_MAX; desc.MaxAnisotropy=1;
    return desc;
}
}

int main() {
    try {
        unsigned value{};
        Require(af::Parse(L"16",value) && value==16 && af::Parse(L"0",value) && value==0 && af::Parse(L"1",value) && value==1 &&
                af::Parse(L"0016",value) && value==16,"Valid levels not parsed");
        Require(!af::Parse(L"3",value) && !af::Parse(L"32",value) && !af::Parse(L"",value) && !af::Parse(L"8x",value) && !af::Parse(nullptr,value),
                "Invalid levels accepted");
        Require(!af::Parse(L"99999999999",value),"Overlong value accepted");

        auto desc=Surface();
        Require(af::Adjust(desc,16) && desc.Filter==D3D11_FILTER_ANISOTROPIC && desc.MaxAnisotropy==16,"A trilinear repeating sampler was not raised");
        Require(!af::Adjust(desc,16),"An already-raised sampler was changed again");
        desc=Surface();
        Require(af::Adjust(desc,1) == false && desc.Filter==D3D11_FILTER_MIN_MAG_MIP_LINEAR,"Trilinear asked of a trilinear sampler changed it");
        desc=Surface(); desc.Filter=D3D11_FILTER_ANISOTROPIC; desc.MaxAnisotropy=8;
        Require(af::Adjust(desc,1) && desc.Filter==D3D11_FILTER_MIN_MAG_MIP_LINEAR && desc.MaxAnisotropy==1,"Level 1 did not turn anisotropy off");

        desc=Surface(); desc.Filter=D3D11_FILTER_MIN_MAG_MIP_POINT;
        Require(!af::Adjust(desc,16),"A point sampler was changed");
        desc=Surface(); desc.Filter=D3D11_FILTER_COMPARISON_MIN_MAG_MIP_LINEAR;
        Require(!af::Adjust(desc,16),"A shadow comparison sampler was changed");
        desc=Surface(); desc.AddressU=desc.AddressV=D3D11_TEXTURE_ADDRESS_CLAMP;
        Require(!af::Adjust(desc,16),"A clamped screen-space sampler was changed");
        desc=Surface(); desc.MaxLOD=0;
        Require(!af::Adjust(desc,16),"A sampler without mips was changed");
        desc=Surface(); desc.AddressU=D3D11_TEXTURE_ADDRESS_CLAMP;
        Require(af::Adjust(desc,4) && desc.MaxAnisotropy==4,"A sampler repeating in one direction was not raised");
        desc=Surface();
        Require(!af::Adjust(desc,0) && !af::Adjust(desc,3),"Level 0 or an invalid level changed a sampler");
        std::puts("anisotropic filtering: ok");
        return 0;
    } catch(const std::exception& error) {
        std::fprintf(stderr,"FAIL: %s\n",error.what());
        return 1;
    }
}
