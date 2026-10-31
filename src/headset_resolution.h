#pragma once
#include <cstdint>
#include <windows.h>

namespace dirt3vr {
// Same bounds as launcher/HeadsetResolution.vb. Output is never silently scaled.
inline bool ValidHeadsetSize(uint32_t width,uint32_t height) noexcept {
    return width>=320 && height>=180 && width<=4096 && height<=4096;
}
struct HeadsetResolutionRequest {
    uint32_t width{},height{},scale{},sceneWidth{},sceneHeight{};
    bool Configure(uint32_t w,uint32_t h,uint32_t percent) noexcept {
        *this={};
        if(!ValidHeadsetSize(w,h) || percent<50 || percent>300) return false;
        const auto sw=w*percent/100,sh=h*percent/100;
        if(!ValidHeadsetSize(sw,sh)) return false;
        width=w; height=h; scale=percent; sceneWidth=sw; sceneHeight=sh;
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
    return request.Configure(number(L"DIRT3VR_HEADSET_WIDTH"),number(L"DIRT3VR_HEADSET_HEIGHT"),number(L"DIRT3VR_VR_RENDER_SCALE"));
}
}
