namespace Auto.Attack;

internal sealed class AutoFsActionGate {
	public const string ReturnMovementOwner = "RETURN_MOVEMENT";
	public const string TrainingMovementOwner = "TRAINING_MOVEMENT";
	public const string SaleRepairOwner = "SALE_REPAIR";
	private readonly object syncRoot = new();
	private readonly object suspensionSyncRoot = new();
	private readonly HashSet<string> lootSuspensionOwners = new(StringComparer.Ordinal);
	private volatile bool automationEnabled;
	private volatile bool runtimeSuspended;

	public bool MasterEnabled => automationEnabled;

	public bool AutomationEnabled => automationEnabled && ! runtimeSuspended;

	// Bật hoặc khóa toàn bộ đường gửi hành động tự động của tài khoản
	public void SetAutomationEnabled(bool enabled) {
		automationEnabled = enabled;
	}

	// Temporarily block every automatic command without changing the user's master setting.
	public void SetRuntimeSuspended(bool suspended) {
		runtimeSuspended = suspended;
	}

	public bool IsLootSuspended {
		get {
			lock (suspensionSyncRoot) return lootSuspensionOwners.Count > 0;
		}
	}

	public string GetLootSuspensionState() {
		lock (suspensionSyncRoot) return lootSuspensionOwners.Count == 0 ? "NONE" : string.Join(",", lootSuspensionOwners.OrderBy(owner => owner, StringComparer.Ordinal));
	}

	public void RunLoot(Action action) {
		lock (syncRoot) {
			if (AutomationEnabled && ! IsLootSuspended) action();
		}
	}

	public bool TryRunAttack(Action action) {
		lock (syncRoot) {
			if (! AutomationEnabled || IsLootSuspended) return false;
			action();
			return true;
		}
	}

	public bool RunCommand(Func<bool> action) {
		lock (syncRoot) return AutomationEnabled && action();
	}

	public bool RunMovement(Func<bool> action) {
		lock (syncRoot) return AutomationEnabled && action();
	}

	// Đường dành riêng cho DebugTools: BỎ QUA công tắc tổng nhưng VẪN giữ khoá syncRoot.
	//
	// Lý do bỏ công tắc: công cụ chẩn đoán do chủ dự án tự bấm từng lần, và nó thường phải chạy đúng lúc tài khoản
	// đang tắt để thử một mình. Bắt bật công tắc tổng thì hoặc là không chẩn được, hoặc phải bật cả automation lên
	// rồi các engine khác cùng chen lệnh vào, làm bẩn phép đo.
	//
	// Lý do GIỮ khoá: hai lệnh gửi đồng thời vào cùng một client vẫn hỏng như thường, kể cả khi một trong hai là
	// lệnh chẩn đoán. Khoá là thứ chống việc đó, không liên quan gì tới công tắc bật/tắt.
	public bool RunDebugCommand(Func<bool> action) {
		lock (syncRoot) return action();
	}

	public void SetLootSuspended(string owner, bool suspended) {
		lock (suspensionSyncRoot) {
			if (suspended) lootSuspensionOwners.Add(owner);
			else lootSuspensionOwners.Remove(owner);
		}
	}
}
