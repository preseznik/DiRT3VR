// Synthetic renderer memory and a hidden fixture window; never loads the game.
#include "../../src/scene_resolution.cpp"
#include <cstdio>
#include <stdexcept>
#include <thread>
#include <vector>

using namespace dirt3vr;
using namespace dirt3vr::sceneresolution;
namespace {
void Check(bool value,const char* why) { if(!value) throw std::runtime_error(why); }
template<size_t N> void Seed(std::vector<unsigned char>& image,uintptr_t rva,const unsigned char (&bytes)[N]) {
    std::memcpy(image.data()+rva,bytes,N);
}
Renderer Fixture(HWND window) {
    Renderer r{};
    r.window=r.swap.OutputWindow=window;
    r.device=reinterpret_cast<IUnknown*>(0x1234);
    r.width=r.windowWidth=r.swap.BufferDesc.Width=1280;
    r.height=r.windowHeight=r.swap.BufferDesc.Height=720;
    r.swap.Windowed=TRUE; r.swap.SampleDesc.Count=1;
    r.swap.BufferDesc.Format=DXGI_FORMAT_R10G10B10A2_UNORM;
    return r;
}
}
int main() {
    HWND window{};
    try {
        WNDCLASSW wc{}; wc.lpfnWndProc=DefWindowProcW;
        wc.hInstance=GetModuleHandleW(nullptr); wc.lpszClassName=L"NeonClass_41";
        Check(RegisterClassW(&wc)!=0,"register fixture window");
        window=CreateWindowW(wc.lpszClassName,L"DiRT3VR resolution fixture",0,0,0,1280,720,nullptr,nullptr,wc.hInstance,nullptr);
        Check(window!=nullptr,"create hidden fixture window");
        std::vector<unsigned char> image(0x3ca000);
        Seed(image,DimensionsRva,DimensionsBytes); Seed(image,DescriptorRva,DescriptorBytes);
        Seed(image,CreateRva,CreateBytes); Seed(image,WindowRva,WindowBytes);
        Seed(image,RecreateRva,RecreateBytes); Seed(image,ResizeRva,ResizeBytes);
        const auto base=reinterpret_cast<uintptr_t>(image.data());
        HeadsetResolutionRequest request;
        Check(request.Configure(3072,3264,100),"valid recommended resolution");
        Binding binding; auto r=Fixture(window); const auto original=r;
        const auto create=[&](uintptr_t caller) {return binding.PrepareCreate(base,caller,r.device,&r.swap,&r.chain,request);};
        Check(!create(base+CreateReturnRva+1),"foreign caller refused");
        Check(!binding.PrepareCreate(base,base+CreateReturnRva,r.device,reinterpret_cast<DXGI_SWAP_CHAIN_DESC*>(1),&r.chain,request),"unreadable renderer refused");
        image[CreateRva]^=1; Check(!create(base+CreateReturnRva),"modified instructions refused"); image[CreateRva]^=1;
        r.windowWidth=1920; Check(!create(base+CreateReturnRva),"disagreeing engine fields refused"); r=original;
        r.fullscreen=1; Check(!create(base+CreateReturnRva),"fullscreen refused"); r=original;
        r.window=r.swap.OutputWindow=GetDesktopWindow(); Check(!create(base+CreateReturnRva),"foreign window refused"); r=original;
        r.swap.SampleDesc.Count=4; Check(!create(base+CreateReturnRva),"MSAA contract refused"); r=original;
        IDXGISwapChain* local{};
        Check(!binding.PrepareCreate(base,base+CreateReturnRva,r.device,&r.swap,&local,request),"initial output must be renderer field");
        auto corrupt=request; corrupt.sceneWidth=4097;
        Check(!binding.PrepareCreate(base,base+CreateReturnRva,r.device,&r.swap,&r.chain,corrupt),"unbounded derived request refused");
        Check(!std::memcmp(&r,&original,sizeof(r)),"all rejected paths preserve renderer bytes");
        auto change=create(base+CreateReturnRva);
        Check(bool(change) && r.width==3072 && r.height==3264 && FieldsAgree(r),"engine and descriptor sizes set together");
        binding.CompleteCreate(change,E_OUTOFMEMORY,nullptr);
        Check(!std::memcmp(&r,&original,sizeof(r)),"failed creation restores exact renderer");
        change=create(base+CreateReturnRva);
        Check(bool(change),"creation can retry after failure");
        auto* chain=reinterpret_cast<IDXGISwapChain*>(0x5678);
        r.chain=chain; binding.CompleteCreate(change,S_OK,chain);
        r.width=r.windowWidth=r.swap.BufferDesc.Width=1600;
        r.height=r.windowHeight=r.swap.BufferDesc.Height=900;
        const auto beforeResize=r;
        UINT width=1600,height=900;
        auto resize=[&](uintptr_t caller) {return binding.PrepareResize(base,caller,chain,width,height,DXGI_FORMAT_UNKNOWN);};
        Check(!resize(base+ResizeReturnRva+1),"unrelated resize caller refused");
        bool foreignAccepted{};
        std::thread foreign([&] {foreignAccepted=bool(resize(base+ResizeReturnRva));}); foreign.join();
        Check(!foreignAccepted,"foreign render thread refused");
        image[ResizeRva]^=1; Check(!resize(base+ResizeReturnRva),"modified resize instructions refused"); image[ResizeRva]^=1;
        change=resize(base+ResizeReturnRva);
        Check(bool(change) && width==3072 && height==3264 && FieldsAgree(r),"resize reasserts request before allocation");
        binding.CompleteResize(change,DXGI_ERROR_INVALID_CALL);
        Check(!std::memcmp(&r,&beforeResize,sizeof(r)),"failed resize restores six words");
        width=height=0; change=resize(base+ResizeReturnRva);
        Check(bool(change) && width==3072 && height==3264,"implicit window size resolved to explicit scene size");
        r.prefix[0]=42; binding.CompleteResize(change,E_FAIL);
        Check(r.prefix[0]==42 && r.width==1600,"rollback preserves unrelated engine writes");
        width=1600; height=900; change=resize(base+ResizeReturnRva);
        r.width=1920; binding.CompleteResize(change,E_FAIL);
        Check(r.width==1920,"rollback does not overwrite subsequent dimension edits");
        r=beforeResize;
        binding.Forget(chain); r.chain=nullptr;
        Check(!resize(base+ResizeReturnRva),"released swapchain cannot resize through stale binding");
        local=nullptr;
        change=binding.PrepareCreate(base,base+RecreateReturnRva,r.device,&r.swap,&local,request);
        Check(bool(change) && change.recreation,"verified recreation accepts native local output");
        binding.CompleteCreate(change,E_FAIL,nullptr);
        Check(r.width==1600 && r.height==900,"failed recreation restores previous dimensions");
        change=binding.PrepareCreate(base,base+RecreateReturnRva,r.device,&r.swap,&local,request);
        local=reinterpret_cast<IDXGISwapChain*>(0x9abc);
        binding.CompleteCreate(change,S_OK,local);
        r.chain=local; // The native assignment happens after the factory returns.
        width=3072; height=3264;
        Check(bool(binding.PrepareResize(base,base+ResizeReturnRva,local,width,height,DXGI_FORMAT_UNKNOWN)),"recreated chain owns later resize");
        DestroyWindow(window);
        puts("Scene resolution: guarded creation, resize, recreation, rollback and ownership passed (synthetic only)");
        return 0;
    } catch(const std::exception& ex) {
        if(window) DestroyWindow(window);
        fprintf(stderr,"%s\n",ex.what()); return 1;
    }
}
