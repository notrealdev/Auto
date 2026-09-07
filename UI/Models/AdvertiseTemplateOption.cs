namespace Auto.UI.Models;

public sealed record AdvertiseTemplateOption(string Name, string Content) {
	public override string ToString() => Name;
}
