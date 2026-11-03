#include "hud_placement.h"
#include <clocale>
#include <cstdio>
#include <stdexcept>

namespace {
void Require(bool value,const char* message) { if(!value) throw std::runtime_error(message); }
}
int main() {
    try {
        using dirt3vr::HudPlacement;
        Require(HudPlacement::ParseDistance("2.5")==2.5f && HudPlacement::ParseDistance("1")==1.f && HudPlacement::ParseDistance("5.0")==5.f,
                "Valid HUD distances not read");
        for(const char* invalid:{"","0.5","5.5","-2","2,5","2.5m"," 2","nan","inf","1e0","20"})
            Require(HudPlacement::ParseDistance(invalid)==HudPlacement::DefaultDistance,"Invalid HUD distance accepted");
        std::setlocale(LC_ALL,"de-DE"); // A comma locale in the game must not change the launcher's "2.5".
        Require(HudPlacement::ParseDistance("2.5")==2.5f,"HUD distance depends on the locale");
        std::setlocale(LC_ALL,"C");
        Require(HudPlacement::ParseFollow("1") && !HudPlacement::ParseFollow("0") && !HudPlacement::ParseFollow("") && !HudPlacement::ParseFollow("true"),
                "HUD follow switch misread");
        HudPlacement placement; placement.distance=5.f;
        Require(placement.Width(1.6f)==4.f && HudPlacement{}.Width(1.6f)==1.6f,"HUD width does not keep its angular size");
        std::puts("HUD placement: distance, follow and angular size passed");
        return 0;
    } catch(const std::exception& error) { std::fprintf(stderr,"%s\n",error.what()); return 1; }
}
