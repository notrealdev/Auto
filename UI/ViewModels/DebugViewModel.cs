namespace Auto.UI.ViewModels;

using System.Collections.ObjectModel;
using System.Text;
using Auto.DebugTools;
using Auto.Runtime;
using Auto.Utils;

public sealed class DebugViewModel : ViewModelBase {
	// Tab này chỉ còn công cụ GỬI LỆNH vào client. Chín mục chỉ-đọc đã chuyển sang tab Thông tin ngày 2026-09-12.
	private const string ToolTransitGate = "Điểm chuyển tiếp: so sánh trước/sau";
	private const string ToolTransitGateSelect = "Điểm chuyển tiếp: thử số thứ tự (DỊCH CHUYỂN THẬT)";
	private const string ToolTransitGateTravel = "Điểm chuyển tiếp: tự đi rồi dịch chuyển (DỊCH CHUYỂN THẬT)";
	// Hai probe chẩn đoán popup NPC. Đã bị xoá nhầm lúc dọn probe 2026-09-07 rồi khôi phục 2026-09-08 khi luồng
	// Sửa đồ với NPC dạng popup xác nhận hỏng mà không còn công cụ nào đọc được vtable thật của popup.
	private const string ToolMonsterIndex = "Quái nằm ở ô nào (đo trần quét entity)";
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
	// Dò ô nhớ số tiền vạn hiển thị trong túi đồ (chưa có offset), dùng lọc vi sai giữa hai lần chạy.
	private const string ToolInventoryMoney = "Tiền vạn trong túi (dò ô nhớ)";
	// Thử hàm mua của chức năng "Tự động mua thuốc" trong client. Dùng ô tick, loại thuốc và số bình ở khối "Mua item hồi phục" (tab Cơ bản).
	private const string ToolQuickBuy = "Mua nhanh HP + MP (GỬI LỆNH THẬT, TIÊU TIỀN)";

	// Trên mức này thì báo cáo đối chiếu không đọc được. Lấy rộng hơn DoctorRouteArrivalDistance (1,5 ô) của luồng
	// Sửa đồ để lượt bấm ngay sát NPC vẫn được coi là hợp lệ.
	private const double ConformanceReadableDistanceCells = 3;
	private readonly GameWindow? game;
	private bool enableDebugLogging = true;
	private string selectedTool;
	private string accountInfoText = "";
	private int transitGateCommand = 7;
	private int transitGateOptionIndex;

	public DebugViewModel() : this(null) { }

	public DebugViewModel(GameWindow? game) {
		this.game = game;
		selectedTool = ToolTransitGate;
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
		ToolTransitGate,
		ToolTransitGateSelect,
		ToolTransitGateTravel,
		ToolMonsterIndex,
		ToolModalVtable,
		ToolNpcMenuCapture,
		ToolImmediateRepair,
		ToolImmediateSale,
		ToolInventoryStrength,
		ToolInventoryInfo,
		ToolInventoryMoney,
		ToolQuickBuy
	];

	// Số lệnh và tham số khi thử điểm đến. Chỉ tool ToolTransitGateSelect đọc hai giá trị này.
	public int TransitGateCommand {
		get => transitGateCommand;
		set => SetField(ref transitGateCommand, value);
	}

	public int TransitGateOptionIndex {
		get => transitGateOptionIndex;
		set => SetField(ref transitGateOptionIndex, value);
	}

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
			case ToolTransitGate:
				StartTransitGate();
				return;
			case ToolTransitGateSelect:
				StartTransitGateSelect();
				return;
			case ToolTransitGateTravel:
				StartTransitGateTravel();
				return;
			case ToolMonsterIndex:
				StartMonsterIndexProbe();
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
			case ToolInventoryMoney:
				StartInventoryMoneyProbe();
				return;
			case ToolQuickBuy:
				StartQuickBuy();
				return;
			default:
				AccountInfoText = $"Không nhận diện được công cụ: {selectedTool}";
				return;
		}
	}

	// Đọc vtable thật của popup đang mở và đối chiếu với các hằng số đang dùng. Phải mở sẵn popup NPC trước khi chạy.
	// Đo dải chỉ số ô mà QUÁI thật sự chiếm, để biết có hạ được trần quét của vòng Đánh không.
	// Giữ mốc cao nhất qua các lần bấm nên phải chạy ở nhiều bãi rồi mới đọc kết luận.
	private async void StartMonsterIndexProbe() {
		if (game == null) {
			AccountInfoText = "Không có account game đang được chọn.";
			return;
		}
		int processId = game.ProcessId;
		AccountInfoText = $"PID={processId} | Đang đo dải ô của quái...";
		string result = await Task.Run(() => MonsterIndexProbe.Run(processId));
		DebugLog.AddDebugForProcess(processId, result);
		AccountInfoText = $"PID={processId} | {result}";
	}

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

	// Dò ô số tiền vạn bằng lọc vi sai: chạy, làm một giao dịch có số tiền biết trước, chạy lại. Chỉ đọc.
	private void StartInventoryMoneyProbe() {
		if (game == null) {
			AccountInfoText = "Không có account game đang được chọn.";
			return;
		}
		string result = InventoryMoneyProbe.Read(game);
		DebugLog.AddDebugForProcess(game.ProcessId, result);
		AccountInfoText = result;
	}

	// GỬI LỆNH THẬT: mua 1 lần đúng loại và số lượng đã đặt ở tab Cơ bản, HP rồi MP.
	private async void StartQuickBuy() {
		if (game == null) {
			AccountInfoText = "Không có account game đang được chọn.";
			return;
		}
		GameWindow target = game;
		BasicSettings settings = target.BasicSettings;
		int hpCode = settings.QuickBuyHpPotionCode;
		int hpQuantity = settings.QuickBuyHpQuantity;
		int mpCode = settings.QuickBuyMpPotionCode;
		int mpQuantity = settings.QuickBuyMpQuantity;
		AccountInfoText = $"PID={target.ProcessId} | Đang gửi lệnh mua nhanh...";
		string result = await Task.Run(() => QuickBuyProbe.Run(target, "máu", hpCode, hpQuantity) + QuickBuyProbe.Run(target, "mana", mpCode, mpQuantity));
		DebugLog.AddDebugForProcess(target.ProcessId, result);
		AccountInfoText = result;
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

	// Đổ danh sách điểm đến trong popup Điểm chuyển tiếp ĐANG hiện. Không đi bộ, không click, không chọn mục nào.
	private async void StartTransitGate() {
		if (game == null) {
			AccountInfoText = "Không có account game đang được chọn.";
			return;
		}
		GameWindow target = game;
		// Quét 512MB nên phải chạy nền, để trên luồng UI thì cửa sổ đứng hình suốt lượt quét.
		AccountInfoText = $"PID={target.ProcessId} | Đang quét toàn bộ bộ nhớ game...";
		string status = await Task.Run(() => TransitGatePopupProbe.Read(target));
		DebugLog.AddDebugForProcess(target.ProcessId, status);
		AccountInfoText = $"PID={target.ProcessId}\r\n{status}\r\n\r\n"
			+ "Bấm HAI lần: lần 1 lúc popup ĐÓNG để chụp nền, lần 2 lúc popup ĐANG MỞ để lấy phần chữ mới.\r\n"
			+ "Công cụ chỉ ĐỌC, không chọn điểm đến nào, không cần bật Auto tổng.";
	}

	// GỬI LỆNH THẬT vào game: bấm thử một số thứ tự trong popup Điểm chuyển tiếp rồi báo map thật sự tới.
	private async void StartTransitGateSelect() {
		if (game == null) {
			AccountInfoText = "Không có account game đang được chọn.";
			return;
		}
		GameWindow target = game;
		int command = transitGateCommand;
		int index = transitGateOptionIndex;
		AccountInfoText = $"PID={target.ProcessId} | Đang gửi lệnh {command} với số {index}...";
		string result = await Task.Run(() => TransitGateSelectProbe.Run(target, command, index));
		DebugLog.AddDebugForProcess(target.ProcessId, result);
		AccountInfoText = $"PID={target.ProcessId}\r\n{result}\r\n\r\n"
			+ "Phải đứng lên Điểm chuyển tiếp cho popup hiện ra TRƯỚC khi bấm. Mỗi lần bấm thử ĐÚNG MỘT số.\r\n"
			+ "DỊCH CHUYỂN THẬT: nếu số đó là điểm đến thì nhân vật đi luôn.";
	}

	// GỬI LỆNH THẬT: nhân vật tự đi ra Điểm chuyển tiếp ở Triều Ca rồi chọn số thứ tự đang nhập.
	private async void StartTransitGateTravel() {
		if (game == null) {
			AccountInfoText = "Không có account game đang được chọn.";
			return;
		}
		GameWindow target = game;
		int index = transitGateOptionIndex;
		AccountInfoText = $"PID={target.ProcessId} | Đang đi ra Điểm chuyển tiếp rồi chọn số {index}...";
		string result = await Task.Run(() => TransitGateTravelProbe.Run(target, index));
		DebugLog.AddDebugForProcess(target.ProcessId, result);
		AccountInfoText = $"PID={target.ProcessId}\r\n{result}\r\n\r\n"
			+ "Nhân vật phải đang ở Triều Ca (Map 21). Không cần bật Auto tổng.\r\n"
			+ "DỊCH CHUYỂN THẬT: nhân vật tự đi ra điểm rồi đi tới map tương ứng với số đã nhập.";
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
