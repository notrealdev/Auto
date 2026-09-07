namespace Auto.DebugTools;

using System.Text;
using Auto.Attack;
using Auto.Runtime;
using Auto.Utils;

// Liệt kê QUÁI THỦ LĨNH/BOSS quanh nhân vật theo phán quyết EliteAvoidance.IsEliteName. Quái thường không in ra,
// nhưng vẫn được đếm: tỉ lệ đứng im của cả bầy là mốc so sánh duy nhất để đọc được cột Δ (xem mục đích 2).
//
// Mục đích 1: xác minh marker nhận diện trên ĐÚNG client đang chạy. Bằng chứng cho marker "(" và "<c=" lấy từ bản
// decompile AutoFS (VectorFactory.cs:117/3356/3717/4899) vốn chạy trên client đời cũ. Probe in cả BYTE THÔ dạng hex:
// kể cả khi bước giải mã tiếng Việt ra chữ sai, vẫn nhìn thấy được client thật sự nhúng ký tự gì vào tên.
// Hai mẫu 2026-09-07 đã xác nhận CẢ HAI marker có thật trên client này:
//   PID 21768 boss    "3C633D673E5468C76E204F616E68" = "<c=g>Thần Oanh"
//   PID  8536 thủ lĩnh "546869D574205472EF6E672848E16129" = "Thiết Trùng(Hỏa)"  ← lưu ý: KHÔNG có khoảng trắng
// 107 con quái thường trong hai mẫu đó không con nào bị bắt nhầm.
//
// Mục đích 2: phân biệt bản ghi entity SỐNG với bản ghi CŨ CHƯA DỌN. Cùng một boss "Thần Oanh" hiện ra ở hai index
// 123 và 103 với Hp giống hệt nhưng toạ độ cách nhau 2377 raw. Bản ghi ma sẽ sinh ra vùng cấm ở chỗ không có gì.
// Vì vậy probe chụp HAI lần cách nhau SampleDelayMilliseconds rồi in độ dịch chuyển của từng entity giữa hai lần.
// Δ=0 một mình KHÔNG kết luận được (quái đứng yên cũng Δ=0) — phải đối chiếu với dòng "Mốc quái thường" ở cuối.
//
// Cột Hp KHÔNG dùng để nhận diện được: đó là máu HIỆN TẠI, không phải máu tối đa. Mẫu PID 8536 cho cùng "Thiết Trùng"
// L40 đọc ra 1265/1076/853/495/199/36/32 vì đang bị đánh dở, nên boss sắp chết sẽ đọc thấp hơn quái thường đầy máu.
// In ra chỉ để tham khảo.
//
// Chỉ đọc bộ nhớ, không gửi lệnh nào, không ghi gì vào game.
public static class EliteMonsterProbe {
	private const string BuildStamp = "ELITE-MONSTER-20260907-03";
	private const int NameColumnWidth = 22;
	private const int MaximumHexBytes = 32;
	private const int SampleDelayMilliseconds = 1000;

	private readonly record struct MonsterSample(byte[] NameBytes, int Status, int Hp, int Level, int RawX, int RawY);

	public static string Run(int processId) {
		StringBuilder output = new();
		output.AppendLine("===== Quái thủ lĩnh / boss quanh nhân vật =====");
		output.AppendLine($"ELITE_PROBE_START | BuildStamp={BuildStamp} | Mode=READ_ONLY | GameMemoryWrite=NO | ProcessId={processId}");

		try {
			using MemoryReader reader = new(processId);
			RuntimeLayout layout = RuntimeLayoutResolver.Resolve(processId);
			if (! layout.Get(RuntimeSubsystem.Entity).Available) return output.Append("ELITE_PROBE_FAIL | Reason=ENTITY_SUBSYSTEM_UNAVAILABLE").ToString();

			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			if (moduleBase == IntPtr.Zero) return output.Append("ELITE_PROBE_FAIL | Reason=MODULE_NOT_FOUND").ToString();
			IntPtr tableBase = reader.ReadPointer32(IntPtr.Add(moduleBase, layout.EntityTableRva));
			if (tableBase == IntPtr.Zero) return output.Append("ELITE_PROBE_FAIL | Reason=ENTITY_TABLE_NULL").ToString();

			Dictionary<int, MonsterSample> first = CollectMonsters(reader, tableBase, layout.EntityStride);
			Thread.Sleep(SampleDelayMilliseconds);
			Dictionary<int, MonsterSample> second = CollectMonsters(reader, tableBase, layout.EntityStride);

			IntPtr playerBase = IntPtr.Add(tableBase, layout.PlayerRecordOffset);
			int playerX = reader.ReadInt32(IntPtr.Add(playerBase, AutoFsClientProfile.RawX));
			int playerY = reader.ReadInt32(IntPtr.Add(playerBase, AutoFsClientProfile.RawY));
			output.AppendLine($"Nhân vật: Raw={playerX}/{playerY} | Ô={playerX / 256}/{playerY / 512} | Hai lần chụp cách {SampleDelayMilliseconds}ms");
			output.AppendLine($"{"#",-4}{"Tên",-NameColumnWidth} | {"Hp",8} | {"Lv",3} | {"St",2} | {"Raw",13} | {"KC",6} | {"Δ",5} | Byte thô");

			List<(double Distance, string Line)> eliteRows = [];
			int normalCount = 0;
			int normalIdleCount = 0;
			int normalMeasuredCount = 0;

			foreach ((int index, MonsterSample sample) in second) {
				string delta = DescribeMovement(first, index, sample, out bool measured, out bool idle);

				if (! EliteAvoidance.IsEliteName(sample.NameBytes)) {
					normalCount++;
					if (measured) normalMeasuredCount++;
					if (idle) normalIdleCount++;
					continue;
				}

				string name = LegacyVietnameseText.Decode(sample.NameBytes);
				double distanceToPlayer = Distance(playerX, playerY, sample.RawX, sample.RawY);
				eliteRows.Add((distanceToPlayer, $"{index,-4}{name.PadRight(NameColumnWidth)} | {sample.Hp,8} | {sample.Level,3} | {sample.Status,2} | {sample.RawX,6}/{sample.RawY,-6} | {distanceToPlayer,6:F0} | {delta,5} | {ToHex(sample.NameBytes)}"));
			}

			eliteRows.Sort((left, right) => left.Distance.CompareTo(right.Distance));
			foreach ((_, string line) in eliteRows) output.AppendLine(line);
			if (eliteRows.Count == 0) output.AppendLine("(không thấy quái thủ lĩnh/boss nào)");

			output.AppendLine($"ELITE_PROBE_END | Thủ lĩnh/boss={eliteRows.Count} | Quái thường={normalCount} (không in)");
			// Mốc so sánh cho cột Δ: nếu cả bầy quái thường cũng đứng im thì Δ=0 của một con thủ lĩnh chẳng nói lên gì.
			output.AppendLine(normalMeasuredCount > 0
				? $"Mốc quái thường: đứng im {normalIdleCount}/{normalMeasuredCount} con"
				: "Mốc quái thường: không đo được con nào");
			output.AppendLine("St=LifecycleStatus (6=đã kết thúc, đã bị loại) | KC=khoảng cách raw tới nhân vật | Hp=máu HIỆN TẠI, không phải máu tối đa");
			output.AppendLine($"Δ=dịch chuyển raw giữa hai lần chụp | Byte thô=tên chưa giải mã, 0x28='(' 3C 63 3D='<c='");
			return output.ToString();
		} catch (Exception ex) {
			output.AppendLine($"ELITE_PROBE_FAIL | {ex.GetType().Name}: {ex.Message}");
			return output.ToString();
		}
	}

	// So cùng một index giữa hai lần chụp. MỚI = chưa có ở lần đầu, ĐỔI = ô entity bị dùng lại cho con khác (tên đổi);
	// cả hai trường hợp khoảng cách giữa hai lần không còn ý nghĩa nên không tính vào mốc đứng im.
	private static string DescribeMovement(Dictionary<int, MonsterSample> first, int index, MonsterSample sample, out bool measured, out bool idle) {
		measured = false;
		idle = false;
		if (! first.TryGetValue(index, out MonsterSample before)) return "MỚI";
		if (! before.NameBytes.AsSpan().SequenceEqual(sample.NameBytes)) return "ĐỔI";
		measured = true;
		double moved = Distance(before.RawX, before.RawY, sample.RawX, sample.RawY);
		idle = moved < 1;
		return idle ? "0" : moved.ToString("F0");
	}

	private static Dictionary<int, MonsterSample> CollectMonsters(MemoryReader reader, IntPtr tableBase, int stride) {
		Dictionary<int, MonsterSample> samples = [];
		for (int index = AutoFsClientProfile.FirstEntityIndex; index <= AutoFsClientProfile.LastEntityIndex; index++) {
			try {
				IntPtr entityBase = IntPtr.Add(tableBase, index * stride);
				int status = reader.ReadInt32(IntPtr.Add(entityBase, AutoFsClientProfile.LifecycleStatus));
				if (status == AutoFsClientProfile.FinishedStatus) continue;
				if (reader.ReadInt32(IntPtr.Add(entityBase, AutoFsClientProfile.EntityType)) != AutoFsClientProfile.MonsterType) continue;

				byte[] nameBytes = ReadName(reader, entityBase);
				if (nameBytes.Length == 0) continue;
				int x = reader.ReadInt32(IntPtr.Add(entityBase, AutoFsClientProfile.RawX));
				int y = reader.ReadInt32(IntPtr.Add(entityBase, AutoFsClientProfile.RawY));
				if (x <= 0 || y <= 0) continue;

				int hp = reader.ReadInt32(IntPtr.Add(entityBase, AutoFsClientProfile.Hp));
				int level = reader.ReadInt32(IntPtr.Add(entityBase, AutoFsClientProfile.Level));
				samples[index] = new MonsterSample(nameBytes, status, hp, level, x, y);
			} catch {
				// Bỏ qua entity đọc lỗi: bảng entity có thể đổi ngay giữa lúc quét, không đáng làm hỏng cả lượt chụp.
			}
		}
		return samples;
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
		return length == 0 ? Array.Empty<byte>() : bytes.AsSpan(0, length).ToArray();
	}

	private static string ToHex(byte[] bytes) {
		int count = Math.Min(bytes.Length, MaximumHexBytes);
		return Convert.ToHexString(bytes, 0, count) + (bytes.Length > count ? "..." : "");
	}
}
