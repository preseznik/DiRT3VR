#include "screenshot.h"
#include <cstdio>
#include <cstring>
#include <stdexcept>

namespace {
void Require(bool value,const char* message) { if(!value) throw std::runtime_error(message); }
namespace ss=dirt3vr::screenshot;
uint32_t Read32(const std::vector<uint8_t>& data,size_t at) {
    return uint32_t(data[at])|uint32_t(data[at+1])<<8|uint32_t(data[at+2])<<16|uint32_t(data[at+3])<<24;
}
}

int main() {
    try {
        Require((ss::ParseSchedule(L"30,90,150")==std::vector<unsigned>{30,90,150}),"Valid schedule rejected");
        Require((ss::ParseSchedule(L"5")==std::vector<unsigned>{5}),"Single time rejected");
        for(const wchar_t* bad:{L"",L"0",L"30,30",L"90,30",L"30,,90",L"30,",L",30",L"3601",L"30 90",L"-5",L"1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17",L"99999999999"})
            Require(ss::ParseSchedule(bad).empty(),"Invalid schedule accepted");
        Require(ss::ParseSchedule(nullptr).empty(),"Null schedule accepted");
        Require(ss::ParseSchedule(L"1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16").size()==16,"Sixteen shots rejected");

        // 2x2 RGBA with a padded pitch: red, green / blue, white (alpha 0 must become opaque).
        const uint8_t rgba[]{255,0,0,0, 0,255,0,0, 9,9,9,9,  0,0,255,0, 255,255,255,0, 9,9,9,9};
        const auto bmp=ss::Bmp(2,2,rgba,12,true);
        Require(bmp.size()==54+16 && bmp[0]=='B' && bmp[1]=='M',"BMP header wrong");
        Require(Read32(bmp,2)==bmp.size() && Read32(bmp,10)==54 && Read32(bmp,18)==2 && int32_t(Read32(bmp,22))==-2,"BMP size fields wrong");
        Require(bmp[28]==32,"BMP is not 32-bit");
        const uint8_t expected[]{0,0,255,255, 0,255,0,255, 255,0,0,255, 255,255,255,255};
        for(size_t i=0;i<sizeof(expected);++i) Require(bmp[54+i]==expected[i],"Pixels not converted to opaque BGRA");
        const auto same=ss::Bmp(1,1,rgba,4,false);
        Require(same[54]==255 && same[56]==0 && same[57]==255,"BGRA rows were swapped");
        // R10G10B10A2: R=1023, G=4 (-> 1), B=512, A=0.
        const uint32_t ten=1023u|(4u<<10)|(512u<<20);
        uint8_t tenRow[4]; std::memcpy(tenRow,&ten,4);
        const auto deep=ss::Bmp10(1,1,tenRow,4);
        Require(deep.size()==58 && deep[54]==128 && deep[55]==1 && deep[56]==255 && deep[57]==255,"10-bit pixel not converted to 8-bit BGRA");
        std::printf("screenshot ok\n");
        return 0;
    } catch(const std::exception& error) {
        std::fprintf(stderr,"%s\n",error.what());
        return 1;
    }
}
