// Include the observer to exercise frame ownership without installing a hook
// or executing the game. Only the test can seed an observed native producer.
#include "frontend_shadow_state.cpp"
#include <cstdio>
#include <limits>
#include <stdexcept>
#include <vector>

using namespace dirt3vr;
namespace {
void Check(bool ok,const char* why) { if(!ok) throw std::runtime_error(why); }
using Point=std::array<float,4>;
Point Transform(const Point& point,const FrontendShadowMatrix& matrix) {
    Point out{};
    for(unsigned c=0;c<4;++c) for(unsigned r=0;r<4;++r) out[c]+=point[r]*matrix[r*4+c];
    return out;
}
FrontendShadowMatrix CameraAt(float angle,float x,float y,float z) {
    const float c=std::cos(angle),s=std::sin(angle);
    return {c,0,-s,0, 0,1,0,0, s,0,c,0, x,y,z,1};
}
Point InCamera(Point world,const FrontendShadowMatrix& camera) {
    // Independent geometric inverse: subtract the camera position and dot
    // against each world-space basis vector, rather than matrix inversion.
    Point view{0,0,0,1};
    for(unsigned axis=0;axis<3;++axis)
        for(unsigned k=0;k<3;++k) view[axis]+=(world[k]-camera[12+k])*camera[axis*4+k];
    return view;
}
void WorldMapping() {
    const auto center=CameraAt(.37f,4,2,-8);
    const FrontendShadowMatrix worldToShadow{.03f,.005f,0,0, 0,0,0,0, -.007f,.04f,0,0, .5f,.3f,0,1};
    FrontendShadowMatrix native{};
    for(unsigned r=0;r<4;++r) {
        Point row{}; std::copy_n(center.begin()+r*4,4,row.begin());
        const auto out=Transform(row,worldToShadow); std::copy(out.begin(),out.end(),native.begin()+r*4);
    }
    bool reproducedStale=false;
    for(float turn:{-.8f,0.f,.9f}) for(float eye:{-.032f,.032f}) {
        const auto tracked=CameraAt(.37f+turn,4+.25f+eye,2+.12f,-8-.3f);
        FrontendShadowMatrix corrected{};
        Check(frontend_shadow_detail::Correct(center,tracked,native,corrected),"valid tracked view refused");
        for(Point world: {Point{-7,0,2,1},Point{8,0,-4,1},Point{0,0,0,1}}) {
            const auto target=Transform(world,worldToShadow);
            const auto view=InCamera(world,tracked);
            const auto fixed=Transform(view,corrected),stale=Transform(view,native);
            for(unsigned c=0;c<4;++c) Check(std::abs(fixed[c]-target[c])<1e-5f,"fixed world shadow moved with eye");
            reproducedStale|=std::abs(stale[0]-target[0])>.005f;
        }
    }
    Check(reproducedStale,"test did not exercise the reported camera-relative drift");
    FrontendShadowMatrix same{};
    Check(frontend_shadow_detail::Correct(center,center,native,same),"native camera rejected");
    for(unsigned i=0;i<16;++i) Check(std::abs(same[i]-native[i])<1e-6f,"neutral eye changed mapping");
    const auto saved=same;
    for(unsigned kind=0;kind<5;++kind) {
        auto bad=center;
        if(kind==0) bad[0]=std::numeric_limits<float>::quiet_NaN();
        if(kind==1) bad[15]=0;
        if(kind==2) bad[4]=bad[0],bad[5]=bad[1],bad[6]=bad[2];
        if(kind==3) bad[12]=1e8f;
        if(kind==4) bad[0]*=2;
        Check(!frontend_shadow_detail::Correct(center,bad,native,same) && same==saved,"invalid eye changed output");
        Check(!frontend_shadow_detail::Correct(bad,center,native,same) && same==saved,"invalid producer changed output");
    }
    auto bad=native; bad[5]=std::numeric_limits<float>::infinity();
    Check(!frontend_shadow_detail::Correct(center,center,bad,same) && same==saved,"invalid native matrix changed output");
}
void Ownership() {
    std::vector<unsigned char> renderer(0xa00),context(0x200),owner(0x800);
    imageBase=0x10000000; // Synthetic ASLR base; these tables are never called.
    const auto put=[](void* at,uintptr_t value) { std::memcpy(at,&value,4); };
    put(renderer.data(),imageBase+RendererTableRva); put(context.data(),imageBase+ContextTableRva);
    put(renderer.data()+0x5f4,reinterpret_cast<uintptr_t>(context.data()));
    put(renderer.data()+0x9c0,reinterpret_cast<uintptr_t>(owner.data()));
    const auto inverse=CameraAt(.3f,1,2,3),matrix=CameraAt(0,0,0,0);
    std::memcpy(context.data()+0x70,inverse.data(),64);
    const auto observe=[&] { pending={owner.data(),context.data(),inverse,matrix,true}; };
    FrontendShadowMatrix expected{},corrected{};
    Check(!FrontendShadowMatrices(expected,corrected),"no scene accepted");
    observe();
    {
        FrontendShadowScene scope(renderer.data(),context.data());
        Check(FrontendShadowMatrices(expected,corrected) && expected==matrix,"fresh producer refused");
        { FrontendShadowScene nested(renderer.data(),context.data()); }
        Check(FrontendShadowMatrices(expected,corrected),"nested scope destroyed owner");
        auto moved=CameraAt(.5f,2,2,3); std::memcpy(context.data()+0x70,moved.data(),64);
        Check(FrontendShadowMatrices(expected,corrected) && expected==matrix,"second eye lost original producer");
    }
    Check(!FrontendShadowMatrices(expected,corrected),"mapping escaped scene");
    { FrontendShadowScene next(renderer.data(),context.data()); Check(!FrontendShadowMatrices(expected,corrected),"producer reused by later scene"); }
    observe(); EndFrontendShadowFrame();
    { FrontendShadowScene next(renderer.data(),context.data()); Check(!FrontendShadowMatrices(expected,corrected),"producer survived Present/stop"); }
    observe(); put(renderer.data()+0x9c0,0);
    { FrontendShadowScene wrong(renderer.data(),context.data()); Check(!FrontendShadowMatrices(expected,corrected),"wrong producer owner accepted"); }
    put(renderer.data()+0x9c0,reinterpret_cast<uintptr_t>(owner.data()));
    { FrontendShadowScene next(renderer.data(),context.data()); Check(!FrontendShadowMatrices(expected,corrected),"refused producer reused"); }
    observe();
    try { FrontendShadowScene interrupted(renderer.data(),context.data()); throw 1; } catch(int) {}
    Check(!FrontendShadowMatrices(expected,corrected),"exception retained scene mapping");
    Check(!InstallFrontendShadowState(nullptr),"missing logger accepted");
}
}
int main() {
    try { WorldMapping(); Ownership(); std::puts("Frontend shadows retain world position for both eyes; native ownership and refusal passed"); return 0; }
    catch(const std::exception& e) { std::puts(e.what()); return 1; }
}
