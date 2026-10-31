"""Generate x86 D3D11 forwarders, preserving Windows export names and ordinals.

Adapted from DiRT2VR tools/generate_exports.py. Only the documented constructors
are intercepted; all other exports preserve registers/flags and jump directly
into the absolute-path system DLL. No exports are guessed or silently omitted.
"""
import pathlib
import struct
import sys


def exports(data):
    def u16(p): return struct.unpack_from('<H', data, p)[0]
    def u32(p): return struct.unpack_from('<I', data, p)[0]
    pe = u32(60)
    if data[:2] != b'MZ' or data[pe:pe+4] != b'PE\0\0' or u16(pe+4) != 0x14c:
        raise ValueError('Expected an x86 PE DLL')
    opt = pe + 24
    if u16(opt) != 0x10b:
        raise ValueError('Expected a PE32 optional header')
    sections = opt + u16(pe+20)

    def offset(rva):
        for i in range(u16(pe+6)):
            p = sections + i * 40
            size, va, raw_size, raw = struct.unpack_from('<IIII', data, p+8)
            if va <= rva < va + max(size, raw_size):
                return raw + rva - va
        raise ValueError(f'Unmapped RVA {rva:x}')

    directory = offset(u32(opt+96))
    base, count, names_count, functions, names, ordinals = struct.unpack_from('<IIIIII', data, directory+16)
    if count != names_count:
        raise ValueError('Unnamed exports need explicit handling')
    result = []
    for i in range(names_count):
        p = offset(u32(offset(names)+4*i))
        name = data[p:data.index(0, p)].decode('ascii')
        result.append((name, base + u16(offset(ordinals)+2*i)))
    if len(set(ordinal for _, ordinal in result)) != len(result):
        raise ValueError('Aliased export ordinals need explicit handling')
    if not {'D3D11CreateDevice', 'D3D11CreateDeviceAndSwapChain'}.issubset(name for name, _ in result):
        raise ValueError('Required D3D11 constructors missing')
    return result


def generate(items):
    special = {'D3D11CreateDevice': 'ProxyCreateDevice',
               'D3D11CreateDeviceAndSwapChain': 'ProxyCreateDeviceAndSwapChain'}
    defs = ['LIBRARY d3d11', 'EXPORTS']
    cpp = ['// Generated. System DLL resolved by absolute path outside DllMain.',
           'static FARPROC forwardTargets[%d]{};' % len(items),
           'static const char* forwardNames[] = {']
    cpp += [f'"{name}",' for name, _ in items]
    cpp += ['};', 'static void __cdecl ResolveForward(unsigned i) {',
            '  InterlockedExchangePointer(reinterpret_cast<PVOID volatile*>(&forwardTargets[i]),',
            '      reinterpret_cast<PVOID>(RealProc(forwardNames[i])));', '}', '']
    for i, (name, ordinal) in enumerate(items):
        wrapper = special.get(name, f'Forward{i}')
        defs.append(f'  {name}={wrapper} @{ordinal}')
        if name in special:
            continue
        cpp += [f'extern "C" __declspec(naked) void {wrapper}() {{',
                '  __asm {', '    pushfd', '    pushad', f'    push {i}',
                '    call ResolveForward', '    add esp, 4', '    popad', '    popfd',
                f'    jmp dword ptr [forwardTargets + {i * 4}]', '  }', '}']
    defs.append(f'  DiRT3VRGetStatus @{max(ordinal for _, ordinal in items) + 1}')
    return '\n'.join(defs)+'\n', '\n'.join(cpp)+'\n'


if __name__ == '__main__':
    items = exports(pathlib.Path(sys.argv[1]).read_bytes())
    output = pathlib.Path(sys.argv[2])
    output.mkdir(parents=True, exist_ok=True)
    defs, cpp = generate(items)
    manifest = ''.join(f'{{"{name}", {ordinal}}},\n' for name, ordinal in items)
    for name, contents in [('exports.def', defs), ('forwarders.inc', cpp), ('export_manifest.inc', manifest)]:
        path = output / name
        if not path.exists() or path.read_text() != contents:
            path.write_text(contents)
    print(f'Generated {len(items)} Windows exports and diagnostic status export')
