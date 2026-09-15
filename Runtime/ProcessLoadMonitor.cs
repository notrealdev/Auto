namespace Auto.Runtime;

using System.Diagnostics;
using System.Runtime.InteropServices;

// Đo tải của CHÍNH tiến trình Auto: %CPU và bộ nhớ, để hiển thị lên thanh trạng thái và để quyết định có ghi
// perf.log hay không.
//
// Vì sao đo CPU thật chứ không dùng số của HotPathProfiler: bộ đo đó đếm THỜI GIAN TƯỜNG, gồm cả lúc luồng đứng
// chờ khoá hay chờ SendMessageTimeout. Đo đối chứng 2026-09-14: profiler báo 517ms/s trong khi CPU thật chỉ
// 179ms/s — chênh 2,9 lần. Chỉ TotalProcessorTime mới là thứ khớp với cột CPU của Task Manager.
public static class ProcessLoadMonitor {
	private static readonly int LogicalProcessorCount = Math.Max(1, Environment.ProcessorCount);
	private static readonly object syncRoot = new();
	private static TimeSpan lastCpuTime;
	private static DateTime lastSampleUtc;
	private static double cpuPercent;
	private static double workingSetMegabytes;
	private static PerformanceCounter? privateWorkingSetCounter;
	private static int counterResolveStarted;

	public static double CpuPercent {
		get { lock (syncRoot) return cpuPercent; }
	}

	public static double WorkingSetMegabytes {
		get { lock (syncRoot) return workingSetMegabytes; }
	}

	public static void Sample() {
		try {
			using Process process = Process.GetCurrentProcess();
			TimeSpan cpuNow = process.TotalProcessorTime;
			DateTime now = DateTime.UtcNow;
			long privateWorkingSetBytes = ReadPrivateWorkingSetBytes(process);
			lock (syncRoot) {
				// Không có P/Invoke công khai nào trả thẳng "private working set" (structure EX2 mà bản trước
				// dùng không tồn tại trong Windows API thật, GetProcessMemoryInfo luôn fail và rớt về WorkingSet64
				// -> đó là lý do số hiển thị bị thổi phồng gấp 3-4 lần so với Task Manager).
				// Nguồn đáng tin duy nhất là perf counter "Working Set - Private", đúng cái Task Manager dùng.
				workingSetMegabytes = (privateWorkingSetBytes > 0 ? privateWorkingSetBytes : process.WorkingSet64) / 1024.0 / 1024.0;
				if (lastSampleUtc != DateTime.MinValue) {
					double elapsedSeconds = (now - lastSampleUtc).TotalSeconds;
					if (elapsedSeconds >= 0.2) {
						double busySeconds = (cpuNow - lastCpuTime).TotalSeconds;
						cpuPercent = Math.Clamp(busySeconds / elapsedSeconds / LogicalProcessorCount * 100.0, 0, 100);
						lastCpuTime = cpuNow;
						lastSampleUtc = now;
					}
					return;
				}
				lastCpuTime = cpuNow;
				lastSampleUtc = now;
			}
		} catch {
		}
	}

	// DỰNG COUNTER PHẢI CHẠY NỀN, KHÔNG ĐƯỢC GỌI THẲNG.
	//
	// Đo 2026-09-15 trên máy chủ dự án (322 tiến trình đang chạy), tách từng bước:
	//   PerformanceCounterCategory("Process").GetInstanceNames() = 5094 ms   <- toàn bộ chi phí nằm ở đây
	//   dò instance theo PID = 34 ms | new PerformanceCounter = 0 ms | NextValue() lần đầu = 18 ms
	// Mà Sample() được gọi thẳng trong constructor của MainWindowViewModel, constructor đó lại nằm trong constructor
	// của MainWindow (MainWindow.xaml.cs:12) chạy trước App.xaml.cs:24 Show() — nên cả 5 giây đó đứng chắn trên
	// luồng UI và cửa sổ Auto mãi mới hiện ra. Chủ dự án báo đúng hiện tượng này.
	//
	// Trong lúc chờ counter dựng xong thì trả 0 để nơi gọi lùi tạm về WorkingSet64: vài giây đầu số RAM sẽ cao hơn
	// thật (WorkingSet64 gồm cả trang dùng chung), sau đó tự về đúng. Đổi lại cửa sổ hiện ra ngay.
	private static long ReadPrivateWorkingSetBytes(Process process) {
		PerformanceCounter? counter = Volatile.Read(ref privateWorkingSetCounter);
		if (counter == null) {
			if (Interlocked.CompareExchange(ref counterResolveStarted, 1, 0) == 0) Task.Run(ResolveCounter);
			return 0;
		}
		try {
			return (long)counter.NextValue();
		} catch {
			// Trường hợp instance bị đổi tên (VD chạy 2 bản cùng lúc, bản kia đóng trước) -> ép dựng lại lần sau.
			Volatile.Write(ref privateWorkingSetCounter, null);
			Volatile.Write(ref counterResolveStarted, 0);
			return 0;
		}
	}

	private static void ResolveCounter() {
		try {
			using Process process = Process.GetCurrentProcess();
			Volatile.Write(ref privateWorkingSetCounter, new PerformanceCounter("Process", "Working Set - Private", GetInstanceName(process)));
		} catch {
			// Dựng hỏng thì mở cờ cho lần sau thử lại; từ giờ tới đó vẫn lùi về WorkingSet64, không làm hỏng luồng gọi.
			Volatile.Write(ref counterResolveStarted, 0);
		}
	}

	private static string GetInstanceName(Process process) {
		var category = new PerformanceCounterCategory("Process");
		foreach (string instance in category.GetInstanceNames()) {
			if (!instance.StartsWith(process.ProcessName, StringComparison.OrdinalIgnoreCase)) continue;
			try {
				using var pidCounter = new PerformanceCounter("Process", "ID Process", instance, true);
				if ((int)pidCounter.RawValue == process.Id) return instance;
			} catch { }
		}
		return process.ProcessName;
	}

	public static string DescribeLoad(double percent) {
		if (percent < 1.0) return "very low";
		if (percent < 3.0) return "low";
		if (percent < 6.0) return "moderate";
		return "high";
	}
}