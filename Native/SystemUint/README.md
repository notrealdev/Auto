# SystemUint for Game.exe 3.0.0.7

This native x86 DLL preserves the public ABI observed from AutoFS: `GetMsg`, `InjectDll(HWND)`, and `UnmapDll(HWND)`.

The original AutoFS binary remains unchanged at `Resource\Auto\Lib\SystemUint.dll` with SHA-256 `AE89957E39260F7A0A9BA355EF04B89083E1CB10E049018F0C9F33B0846ECBFA`.

Confirmed by disassembling that original x86 binary:

* Command `9` calls the game manager vtable method at offset `0x40` with the ground slot ID.
* Command `32` dispatches the original client movement reset with opcode `0x9B` and resets pickup state.
* Command `27` in the original AutoFS DLL rejects blocked client states, resets movement with opcode `0x9B`, invokes movement mode `3` through `0x005A48E0`, and invokes the coordinate action through `0x00652BB0`. The current client uses the confirmed equivalent movement function at RVA `0x0031CE80` and coordinate action at RVA `0x003AA0F0`.
* Command `78` calls the same slot selector, computes `0x00C14AD8 + slot * 0x398`, reads the ground-object pointer at record offset `0x35C`, reads X/Y at pointer offsets `0x10` and `0x14`, invokes movement mode `3` through `0x005A48E0`, and invokes pickup through `0x00652BB0`.
* Command `85` in the original AutoFS DLL invokes action mode `5` with `(skillId, 0, 0)` through `0x005A48E0`, then invokes the cast function with `(skillId, 0, 0)` through `0x00652DD0`.
* Commands `0`, `1`, `2`, and `10` in the AutoFS item-selection flow carry the item ID, container, row, and column. DEV auto command `310` validates the live slot in the AutoFS search order `11 -> 3 -> 16`, builds the seven-field descriptor read by the current client, and invokes the confirmed current-client inventory-use ABI.
* AutoFS Tự Rao appends every encoded script byte with command `34` and executes the completed buffer with command `22`. DEV auto command `311` resets its per-window script buffer before preserving that `34 -> 22` sequence through the confirmed current-client script executor.
* On the current Game.exe client, the confirmed high-level skill function at RVA `0x002C96E0` resolves its target, invokes action mode `5` through RVA `0x0031CE80`, and invokes the cast wrapper through RVA `0x003AA360`. Command `85` preserves the original AutoFS self-buff arguments by calling those current-client equivalents with `(skillId, 0, 0)`.
* The original AutoFS ground pointer offset is `0x35C`. The `0x348` offset belongs to the separately analyzed DEV client 3.0.0.7 layout and must not be attributed to AutoFS.

DEV auto must still use the separately confirmed Game.exe 3.0.0.7 movement opcode `0x9F`; the original AutoFS opcode `0x9B` is evidence for the reference client only.

This implementation accepts attack command `300` with packed target `(0x87 << 16) | index`. It validates the in-memory `Game.exe` as a supported 32-bit PE image, then validates the relevant function signature, manager vtable, object, and ABI path for each command. It does not reject an otherwise compatible client only because the PE timestamp changed.

Build with:

`zig c++ -target x86-windows-gnu -shared -O2 -Wno-nullability-completeness SystemUint.cpp SystemUint.def -o bin\SystemUint.dll -luser32 -lkernel32`
