namespace Auto.Utils;

using System.Globalization;
using System.Text;

public static class RuntimeEntityLocator {
	private const int GlobalEntityTableOffset = GameAddresses.Globals.EntityTable;
	private const int EntityStride = GameAddresses.Entity.Stride;
	private const int ScanCount = 256;
	private const int FirstEntityIndex = 2;
	private const int HandleOffset = GameAddresses.Entity.Handle;
	private const int HpOffset = GameAddresses.Entity.Hp;
	private const int NameOffset = GameAddresses.Entity.Name;
	private const int NameLength = 30;
	private const int PositionOffset = GameAddresses.Entity.RawX;
	private const long MinimumLikelyAddress = 0x01000000;
	private const long MaximumUserModeAddress = 0x7FFFFFFF;

	public static bool TryFindNamedEntity(int processId, string expectedName, int targetRawX, int targetRawY, out RuntimeEntityLocation location, out string reason) {
		location = RuntimeEntityLocation.Empty;
		reason = "";
		string normalizedExpectedName = NormalizeName(expectedName);
		if (normalizedExpectedName.Length == 0) {
			reason = "Tên entity cần tìm đang trống.";
			return false;
		}
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase("Game.exe");
			IntPtr tableBase = new(reader.ReadInt32(IntPtr.Add(moduleBase, GlobalEntityTableOffset)));
			long tableBaseValue = unchecked((uint)tableBase.ToInt64());
			if (tableBaseValue < MinimumLikelyAddress || tableBaseValue > MaximumUserModeAddress) {
				reason = $"Entity TableBase không hợp lệ: 0x{tableBaseValue:X8}.";
				return false;
			}

			double bestDistance = double.MaxValue;
			int namedEntityCount = 0;
			for (int index = FirstEntityIndex; index < ScanCount; index++) {
				long entityAddressValue = tableBaseValue + (long)index * EntityStride;
				if (entityAddressValue < MinimumLikelyAddress || entityAddressValue > MaximumUserModeAddress) continue;
				IntPtr entityAddress = new((int)entityAddressValue);
				byte[] nameBytes = reader.ReadBytes(IntPtr.Add(entityAddress, NameOffset), NameLength);
				int nameLength = nameBytes.Length == NameLength ? Array.IndexOf(nameBytes, (byte)0) : 0;
				if (nameLength < 0) nameLength = NameLength;
				if (nameLength == 0) continue;
				string name = LegacyVietnameseText.Decode(nameBytes.AsSpan(0, nameLength).ToArray()).Trim();
				if (!NormalizeName(name).Contains(normalizedExpectedName, StringComparison.OrdinalIgnoreCase)) continue;
				namedEntityCount++;
				int handle = reader.ReadInt32(IntPtr.Add(entityAddress, HandleOffset));
				byte[] hp = reader.ReadBytes(IntPtr.Add(entityAddress, HpOffset), 8);
				byte[] position = reader.ReadBytes(IntPtr.Add(entityAddress, PositionOffset), 8);
				if (position.Length < 8) continue;
				int currentHp = hp.Length >= 8 ? ReadInt32(hp, 0) : 0;
				int maximumHp = hp.Length >= 8 ? ReadInt32(hp, 4) : 0;
				int rawX = ReadInt32(position, 0);
				int rawY = ReadInt32(position, 4);
				if (rawX <= 0 || rawY <= 0) continue;

				double distance = GetDistance(rawX, rawY, targetRawX, targetRawY);
				RuntimeEntityLocation candidate = new(index, handle, rawX, rawY, currentHp, maximumHp, name, distance);
				if (distance >= bestDistance) continue;
				bestDistance = distance;
				location = candidate;
			}

			if (location.Index >= 0) return true;
			reason = $"Chưa thấy entity có tên '{expectedName}' theo phép quét AutoFS index 2..255 | TênKhớpNhưngThiếuTọaĐộ={namedEntityCount}.";
			return false;
		} catch (Exception ex) {
			reason = ex.Message;
			return false;
		}
	}

	private static int ReadInt32(byte[] bytes, int offset) => BitConverter.ToInt32(bytes, offset);

	private static string NormalizeName(string value) {
		string decomposed = value.Normalize(NormalizationForm.FormD);
		StringBuilder builder = new(decomposed.Length);
		foreach (char character in decomposed) {
			UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);
			if (category == UnicodeCategory.NonSpacingMark) continue;
			builder.Append(character switch {
				'đ' => 'd',
				'Đ' => 'D',
				_ => character
			});
		}
		return builder.ToString().Normalize(NormalizationForm.FormC);
	}

	private static double GetDistance(int rawX1, int rawY1, int rawX2, int rawY2) {
		double dx = (rawX2 - rawX1) / 256.0;
		double dy = (rawY2 - rawY1) / 512.0;
		return Math.Sqrt(dx * dx + dy * dy);
	}
}

public sealed record RuntimeEntityLocation(int Index, int Handle, int RawX, int RawY, int Hp, int MaxHp, string Name, double DistanceToAnchor) {
	public static RuntimeEntityLocation Empty { get; } = new(-1, 0, 0, 0, 0, 0, "", double.MaxValue);
}
