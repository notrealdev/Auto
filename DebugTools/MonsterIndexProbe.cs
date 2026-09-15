namespace Auto.DebugTools;

using System.Collections.Concurrent;
using System.Text;
using Auto.Attack;
using Auto.Runtime;
using Auto.Utils;

// Trả lời ĐÚNG MỘT câu hỏi: quái (EntityType = 0) nằm ở dải chỉ số nào trong bảng entity?
//
// Vì sao cần: vòng quét của luồng Đánh đang quét 2..511 và đó là phần ăn CPU nhiều nhất (perf.log 2026-09-14:
// QuétĐánh ~79 lượt/giây × 510 ô ≈ 40.000 lần đọc bộ nhớ mỗi giây). Trần 511 sinh ra là vì luồng SỬA ĐỒ/NHIỆM VỤ
// cần tìm NPC ở ô cao (#272 Hoàng Thiên Hóa, #324 Thổ Hành Tôn... xem Utils/GameAddresses.cs:45-49), CHỨ KHÔNG
// phải vì quái. Nếu quái luôn nằm ở dải thấp thì hạ trần riêng cho vòng Đánh là cắt đôi phần nặng nhất.
//
// KHÔNG được kết luận từ một lần chạy: bảng entity thay đổi theo map, theo số người chơi quanh đó. Probe này vì vậy
// GIỮ MỐC CAO NHẤT qua các lần bấm trong cùng phiên Auto — chạy ở nhiều bãi, nhiều thời điểm, rồi mới đọc mốc đó.
//
// Quét tới 1023 (VƯỢT trần 511 đang dùng) để phát hiện luôn trường hợp quái nằm trên cả 511.
//
// CHỈ ĐỌC bộ nhớ, không gửi lệnh nào vào game.
public static class MonsterIndexProbe {
	// PHẢI đổi mỗi lần sửa probe: bản -01 không lọc rác nên báo "ô cao nhất 1021" toàn là bản ghi ngoài đuôi bảng,
	// và vì stamp không đổi nên không phân biệt được kết quả thuộc bản nào.
	private const string BuildStamp = "MONSTER-INDEX-20260914-02-LOCRAC";
	private const int ExtendedLastIndex = 1023;
	private const int MaximumListedMonsters = 40;
	private const int CurrentAttackCeiling = AutoFsClientProfile.LastEntityIndex;
	// Trần máu để loại bản ghi rác. Quái thật cao nhất quan sát được là 4340 (Dã Mao thần, Map 35); lấy dư rất
	// nhiều để không cắt nhầm boss máu lớn, nhưng vẫn chặn được các giá trị rác kiểu 1948282581.
	private const int MaximumSaneHp = 100_000_000;

	// Mốc cao nhất quan sát được qua mọi lần bấm, khoá theo tiến trình.
	private static readonly ConcurrentDictionary<int, HighWaterMark> highWaterByProcess = new();

	private sealed class HighWaterMark {
		public int HighestMonsterIndex = -1;
		public int Runs;
		public int MapIdOfHighest;
		public string NameOfHighest = "";
	}

	public static string Run(int processId) {
		StringBuilder output = new();
		output.AppendLine("===== Quái nằm ở dải chỉ số nào? =====");
		output.AppendLine($"MONSTER_INDEX_PROBE | BuildStamp={BuildStamp} | Mode=CHỈ_ĐỌC | ProcessId={processId}");

		try {
			using MemoryReader reader = new(processId);
			RuntimeLayout layout = RuntimeLayoutResolver.Resolve(processId);
			if (! layout.Get(RuntimeSubsystem.Entity).Available) return output.AppendLine("DỪNG | Entity layout chưa sẵn sàng.").ToString();
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			if (moduleBase == IntPtr.Zero) return output.AppendLine("DỪNG | Không thấy module Game.exe.").ToString();
			IntPtr tableBase = reader.ReadPointer32(IntPtr.Add(moduleBase, layout.EntityTableRva));
			if (tableBase == IntPtr.Zero) return output.AppendLine("DỪNG | Con trỏ bảng entity bằng 0.").ToString();

			GameMapInfo map = GameMapReader.Read(processId);
			IntPtr playerBase = IntPtr.Add(tableBase, layout.PlayerRecordOffset);
			int playerX = reader.ReadInt32(IntPtr.Add(playerBase, AutoFsClientProfile.RawX));
			int playerY = reader.ReadInt32(IntPtr.Add(playerBase, AutoFsClientProfile.RawY));
			output.AppendLine($"Map={map.MapId} | Nhân vật={playerX}/{playerY} | Quét 2..{ExtendedLastIndex} (vượt trần {CurrentAttackCeiling} đang dùng)");
			output.AppendLine();

			List<(int Index, string Name, int Hp, double Distance)> monsters = [];
			Dictionary<int, int> countByType = [];
			int highestMonsterIndex = -1;
			int aliveAboveCeiling = 0;

			for (int index = AutoFsClientProfile.FirstEntityIndex; index <= ExtendedLastIndex; index++) {
				try {
					IntPtr entityBase = IntPtr.Add(tableBase, index * layout.EntityStride);
					int status = reader.ReadInt32(IntPtr.Add(entityBase, AutoFsClientProfile.LifecycleStatus));
					if (status == AutoFsClientProfile.FinishedStatus) continue;
					byte[] nameBytes = ReadName(reader, entityBase);
					if (nameBytes.Length == 0) continue;
					int type = reader.ReadInt32(IntPtr.Add(entityBase, AutoFsClientProfile.EntityType));
					countByType[type] = countByType.GetValueOrDefault(type) + 1;
					if (index > CurrentAttackCeiling) aliveAboveCeiling++;
					if (type != AutoFsClientProfile.MonsterType) continue;

					int rawX = reader.ReadInt32(IntPtr.Add(entityBase, AutoFsClientProfile.RawX));
					int rawY = reader.ReadInt32(IntPtr.Add(entityBase, AutoFsClientProfile.RawY));
					int hp = reader.ReadInt32(IntPtr.Add(entityBase, AutoFsClientProfile.Hp));
					// LỌC RÁC, bắt buộc: đọc quá đuôi bảng entity vẫn ra byte đọc được, và chúng lọt hết mọi phép
					// kiểm ở trên. Lần chạy 2026-09-14 trên PID=32824 Map 35 cho 41 "con quái" ở ô 524..1021 với
					// tên 'ÿÿ', '*30,68*', 'ấp 60 không thể sử dụn', HP=0 / 1948282581 / -563994521283 — toàn rác.
					// Khớp đúng ghi chú sẵn có ở Utils/GameAddresses.cs:53-56 ("từ #513 trở lên toàn rác").
					// Quái thật phải có toạ độ dương và HP nằm trong khoảng hợp lý.
					if (rawX <= 0 || rawY <= 0) continue;
					if (hp <= 0 || hp > MaximumSaneHp) continue;
					double distance = Distance(playerX, playerY, rawX, rawY);
					monsters.Add((index, LegacyVietnameseText.Decode(nameBytes), hp, distance));
					if (index > highestMonsterIndex) highestMonsterIndex = index;
				} catch {
					// Ô đọc lỗi thì bỏ qua, không làm hỏng cả lượt quét.
				}
			}

			HighWaterMark mark = highWaterByProcess.GetOrAdd(processId, _ => new HighWaterMark());
			mark.Runs++;
			if (highestMonsterIndex > mark.HighestMonsterIndex) {
				mark.HighestMonsterIndex = highestMonsterIndex;
				mark.MapIdOfHighest = map.MapId;
				mark.NameOfHighest = monsters.Count > 0 ? monsters[^1].Name : "";
			}

			output.AppendLine($"QUÁI TÌM THẤY: {monsters.Count} con (EntityType={AutoFsClientProfile.MonsterType})");
			if (monsters.Count == 0) {
				output.AppendLine("  (không có quái nào — đứng ở BÃI ĐANG ĐÁNH rồi bấm lại, đứng trong thành thì phép đo vô nghĩa)");
			} else {
				output.AppendLine($"  Ô thấp nhất={monsters[0].Index} | Ô CAO NHẤT={highestMonsterIndex}");
				output.AppendLine($"  {"#Ô",-6}{"Tên",-24}{"HP",-9}Cách (ô)");
				// In cả đầu LẪN đuôi danh sách: cắt kiểu Take() làm mất đúng phần ô CAO NHẤT — thứ duy nhất cần nhìn.
				int half = MaximumListedMonsters / 2;
				bool truncated = monsters.Count > MaximumListedMonsters;
				for (int position = 0; position < monsters.Count; position++) {
					if (truncated && position == half) {
						output.AppendLine($"  ... bỏ qua {monsters.Count - MaximumListedMonsters} con ở giữa ...");
						position = monsters.Count - half - 1;
						continue;
					}
					(int index, string name, int hp, double distance) = monsters[position];
					string flag = index > CurrentAttackCeiling ? " <== NGOÀI TRẦN" : "";
					output.AppendLine($"  {index,-6}{Trim(name),-24}{hp,-9}{distance:F1}{flag}");
				}
			}

			output.AppendLine();
			output.AppendLine($"Phân bố mọi entity còn sống theo Type: {string.Join(", ", countByType.OrderBy(entry => entry.Key).Select(entry => $"Type{entry.Key}={entry.Value}"))}");
			output.AppendLine("  (Type 0=quái, 1=người chơi, 3=NPC, 6=Đệ)");
			output.AppendLine($"Entity còn sống nằm NGOÀI trần {CurrentAttackCeiling}: {aliveAboveCeiling}");

			output.AppendLine();
			output.AppendLine("===== MỐC CAO NHẤT QUA CÁC LẦN BẤM (phiên Auto này) =====");
			output.AppendLine($"Số lần đã chạy: {mark.Runs} | Ô quái cao nhất từng thấy: {(mark.HighestMonsterIndex < 0 ? "chưa thấy quái nào" : mark.HighestMonsterIndex.ToString())}" +
				(mark.HighestMonsterIndex >= 0 ? $" (lúc ở Map {mark.MapIdOfHighest})" : ""));
			output.AppendLine();
			output.AppendLine("CÁCH DÙNG: chạy ở NHIỀU bãi khác nhau, chỗ đông quái, đông người chơi, và cả lúc vừa đổi map.");
			output.AppendLine("Một lần chạy KHÔNG đủ kết luận — chỉ số ô do client cấp phát động, thay đổi theo map và số người quanh đó.");
			output.AppendLine($"Nếu sau nhiều lần mốc cao nhất vẫn thấp hơn nhiều so với {CurrentAttackCeiling}, có thể hạ trần riêng cho vòng Đánh.");
			return output.ToString();
		} catch (Exception ex) {
			output.AppendLine($"LỖI | {ex.GetType().Name}: {ex.Message}");
			return output.ToString();
		}
	}

	private static string Trim(string name) => name.Length <= 22 ? name : name[..22];

	// 1 ô = 256 raw trục X, 512 raw trục Y.
	private static double Distance(int firstX, int firstY, int secondX, int secondY) {
		double deltaX = (firstX - (double)secondX) / 256.0;
		double deltaY = (firstY - (double)secondY) / 512.0;
		return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
	}

	private static byte[] ReadName(MemoryReader reader, IntPtr entityBase) {
		byte[] bytes = reader.ReadBytes(IntPtr.Add(entityBase, AutoFsClientProfile.Name), AutoFsClientProfile.MaximumNameLength);
		int length = Array.IndexOf(bytes, (byte)0);
		if (length < 0) length = bytes.Length;
		return length == 0 ? [] : bytes.AsSpan(0, length).ToArray();
	}
}
