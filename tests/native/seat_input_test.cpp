#include "seat_input.h"
#include <cstdio>
#include <stdexcept>

namespace {
void Require(bool value,const char* message) { if(!value) throw std::runtime_error(message); }
namespace si=dirt3vr::seatinput;

RAWKEYBOARD Key(USHORT vkey,UINT message) {
    RAWKEYBOARD key{};
    key.VKey=vkey; key.MakeCode=0x48; key.Message=message;
    key.Flags=(message==WM_KEYUP || message==WM_SYSKEYUP) ? RI_KEY_BREAK : RI_KEY_MAKE;
    return key;
}

void KeyboardFilter() {
    si::KeyboardFilter filter;
    bool menu=false;
    const auto blocks=[&](unsigned vkey) { return menu && vkey==VK_UP; };

    // Menu closed: presses and releases pass, and the game's held keys are tracked.
    auto down=Key(VK_UP,WM_KEYDOWN);
    Require(!filter.Apply(down,blocks) && down.VKey==VK_UP && filter.Held(VK_UP),"an unblocked press passes");
    auto up=Key(VK_UP,WM_KEYUP);
    Require(!filter.Apply(up,blocks) && up.VKey==VK_UP && !filter.Held(VK_UP),"an unblocked release passes");

    // Menu open: a blocked press is hidden (VKey 0xff, which the game ignores), and so is its release.
    menu=true;
    down=Key(VK_UP,WM_KEYDOWN);
    Require(filter.Apply(down,blocks) && down.VKey==0xff && down.MakeCode==0 && !filter.Held(VK_UP),"a blocked press is hidden");
    up=Key(VK_UP,WM_KEYUP);
    Require(filter.Apply(up,blocks) && up.VKey==0xff,"its release is hidden too");
    auto other=Key(VK_LEFT,WM_KEYDOWN);
    Require(!filter.Apply(other,blocks) && other.VKey==VK_LEFT,"keys the menu doesn't use pass");

    // A key the game holds when the menu takes it is released once, then hidden while held.
    menu=false;
    down=Key(VK_UP,WM_KEYDOWN);
    filter.Apply(down,blocks);
    menu=true;
    auto repeat=Key(VK_UP,WM_KEYDOWN);
    Require(filter.Apply(repeat,blocks) && repeat.VKey==VK_UP && repeat.Message==WM_KEYUP && (repeat.Flags&RI_KEY_BREAK) && !filter.Held(VK_UP),
            "a held key is released instead of hidden");
    repeat=Key(VK_UP,WM_KEYDOWN);
    Require(filter.Apply(repeat,blocks) && repeat.VKey==0xff,"then its repeats are hidden");
    auto system=Key(VK_UP,WM_SYSKEYDOWN);
    menu=false; filter.Apply(system,blocks); menu=true;
    system=Key(VK_UP,WM_SYSKEYDOWN);
    Require(filter.Apply(system,blocks) && system.Message==WM_SYSKEYUP,"a held Alt+key is released as a system key");

    // The game's own release of a key it holds always passes, even while the menu blocks it.
    menu=false; down=Key(VK_UP,WM_KEYDOWN); filter.Apply(down,blocks);
    menu=true; up=Key(VK_UP,WM_KEYUP);
    Require(!filter.Apply(up,blocks) && up.VKey==VK_UP && !filter.Held(VK_UP),"a release of a held key passes");

    // Events DiRT 3 ignores are left alone.
    auto overrun=Key(0xff,WM_KEYDOWN);
    Require(!filter.Apply(overrun,blocks),"VKey 0xff is left alone");
    auto character=Key(VK_UP,WM_CHAR);
    Require(!filter.Apply(character,blocks) && character.VKey==VK_UP,"other messages are left alone");
    Require(!filter.Apply(other,nullptr),"no policy, no change");
}

void DirectInputObjects() {
    const GUID wheel{0x1,0x2,0x3,{4,5,6,7,8,9,10,11}};
    si::Policy policy;
    unsigned lastButton=99,lastHat=99; bool lastPressed{},lastCentred{};
    policy.blocksButton=[&](const GUID& device,unsigned button,bool pressed) {
        lastButton=button; lastPressed=pressed; return device==wheel && button==3;
    };
    policy.blocksHat=[&](const GUID& device,unsigned hat,bool centred) {
        lastHat=hat; lastCentred=centred; return device==wheel && hat==0 && !centred;
    };
    BYTE button=0x80;
    Require(si::FilterObject(policy,wheel,DIDFT_PSHBUTTON|DIDFT_MAKEINSTANCE(3),&button,1) && button==0 && lastButton==3 && lastPressed,
            "a blocked button reads released");
    button=0x80;
    Require(!si::FilterObject(policy,wheel,DIDFT_PSHBUTTON|DIDFT_MAKEINSTANCE(4),&button,1) && button==0x80 && lastButton==4,
            "other buttons keep their state");
    DWORD hat=9000;
    Require(si::FilterObject(policy,wheel,DIDFT_POV|DIDFT_MAKEINSTANCE(0),&hat,4) && hat==0xffffffff && lastHat==0 && !lastCentred,
            "a blocked hat reads centred");
    hat=0xffff;
    Require(!si::FilterObject(policy,wheel,DIDFT_POV|DIDFT_MAKEINSTANCE(0),&hat,4) && lastCentred,"a centred hat is reported centred");
    LONG axis=12345;
    Require(!si::FilterObject(policy,wheel,DIDFT_ABSAXIS|DIDFT_MAKEINSTANCE(0),&axis,4) && axis==12345,"axes are never changed");
    si::Policy empty;
    button=0x80;
    Require(!si::FilterObject(empty,wheel,DIDFT_PSHBUTTON|DIDFT_MAKEINSTANCE(3),&button,1) && button==0x80,"no policy, no change");
}
}

int main() {
    try {
        KeyboardFilter();
        DirectInputObjects();
        std::puts("seat input filters: ok");
        return 0;
    } catch(const std::exception& error) {
        std::fprintf(stderr,"FAIL: %s\n",error.what());
        return 1;
    }
}
