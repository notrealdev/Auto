namespace Auto.Runtime;

using System.Collections.Concurrent;
using System.IO;
using System.Text;

// Port từ D:\G\DEV\UI\Debug.cs (DebugLogView) — chỉ giữ phần logging engine, bỏ toàn bộ UI/tool-runner WinForms.
// Ngoại lệ đồng bộ: DEV không có file non-UI tương đương để diff trực tiếp (logic nằm ngay trong UI/Debug.cs,
// WinForms). Khi DEV đổi DebugLogView (tên file .log, từ khóa QueueCategorizedLog...), phải tự đọc lại
// D:\G\DEV\UI\Debug.cs và so tay với file này.
public static class DebugLog {
	private static readonly object runtimeLogLock = new();
	private static string runtimeLogPath = Path.Combine(AppContext.BaseDirectory, AppVersion.DiagnosticsDirectoryName, "auto-runtime.log");
	private static string lootDropLogPath = Path.Combine(AppContext.BaseDirectory, AppVersion.DiagnosticsDirectoryName, "loot-drops.log");
	// LOOT_SCAN chạy 200ms/lượt/account nên ghi chung sẽ đẩy loot-drops.log qua mốc xoay vòng 5 MB mỗi ~20 phút,
	// xoá mất bằng chứng nhặt thật. Tách hẳn sang file nhiễu riêng.
	private static string lootScanLogPath = Path.Combine(AppContext.BaseDirectory, AppVersion.DiagnosticsDirectoryName, "loot-scan.log");
	// Toàn bộ vòng đánh (chọn mục tiêu + gửi lệnh) gộp về một dòng duy nhất trong file này, thay cho target.log cũ
	// vốn trùng nội dung với dòng "Auto Đánh AutoFS" ghi cùng thời điểm.
	private static string attackLogPath = Path.Combine(AppContext.BaseDirectory, AppVersion.DiagnosticsDirectoryName, "attack.log");
	private static readonly string movementLogPath = Path.Combine(AppContext.BaseDirectory, AppVersion.DiagnosticsDirectoryName, "movement.log");
	private static readonly string repairLogPath = Path.Combine(AppContext.BaseDirectory, AppVersion.DiagnosticsDirectoryName, "repair.log");
	private static readonly string trainingMovementLogPath = Path.Combine(AppContext.BaseDirectory, AppVersion.DiagnosticsDirectoryName, "back-to-training.log");
	private static readonly string deathLogPath = Path.Combine(AppContext.BaseDirectory, AppVersion.DiagnosticsDirectoryName, "death.log");
	private static string buffLogPath = Path.Combine(AppContext.BaseDirectory, AppVersion.DiagnosticsDirectoryName, "buff.log");
	// Các file log tách riêng theo chức năng để auto-runtime.log chỉ còn giữ log global/core.
	private static readonly string heartbeatLogPath = Path.Combine(AppContext.BaseDirectory, AppVersion.DiagnosticsDirectoryName, "heartbeat.log");
	private static readonly string coordinationRecoveryLogPath = Path.Combine(AppContext.BaseDirectory, AppVersion.DiagnosticsDirectoryName, "anti-afk.log");
	private static readonly string saleLogPath = Path.Combine(AppContext.BaseDirectory, AppVersion.DiagnosticsDirectoryName, "sale.log");
	private static readonly string returnTalismanLogPath = Path.Combine(AppContext.BaseDirectory, AppVersion.DiagnosticsDirectoryName, "low-hp.log");
	private static readonly string advertiseLogPath = Path.Combine(AppContext.BaseDirectory, AppVersion.DiagnosticsDirectoryName, "chat.log");
	private static readonly string comboScanLogPath = Path.Combine(AppContext.BaseDirectory, AppVersion.DiagnosticsDirectoryName, "scan-list-item.log");
	private static readonly string addressAuditLogPath = Path.Combine(AppContext.BaseDirectory, AppVersion.DiagnosticsDirectoryName, "address-audit.log");
	private const int MaximumFlushCharacters = 64000;
	private const long MaximumLogBytes = 5L * 1024L * 1024L;
	private static readonly ConcurrentQueue<RuntimeLogEntry> pendingRuntimeLines = new();
	private static readonly ConcurrentDictionary<int, byte> enabledProcessLogs = new();
	private static int autoLoggingEnabled;
	private static int runtimeWriterRunning;

	public static void Add(string text) {
		if (Volatile.Read(ref autoLoggingEnabled) == 0) return;
		if (string.IsNullOrWhiteSpace(text)) return;
		QueueRuntimeLog(FormatLine(text));
	}

	public static void AddForProcess(int processId, string text) {
		if (!CanLogProcess(processId)) return;
		if (string.IsNullOrWhiteSpace(text)) return;
		QueueCategorizedLog(EnsureProcessScope(processId, text));
	}

	public static void AddDebugForProcess(int processId, string text) {
		if (processId <= 0 || string.IsNullOrWhiteSpace(text)) return;
		QueueCategorizedLog(EnsureProcessScope(processId, text));
	}

	public static string BeginAutoSession(DateTime startedAt) {
		Volatile.Write(ref autoLoggingEnabled, 1);
		const string runtimeFileName = "auto-runtime.log";
		const string lootFileName = "loot-drops.log";
		const string lootScanFileName = "loot-scan.log";
		const string attackFileName = "attack.log";
		const string buffFileName = "buff.log";
		string runtimePath = Path.Combine(AppContext.BaseDirectory, AppVersion.DiagnosticsDirectoryName, runtimeFileName);
		string lootPath = Path.Combine(AppContext.BaseDirectory, AppVersion.DiagnosticsDirectoryName, lootFileName);
		string lootScanPath = Path.Combine(AppContext.BaseDirectory, AppVersion.DiagnosticsDirectoryName, lootScanFileName);
		string attackPath = Path.Combine(AppContext.BaseDirectory, AppVersion.DiagnosticsDirectoryName, attackFileName);
		string buffPath = Path.Combine(AppContext.BaseDirectory, AppVersion.DiagnosticsDirectoryName, buffFileName);
		lock (runtimeLogLock) {
			runtimeLogPath = runtimePath;
			lootDropLogPath = lootPath;
			lootScanLogPath = lootScanPath;
			attackLogPath = attackPath;
			buffLogPath = buffPath;
		}
		Add($"Phiên Auto bắt đầu | LogFile={runtimeFileName} | LootLogFile={lootFileName} | AttackLog={attackFileName}");
		string sessionStart = $"SESSION_START | StartedAt={startedAt:O} | RuntimeLog={runtimeFileName}";
		AddLootDrop($"{sessionStart} | LootLog={lootFileName}");
		return runtimePath;
	}

	public static void EndAutoSession(string reason) {
		if (Volatile.Read(ref autoLoggingEnabled) == 0) return;
		string sessionEnd = $"SESSION_END | EndedAt={DateTime.Now:O} | Reason={reason}";
		Add("Phiên Auto kết thúc | " + sessionEnd);
		AddLootDrop(sessionEnd);
		Volatile.Write(ref autoLoggingEnabled, 0);
		enabledProcessLogs.Clear();
	}

	// Dòng chỉ có giá trị khi debug: lặp theo từng nhịp và không mang thông tin lỗi nào. Bản release bỏ hẳn, bản beta
	// giữ nguyên. Lọc ở đây thay vì ở từng nơi gọi để chỉ có một chỗ duy nhất quyết định, dễ rà lại.
	// Dòng nào có FAIL/lỗi thì luôn được giữ, kể cả khi mang từ khoá verbose.
	private static readonly string[] verboseOnlyMarkers = [
		"LOOT_SCAN",
		"Auto Đánh AutoFS",
		"BUFF_PASSIVE_SENT",
		"BUFF_PASSIVE_STATE",
		"CURRENT_TARGET_ZERO_HP_OBSERVED",
		"NO_TARGET_CORNER_WALK",
		"NO_TARGET_CORNER_REACHED"
	];

	private static bool IsSuppressedVerbose(string text) {
		if (AppVersion.IsVerboseDiagnostics) return false;
		// Phải khớp đúng hai dạng báo lỗi mà mã nguồn này dùng, KHÔNG được dùng Contains("FAIL") chung chung:
		// mọi dòng LOOT_SCAN đều chứa "ReadFail=0" và "CoordinateReadFail=0" nên phép so lỏng sẽ giữ lại toàn bộ
		// 4.700 dòng lẽ ra phải bỏ.
		if (text.Contains(" FAIL |", StringComparison.OrdinalIgnoreCase) || text.Contains("_FAILED", StringComparison.OrdinalIgnoreCase) || text.Contains("SAFE_REJECT", StringComparison.OrdinalIgnoreCase)) return false;
		foreach (string marker in verboseOnlyMarkers) {
			if (text.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;
		}
		return false;
	}

	public static void AddLootDrop(string text) {
		if (Volatile.Read(ref autoLoggingEnabled) == 0) return;
		if (string.IsNullOrWhiteSpace(text)) return;
		if (IsSuppressedVerbose(text)) return;
		string path;
		lock (runtimeLogLock) path = text.Contains("LOOT_SCAN", StringComparison.OrdinalIgnoreCase) ? lootScanLogPath : lootDropLogPath;
		QueueLog(path, FormatLine(text));
	}

	public static void AddLootDropForProcess(int processId, string text) {
		if (!CanLogProcess(processId)) return;
		AddLootDrop(EnsureProcessScope(processId, text));
	}

	// target.log đã bỏ: dòng chọn mục tiêu gộp vào attack.log, dòng đi bộ khi hết quái sang movement.log.
	// Ba lối vào này giữ nguyên chữ ký cho nơi gọi, nhưng nay đi qua bộ định tuyến từ khoá như mọi log khác.
	public static void AddTargetLifecycleForProcess(int processId, string text) {
		if (!CanLogProcess(processId)) return;
		if (string.IsNullOrWhiteSpace(text)) return;
		QueueCategorizedLog(EnsureProcessScope(processId, text));
	}

	public static void AddTargetViolationForProcess(int processId, string text) {
		if (!CanLogProcess(processId)) return;
		if (string.IsNullOrWhiteSpace(text)) return;
		QueueCategorizedLog(EnsureProcessScope(processId, text));
	}

	public static void AddTargetMovementForProcess(int processId, string text) {
		if (!CanLogProcess(processId)) return;
		if (string.IsNullOrWhiteSpace(text)) return;
		QueueCategorizedLog(EnsureProcessScope(processId, text));
	}

	// Phân loại log theo tiền tố chức năng để mỗi nhóm được ghi vào file riêng.
	private static void QueueCategorizedLog(string text) {
		if (string.IsNullOrWhiteSpace(text)) return;
		if (IsSuppressedVerbose(text)) return;
		string line = FormatLine(text);
		string? dedicatedPath = null;
		// Phải đứng trước mọi nhánh khác: tên mục audit có chứa REPAIR_ và CHAT_ nên sẽ bị định tuyến nhầm nếu xét sau.
		if (text.Contains("ADDRESS_AUDIT_", StringComparison.OrdinalIgnoreCase)) dedicatedPath = addressAuditLogPath;
		// "Sửa toàn bộ"/"độ bền" phải nằm cùng nhánh: các dòng như "Trang bị cần Sửa toàn bộ | Độ bền thấp nhất=..."
		// không chứa chữ "Sửa đồ" nên trước đây rơi nhầm xuống auto-runtime.log.
		else if (text.Contains("Sửa đồ", StringComparison.OrdinalIgnoreCase) || text.Contains("Sửa toàn bộ", StringComparison.OrdinalIgnoreCase) || text.Contains("độ bền", StringComparison.OrdinalIgnoreCase) || text.Contains("Dịch vụ NPC", StringComparison.OrdinalIgnoreCase) || text.Contains("REPAIR_", StringComparison.OrdinalIgnoreCase)) dedicatedPath = repairLogPath;
		else if (text.Contains("Lên bãi", StringComparison.OrdinalIgnoreCase) || text.Contains("Tự lên bãi", StringComparison.OrdinalIgnoreCase)) dedicatedPath = trainingMovementLogPath;
		// "rời trạng thái chết" phải liệt kê riêng: chuỗi đó không chứa cụm "Nhân vật chết" nên trước đây rơi xuống auto-runtime.log.
		else if (text.Contains("Nhân vật chết", StringComparison.OrdinalIgnoreCase) || text.Contains("Về thành", StringComparison.OrdinalIgnoreCase) || text.Contains("Xử lý khi chết", StringComparison.OrdinalIgnoreCase) || text.Contains("rời trạng thái chết", StringComparison.OrdinalIgnoreCase)) dedicatedPath = deathLogPath;
		else if (text.Contains("Auto Đánh AutoFS", StringComparison.OrdinalIgnoreCase) || text.Contains("TARGET_SELECTED", StringComparison.OrdinalIgnoreCase) || text.Contains("CURRENT_TARGET_", StringComparison.OrdinalIgnoreCase)) dedicatedPath = attackLogPath;
		else if (text.Contains("NO_TARGET_CORNER_", StringComparison.OrdinalIgnoreCase) || text.Contains("ELITE_", StringComparison.OrdinalIgnoreCase) || text.Contains("Đổi map", StringComparison.OrdinalIgnoreCase)) dedicatedPath = movementLogPath;
		else if (text.Contains("BUFF_", StringComparison.OrdinalIgnoreCase)) dedicatedPath = buffLogPath;
		else if (text.Contains("HEARTBEAT_", StringComparison.OrdinalIgnoreCase)) dedicatedPath = heartbeatLogPath;
		else if (text.Contains("ANTI_AFK_", StringComparison.OrdinalIgnoreCase)) dedicatedPath = coordinationRecoveryLogPath;
		else if (text.Contains("SCAN_LIST_ITEM_", StringComparison.OrdinalIgnoreCase)) dedicatedPath = comboScanLogPath;
		else if (text.Contains("LOW_HP_", StringComparison.OrdinalIgnoreCase)) dedicatedPath = returnTalismanLogPath;
		else if (text.Contains("CHAT_", StringComparison.OrdinalIgnoreCase)) dedicatedPath = advertiseLogPath;
		else if (text.Contains("SALE_", StringComparison.OrdinalIgnoreCase)) dedicatedPath = saleLogPath;
		if (dedicatedPath == null) QueueRuntimeLog(line);
		else QueueLog(dedicatedPath, line);
	}

	private static string EnsureProcessScope(int processId, string text) {
		return text.Contains($"PID={processId}", StringComparison.OrdinalIgnoreCase) ? text : $"PID={processId} | {text}";
	}

	public static void SetProcessLoggingEnabled(int processId, bool enabled) {
		if (processId <= 0) return;
		if (enabled) enabledProcessLogs[processId] = 0;
		else enabledProcessLogs.TryRemove(processId, out _);
	}

	// Cho phép nơi gọi biết log của tiến trình có đang bật không, để không ghi nhớ trạng thái chống trùng khi dòng log sẽ bị bỏ.
	public static bool IsProcessLoggingEnabled(int processId) {
		return CanLogProcess(processId);
	}

	private static bool CanLogProcess(int processId) {
		return Volatile.Read(ref autoLoggingEnabled) != 0 && enabledProcessLogs.ContainsKey(processId);
	}

	private static string FormatLine(string text) => $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {text}\r\n";

	private static void QueueRuntimeLog(string line) {
		string path;
		lock (runtimeLogLock) path = runtimeLogPath;
		QueueLog(path, line);
	}

	private static void QueueLog(string path, string line) {
		if (!DebugFileLogging.Enabled) return;
		pendingRuntimeLines.Enqueue(new RuntimeLogEntry(path, line));
		if (Interlocked.CompareExchange(ref runtimeWriterRunning, 1, 0) == 0) _ = Task.Run(FlushRuntimeLogQueue);
	}

	private static void FlushRuntimeLogQueue() {
		while (true) {
			StringBuilder batch = new();
			string? path = null;
			while (batch.Length < MaximumFlushCharacters && pendingRuntimeLines.TryPeek(out RuntimeLogEntry next)) {
				if (path != null && !string.Equals(path, next.Path, StringComparison.OrdinalIgnoreCase)) break;
				if (!pendingRuntimeLines.TryDequeue(out RuntimeLogEntry entry)) continue;
				path ??= entry.Path;
				batch.Append(entry.Line);
			}
			if (batch.Length > 0 && path != null) WriteRuntimeLogBatch(path, batch.ToString());
			if (!pendingRuntimeLines.IsEmpty) continue;
			Volatile.Write(ref runtimeWriterRunning, 0);
			if (pendingRuntimeLines.IsEmpty || Interlocked.CompareExchange(ref runtimeWriterRunning, 1, 0) != 0) return;
		}
	}

	private static void WriteRuntimeLogBatch(string path, string text) {
		if (!DebugFileLogging.Enabled) return;
		try {
			lock (runtimeLogLock) {
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				RotateLogIfNeeded(path, Encoding.UTF8.GetByteCount(text));
				File.AppendAllText(path, text, Encoding.UTF8);
			}
		} catch (Exception ex) {
			WriteLogFailure(path, text, ex);
		}
	}

	private static void WriteLogFailure(string failedPath, string failedText, Exception exception) {
		if (!DebugFileLogging.Enabled) return;
		try {
			string diagnosticsDirectory = Path.Combine(AppContext.BaseDirectory, AppVersion.DiagnosticsDirectoryName);
			Directory.CreateDirectory(diagnosticsDirectory);
			string emergencyPath = Path.Combine(diagnosticsDirectory, "log-writer-errors.log");
			string preview = failedText.Length <= 500 ? failedText : failedText[..500] + "...";
			string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] LOG_WRITE_FAILURE | Path={failedPath} | Characters={failedText.Length} | Exception={exception.GetType().Name}: {exception.Message} | Preview={preview.Replace("\r", "\\r").Replace("\n", "\\n")}\r\n";
			lock (runtimeLogLock) {
				RotateLogIfNeeded(emergencyPath, Encoding.UTF8.GetByteCount(line));
				File.AppendAllText(emergencyPath, line, Encoding.UTF8);
			}
		} catch (Exception emergencyException) {
			System.Diagnostics.Trace.WriteLine($"LOG_WRITE_FAILURE | Primary={exception} | Emergency={emergencyException}");
		}
	}

	private static void RotateLogIfNeeded(string path, int incomingBytes) {
		if (!File.Exists(path) || new FileInfo(path).Length + incomingBytes <= MaximumLogBytes) return;
		string directory = Path.GetDirectoryName(path)!;
		string name = Path.GetFileNameWithoutExtension(path);
		string extension = Path.GetExtension(path);
		string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff");
		string archivePath = Path.Combine(directory, $"{name}_{timestamp}{extension}");
		File.Move(path, archivePath);
	}

	private readonly record struct RuntimeLogEntry(string Path, string Line);
}
