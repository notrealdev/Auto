namespace Auto.DebugTools;

using System.Text;
using Auto.Attack;
using Auto.Runtime;
using Auto.Support;
using Auto.Utils;

// In ra Đệ của nhân vật và đối chiếu với con mà PetHealthReader thật sự chọn.
//
// Quy tắc: Đệ.+0x429C == NhânVật.+0x08 (mã nhân vật của chủ). Xem Support/PetHealthReader.cs để biết offset
// này dò ra và được xác nhận thế nào.
//
// Đệ của người khác chỉ ĐẾM chứ không liệt kê. Vẫn phải đếm, vì con số đó cho biết bộ lọc mã chủ có thật sự
// được thử hay không: bằng 0 thì kết quả ĐÚNG chẳng chứng minh gì, code cũ cũng qua được.
//
// Giữ lại sau khi bản vá đã verify vì hai việc: ca Đệ chết rồi gọi lại (chỉ số entity đổi) chưa verify được,
// và khi client update làm lệch offset mã chủ thì đây là công cụ chẩn.
//
// Chỉ đọc bộ nhớ, không gửi lệnh nào, không ghi gì vào game.
public static class PetOwnerProbe {
	private const string BuildStamp = "PET-OWNER-20260908-06";
	private const int PetEntityType = 6;
	private const int OwnerCharacterId = 0x0008;
	private const int PetOwnerCharacterId = 0x429C;

	public static string Run(int processId) {
		StringBuilder output = new();
		output.AppendLine("===== Thông tin Đệ =====");
		output.AppendLine($"PET_PROBE_START | BuildStamp={BuildStamp} | Mode=READ_ONLY | GameMemoryWrite=NO | ProcessId={processId}");

		try {
			using MemoryReader reader = new(processId);
			RuntimeLayout layout = RuntimeLayoutResolver.Resolve(processId);
			if (! layout.Get(RuntimeSubsystem.Entity).Available) return output.Append("PET_PROBE_FAIL | Reason=ENTITY_SUBSYSTEM_UNAVAILABLE").ToString();

			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			if (moduleBase == IntPtr.Zero) return output.Append("PET_PROBE_FAIL | Reason=MODULE_NOT_FOUND").ToString();
			IntPtr tableBase = reader.ReadPointer32(IntPtr.Add(moduleBase, layout.EntityTableRva));
			if (tableBase == IntPtr.Zero) return output.Append("PET_PROBE_FAIL | Reason=ENTITY_TABLE_NULL").ToString();

			IntPtr playerBase = IntPtr.Add(tableBase, layout.PlayerRecordOffset);
			int ownerId = reader.ReadInt32(IntPtr.Add(playerBase, OwnerCharacterId));
			int playerX = reader.ReadInt32(IntPtr.Add(playerBase, AutoFsClientProfile.RawX));
			int playerY = reader.ReadInt32(IntPtr.Add(playerBase, AutoFsClientProfile.RawY));
			output.AppendLine($"Nhân vật: Tên={LegacyVietnameseText.Decode(ReadName(reader, playerBase))} | MãChủ(+0x08)={ownerId}");

			int total = 0;
			int mine = 0;
			int mineIndex = 0;
			for (int index = AutoFsClientProfile.FirstEntityIndex; index <= AutoFsClientProfile.LastEntityIndex; index++) {
				IntPtr entity = IntPtr.Add(tableBase, index * layout.EntityStride);
				if (reader.ReadInt32(IntPtr.Add(entity, AutoFsClientProfile.EntityType)) != PetEntityType) continue;
				if (reader.ReadInt32(IntPtr.Add(entity, AutoFsClientProfile.LifecycleStatus)) == AutoFsClientProfile.FinishedStatus) continue;
				int hp = reader.ReadInt32(IntPtr.Add(entity, layout.HpOffset));
				int maxHp = reader.ReadInt32(IntPtr.Add(entity, layout.MaxHpOffset));
				if (hp <= 0 || maxHp <= 0 || hp > maxHp) continue;

				total++;
				if (reader.ReadInt32(IntPtr.Add(entity, PetOwnerCharacterId)) != ownerId) continue;
				mine++;
				mineIndex = index;

				int x = reader.ReadInt32(IntPtr.Add(entity, AutoFsClientProfile.RawX));
				int y = reader.ReadInt32(IntPtr.Add(entity, AutoFsClientProfile.RawY));
				output.AppendLine($"Đệ của mình: #{index} | {LegacyVietnameseText.Decode(ReadName(reader, entity))} | Hp={hp}/{maxHp} | Lv={reader.ReadInt32(IntPtr.Add(entity, AutoFsClientProfile.Level))} | KC={Distance(playerX, playerY, x, y):F0}");
			}
			output.AppendLine($"Tổng type 6 hợp lệ = {total} | khớp mã chủ = {mine} | Đệ người khác = {total - mine}");

			PetHealthReading reading = PetHealthReader.Read(processId);
			output.AppendLine($"PetHealthReader => Success={reading.Success} | Present={reading.Present} | EntityIndex={reading.EntityIndex} | Hp={reading.CurrentHp}/{reading.MaximumHp}");
			output.AppendLine($"Detail={reading.Detail}");
			output.AppendLine(Verdict(total, mine, mineIndex, reading));
			return output.ToString();
		} catch (Exception ex) {
			output.AppendLine($"PET_PROBE_FAIL | {ex.GetType().Name}: {ex.Message}");
			return output.ToString();
		}
	}

	// Vòng lặp ở trên và PetHealthReader đọc bộ nhớ ở hai thời điểm khác nhau, nên lệch chỉ số có thể xảy ra khi
	// Đệ vừa chết hoặc vừa được gọi ra giữa hai lần đọc — báo riêng chứ không gộp vào SAI.
	private static string Verdict(int total, int mine, int mineIndex, PetHealthReading reading) {
		if (total == 0) return "KẾT LUẬN=KHÔNG_CÓ_ĐỆ | quanh đây không có entity type 6 hợp lệ nào.";
		if (mine == 0) return $"KẾT LUẬN=SAI | {total} con Đệ nhưng không con nào mang mã chủ — offset +0x429C không còn đúng.";
		if (mine > 1) return $"KẾT LUẬN=SAI | {mine} con cùng mang mã chủ — mã chủ không tách được duy nhất.";
		if (! reading.Success || ! reading.Present) return $"KẾT LUẬN=SAI | thấy Đệ #{mineIndex} nhưng PetHealthReader không trả về được.";
		if (reading.EntityIndex != mineIndex) return $"KẾT LUẬN=LỆCH | thấy #{mineIndex}, PetHealthReader trả #{reading.EntityIndex} — có thể do hai lần đọc cách nhau, chạy lại để xác nhận.";
		return total == mine
			? $"KẾT LUẬN=ĐÚNG | Đệ #{mineIndex}. Quanh đây không có Đệ người khác nên lượt này CHƯA thử được bộ lọc mã chủ."
			: $"KẾT LUẬN=ĐÚNG | tách được Đệ #{mineIndex} giữa {total} con.";
	}

	private static double Distance(int firstX, int firstY, int secondX, int secondY) {
		double deltaX = (double)firstX - secondX;
		double deltaY = (double)firstY - secondY;
		return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
	}

	private static byte[] ReadName(MemoryReader reader, IntPtr entityBase) {
		byte[] bytes = reader.ReadBytes(IntPtr.Add(entityBase, AutoFsClientProfile.Name), AutoFsClientProfile.MaximumNameLength);
		int length = Array.IndexOf(bytes, (byte)0);
		if (length < 0) length = bytes.Length;
		return length == 0 ? [] : bytes.AsSpan(0, length).ToArray();
	}
}
