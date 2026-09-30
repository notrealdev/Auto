namespace Auto.Utils;

public static class GameAddresses {
	public const string ModuleName = "Game.exe";
	// Không có nơi nào tham chiếu hằng số này trong code hiện tại (kiểm bằng grep 2026-09-29) — cập nhật cho đúng
	// SizeOfImage thật đo được sau bản update 2026-09-29 (base=0x400000, size=0x1FA7000, PID 9848).
	public const int ModuleMaximumSize = 0x1FA7000;

	public static class Globals {
		// Cập nhật sau bản game update 2026-09-29 (server báo "toang", SizeOfImage đo được 0x1FA7000, cũ 0x1F83000/0x1F7D000).
		// Xác nhận bằng đối chiếu byte-context giữa dump cũ (game2228.bin, 2026-09-25) và dump mới (PID 19040, đang thật sự
		// trong game — 5/6 client khác vẫn ở màn chọn nhân vật nên đọc ra 0 là bình thường, KHÔNG phải bằng chứng RVA sai):
		// EntityTable+ItemTable+GroundRecordTable+InventoryRoot+MapCoordinateRoot đều lệch cùng +0x24060; MapId+MapIdMirror
		// lệch +0x24068; MapIdRuntimeMirror lệch +0x24060. Xác nhận CHẮC (không chỉ khớp offset) bằng cách đi trọn chuỗi
		// InventoryRoot->[+0x41D3C]=chỉ số nhân vật->EntityTable+index*0xD87C: PID 19040 ra player_idx=1, entity đó có
		// HP=511/MaxHP=667 (hợp lý), và cả 3 bản sao MapId cùng đồng thuận =35 (Đại Trạch). CurrentTargetIndex đối chiếu
		// đúng ngữ nghĩa cũ: PID 19040 (đang có mục tiêu) đọc ra 99, 5 client còn lại đọc ra -1 (chưa chọn ai).
		// CHƯA GIẢI QUYẾT (giữ nguyên RVA cũ, gần như chắc chắn cũng sai theo bản update này, cần thêm bằng chứng runtime
		// trực tiếp lúc trạng thái đó xảy ra — mở shop / bật popup Về thành / có mục tiêu chiến đấu đang khoá):
		// ShopState và ModalState đã có ứng viên (delta +0x24068, giống MapId) nhưng CHƯA có state ĐANG BẬT nào để đối
		// chứng (đọc 0/1 giống nhau ở mọi client không loại được khả năng trúng ô hằng-số khác luôn 0). CombatTargetRoot,
		// ReturnToTownModal, DialogPointer không tìm được bằng quét immediate 4-byte trong cả hai dump (có thể được nạp
		// qua thanh ghi/offset nhỏ thay vì literal tuyệt đối) — RVA cũ bên dưới GIỮ NGUYÊN nhưng KHÔNG ĐƯỢC TIN, cần dò
		// lại bằng phương pháp khác (ví dụ bắt đúng lúc popup mở / có mục tiêu combat).
		public const int EntityTable = 0x73A0C4;
		// Sửa 2026-09-17: giá trị cũ 0x4CB668 CHẾT trên client 1.28. Bằng chứng đo trực tiếp trên bản dump toàn
		// module (0x400000 + 0x1F83000) của tiến trình đang chạy:
		//   - 0x4CB668: KHÔNG có một lệnh nào đọc/ghi trong cả 33 MB (quét A3/890D/8915/891D/8935/893D/C705/A1/8B0D),
		//     và đọc ra 0xB56E2063 GIỐNG HỆT NHAU trên cả 6 tiến trình, tức không phải giá trị theo phiên.
		//     Đối chứng cùng cách quét: EntityTable có 1935 tham chiếu, AttackManager có 4398.
		//   - 0x4CE688: = -1 trên 5 client không chọn mục tiêu, = 43 trên client vừa click Đại Phu, và entity số 43
		//     đọc ra đúng tên TCVN3 A7B96920706875 ("Đại phu") tại 59162/93139 — khớp toạ độ trong repair.log.
		//   - Hàm ghi vào nó nằm ở RVA 0x2CB070: cmp eax,0x1FF / mov [0x8CE688],eax / ret 4.
		// CHƯA VERIFY runtime: mới chứng minh đọc ra đúng số, chưa chạy Auto để xem nhánh dùng nó đổi hành vi thế nào.
		public const int CurrentTargetIndex = 0x4F2674;
		public const int InventoryRoot = 0xAA3E84;
		public const int ItemTable = 0x5650C8;
		// UNUSED_NO_RULE từ trước bản update này (không có nơi nào tham chiếu) — giữ nguyên RVA cũ, KHÔNG xác nhận lại.
		public const int AttackManager = 0x4E0640;
		public const int MapCoordinateRoot = 0xA1E0E0;
		// CHƯA XÁC NHẬN LẠI sau bản update 2026-09-29 — xem ghi chú ở EntityTable. Quét immediate 4-byte không tìm
		// được ứng viên nào trong cả hai dump; giữ nguyên RVA cũ dù gần như chắc chắn cũng sai.
		public const int CombatTargetRoot = 0x3A95D8;
		public const int MapId = 0x527B84;
		public const int MapIdMirror = 0x527B88;
		public const int MapIdRuntimeMirror = 0x53CBC8;
		// Ứng viên delta +0x24068 (cùng cụm với MapId/MapIdMirror), nhưng CHƯA đối chứng được lúc modal thật sự đang mở —
		// đọc ra 0 giống nhau ở mọi client không loại được khả năng trúng ô hằng-số khác luôn 0. Cần bắt đúng lúc có popup.
		public const int ModalState = 0x512FF0;
		// CHƯA XÁC NHẬN LẠI — xem ghi chú ở EntityTable.
		public const int DialogPointer = 0x500BE0;
		// Ứng viên delta +0x24068, CHƯA đối chứng lúc shop thật sự đang mở (ShopState nên =2) — xem ghi chú ModalState.
		public const int ShopState = 0x513740;
		// Client shop catalog (BuySell object): +0 pointer to rows (one int* per shop id), +4 item records
		// (stride Item.InventoryRecordStride, name at Item.InventoryName, weight at Item.Weight), +8 max positions per row,
		// +0xC row count, +0x10 record count. Read on PhâyKer 2026-09-27 (pre-update): 61 rows, 46 positions, 1026
		// records; shop 14 (Đại Phu map 32) = Tiểu Hồng đơn, Tiểu Hoàn đơn, Trung Hồng đơn, Trung Hoàn đơn. Id of the
		// open shop: Inventory.OpenShopId.
		// RVA re-verified after the 2026-09-29 server update (same +0x24060 delta as EntityTable/ItemTable): read live
		// on PID 19040, structure content UNCHANGED — 61 rows, 46 positions, 1026 records match exactly.
		public const int ShopCatalog = 0xABD074;
		// Cập nhật sau bản update 2026-09-29: xem GameClientAddresses.h::ReturnToTownObjectRva cho đầy đủ bằng chứng
		// (bắt sống lúc PID 26680 chết thật, delta +0x24068 khớp cụm B, đối chứng chéo bằng vtable +0x22000).
		// Đây chính là nguyên nhân "Không tự về thành khi chết được" — deathModal không bao giờ khớp expectedModal
		// (AccountEngineCoordinator.HandleDeathPopup dòng 960) nên playerDead luôn false dù Hp đã về 0.
		public const int ReturnToTownModal = 0x522DC0;
		// Con trỏ tới khối chứa sức lực mang đồ; cộng Inventory.CurrentStrength / Inventory.MaximumStrength để ra cặp.
		//
		// Tìm bằng cách quét ngược con trỏ tới ô 0x28079C9C đã lọc được: trong 65 con trỏ trỏ vào khối đó, chỉ 2 cái
		// nằm tĩnh trong Game.exe, và chỉ cái này đọc ra cặp hợp lệ trên CẢ 6 client (cái kia, RVA 0x2170384, trả
		// con trỏ 0 hoặc rác ở 5/6 client).
		//
		// Đo 2026-09-23 qua chuỗi này: PID 19860=69/414, 23828=299/515, 2272=81/335, 24512=61/343, 3848=31/335,
		// 24236=105/277. Cả 6 đều thoả 0 < hiện tại <= tối đa, và tối đa KHÁC nhau theo từng nhân vật — đúng bản
		// chất dữ liệu riêng chứ không phải bảng dùng chung. Riêng 299/515 khớp đúng số chủ dự án đọc trong game.
		public const int StrengthRoot = 0x5010D4;
	}

	public static class Entity {
		public const int PlayerIndex = 1;
		// Biên quét bảng entity, DÙNG CHUNG cho mọi nơi. Cả hai đều là chỉ số HỢP LỆ, vòng lặp phải dùng "<=".
		//
		// Trước đây mỗi nơi tự định nghĩa: Attack/AutoFsClientProfile dùng "index <= 256" còn Utils/RuntimeEntityLocator
		// dùng "index < 256", lệch nhau đúng ô 256. Chỉ số của cùng một NPC khác nhau ở từng client — repair.log
		// 2026-09-10 ghi Đại Phu ở index 95, 123, 134, 141, 163, 247 trên 4 client — nên client nào rơi trúng 256 thì
		// luồng sửa đồ không bao giờ thấy NPC trong khi luồng đánh vẫn thấy.
		//
		// Nâng trần 256 -> 511 ngày 2026-09-11. Trần 256 chép theo AutoFS ("for (int k = 2; k < 256; k++)",
		// WindowQueue.cs:24241) và AutoFS SAI ở chỗ này — bảng entity của client lớn hơn thế nhiều.
		//
		// Bằng chứng, EntityTableDumpProbe quét 2..1023 trên PID=22824 đứng cạnh NPC nhiệm vụ:
		//   !272 Hoàng Thiên Hóa | Type=3 | Raw=54980/94428 | cách nhân vật 0,14 ô   <- NPC cần tìm, ngoài trần cũ
		//   Quét 2..1023 | TrongDải(<=256)=15 | NGOÀIDẢI(>256)=123
		// tức Auto chỉ nhìn thấy 15/138 entity có tên. Các NPC khác cũng nằm ngoài: #324 Thổ Hành Tôn,
		// #343 Nhà chiêm tinh, #350 Thủ khố, #405 Chủ Tiền Trang (đều Type=3).
		// Đây là nguyên nhân của cả hai sự cố: luồng Sửa đồ quay 469 chuyến không thấy Đại Phu (repair.log
		// 2026-09-11) và luồng Thám quân không click được NPC (quest.log 2026-09-11).
		//
		// Vì sao dừng ở 511 chứ không quét tiếp: cùng bản dump đó, entity lành cuối cùng là #475
		// (SAOCUNGDUOC | Type=1 | St=7 | Lv=66 | Raw=56060/95359); từ #513 trở lên toàn rác — tên 'ÿÿÿÿ',
		// Type=1919972096, toạ độ 0/0 — tức đã đọc quá đuôi bảng. Khoảng 476..512 không entity nào có tên.
		// 512 ô là ranh giới khớp với dữ liệu, quét thêm chỉ rước rác vào bộ lọc mục tiêu.
		public const int FirstScanIndex = 2;
		public const int LastScanIndex = 511;
		public const int Stride = 0xD87C;
		public const int Handle = 0x0000;
		public const int SlotIndex = 0x0004;
		public const int ClassPointer = 0x0008;
		public const int PrimaryPointer = 0x000C;
		public const int SecondaryPointer = 0x0010;
		public const int MirrorIndex = 0x0014;
		public const int ActiveFlag = 0x0018;
		public const int Type = 0x0028;
		public const int Level = 0x0024;
		public const int Hp = 0x27D0;
		public const int MaxHp = 0x27D4;
		public const int Mp = 0x27DC;
		public const int MaxMp = 0x27E0;
		public const int Name = 0x2D70;
		public const int RawX = 0x434C;
		public const int RawY = 0x4350;
		public const int RawXMirror = 0x71EC;
		public const int RawYMirror = 0x71F0;
		// Cặp toạ độ thứ ba, bản layout AutoFS đời cũ dịch +4 cho client hiện tại
		// (D:\G\DEV\Resource\Tests\DEV-CLIENT-UPDATE-001.lua:14-15 ghi rawX=0x75F0, rawY=0x75F4 trong bảng "old").
		// GameMemory.ReadSnapshot đọc cặp này cho tới 2026-09-16. Giữ lại CHỈ để ClientFreezeWatch đối chiếu ba
		// nguồn — chưa xác định được cặp nào mới là vị trí thật khi ba nguồn bất đồng.
		public const int RawXLegacy = 0x75F4;
		public const int RawYLegacy = 0x75F8;
		public const int PlayerDeathStatus = 0x01E8;
		public const int PlayerDeathState = 0x01EC;
		public const int MoveTargetXCandidate = 0x2CDC;
		public const int MoveTargetYCandidate = 0x2CE0;
		// Đích lệnh di chuyển, xác nhận runtime 27/08/2026: chỉ đổi khi có lệnh mới, giữ nguyên suốt lúc nhân vật đang đi, click giao diện trong game không ghi.
		public const int MovementDestinationX = 0x42D4;
		public const int MovementDestinationY = 0x42D8;
		public const int AnchorXCandidate = 0x42E8;
		public const int AnchorYCandidate = 0x42EC;
	}

	public static class Attack {
		public const int ResetCommand = 0xD794;
		public const int TargetIndexCommand = 0xD798;
		public const int CommandActive = 0xD7A0;
		public const int ColdStartMethodVtable = 0x38;
		public const int PrepareMethodVtable = 0x6C;
	}

	public static class Inventory {
		// Bộ đếm số bình đã mua nhanh trong ngày (u16) tại InventoryRoot + offset này. Hàm mua của client (RVA 0x346010) so nó
		// với giới hạn 1000 (VA 0xE99004) và thoát không mua khi counter >= giới hạn; đọc từ disassembly 2026-09-25, và đo trên
		// 6 client cùng lúc đó thấy 34..413 (đều dưới 1000). Trùng ô nhớ [F4+205692] < 1000 của AutoFS.
		public const int QuickBuyDailyCount = 0x39CCC;
		// Gốc MẢNG container, không phải một đối tượng đơn lẻ.
		//
		// Dịch ngược hàm điều phối của client tại RVA 0x3714D0 (PID 22056, 2026-09-09): container là mảng phần
		// tử 40 byte bắt đầu tại đây, đánh chỉ số bằng mã container, và client chỉ chấp nhận mã 0, 14, 19..28:
		//     test cl,cl / je   -> lea ecx,[ecx+0x4B7BC]      (mã 0)
		//     cmp cl,0x0E / je  -> lea ecx,[ecx+0x4B9EC]      (mã 14)
		//     lea eax,[ecx-0x13] / cmp al,9 / ja              (mã 19..28)
		//     lea ecx,[eax+eax*4] / lea ecx,[ecx*8+0x4B7BC]   ; = 40*mã + 0x4B7BC
		// Nghĩa là ba hằng số *SlotListPointer dưới đây chính là 40*mã chứ không phải ba offset rời rạc:
		//     0x0000 = 40*0   túi chính
		//     0x0230 = 40*14  ô trang bị nhanh
		//     0x02F8 = 40*19  rương 2
		// Ba số này dò ra bằng probe ngày 2026-09-07 và chủ dự án đã đối chiếu tên/số lượng vật phẩm trong game.
		// Lưu ý mã container của AutoFS (3/11/16) KHÁC mã của client (0/14/19).
		public const int Object = 0x4B7BC;
		public const int SaleSlotListPointer = 0x0000;
		public const int SaleSlotCount = 35;
		// Túi chính trong gói mạng: room 0x21, lưới 5 cột, ô thứ c <-> (x = c % 5, y = c / 5). Đối chiếu 22/22 món trong túi
		// PID 21740 (2026-09-26) giữa mảng ô ở trên và toạ độ (+0xD0 room, +0xD4 x, +0xD8 y) trong danh sách vật phẩm của client.
		public const int MainBagRoom = 0x21;
		public const int MainBagColumns = 5;
		// Sửa 0x0140 -> 0x0230 ngày 2026-09-07. Bằng chứng: tool Debug "Probe dò container túi"
		// (BuildStamp INVENTORY-CONTAINER-20260907-01, PID 21184) cho thấy +0x0140 trỏ tới mảng Ids=[0,0,0,0]
		// trong khi +0x0230 ra đúng 4 ô: "Thanh Lộ" x1, "Trung Hồng đơn" x2, "Tiểu Hoàn đơn" x6,
		// "Hồi thành phù (Siêu cấp)" x1 — chủ dự án mở game đối chiếu và xác nhận khớp cả tên lẫn số lượng.
		public const int QuickSlotListPointer = 0x0230;
		public const int QuickSlotCount = 4;
		// Sửa 0x0208 -> 0x02F8 ngày 2026-09-07, cùng lượt probe trên. +0x0208 đọc ra toàn id rác
		// (629249068, 1667854396, ...) trong khi +0x02F8 ra đúng 35 ô với "Hoả Vũ" x250, "Hoàn Quan nhãn" x49,
		// "Đại địa nhãn" x44, "Tiền đồng" x16 — chủ dự án xác nhận đây là rương thứ 2 của túi đồ.
		public const int ExtendedSlotListPointer = 0x02F8;
		public const int ExtendedSlotCount = 35;
		public const int EquippedWeightItemIndex = 0x0BD78;
		// Sức lực mang đồ. Offset tính từ CON TRỎ Globals.StrengthRoot, KHÔNG phải từ Inventory.Object.
		//
		// Bốn hằng số cũ (FirstStrength 0x0FFC8, SecondStrength 0x0FE88, ThirdStrength 0x10090, MaximumStrength
		// 0x21DDC, đều tính từ Inventory.Object) đã bị xoá ngày 2026-09-23 vì ĐO RA LÀ SAI: đọc thử trên 6 client
		// thì ba cái đầu ra 0 ở mọi client, còn 0x21DDC ra 729 trên PID=19860 nhưng đó chỉ là một phần tử của dãy
		// tăng đều 719,718,720,719,721… và cùng offset đó ở 5 client kia ra -1 / số rác / 0. Không ai tham chiếu
		// chúng nên xoá không ảnh hưởng gì.
		//
		// Cặp đúng dò ra bằng lọc vi sai (2026-09-23, PID=23828 TiểuHồngĐơn): chủ dự án đọc trong game 318/515, quét
		// toàn tiến trình được 256 ô mang giá trị 318; đổi sức lực sang 299/515 rồi đọc lại đúng 256 ô đó thì CHỈ
		// MỘT ô đổi — 0x28079C9C: 318 -> 299 — và ô liền trước 0x28079C98 giữ 515. Bảng tĩnh không thể đổi giá trị
		// đúng lúc người chơi vứt đồ, nên phép lọc này loại sạch trùng hợp.
		public const int CurrentStrength = 0x027C;
		public const int MaximumStrength = 0x0278;
		// Số tiền (đơn vị XU, 1 vạn = 10.000 xu) hiện trong túi đồ. Cùng đối tượng với sức lực: tính từ CON TRỎ
		// Globals.StrengthRoot. Dò bằng lọc vi sai trên client thật (2026-09-24, PID=22612 MaiAnhNhe): bán 12 món
		// (SALE_COMPLETE Sold=12) ô này 1.351.038 -> 1.356.312 (+5.274), rồi sửa đồ ngay sau đó 1.356.312 -> 1.355.782 (-530).
		// Chủ dự án đối chiếu số hiển thị trong game với 6 account và xác nhận chính xác (đổi ra vạn = chia 10.000).
		public const int Money = 0x05F0;
		// The strength maximum and money Auto reads (InventoryStrengthReader / InventoryMoneyReader), offsets from the
		// Globals.InventoryRoot POINTER (not Inventory.Object). They replace StrengthRoot+0x278/+0x5F0, which stay 0 after a
		// fresh login until the bag/shop UI opens (2026-09-26 logs: 5 accounts read UNAVAILABLE for 20-40 minutes, MaiAnhNhe
		// looted to 33/35 slots meanwhile) and then freeze until it opens again. On XinLỗiEm right after relogin
		// (StrengthRoot = 0) these read 343 and 333.484, confirmed in game (221/343, 33v3484); on 3 more freshly logged
		// accounts the owner confirmed money and strength exact (2026-09-27).
		public const int LoginMaximumStrength = 0x5DE54;
		// Id of the last opened NPC shop, from the Globals.InventoryRoot pointer; -1 until a shop was opened this session.
		// The buy case of the operation dispatcher (VA 0x6C0C44) reads it to resolve a shop position.
		public const int OpenShopId = 0x4C600;
		public const int LoginMoney = 0x4B7C0;
		public const int ReceiptDecrement = 0x0FE2C;
		public const int ReceiptFirstIncrement = 0x0FE38;
		public const int ReceiptSecondIncrement = 0x0FE7C;
	}

	public static class Item {
		// Cập nhật sau bản update 2026-09-29 (cùng delta +0x24060 với EntityTable/ItemTable). Xác nhận trực tiếp trên
		// PID 18996 đang sống: RVA cũ đọc ra 0 (FAIL_NULL trong address-audit), RVA mới đọc ra con trỏ hợp lệ
		// 0x19B65024, cùng khuôn với các con trỏ khác đã PASS (InventoryRoot=0x135D0048, ItemTable=0x16ED2020).
		public const int GroundRecordTablePointer = 0x571D20;
		public const int GroundRecordStride = 0x3A4;
		public const int GroundRecordId = 0x14;
		public const int GroundRecordType = 0x18;
		public const int GroundRecordKind = 0x1C;
		public const int GroundName = 0x7C;
		public const int GroundQualityCodeA = 0xA0;
		public const int GroundInternalX = 0xA8;
		public const int GroundInternalY = 0xAC;
		public const int GroundQualityCodeB = 0xB0;
		public const int GroundMapObjectIndex = 0x28;
		public const int GroundMapSegmentIndex = 0x2C;
		public const int GroundTileX = 0x30;
		public const int GroundTileY = 0x34;
		public const int GroundSubTileX = 0x38;
		public const int GroundSubTileY = 0x3C;
		public const int MapObjectStride = 0x260;
		public const int MapSegmentCount = 0x210;
		public const int MapSegmentTable = 0x10;
		public const int MapSegmentStride = 0x1590;
		public const int MapSegmentBaseX = 0x178;
		public const int MapSegmentBaseY = 0x17C;
		public const int InventoryRecordStride = 0x1800;
		public const int InventoryName = 0x0704;
		public const int InventoryType = 0x076E;
		public const int InventoryClassField2A4 = 0x02A4;
		public const int InventoryClassField6D8 = 0x06D8;
		public const int InventoryClassField6DC = 0x06DC;
		public const int InventoryClassField6E0 = 0x06E0;
		public const int InventoryClassField700 = 0x0700;
		public const int InventoryClassFieldAA8 = 0x0AA8;
		public const int Name = 0x0704;
		public const int Weight = 0x06EC;
		public const int Quantity = 0x0754;
		public const int GroundStateA0 = 0xA0;
		public const int GroundStateA8 = 0xA8;
		public const int GroundStateAC = 0xAC;
		public const int GroundStateD8 = 0xD8;
		public const int GroundStateDC = 0xDC;
	}

	public static class Combat {
		public const int HpAt82C = 0x82C;
		public const int CurrentHp = 0xCE4;
		public const int MaximumHp = 0xCE8;
	}
}
