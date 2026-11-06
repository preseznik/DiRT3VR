#pragma once
#include <cstdint>
#include <windows.h>

namespace dirt3vr {
// Same bounds as launcher/HeadsetResolution.vb. Output is never silently scaled.
inline bool ValidHeadsetSize(uint32_t width,uint32_t height) noexcept {
    return width>=320 && height>=180 && width<=4096 && height<=4096;
}
// A VR field-of-view crop (#16a86c75, #47d16d40) narrows each eye's view to fovWidth / fovHeight
// percent in tangent space, so the scene shrinks with it. The width keeps DiRT2VR's 70% floor; the
// height goes down to 50%, as a real helmet cuts the view at top and bottom (a helmet view crops only it).
inline bool ValidFovWidth(uint32_t percent) noexcept { return percent>=70 && percent<=100; }
inline bool ValidFovHeight(uint32_t percent) noexcept { return percent>=50 && percent<=100; }
struct HeadsetResolutionRequest {
    uint32_t width{},height{},scale{},sceneWidth{},sceneHeight{},fovWidth=100,fovHeight=100;
    bool Configure(uint32_t w,uint32_t h,uint32_t percent,uint32_t fovW=100,uint32_t fovH=100) noexcept {
        *this={};
        if(!ValidHeadsetSize(w,h) || percent<50 || percent>300 || !ValidFovWidth(fovW) || !ValidFovHeight(fovH)) return false;
        const auto sw=w*percent/100*fovW/100,sh=h*percent/100*fovH/100;
        if(!ValidHeadsetSize(sw,sh)) return false;
        width=w; height=h; scale=percent; sceneWidth=sw; sceneHeight=sh; fovWidth=fovW; fovHeight=fovH;
        return true;
    }
};
inline bool ReadResolution(HeadsetResolutionRequest& request) noexcept {
    const auto number=[](const wchar_t* name) -> unsigned {
        wchar_t value[16]{};
        const auto length=GetEnvironmentVariableW(name,value,16);
        if(!length || length>4) return 0;
        unsigned result{};
        for(unsigned i=0;i<length;++i) {
            if(value[i]<L'0' || value[i]>L'9') return 0;
            result=result*10+unsigned(value[i]-L'0');
        }
        return result;
    };
    // No crop is 100; a crop that is set but invalid refuses the whole request.
    const auto crop=[&](const wchar_t* name) { return GetEnvironmentVariableW(name,nullptr,0) ? number(name) : 100u; };
    return request.Configure(number(L"DIRT3VR_HEADSET_WIDTH"),number(L"DIRT3VR_HEADSET_HEIGHT"),number(L"DIRT3VR_VR_RENDER_SCALE"),
                             crop(L"DIRT3VR_VR_FOV_WIDTH"),crop(L"DIRT3VR_VR_FOV_HEIGHT"));
}
}
