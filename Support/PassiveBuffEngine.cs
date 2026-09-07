namespace Auto.Support;

using Auto.Attack;

// Bật lại liên tục các skill hỗ trợ bị động của hệ Dị Nhân, port từ thread _TBuffDịNhân của AutoFS.
// Vòng lặp gốc (VectorFactory.SplitDisk, khôi phục từ IL vì ILSpy không dịch được) chạy mãi khi đang auto:
// với mỗi skill được tích thì gửi gói bật skill rồi Thread.Sleep(350) trước khi sang skill kế tiếp.
// DEV auto giữ nguyên nhịp 350 ms nhưng chuyển sang mô hình tick để không chiếm thread riêng cho mỗi account.
internal sealed class PassiveBuffEngine {
	private const int SkillIntervalMilliseconds = 350;
	// Sau khi tạm dừng lâu (đổi cấu hình, tắt auto) thì bỏ phần nợ thay vì bắn dồn một loạt.
	private const int MaximumCatchUpMilliseconds = 3000;
	// Bốn id lấy từ chính gói lệnh AutoFS gửi đi (Function.cs: byte thứ hai của mảng 16 byte, phần còn lại
	// nằm ngoài độ dài gói nên không được đọc). Chưa xác nhận id có giữ nguyên trên client hiện tại.
	private const int KimCangSkillId = 43;
	private const int CuongCongSkillId = 46;
	private const int BoDeSkillId = 48;
	private const int TatPhongSkillId = 50;

	private readonly Settings settings;
	private readonly AutoFsAttackTransport transport;
	private readonly List<(int SkillId, string Name)> pendingSkills = new();
	private DateTime nextSendUtc;
	private int nextSkillIndex;
	private string lastStateLine = "";
	private string lastFailureLine = "";

	public PassiveBuffEngine(Settings settings, AutoFsAttackTransport transport) {
		this.settings = settings;
		this.transport = transport;
	}

	// Gửi lần lượt từng skill đang bật, mỗi lần cách nhau đúng nhịp 350 ms của AutoFS.
	public void Tick(int processId, IntPtr gameWindow, bool attackEnabled, Action<string> log) {
		BuildPendingSkills(attackEnabled);
		if (pendingSkills.Count == 0) {
			LogStateChange(log, processId, "IDLE", DescribeReason(attackEnabled));
			nextSendUtc = DateTime.MinValue;
			nextSkillIndex = 0;
			return;
		}

		LogStateChange(log, processId, "RUNNING", string.Join(",", pendingSkills.Select(skill => skill.Name)));
		DateTime now = DateTime.UtcNow;
		if (now < nextSendUtc) return;

		// Trước đây mỗi tick chỉ gửi một skill và mốc kế tiếp tính từ "bây giờ", nên chu kỳ thực bị kéo theo
		// chu kỳ tick (đo được 1735 ms cho một vòng 4 skill thay vì 1400 ms của AutoFS, tức trễ 24%).
		// Nay mốc được cộng dồn tuyệt đối và cho phép gửi bù trong cùng một tick để bám đúng nhịp 350 ms.
		if (nextSendUtc == DateTime.MinValue || (now - nextSendUtc).TotalMilliseconds > MaximumCatchUpMilliseconds) nextSendUtc = now;

		int sentThisTick = 0;
		while (now >= nextSendUtc && sentThisTick < pendingSkills.Count) {
			if (nextSkillIndex >= pendingSkills.Count) nextSkillIndex = 0;
			(int skillId, string name) = pendingSkills[nextSkillIndex];
			nextSkillIndex++;
			nextSendUtc = nextSendUtc.AddMilliseconds(SkillIntervalMilliseconds);
			sentThisTick++;

			if (transport.TrySendPassiveBuff(gameWindow, skillId, out string error)) {
				lastFailureLine = "";
				log($"BUFF_PASSIVE_SENT | PID={processId} | Skill={name} | SkillId={skillId} | Interval={SkillIntervalMilliseconds}ms | Batch={sentThisTick} | Delivery=CONFIRMED");
			} else {
				LogFailure(log, processId, name, skillId, error);
			}
			now = DateTime.UtcNow;
		}
	}

	// Bật ô Buff 3 hệ là bật toàn bộ skill hỗ trợ bị động; giữ đúng thứ tự AutoFS duyệt.
	private void BuildPendingSkills(bool attackEnabled) {
		pendingSkills.Clear();
		if (! attackEnabled || ! settings.BuffThreeSystems) return;
		pendingSkills.Add((KimCangSkillId, "KimCang"));
		pendingSkills.Add((CuongCongSkillId, "CuongCong"));
		pendingSkills.Add((BoDeSkillId, "BoDe"));
		pendingSkills.Add((TatPhongSkillId, "TatPhong"));
	}

	private static string DescribeReason(bool attackEnabled) {
		if (! attackEnabled) return "AttackDisabled";
		return "BuffThreeSystemsDisabled";
	}

	// Chỉ ghi khi trạng thái đổi để log không bị ngập bởi mỗi vòng tick.
	private void LogStateChange(Action<string> log, int processId, string state, string detail) {
		string line = $"BUFF_PASSIVE_STATE | PID={processId} | State={state} | Detail={detail}";
		if (string.Equals(lastStateLine, line, StringComparison.Ordinal)) return;
		lastStateLine = line;
		log(line);
	}

	// Lỗi lặp lại liên tục nên chỉ ghi khi nội dung lỗi đổi.
	private void LogFailure(Action<string> log, int processId, string name, int skillId, string error) {
		string line = $"BUFF_PASSIVE_FAILED | PID={processId} | Skill={name} | SkillId={skillId} | Error={error}";
		if (string.Equals(lastFailureLine, line, StringComparison.Ordinal)) return;
		lastFailureLine = line;
		log(line);
	}

	// Xóa nhịp gửi và bộ nhớ log khi cấu hình đổi hoặc account dừng auto.
	public void Reset() {
		pendingSkills.Clear();
		nextSendUtc = DateTime.MinValue;
		nextSkillIndex = 0;
		lastStateLine = "";
		lastFailureLine = "";
	}
}
