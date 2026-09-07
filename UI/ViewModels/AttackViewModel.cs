namespace Auto.UI.ViewModels;

using System.Collections.ObjectModel;
using System.Windows;
using Auto.Attack;
using Auto.Runtime;
using Auto.UI.Models;
using Auto.UI.Views;
using Auto.Utils;

public sealed class AttackViewModel : ViewModelBase {
	// Ctrl+A toggle theo tài khoản foreground được xử lý ở tầng Runtime/AccountController (đợt tích hợp sau),
	// không thêm control giả lập phím tắt trong UI này.
	// Quy đổi raw -> tọa độ hiển thị trong game, khớp DEV\UI\AttackPage.cs (RawXScale/RawYScale, FormatGameCoordinates).
	private const int RawXScale = 256;
	private const int RawYScale = 512;

	private readonly Settings settings;
	private readonly GameWindow? game;

	private MonsterOption selectedMonster;
	private MonsterOption selectedPlayer;

	private string selectedContinueMap;
	private string selectedContinueMonster = "";
	private CoordinateOption? selectedContinueCoordinate;

	private string selectedTeachingMap;
	private string selectedTeachingMonster = "";
	private CoordinateOption? selectedTeachingCoordinate;

	private string selectedTrainingGroup;
	private string selectedTrainingMap = "";
	private string selectedTrainingMonster = "";
	private CoordinateOption? selectedTrainingCoordinate;

	public AttackViewModel() : this(new Settings(), null) { }

	public AttackViewModel(Settings settings) : this(settings, null) { }

	public AttackViewModel(Settings settings, GameWindow? game) {
		this.settings = settings;
		this.game = game;

		// Giữ lại toạ độ bãi đang lưu trước khi dựng danh sách: setter Selected*Coordinate ghi thẳng vào settings, nên nếu
		// không lưu trước thì mỗi lần chọn sang account khác là toạ độ đã cấu hình của account đó bị ghi đè bằng mục đầu bảng.
		(int RawX, int RawY) savedContinue = (settings.ContinueRawX, settings.ContinueRawY);
		(int RawX, int RawY) savedTeaching = (settings.TeachingRawX, settings.TeachingRawY);
		(int RawX, int RawY) savedTraining = (settings.TrainingRawX, settings.TrainingRawY);

		// Khôi phục quái/người đã chọn từ settings, nếu không thì mỗi lần đổi account là ComboBox nhảy về "Toàn bộ"
		// dù settings vẫn giữ đúng lựa chọn. Gán vào field (không qua property) để không ghi ngược vào settings lúc khởi tạo.
		selectedMonster = RestoreSelection(Monsters, settings.SelectedMonsterName, settings.SelectedMonsterSignature, AutoFsClientProfile.MonsterType);
		selectedPlayer = RestoreSelection(Players, settings.SelectedPlayerName, settings.SelectedPlayerSignature, AutoFsClientProfile.PlayerType);
		selectedContinueMap = TrainingLocationCatalog.BeginnerMaps.Contains(settings.ContinueMap) ? settings.ContinueMap : TrainingLocationCatalog.BeginnerMaps[0];
		selectedTeachingMap = TrainingLocationCatalog.CityMaps.Contains(settings.TeachingMap) ? settings.TeachingMap : TrainingLocationCatalog.CityMaps[0];
		selectedTrainingGroup = TrainingLocationCatalog.MazeMaps.ContainsKey(settings.TrainingGroup) ? settings.TrainingGroup : TrainingLocationCatalog.MazeMaps.Keys.First();
		settings.ContinueMapId = TrainingLocationCatalog.GetMapId(selectedContinueMap);
		settings.TeachingMapId = TrainingLocationCatalog.GetMapId(selectedTeachingMap);

		RepopulateContinueMonsters(settings.ContinueMonster);
		RepopulateTeachingMonsters(settings.TeachingMonster);
		RepopulateTrainingMaps(settings.TrainingMap);

		SelectedContinueCoordinate = FindSavedCoordinate(ContinueCoordinates, savedContinue) ?? SelectedContinueCoordinate;
		SelectedTeachingCoordinate = FindSavedCoordinate(TeachingCoordinates, savedTeaching) ?? SelectedTeachingCoordinate;
		SelectedTrainingCoordinate = FindSavedCoordinate(TrainingCoordinates, savedTraining) ?? SelectedTrainingCoordinate;

		OpenClassPriorityCommand = new RelayCommand(_ => OpenClassPriorityDialog());
		RequestMonsterOptionsCommand = new RelayCommand(_ => RequestMonsterOptions());
	}

	public Settings Settings => settings;

	public bool Enabled {
		get => settings.Enabled;
		set {
			if (settings.Enabled == value) return;
			settings.Enabled = value;
			OnPropertyChanged();
		}
	}

	// Đồng bộ UI khi settings.Enabled bị đổi từ bên ngoài (hotkey Ctrl+A), không qua setter ở trên.
	public void RefreshEnabled() => OnPropertyChanged(nameof(Enabled));

	public bool EnableReturnToTraining {
		get => settings.EnableReturnToTraining;
		set {
			if (settings.EnableReturnToTraining == value) return;
			settings.EnableReturnToTraining = value;
			OnPropertyChanged();
		}
	}

	public bool UseCenterPosition {
		get => settings.UseCenterPosition;
		set {
			if (settings.UseCenterPosition == value) return;
			settings.UseCenterPosition = value;
			OnPropertyChanged();
			if (value && settings.CenterX <= 0 && settings.CenterY <= 0) GetCurrentPosition();
		}
	}

	public string Center {
		get => $"{settings.CenterX / RawXScale}/{settings.CenterY / RawYScale}";
		set {
			string[] parts = value.Split('/', StringSplitOptions.TrimEntries);
			if (parts.Length == 2 && int.TryParse(parts[0], out int x) && int.TryParse(parts[1], out int y)) {
				settings.CenterX = x * RawXScale;
				settings.CenterY = y * RawYScale;
				StoreCenterMapId();
			}
			OnPropertyChanged();
		}
	}

	public int Range {
		get => settings.Range;
		set {
			int clamped = Math.Clamp(value, 1, 9999);
			if (settings.Range == clamped) return;
			settings.Range = clamped;
			OnPropertyChanged();
		}
	}

	public bool AttackMonsters {
		get => settings.AttackMonsters;
		set {
			if (settings.AttackMonsters == value) return;
			settings.AttackMonsters = value;
			OnPropertyChanged();
		}
	}

	public ObservableCollection<MonsterOption> Monsters { get; } = [MonsterOption.All];

	public MonsterOption SelectedMonster {
		get => selectedMonster;
		set {
			// WPF đẩy null về khi Monsters.Clear() làm SelectedItem không còn hợp lệ; bỏ qua để không mất lựa chọn đã lưu.
			if (value == null) return;
			if (!SetField(ref selectedMonster, value)) return;
			settings.OnlySelectedMonster = !value.IsAll;
			settings.SelectedMonsterName = value.IsAll ? "" : value.Name;
			settings.SelectedMonsterSignature = value.IsAll ? "" : value.Signature;
		}
	}

	public bool AttackPlayers {
		get => settings.AttackPlayers;
		set {
			if (settings.AttackPlayers == value) return;
			settings.AttackPlayers = value;
			OnPropertyChanged();
		}
	}

	public ObservableCollection<MonsterOption> Players { get; } = [MonsterOption.All];

	public MonsterOption SelectedPlayer {
		get => selectedPlayer;
		set {
			// WPF đẩy null về khi Players.Clear() làm SelectedItem không còn hợp lệ; bỏ qua để không mất lựa chọn đã lưu.
			if (value == null) return;
			if (!SetField(ref selectedPlayer, value)) return;
			settings.OnlySelectedPlayer = !value.IsAll;
			settings.SelectedPlayerName = value.IsAll ? "" : value.Name;
			settings.SelectedPlayerSignature = value.IsAll ? "" : value.Signature;
		}
	}

	public bool PrioritizeSummonerPet {
		get => settings.PrioritizeSummonerPet;
		set {
			if (settings.PrioritizeSummonerPet == value) return;
			settings.PrioritizeSummonerPet = value;
			OnPropertyChanged();
		}
	}

	public bool PrioritizeLowHp {
		get => settings.PrioritizeLowHp;
		set {
			if (settings.PrioritizeLowHp == value) return;
			settings.PrioritizeLowHp = value;
			OnPropertyChanged();
		}
	}

	public int PrioritizeLowHpPercent {
		get => settings.PrioritizeLowHpPercent;
		set {
			int clamped = Math.Clamp(value, 1, 100);
			if (settings.PrioritizeLowHpPercent == clamped) return;
			settings.PrioritizeLowHpPercent = clamped;
			OnPropertyChanged();
		}
	}

	public bool PrioritizeClass {
		get => settings.PrioritizeClass;
		set {
			if (settings.PrioritizeClass == value) return;
			settings.PrioritizeClass = value;
			OnPropertyChanged();
		}
	}

	public bool PrioritizeTaoist {
		get => settings.PrioritizeTaoist;
		set {
			if (settings.PrioritizeTaoist == value) return;
			settings.PrioritizeTaoist = value;
			OnPropertyChanged();
		}
	}

	public bool PrioritizeSummoner {
		get => settings.PrioritizeSummoner;
		set {
			if (settings.PrioritizeSummoner == value) return;
			settings.PrioritizeSummoner = value;
			OnPropertyChanged();
		}
	}

	public bool PrioritizeWarrior {
		get => settings.PrioritizeWarrior;
		set {
			if (settings.PrioritizeWarrior == value) return;
			settings.PrioritizeWarrior = value;
			OnPropertyChanged();
		}
	}

	public bool ExcludeCamelTransform {
		get => settings.ExcludeCamelTransform;
		set {
			if (settings.ExcludeCamelTransform == value) return;
			settings.ExcludeCamelTransform = value;
			OnPropertyChanged();
		}
	}

	// Tân thủ
	public string[] ContinueMaps { get; } = TrainingLocationCatalog.BeginnerMaps;

	public bool ContinueEnabled {
		get => settings.ContinueEnabled;
		set {
			if (settings.ContinueEnabled == value) return;
			settings.ContinueEnabled = value;
			OnPropertyChanged();
			if (value) {
				TeachingEnabled = false;
				TrainingEnabled = false;
			}
		}
	}

	public string SelectedContinueMap {
		get => selectedContinueMap;
		set {
			if (!SetField(ref selectedContinueMap, value)) return;
			settings.ContinueMap = value;
			settings.ContinueMapId = TrainingLocationCatalog.GetMapId(value);
			RepopulateContinueMonsters(null);
		}
	}

	public ObservableCollection<string> ContinueMonsters { get; } = [];

	public string SelectedContinueMonster {
		get => selectedContinueMonster;
		set {
			if (!SetField(ref selectedContinueMonster, value)) return;
			settings.ContinueMonster = value;
			RepopulateContinueCoordinates(null);
		}
	}

	public ObservableCollection<CoordinateOption> ContinueCoordinates { get; } = [];

	public CoordinateOption? SelectedContinueCoordinate {
		get => selectedContinueCoordinate;
		set {
			if (!SetField(ref selectedContinueCoordinate, value)) return;
			settings.ContinueCoordinate = value?.Display ?? "";
			settings.ContinueRawX = value?.RawX ?? 0;
			settings.ContinueRawY = value?.RawY ?? 0;
		}
	}

	// Thành thị
	public string[] TeachingMaps { get; } = TrainingLocationCatalog.CityMaps;

	public bool TeachingEnabled {
		get => settings.TeachingEnabled;
		set {
			if (settings.TeachingEnabled == value) return;
			settings.TeachingEnabled = value;
			OnPropertyChanged();
			if (value) {
				ContinueEnabled = false;
				TrainingEnabled = false;
			}
		}
	}

	public string SelectedTeachingMap {
		get => selectedTeachingMap;
		set {
			if (!SetField(ref selectedTeachingMap, value)) return;
			settings.TeachingMap = value;
			settings.TeachingMapId = TrainingLocationCatalog.GetMapId(value);
			RepopulateTeachingMonsters(null);
		}
	}

	public ObservableCollection<string> TeachingMonsters { get; } = [];

	public string SelectedTeachingMonster {
		get => selectedTeachingMonster;
		set {
			if (!SetField(ref selectedTeachingMonster, value)) return;
			settings.TeachingMonster = value;
			RepopulateTeachingCoordinates(null);
		}
	}

	public ObservableCollection<CoordinateOption> TeachingCoordinates { get; } = [];

	public CoordinateOption? SelectedTeachingCoordinate {
		get => selectedTeachingCoordinate;
		set {
			if (!SetField(ref selectedTeachingCoordinate, value)) return;
			settings.TeachingCoordinate = value?.Display ?? "";
			settings.TeachingRawX = value?.RawX ?? 0;
			settings.TeachingRawY = value?.RawY ?? 0;
		}
	}

	// Mê cung
	public string[] TrainingGroups { get; } = TrainingLocationCatalog.MazeMaps.Keys.ToArray();

	public bool TrainingEnabled {
		get => settings.TrainingEnabled;
		set {
			if (settings.TrainingEnabled == value) return;
			settings.TrainingEnabled = value;
			OnPropertyChanged();
			if (value) {
				ContinueEnabled = false;
				TeachingEnabled = false;
			}
		}
	}

	public string SelectedTrainingGroup {
		get => selectedTrainingGroup;
		set {
			if (!SetField(ref selectedTrainingGroup, value)) return;
			settings.TrainingGroup = value;
			RepopulateTrainingMaps(null);
		}
	}

	public ObservableCollection<string> TrainingMaps { get; } = [];

	public string SelectedTrainingMap {
		get => selectedTrainingMap;
		set {
			if (!SetField(ref selectedTrainingMap, value)) return;
			settings.TrainingMap = value;
			settings.TrainingMapId = TrainingLocationCatalog.GetMapId(value);
			RepopulateTrainingMonsters(null);
		}
	}

	public ObservableCollection<string> TrainingMonsters { get; } = [];

	public string SelectedTrainingMonster {
		get => selectedTrainingMonster;
		set {
			if (!SetField(ref selectedTrainingMonster, value)) return;
			settings.TrainingMonster = value;
			RepopulateTrainingCoordinates(null);
		}
	}

	public ObservableCollection<CoordinateOption> TrainingCoordinates { get; } = [];

	public CoordinateOption? SelectedTrainingCoordinate {
		get => selectedTrainingCoordinate;
		set {
			if (!SetField(ref selectedTrainingCoordinate, value)) return;
			settings.TrainingCoordinate = value?.Display ?? "";
			settings.TrainingRawX = value?.RawX ?? 0;
			settings.TrainingRawY = value?.RawY ?? 0;
		}
	}

	public RelayCommand OpenClassPriorityCommand { get; }

	public RelayCommand RequestMonsterOptionsCommand { get; }

	private void RequestMonsterOptions() {
		if (game == null) return;
		IReadOnlyList<MonsterOption> options;
		lock (game.AutoSync) {
			game.RuntimeLayout = RuntimeLayoutResolver.Resolve(game.ProcessId);
			if (!game.RuntimeLayout.AttackReady) return;
			options = game.AttackEngine.DiscoverMonsterOptions(GameMemory.ReadSnapshot(game.ProcessId));
		}

		string previousMonsterSignature = settings.SelectedMonsterSignature;
		string previousMonsterName = settings.SelectedMonsterName;
		string previousPlayerSignature = settings.SelectedPlayerSignature;
		string previousPlayerName = settings.SelectedPlayerName;

		Monsters.Clear();
		Monsters.Add(MonsterOption.All);
		foreach (MonsterOption option in options.Where(option => option.Type == AutoFsClientProfile.MonsterType).GroupBy(option => option.Signature).Select(group => group.First()).OrderBy(option => option.Name)) Monsters.Add(option);
		// Bảo lưu quái đang chọn ngay cả khi tạm thời ra khỏi phạm vi quét, khớp DEV\UI\AttackPage.cs:414.
		if (!string.IsNullOrWhiteSpace(previousMonsterSignature) && Monsters.All(option => option.Signature != previousMonsterSignature)) Monsters.Add(new MonsterOption(previousMonsterName, previousMonsterSignature, AutoFsClientProfile.MonsterType));

		Players.Clear();
		Players.Add(MonsterOption.All);
		foreach (MonsterOption option in options.Where(option => option.Type == AutoFsClientProfile.PlayerType).GroupBy(option => option.Signature).Select(group => group.First()).OrderBy(option => option.Name)) Players.Add(option);
		if (!string.IsNullOrWhiteSpace(previousPlayerSignature) && Players.All(option => option.Signature != previousPlayerSignature)) Players.Add(new MonsterOption(previousPlayerName, previousPlayerSignature, AutoFsClientProfile.PlayerType));

		SelectedMonster = Monsters.FirstOrDefault(option => option.Signature == previousMonsterSignature) ?? Monsters[0];
		SelectedPlayer = Players.FirstOrDefault(option => option.Signature == previousPlayerSignature) ?? Players[0];
	}

	private void GetCurrentPosition() {
		if (game == null) return;
		settings.CenterX = game.X;
		settings.CenterY = game.Y;
		StoreCenterMapId();
		OnPropertyChanged(nameof(Center));
	}

	// Ghi lại map đang đứng cùng lúc với tâm bãi, để luồng lên bãi biết phải đi tới map nào khi nhân vật ở nơi khác.
	// Chỉ ghi khi đọc được map thật, không ghi đè bằng 0 làm mất map đã lưu trước đó.
	private void StoreCenterMapId() {
		if (game == null) return;
		GameMapInfo map = GameMapReader.Read(game.ProcessId);
		if (map.Success && map.MapId > 0) settings.CenterMapId = map.MapId;
	}

	// Nạp sẵn lựa chọn đã lưu vào danh sách để ComboBox có phần tử khớp mà hiển thị, rồi trả về chính mục đó.
	private static MonsterOption RestoreSelection(ObservableCollection<MonsterOption> options, string savedName, string savedSignature, int type) {
		if (string.IsNullOrWhiteSpace(savedSignature)) return MonsterOption.All;
		MonsterOption? existing = options.FirstOrDefault(option => option.Signature == savedSignature);
		if (existing != null) return existing;
		MonsterOption restored = new(savedName, savedSignature, type);
		options.Add(restored);
		return restored;
	}

	// Tìm lại đúng mục toạ độ ứng với giá trị đang lưu trong settings; trả null khi chưa có hoặc không còn trong danh sách.
	private static CoordinateOption? FindSavedCoordinate(IEnumerable<CoordinateOption> options, (int RawX, int RawY) saved) {
		if (saved.RawX <= 0 || saved.RawY <= 0) return null;
		return options.FirstOrDefault(option => option.RawX == saved.RawX && option.RawY == saved.RawY);
	}

	private void RepopulateContinueMonsters(string? preferredMonster) {
		ContinueMonsters.Clear();
		foreach (string monster in TrainingLocationCatalog.GetMonsters(selectedContinueMap)) ContinueMonsters.Add(monster);
		SelectedContinueMonster = preferredMonster != null && ContinueMonsters.Contains(preferredMonster) ? preferredMonster : ContinueMonsters.Count > 0 ? ContinueMonsters[0] : "";
	}

	private void RepopulateContinueCoordinates(CoordinateOption? preferredCoordinate) {
		ContinueCoordinates.Clear();
		foreach (CoordinateOption coordinate in TrainingLocationCatalog.GetCoordinates(selectedContinueMap, selectedContinueMonster)) ContinueCoordinates.Add(coordinate);
		SelectedContinueCoordinate = preferredCoordinate != null && ContinueCoordinates.Contains(preferredCoordinate) ? preferredCoordinate : ContinueCoordinates.Count > 0 ? ContinueCoordinates[0] : null;
	}

	private void RepopulateTeachingMonsters(string? preferredMonster) {
		TeachingMonsters.Clear();
		foreach (string monster in TrainingLocationCatalog.GetMonsters(selectedTeachingMap)) TeachingMonsters.Add(monster);
		SelectedTeachingMonster = preferredMonster != null && TeachingMonsters.Contains(preferredMonster) ? preferredMonster : TeachingMonsters.Count > 0 ? TeachingMonsters[0] : "";
	}

	private void RepopulateTeachingCoordinates(CoordinateOption? preferredCoordinate) {
		TeachingCoordinates.Clear();
		foreach (CoordinateOption coordinate in TrainingLocationCatalog.GetCoordinates(selectedTeachingMap, selectedTeachingMonster)) TeachingCoordinates.Add(coordinate);
		SelectedTeachingCoordinate = preferredCoordinate != null && TeachingCoordinates.Contains(preferredCoordinate) ? preferredCoordinate : TeachingCoordinates.Count > 0 ? TeachingCoordinates[0] : null;
	}

	private void RepopulateTrainingMaps(string? preferredMap) {
		TrainingMaps.Clear();
		foreach (string map in TrainingLocationCatalog.MazeMaps.GetValueOrDefault(selectedTrainingGroup) ?? []) TrainingMaps.Add(map);
		SelectedTrainingMap = preferredMap != null && TrainingMaps.Contains(preferredMap) ? preferredMap : TrainingMaps.Count > 0 ? TrainingMaps[0] : "";
	}

	private void RepopulateTrainingMonsters(string? preferredMonster) {
		TrainingMonsters.Clear();
		foreach (string monster in TrainingLocationCatalog.GetMonsters(selectedTrainingMap)) TrainingMonsters.Add(monster);
		SelectedTrainingMonster = preferredMonster != null && TrainingMonsters.Contains(preferredMonster) ? preferredMonster : TrainingMonsters.Count > 0 ? TrainingMonsters[0] : "";
	}

	private void RepopulateTrainingCoordinates(CoordinateOption? preferredCoordinate) {
		TrainingCoordinates.Clear();
		foreach (CoordinateOption coordinate in TrainingLocationCatalog.GetCoordinates(selectedTrainingMap, selectedTrainingMonster)) TrainingCoordinates.Add(coordinate);
		SelectedTrainingCoordinate = preferredCoordinate != null && TrainingCoordinates.Contains(preferredCoordinate) ? preferredCoordinate : TrainingCoordinates.Count > 0 ? TrainingCoordinates[0] : null;
	}

	private void OpenClassPriorityDialog() {
		ClassPriorityDialogViewModel dialogViewModel = new(PrioritizeTaoist, PrioritizeSummoner, PrioritizeWarrior);
		ClassPriorityDialog dialog = new() { DataContext = dialogViewModel, Owner = Application.Current.MainWindow };

		if (dialog.ShowDialog() != true) return;

		PrioritizeTaoist = dialogViewModel.Taoist;
		PrioritizeSummoner = dialogViewModel.Summoner;
		PrioritizeWarrior = dialogViewModel.Warrior;
	}
}
