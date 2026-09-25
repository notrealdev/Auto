namespace Auto.Runtime;

// Một điểm train do người dùng tự lưu qua nút bánh răng ở dòng "Quanh điểm" (tab Đánh).
//
// Toạ độ raw + map chép đúng bản tham chiếu AutoFS (Resource/AutoSource/AutoProV2/StreamTree.cs). KHÔNG có phạm vi
// riêng cho từng điểm: phạm vi là ô Range dùng chung trên dòng "Quanh điểm", đúng như AutoFS để nó ở TầmĐánh cấp
// trên chứ không gắn vào từng điểm (chủ dự án chốt 2026-09-21). Hai trường quái bên dưới là phần THÊM so với AutoFS.
//
// Hiển thị chia 256 (trục X) và 512 (trục Y) — cùng thang với AttackViewModel.RawXScale/RawYScale và với
// StreamTree.XYDisplay của AutoFS.
public readonly record struct TrainingPoint(int MapId, int RawX, int RawY) {
	// Quái của bãi này. LỆCH CÓ CHỦ Ý so với AutoFS (bản đó chỉ lưu toạ độ + map): chủ dự án chốt 2026-09-22 rằng
	// chọn một điểm đã lưu phải áp dụng CẢ quái vào giao diện, vì mỗi bãi gắn với một loại quái cụ thể — lưu toạ độ
	// mà vẫn phải chọn lại quái bằng tay thì điểm đã lưu chỉ dùng được một nửa.
	//
	// Signature là khoá khôi phục thật (AttackViewModel.RestoreSelection và RequestMonsterOptions đều so theo
	// Signature, không so theo tên); Name chỉ để hiển thị và để dựng lại mục khi quái tạm ra khỏi tầm quét.
	//
	// Cả hai để rỗng nghĩa là điểm cũ lưu trước lượt này — lúc áp dụng thì giữ nguyên quái đang chọn, không ghi đè.
	// Nhờ vậy TrainingPoints.json cũ đọc lại vẫn đúng, không cần chuyển đổi.
	// Có backing field và tự chuẩn hoá null -> "" thay vì dùng property tự động với initializer `= ""`.
	//
	// Vì sao: initializer của struct KHÔNG chạy khi System.Text.Json dựng lại đối tượng từ file thiếu hai khoá này.
	// Đo trên chính TrainingPoints.json của chủ dự án (scratchpad 2026-09-22): file cũ không có hai khoá thì đọc ra
	// null, rồi lần lưu sau ghi thẳng `"MonsterName": null` vào file. Ngoài việc rác trong file, ApplyTrainingPoint
	// còn dùng MonsterName để dựng MonsterOption nên có đường phát sinh NullReferenceException.
	// Khai báo nullable đúng bản chất: trình biên dịch nói thẳng rằng hai field này CÓ THỂ null (CS8618), và đó
	// chính là ca đã đo được ở trên. Hai property bên dưới là nơi duy nhất chuẩn hoá, nên ngoài lớp này không ai
	// phải nhìn thấy null.
	private readonly string? monsterName;
	private readonly string? monsterSignature;

	public string MonsterName {
		get => monsterName ?? "";
		init => monsterName = value ?? "";
	}

	public string MonsterSignature {
		get => monsterSignature ?? "";
		init => monsterSignature = value ?? "";
	}

	[System.Text.Json.Serialization.JsonIgnore]
	public bool HasMonster => MonsterSignature.Length > 0;

	// Tên map, CHỈ để đọc bằng mắt khi mở TrainingPoints.json — file đó trước chỉ có "MapId": 32 nên không tra bảng
	// thì không biết là bãi nào. Cùng lý do với AccountProfile.SavedTrainingMapName.
	//
	// KHÔNG JsonIgnore (khác IsValid bên dưới): đây là thông tin người đọc cần, không phải cờ nội bộ. Không có
	// setter nên lúc đọc lại bị bỏ qua, không có đường nào lệch khỏi MapId.
	public string MapName => Auto.Utils.GameMapCatalog.TryGetName(MapId, out string name) ? name : "";

	// JsonIgnore: đây là thuộc tính TÍNH TOÁN, không phải dữ liệu. Không đánh dấu thì nó bị ghi ra Profiles.json
	// thành một dòng "IsValid": true thừa ở mỗi điểm (đo bằng scratchpad 2026-09-22) — rác trong file mà lúc đọc
	// lại còn bị bỏ qua vì không có setter.
	[System.Text.Json.Serialization.JsonIgnore]
	public bool IsValid => MapId > 0 && RawX > 0 && RawY > 0;
}
