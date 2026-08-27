namespace Auto.Utils;

using System.Diagnostics;

public static class GameClientIdentity {
	public const string ExpectedExecutablePath = @"D:\G\FS\Game.exe";

	public static ValidationResult ValidateProcess(int processId) {
		try {
			using Process process = Process.GetProcessById(processId);
			string executablePath = process.MainModule?.FileName ?? "";
			return ValidateFile(executablePath);
		} catch (Exception ex) {
			return ValidationResult.Fail("", "", "", $"{ex.GetType().Name}: {ex.Message}");
		}
	}

	public static ValidationResult ValidateFile(string executablePath) {
		try {
			if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath)) {
				return ValidationResult.Fail(executablePath, "", "", "Game.exe was not found.");
			}

			string fullPath = Path.GetFullPath(executablePath);
			string expectedPath = Path.GetFullPath(ExpectedExecutablePath);
			string actualVersion = FileVersionInfo.GetVersionInfo(fullPath).FileVersion ?? "";
			return string.Equals(fullPath, expectedPath, StringComparison.OrdinalIgnoreCase)
				? ValidationResult.Pass(fullPath, actualVersion, "")
				: ValidationResult.Fail(fullPath, actualVersion, "", $"Game client path mismatch | ExpectedPath={expectedPath}");
		} catch (Exception ex) {
			return ValidationResult.Fail(executablePath, "", "", $"{ex.GetType().Name}: {ex.Message}");
		}
	}
}

public readonly record struct ValidationResult(bool Success, string ExecutablePath, string FileVersion, string Sha256, string FailureReason) {
	public static ValidationResult Pass(string executablePath, string fileVersion, string sha256) => new(true, executablePath, fileVersion, sha256, "");
	public static ValidationResult Fail(string executablePath, string fileVersion, string sha256, string failureReason) => new(false, executablePath, fileVersion, sha256, failureReason);
}
