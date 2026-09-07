namespace Auto.UI.Models;

public sealed record CoordinateOption(int RawX, int RawY) {
	private const int RawXScale = 256;
	private const int RawYScale = 512;

	public string Display => $"{RawX / RawXScale}/{RawY / RawYScale}";

	public override string ToString() => Display;
}
