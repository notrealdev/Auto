namespace Auto.Loot;

public sealed class LootSnapshot {
	private const int RawXScale = 256;
	private const int RawYScale = 512;

	public int Index { get; set; }
	public IntPtr Address { get; set; }
	public IntPtr GroundObjectAddress { get; set; }
	public int Handle { get; set; }
	public int SlotIndex { get; set; }
	public int MirrorIndex { get; set; }
	public int ActiveFlag { get; set; }
	public int Level { get; set; }
	public int Hp { get; set; }
	public int MaxHp { get; set; }
	public int RawX { get; set; }
	public int RawY { get; set; }
	public int InternalX { get; set; }
	public int InternalY { get; set; }
	public int RawXMirror { get; set; }
	public int RawYMirror { get; set; }
	public int ClassPointer { get; set; }
	public int PrimaryPointer { get; set; }
	public double DistanceToCenter { get; set; }
	public double DistanceToPlayer { get; set; }
	public byte[] NameBytes { get; set; } = Array.Empty<byte>();
	public string NameAscii { get; set; } = "";
	public byte[] ItemNameBytes { get; set; } = Array.Empty<byte>();
	public string ItemNameRaw { get; set; } = "";
	public string Source { get; set; } = "Entity";
	public int MemoryFingerprint { get; set; }
	public int QualityCodeA { get; set; } = -1;
	public int QualityCodeB { get; set; } = -1;
	public int ProcessId { get; set; }
	public uint GroundType { get; set; }
	public uint GroundKind { get; set; }
	public int GroundId { get; set; }

	public bool RecordMatchesIndex => SlotIndex == Index && MirrorIndex == Index;
	public bool PositionMirrorMatches => RawX == RawXMirror && RawY == RawYMirror;
	public bool HasValidPosition => RawX > 0 && RawY > 0 && PositionMirrorMatches;
	public bool IsAlive => Hp > 0 && MaxHp > 0 && Hp <= MaxHp;
	public int GameCoordinateX => RawX / RawXScale;
	public int GameCoordinateY => RawY / RawYScale;

	public bool IsLikelyDroppedItem(bool requireName) {
		if (!RecordMatchesIndex || Handle <= 0 || ActiveFlag != 1 || !HasValidPosition) {
			return false;
		}

		if (IsAlive) {
			return false;
		}

		if (MaxHp > 0 && Level > 0) {
			return false;
		}

		return !requireName || NameBytes.Length > 0;
	}

	public override string ToString() {
		string name = string.IsNullOrWhiteSpace(NameAscii) ? "<empty>" : NameAscii;
		string itemName = string.IsNullOrWhiteSpace(ItemNameRaw) ? "<unknown>" : ItemNameRaw;
		return $"[{Index:D3}] Source={Source} Handle={Handle} Hash=0x{unchecked((uint)MemoryFingerprint):X8} Name={name} Item={itemName} Lv={Level} HP={Hp}/{MaxHp} Raw={RawX}/{RawY} Map={GameCoordinateX}/{GameCoordinateY} Distance={DistanceToPlayer:F2}";
	}
}
