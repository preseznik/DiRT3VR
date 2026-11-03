#pragma once
#include <windows.h>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <functional>
#include <mutex>
#include <string>
#include <string_view>
#include <vector>

namespace dirt3vr::seat {
// The in-game seat menu's state, game independent: the launcher's seat channel
// (docs/seat-channel.md, version 1), DiRT2VR's menu logic (src/seat_adjustment.cpp)
// and the live offset the renderer adds to the eye pose. Rendering, input
// filtering and car identification live elsewhere; callers serialize access.

// Metres, x right, y up, z back, each within ±0.5 (the channel's convention).
struct Position {
    float x{},y{},z{};
    bool operator==(const Position&) const=default;
};
inline bool Valid(Position p) {
    return std::isfinite(p.x) && std::isfinite(p.y) && std::isfinite(p.z) &&
        std::abs(p.x)<=.5f && std::abs(p.y)<=.5f && std::abs(p.z)<=.5f;
}
inline Position Difference(Position a,Position b) { return {a.x-b.x,a.y-b.y,a.z-b.z}; }

// DiRT2VR's movement: 5 mm on a press, then 5 cm/s after 0.35 s held.
// Direction bits: left, right, up, down, back, forward; opposites cancel.
class Movement {
    unsigned previous_{};
    double held_{};
public:
    void Reset() { previous_=0; held_=0; }
    bool Update(Position& p,unsigned directions,double seconds) {
        seconds=std::clamp(seconds,0.0,.1);
        const bool edge=directions!=previous_;
        const double before=held_;
        held_=edge ? 0 : held_+seconds;
        previous_=directions;
        const float step=edge ? .005f : float(std::max(0.0,held_-.35)-std::max(0.0,before-.35))*.05f;
        const auto old=p;
        p.x=std::clamp(p.x+step*float(int((directions>>1)&1)-int(directions&1)),-.5f,.5f);
        p.y=std::clamp(p.y+step*float(int((directions>>2)&1)-int((directions>>3)&1)),-.5f,.5f);
        p.z=std::clamp(p.z+step*float(int((directions>>4)&1)-int((directions>>5)&1)),-.5f,.5f);
        return p!=old;
    }
};

// The car code in one of DiRT 3's race vehicle names, "<code>_<livery> <tag>" (e.g. "frx_00 sn"
// is frx; docs/research/seat-adjustment.md); empty when the name has no code.
inline std::string CarCodeFromVehicleName(std::string_view name) {
    const auto end=name.find('_');
    return end==std::string_view::npos || end==0 || end>15 ? std::string{} : std::string(name.substr(0,end));
}

// Held mask bits, written by the launcher (controllers) and added from the keyboard here.
enum Held : unsigned {
    Left=1u<<0,Right=1u<<1,Up=1u<<2,Down=1u<<3,Back=1u<<4,Forward=1u<<5,Sideways=1u<<6,
    NavUp=1u<<7,NavDown=1u<<8,NavLeft=1u<<9,NavRight=1u<<10,Save=1u<<11,Cancel=1u<<12,Open=1u<<13
};
// Renderer status at offset 84.
enum Status : unsigned { Ready=0,CarUnknown=1,InputUnavailable=2 };
constexpr int AllCars=-2;

class Channel {
public:
    static constexpr unsigned Bytes=16384,Table=256,Stride=96,MaxCars=64,Hint=10500,HintChars=400;
    static constexpr uint32_t Magic=0x54414553,Version=1;
    explicit Channel(unsigned char* view=nullptr) : view_(view) {}
    explicit operator bool() const { return view_!=nullptr; }
    // Every value is checked once; a bad header disables the menu for the session.
    static bool Acceptable(const unsigned char* view) {
        if(!view) return false;
        const Channel channel(const_cast<unsigned char*>(view));
        if(channel.Read<uint32_t>(0)!=Magic || channel.Read<uint32_t>(4)!=Version || channel.Read<uint32_t>(8)!=Bytes ||
           channel.Cars()>MaxCars || channel.Read<uint32_t>(188)>1 || !Valid(channel.Read<Position>(192)) || !Valid(channel.Read<Position>(204))) return false;
        for(unsigned i=0;i<channel.Cars();++i)
            if(!Valid(channel.Read<Position>(Table+i*Stride+64)) || !Valid(channel.Read<Position>(Table+i*Stride+76))) return false;
        return true;
    }
    template<class T> T Read(unsigned offset) const { T value{}; std::memcpy(&value,view_+offset,sizeof(value)); return value; }
    template<class T> void Write(unsigned offset,T value) { std::memcpy(view_+offset,&value,sizeof(value)); }
    unsigned Cars() const { return Read<uint32_t>(184); }
    bool Universal() const { return Read<uint32_t>(188)==1; }
    uint32_t Heartbeat() const { return Read<uint32_t>(12); }
    std::string CarCode(int index) const { return Text(index,0,16); }
    std::string CarName(int index) const { return Text(index,16,48); }
    // AllCars reads the every-car values at 192 and 204.
    Position Saved(int index) const { return index==AllCars ? Read<Position>(192) : Read<Position>(Table+unsigned(index)*Stride+64); }
    Position Applied(int index) const { return index==AllCars ? Read<Position>(204) : Read<Position>(Table+unsigned(index)*Stride+76); }
    int FindCar(const std::string& code) const {
        for(unsigned i=0;i<Cars();++i) if(!code.empty() && CarCode(int(i))==code) return int(i);
        return -1;
    }
    struct Key { unsigned key{},modifiers{}; };
    // [0] open / save & close, [1] left, [2] right, [3] up, [4] down, [5] back, [6] forward.
    Key Binding(unsigned index) const { return {Read<uint32_t>(128+index*8),Read<uint32_t>(132+index*8)}; }
    std::wstring HintText() const {
        const auto* text=reinterpret_cast<const wchar_t*>(view_+Hint);
        return std::wstring(text,wcsnlen_s(text,HintChars));
    }
    struct Input { uint32_t held{},source{},slot{}; GUID guid{}; uint32_t buttons[4]{},pov{}; };
    // The launcher's seqlock at 16: false while it writes (odd) or wrote meanwhile.
    bool ReadInput(Input& output) const {
        const auto sequence=Read<uint32_t>(16);
        if(sequence&1) return false;
        MemoryBarrier();
        Input next{};
        next.held=Read<uint32_t>(20); next.source=Read<uint32_t>(36); next.slot=Read<uint32_t>(40);
        next.guid=Read<GUID>(44); std::memcpy(next.buttons,view_+60,sizeof(next.buttons)); next.pov=Read<uint32_t>(76);
        MemoryBarrier();
        if(Read<uint32_t>(16)!=sequence) return false;
        output=next; return true;
    }
    // Renderer state 80–127 inside the seqlock at 92; a commit also stores the position in its car entry (or 192).
    void Publish(bool open,unsigned status,int active,Position current,const int* commitIndex=nullptr) {
        InterlockedIncrement(reinterpret_cast<volatile LONG*>(view_+92));
        MemoryBarrier();
        Write(80,uint32_t(open)); Write(84,uint32_t(status)); Write(88,active); Write(96,current);
        if(commitIndex) {
            Write(108,current); Write(120,*commitIndex);
            Write(124,Read<uint32_t>(124)+1);
            Write(*commitIndex==AllCars ? 192u : Table+unsigned(*commitIndex)*Stride+64,current);
        }
        MemoryBarrier();
        InterlockedIncrement(reinterpret_cast<volatile LONG*>(view_+92));
    }
private:
    std::string Text(int index,unsigned offset,unsigned length) const {
        if(index<0 || unsigned(index)>=Cars()) return {};
        const auto* value=reinterpret_cast<const char*>(view_+Table+unsigned(index)*Stride+offset);
        return std::string(value,strnlen_s(value,length));
    }
    unsigned char* view_;
};

// DiRT2VR's menu: the open key (or the controller's open button) opens and closes
// it, arrows / D-pad move the seat (Shift / sideways modifier: left and right move
// sideways), Enter saves, Esc cancels. Bound direction keys also move the seat while
// it's closed; such a change is committed 300 ms after the last movement.
// Thread safe: the input filters call BlocksKey and Filter* from the game's input threads.
class Menu {
public:
    using KeyDown=std::function<bool(unsigned)>;
    Menu(Channel channel,bool inputReady,KeyDown keyDown) : channel_(channel),inputReady_(inputReady),keyDown_(std::move(keyDown)) {
        for(unsigned key=0;key<256;++key) armed_[key]=!Key(key);
    }
    Menu(const Menu&)=delete;
    Menu& operator=(const Menu&)=delete;
    // The identified car's table index, or -1 when it isn't known.
    void Select(int index) { Lock lock(mutex_); selected_=index>=0 && unsigned(index)<channel_.Cars() ? index : -1; }
    // Once the input filters are installed: the menu opens only when its inputs can be kept from the game.
    void SetInputReady(bool ready) { Lock lock(mutex_); inputReady_=ready; }
    // One rendered frame. now: milliseconds; tick: GetTickCount() for the launcher's heartbeat.
    void Update(uint64_t now,uint32_t tick,bool foreground) {
        Lock lock(mutex_);
        eligible_=inputReady_;
        const double seconds=lastUpdate_ ? double(now-lastUpdate_)/1000 : 0; lastUpdate_=now;
        if(selected_!=active_) {
            CancelPanel(); if(dirty_) Commit();
            active_=selected_; current_=saved_=Saved();
            previousHeld_=~0u;
        }
        channel_.ReadInput(input_);
        if(uint32_t(tick-channel_.Heartbeat())>1000) { CancelPanel(); input_={}; } // The launcher is gone or stuck.
        if(!foreground) { CancelPanel(); previousHeld_=~0u; Publish(); return; }
        const unsigned held=input_.held|HeldKeys(),pressed=held&~previousHeld_; previousHeld_=held;
        if(pressed&Open) {
            if(panel_) { Commit(); panel_=false; }
            else if(inputReady_) {
                Commit(); opening_=current_; panel_=true; ++openings_; movement_.Reset();
                for(unsigned key:Navigation) armed_[key]=!Key(key); // Keys still held from before wait for a release.
            }
        }
        if(panel_ && (pressed&Cancel)) CancelPanel();
        else if(panel_ && (pressed&Save)) { Commit(); panel_=false; }
        sideways_=(held&Sideways)!=0;
        unsigned directions=held&63;
        if(panel_) {
            if(held&NavUp) directions|=Up;
            if(held&NavDown) directions|=Down;
            if(held&NavLeft) directions|=sideways_ ? Left : Back;
            if(held&NavRight) directions|=sideways_ ? Right : Forward;
        }
        if(inputReady_ && CanAdjustLocked() && movement_.Update(current_,directions,seconds)) { dirty_=!panel_; lastChange_=now; }
        if(dirty_ && !directions && now-lastChange_>=300) Commit();
        Publish();
    }
    // Not a cockpit frame, or the session pauses VR: close without saving, keep a pending move.
    void Inactive() {
        Lock lock(mutex_);
        eligible_=false; CancelPanel(); if(dirty_) Commit();
        previousHeld_=~0u; lastUpdate_=0; Publish();
        for(unsigned key=0;key<256;++key) armed_[key]=!Key(key);
    }
    // Added to the eye pose: the camera files already hold the applied position.
    Position Offset() const {
        Lock lock(mutex_);
        if(!CanAdjustLocked()) return {};
        const auto applied=channel_.Applied(Universal() ? AllCars : active_);
        return Valid(current_) && Valid(applied) ? Difference(current_,applied) : Position{};
    }
    // Whether the game must not see this key (a virtual-key code) now.
    bool BlocksKey(unsigned key) {
        Lock lock(mutex_);
        if(key>255) return false;
        bool block=blocked_[key];
        if(eligible_) for(unsigned k=0;k<7;++k) {
            const auto binding=channel_.Binding(k);
            if(key && key==binding.key && Modifiers()==binding.modifiers) block=blocked_[key]=true;
        }
        if(panel_ && (key==VK_SHIFT || key==VK_LSHIFT || key==VK_RSHIFT || key==VK_RETURN || key==VK_ESCAPE || (key>=VK_LEFT && key<=VK_DOWN))) block=true;
        if(!Key(key)) blocked_[key]=false;
        return block;
    }
    // The menu's buttons on the launcher's held device (channel 36–76) are kept from the game while
    // the menu is open; one pressed then stays kept until it's released, as in DiRT2VR.
    // XInput: clears them from a pad's wButtons.
    void FilterXbox(unsigned slot,WORD& buttons) {
        Lock lock(mutex_);
        if(slot>3) return;
        auto mask=WORD(panel_ && input_.source==1 && input_.slot==slot ? input_.buttons[0] : 0u);
        mask=WORD(mask|xboxHeld_[slot]);
        xboxHeld_[slot]=WORD(mask&buttons);
        buttons=WORD(buttons&~mask);
    }
    // DirectInput button 0–127 of a device: true when the game must see it released.
    bool BlocksButton(const GUID& device,unsigned button,bool pressed) {
        Lock lock(mutex_);
        if(button>=128) return false;
        auto& held=Held(device).buttons[button/32];
        const uint32_t bit=1u<<(button%32);
        const bool reserved=panel_ && input_.source==2 && input_.guid==device && (input_.buttons[button/32]&bit);
        if(!reserved && !(held&bit)) return false;
        held=pressed ? held|bit : held&~bit;
        return true;
    }
    // DirectInput hat (POV) 0–3 of a device: true when the game must see it centred. The menu uses hat 0.
    bool BlocksHat(const GUID& device,unsigned hat,bool centred) {
        Lock lock(mutex_);
        if(hat>=4) return false;
        auto& held=Held(device).hats;
        const uint32_t bit=1u<<hat;
        const bool reserved=panel_ && input_.source==2 && input_.guid==device && input_.pov && hat==0;
        if(!reserved && !(held&bit)) return false;
        held=centred ? held&~bit : held|bit;
        return true;
    }
    bool PanelOpen() const { Lock lock(mutex_); return panel_; }
    bool SidewaysHeld() const { Lock lock(mutex_); return sideways_; }
    bool CanAdjust() const { Lock lock(mutex_); return CanAdjustLocked(); }
    bool InputReady() const { Lock lock(mutex_); return inputReady_; }
    unsigned Openings() const { Lock lock(mutex_); return openings_; }
    int Active() const { Lock lock(mutex_); return active_; }
    Position Current() const { Lock lock(mutex_); return current_; }
    const Channel& View() const { return channel_; }
private:
    // Recursive: a key read inside Update may reach a hooked API that asks BlocksKey.
    using Lock=std::lock_guard<std::recursive_mutex>;
    struct DeviceHeld { GUID device{}; uint32_t buttons[4]{},hats{}; };
    static constexpr unsigned Navigation[]={VK_SHIFT,VK_UP,VK_DOWN,VK_LEFT,VK_RIGHT,VK_RETURN,VK_ESCAPE};
    DeviceHeld& Held(const GUID& device) {
        for(auto& entry:deviceHeld_) if(entry.device==device) return entry;
        deviceHeld_.push_back({device});
        return deviceHeld_.back();
    }
    bool CanAdjustLocked() const { return Universal() || active_>=0; }
    bool Universal() const { return channel_.Universal(); }
    Position Saved() const {
        if(!CanAdjustLocked()) return {};
        const int index=Universal() ? AllCars : active_;
        const auto saved=channel_.Saved(index);
        return Valid(saved) ? saved : channel_.Applied(index);
    }
    bool Key(unsigned key) const { return key && key<256 && keyDown_ && keyDown_(key); }
    unsigned Modifiers() const { return (Key(VK_CONTROL) ? 1u : 0u)|(Key(VK_MENU) ? 2u : 0u)|(Key(VK_SHIFT) ? 4u : 0u); }
    unsigned HeldKeys() {
        unsigned held=0;
        for(unsigned k=0;k<7;++k) {
            const auto binding=channel_.Binding(k);
            if(!binding.key || binding.key>255 || binding.modifiers>7) continue;
            if(!Key(binding.key)) { armed_[binding.key]=true; continue; }
            if(armed_[binding.key] && Modifiers()==binding.modifiers) { held|=k==0 ? Open : 1u<<(k-1); blocked_[binding.key]=true; }
        }
        if(panel_) for(unsigned k=0;k<7;++k) {
            const auto key=Navigation[k];
            if(!Key(key)) armed_[key]=true;
            else if(armed_[key]) { held|=Sideways<<k; blocked_[key]=true; }
        }
        return held;
    }
    void Publish(bool commit=false) {
        const unsigned status=!inputReady_ ? InputUnavailable : !CanAdjustLocked() ? CarUnknown : Ready;
        const int index=Universal() ? AllCars : active_;
        channel_.Publish(panel_,status,active_,current_,commit ? &index : nullptr);
    }
    void Commit() {
        if(CanAdjustLocked() && current_!=saved_) { saved_=current_; Publish(true); }
        dirty_=false;
    }
    void CancelPanel() { if(panel_) current_=opening_; panel_=false; movement_.Reset(); }

    Channel channel_;
    bool inputReady_;
    KeyDown keyDown_;
    Channel::Input input_{};
    Movement movement_;
    Position current_{},saved_{},opening_{};
    int selected_=-1,active_=-2;
    bool panel_{},dirty_{},sideways_{},eligible_{};
    bool armed_[256]{},blocked_[256]{};
    unsigned previousHeld_=~0u,openings_{};
    uint64_t lastUpdate_{},lastChange_{};
    mutable std::recursive_mutex mutex_;
    WORD xboxHeld_[4]{};
    std::vector<DeviceHeld> deviceHeld_;
};
}
