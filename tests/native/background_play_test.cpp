#include "background_play.h"
#include <cstdio>
#include <fstream>
#include <stdexcept>
#include <vector>

namespace {
void Require(bool value,const char* message) { if(!value) throw std::runtime_error(message); }
namespace bp=dirt3vr::backgroundplay;

std::vector<uint8_t> Image() {
    std::vector<uint8_t> image(bp::pauseReplayRva+64,0xcc);
    std::copy(std::begin(bp::pauseMenu),std::end(bp::pauseMenu),image.begin()+bp::pauseMenuRva);
    std::copy(std::begin(bp::pauseReplay),std::end(bp::pauseReplay),image.begin()+bp::pauseReplayRva);
    return image;
}

// The supported executable maps .text (RVA 0x1000) from file offset 0x400.
bool InstalledGameMatches(const char* path) {
    std::ifstream file(path,std::ios::binary);
    if(!file) return false;
    std::vector<uint8_t> image(bp::pauseReplayRva+sizeof(bp::pauseReplay));
    for(const auto [rva,size]:{std::pair{bp::pauseMenuRva,sizeof(bp::pauseMenu)},std::pair{bp::pauseReplayRva,sizeof(bp::pauseReplay)}}) {
        file.seekg(static_cast<std::streamoff>(rva-0x1000+0x400));
        file.read(reinterpret_cast<char*>(image.data()+rva),static_cast<std::streamsize>(size));
        if(!file) return false;
    }
    return bp::SitesMatch(image.data());
}
}

int main(int argc,char** argv) {
    try {
        auto image=Image();
        const auto before=image;
        Require(bp::SitesMatch(image.data()) && !bp::SitesSuppressed(image.data()),"Original focus-loss bytes not recognised");
        Require(bp::Suppress(image.data()) && bp::SitesSuppressed(image.data()),"Pause request and replay pause were not suppressed");
        for(size_t i=0;i<image.size();++i) {
            const bool site=(i>=bp::pauseMenuRva && i<bp::pauseMenuRva+sizeof(bp::pauseMenu)) ||
                            (i>=bp::pauseReplayRva && i<bp::pauseReplayRva+sizeof(bp::pauseReplay));
            Require(site || image[i]==before[i],"Bytes outside the two sites changed");
        }
        Require(!bp::Suppress(image.data()),"Already-changed code was accepted again");
        bp::Restore(image.data());
        Require(image==before,"Restore did not put back the original instructions");

        auto other=Image();
        other[bp::pauseReplayRva+3]^=0xff;
        const auto untouched=other;
        Require(!bp::Suppress(other.data()) && other==untouched,"Different code was changed");
        Require(bp::patchLength==bp::pauseReplayRva+sizeof(bp::pauseReplay)-bp::pauseMenuRva,"Patch span is wrong");

        if(argc>1) {
            std::ifstream probe(argv[1],std::ios::binary);
            if(probe) Require(InstalledGameMatches(argv[1]),"Installed dirt3_game.exe has different focus-loss instructions");
            else std::printf("Installed game not found; file check skipped\n");
        }
        std::printf("background play sites ok\n");
        return 0;
    } catch(const std::exception& error) {
        std::fprintf(stderr,"%s\n",error.what());
        return 1;
    }
}
