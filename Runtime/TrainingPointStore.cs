namespace Auto.Runtime;

using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

public sealed class TrainingPointFile {
	public List<TrainingPoint> Points { get; set; } = [];
}

// Danh sách điểm train DÙNG CHUNG cho mọi account (chủ dự án chốt 2026-09-22).
//
// Vì sao để chung chứ không nằm trong hồ sơ từng nhân vật: các bãi là đặc điểm của THẾ GIỚI GAME, không phải của
// nhân vật — thêm một bãi mới thì mọi account đều dùng được ngay, không phải gõ lại 6 lần.
//
// Cái vẫn nằm RIÊNG theo từng account là ĐIỂM ĐANG CHỌN: AccountProfile.SavedTrainingMapId cùng bộ ba
// CenterMapId/CenterX/CenterY trong Attack.Settings. Mỗi nhân vật chọn một bãi khác nhau trong cùng danh sách.
internal static class TrainingPointStore {
	private static readonly string StoreDirectory = Path.Combine(AppContext.BaseDirectory, AppVersion.ProfilesDirectoryName);
	private static readonly string StorePath = Path.Combine(StoreDirectory, "TrainingPoints.json");

	private static readonly JsonSerializerOptions Options = new() {
		WriteIndented = true,
		IndentCharacter = '\t',
		IndentSize = 1,
		// Bộ mã hoá mặc định escape hết tiếng Việt thành \uXXXX, file mở ra không đọc được bằng mắt (đo bằng
		// scratchpad 2026-09-21). File này chưa có chữ Việt nhưng giữ cùng tuỳ chọn với hai file JSON kia để
		// không có chỗ nào lệch khuôn.
		Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
	};

	private static readonly object SyncRoot = new();
	private static List<TrainingPoint> points = [];
	private static bool loaded;

	public static IReadOnlyList<TrainingPoint> Points {
		get {
			EnsureLoaded();
			lock (SyncRoot) return [.. points];
		}
	}

	public static void Load() {
		try {
			lock (SyncRoot) {
				loaded = true;
				if (! File.Exists(StorePath)) {
					points = [];
					DebugLog.AddProfileEvent($"TRAINING_POINTS_LOAD | Chưa có file {StorePath}.");
					return;
				}
				TrainingPointFile? file = JsonSerializer.Deserialize<TrainingPointFile>(File.ReadAllText(StorePath), Options);
				points = file?.Points.Where(point => point.IsValid).ToList() ?? [];
				DebugLog.AddProfileEvent($"TRAINING_POINTS_LOAD | Đã đọc {points.Count} điểm train dùng chung từ {StorePath}.");
			}
		} catch (Exception ex) {
			DebugLog.AddProfileEvent($"TRAINING_POINTS_LOAD_HỎNG | {ex.GetType().Name}: {ex.Message}");
		}
	}

	// Ghi ngay khi người dùng bấm Lưu trong hộp thoại — danh sách dùng chung không bám theo nhịp lưu hồ sơ 30 giây
	// của từng account.
	public static void Save(IEnumerable<TrainingPoint> updated) {
		try {
			lock (SyncRoot) {
				points = updated.Where(point => point.IsValid).ToList();
				Directory.CreateDirectory(StoreDirectory);
				string temporaryPath = StorePath + ".tmp";
				File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new TrainingPointFile { Points = points }, Options));
				File.Move(temporaryPath, StorePath, true);
				loaded = true;
				DebugLog.AddProfileEvent($"TRAINING_POINTS_SAVED | SốĐiểm={points.Count} | {StorePath}");
			}
		} catch (Exception ex) {
			DebugLog.AddProfileEvent($"TRAINING_POINTS_SAVE_HỎNG | {ex.GetType().Name}: {ex.Message}");
		}
	}

	private static void EnsureLoaded() {
		lock (SyncRoot) {
			if (loaded) return;
		}
		Load();
	}
}
