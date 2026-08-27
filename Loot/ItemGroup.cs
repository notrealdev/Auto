namespace Auto.Loot;

public enum ItemGroup { Normal, Herbal, Unknown }
public enum ItemColor { White, Blue, Green, Yellow, Orange, Other }

public sealed record ItemClassification(ItemGroup Group, ItemColor Color, int AttributeClass, bool Complete);

public static class ItemGroupClassifier {
	private const uint HerbalGroundKind = 10;

	public static ItemClassification Classify(LootSnapshot item) {
		ItemColor color = (item.QualityCodeA & 0xFF) switch { 0 => ItemColor.White, 1 => ItemColor.Blue, 2 => ItemColor.Green, 3 => ItemColor.Yellow, 4 => ItemColor.Orange, _ => ItemColor.Other };
		int attributeClass = (item.QualityCodeB & 0xFF) >> 4;
		ItemGroup group = item.GroundKind == HerbalGroundKind ? ItemGroup.Herbal : ItemGroup.Normal;
		return new(group, color, attributeClass, true);
	}

	public static string NormalizeName(string name) {
		return AutoFsSpecialItemClassifier.NormalizeGroundName(name);
	}
}
