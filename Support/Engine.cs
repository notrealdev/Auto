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
	// Xác nhận KHÔNG có Đệ thì nghỉ hẳn quãng này rồi mới dò lại, thay vì quét lại bảng entity mỗi nhịp 100ms.
	// PetHealthReader.Read quét index 2..511 tìm entity type 6 mang mã chủ; bật "Buff Đệ" mà tài khoản không nuôi Đệ
	// thì toàn bộ số lượt đọc đó là vô ích, nhân với 6 tài khoản và 10 nhịp mỗi giây. Dải quét vừa nâng 256 -> 511
	// (2026-09-11) nên chi phí này vừa gấp đôi. 5 giây đủ nhanh để Đệ vừa gọi ra được nhận trong vòng một nhịp buff.
	private const int PetAbsentRecheckMilliseconds = 5000;
	// Phải KHÔNG THẤY ĐỆ bấy nhiêu nhịp LIÊN TIẾP mới được khoá 5 giây ở trên.
	//
	// Vì sao cần (đo trên Release/Diagnostics/buff.log, phiên 10 tiếng 2026-09-15): có 450 lần Đệ "biến mất" rồi
	// quay lại, và 416 lần trong số đó (92%) quay lại trong vòng ~5,2 giây — tức khớp đúng bằng chính khoá 5 giây
	// này, không phải Đệ chết thật. Một nhịp đọc hụt bị khuếch đại thành 5 giây mù hoàn toàn về máu Đệ, cộng dồn
	// khoảng 35 phút trong 10 tiếng. Đúng hiện tượng chủ dự án báo: máu đã dưới ngưỡng mà vẫn đánh quái, không buff.
	//
	// 3 nhịp ở nhịp gọi 100ms chỉ là 0,3 giây, nhưng vẫn giữ nguyên mục đích tiết kiệm CPU ban đầu: tài khoản không
	// nuôi Đệ thì sau 3 nhịp là vào khoá, tức vẫn chỉ tốn 3 lượt quét cho mỗi 5 giây thay vì 50 lượt.
	private const int MinimumPetAbsentReadsBeforeSkip = 3;
	// Trần số lần cast cho MỘT đợt heal Đệ, và thời gian nghỉ sau khi chạm trần (chủ dự án chốt 2026-09-09).
	// Đệ máu thấp mà cast mãi không lên (hết mana, ngoài tầm, Đệ đang bị khoá) thì khối heal giữ quyền điều khiển
	// độc quyền, Đánh và Nhặt bị dừng theo. Chạm trần thì bỏ qua đợt này và 5 giây sau mới xét lại.
	private const int MaximumPetHealCasts = 10;
	private const int PetHealGiveUpCooldownMilliseconds = 5000;
	// Cùng khuôn với trần của nhánh Đệ, thiếu ở nhánh Chủ nên bổ sung 2026-09-10.
	// Bằng chứng từ Release/Diagnostics/buff.log: PID=34032 cast 395 lần liên tiếp 18:49:23 -> 18:53:00, HP đứng yên ở
	// 277/518 (53%) dù ThresholdPercent=45. Lối thoát duy nhất của nhánh Chủ là currentHp >= maximumHp, mà HP không
	// bao giờ đầy nên vòng lặp không kết thúc. Trong lúc đó back-to-training.log ghi 4 lần "Tự lên bãi AutoFS dừng |
	// Buff hỗ trợ giữ quyền điều khiển" -> nhân vật không lên lại được bãi.
	private const int MaximumOwnerHealCasts = 10;
	private const int OwnerHealGiveUpCooldownMilliseconds = 5000;
	private readonly Settings settings;
	private readonly AutoFsAttackTransport transport;
	private DateTime nextOwnerHealUtc;
	private DateTime nextPetHealUtc;
	private bool ownerHealingActive;
	private bool petHealingActive;
	private bool petHealthUnavailableLogged;
	private DateTime petAbsentRecheckUtc;
	private int petAbsentStreak;
	private int petHealCastCount;
	private int petBestHp;
	private DateTime petHealCooldownUntilUtc;
	private int ownerHealCastCount;
	// Máu cao nhất đạt được trong đợt heal hiện tại. Trần cast chỉ đếm những lần KHÔNG làm máu nhích lên.
	private int ownerBestHp;
	private DateTime ownerHealCooldownUntilUtc;
	private int petReadFailureStreak;
	private int ownerReadFailureStreak;
	private int exceptionStreak;
	private string lastCastRejectionLine = "";
	private int castRejectionStreak;
	// Chống lặp dòng BUFF_SKIPPED_OUTSIDE_TRAINING_MAP: nơi gọi chạy mỗi 100ms.
	private bool skillsBlockedLogged;

	// Binds one account support instance to the shared transport.
	public Engine(Settings settings, AutoFsAttackTransport transport) {
		this.settings = settings;
		this.transport = transport;
	}

	// Holds exclusive control from the configured threshold until the selected target reaches full HP.
	// skillsAllowedHere: nhân vật đang đứng ở map được phép dùng kỹ năng hay không. Nơi gọi quyết định, khối này chỉ
	// tuân theo — giữ Support không phải biết gì về bản đồ hay cấu hình bãi.
	//
	// Vì sao cần (chủ dự án chỉ ra 2026-09-11): TRONG THÀNH game CẤM dùng kỹ năng. Trước đây khối này không có một
	// phép kiểm map nào nên nó cast mù, và vì máu không bao giờ lên nên vòng heal không có lối thoát, trong khi nó
	// vẫn giữ quyền điều khiển độc quyền → coordinator dừng Đánh/Nhặt và huỷ Tự lên bãi → nhân vật đứng im.
	//
	// Bằng chứng đo được (Release/Diagnostics, 2026-09-10), cả hai đều trên Map 20 "Tây Kỳ" tức trong thành:
	//   PID=34032  18:49:23->18:53:10  HP đứng yên 277/518  395 lần cast vô ích
	//   PID=16428  19:03:20->19:04:39  HP đứng yên 245/478  145 lần cast vô ích
	// Đây cũng là đính chính: giả thuyết "hết mana" đưa ra trước đó là SAI.
	public bool Tick(int processId, IntPtr gameWindow, GameSnapshot ownerSnapshot, bool attackEnabled, bool skillsAllowedHere, Action<string> log) {
		if (!attackEnabled || (!settings.HealOwner && !settings.HealPet)) {
			Reset();
			return false;
		}
		if (!skillsAllowedHere) {
			// Ghi đúng một lần cho tới khi về lại chỗ dùng được kỹ năng; nơi gọi chạy mỗi 100ms.
			if (!skillsBlockedLogged) {
				skillsBlockedLogged = true;
				log("BUFF_SKIPPED_OUTSIDE_TRAINING_MAP | Lý do=Không đứng ở map bãi nên coi như đang trong thành, game cấm dùng kỹ năng | Action=Nhả quyền điều khiển, không cast");
			}
			Reset();
			return false;
		}
		skillsBlockedLogged = false;
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
				ownerHealCastCount = 0;
				ownerHealCooldownUntilUtc = DateTime.MinValue;
			}
			// Đang nghỉ sau khi chạm trần cast: bỏ hẳn nhánh Chủ, phải xét TRƯỚC khi đọc HP không thì ngưỡng bên dưới
			// bật lại ownerHealingActive ngay. Cùng cách xử lý với petHealCooldownUntilUtc.
			if (settings.HealOwner && DateTime.UtcNow >= ownerHealCooldownUntilUtc) {
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
					ownerHealCastCount = 0;
				} else if (!ownerHealingActive && (long)currentHp * 100 <= (long)maximumHp * settings.HealOwnerHpPercent) {
					// Mỗi đợt heal mới đếm lại từ 0; trần là của một đợt, không phải của cả phiên.
					ownerHealingActive = true;
					ownerHealCastCount = 0;
					ownerBestHp = currentHp;
				}
				// Máu có nhích lên là heal ĐANG ăn -> xoá bộ đếm, không được bỏ cuộc.
				//
				// Trần cast thêm sáng nay đếm theo SỐ LẦN chứ không theo tiến độ, nên nó cắt cả những đợt heal đang chạy
				// tốt. Bằng chứng (Release/Diagnostics/buff.log 2026-09-10): 10 dòng BUFF_OWNER_HEAL_GIVE_UP, trong đó
				// "HP=143/478 | HpPercent=29" và "HP=209/478 | HpPercent=43" đều dưới ngưỡng 45 mà vẫn nhả quyền cho
				// luồng đánh 5 giây; hai dòng khác dừng ở 75% và 87% lúc máu đang lên đều. Đúng hiện tượng chủ dự án
				// báo: máu dưới ngưỡng mà Auto đánh xong quái mới chịu buff.
				if (ownerHealingActive && currentHp > ownerBestHp) {
					ownerBestHp = currentHp;
					ownerHealCastCount = 0;
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
					ownerHealCastCount++;
					log($"BUFF_CAST_CONFIRMED | Target=Owner | SkillId={HealSkillId} | HP={currentHp}/{maximumHp} | HpPercent={currentHp * 100 / maximumHp} | ThresholdPercent={settings.HealOwnerHpPercent} | Cast={ownerHealCastCount}/{MaximumOwnerHealCasts} | Exclusive=True | Transport=Direct | Source=DEV_RUNTIME_LAYOUT | RepeatDelayMs={HealRepeatDelayMilliseconds}");
					// Chạm trần: lần cast cuối vẫn được gửi, sau đó mới bỏ đợt này và nghỉ, nhả quyền điều khiển ngay để
					// Đánh, Nhặt và Tự lên bãi chạy tiếp trong lúc nghỉ.
					if (ownerHealCastCount >= MaximumOwnerHealCasts) {
						ownerHealingActive = false;
						ownerHealCastCount = 0;
						nextOwnerHealUtc = DateTime.MinValue;
						ownerHealCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(OwnerHealGiveUpCooldownMilliseconds);
						log($"BUFF_OWNER_HEAL_GIVE_UP | Casts={MaximumOwnerHealCasts} | HP={currentHp}/{maximumHp} | HpPercent={currentHp * 100 / maximumHp} | CooldownMs={OwnerHealGiveUpCooldownMilliseconds} | Action=Bỏ qua đợt heal Chủ này và nhả quyền điều khiển");
						return false;
					}
					return true;
				}
			}

			if (!settings.HealPet) {
				petHealingActive = false;
				nextPetHealUtc = DateTime.MinValue;
				petHealthUnavailableLogged = false;
				petAbsentRecheckUtc = DateTime.MinValue;
				petAbsentStreak = 0;
				petHealCastCount = 0;
				petHealCooldownUntilUtc = DateTime.MinValue;
				return false;
			}

			// Đang trong thời gian nghỉ sau khi chạm trần cast: bỏ qua hẳn nhánh Đệ và nhả quyền điều khiển.
			// Phải xét TRƯỚC khi đọc HP Đệ, không thì ngưỡng bên dưới bật lại petHealingActive ngay lập tức.
			if (DateTime.UtcNow < petHealCooldownUntilUtc) return false;
			// Đã xác nhận không có Đệ ở lượt trước: bỏ qua hẳn nhánh này cho tới hạn dò lại, không đọc bộ nhớ lần nào.
			if (DateTime.UtcNow < petAbsentRecheckUtc) return false;

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
					// Một nhịp không thấy Đệ CHƯA phải kết luận: bảng entity có lúc hụt Đệ đúng một nhịp rồi có lại.
					// Chỉ khoá sau khi nhiều nhịp liên tiếp đều không thấy, xem MinimumPetAbsentReadsBeforeSkip.
					petAbsentStreak++;
					if (petAbsentStreak >= MinimumPetAbsentReadsBeforeSkip) petAbsentRecheckUtc = DateTime.UtcNow.AddMilliseconds(PetAbsentRecheckMilliseconds);
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
			petAbsentStreak = 0;
			petAbsentRecheckUtc = DateTime.MinValue;
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
				petHealCastCount = 0;
				return false;
			}
			// Mỗi đợt heal mới đếm lại từ 0; trần 10 lần là của một đợt, không phải của cả phiên.
			if (!petHealingActive && (long)pet.CurrentHp * 100 <= (long)pet.MaximumHp * settings.HealPetHpPercent) {
				petHealingActive = true;
				petHealCastCount = 0;
				petBestHp = pet.CurrentHp;
			}
			// Cùng cách xử lý với nhánh Chủ: máu Đệ nhích lên là heal đang ăn, xoá bộ đếm để không bỏ cuộc giữa chừng.
			// buff.log ngày 2026-09-10 có 176 dòng BUFF_PET_HEAL_GIVE_UP, phần lớn chưa chắc là kẹt thật.
			if (petHealingActive && pet.CurrentHp > petBestHp) {
				petBestHp = pet.CurrentHp;
				petHealCastCount = 0;
			}
			if (!petHealingActive) return false;
			if (DateTime.UtcNow < nextPetHealUtc) return true;

			nextPetHealUtc = DateTime.UtcNow.AddMilliseconds(HealRepeatDelayMilliseconds);
			if (!transport.TrySendConfirmedCommand(gameWindow, CastSkillCommand, HealSkillId, out string petCastError)) {
				return HoldOrReleaseAfterRejection(log, "Pet", petCastError);
			}
			lastCastRejectionLine = "";
			castRejectionStreak = 0;
			petHealCastCount++;
			log($"BUFF_CAST_CONFIRMED | Target=Pet | EntityIndex={pet.EntityIndex} | SkillId={HealSkillId} | HP={pet.CurrentHp}/{pet.MaximumHp} | HpPercent={pet.CurrentHp * 100 / pet.MaximumHp} | ThresholdPercent={settings.HealPetHpPercent} | Cast={petHealCastCount}/{MaximumPetHealCasts} | Exclusive=True | Transport=Direct | Source=CURRENT_ENTITY_TYPE_6 | RepeatDelayMs={HealRepeatDelayMilliseconds}");
			// Chạm trần: lần cast thứ 10 vẫn được gửi, sau đó mới bỏ đợt này và nghỉ. Nhả quyền điều khiển ngay để
			// Đánh và Nhặt chạy tiếp trong lúc nghỉ.
			if (petHealCastCount >= MaximumPetHealCasts) {
				petHealingActive = false;
				petHealCastCount = 0;
				nextPetHealUtc = DateTime.MinValue;
				petHealCooldownUntilUtc = DateTime.UtcNow.AddMilliseconds(PetHealGiveUpCooldownMilliseconds);
				log($"BUFF_PET_HEAL_GIVE_UP | Casts={MaximumPetHealCasts} | HP={pet.CurrentHp}/{pet.MaximumHp} | HpPercent={pet.CurrentHp * 100 / pet.MaximumHp} | CooldownMs={PetHealGiveUpCooldownMilliseconds} | Action=Bỏ qua đợt heal Đệ này và nhả quyền điều khiển");
				return false;
			}
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
	// Nhả quyền điều khiển và xoá trạng thái đang heal khi một luồng ưu tiên cao hơn tiếp quản (hiện chỉ có tránh
	// boss). Xoá hẳn thay vì tạm treo để lúc quay lại thì ngưỡng được xét lại từ đầu theo máu thật lúc đó.
	public void ReleaseForHigherPriority() {
		Reset();
	}

	private void Reset() {
		ownerHealingActive = false;
		petHealingActive = false;
		nextOwnerHealUtc = DateTime.MinValue;
		nextPetHealUtc = DateTime.MinValue;
		petHealthUnavailableLogged = false;
		petReadFailureStreak = 0;
		petAbsentStreak = 0;
		ownerReadFailureStreak = 0;
		exceptionStreak = 0;
		petHealCastCount = 0;
		petHealCooldownUntilUtc = DateTime.MinValue;
		ownerHealCastCount = 0;
		ownerHealCooldownUntilUtc = DateTime.MinValue;
	}
}
