namespace Auto.Attack;

public sealed record MonsterOption(string Name, string Signature, int Type) {
	public static MonsterOption All { get; } = new("Toàn bộ", "", -1);

	public bool IsAll => string.IsNullOrWhiteSpace(Signature);

	public override string ToString() => Name;
}
