namespace Auto.Utils;

// Bảng điểm đến của popup ĐIỂM CHUYỂN TIẾP (popup tự hiện khi nhân vật đứng lên điểm, không click NPC nào).
//
// Chọn một mục = gửi lệnh 7 kèm CHỈ SỐ MỤC, cùng lệnh mà ScoutQuestAutomation.DialogOptionCommand dùng cho menu NPC.
// KHÔNG dùng lệnh 96 của AutoFS: đo trên PID=32196 ngày 2026-09-11, lệnh 96 với số 0 và 1 đều không đổi map và
// ModalState giữ nguyên 0x2D9A5688, tức client bỏ qua hoàn toàn.
//
// CHỈ SỐ PHỤ THUỘC MAP ĐANG ĐỨNG, không cố định. Chủ dự án chốt: "ở map nào thì map đó sẽ không hiện ra" — map hiện
// tại bị ẩn khỏi danh sách nên mọi mục đứng sau nó tụt một nấc. Vì thế phải lưu THỨ TỰ danh sách rồi đếm lại, không
// được lưu cứng bảng số -> map.
//
// Thứ tự dưới đây là danh sách chủ dự án cung cấp. Đo thật từ Triều Ca (map 21, Triều Ca bị ẩn nên còn 8 mục):
//   0 -> map 2, 1 -> map 3, 2 -> map 4, 3 -> map 20, 4 -> map 1, 5 -> map 52   (chủ dự án xác nhận đã test hết)
// Số 10 gửi đi bị client bỏ qua, khớp với danh sách chỉ có 8 mục (chỉ số cao nhất là 7).
public static class TransitGateDestinationCatalog {
	// "Viễn Cổ" chưa có trong GameMapCatalog nên chưa biết số map; để 0 để không bao giờ khớp làm đích, nhưng VẪN
	// phải nằm đúng chỗ trong danh sách vì nó chiếm một chỉ số.
	private const int UnknownMapId = 0;

	// Hai map cuối là map nhiệm vụ, chủ dự án chốt bỏ qua khi chọn đích. Giữ lại trong danh sách để chỉ số không lệch.
	private static readonly (string Name, int MapId)[] Destinations = [
		("Sùng Thành Doanh", 2),
		("Ngọc Hư Cung", 3),
		("Xi Vưu Mộ", 4),
		("Tây Kỳ", 20),
		("Triều Ca", 21),
		("Phong Thần Đài", 1),
		("Diêu Trì", 52),
		("Viễn Cổ", UnknownMapId),
		("Trư lung trại", 66)
	];

	// Chỉ số cần gửi kèm lệnh 7 để đi từ currentMapId tới destinationMapId.
	public static bool TryGetOptionIndex(int currentMapId, int destinationMapId, out int optionIndex, out string reason) {
		optionIndex = -1;
		if (destinationMapId <= 0) {
			reason = $"Map đích không hợp lệ ({destinationMapId}).";
			return false;
		}
		if (destinationMapId == currentMapId) {
			reason = $"Đã ở map {currentMapId}, không cần dịch chuyển.";
			return false;
		}
		int index = 0;
		foreach ((string name, int mapId) in Destinations) {
			if (mapId == currentMapId) continue;
			if (mapId == destinationMapId) {
				optionIndex = index;
				reason = $"'{name}' đứng thứ {index} khi đang ở map {currentMapId}.";
				return true;
			}
			index++;
		}
		reason = $"Map {destinationMapId} không nằm trong danh sách Điểm chuyển tiếp.";
		return false;
	}

	public static bool Contains(int mapId) => mapId > 0 && Destinations.Any(destination => destination.MapId == mapId);

	// Toạ độ Điểm chuyển tiếp trên từng map. Popup tự hiện khi nhân vật đứng lên ô này, không click gì cả.
	//
	// MỚI ĐO ĐƯỢC MAP 21. Toạ độ lấy từ các lượt chạy của chủ dự án ngày 2026-09-11 trên PID=32196: mọi lần popup
	// đang mở (ModalState=0x2D9A5688) nhân vật đều đứng trong khoảng 57194..57249 / 98149..98247, lấy điểm giữa.
	// Đã chạy thật qua TransitGateTravelProbe: đi từ 54951/96978 tới nơi, popup hiện ở 57112/98186.
	//
	// Các map khác CHƯA ĐO. Thiếu toạ độ thì nơi gọi phải lui về đường cũ, không được đoán.
	private static readonly Dictionary<int, (int RawX, int RawY)> TransitPointByMapId = new() {
		[21] = (57220, 98200)
	};

	public static bool TryGetTransitPoint(int mapId, out int rawX, out int rawY) {
		rawX = 0;
		rawY = 0;
		if (! TransitPointByMapId.TryGetValue(mapId, out (int RawX, int RawY) point)) return false;
		rawX = point.RawX;
		rawY = point.RawY;
		return true;
	}

	// Map đích không nằm trong danh sách (toàn bộ nhóm mê cung 22-51) thì chọn điểm đến nào đi bộ tới đích ngắn nhất.
	//
	// Số chặng tính bằng chính GameMapRoutePlanner mà bước đi bộ dùng, nên hai bên không thể lệch nhau.
	//
	// CHỈ trả true khi nhảy xong còn ÍT CHẶNG HƠN đứng tại chỗ đi bộ. Không có chốt này thì nhân vật bị đưa sang
	// một map khác mà đường đi không ngắn lại — đúng lỗi chủ dự án đã bắt ở Di ngoại phù ngày 2026-09-11.
	public static bool TryGetNearestOptionIndex(int currentMapId, int destinationMapId, out int optionIndex, out int hubMapId, out int remainingTransitions, out string reason) {
		optionIndex = -1;
		hubMapId = 0;
		remainingTransitions = int.MaxValue;
		int walkingTransitions = GameMapRoutePlanner.FindRoute(currentMapId, destinationMapId).Count;
		int index = 0;
		foreach ((string name, int mapId) in Destinations) {
			if (mapId == currentMapId) continue;
			int currentIndex = index++;
			if (mapId <= 0) continue;
			int count = GameMapRoutePlanner.FindRoute(mapId, destinationMapId).Count;
			if (count == 0 || count >= remainingTransitions) continue;
			optionIndex = currentIndex;
			hubMapId = mapId;
			remainingTransitions = count;
		}
		if (optionIndex < 0) {
			reason = $"Không điểm đến nào của Điểm chuyển tiếp đi bộ được tới Map{destinationMapId}.";
			return false;
		}
		if (walkingTransitions > 0 && remainingTransitions >= walkingTransitions) {
			reason = $"Nhảy tới Map{hubMapId} còn {remainingTransitions} chặng, không ngắn hơn {walkingTransitions} chặng đi bộ thẳng từ Map{currentMapId}.";
			optionIndex = -1;
			hubMapId = 0;
			remainingTransitions = int.MaxValue;
			return false;
		}
		reason = $"Nhảy tới Map{hubMapId} (số {optionIndex}) còn {remainingTransitions} chặng, đi bộ thẳng từ Map{currentMapId} mất {walkingTransitions} chặng.";
		return true;
	}
}
