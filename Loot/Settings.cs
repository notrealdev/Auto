namespace Auto.Loot;

public sealed class Settings {
	public bool Enabled { get; set; } = true;
	public int Range { get; set; } = 10;
	public int CenterX { get; set; }
	public int CenterY { get; set; }
	public bool UseCenterPosition { get; set; } = true;
	public bool OnlyNamedItems { get; set; }
	public bool LogCandidates { get; set; } = true;
	public bool IgnoreExistingItemsOnStart { get; set; }
	public Dictionary<string, bool> ItemSelections { get; } = new(StringComparer.OrdinalIgnoreCase);
	public Dictionary<string, bool> ExcludedItemSelections { get; } = new(StringComparer.OrdinalIgnoreCase);
	public Dictionary<string, bool> SaleItemSelections { get; } = new(StringComparer.OrdinalIgnoreCase);
	public Dictionary<string, bool> PotionNameSelections { get; } = new(StringComparer.OrdinalIgnoreCase);
	public bool EnableSaleQuantityThreshold { get; set; } = true;
	public int SaleQuantityThreshold { get; set; } = 32;
	public bool EnableSaleRemainingStrengthThreshold { get; set; } = true;
	public int SaleRemainingStrengthThreshold { get; set; } = 30;
	// Ngưỡng số lượng cho từng loại dược phẩm: đủ ngần này rồi thì ngừng nhặt riêng loại đó. <= 0 nghĩa là tắt giới hạn.
	public int PotionQuantityLimit { get; set; } = 10;
	public bool PickWhite { get; set; }
	public bool PickBlue { get; set; }
	public bool PickGreen { get; set; } = true;
	public bool PickYellow { get; set; } = true;
	public bool PickOrange { get; set; } = true;
	public bool PickOtherColor { get; set; } = true;
	public int ScanIntervalMilliseconds { get; set; } = 500;
	public int MaxCandidates { get; set; } = 8;

	public Settings() {
		ItemSelections["Đồ Trắng"] = false;
		ItemSelections["Đồ Xanh"] = false;
		ItemSelections["Đồ Lục"] = true;
		ItemSelections["Đồ Vàng"] = true;
		ItemSelections["Đồ Cam"] = true;
		ItemSelections["Đồ Khác"] = true;
		ItemSelections["Dược Phẩm"] = true;
		ItemSelections["Mảnh, Ngọc"] = true;
		ItemSelections["Bí Kíp"] = true;
		ItemSelections["Pháp Bảo"] = true;
		ItemSelections["Quẻ"] = true;
		ItemSelections["Lục Đạo"] = false;
		ItemSelections["Tứ Tượng"] = false;
		ItemSelections["Nhãn Vạn Tiên Trận"] = true;
		ExcludedItemSelections["Đồ Trắng"] = false;
		ExcludedItemSelections["Đồ Xanh"] = false;
		ExcludedItemSelections["Dược Phẩm"] = false;
		SaleItemSelections["Đồ Trắng"] = false;
		SaleItemSelections["Đồ Xanh"] = false;
		SaleItemSelections["Dược Phẩm"] = false;
		PotionNameSelections["Tiểu Hồng đơn"] = true;
		PotionNameSelections["Trung Hồng đơn"] = true;
		PotionNameSelections["Đại Hồng đơn"] = true;
		PotionNameSelections["Tiểu Hoàn đơn"] = true;
		PotionNameSelections["Trung Hoàn đơn"] = true;
		PotionNameSelections["Đại Hoàn đơn"] = true;
	}
}
