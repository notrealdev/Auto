namespace Auto.Runtime;

using System.Text;

// Dòng vật phẩm nhặt được của TẤT CẢ account, gom chung một chỗ để hiển thị thẳng trên giao diện.
//
// Nguồn dữ liệu: đúng những lượt nhặt được ghi vào loot-drops.log, tức nhãn LOOT_PICKED_UP. KHÔNG gồm
// LOOT_ROUTINE_PICKUP (Dược Phẩm, Thảo Dược, Tứ Tượng, Lục Đạo) vì nhóm đó đã được chủ dự án tách sang
// loot-scan.log ngày 2026-09-15 chính vì nhặt liên tục gây nhiễu.
//
// Lấy thẳng từ nơi nhặt (Loot/Engine.cs) chứ KHÔNG đọc ngược file log: đọc file thì phải tách chuỗi đã định dạng,
// hỏng ngay khi ai đó đổi một chữ trong dòng log.
internal static class LootFeed {
	// Giữ bao nhiêu dòng gần nhất. Đủ để soi lại một buổi treo máy mà không phình bộ nhớ; muốn xem đầy đủ thì đã có
	// loot-drops.log giữ toàn bộ.
	private const int MaximumLines = 300;

	// Giữ NGUYÊN LIỆU (giờ, tên, vật phẩm) chứ không giữ chuỗi đã ghép: bề rộng cột tên phụ thuộc tên DÀI NHẤT đang
	// hiển thị, mà tên đó đổi mỗi khi hàng đợi trượt đi, nên phải ghép lại toàn bộ chứ không nối thêm được.
	private readonly record struct Entry(DateTime Moment, string Who, string Item);

	private static readonly object syncRoot = new();
	private static readonly Queue<Entry> entries = new();
	private static string cachedText = "";

	// Bắn sau mỗi lượt nhặt. Nơi nghe phải tự đưa về luồng UI.
	public static event Action? Changed;

	// Bỏ ký tự điều khiển/định dạng khỏi tên nhân vật.
	//
	// Vì sao cần: tên đọc từ client có thể lẫn ký tự vô hình. Đo thật ngày 2026-09-17 trên "TiểuHồngĐơn"
	// (loot-drops.log, PID 1604), codepoint đọc ra là:
	//   0054 0069 1EC3 0075 [0095] 0048 1ED3 006E 0067 [0095] 0110 01A1 006E
	// Hai ô U+0095 không vẽ ra gì nhưng Length vẫn đếm, nên PadRight đệm hụt đúng 2 cột và cột vật phẩm của riêng
	// dòng đó bị kéo sang trái. Các tên khác không dính (XinLỗiEm dài 8 ký tự, đúng 8 codepoint).
	//
	// Chỉ lọc ở ĐÂY, không lọc ở chỗ đọc tên: các file log khác vẫn ghi nguyên tên như client trả về, đổi chỗ đó là
	// đổi luôn nội dung mọi log đang có.
	private static string StripInvisible(string name) {
		if (!name.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format)) {
			return name;
		}
		return new string(name.Where(character => !char.IsControl(character) && char.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.Format).ToArray());
	}

	public static void Add(int processId, string itemName) {
		if (string.IsNullOrWhiteSpace(itemName)) return;
		string characterName = StripInvisible(DebugLog.GetProcessName(processId));
		string who = characterName.Length > 0 ? characterName : $"PID {processId}";
		lock (syncRoot) {
			// Giờ theo đồng hồ máy, cùng thang với dấu thời gian trong loot-drops.log để đối chiếu được.
			entries.Enqueue(new Entry(DateTime.Now, who, itemName));
			while (entries.Count > MaximumLines) entries.Dequeue();
			cachedText = Render(entries);
		}
		Changed?.Invoke();
	}

	// Xoá sạch ô theo dõi trên giao diện. KHÔNG đụng tới loot-drops.log — file đó vẫn là bản đầy đủ.
	public static void Clear() {
		lock (syncRoot) {
			entries.Clear();
			cachedText = "";
		}
		Changed?.Invoke();
	}

	// Toàn bộ dòng đang giữ, cũ trước mới sau — khớp thứ tự đọc của loot-drops.log.
	public static string Text {
		get { lock (syncRoot) return cachedText; }
	}

	// Gom theo NGÀY (một dòng tiêu đề "dd/MM/yyyy:" rồi tới các lượt nhặt trong ngày đó), và canh cột vật phẩm cho
	// thẳng hàng bằng cách đệm khoảng trắng sau "@Tên:".
	//
	// Canh cột chỉ thẳng khi ô hiển thị dùng font đều nét — LootFeedBox trong MainWindow.xaml đang để Consolas.
	// Đổi sang font tỉ lệ thì phần đệm này không còn thẳng nữa.
	private static string Render(IEnumerable<Entry> source) {
		Entry[] items = source.ToArray();
		if (items.Length == 0) return "";
		// Bề rộng cột tên lấy theo tên dài nhất đang hiển thị, cộng 2 khoảng trắng cho dễ đọc.
		int nameColumnWidth = items.Max(entry => entry.Who.Length) + 3;
		StringBuilder builder = new();
		DateOnly? currentDay = null;
		foreach (Entry entry in items) {
			DateOnly day = DateOnly.FromDateTime(entry.Moment);
			if (currentDay != day) {
				if (currentDay != null) builder.AppendLine();
				builder.AppendLine($"{day:dd/MM/yyyy}:");
				currentDay = day;
			}
			string who = ($"@{entry.Who}:").PadRight(nameColumnWidth);
			builder.AppendLine($"{entry.Moment:HH:mm:ss} {who}{entry.Item}");
		}
		return builder.ToString().TrimEnd();
	}
}
