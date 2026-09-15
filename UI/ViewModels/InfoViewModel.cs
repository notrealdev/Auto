namespace Auto.UI.ViewModels;

using System.Collections.ObjectModel;
using Auto.DebugTools;
using Auto.Runtime;

// Tab "Thông tin": các công cụ CHỈ ĐỌC bộ nhớ, không gửi lệnh nào vào game.
//
// Tách khỏi tab Debug ngày 2026-09-12 theo yêu cầu chủ dự án. Ranh giới: mục nào chỉ đọc thì ở đây, mục nào gửi
// lệnh vào client (dịch chuyển thật, bấm nút thật, mở client, đi sửa đồ) thì ở lại tab Debug.
public sealed class InfoViewModel : ViewModelBase {
	private const string ToolSelectedAccountIdentity = "Thông tin account/PID";
	private const string ToolClientAddressAudit = "Thông tin địa chỉ client";
	private const string ToolInventoryInfo = "Thông tin túi đồ";
	private const string ToolEliteMonsterInfo = "Thông tin quái thủ lĩnh";
	private const string ToolEntityTableDump = "Đổ nguyên bảng entity (tìm NPC)";
	private const string ToolGroundItemRecordDump = "Thông tin item dưới đất";
	private const string ToolQuestInfo = "Thông tin nhiệm vụ";
	private const string ToolPetOwner = "Thông tin Đệ";
	private const string ToolReturnTalisman = "Thông tin Hồi thành phù";

	private readonly GameWindow? game;
	private string selectedTool;
	private string outputText = "";

	public InfoViewModel() : this(null) { }

	public InfoViewModel(GameWindow? game) {
		this.game = game;
		selectedTool = ToolSelectedAccountIdentity;
		RunToolCommand = new RelayCommand(_ => RunTool());
		ClearOutputCommand = new RelayCommand(_ => OutputText = "");
	}

	public ObservableCollection<string> Tools { get; } = [
		ToolSelectedAccountIdentity,
		ToolClientAddressAudit,
		ToolInventoryInfo,
		ToolEliteMonsterInfo,
		ToolEntityTableDump,
		ToolGroundItemRecordDump,
		ToolQuestInfo,
		ToolPetOwner,
		ToolReturnTalisman
	];

	public string SelectedTool {
		get => selectedTool;
		set => SetField(ref selectedTool, value);
	}

	public string OutputText {
		get => outputText;
		private set => SetField(ref outputText, value);
	}

	public RelayCommand RunToolCommand { get; }

	public RelayCommand ClearOutputCommand { get; }

	private void RunTool() {
		switch (selectedTool) {
			case ToolSelectedAccountIdentity:
				ShowAccountInfo();
				return;
			case ToolClientAddressAudit:
				StartClientAddressAudit();
				return;
			case ToolInventoryInfo:
				RunForProcess("Đang đọc túi đồ...", InventoryInfoProbe.Run);
				return;
			case ToolEliteMonsterInfo:
				RunForProcess("Đang quét quái quanh nhân vật...", EliteMonsterProbe.Run);
				return;
			case ToolEntityTableDump:
				RunForProcess("Đang đổ bảng entity...", EntityTableDumpProbe.Run);
				return;
			case ToolGroundItemRecordDump:
				RunForProcess("Đang đọc record item dưới đất...", GroundItemRecordDumpProbe.Run);
				return;
			case ToolQuestInfo:
				RunForWindow("Đang kiểm mốc nhận diện nhiệm vụ...", QuestProbe.Run);
				return;
			case ToolPetOwner:
				RunForProcess("Đang quét entity type 6...", PetOwnerProbe.Run);
				return;
			case ToolReturnTalisman:
				RunForWindow("Đang kiểm tính năng Hồi thành phù...", ReturnTalismanProbe.Inspect);
				return;
			default:
				OutputText = $"Không nhận diện được công cụ: {selectedTool}";
				return;
		}
	}

	private async void RunForProcess(string busyText, Func<int, string> probe) {
		if (game == null) {
			OutputText = "Không có account game đang được chọn.";
			return;
		}
		int processId = game.ProcessId;
		OutputText = $"PID={processId} | {busyText}";
		string result = await Task.Run(() => probe(processId));
		DebugLog.AddDebugForProcess(processId, result);
		OutputText = $"PID={processId} | {result}";
	}

	private async void RunForWindow(string busyText, Func<GameWindow, string> probe) {
		if (game == null) {
			OutputText = "Không có account game đang được chọn.";
			return;
		}
		GameWindow target = game;
		OutputText = $"PID={target.ProcessId} | {busyText}";
		string result = await Task.Run(() => probe(target));
		DebugLog.AddDebugForProcess(target.ProcessId, result);
		OutputText = $"PID={target.ProcessId} | {result}";
	}

	private async void StartClientAddressAudit() {
		if (game == null) {
			OutputText = "Không có account game đang được chọn.";
			return;
		}
		GameWindow target = game;
		OutputText = $"PID={target.ProcessId} | Đang kiểm tra toàn bộ địa chỉ client...";
		List<string> lines = await Task.Run(() => ClientAddressAudit.Run(target));
		foreach (string line in lines) DebugLog.AddDebugForProcess(target.ProcessId, line);
		OutputText = string.Join("\r\n", lines);
	}

	private void ShowAccountInfo() {
		if (game == null) {
			OutputText = "Không có account game đang được chọn.";
			return;
		}
		OutputText =
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
