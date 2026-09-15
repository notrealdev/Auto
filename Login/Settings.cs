namespace Auto.Login;

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

// Cấu hình tab Đăng nhập, đọc từ một file JSON do chủ dự án tự tạo.
//
// Tên khoá giữ nguyên của AutoFS (file lưu thật: D:\G\DEV\Resource\Auto\Data\AutoLogin\AutoLogin_Settings.json) để
// sau này còn đối chiếu được, trừ danh sách account: AutoFS lưu account ở nơi khác và mã hoá bằng khoá "AutoPT.Net",
// còn ở đây chủ dự án chốt 2026-09-12 dùng CHỮ THƯỜNG không mã hoá nên không port thuật toán giải mã của AutoFS.
public sealed class LoginAccount {
	[JsonPropertyName("User")] public string User { get; set; } = "";
	[JsonPropertyName("Pass")] public string Pass { get; set; } = "";
	// Số thứ tự phân vùng và máy chủ, khớp List_Server.json của AutoFS. Gửi kèm lệnh 282.
	[JsonPropertyName("PhânVùng")] public int Partition { get; set; }
	[JsonPropertyName("MáyChủ")] public int Server { get; set; }
}

public sealed class Settings {
	// Đường dẫn đầy đủ tới Game.exe. AutoFS mở thẳng đường dẫn này (DiskConverter.cs:99, nhánh GameTab == 0 — đúng
	// mặc định trong file lưu thật). Nhánh copy Game.exe -> Game0001.exe chỉ chạy khi GameTab > 0, KHÔNG port.
	[JsonPropertyName("ExeLink")] public string ExeLink { get; set; } = "";
	// Hệ số giãn nhịp của AutoFS. Mọi quãng chờ đều theo đúng công thức của nó: 100 + Speed * 200, Speed * 50,
	// Speed * 100, Speed * 200. Mặc định 0 y như file lưu thật.
	[JsonPropertyName("Speed")] public int Speed { get; set; }
	// MILI GIÂY chờ thêm giữa hai account, cộng vào 500ms cố định (StoreOptions.cs:129). Mặc định 0 y như file lưu thật.
	[JsonPropertyName("DelayLogin")] public int DelayLogin { get; set; }
	// KHÔNG CÓ Ở AutoFS. AutoFS chờ client sẵn sàng bằng cách đọc bộ nhớ (DiskConverter.cs:869, chuỗi con trỏ
	// 0x754A9C -> +84 -> deref != 0), địa chỉ đó lấy từ bản client cũ nên chưa dùng lại được ở đây. Trong lúc chưa
	// dò ra địa chỉ thật, đây là quãng chờ tay tính từ lúc thấy cửa sổ chính cho tới khi gửi lệnh 280.
	[JsonPropertyName("WaitBeforeLogin")] public int WaitBeforeLogin { get; set; } = 2000;
	// Mỗi bước (đóng hộp thoại, chọn máy chủ) được gửi lại 0,5 giây một lần trong bấy nhiêu mili giây trước khi bỏ cuộc.
	[JsonPropertyName("StepTimeoutMilliseconds")] public int StepTimeoutMilliseconds { get; set; } = 60000;
	[JsonPropertyName("Accounts")] public List<LoginAccount> Accounts { get; set; } = [];

	// Cùng quy ước đường dẫn với Runtime\DebugLog.cs: mọi thứ nằm cạnh file exe.
	public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "Login", "Login.json");

	public static bool TryLoad(string path, out Settings settings, out string failure) {
		settings = new Settings();
		failure = "";
		try {
			if (! File.Exists(path)) {
				failure = $"Không thấy file cấu hình: {path}";
				return false;
			}
			Settings? loaded = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), new JsonSerializerOptions {
				ReadCommentHandling = JsonCommentHandling.Skip,
				AllowTrailingCommas = true
			});
			if (loaded == null) {
				failure = "File cấu hình rỗng hoặc không phải JSON hợp lệ.";
				return false;
			}
			settings = loaded;
			if (settings.Accounts.Count == 0) {
				failure = "Danh sách Accounts rỗng.";
				return false;
			}
			if (! File.Exists(settings.ExeLink)) {
				failure = $"ExeLink không tồn tại: {settings.ExeLink}";
				return false;
			}
			return true;
		} catch (Exception ex) {
			failure = $"{ex.GetType().Name}: {ex.Message}";
			return false;
		}
	}
}
