namespace Auto.Loot;

using System.Globalization;
using System.Text;

public enum AutoFsSpecialItemCategory { None, SkillBook, Artifact, Trigram, SixPaths, FourSymbols, ImmortalFormationLabel }

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

	public static AutoFsSpecialItemCategory Classify(string rawName) {
		string name = ToAsciiUpper(NormalizeGroundName(rawName));
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
