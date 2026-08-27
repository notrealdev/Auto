namespace Auto.Utils;

using System.Text;

public static class LegacyVietnameseText {
	private const string Tcvn3Characters = "µ¸¶·¹¨»¾¼½Æ©ÇÊÈÉË®ÌÐÎÏÑªÒÕÓÔÖ×ÝØÜÞßãáâä«åèæçé¬êíëìîïóñòô­õøö÷ùúýûüþ¡¢§£¤¥¦";
	private const string UnicodeCharacters = "àáảãạăằắẳẵặâầấẩẫậđèéẻẽẹêềếểễệìíỉĩịòóỏõọôồốổỗộơờớởỡợùúủũụưừứửữựỳýỷỹỵĂÂĐÊÔƠƯ";

	public static string Decode(byte[] bytes) {
		if (bytes.Length == 0) return "";
		char[] characters = Encoding.Latin1.GetString(bytes).ToCharArray();
		for (int index = 0; index < characters.Length; index++) {
			int mappedIndex = Tcvn3Characters.IndexOf(characters[index]);
			if (mappedIndex >= 0) characters[index] = UnicodeCharacters[mappedIndex];
		}
		return new string(characters);
	}

	// Mã hóa Unicode sang đúng bảng TCVN3 mà client game dùng cho nội dung chat.
	public static bool TryEncode(string text, out byte[] bytes, out char unsupportedCharacter) {
		List<byte> result = new(text.Length);
		foreach (char character in text) {
			int mappedIndex = UnicodeCharacters.IndexOf(character);
			char encodedCharacter = mappedIndex >= 0 ? Tcvn3Characters[mappedIndex] : character;
			if (encodedCharacter > byte.MaxValue) {
				bytes = Array.Empty<byte>();
				unsupportedCharacter = character;
				return false;
			}
			result.Add((byte)encodedCharacter);
		}
		bytes = result.ToArray();
		unsupportedCharacter = '\0';
		return true;
	}
}
