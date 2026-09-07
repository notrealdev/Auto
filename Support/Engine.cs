namespace Auto.Support;

using Auto.Attack;
using Auto.Utils;

internal sealed class Engine {
	private const int CastSkillCommand = 85;
	private const int HealSkillId = 45;
	private const int HealRepeatDelayMilliseconds = 500;
	private const int MaximumCastRejectionsBeforeRelease = 3;
	private readonly Settings settings;
	private readonly AutoFsAttackTransport transport;
	private DateTime nextOwnerHealUtc;
	private DateTime nextPetHealUtc;
	private bool ownerHealingActive;
	private bool petHealingActive;
	private bool petHealthUnavailableLogged;
	private string lastCastRejectionLine = "";
	private int castRejectionStreak;

	// Binds one account support instance to the shared transport.
	public Engine(Settings settings, AutoFsAttackTransport transport) {
		this.settings = settings;
		this.transport = transport;
	}

	// Holds exclusive control from the configured threshold until the selected target reaches full HP.
	public bool Tick(int processId, IntPtr gameWindow, GameSnapshot ownerSnapshot, bool attackEnabled, Action<string> log) {
		if (!attackEnabled || (!settings.HealOwner && !settings.HealPet)) {
			Reset();
			return false;
		}
		try {
			if (!settings.HealOwner) {
				ownerHealingActive = false;
				nextOwnerHealUtc = DateTime.MinValue;
			}
			if (settings.HealOwner) {
				if (!ownerSnapshot.Success || ownerSnapshot.Hp < 0 || ownerSnapshot.MaxHp <= 0 || ownerSnapshot.Hp > ownerSnapshot.MaxHp) return ownerHealingActive;
				int currentHp = ownerSnapshot.Hp;
				int maximumHp = ownerSnapshot.MaxHp;
				if (currentHp >= maximumHp) {
					ownerHealingActive = false;
					nextOwnerHealUtc = DateTime.MinValue;
				} else if (!ownerHealingActive && (long)currentHp * 100 <= (long)maximumHp * settings.HealOwnerHpPercent) {
					ownerHealingActive = true;
				}
				if (ownerHealingActive) {
					if (DateTime.UtcNow < nextOwnerHealUtc) return true;
					// Đọc lại HP tươi ngay trước khi cast để tránh gửi thừa khi snapshot đầu tick đã cũ so với lần heal trước.
					int freshHp = GameMemory.ReadHp(processId);
					if (freshHp >= 0 && freshHp >= maximumHp) {
						ownerHealingActive = false;
						nextOwnerHealUtc = DateTime.MinValue;
						return false;
					}
					nextOwnerHealUtc = DateTime.UtcNow.AddMilliseconds(HealRepeatDelayMilliseconds);
					// Chuyển sang lệnh có xác nhận: PostMessage chỉ chứng minh đã đẩy vào hàng đợi, không chứng minh
					// native chấp nhận, nên khi cast không có tác dụng thì log cũ không phân biệt được hỏng ở đâu.
					if (!transport.TrySendConfirmedCommand(gameWindow, CastSkillCommand, HealSkillId, out string ownerCastError)) {
						return HoldOrReleaseAfterRejection(log, "Owner", ownerCastError);
					}
					lastCastRejectionLine = "";
					castRejectionStreak = 0;
					log($"BUFF_CAST_CONFIRMED | Target=Owner | SkillId={HealSkillId} | HP={currentHp}/{maximumHp} | HpPercent={currentHp * 100 / maximumHp} | ThresholdPercent={settings.HealOwnerHpPercent} | Exclusive=True | Transport=Direct | Source=DEV_RUNTIME_LAYOUT | RepeatDelayMs={HealRepeatDelayMilliseconds}");
					return true;
				}
			}

			if (!settings.HealPet) {
				petHealingActive = false;
				nextPetHealUtc = DateTime.MinValue;
				petHealthUnavailableLogged = false;
				return false;
			}

			PetHealthReading pet = PetHealthReader.Read(processId);
			if (!pet.Success || !pet.Present) {
				if (!petHealthUnavailableLogged) log($"BUFF_PET_HEALTH_SOURCE_UNAVAILABLE | Success={pet.Success} | Present={pet.Present} | HealingActive={petHealingActive} | Detail={pet.Detail}");
				petHealthUnavailableLogged = true;
				return petHealingActive;
			}
			if (petHealthUnavailableLogged) log($"BUFF_PET_HEALTH_SOURCE_RECOVERED | EntityIndex={pet.EntityIndex} | HP={pet.CurrentHp}/{pet.MaximumHp} | HealingActive={petHealingActive} | Detail={pet.Detail}");
			petHealthUnavailableLogged = false;
			if (pet.CurrentHp <= 0) {
				petHealingActive = false;
				nextPetHealUtc = DateTime.MinValue;
				return false;
			}
			if (pet.CurrentHp >= pet.MaximumHp) {
				petHealingActive = false;
				nextPetHealUtc = DateTime.MinValue;
				return false;
			}
			if (!petHealingActive && (long)pet.CurrentHp * 100 <= (long)pet.MaximumHp * settings.HealPetHpPercent) petHealingActive = true;
			if (!petHealingActive) return false;
			if (DateTime.UtcNow < nextPetHealUtc) return true;

			nextPetHealUtc = DateTime.UtcNow.AddMilliseconds(HealRepeatDelayMilliseconds);
			if (!transport.TrySendConfirmedCommand(gameWindow, CastSkillCommand, HealSkillId, out string petCastError)) {
				return HoldOrReleaseAfterRejection(log, "Pet", petCastError);
			}
			lastCastRejectionLine = "";
			castRejectionStreak = 0;
			log($"BUFF_CAST_CONFIRMED | Target=Pet | EntityIndex={pet.EntityIndex} | SkillId={HealSkillId} | HP={pet.CurrentHp}/{pet.MaximumHp} | HpPercent={pet.CurrentHp * 100 / pet.MaximumHp} | ThresholdPercent={settings.HealPetHpPercent} | Exclusive=True | Transport=Direct | Source=CURRENT_ENTITY_TYPE_6 | RepeatDelayMs={HealRepeatDelayMilliseconds}");
			return true;
		} catch {
			return ownerHealingActive || petHealingActive;
		}
	}

	// Khi native liên tục từ chối lệnh cast thì HP không bao giờ hồi, mà khối heal lại giữ quyền điều khiển độc quyền
	// nên Auto Đánh và Auto Nhặt bị dừng vĩnh viễn và nhân vật đứng im. Sau vài lần từ chối liên tiếp thì nhả quyền
	// điều khiển để các luồng khác chạy tiếp, vẫn giữ nguyên việc thử lại theo nhịp cũ.
	private bool HoldOrReleaseAfterRejection(Action<string> log, string target, string error) {
		LogCastRejected(log, target, error);
		castRejectionStreak++;
		if (castRejectionStreak < MaximumCastRejectionsBeforeRelease) return true;
		if (castRejectionStreak == MaximumCastRejectionsBeforeRelease) {
			log($"BUFF_CAST_RELEASED_CONTROL | Target={target} | Rejections={castRejectionStreak} | Action=Nhả quyền điều khiển để Auto Đánh và Auto Nhặt chạy tiếp");
		}
		return false;
	}

	// Lệnh cast lặp mỗi 500 ms nên chỉ ghi khi nội dung lỗi đổi, tránh ngập log.
	private void LogCastRejected(Action<string> log, string target, string error) {
		string line = $"BUFF_CAST_REJECTED | Target={target} | SkillId={HealSkillId} | Command={CastSkillCommand} | Error={error}";
		if (string.Equals(lastCastRejectionLine, line, StringComparison.Ordinal)) return;
		lastCastRejectionLine = line;
		log(line);
	}

	// Clears all exclusive healing state when support automation is disabled.
	private void Reset() {
		ownerHealingActive = false;
		petHealingActive = false;
		nextOwnerHealUtc = DateTime.MinValue;
		nextPetHealUtc = DateTime.MinValue;
		petHealthUnavailableLogged = false;
	}
}
