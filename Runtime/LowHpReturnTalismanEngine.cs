namespace Auto.Runtime;

using Auto.Attack;
using Auto.Utils;

internal sealed class LowHpReturnTalismanEngine {
	private const int MaximumNameLength = 64;
	private const int MaximumPackedItemId = 0x001FFFFF;
	private const int DispatchRetryMilliseconds = 1000;
	private const int MaximumDispatchAttempts = 5;
	private const int HpCheckIntervalMilliseconds = 5000;
	private const int MaximumStalledHpChecks = 5;
	private const int HoldReminderIntervalMilliseconds = 60000;
	private const string ReturnTalismanNameFragment = "Hồi thành phù";
	private readonly BasicSettings settings;
	private readonly AutoFsAttackTransport transport;
	private TalismanState state;
	private int sourceMapId;
	private int townMapId;
	private DateTime nextDispatchUtc;
	private int dispatchAttempts;
	private DateTime nextHpCheckUtc;
	private int lastCheckedHp;
	private int stalledHpChecks;
	private DateTime nextHoldReminderUtc;
	private bool returnToTrainingRequested;
	private string lastFailure = "";

	public LowHpReturnTalismanEngine(BasicSettings settings, AutoFsAttackTransport transport) {
		this.settings = settings;
		this.transport = transport;
	}

	// Đọc và xóa cờ yêu cầu lên bãi để vòng lặp tài khoản chỉ khởi động luồng lên bãi đúng một lần.
	public bool ConsumeReturnToTrainingRequest() {
		if (! returnToTrainingRequested) return false;
		returnToTrainingRequested = false;
		return true;
	}

	// Dùng Hồi thành phù khi HP chạm ngưỡng, xác nhận bằng đổi map, rồi chờ HP hồi trước khi cho lên bãi lại.
	public bool Tick(int processId, IntPtr gameWindowHandle, GameSnapshot snapshot, int mapId, Action<string>? log) {
		if (! settings.EnableLowHpReturnTalisman || ! snapshot.Success || snapshot.Hp <= 0 || snapshot.MaxHp <= 0) {
			Reset();
			return false;
		}
		// So sánh nguyên: Hp*100 <= Threshold*MaxHp cho đúng kết quả như bản làm tròn lên cũ mà không cần số thực.
		int hpPercent = Math.Clamp(snapshot.Hp * 100 / snapshot.MaxHp, 0, 100);
		bool belowThreshold = snapshot.Hp * 100 <= settings.LowHpReturnTalismanThreshold * snapshot.MaxHp;
		DateTime now = DateTime.UtcNow;
		if (state == TalismanState.Stopped) {
			// Chỉ thoát trạng thái dừng khi HP đã đầy trở lại, tránh bắn bùa tiếp trong lúc không hồi được máu.
			if (snapshot.Hp >= snapshot.MaxHp) {
				log?.Invoke($"LOW_HP_STOP_CLEARED | Hp={snapshot.Hp}/{snapshot.MaxHp} | CurrentMapId={mapId}");
				Reset();
				return false;
			}
			// Nhắc lại định kỳ để log không im hoàn toàn khi tài khoản đang bị giữ đứng im.
			if (now >= nextHoldReminderUtc) {
				nextHoldReminderUtc = now.AddMilliseconds(HoldReminderIntervalMilliseconds);
				log?.Invoke($"LOW_HP_STOPPED_HOLDING | Hp={snapshot.Hp}/{snapshot.MaxHp} | HpPercent={hpPercent} | CurrentMapId={mapId} | TownMapId={townMapId} | Reason=Đang giữ đứng im, chỉ thoát khi HP đầy");
			}
			return true;
		}
		if (state == TalismanState.ArmingAfterReturn) {
			// Chặn bắn bùa lần nữa cho tới khi HP thực sự vượt ngưỡng một lần, tránh vòng lặp thành - bãi - thành.
			if (belowThreshold) {
				// Nhắc định kỳ để không im lặng suốt thời gian tính năng đang bị khoá chờ HP vượt ngưỡng.
				if (now >= nextHoldReminderUtc) {
					nextHoldReminderUtc = now.AddMilliseconds(HoldReminderIntervalMilliseconds);
					log?.Invoke($"LOW_HP_ARMING_HOLD | Hp={snapshot.Hp}/{snapshot.MaxHp} | HpPercent={hpPercent} | Threshold={settings.LowHpReturnTalismanThreshold} | CurrentMapId={mapId} | Reason=Chờ HP vượt ngưỡng một lần trước khi cho dùng bùa lại");
				}
				return false;
			}
			log?.Invoke($"LOW_HP_REARMED | Hp={snapshot.Hp}/{snapshot.MaxHp} | HpPercent={hpPercent} | Threshold={settings.LowHpReturnTalismanThreshold} | CurrentMapId={mapId}");
			int rememberedTownMapId = townMapId;
			Reset();
			townMapId = rememberedTownMapId;
			return false;
		}
		if (state == TalismanState.RecoveringHp) return TickHpRecovery(snapshot, mapId, now, log);
		if (state == TalismanState.WaitingMapChange) {
			if (sourceMapId > 0 && mapId > 0 && mapId != sourceMapId) {
				townMapId = mapId;
				log?.Invoke($"LOW_HP_CONFIRMED | Hp={snapshot.Hp}/{snapshot.MaxHp} | HpPercent={hpPercent} | SourceMapId={sourceMapId} | TownMapId={townMapId} | Attempts={dispatchAttempts}");
				state = TalismanState.RecoveringHp;
				nextHpCheckUtc = now.AddMilliseconds(HpCheckIntervalMilliseconds);
				lastCheckedHp = snapshot.Hp;
				stalledHpChecks = 0;
				return true;
			}
			if (! belowThreshold) {
				log?.Invoke($"LOW_HP_ABORTED_HP_RECOVERED | Hp={snapshot.Hp}/{snapshot.MaxHp} | HpPercent={hpPercent} | Threshold={settings.LowHpReturnTalismanThreshold} | SourceMapId={sourceMapId} | CurrentMapId={mapId} | Attempts={dispatchAttempts} | Reason=HP vượt ngưỡng trước khi map đổi");
				Reset();
				return false;
			}
			if (now < nextDispatchUtc) return true;
			if (dispatchAttempts >= MaximumDispatchAttempts) {
				log?.Invoke($"LOW_HP_GIVE_UP | Hp={snapshot.Hp}/{snapshot.MaxHp} | HpPercent={hpPercent} | SourceMapId={sourceMapId} | CurrentMapId={mapId} | Attempts={dispatchAttempts} | Action=STOP_AND_HOLD");
				state = TalismanState.Stopped;
				return true;
			}
			log?.Invoke($"LOW_HP_MAP_CHANGE_TIMEOUT | Hp={snapshot.Hp}/{snapshot.MaxHp} | HpPercent={hpPercent} | SourceMapId={sourceMapId} | CurrentMapId={mapId} | WaitMilliseconds={DispatchRetryMilliseconds} | Attempts={dispatchAttempts}/{MaximumDispatchAttempts} | Action=RETRY_COMMAND_SEQUENCE");
		}
		if (! belowThreshold) return false;
		// Không bắn bùa khi đang đứng ở đúng map thành mà bùa đã đưa tới trong phiên chạy này.
		if (townMapId > 0 && mapId == townMapId) {
			LogFailureOnce(log, "SKIPPED_IN_TOWN", $"LOW_HP_SKIPPED_IN_TOWN | Hp={snapshot.Hp}/{snapshot.MaxHp} | HpPercent={hpPercent} | TownMapId={townMapId}");
			return false;
		}
		if (! TryFindTalisman(processId, out int memoryIndex, out int container, out int itemId, out string itemName, out string findError)) {
			LogFailureOnce(log, "NOT_FOUND", $"LOW_HP_NOT_FOUND | Hp={snapshot.Hp}/{snapshot.MaxHp} | HpPercent={hpPercent} | Threshold={settings.LowHpReturnTalismanThreshold} | Reason={findError}");
			return false;
		}
		if (itemId > MaximumPackedItemId) {
			LogFailureOnce(log, "ITEM_ID_UNSUPPORTED", $"LOW_HP_ITEM_ID_UNSUPPORTED | ItemId={itemId} | Container={container} | MemoryIndex={memoryIndex}");
			return false;
		}
		// Bùa PHẢI nằm ở ô trang bị nhanh. Đây là ràng buộc đã chấp nhận, không phải thiếu sót tạm thời.
		//
		// Lệnh 310 cũ đã bị gỡ: đo trên PID 22056 ngày 2026-09-09 nó chạy trọn native tới return 1 nhưng game KHÔNG
		// trừ bùa (SốLượng 1 -> 1) và không đổi map, trong khi phím tắt cho SốLượng 1 -> 0 và MapId 37 -> 21 cùng
		// tài khoản. Sau đó đã dò tìm hàm dùng vật phẩm thật của client để dùng được đồ trong túi 35 ô và rương 2;
		// dò ra đối tượng quản lý túi đồ (root+0x41D44, mảng container ở +0x9A78 = Inventory.Object 0x4B7BC) cùng
		// nhiều hàm phụ trợ, nhưng KHÔNG tìm ra hàm dùng vật phẩm. Hướng đó đã dừng hẳn ngày 2026-09-09 theo quyết
		// định của chủ dự án, và probe dò hàm đã bị xoá. Đừng mở lại nếu chưa có bằng chứng mới.
		if (container != AutoFsAttackTransport.QuickSlotContainer || memoryIndex >= AutoFsAttackTransport.QuickSlotCount) {
			LogFailureOnce(log, "NOT_IN_QUICK_SLOT", $"LOW_HP_NOT_IN_QUICK_SLOT | Hp={snapshot.Hp}/{snapshot.MaxHp} | HpPercent={hpPercent} | Container={container} | MemoryIndex={memoryIndex} | Reason=Bùa phải nằm trong {AutoFsAttackTransport.QuickSlotCount} ô trang bị nhanh mới dùng được.");
			return false;
		}
		if (! transport.TryUseQuickSlotHotkey(gameWindowHandle, memoryIndex, out string sendError)) {
			LogFailureOnce(log, "SEND_FAILED", $"LOW_HP_SEND_FAILED | Hp={snapshot.Hp}/{snapshot.MaxHp} | HpPercent={hpPercent} | ItemId={itemId} | Container={container} | MemoryIndex={memoryIndex} | Reason={sendError}");
			return false;
		}
		lastFailure = "";
		state = TalismanState.WaitingMapChange;
		sourceMapId = mapId;
		dispatchAttempts++;
		nextDispatchUtc = now.AddMilliseconds(DispatchRetryMilliseconds);
		log?.Invoke($"LOW_HP_COMMAND_SEQUENCE_POSTED | Hp={snapshot.Hp}/{snapshot.MaxHp} | HpPercent={hpPercent} | Threshold={settings.LowHpReturnTalismanThreshold} | Name={itemName} | ItemId={itemId} | MemoryIndex={memoryIndex} | Container={container} | Method=QUICK_SLOT_HOTKEY | Key={memoryIndex + 1} | MapChangeConfirmed=False | Acceptance=NATIVE_ACCEPTED | RetryDelayMs={DispatchRetryMilliseconds} | Attempts={dispatchAttempts}/{MaximumDispatchAttempts} | SourceMapId={mapId}");
		return true;
	}

	// Sau khi về thành: cứ 5 giây kiểm tra HP một lần, chỉ cần HP đã tăng là cho lên bãi ngay,
	// còn HP đứng yên đủ 5 lần liên tiếp thì dừng hẳn và đứng im.
	private bool TickHpRecovery(GameSnapshot snapshot, int mapId, DateTime now, Action<string>? log) {
		if (now < nextHpCheckUtc) return true;
		nextHpCheckUtc = now.AddMilliseconds(HpCheckIntervalMilliseconds);
		if (snapshot.Hp > lastCheckedHp || snapshot.Hp >= snapshot.MaxHp) {
			log?.Invoke($"LOW_HP_HP_RECOVERING | Hp={snapshot.Hp}/{snapshot.MaxHp} | PreviousHp={lastCheckedHp} | TownMapId={townMapId} | CurrentMapId={mapId} | Action=REQUEST_RETURN_TO_TRAINING");
			int rememberedTownMapId = townMapId;
			Reset();
			townMapId = rememberedTownMapId;
			state = TalismanState.ArmingAfterReturn;
			returnToTrainingRequested = true;
			return false;
		}
		stalledHpChecks++;
		if (stalledHpChecks >= MaximumStalledHpChecks) {
			log?.Invoke($"LOW_HP_HP_STALLED | Hp={snapshot.Hp}/{snapshot.MaxHp} | PreviousHp={lastCheckedHp} | StalledChecks={stalledHpChecks}/{MaximumStalledHpChecks} | IntervalMs={HpCheckIntervalMilliseconds} | Action=STOP_AND_HOLD");
			state = TalismanState.Stopped;
			return true;
		}
		log?.Invoke($"LOW_HP_HP_UNCHANGED | Hp={snapshot.Hp}/{snapshot.MaxHp} | PreviousHp={lastCheckedHp} | StalledChecks={stalledHpChecks}/{MaximumStalledHpChecks} | IntervalMs={HpCheckIntervalMilliseconds}");
		lastCheckedHp = snapshot.Hp;
		return true;
	}

	public void Reset() {
		state = TalismanState.Idle;
		sourceMapId = 0;
		townMapId = 0;
		nextDispatchUtc = DateTime.MinValue;
		dispatchAttempts = 0;
		nextHpCheckUtc = DateTime.MinValue;
		lastCheckedHp = 0;
		stalledHpChecks = 0;
		nextHoldReminderUtc = DateTime.MinValue;
		returnToTrainingRequested = false;
		lastFailure = "";
	}

	private enum TalismanState {
		Idle,
		WaitingMapChange,
		RecoveringHp,
		ArmingAfterReturn,
		Stopped
	}

	private readonly record struct TalismanCandidate(int Slot, int Container, int ItemId, string Name);

	// Tìm vật phẩm theo đúng thứ tự container 11, 3 và 16 của AutoFS.
	// internal chứ không private để DebugTools/ReturnTalismanProbe gọi được ĐÚNG hàm này thay vì chép lại:
	// probe chép lại thì chỉ chứng minh bản chép chạy được, không chứng minh code đang ship chạy được.
	internal static bool TryFindTalisman(int processId, out int memoryIndex, out int container, out int itemId, out string itemName, out string error) {
		return TryFindTalisman(processId, out memoryIndex, out container, out itemId, out itemName, out error, out _);
	}

	// Bản có thêm danh sách ứng viên, cho DebugTools kiểm được thứ tự ưu tiên mà KHÔNG phải dùng thử bùa.
	// Không có nó thì probe chỉ thấy con được chọn, và một kết quả đúng cũng không phân biệt được với code cũ.
	internal static bool TryFindTalisman(int processId, out int memoryIndex, out int container, out int itemId, out string itemName, out string error, out List<string> candidateSummaries) {
		candidateSummaries = [];
		memoryIndex = -1;
		container = 0;
		itemId = 0;
		itemName = "";
		error = "";
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			IntPtr inventoryRoot = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.InventoryRoot));
			IntPtr itemTable = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.ItemTable));
			if (inventoryRoot == IntPtr.Zero || itemTable == IntPtr.Zero) {
				error = "InventoryRoot hoặc ItemTable bằng 0.";
				return false;
			}
			IntPtr inventoryObject = IntPtr.Add(inventoryRoot, GameAddresses.Inventory.Object);
			List<string> scannedItems = [];
			List<TalismanCandidate> candidates = [];
			foreach (InventoryContainer inventoryContainer in InventoryContainer.SearchOrder) {
				// Cô lập từng container và từng slot để dữ liệu rỗng không làm hỏng toàn bộ lượt quét.
				try {
					IntPtr slotList = reader.ReadPointer32(IntPtr.Add(inventoryObject, inventoryContainer.ListPointerOffset));
					if (slotList == IntPtr.Zero) continue;
					for (int index = 0; index < inventoryContainer.SlotCount; index++) {
						try {
							int currentItemId = reader.ReadInt32(IntPtr.Add(slotList, index * sizeof(int)));
							if (currentItemId <= 0) continue;
							if (currentItemId > MaximumPackedItemId) {
								scannedItems.Add($"C{inventoryContainer.Number}#{index}:{currentItemId}:INVALID_ID");
								continue;
							}
							long recordAddress = checked(itemTable.ToInt64() + checked((long)currentItemId * GameAddresses.Item.InventoryRecordStride));
							long nameAddress = checked(recordAddress + GameAddresses.Item.InventoryName);
							if (recordAddress <= 0 || nameAddress <= 0 || nameAddress > uint.MaxValue) {
								scannedItems.Add($"C{inventoryContainer.Number}#{index}:{currentItemId}:INVALID_ADDRESS");
								continue;
							}
							string currentName = InventoryContainer.ReadLegacyString(reader, new IntPtr(nameAddress), MaximumNameLength);
							scannedItems.Add($"C{inventoryContainer.Number}#{index}:{currentItemId}:{currentName}");
							if (currentName.Contains(ReturnTalismanNameFragment, StringComparison.OrdinalIgnoreCase)) {
								candidates.Add(new TalismanCandidate(index, inventoryContainer.Number, currentItemId, currentName));
							}
						} catch (Exception ex) {
							scannedItems.Add($"C{inventoryContainer.Number}#{index}:READ_FAIL:{ex.GetType().Name}");
						}
					}
				} catch (Exception ex) {
					scannedItems.Add($"C{inventoryContainer.Number}:CONTAINER_FAIL:{ex.GetType().Name}");
				}
			}
			if (candidates.Count > 0) {
				// Không xếp hạng: tìm thấy bùa ở đâu dùng ở đó, theo đúng thứ tự quét container 11, 3, 16.
				TalismanCandidate best = candidates[0];
				foreach (TalismanCandidate candidate in candidates) {
					bool chosen = candidate.Slot == best.Slot && candidate.Container == best.Container;
					candidateSummaries.Add($"C{candidate.Container}#{candidate.Slot} {candidate.Name}{(chosen ? "  <== CHỌN" : "")}");
				}
				memoryIndex = best.Slot;
				container = best.Container;
				itemId = best.ItemId;
				itemName = best.Name;
				return true;
			}
			error = $"Không có Hồi thành phù hoặc Hồi thành phù (Siêu cấp) trong container 11, 3 hoặc 16. Items=[{string.Join(";", scannedItems)}]";
			return false;
		} catch (Exception ex) {
			error = $"{ex.GetType().Name}: {ex.Message}";
			return false;
		}
	}

	// Chống lặp theo KHÓA ỔN ĐỊNH chứ không theo cả dòng log.
	//
	// Bản cũ so sánh nguyên văn dòng log, mà mọi dòng đều nhúng Hp hiện tại — HP nhích một điểm là chuỗi khác,
	// nên nó ghi lại MỖI NHỊP thay vì một lần. Nặng nhất là LOW_HP_NOT_FOUND: findError kèm danh sách toàn bộ
	// vật phẩm trong ba container, đổ ra liên tục suốt thời gian máu dưới ngưỡng mà không còn bùa.
	// Chủ dự án xác nhận 2026-09-09 ca hết bùa là chuyện thường gặp, nên đây là đường log nóng.
	private void LogFailureOnce(Action<string>? log, string key, string failure) {
		if (string.Equals(lastFailure, key, StringComparison.Ordinal)) return;
		lastFailure = key;
		log?.Invoke(failure);
	}
}
