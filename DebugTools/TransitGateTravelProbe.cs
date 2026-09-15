namespace Auto.DebugTools;

using System.Text;
using Auto.Movement;
using Auto.Runtime;
using Auto.Utils;

// Chạy trọn luồng ĐIỂM CHUYỂN TIẾP một lần: tự đi bộ ra điểm, chờ popup tự hiện, rồi chọn một số thứ tự.
//
// GỬI LỆNH VÀO GAME: nhân vật đi thật và dịch chuyển thật. Chỉ chạy khi chủ dự án chủ động bấm.
//
// Khác TransitGateSelectProbe ở chỗ probe kia bắt chủ dự án tự đi ra điểm rồi mới bấm; probe này làm cả khâu đi.
internal static class TransitGateTravelProbe {
	private const string BuildStamp = "TRANSIT-GATE-TRAVEL-20260911-01";
	private const int SourceMapId = 21;
	private const int DialogOptionCommand = 7;
	// Toạ độ Điểm chuyển tiếp ở Triều Ca. Lấy từ chính các lượt chạy của chủ dự án ngày 2026-09-11 trên PID=32196:
	// mọi lần popup đang mở (ModalState=0x2D9A5688) nhân vật đều đứng trong khoảng 57194..57249 / 98149..98247.
	// Lấy điểm giữa khoảng đó.
	private const int TransitPointRawX = 57220;
	private const int TransitPointRawY = 98200;
	private const int WalkTimeoutMilliseconds = 60000;
	// Gửi lại tuyến CHỈ khi đứng chững, không gửi theo nhịp cố định. Lệnh đi có gắn cờ mở đầu bằng ResetCommand=32
	// (AutoFsMovementCommand.TryMoveToCore) nên mỗi lần gửi lại là cắt ngang đường đi đang chạy: bản trước gửi lại mỗi
	// 1,5 giây làm nhân vật đi giật cục. Lấy đúng khuôn luồng Sửa đồ đã chạy ổn: mốc chống kẹt tính theo TIẾN ĐỘ tới
	// đích chứ không phải "có nhúc nhích hay không" (WeaponRepairAutomation.ObserveNavigationProgress,
	// StuckDetectionMilliseconds = 8000, MinimumNavigationProgress = 0.08 ô).
	private const int StallResendMilliseconds = 8000;
	private const double MinimumNavigationProgressCells = 0.08;
	private const int PopupWaitMilliseconds = 8000;
	private const int MapChangeTimeoutMilliseconds = 15000;
	private const int PollMilliseconds = 250;
	private const int ProgressLogMilliseconds = 5000;

	public static string Run(GameWindow game, int optionIndex) {
		StringBuilder output = new();
		output.AppendLine("===== Điểm chuyển tiếp: đi ra điểm rồi dịch chuyển =====");
		output.AppendLine($"TRANSIT_GATE_TRAVEL | BuildStamp={BuildStamp} | Mode=GAME_WRITE | ProcessId={game.ProcessId} | Số={optionIndex}");

		GameMapInfo before = GameMapReader.Read(game.ProcessId);
		GameSnapshot start = GameMemory.ReadSnapshot(game.ProcessId);
		output.AppendLine($"TRƯỚC | MapId={before.MapId} | ViTri={(start.Success ? $"{start.X}/{start.Y}" : "không đọc được")} | Đích điểm={TransitPointRawX}/{TransitPointRawY}");
		if (before.MapId != SourceMapId) {
			output.AppendLine($"DỪNG | Probe này chỉ chạy từ Map {SourceMapId} (Triều Ca) vì toạ độ điểm chỉ đo được ở đó. Đang ở Map {before.MapId}.");
			return output.ToString();
		}

		// Bước 1: đi bộ tới điểm, dừng ngay khi popup tự hiện (ModalState khác 0) chứ không dựa vào ngưỡng khoảng cách —
		// vùng kích hoạt popup là thứ client quyết định, đo bằng toạ độ chỉ là gần đúng.
		DateTime walkDeadline = DateTime.UtcNow.AddMilliseconds(WalkTimeoutMilliseconds);
		DateTime nextProgress = DateTime.MinValue;
		DateTime lastProgressUtc = DateTime.UtcNow;
		double bestDistanceCells = double.MaxValue;
		bool routeSent = false;
		uint modalState = 0;
		while (DateTime.UtcNow < walkDeadline) {
			modalState = ReadModalState(game.ProcessId);
			if (modalState != 0) break;
			DateTime now = DateTime.UtcNow;
			GameSnapshot current = GameMemory.ReadSnapshot(game.ProcessId);
			double distanceCells = current.Success ? Distance(current.X, current.Y, TransitPointRawX, TransitPointRawY) / 256.0 : double.MaxValue;
			if (distanceCells <= bestDistanceCells - MinimumNavigationProgressCells) {
				bestDistanceCells = distanceCells;
				lastProgressUtc = now;
			}
			bool stalled = (now - lastProgressUtc).TotalMilliseconds >= StallResendMilliseconds;
			if (! routeSent || stalled) {
				if (! AutoFsMovementCommand.TryMoveToForDebug(game, TransitPointRawX, TransitPointRawY, out string moveResult)) {
					output.AppendLine($"HỎNG | không gửi được lệnh đi | {moveResult}");
					return output.ToString();
				}
				output.AppendLine($"GỬI TUYẾN | {(routeSent ? "gửi lại vì đứng chững" : "lần đầu")} | ViTri={(current.Success ? $"{current.X}/{current.Y}" : "không đọc được")} | {moveResult}");
				routeSent = true;
				lastProgressUtc = now;
				bestDistanceCells = distanceCells;
			} else if (now >= nextProgress) {
				nextProgress = now.AddMilliseconds(ProgressLogMilliseconds);
				output.AppendLine($"ĐANG ĐI | ViTri={(current.Success ? $"{current.X}/{current.Y}" : "không đọc được")} | Cách={(current.Success ? $"{distanceCells:F2} ô" : "?")}");
			}
			Thread.Sleep(PollMilliseconds);
		}

		// Bước 2: nếu tới nơi rồi mà popup chưa kịp hiện thì chờ thêm một nhịp.
		if (modalState == 0) {
			DateTime popupDeadline = DateTime.UtcNow.AddMilliseconds(PopupWaitMilliseconds);
			while (DateTime.UtcNow < popupDeadline && modalState == 0) {
				Thread.Sleep(PollMilliseconds);
				modalState = ReadModalState(game.ProcessId);
			}
		}
		GameSnapshot arrived = GameMemory.ReadSnapshot(game.ProcessId);
		if (modalState == 0) {
			output.AppendLine($"KHÔNG THẤY POPUP | ViTri={(arrived.Success ? $"{arrived.X}/{arrived.Y}" : "không đọc được")} | ModalState=0 sau {WalkTimeoutMilliseconds / 1000}s đi + {PopupWaitMilliseconds / 1000}s chờ.");
			output.AppendLine("     => Hoặc chưa tới đúng ô kích hoạt, hoặc toạ độ điểm trong probe sai. Xem ViTri ở trên để chỉnh.");
			return output.ToString();
		}
		output.AppendLine($"POPUP ĐÃ HIỆN | ViTri={(arrived.Success ? $"{arrived.X}/{arrived.Y}" : "không đọc được")} | ModalState=0x{modalState:X8}");

		// Bước 3: chọn mục. Gửi CÓ XÁC NHẬN để phân biệt native từ chối với client bỏ qua.
		if (! game.AutoFsTransport.TrySendConfirmedCommandForDebug(game.Handle, DialogOptionCommand, optionIndex, out string sendError)) {
			output.AppendLine($"NATIVE TỪ CHỐI | lệnh {DialogOptionCommand} số {optionIndex} không được thi hành | {sendError}");
			return output.ToString();
		}
		output.AppendLine($"NATIVE ĐÃ THI HÀNH | lệnh {DialogOptionCommand} số {optionIndex}, đang chờ tối đa {MapChangeTimeoutMilliseconds / 1000} giây...");

		DateTime mapDeadline = DateTime.UtcNow.AddMilliseconds(MapChangeTimeoutMilliseconds);
		while (DateTime.UtcNow < mapDeadline) {
			Thread.Sleep(PollMilliseconds);
			GameMapInfo current = GameMapReader.Read(game.ProcessId);
			if (current.MapId <= 0 || current.MapId == before.MapId) continue;
			GameSnapshot after = GameMemory.ReadSnapshot(game.ProcessId);
			string name = GameMapCatalog.TryGetName(current.MapId, out string mapName) ? mapName : "chưa có trong GameMapCatalog";
			string expected = TransitGateDestinationCatalog.TryGetOptionIndex(before.MapId, current.MapId, out int catalogIndex, out _)
				? catalogIndex == optionIndex ? "KHỚP bảng" : $"LỆCH bảng (bảng ghi số {catalogIndex})"
				: "map này không có trong bảng";
			output.AppendLine($"KẾT QUẢ | Số={optionIndex} -> MapId={current.MapId} ({name}) | ViTri={(after.Success ? $"{after.X}/{after.Y}" : "không đọc được")} | ĐốiChiếu={expected}");
			return output.ToString();
		}

		output.AppendLine($"KHÔNG ĐỔI MAP | MapId vẫn {before.MapId} sau {MapChangeTimeoutMilliseconds / 1000} giây | ModalState sau=0x{ReadModalState(game.ProcessId):X8}");
		return output.ToString();
	}

	// X chia 256, Y chia 512 — quy đổi ô đúng tỉ lệ của game.
	private static double Distance(int firstX, int firstY, int secondX, int secondY) {
		double deltaX = (double)firstX - secondX;
		double deltaY = ((double)firstY - secondY) * 0.5;
		return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
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
