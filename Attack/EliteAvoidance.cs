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

	// Số hướng quét thử khi tìm đích trốn. 16 hướng = mỗi 22,5 độ, đủ mịn để không bỏ sót khe giữa ba boss mà vẫn
	// chỉ là 16 phép tính số học cho mỗi lượt cần trốn (không đọc bộ nhớ, không gửi lệnh nào).
	private const int RetreatDirectionSamples = 16;

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
	// Chỉ còn vòng đi tuần khi hết quái dùng tới. Luồng tránh boss ĐÃ BỎ 4 góc, xem TryGetRetreatDestination.
	public static (int X, int Y) GetCorner(int centerX, int centerY, int step, int corner) {
		return (centerX + (corner is 1 or 3 ? step : -step), centerY + (corner is 1 or 2 ? step : -step));
	}

	// Khoảng cách ngắn nhất từ một điểm tới ĐOẠN THẲNG, trong hệ đã quy đổi Y.
	private static double SegmentDistance(int pointX, int pointY, int fromX, int fromY, int toX, int toY) {
		double px = pointX, py = pointY * RawYToRawXScale;
		double ax = fromX, ay = fromY * RawYToRawXScale;
		double vx = toX - ax, vy = toY * RawYToRawXScale - ay;
		double lengthSquared = vx * vx + vy * vy;
		double t = lengthSquared > 0 ? ((px - ax) * vx + (py - ay) * vy) / lengthSquared : 0;
		if (t < 0) t = 0;
		else if (t > 1) t = 1;
		double closestX = ax + vx * t, closestY = ay + vy * t;
		return Math.Sqrt((px - closestX) * (px - closestX) + (py - closestY) * (py - closestY));
	}

	// Chỗ SÁT THỦ LĨNH NHẤT trên cả quãng đường đi, không phải chỉ ở điểm đích. Đây là thứ phân biệt "chạy ra xa"
	// với "chạy vòng qua ngay trước mặt boss rồi dừng ở chỗ thoáng bên kia".
	public static double NearestEliteAlongPath(int fromX, int fromY, int toX, int toY, IReadOnlyList<(int X, int Y)> elites) {
		double nearest = double.PositiveInfinity;
		foreach ((int eliteX, int eliteY) in elites) nearest = Math.Min(nearest, SegmentDistance(eliteX, eliteY, fromX, fromY, toX, toY));
		return nearest;
	}

	// Khoảng cách tới thủ lĩnh gần nhất; +vô cùng khi danh sách rỗng.
	public static double NearestEliteDistance(int x, int y, IReadOnlyList<(int X, int Y)> elites) {
		double nearest = double.PositiveInfinity;
		foreach ((int eliteX, int eliteY) in elites) nearest = Math.Min(nearest, Distance(x, y, eliteX, eliteY));
		return nearest;
	}

	// Điểm chạy trốn tính THEO KHOẢNG CÁCH, không phải 4 góc cố định quanh tâm bãi (chủ dự án chốt 2026-09-11).
	//
	// Vì sao bỏ 4 góc: chúng nằm ở tâm ± Range/3 nên chỉ cách tâm 745 raw, hoàn toàn không liên quan tới chỗ boss
	// đang đứng. "Chạy trốn" hoá ra là đi tới 1 trong 4 chỗ có sẵn, có thể còn gần boss hơn chỗ đang đứng, và khi
	// boss đứng gần tâm thì cả 4 đều bẩn nên nhân vật đứng chết (movement.log 2026-09-11 PID=34032, bốn dòng
	// ELITE_NO_SAFE_CORNER cùng một toạ độ Player=63631/90321).
	//
	// Luật mới: đẩy nhân vật ra tới khi khoảng cách tới MỌI thủ lĩnh vượt radius + margin. Hướng đẩy là tổng vector
	// đẩy từ từng con trong bán kính, có trọng số theo độ gần — con càng sát càng đẩy mạnh — nên khi bị nhiều con
	// vây thì đi ra khe giữa chúng chứ không chạy thẳng vào con thứ hai.
	// Boss đuổi theo thì mỗi lượt quét lại cho ra đích mới xa hơn, nhân vật chạy tiếp, không có trạng thái "đứng im".
	//
	// Toàn bộ tính trong hệ đã quy đổi Y (RawYToRawXScale) cho tròn đều theo ô, rồi mới đổi ngược về raw.
	public static bool TryGetRetreatDestination(int playerX, int playerY, int centerX, int centerY, int range,
		IReadOnlyList<(int X, int Y)> elites, int radius, int margin, out int destinationX, out int destinationY) {
		destinationX = 0;
		destinationY = 0;
		if (radius <= 0 || elites.Count == 0) return false;

		double playerScaledY = playerY * RawYToRawXScale;
		double pushX = 0;
		double pushY = 0;
		double nearest = double.PositiveInfinity;
		foreach ((int eliteX, int eliteY) in elites) {
			double deltaX = playerX - (double)eliteX;
			double deltaY = playerScaledY - eliteY * RawYToRawXScale;
			double distance = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
			nearest = Math.Min(nearest, distance);
			if (distance > radius) continue;
			// Đứng trùng toạ độ boss thì không có hướng nào để lấy; đẩy sang +X cho có một hướng xác định.
			if (distance < 1) {
				pushX += 1;
				continue;
			}
			double weight = (radius - distance) / radius;
			pushX += deltaX / distance * weight;
			pushY += deltaY / distance * weight;
		}

		double needed = radius + margin - nearest;
		if (needed < margin) needed = margin;

		// CHẤM ĐIỂM NHIỀU HƯỚNG thay vì tin một hướng đẩy duy nhất (chủ dự án báo 2026-09-15: ba boss đứng ba góc,
		// nhân vật loay hoay trong đó không thoát ra).
		//
		// Hai lỗi của bản chỉ-một-hướng, đo được trên Release/Diagnostics/movement.log phiên 10 tiếng 2026-09-15:
		//   1. Ba boss ở ba hướng thì các vector đẩy TRIỆT TIÊU nhau, tổng còn lại chỉ là phần dư nhiễu. Bản cũ chỉ
		//      chặn triệt tiêu tuyệt đối (pushLength < 1e-6), gần-triệt-tiêu vẫn lọt và cho ra hướng vô nghĩa.
		//   2. Đích không bao giờ được kiểm lại. needed tính từ ĐÚNG MỘT con gần nhất, đi theo ĐÚNG MỘT hướng, và
		//      bước kéo về trong bãi bên dưới còn có thể lôi đích NGƯỢC vào vùng cấm mà không ai kiểm.
		// Hậu quả đo được: 3276/12277 lệnh trốn (26,7%) có đích mà tới nơi VẪN nằm trong vùng cấm; riêng 770 lần
		// trốn dài từ 5 lệnh trở lên thì tỉ lệ đó là 31,7% — càng loay hoay lâu càng do đích chọn sai.
		//
		// Luật mới: thử hướng đẩy tổng hợp CỘNG một vòng quét đều, mọi ứng viên cùng độ dài needed, rồi chọn chỗ
		// CÁCH XA THỦ LĨNH GẦN NHẤT NHẤT — chấm điểm SAU khi đã kéo về trong bãi, vì chỗ kéo về mới là chỗ thật sự
		// tới. Bị vây kín thì không còn "đứng im": vẫn chọn được khe rộng nhất để lách ra.
		// CHẤM ĐIỂM THEO CẢ ĐƯỜNG ĐI, KHÔNG PHẢI CHỈ Ở ĐÍCH — sửa lỗi do chính bản quét 16 hướng ở trên gây ra.
		//
		// Bản đầu của tôi chỉ chấm NearestEliteDistance tại điểm đích. Một chỗ nằm BÊN KIA boss vẫn "thoáng" nên
		// được chọn, mà đường thẳng tới đó xuyên qua boss. Bản một-hướng cũ không dính lỗi này vì nó luôn chạy đúng
		// hướng ra xa; thêm 16 hướng vào mà quên xét đường đi là tôi tự mở ra lỗi mới.
		//
		// Đo trên Release/Diagnostics/movement.log ngày 2026-09-15, 819 lệnh trốn có đủ dữ liệu:
		//   chạy thẳng ra xa boss = 451 (55,1%) | chạy chéo vẫn ra xa = 235 (28,7%) | CHẠY VỀ PHÍA BOSS = 133 (16,2%)
		//   và 106/814 (13,0%) lần trốn có lúc GẦN BOSS HƠN cả lúc xuất phát
		// Đúng hiện tượng chủ dự án báo: "tránh boss mà cứ đi qua đi lại, đi xuyên qua boss".
		//
		// Điểm chính = chỗ SÁT BOSS NHẤT TRÊN CẢ ĐOẠN ĐƯỜNG. Hướng nào cắt ngang qua boss thì điểm tụt hẳn, không
		// bao giờ thắng nữa. Hoà điểm thì mới xét tới độ thoáng ở đích.
		// Lưu ý: nhân vật XUẤT PHÁT từ trong vùng cấm nên điểm đường đi luôn bị chặn trên bởi khoảng cách hiện tại —
		// đó chính là điều mong muốn: hướng nào không tiến lại gần boss thì giữ nguyên mức đó và cùng thắng.
		const double ScoreTieEpsilon = 1.0;
		bool bestClean = false;
		bool bestInsideField = false;
		double bestPathScore = double.NegativeInfinity;
		double bestEndScore = double.NegativeInfinity;
		int bestX = 0;
		int bestY = 0;
		bool found = false;

		// Thứ tự ưu tiên, xét lần lượt:
		//   1. THOÁT ĐƯỢC   — tới nơi đứng ngoài vùng cấm VÀ đường đi không tiến lại gần boss hơn hiện tại
		//   2. Ở TRONG BÃI  — chỉ so khi cả hai cùng thoát được; an toàn tính mạng đứng trên việc bám bãi
		//   3. đường đi thoáng hơn, rồi tới đích thoáng hơn
		void Evaluate(int candidateX, int candidateY, bool insideField) {
			if (candidateX <= 0 || candidateY <= 0) return;
			double pathScore = NearestEliteAlongPath(playerX, playerY, candidateX, candidateY, elites);
			double endScore = NearestEliteDistance(candidateX, candidateY, elites);
			bool clean = endScore >= radius && pathScore >= nearest - ScoreTieEpsilon;

			bool better;
			if (clean != bestClean) better = clean;
			else if (clean && insideField != bestInsideField) better = insideField;
			else if (pathScore > bestPathScore + ScoreTieEpsilon) better = true;
			else if (pathScore < bestPathScore - ScoreTieEpsilon) better = false;
			else better = endScore > bestEndScore;
			if (! better) return;

			bestClean = clean;
			bestInsideField = insideField;
			bestPathScore = pathScore;
			bestEndScore = endScore;
			bestX = candidateX;
			bestY = candidateY;
			found = true;
		}

		void Consider(double directionX, double directionY) {
			double length = Math.Sqrt(directionX * directionX + directionY * directionY);
			if (length < 1e-6) return;
			double rawX = playerX + directionX / length * needed;
			double rawY = (playerScaledY + directionY / length * needed) / RawYToRawXScale;
			double driftX = rawX - centerX;
			double driftY = rawY - centerY;
			double drift = Math.Sqrt(driftX * driftX + driftY * driftY);
			bool wouldLeaveField = range > 0 && drift > range;

			// KHÔNG kéo về bãi một cách vô điều kiện nữa.
			//
			// Bản cũ luôn kéo đích về trong bán kính Range quanh tâm. Khi boss đứng giữa nhân vật và tâm bãi, chỗ
			// kéo về nằm BÊN KIA boss, nên "chạy trốn" hoá ra chạy xuyên qua boss; còn khi cả bãi đều bẩn thì đích
			// bị kéo về trùng đúng chỗ đang đứng và nhân vật kẹt vĩnh viễn (mô phỏng 2026-09-15: hai ca "bãi hẹp"
			// và "nhân vật ở rìa bãi" đều không thoát nổi trong 10 bước).
			//
			// Giờ chào cả hai phương án rồi để bảng ưu tiên ở Evaluate quyết: ở trong bãi vẫn được ưu tiên, nhưng
			// chỉ khi nó cũng thoát được. Bãi bẩn hết thì chấp nhận chạy tạm ra ngoài — luồng Tự lên bãi sẵn có sẽ
			// kéo nhân vật về sau, còn đứng chết cạnh boss thì không có đường cứu.
			if (wouldLeaveField) {
				double shrink = range / drift;
				Evaluate((int)Math.Round(centerX + driftX * shrink), (int)Math.Round(centerY + driftY * shrink), true);
			}
			Evaluate((int)Math.Round(rawX), (int)Math.Round(rawY), ! wouldLeaveField);
		}

		// Hướng đẩy tổng hợp đi trước: ca một boss thì nó chính là hướng chạy thẳng ra, giữ nguyên hành vi cũ.
		Consider(pushX, pushY);
		for (int sample = 0; sample < RetreatDirectionSamples; sample++) {
			double angle = Math.PI * 2 * sample / RetreatDirectionSamples;
			Consider(Math.Cos(angle), Math.Sin(angle));
		}

		if (! found) return false;
		destinationX = bestX;
		destinationY = bestY;
		return true;
	}
}
