namespace Auto.Attack;

using Auto.Runtime;
using Auto.Utils;

internal sealed class AutoFsEntityScanner {
	private const int AutoFsMaximumPlayerDistance = 9999;

	// Khối đầu bản ghi entity, gom ba trường mà vòng lọc cần đọc cho MỌI entity: Level (0x0024), EntityType (0x0028)
	// và LifecycleStatus (0x01E8). Suy ra từ chính AutoFsClientProfile nên đổi offset bên đó là ở đây tự theo.
	private const int HeaderBase = AutoFsClientProfile.Level;
	private const int HeaderLevelOffset = AutoFsClientProfile.Level - HeaderBase;
	private const int HeaderTypeOffset = AutoFsClientProfile.EntityType - HeaderBase;
	private const int HeaderStatusOffset = AutoFsClientProfile.LifecycleStatus - HeaderBase;
	private const int EntityHeaderSize = HeaderStatusOffset + sizeof(int);

	// Bao lâu mới quét lại ĐỦ 510 ô một lần. Giữa hai lần đó chỉ đọc lại những ô đã biết là đang có entity.
	//
	// Vì sao cần (đo thật, perf.log 2026-09-14): vòng này chạy 79 lần/giây cho 5-6 account, mỗi lượt 510 lần đọc
	// => ~40.000 lần đọc bộ nhớ mỗi giây, và đối chiếu với CPU thật của tiến trình (179ms/s) thì nó cùng vòng
	// ĐọcĐệ chiếm gần như toàn bộ CPU của Auto. Số ô thực sự có entity chỉ khoảng 140/510.
	//
	// ĐÁNH ĐỔI, nói rõ: quái xuất hiện ở ô TRƯỚC ĐÓ TRỐNG sẽ được thấy chậm tối đa bằng khoảng này. Quái chết rồi
	// chọn con khác KHÔNG bị ảnh hưởng (con kia đã nằm sẵn trong danh sách ô sống), và ô đang có quái vẫn được đọc
	// lại mỗi lượt nên máu/toạ độ luôn tươi.
	private const int FullSweepIntervalMilliseconds = 500;
	private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, LiveSlotCache> liveSlotsByProcess = new();

	private sealed class LiveSlotCache {
		public DateTime NextFullSweepUtc;
		public int[] Indices = [];
	}

	public static int ReadCurrentTargetIndex(int processId) {
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			return moduleBase == IntPtr.Zero ? -1 : reader.ReadInt32(IntPtr.Add(moduleBase, GameAddresses.Globals.CurrentTargetIndex));
		} catch {
			return -1;
		}
	}

	public IReadOnlyList<AutoFsEntity> Scan(int processId, Settings settings) {
		using MemoryReader reader = new(processId);
		return Scan(reader, settings);
	}

	public IReadOnlyList<AutoFsEntity> Scan(int processId, Settings settings, bool includeAllTargetTypes) {
		using MemoryReader reader = new(processId);
		return Scan(reader, settings, includeAllTargetTypes);
	}

	public IReadOnlyList<AutoFsEntity> Scan(MemoryReader reader, Settings settings, bool includeAllTargetTypes = false) {
		return Scan(reader, settings, out _, out _, out _, includeAllTargetTypes);
	}

	// Quét target và trả về tọa độ hiện tại để nhánh Quanh điểm bám đúng vòng tìm quái của AutoFS.
	// playerLifecycleStatus là ô LifecycleStatus của chính nhân vật, đưa ra ngoài chỉ để đo: AutoFS chặn bước đi bằng
	// SplitDisk() = "ô trạng thái nhân vật == 3" (WindowQueue.cs:26253, đọc O_Player + 464 của client cũ) và hiện
	// CHƯA xác định được ô tương ứng trên client này. Ghi kèm vào log đi 4 góc để đối chiếu sau.
	// eliteCollector: truyền vào một danh sách rỗng để nhận thêm mọi quái thủ lĩnh/boss quanh tâm bãi. Bỏ trống thì
	// bước gom bị tắt hoàn toàn, không tốn thêm lần đọc bộ nhớ nào — các overload cũ giữ nguyên hành vi.
	public IReadOnlyList<AutoFsEntity> Scan(MemoryReader reader, Settings settings, out int playerX, out int playerY, out int playerLifecycleStatus, bool includeAllTargetTypes = false, int preferredTargetIndex = -1, List<AutoFsEntity>? eliteCollector = null) {
		long profilerStart = HotPathProfiler.Begin();
		try {
		playerX = 0;
		playerY = 0;
		playerLifecycleStatus = -1;
		RuntimeLayout layout = RuntimeLayoutResolver.Resolve(reader.ProcessId);
		if (! layout.Get(RuntimeSubsystem.Entity).Available) return Array.Empty<AutoFsEntity>();
		IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
		IntPtr tableBase = reader.ReadPointer32(IntPtr.Add(moduleBase, layout.EntityTableRva));
		if (tableBase == IntPtr.Zero) return Array.Empty<AutoFsEntity>();

		IntPtr playerBase = IntPtr.Add(tableBase, layout.PlayerRecordOffset);
		playerX = reader.ReadInt32(IntPtr.Add(playerBase, AutoFsClientProfile.RawX));
		playerY = reader.ReadInt32(IntPtr.Add(playerBase, AutoFsClientProfile.RawY));
		playerLifecycleStatus = reader.ReadInt32(IntPtr.Add(playerBase, AutoFsClientProfile.LifecycleStatus));
		int currentTargetIndex = reader.ReadInt32(IntPtr.Add(moduleBase, GameAddresses.Globals.CurrentTargetIndex));
		bool configuredTrainingMode = HasConfiguredTrainingMode(settings);
		(int centerX, int centerY) = GetCenter(settings, playerX, playerY);
		List<AutoFsEntity> entities = new();
		List<(int X, int Y)> elitePositions = new();
		// Một buffer duy nhất cho cả lượt quét, cấp phát ở đây (biến cục bộ) thay vì field để không phải lo hai luồng
		// dùng chung: ngoài worker Đánh còn có đường liệt kê ComboBox gọi Scan từ luồng giao diện.
		byte[] header = new byte[EntityHeaderSize];

		// Đường liệt kê cho ComboBox luôn quét đủ; vòng chọn mục tiêu thì quét đủ theo chu kỳ, xen giữa là quét
		// nhanh trên danh sách ô đã biết có entity.
		LiveSlotCache slotCache = liveSlotsByProcess.GetOrAdd(reader.ProcessId, _ => new LiveSlotCache());
		int[] knownLiveSlots;
		bool fullSweep;
		lock (slotCache) {
			knownLiveSlots = slotCache.Indices;
			fullSweep = includeAllTargetTypes || knownLiveSlots.Length == 0 || DateTime.UtcNow >= slotCache.NextFullSweepUtc;
		}
		List<int>? liveSlotsFound = fullSweep ? new List<int>(160) : null;
		int sweepLength = fullSweep ? AutoFsClientProfile.LastEntityIndex - AutoFsClientProfile.FirstEntityIndex + 1 : knownLiveSlots.Length;

		for (int slot = 0; slot < sweepLength; slot++) {
			int index = fullSweep ? AutoFsClientProfile.FirstEntityIndex + slot : knownLiveSlots[slot];
			IntPtr entityBase = GetEntityBase(tableBase, index, layout.EntityStride);
			// MỘT lần đọc lấy cả Level (0x0024), EntityType (0x0028) và LifecycleStatus (0x01E8) — ba trường nằm gọn
			// trong 456 byte. Trước đây là hai lần gọi ReadProcessMemory riêng cho mỗi entity trong 510 entity.
			// Đo ngày 2026-09-14: 510 entity × 2 lần đọc 4-byte = 0,29 ms; × 1 lần đọc 456-byte = 0,15 ms.
			if (! reader.ReadInto(IntPtr.Add(entityBase, AutoFsClientProfile.Level), header, EntityHeaderSize)) continue;
			int status = BitConverter.ToInt32(header, HeaderStatusOffset);
			if (status == AutoFsClientProfile.FinishedStatus) continue;

			int level = BitConverter.ToInt32(header, HeaderLevelOffset);
			int type = BitConverter.ToInt32(header, HeaderTypeOffset);
			bool acceptedType = includeAllTargetTypes
				? type == AutoFsClientProfile.MonsterType || type == AutoFsClientProfile.PlayerType
				// "Chỉ đánh Boss" là luồng song song với "Đánh quái", không phụ thuộc: bật nó thì quái vẫn được nhận vào
				// dù "Đánh quái" đang tắt. Ngược lại "Không đánh Boss" chỉ là bộ lọc trừ nên không tự mở luồng nào.
				: (configuredTrainingMode || settings.AttackMonsters || settings.OnlyAttackBoss) && type == AutoFsClientProfile.MonsterType;
			if (!acceptedType) continue;

			byte[] nameBytes = ReadName(reader, entityBase);
			if (nameBytes.Length == 0) continue;
			// Ghi nhận ô có QUÁI THẬT (đã qua lọc loại và CÓ TÊN), không phải mọi ô "chưa kết thúc".
			//
			// Bản trước ghi ngay sau phép kiểm status và KHÔNG ăn thua gì — đo được ở perf.log 2026-09-14 22:01:
			// QuétĐánh vẫn 1988-2033us/lần, y hệt mức 2125us/lần trước khi thêm cache. Lý do: ô TRỐNG không mang
			// status = FinishedStatus (6) nên chúng lọt qua phép kiểm đó, danh sách "ô sống" gom gần đủ 510 ô và
			// lượt quét nhanh hoá ra vẫn là quét đủ. Đặt ở đây thì danh sách chỉ còn đúng số quái thật.
			liveSlotsFound?.Add(index);
			string name = LegacyVietnameseText.Decode(nameBytes);

			// Đọc toạ độ TRƯỚC bộ lọc tên. Hai phép lọc độc lập nhau nên tập kết quả không đổi, nhưng nhờ vậy
			// nhánh gom quái thủ lĩnh ngay dưới có sẵn toạ độ mà không phải đọc bộ nhớ thêm lần nữa.
			// RawX (0x434C) và RawY (0x4350) liền nhau nên đọc chung một lần.
			if (! reader.TryReadInt32Pair(IntPtr.Add(entityBase, AutoFsClientProfile.RawX), out int x, out int y)) continue;
			if (x <= 0 || y <= 0) continue;

			// Gom riêng quái thủ lĩnh/boss TRƯỚC bộ lọc tên. Lý do: khi người dùng chọn đích danh một loại quái thì
			// "Sa Hồn ( cuồng )" không khớp "Sa Hồn" và bị loại ngay ở hai dòng dưới, nên không gom ở đây thì auto
			// hoàn toàn không biết chúng nằm đâu để mà tránh — nó chỉ vô tình không đánh thôi.
			bool isElite = type == AutoFsClientProfile.MonsterType && EliteAvoidance.IsEliteName(nameBytes);

			if (isElite) {
				double eliteToCenter = GetMapDistance(centerX, centerY, x, y);
				// Nới rộng hơn Range để thấy cả con vừa ra khỏi bãi mà vùng cấm của nó còn liếm vào trong bãi.
				if (eliteToCenter <= Math.Max(settings.Range, 1) + Math.Max(settings.EliteAvoidRadius, 0)) {
					elitePositions.Add((x, y));
					if (eliteCollector != null) {
						// level đã đọc chung với type ở trên, chỉ còn phải đọc thêm hp.
						int eliteHp = reader.ReadInt32(IntPtr.Add(entityBase, AutoFsClientProfile.Hp));
						eliteCollector.Add(new AutoFsEntity(index, type, status, name, nameBytes, level, eliteHp, x, y, GetMapDistance(playerX, playerY, x, y), eliteToCenter, centerX, centerY, index == currentTargetIndex));
					}
				}
			}

			// Bộ lọc Boss, port từ AutoFS (VectorFactory.cs:3345-3432). Hai ô này dùng CHUNG một phép thử tên, chỉ
			// đảo chiều nhau; AutoFS không có danh sách tên boss riêng nào cả.
			// includeAllTargetTypes là đường quét để LIỆT KÊ danh sách quái cho ComboBox, không phải chọn mục tiêu;
			// lọc ở đó thì tên thủ lĩnh biến mất khỏi danh sách chọn.
			if (!includeAllTargetTypes && type == AutoFsClientProfile.MonsterType) {
				if (settings.OnlyAttackBoss && !isElite) continue;
				if (settings.DoNotAttackBoss && isElite) continue;
			}

			string requiredName = GetRequiredName(settings, type);
			byte[] requiredSignature = GetRequiredSignature(settings, type);
			// "Chỉ đánh Boss" phải bỏ qua bộ lọc tên, nếu không thì ô này không bao giờ chọn được con nào: tên thủ
			// lĩnh luôn có hậu tố dạng "( cuồng )" nên không khớp tên gốc trong ComboBox. AutoFS cũng nhảy thẳng tới
			// đoạn tính khoảng cách (nhãn IL_2433, VectorFactory.cs:3519) mà không so tên.
			bool bypassNameFilter = settings.OnlyAttackBoss && type == AutoFsClientProfile.MonsterType;
			if (!bypassNameFilter) {
				if (requiredSignature.Length > 0 && !nameBytes.AsSpan().SequenceEqual(requiredSignature)) continue;
				if (requiredSignature.Length == 0 && requiredName.Length > 0 && !string.Equals(name, requiredName, StringComparison.OrdinalIgnoreCase)) continue;
			}

			double distanceToCenter = GetMapDistance(centerX, centerY, x, y);
			if ((configuredTrainingMode || settings.UseCenterPosition) && distanceToCenter > Math.Max(settings.Range, 1)) continue;
			double distanceToPlayer = GetMapDistance(playerX, playerY, x, y);
			// AutoFS chặn trên khoảng cách tới nhân vật trước khi nạp vào danh sách ứng viên (VectorFactory.cs, cả hai
			// nhánh lọc tên đều là "if (num28 >= 0) { if (num28 <= 9999) { ... list.Add ... } }").
			// Khi tắt Quanh điểm thì đây là chặn duy nhất, không có nó thì auto bám cả quái ở đầu kia bản đồ.
			if (distanceToPlayer > AutoFsMaximumPlayerDistance) continue;

			int hp = reader.ReadInt32(IntPtr.Add(entityBase, AutoFsClientProfile.Hp));
			// Xác quái: máu đã về 0 nhưng client chưa kịp lật LifecycleStatus sang FinishedStatus. Trước đây không có
			// bộ lọc nào trên hp nên nó vẫn nằm trong danh sách ứng viên, và vì khoá sắp xếp đặt IsCurrentTarget lên
			// trên khoảng cách nên Auto bám luôn cái xác, bắn lệnh đánh mỗi 50ms cho tới khi client tự dọn.
			// Chính Engine.cs đã thấy chuyện này từ trước mà không xử lý — nó ghi CURRENT_TARGET_ZERO_HP_OBSERVED
			// với Action=OBSERVE_ONLY.
			// Đường liệt kê cho ComboBox (includeAllTargetTypes) KHÔNG lọc: ở đó cần thấy đủ tên quái để chọn.
			if (!includeAllTargetTypes && hp <= 0) continue;
			entities.Add(new AutoFsEntity(index, type, status, name, nameBytes, level, hp, x, y, distanceToPlayer, distanceToCenter, centerX, centerY, index == currentTargetIndex));
		}

		// Chốt danh sách ô sống cho các lượt quét nhanh kế tiếp. Chỉ lượt quét ĐỦ mới được ghi: lượt quét nhanh vốn
		// chỉ nhìn một phần bảng nên danh sách nó thấy luôn hẹp hơn, ghi đè vào là danh sách teo dần mỗi lượt.
		if (liveSlotsFound != null && ! includeAllTargetTypes) {
			lock (slotCache) {
				slotCache.Indices = liveSlotsFound.ToArray();
				slotCache.NextFullSweepUtc = DateTime.UtcNow.AddMilliseconds(FullSweepIntervalMilliseconds);
			}
		}

		// Vùng cấm quanh thủ lĩnh: loại nốt QUÁI THƯỜNG đứng trong bán kính. Đây mới là chỗ xử lý đúng vấn đề — bỏ
		// riêng con thủ lĩnh thì auto vẫn bị đám quái thường đứng sát nó kéo thẳng vào ổ.
		// Bật/tắt bằng chính ô "Không đánh Boss" (chốt với chủ dự án 2026-09-07), không có công tắc riêng.
		// includeAllTargetTypes là đường liệt kê cho ComboBox nên không lọc.
		// KHÔNG loại con ĐANG ĐÁNH DỞ: vùng cấm chỉ dùng để CHỌN mục tiêu mới, không dùng để bỏ mục tiêu đang đánh.
		//
		// Bản trước loại cả nó. Vùng cấm tính theo khoảng cách QUÁI-tới-BOSS chứ không phải nhân vật-tới-boss, nên
		// boss đi ngang qua con quái đang đánh là con đó biến khỏi danh sách ngay giữa trận, kể cả khi nhân vật đứng
		// rất xa boss. Khối sắp xếp ngay bên dưới cố giữ IsCurrentTarget nhưng vô nghĩa vì RemoveAll chạy trước.
		// Chủ dự án báo 2026-09-11: "đôi khi ngoài phạm vi của boss nhưng tao vẫn thấy nhân vật tự chuyển target".
		// Miễn trừ chỉ có hiệu lực khi NHÂN VẬT đang ở ngoài vùng an toàn của chính nó. Tránh boss là ưu tiên số 1
		// (chủ dự án chốt 2026-09-11): khi nhân vật đã bị kéo vào trong bán kính, con đang đánh dở cũng bị bỏ, nếu
		// không thì trốn xong lại quay vào đúng con đó rồi lại trốn — giằng co vô hạn.
		if (!includeAllTargetTypes && settings.DoNotAttackBoss && settings.EliteAvoidRadius > 0 && elitePositions.Count > 0) {
			// BẤT BIẾN: con quái được phép đánh phải NẰM Ở CHỖ NHÂN VẬT ĐƯỢC PHÉP ĐỨNG.
			//
			// Trước đây hai bán kính này độc lập nhau và mặc định lệch hẳn: EliteAvoidRadius=500 lọc quái, còn
			// ElitePlayerRetreatRadius=768 cấm nhân vật đứng. Con quái nằm trong dải 500..768 vừa được phép đánh vừa
			// nằm ở chỗ bị cấm đứng, nên hai luật đá nhau và sinh vòng lặp CHẮC CHẮN, không phải hãn hữu:
			// trốn đẩy ra ngoài 768 -> hết trốn -> luồng đánh kéo vào con cách boss 600 -> lại vi phạm -> trốn tiếp.
			// Chủ dự án báo 2026-09-15: "đi đến đích xong bị kéo về chỗ cũ ngay lập tức" và "cứ đi qua đi lại".
			//
			// Lấy sàn bằng bán kính cấm đứng thì dải mâu thuẫn biến mất. Vẫn cho phép đặt EliteAvoidRadius LỚN hơn
			// nếu muốn né rộng hơn nữa; chỉ chặn không cho nhỏ hơn.
			int monsterAvoidRadius = Math.Max(settings.EliteAvoidRadius, Math.Max(settings.ElitePlayerRetreatRadius, 0));
			bool playerInsideRetreatZone = EliteAvoidance.IsNearAnyElite(playerX, playerY, elitePositions, settings.ElitePlayerRetreatRadius);
			entities.RemoveAll(entity => entity.Type == AutoFsClientProfile.MonsterType
				&& (playerInsideRetreatZone || !entity.IsCurrentTarget)
				&& EliteAvoidance.IsNearAnyElite(entity.RawX, entity.RawY, elitePositions, monsterAvoidRadius));
		}

		// Giữ nguyên quái đang target (game tự báo qua CurrentTargetIndex) cho tới khi nó chết/rời danh sách ứng viên,
		// không chọn lại quái gần nhất mỗi chu kỳ nữa — luồng tự đổi target thủ công (preferredTargetIndex) đã bị bỏ,
		// nên không còn lý do phải luôn ưu tiên chọn lại theo khoảng cách mỗi lần quét.
		entities.Sort((left, right) => {
			int leftPriority = left.Index == preferredTargetIndex ? 2 : (left.IsCurrentTarget ? 1 : 0);
			int rightPriority = right.Index == preferredTargetIndex ? 2 : (right.IsCurrentTarget ? 1 : 0);
			int targetPriority = rightPriority.CompareTo(leftPriority);
			return targetPriority != 0 ? targetPriority : left.Distance.CompareTo(right.Distance);
		});
		return entities;
		} finally {
			HotPathProfiler.End(HotPathProfiler.AttackScan, profilerStart);
		}
	}

	private static IntPtr GetEntityBase(IntPtr tableBase, int index, int stride) => IntPtr.Add(tableBase, index * stride);

	private static byte[] ReadName(MemoryReader reader, IntPtr entityBase) {
		byte[] bytes = reader.ReadBytes(IntPtr.Add(entityBase, AutoFsClientProfile.Name), AutoFsClientProfile.MaximumNameLength);
		int length = Array.IndexOf(bytes, (byte)0);
		if (length < 0) length = bytes.Length;
		if (length == 0) return Array.Empty<byte>();
		return bytes.AsSpan(0, length).ToArray();
	}

	private static string GetRequiredName(Settings settings, int type) {
		if (type != AutoFsClientProfile.MonsterType) return "";
		if (settings.TrainingEnabled) return settings.TrainingMonster.Trim();
		if (settings.TeachingEnabled) return settings.TeachingMonster.Trim();
		if (settings.ContinueEnabled) return settings.ContinueMonster.Trim();
		return "";
	}

	// Xác định chế độ bãi đang giữ quyền ưu tiên đối với loại quái, tâm bãi và phạm vi quét.
	private static bool HasConfiguredTrainingMode(Settings settings) {
		return settings.TrainingEnabled || settings.TeachingEnabled || settings.ContinueEnabled;
	}

	private static byte[] GetRequiredSignature(Settings settings, int type) {
		if (type == AutoFsClientProfile.MonsterType && (settings.TrainingEnabled || settings.TeachingEnabled || settings.ContinueEnabled)) return Array.Empty<byte>();
		string signature = type == AutoFsClientProfile.PlayerType ? settings.SelectedPlayerSignature : settings.SelectedMonsterSignature;
		bool filterEnabled = type == AutoFsClientProfile.PlayerType ? settings.OnlySelectedPlayer : settings.OnlySelectedMonster;
		if (!filterEnabled || string.IsNullOrWhiteSpace(signature)) return Array.Empty<byte>();
		try {
			return Convert.FromHexString(signature);
		} catch (FormatException) {
			return Array.Empty<byte>();
		}
	}

	// Khớp đúng công thức khoảng cách của AutoFS gốc (VectorFactory.cs): Euclid thô trên tọa độ raw, không có hệ số quy đổi trục.
	private static double GetMapDistance(int firstX, int firstY, int secondX, int secondY) {
		long deltaX = (long)firstX - secondX;
		long deltaY = (long)firstY - secondY;
		return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
	}

	// Nội bộ (không private) để Loot\Engine.cs dùng lại đúng logic xác định tâm bãi khi cần đưa nhân vật về sau khi nhặt.
	internal static (int X, int Y) GetCenter(Settings settings, int playerX, int playerY) {
		if (settings.TrainingEnabled && settings.TrainingRawX > 0 && settings.TrainingRawY > 0) return (settings.TrainingRawX, settings.TrainingRawY);
		if (settings.TeachingEnabled && settings.TeachingRawX > 0 && settings.TeachingRawY > 0) return (settings.TeachingRawX, settings.TeachingRawY);
		if (settings.ContinueEnabled && settings.ContinueRawX > 0 && settings.ContinueRawY > 0) return (settings.ContinueRawX, settings.ContinueRawY);
		if (settings.UseCenterPosition && settings.CenterX > 0 && settings.CenterY > 0) return (settings.CenterX, settings.CenterY);
		return (playerX, playerY);
	}
}

internal sealed record AutoFsEntity(int Index, int Type, int Status, string Name, byte[] NameBytes, int Level, int Hp, int RawX, int RawY, double Distance, double DistanceToCenter, int CenterX, int CenterY, bool IsCurrentTarget);
