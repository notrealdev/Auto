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

	// Đọc HP Đệ từ entity type 6 bằng chính entity layout đã xác nhận của client hiện tại.
	public static PetHealthReading Read(int processId) {
		try {
			using MemoryReader reader = new(processId);
			RuntimeLayout layout = RuntimeLayoutResolver.Resolve(processId);
			if (! layout.Get(RuntimeSubsystem.Entity).Available) return PetHealthReading.Fail("Entity layout chưa sẵn sàng.");
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			IntPtr entityTable = reader.ReadPointer32(IntPtr.Add(moduleBase, layout.EntityTableRva));
			if (entityTable == IntPtr.Zero) return PetHealthReading.Fail("EntityTable bằng 0.");
			List<PetHealthReading> candidates = [];
			for (int index = AutoFsClientProfile.FirstEntityIndex; index <= AutoFsClientProfile.LastEntityIndex; index++) {
				IntPtr entity = IntPtr.Add(entityTable, index * layout.EntityStride);
				if (reader.ReadInt32(IntPtr.Add(entity, AutoFsClientProfile.EntityType)) != PetEntityType) continue;
				if (reader.ReadInt32(IntPtr.Add(entity, AutoFsClientProfile.LifecycleStatus)) == AutoFsClientProfile.FinishedStatus) continue;
				int currentHp = reader.ReadInt32(IntPtr.Add(entity, layout.HpOffset));
				int maximumHp = reader.ReadInt32(IntPtr.Add(entity, layout.MaxHpOffset));
				if (currentHp <= 0 || maximumHp <= 0 || currentHp > maximumHp) continue;
				candidates.Add(new PetHealthReading(true, true, index, currentHp, maximumHp, $"EntityType={PetEntityType}; EntityIndex={index}; Source=CURRENT_ENTITY_LAYOUT."));
			}
			if (candidates.Count == 0) return PetHealthReading.Missing("Không có entity type 6 đang sống với cặp HP hợp lệ.");
			if (candidates.Count > 1) return PetHealthReading.Fail($"Có {candidates.Count} entity type 6; chưa đủ bằng chứng xác định Đệ của nhân vật.");
			return candidates[0];
		} catch (Exception ex) {
			return PetHealthReading.Fail($"{ex.GetType().Name}: {ex.Message}");
		}
	}
}
