#pragma once
#include <windows.h>
#include <d3d11.h>
#include <filesystem>

namespace dirt3vr::frametiming {
// Session performance summary for VR (GRID2VR's frame timing, #4896ffd3): stereo FPS windows,
// render-thread time blocked in xrWaitFrame and Present, GPU usage, and non-blocking D3D11
// timestamps around both eye renders. performance-summary.json keeps GRID2VR's schema 1 for
// the launcher; DiRT3VR renders two eyes of one prepared scene, so "center" is the left eye,
// "left" the time between the eyes and "right" the right eye. Timestamp queries change no
// pipeline binding and results are read without waiting. Every call is ignored unless made on
// the thread that started the summary.
enum Point : unsigned {FrameBegin,CenterBegin,CenterEnd,RightBegin,RightEnd,FrameEnd,PointCount};
bool Start(ID3D11Device* device,const std::filesystem::path& output,unsigned headsetWidth,unsigned headsetHeight,unsigned scale);
LONGLONG Now();
void Mark(Point point);
void Wait(LONGLONG started,LONGLONG periodNanoseconds);
void Submitted(bool stereo);
// After the native Present returns; started is Now() taken just before it.
void Present(LONGLONG started);
// The latest one-second GPU sample (the headset strip shares it), -1 if none.
double GpuPercent();
void Stop();
}
