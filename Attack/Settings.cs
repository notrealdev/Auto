namespace Auto.Attack;

public sealed class Settings {
	public bool Enabled { get; set; }

	public bool EnableReturnToTraining { get; set; } = true;

	public bool OnlySelectedMonster { get; set; }

	public bool OnlySelectedPlayer { get; set; }

	public bool AttackMonsters { get; set; } = true;

	public bool AttackPlayers { get; set; }

	public bool PrioritizeSummonerPet { get; set; }

	public bool PrioritizeLowHp { get; set; }

	public int PrioritizeLowHpPercent { get; set; } = 50;

	public bool PrioritizeClass { get; set; }

	public bool PrioritizeTaoist { get; set; }

	public bool PrioritizeSummoner { get; set; }

	public bool PrioritizeWarrior { get; set; }

	public bool ExcludeCamelTransform { get; set; } = true;

	public string SelectedMonsterName { get; set; } = "";

	public string SelectedMonsterSignature { get; set; } = "";

	public string SelectedPlayerName { get; set; } = "";

	public string SelectedPlayerSignature { get; set; } = "";

	public bool ContinueEnabled { get; set; }

	public string ContinueMap { get; set; } = "Du Hồn";

	public int ContinueMapId { get; set; } = 11;

	public string ContinueMonster { get; set; } = "Hỏa Diện";

	public string ContinueCoordinate { get; set; } = "229/223";

	public int ContinueRawX { get; set; } = 58816;

	public int ContinueRawY { get; set; } = 114656;

	public bool TeachingEnabled { get; set; }

	public string TeachingMap { get; set; } = "Đồng Quan";

	public string TeachingMonster { get; set; } = "Cổ Điêu";

	public string TeachingCoordinate { get; set; } = "164/221";

	public int TeachingRawX { get; set; } = 42011;

	public int TeachingRawY { get; set; } = 113653;

	public int TeachingMapId { get; set; } = 14;

	public bool TrainingEnabled { get; set; }

	public string TrainingGroup { get; set; } = "Hoang Mạc";

	// SỬA 2026-09-22: sáu giá trị dưới đây trước là bản copy y nguyên của khối Teaching* ("Đồng Quan"/"Cổ Điêu"/
	// "164/221"/42011/113653/14) — nhưng Đồng Quan thuộc nhóm THÀNH THỊ (TrainingLocationCatalog.CityMaps), không
	// nằm trong nhóm Mê cung "Hoang Mạc" ở dòng trên, nên bộ mặc định tự mâu thuẫn.
	//
	// Hệ quả đã thấy: mỗi lần dựng giao diện, RepopulateTrainingMaps phải chữa lại thành map hợp lệ đầu tiên của
	// nhóm, rồi giá trị đã chữa đó bị lưu xuống Profiles.json. Nên file luôn hiện "TrainingMap": "Hoang Mạc" ở MỌI
	// nhân vật dù không ai bật chế độ Mê cung (TrainingEnabled=false), làm người đọc file tưởng đó là bãi đang train.
	//
	// Giá trị mới lấy từ chính Data/ToaDo/ToaDoQuai.map: mục "[Hoang Mạc]" có quái đầu là "Sa Hồn=48055,108397".
	// Quy đổi hiển thị 48055/256=187, 108397/512=211 -> "187/211", khớp đúng giá trị mà giao diện tự sinh ra.
	public string TrainingMap { get; set; } = "Hoang Mạc";

	public int TrainingMapId { get; set; } = 22;

	public string TrainingMonster { get; set; } = "Sa Hồn";

	public string TrainingCoordinate { get; set; } = "187/211";

	public int TrainingRawX { get; set; } = 48055;

	public int TrainingRawY { get; set; } = 108397;

	// Bộ lọc mục tiêu theo quái thủ lĩnh/boss, port từ AutoFS (VectorFactory.cs:3345-3432). Hai cờ loại trừ nhau.
	// DoNotAttackBoss còn kiêm luôn công tắc cơ chế NÉ (vùng cấm + chủ động lùi) — chốt với chủ dự án 2026-09-07,
	// cố ý rộng hơn AutoFS vốn chỉ lọc mục tiêu chứ không né.
	public bool OnlyAttackBoss { get; set; }
	public bool DoNotAttackBoss { get; set; } = true;

	// Bán kính vùng cấm quanh thủ lĩnh/boss, raw trục X cùng thang với Range (1 ô ≈ 256 raw); trục Y đã được quy đổi
	// trong EliteAvoidance.Distance. <= 0 nghĩa là tắt né nhưng vẫn lọc mục tiêu.
	public int EliteAvoidRadius { get; set; } = 500;

	// Bán kính NHÂN VẬT phải chạy ra khỏi, tách riêng khỏi EliteAvoidRadius vì hai con số phục vụ hai việc khác nhau:
	// EliteAvoidRadius lọc quái thường quanh boss (AutoFsEntityScanner.cs:145), nâng nó lên thì 3 con boss quanh tâm
	// bãi quét sạch quái và Auto phải chọn quái xa. Con số dưới đây chỉ dùng cho chính nhân vật.
	//
	// 768 raw = 3 ô, chủ dự án chốt 2026-09-11 ("tránh boss tối thiểu là 3 ô thay vì 2 ô").
	//
	// Đã thử 1280 (5 ô) và PHẢI hạ xuống: 4 góc trốn nằm ở tâm ± Range/3, quy đổi trục Y thì chỉ cách tâm
	// 666*1,118 = 745 raw — nhỏ hơn 1280 — nên không góc nào sạch và nhân vật đứng chết một chỗ. Bằng chứng
	// (movement.log 2026-09-11, PID=34032): ELITE_NO_SAFE_CORNER lúc 14:50:38 / 14:50:48 / 14:50:58 / 14:51:09 đều
	// ghi đúng một toạ độ Player=63631/90321.
	// Với 768 thì retreatStep = max(666, 768) = 768, góc cách tâm 768*1,118 = 858 > 768 nên luôn còn lối thoát.
	// Đổi số này phải kiểm lại bất đẳng thức đó, nếu không lỗi đứng im quay lại ngay.
	public int ElitePlayerRetreatRadius { get; set; } = 768;

	public int Range { get; set; } = 3000;

	public int CenterX { get; set; }

	public int CenterY { get; set; }

	// Map ghi nhận lúc đặt tâm bãi. Không có trường này thì luồng lên bãi không biết tâm thuộc map nào,
	// nên đứng ở map khác vẫn đem toạ độ ra trừ nhau và không thể đi xuyên map về đúng bãi.
	public int CenterMapId { get; set; }

	public bool UseCenterPosition { get; set; } = true;

	public int MinHpPercent { get; set; } = 30;

	public int MinMpPercent { get; set; } = 10;

	public Mode Mode { get; set; } = Mode.FarmAroundPoint;
}
