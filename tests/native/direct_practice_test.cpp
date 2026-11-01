#include "direct_practice.h"
#include <cstdio>
#include <fstream>
#include <stdexcept>
#include <utility>
#include <vector>

namespace {
void Require(bool value,const char* message) { if(!value) throw std::runtime_error(message); }
namespace dp=dirt3vr::directpractice;

// A fake image whose compare is relocated for loadedBase.
std::vector<uint8_t> Image(uintptr_t loadedBase) {
    std::vector<uint8_t> image(dp::forceAiRva+64,0xcc);
    uint8_t check[13]; dp::BenchmarkCheck(loadedBase,check);
    std::copy(std::begin(check),std::end(check),image.begin()+dp::benchmarkCheckRva);
    std::copy(std::begin(dp::forceAi),std::end(dp::forceAi),image.begin()+dp::forceAiRva);
    return image;
}

// The supported executable maps .text (RVA 0x1000) from file offset 0x400; on disk the
// absolute addresses are for the preferred base 0x400000.
bool InstalledGameMatches(const char* path) {
    std::ifstream file(path,std::ios::binary);
    if(!file) return false;
    std::vector<uint8_t> image(dp::forceAiRva+sizeof(dp::forceAi));
    for(const auto [rva,size]:{std::pair{dp::benchmarkCheckRva,size_t{13}},std::pair{dp::forceAiRva,sizeof(dp::forceAi)}}) {
        file.seekg(static_cast<std::streamoff>(rva-0x1000+0x400));
        file.read(reinterpret_cast<char*>(image.data()+rva),static_cast<std::streamsize>(size));
        if(!file) return false;
    }
    return dp::SitesMatch(image.data(),0x400000);
}
}

int main(int argc,char** argv) {
    try {
        uint8_t preferred[13]; dp::BenchmarkCheck(0x400000,preferred);
        Require(preferred[2]==0xf0 && preferred[3]==0xce && preferred[4]==0xf9 && preferred[5]==0x00,"Preferred-base flag address is not 0xf9cef0");

        for(const uintptr_t base:{uintptr_t{0x400000},uintptr_t{0x2a0000}}) {
            auto image=Image(base);
            const auto before=image;
            Require(dp::SitesMatch(image.data(),base) && !dp::Applied(image.data(),base),"Original controller update not recognised");
            Require(!dp::SitesMatch(image.data(),base+0x10000),"Compare relocated for another base was accepted");
            Require(dp::Keep(image.data(),base) && dp::Applied(image.data(),base),"Force-AI branch was not made unconditional");
            for(size_t i=0;i<image.size();++i) Require(i==dp::forceAiRva || image[i]==before[i],"Bytes other than the branch changed");
            Require(image[dp::forceAiRva]==0xeb,"Branch opcode is not jmp");
            Require(!dp::Keep(image.data(),base),"Already-changed code was accepted again");
        }

        auto other=Image(0x400000);
        other[dp::benchmarkCheckRva+2]^=0xff; // a different global: not the benchmark flag
        const auto untouched=other;
        Require(!dp::Keep(other.data(),0x400000) && other==untouched,"Code checking another flag was changed");
        auto shifted=Image(0x400000);
        shifted[dp::forceAiRva+4]=0x2d; // a different controller field
        Require(!dp::Keep(shifted.data(),0x400000),"Code writing another field was changed");

        if(argc>1) {
            std::ifstream probe(argv[1],std::ios::binary);
            if(probe) Require(InstalledGameMatches(argv[1]),"Installed dirt3_game.exe has a different controller update");
            else std::printf("Installed game not found; file check skipped\n");
        }
        std::printf("direct practice site ok\n");
        return 0;
    } catch(const std::exception& error) {
        std::fprintf(stderr,"%s\n",error.what());
        return 1;
    }
}
