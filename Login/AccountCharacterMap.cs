namespace Auto.Login;

using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Auto.Runtime;

public sealed class AccountCharacterLink {
	[JsonPropertyName("User")] public string User { get; set; } = "";
	[JsonPropertyName("CharacterName")] public string CharacterName { get; set; } = "";
	[JsonPropertyName("LearnedAtUtc")] public string LearnedAtUtc { get; set; } = "";
	// Chỉ để người đọc tự đánh dấu dòng nào mình sửa tay. Code KHÔNG xử lý khác đi theo cờ này — không bịa thêm
	// logic chưa ai cần.
	[JsonPropertyName("Manual")] public bool Manual { get; set; }
}

public sealed class AccountCharacterFile {
	[JsonPropertyName("Links")] public List<AccountCharacterLink> Links { get; set; } = [];
}

// Bản đồ tài khoản đăng nhập ↔ tên nhân vật.
//
// Vì sao phải dựng mới: trước lượt này KHÔNG có bất cứ thứ gì trong app nối một client đang chạy với một dòng
// trong Login.json. Tên nhân vật đọc được từ bộ nhớ, tài khoản thì chỉ tab Login biết, và hai thứ đó chưa bao giờ
// gặp nhau ở chỗ nào được giữ lại.
//
// Chỗ DUY NHẤT biết cùng lúc cả hai là LoginAutomation.TryEnterWorld, ngay sau khi đọc được tên nhân vật của
// đúng tiến trình nó vừa đăng nhập — xem Learn được gọi ở đó.
//
// Đặt cạnh Profiles/ chứ không trong Login/: thư mục Login/ chứa mật khẩu thật, không thêm file nào vào đó.
internal static class AccountCharacterMap {
	private static readonly string MapDirectory = Path.Combine(AppContext.BaseDirectory, AppVersion.ProfilesDirectoryName);
	private static readonly string MapPath = Path.Combine(MapDirectory, "AccountCharacters.json");

	private static readonly JsonSerializerOptions Options = new() {
		WriteIndented = true,
		IndentCharacter = '\t',
		IndentSize = 1,
		// Tên nhân vật có dấu tiếng Việt. Bộ mã hoá mặc định escape hết thành \uXXXX, file mở ra không đọc được
		// bằng mắt (đo bằng scratchpad 2026-09-21). Create(UnicodeRanges.All) giữ chữ Việt nguyên vẹn mà vẫn
		// escape các ký tự nhạy cảm HTML.
		Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
	};

	private static readonly object SyncRoot = new();
	private static List<AccountCharacterLink> links = [];
	private static DateTime lastWriteUtc = DateTime.MinValue;

	public static bool TryResolveUser(string characterName, out string user) {
		user = "";
		if (characterName.Length == 0) return false;
		EnsureLoaded();
		lock (SyncRoot) {
			AccountCharacterLink? match = links.FirstOrDefault(link => string.Equals(link.CharacterName, characterName, StringComparison.Ordinal));
			if (match == null || match.User.Length == 0) return false;
			user = match.User;
			return true;
		}
	}

	public static bool TryResolveCharacter(string user, out string characterName) {
		characterName = "";
		if (user.Length == 0) return false;
		EnsureLoaded();
		lock (SyncRoot) {
			AccountCharacterLink? match = links.FirstOrDefault(link => string.Equals(link.User, user, StringComparison.Ordinal));
			if (match == null || match.CharacterName.Length == 0) return false;
			characterName = match.CharacterName;
			return true;
		}
	}

	// Ghi nhớ cặp vừa học được từ một lượt đăng nhập thành công.
	//
	// KHÔNG bao giờ xoá dòng nào: bản sửa tay của chủ dự án phải sống sót. Dòng đã có mà tên nhân vật trùng thì
	// không ghi lại file, để việc đăng nhập hằng ngày không đụng đĩa vô ích.
	public static void Learn(string user, string characterName, Action<string>? log) {
		if (user.Length == 0 || characterName.Length == 0) return;
		EnsureLoaded();
		try {
			lock (SyncRoot) {
				AccountCharacterLink? existing = links.FirstOrDefault(link => string.Equals(link.User, user, StringComparison.Ordinal));
				if (existing != null && string.Equals(existing.CharacterName, characterName, StringComparison.Ordinal)) return;
				if (existing == null) {
					links.Add(new AccountCharacterLink {
						User = user,
						CharacterName = characterName,
						LearnedAtUtc = DateTime.UtcNow.ToString("O")
					});
				} else {
					log?.Invoke($"BẢN ĐỒ ACCOUNT | {user} đổi nhân vật '{existing.CharacterName}' -> '{characterName}'.");
					existing.CharacterName = characterName;
					existing.LearnedAtUtc = DateTime.UtcNow.ToString("O");
					existing.Manual = false;
				}
				Save();
			}
			log?.Invoke($"BẢN ĐỒ ACCOUNT | Đã ghi nhớ {user} ↔ {characterName} vào {MapPath}.");
		} catch (Exception ex) {
			log?.Invoke($"BẢN ĐỒ ACCOUNT HỎNG | Không ghi được {MapPath} | {ex.GetType().Name}: {ex.Message}");
		}
	}

	// Nạp lại khi file đổi trên đĩa, để chủ dự án sửa bằng Notepad là có hiệu lực ngay, không phải khởi động lại Auto.
	private static void EnsureLoaded() {
		try {
			lock (SyncRoot) {
				if (! File.Exists(MapPath)) {
					links = [];
					lastWriteUtc = DateTime.MinValue;
					return;
				}
				DateTime writeUtc = File.GetLastWriteTimeUtc(MapPath);
				if (writeUtc == lastWriteUtc) return;
				AccountCharacterFile? file = JsonSerializer.Deserialize<AccountCharacterFile>(File.ReadAllText(MapPath), Options);
				links = file?.Links ?? [];
				lastWriteUtc = writeUtc;
			}
		} catch (Exception ex) {
			DebugLog.AddLoginEvent($"BẢN ĐỒ ACCOUNT HỎNG | Không đọc được {MapPath} | {ex.GetType().Name}: {ex.Message}");
		}
	}

	// Người gọi phải đang giữ SyncRoot.
	private static void Save() {
		Directory.CreateDirectory(MapDirectory);
		AccountCharacterFile file = new() { Links = links };
		string temporaryPath = MapPath + ".tmp";
		File.WriteAllText(temporaryPath, JsonSerializer.Serialize(file, Options));
		File.Move(temporaryPath, MapPath, true);
		lastWriteUtc = File.GetLastWriteTimeUtc(MapPath);
	}
}
