# Ghi chú địa chỉ game (`Utils/GameAddresses.cs`)

File này là phần chú thích của `Utils/GameAddresses.cs`. File code chỉ có hằng số, mọi ghi chú và bằng chứng nằm ở đây (chủ dự án chốt ngày 2026-10-15).

**Mục đích:** khi client update, dùng file này để biết mỗi địa chỉ được tìm ra bằng cách nào, nên dò lại theo hướng nào, và địa chỉ nào trước đây đã dịch cùng nhau.

## Thêm hoặc sửa địa chỉ

- Thêm hằng vào đúng lớp trong `GameAddresses.cs`, rồi thêm một dòng vào bảng tương ứng ở đây. Ghi: nguồn, ngày đo, bằng chứng, và trạng thái (đã xác nhận / chưa xác nhận).
- Phía native nằm ở `Native/SystemUint/GameClientAddresses.h`. Hằng nào có mặt ở cả hai file thì phải sửa cả hai.
- Viết RVA tính từ base `0x400000` của `Game.exe`. Ví dụ VA `0x8F2E10` thì RVA là `0x4F2E10`.

## Kinh nghiệm dò lại sau khi client update

Rút ra từ bản update ngày 2026-09-29. Lần đó `SizeOfImage` đổi `0x1F83000` → `0x1FA7000`, đo trên PID 9848.

- **Cụm A, lệch `+0x24060`:**
  - `EntityTable`, `ItemTable`, `GroundRecordTablePointer`, `InventoryRoot`, `MapCoordinateRoot`, `MapIdRuntimeMirror`, `ShopCatalog`;
  - các địa chỉ lấy theo chúng: `EquippedWeaponDurabilityProbe` (game root = `InventoryRoot`, item table).
- **Cụm B, lệch `+0x24068`:**
  - `MapId`, `MapIdMirror`, `ModalState`, `ShopState`, `ReturnToTownModal`, `ChatChannelManager`, `ChatChannelCodeTable`.
- **Không theo delta nào, phải dò riêng:**
  - Hàm: dùng Ghidra headless + BSim (Similarity 1.0), hoặc quét chữ ký byte là duy nhất trong ảnh.
  - Vtable: quét context-byte quanh literal cũ, rồi xác nhận bằng cách đọc sống đối tượng thật.
- **Chỉ coi là đã xác nhận khi đọc sống ra dữ liệu có nghĩa.** Khớp offset thì chưa đủ.
  - Ví dụ: đi trọn chuỗi `InventoryRoot → [+0x41D3C] = chỉ số nhân vật → EntityTable + index × 0xD87C` trên PID 19040, ra `player_idx = 1`, HP 511/667, cả 3 bản sao MapId cùng ra 35.
  - Client đang ở màn chọn nhân vật đọc ra 0 là bình thường, không phải bằng chứng RVA sai.

## Gốc

| Hằng | Giá trị | Ghi chú |
|---|---|---|
| `ModuleName` | `Game.exe` | |
| `ModuleMaximumSize` | `0x1FA7000` | SizeOfImage sau bản update 2026-09-29. `RuntimeLayoutResolver.IsCurrentUpdateClient` dùng nó để nhận diện đúng bản client. |

## `Globals`: con trỏ và ô nhớ toàn cục (RVA)

| Hằng | Giá trị | Ghi chú |
|---|---|---|
| `EntityTable` | `0x73A0C4` | Cụm A. Xác nhận sống trên PID 19040 qua chuỗi InventoryRoot (xem phần Kinh nghiệm). |
| `CurrentTargetIndex` | `0x4F2674` | Sửa ngày 2026-09-17: giá trị cũ `0x4CB668` không có lệnh nào đọc/ghi trong cả 33 MB, và đọc ra giống hệt nhau trên 6 tiến trình. Ô `0x4CE688` thì ra −1 khi không chọn mục tiêu, ra 43 khi vừa click Đại Phu (entity 43 đúng tên "Đại phu"). Hàm ghi vào ô này: RVA `0x2CB070` (`cmp eax,0x1FF / mov [..],eax`). Sau bản 2026-09-29: PID 19040 có mục tiêu đọc ra 99, 5 client khác ra −1. |
| `InventoryRoot` | `0xAA3E84` | Cụm A. Gốc túi đồ, cũng là "game root" trong probe độ bền cũ. |
| `ItemTable` | `0x5650C8` | Cụm A. |
| `MagicAttributeDescriptorTable` | `0x4F2E10` | Bảng mô tả dòng thuộc tính (VA `0x8F2E10`, ảnh ngày 2026-10-09). `0xA4` byte mỗi id: `+0` con trỏ khoá (vd `"attackspeed_v"`), `+4` mẫu tooltip TCVN3 (vd `"Xuất chiêu (Vũ khí):#d1+%"`). Thứ tự id theo `[Descript]` trong `2008settings.pak` mục `93F6768D` (`skill_begin = 0`, `attackspeed_v = 115`). |
| `AttackManager` | `0x4E0640` | Không nơi nào dùng (address-audit ghi `UNUSED_NO_RULE`). Đây là RVA của client đời "recovery", chưa xác nhận lại. Bản hiện tại dùng `Functions.AttackManagerPointer`. |
| `MapCoordinateRoot` | `0xA1E0E0` | Cụm A. Probe độ bền dùng nó làm bảng class state. |
| `CombatTargetRoot` | `0x3A95D8` | **CHƯA xác nhận lại sau 2026-09-29.** Quét immediate 4-byte không ra ứng viên. Address-audit báo `FAIL_POINTER_UNREADABLE`. |
| `MapId` / `MapIdMirror` | `0x527B84` / `0x527B88` | Cụm B. |
| `MapIdRuntimeMirror` | `0x53CBC8` | Cụm A. Ba bản MapId phải đồng thuận thì subsystem Map mới được coi là sẵn sàng. |
| `ModalState` | `0x512FF0` | Cụm B. Lúc dò, chưa đối chứng được khi modal đang mở. Sau này `ANTI_AFK_STUCK_SUPERVISOR_ESC` đọc ra `0x00922DC0` lúc popup chết đang mở. |
| `DialogPointer` | `0x500BE0` | **CHƯA xác nhận lại.** Chỉ DebugTools dùng (`ShopRepairDebugCommand`, `ModalVtableProbe`). |
| `ShopState` | `0x513740` | Cụm B. Bằng 2 khi shop NPC đang mở (`NpcShopReader.ShopOpenState`). |
| `ShopCatalog` | `0xABD074` | Đối tượng BuySell của client: `+0` con trỏ các dòng (mỗi shop id một `int*`), `+4` bản ghi vật phẩm (stride `Item.InventoryRecordStride`), `+8` số vị trí tối đa mỗi dòng, `+0xC` số dòng, `+0x10` số bản ghi (xem lớp `Shop`). Đo trên PhâyKer ngày 2026-09-27: 61 dòng, 46 vị trí, 1026 bản ghi; shop 14 (Đại Phu map 32) = Tiểu/Trung Hồng đơn, Tiểu/Trung Hoàn đơn. Sau bản 2026-09-29 đọc lại trên PID 19040, khớp hoàn toàn. |
| `ReturnToTownModal` | `0x522DC0` | Cụm B. Bắt sống lúc PID 26680 chết thật, đối chứng chéo bằng vtable. Trước đây giá trị sai làm `HandleDeathPopup` không bao giờ nhận ra popup chết. |
| `StrengthRoot` | `0x5010D4` | Con trỏ tới khối sức lực (xem `Inventory.CurrentStrength`). Trong 65 con trỏ trỏ vào khối, chỉ con trỏ này đọc ra cặp hợp lệ trên cả 6 client. Con trỏ kia (RVA `0x2170384`) ra 0 hoặc rác. Đo ngày 2026-09-23, ví dụ PID 23828 = 299/515, khớp số trong game. Khối này bằng 0 sau khi vừa đăng nhập, cho tới lúc mở túi/shop, nên Auto đọc `Inventory.LoginMaximumStrength` thay thế. |
| `ChatChannelManager` | `0x5191DC` | Cụm B (`0x4F5174` → `0x5191DC`). Ra Count = 7 hợp lệ trên PID 17268/20872. Thử cụm A thì ra con trỏ rác. Cũng là gốc để đọc tin nhắn đến (`Chat.InboxDirectList`) và lịch sử tin đã gửi (`Chat.InputControl`). |
| `ChatChannelCodeTable` | `0x519220` | Cụm B. Xác nhận sống trên PID 17268: 4 mã kênh (Cận / Giao / Khu vực / Lãnh địa) khớp từng byte. |
| `LoginNoticeDialog` / `LoginVersionDialog` / `LoginServerDialog` | `0x522D7C` / `0x512D38` / `0x5287E4` | Hộp thoại màn đăng nhập, client 1.34 (2026-10-10). Nguồn chuẩn ở `GameClientAddresses.h`. Lệnh 294 trả 3 khi hộp còn sống. |
| `MouseInputObject` | `0x5228B8` | Đối tượng nhận chuột của client, dùng cho giả lập click (mở shop NPC, click Đại Phu). Quét context-byte chỉ 3/30 phiếu đồng thuận, nhưng đọc sống trên PID 26680 ra `0x00889F20`, đúng bằng `Vtables.MouseInputObject`. |
| `MouseInputClientX` / `MouseInputClientY` | `0x3BACA4` / `0x3BACA8` | Toạ độ chuột client mà handler đọc. |

## `Functions`: địa chỉ hàm (RVA)

| Hằng | Giá trị | Ghi chú |
|---|---|---|
| `AttackManagerPointer` | `0x5066C8` | Ô con trỏ tới attack manager. Đọc sống trên PID 18996: `[0x5066C8] = 0x8F06A8`, `[0x8F06A8] = 0x899988` (= `Vtables.AttackManager`). |
| `GroundCoordinateConverter` | `0x300E10` | Bản trước là `0x2FBC30`. Chữ ký 12 byte `GroundCoordinateConverterSignature` là duy nhất trong cả ảnh cũ lẫn mới. |
| `Movement` | `0x324240` | Bản trước là `0x31EF70`. Chữ ký 9 byte `55 8B EC 81 EC 4C 04 00 00` là duy nhất. |
| `Pickup` | `0x3B1510` | BSim: VA cũ `0x7AC300` khớp duy nhất `FUN_007b1510` (Similarity 1.0, Significance 124). Cả hai hàm cùng nhúng literal InventoryRoot ở cùng vị trí lệnh. |
| `RepairConfirmation` | `0x297D60` | Ghidra + BSim (Similarity 1.0), khớp `RepairConfirmationSignature`. Hàm xác nhận "sửa toàn bộ". |
| `MouseHandler` | `0x184530` | BSim: VA cũ `0x5836D0` khớp duy nhất `FUN_00584530` (Similarity 1.0, Significance 357.3). Đối chứng: slot `+0x10` của vtable mới cũng trỏ đúng hàm này. |
| `ReturnToTown` | `0x1A8C90` | BSim (Similarity 1.0, Significance 25.7). Nguồn chuẩn: `GameClientAddresses.h::ReturnToTownFunctionRva`. |

## `Vtables`

| Hằng | Giá trị | Ghi chú |
|---|---|---|
| `AttackManager` | `0x499988` | Quét 2 điều kiện cùng lúc: `[V+0x40]` = ATTACK mới VÀ `[V+0x48]` = SELECT_GROUND_ITEM mới. Chỉ ra đúng 1 vị trí trong ảnh 33 MB. |
| `AttackMethodSlot` / `CoordinateDispatcherMethodSlot` | `0x40` / `0x10` | Slot method trong vtable attack manager: hàm đánh, và hàm điều phối toạ độ (opcode `0x9F`, di chuyển có cờ). |
| `ChatInputControl` | `0x479988` | Vtable ô nhập chat (VA `0x00879988`), giống nhau trên PID 4832/6596/12520 (2026-10-13). |
| `DoctorConfirmModal` | `0x48CF3C` | Popup xác nhận của menu NPC (Đại Phu). Bản trước là `0x46AF3C`. Phải khớp `GameClientAddresses.h::NpcConfirmModalVtableRva` (context-byte 14/14, và bắt sống đối tượng thật). |
| `MouseInputObject` | `0x489F20` | Bản cũ là literal `0x00867F20`. 4 cửa sổ context-byte độc lập đều cho đúng 1 vị trí. |
| `ReturnToTownObject` | `0x48B6E0` | Vtable đối tượng popup Về thành. Nguồn chuẩn ở `GameClientAddresses.h`. |
| `NpcConfirmModal` | `0x469E34` | Giá trị **cũ** mà `ModalVtableProbe` còn in ra để đối chiếu. Bản hiện tại là `DoctorConfirmModal`. |
| `RepairConfirmModal` | `0x4952BC` | Bản trước là `0x4732BC`. Xem `GameClientAddresses.h::RepairConfirmModalVtableRva`. |

## `LegacyClients`: client cũ

`RuntimeLayoutResolver` dùng để nhận diện đời client. "Recovery" là bản có ảnh ≥ `0x1FC0000`, "Old" là bản trước nữa. Không dùng cho client hiện tại; giữ lại để nhánh nhận diện phiên bản vẫn chạy được.

| Hằng | Giá trị |
|---|---|
| `RecoveryMinimumImageSize` | `0x1FC0000` |
| `RecoveryEntityTable` | `0x95DF40` |
| `RecoveryGroundRecordTablePointer` | `0x54BCA0` |
| `RecoveryAttackManagerPointer` / `RecoveryAttackManagerVtable` | `0x4E0640` / `0x476618` |
| `RecoveryGroundCoordinateConverter` / `RecoveryMovement` / `RecoveryPickup` | `0x2F9E00` / `0x31CE80` / `0x3AA0F0` |
| `OldAttackManagerPointer` / `OldAttackManagerVtable` | `0x4DF600` / `0x4755F8` |
| `OldGroundCoordinateConverter` / `OldMovement` / `OldPickup` | `0x2F9DA0` / `0x31CDE0` / `0x3A9C50` |

## `Entity`: bản ghi entity (offset tính từ đầu entity)

| Hằng | Giá trị | Ghi chú |
|---|---|---|
| `PlayerIndex` | 1 | Ô entity của nhân vật mình. |
| `ReversedPlayerIndex` | `0x353B` | Hằng cũ của `MemoryLayout.PlayerIndex`, ghi chú gốc là "tạm hardcode theo kết quả reverse". Không còn nơi nào dùng. |
| `FirstScanIndex` / `LastScanIndex` | 2 / 511 | Biên quét bảng entity dùng chung cho mọi nơi; cả hai là chỉ số hợp lệ nên vòng lặp dùng `<=`. Nâng 256 → 511 ngày 2026-09-11. Trần 256 chép từ AutoFS (`WindowQueue.cs:24241`) là sai: với trần đó Auto chỉ thấy 15/138 entity có tên, NPC Hoàng Thiên Hóa ở #272. Từ #513 trở đi toàn rác. Xem memory "bảng entity có 512 ô". |
| `Stride` | `0xD87C` | |
| `ReadSize` | `0x70B8` | Số byte mỗi lần đọc một entity (`EntityFinder`, `MonsterFinder`). Phải lớn hơn offset lớn nhất được đọc. |
| `Handle` / `SlotIndex` / `PrimaryPointer` / `SecondaryPointer` / `MirrorIndex` / `ActiveFlag` | `0x00` / `0x04` / `0x0C` / `0x10` / `0x14` / `0x18` | |
| `ClassPointer` | `0x08` | Tên này có thể sai: giá trị đọc ra (`0x223C`) không phải con trỏ (ghi chú trong `PetHealthReader`). |
| `OwnerCharacterId` | `0x08` | Mã nhân vật; Đệ mang mã chủ trùng với số này. |
| `PetOwnerCharacterId` | `0x429C` | Mã chủ của Đệ (entity type 6). Đã verify cả khi Đệ chết rồi gọi lại: chỉ số entity nhảy từ #5 sang #54 mà mã chủ vẫn giữ nguyên. |
| `Type` / `Level` | `0x28` / `0x24` | |
| `LifecycleStatus` / `PlayerDeathStatus` | `0x1E8` | Cùng ô. Trạng thái 6 = Finished. Nhân vật chết thì Status 7. |
| `PlayerDeathState` | `0x1EC` | |
| `Hp` / `MaxHp` / `Mp` / `MaxMp` | `0x27D0` / `0x27D4` / `0x27DC` / `0x27E0` | |
| `Name` | `0x2D70` | |
| `RawX` / `RawY` | `0x434C` / `0x4350` | Nguồn toạ độ còn sống khi ba nguồn bất đồng (đo trên PID 3380 lúc bị kẹt). |
| `RawXMirror` / `RawYMirror` | `0x71EC` / `0x71F0` | Bản sao. Có lúc lạc (vd 400/133 ở thành ngày 2026-10-15). |
| `RawXLegacy` / `RawYLegacy` | `0x75F4` / `0x75F8` | Layout AutoFS cũ dịch +4 (`DEV-CLIENT-UPDATE-001.lua:14-15`). Chỉ `ClientFreezeWatch` dùng để đối chiếu ba nguồn. |
| `MoveTargetXCandidate` / `MoveTargetYCandidate` | `0x2CDC` / `0x2CE0` | Ứng viên đích di chuyển mà client đang giữ. |
| `MovementDestinationX` / `MovementDestinationY` | `0x42D4` / `0x42D8` | Đích của lệnh di chuyển, xác nhận runtime ngày 2026-08-27: chỉ đổi khi có lệnh mới, click giao diện không ghi vào. |
| `AnchorXCandidate` / `AnchorYCandidate` | `0x42E8` / `0x42EC` | Ứng viên, chưa xác nhận. |

## `Attack`: trường của attack manager

| Hằng | Giá trị |
|---|---|
| `ResetCommand` / `TargetIndexCommand` / `CommandActive` | `0xD794` / `0xD798` / `0xD7A0` |
| `ColdStartMethodVtable` / `PrepareMethodVtable` | `0x38` / `0x6C` |

## `Inventory`: offset từ `InventoryRoot`, hoặc từ `Inventory.Object` / `StrengthRoot` khi có ghi rõ

| Hằng | Giá trị | Ghi chú |
|---|---|---|
| `QuickBuyDailyCount` | `0x39CCC` | Bộ đếm mua nhanh trong ngày (u16). Hàm mua của client (RVA `0x346010`) so với giới hạn 1000 (VA `0xE99004`). Đo trên 6 client ngày 2026-09-25 ra 34..413. |
| `QuickBuyBlockFlag` | `0x4B79C` | Hàm mua thoát ngay khi ô này ≠ 0 (disassembly ở `0x746082`). Chưa biết ý nghĩa. |
| `Object` | `0x4B7BC` | Gốc **mảng** container, mỗi phần tử 40 byte, đánh chỉ số theo mã container. Client chỉ nhận mã 0, 14, 19..28 (dịch ngược hàm điều phối RVA `0x3714D0`). Mã container của AutoFS (3/11/16) khác mã của client (0/14/19). |
| `SaleSlotListPointer` / `SaleSlotCount` | `0x0000` / 35 | Túi chính = 40 × 0. |
| `MainBagRoom` / `MainBagColumns` | `0x21` / 5 | Túi chính trong gói mạng: ô c ↔ (x = c % 5, y = c / 5). Đối chiếu 22/22 món trên PID 21740. |
| `QuickSlotListPointer` / `QuickSlotCount` | `0x0230` / 4 | Ô trang bị nhanh = 40 × 14. Sửa từ `0x0140` ngày 2026-09-07; chủ dự án đối chiếu tên và số lượng trong game. |
| `ExtendedSlotListPointer` / `ExtendedSlotCount` | `0x02F8` / 35 | Rương 2 = 40 × 19. Sửa từ `0x0208` ngày 2026-09-07. |
| `EquippedWeightItemIndex` | `0x0BD78` | |
| `CurrentStrength` / `MaximumStrength` | `0x027C` / `0x0278` | Tính từ **con trỏ** `StrengthRoot`. Dò bằng lọc vi sai: đổi 318 → 299 thì chỉ đúng 1 ô đổi. |
| `Money` | `0x05F0` | Từ `StrengthRoot`, đơn vị xu. Lọc vi sai bán/sửa trên PID 22612; chủ dự án đối chiếu 6 account. |
| `LoginMaximumStrength` / `LoginMoney` | `0x5DE54` / `0x4B7C0` | Từ `InventoryRoot`. Đọc đúng ngay sau khi đăng nhập (`StrengthRoot` lúc đó bằng 0). Chủ dự án xác nhận ngày 2026-09-27. |
| `OpenShopId` | `0x4C600` | Id shop NPC mở gần nhất, −1 nếu chưa mở. Nhánh mua của dispatcher (VA `0x6C0C44`) đọc ô này. |
| `ReceiptDecrement` / `ReceiptFirstIncrement` / `ReceiptSecondIncrement` | `0x0FE2C` / `0x0FE38` / `0x0FE7C` | |

## `Shop`: trường của `Globals.ShopCatalog`

| Hằng | Giá trị |
|---|---|
| `CatalogRecords` / `CatalogPositionsPerRow` / `CatalogRowCount` / `CatalogRecordCount` | `0x04` / `0x08` / `0x0C` / `0x10` |

## `Item`: vật phẩm dưới đất, bản ghi vật phẩm, thuộc tính

| Hằng | Giá trị | Ghi chú |
|---|---|---|
| `GroundRecordTablePointer` | `0x571D20` | Cụm A. Xác nhận trên PID 18996: RVA cũ đọc ra 0, RVA mới ra con trỏ hợp lệ. |
| `GroundRecordStride` / `GroundRecordId` / `GroundRecordType` / `GroundRecordKind` | `0x3A4` / `0x14` / `0x18` / `0x1C` | |
| `GroundName` / `GroundQualityCodeA` / `GroundQualityCodeB` | `0x7C` / `0xA0` / `0xB0` | Vùng tên là `0x7C..0x9F`. |
| `GroundInternalX` / `GroundInternalY` | `0xA8` / `0xAC` | |
| `GroundMapObjectIndex` … `GroundSubTileY` | `0x28` … `0x3C` | |
| `SpriteCoordinateBeforeSprite` / `SpriteGroundTypeBeforeCoordinate` | `0x20` / `0x29C` | Dùng cho `SpriteItemScanner`: toạ độ nằm trước chuỗi sprite, loại vật phẩm nằm trước toạ độ. |
| `MapObjectStride` / `MapSegmentCount` / `MapSegmentTable` / `MapSegmentStride` / `MapSegmentBaseX` / `MapSegmentBaseY` | `0x260` / `0x210` / `0x10` / `0x1590` / `0x178` / `0x17C` | |
| `InventoryRecordStride` | `0x1800` | Bản ghi trong `ItemTable`. |
| `InventoryName` / `Name` | `0x0704` | |
| `InventoryType` / `Weight` / `Quantity` | `0x076E` / `0x06EC` / `0x0754` | |
| `InventoryClassField2A4/6D8/6DC/6E0/6FC/700/A78/A7C/AA4/AA8` | theo tên | Các trường phân loại. `6D8`/`6DC`/`6E0` = loại/chi tiết/cụ thể, dùng cho lọc bán và khối link vật phẩm. `6FC`, `A78`, `A7C`, `AA4` là byte 7/8/14/21 của khối link chat; `A78`/`A7C` cùng giá trị trên 3 món đã đo, chưa rõ trường nào là gì. |
| `UniqueId` / `AttributeLevels` / `Seed` | `0x0A58` / `0x0A5C` / `0x17F0` | Mã riêng của món (byte 31 của link), 6 cấp thuộc tính (byte 15..20), seed (byte 9..12). Đo trên PID 4832 ngày 2026-10-13, 3 món khớp đủ 152 byte. |
| `MagicAttributeArray` / `MagicAttributeStride` / `MagicAttributeMaximumCount` | `0x02A4` / `0x34` / 6 | Mỗi dòng: `{int id, 4 × (value, a, b)}`, tooltip hiện value đầu. Verify ngày 2026-10-09 khớp tooltip, ví dụ "Phục Thế phủ" id 115 value 10 = "Xuất chiêu (Vũ khí): +10%". |
| `MagicAttributeDescriptorStride` / `MagicAttributeDescriptorTemplate` | `0xA4` / `0x04` | Xem `Globals.MagicAttributeDescriptorTable`. |
| `GroundStateA0/A8/AC/D8/DC` | theo tên | |

## `Durability`: độ bền trang bị (`EquippedWeaponDurabilityProbe`, `WeaponRepairMonitor` gọi vào)

| Hằng | Giá trị | Ghi chú |
|---|---|---|
| `EquipmentManager` | `0x41D44` | Từ `InventoryRoot`. Trên PID 17268, `[root+0x41D44] = 1`, trùng chỉ số nhân vật đọc ở `[InventoryRoot+0x41D3C]`. |
| `EquipmentSlotArray` / `EquipmentSlotStride` | `0x0C` / `0x08` | 12 ô trang bị. |
| `Bias` | `0x0AA4` | |
| `CharacterStride` / `CharacterClassRecord` / `AlternateEquipmentState` | `0x35F60` / `0xBDDC` / `0x168E4` | |
| `ClassRelationOffset` | `0x27B8` | Bảng là `Globals.EntityTable`, stride `Entity.Stride`. Thử áp delta cụm A cho `0x95FF60` thì ra NULL; dùng thẳng EntityTable thì cho ClassStateIndex ổn định trên 4 tiến trình. |
| `ClassStateStride` / `ClassStateOffset` | `0x260` / `0x08` | Bảng là `Globals.MapCoordinateRoot`. |
| `Legacy*` | | Hằng của client cũ (`ItemRecordStride 0x17C0`, game root `0x40EE1C`…) mà nhánh dò cũ của probe còn dùng. Không áp dụng cho client hiện tại. |

## `NpcMenu`: đối tượng menu NPC (từ `Globals.ModalState`)

| Hằng | Giá trị | Ghi chú |
|---|---|---|
| `TextOffset` / `OptionStride` | `0x7EC` / `0x69C` | Bố cục menu NPC hiện tại: 8 mục. Mục cuối nằm khoảng `+0x3C1C`. Thám quân, Đại Phu và sửa đồ cùng dùng. Riêng menu Di ngoại phù đọc theo bố cục này thì ra rác (quest.log 2026-09-09 22:18:59), nên menu phù dùng bảng chép tay `TalismanDestinationCatalog`. |
| `AutoFsListPointer` / `DirectOptionBase` / `AutoFsOptionStride` / `OptionCount` / `DirectText` / `AutoFsText` / `TextLength` | `0x1B4` / `0x15C` / `0x57C` / `0x35DC` / `0x454` / `0x5B0` / `0x554` | Bố cục kiểu AutoFS mà `NpcMenuOptionCapture` (tab Debug) dò. Code chạy thật không dùng. |

## `MouseInput`

| Hằng | Giá trị | Ghi chú |
|---|---|---|
| `LeftState` | `0xFC` | Trạng thái nút trái trong `Globals.MouseInputObject`. |

## `Chat`

| Hằng | Giá trị | Ghi chú |
|---|---|---|
| `ChannelCount` / `ChannelList` / `ChannelEntryStride` / `ChannelNameIndex` | `0x8E4C` / `0x8E50` / `0x2818` / `0x08` | Danh sách kênh trong `ChatChannelManager`. |
| `ChannelCodeStride` | `0x264` | Bảng `ChatChannelCodeTable`. |
| `InputControl` | `0x438` | `[ChatChannelManager] + 0x438` = ô nhập chat (vtable `Vtables.ChatInputControl`). |
| `HistoryEntries` / `HistoryEntryStride` / `HistoryNextSlot` / `HistoryLengths` | `0x25E` / `0x200` / `0x25D` / `0x1260` | Lịch sử 8 tin đã gửi: tin i nằm ở `control + 0x25E + i × 0x200`, độ dài ở `control + 0x1260 + i × 4`. Đọc lại chính xác "AUTOMARK-A" = 10, kèm link vật phẩm = 162, kèm link map = 42 (PID 4832, 2026-10-13). |
| `InboxDirectList` | `0x19DC` | `[[ChatChannelManager] + 0x19DC]` là vùng chứa con trỏ tới các bản ghi tin nhắn đến. Client PID 24192: các bản sao ở `+0xE0`/`+0xEC` và `+0x20C`/`+0x210`. Đọc 0x800 byte, ổn định (`LầnĐọcHỏngXen=0`). Chưa thử qua đăng nhập lại. |
| `InboxListPath` | `0x22C → 0x73C → 0x47C` | Đường cũ, tìm trên PID 12972. Đường này chập chờn (có lần 55 lần đọc liền hỏng) và **không còn dùng được trên client mới** PID 24192. Vẫn đọc kèm để dự phòng. |
| `InboxSender` / `InboxSenderLength` / `InboxLinePrefix` | `0x04` / `0x20` / `0x48` | Bản ghi tin: `+0` số dòng hiển thị (1..16, không phải luôn bằng 1), `+4` tên người gửi, `+0x48` chuỗi màu `2B 01 …` kết thúc bằng `03 00 00 00 FF`, rồi `Tên:` `02 04` nội dung. Thông báo hệ thống có chuỗi màu `13 01` nên bị loại. |

## `Combat`

| Hằng | Giá trị | Ghi chú |
|---|---|---|
| `HpAt82C` / `CurrentHp` / `MaximumHp` | `0x82C` / `0xCE4` / `0xCE8` | Trường trong `CombatTargetRoot`; gốc này chưa xác nhận lại (xem `Globals`). |
