namespace Auto.Runtime;

using System.Collections.Concurrent;
using Auto.Loot;

// Confirmed ground pickups per account, so BagAttributeWatcher logs an item only when it entered the bag by being picked up
// (owner rule 2026-10-11). Without it, moving an item inside the bag or receiving it in a trade made it look new and it was
// logged again. Each confirmed pickup can be consumed once.
internal static class PickupJournal {
	private static readonly ConcurrentDictionary<int, List<(string Key, DateTime Utc, bool ShownOnFeed)>> pickupsByProcess = new();

	// Called by the loot engine once the bag count of that name went up. shownOnFeed: the loot engine already put this
	// pickup on the UI feed itself (a listed "Vũ khí xanh"), so the watcher must not add a second line.
	public static void Record(int processId, string groundName, bool shownOnFeed) {
		string key = ToKey(groundName);
		if (processId <= 0 || key.Length == 0) return;
		List<(string Key, DateTime Utc, bool ShownOnFeed)> pickups = pickupsByProcess.GetOrAdd(processId, _ => []);
		lock (pickups) pickups.Add((key, DateTime.UtcNow, shownOnFeed));
	}

	// Takes one pickup of this name not older than maximumAge; older entries are dropped on the way.
	public static bool TryConsume(int processId, string bagName, TimeSpan maximumAge, out bool shownOnFeed) {
		shownOnFeed = false;
		if (!pickupsByProcess.TryGetValue(processId, out List<(string Key, DateTime Utc, bool ShownOnFeed)>? pickups)) return false;
		string key = ToKey(bagName);
		DateTime oldest = DateTime.UtcNow - maximumAge;
		lock (pickups) {
			pickups.RemoveAll(pickup => pickup.Utc < oldest);
			int index = pickups.FindIndex(pickup => pickup.Key == key);
			if (index < 0) return false;
			shownOnFeed = pickups[index].ShownOnFeed;
			pickups.RemoveAt(index);
			return true;
		}
	}

	// Ground names carry an "xN" stack suffix that bag names do not.
	private static string ToKey(string name) => AutoFsSpecialItemClassifier.ToAsciiUpper(AutoFsSpecialItemClassifier.NormalizeGroundName(name ?? ""));
}
