namespace Auto.Loot;

public sealed class LootSpriteScanResult {
	public List<LootSnapshot> Items { get; } = new();
	public LootSpriteScanDiagnostics Diagnostics { get; } = new();
}

public sealed class LootSpriteScanDiagnostics {
	public int ReadableRegionCount { get; set; }
	public int PatternCount { get; set; }
	public int GroundPointerCount { get; set; }
	public int CoordinateReadFailureCount { get; set; }
	public int LayoutRejectedCount { get; set; }
	public int InvalidCoordinateCount { get; set; }
	public int OutOfRangeCount { get; set; }
	public int StaleCount { get; set; }
	public int PathRejectedCount { get; set; }
	public int DuplicateCount { get; set; }
	public string FirstRejected { get; private set; } = "";
	public List<string> Rejections { get; } = new();

	public int BadCount => LayoutRejectedCount + InvalidCoordinateCount + PathRejectedCount;

	public void CaptureFirstRejected(string value) {
		if (string.IsNullOrWhiteSpace(FirstRejected)) {
			FirstRejected = value;
		}
	}

	public void CaptureRejected(string value) {
		CaptureFirstRejected(value);
		Rejections.Add(value);
	}
}
