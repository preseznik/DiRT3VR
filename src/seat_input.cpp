#define DIRECTINPUT_VERSION 0x0800
#include "seat_input.h"
#include <dinput.h>
#include <xinput.h>
#include <MinHook.h>
#include <intrin.h>
#include <algorithm>
#include <atomic>
#include <map>
#include <memory>
#include <mutex>
#include <vector>

namespace dirt3vr::seatinput {
namespace {
std::atomic<std::shared_ptr<const Policy>> policy;
void (*logLine)(const char*,...){};
bool ready{};

// Only the game's own reads are filtered; DiRT3VR's and the runtime's pass unchanged.
bool GameCaller(void* caller) {
    static const auto base=reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    static const auto size=reinterpret_cast<IMAGE_NT_HEADERS*>(base+reinterpret_cast<IMAGE_DOS_HEADER*>(base)->e_lfanew)->OptionalHeader.SizeOfImage;
    const auto address=reinterpret_cast<uintptr_t>(caller);
    return address>=base && address<base+size;
}
bool Hook(void* target,void* replacement,void** original) {
    if(!target) return false;
    const auto created=MH_CreateHook(target,replacement,original);
    if(created!=MH_OK) return false;
    if(MH_EnableHook(target)!=MH_OK) { MH_RemoveHook(target); return false; }
    return true;
}

// Keyboard: Raw Input.
using RawDataFn=UINT (WINAPI*)(HRAWINPUT,UINT,LPVOID,PUINT,UINT);
RawDataFn rawData{};
std::mutex keyboardMutex;
KeyboardFilter keyboard;
UINT WINAPI RawData(HRAWINPUT input,UINT command,LPVOID data,PUINT size,UINT headerSize) {
    const auto result=rawData(input,command,data,size,headerSize);
    if(result==UINT(-1) || command!=RID_INPUT || !data || headerSize!=sizeof(RAWINPUTHEADER) || result<sizeof(RAWINPUTHEADER)+sizeof(RAWKEYBOARD) ||
       !GameCaller(_ReturnAddress()))
        return result;
    auto* raw=static_cast<RAWINPUT*>(data);
    if(raw->header.dwType!=RIM_TYPEKEYBOARD) return result;
    const auto current=policy.load();
    if(!current || !current->blocksKey) return result;
    std::lock_guard lock(keyboardMutex);
    keyboard.Apply(raw->data.keyboard,current->blocksKey);
    return result;
}

// DirectInput devices: every object of a read is offered to the policy.
struct Device { GUID id{}; bool keyboard{},unicode{}; std::vector<DIOBJECTDATAFORMAT> objects; };
std::mutex deviceMutex;
std::map<void*,Device> devices;
using CreateFn=HRESULT (STDMETHODCALLTYPE*)(IDirectInput8W*,REFGUID,IDirectInputDevice8W**,IUnknown*);
using StateFn=HRESULT (STDMETHODCALLTYPE*)(IDirectInputDevice8W*,DWORD,void*);
using DataFn=HRESULT (STDMETHODCALLTYPE*)(IDirectInputDevice8W*,DWORD,DIDEVICEOBJECTDATA*,DWORD*,DWORD);
using FormatFn=HRESULT (STDMETHODCALLTYPE*)(IDirectInputDevice8W*,const DIDATAFORMAT*);
CreateFn create{},createA{};
StateFn state{};
DataFn data{};
FormatFn format{};
bool deviceHooks{};

// A device the game opened before the hooks is first seen when read: learn its identity
// and objects then. Its interface may be ANSI or Unicode, and each refuses the other's
// structure size, so both are tried. Caller holds deviceMutex.
template<class Info,class Interface> bool DeviceInfo(IDirectInputDevice8W* self,GUID& id,bool& isKeyboard) {
    Info info{sizeof(info)};
    if(FAILED(reinterpret_cast<Interface*>(self)->GetDeviceInfo(&info))) return false;
    id=info.guidInstance;
    isKeyboard=GET_DIDEVICE_TYPE(info.dwDevType)==DI8DEVTYPE_KEYBOARD;
    return true;
}
template<class Info,class Interface> bool ObjectType(IDirectInputDevice8W* self,DWORD offset,DWORD& type) {
    Info info{sizeof(info)};
    if(FAILED(reinterpret_cast<Interface*>(self)->GetObjectInfo(&info,offset,DIPH_BYOFFSET)) || info.dwOfs!=offset) return false;
    type=info.dwType;
    return true;
}
Device* Known(IDirectInputDevice8W* self,DWORD length) {
    if(auto found=devices.find(self);found!=devices.end()) return &found->second;
    Device device;
    if(!DeviceInfo<DIDEVICEINSTANCEW,IDirectInputDevice8W>(self,device.id,device.keyboard) &&
       !DeviceInfo<DIDEVICEINSTANCEA,IDirectInputDevice8A>(self,device.id,device.keyboard))
        return nullptr;
    for(DWORD offset=0;offset<std::min<DWORD>(length,1024);) {
        DWORD type{};
        const bool found=ObjectType<DIDEVICEOBJECTINSTANCEW,IDirectInputDevice8W>(self,offset,type) ||
                         ObjectType<DIDEVICEOBJECTINSTANCEA,IDirectInputDevice8A>(self,offset,type);
        if(found) device.objects.push_back({nullptr,offset,type,0});
        offset+=found && !(type&DIDFT_BUTTON) ? 4u : 1u;
    }
    return &(devices[self]=std::move(device));
}

void FilterDeviceObject(const Policy& current,const Device& device,const DIOBJECTDATAFORMAT& object,void* value,DWORD size) {
    // DiRT 3 reads its keyboard through Raw Input; a DirectInput keyboard is never the game's.
    if(!device.keyboard) FilterObject(current,device.id,object.dwType,value,size);
}
HRESULT STDMETHODCALLTYPE State(IDirectInputDevice8W* self,DWORD length,void* output) {
    const auto hr=state(self,length,output);
    if(FAILED(hr) || !output || !GameCaller(_ReturnAddress())) return hr;
    const auto current=policy.load();
    if(!current) return hr;
    std::lock_guard lock(deviceMutex);
    const auto* device=Known(self,length);
    if(!device) return hr;
    for(const auto& object:device->objects) {
        const DWORD size=(object.dwType&DIDFT_BUTTON) ? 1 : 4;
        if(object.dwOfs<=length && size<=length-object.dwOfs)
            FilterDeviceObject(*current,*device,object,static_cast<BYTE*>(output)+object.dwOfs,size);
    }
    return hr;
}
HRESULT STDMETHODCALLTYPE Data(IDirectInputDevice8W* self,DWORD objectSize,DIDEVICEOBJECTDATA* output,DWORD* count,DWORD flags) {
    const auto hr=data(self,objectSize,output,count,flags);
    if(FAILED(hr) || !output || !count || objectSize<16 || !GameCaller(_ReturnAddress())) return hr;
    const auto current=policy.load();
    if(!current) return hr;
    std::lock_guard lock(deviceMutex);
    const auto* device=Known(self,sizeof(DIJOYSTATE2));
    if(!device) return hr;
    for(DWORD i=0;i<*count;++i) {
        auto* row=reinterpret_cast<DIDEVICEOBJECTDATA*>(reinterpret_cast<BYTE*>(output)+i*objectSize);
        for(const auto& object:device->objects)
            if(object.dwOfs==row->dwOfs) { FilterDeviceObject(*current,*device,object,&row->dwData,(object.dwType&DIDFT_BUTTON) ? 1 : 4); break; }
    }
    return hr;
}
// The object list comes from the game's data format, with each object's real type.
HRESULT STDMETHODCALLTYPE Format(IDirectInputDevice8W* self,const DIDATAFORMAT* value) {
    const auto hr=format(self,value);
    if(FAILED(hr) || !value || value->dwObjSize!=sizeof(DIOBJECTDATAFORMAT) || value->dwNumObjs>512) return hr;
    std::vector<DIOBJECTDATAFORMAT> objects(value->rgodf,value->rgodf+value->dwNumObjs);
    bool unicode{};
    { std::lock_guard lock(deviceMutex); unicode=devices[self].unicode; }
    for(auto& object:objects) {
        if(unicode) {
            DIDEVICEOBJECTINSTANCEW info{sizeof(info)};
            if(SUCCEEDED(self->GetObjectInfo(&info,object.dwOfs,DIPH_BYOFFSET))) object.dwType=info.dwType;
        } else {
            DIDEVICEOBJECTINSTANCEA info{sizeof(info)};
            if(SUCCEEDED(reinterpret_cast<IDirectInputDevice8A*>(self)->GetObjectInfo(&info,object.dwOfs,DIPH_BYOFFSET))) object.dwType=info.dwType;
        }
    }
    std::lock_guard lock(deviceMutex);
    devices[self].objects=std::move(objects);
    return hr;
}
template<bool Unicode> HRESULT STDMETHODCALLTYPE CreateDevice(IDirectInput8W* self,REFGUID id,IDirectInputDevice8W** out,IUnknown* outer) {
    const auto hr=(Unicode ? create : createA)(self,id,out,outer);
    if(FAILED(hr) || !out || !*out) return hr;
    GUID instance=id;
    bool isKeyboard=id==GUID_SysKeyboard;
    if constexpr(Unicode) {
        DIDEVICEINSTANCEW info{sizeof(info)};
        if(SUCCEEDED((*out)->GetDeviceInfo(&info))) { instance=info.guidInstance; isKeyboard=GET_DIDEVICE_TYPE(info.dwDevType)==DI8DEVTYPE_KEYBOARD; }
    } else {
        DIDEVICEINSTANCEA info{sizeof(info)};
        if(SUCCEEDED(reinterpret_cast<IDirectInputDevice8A*>(*out)->GetDeviceInfo(&info))) { instance=info.guidInstance; isKeyboard=GET_DIDEVICE_TYPE(info.dwDevType)==DI8DEVTYPE_KEYBOARD; }
    }
    std::lock_guard lock(deviceMutex);
    devices[*out]={instance,isKeyboard,Unicode,{}};
    // ANSI and Unicode devices share one implementation, so one set of hooks covers both.
    if(!deviceHooks) {
        auto** table=*reinterpret_cast<void***>(*out);
        deviceHooks=Hook(table[9],reinterpret_cast<void*>(State),reinterpret_cast<void**>(&state)) &&
                    Hook(table[10],reinterpret_cast<void*>(Data),reinterpret_cast<void**>(&data)) &&
                    Hook(table[11],reinterpret_cast<void*>(Format),reinterpret_cast<void**>(&format));
    }
    return hr;
}

// Xbox pads: XInput.
using XboxFn=DWORD (WINAPI*)(DWORD,XINPUT_STATE*);
XboxFn xbox{};
DWORD WINAPI Xbox(DWORD slot,XINPUT_STATE* output) {
    const auto result=xbox(slot,output);
    if(result!=ERROR_SUCCESS || !output || !GameCaller(_ReturnAddress())) return result;
    const auto current=policy.load();
    if(current && current->filterXbox) current->filterXbox(slot,output->Gamepad.wButtons);
    return result;
}

bool InstallHooks() {
    const auto initialized=MH_Initialize();
    if(initialized!=MH_OK && initialized!=MH_ERROR_ALREADY_INITIALIZED) return false;
    auto* user=GetModuleHandleW(L"user32.dll");
    const bool keys=user && Hook(reinterpret_cast<void*>(GetProcAddress(user,"GetRawInputData")),reinterpret_cast<void*>(RawData),reinterpret_cast<void**>(&rawData));

    // A throwaway DirectInput object exposes the factory and device tables the game's share.
    bool sticks=false;
    if(auto* module=LoadLibraryExW(L"dinput8.dll",nullptr,LOAD_LIBRARY_SEARCH_SYSTEM32)) {
        using Factory=HRESULT (WINAPI*)(HINSTANCE,DWORD,REFIID,void**,IUnknown*);
        const auto factory=reinterpret_cast<Factory>(GetProcAddress(module,"DirectInput8Create"));
        IDirectInput8W* wide{};
        if(factory && SUCCEEDED(factory(GetModuleHandleW(nullptr),DIRECTINPUT_VERSION,IID_IDirectInput8W,reinterpret_cast<void**>(&wide),nullptr))) {
            sticks=Hook((*reinterpret_cast<void***>(wide))[3],reinterpret_cast<void*>(CreateDevice<true>),reinterpret_cast<void**>(&create));
            IDirectInputDevice8W* probe{};
            if(sticks && SUCCEEDED(wide->CreateDevice(GUID_SysKeyboard,&probe,nullptr))) {
                { std::lock_guard lock(deviceMutex); devices.erase(probe); }
                probe->Release();
            }
            wide->Release();
            IDirectInput8A* ansi{};
            if(SUCCEEDED(factory(GetModuleHandleW(nullptr),DIRECTINPUT_VERSION,IID_IDirectInput8A,reinterpret_cast<void**>(&ansi),nullptr))) {
                sticks=Hook((*reinterpret_cast<void***>(ansi))[3],reinterpret_cast<void*>(CreateDevice<false>),reinterpret_cast<void**>(&createA)) && sticks;
                ansi->Release();
            } else sticks=false;
            sticks=sticks && deviceHooks;
        }
    }

    // DiRT 3 links xinput1_3.dll (by ordinal), the same export as XInputGetState.
    bool pads=false;
    if(auto* library=LoadLibraryExW(L"xinput1_3.dll",nullptr,LOAD_LIBRARY_SEARCH_SYSTEM32))
        pads=Hook(reinterpret_cast<void*>(GetProcAddress(library,"XInputGetState")),reinterpret_cast<void*>(Xbox),reinterpret_cast<void**>(&xbox));
    if(logLine) logLine("Seat input filters: keyboard=%d directinput=%d xinput=%d",int(keys),int(sticks),int(pads));
    return keys && sticks && pads;
}
}

bool Install(Policy next,void (*log)(const char*,...)) {
    static std::once_flag once;
    logLine=log;
    policy.store(std::make_shared<const Policy>(std::move(next)));
    std::call_once(once,[] { ready=InstallHooks(); });
    return ready;
}
bool Ready() { return ready; }

}
