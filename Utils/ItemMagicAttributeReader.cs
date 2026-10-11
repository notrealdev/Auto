namespace Auto.Utils;

using System.Text.RegularExpressions;

// Reads the magic attribute lines of an item in the item table (the stat lines of the tooltip). Layout and ids are
// documented at GameAddresses.Item.MagicAttributeArray and Globals.MagicAttributeDescriptorTable. Read-only.
public static class ItemMagicAttributeReader {
	// "Xuất chiêu (Vũ khí)" and "Xuất chiêu (bùa pháp)".
	public const int AttackSpeedId = 115;
	public const int CastSpeedId = 116;
	private const int MaximumAttributeId = 400;
	private const int DescriptorStride = 0xA4;
	private const int DescriptorTemplateOffset = 4;
	private const int MaximumKeyLength = 32;
	private const int MaximumTemplateLength = 0xA0;
	private static readonly Regex ValuePlaceholder = new("#d1([+\\-~]?)", RegexOptions.Compiled);

	public static IReadOnlyList<ItemMagicAttribute> Read(MemoryReader reader, IntPtr moduleBase, IntPtr itemRecord) {
		List<ItemMagicAttribute> attributes = [];
		byte[] block = reader.ReadBytes(IntPtr.Add(itemRecord, GameAddresses.Item.MagicAttributeArray), GameAddresses.Item.MagicAttributeStride * GameAddresses.Item.MagicAttributeMaximumCount);
		for (int line = 0; line < GameAddresses.Item.MagicAttributeMaximumCount; line++) {
			int offset = line * GameAddresses.Item.MagicAttributeStride;
			if (offset + 8 > block.Length) break;
			int id = BitConverter.ToInt32(block, offset);
			if (id <= 0 || id > MaximumAttributeId) break;
			int value = BitConverter.ToInt32(block, offset + 4);
			(string key, string template) = ReadDescriptor(reader, moduleBase, id);
			attributes.Add(new ItemMagicAttribute(id, key, FormatText(template, value, key), value));
		}
		return attributes;
	}

	public static bool HasAttackSpeed(IReadOnlyList<ItemMagicAttribute> attributes) => attributes.Any(attribute => attribute.Id is AttackSpeedId or CastSpeedId);

	public static string Describe(IReadOnlyList<ItemMagicAttribute> attributes) => string.Join(", ", attributes.Select(attribute => attribute.Text));

	private static (string Key, string Template) ReadDescriptor(MemoryReader reader, IntPtr moduleBase, int id) {
		IntPtr entry = IntPtr.Add(moduleBase, GameAddresses.Globals.MagicAttributeDescriptorTable + id * DescriptorStride);
		IntPtr keyAddress = reader.ReadPointer32(entry);
		string key = keyAddress == IntPtr.Zero ? "" : System.Text.Encoding.ASCII.GetString(UntilNull(reader.ReadBytes(keyAddress, MaximumKeyLength)));
		string template = LegacyVietnameseText.Decode(UntilNull(reader.ReadBytes(IntPtr.Add(entry, DescriptorTemplateOffset), MaximumTemplateLength)));
		return (key, template);
	}

	// "Xuất chiêu (Vũ khí):#d1+%" with 10 -> "Xuất chiêu (Vũ khí): +10%". Unknown templates fall back to "key=value".
	private static string FormatText(string template, int value, string key) {
		if (! template.Contains("#d1", StringComparison.Ordinal)) return $"{(key.Length > 0 ? key : "?")}={value}";
		string text = ValuePlaceholder.Replace(template, match => (match.Groups[1].Value == "+" ? "+" : match.Groups[1].Value == "-" ? "-" : "") + value, 1);
		int colon = text.IndexOf(':');
		return colon >= 0 && colon + 1 < text.Length && text[colon + 1] != ' ' ? text.Insert(colon + 1, " ") : text;
	}

	private static byte[] UntilNull(byte[] bytes) {
		int length = Array.IndexOf(bytes, (byte)0);
		return length < 0 ? bytes : bytes[..length];
	}
}

public sealed record ItemMagicAttribute(int Id, string Key, string Text, int Value);
