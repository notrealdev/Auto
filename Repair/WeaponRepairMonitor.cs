namespace Auto.Repair;

using Auto.DebugTools;

public sealed class WeaponRepairMonitor {
	private static readonly bool EnableAutomaticRepairRequests = true;
	private const int CheckIntervalSeconds = 5;
	private const int ReadingConfirmationMilliseconds = 500;
	private const int ExclusivePreparationMilliseconds = 3000;
	private const int ExclusiveRetryMilliseconds = 500;
	private const int ExclusiveTimeoutMilliseconds = 8000;
	private DateTime nextCheckUtc = DateTime.MinValue;
	private string lastFailure = "";
	private uint? lastThresholdValue;
	private bool enabled;
	private bool logNextReading;
	private string nextReadingReason = "ngay khi bật Sửa đồ";
	private bool repairRequested;
	private uint requestedDurability;
	private long pendingReadingAddress;
	private uint pendingReadingRecordOffset;
	private uint pendingReadingCurrent;
	private int pendingReadingCount;
	private bool exclusiveCheckActive;
	private bool exclusiveCheckSuppressed;
	private bool unavailableTransportLogged;
	private DateTime exclusiveCheckDeadlineUtc = DateTime.MinValue;

	public bool RequiresExclusiveControl => enabled && exclusiveCheckActive;
	public bool HasPendingRepairRequest => enabled && repairRequested;

	public void SetEnabled(bool value) {
		if (value && !enabled) {
			nextCheckUtc = DateTime.MinValue;
			logNextReading = true;
			nextReadingReason = "ngay khi bật Sửa đồ";
			lastFailure = "";
			repairRequested = false;
			requestedDurability = 0;
			ResetPendingReading();
			exclusiveCheckActive = true;
			exclusiveCheckDeadlineUtc = DateTime.UtcNow.AddMilliseconds(ExclusiveTimeoutMilliseconds);
			exclusiveCheckSuppressed = false;
			unavailableTransportLogged = false;
		}
		if (!value) {
			nextCheckUtc = DateTime.MinValue;
			lastThresholdValue = null;
			logNextReading = false;
			repairRequested = false;
			requestedDurability = 0;
			ResetPendingReading();
			EndExclusiveCheck();
			exclusiveCheckSuppressed = false;
			unavailableTransportLogged = false;
		}
		enabled = value;
	}

	public void Tick(int processId, int threshold, bool competingEnginesEnabled, Action<string>? log) {
		if (!enabled || processId <= 0 || DateTime.UtcNow < nextCheckUtc) return;
		DateTime now = DateTime.UtcNow;
		EquippedWeaponDurabilityReading reading;
		if (!exclusiveCheckActive) {
			reading = ReadDirect(processId);
			bool definitiveSafeReject = !reading.Success && reading.FailureReason.Contains("SAFE_REJECT", StringComparison.Ordinal);
			if (!reading.Success && !definitiveSafeReject && competingEnginesEnabled && logNextReading && !exclusiveCheckSuppressed) {
				exclusiveCheckActive = true;
				exclusiveCheckDeadlineUtc = now.AddMilliseconds(ExclusiveTimeoutMilliseconds);
				nextCheckUtc = now.AddMilliseconds(ExclusivePreparationMilliseconds);
				ResetPendingReading();
				string reason = logNextReading ? nextReadingReason : "định kỳ";
				log?.Invoke($"Kiểm tra độ bền {reason} | tạm giữ Tự đánh/Nhặt đồ {ExclusivePreparationMilliseconds}ms để đọc trang bị ổn định.");
				return;
			}
		} else {
			reading = ReadNow(processId);
		}
		if (!reading.Success) {
			ResetPendingReading();
			bool definitiveSafeReject = reading.FailureReason.Contains("SAFE_REJECT", StringComparison.Ordinal);
			if (exclusiveCheckActive && now < exclusiveCheckDeadlineUtc && !definitiveSafeReject) {
				nextCheckUtc = now.AddMilliseconds(ExclusiveRetryMilliseconds);
				return;
			}
			bool failedExclusiveCheck = exclusiveCheckActive;
			EndExclusiveCheck();
			if (failedExclusiveCheck || !competingEnginesEnabled) exclusiveCheckSuppressed = true;
			nextCheckUtc = now.AddSeconds(CheckIntervalSeconds);
			string suppressionNote = exclusiveCheckSuppressed ? " | Các lần định kỳ tiếp theo chỉ dò thụ động, không tạm giữ Tự đánh/Nhặt đồ." : "";
			if (logNextReading) {
				log?.Invoke($"Kiểm tra độ bền {nextReadingReason} thất bại | {reading.FailureReason} | Sẽ thử lại sau {CheckIntervalSeconds}s.{suppressionNote}");
				logNextReading = false;
			} else if (!string.Equals(lastFailure, reading.FailureReason, StringComparison.Ordinal)) {
				lastFailure = reading.FailureReason;
				log?.Invoke("Kiểm tra độ bền thất bại | " + reading.FailureReason + suppressionNote);
			}
			lastFailure = reading.FailureReason;
			repairRequested = false;
			requestedDurability = 0;
			return;
		}
		exclusiveCheckSuppressed = false;
		nextCheckUtc = now.AddSeconds(CheckIntervalSeconds);

		bool sameReading = pendingReadingCount > 0
			&& pendingReadingAddress == reading.Address
			&& pendingReadingRecordOffset == reading.RecordOffset
			&& pendingReadingCurrent == reading.Current;
		if (!sameReading) {
			pendingReadingAddress = reading.Address;
			pendingReadingRecordOffset = reading.RecordOffset;
			pendingReadingCurrent = reading.Current;
			pendingReadingCount = 1;
			nextCheckUtc = now.AddMilliseconds(ReadingConfirmationMilliseconds);
			if (logNextReading) log?.Invoke($"Đọc độ bền toàn bộ trang bị {nextReadingReason} | Minimum={reading.Current} | Layout={(reading.UsesDirectCurrent ? "Direct" : "Paired")} | Source={(reading.UsesCachedRecord ? "ConfirmedCache" : "EquippedSlots")} | SlotThấpNhất=0x{reading.MirrorAddressA:X8} | ItemTable=0x{reading.MirrorAddressB:X8} | RecordOffset=0x{reading.RecordOffset:X8} | Field=0x{reading.Address:X8} | {reading.Evidence} | XácNhận=1/2");
			return;
		}
		pendingReadingCount++;
		if (pendingReadingCount < 2) {
			nextCheckUtc = now.AddMilliseconds(ReadingConfirmationMilliseconds);
			return;
		}
		ResetPendingReading();
		EndExclusiveCheck();

		lastFailure = "";
		if (logNextReading) {
			log?.Invoke($"Kiểm tra độ bền toàn bộ trang bị {nextReadingReason} | Minimum={reading.Current} | Ngưỡng={threshold} | CầnSửaToànBộ={reading.Current <= threshold} | SlotThấpNhất=0x{reading.MirrorAddressA:X8} | ItemTable=0x{reading.MirrorAddressB:X8} | RecordOffset=0x{reading.RecordOffset:X8} | Field=0x{reading.Address:X8} | {reading.Evidence}");
			logNextReading = false;
		}
		if (reading.Current <= threshold) {
			if (lastThresholdValue != reading.Current) log?.Invoke($"Trang bị cần Sửa toàn bộ | Độ bền thấp nhất={reading.Current} | Ngưỡng={threshold} | Source={(reading.UsesCachedRecord ? "ConfirmedCache" : "EquippedSlots")} | SlotThấpNhất=0x{reading.MirrorAddressA:X8} | RecordOffset=0x{reading.RecordOffset:X8} | Field=0x{reading.Address:X8} | {reading.Evidence}");
			lastThresholdValue = reading.Current;
			if (!EnableAutomaticRepairRequests) {
				repairRequested = false;
				requestedDurability = 0;
				log?.Invoke("Sửa đồ bị khóa an toàn | Nguồn nhận diện vũ khí đang trang bị chưa được xác nhận; auto không di chuyển tới NPC.");
				return;
			}
			repairRequested = true;
			requestedDurability = reading.Current;
		} else {
			lastThresholdValue = null;
			repairRequested = false;
			requestedDurability = 0;
			unavailableTransportLogged = false;
		}
	}

	public void ReportUnavailableTransport(string reason, Action<string>? log) {
		if (!HasPendingRepairRequest || unavailableTransportLogged) return;
		unavailableTransportLogged = true;
		log?.Invoke($"REPAIR_WEAPON_PROTECTION | Đã dừng Đánh/Nhặt vì độ bền={requestedDurability} và luồng NPC chưa sẵn sàng | {reason}");
	}

	public EquippedWeaponDurabilityReading ReadNow(int processId) {
		return ReadDirect(processId);
	}

	public EquippedWeaponDurabilityReading ReadFresh(int processId) {
		return ReadDirect(processId);
	}

	public bool TryTakeRepairRequest(out uint current) {
		current = requestedDurability;
		if (!repairRequested) return false;
		unavailableTransportLogged = false;
		return true;
	}

	public void ConfirmRepairCompleted() {
		repairRequested = false;
		requestedDurability = 0;
		lastThresholdValue = null;
		unavailableTransportLogged = false;
		ResetPendingReading();
	}

	public void ScheduleImmediateCheck(string? reason = null) {
		EndExclusiveCheck();
		nextCheckUtc = DateTime.MinValue;
		if (!string.IsNullOrWhiteSpace(reason)) {
			logNextReading = true;
			nextReadingReason = reason;
		}
	}

	public void NotifyThresholdChanged() {
		repairRequested = false;
		requestedDurability = 0;
		unavailableTransportLogged = false;
		lastThresholdValue = null;
		ResetPendingReading();
		ScheduleImmediateCheck("sau đổi ngưỡng");
	}

	public void ResetCache() {
		EndExclusiveCheck();
		nextCheckUtc = DateTime.MinValue;
		ResetPendingReading();
	}

	private void ResetPendingReading() {
		pendingReadingAddress = 0;
		pendingReadingRecordOffset = 0;
		pendingReadingCurrent = 0;
		pendingReadingCount = 0;
	}

	private void EndExclusiveCheck() {
		exclusiveCheckActive = false;
		exclusiveCheckDeadlineUtc = DateTime.MinValue;
	}

	private static EquippedWeaponDurabilityReading ReadDirect(int processId) {
		return EquippedWeaponDurabilityProbe.ReadCurrent(processId);
	}
}
