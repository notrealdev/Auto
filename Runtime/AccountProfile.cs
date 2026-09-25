namespace Auto.Runtime;

// Hồ sơ cấu hình của MỘT nhân vật, lưu ra Profiles/Profiles.json.
//
// KHOÁ LÀ CharacterName (so Ordinal). Đó là thứ duy nhất đọc được từ client mà ổn định qua việc đóng/mở lại
// client: ProcessId và HWND đổi mỗi lần mở, còn tiêu đề cửa sổ thì cả 6 client giống hệt nhau
// ("thaptuyettran.vn - version 1.30 FPS:.. PING:..").
//
// LoginUser chỉ để đọc bằng mắt và đối chiếu với Profiles/AccountCharacters.json — KHÔNG dùng làm khoá.
public sealed class AccountProfile {
	public string CharacterName { get; set; } = "";

	public string SavedAtUtc { get; set; } = "";

	// Auto tổng (GameWindow.Enabled). Chỉ được bật lại khi client do chính Auto đăng nhập lại — xem
	// AccountProfileStore.Apply(autoEnableMaster).
	public bool MasterEnabled { get; set; }

	public Auto.Attack.Settings Attack { get; set; } = new();

	public Auto.Loot.Settings Loot { get; set; } = new();

	public Auto.Support.Settings Support { get; set; } = new();

	public BasicSettings Basic { get; set; } = new();

	public Auto.Market.Settings Market { get; set; } = new();

	public Auto.Quest.Settings Quest { get; set; } = new();

	// Điểm train ĐANG CHỌN của nhân vật này.
	//
	// Bản thân DANH SÁCH điểm nằm ở file dùng chung Profiles/TrainingPoints.json (xem TrainingPointStore) — bãi là
	// đặc điểm của thế giới game, không phải của nhân vật. Ở đây chỉ giữ "nhân vật này đang dùng bãi nào".
	// Toạ độ của bãi đó chính là Attack.CenterMapId/CenterX/CenterY, không lưu lặp thêm một lần nữa.
	public int SavedTrainingMapId { get; set; }

	// Tên của bãi đang chọn, CHỈ để đọc bằng mắt khi mở file.
	//
	// Vì sao cần: bãi thật chỉ được lưu bằng SỐ (SavedTrainingMapId + Attack.CenterMapId), trong khi trường duy
	// nhất trong file có TÊN map lại là Attack.TrainingMap của chế độ Mê cung — chế độ đang tắt. Mở file ra thì
	// thấy tên của thứ không dùng và số của thứ đang dùng, nên rất dễ tưởng Auto lưu sai bãi (chủ dự án báo đúng
	// hiện tượng này 2026-09-22).
	//
	// Không có setter: System.Text.Json ghi nó ra file nhưng lúc đọc lại thì bỏ qua, nên không có đường nào để
	// giá trị này lệch khỏi SavedTrainingMapId. Sửa tay trong file cũng vô hại — lần lưu sau nó tự về đúng.
	public string SavedTrainingMapName =>
		Auto.Utils.GameMapCatalog.TryGetName(SavedTrainingMapId, out string name) ? name : "";
}
