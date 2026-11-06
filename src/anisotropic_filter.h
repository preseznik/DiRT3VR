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

// Which of the game's samplers are raised, for finding the one behind the green and teal ground in VR at 16x
// (#24f76866): DIRT3VR_ANISOTROPY_RAISE=anisotropic raises only those the game made anisotropic itself (4x, 8x),
// =trilinear only its trilinear ones; unset or "all" raises both. iztok's runs: Off (trilinear) shows no teal.
enum class Scope { All, Anisotropic, Trilinear };

// Parses DIRT3VR_ANISOTROPY_RAISE; false (and All) for anything else.
inline bool ParseScope(const wchar_t* text,Scope& scope) {
    scope=Scope::All;
    if(!text || !*text) return true;
    const auto same=[text](const wchar_t* word) {
        const wchar_t* a=text; const wchar_t* b=word;
        for(;*a && *b;++a,++b) if((*a|0x20)!=*b) return false;
        return !*a && !*b;
    };
    if(same(L"all")) return true;
    if(same(L"anisotropic")) { scope=Scope::Anisotropic; return true; }
    if(same(L"trilinear")) { scope=Scope::Trilinear; return true; }
    return false;
}

// Whether a sampler the game asked for (before Adjust) is raised in this scope.
inline bool InScope(const D3D11_SAMPLER_DESC& game,Scope scope) {
    if(scope==Scope::Anisotropic) return game.Filter==D3D11_FILTER_ANISOTROPIC;
    if(scope==Scope::Trilinear) return game.Filter==D3D11_FILTER_MIN_MAG_MIP_LINEAR;
    return true;
}

// The colour lookup (#24f76866). DiRT 3 grades each frame through a colour palette: a 3D colour table unwrapped
// into a 32x256 texture with one mip level, read through the same trilinear repeating sampler as its surfaces, so
// Adjust can't tell them apart (D3D11 even returns one object for equal descriptions). Anisotropic filtering takes
// several samples along the screen-space gradient of the lookup coordinate, which jumps at every colour edge, so it
// averages unrelated table entries: green and teal patches on the ground in VR (iztok, 2026-11-06). So while a
// palette is bound to a pixel-shader slot, the device gets the game's own samplers. (VR runs at 16x showed the game
// creating its 11 palettes but not binding them directly, so the trace looks for what the grading reads instead.)
inline bool IsPalette(const D3D11_TEXTURE2D_DESC& desc) {
    return desc.Width==32 && desc.Height==256 && desc.MipLevels==1 && desc.ArraySize==1;
}

// Which pixel-shader resource slots of one device context hold a palette.
class PaletteSlots {
public:
    // Records slots [start, start+count); palette[i] says whether the view bound there is a palette (palette may be
    // null when every view is). Returns whether any slot holds a palette afterwards.
    bool Set(unsigned start,unsigned count,const bool* palette) {
        for(unsigned i=0;i<count && start+i<Slots;++i) {
            const auto slot=start+i;
            const auto bit=uint64_t{1}<<(slot%64);
            if(palette && palette[i]) mask_[slot/64]|=bit; else mask_[slot/64]&=~bit;
        }
        return Bound();
    }
    bool Bound() const { return mask_[0]!=0 || mask_[1]!=0; }
    static constexpr unsigned Slots=D3D11_COMMONSHADER_INPUT_RESOURCE_SLOT_COUNT;
private:
    uint64_t mask_[2]{};
};
static_assert(PaletteSlots::Slots<=128,"PaletteSlots holds 128 slots");

// Hooks the device's CreateSamplerState once when DIRT3VR_ANISOTROPY asks for a level, and, for the colour
// lookup, PSSetShaderResources and PSSetSamplers of the immediate context (and of deferred contexts when they have
// their own code); returns false when it was requested but couldn't be installed. DIRT3VR_ANISOTROPY_RAISE limits
// which samplers are raised (Scope); DIRT3VR_ANISOTROPY_TRACE=1 logs each distinct texture the pixel shader gets.
bool Install(ID3D11Device* device,void (*log)(const char*,...));

}
