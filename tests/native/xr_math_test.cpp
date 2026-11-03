#include "vr_math.h"
#include <cstdio>
#include <limits>
#include <stdexcept>

namespace {
void Require(bool value,const char* message) { if(!value) throw std::runtime_error(message); }
bool Near(float a,float b) { return std::abs(a-b)<.00001f; }
}
int main() {
    try {
        { // FOV crop: tangents scale about the optical axis, a factor of one keeps the angles.
            const XrFovf fov{-.9f,.7f,.8f,-.6f};
            const auto helmet=dirt3vr::math::CropFov(fov,1.f,.8f);
            Require(helmet.angleLeft==fov.angleLeft && helmet.angleRight==fov.angleRight,"Helmet crop changed the width");
            Require(Near(std::tan(helmet.angleUp),std::tan(.8f)*.8f) && Near(std::tan(helmet.angleDown),std::tan(-.6f)*.8f),"Height not cropped in tangent space");
            const auto narrow=dirt3vr::math::CropFov(fov,.7f,.7f);
            Require(Near(std::tan(narrow.angleLeft),std::tan(-.9f)*.7f) && narrow.angleLeft<0 && narrow.angleRight>0 && dirt3vr::math::ValidFov(narrow),"Asymmetric crop lost its shape");
        }
        using namespace dirt3vr::math;
        std::array<XrView,2> views{{{XR_TYPE_VIEW},{XR_TYPE_VIEW}}};
        views[0].pose={{0,0,0,1},{-.032f,1.4f,0}};
        views[1].pose={{0,0,0,-1},{.032f,1.4f,0}}; // q and -q describe the same rotation.
        const auto center=CenterPose(views);
        Require(Near(center.orientation.w,1) && Near(center.position.y,1.4f),"Opposite quaternion signs cancelled head orientation");
        const XrPosef origin{{0,0,0,1},{0,0,0}};
        const auto reference=RecenterPose(center,origin);
        const auto left=RelativePose(reference,views[0].pose),right=RelativePose(reference,views[1].pose);
        Require(Near(right.position.x-left.position.x,.064f) && Near(left.position.y,0),"Recenter changed IPD or retained floor height");
        const float half=std::sqrt(.5f);
        const XrPosef turn{{0,half,0,half},{5,2,3}};
        const auto offset=OffsetPose(turn,{1,0,0});
        Require(Near(offset.position.x,5) && Near(offset.position.y,2) && Near(offset.position.z,2),"Local eye offset ignored head rotation");
        const auto local=RelativePose(turn,offset);
        Require(Near(local.position.x,1) && Near(local.position.z,0),"Pose relative transform does not invert local offset");
        const auto up=RecenterPose({{half,0,0,half},{3,2,1}},turn);
        Require(Near(up.orientation.y,half) && Near(up.orientation.w,half),"Vertical recenter lost the last valid heading");
        // Including tilt: the tilted view becomes straight ahead and level; yaw-only keeps the tilt.
        const float s15=std::sin(.1309f),c15=std::cos(.1309f); // 15 degrees of roll, about the view axis
        const XrPosef tilted{{0,0,s15,c15},{0,1.4f,0}};
        const auto withTilt=RecenterPoseWithTilt(tilted);
        const auto level=RelativePose(withTilt,tilted);
        Require(Near(level.orientation.w,1) && Near(level.orientation.z,0) && Near(level.position.y,0),"Recentering with tilt did not level the current view");
        const auto keepLevel=RelativePose(RecenterPose(tilted,origin),tilted);
        Require(Near(std::abs(keepLevel.orientation.z),s15),"Yaw-only recentering removed the head's roll");
        bool rejected=false;
        try { Normalize({0,0,0,0}); } catch(const std::invalid_argument&) { rejected=true; }
        Require(rejected,"Zero quaternion accepted");
        rejected=false;
        try { RelativePose(origin,{{0,0,0,1},{std::numeric_limits<float>::quiet_NaN(),0,0}}); } catch(const std::invalid_argument&) { rejected=true; }
        Require(rejected,"Nonfinite tracking position accepted");
        std::array<float,16> projection{}; projection[10]=-1.0001f; projection[11]=-1; projection[14]=-.10001f;
        const auto original=projection;
        const XrFovf fov{-.9f,.7f,.8f,-.6f};
        Require(ApplyFov(projection.data(),fov),"Asymmetric perspective rejected");
        // Rays on each FOV boundary must map exactly to the corresponding NDC edge.
        const auto ndcX=[&](float angle) { return projection[0]*std::tan(angle)-projection[8]; };
        const auto ndcY=[&](float angle) { return projection[5]*std::tan(angle)-projection[9]; };
        Require(Near(ndcX(fov.angleLeft),-1) && Near(ndcX(fov.angleRight),1) &&
                Near(ndcY(fov.angleDown),-1) && Near(ndcY(fov.angleUp),1),"Asymmetric FOV moves edge rays or stretches eye image");
        Require(projection[10]==original[10] && projection[14]==original[14] && projection[11]==-1,"FOV edit changed game depth convention");
        const auto accepted=projection;
        Require(!ApplyFov(projection.data(),{.9f,-.9f,.8f,-.6f}) && projection==accepted,"Invalid FOV partially changed projection");
        projection[11]=1; const auto unsupported=projection;
        Require(!ApplyFov(projection.data(),fov) && projection==unsupported,"Unsupported projection convention changed");
        puts("XR math: IPD, orientation, recenter, asymmetric projection and invalid inputs passed"); return 0;
    } catch(const std::exception& error) { puts(error.what()); return 1; }
}
