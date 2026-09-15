namespace Auto.Runtime;

using System.Collections.Concurrent;
using System.Diagnostics;

// Đồng hồ đo các đoạn chạy nóng, để biết CHÍNH XÁC đoạn nào ăn CPU thay vì suy luận.
//
// Vì sao cần: các lượt tối ưu trước đều dựa trên ước lượng "mỗi lần đọc bộ nhớ tốn bao nhiêu us" đo bằng benchmark
// ngoài, mà benchmark đó đọc trong chính tiến trình mình (cache nóng) nên lệch xa thực tế đọc xuyên tiến trình.
// Hai lần suy luận sai liên tiếp; lần này đo tại chỗ.
//
// Chi phí của chính bộ đo: mỗi lần Begin/End là hai lần Stopwatch.GetTimestamp (QPC, cỡ chục nano-giây). Chỉ bọc ở
// mức TỪNG LƯỢT QUÉT (khoảng trăm lần mỗi giây), KHÔNG bọc từng lần đọc bộ nhớ (hàng vạn lần mỗi giây) — bọc ở đó
// thì bộ đo còn đắt hơn thứ nó đo.
public static class HotPathProfiler {
	public const string AttackScan = "QuétĐánh";
	public const string PetRead = "ĐọcĐệ";
	public const string Snapshot = "ĐọcNhânVật";
	public const string WindowScan = "QuétCửaSổ";
	public const string LootScan = "QuétNhặt";
	public const string AccountTick = "MộtNhịpAccount";

	private const int ReportIntervalSeconds = 10;
	// CHỈ ghi perf.log khi CPU thật của tiến trình vượt ngưỡng này (chủ dự án chốt 2026-09-15): chạy bình thường thì
	// file im lặng, chỉ lên tiếng lúc hiệu năng tụt.
	//
	// Ngưỡng lấy từ hai mốc ĐO ĐƯỢC trên máy chủ dự án, không phải đoán:
	//   sau tối ưu  : 42ms/s CPU  = 4,2% của một lõi -> Task Manager 0,5%, Power usage "Very low"
	//   trước tối ưu: 179ms/s CPU = 17,9% của một lõi -> Task Manager 2,1%, Power usage "Moderate"
	// Lấy 8% của một lõi: trên gần gấp đôi mức bình thường nên không kêu oan, dưới xa mức hỏng nên không bỏ sót.
	private const double ReportCpuPercentOfOneCoreThreshold = 8.0;
	private static readonly ConcurrentDictionary<string, Counter> counters = new(StringComparer.Ordinal);
	private static long windowStartTimestamp = Stopwatch.GetTimestamp();
	private static int reportInProgress;

	public static long Begin() => Stopwatch.GetTimestamp();

	public static void End(string section, long startTimestamp) {
		Counter counter = counters.GetOrAdd(section, _ => new Counter());
		Interlocked.Add(ref counter.ElapsedTimestamp, Stopwatch.GetTimestamp() - startTimestamp);
		Interlocked.Increment(ref counter.Calls);
		TryReport();
	}

	// Gom báo cáo ngay trong luồng gọi thay vì dựng thêm timer riêng: một luồng duy nhất lọt qua CompareExchange,
	// các luồng khác đi thẳng nên không ai bị chặn.
	private static void TryReport() {
		long now = Stopwatch.GetTimestamp();
		double elapsedSeconds = (double)(now - Volatile.Read(ref windowStartTimestamp)) / Stopwatch.Frequency;
		if (elapsedSeconds < ReportIntervalSeconds) return;
		if (Interlocked.CompareExchange(ref reportInProgress, 1, 0) != 0) return;
		try {
			Volatile.Write(ref windowStartTimestamp, now);
			// Lấy mẫu CPU thật rồi mới quyết định có ghi không. PHẢI xoá sạch bộ đếm kể cả khi không ghi, nếu không
			// số của cửa sổ này dồn sang cửa sổ sau và dòng log lúc thật sự tụt hiệu năng sẽ bị thổi phồng.
			ProcessLoadMonitor.Sample();
			double cpuPercentOfOneCore = ProcessLoadMonitor.CpuPercent * Math.Max(1, Environment.ProcessorCount);
			bool degraded = cpuPercentOfOneCore >= ReportCpuPercentOfOneCoreThreshold;
			List<string> parts = [];
			double totalMillisecondsPerSecond = 0;
			string topSection = "";
			double topMillisecondsPerSecond = -1;
			foreach (string section in counters.Keys.OrderBy(key => key, StringComparer.Ordinal)) {
				if (! counters.TryGetValue(section, out Counter? counter)) continue;
				long ticks = Interlocked.Exchange(ref counter.ElapsedTimestamp, 0);
				long calls = Interlocked.Exchange(ref counter.Calls, 0);
				if (calls == 0) continue;
				double totalMilliseconds = (double)ticks / Stopwatch.Frequency * 1000.0;
				double millisecondsPerSecond = totalMilliseconds / elapsedSeconds;
				totalMillisecondsPerSecond += millisecondsPerSecond;
				if (millisecondsPerSecond > topMillisecondsPerSecond) {
					topMillisecondsPerSecond = millisecondsPerSecond;
					topSection = section;
				}
				parts.Add($"{section}={millisecondsPerSecond:F1}ms/s ({calls / elapsedSeconds:F0} lần/s, {totalMilliseconds * 1000 / calls:F0}us/lần)");
			}
			if (parts.Count == 0 || ! degraded) return;
			// Dòng tóm tắt đứng đầu: chủ dự án đọc thẳng 1 câu là biết vượt ngưỡng bao nhiêu lần và đoạn nào tốn nhất,
			// khỏi phải tự so 6 con số phía sau. "Tốn nhất" so trên TổngThờiGianTường của từng đoạn — chỉ đúng khi so
			// TƯƠNG ĐỐI giữa các đoạn với nhau (xem giải thích NHÃN bên dưới), không phải % CPU tuyệt đối của đoạn đó.
			double overCapRatio = cpuPercentOfOneCore / ReportCpuPercentOfOneCoreThreshold;
			string summary = $"CPU cao gấp {overCapRatio:F1} lần ngưỡng bình thường | Tốn nhất: {topSection} ({topMillisecondsPerSecond:F1}ms/s)";
			// NHÃN PHẢI GHI RÕ "thời gian tường": Stopwatch đếm cả lúc luồng đang CHỜ (khoá AutoFsActionGate,
			// SendMessageTimeout gửi lệnh vào client) chứ không riêng lúc đốt CPU.
			// Đo đối chứng 2026-09-14: bộ đo này báo 517ms/s trong khi CPU thật của tiến trình (TotalProcessorTime)
			// chỉ 179ms/s — chênh 2,9 lần. Bản đầu ghi nhầm là "TổngCPU ... % của 1 lõi" nên đọc ra sẽ thổi phồng.
			// Các mục con vẫn dùng được để SO SÁNH TƯƠNG ĐỐI với nhau, chỉ đừng đọc số tuyệt đối như CPU.
			DebugLog.AddPerf($"PERF_TỤT_HIỆU_NĂNG | {summary} | CPU={cpuPercentOfOneCore:F1}% của 1 lõi (ngưỡng {ReportCpuPercentOfOneCoreThreshold:F0}%) | Cửa sổ={elapsedSeconds:F0}s | TổngThờiGianTường={totalMillisecondsPerSecond:F0}ms/s (gồm cả lúc chờ khoá/gửi lệnh, KHÔNG phải CPU thuần) | {string.Join(" | ", parts)}");
		} finally {
			Volatile.Write(ref reportInProgress, 0);
		}
	}

	private sealed class Counter {
		public long ElapsedTimestamp;
		public long Calls;
	}
}
