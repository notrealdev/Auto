namespace Auto.Runtime;

using System.Collections.Concurrent;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

// Lưu và khôi phục toàn bộ cấu hình của một account, khoá theo TÊN NHÂN VẬT.
//
// Vì sao cần: trước lượt này không một cấu hình account nào được lưu ra đĩa. Mọi Settings là POCO nằm trong
// GameWindow, mà GameWindow được khoá theo ProcessId ở AccountListViewModel.knownByProcessId và bị vứt đi khi
// cửa sổ game biến mất (ApplyScanResult, nhánh GAME_WINDOW_LOST). Đóng/mở lại client là mất sạch tâm bãi và mọi
// thứ đã cấu hình.
//
// HAI ĐIỀU ĐÃ ĐO BẰNG SCRATCHPAD NGÀY 2026-09-21, đừng đảo lại nếu chưa đo lại:
//   1. System.Text.Json KHÔNG đổ dữ liệu vào property Dictionary chỉ-có-getter. Đo trên chính Auto.Loot.Settings:
//      đảo một giá trị mặc định, thêm một khoá mới, xoá một khoá — sau round-trip cả ba đều không có tác dụng,
//      dictionary giữ nguyên giá trị do constructor dựng. Nên 4 dictionary của Loot.Settings PHẢI nạp tay qua
//      JsonNode, xem LoadLootDictionaries.
//   2. Tuỳ chọn mặc định escape toàn bộ tiếng Việt thành \uXXXX ("Đồ Trắng" -> "Đồ Trắng"), file
//      mở ra không đọc được bằng mắt. JavaScriptEncoder.Create(UnicodeRanges.All) ghi ra chữ Việt nguyên vẹn mà
//      VẪN escape các ký tự nhạy cảm HTML (< > & ' " -> < ...), nên không phải dùng bản
//      UnsafeRelaxedJsonEscaping.
//
// HÌNH DẠNG FILE (chủ dự án chốt 2026-09-22): một khối "Defaults" dùng chung, rồi mỗi account CHỈ liệt kê những
// trường khác Defaults.
//
//   { "Defaults": { ...đầy đủ... }, "Accounts": [ { "CharacterName": "...", "Attack": { "Range": 12 } } ] }
//
// Lý do đổi: bản ghi đầy đủ đo được 993 dòng / 28.343 byte cho 6 nhân vật, trong đó phần lớn là cùng một giá trị
// mặc định lặp lại 6 lần. Sửa Defaults bằng tay là cả 6 account đổi theo — đó chính là ngữ nghĩa được yêu cầu.
// Defaults đọc từ file được GIỮ NGUYÊN khi ghi lại, không dựng lại từ constructor, nếu không thì bản sửa tay của
// chủ dự án bị xoá ngay lần lưu kế tiếp.
//
// File ở dạng cũ (mỗi account ghi đầy đủ) vẫn đọc được không cần chuyển đổi: nó chỉ là trường hợp "diff chứa tất
// cả các trường". Lần lưu kế tiếp sẽ tự ghi lại ở dạng mới.
internal static class AccountProfileStore {
	// Tách hẳn khỏi thư mục Login/ — file đó chứa mật khẩu thật, không mở rộng vùng nhạy cảm sang thứ không bí mật.
	// Release/ đã nằm trong .gitignore nên file này không bao giờ vào git.
	private static readonly string ProfileDirectory = Path.Combine(AppContext.BaseDirectory, AppVersion.ProfilesDirectoryName);
	private static readonly string ProfilePath = Path.Combine(ProfileDirectory, "Profiles.json");

	private static readonly JsonSerializerOptions Options = new() {
		WriteIndented = true,
		IndentCharacter = '\t',
		IndentSize = 1,
		Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
	};

	private static readonly ConcurrentDictionary<string, AccountProfile> profilesByCharacter = new(StringComparer.Ordinal);
	private static string lastWrittenJson = "";

	// Nền để so diff. Nạp từ khối "Defaults" của file nếu có, không thì dựng từ constructor các lớp Settings.
	private static JsonObject defaultsNode = BuildConstructorDefaults();

	// Bốn dictionary của Loot.Settings so NGUYÊN KHỐI chứ không diff theo từng khoá: diff theo khoá không biểu diễn
	// được thao tác XOÁ một khoá có sẵn trong Defaults, nên khoá người dùng đã xoá trên giao diện sẽ sống lại sau
	// khi merge. Giống Defaults thì bỏ hẳn khỏi account, khác một khoá thôi thì ghi đủ cả khối.
	private static readonly HashSet<string> WholeBlockKeys = new(StringComparer.Ordinal) {
		"ItemSelections", "ExcludedItemSelections", "SaleItemSelections", "PotionNameSelections"
	};

	// Đọc file một lần lúc khởi động. Hỏng thì ghi log và chạy tiếp với mặc định — không được chặn app khởi động.
	public static void LoadAll() {
		try {
			if (! File.Exists(ProfilePath)) {
				DebugLog.AddProfileEvent($"PROFILE_LOAD | Chưa có file {ProfilePath}, chạy với cấu hình mặc định.");
				return;
			}
			string json = File.ReadAllText(ProfilePath);
			if (JsonNode.Parse(json) is not JsonObject root) {
				DebugLog.AddProfileEvent("PROFILE_LOAD_HỎNG | File rỗng hoặc không phải JSON hợp lệ.");
				return;
			}
			// Giữ nguyên bản Defaults đọc được để lần ghi sau không xoá mất phần chủ dự án sửa tay.
			if (root["Defaults"] is JsonObject storedDefaults) defaultsNode = storedDefaults.DeepClone().AsObject();
			foreach (JsonNode? accountNode in root["Accounts"] as JsonArray ?? []) {
				if (accountNode is not JsonObject overlay) continue;
				JsonObject merged = Merge(defaultsNode, overlay);
				AccountProfile? profile = merged.Deserialize<AccountProfile>(Options);
				if (profile == null || profile.CharacterName.Length == 0) continue;
				// 4 dictionary của Loot.Settings phải nạp tay — xem ghi chú đo được ở đầu file.
				LoadLootDictionaries(merged["Loot"], profile.Loot);
				profilesByCharacter[profile.CharacterName] = profile;
			}
			// Cố ý KHÔNG đặt lastWrittenJson từ chuỗi vừa đọc: chuỗi đó có SavedAtUtc và có thể còn ở dạng cũ, không
			// cùng khuôn với bản đem so ở SaveIfChanged. Để trống thì mỗi phiên chạy ghi lại đúng một lần (qua đó
			// chuyển file cũ sang dạng mới), rồi im lặng cho tới khi có thay đổi thật.
			DebugLog.AddProfileEvent($"PROFILE_LOAD | Đã đọc {profilesByCharacter.Count} hồ sơ từ {ProfilePath}.");
			LogSkippedFields();
		} catch (Exception ex) {
			DebugLog.AddProfileEvent($"PROFILE_LOAD_HỎNG | {ex.GetType().Name}: {ex.Message}");
		}
	}

	public static bool TryGet(string characterName, out AccountProfile profile) =>
		profilesByCharacter.TryGetValue(characterName, out profile!);

	// Dựng hồ sơ từ trạng thái sống. Người gọi phải đang giữ lock(game.AutoSync).
	public static AccountProfile Capture(GameWindow game) {
		// SavedAtUtc cố ý KHÔNG đặt ở đây: nó chỉ được đóng dấu đúng lúc ghi ra đĩa, xem SaveIfChanged.
		AccountProfile profile = new() {
			CharacterName = game.CharacterName,
			MasterEnabled = game.Enabled,
			SavedTrainingMapId = game.SavedTrainingMapId
		};
		SettingsCopier.CopyScalars(game.AttackSettings, profile.Attack);
		SettingsCopier.CopyScalars(game.LootSettings, profile.Loot);
		SettingsCopier.CopyScalars(game.SupportSettings, profile.Support);
		SettingsCopier.CopyScalars(game.BasicSettings, profile.Basic);
		SettingsCopier.CopyScalars(game.MarketSettings, profile.Market);
		SettingsCopier.CopyScalars(game.QuestSettings, profile.Quest);
		CopyDictionary(game.LootSettings.ItemSelections, profile.Loot.ItemSelections);
		CopyDictionary(game.LootSettings.ExcludedItemSelections, profile.Loot.ExcludedItemSelections);
		CopyDictionary(game.LootSettings.SaleItemSelections, profile.Loot.SaleItemSelections);
		CopyDictionary(game.LootSettings.PotionNameSelections, profile.Loot.PotionNameSelections);
		return profile;
	}

	// Chép hồ sơ ngược vào các đối tượng đang sống. Người gọi phải đang giữ lock(game.AutoSync).
	//
	// autoEnableMaster: chỉ true khi client do chính Auto đăng nhập lại. Client chủ dự án tự mở tay thì nạp đủ
	// tham số nhưng để Auto tổng TẮT, tránh Auto chen vào điều khiển ngoài ý muốn.
	public static void Apply(GameWindow game, AccountProfile profile, bool autoEnableMaster) {
		SettingsCopier.CopyScalars(profile.Attack, game.AttackSettings);
		SettingsCopier.CopyScalars(profile.Loot, game.LootSettings);
		SettingsCopier.CopyScalars(profile.Support, game.SupportSettings);
		SettingsCopier.CopyScalars(profile.Basic, game.BasicSettings);
		SettingsCopier.CopyScalars(profile.Market, game.MarketSettings);
		SettingsCopier.CopyScalars(profile.Quest, game.QuestSettings);
		CopyDictionary(profile.Loot.ItemSelections, game.LootSettings.ItemSelections);
		CopyDictionary(profile.Loot.ExcludedItemSelections, game.LootSettings.ExcludedItemSelections);
		CopyDictionary(profile.Loot.SaleItemSelections, game.LootSettings.SaleItemSelections);
		CopyDictionary(profile.Loot.PotionNameSelections, game.LootSettings.PotionNameSelections);

		// Dựng lại nhánh "bãi đã lưu" cho hai resolver bên Movement/ từ chính điểm đang chọn. Toạ độ lấy từ bộ ba
		// Center* vừa chép ở trên — đó CHÍNH LÀ điểm đã chọn, nên không phải tra lại danh sách dùng chung.
		game.SavedTrainingMapId = profile.SavedTrainingMapId;
		game.TrainingPositionsByMap.Clear();
		if (game.SavedTrainingMapId > 0 && game.AttackSettings.CenterMapId == game.SavedTrainingMapId
			&& game.AttackSettings.CenterX > 0 && game.AttackSettings.CenterY > 0) {
			game.TrainingPositionsByMap[game.SavedTrainingMapId] = (game.AttackSettings.CenterX, game.AttackSettings.CenterY);
		}

		game.Enabled = autoEnableMaster && profile.MasterEnabled;
		DebugLog.AddProfileEvent($"PROFILE_RESTORED | PID={game.ProcessId} | NhânVật={game.CharacterName} | TựMở={autoEnableMaster} | AutoTổng={game.Enabled} | Đánh={game.AttackSettings.Enabled} | Nhặt={game.LootSettings.Enabled} | Tâm={game.AttackSettings.CenterX}/{game.AttackSettings.CenterY} | MapTâm={game.AttackSettings.CenterMapId} | Range={game.AttackSettings.Range} | BãiĐãLưu={game.SavedTrainingMapId}");
	}

	// Ghi ra đĩa NẾU nội dung thật sự đổi so với lần ghi trước.
	//
	// So bằng chính chuỗi JSON cuối cùng chứ không so từng trường: một phép so chuỗi rẻ hơn nhiều so với rủi ro
	// bỏ sót một trường mới thêm.
	public static void SaveIfChanged(IReadOnlyList<AccountProfile> captured) {
		try {
			foreach (AccountProfile profile in captured) {
				if (profile.CharacterName.Length == 0) continue;
				profilesByCharacter[profile.CharacterName] = profile;
			}
			JsonArray accounts = [];
			foreach (AccountProfile profile in profilesByCharacter.Values.OrderBy(profile => profile.CharacterName, StringComparer.Ordinal)) {
				JsonObject slim = Diff(SerializeProfile(profile), defaultsNode);
				// Dấu thời gian bị loại khỏi bản đem so, xem ghi chú ngay dưới.
				slim.Remove("SavedAtUtc");
				// Khoá phải còn lại kể cả khi mọi thứ khác trùng Defaults, nếu không hồ sơ mất danh tính.
				slim["CharacterName"] = profile.CharacterName;
				accounts.Add(slim);
			}
			JsonObject root = new() {
				["Defaults"] = defaultsNode.DeepClone(),
				["Accounts"] = accounts
			};
			// So bản KHÔNG có dấu thời gian. Bản cũ đóng dấu SavedAtUtc ngay lúc chụp nên chuỗi JSON luôn khác chính
			// nó, phép so không bao giờ trúng, và file 27KB bị ghi đè mỗi 30 giây suốt ngày — đo được 30 dòng
			// PROFILE_SAVED liên tiếp cùng Bytes=27470 trong DiagnosticsBeta/profile.log ngày 2026-09-22.
			string comparable = root.ToJsonString(Options);
			if (string.Equals(comparable, lastWrittenJson, StringComparison.Ordinal)) return;
			lastWrittenJson = comparable;

			// Chỉ khi thật sự sắp ghi mới đóng dấu, một mốc chung cho cả file.
			string savedAtUtc = DateTime.UtcNow.ToString("O");
			foreach (JsonNode? account in accounts) {
				if (account is JsonObject node) node["SavedAtUtc"] = savedAtUtc;
			}
			string json = root.ToJsonString(Options);

			Directory.CreateDirectory(ProfileDirectory);
			// Ghi ra .tmp rồi đổi tên: app bị kill giữa lúc ghi thì file cũ vẫn nguyên vẹn, không để lại file cụt.
			string temporaryPath = ProfilePath + ".tmp";
			File.WriteAllText(temporaryPath, json);
			File.Move(temporaryPath, ProfilePath, true);
			DebugLog.AddProfileEvent($"PROFILE_SAVED | SốHồSơ={accounts.Count} | Bytes={json.Length}");
		} catch (Exception ex) {
			DebugLog.AddProfileEvent($"PROFILE_SAVE_HỎNG | {ex.GetType().Name}: {ex.Message}");
		}
	}

	// Lưới an toàn của phép chép bằng reflection: ai thêm một property kiểu phức tạp mới vào lớp settings nào thì
	// dòng này đổi nội dung ngay, thay vì mất dữ liệu trong im lặng. Bốn dictionary của Loot đã được nạp tay nên
	// xuất hiện ở đây là bình thường.
	private static void LogSkippedFields() {
		foreach (Type type in new[] {
			typeof(Auto.Attack.Settings), typeof(Auto.Loot.Settings), typeof(Auto.Support.Settings),
			typeof(BasicSettings), typeof(Auto.Market.Settings), typeof(Auto.Quest.Settings)
		}) {
			IReadOnlyList<string> skipped = SettingsCopier.DescribeSkipped(type);
			if (skipped.Count == 0) continue;
			DebugLog.AddProfileEvent($"PROFILE_SKIPPED_FIELDS | {type.FullName} | {string.Join(", ", skipped)}");
		}
	}

	private static JsonObject SerializeProfile(AccountProfile profile) =>
		JsonSerializer.SerializeToNode(profile, Options)!.AsObject();

	// Nền mặc định dựng từ chính constructor các lớp Settings. Bỏ hai trường chỉ có nghĩa ở mức từng nhân vật —
	// để lại trong khối dùng chung thì vừa thừa vừa dễ gây hiểu nhầm là có thể đặt mặc định cho chúng.
	private static JsonObject BuildConstructorDefaults() {
		JsonObject node = SerializeProfile(new AccountProfile());
		node.Remove("CharacterName");
		node.Remove("SavedAtUtc");
		return node;
	}

	// Giữ lại đúng những nhánh khác Defaults. Object thì đệ quy xuống, còn lại so nguyên khối.
	private static JsonObject Diff(JsonObject value, JsonObject baseline) {
		JsonObject result = [];
		foreach (KeyValuePair<string, JsonNode?> pair in value) {
			JsonNode? baselineChild = baseline[pair.Key];
			if (! WholeBlockKeys.Contains(pair.Key) && pair.Value is JsonObject child && baselineChild is JsonObject baselineObject) {
				JsonObject nested = Diff(child, baselineObject);
				if (nested.Count > 0) result[pair.Key] = nested;
				continue;
			}
			if (! JsonNode.DeepEquals(pair.Value, baselineChild)) result[pair.Key] = pair.Value?.DeepClone();
		}
		return result;
	}

	// Phép ngược của Diff: phủ phần riêng của account lên nền dùng chung.
	private static JsonObject Merge(JsonObject baseline, JsonObject overlay) {
		JsonObject result = baseline.DeepClone().AsObject();
		foreach (KeyValuePair<string, JsonNode?> pair in overlay) {
			if (! WholeBlockKeys.Contains(pair.Key) && pair.Value is JsonObject child && result[pair.Key] is JsonObject baselineChild) {
				result[pair.Key] = Merge(baselineChild, child);
				continue;
			}
			result[pair.Key] = pair.Value?.DeepClone();
		}
		return result;
	}

	private static void LoadLootDictionaries(JsonNode? lootNode, Auto.Loot.Settings target) {
		if (lootNode is not JsonObject loot) return;
		ReadDictionary(loot["ItemSelections"], target.ItemSelections);
		ReadDictionary(loot["ExcludedItemSelections"], target.ExcludedItemSelections);
		ReadDictionary(loot["SaleItemSelections"], target.SaleItemSelections);
		ReadDictionary(loot["PotionNameSelections"], target.PotionNameSelections);
	}

	private static void ReadDictionary(JsonNode? source, Dictionary<string, bool> target) {
		if (source is not JsonObject node) return;
		target.Clear();
		foreach (KeyValuePair<string, JsonNode?> pair in node) {
			if (pair.Value is JsonValue value && value.TryGetValue(out bool flag)) target[pair.Key] = flag;
		}
	}

	// Clear() là BẮT BUỘC: constructor Loot.Settings dựng sẵn ~20 khoá mặc định (Loot/Settings.cs:31-67), chỉ ghi
	// đè mà không xoá thì khoá người dùng đã xoá trên giao diện sẽ sống lại sau mỗi lần khôi phục.
	private static void CopyDictionary(Dictionary<string, bool> source, Dictionary<string, bool> target) {
		target.Clear();
		foreach (KeyValuePair<string, bool> pair in source) target[pair.Key] = pair.Value;
	}
}
