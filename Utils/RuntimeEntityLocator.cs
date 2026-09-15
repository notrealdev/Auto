namespace Auto.Utils;

using System.Globalization;
using System.Text;

public static class RuntimeEntityLocator {
	private const int GlobalEntityTableOffset = GameAddresses.Globals.EntityTable;
	private const int EntityStride = GameAddresses.Entity.Stride;
	// Dùng chung biên với luồng đánh (GameAddresses.Entity), không tự định nghĩa lại.
	private const int FirstEntityIndex = GameAddresses.Entity.FirstScanIndex;
	private const int LastEntityIndex = GameAddresses.Entity.LastScanIndex;
	// Số tên gần đích nhất đem ra in khi tìm không thấy, đủ để biết bảng entity có đọc được không.
	private const int DiagnosticNameCount = 5;
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
			int readableEntityCount = 0;
			RuntimeEntityLocation positionlessMatch = RuntimeEntityLocation.Empty;
			int positionlessMatchCount = 0;
			List<(double Distance, int Index, string Name)> nearbyNames = [];
			for (int index = FirstEntityIndex; index <= LastEntityIndex; index++) {
				long entityAddressValue = tableBaseValue + (long)index * EntityStride;
				if (entityAddressValue < MinimumLikelyAddress || entityAddressValue > MaximumUserModeAddress) continue;
				IntPtr entityAddress = new((int)entityAddressValue);
				byte[] nameBytes = reader.ReadBytes(IntPtr.Add(entityAddress, NameOffset), NameLength);
				int nameLength = nameBytes.Length == NameLength ? Array.IndexOf(nameBytes, (byte)0) : 0;
				if (nameLength < 0) nameLength = NameLength;
				if (nameLength == 0) continue;
				string name = LegacyVietnameseText.Decode(nameBytes.AsSpan(0, nameLength).ToArray()).Trim();
				byte[] position = reader.ReadBytes(IntPtr.Add(entityAddress, PositionOffset), 8);
				if (position.Length < 8) continue;
				int rawX = ReadInt32(position, 0);
				int rawY = ReadInt32(position, 4);
				// Entity KHÔNG có toạ độ vẫn phải được xét tên. AutoFS tìm NPC theo tên chỉ đọc đúng trường tên,
				// không đọc toạ độ và không có bộ lọc nào theo toạ độ (WindowQueue.cs:24241-24270: vòng
				// "for (int k = 2; k < 256; k++)" chỉ gọi DisposeNode(..., O_Name, 30) rồi so Contains).
				// Bản cũ ở đây "continue" ngay khi rawX/rawY <= 0, nên nếu NPC nằm trong nhóm đó thì nó vô hình
				// với luồng Sửa đồ mà log không để lại dấu vết — đúng nhóm chưa loại trừ được của sự cố PID=22824.
				if (rawX <= 0 || rawY <= 0) {
					if (! NormalizeName(name).Contains(normalizedExpectedName, StringComparison.OrdinalIgnoreCase)) continue;
					positionlessMatchCount++;
					if (positionlessMatch.Index < 0) {
						int positionlessHandle = reader.ReadInt32(IntPtr.Add(entityAddress, HandleOffset));
						positionlessMatch = new RuntimeEntityLocation(index, positionlessHandle, 0, 0, 0, 0, name, double.MaxValue);
					}
					continue;
				}
				readableEntityCount++;
				double distance = GetDistance(rawX, rawY, targetRawX, targetRawY);
				// Gom mọi tên đọc được kèm khoảng cách, để lúc tìm không thấy còn phân biệt được "bảng entity đọc hỏng"
				// với "NPC vẫn ở đó nhưng mang tên khác". Lọc lấy vài con gần nhất ở dưới.
				nearbyNames.Add((distance, index, name));
				if (!NormalizeName(name).Contains(normalizedExpectedName, StringComparison.OrdinalIgnoreCase)) continue;
				namedEntityCount++;
				int handle = reader.ReadInt32(IntPtr.Add(entityAddress, HandleOffset));
				byte[] hp = reader.ReadBytes(IntPtr.Add(entityAddress, HpOffset), 8);
				int currentHp = hp.Length >= 8 ? ReadInt32(hp, 0) : 0;
				int maximumHp = hp.Length >= 8 ? ReadInt32(hp, 4) : 0;
				RuntimeEntityLocation candidate = new(index, handle, rawX, rawY, currentHp, maximumHp, name, distance);
				if (distance >= bestDistance) continue;
				bestDistance = distance;
				location = candidate;
			}

			if (location.Index >= 0) return true;
			// Không con nào khớp tên mà có toạ độ, nhưng có con khớp tên KHÔNG toạ độ thì vẫn trả về: nó vẫn click
			// được bằng lệnh theo index giống AutoFS, chỉ là không click được theo toạ độ.
			if (positionlessMatch.Index >= 0) {
				location = positionlessMatch;
				return true;
			}
			string nearest = string.Join(", ", nearbyNames.OrderBy(entry => entry.Distance).Take(DiagnosticNameCount).Select(entry => $"#{entry.Index}:'{entry.Name}'@{entry.Distance:F1}"));
			reason = $"Chưa thấy entity có tên '{expectedName}' | Quét index {FirstEntityIndex}..{LastEntityIndex} | ĐọcĐược={readableEntityCount} | TênKhớp={namedEntityCount} | KhớpTênKhôngToạĐộ={positionlessMatchCount} | GầnĐíchNhất=[{nearest}]";
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
