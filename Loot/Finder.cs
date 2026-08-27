namespace Auto.Loot;

using Auto.Utils;

public sealed class Finder {
	private const int AttackSafetyMaxItems = 128;

	private readonly Settings settings;
	private readonly AutoFsGroundItemScanner spriteItemScanner = new();

	public Finder(Settings settings) {
		this.settings = settings;
	}

	public List<LootSnapshot> FindVisibleSpriteItems(GameSnapshot snapshot) {
		return spriteItemScanner.Find(snapshot.ProcessId, snapshot.X, snapshot.Y, settings.Range, 128);
	}

	public List<LootSnapshot> FindVisibleSpriteItemsForAttackSafety(GameSnapshot snapshot) {
		return spriteItemScanner.Find(snapshot.ProcessId, snapshot.X, snapshot.Y, Math.Max(settings.Range, 10), AttackSafetyMaxItems);
	}

	public LootFindResult Find(GameSnapshot snapshot) {
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
			int centerX = GetCenterX(snapshot);
			int centerY = GetCenterY(snapshot);
			int maxRange = Math.Max(settings.Range, 1);

			result.Success = true;
			result.CenterRawX = centerX;
			result.CenterRawY = centerY;
			result.Range = 0;

			AddSpriteItemCandidates(snapshot, result, centerX, centerY, maxRange);
			result.ObservedItems.AddRange(result.Candidates);
			result.Candidates.RemoveAll(item => !ShouldPick(item, ItemGroupClassifier.Classify(item), settings));

			result.Candidates.Sort(CompareCandidates);

			return result;
		} catch (Exception ex) {
			result.Success = false;
			result.FailReason = ex.Message;
			return result;
		}
	}

	public static LootFilterDecision EvaluateFilter(LootSnapshot item, Settings settings) {
		ItemClassification classification = ItemGroupClassifier.Classify(item);
		bool accepted = ShouldPick(item, classification, settings);
		return new LootFilterDecision(classification, accepted, DescribeFilterReason(item, classification, settings, accepted));
	}

	// Áp dụng category đã biết trước item riêng để checkbox category luôn có quyền quyết định
	private static bool ShouldPick(LootSnapshot snapshot, ItemClassification item, Settings settings) {
		if (item.Group != ItemGroup.Normal) return false;
		string itemName = ItemGroupClassifier.NormalizeName(snapshot.ItemNameRaw);
		if (GetExclusionReason(itemName, item, settings).Length > 0) return false;
		bool explicitlySelected = IsExactItemSelected(settings, itemName);
		AutoFsSpecialItemCategory specialCategory = AutoFsSpecialItemClassifier.Classify(itemName);
		if (specialCategory != AutoFsSpecialItemCategory.None) return IsSelected(settings, GetSelectionName(specialCategory));
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
		return name is "Đồ Trắng" or "Đồ Xanh" or "Đồ Lục" or "Đồ Vàng" or "Đồ Cam" or "Đồ Khác" or "Dược Phẩm" or "Mảnh, Ngọc" or "Bí Kíp" or "Pháp Bảo" or "Quẻ" or "Lục Đạo" or "Tứ Tượng" or "Nhãn Vạn Tiên Trận";
	}
	private static string GetSelectionName(AutoFsSpecialItemCategory category) {
		return category switch {
			AutoFsSpecialItemCategory.SkillBook => "Bí Kíp",
			AutoFsSpecialItemCategory.Artifact => "Pháp Bảo",
			AutoFsSpecialItemCategory.Trigram => "Quẻ",
			AutoFsSpecialItemCategory.SixPaths => "Lục Đạo",
			AutoFsSpecialItemCategory.FourSymbols => "Tứ Tượng",
			AutoFsSpecialItemCategory.ImmortalFormationLabel => "Nhãn Vạn Tiên Trận",
			_ => ""
		};
	}

	private static bool IsColorAllowed(ItemColor color, Settings settings) {
		return color switch { ItemColor.White => settings.PickWhite, ItemColor.Blue => settings.PickBlue, ItemColor.Green => settings.PickGreen, ItemColor.Yellow => settings.PickYellow, ItemColor.Orange => settings.PickOrange, _ => settings.PickOtherColor };
	}

	private static string DescribeFilterReason(LootSnapshot snapshot, ItemClassification item, Settings settings, bool accepted) {
		string exclusionReason = GetExclusionReason(ItemGroupClassifier.NormalizeName(snapshot.ItemNameRaw), item, settings);
		if (exclusionReason.Length > 0) return exclusionReason;
		if (accepted) return "ALLOWED";
		AutoFsSpecialItemCategory specialCategory = AutoFsSpecialItemClassifier.Classify(snapshot.ItemNameRaw);
		if (specialCategory != AutoFsSpecialItemCategory.None) return $"AUTOFS_{specialCategory.ToString().ToUpperInvariant()}_DISABLED";
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

	private int GetCenterX(GameSnapshot snapshot) {
		if (settings.UseCenterPosition && settings.CenterX > 0) {
			return settings.CenterX;
		}

		return snapshot.X;
	}

	private int GetCenterY(GameSnapshot snapshot) {
		if (settings.UseCenterPosition && settings.CenterY > 0) {
			return settings.CenterY;
		}

		return snapshot.Y;
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
		return $"Find Loot | CenterRaw={CenterRawX}/{CenterRawY} | Range=UNLIMITED | Rows={ReadCount} | ReadFail={ReadFailedCount} | GroundPointers={SpriteGroundPointerCount} | CoordinateReadFail={SpriteCoordinateReadFailureCount} | Rejected={RejectedCount} | OutOfRange={OutOfRangeCount} | Sprite={SpriteCandidateCount} | SpriteRaw={SpritePatternCount} | SpriteOut={SpriteOutOfRangeCount} | SpriteStale={SpriteStaleCount} | SpriteBad={SpriteBadCount} | SpriteDup={SpriteDuplicateCount} | SpriteNoName={SpriteNameRejectedCount} | SpriteRegions={SpriteReadableRegionCount} | Startup={StartupIgnoredCount} | Handled={HandledIgnoredCount} | Accepted={Candidates.Count}{ignoredPreview}{spritePreview}";
	}
}

public sealed record LootFilterDecision(ItemClassification Classification, bool Accepted, string Reason);
