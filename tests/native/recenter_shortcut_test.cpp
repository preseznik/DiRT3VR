#include "recenter_shortcut.h"
#include <cstdio>
#include <stdexcept>

namespace {
void Require(bool value,const char* message) { if(!value) throw std::runtime_error(message); }
}
int main() {
    try {
        using dirt3vr::RecenterShortcut;
        using dirt3vr::RecenterPoll;
        RecenterShortcut value;
        Require(value.key==121 && value.modifiers==0 && value.Valid() && value.Describe()=="F10","Default recenter is not F10");
        Require(RecenterShortcut::Parse("82 5",value) && value.key==82 && value.modifiers==5 && value.Describe()=="Ctrl+Shift+R","Ctrl+Shift+R did not parse");
        Require(RecenterShortcut::Parse("101 0",value) && value.Describe()=="Num5","Numpad key did not parse");
        Require(RecenterShortcut::Parse("135 2",value) && value.Describe()=="Alt+F24","F24 did not parse");
        const RecenterShortcut before=value;
        for(const char* invalid:{"","121","121 ","121 0 ","x 0","121 8","121 -1","16 0","17 0","18 0","27 0","91 0","60 0","9 2","115 2","46 3","46 7","999999999999 0","121 0000000000000001"}) {
            Require(!RecenterShortcut::Parse(invalid,value) && value.key==before.key && value.modifiers==before.modifiers,"Invalid or reserved shortcut accepted, or output changed on failure");
        }
        Require(RecenterShortcut::Parse("46 1",value) && RecenterShortcut::Parse("115 1",value) && RecenterShortcut::Parse("9 1",value),"Allowed Ctrl combinations were refused");

        const RecenterShortcut f10;
        RecenterPoll poll;
        Require(!poll.Update(f10,false,0,true),"Recenter without a key press");
        Require(poll.Update(f10,true,0,true),"F10 press did not recenter");
        Require(!poll.Update(f10,true,0,true) && !poll.Update(f10,true,0,true),"Holding F10 recentered again");
        Require(!poll.Update(f10,false,0,true) && poll.Update(f10,true,0,true),"Second F10 press did not recenter");
        poll.Update(f10,false,0,true);
        Require(!poll.Update(f10,true,1,true),"Ctrl+F10 triggered a plain F10 binding");
        Require(!poll.Update(f10,true,0,true),"Releasing Ctrl while holding F10 triggered recenter");
        poll.Update(f10,false,0,true);
        Require(!poll.Update(f10,true,0,false),"Key press in another window recentered");
        Require(!poll.Update(f10,true,0,true),"Switching to the game while holding F10 recentered");
        RecenterShortcut chord; RecenterShortcut::Parse("82 1",chord);
        RecenterPoll chordPoll;
        Require(!chordPoll.Update(chord,true,0,true),"R alone triggered Ctrl+R");
        chordPoll.Update(chord,false,0,true);
        Require(chordPoll.Update(chord,true,1,true),"Ctrl+R did not recenter");
        std::puts("Recenter shortcut parsing, validation and key edges passed");
        return 0;
    } catch(const std::exception& error) {
        std::fprintf(stderr,"FAIL: %s\n",error.what());
        return 1;
    }
}
