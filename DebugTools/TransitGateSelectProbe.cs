namespace Auto.DebugTools;

using System.Text;
using Auto.Runtime;
using Auto.Utils;

// Đo bảng "số thứ tự -> map đích" của popup ĐIỂM CHUYỂN TIẾP bằng cách GỬI LỆNH THẬT, đúng cách AutoFS làm.
//
// GỬI LỆNH VÀO GAME: nhân vật sẽ dịch chuyển thật. Chỉ chạy khi chủ dự án chủ động bấm.
//
// Vì sao phải đo thay vì đọc: bảy cách đọc chữ trong popup đều tắc, và lượt so trước/sau ngày 2026-09-11 cho câu trả
// lời dứt khoát — nền chụp lúc ModalState=0x00000000, đo lúc ModalState=0x2D9A5688, ra đúng 42 chuỗi mới và KHÔNG
// chuỗi nào là tên điểm đến. Client nạp sẵn chữ giao diện từ .ini lúc khởi động nên phép so không thể thấy.
//
// AutoFS cũng KHÔNG đọc chữ. Nó gửi lệnh kèm số (WindowQueue.cs:24619-24667):
//     int[] array = new int[4] { 84, 80, 80, 8 };
//     int num2 = NetworkSet.OrderQueue(HProcess, StreamAttribute.B_Bảng, array);
//     if (num2 == 21 || num2 == 235) NetworkSet.DisposeNode(Handle, NetworkSet.fontInstance, 96, 13);
// tức lệnh 96 + một số. Biết số nào ra map nào là đủ dùng, không cần biết popup ghi gì.
//
// Cùng khuôn với cách đã đo xong menu Di ngoại phù: TalismanDestinationCatalog.cs:46 ghi "Chỉ số đã đo bằng
// TalismanTravelProbe ngày 2026-09-10, đều KHỚP bảng".
internal static class TransitGateSelectProbe {
	private const string BuildStamp = "TRANSIT-GATE-SELECT-20260911-01";
	// Không khoá cứng số lệnh. AutoFS dùng 96 cho Điểm chuyển tiếp, nhưng chính Auto đang chọn mục menu NPC bằng lệnh
	// 7 (ScoutQuestAutomation.DialogOptionCommand) và xử lý popup chết bằng lệnh 38 (AccountEngineCoordinator) —
	// cả hai đã chạy thật trên client này. Lượt PID=32196 ngày 2026-09-11 gửi 96 với số 0 và 1 đều không đổi map và
	// popup vẫn mở, nên phải thử được cả các lệnh khác.
	private const int MapChangeTimeoutMilliseconds = 12000;
	private const int PollMilliseconds = 250;

	public static string Run(GameWindow game, int command, int optionIndex) {
		StringBuilder output = new();
		output.AppendLine("===== Điểm chuyển tiếp: thử một số thứ tự =====");
		output.AppendLine($"TRANSIT_GATE_SELECT | BuildStamp={BuildStamp} | Mode=GAME_WRITE | ProcessId={game.ProcessId} | Lệnh={command} | Số={optionIndex}");

		uint modalBefore = ReadModalState(game.ProcessId);
		GameMapInfo before = GameMapReader.Read(game.ProcessId);
		GameSnapshot snapshotBefore = GameMemory.ReadSnapshot(game.ProcessId);
		output.AppendLine($"TRƯỚC | ModalState=0x{modalBefore:X8} | MapId={before.MapId} | ViTri={(snapshotBefore.Success ? $"{snapshotBefore.X}/{snapshotBefore.Y}" : "không đọc được")}");
		// KHÔNG chặn khi ModalState=0. Cổng chặn cũ là do tao tự thêm chứ không phải của AutoFS — AutoFS kiểm điều kiện
		// khác hẳn (đọc số qua chuỗi con trỏ B_Bảng+0x54/+0x50/+0x50/+0x08, so với 21 hoặc 235). Lượt PID=34032 ngày
		// 2026-09-11 bị nó chặn nên không thu được dữ liệu nào, trong khi chưa phân biệt được là popup chưa mở hay
		// popup không đăng ký làm modal. Cứ gửi rồi đọc ModalState cả trước lẫn sau thì một lần chạy trả lời được.
		if (modalBefore == 0) output.AppendLine("CẢNH BÁO | ModalState=0. Hoặc popup chưa mở, hoặc popup này không đăng ký làm modal. Vẫn gửi lệnh để biết câu trả lời.");

		// Gửi CÓ XÁC NHẬN: bản chỉ post vào hàng đợi luôn báo thành công, nên không phân biệt được native từ chối
		// (ví dụ chỉ số vượt MaximumDialogOptionIndex=63, hoặc vtable lệch) với client nhận rồi bỏ qua.
		if (! game.AutoFsTransport.TrySendConfirmedCommandForDebug(game.Handle, command, optionIndex, out string sendError)) {
			output.AppendLine($"NATIVE TỪ CHỐI | lệnh {command} số {optionIndex} không được thi hành | {sendError}");
			return output.ToString();
		}
		output.AppendLine($"NATIVE ĐÃ THI HÀNH | lệnh {command} tham số {optionIndex}, đang chờ tối đa {MapChangeTimeoutMilliseconds / 1000} giây...");

		DateTime deadline = DateTime.UtcNow.AddMilliseconds(MapChangeTimeoutMilliseconds);
		while (DateTime.UtcNow < deadline) {
			Thread.Sleep(PollMilliseconds);
			GameMapInfo current = GameMapReader.Read(game.ProcessId);
			if (current.MapId > 0 && before.MapId > 0 && current.MapId != before.MapId) {
				GameSnapshot snapshotAfter = GameMemory.ReadSnapshot(game.ProcessId);
				string name = GameMapCatalog.TryGetName(current.MapId, out string mapName) ? mapName : "chưa có trong GameMapCatalog";
				output.AppendLine($"KẾT QUẢ | Số={optionIndex} -> MapId={current.MapId} ({name}) | ViTri={(snapshotAfter.Success ? $"{snapshotAfter.X}/{snapshotAfter.Y}" : "không đọc được")}");
				return output.ToString();
			}
		}

		uint modalAfter = ReadModalState(game.ProcessId);
		output.AppendLine($"KHÔNG ĐỔI MAP | MapId vẫn {before.MapId} sau {MapChangeTimeoutMilliseconds / 1000} giây | ModalState sau=0x{modalAfter:X8}");
		output.AppendLine(modalAfter == 0
			? "     => Popup đã ĐÓNG: lệnh có tác dụng nhưng số này không phải điểm đến (huỷ/thoát), hoặc đích trùng map hiện tại."
			: "     => Popup vẫn MỞ: client không nhận số này, thử số khác.");
		return output.ToString();
	}

	private static uint ReadModalState(int processId) {
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			return moduleBase == IntPtr.Zero ? 0 : unchecked((uint)reader.ReadInt32(IntPtr.Add(moduleBase, GameAddresses.Globals.ModalState)));
		} catch {
			return 0;
		}
	}
}
