#pragma once
#include "seat_menu.h"
#include <cstdint>
#include <cstring>

namespace dirt3vr {
// The selected cockpit camera on the monitor (Wrench's #88b7f778, docs/research/seat-adjustment.md):
// [[[[base+0xbe7404]+8]+0x11b4]+0x24] is the camera manager, its selected entry [manager+0x140]
// (0x280-byte entries between [manager] and [manager+4]) the owner. It's the head camera when it
// has the Head vtable (base+0xb28530) and the name "head-cam" at +0xa1. The view follows its own
// eye point at +0x19d0 (x left, y up, z forward, metres) and base FOV at +0x19e4 (degrees) every frame.
struct HeadCamera {
    static constexpr uintptr_t RootRva=0xbe7404,HeadVtableRva=0xb28530,Eye=0x19d0,Fov=0x19e4;
    uintptr_t owner{};
    float eye[3]{},fov{};
    using ReadMemory=bool (*)(uintptr_t address,void* output,size_t bytes);
    static bool Find(uintptr_t base,ReadMemory read,HeadCamera& out) noexcept {
        const auto pointer=[&](uintptr_t at,uintptr_t& value) { uint32_t raw{}; value=0; return read(at,&raw,4) && (value=raw)!=0; };
        uintptr_t root{},game{},controller{},manager{},begin{},end{},owner{},table{};
        uint32_t index{};
        char name[9]{};
        if(!pointer(base+RootRva,root) || !pointer(root+8,game) || !pointer(game+0x11b4,controller) || !pointer(controller+0x24,manager) ||
           !pointer(manager,begin) || !pointer(manager+4,end) || end<begin || (end-begin)%0x280 || (end-begin)/0x280>1024 ||
           !read(manager+0x140,&index,4) || index>=(end-begin)/0x280 || !pointer(begin+index*0x280,owner) ||
           !pointer(owner,table) || table!=base+HeadVtableRva || !read(owner+0xa1,name,sizeof(name)) || std::memcmp(name,"head-cam",9)) return false;
        HeadCamera found{owner};
        if(!read(owner+Eye,found.eye,sizeof(found.eye)) || !read(owner+Fov,&found.fov,4)) return false;
        for(const float value:{found.eye[0],found.eye[1],found.eye[2],found.fov}) if(!std::isfinite(value)) return false;
        out=found; return true;
    }
    // What to write: the camera's own values (which already hold the launch seat and FOV) plus the
    // menu's change. Seat offsets are x right, y up, z back; the camera's eye point x left, z forward.
    static void Seated(const float ownEye[3],float ownFov,seat::Position change,int fovChange,float eye[3],float& fov) noexcept {
        eye[0]=ownEye[0]-change.x; eye[1]=ownEye[1]+change.y; eye[2]=ownEye[2]-change.z;
        fov=ownFov+float(fovChange);
    }
};
}
