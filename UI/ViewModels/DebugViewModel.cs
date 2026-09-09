namespace Auto.UI.ViewModels;

using System.Collections.ObjectModel;
using Auto.DebugTools;
using Auto.Runtime;
using Auto.Sale;
using Auto.Utils;

public sealed class DebugViewModel : ViewModelBase {
	// Ba mục đầu là công cụ chẩn đoán cố định, không được xóa.
	private const string ToolSelectedAccountIdentity = "Thông tin account/PID";
	private const string ToolClientAddressAudit = "Thông tin địa chỉ client";
	private const string ToolInventoryInfo = "Thông tin túi đồ";
	private const string ToolEliteMonsterInfo = "Thông tin quái thủ lĩnh";
	private const string ToolPetOwner = "Thông tin Đệ";
	private const string ToolReturnTalisman = "Thông tin Hồi thành phù";
	private const string ToolHotkeyReturnTalisman = "Dùng Hồi thành phù bằng phím tắt";
	// Hai probe chẩn đoán popup NPC. Đã bị xoá nhầm lúc dọn probe 2026-09-07 rồi khôi phục 2026-09-08 khi luồng
	// Sửa đồ với NPC dạng popup xác nhận hỏng mà không còn công cụ nào đọc được vtable thật của popup.
	private const string ToolModalVtable = "Thông tin popup (vtable)";
	private const string ToolNpcMenuCapture = "Thông tin menu NPC";
	private const string ToolImmediateSale = "Bán ngay (shop đang mở)";
	private const string ToolImmediateShopRepair = "Sửa ngay (shop đang mở)";
	private const string ToolImmediateRepair = "Đi sửa đồ";

	private const int ImmediateSaleTimeoutSeconds = 45;
	private const int ImmediateSalePollMilliseconds = 100;

	private readonly GameWindow? game;
	private bool enableDebugLogging = true;
	private string selectedTool;
	private string accountInfoText = "";

	public DebugViewModel() : this(null) { }

	public DebugViewModel(GameWindow? game) {
		this.game = game;
		selectedTool = ToolSelectedAccountIdentity;
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
		ToolSelectedAccountIdentity,
		ToolClientAddressAudit,
		ToolInventoryInfo,
		ToolEliteMonsterInfo,
		ToolPetOwner,
		ToolReturnTalisman,
		ToolHotkeyReturnTalisman,
		ToolModalVtable,
		ToolNpcMenuCapture,
		ToolImmediateSale,
		ToolImmediateShopRepair,
		ToolImmediateRepair
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
			case ToolSelectedAccountIdentity:
				ShowAccountInfo();
				return;
			case ToolClientAddressAudit:
				StartClientAddressAudit();
				return;
			case ToolInventoryInfo:
				StartInventoryInfo();
				return;
			case ToolEliteMonsterInfo:
				StartEliteMonsterInfo();
				return;
			case ToolPetOwner:
				StartPetOwnerProbe();
				return;
			case ToolReturnTalisman:
				StartReturnTalismanProbe();
				return;
			case ToolHotkeyReturnTalisman:
				StartHotkeyReturnTalisman();
				return;
			case ToolModalVtable:
				StartModalVtableProbe();
				return;
			case ToolNpcMenuCapture:
				StartNpcMenuCapture();
				return;
			case ToolImmediateSale:
				StartImmediateSale();
				return;
			case ToolImmediateShopRepair:
				StartImmediateShopRepair();
				return;
			case ToolImmediateRepair:
				StartWeaponRepairFlowTest();
				return;
			default:
				AccountInfoText = $"Không nhận diện được công cụ: {selectedTool}";
				return;
		}
	}

	// Chạy ClientAddressAudit.Run và hiển thị nguyên khối kết quả, khớp DEV\UI\Debug.cs.StartClientAddressAudit.
	private async void StartClientAddressAudit() {
		if (game == null) {
			AccountInfoText = "Không có account game đang được chọn.";
			return;
		}
		AccountInfoText = $"PID={game.ProcessId} | Đang kiểm tra toàn bộ địa chỉ client...";
		List<string> lines = await Task.Run(() => ClientAddressAudit.Run(game));
		foreach (string line in lines) DebugLog.AddDebugForProcess(game.ProcessId, line);
		AccountInfoText = string.Join("\r\n", lines);
	}

	// Liệt kê tên, số lượng và trọng lượng vật phẩm ở cả 3 container, chỉ đọc bộ nhớ.
	private async void StartInventoryInfo() {
		if (game == null) {
			AccountInfoText = "Không có account game đang được chọn.";
			return;
		}
		int processId = game.ProcessId;
		AccountInfoText = $"PID={processId} | Đang đọc túi đồ...";
		string result = await Task.Run(() => InventoryInfoProbe.Run(processId));
		DebugLog.AddDebugForProcess(processId, result);
		AccountInfoText = $"PID={processId} | {result}";
	}

	// Liệt kê quái quanh nhân vật kèm phán quyết thủ lĩnh/boss và byte thô của tên, chỉ đọc bộ nhớ.
	private async void StartEliteMonsterInfo() {
		if (game == null) {
			AccountInfoText = "Không có account game đang được chọn.";
			return;
		}
		int processId = game.ProcessId;
		AccountInfoText = $"PID={processId} | Đang quét quái quanh nhân vật...";
		string result = await Task.Run(() => EliteMonsterProbe.Run(processId));
		DebugLog.AddDebugForProcess(processId, result);
		AccountInfoText = $"PID={processId} | {result}";
	}

	// Dò cách tách Đệ của nhân vật ra khỏi các entity type 6 khác. Phải có Đệ đang ra ngoài khi chạy.
	private async void StartPetOwnerProbe() {
		if (game == null) {
			AccountInfoText = "Không có account game đang được chọn.";
			return;
		}
		int processId = game.ProcessId;
		AccountInfoText = $"PID={processId} | Đang quét entity type 6...";
		string result = await Task.Run(() => PetOwnerProbe.Run(processId));
		DebugLog.AddDebugForProcess(processId, result);
		AccountInfoText = $"PID={processId} | {result}";
	}

	// Kiểm từng cổng của tính năng Hồi thành phù, không đụng gì vào game.
	private async void StartReturnTalismanProbe() {
		if (game == null) {
			AccountInfoText = "Không có account game đang được chọn.";
			return;
		}
		GameWindow target = game;
		AccountInfoText = $"PID={target.ProcessId} | Đang kiểm tính năng Hồi thành phù...";
		string result = await Task.Run(() => ReturnTalismanProbe.Inspect(target));
		DebugLog.AddDebugForProcess(target.ProcessId, result);
		AccountInfoText = $"PID={target.ProcessId} | {result}";
	}

	// GỬI PHÍM THẬT vào cửa sổ game theo đường phím tắt trang bị nhanh. Bùa phải nằm ở container 11.
	private async void StartHotkeyReturnTalisman() {
		if (game == null) {
			AccountInfoText = "Không có account game đang được chọn.";
			return;
		}
		GameWindow target = game;
		AccountInfoText = $"PID={target.ProcessId} | Đang gửi phím tắt trang bị nhanh...";
		string result = await Task.Run(() => ReturnTalismanProbe.UseByHotkey(target));
		DebugLog.AddDebugForProcess(target.ProcessId, result);
		AccountInfoText = $"PID={target.ProcessId} | {result}";
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

	// Chạy một lượt kiểm tra sửa toàn bộ khi account đã mở sẵn shop NPC.
	private async void StartImmediateShopRepair() {
		if (game == null) {
			AccountInfoText = "Không có account game đang được chọn.";
			return;
		}
		if (!game.AutoFsActionGate.MasterEnabled) {
			AccountInfoText = "Hãy bật Auto tổng cho account đã chọn để command nội bộ hoạt động; có thể tắt riêng Đánh và Nhặt.";
			return;
		}
		if (game.WeaponRepairAutomation.IsBusy) {
			AccountInfoText = "Luồng Sửa đồ tự động đang bận. Hãy tắt riêng Sửa đồ trước khi chạy debug shop.";
			return;
		}
		AccountInfoText = $"PID={game.ProcessId} | Debug Sửa ngay bắt đầu. Giữ shop đang mở.";
		string result = await Task.Run(() => ShopRepairDebugCommand.Run(game, text => DebugLog.AddDebugForProcess(game.ProcessId, text)));
		DebugLog.AddDebugForProcess(game.ProcessId, result);
		AccountInfoText = $"PID={game.ProcessId} | {result}";
	}

	// Đi sửa đồ toàn bộ theo yêu cầu, bỏ qua ngưỡng độ bền; giữ Auto tổng đang bật.
	private void StartWeaponRepairFlowTest() {
		if (game == null) {
			AccountInfoText = "Không có account game đang được chọn.";
			return;
		}
		string status;
		lock (game.AutoSync) status = game.WeaponRepairAutomation.RequestDebugRun();
		DebugLog.AddForProcess(game.ProcessId, status);
		AccountInfoText = $"PID={game.ProcessId} | {status}\r\n" +
			"Chỉ cần bật Auto tổng, KHÔNG cần bật Đánh. Nhân vật tự đi tới NPC, sửa toàn bộ rồi quay lại bãi, bỏ qua ngưỡng độ bền.\r\n" +
			"Xem repair.log để biết NPC thuộc dạng nào: \"Doctor confirmation modal\" = popup xác nhận, \"Doctor shop menu\" = menu nhiều lựa chọn.";
	}

	private async void StartImmediateSale() {
		if (game == null) {
			AccountInfoText = "Không có account game đang được chọn.";
			return;
		}
		if (game.Enabled) {
			AccountInfoText = "Hãy tắt Auto tổng trước khi chạy Bán ngay để tránh tranh chấp với Tự đánh/Nhặt/Sửa đồ.";
			return;
		}
		if (game.WeaponRepairAutomation.IsBusy) {
			AccountInfoText = "Không thể chạy Bán ngay vì flow Sửa đồ đang bận.";
			return;
		}
		if (!IsShopReadyForImmediateSale(game.ProcessId, out string shopState)) {
			AccountInfoText = "Bán ngay yêu cầu bạn mở shop NPC thủ công trước. " + shopState;
			return;
		}

		AccountInfoText = $"PID={game.ProcessId} | Bán ngay bắt đầu. Không đóng shop cho tới khi hoàn tất.";
		GameWindow saleGame = game;
		string finalText = await Task.Run(() => RunImmediateSale(saleGame));
		AccountInfoText = finalText;
	}

	// Chạy nền toàn bộ vòng bán và chỉ trả về câu kết luận để nơi gọi cập nhật giao diện trên luồng UI.
	private static string RunImmediateSale(GameWindow game) {
		Action<string> log = text => DebugLog.AddDebugForProcess(game.ProcessId, text);
		lock (game.AutoSync) game.InventorySaleEngine.Start(log);
		DateTime deadlineUtc = DateTime.UtcNow.AddSeconds(ImmediateSaleTimeoutSeconds);
		while (DateTime.UtcNow < deadlineUtc) {
			InventorySaleTickResult result;
			string failure;
			lock (game.AutoSync) result = game.InventorySaleEngine.Tick(game.ProcessId, game.Handle, log, out failure);
			if (result == InventorySaleTickResult.Completed) return $"PID={game.ProcessId} | Bán ngay hoàn tất. Kiểm tra log SALE_*.";
			if (result == InventorySaleTickResult.Failed) return $"PID={game.ProcessId} | Bán ngay dừng an toàn | {failure}";
			Thread.Sleep(ImmediateSalePollMilliseconds);
		}
		lock (game.AutoSync) game.InventorySaleEngine.Reset();
		return $"PID={game.ProcessId} | Bán ngay timeout sau {ImmediateSaleTimeoutSeconds} giây.";
	}

	private static bool IsShopReadyForImmediateSale(int processId, out string state) {
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			if (moduleBase == IntPtr.Zero) {
				state = "Game.exe không tồn tại.";
				return false;
			}
			uint modalState = unchecked((uint)reader.ReadInt32(IntPtr.Add(moduleBase, GameAddresses.Globals.ModalState)));
			uint shopState = unchecked((uint)reader.ReadInt32(IntPtr.Add(moduleBase, GameAddresses.Globals.ShopState)));
			state = $"ModalState={modalState} | ShopState={shopState}";
			return modalState == 0 && shopState == 2;
		} catch (Exception ex) {
			state = ex.GetType().Name + ": " + ex.Message;
			return false;
		}
	}

	private void ShowAccountInfo() {
		if (game == null) {
			AccountInfoText = "Không có account game đang được chọn.";
			return;
		}
		AccountInfoText =
			"===== Selected Account Identity =====\r\n" +
			"BuildStamp = SELECTED-ACCOUNT-PID-20260728-01\r\n" +
			$"ProcessId = {game.ProcessId}\r\n" +
			$"Tên = {game.DisplayName}\r\n" +
			$"Level = {game.Level}\r\n" +
			$"HP = {game.Hp}/{game.MaxHp}\r\n" +
			$"CE Process = {game.ProcessId:X8}-Game.exe\r\n" +
			"Status = COMPLETED";
	}
}
