#include "cockpit_start.h"
#include <cstdio>
#include <fstream>
#include <stdexcept>
#include <vector>

namespace {
void Require(bool value,const char* message) { if(!value) throw std::runtime_error(message); }
namespace cs=dirt3vr::cockpitstart;

std::vector<uint8_t> Image(uintptr_t loadedBase) {
    std::vector<uint8_t> image(cs::defaultRva+0x100,0xcc);
    uint8_t push[5]; cs::Push(loadedBase,push);
    for(const auto& site:cs::sites) {
        std::copy(std::begin(push),std::end(push),image.begin()+site.rva);
        std::copy(site.after,site.after+site.afterSize,image.begin()+site.rva+5);
        std::copy(site.end,site.end+site.endSize,image.begin()+site.endRva);
    }
    std::copy(std::begin(push),std::end(push),image.begin()+cs::defaultRva);
    std::copy(std::begin(cs::defaultAfter),std::end(cs::defaultAfter),image.begin()+cs::defaultRva+5);
    return image;
}

// The supported executable maps .text (RVA 0x1000) from file offset 0x400; on disk the absolute
// addresses are for the preferred base 0x400000.
bool InstalledGameMatches(const char* path) {
    std::ifstream file(path,std::ios::binary);
    if(!file) return false;
    std::vector<uint8_t> image(cs::defaultRva+0x100,0);
    std::vector<std::pair<uintptr_t,size_t>> ranges{{cs::defaultRva,5+sizeof(cs::defaultAfter)}};
    for(const auto& site:cs::sites) { ranges.push_back({site.rva,5+site.afterSize}); ranges.push_back({site.endRva,site.endSize}); }
    for(const auto [rva,size]:ranges) {
        file.seekg(static_cast<std::streamoff>(rva-0x1000+0x400));
        file.read(reinterpret_cast<char*>(image.data()+rva),static_cast<std::streamsize>(size));
        if(!file) return false;
    }
    return cs::SitesMatch(image.data(),0x400000);
}
}

int main(int argc,char** argv) {
    try {
        uint8_t preferred[5]; cs::Push(0x400000,preferred);
        Require(preferred[0]==0x68 && preferred[1]==0x38 && preferred[2]==0x55 && preferred[3]==0xe8 && preferred[4]==0x00,
                "The push is not of 0xe85538 at the preferred base");
        uint8_t setup[5],sequence[5]; cs::Patched(cs::sites[0],setup); cs::Patched(cs::sites[1],sequence);
        Require(setup[0]==0xe9 && setup[1]==0x11 && setup[2]==0 && setup[3]==0 && setup[4]==0,"The race-setup jump does not reach 0x553c19");
        Require(sequence[0]==0xe9 && sequence[1]==0x99 && sequence[2]==0 && sequence[3]==0 && sequence[4]==0,"The start-sequence jump does not reach 0x53bd67");

        for(const uintptr_t base:{uintptr_t{0x400000},uintptr_t{0x550000}}) {
            auto image=Image(base);
            Require(cs::SitesMatch(image.data(),base),"The sites were not recognised at their own base");
            Require(!cs::SitesMatch(image.data(),base+0x10000),"Sites relocated for another base were accepted");
            for(const auto& site:cs::sites) {
                Require(!cs::Applied(image.data(),site),"An unpatched site looked applied");
                uint8_t patched[5]; cs::Patched(site,patched);
                std::copy(std::begin(patched),std::end(patched),image.begin()+site.rva);
                Require(cs::Applied(image.data(),site) && !cs::SiteMatches(image.data(),base,site),"A patched site was not recognised");
            }
        }
        uint8_t headCam[5]; cs::Push(0x400000,headCam,cs::headCamRva);
        Require(headCam[0]==0x68 && headCam[1]==0x2c && headCam[2]==0x55 && headCam[3]==0xe8 && headCam[4]==0x00,
                "The default does not push 0xe8552c (head-cam) at the preferred base");
        for(const uintptr_t base:{uintptr_t{0x400000},uintptr_t{0x550000}}) {
            auto image=Image(base);
            Require(cs::DefaultMatches(image.data(),base,cs::chaseCloseRva) && !cs::DefaultMatches(image.data(),base,cs::headCamRva),
                    "The current_camera default was not recognised");
            uint8_t patched[5]; cs::Push(base,patched,cs::headCamRva);
            std::copy(std::begin(patched),std::end(patched),image.begin()+cs::defaultRva);
            Require(cs::DefaultMatches(image.data(),base,cs::headCamRva) && !cs::SitesMatch(image.data(),base),
                    "The patched default was not recognised");
        }
        {
            auto image=Image(0x400000);
            image[cs::defaultRva+5]=0x90;
            Require(!cs::SitesMatch(image.data(),0x400000),"A different instruction after the default was accepted");
        }
        for(const auto& site:cs::sites) {
            auto image=Image(0x400000);
            image[site.endRva]=0x90;
            Require(!cs::SitesMatch(image.data(),0x400000),"A different jump target was accepted");
            image=Image(0x400000);
            image[site.rva+5]^=0xff;
            Require(!cs::SitesMatch(image.data(),0x400000),"A different instruction after the push was accepted");
        }

        if(argc>1 && argv[1][0]) {
            Require(InstalledGameMatches(argv[1]),"The installed game's chase-view sites differ");
            std::puts("cockpit start: installed game matches");
        }
        std::puts("cockpit start: ok");
        return 0;
    } catch(const std::exception& error) {
        std::fprintf(stderr,"FAIL: %s\n",error.what());
        return 1;
    }
}
