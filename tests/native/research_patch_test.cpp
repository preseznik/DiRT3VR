#include "research_patch.h"
#include <cstdio>
#include <stdexcept>

namespace {
void Require(bool value,const char* message) { if(!value) throw std::runtime_error(message); }
}
int main() {
    try {
        using dirt3vr::research::Parse;
        std::vector<dirt3vr::research::Patch> patches;
        Require(Parse("2970bf:7404:eb04",patches) && patches.size()==1 && patches[0].rva==0x2970bf &&
                patches[0].original==std::vector<uint8_t>{0x74,0x04} && patches[0].replacement==std::vector<uint8_t>{0xeb,0x04},"Single patch did not parse");
        Require(Parse("296812:C686d51d000001:90909090909090,29685e:6a02:9090",patches) && patches.size()==2,"Two patches did not parse");
        for(const char* bad:{"","2970bf","2970bf:74","2970bf:7404:eb","2970bf:7404:eb04:00","zz:7404:eb04","2970bf:74x4:eb04",
                             "fff:7404:eb04","a00000:7404:eb04","123456789:7404:eb04","2970bf::","2970bf:7404:eb04,"}) {
            Require(!Parse(bad,patches) && patches.empty(),"Malformed patch accepted");
        }
        std::printf("research patch parser ok\n");
        return 0;
    } catch(const std::exception& error) {
        std::fprintf(stderr,"%s\n",error.what());
        return 1;
    }
}
