namespace Auto.Loot;

using System.Globalization;
using System.Text;

public enum AutoFsSpecialItemCategory { None, SkillBook, Artifact, Trigram, SixPaths, FourSymbols, ImmortalFormationLabel, Herb, GreenWeapon }

public static class AutoFsSpecialItemClassifier {
	private static readonly HashSet<string> SkillBookNames = new(StringComparer.OrdinalIgnoreCase) {
		"Chưởng Tâm Lôi", "Lưu Tinh Thạch", "Băng Tuyết đạn", "Tích Lịch Hỏa", "Tinh Thông Lôi Hệ", "Thiên Phong Địa Nhận", "Băng Cơ Tuyết Cốt", "Phong lâm hỏa sơn", "Hạn Địa Lôi", "Tinh Thông Thổ Hệ",
		"Thiết Mã Băng Qua", "Tinh Thông Hỏa Hệ", "Phong Vân Lôi Động", "Ngũ Nhạc Triều Tông", "Tinh Thông Băng Hệ", "Thập Phương Liệt Hỏa", "Lôi Phong Giáp", "Thiên Băng địa liệt", "Băng Phong Bạo", "Chúc Dung Chân Khí",
		"Lôi Động Cửu thiên", "Huyền Nữ Bổ Thiên", "Băng Phong Vạn Lý", "Tam Muội Chân Hỏa", "Tế Huyết trảm", "Lăng Ba Vi Bộ", "Khai sơn trảm", "Hồi Phong Trảm", "Điện Quang Trảm", "Hoành Không Trảm",
		"Tinh Thông Đoản Đao", "Tinh Thông Trường Đao", "Tam Đầu Lục Thủ", "Huyền Băng trảm", "Hỏa Quang Trảm", "Liên Hoàn Trảm", "Lạc Địa Trảm", "Thuần Dương Hộ Thể", "Thiên Quân Trảm", "Khuynh Thành Nhất Kích",
		"Kim Cang chú", "Thôi Thân chú", "Bổ Tâm chú", "Cường Công chú", "Phá Giáp chú", "Bồ Đề chú", "Trảm Tâm chú", "Tật Phong chú", "Vạn Cốt Toàn Khô", "Bàn Cổ khai thiên",
		"Ban Môn Lộng Phủ", "Tam Vị Chân Hỏa", "Tấn công vật lý", "Bạch Liên Thiên Hoả", "Phệ ảnh Lôi Quang", "An Hồn Tịnh Thổ", "Tuyết Vũ Băng Phong", "Tịch Diệt Chân Hỏa", "Băng Tinh Trùng Sinh", "Phần Hỏa Đồ Đằng",
		"Lực Sĩ tế", "Trường Cung tế", "Thiên Vũ tế", "Liên Nỗ tế", "Hỏa Lôi tế", "Toái Cốt tế", "Lưu Tinh tế", "Truy Hồn tế", "Phong Quyển Tàn Vân"
	};

	private static readonly HashSet<string> ArtifactNames = new(StringComparer.OrdinalIgnoreCase) {
		"Hỏa Long Tiêu", "Ngũ Quang thạch", "Hình Thiên ấn", "Bình Lưu Ly", "Hỗn Thiên Lăng", "Càn Khôn Xích",
		"Túi Ngô Phong", "Kim Cang Phách", "Âm Dương Kính", "Bích Tỳ Bà",
		"Thái Dương Châm", "Bàn Cổ phướn", "Dây Phược Long", "ấm Vạn Nha",
		"Dây Khổn Tiên", "Linh Lung Tháp", "Kim Bát Vu", "Chấn Thiên Cung", "Chân Kính", "Ngọc Hư Phù",
		"Toàn Tâm Đinh", "Phong Hỏa Luân", "Hạnh Hoàng Kỳ", "Lạc Hồn Chung", "Dung Tinh Lộ", "Hồng Hồ Lô",
		"Cọc Độn Long", "Kính Chiếu Yêu", "Kim Quang Tỏa", "Càn Khôn Khuyên", "Định Phong Châu", "Thanh Vân Kiếm",
		"Ngọc Như Ý", "Hỏa Tỳ Bà", "An Mệnh Phù", "Hỗn Nguyên Châu"
	};

	private static readonly HashSet<string> SixPathNames = new(StringComparer.OrdinalIgnoreCase) {
		"Đoản Kiếm", "Đoạn Kiếm", "Mảnh Giáp", "Toái Giáp", "Băng Cơ", "Ngọc Cốt", "Mặt Quỷ", "Quỷ Diện", "Hỏa Vũ"
	};

	// Thảo Dược. Nhận diện BẰNG TÊN chứ không bằng trường phân loại trong record.
	//
	// Vì sao không dùng ItemGroup.Herbal đã có sẵn: nhánh đó không bao giờ chạy được. Nó so GroundKind với 10, mà
	// GroundKind đọc ở offset GroundRecordKind (0x1C) — ĐÚNG offset mà AutoFsGroundItemScanner dùng làm điều kiện
	// "đang nằm trên đất" (groundState != 3 thì bỏ qua), nên mọi item lọt qua vòng quét đều có GroundKind = 3.
	// Lỗi này đã ghi trong Resource/CLIENT-UPDATE-RECOVERY.md dòng 175 (2026-08-28) và xác nhận lại bằng bản đổ
	// record ngày 2026-09-11: cả ba ô 3/5/9 đều ra +0x01C=0x00000003.
	//
	// Cũng KHÔNG dùng được QualityCodeB: bản đổ record 2026-09-11 PID=32196 cho thấy 'Liên Kiều' và 'Mặc Long Quy'
	// đều có +0x0B0 = 0x01000030, tức AttributeClass = 3 — trùng đúng nhóm 'Mảnh, Ngọc'. Đó chính là lý do thảo dược
	// vẫn bị nhặt suốt: 'Mảnh, Ngọc' mặc định BẬT nên nhánh AttributeClass == 3 trong Finder.ShouldPick cho qua.
	// Dò cả 233 DWORD của hai record cũng không thấy trường nào tách được thảo dược khỏi mảnh/ngọc.
	//
	// So khớp ĐÚNG TUYỆT ĐỐI sau chuẩn hoá, cùng lý do với Lục Đạo ở dưới: bao hàm hai chiều có thể nuốt nhầm món
	// khác có tên dài hơn chứa đúng chuỗi này. Ở đây bắt buộc phải tuyệt đối: bảng vật phẩm còn có
	// 'Tinh Hoa Mặc Long Quy' (mã 965, "Sản vật gia công cấp 8") KHÔNG phải thảo dược nhưng chứa nguyên tên món 919.
	//
	// Nguồn: bảng vật phẩm trích từ settings.pak của client, D:\G\Tools\FSData\Data\settings.pak.txt, cột "Mã,Tên".
	// Lọc mọi dòng có mô tả "(Thảo Dược cấp N)" ra đúng 16 món, mã liên tiếp 910..925 — trọn một khối, không sót.
	// Ghi kèm mã và cấp để lần sau đối chiếu lại được với file gốc.
	private static readonly HashSet<string> HerbNames = new(StringComparer.OrdinalIgnoreCase) {
		"Bạch Trà",            // 910, cấp 1
		"Địa Hoàng",           // 911, cấp 1
		"Huyên thảo",          // 912, cấp 1
		"Cát Căn",             // 913, cấp 2
		"Liên Kiều",           // 914, cấp 3
		"Lạc Thạch Đằng",      // 915, cấp 4
		"Tiên Hạc Thảo",       // 916, cấp 5
		"Bạch Phụ Tử",         // 917, cấp 6
		"Hà Thủ Ô",            // 918, cấp 7
		"Mặc Long Quy",        // 919, cấp 8
		"Mặc Long Đảm",        // 920, cấp 8
		"Phục Thần Tử",        // 921, cấp 9
		"Trường Bạch Sâm",     // 922, cấp 9
		"Tiên Vân Lão Sâm",    // 923, cấp 10
		"Tuyết Chi Phục Linh", // 924, cấp 10
		"Long Huyết Linh Chi"  // 925, cấp 10
	};

	// Nhóm "Vũ khí xanh". Chủ dự án cung cấp 2026-09-12: rìu (phủ) từ level 40 tới 100.
	//
	// Nhóm này KHÁC mọi nhóm còn lại ở chỗ nó CHỈ THÊM, không bao giờ bớt: nó nhận thêm bản XANH LỤC
	// (QualityCodeA & 0xFF == 2) của các tên dưới đây, còn màu khác vẫn theo nguyên ô tick màu. Các ô Đồ Lục/Vàng/Cam
	// giữ quyền ưu tiên số 1 (chủ dự án chốt 2026-09-12). Điều kiện màu nằm ở Finder.ShouldPick chứ không ở đây,
	// vì lớp này chỉ nhìn thấy tên.
	//
	// So khớp ĐÚNG TUYỆT ĐỐI, cùng lý do với Thảo Dược và Lục Đạo. Đã rà 7 tên này với toàn bộ luật phía dưới
	// (2026-09-12): không tên nào bị nhóm khác nuốt, không tên nào là chuỗi con của tên khác. Chỗ suýt trúng duy nhất
	// là Pháp Bảo 'Lạc Hồn Chung' so với 'Lạc Hồn phủ' — phép so của Pháp Bảo là
	// "LAC HON CHUNG".Contains("LAC HON PHU") nên không khớp.
	private static readonly HashSet<string> GreenWeaponNames = new(StringComparer.OrdinalIgnoreCase) {
		"Phục Thế phủ",
		"Lạc Hồn phủ",
		"Thất bảo phủ",
		"Tụ Tiên phủ",
		"Tuyệt Tiên phủ",
		"Hỗn Thiên phủ",
		"Diệt Thần phủ"
	};

	public static AutoFsSpecialItemCategory Classify(string rawName) {
		string name = ToAsciiUpper(NormalizeGroundName(rawName));
		if (GreenWeaponNames.Any(knownName => string.Equals(name, ToAsciiUpper(knownName), StringComparison.Ordinal))) return AutoFsSpecialItemCategory.GreenWeapon;
		if (HerbNames.Any(knownName => string.Equals(name, ToAsciiUpper(knownName), StringComparison.Ordinal))) return AutoFsSpecialItemCategory.Herb;
		// Kiểm tra Lục Đạo trước Bí Kíp/Bảo Vật và so khớp đúng tuyệt đối (không bao hàm):
		// tên món Lục Đạo luôn là tên đầy đủ của chính món đó (vd. "Băng Cơ"), trong khi Bí Kíp lại có tên dài chứa
		// đúng từ đó (vd. "Băng Cơ Tuyết Cốt"). Bao hàm 2 chiều đều có thể nuốt nhầm; so khớp đúng tuyệt đối mới tránh cả 2 chiều.
		if (SixPathNames.Any(knownName => string.Equals(name, ToAsciiUpper(knownName), StringComparison.Ordinal))) return AutoFsSpecialItemCategory.SixPaths;
		if (SkillBookNames.Any(knownName => ToAsciiUpper(knownName).Contains(name, StringComparison.Ordinal))) return AutoFsSpecialItemCategory.SkillBook;
		if (ArtifactNames.Any(knownName => ToAsciiUpper(knownName).Contains(name, StringComparison.Ordinal))) return AutoFsSpecialItemCategory.Artifact;
		if (name.Contains("QUE", StringComparison.Ordinal)) return AutoFsSpecialItemCategory.Trigram;
		if (name.Contains("PHONG LE", StringComparison.Ordinal) || name.Contains("HOA LINH", StringComparison.Ordinal) || name.Contains("DIA TAM", StringComparison.Ordinal) || name.Contains("THUY HON", StringComparison.Ordinal)) return AutoFsSpecialItemCategory.FourSymbols;
		if (name.Contains("NHAN", StringComparison.Ordinal)) return AutoFsSpecialItemCategory.ImmortalFormationLabel;
		return AutoFsSpecialItemCategory.None;
	}

	public static string NormalizeGroundName(string name) {
		string normalized = name.Trim();
		int suffixStart = normalized.Length;
		while (suffixStart > 0 && char.IsDigit(normalized[suffixStart - 1])) suffixStart--;
		if (suffixStart > 0 && suffixStart < normalized.Length && (normalized[suffixStart - 1] == 'x' || normalized[suffixStart - 1] == 'X')) normalized = normalized[..(suffixStart - 1)].TrimEnd();
		return normalized;
	}

	// Đọc số lượng của một chồng item nằm dưới đất từ hậu tố "xN" trong tên hiển thị.
	// Đối xứng với NormalizeGroundName: cùng cách quét ngược chữ số rồi 'x'/'X', nhưng trả về N thay vì cắt bỏ.
	// Không có hậu tố thì coi như 1.
	public static int GetGroundStackCount(string rawName) {
		string normalized = rawName.Trim();
		int suffixStart = normalized.Length;
		while (suffixStart > 0 && char.IsDigit(normalized[suffixStart - 1])) suffixStart--;
		if (suffixStart == 0 || suffixStart >= normalized.Length) return 1;
		if (normalized[suffixStart - 1] != 'x' && normalized[suffixStart - 1] != 'X') return 1;
		return int.TryParse(normalized[suffixStart..], NumberStyles.None, CultureInfo.InvariantCulture, out int count) && count > 0 ? count : 1;
	}

	// Bỏ dấu tiếng Việt và chuyển hoa để so khớp bất kể cách gõ dấu khác nhau (VD: "Hỏa"/"Hoả" đều thành "HOA").
	public static string ToAsciiUpper(string value) {
		string decomposed = value.Replace('Đ', 'D').Replace('đ', 'd').Normalize(NormalizationForm.FormD);
		StringBuilder builder = new(decomposed.Length);
		foreach (char c in decomposed) {
			if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
			builder.Append(c);
		}
		return builder.ToString().ToUpperInvariant();
	}
}
