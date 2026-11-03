#include "head_camera.h"
#include <cmath>
#include <cstdio>
#include <map>
#include <stdexcept>

namespace {
using dirt3vr::HeadCamera;
void Require(bool value,const char* message) { if(!value) throw std::runtime_error(message); }
bool Near(float a,float b) { return std::abs(a-b)<1e-5f; }
// A sparse fake of DiRT 3's memory: only bytes that were put can be read.
std::map<uintptr_t,unsigned char> memory;
void Put(uintptr_t address,const void* data,size_t bytes) { for(size_t i=0;i<bytes;++i) memory[address+i]=static_cast<const unsigned char*>(data)[i]; }
void Put32(uintptr_t address,uint32_t value) { Put(address,&value,4); }
bool Read(uintptr_t address,void* output,size_t bytes) {
    for(size_t i=0;i<bytes;++i) {
        const auto found=memory.find(address+i);
        if(found==memory.end()) return false;
        static_cast<unsigned char*>(output)[i]=found->second;
    }
    return true;
}
constexpr uintptr_t Base=0x400000,Root=0x1000000,Game=0x1100000,Controller=0x1200000,Manager=0x1300000,Begin=0x1400000,Owner=0x1500000;
// The chain Wrench found (docs/research/seat-adjustment.md), with three cameras and the second selected.
void Layout(const char* name="head-cam",uintptr_t vtable=Base+HeadCamera::HeadVtableRva,uint32_t index=1) {
    memory.clear();
    Put32(Base+HeadCamera::RootRva,Root); Put32(Root+8,Game); Put32(Game+0x11b4,Controller); Put32(Controller+0x24,Manager);
    Put32(Manager,Begin); Put32(Manager+4,Begin+3*0x280); Put32(Manager+0x140,index);
    Put32(Begin+0x280,Owner); Put32(Owner,uint32_t(vtable));
    char text[16]{}; strcpy_s(text,name); Put(Owner+0xa1,text,sizeof(text));
    const float eye[3]{.1f,1.068f,-.2f},fov=55;
    Put(Owner+HeadCamera::Eye,eye,sizeof(eye)); Put(Owner+HeadCamera::Fov,&fov,4);
}
}
int main() {
    try {
        Layout(); HeadCamera head;
        Require(HeadCamera::Find(Base,Read,head) && head.owner==Owner && Near(head.eye[1],1.068f) && Near(head.eye[2],-.2f) && Near(head.fov,55),
                "Selected head camera or its eye point and FOV not found");
        Layout("bonnet-cam");
        Require(!HeadCamera::Find(Base,Read,head),"Another camera taken for the head camera");
        Layout("head-cam",Base+0x123456);
        Require(!HeadCamera::Find(Base,Read,head),"A camera without the Head class accepted");
        Layout("head-cam",Base+HeadCamera::HeadVtableRva,3);
        Require(!HeadCamera::Find(Base,Read,head),"A selected index past the camera list accepted");
        Layout(); memory.erase(Game+0x11b4);
        Require(!HeadCamera::Find(Base,Read,head),"A missing pointer did not fail the lookup");
        Layout(); const float bad=NAN; Put(Owner+HeadCamera::Fov,&bad,4);
        Require(!HeadCamera::Find(Base,Read,head),"A non-finite FOV accepted");

        // Seat offsets are x right, y up, z back; the camera's eye point is x left, y up, z forward.
        const float own[3]{.1f,1.068f,-.2f};
        float eye[3]{},fov{};
        HeadCamera::Seated(own,55,{.02f,.3f,.04f},5,eye,fov);
        Require(Near(eye[0],.08f) && Near(eye[1],1.368f) && Near(eye[2],-.24f) && Near(fov,60),"Seat change not converted to the camera's axes");
        HeadCamera::Seated(own,55,{},0,eye,fov);
        Require(Near(eye[0],own[0]) && Near(eye[1],own[1]) && Near(eye[2],own[2]) && Near(fov,55),"No change moved the camera");
        std::puts("Head camera: selected head-cam lookup, guards and seat conversion passed");
        return 0;
    } catch(const std::exception& error) { std::fprintf(stderr,"%s\n",error.what()); return 1; }
}
