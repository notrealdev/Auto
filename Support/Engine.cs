namespace Auto.Support;

using Auto.Attack;
using Auto.Utils;

internal sealed class Engine {
	private const int CastSkillCommand = 85;
	private const int HealSkillId = 45;
	private const int HealRepeatDelayMilliseconds = 500;
	private readonly Settings settings;
	private readonly AutoFsAttackTransport transport;
	private DateTime nextOwnerHealUtc;
	private DateTime nextPetHealUtc;
	private bool ownerHealingActive;
	private bool petHealingActive;
	private bool petHealthUnavailableLogged;

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
					if (!transport.TrySendTestCommand(gameWindow, CastSkillCommand, HealSkillId, out _)) return true;
					log($"BUFF_CAST_POSTED | Target=Owner | SkillId={HealSkillId} | HP={currentHp}/{maximumHp} | HpPercent={currentHp * 100 / maximumHp} | ThresholdPercent={settings.HealOwnerHpPercent} | Exclusive=True | Transport=Direct | Source=DEV_RUNTIME_LAYOUT | RepeatDelayMs={HealRepeatDelayMilliseconds}");
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
				if (!petHealthUnavailableLogged) log($"PET_HEALTH_SOURCE_UNAVAILABLE | Success={pet.Success} | Present={pet.Present} | HealingActive={petHealingActive} | Detail={pet.Detail}");
				petHealthUnavailableLogged = true;
				return petHealingActive;
			}
			if (petHealthUnavailableLogged) log($"PET_HEALTH_SOURCE_RECOVERED | EntityIndex={pet.EntityIndex} | HP={pet.CurrentHp}/{pet.MaximumHp} | HealingActive={petHealingActive} | Detail={pet.Detail}");
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
			if (!transport.TrySendTestCommand(gameWindow, CastSkillCommand, HealSkillId, out _)) return true;
			log($"BUFF_CAST_POSTED | Target=Pet | EntityIndex={pet.EntityIndex} | SkillId={HealSkillId} | HP={pet.CurrentHp}/{pet.MaximumHp} | HpPercent={pet.CurrentHp * 100 / pet.MaximumHp} | ThresholdPercent={settings.HealPetHpPercent} | Exclusive=True | Transport=Direct | Source=CURRENT_ENTITY_TYPE_6 | RepeatDelayMs={HealRepeatDelayMilliseconds}");
			return true;
		} catch {
			return ownerHealingActive || petHealingActive;
		}
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
