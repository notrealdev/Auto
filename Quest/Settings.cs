namespace Auto.Quest;

// Cấu hình hai nhiệm vụ hằng ngày port từ AutoFS: Thám Quân (Scout) và Bào Thương (Caravan).
//
// Tên khoá gốc và giá trị mặc định lấy từ file lưu thật của AutoFS
// D:\G\DEV\Resource\Auto\Data\UserAccount\*\NhiệmVụ.json, nhãn và danh sách ComboBox trích từ BAML
// view/pages/nhiệmvụ/hằngngày/thámquân.baml và bàothương.baml.
//
// KHÔNG port 3 toggle "Tự mua khi hết" của AutoFS (ThámQuân_Đi_MuaPhù, ThámQuân_Về_MuaHồiThànhPhù,
// ThámQuân_Về_MuaHồiThànhPhùSC): Auto chưa có luồng mua vật phẩm ở NPC, chỉ có luồng bán
// (Sale/InventorySaleEngine.cs). Chủ dự án chốt bỏ ở bản đầu 2026-09-09.
//
// Hiện chưa có engine nào đọc lớp này — lượt này chỉ dựng tab và settings.
//
// Thứ tự chạy khi bật cả hai (chủ dự án chốt 2026-09-09): làm lần lượt từ trên xuống theo đúng thứ tự hiển
// thị trong tab — Thám Quân trước, Bào Thương sau. Engine sau này phải theo đúng thứ tự này, không chạy song song.
public sealed class Settings {
	// ===== Thám Quân =====

	// Công tắc chính: tắt thì bỏ qua toàn bộ nhánh Thám Quân, mọi tuỳ chọn bên dưới không có tác dụng.
	public bool ScoutEnabled { get; set; }

	// ThámQuân_LoạiNV: 0 = Free, 1 = Max. Chủ dự án chốt mặc định Free (AutoFS lưu 1).
	public int ScoutQuestType { get; set; }

	// ThámQuân_LoạiTuLuyện: 0 = Thường, 1 = Nhân đôi. Chủ dự án chốt mặc định Thường (AutoFS lưu 1).
	public int ScoutCultivationType { get; set; }

	// ThámQuân_ThẻKimDật.
	public bool ScoutUseGoldenCard { get; set; }

	// ThámQuân_Đi_DiNgoạiPhù — nhãn AutoFS "Di ngoại phù & Phù đặc biệt".
	//
	// Mặc định TẮT (chủ dự án chốt 2026-09-11): đường đi giờ ưu tiên Điểm chuyển tiếp, không tốn phù.
	public bool ScoutOutboundTravelTalisman { get; set; }

	// ThámQuân_Về_KhứLaiPhù.
	public bool ScoutReturnRoundTripTalisman { get; set; }

	// ThámQuân_Về_KhứLaiPhù_Loại: 0 = Tự sát, 1 = Về Diêu Trì.
	public int ScoutReturnRoundTripTalismanType { get; set; }

	// Gộp hai khoá AutoFS ThámQuân_Về_HồiThànhPhù và ThámQuân_Về_HồiThànhPhùSC làm một (chủ dự án chốt
	// 2026-09-09): engine tìm trong ô trang bị nhanh, thấy bản nào thì dùng bản đó, không bắt chọn trước.
	public bool ScoutReturnTownTalisman { get; set; } = true;

	// ===== Bào Thương =====

	// Công tắc chính: tắt thì bỏ qua toàn bộ nhánh Bào Thương.
	public bool CaravanEnabled { get; set; }

	// BàoThương_MapNhậnNV: 0 = Triều Ca, 1 = Tây Kỳ.
	public int CaravanReceiveMap { get; set; }

	// BàoThương_LoạiLạcĐà: 0 = Thường, 1 = Siêu. Chủ dự án chốt mặc định Thường (AutoFS lưu 1).
	public int CaravanCamelType { get; set; }

	// BàoThương_LoạiNhiệmVụ: 0 = Free, 1 = Max. Chủ dự án chốt mặc định Free (AutoFS lưu 1).
	public int CaravanQuestType { get; set; }

	// BàoThương_LoạiTuLuyện: 0 = Thường, 1 = Nhân đôi. Chủ dự án chốt mặc định Thường (AutoFS lưu 1).
	public int CaravanCultivationType { get; set; }

	// BàoThương_ThẻKimDật.
	public bool CaravanUseGoldenCard { get; set; }

	// BàoThương_HồiThànhPhù — nhãn AutoFS "Sử dụng Bào thương hồi thành phù".
	// Chủ dự án chốt mặc định TẮT (AutoFS lưu true).
	public bool CaravanTownTalisman { get; set; }
}
