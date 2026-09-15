namespace Auto.Utils;


// Danh sách điểm đến của Di ngoại phù, theo ĐÚNG thứ tự hiện trong menu game.
//
// Nguồn: chủ dự án chép tay từ client 2026-08 (23 dòng, cả bản thường 10 lần lẫn bản siêu cấp 50 lần dùng chung
// danh sách này). Đây là nguồn DUY NHẤT — không đọc được menu từ bộ nhớ:
//   - Bố cục menu NPC (+0x7EC, bước 0x69C) đọc ra rác: "Options=[#4=ặ.spr,#7=ề ^]" (quest.log 2026-09-09 22:18:59)
//   - Chuỗi con trỏ AutoFS [0x54,...] đứt ngay hop đầu: "Chuỗi con trỏ đứt tại +0x50" (quest.log 2026-09-10 00:38:31)
//   - Quét toàn bộ bộ nhớ (TalismanMenuProbe, 2026-09-10) tìm thấy tên bản đồ ở 116 chỗ nhưng không chỗ nào là
//     mảng menu; khối gọn nhất 0x30DD8xxx chứa ~20 map KHÔNG khớp danh sách 23 dòng này nên không phải menu.
//
// Vì bấm theo số thứ tự, THỨ TỰ DƯỚI ĐÂY LÀ HỢP ĐỒNG: sai một dòng là nhân vật dịch chuyển nhầm map và mất một
// lượt phù. Đừng sắp xếp lại, đừng chèn thêm, trừ khi chủ dự án chép lại menu và xác nhận.
//
// VERIFIED một phần: 5 chỉ số đã bấm thật và tới đúng map (chi tiết ở ghi chú ngay trên Count), gồm cả dòng đầu
// và dòng cuối. Các dòng giữa suy theo đó, chưa bấm thử từng cái.
public static class TalismanDestinationCatalog {
	// Chỉ số trong mảng = số truyền cho command 7. Giá trị = map id trong GameMapCatalog.
	private static readonly int[] MenuOrderMapIds = [
		1,  // Phong Thần đài
		52, // Diêu Trì
		20, // Tây Kỳ
		21, // Triều Ca
		2,  // Sùng Thành Doanh
		3,  // Ngọc Hư Cung
		4,  // Xi Vưu Mộ
		5,  // Sùng Thành
		6,  // Bắc Hải
		7,  // Yến Sơn
		8,  // Chân núi Côn Lôn
		9,  // Tây Côn Lôn
		10, // Thủ Dương Sơn
		11, // Du Hồn
		12, // Miêu Cương
		13, // Cự Lộc
		14, // Đồng quan
		15, // Mạnh Tân
		16, // Tam Sơn
		17, // Kỳ Sơn
		18, // Mục Dã
		19, // Tuyệt Long lĩnh
		65  // Trần Đường
	];

	// Chỉ số đã đo bằng TalismanTravelProbe ngày 2026-09-10, đều KHỚP bảng:
	//   số 0 -> Map1 Phong Thần Đài | số 1 -> Map52 Diêu Trì | số 7 -> Map5 Sùng Thành
	//   số 8 -> Map6 Bắc Hải        | số 22 -> Map65 Trần Đường (PID 22824, từ Map7 Yến Sơn)
	// Đo được cả dòng đầu lẫn dòng cuối nên thứ tự bảng coi như đúng; các dòng giữa chưa đo từng cái một.
	//
	// Trước đó số >= 10 đều "KHÔNG đổi map sau 15000ms". Nguyên nhân KHÔNG phải ở game mà ở native của chính dự
	// án: SystemUint.cpp TrySelectDialogOption chặn cứng optionIndex > 9 và trả false, trong khi phía C#
	// TrySendCommand chỉ kiểm PostMessageA gửi được nên vẫn báo thành công. Trần đã nâng thành
	// MaximumDialogOptionIndex = 63 và build lại native (NativeBuildStamp 20260909 -> 20260910).

	public static int Count => MenuOrderMapIds.Length;

	public static bool TryGetMapIdAt(int menuIndex, out int mapId) {
		mapId = 0;
		if (menuIndex < 0 || menuIndex >= MenuOrderMapIds.Length) return false;
		mapId = MenuOrderMapIds[menuIndex];
		return true;
	}

	public static bool TryGetMenuIndex(int mapId, out int menuIndex) {
		menuIndex = Array.IndexOf(MenuOrderMapIds, mapId);
		return menuIndex >= 0;
	}

	// Map đích không nằm trong menu (toàn bộ nhóm mê cung 22-51) thì chọn điểm đến nào đi bộ tới đích ngắn nhất.
	//
	// Số chặng tính bằng chính GameMapRoutePlanner mà bước đi bộ dùng, nên hai bên không thể lệch nhau. Số liệu
	// tính thử trên Data\Maps ngày 2026-09-10: map 22/32 từ Tây Kỳ chỉ 1 chặng thay vì 5 chặng nếu về Triều Ca;
	// map 37-41 từ Trần Đường 1-5 chặng thay vì 2-6; map 42-46 từ Tuyệt Long lĩnh 1-5 thay vì 4-8.
	//
	// Nhóm 47-51 (Khổn Tiên Cung) KHÔNG chọn được điểm đến nào: Data\Maps không có dòng chuyển map nào tới hoặc
	// đi khỏi 47-52, nên mọi tuyến đều rỗng. Hàm trả false, nơi gọi lui về đi bộ rồi sẽ báo "No route".
	public static bool TryGetNearestMenuIndex(int destinationMapId, out int menuIndex, out int hubMapId, out int transitionCount) {
		menuIndex = -1;
		hubMapId = 0;
		transitionCount = int.MaxValue;
		for (int index = 0; index < MenuOrderMapIds.Length; index++) {
			int candidate = MenuOrderMapIds[index];
			int count = GameMapRoutePlanner.FindRoute(candidate, destinationMapId).Count;
			if (count == 0 || count >= transitionCount) continue;
			menuIndex = index;
			hubMapId = candidate;
			transitionCount = count;
		}
		return menuIndex >= 0;
	}
}
