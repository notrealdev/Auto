namespace Auto.DebugTools;

using System.Text;
using Auto.Runtime;
using Auto.Utils;

// Dò bước còn thiếu để bỏ chuột giả lập khi click NPC: gọi hàm "chọn entity theo chỉ số" của client 1.28
// (vtable manager + 0x1C) rồi đọc lại biến mục tiêu ở RVA 0x4CE688 xem có ghi thật không.
//
// Vì sao phải đọc lại thay vì tin giá trị trả về của lệnh: ngày 2026-09-17 lệnh 8 chép từ AutoFS được gửi 20 lần,
// PostMessageA lần nào cũng thành công nên phía C# báo click xong, trong khi DLL của Auto KHÔNG có handler nào
// khớp số 8 nên message bị bỏ im lặng — 20/20 chuyến Sửa đồ hỏng (repair.log 22:37-22:38).
//
// Probe này KHÔNG đụng vào luồng Sửa đồ. Nó chỉ chọn mục tiêu; nếu hội thoại có mở ra thì đó là thông tin cần biết.
public static class SelectEntityProbe {
	private const string DoctorName = "Đại phu";
	// Phải khớp NativeBuildStamp trong Native/SystemUint/SystemUint.cpp.
	private const ulong RequiredNativeBuildStamp = 99990004;

	public static string Run(GameWindow game) {
		StringBuilder report = new();
		report.AppendLine($"PID={game.ProcessId} | Dò lệnh chọn entity (vtable +0x1C)");

		// DLL sống lâu hơn một phiên Auto: client đang mở vẫn giữ bản cũ sau khi build lại native, mà đo trên bản cũ
		// thì mọi kết luận đều vô nghĩa. Phải kiểm dấu bản trước mọi thứ khác.
		if (game.AutoFsTransport.TryQueryNativeBuildStamp(game.Handle, out ulong stamp, out string stampError)) {
			report.AppendLine($"Dấu bản native = {stamp}{(stamp == RequiredNativeBuildStamp ? "" : $" ← CẦN {RequiredNativeBuildStamp}, client đang giữ DLL CŨ, phải tắt và mở lại client")}");
		} else {
			report.AppendLine($"Không đọc được dấu bản native: {stampError}");
		}

		GameMapInfo map = GameMapReader.Read(game.ProcessId);
		if (!map.Success || !map.HasDoctor) {
			report.AppendLine($"KHÔNG lấy được toạ độ Đại Phu từ Data/Maps: {(map.Success ? "map này không có Đại Phu" : map.FailureReason)}");
			report.AppendLine("Đứng ở map có Đại Phu rồi bấm lại.");
			return report.ToString();
		}

		if (!RuntimeEntityLocator.TryFindNamedEntity(game.ProcessId, DoctorName, map.DoctorRawX, map.DoctorRawY, out RuntimeEntityLocation doctor, out string findReason)) {
			report.AppendLine($"KHÔNG thấy entity '{DoctorName}' trong bảng: {findReason}");
			report.AppendLine("Đi tới chỗ Đại Phu cho NPC nạp vào bảng rồi bấm lại.");
			return report.ToString();
		}
		report.AppendLine($"Đại Phu: Index={doctor.Index} | Raw={doctor.RawX}/{doctor.RawY} | LệchMap={doctor.DistanceToAnchor:F2}");

		if (!game.AutoFsTransport.TryProbeCurrentTarget(game.Handle, out int before, out string beforeError)) {
			report.AppendLine($"Đọc mục tiêu TRƯỚC thất bại: {beforeError}");
			return report.ToString();
		}
		report.AppendLine($"Mục tiêu TRƯỚC = {Describe(before)}");

		if (!game.AutoFsTransport.TryProbeSelectEntity(game.Handle, doctor.Index, out int after, out string selectError)) {
			report.AppendLine($"Gửi lệnh chọn thất bại: {selectError}");
			return report.ToString();
		}
		report.AppendLine($"Mục tiêu SAU = {Describe(after)}");

		if (after == int.MinValue) {
			report.AppendLine("KẾT LUẬN: native TỪ CHỐI (chỉ số sai, manager/vtable không qua kiểm tra, hoặc con trỏ không thực thi được).");
			return report.ToString();
		}
		report.AppendLine(after == doctor.Index
			? "KẾT LUẬN: hàm GHI ĐÚNG chỉ số Đại Phu vào biến mục tiêu."
			: $"KẾT LUẬN: hàm chạy nhưng biến mục tiêu = {after}, KHÔNG phải {doctor.Index}.");

		ReadShopState(game.ProcessId, out uint modalState, out uint shopState);
		report.AppendLine($"Sau khi chọn: ModalState=0x{modalState:X8} | ShopState={shopState}");
		report.AppendLine(shopState == 2
			? "ShopState=2 → hội thoại/cửa hàng ĐÃ mở chỉ bằng lệnh chọn."
			: "ShopState≠2 → chọn được mục tiêu nhưng CHƯA mở hội thoại; cần thêm bước nữa. Nhìn màn hình xem NPC có được tô sáng/chọn không rồi báo lại.");
		return report.ToString();
	}

	private static string Describe(int value) {
		if (value == int.MinValue) return "int.MinValue (native từ chối)";
		if (value == -1) return "-1 (không chọn ai)";
		return value.ToString();
	}

	private static void ReadShopState(int processId, out uint modalState, out uint shopState) {
		modalState = 0;
		shopState = 0;
		try {
			using MemoryReader reader = new MemoryReader(processId);
			IntPtr moduleBase = reader.GetModuleBase("Game.exe");
			if (moduleBase == IntPtr.Zero) return;
			modalState = unchecked((uint)reader.ReadInt32(IntPtr.Add(moduleBase, GameAddresses.Globals.ModalState)));
			shopState = unchecked((uint)reader.ReadInt32(IntPtr.Add(moduleBase, GameAddresses.Globals.ShopState)));
		} catch {
			// Probe chẩn đoán: đọc không được thì để 0, báo cáo vẫn còn phần chính.
		}
	}
}
