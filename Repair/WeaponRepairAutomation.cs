namespace Auto.Repair;

using Auto.Movement;
using System.Drawing;
using System.Runtime.InteropServices;
using Auto.Runtime;
using Auto.Sale;
using Auto.Utils;

public sealed class WeaponRepairAutomation {
	private const int ModalStateOffset = GameAddresses.Globals.ModalState;
	private const int ShopStateOffset = GameAddresses.Globals.ShopState;
	private const int MaximumRepairCommandAttempts = 3;
	private const int AutoFsDialogTransitionTimeoutMilliseconds = 1500;
	private const int AutoFsShopReadyTimeoutMilliseconds = 5000;
	private const int DoctorEntityLoadTimeoutMilliseconds = 15000;
	private const int MaximumDoctorClickAttempts = 6;
	private const int MaximumMovementFailures = 3;
	private const int StuckDetectionMilliseconds = 8000;
	private const int SaleTriggerCheckMilliseconds = 1000;
	private const int AutoFsDialogPollMilliseconds = 300;
	private const int AutoFsShopReadyPollMilliseconds = 500;
	private const double MinimumObservedMovement = 0.03;
	private const double DoctorRouteArrivalDistance = 1.5;
	private const double MinimumNavigationProgress = 0.08;
	private RepairState state;
	private DateTime deadlineUtc;
	private DateTime nextActionUtc;
	private DateTime nextMoveRefreshUtc;
	private DateTime nextProgressLogUtc;
	private DateTime nextDoctorEntityLogUtc;
	private int mapId;
	private int doctorRawX;
	private int doctorRawY;
	private int returnRawX;
	private int returnRawY;
	private int doctorClickAttempts;
	private int menuProfileIndex;
	private int repairClickAttempts;
	private uint durabilityBeforeRepair;
	private uint pendingSuccessfulDurability;
	private int successConfirmationCount;
	private RepairState lastLoggedState = RepairState.Idle;
	private bool shopOpened;
	private bool interactionLocked;
	private bool repairConfirmationObserved;
	private bool repairCompletionConfirmed;
	private uint handledDoctorModalState;
	private int navigationTargetRawX;
	private int navigationTargetRawY;
	private int lastObservedRawX;
	private int lastObservedRawY;
	private double bestNavigationDistance;
	private DateTime lastMovementUtc;
	private int movementFailures;
	private bool debugMode;
	private bool debugRunRequested;
	private bool saleRequestPending;
	private bool runSaleThisVisit;
	private bool runRepairThisVisit;
	private bool repairRequestedThisVisit;
	private DateTime nextSaleTriggerCheckUtc;
	private string lastSaleTriggerState = "";

	public bool IsBusy => state != RepairState.Idle;

	public string RequestDebugRun() {
		if (state == RepairState.PausedAtDoctor) Reset();
		if (IsBusy || debugRunRequested) return "DEBUG Sửa đồ chưa thể chạy vì flow hiện tại đang bận.";
		debugRunRequested = true;
		return "DEBUG Sửa đồ đã xếp lịch cho account đang chọn.";
	}

	public bool Tick(GameWindow game, GameSnapshot snapshot, Action<string>? log) {
		if (state == RepairState.Idle) {
			if (debugRunRequested) {
				if (! game.RuntimeLayout.RepairReady) {
					debugRunRequested = false;
					log?.Invoke("DEBUG Sửa đồ bị chặn an toàn | " + game.RuntimeLayout.DescribeUnavailable(RuntimeSubsystem.Map, RuntimeSubsystem.Shop, RuntimeSubsystem.RepairTransport));
					return false;
				}
				debugRunRequested = false;
				debugMode = true;
				EquippedWeaponDurabilityReading reading = game.WeaponRepairMonitor.ReadFresh(game.ProcessId);
				durabilityBeforeRepair = reading.Success ? reading.Current : 0;
				runSaleThisVisit = game.InventorySaleEngine.IsAutomaticSaleEnabled;
				runRepairThisVisit = true;
				repairRequestedThisVisit = true;
				log?.Invoke($"DEBUG Sửa đồ bắt đầu theo nút Chạy | Durability={(reading.Success ? reading.Current : 0)} | Reading={(reading.Success ? "OK" : reading.FailureReason)}");
			} else {
				debugMode = false;
				RefreshSaleRequest(game, log);
				uint requestedDurability = 0;
				bool repairRequested = game.RuntimeLayout.RepairReady && game.WeaponRepairMonitor.TryTakeRepairRequest(out requestedDurability);
				if (! saleRequestPending && ! repairRequested) return false;
				if (saleRequestPending && ! game.RuntimeLayout.SaleReady) return false;
				runSaleThisVisit = game.InventorySaleEngine.IsAutomaticSaleEnabled;
				runRepairThisVisit = game.BasicSettings.EnableWeaponRepair && game.RuntimeLayout.RepairReady;
				repairRequestedThisVisit = repairRequested;
				if (repairRequested) durabilityBeforeRepair = requestedDurability;
				else if (runRepairThisVisit) {
					EquippedWeaponDurabilityReading reading = game.WeaponRepairMonitor.ReadFresh(game.ProcessId);
					durabilityBeforeRepair = reading.Success ? reading.Current : 0;
					log?.Invoke($"Dịch vụ NPC ghép Bán/Sửa | Bán kích hoạt, chuẩn bị sửa cùng chuyến | Durability={(reading.Success ? reading.Current : 0)} | Reading={(reading.Success ? "OK" : reading.FailureReason)}");
				}
				saleRequestPending = false;
			}
			return Start(game, snapshot, log);
		}

		if (!snapshot.Success) return true;
		GameMapInfo currentMap = GameMapReader.Read(game.ProcessId);
		if (!currentMap.Success || currentMap.MapId != mapId) {
			if (debugMode && state == RepairState.PausedAtDoctor) return true;
			string currentMapDetail = currentMap.Success ? currentMap.MapId.ToString() : "UNKNOWN:" + currentMap.FailureReason;
			return AbortAndRetry(game, $"Map thay đổi trong lúc sửa đồ | Map={mapId}->{currentMapDetail}", log);
		}
		LogState(log);

		switch (state) {
			case RepairState.MovingToDoctor:
				double doctorDistance = GetDistance(snapshot.X, snapshot.Y, doctorRawX, doctorRawY);
				ObserveNavigationProgress(snapshot.X, snapshot.Y, doctorRawX, doctorRawY);
				if (doctorDistance <= DoctorRouteArrivalDistance) {
					state = RepairState.ClickingDoctor;
					nextActionUtc = DateTime.UtcNow.AddMilliseconds(1500);
					deadlineUtc = DateTime.UtcNow.AddMilliseconds(DoctorEntityLoadTimeoutMilliseconds);
					nextDoctorEntityLogUtc = DateTime.MinValue;
				}
				if (state == RepairState.MovingToDoctor && doctorDistance > DoctorRouteArrivalDistance && DateTime.UtcNow >= nextMoveRefreshUtc && HasNavigationStalled()) {
					nextMoveRefreshUtc = DateTime.UtcNow.AddMilliseconds(StuckDetectionMilliseconds);
					if (!TryAutoFsMovement(game, doctorRawX, doctorRawY, out string moveResult)) {
						movementFailures++;
						if (movementFailures >= MaximumMovementFailures) return Fail(game, $"Không thể tiếp tục đi tới Đại Phu sau {movementFailures} lần thử | " + moveResult, log);
						nextMoveRefreshUtc = DateTime.UtcNow.AddMilliseconds(500);
						log?.Invoke($"Sửa đồ | lệnh di chuyển lỗi thoáng qua, sẽ thử lại | Lần={movementFailures}/{MaximumMovementFailures} | {moveResult}");
					} else {
						movementFailures = 0;
						lastMovementUtc = DateTime.UtcNow;
						log?.Invoke($"Sửa đồ | đứng yên 8 giây, đã gửi lại nguyên tuyến tới Đại Phu | Đích={doctorRawX}/{doctorRawY} | {moveResult}");
					}
				}
				if (DateTime.UtcNow >= nextProgressLogUtc) {
					nextProgressLogUtc = DateTime.UtcNow.AddSeconds(10);
					log?.Invoke($"Sửa đồ | đang đi tới Đại Phu | HiệnTại={snapshot.X}/{snapshot.Y} | Còn={doctorDistance:F2}");
				}
				break;
			case RepairState.ClickingDoctor:
				if (DateTime.UtcNow < nextActionUtc) break;
				double alignmentDistance = GetDistance(snapshot.X, snapshot.Y, doctorRawX, doctorRawY);
				if (alignmentDistance > DoctorRouteArrivalDistance) {
					bool moveSent = TryAutoFsMovement(game, doctorRawX, doctorRawY, out string moveResult);
					if (!moveSent) return Fail(game, "Bị lệch khỏi Đại Phu và không thể quay lại | " + moveResult, log);
					InitializeNavigation(snapshot.X, snapshot.Y, doctorRawX, doctorRawY);
					state = RepairState.MovingToDoctor;
					nextMoveRefreshUtc = DateTime.UtcNow.AddMilliseconds(StuckDetectionMilliseconds);
					log?.Invoke($"Sửa đồ | vị trí đã lệch, đang quay lại Đại Phu | HiệnTại={snapshot.X}/{snapshot.Y}");
					break;
				}
				if (!RuntimeEntityLocator.TryFindNamedEntity(game.ProcessId, "Đại phu", doctorRawX, doctorRawY, out RuntimeEntityLocation doctor, out string findReason)) {
					if (HasTimedOut()) return AbortAndRetry(game, $"Không thấy entity Đại Phu sau {DoctorEntityLoadTimeoutMilliseconds}ms | {findReason}", log);
					nextActionUtc = DateTime.UtcNow.AddSeconds(1);
					if (DateTime.UtcNow >= nextDoctorEntityLogUtc) {
						nextDoctorEntityLogUtc = DateTime.UtcNow.AddSeconds(5);
						log?.Invoke("Sửa đồ | đã tới nơi, đang chờ entity Đại Phu tải | " + findReason);
					}
					break;
				}
				ClickDoctor(game, snapshot, doctor, log);
				break;
			case RepairState.WaitingDialog:
				if (DateTime.UtcNow < nextActionUtc) break;
				ReadShopState(game.ProcessId, out uint openingModalState, out uint openingShopState);
				if (openingModalState == 0 && openingShopState == 2) {
					handledDoctorModalState = 0;
					interactionLocked = true;
					shopOpened = true;
					repairClickAttempts = 0;
					state = runSaleThisVisit ? RepairState.SellingItems : RepairState.ClickingRepair;
					if (runSaleThisVisit) game.InventorySaleEngine.Start(log);
					nextActionUtc = DateTime.UtcNow.AddMilliseconds(500);
					log?.Invoke($"Sửa đồ | Đại Phu đã mở trực tiếp cửa hàng, bỏ qua menu hội thoại | ShopState={openingShopState}");
				} else if (handledDoctorModalState != 0 && openingModalState == handledDoctorModalState) {
					if (HasTimedOut()) return Fail(game, $"Popup Đại Phu không chuyển trạng thái theo chu kỳ AutoFS | PreviousModal=0x{handledDoctorModalState:X8} | CurrentModal=0x{openingModalState:X8} | ShopState={openingShopState}.", log);
					if (! game.AutoFsTransport.TrySendCommand(game.Handle, 7, -1, out string retryResult)) return Fail(game, "Không gửi lại được command 7/-1 trong chu kỳ popup Đại Phu | " + retryResult, log);
					nextActionUtc = DateTime.UtcNow.AddMilliseconds(AutoFsDialogPollMilliseconds);
				} else if (handledDoctorModalState != 0 && openingModalState == 0) {
					log?.Invoke($"Sửa đồ | popup Đại Phu đã chuyển trạng thái theo AutoFS | PreviousModal=0x{handledDoctorModalState:X8} | CurrentModal=0x00000000 | đang chờ cửa hàng sẵn sàng");
					handledDoctorModalState = 0;
					state = RepairState.WaitingShop;
					nextActionUtc = DateTime.UtcNow.AddMilliseconds(AutoFsShopReadyPollMilliseconds);
					deadlineUtc = DateTime.UtcNow.AddMilliseconds(AutoFsShopReadyTimeoutMilliseconds);
				} else if (openingModalState != 0) {
					handledDoctorModalState = 0;
					interactionLocked = true;
					state = RepairState.SelectingShopAction;
					nextActionUtc = DateTime.UtcNow.AddMilliseconds(1200);
				} else if (HasTimedOut()) {
					if (doctorClickAttempts < MaximumDoctorClickAttempts) {
						state = RepairState.ClickingDoctor;
						nextActionUtc = DateTime.UtcNow.AddMilliseconds(1000);
						deadlineUtc = DateTime.UtcNow.AddSeconds(30);
						log?.Invoke($"Sửa đồ | chưa thấy hội thoại, chờ NPC tải và thử lại | Lần={doctorClickAttempts + 1}/{MaximumDoctorClickAttempts}");
					} else {
						return Fail(game, "Không mở được hội thoại Đại Phu.", log);
					}
				}
				break;
			case RepairState.SelectingShopAction:
				if (DateTime.UtcNow >= nextActionUtc) ClickShopAction(game, log);
				break;
			case RepairState.WaitingShop:
				if (DateTime.UtcNow < nextActionUtc) break;
				ReadShopState(game.ProcessId, out uint modalState, out uint shopState);
				if (modalState == 0 && shopState == 2) {
					shopOpened = true;
					repairClickAttempts = 0;
					if (runSaleThisVisit) {
						game.InventorySaleEngine.Start(log);
						state = RepairState.SellingItems;
					} else if (runRepairThisVisit) {
						state = RepairState.ClickingRepair;
					} else {
						state = RepairState.ClosingUiFirst;
					}
					nextActionUtc = DateTime.UtcNow.AddMilliseconds(500);
					log?.Invoke($"Sửa đồ | đã xác nhận cửa hàng nội bộ sẵn sàng | ShopState={shopState}");
				} else if (HasTimedOut()) {
					if (modalState == 0) return Fail(game, $"Hội thoại đã đóng nhưng cửa hàng nội bộ chưa sẵn sàng | ShopState={shopState}.", log);
					menuProfileIndex++;
					if (menuProfileIndex >= GetMenuProfileCount(game.Handle)) return Fail(game, "Không chọn được chức năng Mua bán/Xác định.", log);
					state = RepairState.SelectingShopAction;
					nextActionUtc = DateTime.UtcNow;
				} else {
					nextActionUtc = DateTime.UtcNow.AddMilliseconds(AutoFsShopReadyPollMilliseconds);
				}
				break;
			case RepairState.SellingItems:
				if (DateTime.UtcNow < nextActionUtc) break;
				InventorySaleTickResult saleResult = game.InventorySaleEngine.Tick(game.ProcessId, game.Handle, log, out string saleFailure);
				if (saleResult == InventorySaleTickResult.Failed) return Fail(game, "Bán đồ dừng an toàn | " + saleFailure, log);
				if (saleResult == InventorySaleTickResult.Completed) {
					state = runRepairThisVisit ? RepairState.ClickingRepair : RepairState.ClosingUiFirst;
					nextActionUtc = DateTime.UtcNow;
				}
				break;
			case RepairState.ClickingRepair:
				if (DateTime.UtcNow >= nextActionUtc) ClickRepair(game, log);
				break;
			case RepairState.WaitingRepairConfirm:
				if (ReadModalState(game.ProcessId) != 0) {
					repairConfirmationObserved = true;
					state = RepairState.ClickingConfirm;
					nextActionUtc = DateTime.UtcNow.AddMilliseconds(1200);
				} else if (HasTimedOut()) {
					if (debugMode) {
						log?.Invoke("DEBUG Sửa đồ | shop đã mở nhưng không xuất hiện xác nhận sửa; chấp nhận cho vũ khí không cần sửa.");
						state = RepairState.ClosingUiFirst;
						nextActionUtc = DateTime.UtcNow;
						break;
					}
					if (! repairRequestedThisVisit) {
						log?.Invoke("Sửa đồ cùng chuyến Bán | đã gửi hành động sửa nhưng không xuất hiện popup xác nhận; bỏ qua xác nhận và quay lại bãi.");
						state = RepairState.ClosingUiFirst;
						nextActionUtc = DateTime.UtcNow;
						break;
					}
					return Fail(game, "Không mở được xác nhận sửa đồ sau một lần click; giữ nguyên vị trí và giao diện hiện tại.", log);
				}
				break;
			case RepairState.ClickingConfirm:
				if (DateTime.UtcNow >= nextActionUtc) ClickConfirm(game, log);
				break;
			case RepairState.WaitingDurability:
				if (DateTime.UtcNow < nextActionUtc) break;
				EquippedWeaponDurabilityReading reading = game.WeaponRepairMonitor.ReadFresh(game.ProcessId);
				if (reading.Success && reading.Maximum > 0 && reading.Current == reading.Maximum) {
					repairCompletionConfirmed = true;
					log?.Invoke($"Sửa đồ hoàn tất | Độ bền đã đầy={reading.Current}/{reading.Maximum}; không yêu cầu giá trị tăng thêm.");
					state = RepairState.ClosingUiFirst;
					nextActionUtc = DateTime.UtcNow;
				} else if (reading.Success && reading.Current > durabilityBeforeRepair) {
					if (pendingSuccessfulDurability == reading.Current) successConfirmationCount++;
					else {
						pendingSuccessfulDurability = reading.Current;
						successConfirmationCount = 1;
					}
					if (successConfirmationCount >= 2) {
						repairCompletionConfirmed = true;
						log?.Invoke($"Sửa toàn bộ thành công | Độ bền thấp nhất={durabilityBeforeRepair}->{reading.Current} | {reading.Evidence} | XácNhận=2/2");
						state = RepairState.ClosingUiFirst;
						nextActionUtc = DateTime.UtcNow;
					} else {
						log?.Invoke($"Sửa toàn bộ | độ bền thấp nhất đã tăng, chờ xác nhận ổn định | Minimum={reading.Current} | {reading.Evidence} | XácNhận=1/2");
						nextActionUtc = DateTime.UtcNow.AddMilliseconds(500);
					}
				} else if (HasTimedOut()) {
					if (debugMode) {
						log?.Invoke($"DEBUG Sửa đồ hoàn tất thao tác | Độ bền không đổi như dự kiến | Current={(reading.Success ? reading.Current : 0)}");
						state = RepairState.ClosingUiFirst;
						nextActionUtc = DateTime.UtcNow;
						break;
					}
					return Fail(game, reading.Success ? $"Độ bền không tăng sau xác nhận | Current={reading.Current}" : reading.FailureReason, log);
				} else {
					pendingSuccessfulDurability = 0;
					successConfirmationCount = 0;
					nextActionUtc = DateTime.UtcNow.AddMilliseconds(300);
				}
				break;
			case RepairState.PausedAtDoctor:
				break;
			case RepairState.ClosingUiFirst:
				if (shopOpened && ! runRepairThisVisit) {
					BackgroundEscapeCommand.Run(game.ProcessId, game.Handle);
					log?.Invoke("Dịch vụ NPC | đã bán và chỉnh lý, gửi một ESC để đóng shop.");
				} else if (shopOpened && ! repairRequestedThisVisit && ! repairConfirmationObserved) {
					BackgroundEscapeCommand.Run(game.ProcessId, game.Handle);
					log?.Invoke("Dịch vụ NPC | đã kiểm tra sửa đồ cùng chuyến, không có xác nhận sửa; gửi một ESC để đóng shop.");
				} else if (debugMode && shopOpened) {
					BackgroundEscapeCommand.Run(game.ProcessId, game.Handle);
					log?.Invoke("DEBUG Sửa đồ | gửi một ESC để đóng shop sau khi hoàn tất test.");
				} else if (shopOpened && repairConfirmationObserved && repairCompletionConfirmed) {
					BackgroundEscapeCommand.Run(game.ProcessId, game.Handle);
					log?.Invoke("Sửa đồ | đã xác nhận thành công, gửi một ESC để đóng shop.");
				} else {
					log?.Invoke("Sửa đồ | không gửi ESC vì phiên shop/xác nhận sửa chưa được chứng minh đầy đủ.");
				}
				BeginReturn(game, snapshot, log);
				break;
			case RepairState.Returning:
				if (GetDistance(snapshot.X, snapshot.Y, returnRawX, returnRawY) <= 1.0) return Complete(game, log);
				ObserveNavigationProgress(snapshot.X, snapshot.Y, returnRawX, returnRawY);
				if (DateTime.UtcNow >= nextMoveRefreshUtc && HasNavigationStalled()) {
					nextMoveRefreshUtc = DateTime.UtcNow.AddMilliseconds(StuckDetectionMilliseconds);
					if (!TryAutoFsMovement(game, returnRawX, returnRawY, out string moveResult)) return Fail(game, "Không thể tiếp tục quay lại bãi | " + moveResult, log);
					lastMovementUtc = DateTime.UtcNow;
					log?.Invoke($"Sửa đồ | đứng yên 8 giây, đã gửi lại nguyên tuyến về bãi | Đích={returnRawX}/{returnRawY} | {moveResult}");
				}
				break;
		}
		return true;
	}

	public void Cancel(GameWindow game) {
		if (!IsBusy) return;
		game.InventorySaleEngine.Reset();
		Reset();
	}

	private void RefreshSaleRequest(GameWindow game, Action<string>? log) {
		if (! game.InventorySaleEngine.IsAutomaticSaleEnabled || DateTime.UtcNow < nextSaleTriggerCheckUtc) return;
		nextSaleTriggerCheckUtc = DateTime.UtcNow.AddMilliseconds(SaleTriggerCheckMilliseconds);
		InventorySaleTriggerReading reading = game.InventorySaleEngine.ReadAutomaticTrigger(game.ProcessId);
		string stateText = reading.Success
			? $"Occupied={reading.OccupiedSlots}/{GameAddresses.Inventory.SaleSlotCount} | Quantity={reading.OccupiedSlots}>{game.LootSettings.SaleQuantityThreshold}:{reading.QuantityTriggered} | RemainingStrength={reading.RemainingStrength?.ToString() ?? "UNAVAILABLE"}<{game.LootSettings.SaleRemainingStrengthThreshold}:{reading.StrengthTriggered} | Triggered={reading.Triggered}"
			: "FAIL | " + reading.FailureReason;
		if (! string.Equals(lastSaleTriggerState, stateText, StringComparison.Ordinal)) {
			lastSaleTriggerState = stateText;
			log?.Invoke("SALE_TRIGGER | " + stateText);
		}
		if (reading.Triggered) saleRequestPending = true;
	}

	private bool Start(GameWindow game, GameSnapshot snapshot, Action<string>? log) {
		GameMapInfo map = GameMapReader.Read(game.ProcessId);
		if (!map.Success || !map.HasDoctor) {
			log?.Invoke("Sửa đồ FAIL | " + (map.Success ? "Map hiện tại không có tọa độ Đại Phu." : map.FailureReason));
			Reset();
			return false;
		}
		mapId = map.MapId;
		doctorRawX = map.DoctorRawX;
		doctorRawY = map.DoctorRawY;
		returnRawX = game.AttackSettings.UseCenterPosition && game.AttackSettings.CenterX > 0 ? game.AttackSettings.CenterX : snapshot.X;
		returnRawY = game.AttackSettings.UseCenterPosition && game.AttackSettings.CenterY > 0 ? game.AttackSettings.CenterY : snapshot.Y;
		if (!TryAutoFsMovement(game, doctorRawX, doctorRawY, out string routeResult)) {
			log?.Invoke("Sửa đồ FAIL | Không khởi tạo được tuyến nội bộ tới Đại Phu | " + routeResult);
			Reset();
			return false;
		}
		InitializeNavigation(snapshot.X, snapshot.Y, doctorRawX, doctorRawY);
		doctorClickAttempts = 0;
		state = RepairState.MovingToDoctor;
		lastLoggedState = RepairState.Idle;
		nextMoveRefreshUtc = DateTime.UtcNow.AddMilliseconds(StuckDetectionMilliseconds);
		nextProgressLogUtc = DateTime.UtcNow.AddSeconds(10);
		log?.Invoke($"Sửa đồ bắt đầu | Map={map.MapId} | ĐạiPhu={map.DoctorRawX}/{map.DoctorRawY} | QuayLại={returnRawX}/{returnRawY}");
		log?.Invoke("Sửa đồ | đã gửi lệnh di chuyển AutoFS tới Đại Phu | " + routeResult);
		return true;
	}

	private void InitializeNavigation(int rawX, int rawY, int targetRawX, int targetRawY) {
		navigationTargetRawX = targetRawX;
		navigationTargetRawY = targetRawY;
		lastObservedRawX = rawX;
		lastObservedRawY = rawY;
		bestNavigationDistance = GetDistance(rawX, rawY, targetRawX, targetRawY);
		lastMovementUtc = DateTime.UtcNow;
		movementFailures = 0;
	}

	private void ObserveNavigationProgress(int rawX, int rawY, int targetRawX, int targetRawY) {
		double distance = GetDistance(rawX, rawY, targetRawX, targetRawY);
		if (navigationTargetRawX != targetRawX || navigationTargetRawY != targetRawY) {
			navigationTargetRawX = targetRawX;
			navigationTargetRawY = targetRawY;
			lastObservedRawX = rawX;
			lastObservedRawY = rawY;
			bestNavigationDistance = distance;
			lastMovementUtc = DateTime.UtcNow;
			return;
		}
		if (GetDistance(lastObservedRawX, lastObservedRawY, rawX, rawY) >= MinimumObservedMovement) {
			lastObservedRawX = rawX;
			lastObservedRawY = rawY;
			lastMovementUtc = DateTime.UtcNow;
		}
		if (distance <= bestNavigationDistance - MinimumNavigationProgress) bestNavigationDistance = distance;
	}

	private bool HasNavigationStalled() => (DateTime.UtcNow - lastMovementUtc).TotalMilliseconds >= StuckDetectionMilliseconds;

	private void ClickDoctor(GameWindow game, GameSnapshot snapshot, RuntimeEntityLocation doctor, Action<string>? log) {
		doctorClickAttempts++;
		log?.Invoke($"Sửa đồ | click entity Đại Phu | Index={doctor.Index} | Handle={doctor.Handle} | Raw={doctor.RawX}/{doctor.RawY} | LệchMap={doctor.DistanceToAnchor:F2}");
		bool clicked = FullMouseHandlerPickupCommand.TryClickRawPosition(game.ProcessId, game.Handle, snapshot.X, snapshot.Y, doctor.RawX, doctor.RawY, out _, out string result);
		if (!clicked) {
			if (doctorClickAttempts < MaximumDoctorClickAttempts) {
				bool moveSent = TryAutoFsMovement(game, doctorRawX, doctorRawY, out string moveResult);
				if (moveSent) {
					InitializeNavigation(snapshot.X, snapshot.Y, doctorRawX, doctorRawY);
					state = RepairState.MovingToDoctor;
					nextMoveRefreshUtc = DateTime.UtcNow.AddMilliseconds(900);
					nextProgressLogUtc = DateTime.UtcNow.AddSeconds(10);
				} else {
					state = RepairState.ClickingDoctor;
					nextActionUtc = DateTime.UtcNow.AddSeconds(1);
				}
				log?.Invoke($"Sửa đồ | click Đại Phu chưa hợp lệ, sẽ thử lại trong cùng chuyến | Lần={doctorClickAttempts}/{MaximumDoctorClickAttempts} | Click={result} | Move={moveResult}");
				return;
			}
			Fail(game, "Click Đại Phu thất bại | " + result, log);
			return;
		}
		state = RepairState.WaitingDialog;
		deadlineUtc = DateTime.UtcNow.AddSeconds(4);
	}

	private void ClickShopAction(GameWindow game, Action<string>? log) {
		uint invokedModalState = ReadModalState(game.ProcessId);
		if (!DoctorShopSemanticCommand.TryInvoke(game, out string semanticResult)) {
			Fail(game, "Không gọi được lệnh nội bộ mở shop | " + semanticResult, log);
			return;
		}
		log?.Invoke("Sửa đồ | đã gọi lệnh nội bộ mở shop | " + semanticResult);
		if (semanticResult.StartsWith("Doctor confirmation modal", StringComparison.Ordinal)) {
			handledDoctorModalState = invokedModalState;
			state = RepairState.WaitingDialog;
			nextActionUtc = DateTime.UtcNow.AddMilliseconds(AutoFsDialogPollMilliseconds);
			deadlineUtc = DateTime.UtcNow.AddMilliseconds(AutoFsDialogTransitionTimeoutMilliseconds);
			return;
		}
		state = RepairState.WaitingShop;
		nextActionUtc = DateTime.UtcNow.AddMilliseconds(1000);
		deadlineUtc = DateTime.UtcNow.AddSeconds(4);
	}

	private Point[] GetMenuProfiles(int clientWidth) {
		if (clientWidth >= 1000) {
			return mapId switch {
				13 => new[] { new Point(409, 418) },
				18 => new[] { new Point(519, 424) },
				11 => new[] { new Point(420, 474) },
				12 => new[] { new Point(421, 473) },
				21 => new[] { new Point(379, 393) },
				65 => new[] { new Point(409, 420) },
				10 => new[] { new Point(490, 385) },
				_ => new[] { new Point(379, 393) }
			};
		}
		return mapId switch {
			13 => new[] { new Point(320, 327) },
			18 => new[] { new Point(405, 331) },
			11 => new[] { new Point(328, 370) },
			12 => new[] { new Point(329, 370) },
			21 => new[] { new Point(296, 307) },
			65 => new[] { new Point(320, 328) },
			10 => new[] { new Point(383, 301) },
			_ => new[] { new Point(296, 307) }
		};
	}

	private int GetMenuProfileCount(IntPtr windowHandle) {
		return TryGetClientSize(windowHandle, out int width, out _) ? GetMenuProfiles(width).Length : 0;
	}

	private void ClickRepair(GameWindow game, Action<string>? log) {
		repairClickAttempts++;
		log?.Invoke($"Sửa đồ | gọi lệnh nội bộ Sửa toàn bộ | Lần={repairClickAttempts}/{MaximumRepairCommandAttempts}");
		if (!FullMouseHandlerPickupCommand.TryOpenRepairAllConfirmation(game.ProcessId, game.Handle, out string result)) {
			if (repairClickAttempts < MaximumRepairCommandAttempts) {
				log?.Invoke($"Sửa đồ | lệnh mở xác nhận chưa sẵn sàng, sẽ thử lại sau 1000ms | {result}");
				nextActionUtc = DateTime.UtcNow.AddMilliseconds(1000);
				return;
			}
			if (debugMode) {
				log?.Invoke($"DEBUG Sửa đồ | shop đã mở; lệnh xác nhận trả về 0 sau {MaximumRepairCommandAttempts} lần, chấp nhận cho vũ khí không cần sửa | {result}");
				state = RepairState.ClosingUiFirst;
				nextActionUtc = DateTime.UtcNow;
				return;
			}
			if (! repairRequestedThisVisit) {
				log?.Invoke($"Sửa đồ cùng chuyến Bán | đã gửi hành động sửa {MaximumRepairCommandAttempts} lần nhưng không phát sinh popup xác nhận; bỏ qua xác nhận và quay lại bãi | {result}");
				state = RepairState.ClosingUiFirst;
				nextActionUtc = DateTime.UtcNow;
				return;
			}
			Fail(game, $"Lệnh nội bộ mở xác nhận sửa thất bại sau {MaximumRepairCommandAttempts} lần | " + result, log);
			return;
		}
		log?.Invoke("Sửa đồ | đã gửi lệnh nội bộ Sửa toàn bộ và mở xác nhận | " + result);
		state = RepairState.WaitingRepairConfirm;
		deadlineUtc = DateTime.UtcNow.AddSeconds(6);
	}

	private void ClickConfirm(GameWindow game, Action<string>? log) {
		log?.Invoke("Sửa đồ | gửi command 7/-1 theo AutoFS để xác nhận sửa đồ");
		if (!game.AutoFsTransport.TrySendCommand(game.Handle, 7, -1, out string result)) {
			Fail(game, "Không gửi được command 7/-1 xác nhận sửa đồ | " + result, log);
			return;
		}
		game.WeaponRepairMonitor.ResetCache();
		state = RepairState.WaitingDurability;
		deadlineUtc = DateTime.UtcNow.AddSeconds(debugMode ? 3 : 10);
		nextActionUtc = DateTime.UtcNow.AddMilliseconds(350);
	}

	private void BeginReturn(GameWindow game, GameSnapshot snapshot, Action<string>? log) {
		interactionLocked = false;
		bool invalidReturnPoint = returnRawX <= 0 || returnRawY <= 0;
		string result = "";
		if (invalidReturnPoint || !TryAutoFsMovement(game, returnRawX, returnRawY, out result)) {
			log?.Invoke("Sửa đồ hoàn tất nhưng không thể quay lại bãi | " + (invalidReturnPoint ? "Tâm bãi không hợp lệ." : result));
			Complete(game, log);
			return;
		}
		InitializeNavigation(snapshot.X, snapshot.Y, returnRawX, returnRawY);
		state = RepairState.Returning;
		nextMoveRefreshUtc = DateTime.UtcNow.AddMilliseconds(StuckDetectionMilliseconds);
		log?.Invoke("Sửa đồ | đã gửi lệnh di chuyển AutoFS để quay lại bãi | " + result);
	}

	private static bool TryAutoFsMovement(GameWindow game, int destinationRawX, int destinationRawY, out string result) {
		return AutoFsMovementCommand.TryMoveTo(game, destinationRawX, destinationRawY, out result);
	}

	private bool Complete(GameWindow game, Action<string>? log) {
		game.InventorySaleEngine.Reset();
		if (repairCompletionConfirmed) game.WeaponRepairMonitor.ConfirmRepairCompleted();
		game.WeaponRepairMonitor.ResetCache();
		game.WeaponRepairMonitor.ScheduleImmediateCheck();
		log?.Invoke(debugMode
			? $"DEBUG Sửa đồ hoàn tất | Đã quay lại {returnRawX}/{returnRawY}."
			: $"Sửa đồ hoàn tất | Đã quay lại {returnRawX}/{returnRawY}.");
		Reset();
		return true;
	}

	private bool Fail(GameWindow game, string reason, Action<string>? log) {
		game.InventorySaleEngine.Reset();
		if (debugMode) {
			log?.Invoke("DEBUG Sửa đồ dừng an toàn | " + reason + (interactionLocked ? " | Đang giữ nguyên tại NPC; đổi map rồi bấm Chạy để test tiếp." : ""));
			if (interactionLocked) {
				state = RepairState.PausedAtDoctor;
						return true;
			}
			Reset();
			return true;
		}
		if (interactionLocked) {
			log?.Invoke("Sửa đồ FAIL | " + reason + " | Đã khóa tại NPC; tắt rồi bật lại Sửa đồ để thử lại.");
			state = RepairState.PausedAtDoctor;
				return true;
		}
		if (repairRequestedThisVisit) {
			log?.Invoke("Sửa đồ FAIL | " + reason + " | Giữ quyền điều khiển tại Đại Phu vì độ bền chưa được xác nhận đã sửa; không cho phép quay lại bãi.");
			state = RepairState.PausedAtDoctor;
				return true;
		}
		log?.Invoke("Sửa đồ FAIL | " + reason);
		Reset();
		return true;
	}

	// Giải phóng quyền điều khiển và xếp lại kiểm tra thay vì giữ account vô hạn trong flow lỗi.
	private bool AbortAndRetry(GameWindow game, string reason, Action<string>? log) {
		game.InventorySaleEngine.Reset();
		if (repairRequestedThisVisit) {
			game.WeaponRepairMonitor.ResetCache();
			game.WeaponRepairMonitor.ScheduleImmediateCheck("sau khi luồng sửa đồ bị gián đoạn");
		}
		log?.Invoke("Sửa đồ hoãn | " + reason + " | Đã giải phóng quyền điều khiển và xếp lại kiểm tra.");
		Reset();
		return false;
	}

	private static uint ReadModalState(int processId) {
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase("Game.exe");
			return unchecked((uint)reader.ReadInt32(IntPtr.Add(moduleBase, ModalStateOffset)));
		} catch {
			return 0;
		}
	}

	private static void ReadShopState(int processId, out uint modalState, out uint shopState) {
		modalState = 0;
		shopState = 0;
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			modalState = unchecked((uint)reader.ReadInt32(IntPtr.Add(moduleBase, ModalStateOffset)));
			shopState = unchecked((uint)reader.ReadInt32(IntPtr.Add(moduleBase, ShopStateOffset)));
		} catch {
		}
	}

	private static double GetDistance(int rawX1, int rawY1, int rawX2, int rawY2) {
		double dx = (rawX2 - rawX1) / 256.0;
		double dy = (rawY2 - rawY1) / 512.0;
		return Math.Sqrt(dx * dx + dy * dy);
	}

	private bool HasTimedOut() => DateTime.UtcNow >= deadlineUtc;

	private void LogState(Action<string>? log) {
		if (state == lastLoggedState) return;
		lastLoggedState = state;
		string text = state switch {
			RepairState.MovingToDoctor => "đang di chuyển tới Đại Phu",
			RepairState.ClickingDoctor => "đã tới Đại Phu, chuẩn bị mở hội thoại",
			RepairState.WaitingDialog => "đang chờ hội thoại Đại Phu",
			RepairState.SelectingShopAction => "đang chọn Mua bán/Xác định",
			RepairState.WaitingShop => "đang chờ cửa hàng",
			RepairState.SellingItems => "đang bán vật phẩm đã chọn",
			RepairState.ClickingRepair => "đang mở xác nhận sửa đồ",
			RepairState.WaitingRepairConfirm => "đang chờ xác nhận sửa đồ",
			RepairState.ClickingConfirm => "đang xác nhận sửa đồ",
			RepairState.WaitingDurability => "đang kiểm tra độ bền sau sửa",
			RepairState.ClosingUiFirst => "đang đóng giao diện Đại Phu",
			RepairState.Returning => "đang quay lại bãi",
			_ => ""
		};
		if (text.Length > 0) log?.Invoke("Sửa đồ | " + text);
	}

	private void Reset() {
		state = RepairState.Idle;
		lastLoggedState = RepairState.Idle;
		nextActionUtc = DateTime.MinValue;
		nextMoveRefreshUtc = DateTime.MinValue;
		nextProgressLogUtc = DateTime.MinValue;
		nextDoctorEntityLogUtc = DateTime.MinValue;
		shopOpened = false;
		interactionLocked = false;
		repairConfirmationObserved = false;
		repairCompletionConfirmed = false;
		handledDoctorModalState = 0;
		pendingSuccessfulDurability = 0;
		successConfirmationCount = 0;
		movementFailures = 0;
		doctorClickAttempts = 0;
		debugMode = false;
		debugRunRequested = false;
		saleRequestPending = false;
		runSaleThisVisit = false;
		runRepairThisVisit = false;
		repairRequestedThisVisit = false;
	}

	private static bool TryGetClientSize(IntPtr windowHandle, out int width, out int height) {
		width = 0;
		height = 0;
		if (!GetClientRect(windowHandle, out NativeRect rect)) return false;
		width = rect.Right - rect.Left;
		height = rect.Bottom - rect.Top;
		return width > 0 && height > 0;
	}

	private enum RepairState {
		Idle,
		MovingToDoctor,
		ClickingDoctor,
		WaitingDialog,
		SelectingShopAction,
		WaitingShop,
		SellingItems,
		ClickingRepair,
		WaitingRepairConfirm,
		ClickingConfirm,
		WaitingDurability,
		PausedAtDoctor,
		ClosingUiFirst,
		Returning
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativeRect {
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool GetClientRect(IntPtr windowHandle, out NativeRect rect);
}
