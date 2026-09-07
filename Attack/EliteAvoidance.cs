namespace Auto.Attack;

// Nhận diện quái thủ lĩnh (quái xanh) và boss.
//
// AutoFS gộp chung hai loại này làm MỘT khái niệm "Boss" và nhận diện HOÀN TOÀN bằng tên. Grep bản decompile
// D:\G\DEV\Resource\AutoSource\AutoProV2\VectorFactory.cs cho đúng một công thức, lặp lại ở các dòng
// 117, 3356, 3717, 4899:
//     Tộc == "Quái" && ( Name.Contains("<c=") || Name.Contains("(") || Name.Contains("=") )
// AutoFS KHÔNG dùng thanh máu để nhận diện: grep MaxHp|HpMax|MaxHP toàn AutoSource cho 0 kết quả, và
// StreamAttribute của nó cũng không có offset loại/rank/màu nào.
//
// Ở đây cố tình BỎ marker "=" đứng một mình: nó quá rộng, một tên quái thường có chứa "=" cũng dính.
// Giữ lại hai marker có nghĩa rõ ràng:
//   "("   quái thủ lĩnh, ví dụ "Sa Hồn ( cuồng )", "Thiết Trùng ( Tốc )"
//   "<c=" tag màu mà game nhúng vào tên để tô màu
public static class EliteAvoidance {
	private const byte OpenParenthesis = 0x28;                          // '('
	private static readonly byte[] ColorTagMarker = [0x3C, 0x63, 0x3D]; // "<c="

	// Dò trên BYTE THÔ chứ không trên chuỗi đã giải mã: cả hai marker đều là ASCII nên cách này không phụ thuộc
	// bảng mã tiếng Việt cũ, và không sợ bước giải mã làm hỏng ký tự.
	public static bool IsEliteName(ReadOnlySpan<byte> nameBytes) {
		for (int index = 0; index < nameBytes.Length; index++) {
			if (nameBytes[index] == OpenParenthesis) return true;
			if (index + ColorTagMarker.Length > nameBytes.Length) continue;
			if (nameBytes.Slice(index, ColorTagMarker.Length).SequenceEqual(ColorTagMarker)) return true;
		}
		return false;
	}

	// 1 ô hiển thị = 256 raw trục X nhưng 512 raw trục Y. Không quy đổi thì "bán kính 500" hoá ra hình elip dẹt:
	// gần 2 ô theo chiều ngang nhưng chưa tới 1 ô theo chiều dọc. Quy Y về đơn vị X rồi mới Euclid để vùng cấm
	// tròn đều theo ô. Cố tình KHÁC AutoFsEntityScanner.GetMapDistance (Euclid thô, bám AutoFS) mà Range đang dùng:
	// đây là metric mới của tính năng này, AutoFS không có tính năng tương ứng nên không có gì để lệch.
	public const double RawYToRawXScale = 0.5;

	public static double Distance(int firstX, int firstY, int secondX, int secondY) {
		double deltaX = (double)firstX - secondX;
		double deltaY = ((double)firstY - secondY) * RawYToRawXScale;
		return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
	}

	public static bool IsNearAnyElite(int x, int y, IReadOnlyList<(int X, int Y)> elites, int radius) {
		if (radius <= 0) return false;
		foreach ((int eliteX, int eliteY) in elites) {
			if (Distance(x, y, eliteX, eliteY) <= radius) return true;
		}
		return false;
	}

	// Thứ tự góc BẮT BUỘC khớp Engine.TryWanderTrainingCorners: 0=(-,-) 1=(+,+) 2=(-,+) 3=(+,-).
	public static (int X, int Y) GetCorner(int centerX, int centerY, int step, int corner) {
		return (centerX + (corner is 1 or 3 ? step : -step), centerY + (corner is 1 or 2 ? step : -step));
	}

	// Trả về góc (0..3) có khoảng hở tới thủ lĩnh gần nhất là lớn nhất; -1 khi MỌI góc đều nằm trong vùng cấm.
	// Không có thủ lĩnh nào thì mọi góc đều hở vô hạn nên trả về góc 0.
	public static int PickSafestCorner(int centerX, int centerY, int step, IReadOnlyList<(int X, int Y)> elites, int radius) {
		int best = -1;
		double bestClearance = double.NegativeInfinity;
		for (int corner = 0; corner < 4; corner++) {
			(int cornerX, int cornerY) = GetCorner(centerX, centerY, step, corner);
			double clearance = double.PositiveInfinity;
			foreach ((int eliteX, int eliteY) in elites) clearance = Math.Min(clearance, Distance(cornerX, cornerY, eliteX, eliteY));
			if (clearance <= radius || clearance <= bestClearance) continue;
			bestClearance = clearance;
			best = corner;
		}
		return best;
	}
}
