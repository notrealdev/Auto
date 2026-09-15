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
	// Chỉ số giả để TryReadPetAt báo "là Đệ nhưng của người khác", tách khỏi "không phải Đệ".
	private const int ForeignPetMarker = -1;
	// Ô entity của Đệ ở lần tìm thành công gần nhất, khoá theo tiến trình. ConcurrentDictionary vì nhiều account
	// được TickOne xử lý song song (AccountListViewModel dùng Parallel.ForEach).
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> cachedPetIndexByProcess = new();

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

	// Khối đầu bản ghi entity, gom hai trường phải đọc cho MỌI entity: EntityType (0x0028) và LifecycleStatus
	// (0x01E8). Suy ra từ AutoFsClientProfile nên đổi offset bên đó là ở đây tự theo.
	private const int HeaderBase = AutoFsClientProfile.Level;
	private const int HeaderTypeOffset = AutoFsClientProfile.EntityType - HeaderBase;
	private const int HeaderStatusOffset = AutoFsClientProfile.LifecycleStatus - HeaderBase;
	private const int EntityHeaderSize = HeaderStatusOffset + sizeof(int);

	// Đọc HP Đệ từ entity type 6 bằng chính entity layout đã xác nhận của client hiện tại.
	public static PetHealthReading Read(int processId) {
		long profilerStart = HotPathProfiler.Begin();
		try {
		try {
			using MemoryReader reader = new(processId);
			RuntimeLayout layout = RuntimeLayoutResolver.Resolve(processId);
			if (! layout.Get(RuntimeSubsystem.Entity).Available) return PetHealthReading.Fail("Entity layout chưa sẵn sàng.");
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			IntPtr entityTable = reader.ReadPointer32(IntPtr.Add(moduleBase, layout.EntityTableRva));
			if (entityTable == IntPtr.Zero) return PetHealthReading.Fail("EntityTable bằng 0.");
			int ownerId = reader.ReadInt32(IntPtr.Add(IntPtr.Add(entityTable, layout.PlayerRecordOffset), OwnerCharacterId));
			if (ownerId == 0) return PetHealthReading.Fail("Không đọc được mã nhân vật tại +0x08.");

			// ĐƯỜNG NHANH: thử lại đúng ô đã tìm được lần trước thay vì quét lại cả 510 ô.
			//
			// Đệ hầu như không đổi ô giữa hai nhịp 100ms. Đo ngày 2026-09-14 (perf.log): hàm này chạy 44 lần/giây và
			// mỗi lần tốn ~1040us vì quét đủ 510 ô — tức ~22.000 lần đọc bộ nhớ mỗi giây chỉ để tìm một con.
			// Đường nhanh chỉ tốn 3 lần đọc.
			//
			// AN TOÀN vì kiểm lại ĐẦY ĐỦ đúng những điều kiện của vòng quét: type 6, chưa kết thúc, HP hợp lệ, và mã
			// chủ khớp. Sai bất kỳ điều nào là bỏ cache, quét lại toàn bảng — nên Đệ chết, đổi ô, hay gọi con khác đều
			// tự phục hồi ở đúng nhịp kế tiếp.
			//
			// Đánh đổi đã biết: phép kiểm "hai Đệ cùng mã chủ" ở cuối hàm chỉ còn chạy trong lượt quét đầy đủ. Chấp
			// nhận được vì tính tới 2026-09-08 đã 1294 lượt chưa lần nào chạm nhánh đó.
			byte[] header = new byte[EntityHeaderSize];
			if (cachedPetIndexByProcess.TryGetValue(processId, out int cachedIndex)) {
				PetHealthReading cached = TryReadPetAt(reader, layout, entityTable, cachedIndex, ownerId, header);
				if (cached.Present) return cached;
				cachedPetIndexByProcess.TryRemove(processId, out _);
			}

			List<PetHealthReading> candidates = [];
			int foreignPets = 0;
			// Lượt quét ĐẦY ĐỦ: chỉ chạy khi chưa có cache hoặc ô cache không còn đúng.
			// MỘT lần đọc lấy cả EntityType (0x0028) lẫn LifecycleStatus (0x01E8) — hai trường nằm gọn trong 456 byte.
			for (int index = AutoFsClientProfile.FirstEntityIndex; index <= AutoFsClientProfile.LastEntityIndex; index++) {
				PetHealthReading found = TryReadPetAt(reader, layout, entityTable, index, ownerId, header);
				if (found.Present) candidates.Add(found);
				else if (found.EntityIndex == ForeignPetMarker) foreignPets++;
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
			cachedPetIndexByProcess[processId] = candidates[0].EntityIndex;
			return candidates[0];
		} catch (Exception ex) {
			return PetHealthReading.Fail($"{ex.GetType().Name}: {ex.Message}");
		}
		} finally {
			HotPathProfiler.End(HotPathProfiler.PetRead, profilerStart);
		}
	}

	// Đọc và kiểm ĐỦ một ô entity xem có phải Đệ của mình không. Dùng chung cho cả đường nhanh lẫn lượt quét đầy đủ,
	// nên hai đường KHÔNG THỂ lệch điều kiện nhau — đó là lý do tách hàm thay vì chép lại phép kiểm.
	// Trả Present=true khi đúng là Đệ của mình; EntityIndex=ForeignPetMarker khi là Đệ của người khác.
	private static PetHealthReading TryReadPetAt(MemoryReader reader, RuntimeLayout layout, IntPtr entityTable, int index, int ownerId, byte[] header) {
		if (index < AutoFsClientProfile.FirstEntityIndex || index > AutoFsClientProfile.LastEntityIndex) return PetHealthReading.Missing();
		IntPtr entity = IntPtr.Add(entityTable, index * layout.EntityStride);
		if (! reader.ReadInto(IntPtr.Add(entity, HeaderBase), header, EntityHeaderSize)) return PetHealthReading.Missing();
		if (BitConverter.ToInt32(header, HeaderTypeOffset) != PetEntityType) return PetHealthReading.Missing();
		if (BitConverter.ToInt32(header, HeaderStatusOffset) == AutoFsClientProfile.FinishedStatus) return PetHealthReading.Missing();
		// Hp (0x27D0) và MaxHp (0x27D4) liền nhau nên gộp một lần đọc; vẫn kiểm liền kề vì hai offset này do
		// RuntimeLayout cấp lúc chạy chứ không phải hằng số cứng.
		int currentHp;
		int maximumHp;
		if (layout.MaxHpOffset == layout.HpOffset + sizeof(int)) {
			if (! reader.TryReadInt32Pair(IntPtr.Add(entity, layout.HpOffset), out currentHp, out maximumHp)) return PetHealthReading.Missing();
		} else {
			currentHp = reader.ReadInt32(IntPtr.Add(entity, layout.HpOffset));
			maximumHp = reader.ReadInt32(IntPtr.Add(entity, layout.MaxHpOffset));
		}
		if (currentHp <= 0 || maximumHp <= 0 || currentHp > maximumHp) return PetHealthReading.Missing();
		if (reader.ReadInt32(IntPtr.Add(entity, PetOwnerCharacterId)) != ownerId) return new PetHealthReading(true, false, ForeignPetMarker, 0, 0, "");
		return new PetHealthReading(true, true, index, currentHp, maximumHp, $"EntityType={PetEntityType}; EntityIndex={index}; OwnerId={ownerId}; Source=OWNER_ID_MATCH.");
	}
}
