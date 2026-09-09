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

	public string TrainingMap { get; set; } = "Đồng Quan";

	public int TrainingMapId { get; set; } = 14;

	public string TrainingMonster { get; set; } = "Cổ Điêu";

	public string TrainingCoordinate { get; set; } = "164/221";

	public int TrainingRawX { get; set; } = 42011;

	public int TrainingRawY { get; set; } = 113653;

	// Bộ lọc mục tiêu theo quái thủ lĩnh/boss, port từ AutoFS (VectorFactory.cs:3345-3432). Hai cờ loại trừ nhau.
	// DoNotAttackBoss còn kiêm luôn công tắc cơ chế NÉ (vùng cấm + chủ động lùi) — chốt với chủ dự án 2026-09-07,
	// cố ý rộng hơn AutoFS vốn chỉ lọc mục tiêu chứ không né.
	public bool OnlyAttackBoss { get; set; }
	public bool DoNotAttackBoss { get; set; } = true;

	// Bán kính vùng cấm quanh thủ lĩnh/boss, raw trục X cùng thang với Range (1 ô ≈ 256 raw); trục Y đã được quy đổi
	// trong EliteAvoidance.Distance. <= 0 nghĩa là tắt né nhưng vẫn lọc mục tiêu.
	public int EliteAvoidRadius { get; set; } = 500;

	public int Range { get; set; } = 2000;

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
