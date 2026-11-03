#pragma once
#include <d3d11.h>
#include <cstdint>

// Anisotropic filtering (#15714dd2). DiRT 3 has no setting for it (research #f89609d3), so
// the renderer raises the game's own texture samplers, a port of DiRT2VR's
// anisotropic_filter.cpp: ID3D11Device::CreateSamplerState (vtable slot 23) is wrapped, and
// only samplers the game executable creates are changed. No game addresses are involved.
// DIRT3VR_ANISOTROPY = 1 (trilinear), 2, 4, 8 or 16; unset or 0 leaves the game's samplers.
namespace dirt3vr::anisotropy {

inline bool Valid(unsigned value) {
    return value==0 || value==1 || value==2 || value==4 || value==8 || value==16;
}

// Parses the variable's digits; false for anything that isn't a valid level.
inline bool Parse(const wchar_t* text,unsigned& value) {
    value=0;
    if(!text || !*text) return false;
    unsigned parsed=0;
    for(const wchar_t* p=text;*p;++p) {
        if(*p<L'0' || *p>L'9' || parsed>16) return false;
        parsed=parsed*10+unsigned(*p-L'0');
    }
    if(!Valid(parsed)) return false;
    value=parsed;
    return true;
}

// Only ordinary mipmapped, repeating surface samplers: point sampling, comparison (shadow)
// samplers and clamped screen-space passes stay as the game made them. Returns whether desc
// was changed.
inline bool Adjust(D3D11_SAMPLER_DESC& desc,unsigned value) {
    if(!value || !Valid(value) || desc.MaxLOD<=desc.MinLOD ||
       (desc.Filter!=D3D11_FILTER_MIN_MAG_MIP_LINEAR && desc.Filter!=D3D11_FILTER_ANISOTROPIC)) return false;
    const auto repeat=[](D3D11_TEXTURE_ADDRESS_MODE mode) { return mode==D3D11_TEXTURE_ADDRESS_WRAP || mode==D3D11_TEXTURE_ADDRESS_MIRROR; };
    if(!repeat(desc.AddressU) && !repeat(desc.AddressV)) return false;
    const auto filter=value==1 ? D3D11_FILTER_MIN_MAG_MIP_LINEAR : D3D11_FILTER_ANISOTROPIC;
    if(desc.Filter==filter && desc.MaxAnisotropy==value) return false;
    desc.Filter=filter;
    desc.MaxAnisotropy=value;
    return true;
}

// Hooks the device's CreateSamplerState once when DIRT3VR_ANISOTROPY asks for a level;
// returns false when it was requested but couldn't be installed.
bool Install(ID3D11Device* device,void (*log)(const char*,...));

}
