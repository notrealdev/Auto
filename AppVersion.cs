namespace Auto;

using System.IO;

public static class AppVersion {
	public static string Current {
		get {
			Version version = typeof(AppVersion).Assembly.GetName().Version ?? new Version(1, 0, 0);
			return version.ToString(4);
		}
	}

	public static string Title {
		get {
#if AUTO_BETA
			return "Auto " + Current + "-beta";
#else
			return "Auto " + Current;
#endif
		}
	}

	// Hai luồng log tách hẳn nhau: "Diagnostics" chỉ dành cho bản Release ổn định, "DiagnosticsBeta" cho mọi bản
	// test/debug. Nhận diện theo ba dấu hiệu độc lập để một bản test không bao giờ ghi đè log của bản ổn định:
	// tên executable có hậu tố -beta, ký hiệu AUTO_BETA do Build-Release.ps1 -Beta định nghĩa, và cấu hình Debug.
	public static string DiagnosticsDirectoryName {
		get {
			return IsBetaBuild ? "DiagnosticsBeta" : "Diagnostics";
		}
	}

	// Bản beta ghi đủ cả dòng thành công theo từng nhịp để soi được lỗi kiểu "gửi đúng lệnh nhưng client làm sai";
	// bản release chỉ ghi lỗi và mốc chuyển trạng thái. Đo 2026-09-06: release ghi ~37 MB trong 1h20m, trong đó
	// loot-scan/attack/buff/movement là 14.840/14.847 dòng đều không có lỗi.
	public static bool IsVerboseDiagnostics => IsBetaBuild;

	private static bool IsBetaBuild {
		get {
			string executableName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? string.Empty);
			if (executableName.EndsWith("-beta", StringComparison.OrdinalIgnoreCase)) return true;
#if AUTO_BETA || DEBUG
			return true;
#else
			return false;
#endif
		}
	}
}
