#include "seat_session.h"
#include "seat_input.h"
#include <mutex>
#include <optional>

namespace dirt3vr {
namespace {
// Deliberately leaked: the mapping, the menu and the input hooks' policy live for the process,
// and must not be torn down from DllMain or a global destructor.
std::optional<seat::Menu>* menu{};
}
seat::Menu* OpenSeatMenu(void (*log)(const char*,...)) noexcept {
    static std::once_flag once;
    try {
        std::call_once(once,[log] {
            wchar_t name[96]{};
            const DWORD length=GetEnvironmentVariableW(L"DIRT3VR_SEAT_CHANNEL",name,96);
            if(!length) return;
            if(length>=96 || wcsncmp(name,L"Local\\DiRT3VR.Seat.",19)) { if(log) log("Seat menu off: channel name rejected"); return; }
            const HANDLE mapping=OpenFileMappingW(FILE_MAP_ALL_ACCESS,FALSE,name);
            auto* view=mapping ? static_cast<unsigned char*>(MapViewOfFile(mapping,FILE_MAP_ALL_ACCESS,0,0,seat::Channel::Bytes)) : nullptr;
            if(mapping) CloseHandle(mapping);
            if(!view || !seat::Channel::Acceptable(view)) {
                if(view) UnmapViewOfFile(view);
                if(log) log("Seat menu off: channel unavailable or invalid");
                return;
            }
            // The launcher reads commits until it exits, so the mapping stays for the process.
            menu=new std::optional<seat::Menu>;
            auto& open=menu->emplace(seat::Channel(view),false,[](unsigned key) { return (GetAsyncKeyState(int(key))&0x8000)!=0; });
            // The menu's keys and buttons must not reach the game. Without every filter it stays
            // closed (status 2) and only the saved seat positions apply.
            seatinput::Policy policy;
            policy.blocksKey=[&open](unsigned key) { return open.BlocksKey(key); };
            policy.filterXbox=[&open](unsigned slot,WORD& buttons) { open.FilterXbox(slot,buttons); };
            policy.blocksButton=[&open](const GUID& device,unsigned button,bool pressed) { return open.BlocksButton(device,button,pressed); };
            policy.blocksHat=[&open](const GUID& device,unsigned hat,bool centred) { return open.BlocksHat(device,hat,centred); };
            // With the keyboard filter the menu opens; a controller type without its filter is ignored.
            if(log) seatinput::Install(std::move(policy),log);
            const auto filters=log ? seatinput::Installed() : seatinput::Filters{};
            open.SetInputReady(filters.keyboard,filters.xinput,filters.directInput);
            if(log) log("Seat menu channel open cars=%u every_car=%u monitor=%u input_filtering=%u (pad=%u wheel=%u)",
                open.View().Cars(),unsigned(open.View().Universal()),unsigned(open.Desktop()),unsigned(filters.keyboard),
                unsigned(filters.xinput),unsigned(filters.directInput));
        });
    } catch(...) { return nullptr; }
    return menu && menu->has_value() ? &**menu : nullptr;
}
// Entry 0 of the race's vehicle table, 16-byte "<code>_<livery> <tag>" names at 0xf9fc08 counted by
// [[0xf9fbfc]+0xb234] (docs/research/seat-adjustment.md). Checked for direct starts; races from the
// menus aren't yet. Read through ReadProcessMemory, so a missing table only fails the read.
std::string PlayerCarCode() noexcept {
    try {
        const auto* image=reinterpret_cast<const unsigned char*>(GetModuleHandleW(nullptr));
        const auto process=GetCurrentProcess();
        uintptr_t owner{}; int count{}; char name[17]{}; SIZE_T read{};
        if(!ReadProcessMemory(process,image+(0xf9fbfc-0x400000),&owner,sizeof(owner),&read) || !owner ||
           !ReadProcessMemory(process,reinterpret_cast<const void*>(owner+0xb234),&count,sizeof(count),&read) || count<1 || count>64 ||
           !ReadProcessMemory(process,image+(0xf9fc08-0x400000),name,16,&read)) return {};
        return seat::CarCodeFromVehicleName(std::string_view(name,strnlen_s(name,16)));
    } catch(...) { return {}; }
}
}
