#include "intro_state.h"
#include <windows.h>
#include <bcrypt.h>
#include <array>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <stdexcept>
#include <thread>
#include <vector>

using namespace dirt3vr;
namespace {
void Check(bool value,const char* message) { if(!value) throw std::runtime_error(message); }
void Log(const char*,...) {}
struct Image {
    std::vector<unsigned char> bytes;
    uint32_t base{},headers{}; unsigned sections{};
    size_t Offset(uintptr_t address,size_t length) const {
        if(address<base) throw std::runtime_error("address before image");
        const auto rva=address-base;
        for(unsigned index=0;index<sections;++index) {
            const auto header=headers+index*40;
            const auto sectionRva=Word(header+12),rawSize=Word(header+16),raw=Word(header+20);
            if(rva>=sectionRva && rva-sectionRva<=rawSize && length<=rawSize-(rva-sectionRva) &&
                raw+(rva-sectionRva)<=bytes.size() && length<=bytes.size()-(raw+(rva-sectionRva))) return size_t(raw+rva-sectionRva);
        }
        throw std::runtime_error("unmapped image address");
    }
    uint32_t Word(size_t at) const {
        Check(at<=bytes.size() && 4<=bytes.size()-at,"bounded image word");
        uint32_t result{}; std::memcpy(&result,bytes.data()+at,4); return result;
    }
    static bool Read(uintptr_t address,void* output,size_t size,void* opaque) noexcept {
        try { auto& image=*static_cast<Image*>(opaque); std::memcpy(output,image.bytes.data()+image.Offset(address,size),size); return true; }
        catch(...) { return false; }
    }
};
void FileContract(const std::filesystem::path& file) {
    if(!std::filesystem::is_regular_file(file)) { puts("Optional installed-executable contract skipped: file absent"); return; }
    std::ifstream source(file,std::ios::binary);
    Image image; image.bytes.assign(std::istreambuf_iterator<char>(source),{});
    Check(source.eof() || !source.fail(),"read executable");
    BCRYPT_ALG_HANDLE algorithm{}; BCRYPT_HASH_HANDLE hash{};
    Check(BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)>=0,"SHA provider");
    Check(BCryptCreateHash(algorithm,&hash,nullptr,0,nullptr,0,0)>=0,"SHA state");
    std::array<unsigned char,32> digest{};
    const bool hashed=BCryptHashData(hash,image.bytes.data(),ULONG(image.bytes.size()),0)>=0 && BCryptFinishHash(hash,digest.data(),ULONG(digest.size()),0)>=0;
    BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(algorithm,0); Check(hashed,"SHA executable");
    const unsigned char expected[]={0xc5,0x02,0xb4,0x95,0xbb,0x34,0xa0,0x97,0x44,0x07,0x56,0x76,0x9c,0x80,0xdc,0xb3,
        0x6a,0x79,0xb6,0x18,0x94,0x3f,0x73,0x9e,0x20,0xd5,0x91,0xd5,0x41,0x71,0xff,0x98};
    Check(!std::memcmp(expected,digest.data(),32),"supported executable fingerprint");
    const auto pe=image.Word(60),optional=pe+24;
    Check((image.Word(0)&0xffff)==0x5a4d && image.Word(pe)==0x4550 && (image.Word(pe+4)&0xffff)==0x14c &&
        (image.Word(optional)&0xffff)==0x10b,"x86 PE contract");
    image.base=image.Word(optional+28); image.sections=image.Word(pe+4)>>16;
    image.headers=optional+(image.Word(pe+20)&0xffff);
    Check(intro_detail::ValidateContract(image.base,Image::Read,&image),"native callback bytes/vtable/type contract");
    for(const auto rva:{0xe0820u,0xe09d0u,0xa953a8u,0xa953b4u,0xe06fau,0xa9a9f4u}) {
        auto& value=image.bytes[image.Offset(image.base+rva,1)]; value^=1;
        Check(!intro_detail::ValidateContract(image.base,Image::Read,&image),"mutated native contract refused"); value^=1;
    }
    Check(!intro_detail::ValidateContract(image.base+0x1000,Image::Read,&image),"wrong image base refused");
    // Model the loader relocating the three absolute pointers consumed by
    // the guard. Relative call bytes and type text must remain unchanged.
    constexpr uint32_t relocation=0x20000000;
    for(const auto rva:{0xa953a8u,0xa953b4u,0xe06fau}) {
        const auto at=image.Offset(image.base+rva,4); const auto pointer=image.Word(at)+relocation;
        std::memcpy(image.bytes.data()+at,&pointer,4);
    }
    image.base+=relocation;
    Check(intro_detail::ValidateContract(image.base,Image::Read,&image),"properly relocated image contract accepted");
}
void LatchScenarios() {
    intro_detail::IntroLatch intro;
    Check(!intro.Active(false),"startup does not force a screen before evidence");
    Check(intro.Active(true) && intro.Active(false),"first movie arms startup hold");
    for(unsigned frame=0;frame<10000;++frame) Check(intro.Active(false),"clip boundary/native loading has no arbitrary expiry");
    Check(intro.Active(true),"second intro movie remains on screen"); intro.Enter();
    Check(intro.Active(false),"native final legal remains on screen without YUV"); intro.Leave();
    Check(!intro.Active(false),"native legal completion releases startup hold");
    Check(intro.Active(true) && !intro.Active(false),"later video is frame-local and cannot rearm startup");
    intro.Enter(); Check(!intro.Active(false),"repeated native enter cannot undo completion");
    intro_detail::IntroLatch skipped; skipped.Enter(); Check(skipped.Active(false),"skipped movies still admit native final legal");
    skipped.Leave(); Check(!skipped.Active(false),"skipped movie flow completes");
    intro_detail::IntroLatch late; late.Leave(); Check(late.Active(true) && !late.Active(false),"late first movie cannot rearm completed intro");
    for(unsigned iteration=0;iteration<100;++iteration) {
        intro_detail::IntroLatch concurrent;
        std::thread render([&] { for(unsigned i=0;i<1000;++i) concurrent.Active(i%2==0); });
        std::thread callbacks([&] { concurrent.Enter(); concurrent.Leave(); for(unsigned i=0;i<1000;++i) concurrent.Enter(); });
        render.join(); callbacks.join();
        Check(!concurrent.Active(false),"concurrent video/enter cannot overwrite completed state");
    }
}
}
int main(int argc,char** argv) {
    try {
        Check(!IntroScreenActive(true),"uninitialized observer cannot retain intro");
        Check(!InitializeIntroState(nullptr) && !InitializeIntroState(Log),"test executable must refuse production hooks");
        Check(!IntroScreenActive(true),"failed guards cannot activate presentation policy");
        Check(!intro_detail::ValidateContract(0,nullptr,nullptr),"missing reader contract refused");
        LatchScenarios();
        FileContract(argc>1 ? std::filesystem::path(argv[1]) : std::filesystem::path(L"C:/Program Files (x86)/Steam/steamapps/common/DiRT 3 Complete Edition/dirt3_game.exe"));
        puts("native initial-legal signatures, intro handoff, refusal and atomic latch scenarios passed"); return 0;
    } catch(const std::exception& error) { fprintf(stderr,"intro state test: %s\n",error.what()); return 1; }
}
