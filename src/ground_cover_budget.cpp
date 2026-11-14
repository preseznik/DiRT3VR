#include "ground_cover_budget.h"
#include <windows.h>
#include <MinHook.h>
#include <algorithm>
#include <atomic>
#include <cstdint>
#include <cstring>

namespace dirt3vr {
namespace {
// Roadside grass and bushes (#d20e9cf7; docs/research/vegetation-culling.md) come from GroundCoverPlugin (vtable
// 0xec8acc). Slot 2 (0x72f6e0) creates its views from a table on its stack: "main view" 0x2800 tufts at offset 0,
// split0/split1 inside main's range, "rearview" 0xc00 tufts at offset 0x2800, in one pool of 0x3400 tufts (128 bytes
// each). The main view picks the tufts nearest the camera, all around it, until its budget is full. DiRT 3's own stats
// (0xb4e880) show it full all the time while driving, so the grass ends where the budget runs out, well before its
// 210 m draw distance and fade, and new grass pops in there. DiRT2VR found the same in DiRT 2.
constexpr uintptr_t PoolImmediateRva = 0x32f87e, MainImmediateRva = 0x32f892, RearOffsetImmediateRva = 0x32f8f1;
constexpr unsigned char PoolInstruction[] = {0xc7,0x85,0x54,0xff,0xff,0xff,0x00,0x34,0x00,0x00};   // mov [ebp-ac],3400
constexpr unsigned char MainInstruction[] = {0xc7,0x85,0x6c,0xff,0xff,0xff,0x00,0x28,0x00,0x00};   // mov [ebp-94],2800
constexpr unsigned char RearOffsetInstruction[] = {0xc7,0x45,0xd0,0x00,0x28,0x00,0x00};            // mov [ebp-30],2800
constexpr uintptr_t PluginVtableRva = 0xac8acc, CreateViewsRva = 0x32f6e0, FinaliseRva = 0x31e850;
constexpr unsigned char CreateViewsEntry[] = {0x55,0x8b,0xec,0x81,0xec,0xc8,0x00,0x00,0x00};       // push ebp; mov ebp,esp; sub esp,c8
constexpr unsigned char FinaliseEntry[] = {0x55,0x8b,0xec,0x83,0xec,0x24,0x56,0x8b,0xf1};          // push ebp; ...; mov esi,ecx
// In VR the main view gets 40,000 tufts: on Finland rally it then holds 28,000-36,000, all the grass within its
// distance, so it never fills up. Fake-headset frame time stayed the same.
constexpr uint32_t GameMainTufts = 0x2800, RearTufts = 0xc00, VrMainTufts = 40000;
// Slot 5 (0x71e850) sizes the shared tuft cache from the track's maxitems (+0x55c, doubled by the loader at 0x71e8b4;
// Finland: 20,000). The larger view uses it up to the 16-bit limit the cache allocator keeps (as in DiRT 2). The
// track's zones (+0x560) stay: more zones split the same cache over more patches and the grass grows thinner.
constexpr int CacheItems = 32767;

// The engine's shared quad index buffer (0x8d7930, called at 0x8d878b and 0x8d9063; ret 4) holds 0xffff 16-bit
// indices (push 0xfcf988 at 0x8d7a0b, push 0xffff at 0x8d7a10), 0x2aaa quads as c,c+1,c+2,c,c+2,c+3. Grass is drawn
// from it, and in VR DiRT 3 skips a grass draw that needs more indices than that (about 10,922 tufts), so a larger
// budget alone left out the main view's grass. As in DiRT2VR, VR makes it a 32-bit buffer with room for the larger
// budget: the two pushes take our count and the 32-bit format (0xfcf8e8, which 0x8b358a picks for 4-byte indices),
// and after DiRT 3's own 16-bit fill the buffer is mapped (owner slot 0x64) and filled again with 32-bit quads, then
// committed (slot 0x74) as DiRT 3 does. Every other quad draw reads the same quads from it.
constexpr uintptr_t QuadIndicesRva = 0x4d7930, IndexFormatPushRva = 0x4d7a0b, IndexCountPushRva = 0x4d7a10;
constexpr uintptr_t Index16FormatRva = 0xbcf988, Index32FormatRva = 0xbcf8e8, Index32PickRva = 0x4b358a;
constexpr unsigned char QuadIndicesEntry[] = {0x55,0x8b,0xec,0x81,0xec,0x88,0x00,0x00,0x00,0x53}; // push ebp; ...; sub esp,88; push ebx
constexpr uint32_t GameIndexCount = 0xffff, IndexCount = 6 * (VrMainTufts + RearTufts);

using PluginFn = bool (__thiscall*)(void*);
using QuadIndicesFn = void (__thiscall*)(void*, void*);
uintptr_t base{};
DiagnosticLogger report{};
PluginFn realCreateViews{}, realFinalise{};
QuadIndicesFn realQuadIndices{};
std::atomic_bool wideIndices{};
std::atomic_uint32_t indexBuilds{}, loads{};
bool budgetRaised{};

bool Bytes(uintptr_t rva, const unsigned char* bytes, size_t size) noexcept {
    __try { return std::memcmp(reinterpret_cast<const void*>(base + rva), bytes, size) == 0; }
    __except(EXCEPTION_EXECUTE_HANDLER) { return false; }
}
bool Pointer(uintptr_t rva, uintptr_t value) noexcept {
    __try { return *reinterpret_cast<const uintptr_t*>(base + rva) == value; }
    __except(EXCEPTION_EXECUTE_HANDLER) { return false; }
}
// An instruction that is one opcode byte and a 32-bit immediate (push imm32, mov eax,imm32).
bool Immediate(uintptr_t rva, unsigned char opcode, uint32_t value) noexcept {
    unsigned char expected[5]{opcode};
    std::memcpy(expected + 1, &value, 4);
    return Bytes(rva, expected, sizeof(expected));
}
bool WriteImmediate(uintptr_t rva, size_t offset, uint32_t value) noexcept {
    auto* target = reinterpret_cast<unsigned char*>(base + rva + offset);
    DWORD old{};
    if(!VirtualProtect(target, 4, PAGE_EXECUTE_READWRITE, &old)) return false;
    std::memcpy(target, &value, 4);
    DWORD ignored{};
    VirtualProtect(target, 4, old, &ignored);
    FlushInstructionCache(GetCurrentProcess(), target, 4);
    return true;
}
bool WriteIndexPushes(bool wide) noexcept {
    return WriteImmediate(IndexCountPushRva, 1, wide ? IndexCount : GameIndexCount) &&
           WriteImmediate(IndexFormatPushRva, 1, uint32_t(base + (wide ? Index32FormatRva : Index16FormatRva)));
}

// DiRT 3 has just built the quad index buffer as 32-bit, with its own 16-bit fill; fill it again with 32-bit quads.
bool FillWideIndices(void* owner) noexcept {
    __try {
        auto* source = *reinterpret_cast<void**>(static_cast<unsigned char*>(owner) + 8);
        if(!source) return false;
        auto** table = *reinterpret_cast<void***>(owner);
        using MapFn = void* (__thiscall*)(void*, void*);
        using CommitFn = void (__thiscall*)(void*, void*);
        auto* data = static_cast<uint32_t*>(reinterpret_cast<MapFn>(table[0x64 / 4])(owner, source));
        if(!data) return false;
        for(uint32_t quad = 0; quad < IndexCount / 6; ++quad) {
            const uint32_t c = quad * 4;
            uint32_t* out = data + quad * 6;
            out[0] = c; out[1] = c + 1; out[2] = c + 2; out[3] = c; out[4] = c + 2; out[5] = c + 3;
        }
        reinterpret_cast<CommitFn>(table[0x74 / 4])(owner, source);
        return true;
    } __except(EXCEPTION_EXECUTE_HANDLER) { return false; }
}
void __fastcall QuadIndices(void* owner, void*, void* argument) noexcept {
    realQuadIndices(owner, argument);
    bool wide = FillWideIndices(owner);
    if(!wide && WriteIndexPushes(false)) {
        // Never leave 16-bit quads in a 32-bit buffer: build DiRT 3's own buffer again, and keep its budget.
        realQuadIndices(owner, argument);
        WriteIndexPushes(true);
    }
    wideIndices.store(wide, std::memory_order_release);
    if(indexBuilds.fetch_add(1) < 4 && report)
        report(wide ? "XR quad index buffer built as 32-bit: %u indices (DiRT 3: 65,535 16-bit)"
                    : "XR quad index buffer 32-bit fill failed (%u indices); DiRT 3's own buffer and grass budget kept", IndexCount);
}

// Main view, rearview offset and pool for one track load. Raising writes pool, offset, main; lowering the reverse,
// so a failed write never leaves two views overlapping.
bool WriteBudget(bool raise) noexcept {
    const uint32_t main = raise ? VrMainTufts : GameMainTufts;
    if(raise) return WriteImmediate(PoolImmediateRva, 6, main + RearTufts) && WriteImmediate(RearOffsetImmediateRva, 3, main) &&
                     WriteImmediate(MainImmediateRva, 6, main);
    return WriteImmediate(MainImmediateRva, 6, main) && WriteImmediate(RearOffsetImmediateRva, 3, main) &&
           WriteImmediate(PoolImmediateRva, 6, main + RearTufts);
}
// Each track load creates the views. The larger budget needs the 32-bit quad indices; without them the views keep
// DiRT 3's own budget.
bool __fastcall CreateViews(void* plugin, void*) noexcept {
    const bool raise = wideIndices.load(std::memory_order_acquire);
    budgetRaised = WriteBudget(raise) && raise;
    if(!budgetRaised && raise) WriteBudget(false);
    if(loads.fetch_add(1) == 0 && report)
        report(budgetRaised ? "XR ground cover budget raised: main grass view %u tufts (DiRT 3: %u)"
                            : "XR ground cover budget kept: main grass view %u tufts; no 32-bit quad indices",
               budgetRaised ? VrMainTufts : GameMainTufts, GameMainTufts);
    return realCreateViews(plugin);
}
// Before the plugin allocates its shared cache from the track's maxitems.
bool __fastcall Finalise(void* plugin, void*) noexcept {
    __try {
        auto* bytes = static_cast<unsigned char*>(plugin);
        if(budgetRaised && *reinterpret_cast<uintptr_t*>(bytes) == base + PluginVtableRva) {
            budgetRaised = false; // once per load
            auto& items = *reinterpret_cast<int*>(bytes + 0x55c);
            if(items > 0 && items < CacheItems) {
                if(loads.load() == 1 && report) report("XR ground cover cache raised with the budget: maxitems %d->%d (the loader doubles it)", items, CacheItems);
                items = CacheItems;
            }
        }
    } __except(EXCEPTION_EXECUTE_HANDLER) {}
    return realFinalise(plugin);
}
}

void InstallGroundCoverBudget(DiagnosticLogger logger) noexcept {
    report = logger;
    wchar_t flag[4]{};
    if(GetEnvironmentVariableW(L"DIRT3VR_GRASS_BUDGET", flag, 4) == 1 && flag[0] == L'0') {
        if(report) report("XR ground cover budget off (DIRT3VR_GRASS_BUDGET=0); DiRT 3's own grass budget kept");
        return;
    }
    base = reinterpret_cast<uintptr_t>(GetModuleHandleW(nullptr));
    if(!Bytes(PoolImmediateRva, PoolInstruction, sizeof(PoolInstruction)) || !Bytes(MainImmediateRva, MainInstruction, sizeof(MainInstruction)) ||
       !Bytes(RearOffsetImmediateRva, RearOffsetInstruction, sizeof(RearOffsetInstruction)) ||
       !Bytes(CreateViewsRva, CreateViewsEntry, sizeof(CreateViewsEntry)) || !Bytes(FinaliseRva, FinaliseEntry, sizeof(FinaliseEntry)) ||
       !Pointer(PluginVtableRva + 2 * 4, base + CreateViewsRva) || !Pointer(PluginVtableRva + 5 * 4, base + FinaliseRva) ||
       !Bytes(QuadIndicesRva, QuadIndicesEntry, sizeof(QuadIndicesEntry)) ||
       !Immediate(IndexFormatPushRva, 0x68, uint32_t(base + Index16FormatRva)) || !Immediate(IndexCountPushRva, 0x68, GameIndexCount) ||
       !Immediate(Index32PickRva, 0xb8, uint32_t(base + Index32FormatRva))) {
        if(report) report("XR ground cover budget refused: loaded binary contract mismatch; DiRT 3's own grass budget kept");
        return;
    }
    const auto init = MH_Initialize();
    if(init != MH_OK && init != MH_ERROR_ALREADY_INITIALIZED) return;
    auto* indices = reinterpret_cast<void*>(base + QuadIndicesRva);
    auto* create = reinterpret_cast<void*>(base + CreateViewsRva);
    auto* finalise = reinterpret_cast<void*>(base + FinaliseRva);
    if(MH_CreateHook(indices, reinterpret_cast<void*>(&QuadIndices), reinterpret_cast<void**>(&realQuadIndices)) != MH_OK) return;
    if(MH_CreateHook(create, reinterpret_cast<void*>(&CreateViews), reinterpret_cast<void**>(&realCreateViews)) != MH_OK ||
       MH_CreateHook(finalise, reinterpret_cast<void*>(&Finalise), reinterpret_cast<void**>(&realFinalise)) != MH_OK) {
        MH_RemoveHook(create); MH_RemoveHook(indices); return;
    }
    if(MH_QueueEnableHook(indices) != MH_OK || MH_QueueEnableHook(create) != MH_OK || MH_QueueEnableHook(finalise) != MH_OK ||
       MH_ApplyQueued() != MH_OK) {
        MH_DisableHook(indices); MH_DisableHook(create); MH_DisableHook(finalise);
        MH_RemoveHook(indices); MH_RemoveHook(create); MH_RemoveHook(finalise);
        if(report) report("XR ground cover budget activation failed; DiRT 3's own grass budget kept");
        return;
    }
    // Only once the hook is live, so DiRT 3 never builds the 32-bit buffer without our 32-bit fill.
    if(!WriteIndexPushes(true)) {
        WriteIndexPushes(false);
        if(report) report("XR ground cover budget refused: quad index patch failed; DiRT 3's own grass budget kept");
        return;
    }
    if(report) report("XR ground cover budget attached: main grass view %u tufts with 32-bit quad indices", VrMainTufts);
}
}
