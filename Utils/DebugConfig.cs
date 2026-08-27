namespace Auto.Utils;

public enum DebugLevel {
	None,
	Error,
	Info,
	Verbose
}

public static class DebugConfig {
	public static DebugLevel Level { get; set; } = DebugLevel.Verbose;

	public static bool EnableLog => Level != DebugLevel.None;

	public static bool LogError => Level >= DebugLevel.Error;

	public static bool LogInfo => Level >= DebugLevel.Info;

	public static bool LogVerbose => Level >= DebugLevel.Verbose;
}