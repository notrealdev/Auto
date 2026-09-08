namespace Auto.Support;

using Auto.Attack;
using Auto.Utils;

internal sealed class Engine {
	private const int CastSkillCommand = 85;
	private const int HealSkillId = 45;
	private const int HealRepeatDelayMilliseconds = 500;
	private const int MaximumCastRejectionsBeforeRelease = 3;
	// Dùng chung cho cả nhánh Chủ và nhánh Đệ: đọc nguồn HP hỏng bao nhiêu nhịp liên tiếp thì nhả quyền điều khiển.
	private const int MaximumReadFailuresBeforeRelease = 3;
	private readonly Settings settings;
	private readonly AutoFsAttackTransport transport;
	private DateTime nextOwnerHealUtc;
	private DateTime nextPetHealUtc;
	private bool ownerHealingActive;
	private bool petHealingActive;
	private bool petHealthUnavailableLogged;
	private int petReadFailureStreak;
	private int ownerReadFailureStreak;
	private int exceptionStreak;
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
			bool held = TickCore(processId, gameWindow, ownerSnapshot, log);
			// Chỉ một nhịp chạy trót lọt mới xoá chuỗi ngoại lệ; đặt ở đầu try thì streak không bao giờ đếm lên được.
			exceptionStreak = 0;
			return held;
		} catch (Exception ex) {
			// Chỗ thứ ba cùng khuôn: lỗi lặp lại mỗi nhịp mà trả thẳng cờ đang-heal thì giữ quyền độc quyền vĩnh viễn.
			// Đếm rồi nhả như hai nhánh trong TickCore; ngoại lệ thoáng qua vẫn được giữ vài nhịp để không cắt ngang heal.
			exceptionStreak++;
			if (exceptionStreak < MaximumReadFailuresBeforeRelease) return ownerHealingActive || petHealingActive;
			if (exceptionStreak == MaximumReadFailuresBeforeRelease) {
				log($"BUFF_EXCEPTION_RELEASED_CONTROL | Failures={exceptionStreak} | {ex.GetType().Name}: {ex.Message} | Action=Nhả quyền điều khiển để Auto Đánh và Auto Nhặt chạy tiếp");
			}
			ownerHealingActive = false;
			petHealingActive = false;
			nextOwnerHealUtc = DateTime.MinValue;
			nextPetHealUtc = DateTime.MinValue;
			return false;
		}
	}

	private bool TickCore(int processId, IntPtr gameWindow, GameSnapshot ownerSnapshot, Action<string> log) {
		{
			if (!settings.HealOwner) {
				ownerHealingActive = false;
				nextOwnerHealUtc = DateTime.MinValue;
			}
			if (settings.HealOwner) {
				// Cùng khuôn lỗi với nhánh Đệ bên dưới: trước đây đọc HP hỏng thì trả thẳng ownerHealingActive, mà cờ đó
				// không có đường reset trong nhánh này — kẹt true là khối heal giữ quyền độc quyền vĩnh viễn, coordinator
				// dừng Đánh + Nhặt rồi return, nhân vật đứng im. Giữ tối đa vài nhịp rồi nhả, vì bỏ lỡ một nhịp heal luôn
				// nhẹ hơn đứng im cả đêm.
				if (!ownerSnapshot.Success || ownerSnapshot.Hp < 0 || ownerSnapshot.MaxHp <= 0 || ownerSnapshot.Hp > ownerSnapshot.MaxHp) {
					if (!ownerHealingActive) return false;
					ownerReadFailureStreak++;
					if (ownerReadFailureStreak < MaximumReadFailuresBeforeRelease) return true;
					if (ownerReadFailureStreak == MaximumReadFailuresBeforeRelease) {
						log($"BUFF_OWNER_RELEASED_CONTROL | Failures={ownerReadFailureStreak} | Success={ownerSnapshot.Success} | HP={ownerSnapshot.Hp}/{ownerSnapshot.MaxHp} | Action=Nhả quyền điều khiển để Auto Đánh và Auto Nhặt chạy tiếp");
					}
					ownerHealingActive = false;
					nextOwnerHealUtc = DateTime.MinValue;
					return false;
				}
				ownerReadFailureStreak = 0;
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
				// Đọc THÀNH CÔNG mà không có Đệ (PetHealthReader.Missing) là kết luận chắc chắn — Đệ chết hoặc đã biến
				// mất. Trước đây nhánh này trả về petHealingActive, nên nếu Đệ chết đúng lúc đang heal thì cờ đó kẹt ở
				// true và khối heal giữ quyền điều khiển độc quyền vĩnh viễn: coordinator dừng Đánh + Nhặt rồi return,
				// nhân vật đứng im. Nhánh pet.CurrentHp <= 0 bên dưới KHÔNG cứu được vì nó cần Đệ còn tồn tại.
				if (pet.Success) {
					petHealingActive = false;
					nextPetHealUtc = DateTime.MinValue;
					petReadFailureStreak = 0;
					return false;
				}
				// Đọc LỖI (PetHealthReader.Fail) thì có thể chỉ thoáng qua, giữ quyền thêm vài nhịp rồi nhả — cùng cách
				// xử lý với HoldOrReleaseAfterRejection, vì đứng im vĩnh viễn luôn tệ hơn bỏ lỡ một nhịp heal.
				if (!petHealingActive) return false;
				petReadFailureStreak++;
				if (petReadFailureStreak < MaximumReadFailuresBeforeRelease) return true;
				if (petReadFailureStreak == MaximumReadFailuresBeforeRelease) {
					log($"BUFF_PET_RELEASED_CONTROL | Failures={petReadFailureStreak} | Detail={pet.Detail} | Action=Nhả quyền điều khiển để Auto Đánh và Auto Nhặt chạy tiếp");
				}
				petHealingActive = false;
				nextPetHealUtc = DateTime.MinValue;
				return false;
			}
			petReadFailureStreak = 0;
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
		petReadFailureStreak = 0;
		ownerReadFailureStreak = 0;
		exceptionStreak = 0;
	}
}
