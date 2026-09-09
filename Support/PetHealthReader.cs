namespace Auto.Support;

using Auto.Attack;
using Auto.Runtime;
using Auto.Utils;

internal sealed record PetHealthReading(bool Success, bool Present, int EntityIndex, int CurrentHp, int MaximumHp, string Detail) {
	public static PetHealthReading Missing(string detail = "") => new(true, false, 0, 0, 0, detail);
	public static PetHealthReading Fail(string detail = "") => new(false, false, 0, 0, 0, detail);
}

internal static class PetHealthReader {
	private const int PetEntityType = 6;

	// Mã nhân vật của CHỦ con Đệ, và mã nhân vật của chính mình. Bằng nhau tức là Đệ của mình.
	//
	// Trước đây hàm này bail ra ngay khi có nhiều hơn một entity type 6, nên Buff Đệ tắt hẳn ở bãi đông:
	// 31/31 dòng nhả quyền trong Release\Diagnostics\buff.log ngày 2026-09-07 đều là ca đó.
	//
	// Hai offset dưới đây dò ra bằng DebugTools/PetOwnerProbe.cs (BuildStamp PET-OWNER-20260908-03/04) trên
	// PID 21184, nhân vật TiểuHồngĐơn, Đệ được chủ dự án xác nhận là entity #164 "Hỏa Lôi Tế" Lv10 MaxHp=549:
	//   lần chạy 9 Đệ — #164 mang 8764 tại +0x429C, tám con kia mang 14827..17926
	//   lần chạy 7 Đệ — #164 mang 8764 tại +0x429C, sáu con kia mang 14827..15596
	// và 8764 = 0x223C đúng bằng giá trị tại +0x08 của bản ghi nhân vật ở cả hai lần (hex header ...3C220000...).
	// Mỗi con Đệ một số riêng cùng dải, nên đây là MÃ NHÂN VẬT chứ không phải cờ 0/1 — khớp giá trị động thì
	// khó trùng ngẫu nhiên hơn hẳn hai ứng viên cờ boolean +0xFC và +0x577C cũng sống qua hai lần chạy đó.
	//
	// VERIFIED 2026-09-08 bằng harness scratchpad gọi thẳng hàm này qua reflection trên 6 client đang chạy thật.
	// Mỗi client đều tách ra ĐÚNG MỘT con Đệ khớp mã chủ, giữa 3..29 con Đệ hợp lệ:
	//   PID 21184 TiểuHồngĐơn  mã 8764  | 7 Đệ  | chọn #34  376/549
	//   PID 22292 1đêm2nháy    mã 45864 | 3 Đệ  | chọn #26  192/260
	//   PID  8536 PhâyKer      mã 20592 | 29 Đệ | chọn #84  321/327
	//   PID 14236 ZALO0988...  mã 20590 | 28 Đệ | chọn #135 292/292
	//   PID 21768 XinLỗiEm     mã 20597 | 29 Đệ | chọn #113  84/270
	//   PID 22056 MaiAnhNhe    mã 20591 | 29 Đệ | chọn #105 290/300
	// Bốn account 8536/14236/21768/22056 đứng cùng bãi nên nhìn thấy Đệ của nhau, và mã chủ khớp đúng chiều:
	// mã 20592 của PhâyKer xuất hiện đúng một lần trong bảng entity của cả ba client kia, tương tự 20590/20591/
	// 20597. Hai account ở bãi khác không hề thấy mã của nhóm này. Mạng khớp chéo đó loại trừ trùng ngẫu nhiên.
	//
	// Ca Đệ chết rồi gọi lại cũng đã verify cùng ngày trên PID 22292: chỉ số entity nhảy #5 -> #54 nhưng mã chủ
	// 45864 giữ nguyên nên vẫn tách đúng một con (giữa 7 rồi 5 rồi 8 entity type 6). Bộ lọc không dùng chỉ số
	// nên việc entity dời ô không ảnh hưởng.
	//
	// Lưu ý cặp HP đọc ngay sau khi gọi Đệ ra có thể chưa phải số thật: cùng entity #54 cùng Lv5 đọc ra 150/150
	// lúc vừa gọi rồi 90/275 sau đó. Guard currentHp > maximumHp ở dưới đã loại được cặp mâu thuẫn, nhưng cặp
	// tạm mà vẫn hợp lệ thì lọt, và một nhịp Buff Đệ có thể tính phần trăm trên MaxHp sai. Chưa đo được cửa sổ
	// này dài bao lâu nên chưa xử lý — ghi lại để khỏi tưởng là lỗi bộ lọc mã chủ.
	//
	// Lưu ý +0x08 đang được GameAddresses.Entity đặt tên ClassPointer. Giá trị đọc được là 0x223C, không phải
	// con trỏ hợp lệ, nên cái tên đó nhiều khả năng sai — không đổi ở đây vì ngoài phạm vi việc đang làm.
	private const int OwnerCharacterId = 0x0008;
	private const int PetOwnerCharacterId = 0x429C;

	// Đọc HP Đệ từ entity type 6 bằng chính entity layout đã xác nhận của client hiện tại.
	public static PetHealthReading Read(int processId) {
		try {
			using MemoryReader reader = new(processId);
			RuntimeLayout layout = RuntimeLayoutResolver.Resolve(processId);
			if (! layout.Get(RuntimeSubsystem.Entity).Available) return PetHealthReading.Fail("Entity layout chưa sẵn sàng.");
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			IntPtr entityTable = reader.ReadPointer32(IntPtr.Add(moduleBase, layout.EntityTableRva));
			if (entityTable == IntPtr.Zero) return PetHealthReading.Fail("EntityTable bằng 0.");
			int ownerId = reader.ReadInt32(IntPtr.Add(IntPtr.Add(entityTable, layout.PlayerRecordOffset), OwnerCharacterId));
			if (ownerId == 0) return PetHealthReading.Fail("Không đọc được mã nhân vật tại +0x08.");

			List<PetHealthReading> candidates = [];
			int foreignPets = 0;
			for (int index = AutoFsClientProfile.FirstEntityIndex; index <= AutoFsClientProfile.LastEntityIndex; index++) {
				IntPtr entity = IntPtr.Add(entityTable, index * layout.EntityStride);
				if (reader.ReadInt32(IntPtr.Add(entity, AutoFsClientProfile.EntityType)) != PetEntityType) continue;
				if (reader.ReadInt32(IntPtr.Add(entity, AutoFsClientProfile.LifecycleStatus)) == AutoFsClientProfile.FinishedStatus) continue;
				int currentHp = reader.ReadInt32(IntPtr.Add(entity, layout.HpOffset));
				int maximumHp = reader.ReadInt32(IntPtr.Add(entity, layout.MaxHpOffset));
				if (currentHp <= 0 || maximumHp <= 0 || currentHp > maximumHp) continue;
				// Đệ của người khác bị loại ở đây thay vì làm cả hàm bail như trước.
				int petOwnerId = reader.ReadInt32(IntPtr.Add(entity, PetOwnerCharacterId));
				if (petOwnerId != ownerId) {
					foreignPets++;
					continue;
				}
				candidates.Add(new PetHealthReading(true, true, index, currentHp, maximumHp, $"EntityType={PetEntityType}; EntityIndex={index}; OwnerId={ownerId}; Source=OWNER_ID_MATCH."));
			}
			// Không con nào mang mã chủ của mình = coi như không có Đệ, để luồng Buff Đệ nhả quyền sạch sẽ.
			// Nếu offset mã chủ sai thì nhánh này chạy suốt: Buff Đệ không kích, nhưng nhân vật KHÔNG bị treo.
			if (candidates.Count == 0) return PetHealthReading.Missing($"Không entity type 6 nào mang mã chủ {ownerId}; đã bỏ qua {foreignPets} Đệ của người khác.");
			// Chủ dự án xác nhận 2026-09-08: mỗi nhân vật chỉ có ĐÚNG MỘT Đệ. Đây là luật của game, không đọc ra
			// được từ code, nên ghi lại ở đây. Vì vậy hai con cùng mã chủ là chuyện KHÔNG được phép xảy ra: hoặc
			// +0x429C không còn là mã chủ sau một bản client mới, hoặc bảng entity còn bản ghi cũ chưa dọn. Cả hai
			// đều là tín hiệu phải điều tra chứ không phải trạng thái bình thường, nên trả Fail thay vì đoán bừa.
			// Tính tới 22:48 ngày 2026-09-08, 1294 lượt buff Đệ trong Release\Diagnostics\buff.log chưa lần nào
			// chạm nhánh này.
			if (candidates.Count > 1) return PetHealthReading.Fail($"Có {candidates.Count} entity type 6 cùng mang mã chủ {ownerId}; mã chủ không tách được duy nhất.");
			return candidates[0];
		} catch (Exception ex) {
			return PetHealthReading.Fail($"{ex.GetType().Name}: {ex.Message}");
		}
	}
}
