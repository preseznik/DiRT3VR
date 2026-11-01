#pragma once
#include <openxr/openxr.h>
#include <array>
#include <cmath>
#include <stdexcept>

// Game-independent pose/projection math adapted from DiRT2VR camera_math.cpp.
// No assumptions about DiRT 3's camera structure or world-axis mapping live here.
namespace dirt3vr::math {
inline XrQuaternionf Normalize(XrQuaternionf q) {
    const double length = std::sqrt(double(q.x)*q.x + double(q.y)*q.y + double(q.z)*q.z + double(q.w)*q.w);
    if(!std::isfinite(length) || length < 0.001) throw std::invalid_argument("Invalid XR orientation");
    return {float(q.x/length),float(q.y/length),float(q.z/length),float(q.w/length)};
}
inline XrQuaternionf Multiply(XrQuaternionf a, XrQuaternionf b) {
    return {a.w*b.x+a.x*b.w+a.y*b.z-a.z*b.y, a.w*b.y-a.x*b.z+a.y*b.w+a.z*b.x,
            a.w*b.z+a.x*b.y-a.y*b.x+a.z*b.w, a.w*b.w-a.x*b.x-a.y*b.y-a.z*b.z};
}
inline XrVector3f Rotate(XrQuaternionf q, XrVector3f v) {
    const auto r = Multiply(Multiply(q,{v.x,v.y,v.z,0}),{-q.x,-q.y,-q.z,q.w});
    return {r.x,r.y,r.z};
}
inline bool Finite(XrVector3f v) { return std::isfinite(v.x) && std::isfinite(v.y) && std::isfinite(v.z); }
inline bool ValidFov(XrFovf fov) {
    for(float value : {fov.angleLeft,fov.angleRight,fov.angleUp,fov.angleDown})
        if(!std::isfinite(value) || std::abs(value) > 1.55f) return false;
    return std::tan(fov.angleRight)-std::tan(fov.angleLeft) >= .01f &&
           std::tan(fov.angleUp)-std::tan(fov.angleDown) >= .01f;
}
inline XrPosef CenterPose(const std::array<XrView,2>& views) {
    const auto& a=views[0].pose; const auto& b=views[1].pose;
    if(!Finite(a.position) || !Finite(b.position)) throw std::invalid_argument("Invalid XR position");
    const auto q=Normalize(a.orientation),r=Normalize(b.orientation);
    const float sign=q.x*r.x+q.y*r.y+q.z*r.z+q.w*r.w < 0 ? -1.f : 1.f;
    return {Normalize({q.x+sign*r.x,q.y+sign*r.y,q.z+sign*r.z,q.w+sign*r.w}),
            {(a.position.x+b.position.x)*.5f,(a.position.y+b.position.y)*.5f,(a.position.z+b.position.z)*.5f}};
}
inline XrPosef RecenterPose(const XrPosef& center, const XrPosef& previous) {
    if(!Finite(center.position)) throw std::invalid_argument("Invalid XR recenter position");
    const auto forward=Rotate(Normalize(center.orientation),{0,0,-1});
    auto heading=Normalize(previous.orientation);
    // Straight up/down has no reliable yaw. Preserve the previous yaw reference.
    if(forward.x*forward.x+forward.z*forward.z >= .0001f) {
        const float yaw=std::atan2(-forward.x,-forward.z);
        heading={0,std::sin(yaw*.5f),0,std::cos(yaw*.5f)};
    }
    return {heading,center.position};
}
// Recenter on the full head orientation (yaw, pitch and roll): the current view becomes straight
// ahead and level. Only for an explicit request; automatic centering keeps the horizon level.
inline XrPosef RecenterPoseWithTilt(const XrPosef& center) {
    if(!Finite(center.position)) throw std::invalid_argument("Invalid XR recenter position");
    return {Normalize(center.orientation),center.position};
}
inline XrPosef RelativePose(const XrPosef& reference, const XrPosef& pose) {
    if(!Finite(reference.position) || !Finite(pose.position)) throw std::invalid_argument("Invalid XR position");
    const auto q=Normalize(reference.orientation);
    const XrQuaternionf inverse{-q.x,-q.y,-q.z,q.w};
    return {Normalize(Multiply(inverse,Normalize(pose.orientation))),
            Rotate(inverse,{pose.position.x-reference.position.x,pose.position.y-reference.position.y,pose.position.z-reference.position.z})};
}
inline XrPosef OffsetPose(const XrPosef& pose, XrVector3f localOffset) {
    if(!Finite(pose.position) || !Finite(localOffset)) throw std::invalid_argument("Invalid XR offset");
    const auto q=Normalize(pose.orientation); const auto offset=Rotate(q,localOffset);
    return {q,{pose.position.x+offset.x,pose.position.y+offset.y,pose.position.z+offset.z}};
}
// Replace angular coefficients only in a verified right-handed perspective
// matrix. Preserve its near/far/depth convention and leave failures untouched.
inline bool ApplyFov(float* projection, XrFovf fov) noexcept {
    if(!projection || !ValidFov(fov) || projection[11] != -1.f || projection[15] != 0.f ||
       !std::isfinite(projection[10]) || !std::isfinite(projection[14])) return false;
    const float l=std::tan(fov.angleLeft),r=std::tan(fov.angleRight),d=std::tan(fov.angleDown),u=std::tan(fov.angleUp);
    projection[0]=2/(r-l); projection[5]=2/(u-d);
    projection[8]=(r+l)/(r-l); projection[9]=(u+d)/(u-d);
    return true;
}
}
