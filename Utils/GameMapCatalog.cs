namespace Auto.Utils;

// Bảng tra tên bản đồ -> số bản đồ, port nguyên từ AutoFS ResourceStream.DisposeNode(int)
// (D:\G\DEV\Resource\AutoSource\AutoProV2\ResourceStream.cs dòng 16-89).
//
// Cần bảng này vì popup nhiệm vụ Thám Quân chỉ ghi TÊN bản đồ, còn Data\Maps\*.map lại đặt tên file theo SỐ.
// Bằng chứng popup (quest.log 2026-09-09, PID 22056): "Lần này ta cần ngươi đi ⟦Trần Đường⟧ tìm Đại phu thu
// thập tin tức." — "Trần Đường" ứng với map 65 trong bảng dưới.
//
// Giữ nguyên chính tả của AutoFS, kể cả chỗ thiếu dấu ("Phong Than" ở 24), vì đây là bảng đối chiếu với chữ do
// chính client sinh ra; tự ý sửa dấu là hỏng phép so khớp.
public static class GameMapCatalog {
	private static readonly Dictionary<string, int> MapIdByName = new(StringComparer.OrdinalIgnoreCase) {
		["Phong Thần Đài"] = 1,
		["Sùng Thành Doanh"] = 2,
		["Ngọc Hư Cung"] = 3,
		["Xi Vưu Mộ"] = 4,
		["Sùng Thành"] = 5,
		["Bắc Hải"] = 6,
		["Yến Sơn"] = 7,
		["Chân Núi Côn Lôn"] = 8,
		["Tây Côn Lôn"] = 9,
		["Thủ Dương Sơn"] = 10,
		["Du Hồn"] = 11,
		["Miêu Cương"] = 12,
		["Cự Lộc"] = 13,
		["Đồng Quan"] = 14,
		["Mạnh Tân"] = 15,
		["Tam Sơn"] = 16,
		["Kỳ Sơn"] = 17,
		["Mục Dã"] = 18,
		["Tuyệt Long Lĩnh"] = 19,
		["Tây Kỳ"] = 20,
		["Triều Ca"] = 21,
		["Hoang Mạc"] = 22,
		["Thổ Thành"] = 23,
		["Phong Than"] = 24,
		["Lục Châu"] = 25,
		["Sa Mạc Chết"] = 26,
		["Hiên Viên T1"] = 27,
		["Hiên Viên T2"] = 28,
		["Hiên Viên T3"] = 29,
		["Hiên Viên T4"] = 30,
		["Hiên Viên T5"] = 31,
		["Ngọc Tuyền"] = 32,
		["Tuyết Cốc"] = 33,
		["Đại Phong"] = 34,
		["Đại Trạch"] = 35,
		["Băng Xuyên Chi Cực"] = 36,
		["Thủy Vực"] = 37,
		["Long Cung"] = 38,
		["Hải Câu"] = 39,
		["Long Vực"] = 40,
		["Long Uyên"] = 41,
		["Bích Dung Cung T1"] = 42,
		["Bích Dung Cung T2"] = 43,
		["Bích Dung Cung T3"] = 44,
		["Bích Dung Cung T4"] = 45,
		["Bích Dung Cung T5"] = 46,
		["Khổn Tiên Cung T1"] = 47,
		["Khổn Tiên Cung T2"] = 48,
		["Khổn Tiên Cung T3"] = 49,
		["Khổn Tiên Cung T4"] = 50,
		["Khổn Tiên Cung T5"] = 51,
		["Diêu Trì"] = 52,
		["Bồng Lai"] = 54,
		["Đông Doanh"] = 55,
		["Phương Trượng"] = 56,
		["Ngọc Hư 10 năm trước"] = 61,
		["Ngọc Hư 10 năm sau"] = 62,
		["Triều Ca 10 năm sau"] = 63,
		["Trần Đường"] = 65,
		["Trư lung trại"] = 66,
		["Vạn Tiên Trận Thổ"] = 67,
		["Vạn Tiên Trận Thủy"] = 68,
		["Vạn Tiên Trận Hỏa"] = 69,
		["Vạn Tiên Trận Phong"] = 70,
		["Tiên Giới"] = 73,
		["Nam Kha Quận"] = 99
	};

	// Tên client 2026-08 dùng trong popup nhiệm vụ, KHÁC tên trong bảng AutoFS ở trên.
	//
	// VERIFIED (quest.log 2026-09-10 00:52:58, PID 22056): popup ghi "đoạn tô màu trong popup | [Tầng 1 Hiên Viên
	// động | Lực Nguyên]" trong khi bảng trên ghi "Hiên Viên T1", nên tra không ra và Auto tưởng hết nhiệm vụ.
	//
	// CHƯA VERIFY: bốn dòng tầng 2-5 là suy ra theo mẫu của tầng 1, chưa thấy popup nào ghi chúng. Sai thì chỉ
	// tra không ra rồi báo lỗi to, KHÔNG dẫn tới đi nhầm map — mỗi chuỗi chỉ ánh xạ về đúng một số.
	//
	// Các mê cung khác (Bích Dung Cung, Khổn Tiên Cung, Vạn Tiên Trận...) chưa biết client gọi là gì; gặp lần đầu
	// sẽ báo "ĐÍCH KHÔNG ĐỌC ĐƯỢC" kèm nguyên chữ trong popup, cứ thế bổ sung vào đây.
	private static readonly Dictionary<string, int> ClientAliasMapIdByName = new(StringComparer.OrdinalIgnoreCase) {
		["Tầng 1 Hiên Viên động"] = 27,
		["Tầng 2 Hiên Viên động"] = 28,
		["Tầng 3 Hiên Viên động"] = 29,
		["Tầng 4 Hiên Viên động"] = 30,
		["Tầng 5 Hiên Viên động"] = 31
	};

	public static bool TryGetName(int mapId, out string name) {
		foreach ((string candidate, int id) in MapIdByName) {
			if (id != mapId) continue;
			name = candidate;
			return true;
		}
		name = "";
		return false;
	}

	public static bool TryResolve(string name, out int mapId) {
		mapId = 0;
		if (string.IsNullOrWhiteSpace(name)) return false;
		string trimmed = name.Trim();
		return MapIdByName.TryGetValue(trimmed, out mapId) || ClientAliasMapIdByName.TryGetValue(trimmed, out mapId);
	}
}
