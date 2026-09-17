namespace Auto.Quest;

using Auto.DebugTools;
using Auto.Movement;
using Auto.Runtime;
using Auto.Utils;

// Nhiệm vụ Thám Quân. Một lượt gồm ba chặng, lặp tối đa ba lượt rồi dừng:
//
//   1. NHẬN   ở Hoàng Thiên Hóa (map 21) — menu 3 tầng, tầng 3 xác nhận, rồi ESC. Popup cuối ghi map đích.
//   2. GIAO   ở Đại phu tại map đích     — chỉ click NPC, hiện popup, chỉ cần ESC, KHÔNG có bước xác nhận.
//   3. TRẢ    ở Hoàng Thiên Hóa          — đúng chuỗi thao tác như chặng 1.
//
// Mọi mốc dưới đây đều lấy từ quan sát thật trên client 2026-08, không suy từ AutoFS:
//   - NPC "Hoàng Thiên Hóa" ở map 21, entity raw 54980/94428; toạ độ neo 54923/94515 lấy từ Data\Maps\21.map.
//     (probe QUEST-20260909-01, PID 14236)
//   - Menu tầng 1 có "#0 Thám quân / #1 Kế Ly Gián / #2 Sơ Hiện Đoan Nghê"; chọn bằng command 7/index.
//   - Tầng 2 chỉ có một mục "Thám quân"; ĐỌC KHÔNG ĐƯỢC nên phải bấm mù command 7/0 (xem ghi chú ở state đó).
//   - Tầng 3 là popup xác nhận "Xác định"/"Hủy", vtable RVA 0x46AF3C, xác nhận bằng command 7/-1.
//   - Popup sau xác nhận ghi: "Lần này ta cần ngươi đi ⟦<tên map>⟧ tìm Đại phu thu thập tin tức."
//     Tên map được tô màu, bọc giữa byte 0x01 + 4 byte màu và byte 0x02. NPC đích LUÔN là "Đại phu".
//
// Không lưu tiến trình ra đĩa. Đọc không ra map đích thì DỪNG và ghi log, không đi mò — xem ghi chú ở
// ReadQuestDestination.
public sealed class ScoutQuestAutomation {
	private const int QuestNpcMapId = 21;
	private const int QuestNpcRawX = 54923;
	private const int QuestNpcRawY = 94515;
	private const string QuestNpcName = "Hoàng Thiên Hóa";
	private const string DoctorNpcName = "Đại phu";
	private const string QuestMenuOption = "Thám quân";
	private const int MinimumLevel = 30;
	private const int MaximumRounds = 3;
	private const double ApproachArrivalDistance = 0.5;
	private const int ApproachTimeoutMilliseconds = 30000;
	private const int ApproachResendMilliseconds = 3000;
	private const int MenuTextOffset = 0x7EC;
	private const int MenuOptionStride = 0x69C;
	private const int MenuOptionCount = 8;
	private const int MaximumMenuTextLength = 128;
	private const int DialogOptionCommand = 7;
	private const int MaximumNpcClickAttempts = 6;
	private const int NpcEntityLoadTimeoutMilliseconds = 15000;
	private const int MenuOpenTimeoutMilliseconds = 6000;
	// Tầng 2 chỉ có một mục nên index luôn 0. Chờ 1500ms cho tầng 2 kịp hiện trước khi bấm.
	private const int SecondLevelOptionIndex = 0;
	private const int SecondLevelDelayMilliseconds = 1500;
	// Popup xác nhận tầng 3 dùng cùng lệnh với popup xác nhận Sửa đồ: command 7 với payload -1.
	private const int ConfirmOptionIndex = -1;
	private const int ConfirmDelayMilliseconds = 1500;
	private const int ConfirmWaitTimeoutMilliseconds = 8000;
	private const int ConfirmPollMilliseconds = 300;
	private const int ClosePopupDelayMilliseconds = 1000;
	private const int ModalTextScanLength = 0x4000;
	private const int MaximumModalTextLines = 60;
	private const int MaximumModalTextLength = 200;
	private const int MinimumModalTextLetters = 4;
	private const byte ColorStartMarker = 0x01;
	private const byte ColorEndMarker = 0x02;
	private const int ColorPayloadLength = 4;
	private const int MaximumColoredSegmentLength = 64;
	// Lượt cuối có hai popup liên tiếp; để dư mấy lần cho máy chậm, nhưng vẫn có trần để không lặp mãi.
	// Khớp theo chuỗi con nên bắt được cả "Di Ngoại Phù (siêu cấp)" lẫn bản thường.
	private const string TravelTalismanName = "Di ngoại phù";
	// Lượt về dùng phù này. Khớp chuỗi con nên bắt cả bản siêu cấp.
	private const string ReturnTalismanName = "Hồi thành phù";
	// Cùng nhịp với LowHpEngine.DispatchRetryMilliseconds/MaximumDispatchAttempts (1000ms x 5 lần) —
	// đó là đường dùng Hồi thành phù duy nhất đang chạy được, không tự đặt nhịp khác.
	private const int ReturnTalismanRetryMilliseconds = 1000;
	private const int MaximumReturnTalismanAttempts = 5;
	private const int TalismanMenuDelayMilliseconds = 1200;
	private const int TalismanMenuTimeoutMilliseconds = 8000;
	private const int MaximumQuickSlotNameLength = 64;
	private const int MaximumEscapeAttempts = 6;
	private const int EscapeRetryMilliseconds = 700;
	// Phải khớp DoctorShopSemanticCommand.ExpectedDialogVtableRva — sửa một bên mà quên bên kia thì hai nơi nhận
	// diện popup xác nhận khác nhau.
	private const int DoctorConfirmModalVtableRva = 0x46AF3C;
	// ĐIỂM CHUYỂN TIẾP. Popup tự hiện khi nhân vật đứng lên điểm, chọn mục bằng cùng command 7 của menu NPC.
	// Toạ độ điểm và bảng điểm đến nằm ở Utils/TransitGateDestinationCatalog.cs.
	//
	// Bốn mốc dưới đây chép từ TransitGateTravelProbe — probe đó đã chạy thật ngày 2026-09-11 và tới đúng Map52.
	// Nhịp gửi lại tuyến tính theo TIẾN ĐỘ tới đích chứ không theo nhịp cố định: lệnh đi có gắn cờ mở đầu bằng
	// ResetCommand=32 nên mỗi lần gửi lại là cắt ngang đường đang chạy, gửi theo nhịp cố định làm nhân vật đi giật.
	// Hai số 8000ms/0.08 ô lấy đúng của WeaponRepairAutomation (StuckDetectionMilliseconds, MinimumNavigationProgress).
	private const int TransitGateWalkTimeoutMilliseconds = 60000;
	private const int TransitGateStallResendMilliseconds = 8000;
	private const double TransitGateMinimumProgressCells = 0.08;
	private const int TransitGatePopupWaitMilliseconds = 8000;
	private const int TransitGateMapChangeTimeoutMilliseconds = 15000;
	private const int TransitGatePollMilliseconds = 300;

	private ScoutState state;
	private ScoutPhase phase;
	private DateTime deadlineUtc;
	private DateTime nextActionUtc;
	private DateTime nextProgressLogUtc;
	private DateTime nextMoveRefreshUtc;
	private readonly AutoFsTrainingOrderQueue travelQueue = new();
	private int npcClickAttempts;
	private string selectedOptionEvidence = "";
	private ScoutState lastLoggedState = ScoutState.Idle;
	// Vòng lặp chỉ chạy cho tới khi xong hoặc lỗi; không có cờ này thì Tick thấy state Idle là lại Start.
	private bool roundFinished;
	private int completedRounds;
	// Đích của chặng đang đi. Đổi theo phase.
	private string targetNpcName = "";
	private int targetMapId;
	private int targetRawX;
	private int targetRawY;
	// Map đích đọc được từ popup nhận nhiệm vụ.
	private string destinationMapName = "";
	private int destinationMapId;
	// Đích của nhiệm vụ ĐANG DỞ, cố ý SỐNG SÓT qua Reset(). Chỉ xoá khi trả xong một lượt.
	//
	// Vì sao cần: bỏ tick rồi tick lại "Làm nhiệm vụ" gọi Reset() và xoá sạch destination*, trong khi nhiệm vụ trong
	// game vẫn đang dở. Lần nói chuyện sau NPC chỉ nhắc "Ngươi chưa tìm được Đại phu sao?" và popup đó KHÔNG kèm tên
	// map tô màu, nên chặng NHẬN FAIL vĩnh viễn. Bằng chứng (quest.log 2026-09-11, PID=32196): 13:49:00 đọc được
	// "ĐÍCH = Đại phu ở 'Hoang mạc' | MapId=22 | Raw=51963/104343"; sau khi tick lại, 13:49:53 và 13:50:19 đều ra
	// "đoạn tô màu trong popup | []" rồi "ĐÍCH KHÔNG ĐỌC ĐƯỢC". Số đích đã nằm sẵn trong bộ nhớ mà bị vứt đi.
	// App không hề tắt giữa chừng nên không cần ghi ra đĩa, chỉ cần đừng xoá.
	private bool npcCoordinateFallbackUsed;
	private string pendingMapName = "";
	private int pendingMapId;
	private int pendingRawX;
	private int pendingRawY;
	private bool pendingDoctorVisited;
	private int destinationRawX;
	private int destinationRawY;
	private bool destinationRead;
	// Chữ tô màu đọc được nhưng không tra ra map. Rỗng nghĩa là popup thật sự không kèm nhiệm vụ mới.
	private string unmatchedSegments = "";
	private int escapeAttempts;
	private bool talismanTried;
	private int returnTalismanAttempts;
	// Ba trường chỉ phục vụ chốt đối chiếu map sau khi dùng phù, không ảnh hưởng máy trạng thái.
	private int talismanUsedIndex = -1;
	private int talismanSourceMapId;
	private int talismanExpectedMapId;
	// Trạng thái của chặng đi qua Điểm chuyển tiếp. Chỉ dùng ở chặng ĐI.
	private bool transitGateTried;
	private int transitGateOptionIndex = -1;
	private int transitGateExpectedMapId;
	private int transitGateSourceMapId;
	private int transitGateRawX;
	private int transitGateRawY;
	private bool transitGateRouteSent;
	private bool transitGateCommandSent;
	private DateTime transitGateProgressUtc;
	private double transitGateBestDistance;

	public bool IsBusy => state != ScoutState.Idle;

	// Gọi khi người dùng bỏ tick "Làm nhiệm vụ", để lần bật lại còn chạy được.
	public void ResetSession() {
		roundFinished = false;
		completedRounds = 0;
		Cancel();
	}

	public bool Tick(GameWindow game, GameSnapshot snapshot, Action<string>? log) {
		if (state == ScoutState.Idle) return Start(game, snapshot, log);
		if (! snapshot.Success) return true;
		LogState(log);

		switch (state) {
			// Dùng Di ngoại phù ở ô trang bị nhanh để nhảy thẳng tới map đích, thay vì chạy bộ xuyên nhiều map.
			//
			// Chỉ dùng được đồ ở Ô TRANG BỊ NHANH — ràng buộc đã chốt 2026-09-09, xem Runtime/LowHpEngine.cs.
			// Không tìm thấy phù thì lui về chạy bộ, không báo lỗi.
			//
			// Chọn điểm đến bằng SỐ THỨ TỰ trong TalismanDestinationCatalog, không đọc chữ trong menu — ba cách đọc
			// đều đã thất bại, xem ghi chú đầu file đó. 16/23 điểm đến trùng luôn map nhiệm vụ nên đa số lượt đi
			// thẳng; riêng nhóm mê cung 22-51 thì nhảy tới điểm đến gần nhất rồi đi bộ nốt.
			case ScoutState.UsingTalisman:
				if (DateTime.UtcNow < nextActionUtc) break;
				if (game.LastObservedMapId == targetMapId) {
					BeginWalking(log, "đã ở đúng map đích");
					break;
				}
				// Tôn trọng hai ô tick trong tab Nhiệm vụ. Trước đây engine không đọc Settings lần nào nên bỏ tick
				// cũng vô nghĩa, phù vẫn bị dùng.
				//
				// Tắt Di ngoại phù là mất LUÔN bước nhảy tới map gần đích, vì đó cũng chính là Di ngoại phù chứ
				// không có cơ chế nào khác — nhân vật sẽ chạy bộ từ Triều Ca. Đo trên Data\Maps: tuyến xa nhất là
				// map 26 và 36, 9 chặng thay vì 5.
				bool outboundLeg = phase == ScoutPhase.MeetingDoctor;
				// THỨ TỰ ƯU TIÊN Ở CHẶNG ĐI (chủ dự án chốt 2026-09-14): Di ngoại phù TRƯỚC, Điểm chuyển tiếp chỉ là
				// phương án dự phòng khi không dùng được phù — tắt tuỳ chọn, không có phù ở ô nhanh, bấm phím hỏng,
				// menu không hiện, hoặc map đích không nằm trong menu phù.
				//
				// Trước 2026-09-14 thứ tự ngược lại: TryBeginTransitGate đứng ngay đây, trước cả phép kiểm ô tick,
				// nên bật "Di ngoại phù" rồi mà nhân vật vẫn chạy bộ ra điểm chuyển tiếp. Lý do cũ tao đặt là "điểm
				// chuyển tiếp không tốn vật phẩm" — không phải điều chủ dự án yêu cầu.
				if (outboundLeg && ! game.QuestSettings.ScoutOutboundTravelTalisman) {
					BeginWalkingOrTransitGate(game, log, $"tắt tuỳ chọn '{TravelTalismanName}' ở đường đi");
					break;
				}
				if (! outboundLeg && ! game.QuestSettings.ScoutReturnTownTalisman) {
					BeginWalking(log, $"tắt tuỳ chọn '{ReturnTalismanName}' ở đường về nên chạy bộ");
					break;
				}
				// Lượt VỀ dùng Hồi thành phù, KHÔNG dùng Di ngoại phù (chủ dự án chốt 2026-09-09). Phù này không mở
				// menu chọn điểm đến — bấm phím là về thẳng thành, nên không vướng bài toán đọc menu như lượt đi.
				// Chỉ cần chờ MapId đổi sang map của Hoàng Thiên Hóa rồi đi bộ nốt trong map.
				if (phase != ScoutPhase.MeetingDoctor) {
					// BẤM LẠI theo chu kỳ, không bấm một lần rồi ngồi chờ hết giờ.
					//
					// Bằng chứng vì sao đổi (quest.log 2026-09-10 00:41:08.740 -> 00:41:20.936, PID 22056): bấm ô 4
					// đúng ô chứa 'Hồi thành phù' ("ÔNhanh=[#3=Di Ngoại Phù (siêu cấp),#4=Hồi thành phù]") nhưng
					// 12000ms sau vẫn ở Map11, phải đi bộ. Cùng cơ chế phím tắt đó ở ô 3 lại mở được Di ngoại phù lúc
					// 00:38:29.863, nên phím tắt tự nó không hỏng — client bỏ qua lần bấm này.
					//
					// LowHpEngine dùng ĐÚNG hàm TryUseQuickSlotHotkey này và đang chạy được, khác duy
					// nhất ở chỗ nó bấm lại mỗi 1000ms tối đa 5 lần (DispatchRetryMilliseconds/MaximumDispatchAttempts,
					// LowHpEngine.cs dòng 9-10 và nhánh LOW_HP_MAP_CHANGE_TIMEOUT dòng 103). Chép lại
					// đúng nhịp đó thay vì tự đặt nhịp khác.
					//
					// CHƯA VERIFY: chưa quan sát được nhân vật có về thành sau khi bấm lại hay không.
					if (returnTalismanAttempts >= MaximumReturnTalismanAttempts) {
						BeginWalking(log, $"bấm '{ReturnTalismanName}' {MaximumReturnTalismanAttempts} lần vẫn ở Map{game.LastObservedMapId}");
						break;
					}
					if (! TryFindQuickSlot(game.ProcessId, ReturnTalismanName, out int returnSlot, out string returnSlotEvidence)) {
						BeginWalking(log, $"không có '{ReturnTalismanName}' ở ô trang bị nhanh | {returnSlotEvidence}");
						break;
					}
					if (! game.AutoFsTransport.TryUseQuickSlotHotkey(game.Handle, returnSlot, out string returnUseError)) {
						BeginWalking(log, $"bấm phím ô {returnSlot + 1} thất bại | {returnUseError}");
						break;
					}
					returnTalismanAttempts++;
					log?.Invoke($"Thám quân | dùng '{ReturnTalismanName}' ở ô {returnSlot + 1} | Lần={returnTalismanAttempts}/{MaximumReturnTalismanAttempts} | Map={game.LastObservedMapId} | {returnSlotEvidence}");
					nextActionUtc = DateTime.UtcNow.AddMilliseconds(ReturnTalismanRetryMilliseconds);
					break;
				}
				if (! talismanTried) {
					talismanTried = true;
					if (! TryFindQuickSlot(game.ProcessId, TravelTalismanName, out int talismanSlot, out string slotEvidence)) {
						BeginWalkingOrTransitGate(game, log, $"không có '{TravelTalismanName}' ở ô trang bị nhanh | {slotEvidence}");
						break;
					}
					if (! game.AutoFsTransport.TryUseQuickSlotHotkey(game.Handle, talismanSlot, out string useError)) {
						BeginWalkingOrTransitGate(game, log, $"bấm phím ô {talismanSlot + 1} thất bại | {useError}");
						break;
					}
					log?.Invoke($"Thám quân | dùng '{TravelTalismanName}' ở ô {talismanSlot + 1} | {slotEvidence}");
					nextActionUtc = DateTime.UtcNow.AddMilliseconds(TalismanMenuDelayMilliseconds);
					deadlineUtc = DateTime.UtcNow.AddMilliseconds(TalismanMenuTimeoutMilliseconds);
					break;
				}
				IntPtr talismanMenu = ReadMenu(game.ProcessId);
				if (talismanMenu == IntPtr.Zero) {
					if (HasTimedOut()) {
						BeginWalkingOrTransitGate(game, log, $"dùng phù nhưng không thấy menu điểm đến trong {TalismanMenuTimeoutMilliseconds}ms");
						break;
					}
					nextActionUtc = DateTime.UtcNow.AddMilliseconds(ConfirmPollMilliseconds);
					break;
				}
				// Bấm theo SỐ THỨ TỰ trong TalismanDestinationCatalog, không đọc chữ trong menu.
				//
				// Đã thử ba cách đọc và cả ba thất bại (xem ghi chú đầu TalismanDestinationCatalog.cs). Chủ dự án
				// chép tay 23 dòng menu ngày 2026-09-10, đó là nguồn duy nhất còn lại.
				//
				// CHỈ dùng phù khi menu có ĐÚNG map đích. Không nhảy tới map trung gian rồi đi bộ nốt (chủ dự án
				// chốt 2026-09-11), vì chuyến trung gian đưa nhân vật sang một map khác hẳn rồi mới đi bộ ngược lại.
				// Bằng chứng hỏng (quest.log 2026-09-11 13:49:02, PID=32196): đích là Map22 'Hoang mạc' nhưng Auto bấm
				// điểm đến #2 với "ĐíchNgoàiMenu | ĐiQuaMap=20 | CònLại=1 chặng đi bộ | ChờTới=Map20" — phù đưa sang
				// Map20 chứ không tới Map22.
				if (! TalismanDestinationCatalog.TryGetMenuIndex(targetMapId, out int destinationIndex)) {
					BeginWalkingOrTransitGate(game, log, $"Map{targetMapId} không có trong menu Di ngoại phù");
					BackgroundEscapeCommand.Run(game.ProcessId, game.Handle);
					break;
				}
				string destinationEvidence = $"ĐíchNằmTrongMenu | Map={targetMapId}";
				string destinationName = GameMapCatalog.TryGetName(targetMapId, out string resolvedName) ? resolvedName : $"Map{targetMapId}";
				if (! game.AutoFsTransport.TrySendCommand(game.Handle, DialogOptionCommand, destinationIndex, out string travelResult)) {
					BackgroundEscapeCommand.Run(game.ProcessId, game.Handle);
					BeginWalking(log, $"không gửi được command {DialogOptionCommand}/{destinationIndex} | {travelResult}");
					break;
				}
				talismanUsedIndex = destinationIndex;
				talismanSourceMapId = game.LastObservedMapId;
				talismanExpectedMapId = TalismanDestinationCatalog.TryGetMapIdAt(destinationIndex, out int expectedMapId) ? expectedMapId : 0;
				log?.Invoke($"Thám quân | chọn điểm đến #{destinationIndex} để tới '{destinationName}' | Command={DialogOptionCommand}/{destinationIndex} | {destinationEvidence} | ChờTới=Map{talismanExpectedMapId} | {travelResult}");
				BeginWalking(log, "đã dùng phù, chờ đổi map rồi đi nốt trong map");
				break;

			// Đi bộ tới ô Điểm chuyển tiếp. Dừng khi POPUP TỰ HIỆN (ModalState khác 0) chứ không dựa vào ngưỡng
			// khoảng cách: vùng kích hoạt do client quyết định, toạ độ điểm chỉ là số đo gần đúng.
			case ScoutState.WalkingToTransitGate:
				if (ReadMenu(game.ProcessId) != IntPtr.Zero) {
					state = ScoutState.SelectingTransitGate;
					nextActionUtc = DateTime.UtcNow.AddMilliseconds(TransitGatePollMilliseconds);
					deadlineUtc = DateTime.UtcNow.AddMilliseconds(TransitGateMapChangeTimeoutMilliseconds);
					log?.Invoke($"Thám quân | popup Điểm chuyển tiếp đã hiện | ViTri={snapshot.X}/{snapshot.Y}");
					break;
				}
				if (HasTimedOut()) {
					BeginWalking(log, $"tới Điểm chuyển tiếp mà popup không hiện trong {(TransitGateWalkTimeoutMilliseconds + TransitGatePopupWaitMilliseconds) / 1000}s | ViTri={snapshot.X}/{snapshot.Y} | Điểm={transitGateRawX}/{transitGateRawY}");
					break;
				}
				double gateDistance = GetDistance(snapshot.X, snapshot.Y, transitGateRawX, transitGateRawY);
				if (gateDistance <= transitGateBestDistance - TransitGateMinimumProgressCells) {
					transitGateBestDistance = gateDistance;
					transitGateProgressUtc = DateTime.UtcNow;
				}
				bool gateStalled = (DateTime.UtcNow - transitGateProgressUtc).TotalMilliseconds >= TransitGateStallResendMilliseconds;
				if (transitGateRouteSent && ! gateStalled) break;
				bool gateMoveSent = AutoFsMovementCommand.TryMoveTo(game, transitGateRawX, transitGateRawY, out string gateMoveResult);
				log?.Invoke($"Thám quân | gửi tuyến tới Điểm chuyển tiếp | {(transitGateRouteSent ? "gửi lại vì đứng chững" : "lần đầu")} | ViTri={snapshot.X}/{snapshot.Y} | Còn={gateDistance:F2} | Gửi={gateMoveSent} | {gateMoveResult}");
				if (! gateMoveSent && ! transitGateRouteSent) {
					BeginWalking(log, $"không gửi được lệnh đi tới Điểm chuyển tiếp | {gateMoveResult}");
					break;
				}
				transitGateRouteSent = true;
				transitGateProgressUtc = DateTime.UtcNow;
				transitGateBestDistance = gateDistance;
				break;

			// Popup đang mở: bấm số thứ tự rồi chờ MapId đổi. Bấm ĐÚNG MỘT LẦN — mỗi lần bấm là một chuyến dịch
			// chuyển thật, bấm lại khi chuyến trước đang chạy là đưa nhân vật đi lung tung.
			case ScoutState.SelectingTransitGate:
				if (DateTime.UtcNow < nextActionUtc) break;
				if (transitGateSourceMapId > 0 && game.LastObservedMapId > 0 && game.LastObservedMapId != transitGateSourceMapId) {
					if (transitGateExpectedMapId > 0 && game.LastObservedMapId != transitGateExpectedMapId) {
						log?.Invoke($"Thám quân | BẢNG ĐIỂM CHUYỂN TIẾP LỆCH | bấm số {transitGateOptionIndex} lẽ ra tới Map{transitGateExpectedMapId} nhưng đang ở Map{game.LastObservedMapId} | Kiểm lại TransitGateDestinationCatalog");
					}
					BeginWalking(log, $"Điểm chuyển tiếp đưa tới Map{game.LastObservedMapId}, đi bộ nốt trong map");
					break;
				}
				if (! transitGateCommandSent) {
					if (! game.AutoFsTransport.TrySendCommand(game.Handle, DialogOptionCommand, transitGateOptionIndex, out string gateSelectResult)) {
						BackgroundEscapeCommand.Run(game.ProcessId, game.Handle);
						BeginWalking(log, $"không gửi được command {DialogOptionCommand}/{transitGateOptionIndex} ở Điểm chuyển tiếp | {gateSelectResult}");
						break;
					}
					transitGateCommandSent = true;
					deadlineUtc = DateTime.UtcNow.AddMilliseconds(TransitGateMapChangeTimeoutMilliseconds);
					log?.Invoke($"Thám quân | chọn điểm đến #{transitGateOptionIndex} ở Điểm chuyển tiếp | Command={DialogOptionCommand}/{transitGateOptionIndex} | ChờTới=Map{transitGateExpectedMapId} | {gateSelectResult}");
					nextActionUtc = DateTime.UtcNow.AddMilliseconds(TransitGatePollMilliseconds);
					break;
				}
				if (HasTimedOut()) {
					BackgroundEscapeCommand.Run(game.ProcessId, game.Handle);
					BeginWalking(log, $"bấm số {transitGateOptionIndex} nhưng vẫn ở Map{game.LastObservedMapId} sau {TransitGateMapChangeTimeoutMilliseconds / 1000}s");
					break;
				}
				nextActionUtc = DateTime.UtcNow.AddMilliseconds(TransitGatePollMilliseconds);
				break;

			case ScoutState.Travelling:
				// Đối chiếu map thật tới được với map mà bảng index nói sẽ tới.
				//
				// TalismanDestinationCatalog bấm theo SỐ THỨ TỰ cứng. Bảng đó đang đúng — 5 chỉ số đã bấm thật đều
				// tới đúng map, và menu Di ngoại phù KHÔNG ẩn map đang đứng (đo 2026-09-10: đứng Map52 bấm 8 ra
				// Map6 chứ không phải Map7; đứng Map1 bấm 22 vẫn ra Map65). Nhưng client cập nhật thêm hoặc bớt
				// một dòng là cả bảng lệch, mà nhảy nhầm map thì travelQueue vẫn lặng lẽ đi bộ tiếp từ chỗ nhầm.
				// Chốt này biến lỗi im lặng thành một dòng log.
				if (talismanExpectedMapId > 0 && game.LastObservedMapId > 0 && game.LastObservedMapId != talismanSourceMapId) {
					if (game.LastObservedMapId != talismanExpectedMapId) {
						log?.Invoke($"Thám quân | BẢNG ĐIỂM ĐẾN LỆCH | bấm số {talismanUsedIndex} lẽ ra tới Map{talismanExpectedMapId} nhưng đang ở Map{game.LastObservedMapId} | Kiểm lại TalismanDestinationCatalog");
					}
					talismanExpectedMapId = 0;
				}
				if (travelQueue.Tick(game, snapshot, targetMapId, targetRawX, targetRawY, out string travelDetail)) {
					state = ScoutState.Approaching;
					nextMoveRefreshUtc = DateTime.MinValue;
					deadlineUtc = DateTime.UtcNow.AddMilliseconds(ApproachTimeoutMilliseconds);
					log?.Invoke($"Thám quân | tới đúng map của {targetNpcName} | {travelDetail} | chuyển sang bước áp sát");
					break;
				}
				if (DateTime.UtcNow >= nextProgressLogUtc) {
					nextProgressLogUtc = DateTime.UtcNow.AddSeconds(10);
					log?.Invoke($"Thám quân | đang đi tới {targetNpcName} | HiệnTại=Map{game.LastObservedMapId}/{snapshot.X}/{snapshot.Y} | {travelDetail}");
				}
				break;

			// Bước áp sát riêng, KHÔNG dùng lại AutoFsTrainingOrderQueue cho đoạn cuối.
			//
			// Bằng chứng vì sao cần bước này (quest.log 2026-09-09 18:53:40 PID 14236): hàng đợi đó báo
			// "Arrived | Raw=55000/95190 | Distance=1,35" trong khi NPC ở 54923/94515 — lệch 675 đơn vị trục Y —
			// nên nó KHÔNG gửi lệnh đi nào và nhân vật đứng im. Sau đó 6 lần click đều trượt với lý do
			// "Attack projection ngoài client | Projected=379/-152 | ClientSize=800x600 | RawDelta=-20/-762".
			//
			// Ngưỡng 1.5 của hàng đợi hợp cho "về bãi" nhưng quá lỏng để click NPC. Ở đây siết còn 0.5 —
			// tương đương ~256 đơn vị trục Y, chiếu ra khoảng 150 điểm ảnh, nằm gọn trong khung 600.
			case ScoutState.Approaching:
				int approachRawX = targetRawX;
				int approachRawY = targetRawY;
				// Đích áp sát LUÔN là toạ độ Data/Maps, không tinh chỉnh theo toạ độ entity: AutoFS đi tới toạ độ
				// cứng rồi mới tìm NPC theo tên, không bao giờ đọc toạ độ entity để đi
				// (MenuAttribute.cs:24227-24245: OrderQueue(21, 1716, 2955) -> NavigateSelection() ->
				// OrderQueue("Hoàng Thiên Hóa", 30); MenuAttribute.cs:23912-23971 cùng khuôn đó với Đại Phu).
				double approachDistance = GetDistance(snapshot.X, snapshot.Y, approachRawX, approachRawY);
				if (approachDistance <= ApproachArrivalDistance) {
					state = ScoutState.ClickingNpc;
					npcClickAttempts = 0;
					npcCoordinateFallbackUsed = false;
					nextActionUtc = DateTime.UtcNow.AddMilliseconds(500);
					deadlineUtc = DateTime.UtcNow.AddMilliseconds(NpcEntityLoadTimeoutMilliseconds);
					log?.Invoke($"Thám quân | đã áp sát {targetNpcName} | HiệnTại={snapshot.X}/{snapshot.Y} | Đích={approachRawX}/{approachRawY} | Còn={approachDistance:F2}");
					break;
				}
				if (HasTimedOut()) return Fail($"Không áp sát được {targetNpcName} trong {ApproachTimeoutMilliseconds}ms | HiệnTại={snapshot.X}/{snapshot.Y} | Đích={approachRawX}/{approachRawY} | Còn={approachDistance:F2}", log);
				if (DateTime.UtcNow >= nextMoveRefreshUtc) {
					nextMoveRefreshUtc = DateTime.UtcNow.AddMilliseconds(ApproachResendMilliseconds);
					bool moveSent = AutoFsMovementCommand.TryMoveTo(game, approachRawX, approachRawY, out string moveResult);
					log?.Invoke($"Thám quân | gửi lệnh áp sát {targetNpcName} | Đích={approachRawX}/{approachRawY} | Còn={approachDistance:F2} | Gửi={moveSent} | {moveResult}");
				}
				break;

			case ScoutState.ClickingNpc:
				if (DateTime.UtcNow < nextActionUtc) break;
				if (! RuntimeEntityLocator.TryFindNamedEntity(game.ProcessId, targetNpcName, targetRawX, targetRawY, out RuntimeEntityLocation npc, out string findReason)) {
					// Hết giờ chờ entity thì làm đúng như luồng Sửa đồ: click theo toạ độ Data/Maps thay vì FAIL ngay.
					// Chưa verify được đường này có mở nổi menu NPC hay không — ở luồng Sửa đồ nó đã chạy 476 lần mà
					// không mở được lần nào trên PID=22824 (repair.log 2026-09-11). Giữ lại vì nó là bước duy nhất còn
					// thử được, và vì hai luồng phải hành xử giống nhau thay vì mỗi nơi một kiểu.
					if (! HasTimedOut()) {
						nextActionUtc = DateTime.UtcNow.AddSeconds(1);
						break;
					}
					if (npcCoordinateFallbackUsed) return Fail($"Không thấy entity {targetNpcName} sau {NpcEntityLoadTimeoutMilliseconds}ms, click theo toạ độ cũng không mở được | {findReason}", log);
					npcCoordinateFallbackUsed = true;
					log?.Invoke($"Thám quân | không thấy entity {targetNpcName}, chuyển sang click theo toạ độ Data/Maps | Đích={targetRawX}/{targetRawY} | {findReason}");
					npc = new RuntimeEntityLocation(-1, 0, targetRawX, targetRawY, 0, 0, $"{targetNpcName} (toạ độ Data/Maps)", 0);
					deadlineUtc = DateTime.UtcNow.AddMilliseconds(NpcEntityLoadTimeoutMilliseconds);
				}
				npcClickAttempts++;
				// Cùng luật với luồng Sửa đồ: NPC không có toạ độ thì click theo index như AutoFS, còn lại giữ
				// nguyên đường toạ độ. Bản "luôn dùng index" đã thử và hoàn nguyên cùng lượt với Sửa đồ — xem khối
				// bằng chứng ở WeaponRepairAutomation.ClickDoctor (20/20 lệnh 8 không mở được cửa hàng).
				bool npcByIndex = npc.RawX <= 0 || npc.RawY <= 0;
				log?.Invoke($"Thám quân | click {targetNpcName} | Index={npc.Index} | Raw={npc.RawX}/{npc.RawY} | Cách={(npcByIndex ? "AUTOFS_INDEX" : "TOẠ_ĐỘ")} | Lần={npcClickAttempts}/{MaximumNpcClickAttempts}");
				bool npcClicked = npcByIndex
					? game.AutoFsTransport.TrySelectEntity(game.Handle, npc.Index, out string clickResult)
					: FullMouseHandlerPickupCommand.TryClickRawPosition(game.ProcessId, game.Handle, snapshot.X, snapshot.Y, npc.RawX, npc.RawY, out _, out clickResult);
				if (! npcClicked) {
					if (npcClickAttempts >= MaximumNpcClickAttempts) return Fail($"Click {targetNpcName} thất bại | {clickResult}", log);
					nextActionUtc = DateTime.UtcNow.AddSeconds(1);
					break;
				}
				// Ở Đại phu chỉ hiện popup, không có menu chọn và không có bước xác nhận — chỉ cần ESC.
				if (phase == ScoutPhase.MeetingDoctor) {
					BeginClosingPopup();
					break;
				}
				state = ScoutState.WaitingMenu;
				deadlineUtc = DateTime.UtcNow.AddMilliseconds(MenuOpenTimeoutMilliseconds);
				nextActionUtc = DateTime.UtcNow.AddMilliseconds(500);
				break;

			case ScoutState.WaitingMenu:
				if (DateTime.UtcNow < nextActionUtc) break;
				IntPtr menu = ReadMenu(game.ProcessId);
				if (menu == IntPtr.Zero) {
					if (HasTimedOut()) {
						if (npcClickAttempts >= MaximumNpcClickAttempts) return Fail($"Không mở được hội thoại {targetNpcName}.", log);
						state = ScoutState.ClickingNpc;
						nextActionUtc = DateTime.UtcNow.AddMilliseconds(1000);
						deadlineUtc = DateTime.UtcNow.AddMilliseconds(NpcEntityLoadTimeoutMilliseconds);
						log?.Invoke("Thám quân | chưa thấy hội thoại, click lại NPC");
						break;
					}
					nextActionUtc = DateTime.UtcNow.AddMilliseconds(300);
					break;
				}
				if (! TryFindMenuOption(game.ProcessId, menu, QuestMenuOption, out int optionIndex, out string menuEvidence)) {
					return Fail($"Menu {targetNpcName} không có mục '{QuestMenuOption}' | MenuState=0x{menu.ToInt64():X8} | {menuEvidence}", log);
				}
				if (! game.AutoFsTransport.TrySendCommand(game.Handle, DialogOptionCommand, optionIndex, out string selectResult)) {
					return Fail($"Không gửi được command {DialogOptionCommand}/{optionIndex} để chọn '{QuestMenuOption}' | {selectResult}", log);
				}
				selectedOptionEvidence = $"MenuState=0x{menu.ToInt64():X8} | {menuEvidence}";
				log?.Invoke($"Thám quân | đã chọn '{QuestMenuOption}' tầng 1 | Command={DialogOptionCommand}/{optionIndex} | {selectedOptionEvidence}");
				state = ScoutState.SelectingSecondLevel;
				nextActionUtc = DateTime.UtcNow.AddMilliseconds(SecondLevelDelayMilliseconds);
				break;

			// Tầng 2 bấm MÙ, không đọc menu để xác nhận.
			//
			// Bằng chứng bắt buộc phải bấm mù (quest.log 2026-09-09 19:44:06-07, PID 14236): sau khi gửi
			// command 7/0 ở tầng 1, game ĐÃ chuyển sang tầng 2 — chủ dự án xác nhận nhìn thấy trên màn hình —
			// nhưng đọc lại ModalState vẫn ra nguyên ba mục của tầng 1. Con trỏ đó không phản ánh tầng 2, nên mọi
			// phép đọc để xác nhận đều vô nghĩa. Đã thử 3 cách đọc khác nhau (bố cục cố định, DialogPointer, dò
			// object con) đều không ra; đừng mở lại hướng đó nếu chưa có địa chỉ mới.
			//
			// Tầng 2 chỉ có ĐÚNG MỘT mục "Thám quân" (chủ dự án xác nhận), nên index luôn là 0.
			case ScoutState.SelectingSecondLevel:
				if (DateTime.UtcNow < nextActionUtc) break;
				if (! game.AutoFsTransport.TrySendCommand(game.Handle, DialogOptionCommand, SecondLevelOptionIndex, out string confirmResult)) {
					return Fail($"Không gửi được command {DialogOptionCommand}/{SecondLevelOptionIndex} để chọn '{QuestMenuOption}' ở tầng 2 | {confirmResult}", log);
				}
				log?.Invoke($"Thám quân | đã chọn '{QuestMenuOption}' tầng 2 | Command={DialogOptionCommand}/{SecondLevelOptionIndex} | Chờ={SecondLevelDelayMilliseconds}ms | {confirmResult}");
				state = ScoutState.ConfirmingQuest;
				nextActionUtc = DateTime.UtcNow.AddMilliseconds(ConfirmDelayMilliseconds);
				deadlineUtc = DateTime.UtcNow.AddMilliseconds(ConfirmWaitTimeoutMilliseconds);
				break;

			// Tầng 3 ĐỌC ĐƯỢC nên chờ đúng popup rồi mới bấm, khác hẳn tầng 2.
			//
			// Bằng chứng (quest.log 2026-09-09 20:35:21, PID 14236):
			//   "Modal=0x289AB8A0 | Vtable=0x0086AF3C | VtableRva=0x46AF3C | KhớpPopupĐạiPhu=CÓ"
			// Con trỏ đổi hẳn sang địa chỉ khác với tầng 1 (0x28AB9F20) và vtable trùng popup xác nhận Đại Phu.
			// Popup có hai nút "Xác định" và "Hủy". Cùng lần chạy đó chủ dự án xác nhận đã NHẬN ĐƯỢC nhiệm vụ,
			// nên command 7/-1 rơi vào "Xác định".
			case ScoutState.ConfirmingQuest:
				if (DateTime.UtcNow < nextActionUtc) break;
				string modalEvidence = DescribeModalVtable(game.ProcessId, out bool confirmModalReady, out bool anyModalOpen);
				if (confirmModalReady) {
					log?.Invoke($"Thám quân | nhận diện popup tầng 3 | {modalEvidence}");
					if (! game.AutoFsTransport.TrySendCommand(game.Handle, DialogOptionCommand, ConfirmOptionIndex, out string acceptResult)) {
						return Fail($"Không gửi được command {DialogOptionCommand}/{ConfirmOptionIndex} để xác nhận | {acceptResult}", log);
					}
					log?.Invoke($"Thám quân | đã xác nhận tầng 3 | Command={DialogOptionCommand}/{ConfirmOptionIndex} | {acceptResult}");
					BeginClosingPopup();
					break;
				}
				// Lượt thứ 3 (lượt cuối) KHÔNG có popup xác nhận/hủy, chỉ có popup thường (chủ dự án xác nhận
				// 2026-09-09). Bắt buộc phải chấp nhận nhánh này, nếu không lượt cuối luôn kết thúc bằng FAIL.
				if (anyModalOpen) {
					log?.Invoke($"Thám quân | tầng 3 KHÔNG phải popup xác nhận, bỏ qua bước xác nhận | {modalEvidence}");
					BeginClosingPopup();
					break;
				}
				if (HasTimedOut()) {
					log?.Invoke($"Thám quân | hết {ConfirmWaitTimeoutMilliseconds}ms mà không có popup nào ở tầng 3 | {modalEvidence}");
					BeginClosingPopup();
					break;
				}
				nextActionUtc = DateTime.UtcNow.AddMilliseconds(ConfirmPollMilliseconds);
				break;

			// Đóng popup bằng ESC, LẶP cho tới khi không còn popup nào.
			//
			// Không đếm cứng số lần: lượt thứ 3 hiện tới HAI popup liên tiếp nên một lần ESC là không đủ (chủ dự án
			// quan sát 2026-09-09), còn lượt thường chỉ có một. Dùng chính ModalState làm điều kiện dừng — nó đã
			// chứng minh đọc được đúng ở đây (0x008FFC58 khi có popup, 0 khi không có), chắc chắn hơn là đo xem
			// nhân vật đã đi lại được chưa.
			//
			// Đọc chữ popup TRƯỚC khi gửi ESC đầu tiên, vì ESC đóng popup là mất dữ liệu.
			case ScoutState.ClosingPopup:
				if (DateTime.UtcNow < nextActionUtc) break;
				if (! destinationRead) {
					destinationRead = true;
					log?.Invoke($"Thám quân | popup của chặng {DescribePhase(phase)} | {DescribeModalVtable(game.ProcessId, out _, out _)}");
					foreach (string line in DumpModalText(game.ProcessId)) log?.Invoke("Thám quân | chữ popup | " + line);
					// Đọc đích ở CẢ chặng NHẬN lẫn chặng TRẢ. Bằng chứng (quest.log 2026-09-09 21:55:25, PID 22056):
					// popup ngay sau khi trả đã mang chữ giao nhiệm vụ mới (" tìm Đại phu thu thập tin tức."), tức
					// trả và nhận diễn ra trong CÙNG một lần nói chuyện. Bản trước chỉ đọc ở chặng NHẬN nên vứt mất
					// đích của lượt kế, đi thêm một chuyến NHẬN thừa và bị game trả lời "Ngươi chưa tìm được Đại phu
					// sao? Nhanh chân lên!" — câu đó không có tên map nên luồng chết ở đó.
					if (phase != ScoutPhase.MeetingDoctor) ReadQuestDestination(game.ProcessId, log);
				}
				DescribeModalVtable(game.ProcessId, out _, out bool popupStillOpen);
				if (! popupStillOpen) return AdvancePhase(log);
				if (escapeAttempts >= MaximumEscapeAttempts) {
					return Fail($"Đã gửi {escapeAttempts} lần ESC mà popup vẫn còn | Chặng={DescribePhase(phase)}", log);
				}
				escapeAttempts++;
				string escapeResult = BackgroundEscapeCommand.Run(game.ProcessId, game.Handle);
				log?.Invoke($"Thám quân | gửi ESC lần {escapeAttempts}/{MaximumEscapeAttempts} | {escapeResult.Replace("\r\n", " | ")}");
				nextActionUtc = DateTime.UtcNow.AddMilliseconds(EscapeRetryMilliseconds);
				break;
		}
		return true;
	}

	public void Cancel(Action<string>? log = null, string? reason = null) {
		if (! IsBusy) return;
		if (reason != null) log?.Invoke($"Thám quân | huỷ | {reason}");
		Reset();
	}

	private bool Start(GameWindow game, GameSnapshot snapshot, Action<string>? log) {
		if (roundFinished) return false;
		if (snapshot.Level < MinimumLevel) {
			roundFinished = true;
			log?.Invoke($"Thám quân bỏ qua | Level={snapshot.Level} < {MinimumLevel}");
			return false;
		}
		// Không đặt roundFinished ở đây: layout chưa sẵn sàng là trạng thái tạm thời, chờ tick sau là chạy được.
		if (! game.RuntimeLayout.MovementReady) {
			log?.Invoke("Thám quân bỏ qua | " + game.RuntimeLayout.DescribeUnavailable(RuntimeSubsystem.MovementTransport, RuntimeSubsystem.Map));
			return false;
		}
		lastLoggedState = ScoutState.Idle;
		log?.Invoke($"Thám quân bắt đầu | Level={snapshot.Level} | Lượt={completedRounds + 1}/{MaximumRounds} | HiệnTại=Map{game.LastObservedMapId}/{snapshot.X}/{snapshot.Y}");
		BeginPhase(ScoutPhase.ReceivingQuest, log);
		return true;
	}

	// Chuyển sang chặng kế tiếp sau khi đã đóng hết popup của chặng hiện tại.
	private bool AdvancePhase(Action<string>? log) {
		log?.Invoke($"Thám quân | xong chặng {DescribePhase(phase)} sau {escapeAttempts} lần ESC");
		switch (phase) {
			case ScoutPhase.ReceivingQuest:
				// Đọc không ra map đích thì DỪNG, không đi mò. Auto không lưu tiến trình ra đĩa, nên nếu người dùng
				// tắt Auto giữa chừng rồi bật lại, chặng nhận sẽ gặp popup "sao chưa làm việc ta giao" thay vì popup
				// giao nhiệm vụ, và popup đó KHÔNG nhắc lại map đích (chủ dự án xác nhận 2026-09-09). Lúc đó không
				// có cách nào biết phải đi đâu; báo rõ còn hơn đưa nhân vật tới map sai.
				if (! HasDestination()) {
					// Popup không kèm đích NHƯNG phiên chạy này đã từng đọc được đích của một lượt chưa trả xong:
					// chính là ca bỏ tick/tick lại. Game vừa xác nhận nhiệm vụ còn dở bằng câu nhắc, nên đi tiếp
					// theo đích đã nhớ thay vì FAIL. Không có cái nhớ này thì chỉ còn nước mò, nên vẫn FAIL như cũ.
					if (HasPendingQuest()) {
						destinationMapName = pendingMapName;
						destinationMapId = pendingMapId;
						destinationRawX = pendingRawX;
						destinationRawY = pendingRawY;
						log?.Invoke($"Thám quân | popup không kèm đích, dùng lại đích của lượt đang dở | Map{pendingMapId} '{pendingMapName}' | Raw={pendingRawX}/{pendingRawY} | ĐãGặpĐạiPhu={pendingDoctorVisited}");
						if (pendingDoctorVisited) {
							BeginPhase(ScoutPhase.ReturningQuest, log);
							return true;
						}
						BeginDoctorPhase(log);
						return true;
					}
					return Fail($"Không đọc được map đích từ popup | Đang có nhiệm vụ dở từ trước? | Tên đọc được='{destinationMapName}' | MapId={destinationMapId}", log);
				}
				BeginDoctorPhase(log);
				return true;
			case ScoutPhase.MeetingDoctor:
				pendingDoctorVisited = true;
				BeginPhase(ScoutPhase.ReturningQuest, log);
				return true;
			default:
				// Trả xong nghĩa là lượt đó khép lại: phải quên đích đã nhớ, không thì lượt sau lấy nhầm đích cũ.
				ClearPendingQuest();
				completedRounds++;
				if (completedRounds >= MaximumRounds) {
					log?.Invoke($"Thám quân HOÀN TẤT | Đã làm đủ {completedRounds}/{MaximumRounds} lượt.");
					roundFinished = true;
					Reset();
					return false;
				}
				// Trả xong mà popup đã kèm đích mới thì đi thẳng tới Đại phu, không đi thêm một chuyến NHẬN thừa.
				if (HasDestination()) {
					log?.Invoke($"Thám quân | xong lượt {completedRounds}/{MaximumRounds}, popup trả đã kèm đích mới");
					BeginDoctorPhase(log);
					return true;
				}
				// Trả xong mà popup KHÔNG kèm nhiệm vụ mới thì coi là hết lượt trong ngày và dừng hẳn.
				//
				// Bằng chứng (quest.log 2026-09-09 PID 21768): hai lượt đầu popup trả đều kèm đích mới ngay
				// ("ĐÍCH = Đại phu ở 'Trần Đường'"), tức game luôn giao tiếp nhiệm vụ trong cùng lần nói chuyện.
				// Tới lượt cuối thì không đọc ra đích nào, và popup lần đó cần TỚI HAI lần ESC thay vì một — khớp
				// mô tả của chủ dự án về lượt cuối. Quay lại hỏi NPC thêm một chuyến chỉ tốn thời gian rồi cũng FAIL.
				// Popup CÓ giao nhiệm vụ mới nhưng Auto không tra được tên map thì đây là LỖI, không phải hết lượt.
				// Báo ra nguyên chữ trong popup để bổ sung vào GameMapCatalog.ClientAliasMapIdByName.
				// Bằng chứng vì sao tách (quest.log 2026-09-10 00:52:58, PID 22056): popup ghi "Tầng 1 Hiên Viên động"
				// còn bảng tên ghi "Hiên Viên T1", tra không ra, và Auto báo "HOÀN TẤT | Đã làm 2/3 lượt" rồi dừng êm.
				if (unmatchedSegments.Length > 0) {
					log?.Invoke($"Thám quân DỪNG VÌ LỖI | Đã làm {completedRounds}/{MaximumRounds} lượt | Popup có giao nhiệm vụ nhưng không tra được tên bản đồ: [{unmatchedSegments}] | Thêm tên này vào GameMapCatalog rồi chạy lại.");
					roundFinished = true;
					Reset();
					return false;
				}
				log?.Invoke($"Thám quân HOÀN TẤT | Đã làm {completedRounds}/{MaximumRounds} lượt | Popup trả không kèm nhiệm vụ mới, coi là hết lượt.");
				roundFinished = true;
				Reset();
				return false;
		}
	}

	// Quyết định có đi qua Điểm chuyển tiếp hay không, và nếu có thì bấm số mấy.
	//
	// Trả false là "không dùng được, cứ theo đường cũ" — mọi nhánh false đều ghi rõ lý do. Gọi đúng MỘT lần mỗi
	// chặng (cờ transitGateTried) để không dò lại sau khi đã lui về đi bộ.
	//
	// Hai đường chọn số:
	//   - Map đích nằm ngay trong danh sách 9 điểm đến  -> bấm thẳng.
	//   - Map đích ngoài danh sách (nhóm mê cung 22-51) -> bấm điểm đến nào đi bộ nốt NGẮN NHẤT, và chỉ khi nó
	//     thật sự ngắn hơn đứng tại chỗ đi bộ. Số chặng đo bằng chính GameMapRoutePlanner mà bước đi bộ dùng.
	private bool TryBeginTransitGate(GameWindow game, Action<string>? log) {
		transitGateTried = true;
		int currentMapId = game.LastObservedMapId;
		if (! TransitGateDestinationCatalog.TryGetTransitPoint(currentMapId, out int gateRawX, out int gateRawY)) {
			log?.Invoke($"Thám quân | bỏ qua Điểm chuyển tiếp | chưa đo được toạ độ điểm trên Map{currentMapId}");
			return false;
		}
		int optionIndex;
		int expectedMapId;
		string routeEvidence;
		if (TransitGateDestinationCatalog.TryGetOptionIndex(currentMapId, targetMapId, out optionIndex, out string directReason)) {
			expectedMapId = targetMapId;
			routeEvidence = $"ĐíchNằmTrongDanhSách | {directReason}";
		} else if (TransitGateDestinationCatalog.TryGetNearestOptionIndex(currentMapId, targetMapId, out optionIndex, out int hubMapId, out int remainingTransitions, out string nearestReason)) {
			expectedMapId = hubMapId;
			routeEvidence = $"ĐíchNgoàiDanhSách | CònLại={remainingTransitions} chặng đi bộ | {nearestReason}";
		} else {
			log?.Invoke($"Thám quân | bỏ qua Điểm chuyển tiếp | {directReason}");
			return false;
		}
		transitGateOptionIndex = optionIndex;
		transitGateExpectedMapId = expectedMapId;
		transitGateSourceMapId = currentMapId;
		transitGateRawX = gateRawX;
		transitGateRawY = gateRawY;
		transitGateRouteSent = false;
		transitGateCommandSent = false;
		transitGateProgressUtc = DateTime.UtcNow;
		transitGateBestDistance = double.MaxValue;
		state = ScoutState.WalkingToTransitGate;
		nextMoveRefreshUtc = DateTime.MinValue;
		deadlineUtc = DateTime.UtcNow.AddMilliseconds(TransitGateWalkTimeoutMilliseconds + TransitGatePopupWaitMilliseconds);
		log?.Invoke($"Thám quân | đi tới Điểm chuyển tiếp trên Map{currentMapId} | Điểm={gateRawX}/{gateRawY} | Số={optionIndex} | ChờTới=Map{expectedMapId} | {routeEvidence}");
		return true;
	}

	// Mọi lối "thôi đi bộ" của CHẶNG ĐI đều đi qua đây, không gọi thẳng BeginWalking nữa.
	//
	// Điểm chuyển tiếp là phương án DỰ PHÒNG sau Di ngoại phù, nên chỉ được thử ở đúng lúc phù đã hỏng hoặc bị tắt.
	// Đây cũng là đường duy nhất đưa nhân vật tới các map Mê cung: đo trên Data\Maps thì map 22-26 và 32-36 KHÔNG có
	// trong menu Di ngoại phù, trước đây rơi thẳng xuống BeginWalking nên phải đi bộ 9 chặng.
	private void BeginWalkingOrTransitGate(GameWindow game, Action<string>? log, string reason) {
		if (! transitGateTried && TryBeginTransitGate(game, log)) {
			log?.Invoke($"Thám quân | không dùng được Di ngoại phù nên chuyển sang Điểm chuyển tiếp | {reason}");
			return;
		}
		BeginWalking(log, reason);
	}

	private void BeginWalking(Action<string>? log, string reason) {
		log?.Invoke($"Thám quân | chuyển sang đi bộ tới {targetNpcName} | {reason}");
		travelQueue.Reset();
		state = ScoutState.Travelling;
		nextProgressLogUtc = DateTime.UtcNow.AddSeconds(10);
	}


	// Tìm ô trang bị nhanh chứa vật phẩm có tên khớp. Cùng đường đọc mà QuestProbe dùng để liệt kê phù.
	// internal để TalismanTravelProbe dùng lại đúng đường đọc này, không chép thành bản thứ hai rồi lệch nhau.
	internal static bool TryFindQuickSlot(int processId, string itemName, out int slotIndex, out string evidence) {
		slotIndex = -1;
		List<string> slots = [];
		try {
			InventoryContainer quickSlot = InventoryContainer.SearchOrder[0];
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			IntPtr inventoryRoot = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.InventoryRoot));
			IntPtr itemTable = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.ItemTable));
			if (inventoryRoot == IntPtr.Zero || itemTable == IntPtr.Zero) {
				evidence = "InventoryRoot hoặc ItemTable bằng 0.";
				return false;
			}
			IntPtr slotList = reader.ReadPointer32(IntPtr.Add(IntPtr.Add(inventoryRoot, GameAddresses.Inventory.Object), quickSlot.ListPointerOffset));
			if (slotList == IntPtr.Zero) {
				evidence = "Danh sách ô trang bị nhanh bằng 0.";
				return false;
			}
			byte[] idBytes = reader.ReadBytes(slotList, quickSlot.SlotCount * sizeof(int));
			if (idBytes.Length != quickSlot.SlotCount * sizeof(int)) {
				evidence = "Đọc hụt danh sách ô trang bị nhanh.";
				return false;
			}
			for (int index = 0; index < quickSlot.SlotCount; index++) {
				int itemId = BitConverter.ToInt32(idBytes, index * sizeof(int));
				if (itemId <= 0) continue;
				long record = itemTable.ToInt64() + (long)itemId * GameAddresses.Item.InventoryRecordStride;
				if (record <= 0 || record > uint.MaxValue) continue;
				string name = InventoryContainer.ReadLegacyString(reader, new IntPtr(record + GameAddresses.Item.InventoryName), MaximumQuickSlotNameLength);
				slots.Add($"#{index + 1}={name}");
				if (slotIndex < 0 && name.Contains(itemName, StringComparison.OrdinalIgnoreCase)) slotIndex = index;
			}
		} catch (Exception ex) {
			evidence = $"ĐọcÔNhanhLỗi={ex.GetType().Name}: {ex.Message}";
			return false;
		}
		evidence = "ÔNhanh=[" + string.Join(",", slots) + "]";
		return slotIndex >= 0;
	}

	private bool HasDestination() => destinationMapId > 0 && destinationRawX > 0 && destinationRawY > 0;

	private void BeginDoctorPhase(Action<string>? log) {
		targetNpcName = DoctorNpcName;
		targetMapId = destinationMapId;
		targetRawX = destinationRawX;
		targetRawY = destinationRawY;
		// Ghi nhớ đích của lượt đang dở ngay khi cam kết đi, để bỏ tick/tick lại giữa chừng vẫn đi tiếp được.
		pendingMapName = destinationMapName;
		pendingMapId = destinationMapId;
		pendingRawX = destinationRawX;
		pendingRawY = destinationRawY;
		pendingDoctorVisited = false;
		BeginPhase(ScoutPhase.MeetingDoctor, log);
	}

	private bool HasPendingQuest() => pendingMapId > 0 && pendingRawX > 0 && pendingRawY > 0;

	private void ClearPendingQuest() {
		pendingMapName = "";
		pendingMapId = 0;
		pendingRawX = 0;
		pendingRawY = 0;
		pendingDoctorVisited = false;
	}

	private void BeginPhase(ScoutPhase next, Action<string>? log) {
		phase = next;
		if (next != ScoutPhase.MeetingDoctor) {
			targetNpcName = QuestNpcName;
			targetMapId = QuestNpcMapId;
			targetRawX = QuestNpcRawX;
			targetRawY = QuestNpcRawY;
		}
		if (next == ScoutPhase.ReceivingQuest) {
			destinationMapName = "";
			destinationMapId = 0;
			destinationRawX = 0;
			destinationRawY = 0;
		}
		travelQueue.Reset();
		// Thử phù trước, chạy bộ chỉ là đường lui. Bằng chứng vì sao bắt buộc (quest.log 2026-09-09 lượt 1,
		// PID 22056): chặng đi Miêu Cương mất 4 phút 18 giây (21:46:38 -> 21:50:56) qua 5 map, chặng về thêm
		// ~4 phút nữa. Một lượt gần 9 phút chỉ để đi lại.
		state = ScoutState.UsingTalisman;
		talismanTried = false;
		returnTalismanAttempts = 0;
		talismanUsedIndex = -1;
		talismanSourceMapId = 0;
		talismanExpectedMapId = 0;
		ResetTransitGate();
		nextActionUtc = DateTime.MinValue;
		deadlineUtc = DateTime.UtcNow.AddMilliseconds(TalismanMenuTimeoutMilliseconds);
		nextProgressLogUtc = DateTime.UtcNow.AddSeconds(10);
		log?.Invoke($"Thám quân | chặng {DescribePhase(next)} | Đích={targetNpcName} tại Map{targetMapId}/{targetRawX}/{targetRawY}");
	}

	private void BeginClosingPopup() {
		state = ScoutState.ClosingPopup;
		destinationRead = false;
		escapeAttempts = 0;
		nextActionUtc = DateTime.UtcNow.AddMilliseconds(ClosePopupDelayMilliseconds);
	}

	// Lấy map đích của nhiệm vụ từ popup, qua các đoạn chữ ĐƯỢC TÔ MÀU trong đó.
	//
	// Bố cục đọc được từ vùng thô (quest.log 2026-09-09, PID 22056):
	//   "... ta cần ngươi đi " 01 00FF00FF "Trần Đường" 02 " tìm Đại phu thu thập tin tức."
	// Mỗi đoạn tô màu là: byte 0x01, 4 byte màu, chữ, byte 0x02. Chính hai byte 0x01/0x02 này làm phép quét chuỗi
	// thường cắt vụn câu ("hiên Hóa." mất chữ đầu ở lượt dò trước).
	//
	// Lấy đoạn tô màu ĐẦU TIÊN tra được trong bảng tên bản đồ, thay vì bám vào câu chữ hay vào màu: cùng popup còn
	// có "Lực Nguyên" cũng màu xanh lục (00FF00FF) và "15" màu trắng (00FFFFFF), nhưng chúng không phải tên bản đồ
	// nên tự bị loại.
	//
	// NPC đích luôn là "Đại phu", chỉ map đổi — khớp AutoFS vốn cắm cứng chuỗi "Dai Phu" khi đọc file map
	// (MenuAttribute.cs:23913) và cắm cứng tên entity "Đại phu" khi tìm NPC (dòng 23958).
	private void ReadQuestDestination(int processId, Action<string>? log) {
		// Xoá đích cũ TRƯỚC khi đọc. Không xoá thì popup không có nhiệm vụ mới sẽ để nguyên giá trị của lượt trước
		// và HasDestination() báo nhầm là có — bằng chứng quest.log 2026-09-09 22:27:33 PID 21768: lượt cuối không
		// đọc ra đích nào nhưng Auto vẫn đi lại đúng Map65/52510/96680 của lượt liền trước.
		destinationMapName = "";
		destinationMapId = 0;
		destinationRawX = 0;
		destinationRawY = 0;
		unmatchedSegments = "";
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			IntPtr modal = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.ModalState));
			if (modal == IntPtr.Zero) {
				log?.Invoke("Thám quân | ĐÍCH KHÔNG ĐỌC ĐƯỢC | Không có popup nào đang mở.");
				return;
			}
			byte[] block = reader.ReadBytes(modal, ModalTextScanLength);
			List<string> segments = ReadColoredSegments(block);
			log?.Invoke($"Thám quân | đoạn tô màu trong popup | [{string.Join(" | ", segments)}]");
			foreach (string segment in segments) {
				if (! GameMapCatalog.TryResolve(segment, out int mapId)) continue;
				destinationMapName = segment;
				destinationMapId = mapId;
				if (GameMapReader.TryReadDoctorPoint(mapId, out int doctorRawX, out int doctorRawY, out string mapFailure)) {
					destinationRawX = doctorRawX;
					destinationRawY = doctorRawY;
					log?.Invoke($"Thám quân | ĐÍCH = {DoctorNpcName} ở '{segment}' | MapId={mapId} | Raw={doctorRawX}/{doctorRawY}");
				} else {
					log?.Invoke($"Thám quân | ĐÍCH = '{segment}' | MapId={mapId} | KHÔNG lấy được toạ độ {DoctorNpcName} | {mapFailure}");
				}
				return;
			}
			// Đánh dấu RIÊNG trường hợp popup CÓ chữ tô màu nhưng không tra ra map. Nó khác hẳn popup không có
			// nhiệm vụ mới, mà trước đây bị gộp làm một nên Auto báo "HOÀN TẤT" và dừng êm — che mất lỗi lệch tên.
			unmatchedSegments = segments.Count > 0 ? string.Join(" | ", segments) : "";
			log?.Invoke("Thám quân | ĐÍCH KHÔNG ĐỌC ĐƯỢC | Không đoạn tô màu nào khớp bảng tên bản đồ.");
		} catch (Exception ex) {
			log?.Invoke($"Thám quân | ĐÍCH KHÔNG ĐỌC ĐƯỢC | {ex.GetType().Name}: {ex.Message}");
		}
	}

	// Mỗi đoạn tô màu: 0x01, 4 byte màu, chữ, 0x02.
	private static List<string> ReadColoredSegments(byte[] block) {
		List<string> segments = [];
		for (int index = 0; index < block.Length; index++) {
			if (block[index] != ColorStartMarker) continue;
			int textStart = index + 1 + ColorPayloadLength;
			int end = textStart;
			while (end < block.Length && block[end] != ColorEndMarker && block[end] != 0 && end - textStart < MaximumColoredSegmentLength) end++;
			if (end >= block.Length || block[end] != ColorEndMarker || end == textStart) continue;
			string decoded = LegacyVietnameseText.Decode(block[textStart..end]).Trim();
			if (decoded.Length > 0) segments.Add(decoded);
			index = end;
		}
		return segments;
	}

	// Liệt kê mọi chuỗi kết thúc bằng null trong object popup đang mở, kèm offset. Chỉ đọc, không ghi gì.
	private static List<string> DumpModalText(int processId) {
		List<string> lines = [];
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			IntPtr modal = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.ModalState));
			if (modal == IntPtr.Zero) return ["Modal=0"];
			byte[] block = reader.ReadBytes(modal, ModalTextScanLength);
			if (block.Length == 0) return ["Không đọc được object popup."];
			int start = 0;
			while (start < block.Length && lines.Count < MaximumModalTextLines) {
				while (start < block.Length && ! IsTextByte(block[start])) start++;
				int end = start;
				while (end < block.Length && IsTextByte(block[end]) && end - start < MaximumModalTextLength) end++;
				if (end > start && end < block.Length && block[end] == 0) {
					string decoded = LegacyVietnameseText.Decode(block[start..end]);
					// Bỏ đường dẫn sprite: object giao diện nào cũng đầy ".spr", che mất chữ thật.
					if (decoded.Count(char.IsLetter) >= MinimumModalTextLetters && ! decoded.Contains(".spr", StringComparison.OrdinalIgnoreCase)) {
						lines.Add($"+0x{start:X4} | {decoded}");
					}
				}
				start = end > start ? end + 1 : start + 1;
			}
			if (lines.Count == 0) lines.Add("Không có chuỗi nào đạt điều kiện trong vùng đã quét.");
		} catch (Exception ex) {
			lines.Add($"ĐọcLỗi={ex.GetType().Name}: {ex.Message}");
		}
		return lines;
	}

	private static bool IsTextByte(byte value) => value is >= 0x20 and <= 0x7E || value >= 0x80;


	// So vtable của modal đang mở với vtable popup xác nhận của Đại Phu.
	private static string DescribeModalVtable(int processId, out bool isConfirmModal, out bool isAnyModalOpen) {
		isConfirmModal = false;
		isAnyModalOpen = false;
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			IntPtr modal = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.ModalState));
			if (modal == IntPtr.Zero) return "Modal=0x00000000 | KhớpPopupXácNhận=KHÔNG_CÓ_MODAL";
			isAnyModalOpen = true;
			long vtable = unchecked((uint)reader.ReadInt32(modal));
			isConfirmModal = vtable == moduleBase.ToInt64() + DoctorConfirmModalVtableRva;
			return $"Modal=0x{modal.ToInt64():X8} | Vtable=0x{vtable:X8} | VtableRva=0x{vtable - moduleBase.ToInt64():X6} | KhớpPopupXácNhận={(isConfirmModal ? "CÓ" : "KHÔNG")}";
		} catch (Exception ex) {
			return $"ĐọcModalLỗi={ex.GetType().Name}: {ex.Message}";
		}
	}

	private static IntPtr ReadMenu(int processId) {
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			return reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.ModalState));
		} catch {
			return IntPtr.Zero;
		}
	}

	// Cùng bố cục menu mà DoctorShopSemanticCommand đang dùng và QuestProbe đã đọc ra được "#0 | Thám quân".
	// Đòi khớp DUY NHẤT: hai mục cùng chứa chữ tìm kiếm thì dừng, không tự chọn cái đầu.
	private static bool TryFindMenuOption(int processId, IntPtr menu, string wanted, out int optionIndex, out string evidence) {
		optionIndex = -1;
		List<string> options = new(MenuOptionCount);
		try {
			using MemoryReader reader = new(processId);
			for (int index = 0; index < MenuOptionCount; index++) {
				string decoded = ReadOption(reader, menu, index);
				if (decoded.Length == 0) continue;
				options.Add($"#{index}={decoded}");
				if (! decoded.Contains(wanted, StringComparison.OrdinalIgnoreCase)) continue;
				if (optionIndex >= 0) {
					evidence = "Options=[" + string.Join(",", options) + "] | Match=AMBIGUOUS";
					return false;
				}
				optionIndex = index;
			}
		} catch (Exception ex) {
			evidence = $"ĐọcMenuLỗi={ex.GetType().Name}: {ex.Message}";
			return false;
		}
		evidence = "Options=[" + string.Join(",", options) + $"] | Match={(optionIndex >= 0 ? "UNIQUE" : "NOT_FOUND")}";
		return optionIndex >= 0;
	}

	private static string ReadOption(MemoryReader reader, IntPtr menu, int index) {
		byte[] bytes = reader.ReadBytes(IntPtr.Add(menu, MenuTextOffset + index * MenuOptionStride), MaximumMenuTextLength);
		int terminator = Array.IndexOf(bytes, (byte)0);
		if (terminator >= 0) bytes = bytes[..terminator];
		return LegacyVietnameseText.Decode(bytes).Trim();
	}

	// Vòng lặp dừng hẳn khi lỗi thay vì thử lại: chưa có bằng chứng nào cho thấy thử lại là an toàn, mà lặp lại một
	// chuỗi thao tác sai ở NPC chỉ làm log rối. Bỏ tick rồi tick lại ô "Làm nhiệm vụ" để chạy lần nữa.
	private bool Fail(string reason, Action<string>? log) {
		log?.Invoke($"Thám quân FAIL | Chặng={DescribePhase(phase)} | Lượt={completedRounds + 1}/{MaximumRounds} | {reason} | Bỏ tick rồi tick lại 'Làm nhiệm vụ' để thử lại.");
		roundFinished = true;
		Reset();
		return false;
	}

	private bool HasTimedOut() => DateTime.UtcNow >= deadlineUtc;

	// Cùng phép đo mà WeaponRepairAutomation và AutoFsTrainingOrderQueue dùng: trục X chia 256, trục Y chia 512.
	private static double GetDistance(int rawX1, int rawY1, int rawX2, int rawY2) {
		double deltaX = (rawX2 - rawX1) / 256.0;
		double deltaY = (rawY2 - rawY1) / 512.0;
		return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
	}

	private static string DescribePhase(ScoutPhase value) => value switch {
		ScoutPhase.ReceivingQuest => "NHẬN",
		ScoutPhase.MeetingDoctor => "GẶP ĐẠI PHU",
		_ => "TRẢ"
	};

	private void LogState(Action<string>? log) {
		if (state == lastLoggedState) return;
		lastLoggedState = state;
		string text = state switch {
			ScoutState.UsingTalisman => "đang thử dùng phù để tới map đích",
			ScoutState.WalkingToTransitGate => "đang đi ra Điểm chuyển tiếp",
			ScoutState.SelectingTransitGate => "đang chọn điểm đến ở Điểm chuyển tiếp",
			ScoutState.Travelling => "đang di chuyển tới map của NPC",
			ScoutState.Approaching => "đang áp sát NPC",
			ScoutState.ClickingNpc => "đã tới nơi, chuẩn bị click NPC",
			ScoutState.WaitingMenu => "đang chờ menu NPC",
			ScoutState.SelectingSecondLevel => "đang chọn Thám quân ở tầng 2",
			ScoutState.ConfirmingQuest => "đang chờ popup xác nhận tầng 3",
			ScoutState.ClosingPopup => "đang đóng popup",
			_ => ""
		};
		if (text.Length > 0) log?.Invoke("Thám quân | " + text);
	}

	private void Reset() {
		state = ScoutState.Idle;
		phase = ScoutPhase.ReceivingQuest;
		lastLoggedState = ScoutState.Idle;
		deadlineUtc = DateTime.MinValue;
		nextActionUtc = DateTime.MinValue;
		nextProgressLogUtc = DateTime.MinValue;
		nextMoveRefreshUtc = DateTime.MinValue;
		npcClickAttempts = 0;
		npcCoordinateFallbackUsed = false;
		selectedOptionEvidence = "";
		targetNpcName = "";
		targetMapId = 0;
		targetRawX = 0;
		targetRawY = 0;
		destinationMapName = "";
		destinationMapId = 0;
		destinationRawX = 0;
		destinationRawY = 0;
		destinationRead = false;
		escapeAttempts = 0;
		talismanTried = false;
		returnTalismanAttempts = 0;
		talismanUsedIndex = -1;
		talismanSourceMapId = 0;
		talismanExpectedMapId = 0;
		ResetTransitGate();
		travelQueue.Reset();
	}

	private void ResetTransitGate() {
		transitGateTried = false;
		transitGateOptionIndex = -1;
		transitGateExpectedMapId = 0;
		transitGateSourceMapId = 0;
		transitGateRawX = 0;
		transitGateRawY = 0;
		transitGateRouteSent = false;
		transitGateCommandSent = false;
		transitGateProgressUtc = DateTime.MinValue;
		transitGateBestDistance = double.MaxValue;
	}

	private enum ScoutPhase {
		ReceivingQuest,
		MeetingDoctor,
		ReturningQuest
	}

	private enum ScoutState {
		Idle,
		UsingTalisman,
		WalkingToTransitGate,
		SelectingTransitGate,
		Travelling,
		Approaching,
		ClickingNpc,
		WaitingMenu,
		SelectingSecondLevel,
		ConfirmingQuest,
		ClosingPopup
	}
}
