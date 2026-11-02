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
    if(!dp::SitesMatch(image.data(),0x400000)) return false;
    std::vector<uint8_t> exit(dp::exitCheckRva+26);
    file.seekg(static_cast<std::streamoff>(dp::exitCheckRva-0x1000+0x400));
    file.read(reinterpret_cast<char*>(exit.data()+dp::exitCheckRva),26);
    if(!file || !dp::ExitSiteMatches(exit.data(),0x400000)) return false;
    std::vector<uint8_t> pause(dp::pauseCheckRva+13);
    file.seekg(static_cast<std::streamoff>(dp::pauseCheckRva-0x1000+0x400));
    file.read(reinterpret_cast<char*>(pause.data()+dp::pauseCheckRva),13);
    if(!file || !dp::PauseSiteMatches(pause.data(),0x400000)) return false;
    std::vector<uint8_t> lookup(dp::difficultyRva+0x23);
    file.seekg(static_cast<std::streamoff>(dp::difficultyRva-0x1000+0x400));
    file.read(reinterpret_cast<char*>(lookup.data()+dp::difficultyRva),0x23);
    if(!file || !dp::DifficultySiteMatches(lookup.data())) return false;
    std::vector<uint8_t> settings(dp::settingLookupRva+sizeof(dp::settingLookupEntry));
    file.seekg(static_cast<std::streamoff>(dp::settingLookupRva-0x1000+0x400));
    file.read(reinterpret_cast<char*>(settings.data()+dp::settingLookupRva),sizeof(dp::settingLookupEntry));
    return file && dp::SettingLookupMatches(settings.data());
}

std::vector<uint8_t> ExitImage(uintptr_t loadedBase) {
    std::vector<uint8_t> image(dp::exitCheckRva+64,0xcc);
    uint8_t check[26]; dp::ExitCheck(loadedBase,check);
    std::copy(std::begin(check),std::end(check),image.begin()+dp::exitCheckRva);
    return image;
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

        uint32_t count=0;
        Require(!dp::KeepLaps(0,count,3) && count==0,"Laps written while no benchmark is active");
        Require(dp::KeepLaps(1,count,3) && count==3,"Requested laps not written into the benchmark count");
        Require(!dp::KeepLaps(1,count,3) && count==3,"Unchanged count rewritten");
        count=7;
        Require(dp::KeepLaps(1,count,2) && count==2,"Count the game changed was not restored to the request");
        Require(!dp::KeepLaps(1,count,0) && !dp::KeepLaps(1,count,dp::maxLaps+1) && count==2,"Out-of-range lap request accepted");
        Require(!dp::KeepLaps(2,count,3) && count==2,"Unexpected benchmark flag value treated as active");
        Require(dp::lapsRva==dp::benchmarkFlagRva+0xe74,"Lap count is not config+0xef4 (flag is config+0x80)");

        uint8_t preferredExit[26]; dp::ExitCheck(0x400000,preferredExit);
        Require(preferredExit[22]==0x9c && preferredExit[23]==0x11 && preferredExit[24]==0xe0 && preferredExit[25]==0x00,"Preferred-base ExitProcess slot is not 0xe0119c");
        Require(dp::exitBranchRva==dp::exitCheckRva+12,"Exit branch is not the je after the +0x81 compare");
        for(const uintptr_t base:{uintptr_t{0x400000},uintptr_t{0x280000}}) {
            auto exit=ExitImage(base);
            const auto before=exit;
            Require(dp::ExitSiteMatches(exit.data(),base) && !dp::ExitSiteMatches(exit.data(),base+0x10000),"Exit site not recognised for its own base only");
            Require(dp::SkipExit(exit.data(),base),"Exit branch not replaced");
            for(size_t i=0;i<exit.size();++i) {
                const bool inBranch=i>=dp::exitBranchRva && i<dp::exitBranchRva+sizeof(dp::skipExit);
                Require(inBranch ? exit[i]==dp::skipExit[i-dp::exitBranchRva] : exit[i]==before[i],"Bytes outside the exit branch changed");
            }
            // jmp rel32 from the branch must land where the je went (0x6a8757 = RVA 0x2a8757).
            int32_t rel; std::memcpy(&rel,exit.data()+dp::exitBranchRva+1,4);
            Require(dp::exitBranchRva+5+rel==0x2a8757,"Jump target differs from the je's target");
            Require(!dp::SkipExit(exit.data(),base),"Already-changed exit code was accepted again");
        }
        for(const uintptr_t base:{uintptr_t{0x400000},uintptr_t{0x280000}}) {
            std::vector<uint8_t> pause(dp::pauseCheckRva+32,0xcc);
            uint8_t check[13]; dp::PauseCheck(base,check);
            std::copy(std::begin(check),std::end(check),pause.begin()+dp::pauseCheckRva);
            const auto before=pause;
            Require(dp::PauseSiteMatches(pause.data(),base) && !dp::PauseSiteMatches(pause.data(),base+0x10000),"Pause site not recognised for its own base only");
            Require(dp::AllowPause(pause.data(),base),"Pause branch not made unconditional");
            for(size_t i=0;i<pause.size();++i) Require(i==dp::pauseBranchRva ? pause[i]==0xeb : pause[i]==before[i],"Bytes other than the pause branch changed");
            Require(!dp::AllowPause(pause.data(),base),"Already-changed pause code was accepted again");
        }
        Require(dp::pauseBranchRva==dp::pauseCheckRva+7,"Pause branch is not the je after the flag compare");

        {
            std::vector<uint8_t> lookup(dp::difficultyRva+64,0xcc);
            std::copy(std::begin(dp::difficultyEntry),std::end(dp::difficultyEntry),lookup.begin()+dp::difficultyRva);
            std::copy(std::begin(dp::difficultyBranch),std::end(dp::difficultyBranch),lookup.begin()+dp::difficultyRva+0x15);
            std::copy(std::begin(dp::difficultyTail),std::end(dp::difficultyTail),lookup.begin()+dp::difficultyRva+0x1c);
            Require(dp::DifficultySiteMatches(lookup.data()),"Difficulty lookup not recognised");
            lookup[dp::difficultyRva+0x1d]=0xe9;
            Require(!dp::DifficultySiteMatches(lookup.data()),"Difficulty lookup with another call accepted");
        }
        Require(dp::DifficultyAnswer(1,0,3)==0 && dp::DifficultyAnswer(1,5,-1)==5,"Requested difficulty not used in a benchmark start");
        Require(dp::DifficultyAnswer(0,5,2)==2,"Difficulty changed outside a benchmark start");
        Require(dp::DifficultyAnswer(1,-1,2)==2 && dp::DifficultyAnswer(1,6,2)==2,"Unset or invalid difficulty request applied");

        {
            int values[dp::assistCount];
            Require(dp::ParseAssists("assists_abs=0,transmission_type=3,assists_dyn_race_line=2",values),"Valid assists request rejected");
            Require(values[dp::AssistIndex("assists_abs")]==0 && values[dp::AssistIndex("transmission_type")]==3 &&
                    values[dp::AssistIndex("assists_dyn_race_line")]==2 && values[dp::AssistIndex("assists_stability")]==-1,
                    "Assists request parsed into the wrong settings");
            for(const char* bad:{"","assists_abs","assists_abs=","assists_abs=2","assists_dyn_race_line=3","transmission_type=4",
                                 "assists_abs=0,","assists_abs=0,assists_abs=1","difficulty=1","assists_abs=0x","=1",
                                 "assists_abs=0;assists_stability=0","assists_abs=10"}) {
                Require(!dp::ParseAssists(bad,values),"Invalid assists request accepted");
                for(const int value:values) Require(value==-1,"Rejected assists request left values behind");
            }
            Require(dp::AssistIndex("assists_abs_db_item")==-1 && dp::AssistIndex(nullptr)==-1,"Unlisted setting name accepted");
            Require(dp::AssistAnswer(1,0,1)==0 && dp::AssistAnswer(1,3,0)==3,"Requested assist not used in a benchmark start");
            Require(dp::AssistAnswer(0,0,1)==1 && dp::AssistAnswer(1,-1,1)==1,"Assist changed outside a benchmark start or unrequested");
            std::vector<uint8_t> image(dp::settingLookupRva+32,0xcc);
            std::copy(std::begin(dp::settingLookupEntry),std::end(dp::settingLookupEntry),image.begin()+dp::settingLookupRva);
            Require(dp::SettingLookupMatches(image.data()),"Settings lookup not recognised");
            image[dp::settingLookupRva+10]=0x78;
            Require(!dp::SettingLookupMatches(image.data()),"Settings lookup on another table accepted");
        }

        uint8_t active=1,commandLine=1;
        Require(!dp::EndBenchmark(active,commandLine,1,0) && active==1,"Benchmark ended before the track was complete");
        Require(!dp::EndBenchmark(active,commandLine,0,0) && active==1,"Benchmark ended with no requested tracks");
        Require(dp::EndBenchmark(active,commandLine,1,1) && active==0 && commandLine==0,"Benchmark mode not ended after the result");
        Require(!dp::EndBenchmark(active,commandLine,1,1),"Inactive benchmark ended twice");

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
