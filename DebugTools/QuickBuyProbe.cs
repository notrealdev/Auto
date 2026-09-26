namespace Auto.DebugTools;

using System.Text;
using Auto.Loot;
using Auto.Runtime;
using Auto.Utils;

// Gửi lệnh mua nhanh thuốc (lệnh native 328, gói tự dựng, ô đích = ô trống đầu tiên của túi chính) vào ĐÚNG account đang chọn rồi đọc lại túi để biết server có bán thật không.
//
// Vì sao cần: hàm mua của client (RVA 0x346010) mới chỉ được tìm bằng phân tích tĩnh, chưa từng gọi. Native trả 1 chỉ
// nói hàm đã được gọi, không nói server đã bán. Bằng chứng duy nhất là số thuốc trong túi tăng lên (chủ dự án chốt
// 2026-09-25: không thấy item tăng là đã chạm giới hạn 1000 bình/ngày của server).
//
// GỬI LỆNH THẬT và TIÊU TIỀN THẬT của account đó.
public static class QuickBuyProbe {
	public const string BuildStamp = "QUICK-BUY-20260926-01";
	// Phải khớp NativeBuildStamp trong SystemUint.cpp. Lệch nghĩa là tiến trình game còn giữ DLL cũ, chưa có lệnh 328.
	private const ulong ExpectedNativeBuildStamp = 99990013;
	private const int PollIntervalMilliseconds = 400;
	private const int PollTimeoutMilliseconds = 6000;

	public static string Run(GameWindow game, string label, int potionCode, int quantity) {
		StringBuilder text = new();
		text.AppendLine($"===== Mua nhanh {label} =====");
		text.AppendLine($"BuildStamp = {BuildStamp}");
		text.AppendLine($"PID = {game.ProcessId} | Nhân vật = {game.CharacterName} | Mã thuốc = {potionCode} | Số lượng = {quantity}");

		bool stampRead = game.AutoFsTransport.TryQueryNativeBuildStamp(game.Handle, out ulong nativeStamp, out string stampError);
		if (!stampRead || nativeStamp != ExpectedNativeBuildStamp) {
			text.AppendLine($"DỪNG | Native đang sống BuildStamp={(stampRead ? nativeStamp.ToString() : "đọc lỗi: " + stampError)} | Mong đợi={ExpectedNativeBuildStamp}");
			text.AppendLine("Tiến trình game còn giữ DLL cũ (chưa có lệnh 328). Phải tắt client này rồi mở lại để nạp DLL mới.");
			return text.ToString();
		}

		InventoryPotionCounter counter = new();
		if (!counter.TrySnapshot(game.ProcessId, out Dictionary<string, int> before)) {
			text.AppendLine("DỪNG | Không đọc được túi đồ trước khi mua.");
			return text.ToString();
		}
		if (!QuickBuyPotions.TryGetName(QuickBuyPotions.Hp, potionCode, out string potionName) && !QuickBuyPotions.TryGetName(QuickBuyPotions.Mp, potionCode, out potionName)) {
			text.AppendLine($"DỪNG | Mã thuốc {potionCode} không nằm trong danh sách.");
			return text.ToString();
		}
		text.AppendLine($"Trong túi trước khi gửi: {QuickBuyEngine.CountPotion(before, potionName)} {potionName}");
		text.AppendLine($"Cờ chặn của hàm mua client [root+0x4B79C] = {QuickBuyEngine.TryReadBlockFlag(game.ProcessId)?.ToString() ?? "?"} (khác 0 thì hàm thoát ngay, không mua)");
		int moneyBefore = InventoryMoneyReader.Read(game.ProcessId);
		int? dailyBefore = QuickBuyEngine.TryReadDailyCount(game.ProcessId);

		if (!QuickBuyEngine.TryFindFreeMainBagSlot(game.ProcessId, out int slotX, out int slotY)) {
			text.AppendLine("DỪNG | Túi chính không còn ô trống (hoặc không đọc được) để làm ô đích cho thuốc mua về.");
			return text.ToString();
		}
		text.AppendLine($"Ô đích = room 0x{GameAddresses.Inventory.MainBagRoom:X2}, x={slotX}, y={slotY}");
		if (!game.AutoFsTransport.TrySendQuickBuyRawForDebug(game.Handle, potionCode, quantity, GameAddresses.Inventory.MainBagRoom, slotX, slotY, out string sendError)) {
			text.AppendLine($"GỬI THẤT BẠI | {sendError}");
			return text.ToString();
		}
		text.AppendLine("Native đã gửi gói mua (Return=1). Đang chờ số thuốc trong túi tăng...");

		DateTime startUtc = DateTime.UtcNow;
		Dictionary<string, int> increases = [];
		Dictionary<string, int> after = before;
		while ((DateTime.UtcNow - startUtc).TotalMilliseconds < PollTimeoutMilliseconds) {
			Thread.Sleep(PollIntervalMilliseconds);
			counter.Invalidate();
			if (!counter.TrySnapshot(game.ProcessId, out after)) continue;
			increases = Diff(before, after, potionName);
			if (increases.Count > 0) break;
		}
		int elapsedMilliseconds = (int)(DateTime.UtcNow - startUtc).TotalMilliseconds;
		int moneyAfter = InventoryMoneyReader.Read(game.ProcessId);
		int? dailyAfter = QuickBuyEngine.TryReadDailyCount(game.ProcessId);
		text.AppendLine($"Bộ đếm bình/ngày trước/sau = {dailyBefore?.ToString() ?? "?"} / {dailyAfter?.ToString() ?? "?"}");
		text.AppendLine($"THÀNH CÔNG = {(increases.Count > 0 ? "CÓ" : "KHÔNG")} (chỉ tính khi số {potionName} trong túi tăng)");

		text.AppendLine($"Tiền trước/sau (xu) = {moneyBefore} / {moneyAfter} | Chênh = {(moneyBefore >= 0 && moneyAfter >= 0 ? (moneyAfter - moneyBefore).ToString() : "không đọc được")}");
		if (increases.Count > 0) {
			text.AppendLine($"KẾT QUẢ | Túi TĂNG sau {elapsedMilliseconds} ms: {string.Join(", ", increases.Select(entry => $"{entry.Key} +{entry.Value}"))}");
		} else {
			text.AppendLine($"KẾT QUẢ | KHÔNG thấy món nào tăng sau {elapsedMilliseconds} ms. Nguyên nhân chưa biết.");
		}
		return text.ToString();
	}

	private static Dictionary<string, int> Diff(Dictionary<string, int> before, Dictionary<string, int> after, string potionName) {
		Dictionary<string, int> increases = [];
		foreach ((string key, int count) in after) {
			if (QuickBuyEngine.CountPotion(new Dictionary<string, int> { [key] = 1 }, potionName) == 0) continue;
			int delta = count - before.GetValueOrDefault(key);
			if (delta > 0) increases[key] = delta;
		}
		return increases;
	}
}
