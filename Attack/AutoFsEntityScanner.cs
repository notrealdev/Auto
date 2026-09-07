namespace Auto.Attack;

using Auto.Runtime;
using Auto.Utils;

internal sealed class AutoFsEntityScanner {
	private const int AutoFsMaximumPlayerDistance = 9999;

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
	public IReadOnlyList<AutoFsEntity> Scan(MemoryReader reader, Settings settings, out int playerX, out int playerY, out int playerLifecycleStatus, bool includeAllTargetTypes = false, int preferredTargetIndex = -1) {
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

		for (int index = AutoFsClientProfile.FirstEntityIndex; index <= AutoFsClientProfile.LastEntityIndex; index++) {
			IntPtr entityBase = GetEntityBase(tableBase, index, layout.EntityStride);
			int status = reader.ReadInt32(IntPtr.Add(entityBase, AutoFsClientProfile.LifecycleStatus));
			if (status == AutoFsClientProfile.FinishedStatus) continue;

			int type = reader.ReadInt32(IntPtr.Add(entityBase, AutoFsClientProfile.EntityType));
			bool acceptedType = includeAllTargetTypes
				? type == AutoFsClientProfile.MonsterType || type == AutoFsClientProfile.PlayerType
				: (configuredTrainingMode || settings.AttackMonsters) && type == AutoFsClientProfile.MonsterType;
			if (!acceptedType) continue;

			byte[] nameBytes = ReadName(reader, entityBase);
			if (nameBytes.Length == 0) continue;
			string name = LegacyVietnameseText.Decode(nameBytes);
			string requiredName = GetRequiredName(settings, type);
			byte[] requiredSignature = GetRequiredSignature(settings, type);
			if (requiredSignature.Length > 0 && !nameBytes.AsSpan().SequenceEqual(requiredSignature)) continue;
			if (requiredSignature.Length == 0 && requiredName.Length > 0 && !string.Equals(name, requiredName, StringComparison.OrdinalIgnoreCase)) continue;

			int x = reader.ReadInt32(IntPtr.Add(entityBase, AutoFsClientProfile.RawX));
			int y = reader.ReadInt32(IntPtr.Add(entityBase, AutoFsClientProfile.RawY));
			if (x <= 0 || y <= 0) continue;

			double distanceToCenter = GetMapDistance(centerX, centerY, x, y);
			if ((configuredTrainingMode || settings.UseCenterPosition) && distanceToCenter > Math.Max(settings.Range, 1)) continue;
			double distanceToPlayer = GetMapDistance(playerX, playerY, x, y);
			// AutoFS chặn trên khoảng cách tới nhân vật trước khi nạp vào danh sách ứng viên (VectorFactory.cs, cả hai
			// nhánh lọc tên đều là "if (num28 >= 0) { if (num28 <= 9999) { ... list.Add ... } }").
			// Khi tắt Quanh điểm thì đây là chặn duy nhất, không có nó thì auto bám cả quái ở đầu kia bản đồ.
			if (distanceToPlayer > AutoFsMaximumPlayerDistance) continue;

			int hp = reader.ReadInt32(IntPtr.Add(entityBase, AutoFsClientProfile.Hp));
			int level = reader.ReadInt32(IntPtr.Add(entityBase, AutoFsClientProfile.Level));
			entities.Add(new AutoFsEntity(index, type, status, name, nameBytes, level, hp, x, y, distanceToPlayer, distanceToCenter, centerX, centerY, index == currentTargetIndex));
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
