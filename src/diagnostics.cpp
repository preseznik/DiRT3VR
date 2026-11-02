#include "diagnostics.h"
#include "scene_observer.h"
#include "game_vr.h"
#include "scene_resolution.h"
#include "hud_hooks.h"
#include "background_play.h"
#include "direct_practice.h"
#include "screenshot.h"
#if defined(DIRT3VR_RESEARCH_PATCHES)
#include "research_patch.h"
#endif
#include <intrin.h>
#include <MinHook.h>
#include <bcrypt.h>
#include <wrl/client.h>
#include <array>
#include <atomic>
#include <cstdarg>
#include <cstdio>
#include <share.h>
#include <filesystem>
#include <fstream>
#include <mutex>
#include <string>
#include <unordered_set>

namespace dirt3vr {
namespace {
using Microsoft::WRL::ComPtr;
constexpr char SupportedSha256[] = "c502b495bb34a097440756769c80dcb36a79b618943f739e20d591d54171ff98";
struct State {
    std::mutex mutex;
    std::mutex logMutex;
    FILE* log{}; // Kept open until process exit; never closed under loader lock.
    std::filesystem::path logDirectory;
    bool enabled{}, supported{}, fixture{}, attached{};
    std::unordered_set<IDXGISwapChain*> chains;
    std::unordered_set<ID3D11Device*> observedDevices;
    std::atomic_uint32_t devices{}, swapchains{}, presents{}, resizes{}, failedPresents{}, failedResizes{}, width{}, height{};
    LARGE_INTEGER frequency{}, lastPresent{};
    DWORD presentThread{};
};
State& GetState() {
    static State* state = new State; // Avoid destructor ordering during game shutdown.
    return *state;
}
uint32_t PresentSequence() { return GetState().presents.load(std::memory_order_relaxed); }
void Log(const char* format, ...) noexcept {
    try {
        auto& state = GetState();
        std::lock_guard lock(state.logMutex);
        if(!state.log) return;
        SYSTEMTIME time{}; GetSystemTime(&time);
        fprintf(state.log, "%04u-%02u-%02uT%02u:%02u:%02u.%03uZ ", time.wYear, time.wMonth,
                time.wDay, time.wHour, time.wMinute, time.wSecond, time.wMilliseconds);
        va_list args; va_start(args, format); vfprintf(state.log, format, args); va_end(args);
        fputc('\n', state.log); fflush(state.log);
    } catch(...) { /* Diagnostics must never change the game API's result. */ }
}
std::string FileHash(const std::filesystem::path& path) {
    BCRYPT_ALG_HANDLE algorithm{};
    BCRYPT_HASH_HANDLE hash{};
    if(BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) return {};
    const bool created = BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) >= 0;
    std::ifstream input(path, std::ios::binary);
    bool valid = created && input.good();
    std::array<char, 65536> buffer{};
    while(valid && input) {
        input.read(buffer.data(), buffer.size());
        valid = BCryptHashData(hash, reinterpret_cast<PUCHAR>(buffer.data()), static_cast<ULONG>(input.gcount()), 0) >= 0;
    }
    std::array<UCHAR, 32> digest{};
    valid = valid && !input.bad() && BCryptFinishHash(hash, digest.data(), static_cast<ULONG>(digest.size()), 0) >= 0;
    if(hash) BCryptDestroyHash(hash);
    BCryptCloseAlgorithmProvider(algorithm, 0);
    if(!valid) return {};
    char text[65]{};
    for(size_t i = 0; i < digest.size(); ++i) sprintf_s(text + i * 2, 3, "%02x", digest[i]);
    return text;
}
void Initialize() noexcept {
    static std::once_flag once;
    try {
        std::call_once(once, [] {
            auto& state = GetState();
            wchar_t flag[8]{};
            state.enabled = GetEnvironmentVariableW(L"DIRT3VR_DIAGNOSTICS", flag, 8) == 1 && flag[0] == L'1';
            wchar_t hostPath[32768]{};
            const auto length = GetModuleFileNameW(nullptr, hostPath, 32768);
            const bool pathValid = length && length < 32768;
            const auto hash = pathValid ? FileHash(hostPath) : std::string{};
            state.supported = hash == SupportedSha256;
#if defined(DIRT3VR_TEST_FIXTURE)
            // Deliberately absent from the shipping binary. No environment value
            // can make a real game eligible for the synthetic fixture allowance.
            state.fixture = pathValid && _wcsicmp(std::filesystem::path(hostPath).filename().c_str(), L"DiRT3VR.NativeFixture.exe") == 0;
#endif
            if(state.enabled) {
                wchar_t folder[32768]{};
                const auto count = GetEnvironmentVariableW(L"DIRT3VR_LOG_DIR", folder, 32768);
                if(count && count < 32768) {
                    const std::filesystem::path directory(folder);
                    if(directory.is_absolute()) {
                        std::filesystem::create_directories(directory);
                        state.logDirectory = directory;
                        const auto path = directory / (L"native-" + std::to_wstring(GetCurrentProcessId()) + L".log");
                        state.log = _wfsopen(path.c_str(), L"a", _SH_DENYWR);
                    }
                }
            }
            QueryPerformanceFrequency(&state.frequency);
            Log("DiRT3VR native schema=1 diagnostics=%u supported_host=%u synthetic_fixture=%u stereo=false exe_sha256=%s",
                state.enabled, state.supported, state.fixture, hash.c_str());
            if(!state.supported && !state.fixture) Log("Unsupported executable: all game instrumentation refused; D3D11 exports remain pass-through");
        });
    } catch(...) { Log("Diagnostic initialization failed; pass-through retained"); }
}
using PresentFn = HRESULT (STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT);
using ResizeFn = HRESULT (STDMETHODCALLTYPE*)(IDXGISwapChain*, UINT, UINT, UINT, DXGI_FORMAT, UINT);
using ReleaseFn = ULONG (STDMETHODCALLTYPE*)(IDXGISwapChain*);
using CreateSwapchainFn = HRESULT (STDMETHODCALLTYPE*)(IDXGIFactory*, IUnknown*, DXGI_SWAP_CHAIN_DESC*, IDXGISwapChain**);
PresentFn realPresent{};
ResizeFn realResize{};
ReleaseFn realRelease{};
CreateSwapchainFn realCreateSwapchain{};
void* factoryTarget{};
sceneresolution::Binding sceneResolution;
void Attach(IDXGISwapChain* chain);

void PinModule(const void* address) {
    HMODULE pinned{};
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
                      reinterpret_cast<LPCWSTR>(address), &pinned);
}
ULONG STDMETHODCALLTYPE Release(IDXGISwapChain* chain) {
    const auto remaining = realRelease(chain);
    if(!remaining) {
        sceneResolution.Forget(chain);
        try {
            auto& state = GetState(); std::lock_guard lock(state.mutex);
            state.chains.erase(chain); // Pointer only; the object has already gone.
        } catch(...) { }
    }
    return remaining;
}
HRESULT STDMETHODCALLTYPE CreateSwapchain(IDXGIFactory* factory, IUnknown* device, DXGI_SWAP_CHAIN_DESC* desc, IDXGISwapChain** chain) {
    sceneresolution::Change change;
    HeadsetResolutionRequest request;
    if(GetState().supported && InteractiveVrEnabled() && ReadResolution(request)) {
        change=sceneResolution.PrepareCreate(reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr)),
            reinterpret_cast<uintptr_t>(_ReturnAddress()),device,desc,chain,request,Log);
        Log("Scene resolution create guarded=%u requested=%ux%u",bool(change),request.sceneWidth,request.sceneHeight);
    }
    const HRESULT result = realCreateSwapchain(factory, device, desc, chain);
    sceneResolution.CompleteCreate(change,result,SUCCEEDED(result) && chain ? *chain : nullptr);
    try {
        if(FAILED(result) || !chain || !*chain || !device) return result;
        ComPtr<ID3D11Device> d3d;
        if(FAILED(device->QueryInterface(IID_PPV_ARGS(&d3d)))) return result;
        bool observed{};
        {
            auto& state = GetState(); std::lock_guard lock(state.mutex);
            observed = state.observedDevices.contains(d3d.Get());
        }
        if(observed) {
            Log("DXGI factory CreateSwapChain result=0x%08lx device=%p swapchain=%p", result, d3d.Get(), *chain);
            Attach(*chain);
        }
    } catch(...) { Log("Factory observer failed; original creation result preserved"); }
    return result;
}
void ObserveFactory(ID3D11Device* device) {
    ComPtr<IDXGIDevice> dxgi; ComPtr<IDXGIAdapter> adapter; ComPtr<IDXGIFactory> factory;
    if(FAILED(device->QueryInterface(IID_PPV_ARGS(&dxgi))) || FAILED(dxgi->GetAdapter(&adapter)) ||
       FAILED(adapter->GetParent(IID_PPV_ARGS(&factory)))) return;
    auto& state = GetState(); std::lock_guard lock(state.mutex);
    state.observedDevices.insert(device);
    auto** table = *reinterpret_cast<void***>(factory.Get());
    if(factoryTarget) {
        if(factoryTarget != table[10]) Log("Different DXGI factory implementation: creation observer unavailable for this factory");
        return;
    }
    const auto initialized = MH_Initialize();
    if(initialized != MH_OK && initialized != MH_ERROR_ALREADY_INITIALIZED) return;
    const auto created = MH_CreateHook(table[10], reinterpret_cast<void*>(&CreateSwapchain), reinterpret_cast<void**>(&realCreateSwapchain));
    if(created != MH_OK) { Log("Factory observer unavailable: MinHook=%d", created); return; }
    const auto enabled = MH_EnableHook(table[10]);
    if(enabled != MH_OK) { MH_RemoveHook(table[10]); Log("Factory observer activation failed: MinHook=%d", enabled); return; }
    factoryTarget = table[10]; PinModule(reinterpret_cast<const void*>(&CreateSwapchain));
    Log("DXGI factory CreateSwapChain observer attached for separate device/swapchain creation");
}

bool Observed(IDXGISwapChain* chain) {
    auto& state = GetState();
    std::lock_guard lock(state.mutex);
    return state.chains.contains(chain);
}
HRESULT STDMETHODCALLTYPE Present(IDXGISwapChain* chain, UINT interval, UINT flags) {
    try { if(Observed(chain) && !(flags & DXGI_PRESENT_TEST)) VrBeforePresent(chain); } catch(...) { }
    try { if(Observed(chain) && !(flags & DXGI_PRESENT_TEST)) screenshot::BeforePresent(chain, Log); } catch(...) { }
    const HRESULT result = realPresent(chain, interval, flags);
    try {
        if(!Observed(chain)) return result;
        auto& state = GetState();
        const auto count = ++state.presents;
        directpractice::Tick(Log);
#if defined(DIRT3VR_RESEARCH_PATCHES)
        research::Tick(Log);
#endif
        if(FAILED(result)) ++state.failedPresents;
        LARGE_INTEGER now{}; QueryPerformanceCounter(&now);
        double elapsed{};
        {
            std::lock_guard lock(state.mutex);
            const DWORD thread = GetCurrentThreadId();
            if(state.lastPresent.QuadPart && state.presentThread == thread && state.frequency.QuadPart)
                elapsed = 1000.0 * (now.QuadPart - state.lastPresent.QuadPart) / state.frequency.QuadPart;
            state.lastPresent = now; state.presentThread = thread;
        }
        if(count == 1 || count % 120 == 0 || FAILED(result)) {
            DWORD foregroundProcess{};
            GetWindowThreadProcessId(GetForegroundWindow(), &foregroundProcess);
            Log("Present count=%u chain=%p thread=%lu interval=%u flags=0x%x result=0x%08lx interval_ms=%.3f foreground=%u",
                count, chain, GetCurrentThreadId(), interval, flags, result, elapsed, foregroundProcess == GetCurrentProcessId());
        }
    } catch(...) { }
    return result;
}
HRESULT STDMETHODCALLTYPE ResizeBuffers(IDXGISwapChain* chain, UINT count, UINT width, UINT height, DXGI_FORMAT format, UINT flags) {
    const UINT incomingWidth=width,incomingHeight=height;
    sceneresolution::Change change;
    if(GetState().supported && InteractiveVrEnabled())
        change=sceneResolution.PrepareResize(reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr)),
            reinterpret_cast<uintptr_t>(_ReturnAddress()),chain,width,height,format);
    const HRESULT result = realResize(chain, count, width, height, format, flags);
    sceneResolution.CompleteResize(change,result);
    try {
        if(!Observed(chain)) return result;
        auto& state = GetState(); ++state.resizes;
        if(FAILED(result)) ++state.failedResizes;
        DXGI_SWAP_CHAIN_DESC desc{};
        if(SUCCEEDED(result) && SUCCEEDED(chain->GetDesc(&desc))) {
            state.width = desc.BufferDesc.Width; state.height = desc.BufferDesc.Height;
        }
        Log("ResizeBuffers count=%u requested=%ux%u format=%u flags=0x%x result=0x%08lx actual=%ux%u",
            count, width, height, unsigned(format), flags, result, state.width.load(), state.height.load());
        if(change) Log("Scene resolution resize guarded=1 incoming=%ux%u effective=%ux%u",incomingWidth,incomingHeight,width,height);
    } catch(...) { }
    return result;
}
void Attach(IDXGISwapChain* chain) {
    auto& state = GetState();
    std::lock_guard lock(state.mutex);
    auto** table = *reinterpret_cast<void***>(chain);
    if(!state.attached) {
        const auto initialized = MH_Initialize();
        if(initialized != MH_OK && initialized != MH_ERROR_ALREADY_INITIALIZED) { Log("Observer unavailable: MinHook=%d", initialized); return; }
        const auto present = MH_CreateHook(table[8], reinterpret_cast<void*>(&Present), reinterpret_cast<void**>(&realPresent));
        if(present != MH_OK) { Log("Present observer unavailable: MinHook=%d", present); return; }
        const auto resize = MH_CreateHook(table[13], reinterpret_cast<void*>(&ResizeBuffers), reinterpret_cast<void**>(&realResize));
        if(resize != MH_OK) { MH_RemoveHook(table[8]); Log("Resize observer unavailable: MinHook=%d", resize); return; }
        const auto release = MH_CreateHook(table[2], reinterpret_cast<void*>(&Release), reinterpret_cast<void**>(&realRelease));
        if(release != MH_OK) { MH_RemoveHook(table[8]); MH_RemoveHook(table[13]); Log("Release observer unavailable: MinHook=%d", release); return; }
        if(MH_QueueEnableHook(table[8]) != MH_OK || MH_QueueEnableHook(table[13]) != MH_OK ||
           MH_QueueEnableHook(table[2]) != MH_OK || MH_ApplyQueued() != MH_OK) {
            MH_DisableHook(table[8]); MH_DisableHook(table[13]); MH_DisableHook(table[2]);
            MH_RemoveHook(table[8]); MH_RemoveHook(table[13]); MH_RemoveHook(table[2]);
            Log("Observer activation failed; pass-through retained"); return;
        }
        // The callbacks must outlive every observed swapchain. The process owns
        // this module after attachment, and Windows reclaims it on process exit.
        PinModule(reinterpret_cast<const void*>(&Present));
        state.attached = true;
        Log("DXGI swapchain observers attached; stereo=false");
    }
    if(state.chains.insert(chain).second) ++state.swapchains;
    DXGI_SWAP_CHAIN_DESC desc{};
    if(SUCCEEDED(chain->GetDesc(&desc))) {
        state.width = desc.BufferDesc.Width; state.height = desc.BufferDesc.Height;
        Log("Swapchain chain=%p size=%ux%u format=%u samples=%u windowed=%u buffers=%u",
            chain, desc.BufferDesc.Width, desc.BufferDesc.Height, unsigned(desc.BufferDesc.Format),
            desc.SampleDesc.Count, desc.Windowed, desc.BufferCount);
    }
}
}

void ObserveCreation(HRESULT result, ID3D11Device* device, IDXGISwapChain* chain) noexcept {
    try {
        Initialize();
        auto& state = GetState();
        if(state.supported && backgroundplay::Requested()) backgroundplay::Install(Log);
        if(state.supported && directpractice::Requested()) directpractice::Install(Log);
        if(state.supported && directpractice::ReturnRequested()) directpractice::InstallReturn(Log);
        if(state.supported && directpractice::RequestedDifficulty()>=0) directpractice::InstallDifficulty(Log);
        if(state.supported && directpractice::AssistsRequested()) directpractice::InstallAssists(Log);
#if defined(DIRT3VR_RESEARCH_PATCHES)
        if(state.supported && state.enabled) research::Apply(Log);
#endif
        if(!state.enabled || (!state.supported && !state.fixture)) return;
        Log("D3D11 creation result=0x%08lx device=%p swapchain=%p", result, device, chain);
        if(FAILED(result)) return;
        if(device) {
            ++state.devices;
            ComPtr<IDXGIDevice> dxgi; ComPtr<IDXGIAdapter> adapter; DXGI_ADAPTER_DESC desc{};
            if(SUCCEEDED(device->QueryInterface(IID_PPV_ARGS(&dxgi))) && SUCCEEDED(dxgi->GetAdapter(&adapter)) && SUCCEEDED(adapter->GetDesc(&desc)))
                Log("D3D11 feature_level=0x%x adapter_luid=%08lx:%08lx dedicated_video_bytes=%llu",
                    unsigned(device->GetFeatureLevel()), desc.AdapterLuid.HighPart, desc.AdapterLuid.LowPart,
                    static_cast<unsigned long long>(desc.DedicatedVideoMemory));
            ObserveFactory(device);
            if(state.supported && InteractiveVrEnabled()) InstallHudHooks(device,Log);
        }
        if(chain) Attach(chain);
        // The synthetic D3D fixture never qualifies for engine instrumentation.
        if(state.supported) InstallSceneObserver(Log, PresentSequence);
    } catch(...) { Log("Observer failed; D3D11 call result preserved"); }
}
void ReadStatus(DiRT3VRStatus& result) noexcept {
    Initialize();
    auto& state = GetState();
    std::lock_guard lock(state.mutex);
    result = {};
    result.diagnosticsEnabled = state.enabled; result.supportedHost = state.supported;
    result.syntheticFixture = state.fixture; result.observerAttached = state.attached;
    result.deviceCreates = state.devices; result.swapchains = state.swapchains;
    result.presents = state.presents; result.resizes = state.resizes;
    result.failedPresents = state.failedPresents; result.failedResizes = state.failedResizes;
    result.width = state.width; result.height = state.height;
}
bool AcquireDiagnosticBackbuffer(ID3D11Texture2D** texture, std::filesystem::path& directory) noexcept {
    if(!texture) return false;
    *texture = nullptr;
    try {
        auto& state = GetState();
        std::lock_guard lock(state.mutex);
        if(!state.supported || !state.log || state.chains.size() != 1) return false;
        directory = state.logDirectory;
        return SUCCEEDED((*state.chains.begin())->GetBuffer(0, IID_PPV_ARGS(texture)));
    } catch(...) { return false; }
}
}
