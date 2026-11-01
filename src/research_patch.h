#pragma once
#include <cstdint>
#include <string>
#include <vector>

// Research-only code changes for bounded desktop diagnostics. Compiled into the separate
// research proxy (DIRT3VR_RESEARCH_PATCHES); the shipped proxy never reads the variable.
// DIRT3VR_RESEARCH_PATCH="rva:originalhex:replacementhex[,...]", for example
// "2970bf:7404:eb04". Every original must match before anything is written.
namespace dirt3vr::research {
struct Patch {
    uintptr_t rva{};
    std::vector<uint8_t> original,replacement;
};

inline bool ParseHex(const std::string& text,std::vector<uint8_t>& bytes) {
    if(text.empty() || text.size()%2 || text.size()>64) return false;
    bytes.clear();
    for(size_t i=0;i<text.size();i+=2) {
        auto nibble=[](char c)->int { return c>='0'&&c<='9'?c-'0':c>='a'&&c<='f'?c-'a'+10:c>='A'&&c<='F'?c-'A'+10:-1; };
        const int high=nibble(text[i]),low=nibble(text[i+1]);
        if(high<0 || low<0) return false;
        bytes.push_back(static_cast<uint8_t>(high*16+low));
    }
    return true;
}

// Returns false (and an empty list) for anything malformed.
inline bool Parse(const std::string& text,std::vector<Patch>& patches) {
    patches.clear();
    if(text.empty() || text.size()>512) return false;
    size_t start=0;
    while(start<=text.size()) {
        const auto end=text.find(',',start);
        const auto item=text.substr(start,end==std::string::npos?std::string::npos:end-start);
        const auto first=item.find(':'),second=first==std::string::npos?std::string::npos:item.find(':',first+1);
        Patch patch;
        if(first==std::string::npos || second==std::string::npos || item.find(':',second+1)!=std::string::npos) { patches.clear(); return false; }
        std::vector<uint8_t> address;
        const auto rvaText=item.substr(0,first);
        if(rvaText.empty() || rvaText.size()>8 || rvaText.find_first_not_of("0123456789abcdefABCDEF")!=std::string::npos) { patches.clear(); return false; }
        patch.rva=static_cast<uintptr_t>(std::stoul(rvaText,nullptr,16));
        if(patch.rva<0x1000 || patch.rva>=0xa00000 ||
           !ParseHex(item.substr(first+1,second-first-1),patch.original) ||
           !ParseHex(item.substr(second+1),patch.replacement) ||
           patch.original.size()!=patch.replacement.size()) { patches.clear(); return false; }
        patches.push_back(std::move(patch));
        if(end==std::string::npos) break;
        start=end+1;
    }
    return !patches.empty() && patches.size()<=8;
}

void Apply(void (*log)(const char*,...));
}
