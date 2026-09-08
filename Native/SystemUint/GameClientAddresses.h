#pragma once

#include <windows.h>
#include <stdint.h>

namespace GameClientAddresses {
	// Cập nhật sau bản game 2026-08-28 (PE TimeDateStamp 0x6A8DD698, SizeOfImage đo được 0x01F83000).
	constexpr uint32_t MinimumSupportedImageSize = 0x01F83000;
	// Cập nhật sau bản game 2026-08-28: xác nhận bằng byte-scan tĩnh 2 tầng (manager object nhúng tĩnh trong image, vtable pointer khớp cả 2 slot ATTACK/SELECT_GROUND đã đối chiếu decompile), chưa build/test runtime.
	constexpr uintptr_t AttackManagerRva = 0x004E2660;
	constexpr uintptr_t ExpectedManagerVtableRva = 0x00477804;
	constexpr int CoordinateOpcode = 0x9F;
	constexpr size_t CoordinateDispatcherMethodVtableOffset = 0x10;
	constexpr size_t DialogOptionMethodVtableOffset = 0x10;
	constexpr int DialogOptionOpcode = 9;
	// Cập nhật sau bản game 2026-08-28: xác nhận bằng byte thật (ngữ cảnh khớp bản cũ), delta +0x2020. Chưa build/test runtime.
	constexpr uintptr_t ModalStateRva = 0x004EEF88;
	constexpr size_t ModalEventMethodVtableOffset = 0x10;
	// Sửa 0x00469E34 -> 0x0046AF3C ngày 2026-09-08. Bản game 2026-08-28 dời hằng số này nhưng lượt rebase trước bỏ sót,
	// nên address-audit.log ghi FAIL_VTABLE_MISMATCH (Code=14) — lỗi DUY NHẤT trên 34 địa chỉ. Hệ quả: popup xác nhận
	// của NPC không được nhận diện, luồng Sửa đồ rơi xuống nhánh đọc menu và fail "Menu NPC không có duy nhất...".
	// Đo trực tiếp bằng ModalVtableProbe khi popup đang mở (PID 22056): MODAL_VTABLE_TABLE | VtableRva=0x46AF3C.
	// Kiểm chứng chéo: delta +0x1108 trùng khít delta của RepairConfirmModalVtableRva (0x4721B4 -> 0x4732BC) — hai
	// vtable cùng vùng .rdata dời cùng lượng, và hằng số kia đã PASS_VTABLE độc lập.
	constexpr uintptr_t NpcConfirmModalVtableRva = 0x0046AF3C;
	constexpr uintptr_t NpcConfirmControlOffset = 0x00000248;
	// Cập nhật sau bản game 2026-08-28: xác nhận bằng byte thật đọc trực tiếp từ runtime (DEBUG_REPAIR_POPUP_REFERENCE_SUMMARY | Label=VTABLE | Target=0x008732BC, auto-runtime.log 19:36:59.014), chưa build/test runtime.
	constexpr uintptr_t RepairConfirmModalVtableRva = 0x004732BC;
	constexpr uintptr_t RepairConfirmControlOffset = 0x00001174;
	constexpr int ModalConfirmEvent = 0x565;
	constexpr size_t PrepareMethodVtableOffset = 0x74;
	constexpr size_t AttackMethodVtableOffset = 0x40;
	constexpr int AttackPrepareOpcode = 9;
	constexpr size_t SelectGroundItemMethodVtableOffset = 0x48;
	constexpr size_t SaleMethodVtableOffset = 0x10;
	constexpr int SaleOpcode = 0x19;
	// Cập nhật sau bản game 2026-08-28: xác nhận qua symbol Ghidra DAT_00e7fe24 trong decompile hàm ATTACK mới, khớp delta +0x2020. Chưa build/test runtime.
	constexpr uintptr_t InventoryRootRva = 0x00A7FE24;
	constexpr uintptr_t InventoryObjectOffset = 0x0004B7BC;
	constexpr uintptr_t InventorySlotListPointerOffset = 0x00000000;
	constexpr int InventorySlotCount = 35;
	// Cập nhật 2026-09-07: hai offset cũ (0x140 và 0x208) đọc ra mảng rỗng và mảng id rác. Bằng chứng: tool Debug
	// "Probe dò container túi" (BuildStamp INVENTORY-CONTAINER-20260907-01, PID 21184) dò toàn inventory object
	// và tìm ra +0x230 là 4 ô trang bị nhanh, +0x2F8 là rương thứ 2 (35 ô); chủ dự án mở game đối chiếu và xác nhận.
	// Phải khớp với GameAddresses.Inventory bên C#: TryDispatchInventoryItem kiểm slotList[memoryIndex] == expectedItemId,
	// hai bên lệch offset là mọi lệnh dùng item ở container đó bị từ chối với mã 14.
	constexpr uintptr_t InventoryQuickSlotListPointerOffset = 0x00000230;
	constexpr int InventoryQuickSlotCount = 4;
	constexpr uintptr_t InventoryExtendedSlotListPointerOffset = 0x000002F8;
	constexpr int InventoryExtendedSlotCount = 35;
	constexpr int InventoryContainerType = 3;
	constexpr int InventoryUseOpcode = 10;
	constexpr int InventoryUsePrepareOpcode = 0x12A;
	// Cập nhật 2026-09-03: runtime bản 28/08 trả mã 15 cho lệnh 310 vì cặp RVA cũ (0x44C70 + call site 0x1B0F5B)
	// đã lệch. Giá trị mới do BuffPacketSenderProbe đọc trực tiếp trên tiến trình game: chữ ký 34 byte khớp đúng
	// một lần trong toàn ảnh, toán hạng global đọc ra 0x008EEF98 (bản cũ 0x008ECF78).
	// Không còn dùng call site để xác thực vì đó chính là thứ vỡ sau mỗi lần client cập nhật.
	constexpr uintptr_t InventoryCoordinateFunctionRva = 0x00045050;
	// EntityTableRva, GroundRecordTablePointerRva, GroundCoordinateConverterRva, ResetPickupFunctionRva, PickupMovementFunctionRva, BuffActionFunctionRva, PickupFunctionRva:
	// cập nhật sau bản game 2026-08-28, xác nhận bằng BSim + decompile đối chiếu chéo (control-flow, field offset, call-graph) với dump runtime mới; chưa build/test runtime.
	constexpr uintptr_t EntityTableRva = 0x0095FF60;
	constexpr uintptr_t PlayerEntityIndex = 1;
	constexpr uintptr_t EntityStride = 0x0000D87C;
	constexpr uintptr_t GroundRecordTablePointerRva = 0x0054DCC0;
	constexpr uintptr_t GroundRecordStride = 0x000003A4;
	constexpr uintptr_t GroundRecordIdOffset = 0x00000014;
	constexpr uintptr_t GroundRecordStateOffset = 0x0000001C;
	constexpr uintptr_t GroundCoordinateConverterRva = 0x002FBC30;
	constexpr uintptr_t ResetPickupFunctionRva = 0x00347380;
	constexpr uintptr_t PickupMovementFunctionRva = 0x0031EF70;
	constexpr uintptr_t BuffActionFunctionRva = 0x0031EF70;
	// Cập nhật 2026-09-03: runtime bản 28/08 từ chối lệnh 85. Probe đọc trên tiến trình game cho thấy hàm dựng gói
	// cast (opcode 0xB5, độ dài 11) nay ở RVA 0x3AC740, thunk JMP ở 0x3AC590, CALL builder ở 0x3AC5AE.
	// Bản 24/08 có bộ ba tương ứng 0x3AA500 / 0x3AA360 / 0x3AA37E, khoảng cách thunk→CALL giữ nguyên 0x1E.
	// DEV auto gọi thẳng builder vì thunk chỉ là một lệnh JMP, và xác thực bằng chữ ký byte trong chính hàm.
	constexpr uintptr_t CastSkillFunctionRva = 0x003AC740;
	constexpr uintptr_t CastSendSiteOffset = 0x00000031;
	// Cập nhật 2026-09-03: hàm gửi skill hỗ trợ bị động của hệ Dị Nhân (Kim Cang / Cường Công / Bồ Đề / Tật Phong).
	// Chuỗi bằng chứng: thân thread _TBuffDịNhân của AutoFS (VectorFactory.SplitDisk, khôi phục từ IL vì ILSpy hỏng)
	// bơm từng byte của một gói 16 byte qua lệnh 310 rồi chốt bằng lệnh 311; handler 311 trong SystemUint.dll gốc
	// (0x10003EAF) gọi vtable[0x20](obj, &buffer, &length) với length = 5, nên gói thật chỉ là "72 <id32>".
	// Client hiện tại có sẵn hàm dựng đúng gói đó; probe BuffPacketSenderProbe khớp chữ ký duy nhất 1 lần và
	// cả cách quét khuôn chung 175 hàm cũng chỉ ra đúng một hàm có opcode 0x72 kèm độ dài 5.
	// Cả 5 caller của hàm này đều dựng this bằng entityTable + index * EntityStride, tức this là entity nhân vật.
	// Chưa build/test runtime.
	constexpr uintptr_t PassiveBuffFunctionRva = 0x0031F850;
	constexpr uintptr_t PassiveBuffSendSiteOffset = 0x00000056;
	constexpr int PassiveBuffMaximumSkillId = 0x7CF;
	constexpr uintptr_t PickupFunctionRva = 0x003AC300;
	// Cập nhật 2026-09-06 sau khi client dời object popup Về thành. Nguồn: ModalVtableProbe chạy lúc popup chết đang mở
	// (ModuleBase=0x00400000, Modal=0x008FED58), đối chiếu death.log ghi cùng giá trị ở cả 5 tiến trình lúc chết.
	//   Object  0x004FCD38 -> 0x004FED58  (MODAL_VTABLE_OBJECT | ObjectRva=0x4FED58)
	//   Vtable  0x004685D8 -> 0x004696E0  (MODAL_VTABLE_TABLE | VtableRva=0x4696E0)
	// Hàm xử lý lựa chọn: 0x001A7810. Tìm ra 2026-09-06 bằng cách disassemble crash dump Game.exe.5140.dmp
	// (dump chụp đúng lúc popup chết đang mở, ModalState = 0x008FED58).
	// Chuỗi bằng chứng, không suy đoán bước nào:
	//   1. Object popup 0x008FED58 chứa 3 control con cùng lớp (vtable 0x00858418) tại +0x06E8 / +0x0994 / +0x0C50,
	//      nối nhau bằng danh sách hai chiều ở +0x68/+0x6C, và mỗi control mang nhãn inline tại +0x24C:
	//      "Về thành" / "Thế mạng" / "Phục sinh" (độ dài chuỗi ở +0x28C khớp 8/8/9).
	//   2. Bộ điều phối sự kiện của modal = vtable[ModalEventMethodVtableOffset] = 0x005A7F40. Disassembly của nó:
	//      cmp [ebp+8], 0x565 (= ModalConfirmEvent) rồi so [ebp+0Ch] lần lượt với this+0x6E8 / this+0x994 / this+0xC50
	//      và gọi CALL 0x005A7810 với tham số push 0 / push 1 / push 2.
	//      => thứ tự nút trùng khít enum DeathAction (ReturnToTown=0, Substitute=1, Revive=2).
	//   3. 0x005A7810 kết thúc bằng "ret 4" -> đúng __thiscall một đối số như ReturnToTownFunction đang khai báo.
	//      Thân hàm: đóng popup rồi lấy this->[0xC40]->vtable[0x10] gửi gói (0x501, this->[0xC44], option);
	//      khi popup không mở thì this->[0xC40] == 0 nên hàm tự bỏ qua phần gửi.
	//   4. Chữ ký 0x27 byte dưới đây khớp đúng 1 lần trong toàn bộ vùng ảnh Game.exe đã map của dump.
	// Giá trị cũ 0x001A74E0 rơi vào giữa một lệnh khác (byte thật: DC 8B 11 89 55 C4 8B 45 F0) nên memcmp luôn
	// safe-reject — đó chính là lý do "Về thành" chưa bao giờ chạy, chứ không phải lỗi phía C#.
	//
	// Lịch sử 2026-09-06: từng đặt hàm = 0x00051150 (ô +0x40 của vtable, khớp chữ ký prologue 9 byte cũ) và
	// client Game.exe PID 5140 crash ngay: 0xC0000005 rồi 0xC000041D (STATUS_FATAL_USER_CALLBACK_EXCEPTION) trong WndProc.
	// 0x51150 nhận HAI đối số (this->[0x18] += [ebp+8] ; this->[0x1C] += [ebp+0Ch], hàm dời toạ độ UI) nên gọi bằng
	// __thiscall một đối số làm lệch stack. Hàm Về thành thật KHÔNG nằm trong vtable của modal, đúng như manifest gốc
	// AutoFS (MOV EAX,imm / CALL EAX = hàm không ảo); nó chỉ được gọi từ bộ điều phối sự kiện ở bước 2.
	constexpr uintptr_t ReturnToTownObjectRva = 0x004FED58;
	constexpr uintptr_t ReturnToTownObjectVtableRva = 0x004696E0;
	constexpr uintptr_t ReturnToTownFunctionRva = 0x001A7810;
	// Cập nhật 2026-09-02 (PE TimeDateStamp 0x6A8DD698): toàn bộ chuỗi hàm dưới đây lấy từ decompile + disassembly thật
	// của FUN_004637c0 trong dump đã phân tích bằng Ghidra. FUN_004637c0 chính là binding Chat(kênh, nội dung) mà
	// script của client gọi, và nó tự thực hiện đủ các bước gửi chat; mọi lời gọi đều là __cdecl (mỗi CALL đều kèm
	// ADD ESP tương ứng trong disassembly gốc).
	//   FUN_004b0850(index)                        -> con trỏ mã kênh trong ChannelCodeTable
	//   FUN_004b02d0(index)                        -> channel type id, trả -1 khi index không hợp lệ
	//   FUN_004b3e40(msg, len, code, type)         -> cổng kiểm tra, phải khác 0 mới được gửi
	//   FUN_0057c6c0(buf, 0x600, msg, len)         -> đóng gói nội dung vào buffer 0x600 byte
	//   FUN_00817f5e(buf, len)                     -> mã hóa buffer, trả độ dài mới
	//   FUN_004ae3c0(index, 1)                     -> kích hoạt kênh trước khi gửi
	//   FUN_004b8620(type, buf, len, -1)           -> gửi gói tin chat thật
	// Ba hằng số ScriptExecuteFunctionRva/ScriptExecuteCallRva/ScriptContextRva đã bị gỡ: decompile FUN_00471480 cho
	// thấy nó chỉ duyệt danh sách handler script đã đăng ký rồi trả -1 khi không handler nào chặn, nên nó KHÔNG gửi
	// chat - đúng với bằng chứng runtime 2026-09-02 (Confirmed=True nhưng tin nhắn không xuất hiện).
	constexpr uintptr_t ChannelCodeFromIndexRva = 0x000B0850;
	constexpr uintptr_t ChannelTypeFromIndexRva = 0x000B02D0;
	constexpr uintptr_t ChatGateFunctionRva = 0x000B3E40;
	constexpr uintptr_t ChatPackFunctionRva = 0x0017C6C0;
	constexpr uintptr_t ChatEncodeFunctionRva = 0x00417F5E;
	constexpr uintptr_t ChannelActivateFunctionRva = 0x000AE3C0;
	constexpr uintptr_t ChatSendFunctionRva = 0x000B8620;
	constexpr size_t ChatPacketBufferSize = 0x600;
}
