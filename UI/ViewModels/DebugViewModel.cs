namespace Auto.UI.ViewModels;

using System.Collections.ObjectModel;
using System.Text;
using Auto.DebugTools;
using Auto.Runtime;
using Auto.Utils;

public sealed class DebugViewModel : ViewModelBase {
	// Tab này chỉ còn công cụ GỬI LỆNH vào client. Chín mục chỉ-đọc đã chuyển sang tab Thông tin ngày 2026-09-12.
	// Start/stop toggle: records map changes while the owner walks by hand (Khổn Tiên cung entrance), read-only.
	private const string ToolMapTransition = "Ghi chuyển map (bấm Chạy để bắt đầu / dừng)";
	// Hai probe chẩn đoán popup NPC. Đã bị xoá nhầm lúc dọn probe 2026-09-07 rồi khôi phục 2026-09-08 khi luồng
	// Sửa đồ với NPC dạng popup xác nhận hỏng mà không còn công cụ nào đọc được vtable thật của popup.
	// Wall between character and monster: does a flagless move go around it like the flagged route move does?
	private const string ToolMonsterPathfind = "Tới quái gần nhất: TÌM ĐƯỜNG, có cờ (lệnh 0/5) - bấm Chạy để bắt đầu / dừng";
	private const string ToolMonsterRouteNoFlag = "Tới quái gần nhất: TÌM ĐƯỜNG KHÔNG CỜ, thử (lệnh 331) - bấm Chạy để bắt đầu / dừng";
	private const string ToolMonsterWalk = "Tới quái gần nhất: ĐI THẲNG, không cờ (lệnh 321) - bấm Chạy để bắt đầu / dừng";
	private const string ToolKhonTienRoute = "Đi thử Diêu Trì -> Khổn Tiên Tầng 5 (GỬI LỆNH THẬT, bấm Chạy để bắt đầu / dừng)";
	private const string ToolModalVtable = "Thông tin popup (vtable)";
	private const string ToolNpcMenuCapture = "Thông tin menu NPC";
	private const string ToolImmediateRepair = "Đi sửa đồ";
	private const string ToolImmediateSale = "Bán ngay (phải mở sẵn cửa hàng)";
	private const string ToolInventoryStrength = "Sức lực mang đồ (đối chiếu với game)";
	// Chủ dự án chỉ ra 2026-09-24: "Tham Lang Yêu Đái" chỉ nặng 4 điểm trong game, nhưng cặp CurrentStrength/
	// MaximumStrength lại đo được lệch 72 điểm sau đúng 1 lần nhặt món này — nghi cặp offset StrengthRoot+0x27C/
	// 0x278 đọc sai địa chỉ. InventoryInfoProbe dùng offset Item.Weight (0x6EC) ĐÃ xác minh riêng (2026-09-07, đối
	// chiếu game), độc lập hoàn toàn với cặp StrengthRoot — dùng để đối chiếu chéo, không phải để sửa mù.
	private const string ToolInventoryInfo = "Trọng lượng từng món trong túi (đối chiếu với game)";
	// Test the WHOLE low-hp doctor flow: walk to NPC -> open shop -> buy -> return. Runtime.DoctorPurchaseAutomation.
	private const string ToolDoctorPurchaseFlow = "Đi tới Đại Phu, mở shop, mua đủ HP+MP rồi quay lại (GỬI LỆNH THẬT, TIÊU TIỀN)";

	// Trên mức này thì báo cáo đối chiếu không đọc được. Lấy rộng hơn DoctorRouteArrivalDistance (1,5 ô) của luồng
	// Sửa đồ để lượt bấm ngay sát NPC vẫn được coi là hợp lệ.
	private const double ConformanceReadableDistanceCells = 3;
	private readonly GameWindow? game;
	private bool enableDebugLogging = true;
	private string selectedTool;
	private string accountInfoText = "";

	public DebugViewModel() : this(null) { }

	public DebugViewModel(GameWindow? game) {
		this.game = game;
		selectedTool = ToolMonsterRouteNoFlag;
		RunToolCommand = new RelayCommand(_ => RunTool());
		ClearOutputCommand = new RelayCommand(_ => AccountInfoText = "");
	}

	public bool EnableDebugLogging {
		get => enableDebugLogging;
		set {
			if (!SetField(ref enableDebugLogging, value)) return;
			DebugFileLogging.SetEnabled(value);
		}
	}

	public ObservableCollection<string> Tools { get; } = [
		ToolMonsterRouteNoFlag,
		ToolMonsterPathfind,
		ToolMonsterWalk,
		ToolKhonTienRoute,
		ToolMapTransition,
		ToolModalVtable,
		ToolNpcMenuCapture,
		ToolImmediateRepair,
		ToolImmediateSale,
		ToolInventoryStrength,
		ToolInventoryInfo,
		ToolDoctorPurchaseFlow
	];

	public string SelectedTool {
		get => selectedTool;
		set => SetField(ref selectedTool, value);
	}

	public string AccountInfoText {
		get => accountInfoText;
		private set => SetField(ref accountInfoText, value);
	}

	public RelayCommand RunToolCommand { get; }

	public RelayCommand ClearOutputCommand { get; }

	// Khớp DEV\UI\Debug.cs.RunTool: chọn công cụ theo dropdown rồi chạy đúng 1 nút "Chạy" chung.
	private void RunTool() {
		switch (selectedTool) {
			case ToolMonsterPathfind:
				ToggleMonsterPath(MonsterPathProbe.Mode.Pathfind);
				return;
			case ToolMonsterRouteNoFlag:
				ToggleMonsterPath(MonsterPathProbe.Mode.RouteNoFlag);
				return;
			case ToolMonsterWalk:
				ToggleMonsterPath(MonsterPathProbe.Mode.Walk);
				return;
			case ToolKhonTienRoute:
				ToggleKhonTienRoute();
				return;
			case ToolMapTransition:
				ToggleMapTransitionRecorder();
				return;
			case ToolModalVtable:
				StartModalVtableProbe();
				return;
			case ToolNpcMenuCapture:
				StartNpcMenuCapture();
				return;
			case ToolImmediateRepair:
				StartWeaponRepairFlowTest();
				return;
			case ToolImmediateSale:
				StartImmediateSale();
				return;
			case ToolInventoryStrength:
				StartInventoryStrengthProbe();
				return;
			case ToolInventoryInfo:
				StartInventoryInfoProbe();
				return;
			case ToolDoctorPurchaseFlow:
				StartDoctorPurchaseFlow();
				return;
			default:
				AccountInfoText = $"Không nhận diện được công cụ: {selectedTool}";
				return;
		}
	}

	// Walks to the nearest live monster with one movement primitive; run again to stop.
	private void ToggleMonsterPath(MonsterPathProbe.Mode mode) {
		if (game == null) {
			AccountInfoText = "Không có account game đang được chọn.";
			return;
		}
		if (MonsterPathProbe.IsRunning(game.ProcessId)) {
			AccountInfoText = MonsterPathProbe.Stop(game.ProcessId);
			return;
		}
		AccountInfoText = MonsterPathProbe.Start(game, mode) + "\r\n\r\n"
			+ "Auto tổng phải TẮT. Đứng sao cho có tường giữa nhân vật và con quái gần nhất.\r\n"
			+ "Nhân vật tự đi tới vị trí con quái (không đánh), dừng khi cách dưới 1 ô, quái chết/mất, hoặc sau 30 giây.\r\n"
			+ "Chạy lần lượt cả hai công cụ TÌM ĐƯỜNG và ĐI THẲNG ở cùng một chỗ để so sánh. Kết quả ở map-transition.log (MONSTER_PATH_*).";
	}

	// Walks the recorded Khổn Tiên portal chain with the quest's movement queue; run again to stop.
	private void ToggleKhonTienRoute() {
		if (game == null) {
			AccountInfoText = "Không có account game đang được chọn.";
			return;
		}
		if (KhonTienRouteProbe.IsWalking(game.ProcessId)) {
			AccountInfoText = KhonTienRouteProbe.Stop(game.ProcessId);
			return;
		}
		AccountInfoText = KhonTienRouteProbe.Start(game) + "\r\n\r\n"
			+ "Auto tổng phải TẮT. Nhân vật tự đi qua từng cổng tới Đại Phu Tầng 5, dùng đúng hàng đợi di chuyển của Thám quân.\r\n"
			+ "Dừng khi tới nơi, hết 6 phút, bật Auto tổng, hoặc bấm Chạy lần nữa.\r\n"
			+ "Kết quả từng bước ghi vào map-transition.log (KHONTIEN_ROUTE_*).";
	}

	// Read-only recorder for the Khổn Tiên cung entrance: start, walk there by hand, then run again to stop.
	private void ToggleMapTransitionRecorder() {
		if (MapTransitionRecorder.IsRunning) {
			AccountInfoText = MapTransitionRecorder.Stop();
			return;
		}
		if (game == null) {
			AccountInfoText = "Không có account game đang được chọn.";
			return;
		}
		AccountInfoText = MapTransitionRecorder.Start(game) + "\r\n\r\n"
			+ "Công cụ CHỈ ĐỌC, không gửi lệnh nào. Không cần bật Auto tổng.\r\n"
			+ "Tự đi tay từ map đang đứng vào Khổn Tiên cung (và lên các tầng nếu được). Mỗi lần đổi map ghi một dòng MAP_CHANGE.\r\n"
			+ "Nếu phải nói chuyện với NPC để vào, cứ mở menu và chọn bình thường: popup được ghi lại tự động (MAP_POPUP).\r\n"
			+ "Xong thì bấm Chạy lần nữa để dừng.";
	}

	// Đọc vtable thật của popup đang mở và đối chiếu với các hằng số đang dùng. Phải mở sẵn popup NPC trước khi chạy.
	private async void StartModalVtableProbe() {
		if (game == null) {
			AccountInfoText = "Không có account game đang được chọn.";
			return;
		}
		int processId = game.ProcessId;
		AccountInfoText = $"PID={processId} | Đang đọc popup... Giữ nguyên popup, đừng đóng.";
		string result = await Task.Run(() => ModalVtableProbe.Run(processId));
		DebugLog.AddDebugForProcess(processId, result);
		AccountInfoText = $"PID={processId} | {result}";
	}

	// Dump danh sách lựa chọn của menu NPC đang mở, để đối chiếu offset và stride thật.
	private async void StartNpcMenuCapture() {
		if (game == null) {
			AccountInfoText = "Không có account game đang được chọn.";
			return;
		}
		int processId = game.ProcessId;
		AccountInfoText = $"PID={processId} | Đang đọc menu NPC... Giữ nguyên popup, đừng đóng.";
		string result = await Task.Run(() => NpcMenuOptionCapture.Capture(processId, "Đại Phu"));
		DebugLog.AddDebugForProcess(processId, result);
		AccountInfoText = $"PID={processId} | {result}";
	}

	// Đi sửa đồ toàn bộ theo yêu cầu, bỏ qua ngưỡng độ bền; giữ Auto tổng đang bật.
	private void StartWeaponRepairFlowTest() {
		if (game == null) {
			AccountInfoText = "Không có account game đang được chọn.";
			return;
		}
		// Chụp quyết định của luồng Sửa đồ TRƯỚC khi chạy, rồi mới chạy. Không có phần này thì chỉ biết chuyến sửa
		// thành công hay hỏng, không biết Auto đã bám đúng cách AutoFS tìm NPC hay chưa.
		string conformance = DescribeAutoFsConformance(game);
		DebugLog.AddDebugForProcess(game.ProcessId, conformance);
		string status;
		lock (game.AutoSync) status = game.WeaponRepairAutomation.RequestDebugRun();
		DebugLog.AddForProcess(game.ProcessId, status);
		AccountInfoText = $"PID={game.ProcessId} | {status}\r\n\r\n{conformance}\r\n" +
			"KHÔNG cần bật Auto tổng, KHÔNG cần bật Đánh, KHÔNG cần đợi độ bền tụt. Nhân vật tự đi tới NPC, sửa toàn bộ rồi quay lại chỗ cũ.\r\n" +
			"Xem repair.log để biết NPC thuộc dạng nào: \"Doctor confirmation modal\" = popup xác nhận, \"Doctor shop menu\" = menu nhiều lựa chọn.";
	}

	// Đối chiếu sức lực đọc từ bộ nhớ với số hiển thị trong game, cho ĐÚNG account đang chọn.
	// Chỉ đọc, không gửi lệnh nào — chạy được kể cả khi Auto tổng đang bật.
	private void StartInventoryStrengthProbe() {
		if (game == null) {
			AccountInfoText = "Không có account game đang được chọn.";
			return;
		}
		string result = InventoryStrengthProbe.Read(game);
		DebugLog.AddDebugForProcess(game.ProcessId, result);
		AccountInfoText = result;
	}

	// Đối chiếu chéo với ToolInventoryStrength: đọc TRỌNG LƯỢNG THẬT từng món trong túi qua offset Item.Weight
	// (đã xác minh riêng, độc lập với cặp StrengthRoot đang bị nghi đọc sai). Chỉ đọc, không gửi lệnh nào.
	private void StartInventoryInfoProbe() {
		if (game == null) {
			AccountInfoText = "Không có account game đang được chọn.";
			return;
		}
		string result = InventoryInfoProbe.Run(game.ProcessId);
		DebugLog.AddDebugForProcess(game.ProcessId, result);
		AccountInfoText = result;
	}

	// Chạy toàn bộ chuỗi Runtime.DoctorPurchaseAutomation: tự đi tới Đại Phu của map hiện tại, mở cửa hàng, mua cho đủ
	// 10 bình mỗi loại HP/MP đã chọn ở "Hồi phục" (chỉ mua phần còn thiếu), rồi tự quay lại bãi. Chạy được kể cả khi
	// Auto tổng đang tắt (giống nút "Đi sửa đồ").
	private void StartDoctorPurchaseFlow() {
		if (game == null) {
			AccountInfoText = "Không có account game đang được chọn.";
			return;
		}
		string status;
		lock (game.AutoSync) status = game.DoctorPurchaseAutomation.RequestDebugRun();
		DebugLog.AddForProcess(game.ProcessId, status);
		AccountInfoText = $"PID={game.ProcessId} | {status}\r\n\r\n" +
			"KHÔNG cần bật Auto tổng. Nhân vật tự đi tới Đại Phu của map hiện tại, mở cửa hàng, mua cho đủ 10 bình mỗi loại (chỉ mua phần còn thiếu), rồi tự quay lại chỗ cũ.\r\n" +
			"Xem log để theo từng bước: \"Mua ở Đại Phu | ...\".";
	}

	// Bán ngay số đồ đã tick ở mục "Bán" (tab Nhặt), bỏ qua hai ngưỡng số lượng/sức lực.
	//
	// Vì sao cần: luồng bán tự động chỉ kích hoạt khi túi quá ngưỡng, nên muốn kiểm nó có chạy đúng không thì phải
	// ngồi đợi đầy túi. Tính tới 2026-09-23 chức năng bán CHƯA CHẠY LẦN NÀO (0 dòng SALE_ trong Diagnostics,
	// 3912/3912 dòng heartbeat ghi Sale=False), nên đây là đường duy nhất lấy được bằng chứng trong vài giây.
	//
	// KHÁC nút "Đi sửa đồ": nút này KHÔNG tự đi tới NPC. Engine đòi ShopState == 2 (InventorySaleEngine.Tick) tức
	// cửa hàng phải đang mở sẵn trên màn hình.
	private void StartImmediateSale() {
		if (game == null) {
			AccountInfoText = "Không có account game đang được chọn.";
			return;
		}
		if (game.Enabled) {
			AccountInfoText = $"PID={game.ProcessId} | Phải TẮT Auto tổng của account này trước khi bán ngay.\r\n" +
				"Auto tổng đang bật thì Đánh/Nhặt/Sửa đồ cùng chen lệnh vào và làm bẩn phép đo.";
			return;
		}
		string selected = string.Join(", ", game.LootSettings.SaleItemSelections.Where(entry => entry.Value).Select(entry => entry.Key));
		if (selected.Length == 0) {
			AccountInfoText = $"PID={game.ProcessId} | Chưa tick món nào ở mục \"Bán\" (tab Nhặt) nên không có gì để bán.";
			return;
		}
		lock (game.AutoSync) game.InventorySaleEngine.Start(text => DebugLog.AddDebugForProcess(game.ProcessId, text), bypassMasterSwitch: true);
		AccountInfoText = $"PID={game.ProcessId} | Đã bắt đầu bán ngay.\r\nĐang bán: {selected}\r\n\r\n" +
			"Cửa hàng Đại Phu PHẢI đang mở sẵn — nút này không tự đi tới NPC.\r\n" +
			"Xem auto-runtime.log để đối chiếu: SALE_START -> SALE_FILTER từng ô -> SALE_COMMAND_POSTED -> SALE_CONFIRMED -> SALE_COMPLETE.\r\n" +
			"Đồ Lục và Đồ Vàng bị chặn cứng, sẽ hiện GREEN_POPUP_BLOCKED / YELLOW_VALUABLE_BLOCKED ở dòng SALE_FILTER.";
	}

	// Quy đổi raw sang ô đúng tỉ lệ của game: X chia 256, Y chia 512.
	private static double GetCellDistance(int firstX, int firstY, int secondX, int secondY) {
		double deltaX = (firstX - (double)secondX) / 256.0;
		double deltaY = (firstY - (double)secondY) / 512.0;
		return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
	}

	// Đối chiếu từng bước tìm NPC của Auto với bản decompile AutoFS (WindowQueue.cs:24241-24270, hàm OrderQueue được
	// gọi bằng OrderQueue("Đại phu", 30) ở MenuAttribute.cs:23958). Gọi đúng những API mà luồng Sửa đồ thật đang gọi
	// (GameMapReader.Read, RuntimeEntityLocator.TryFindNamedEntity) nên báo cáo phản ánh đúng quyết định sắp diễn ra,
	// không phải một bản mô phỏng riêng.
	private static string DescribeAutoFsConformance(GameWindow game) {
		StringBuilder output = new();
		output.AppendLine("===== Đối chiếu cách tìm Đại Phu với AutoFS =====");
		GameSnapshot snapshot = GameMemory.ReadSnapshot(game.ProcessId);
		GameMapInfo map = GameMapReader.Read(game.ProcessId);
		if (!map.Success || !map.HasDoctor) {
			output.AppendLine($"DỪNG | {(map.Success ? $"Map {map.MapId} không có toạ độ Đại Phu trong Data/Maps." : map.FailureReason)}");
			return output.ToString();
		}
		// Khoảng cách tới mốc quyết định số liệu có đọc được hay không: client chỉ nạp entity quanh nhân vật, đứng xa
		// thì NPC vắng mặt là bình thường chứ không phải lỗi. Bản đầu không in số này nên một lượt bấm từ xa
		// (PID=34032, 20,85 ô, 2026-09-11) ra dòng "KHÔNG thấy" kèm kết luận sai là NPC vắng khỏi bảng entity.
		double anchorDistance = GetCellDistance(snapshot.X, snapshot.Y, map.DoctorRawX, map.DoctorRawY);
		bool nearAnchor = anchorDistance <= ConformanceReadableDistanceCells;
		output.AppendLine($"Map={map.MapId} | MốcĐạiPhu(Data/Maps)={map.DoctorRawX}/{map.DoctorRawY} | NhânVật={snapshot.X}/{snapshot.Y} | CáchMốc={anchorDistance:F2} ô");
		if (!nearAnchor) output.AppendLine($"CẢNH BÁO | Đứng cách mốc {anchorDistance:F2} ô (> {ConformanceReadableDistanceCells} ô) nên client chưa nạp NPC. Số liệu dưới đây KHÔNG dùng để kết luận được — bấm lại khi nhân vật đang đứng cạnh Đại Phu.");

		bool found = RuntimeEntityLocator.TryFindNamedEntity(game.ProcessId, "Đại phu", map.DoctorRawX, map.DoctorRawY, out RuntimeEntityLocation doctor, out string reason);
		output.AppendLine($"[1] Quét index {GameAddresses.Entity.FirstScanIndex}..{GameAddresses.Entity.LastScanIndex} | AutoFS: for (int k = 2; k < 256; k++) | Khớp=CÓ");
		output.AppendLine($"[2] So tên bằng Contains, KHÔNG lọc EntityType/LifecycleStatus | AutoFS: text.Contains(name) | Khớp=CÓ");
		output.AppendLine($"[3] KHÔNG loại entity thiếu toạ độ | AutoFS chỉ đọc trường tên, không đọc toạ độ | Khớp=CÓ");
		if (!found) {
			output.AppendLine($"[4] KẾT QUẢ: KHÔNG thấy entity 'Đại phu' | {reason}");
			output.AppendLine(nearAnchor
				? "     => Đang đứng sát mốc mà vẫn không thấy: nếu KhớpTênKhôngToạĐộ=0 thì NPC thật sự vắng khỏi bảng entity, không phải lỗi bộ lọc."
				: "     => KHÔNG kết luận được vì đứng quá xa mốc, xem dòng CẢNH BÁO ở trên.");
			return output.ToString();
		}
		bool byIndex = doctor.RawX <= 0 || doctor.RawY <= 0;
		output.AppendLine($"[4] KẾT QUẢ: thấy '{doctor.Name}' | Index={doctor.Index} | Raw={doctor.RawX}/{doctor.RawY} | CóToạĐộ={(byIndex ? "KHÔNG" : "CÓ")}");
		output.AppendLine(byIndex
			? "[5] Sẽ click bằng lệnh 8 + index, đúng cách AutoFS (NetworkSet.DisposeNode(Handle, fontInstance, 8, k)) | Khớp=CÓ"
			: "[5] Sẽ click theo toạ độ màn hình | AutoFS dùng lệnh 8 + index | Khớp=KHÔNG (giữ nguyên vì đây là đường đã sửa đồ thành công)");
		return output.ToString();
	}

}
