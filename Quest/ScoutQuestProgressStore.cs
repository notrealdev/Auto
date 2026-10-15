namespace Auto.Quest;

using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using Auto.Runtime;

public sealed class ScoutQuestProgress {
	// Local date the progress belongs to, "yyyy-MM-dd". Progress of an earlier day is ignored.
	public string Day { get; set; } = "";
	public int CompletedRounds { get; set; }
	// Destination of the round accepted but not handed in yet; MapId 0 = none.
	public string PendingMapName { get; set; } = "";
	public int PendingMapId { get; set; }
	public int PendingRawX { get; set; }
	public int PendingRawY { get; set; }
	public bool PendingDoctorVisited { get; set; }
}

// Thám quân progress per character, written to disk on every change (owner 2026-10-13: remember it, the connection can
// drop in the middle of a quest). A reconnect or an Auto restart builds a new GameWindow and a new ScoutQuestAutomation,
// so the in-memory progress alone was lost exactly when it was needed: Hoàng Thiên Hóa only says "Ngươi chưa tìm được
// Đại phu sao?" and never repeats the map. Saved automatically, unlike Profiles.json, because it is state, not settings.
internal static class ScoutQuestProgressStore {
	private static readonly string StoreDirectory = Path.Combine(AppContext.BaseDirectory, AppVersion.ProfilesDirectoryName);
	private static readonly string StorePath = Path.Combine(StoreDirectory, "ScoutQuestProgress.json");

	private static readonly JsonSerializerOptions Options = new() {
		WriteIndented = true,
		IndentCharacter = '\t',
		IndentSize = 1,
		Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
	};

	private static readonly object SyncRoot = new();
	private static Dictionary<string, ScoutQuestProgress> progressByCharacter = new(StringComparer.Ordinal);
	private static bool loaded;

	// The daily quest count resets at local midnight (owner confirmed 2026-10-13).
	public static string Today => DateTime.Now.ToString("yyyy-MM-dd");

	// Today's progress of this character, or a fresh one.
	public static ScoutQuestProgress Get(string characterName) {
		lock (SyncRoot) {
			EnsureLoaded();
			if (characterName.Length > 0 && progressByCharacter.TryGetValue(characterName, out ScoutQuestProgress? stored) && stored.Day == Today) {
				return new ScoutQuestProgress {
					Day = stored.Day,
					CompletedRounds = stored.CompletedRounds,
					PendingMapName = stored.PendingMapName,
					PendingMapId = stored.PendingMapId,
					PendingRawX = stored.PendingRawX,
					PendingRawY = stored.PendingRawY,
					PendingDoctorVisited = stored.PendingDoctorVisited
				};
			}
			return new ScoutQuestProgress { Day = Today };
		}
	}

	public static void Save(string characterName, ScoutQuestProgress progress) {
		if (characterName.Length == 0) return;
		try {
			lock (SyncRoot) {
				EnsureLoaded();
				progressByCharacter[characterName] = progress;
				Directory.CreateDirectory(StoreDirectory);
				string temporaryPath = StorePath + ".tmp";
				File.WriteAllText(temporaryPath, JsonSerializer.Serialize(progressByCharacter, Options));
				File.Move(temporaryPath, StorePath, true);
			}
		} catch (Exception ex) {
			DebugLog.AddProfileEvent($"SCOUT_PROGRESS_SAVE_HỎNG | {ex.GetType().Name}: {ex.Message}");
		}
	}

	private static void EnsureLoaded() {
		if (loaded) return;
		loaded = true;
		try {
			if (! File.Exists(StorePath)) return;
			progressByCharacter = JsonSerializer.Deserialize<Dictionary<string, ScoutQuestProgress>>(File.ReadAllText(StorePath), Options)
				?? new(StringComparer.Ordinal);
			progressByCharacter = new(progressByCharacter, StringComparer.Ordinal);
		} catch (Exception ex) {
			DebugLog.AddProfileEvent($"SCOUT_PROGRESS_LOAD_HỎNG | {ex.GetType().Name}: {ex.Message}");
		}
	}
}
