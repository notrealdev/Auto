namespace Auto.Runtime;

// Các loại thuốc mua nhanh được. Code là mã chi tiết trong bộ ba (1, mã, 0) của vật phẩm, khớp bảng mã lệnh 95 của
// AutoFS và đã đối chiếu với danh sách thuốc trong client (Tiểu Hồng=(1,0,0), Trung Hồng=(1,1,0)). Riêng Tiểu Hoàn (3) và
// Trung Hoàn (4) chưa thấy trong danh sách client nên chưa đối chiếu được.
internal static class QuickBuyPotions {
	public static readonly IReadOnlyList<(string Name, int Code)> Hp = [
		("Tiểu Hồng đơn", 0),
		("Trung Hồng đơn", 1)
	];

	public static readonly IReadOnlyList<(string Name, int Code)> Mp = [
		("Tiểu Hoàn đơn", 3),
		("Trung Hoàn đơn", 4)
	];

	public static bool TryGetName(IReadOnlyList<(string Name, int Code)> list, int code, out string name) {
		foreach ((string entryName, int entryCode) in list) {
			if (entryCode != code) continue;
			name = entryName;
			return true;
		}
		name = "";
		return false;
	}
}
