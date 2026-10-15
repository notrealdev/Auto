namespace Auto.Runtime;

using System.Text;

// Chat messages other players sent to any account, shown in one box above the loot box (owner 2026-10-15). Same shape as
// LootFeed: keep the raw entries and rebuild the whole text, because the name column width follows the longest name shown.
//
// Fed by Market/ChatInboxReader.cs, for accounts whose master switch is on.
internal static class ChatFeed {
	private const int MaximumLines = 300;

	private readonly record struct Entry(DateTime Moment, string Receiver, string Sender, string Text);

	private static readonly object syncRoot = new();
	private static readonly Queue<Entry> entries = new();
	private static string cachedText = "";

	// Raised after each change. Listeners must marshal to the UI thread themselves.
	public static event Action? Changed;

	public static void Add(int processId, string sender, string text) {
		if (string.IsNullOrWhiteSpace(text)) return;
		string characterName = DebugLog.GetProcessName(processId);
		string receiver = characterName.Length > 0 ? characterName : $"PID {processId}";
		lock (syncRoot) {
			entries.Enqueue(new Entry(DateTime.Now, receiver, sender, text));
			while (entries.Count > MaximumLines) entries.Dequeue();
			cachedText = Render(entries);
		}
		Changed?.Invoke();
	}

	// Clears the box only; logs are not touched.
	public static void Clear() {
		lock (syncRoot) {
			entries.Clear();
			cachedText = "";
		}
		Changed?.Invoke();
	}

	public static string Text {
		get { lock (syncRoot) return cachedText; }
	}

	// One "dd/MM/yyyy:" header per day, then "HH:mm:ss @Receiver: [Sender] text" with the text column aligned for the
	// monospaced box (Consolas in MainWindow.xaml).
	private static string Render(IEnumerable<Entry> source) {
		Entry[] items = source.ToArray();
		if (items.Length == 0) return "";
		int nameColumnWidth = items.Max(entry => entry.Receiver.Length) + 3;
		StringBuilder builder = new();
		DateOnly? currentDay = null;
		foreach (Entry entry in items) {
			DateOnly day = DateOnly.FromDateTime(entry.Moment);
			if (currentDay != day) {
				if (currentDay != null) builder.AppendLine();
				builder.AppendLine($"{day:dd/MM/yyyy}:");
				currentDay = day;
			}
			string receiver = ($"@{entry.Receiver}:").PadRight(nameColumnWidth);
			builder.AppendLine($"{entry.Moment:HH:mm:ss} {receiver}[{entry.Sender}] {entry.Text}");
		}
		return builder.ToString().TrimEnd();
	}
}
