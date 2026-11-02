#include "seat_menu.h"
#include <cstdio>
#include <set>
#include <stdexcept>
#include <vector>

namespace {
using namespace dirt3vr::seat;
void Require(bool value,const char* message) { if(!value) throw std::runtime_error(message); }
bool Near(float a,float b) { return std::abs(a-b)<1e-4f; }
bool Near(Position a,Position b) { return Near(a.x,b.x) && Near(a.y,b.y) && Near(a.z,b.z); }
constexpr unsigned F11=VK_F11;
std::set<unsigned> down;
bool KeyDown(unsigned key) { return down.count(key)!=0; }

// A channel as launcher/SeatChannel.vb writes it: two cars, F11 opens, Num8 / Num2 move up / down.
std::vector<unsigned char> Launcher(bool universal=false) {
    std::vector<unsigned char> bytes(Channel::Bytes);
    Channel c(bytes.data());
    c.Write(0,Channel::Magic); c.Write(4,Channel::Version); c.Write(8,Channel::Bytes); c.Write(12,uint32_t(5000));
    c.Write(128,F11); c.Write(132,0u);
    c.Write(128+3*8,unsigned(VK_NUMPAD8)); c.Write(128+4*8,unsigned(VK_NUMPAD2));
    c.Write(184,2u); c.Write(188,uint32_t(universal));
    c.Write(192,Position{0,.02f,0}); c.Write(204,Position{0,.01f,0});
    const char* codes[]={"ford_fiesta","mini_cooper"};
    for(unsigned i=0;i<2;++i) {
        std::memcpy(bytes.data()+Channel::Table+i*Channel::Stride,codes[i],strlen(codes[i]));
        std::memcpy(bytes.data()+Channel::Table+i*Channel::Stride+16,"Car",3);
    }
    c.Write(Channel::Table+Channel::Stride+64,Position{-.03f,.05f,.04f}); // mini: saved
    c.Write(Channel::Table+Channel::Stride+76,Position{-.03f,.04f,.04f}); // mini: in its camera file
    const wchar_t hint[]=L"D-pad: move";
    std::memcpy(bytes.data()+Channel::Hint,hint,sizeof(hint));
    return bytes;
}
uint32_t Generation(const Channel& c) { return c.Read<uint32_t>(124); }
// A frame every 16 ms with a fresh heartbeat.
struct Clock { uint64_t now=1000; void Frame(Menu& menu,bool foreground=true) { now+=16; menu.Update(now,5000,foreground); } };
}
int main() {
    try {
        {
            auto bytes=Launcher(); Require(Channel::Acceptable(bytes.data()),"Launcher layout refused");
            Channel c(bytes.data());
            Require(c.CarCode(1)=="mini_cooper" && c.CarName(1)=="Car" && c.FindCar("mini_cooper")==1 && c.FindCar("")==-1 && c.HintText()==L"D-pad: move",
                    "Car table or hint misread");
            for(auto [offset,value]:std::initializer_list<std::pair<unsigned,uint32_t>>{{0,1},{4,2},{8,4096},{184,65},{188,2}}) {
                auto bad=Launcher(); Channel(bad.data()).Write(offset,value);
                Require(!Channel::Acceptable(bad.data()),"Bad header accepted");
            }
            auto outside=Launcher(); Channel(outside.data()).Write(Channel::Table+76,Position{0,.6f,0});
            Require(!Channel::Acceptable(outside.data()) && !Channel::Acceptable(nullptr),"Out-of-range applied position accepted");
            Channel::Input input; c.Write(16,1u);
            Require(!c.ReadInput(input),"Input read while the launcher writes");
            c.Write(16,2u); c.Write(20,unsigned(Open));
            Require(c.ReadInput(input) && input.held==Open,"Launcher input misread");
        }
        { // No input filtering: the menu never opens and nothing moves.
            down.clear(); auto bytes=Launcher(); Channel c(bytes.data()); Clock clock;
            Menu menu(c,false,KeyDown); menu.Select(1);
            clock.Frame(menu); down={F11}; clock.Frame(menu); down={VK_NUMPAD8}; clock.Frame(menu); clock.Frame(menu);
            Require(!menu.PanelOpen() && c.Read<uint32_t>(84)==InputUnavailable && Generation(c)==0,"Menu worked without input filtering");
            Require(Near(menu.Offset(),{0,.01f,0}),"Saved position not applied live");
            menu.SetInputReady(true); down.clear(); clock.Frame(menu); down={F11}; clock.Frame(menu);
            Require(menu.PanelOpen() && c.Read<uint32_t>(84)==Ready,"Menu did not open once its input filters were installed");
        }
        { // Car not identified: status 1, no offset, no movement.
            down.clear(); auto bytes=Launcher(); Channel c(bytes.data()); Clock clock;
            Menu menu(c,true,KeyDown); clock.Frame(menu);
            down={VK_NUMPAD8}; clock.Frame(menu);
            Require(c.Read<uint32_t>(84)==CarUnknown && c.Read<int>(88)==-1 && Near(menu.Offset(),{}) && Generation(c)==0,"Unknown car adjusted");
        }
        { // The VR menu with an identified car.
            down={F11}; auto bytes=Launcher(); Channel c(bytes.data()); Clock clock;
            Menu menu(c,true,KeyDown); menu.Select(1);
            clock.Frame(menu);
            Require(!menu.PanelOpen() && c.Read<uint32_t>(84)==Ready && c.Read<int>(88)==1 && Near(menu.Offset(),{0,.01f,0}),"Key held at start opened the menu, or car state wrong");
            Require((c.Read<uint32_t>(92)&1)==0,"State seqlock left odd");
            down.clear(); clock.Frame(menu); down={F11,VK_UP}; clock.Frame(menu);
            Require(menu.PanelOpen() && c.Read<uint32_t>(80)==1 && menu.Openings()==1,"Open key did not open the menu");
            Require(menu.BlocksKey(VK_UP) && menu.BlocksKey(F11),"Menu keys reach the game");
            Require(Near(menu.Current(),{-.03f,.05f,.04f}),"Arrow held while opening moved the seat");
            down={VK_UP}; clock.Frame(menu);
            Require(Near(menu.Current(),{-.03f,.05f,.04f}),"Arrow held from before moved the seat");
            down.clear(); clock.Frame(menu); down={VK_UP}; clock.Frame(menu);
            Require(Near(menu.Current(),{-.03f,.055f,.04f}) && Near(menu.Offset(),{0,.015f,0}),"Up arrow press is not one 5 mm step");
            down={VK_RIGHT}; clock.Frame(menu);
            Require(Near(menu.Current(),{-.03f,.055f,.035f}),"Right arrow did not move forward");
            down={VK_SHIFT,VK_RIGHT}; clock.Frame(menu);
            Require(menu.SidewaysHeld() &&Near(menu.Current(),{-.025f,.055f,.035f}),"Shift+Right did not move sideways");
            down={VK_ESCAPE}; clock.Frame(menu);
            Require(!menu.PanelOpen() && Near(menu.Current(),{-.03f,.05f,.04f}) && Generation(c)==0,"Esc did not cancel");
            down.clear(); clock.Frame(menu); down={F11}; clock.Frame(menu); down.clear(); clock.Frame(menu);
            down={VK_DOWN}; clock.Frame(menu); down.clear(); clock.Frame(menu);
            down={VK_RETURN}; clock.Frame(menu);
            Require(!menu.PanelOpen() && Generation(c)==1 && c.Read<int>(120)==1 && Near(c.Read<Position>(108),{-.03f,.045f,.04f}) &&
                    Near(c.Saved(1),{-.03f,.045f,.04f}) && Near(menu.Offset(),{0,.005f,0}),"Enter did not commit the car's position");
            Require(Near(c.Saved(0),{}) && Near(c.Read<Position>(192),{0,.02f,0}),"Commit wrote another car or the every-car position");
            down.clear(); clock.Frame(menu); down={F11}; clock.Frame(menu);
            down={VK_UP}; clock.Frame(menu); clock.Frame(menu, false);
            Require(!menu.PanelOpen() && Near(menu.Current(),{-.03f,.045f,.04f}),"Leaving the game window kept the menu");
            down.clear(); clock.Frame(menu); down={F11}; clock.Frame(menu);
            Require(menu.PanelOpen(),"Menu did not reopen");
            menu.Update(clock.now+=16,5000+1500,true);
            Require(!menu.PanelOpen(),"Menu stayed open without the launcher to save it");
            down.clear(); clock.Frame(menu); down={F11}; clock.Frame(menu);
            menu.Inactive();
            Require(!menu.PanelOpen() && c.Read<uint32_t>(80)==0,"Inactive frame kept the menu");
        }
        { // Controller input from the launcher, and a bound key moving the seat while the menu is closed.
            down.clear(); auto bytes=Launcher(); Channel c(bytes.data()); Clock clock;
            Menu menu(c,true,KeyDown); menu.Select(1); clock.Frame(menu);
            c.Write(20,unsigned(Open)); clock.Frame(menu);
            Require(menu.PanelOpen(),"Controller open button did not open the menu");
            c.Write(20,0u); clock.Frame(menu); c.Write(20,unsigned(Open)); clock.Frame(menu);
            Require(!menu.PanelOpen(),"Controller open button did not close the menu");
            c.Write(20,0u); clock.Frame(menu);
            down={VK_NUMPAD8}; clock.Frame(menu);
            Require(!menu.PanelOpen() && Near(menu.Current(),{-.03f,.055f,.04f}) && Generation(c)==0,"Bound key did not move the closed menu's seat");
            Require(menu.BlocksKey(VK_NUMPAD8) && !menu.BlocksKey(VK_UP),"Bound key reaches the game, or a free arrow is blocked");
            for(unsigned frame=0;frame<30;++frame) clock.Frame(menu);
            Require(menu.Current().y>.056f,"Held key did not keep moving");
            down.clear(); clock.Frame(menu);
            Require(Generation(c)==0,"Committed before 300 ms without movement");
            for(unsigned frame=0;frame<20;++frame) clock.Frame(menu);
            Require(Generation(c)==1 && c.Read<int>(120)==1 && Near(c.Saved(1),menu.Current()),"Live change not committed after 300 ms");
            down={VK_CONTROL,F11}; clock.Frame(menu);
            Require(!menu.PanelOpen(),"Ctrl+F11 opened a plain F11 menu");
            c.Write(12,uint32_t(1000)); c.Write(20,unsigned(Open)); down.clear(); clock.Frame(menu);
            Require(!menu.PanelOpen(),"Input from a stale launcher opened the menu");
        }
        { // Controller filters: the menu's buttons on the launcher's held device, while it's open.
            down.clear(); auto bytes=Launcher(); Channel c(bytes.data()); Clock clock;
            Menu menu(c,true,KeyDown); menu.Select(1); clock.Frame(menu);
            const GUID wheel{0x1234,1,2,{3,4,5,6,7,8,9,10}},other{0x5678,1,2,{3,4,5,6,7,8,9,10}};
            c.Write(36,1u); c.Write(40,0u); c.Write(60,0x100fu); // XInput pad 0: D-pad and A.
            WORD buttons=0x100f|0x2000; menu.FilterXbox(0,buttons);
            Require(buttons==0x300f,"Pad filtered while the menu is closed");
            c.Write(20,unsigned(Open)); clock.Frame(menu); c.Write(20,0u); clock.Frame(menu);
            Require(menu.PanelOpen(),"Controller did not open the menu");
            buttons=0x1001|0x2000; menu.FilterXbox(0,buttons);
            Require(buttons==0x2000,"Menu buttons reached the game, or B was taken");
            buttons=0x1001; menu.FilterXbox(1,buttons);
            Require(buttons==0x1001,"Another pad was filtered");
            c.Write(20,unsigned(Open)); clock.Frame(menu); c.Write(20,0u); clock.Frame(menu);
            Require(!menu.PanelOpen(),"Controller did not close the menu");
            buttons=0x1000; menu.FilterXbox(0,buttons);
            Require(buttons==0,"A held from the menu reached the game after it closed");
            buttons=0; menu.FilterXbox(0,buttons); buttons=0x1000; menu.FilterXbox(0,buttons);
            Require(buttons==0x1000,"A stayed blocked after its release");

            c.Write(36,2u); c.Write(44,wheel); c.Write(60,1u<<3); c.Write(64,1u<<1); c.Write(76,1u); // Wheel: buttons 3 and 33, its hat.
            Require(!menu.BlocksButton(wheel,3,true) && !menu.BlocksHat(wheel,0,false),"Wheel filtered while the menu is closed");
            c.Write(20,unsigned(Open)); clock.Frame(menu); c.Write(20,0u); clock.Frame(menu);
            Require(menu.BlocksButton(wheel,3,true) && menu.BlocksButton(wheel,33,false) && !menu.BlocksButton(wheel,4,true) && !menu.BlocksButton(wheel,200,true),
                    "Wheel buttons filtered wrongly");
            Require(!menu.BlocksButton(other,3,true) && !menu.BlocksHat(other,0,false),"Another device was filtered");
            Require(menu.BlocksHat(wheel,0,false) && !menu.BlocksHat(wheel,1,false),"Wheel hat filtered wrongly");
            c.Write(20,unsigned(Open)); clock.Frame(menu); c.Write(20,0u); clock.Frame(menu);
            Require(menu.BlocksButton(wheel,3,true) && menu.BlocksHat(wheel,0,false),"Held wheel input reached the game after the menu closed");
            Require(menu.BlocksButton(wheel,3,false) && menu.BlocksHat(wheel,0,true),"Release of a held wheel input not kept");
            Require(!menu.BlocksButton(wheel,3,true) && !menu.BlocksHat(wheel,0,false),"Wheel input stayed blocked after its release");
        }
        { // One position for every car.
            down.clear(); auto bytes=Launcher(true); Channel c(bytes.data()); Clock clock;
            Menu menu(c,true,KeyDown); clock.Frame(menu);
            Require(c.Read<uint32_t>(84)==Ready && Near(menu.Offset(),{0,.01f,0}),"Every-car position not applied without a car");
            down={F11}; clock.Frame(menu); down.clear(); clock.Frame(menu); down={VK_UP}; clock.Frame(menu); down={VK_RETURN}; clock.Frame(menu);
            Require(Generation(c)==1 && c.Read<int>(120)==AllCars && Near(c.Read<Position>(192),{0,.025f,0}) && Near(c.Saved(1),{-.03f,.05f,.04f}),
                    "Every-car commit wrong");
        }
        { // Limits: the seat stays within ±0.5 m.
            Position p{0,.499f,0}; Movement movement;
            for(unsigned i=0;i<200;++i) movement.Update(p,Up,.1);
            Require(Near(p,{0,.5f,0}) && Valid(p) && !Valid({0,.51f,0}) && !Valid({NAN,0,0}),"Seat moved past its limit");
        }
        std::puts("seat menu: channel, menu keys, commits and live offset passed");
        return 0;
    } catch(const std::exception& error) { std::fprintf(stderr,"%s\n",error.what()); return 1; }
}
