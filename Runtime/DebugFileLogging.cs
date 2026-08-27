namespace Auto.Runtime;

public static class DebugFileLogging {
	private static int enabled = 1;

	public static bool Enabled => Volatile.Read(ref enabled) != 0;

	// Changes the global file-log gate without changing any automation setting.
	public static void SetEnabled(bool value) {
		Volatile.Write(ref enabled, value ? 1 : 0);
	}
}
