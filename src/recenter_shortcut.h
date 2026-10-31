#pragma once
#include <charconv>
#include <string>
#include <string_view>
#include <system_error>

namespace dirt3vr {
// The recenter key the launcher passes in DIRT3VR_RECENTER_KEY as "key modifiers": a Windows
// virtual-key code and Ctrl=1, Alt=2, Shift=4, as in DiRT2VR and GRID2VR. Default: F10 alone.
// Keep Valid() identical to launcher/RecenterShortcut.vb.
struct RecenterShortcut {
    unsigned key{121},modifiers{};
    bool Valid() const {
        const bool validKey=(key>=48 && key<=57) || (key>=65 && key<=90) || (key>=96 && key<=135) ||
            key==8 || key==9 || key==13 || key==19 || key==32 || (key>=33 && key<=40) || key==45 || key==46;
        // Alt+Tab, Alt+F4 and Ctrl+Alt+Delete stay with Windows.
        return validKey && modifiers<=7 && !((modifiers&2) && (key==9 || key==115)) && !(key==46 && (modifiers&3)==3);
    }
    static bool Parse(std::string_view text,RecenterShortcut& output) {
        if(text.empty() || text.size()>16) return false;
        RecenterShortcut value;
        const char* begin=text.data(); const char* end=begin+text.size();
        const auto first=std::from_chars(begin,end,value.key);
        if(first.ec!=std::errc{} || first.ptr==end || *first.ptr!=' ') return false;
        const auto second=std::from_chars(first.ptr+1,end,value.modifiers);
        if(second.ec!=std::errc{} || second.ptr!=end || !value.Valid()) return false;
        output=value; return true;
    }
    // For logs only; the launcher shows its own label.
    std::string Describe() const {
        std::string text=std::string((modifiers&1)?"Ctrl+":"")+((modifiers&2)?"Alt+":"")+((modifiers&4)?"Shift+":"");
        if(key>=112 && key<=135) return text+"F"+std::to_string(key-111);
        if((key>=48 && key<=57) || (key>=65 && key<=90)) return text+static_cast<char>(key);
        if(key>=96 && key<=105) return text+"Num"+std::to_string(key-96);
        return text+"VK"+std::to_string(key);
    }
};

// Polled once per rendered frame. Recenter fires when the bound key goes down while DiRT 3 is the
// foreground window and exactly the bound modifiers are held, so Ctrl+F10 doesn't trigger a plain
// F10 binding and holding the key fires once.
class RecenterPoll {
    bool wasDown{};
public:
    bool Update(const RecenterShortcut& binding,bool keyDown,unsigned modifiers,bool foreground) {
        const bool pressed=keyDown && !wasDown;
        wasDown=keyDown;
        return pressed && foreground && modifiers==binding.modifiers;
    }
};
}
