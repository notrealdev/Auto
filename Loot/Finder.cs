namespace Auto.Loot;

using Auto.Utils;

public sealed class Finder {
	private const int AttackSafetyMaxItems = 128;
	// Quy đổi 1 ô hiển thị = 256 raw trục X, dùng riêng cho bán kính liệt kê đồ dưới đất ở ô chọn tab Nhặt.
	private const int GroundDiscoveryRawPerMapUnit = 256;
	// Bán kính quét Nhặt tính từ chính nhân vật. Giữ nhỏ để lệnh nhặt không bao giờ kéo nhân vật ra khỏi bãi.
	// Đơn vị là raw: 1 ô toạ độ hiển thị = 256 raw theo trục X và 512 raw theo trục Y, nên 150 raw chưa tới nửa ô.
	// public để Loot/Engine dùng chung đúng con số này cho chốt chặn OUT_OF_SCAN_RADIUS trong vòng lặp nhặt.
	public const int PlayerScanRadius = 150;
	// Một chỗ duy nhất giữ nhãn nhóm "Vũ khí xanh": Finder đọc, LootViewModel dựng ô tick, Settings đặt mặc định.
	public const string GreenWeaponSelectionName = "Vũ khí xanh";
	// Cùng khuôn với trên: nhãn nhóm "Bá Lạc Nhãn" (vật phẩm cường hoá thú cưỡi) chỉ khai báo ở đúng một chỗ.
	public const string MountUpgradeSelectionName = "Bá Lạc Nhãn";

	private readonly Settings settings;
	private readonly AutoFsGroundItemScanner spriteItemScanner = new();
	private readonly InventoryPotionCounter potionCounter = new();

	public Finder(Settings settings) {
		this.settings = settings;
	}

	public List<LootSnapshot> FindVisibleSpriteItems(GameSnapshot snapshot) {
		return spriteItemScanner.Find(snapshot.ProcessId, snapshot.X, snapshot.Y, settings.Range, 128);
	}

	// Dùng cho MỘT nơi duy nhất: đổ danh sách tên đồ dưới đất cho ô chọn ở tab Nhặt (Loot.Engine.DiscoverItemOptions).
	//
	// SỬA 2026-09-23 — lỗi lệch đơn vị. Trước đây truyền thẳng Math.Max(settings.Range, 10) làm bán kính, nhưng
	// AutoFsGroundItemScanner so bán kính với GetRawDistance, tức đơn vị RAW (AutoFsGroundItemScanner.cs:89-90).
	// settings.Range là số ô trên giao diện (mặc định 10), nên bán kính thật chỉ là 10 raw — chưa tới 1/25 ô, đồ
	// phải nằm đúng chỗ nhân vật đứng mới lọt. Đo bằng scratchpad trên cả 6 client lúc dưới đất ĐANG có đồ: hàm
	// này trả về 0 ở cả 6, đúng hiện tượng "ô chọn không hiện gì".
	//
	// Lỗi chỉ lộ ra sau khi bộ lọc bán kính được thêm vào scanner; trước đó tham số bị bỏ qua nên truyền gì cũng chạy
	// (xem chính ghi chú ở AutoFsGroundItemScanner.cs:86-88).
	//
	// Quy đổi 1 ô = 256 raw, cùng thang RawXScale mà AttackViewModel dùng. KHÔNG dùng PlayerScanRadius (150 raw)
	// của luồng nhặt thật: đó là bán kính để RA TAY nhặt, còn đây chỉ liệt kê tên cho người dùng chọn nên nhìn rộng
	// bằng đúng ô Range người dùng đặt thì hợp lý hơn.
	public List<LootSnapshot> FindVisibleSpriteItemsForAttackSafety(GameSnapshot snapshot) {
		int rangeRaw = Math.Max(settings.Range, 1) * GroundDiscoveryRawPerMapUnit;
		return spriteItemScanner.Find(snapshot.ProcessId, snapshot.X, snapshot.Y, rangeRaw, AttackSafetyMaxItems);
	}

	public LootFindResult Find(GameSnapshot snapshot) {
		long profilerStart = Auto.Runtime.HotPathProfiler.Begin();
		try {
		LootFindResult result = new LootFindResult { ProcessId = snapshot.ProcessId };

		if (!snapshot.Success) {
			result.FailReason = "Snapshot không hợp lệ: " + snapshot.FailReason;
			return result;
		}

		if (snapshot.ProcessId <= 0) {
			result.FailReason = "ProcessId chưa hợp lệ.";
			return result;
		}

		try {
			// Quét quanh chính nhân vật với bán kính nhỏ cố định, không dùng tâm bãi nữa.
			// Trước đây quét quanh tâm bãi với bán kính 1.5 lần phạm vi đánh (1800 khi đánh 1200), nên một item nằm
			// ngoài phạm vi đánh vẫn được nhận và lệnh nhặt kéo nhân vật ra khỏi bãi. Bán kính 150 nhỏ hơn ngưỡng 200
			// mà AutoFS dùng cho lệnh 9 (AGENTS.md "Confirmed AutoFS Loot Flow"), nên item chỉ được nhận khi đã ở sát nhân vật.
			int centerX = snapshot.X;
			int centerY = snapshot.Y;
			int maxRange = PlayerScanRadius;

			result.Success = true;
			result.CenterRawX = centerX;
			result.CenterRawY = centerY;
			result.Range = maxRange;

			AddSpriteItemCandidates(snapshot, result, centerX, centerY, maxRange);
			result.ObservedItems.AddRange(result.Candidates);
			// Chủ dự án báo 2026-09-24: nhân vật nhặt tới mức vượt sức lực tối đa thì game khoá di chuyển. Nhặt trước
			// đây không hề biết tới sức lực (0 tham chiếu InventoryStrengthReader trong cả file), nên dù luồng đi bán
			// đã đúng công thức (RemainingStrength = Maximum - Current, đi bán khi < ngưỡng) vẫn không kịp cản một
			// lần nhặt duy nhất đẩy Current vọt qua Maximum. Chặn nhặt tiếp ngay khi chạm đúng ngưỡng đi bán, cùng
			// một điều kiện với Sale/InventorySaleEngine.ReadAutomaticTrigger để hai nơi không lệch nhau.
			if (IsCarryingCapacityExhausted(snapshot.ProcessId)) {
				result.Candidates.Clear();
			} else {
				result.Candidates.RemoveAll(item => !ShouldPick(item, ItemGroupClassifier.Classify(item)));
			}

			result.Candidates.Sort(CompareCandidates);

			return result;
		} catch (Exception ex) {
			result.Success = false;
			result.FailReason = ex.Message;
			return result;
		}
		} finally {
			Auto.Runtime.HotPathProfiler.End(Auto.Runtime.HotPathProfiler.LootScan, profilerStart);
		}
	}

	public LootFilterDecision EvaluateFilter(LootSnapshot item) {
		ItemClassification classification = ItemGroupClassifier.Classify(item);
		bool accepted = ShouldPick(item, classification);
		return new LootFilterDecision(classification, accepted, DescribeFilterReason(item, classification, accepted));
	}

	public void InvalidatePotionCounts() => potionCounter.Invalidate();

	// Đếm TƯƠI số lượng một item bất kỳ trong túi. Bỏ cache trước khi đọc vì nơi gọi cần so trước/sau một lệnh nhặt,
	// mà cache còn hạn sẽ trả về đúng con số cũ ở cả hai lần đo.
	//
	// Dùng lại nguyên InventoryPotionCounter: Refresh của nó gom TẤT CẢ tên trong 3 container chứ không riêng dược
	// phẩm, chỉ có GetPotionLimitReason mới giới hạn ở 6 loại trong popup.
	public bool TryCountInInventory(int processId, string itemName, out int count) {
		count = 0;
		string key = InventoryPotionCounter.ToKey(itemName);
		if (key.Length == 0) return false;
		potionCounter.Invalidate();
		return potionCounter.TryGetCount(processId, key, out count);
	}

	// Chụp tươi toàn bộ túi để nơi gọi so trước/sau một lệnh nhặt. Bỏ cache vì lý do y hệt TryCountInInventory.
	public bool TrySnapshotInventory(int processId, out Dictionary<string, int> snapshot) {
		potionCounter.Invalidate();
		return potionCounter.TrySnapshot(processId, out snapshot);
	}

	// Khoá của một tên trong bảng chụp ở trên. Tên dưới đất có hậu tố "xN" nên phải chuẩn hoá mới so được.
	public static string ToInventoryKey(string itemName) => InventoryPotionCounter.ToKey(itemName);

	public string[] ConsumePotionCountDiagnostics() => potionCounter.ConsumeDiagnostics();

	// public để Loot/Engine.ProcessCandidateBatch gọi lại GIỮA CHỪNG một loạt nhặt, không chỉ một lần đầu mỗi vòng
	// quét — một batch có thể chứa nhiều món, nhặt hết cả loạt mới quay lại Find() là quá trễ.
	//
	// Chỉ đọc bộ nhớ khi tính năng "Sức lực còn dưới ngưỡng" đang bật — tắt thì giữ nguyên hành vi cũ, không thêm
	// chi phí đọc bộ nhớ mỗi vòng quét 200ms.
	public bool IsCarryingCapacityExhausted(int processId) {
		if (! settings.EnableSaleRemainingStrengthThreshold) return false;
		InventoryStrengthReading strength = InventoryStrengthReader.Read(processId);
		return strength.Success && strength.Free < settings.SaleRemainingStrengthThreshold;
	}

	// Áp dụng category đã biết trước item riêng để checkbox category luôn có quyền quyết định
	private bool ShouldPick(LootSnapshot snapshot, ItemClassification item) {
		if (item.Group != ItemGroup.Normal) return false;
		string itemName = ItemGroupClassifier.NormalizeName(snapshot.ItemNameRaw);
		if (GetExclusionReason(itemName, item, settings).Length > 0) return false;
		bool explicitlySelected = IsExactItemSelected(settings, itemName);
		AutoFsSpecialItemCategory specialCategory = AutoFsSpecialItemClassifier.Classify(itemName);
		// "Vũ khí xanh" là nhóm CHỈ THÊM, không bao giờ bớt — khác mọi nhóm còn lại.
		//
		// Chủ dự án chốt 2026-09-12: các ô tick màu (Đồ Lục / Đồ Vàng / Đồ Cam) giữ quyền ƯU TIÊN SỐ 1. Nên ở đây
		// chỉ nhận thẳng khi tên nằm trong danh sách VÀ màu là xanh lục; mọi trường hợp còn lại rơi xuống nguyên
		// luật cũ bên dưới thay vì return false. Nhờ vậy rìu vàng/cam vẫn được nhặt qua ô tick màu của chúng, và
		// nhóm này không thể làm mất món nào so với trước khi có nó.
		if (specialCategory == AutoFsSpecialItemCategory.GreenWeapon) {
			if (IsSelected(settings, GreenWeaponSelectionName) && item.Color == ItemColor.Green) return true;
		} else if (specialCategory != AutoFsSpecialItemCategory.None) {
			return IsSelected(settings, GetSelectionName(specialCategory));
		}
		// Ngưỡng số lượng đặt TRƯỚC nhánh explicitlySelected: gõ tay tên dược phẩm vào ô "Vật phẩm" cũng không vượt được giới hạn.
		if (GetPotionLimitReason(snapshot, itemName).Length > 0) return false;
		bool allowed = explicitlySelected
			? true
			: item.AttributeClass switch {
				1 => IsSelected(settings, "Dược Phẩm") && IsPotionNameAllowed(itemName, settings),
				3 => IsSelected(settings, "Mảnh, Ngọc"),
				_ => IsColorAllowed(item.Color, settings)
			};
		return allowed;
	}

	// Tên khớp 1 mục trong popup "Dược Phẩm" thì theo trạng thái mục đó; tên ngoài danh sách vẫn được nhặt.
	private static bool IsPotionNameAllowed(string groundName, Settings settings) {
		foreach ((string name, bool enabled) in settings.PotionNameSelections) {
			string potionName = ItemGroupClassifier.NormalizeName(name);
			if (potionName.Contains(groundName, StringComparison.OrdinalIgnoreCase) || groundName.Contains(potionName, StringComparison.OrdinalIgnoreCase)) return enabled;
		}
		return true;
	}

	// Trả về lý do chặn khi nhặt chồng item này sẽ làm tổng vượt ngưỡng, chuỗi rỗng nghĩa là cho nhặt.
	// Luật chốt với chủ dự án 2026-09-07: cộng SỐ TRONG TÚI + SỐ CHỒNG DƯỚI ĐẤT, tổng > ngưỡng thì không nhặt.
	// Ví dụ ngưỡng 10: có 9 gặp chồng x1 -> tổng 10, nhặt; có 9 gặp chồng x5 -> tổng 14, không nhặt.
	// Đánh đổi đã báo và chủ dự án đã chốt: nếu bãi chỉ rơi chồng x5 thì nhân vật đứng yên ở 9 viên, không lên nữa.
	// Chỉ áp cho đúng 6 loại trong popup "Dược Phẩm" và so khớp BẰNG NHAU sau chuẩn hoá - cố tình khác
	// IsPotionNameAllowed (bao hàm 2 chiều), vì đếm nhầm sang item khác sẽ âm thầm ngừng nhặt, lỗi rất khó chẩn đoán.
	private string GetPotionLimitReason(LootSnapshot snapshot, string itemName) {
		int limit = settings.PotionQuantityLimit;
		if (limit <= 0) return "";
		string key = InventoryPotionCounter.ToKey(itemName);
		if (key.Length == 0) return "";
		bool isKnownPotion = false;
		foreach ((string name, _) in settings.PotionNameSelections) {
			if (! string.Equals(InventoryPotionCounter.ToKey(name), key, StringComparison.Ordinal)) continue;
			isKnownPotion = true;
			break;
		}
		// Không phải dược phẩm trong danh sách thì không đọc túi lần nào - đây là chỗ giữ cho vòng quét không phát sinh chi phí.
		if (! isKnownPotion) return "";
		if (! potionCounter.TryGetCount(snapshot.ProcessId, key, out int count)) return "";
		int stack = AutoFsSpecialItemClassifier.GetGroundStackCount(snapshot.ItemNameRaw);
		if (count + stack <= limit) return "";
		return $"MEDICINE_QUANTITY_LIMIT_{key}_{count}+{stack}/{limit}";
	}

	private static bool IsSelected(Settings settings, string name) => settings.ItemSelections.TryGetValue(name, out bool enabled) && enabled;
	private static string GetExclusionReason(string groundName, ItemClassification item, Settings settings) {
		if (item.Color == ItemColor.White && IsExcludedSelection(settings, "Đồ Trắng")) return "EXCLUDED_WHITE";
		if (item.Color == ItemColor.Blue && IsExcludedSelection(settings, "Đồ Xanh")) return "EXCLUDED_BLUE";
		if (item.AttributeClass == 1 && IsExcludedSelection(settings, "Dược Phẩm")) return "EXCLUDED_MEDICINE";
		foreach ((string name, bool enabled) in settings.ExcludedItemSelections) {
			if (! enabled || IsBuiltInExcludedSelection(name)) continue;
			string excludedName = ItemGroupClassifier.NormalizeName(name);
			if (excludedName.Contains(groundName, StringComparison.OrdinalIgnoreCase) || groundName.Contains(excludedName, StringComparison.OrdinalIgnoreCase)) return $"EXCLUDED_EXACT_ITEM_{name}";
		}
		return "";
	}
	private static bool IsExcludedSelection(Settings settings, string name) => settings.ExcludedItemSelections.TryGetValue(name, out bool enabled) && enabled;
	private static bool IsBuiltInExcludedSelection(string name) => name is "Đồ Trắng" or "Đồ Xanh" or "Dược Phẩm";
	private static bool IsExactItemSelected(Settings settings, string groundName) {
		foreach ((string name, bool enabled) in settings.ItemSelections) {
			if (! enabled || IsBuiltInSelection(name)) continue;
			string selectedName = ItemGroupClassifier.NormalizeName(name);
			if (selectedName.Contains(groundName, StringComparison.OrdinalIgnoreCase) || groundName.Contains(selectedName, StringComparison.OrdinalIgnoreCase)) return true;
		}
		return false;
	}
	private static bool IsBuiltInSelection(string name) {
		return name is "Đồ Trắng" or "Đồ Xanh" or "Đồ Lục" or "Đồ Vàng" or "Đồ Cam" or "Đồ Khác" or "Dược Phẩm" or "Thảo Dược" or GreenWeaponSelectionName or "Mảnh, Ngọc" or "Bí Kíp" or "Pháp Bảo" or "Quẻ" or "Lục Đạo" or "Tứ Tượng" or "Nhãn Vạn Tiên Trận" or MountUpgradeSelectionName;
	}
	private static string GetSelectionName(AutoFsSpecialItemCategory category) {
		return category switch {
			AutoFsSpecialItemCategory.SkillBook => "Bí Kíp",
			AutoFsSpecialItemCategory.Artifact => "Pháp Bảo",
			AutoFsSpecialItemCategory.Trigram => "Quẻ",
			AutoFsSpecialItemCategory.SixPaths => "Lục Đạo",
			AutoFsSpecialItemCategory.FourSymbols => "Tứ Tượng",
			AutoFsSpecialItemCategory.ImmortalFormationLabel => "Nhãn Vạn Tiên Trận",
			AutoFsSpecialItemCategory.Herb => "Thảo Dược",
			AutoFsSpecialItemCategory.GreenWeapon => GreenWeaponSelectionName,
			AutoFsSpecialItemCategory.MountUpgrade => MountUpgradeSelectionName,
			_ => ""
		};
	}

	private static bool IsColorAllowed(ItemColor color, Settings settings) {
		return color switch { ItemColor.White => settings.PickWhite, ItemColor.Blue => settings.PickBlue, ItemColor.Green => settings.PickGreen, ItemColor.Yellow => settings.PickYellow, ItemColor.Orange => settings.PickOrange, _ => settings.PickOtherColor };
	}

	private string DescribeFilterReason(LootSnapshot snapshot, ItemClassification item, bool accepted) {
		string itemName = ItemGroupClassifier.NormalizeName(snapshot.ItemNameRaw);
		string exclusionReason = GetExclusionReason(itemName, item, settings);
		if (exclusionReason.Length > 0) return exclusionReason;
		if (accepted) return "ALLOWED";
		// Phân loại từ itemName ĐÃ CHUẨN HOÁ, đúng thứ ShouldPick dùng ở trên. Trước 2026-09-17 chỗ này truyền
		// snapshot.ItemNameRaw còn ShouldPick truyền tên đã qua ItemGroupClassifier.NormalizeName — hai đầu vào khác
		// nhau, nên lý do in ra log có thể không phải lý do bộ lọc thật sự dùng.
		AutoFsSpecialItemCategory specialCategory = AutoFsSpecialItemClassifier.Classify(itemName);
		// GreenWeapon KHÔNG báo AUTOFS_..._DISABLED: nhóm đó chỉ thêm chứ không loại, nên món bị bỏ là do luật màu
		// bên dưới chứ không phải do ô tick "Vũ khí xanh". Báo nhầm ở đây là gửi chủ dự án đi sai hướng khi đọc log.
		if (specialCategory != AutoFsSpecialItemCategory.None && specialCategory != AutoFsSpecialItemCategory.GreenWeapon) {
			return $"AUTOFS_{specialCategory.ToString().ToUpperInvariant()}_DISABLED";
		}
		// Phải đứng trước nhánh MEDICINE_DISABLED bên dưới, nếu không log sẽ báo là checkbox bị tắt trong khi thật ra là chạm ngưỡng.
		string potionLimitReason = GetPotionLimitReason(snapshot, itemName);
		if (potionLimitReason.Length > 0) return potionLimitReason;
		return item.Group switch {
			ItemGroup.Normal when item.AttributeClass == 1 => "MEDICINE_DISABLED",
			ItemGroup.Normal when item.AttributeClass == 3 => "FRAGMENT_GEM_DISABLED",
			ItemGroup.Normal => "COLOR_AND_EXACT_ITEM_DISABLED",
			ItemGroup.Herbal => "HERBAL_EXCLUDED",
			_ => "EXACT_ITEM_NOT_SELECTED"
		};
	}

	private void AddSpriteItemCandidates(GameSnapshot snapshot, LootFindResult result, int centerX, int centerY, int maxRange) {
		LootSpriteScanResult spriteScan = spriteItemScanner.Scan(snapshot.ProcessId, centerX, centerY, maxRange, settings.MaxCandidates);
		List<LootSnapshot> spriteItems = spriteScan.Items;
		result.SpriteCandidateCount = spriteItems.Count;
		result.ReadCount = 127;
		result.ReadFailedCount = spriteScan.Diagnostics.CoordinateReadFailureCount;
		result.SpriteReadableRegionCount = spriteScan.Diagnostics.ReadableRegionCount;
		result.SpritePatternCount = spriteScan.Diagnostics.PatternCount;
		result.SpriteGroundPointerCount = spriteScan.Diagnostics.GroundPointerCount;
		result.SpriteCoordinateReadFailureCount = spriteScan.Diagnostics.CoordinateReadFailureCount;
		result.SpriteOutOfRangeCount = spriteScan.Diagnostics.OutOfRangeCount;
		result.SpriteStaleCount = spriteScan.Diagnostics.StaleCount;
		result.SpriteBadCount = spriteScan.Diagnostics.BadCount;
		result.SpriteDuplicateCount = spriteScan.Diagnostics.DuplicateCount;
		result.SpriteRejectedPreview = spriteScan.Diagnostics.FirstRejected;
		result.SpriteRejectedDetails.AddRange(spriteScan.Diagnostics.Rejections);

		foreach (LootSnapshot item in spriteItems) {
			item.DistanceToCenter = GetRawDistance(item.RawX, item.RawY, centerX, centerY);
			item.DistanceToPlayer = GetRawDistance(item.RawX, item.RawY, snapshot.X, snapshot.Y);

			if (item.ItemNameBytes.Length == 0) {
				result.RejectedCount++;
				result.SpriteNameRejectedCount++;
				continue;
			}

			result.Candidates.Add(item);
		}
	}


	private static double GetRawDistance(int rawX1, int rawY1, int rawX2, int rawY2) {
		long deltaX = rawX1 - rawX2;
		long deltaY = rawY1 - rawY2;
		return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
	}

	private static int CompareCandidates(LootSnapshot left, LootSnapshot right) {
		int distanceCompare = left.DistanceToPlayer.CompareTo(right.DistanceToPlayer);
		return distanceCompare != 0 ? distanceCompare : left.Index.CompareTo(right.Index);
	}

}

public sealed class LootFindResult {
	public bool Success { get; set; }
	public string FailReason { get; set; } = "";
	public int ProcessId { get; set; }
	public IntPtr TableBase { get; set; }
	public int CenterRawX { get; set; }
	public int CenterRawY { get; set; }
	public int Range { get; set; }
	public int ReadCount { get; set; }
	public int ReadFailedCount { get; set; }
	public int LocalPlayerCount { get; set; }
	public int RejectedCount { get; set; }
	public int OutOfRangeCount { get; set; }
	public int TrimmedCount { get; set; }
	public int SpriteCandidateCount { get; set; }
	public int SpriteReadableRegionCount { get; set; }
	public int SpritePatternCount { get; set; }
	public int SpriteGroundPointerCount { get; set; }
	public int SpriteCoordinateReadFailureCount { get; set; }
	public int SpriteOutOfRangeCount { get; set; }
	public int SpriteStaleCount { get; set; }
	public int SpriteBadCount { get; set; }
	public int SpriteDuplicateCount { get; set; }
	public int SpriteNameRejectedCount { get; set; }
	public string SpriteRejectedPreview { get; set; } = "";
	public List<string> SpriteRejectedDetails { get; } = new();
	public int HandledIgnoredCount { get; set; }
	public int StartupIgnoredCount { get; set; }
	public string HandledIgnoredPreview { get; set; } = "";
	public List<LootSnapshot> ObservedItems { get; } = new();
	public List<LootSnapshot> Candidates { get; } = new();

	public string ToSummary() {
		if (!Success) {
			return "Find Loot FAIL: " + FailReason;
		}

		string ignoredPreview = string.IsNullOrWhiteSpace(HandledIgnoredPreview) ? "" : " | IgnoredFirst=" + HandledIgnoredPreview;
		string spritePreview = string.IsNullOrWhiteSpace(SpriteRejectedPreview) ? "" : " | SpriteFirst=" + SpriteRejectedPreview;
		return $"Find Loot | CenterRaw={CenterRawX}/{CenterRawY} | Range={Range} | Rows={ReadCount} | ReadFail={ReadFailedCount} | GroundPointers={SpriteGroundPointerCount} | CoordinateReadFail={SpriteCoordinateReadFailureCount} | Rejected={RejectedCount} | OutOfRange={OutOfRangeCount} | Sprite={SpriteCandidateCount} | SpriteRaw={SpritePatternCount} | SpriteOut={SpriteOutOfRangeCount} | SpriteStale={SpriteStaleCount} | SpriteBad={SpriteBadCount} | SpriteDup={SpriteDuplicateCount} | SpriteNoName={SpriteNameRejectedCount} | SpriteRegions={SpriteReadableRegionCount} | Startup={StartupIgnoredCount} | Handled={HandledIgnoredCount} | Accepted={Candidates.Count}{ignoredPreview}{spritePreview}";
	}
}

public sealed record LootFilterDecision(ItemClassification Classification, bool Accepted, string Reason);
