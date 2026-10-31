#include "vr_frame_boundary.h"
#include <cstdlib>

int main() {
    dirt3vr::VrFrameBoundary frame;
    if(frame.Sequence()!=1 || !frame.Claim() || frame.Claim()) return EXIT_FAILURE;
    // Invisible/partial stereo still owns this Present; screen must wait.
    if(frame.Claim()) return EXIT_FAILURE;
    frame.Presented();
    if(frame.Sequence()!=2 || !frame.Claim()) return EXIT_FAILURE;
    frame.Presented();
    frame.Presented(); // A desktop-only frame needs no XR attempt.
    if(frame.Sequence()!=4 || !frame.Claim() || frame.Claim()) return EXIT_FAILURE;
    dirt3vr::VrHistoryContinuity history;
    const int plugin=1, replacement=2;
    if(!history.NeedsPrime(&plugin,1,1)) return EXIT_FAILURE;
    history.Completed(&plugin,1,1);
    if(history.NeedsPrime(&plugin,2,1)) return EXIT_FAILURE;
    // Tracking failure/screen-only rendering leaves a gap in completed pairs.
    if(!history.NeedsPrime(&plugin,3,1) || !history.NeedsPrime(&replacement,2,1)) return EXIT_FAILURE;
    // A recenter processed by XR is a discontinuity even with the same camera.
    if(!history.NeedsPrime(&plugin,2,2)) return EXIT_FAILURE;
    history.Completed(&plugin,2,2);
    if(history.NeedsPrime(&plugin,3,2)) return EXIT_FAILURE;
    history.Reset(); // Camera identity can change without a sequence gap.
    if(!history.NeedsPrime(&plugin,3,2)) return EXIT_FAILURE;
    history.Completed(&plugin,3,2);
    if(history.NeedsPrime(&plugin,4,2)) return EXIT_FAILURE;
    return EXIT_SUCCESS;
}
