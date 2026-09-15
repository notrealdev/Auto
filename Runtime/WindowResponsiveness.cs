namespace Auto.Runtime;

using System.Diagnostics;
using System.Runtime.InteropServices;

// Phát hiện cửa sổ game bị TREO (đóng băng) — khác hẳn ca cửa sổ biến mất mà GAME_WINDOW_LOST lo.
//
// Vì sao cần (chủ dự án báo 2026-09-15): client thỉnh thoảng đơ cứng nhưng KHÔNG chết, nên Auto vẫn thấy cửa sổ,
// vẫn giữ account trong danh sách, vẫn bắn lệnh — và log không có lấy một dấu hiệu nào. Lần gần nhất tôi truy
// PID=16116 thì không tìm được gì vì không có đồng hồ nào đo thứ này.
//
// "Treo" trong Win32 có nghĩa rất cụ thể: luồng UI ngừng bơm message queue. Auto giao tiếp với client đúng qua
// message (SendMessageTimeoutA + PostMessageA trong AutoFsAttackTransport), nên đây vừa là thứ đo được, vừa là
// thứ ảnh hưởng trực tiếp tới mọi lệnh Auto gửi.
//
// Đo nền 2026-09-15 trên cả 6 client đang chạy bình thường: IsHungAppWindow=False, WM_NULL quay về trong 11-15ms
// (đúng một khung hình 60fps — client bơm message mỗi frame). Nên mốc "lành" là ~15ms.
internal static class WindowResponsiveness {
	// IsHungAppWindow chỉ HỎI window manager, không gửi message nào nên gần như miễn phí — gọi mỗi nhịp quét được.
	// Nó bật true sau khoảng 5 giây cửa sổ không bơm message, thừa nhanh cho một cú đơ thật.
	[DllImport("user32.dll")]
	private static extern bool IsHungAppWindow(IntPtr windowHandle);

	// Chỉ dùng để ĐO ĐỘ NẶNG sau khi đã biết là treo, không gọi ở đường chạy bình thường: mỗi lần là một chuyến
	// đồng bộ sang luồng UI của client.
	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr SendMessageTimeoutA(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeoutMilliseconds, out UIntPtr result);

	private const uint WmNull = 0x0000;
	private const uint SmtoAbortIfHung = 0x0002;
	private const uint ProbeTimeoutMilliseconds = 1000;
	private static readonly Dictionary<int, DateTime> hungSinceByProcessId = new();

	// Gọi mỗi nhịp quét cửa sổ. Ghi đúng MỘT dòng lúc bắt đầu treo và MỘT dòng lúc hồi phục, không lặp mỗi nhịp.
	public static void Probe(int processId, IntPtr windowHandle, string characterName) {
		if (windowHandle == IntPtr.Zero) return;
		bool hung = IsHungAppWindow(windowHandle);
		lock (hungSinceByProcessId) {
			if (hung) {
				if (hungSinceByProcessId.ContainsKey(processId)) return;
				hungSinceByProcessId[processId] = DateTime.UtcNow;
			} else {
				if (! hungSinceByProcessId.Remove(processId, out DateTime hungSince)) return;
				DebugLog.AddClientEvent($"GAME_WINDOW_RESPONSIVE_AGAIN | PID={processId} | {characterName} | TreoTrong={(int)(DateTime.UtcNow - hungSince).TotalMilliseconds}ms");
				return;
			}
		}
		// Chỉ tới đây khi VỪA chuyển sang treo. Đo thêm một chuyến WM_NULL để ghi lại mức độ; nền lành là 11-15ms.
		Stopwatch stopwatch = Stopwatch.StartNew();
		IntPtr answered = SendMessageTimeoutA(windowHandle, WmNull, IntPtr.Zero, IntPtr.Zero, SmtoAbortIfHung, ProbeTimeoutMilliseconds, out _);
		stopwatch.Stop();
		string roundTrip = answered == IntPtr.Zero
			? $"WM_NULL=KHÔNG_TRẢ_LỜI sau {stopwatch.ElapsedMilliseconds}ms (Win32Error={Marshal.GetLastWin32Error()})"
			: $"WM_NULL={stopwatch.ElapsedMilliseconds}ms";
		DebugLog.AddClientEvent($"GAME_WINDOW_HUNG | PID={processId} | {characterName} | {roundTrip} | NềnLành=11-15ms | Action=Chỉ ghi nhận, Auto không đổi hành vi");
	}

	// Cửa sổ biến mất thì bỏ luôn trạng thái treo, không thì lần sau mở lại bị coi là "vừa hồi phục".
	public static void Forget(int processId) {
		lock (hungSinceByProcessId) hungSinceByProcessId.Remove(processId);
	}
}
