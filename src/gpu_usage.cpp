#include "gpu_usage.h"
#include "performance_stats.h"
#include <dxgi.h>
#include <pdh.h>
#include <pdhmsg.h>
#include <wrl/client.h>
#include <atomic>
#include <cstdio>
#include <string>
#include <vector>

// GRID2VR's src/performance_display.cpp GPU sampler, unchanged apart from the namespace.
namespace dirt3vr {
struct GpuCounterState {
    HANDLE stop{CreateEventW(nullptr,TRUE,FALSE,nullptr)};
    std::atomic<double> percent{-1};
    std::wstring adapter;
    ~GpuCounterState() {if(stop)CloseHandle(stop);}
};
namespace {
struct GpuWorker {std::shared_ptr<GpuCounterState> state;HMODULE module;};
}
static void CollectGpu(GpuCounterState& self) {
    PDH_HQUERY query{};PDH_HCOUNTER counter{};
    if(PdhOpenQueryW(nullptr,0,&query)!=ERROR_SUCCESS)return;
    if(PdhAddEnglishCounterW(query,L"\\GPU Engine(*)\\Utilization Percentage",0,&counter)!=ERROR_SUCCESS) {PdhCloseQuery(query);return;}
    PdhCollectQueryData(query);
    try { while(WaitForSingleObject(self.stop,1000)==WAIT_TIMEOUT) {
        double value=-1;DWORD bytes{},count{};
        if(PdhCollectQueryData(query)==ERROR_SUCCESS &&
           PdhGetFormattedCounterArrayW(counter,PDH_FMT_DOUBLE,&bytes,&count,nullptr)==PDH_MORE_DATA && bytes<=4*1024*1024) {
            std::vector<unsigned char> storage(bytes);
            auto* items=reinterpret_cast<PDH_FMT_COUNTERVALUE_ITEM_W*>(storage.data());
            if(PdhGetFormattedCounterArrayW(counter,PDH_FMT_DOUBLE,&bytes,&count,items)==ERROR_SUCCESS &&
               count<=storage.size()/sizeof(*items)) {
                GpuEngineUsage usage;
                for(DWORD i=0;i<count;++i)if(items[i].szName)usage.Add(items[i].szName,self.adapter,items[i].FmtValue.doubleValue,
                    items[i].FmtValue.CStatus==PDH_CSTATUS_VALID_DATA || items[i].FmtValue.CStatus==PDH_CSTATUS_NEW_DATA);
                value=usage.Percent();
            }
        }
        self.percent.store(value);
    } } catch(...) {self.percent.store(-1);}
    PdhCloseQuery(query);
}
static DWORD WINAPI SampleGpu(void* raw) {
    auto* worker=static_cast<GpuWorker*>(raw);const auto module=worker->module;
    CollectGpu(*worker->state);
    delete worker;
    // The counter owns no D3D/XR objects. Keep its code loaded until it exits,
    // without making game Quit wait for a slow Windows performance provider.
    FreeLibraryAndExitThread(module,0);
}
bool GpuUsageSampler::Start(ID3D11Device* device) {
    if(state_ || !device)return false;
    Microsoft::WRL::ComPtr<IDXGIDevice> dxgi;Microsoft::WRL::ComPtr<IDXGIAdapter> adapter;DXGI_ADAPTER_DESC desc{};
    if(FAILED(device->QueryInterface(IID_PPV_ARGS(&dxgi))) || FAILED(dxgi->GetAdapter(&adapter)) || FAILED(adapter->GetDesc(&desc)))return false;
    wchar_t name[64]{};swprintf_s(name,L"luid_0x%08x_0x%08x",unsigned(desc.AdapterLuid.HighPart),desc.AdapterLuid.LowPart);
    auto state=std::make_shared<GpuCounterState>();state->adapter=name;HMODULE module{};
    if(!state->stop || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,reinterpret_cast<LPCWSTR>(&SampleGpu),&module))return false;
    auto* worker=new GpuWorker{state,module};const auto handle=CreateThread(nullptr,0,SampleGpu,worker,0,nullptr);
    if(!handle) {delete worker;FreeLibrary(module);return false;}
    CloseHandle(handle);state_=std::move(state);return true;
}
double GpuUsageSampler::Percent() const {return state_?state_->percent.load():-1.;}
void GpuUsageSampler::Stop() {if(state_) {SetEvent(state_->stop);state_.reset();}}
}
