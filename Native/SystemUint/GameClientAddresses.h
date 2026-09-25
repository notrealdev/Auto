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
	// Chọn entity theo chỉ số, tương đương việc click chuột vào NPC/quái. Tìm được 2026-09-17 bằng cách quét bản
	// dump toàn module của tiến trình đang chạy (chỉ đọc):
	//   - Biến "mục tiêu đang chọn" = VA 0x8CE688 (RVA 0x4CE688): = -1 trên 5 client không chọn ai, = 43 trên
	//     client vừa click Đại Phu, và entity 43 đọc ra đúng tên TCVN3 "Đại phu" tại 59162/93139.
	//   - Hàm ghi vào biến đó nằm ở VA 0x6CB070, và VA đó xuất hiện đúng MỘT lần trong cả 33 MB dưới dạng dữ liệu:
	//     tại 0x877820 = ExpectedManagerVtableRva (0x477804) + 0x1C. Nên gọi qua vtable, không hardcode RVA.
	//   - Thân hàm: push ebp / mov ebp,esp / mov eax,[ebp+8] / cmp eax,0x1FF / mov [0x8CE688],eax / mov eax,1 /
	//     ret 4. Nhánh index > 0x1FF ghi -1 (bỏ chọn). Chữ ký khớp SelectGroundItemFunction: __thiscall(void*, int).
	// CHƯA VERIFY runtime: mới chứng minh hàm này GHI biến mục tiêu, chưa chứng minh gọi nó là hội thoại NPC mở ra.
	constexpr size_t SelectEntityMethodVtableOffset = 0x1C;
	constexpr uintptr_t CurrentTargetIndexRva = 0x004CE688;
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
	// Hàm mua vật phẩm của chức năng "Tự động mua thuốc" có sẵn trong client (tương ứng lệnh 95 của AutoFS, gọi hàm
	// client cũ 0x5C3CB0 với (this, 1, mã, 0, số lượng)). Tìm ngày 2026-09-25 bằng dump ảnh Game.exe đang chạy (PID 2228):
	//   - Xref chuỗi "Bạc và bạc khóa của bạn không đủ... tự động mua thuốc" và "Tắt/Mở Tự động dùng thuốc" dẫn tới
	//     cụm 0x6C7xxx, cài đặt nằm ở InventoryRoot+0x6B760 (= this+0x35800 của hàm bọc 0x745ED0).
	//   - Hàm bọc 0x745ED0 (this = InventoryRoot+0x35F60) gọi 0x746010 cho HP rồi MP với bộ ba lấy từ danh sách thuốc.
	//   - 0x746010 dựng gói opcode 0xBE rồi gửi qua vtable[0x20] của [0x918718]; ret 0x1C = 7 tham số stack:
	//     (loại, chi tiết, cụ thể, số lượng, byte1, byte2, byte3).
	//   - Bộ ba thuốc đối chiếu với danh sách trong client: Tiểu Hồng (1,0,0), Trung Hồng (1,1,0), Bổ Tâm (1,19,0),
	//     Bổ Tâm trung (1,20,0) — khớp mã 0/1/19/20 của AutoFS. Ba byte cuối do gói server opcode 0x32 mang tới, ý nghĩa
	//     CHƯA biết nên Auto truyền 0.
	// Chữ ký trùng khớp trên cả 6 client (cùng bản build 2026-09-18). Chưa gọi thử ở runtime.
	constexpr uintptr_t QuickBuyFunctionRva = 0x00346010;
	constexpr uintptr_t QuickBuyThisOffset = 0x00035F60;
	constexpr int QuickBuyMaximumQuantity = 100;
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

	// --- Đăng nhập ---
	//
	// Dịch ngược từ DLL của AutoFS (D:\G\DEV\Resource\Auto\Lib\SystemUint.dll, PE 32-bit, 577.536 byte, 02/11/2024).
	// Cách tìm: WndProc của nó ở VA 0x10002950, so message với global 0x1008B6A0 rồi tra hai bảng —
	// bảng byte tại 0x10004168 (chỉ số case theo wParam 0..0x137) và bảng dword tại 0x10003F34 (địa chỉ handler).
	// Năm handler lấy được: 280 -> 0x100039E2, 281 -> 0x10003A1D, 282 -> 0x10003A58, 283 -> 0x10003B12,
	// 284 -> 0x10003B63.
	//
	// AutoFS ghi địa chỉ game ở dạng VA tuyệt đối; Game.exe có ImageBase = 0x00400000 (đọc header 2026-09-12) nên
	// RVA = VA - 0x400000.
	//
	// XÁC NHẬN SAI trên client 1.28 đang chạy: ba RVA gốc của AutoFS (build 11/2024, stride record item dưới đất
	// 920 so với 932 của client này) đều trỏ vào rác/vùng mã lệnh, không phải con trỏ đối tượng — xác nhận bằng
	// LoginAutoFsAddressProbe ngày 2026-09-16 (cả ba ô đều FAIL_UNREADABLE, giá trị đọc được là opcode x86, ví dụ
	// 0x354C50 giữ byte của "push esi; push edi; mov edi,ecx").
	//
	// Hai RVA dưới đây (280, 281) đã được thay bằng địa chỉ đo được TRÊN CHÍNH client 1.28, bằng cách chụp ảnh bộ
	// nhớ Game.exe ngay trước/sau khi đóng từng hộp và tìm ô duy nhất về giá trị 0 lúc hộp bị huỷ — tái lập được
	// 2 lần độc lập trên 2 tiến trình khác nhau, cùng cho kết quả giống hệt:
	//   Khuyến cáo (280): RVA 0x354D5C -> 0x4FED14, một ô duy nhất về 0 trong toàn bộ 33MB ảnh khi đóng hộp.
	//   Phiên bản (281): RVA 0x354C50 -> 0x4EECD0 (dùng ô này), đối chứng bằng ô song song 0x4F47DC cùng trỏ một
	//     đối tượng và cùng về 0 lúc đóng — hai ô là bản sao của nhau, dùng ô nào cũng ra cùng đối tượng.
	constexpr uintptr_t LoginNoticeDialogRva = 0x004FED14;   // lệnh 280: hộp "Khuyến cáo chơi game"
	constexpr uintptr_t LoginVersionDialogRva = 0x004EECD0;  // lệnh 281: hộp "Thông tin phiên bản"
	// Hộp "Chọn máy chủ". Hai ứng viên tìm bằng kỹ thuật "quét về 0" trước đó (0x4EEFAC, 0x4EE7FC) đều ĐÃ LOẠI:
	// đọc lại trên nhiều tiến trình độc lập chỉ hợp lệ ~50% và 0%, trong khi một global slot thật phải hợp lệ 100%.
	// Giá trị dưới đây tìm bằng cách khác, chặt hơn: chụp ảnh bộ nhớ lúc đang ở hộp 1 (chưa có hộp "Chọn máy chủ")
	// và lúc đã sang hộp 3, lấy GIAO các ô thay đổi qua 4 tiến trình độc lập — lọc từ hàng nghìn ô xuống 8 ứng viên,
	// rồi đọc lại trên 3 tiến trình mới: 0x50477C hợp lệ 3/3 và là ô DUY NHẤT có vtable nằm trong ảnh Game.exe.
	constexpr uintptr_t LoginServerDialogRva = 0x0050477C;   // lệnh 282: hộp "Chọn máy chủ"
	// Bố cục bên trong hộp "Chọn máy chủ", đọc trực tiếp từ mã máy của client (không suy đoán từ AutoFS):
	//   Bộ điều phối sự kiện = vtable[0x10] tại RVA 0x1FE9D0, chỉ nhận đúng hai mã sự kiện AutoFS dùng:
	//     0x1FE9D0+0x3E: cmp [ebp-0xC], 0x565 (bấm nút)   0x1FE9D0+0x4C: cmp [ebp-0xC], 0x691 (chọn dòng)
	//   Nhánh 0x691 so con trỏ control lần lượt với this+0x280 + 0x2B0*i, i = 0..3 → mảng 4 danh sách:
	//     0x1FE9D0+0xA5: mov edx,0x2B0 / imul eax,edx,0 / lea edx,[ecx+eax+0x280] / cmp [ebp+0xC],edx
	//   Nhánh 0x565 gọi hàm con 0x1FDED0, hàm này so control với đúng hai nút:
	//     0x1FDED0+0x0A: add eax,0x2280 / cmp [ebp+8],eax   → "Vào trò chơi"
	//     0x1FDED0+0x21: add ecx,0x27EC / cmp [ebp+8],ecx   → "Thoát game" (hàm con của nó là push 1; call → thoát)
	constexpr size_t LoginServerListArrayOffset = 0x280;   // danh sách thứ i = dialog + 0x280 + 0x2B0*i
	constexpr size_t LoginServerListStride = 0x2B0;
	// Mã điều phối chỉ so con trỏ với 4 phần tử đầu, nhưng thực tế có phần tử thứ 5 ngay sau đó:
	// chọn cụm "Cụm hồi ức 2008" rồi so ảnh bộ nhớ của chính đối tượng hộp thoại thì ô +0xED4 đổi 0 -> 3,
	// đúng bằng số máy chủ vừa hiện trên màn hình; 0xED4 - 0x194 = 0xD40 = 0x280 + 0x2B0*4.
	constexpr int LoginServerListCount = 5;
	constexpr int LoginPartitionListIndex = 0;   // danh sách cụm máy chủ (3 dòng, đã kiểm chứng)
	constexpr int LoginServerListIndex = 4;      // danh sách máy chủ của cụm đang chọn
	// Dòng đang chọn của một danh sách: cùng lần so ảnh trên, ô +0x428 đổi 0 -> 2 đúng bằng dòng vừa chọn,
	// mà 0x428 - 0x280 = 0x1A8 là offset trong chính danh sách thứ 0.
	constexpr size_t ListSelectedIndexOffset = 0x1A8;
	constexpr size_t LoginServerEnterButtonOffset = 0x2280;  // nút "Vào trò chơi"
	// Chỉ ghi lại để KHÔNG bao giờ gửi nhầm — nút này thoát game.
	constexpr size_t LoginServerQuitButtonOffset = 0x27EC;
	// Hàm đặt dòng đang chọn của một danh sách, thiscall(control, index). Tìm được vì hàm xử lý chọn dòng
	// (0x1FE7C0) gọi chính nó với tham số -1 để xoá chọn ba danh sách còn lại:
	//   0x1FE7C0+0x3F: push -1 / mov ecx,[ebp-8] / call 0x355B0
	// Bên trong 0x355B0 có "cmp ecx,[eax+0x194]" → số phần tử của danh sách nằm ở control+0x194.
	constexpr uintptr_t ListSetSelectionFunctionRva = 0x000355B0;
	constexpr size_t ListItemCountOffset = 0x194;
	// obj -> +0x54 lấy khung giao diện, khung -> +0x58 lấy đối tượng nhận sự kiện, gọi vtable[0x10] của nó.
	// XÁC NHẬN SAI trên client 1.28: đo trực tiếp bằng lệnh chẩn đoán 286 (DiagnoseLoginControlChain) ngày
	// 2026-09-16, dialog đọc đúng nhưng *(dialog+0x54) luôn ra NULL. Vẫn giữ hai hằng số này vì
	// TryDispatchLoginControlEvent/TryDispatchLoginSelectServer (lệnh 282, "Chọn máy chủ") vẫn đang dùng —
	// KHÔNG dùng cho lệnh 280/281 nữa, xem LoginConfirmControlOffset bên dưới.
	constexpr size_t LoginDialogFrameOffset = 0x54;
	constexpr size_t LoginDialogDispatcherOffset = 0x58;
	// Khuôn đúng cho lệnh 280/281, xác nhận bằng quan sát pixel thật (không phải chỉ đọc bộ nhớ): dispatchEvent lấy
	// thẳng từ vtable CỦA CHÍNH dialog (giống TrySelectDialogOption của modal Npc/Repair), không qua "control"/
	// "receiver" trung gian như mô hình +0x54/+0x58 ở trên. confirmControl = dialog + 0x278 - dò bằng quét tuần tự
	// offset trên vùng nhớ dialog rồi thử gọi thật, không suy luận từ cấu trúc lớp. Đo 2026-09-16: dispatch lệnh 280
	// (offset 0x278) đưa "Khuyến cáo" -> "Thông tin phiên bản"; dispatch lệnh 281 (cùng offset 0x278) đưa
	// "Thông tin phiên bản" -> "Chọn máy chủ" - cả hai đều chụp màn hình xác nhận, không suy đoán.
	constexpr size_t LoginConfirmControlOffset = 0x278;
	// Ô lưu dòng đang chọn của một danh sách, ghi thẳng trước khi bắn sự kiện chọn.
	constexpr size_t LoginListSelectionOffset = 0x88;
	// AutoFS gốc còn hai hằng số cho hộp "Chọn máy chủ": +0x970 (danh sách máy chủ) và +0x106C (nút "Vào trò chơi").
	// Đã bỏ vì client 1.28 dùng bố cục khác hẳn, đọc thẳng từ mã máy: xem LoginServerListArrayOffset và
	// LoginServerEnterButtonOffset ở trên.
	constexpr int LoginListSelectEvent = 0x691;       // chọn một dòng trong danh sách (ModalConfirmEvent 0x565 là bấm nút)
	// --- Màn hình đăng nhập cuối (ô Tài khoản/Mật khẩu + bàn phím ảo) ---
	//
	// Tìm bằng bộ quét ScanForDialogByEvent: đối chiếu danh sách object có nút (sự kiện 0x565) lúc đang ở màn
	// "Chọn máy chủ" với lúc đã sang màn đăng nhập, lấy phần chênh lệch. Bộ điều phối của nó (RVA 0x1BB760) bắt
	// nhiều sự kiện (0x565, 0x100, 0x104, 0x502, 0x62E, 0x6F5) và có khung stack 0xE0 kèm stack cookie — dấu hiệu
	// hàm có xử lý chuỗi, khớp với việc phải đọc tài khoản/mật khẩu.
	//
	// Nhánh 0x565 của nó so control với hai offset; ĐO THẬT trên client (không suy từ mã):
	//   dialog + 0x0AC4 -> "Bắt đầu trò chơi": bấm khi mật khẩu trống làm hiện popup "HỆ THỐNG THÔNG BÁO" (có ảnh).
	//   dialog + 0x159C -> nút xổ danh sách tài khoản đã lưu (có ảnh: danh sách tl_ajaja/ti_alala/ahihea bung ra).
	// Lưu ý: đọc mã lệnh thì tưởng 0x159C mới là nút đăng nhập, nhưng thử thật cho kết quả NGƯỢC LẠI — giữ đúng
	// kết quả đo được, không theo suy luận.
	//
	// Nhánh đăng nhập lấy hai chuỗi từ hai Ô NHẬP trên giao diện rồi mới gửi đi — nội dung cũ còn trong ô tài khoản
	// CÓ ảnh hưởng, nên phải ghi đè chứ không nối thêm.
	constexpr uintptr_t LoginCredentialDialogRva = 0x00503A90;
	constexpr size_t LoginStartButtonOffset = 0x0AC4;      // "Bắt đầu trò chơi"
	constexpr size_t LoginAccountDropdownOffset = 0x159C;  // xổ danh sách tài khoản đã lưu
	//
	// Hai ô nhập. Đọc byte thật của hàm lấy thông tin đăng nhập (RVA 0x1BA600, __thiscall(out tàiKhoản, out mậtKhẩu)):
	//   0x1BA63E "add edx, 0x27C" rồi gọi GetText(out, 0x20, 0)  -> ô TÀI KHOẢN
	//   0x1BA65E "add ecx, 0x790" rồi gọi cùng hàm đó            -> ô MẬT KHẨU
	//   nếu ô nào rỗng thì hàm trả 0 và client hiện "Xin nhập tài khoản và mật mã" (khớp popup đã chụp được).
	// Hai offset này trùng đúng hai control mà bản đồ đối tượng tìm ra (vtable 0x4577B0 và 0x457764).
	constexpr size_t LoginAccountFieldOffset = 0x27C;
	constexpr size_t LoginPasswordFieldOffset = 0x790;
	//
	// Lớp ô nhập: chuỗi nằm ở field+0x27C, độ dài ở field+0x288, sức chứa ở field+0x284.
	//   GetText  = RVA 0x28820, __thiscall(char* out, int maxLen, int flag), "ret 0xC".
	//   SetText  = RVA 0x2BEB0, __thiscall(const char* text, int length, int flag), "ret 0xC".
	// Thân SetText đọc được: nếu text == null thì thoát; truyền length = -1 thì tự gọi strlen; sau đó memcpy vào
	// [this+0x27C], ghi độ dài vào [this+0x288], đặt NUL, xoá vùng chọn (+0x290/+0x294) và đưa con trỏ nhập
	// (+0x29C) về cuối chuỗi. Vì nó GHI ĐÈ nên không cần xoá ô tài khoản trước.
	constexpr uintptr_t FieldGetTextFunctionRva = 0x00028820;
	constexpr uintptr_t FieldSetTextFunctionRva = 0x0002BEB0;
	//
	// ĐÃ KIỂM CHỨNG ĐẦU-CUỐI trên client mới PID 17128 (2026-09-16), chỉ dùng đường dispatch nội bộ:
	//   280 -> 281 -> 282 -> ghi hai ô nhập bằng SetText -> bật ô Điều khoản -> bấm nút 0xAC4.
	//   Ảnh 1: ô tài khoản hiện "tl_sai_tk", ô mật khẩu hiện 10 dấu sao -> SetText ghi đúng cả hai ô.
	//   Ảnh 2: client hiện popup "HỆ THỐNG THÔNG BÁO — Tài khoản hoặc mật mã không đúng !".
	//   Popup đó do MÁY CHỦ trả về (tài khoản cố tình sai), nên nó chứng minh yêu cầu đăng nhập đã thật sự gửi đi.
	// Nhờ vậy KHÔNG cần tới hàm submit của AutoFS (LoginSubmitFunctionRva đã chết) và cũng không cần bàn phím ảo.
	//
	// Câu thông báo mà client đang hiển thị ở màn đăng nhập. Tìm ra bằng cách quét các ô toàn cục trỏ tới hộp thoại
	// rồi dò chuỗi trong thân đối tượng; xác nhận bằng TƯƠNG PHẢN BA TRẠNG THÁI trên cùng một offset, mỗi lần đối
	// chiếu với ảnh chụp màn hình (2026-09-16):
	//   chưa bấm gì      -> "Đang kết nối với máy chủ."
	//   sai mật khẩu     -> "Tài khoản hoặc mật mã không đúng!"        (PID 9796)
	//   hai ô để rỗng    -> "Xin nhập tài khoản và mật mã."            (PID 22204)
	// Chuỗi mã hoá TCVN3 chứ không phải UTF-8: đọc được ả=0xB6, đ=0xAE, ọ=0xE4, ò=0xDF, cả bốn khớp bảng TCVN3.
	constexpr uintptr_t LoginStatusMessageObjectRva = 0x004FDA48;
	constexpr size_t LoginStatusMessageOffset = 0x22E4;
	constexpr int LoginStatusMessageMaxLength = 128;
	//
	// Ô "Đồng ý Điều khoản". Đường đi: nhánh 0x565 tại RVA 0x1BB0F0 làm "add ecx,0x1B28 / call / test eax,eax /
	// jnz bỏ_qua" — nếu hàm trả 0 thì hiện popup rồi thoát, KHÔNG gửi đăng nhập. Hàm được gọi nằm ở RVA 0x24900,
	// thân hàm chỉ có "movzx eax, word [this+0x248]; and eax, 0x200" -> cờ = bit 0x200 của word tại 0x1B28+0x248.
	// ĐO THẬT (client PID 9212, cùng một đối tượng dialog 0x0EE3B3D8, chỉ khác thao tác tick chuột của người dùng):
	//   chưa tick -> word = 0x0002 (bit 0x200 = 0)
	//   đã  tick -> word = 0x0202 (bit 0x200 = 512)
	// Đây là tương phản trước/sau trên cùng tiến trình nên xác nhận được offset, không phải suy luận từ mã.
	constexpr size_t LoginAgreeTermsControlOffset = 0x1B28;
	constexpr size_t LoginAgreeTermsFlagOffset = 0x1D70;   // = 0x1B28 + 0x248
	constexpr uint16_t LoginAgreeTermsFlagMask = 0x0200;
	//
	// Hàm xử lý CÚ BẤM của chính control (không phải của hộp thoại). Đọc byte thật từ tiến trình đang chạy:
	//   0x24930 "55 8B EC 83 EC 34 89 4D FC" -> prologue sạch; kết thúc 0x24B26 "8B E5 5D C2 08 00" -> __thiscall, 2 tham số.
	//   0x2493D "cmp dword [ebp+0xC],0 / je 0x249F0" -> truyền toạ độ = 0 thì BỎ QUA hit-test, vào thẳng thân xử lý.
	//   0x24A3A "and eax,2 / jnz 0x24AB9" -> với word cờ 0x0002 thì rẽ sang nhánh đảo trạng thái.
	//   0x24AB9..0x24AD9 đọc bit 0x200 rồi đặt biến cục bộ = NGHỊCH ĐẢO, gọi 0x23770(bool) -> đúng nghĩa toggle.
	//   0x24B19 push 0x565 rồi gọi vtable[0x10] của dialog cha -> control tự báo lại cho hộp thoại.
	// Tham số 1 ([ebp+8]) chỉ dùng ở nhánh kia (chọn mã sự kiện 0x56A/0x566), nhánh này bỏ qua.
	constexpr uintptr_t ControlClickFunctionRva = 0x00024930;
	// Ngõ cụt đã loại trừ, ghi lại để khỏi thử lại: bắn 0x565 thẳng vào dialog+0x1B28 trả về 1 nhưng ô KHÔNG đổi
	// trạng thái (có ảnh trước/sau). Bộ điều phối của hộp thoại chỉ NHẬN thông báo, chính control mới tự đảo cờ.
	//
	// ĐÃ KIỂM CHỨNG trên client mới PID 33808 (lệnh 309 gọi ControlClickFunctionRva với this = dialog+0x1B28):
	//   đọc cờ trước = 0 -> gọi hàm -> đọc cờ sau = 1, và ảnh chụp cửa sổ cho thấy ô đã có dấu tích ĐỎ,
	//   cùng kiểu với ô "Nhớ tài khoản" đang bật. Đây là bằng chứng cả ở bộ nhớ lẫn ở pixel.
	// Kèm theo: dấu tích NHẠT nhìn thấy ở các ảnh trước đó là trạng thái CHƯA tích — chỉ dấu tích đỏ mới là đã tích.
	//
	// QUAN TRỌNG: hàm này ĐẢO trạng thái, không phải "bật". Client NHỚ lựa chọn giữa các phiên: client mới PID 17128
	// mở lên đã đọc ra cờ = 1 sẵn, gọi hàm một phát thành 0 (ảnh cho thấy dấu tích chuyển từ đỏ sang nhạt).
	// Vì vậy luôn phải ĐỌC cờ trước rồi mới đảo khi cần, đừng gọi thẳng.
	// Hai ô toàn cục do nút "Vào trò chơi" ghi ra, đọc từ mã của chính nó (RVA 0x1FE0A0 + 0x39 và + 0x42):
	constexpr uintptr_t SelectedClusterIndexRva = 0x00504790;
	constexpr uintptr_t SelectedServerIndexRva = 0x00504794;

	// Lệnh 284: gọi thiscall 0x00576450 với this = 0x00774140 và hai chuỗi tài khoản/mật khẩu, rồi ba hàm cdecl dọn dẹp.
	//
	// XÁC NHẬN SAI trên client 1.28 (đo 2026-09-16, đọc byte thật từ tiến trình đang chạy):
	//   - Byte tại RVA 0x176450 là "83 C2 01" (add edx,1) rồi "89 55 FC" (mov [ebp-4],edx) — DÙNG ebp mà không hề
	//     thiết lập, tức nằm GIỮA THÂN HÀM chứ không phải điểm vào.
	//   - Quét ngược thấy prologue sạch "55 8B EC 83 EC 18" tại RVA 0x176420, ngay sau đệm CC CC. Vậy hàm bắt đầu ở
	//     0x176420 và 0x176450 nằm sâu 0x30 byte bên trong nó.
	//   - Hàm 0x176420 kết thúc bằng "C2 08 00" (ret 8 = 2 tham số) trong khi LoginSubmitFunction đang khai báo 4
	//     tham số; thân nó là vòng lặp giới hạn 0x0A, không phải thủ tục đăng nhập bằng tài khoản/mật khẩu.
	// Đây cùng khuôn lỗi với ba RVA hộp thoại đăng nhập đã phải sửa. AuditLoginSubmitFunctions KHÔNG phát hiện được
	// vì nó chỉ hỏi IsExecutableAddress (địa chỉ có nằm trong trang mã hay không) — mọi địa chỉ giữa .text đều đạt,
	// nên kết quả "PASS" của nó KHÔNG phải bằng chứng địa chỉ đúng. Lệnh 284 coi như CHƯA DÙNG ĐƯỢC cho tới khi tìm
	// được điểm vào thật; ba hằng số dọn dẹp bên dưới cũng chưa được kiểm chứng theo cách nào mạnh hơn.
	constexpr uintptr_t LoginSubmitContextRva = 0x00374140;
	constexpr uintptr_t LoginSubmitFunctionRva = 0x00176450;
	constexpr uintptr_t LoginAfterSubmitFunctionARva = 0x000247C0; // gọi với (1, 5, 0, 0)
	constexpr uintptr_t LoginAfterSubmitFunctionBRva = 0x00025EB0; // gọi với (0)
	constexpr uintptr_t LoginAfterSubmitFunctionCRva = 0x00026F90; // gọi với (1)
	// AutoFS cấp đúng 32 byte cho mỗi chuỗi (lệnh 284 xoá 8 dword cho mỗi vùng đệm trước khi trả).
	constexpr size_t LoginCredentialBufferSize = 32;
}
