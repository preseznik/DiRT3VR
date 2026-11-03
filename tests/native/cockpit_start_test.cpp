#include "cockpit_start.h"
#include <cstdio>
#include <fstream>
#include <stdexcept>
#include <vector>

namespace {
void Require(bool value,const char* message) { if(!value) throw std::runtime_error(message); }
namespace cs=dirt3vr::cockpitstart;

std::vector<uint8_t> Image(uintptr_t loadedBase) {
    std::vector<uint8_t> image(cs::switchRva+0x100,0xcc);
    uint8_t original[7]; cs::Original(loadedBase,original);
    std::copy(std::begin(original),std::end(original),image.begin()+cs::switchRva);
    std::copy(std::begin(cs::switchEnd),std::end(cs::switchEnd),image.begin()+cs::switchEndRva);
    return image;
}

// The supported executable maps .text (RVA 0x1000) from file offset 0x400; on disk the absolute
// addresses are for the preferred base 0x400000.
bool InstalledGameMatches(const char* path) {
    std::ifstream file(path,std::ios::binary);
    if(!file) return false;
    std::vector<uint8_t> image(cs::switchRva+0x100,0);
    for(const auto [rva,size]:{std::pair{cs::switchRva,size_t{7}},std::pair{cs::switchEndRva,sizeof(cs::switchEnd)}}) {
        file.seekg(static_cast<std::streamoff>(rva-0x1000+0x400));
        file.read(reinterpret_cast<char*>(image.data()+rva),static_cast<std::streamsize>(size));
        if(!file) return false;
    }
    return cs::SiteMatches(image.data(),0x400000);
}
}

int main(int argc,char** argv) {
    try {
        uint8_t preferred[7]; cs::Original(0x400000,preferred);
        Require(preferred[0]==0x68 && preferred[1]==0x38 && preferred[2]==0x55 && preferred[3]==0xe8 && preferred[4]==0x00,
                "The push is not of 0xe85538 at the preferred base");
        uint8_t patched[5]; cs::Patched(patched);
        Require(patched[0]==0xe9 && patched[1]==0x99 && patched[2]==0 && patched[3]==0 && patched[4]==0,"The jump does not reach 0x53bd67");

        for(const uintptr_t base:{uintptr_t{0x400000},uintptr_t{0x2a0000}}) {
            auto image=Image(base);
            Require(cs::SiteMatches(image.data(),base),"The site was not recognised at its own base");
            Require(!cs::SiteMatches(image.data(),base+0x10000),"A site relocated for another base was accepted");
            Require(!cs::Applied(image.data()),"An unpatched site looked applied");
            std::copy(std::begin(patched),std::end(patched),image.begin()+cs::switchRva);
            Require(cs::Applied(image.data()) && !cs::SiteMatches(image.data(),base),"The patched site was not recognised");
        }
        auto image=Image(0x400000);
        image[cs::switchEndRva]=0x90;
        Require(!cs::SiteMatches(image.data(),0x400000),"A different jump target was accepted");

        if(argc>1) {
            if(!InstalledGameMatches(argv[1])) { std::puts("cockpit start: installed game skipped (not found or different)"); }
            else std::puts("cockpit start: installed game matches");
        }
        std::puts("cockpit start: ok");
        return 0;
    } catch(const std::exception& error) {
        std::fprintf(stderr,"FAIL: %s\n",error.what());
        return 1;
    }
}
