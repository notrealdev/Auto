namespace Auto.DebugTools;

using System.Text;
using Auto.Loot;
using Auto.Runtime;
using Auto.Utils;

// Buys the selected HP and MP potion once each from the NPC shop that is ALREADY open on screen (native command 330),
// then reads the bag back. Success counts only when the potion count in the bag goes up, same rule as QuickBuyProbe.
//
// Why: the shop buy function (GameClientAddresses::ShopBuyFunctionRva) was found by static analysis only; this is the
// first real call. It does not walk to the NPC: open the Đại Phu shop by hand first.
//
// SENDS REAL COMMANDS and SPENDS REAL MONEY of that account.
public static class DoctorShopBuyProbe {
	public const string BuildStamp = "DOCTOR-SHOP-BUY-20260927-01";
	// Must match NativeBuildStamp in SystemUint.cpp. Older DLLs have no command 330.
	private const ulong ExpectedNativeBuildStamp = 99990014;
	private const int PollIntervalMilliseconds = 400;
	private const int PollTimeoutMilliseconds = 6000;

	public static string Run(GameWindow game, string label, IReadOnlyList<(string Name, int Code)> list, int potionCode, int count) {
		StringBuilder text = new();
		text.AppendLine($"===== Mua ở Đại Phu: {label} =====");
		text.AppendLine($"BuildStamp = {BuildStamp}");
		text.AppendLine($"PID = {game.ProcessId} | Nhân vật = {game.CharacterName} | Mã thuốc = {potionCode} | Số lượng muốn mua = {count}");

		bool stampRead = game.AutoFsTransport.TryQueryNativeBuildStamp(game.Handle, out ulong nativeStamp, out string stampError);
		if (!stampRead || nativeStamp != ExpectedNativeBuildStamp) {
			text.AppendLine($"DỪNG | Native đang sống BuildStamp={(stampRead ? nativeStamp.ToString() : "đọc lỗi: " + stampError)} | Mong đợi={ExpectedNativeBuildStamp}");
			text.AppendLine("Tiến trình game còn giữ DLL cũ (chưa có lệnh 330). Phải tắt client này rồi mở lại để nạp DLL mới.");
			return text.ToString();
		}
		if (!QuickBuyPotions.TryGetName(list, potionCode, out string potionName)) {
			text.AppendLine($"DỪNG | Mã thuốc {potionCode} không nằm trong danh sách.");
			return text.ToString();
		}
		if (!NpcShopReader.IsShopOpen(game.ProcessId)) {
			text.AppendLine("DỪNG | Cửa hàng chưa mở trên màn hình (ShopState khác 2). Mở cửa hàng Đại Phu bằng tay rồi bấm lại.");
			return text.ToString();
		}
		if (!NpcShopReader.TryFindItem(game.ProcessId, potionName, out int position, out int unitWeight, out string shopDetail)) {
			text.AppendLine($"DỪNG | {shopDetail}");
			return text.ToString();
		}
		text.AppendLine($"Cửa hàng: {shopDetail}");

		InventoryPotionCounter counter = new();
		if (!counter.TrySnapshot(game.ProcessId, out Dictionary<string, int> before)) {
			text.AppendLine("DỪNG | Không đọc được túi đồ trước khi mua.");
			return text.ToString();
		}
		int countBefore = QuickBuyEngine.CountPotion(before, potionName);
		text.AppendLine($"Trong túi trước khi gửi: {countBefore} {potionName}");

		// Mua CHO ĐỦ count, không phải mua thêm count (chủ dự án chỉ ra 2026-09-27: có sẵn vài bình vẫn mua đủ 10).
		int needed = Math.Max(0, count - countBefore);
		if (needed == 0) {
			text.AppendLine($"DỪNG | Đã có đủ {countBefore}/{count} {potionName}, không cần mua.");
			return text.ToString();
		}

		// Strength: never buy more than the free strength can carry (owner 2026-09-27).
		InventoryStrengthReading strength = InventoryStrengthReader.Read(game.ProcessId);
		int buyCount = needed;
		if (strength.Success && unitWeight > 0) buyCount = Math.Min(needed, Math.Max(0, strength.Free) / unitWeight);
		text.AppendLine($"Cần mua thêm = {needed} (để đủ {count}) | Sức lực = {(strength.Success ? $"{strength.Current}/{strength.Maximum} | Còn {strength.Free}" : "đọc lỗi: " + strength.FailureReason)} | Nặng mỗi bình = {unitWeight} | Sẽ mua = {buyCount}");
		if (buyCount <= 0) {
			text.AppendLine("DỪNG | Không đủ sức lực để mua thêm bình nào.");
			return text.ToString();
		}
		text.AppendLine($"Cờ khoá vật phẩm [root+0x4B79C] = {QuickBuyEngine.TryReadBlockFlag(game.ProcessId)?.ToString() ?? "?"} (khác 0 thì native trả 55, không gửi)");
		int moneyBefore = InventoryMoneyReader.Read(game.ProcessId);

		if (!game.AutoFsTransport.TryBuyFromShop(game.Handle, position, buyCount, bypassMasterSwitch: true, out string sendError)) {
			text.AppendLine($"GỬI THẤT BẠI | {sendError}");
			return text.ToString();
		}
		text.AppendLine("Native đã gọi hàm mua của client (Return=1). Đang chờ số thuốc trong túi tăng...");

		DateTime startUtc = DateTime.UtcNow;
		int countAfter = countBefore;
		while ((DateTime.UtcNow - startUtc).TotalMilliseconds < PollTimeoutMilliseconds) {
			Thread.Sleep(PollIntervalMilliseconds);
			counter.Invalidate();
			if (!counter.TrySnapshot(game.ProcessId, out Dictionary<string, int> after)) continue;
			countAfter = QuickBuyEngine.CountPotion(after, potionName);
			if (countAfter > countBefore) break;
		}
		int elapsedMilliseconds = (int)(DateTime.UtcNow - startUtc).TotalMilliseconds;
		int moneyAfter = InventoryMoneyReader.Read(game.ProcessId);
		text.AppendLine($"THÀNH CÔNG = {(countAfter > countBefore ? "CÓ" : "KHÔNG")} (chỉ tính khi số {potionName} trong túi tăng)");
		text.AppendLine($"Trong túi trước/sau = {countBefore} / {countAfter} | Tăng = {countAfter - countBefore} | Sau {elapsedMilliseconds} ms");
		text.AppendLine($"Tiền trước/sau (xu) = {moneyBefore} / {moneyAfter} | Chênh = {(moneyBefore >= 0 && moneyAfter >= 0 ? (moneyAfter - moneyBefore).ToString() : "không đọc được")}");
		return text.ToString();
	}
}
