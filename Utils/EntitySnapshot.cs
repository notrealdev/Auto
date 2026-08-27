using System.Text;

namespace Auto.Utils;

public enum TargetLifecycleState {
	Active,
	Inactive,
	Missing,
	Unknown
}

public sealed record TargetLifecycleInspection(TargetLifecycleState State, EntitySnapshot? Entity, string Evidence);

public sealed class EntitySnapshot {
	private const int GlobalEntityTableOffset = GameAddresses.Globals.EntityTable;
	private const int CurrentTargetIndexOffset = GameAddresses.Globals.CurrentTargetIndex;
	private const int EntityStride = GameAddresses.Entity.Stride;

	private const int HandleOffset = GameAddresses.Entity.Handle;
	private const int SlotIndexOffset = GameAddresses.Entity.SlotIndex;
	private const int ClassPointerOffset = GameAddresses.Entity.ClassPointer;
	private const int PrimaryPointerOffset = GameAddresses.Entity.PrimaryPointer;
	private const int SecondaryPointerOffset = GameAddresses.Entity.SecondaryPointer;
	private const int MirrorIndexOffset = GameAddresses.Entity.MirrorIndex;
	private const int ActiveFlagOffset = GameAddresses.Entity.ActiveFlag;
	private const int LevelOffset = GameAddresses.Entity.Level;

	private const int HpOffset = GameAddresses.Entity.Hp;
	private const int MaxHpOffset = GameAddresses.Entity.MaxHp;
	private const int MpOffset = GameAddresses.Entity.Mp;
	private const int MaxMpOffset = GameAddresses.Entity.MaxMp;

	private const int MoveTargetXCandidateOffset = GameAddresses.Entity.MoveTargetXCandidate;
	private const int MoveTargetYCandidateOffset = GameAddresses.Entity.MoveTargetYCandidate;

	private const int AnchorXCandidateOffset = GameAddresses.Entity.AnchorXCandidate;
	private const int AnchorYCandidateOffset = GameAddresses.Entity.AnchorYCandidate;

	private const int RawXOffset = GameAddresses.Entity.RawX;
	private const int RawYOffset = GameAddresses.Entity.RawY;

	private const int RawXMirrorOffset = GameAddresses.Entity.RawXMirror;
	private const int RawYMirrorOffset = GameAddresses.Entity.RawYMirror;

	private const int RawXScale = 256;
	private const int RawYScale = 512;

	private const long MinimumLikelyAddress = 0x01000000;
	private const long MaximumUserModeAddress = 0x7FFFFFFF;

	public bool Success { get; private set; }
	public string FailReason { get; private set; } = "";

	public int ProcessId { get; private set; }
	public int Index { get; private set; }

	public IntPtr TableBase { get; private set; }
	public IntPtr EntityBase { get; private set; }

	public int Handle { get; private set; }
	public int SlotIndex { get; private set; }
	public int MirrorIndex { get; private set; }

	public int ClassPointer { get; private set; }
	public int PrimaryPointer { get; private set; }
	public int SecondaryPointer { get; private set; }

	public int ActiveFlag { get; private set; }
	public int Level { get; private set; }

	public int Hp { get; private set; }
	public int MaxHp { get; private set; }
	public int Mp { get; private set; }
	public int MaxMp { get; private set; }

	public int MoveTargetRawXCandidate { get; private set; }
	public int MoveTargetRawYCandidate { get; private set; }

	public int AnchorRawXCandidate { get; private set; }
	public int AnchorRawYCandidate { get; private set; }

	public int RawX { get; private set; }
	public int RawY { get; private set; }

	public int RawXMirror { get; private set; }
	public int RawYMirror { get; private set; }

	public bool RecordMatchesIndex {
		get {
			return SlotIndex == Index && MirrorIndex == Index;
		}
	}

	public bool PositionMirrorMatches {
		get {
			return RawX == RawXMirror && RawY == RawYMirror;
		}
	}

	public bool HasValidPosition {
		get {
			return
				Success &&
				RawX > 0 &&
				RawY > 0 &&
				PositionMirrorMatches;
		}
	}

	public bool IsAlive {
		get {
			return Success && Hp > 0 && MaxHp > 0;
		}
	}

	public bool IsUsableRuntimeEntity {
		get {
			return
				Success &&
				RecordMatchesIndex &&
				Handle > 0 &&
				ActiveFlag == 1 &&
				Level > 0 &&
				HasValidPosition;
		}
	}

	public bool IsUsableCommandEntity {
		get {
			return
				Success &&
				EntityBase != IntPtr.Zero &&
				RecordMatchesIndex &&
				Handle > 0 &&
				ActiveFlag == 1 &&
				Level > 0;
		}
	}

	public int GameCoordinateX {
		get {
			return RawX / RawXScale;
		}
	}

	public int GameCoordinateY {
		get {
			return RawY / RawYScale;
		}
	}

	public static EntitySnapshot ReadCurrentTarget(int processId) {
		try {
			using MemoryReader reader = new MemoryReader(processId);
			IntPtr moduleBase = reader.GetModuleBase("Game.exe");

			if (moduleBase == IntPtr.Zero) {
				return CreateFail(processId, -1, "Không tìm thấy module Game.exe.");
			}

			IntPtr targetIndexAddress = IntPtr.Add(moduleBase, CurrentTargetIndexOffset);
			int targetIndex = reader.ReadInt32(targetIndexAddress);
			EntitySnapshot uiTarget = ReadInternal(reader, processId, moduleBase, targetIndex);
			if (uiTarget.IsUsableCommandEntity) return uiTarget;

			IntPtr tableBase = reader.ReadPointer32(IntPtr.Add(moduleBase, GlobalEntityTableOffset));
			if (! IsLikelyAddress(tableBase)) return uiTarget;
			IntPtr playerEntity = IntPtr.Add(tableBase, GameAddresses.Entity.PlayerIndex * EntityStride);
			int commandActive = reader.ReadInt32(IntPtr.Add(playerEntity, GameAddresses.Attack.CommandActive));
			if (commandActive != 1) return uiTarget;
			int actionTargetIndex = reader.ReadInt32(IntPtr.Add(playerEntity, GameAddresses.Attack.TargetIndexCommand));
			return ReadInternal(reader, processId, moduleBase, actionTargetIndex);
		} catch (Exception ex) {
			return CreateFail(processId, -1, ex.Message);
		}
	}

	public static EntitySnapshot Read(int processId, int index) {
		try {
			using MemoryReader reader = new MemoryReader(processId);
			IntPtr moduleBase = reader.GetModuleBase("Game.exe");

			if (moduleBase == IntPtr.Zero) {
				return CreateFail(processId, index, "Không tìm thấy module Game.exe.");
			}

			return ReadInternal(reader, processId, moduleBase, index);
		} catch (Exception ex) {
			return CreateFail(processId, index, ex.Message);
		}
	}

	public static string CaptureTargetLifecycle(int processId, int trackedIndex, int trackedHandle, CombatSnapshot combat) {
		return InspectTargetLifecycle(processId, trackedIndex, trackedHandle, combat).Evidence;
	}

	public static TargetLifecycleInspection InspectTargetLifecycle(int processId, int trackedIndex, int trackedHandle, CombatSnapshot combat) {
		try {
			using MemoryReader reader = new MemoryReader(processId);
			IntPtr moduleBase = reader.GetModuleBase("Game.exe");
			if (moduleBase == IntPtr.Zero) {
				string evidence = $"TARGET_LIFECYCLE_CAPTURE_FAIL | Tracked={trackedIndex}/{trackedHandle} | Reason=MODULE_NOT_FOUND";
				return new TargetLifecycleInspection(TargetLifecycleState.Unknown, null, evidence);
			}

			int currentTargetIndex = reader.ReadInt32(IntPtr.Add(moduleBase, CurrentTargetIndexOffset));
			IntPtr tableBase = new IntPtr(reader.ReadInt32(IntPtr.Add(moduleBase, GlobalEntityTableOffset)));
			if (!IsLikelyAddress(tableBase)) {
				string evidence = $"TARGET_LIFECYCLE_CAPTURE_FAIL | Tracked={trackedIndex}/{trackedHandle} | CurrentTargetIndex={currentTargetIndex} | TableBase={FormatAddress(tableBase)} | Reason=INVALID_TABLE";
				return new TargetLifecycleInspection(TargetLifecycleState.Unknown, null, evidence);
			}

			if (trackedIndex < 0 || trackedIndex >= 128 || trackedHandle <= 0) {
				string evidence = $"TARGET_LIFECYCLE_CAPTURE_FAIL | Tracked={trackedIndex}/{trackedHandle} | CurrentTargetIndex={currentTargetIndex} | Reason=INVALID_IDENTITY";
				return new TargetLifecycleInspection(TargetLifecycleState.Unknown, null, evidence);
			}

			EntitySnapshot trackedEntity = ReadInternal(reader, processId, moduleBase, trackedIndex);
			TargetLifecycleState state = ClassifyExactIdentity(trackedEntity.Success, trackedEntity.Handle, trackedEntity.ActiveFlag, trackedHandle);
			bool exactIdentityActive = state == TargetLifecycleState.Active;
			EntitySnapshot? activeEntity = exactIdentityActive ? trackedEntity : null;
			string evidenceText = $"TARGET_LIFECYCLE | State={state} | Identity=EXACT_INDEX_HANDLE | Tracked={trackedIndex}/{trackedHandle} | CurrentTargetIndex={currentTargetIndex} | Combat={combat.Success}/{combat.CurrentHp}/{combat.MaxHpCandidate} | TableBase={FormatAddress(tableBase)} | Record={FormatLifecycleRecord(trackedEntity)}";
			return new TargetLifecycleInspection(state, activeEntity, evidenceText);
		} catch (Exception ex) {
			string evidence = $"TARGET_LIFECYCLE_CAPTURE_FAIL | Tracked={trackedIndex}/{trackedHandle} | Reason={ex.GetType().Name}:{ex.Message}";
			return new TargetLifecycleInspection(TargetLifecycleState.Unknown, null, evidence);
		}
	}

	internal static TargetLifecycleState ClassifyExactIdentity(bool readSuccess, int actualHandle, int activeFlag, int trackedHandle) {
		if (!readSuccess) return TargetLifecycleState.Unknown;
		return actualHandle == trackedHandle && activeFlag == 1 ? TargetLifecycleState.Active : TargetLifecycleState.Inactive;
	}

	public static string DebugCurrentTarget(int processId) {
		EntitySnapshot entity = ReadCurrentTarget(processId);
		StringBuilder sb = new StringBuilder();

		sb.AppendLine("===== Entity Snapshot =====");
		sb.AppendLine($"Success = {entity.Success}");
		sb.AppendLine($"ProcessId = {entity.ProcessId}");

		if (!entity.Success) {
			sb.AppendLine($"FailReason = {entity.FailReason}");
			return sb.ToString();
		}

		sb.AppendLine($"Index = {entity.Index}");
		sb.AppendLine($"TableBase = {FormatAddress(entity.TableBase)}");
		sb.AppendLine($"EntityBase = {FormatAddress(entity.EntityBase)}");
		sb.AppendLine();

		sb.AppendLine($"Handle = {entity.Handle} / 0x{FormatDword(entity.Handle)}");
		sb.AppendLine($"SlotIndex = {entity.SlotIndex}");
		sb.AppendLine($"MirrorIndex = {entity.MirrorIndex}");
		sb.AppendLine($"RecordMatchesIndex = {entity.RecordMatchesIndex}");
		sb.AppendLine($"ActiveFlag = {entity.ActiveFlag}");
		sb.AppendLine($"Level = {entity.Level}");
		sb.AppendLine();

		sb.AppendLine($"HP = {entity.Hp}/{entity.MaxHp}");
		sb.AppendLine($"MP = {entity.Mp}/{entity.MaxMp}");
		sb.AppendLine($"IsAlive = {entity.IsAlive}");
		sb.AppendLine();

		sb.AppendLine($"MoveTargetRawCandidate = {entity.MoveTargetRawXCandidate}/{entity.MoveTargetRawYCandidate}");
		sb.AppendLine($"AnchorRawCandidate = {entity.AnchorRawXCandidate}/{entity.AnchorRawYCandidate}");
		sb.AppendLine();

		sb.AppendLine($"RawPosition = {entity.RawX}/{entity.RawY}");
		sb.AppendLine($"RawPositionMirror = {entity.RawXMirror}/{entity.RawYMirror}");
		sb.AppendLine($"PositionMirrorMatches = {entity.PositionMirrorMatches}");
		sb.AppendLine($"HasValidPosition = {entity.HasValidPosition}");
		sb.AppendLine($"GameCoordinateCandidate = {entity.GameCoordinateX}/{entity.GameCoordinateY}");
		sb.AppendLine();

		sb.AppendLine($"IsUsableRuntimeEntity = {entity.IsUsableRuntimeEntity}");

		return sb.ToString();
	}

	public long GetNormalizedDistanceSquaredTo(int playerRawX, int playerRawY) {
		long deltaX = (long)RawX - playerRawX;
		long deltaY = (long)RawY - playerRawY;

		long normalizedDeltaX = deltaX * 2L;
		long normalizedDeltaY = deltaY;

		return normalizedDeltaX * normalizedDeltaX + normalizedDeltaY * normalizedDeltaY;
	}

	public double GetMapDistanceTo(int playerRawX, int playerRawY) {
		double normalizedDistance = Math.Sqrt(GetNormalizedDistanceSquaredTo(playerRawX, playerRawY));
		return normalizedDistance / RawYScale;
	}

	private static EntitySnapshot ReadInternal(
		MemoryReader reader,
		int processId,
		IntPtr moduleBase,
		int index
	) {
		if (index < 0 || index >= 128) {
			return CreateFail(processId, index, $"Index không hợp lệ: {index}");
		}

		IntPtr tableBaseAddress = IntPtr.Add(moduleBase, GlobalEntityTableOffset);
		int tableBaseRaw = reader.ReadInt32(tableBaseAddress);
		IntPtr tableBase = new IntPtr(tableBaseRaw);

		if (!IsLikelyAddress(tableBase)) {
			return CreateFail(
				processId,
				index,
				$"TableBase không hợp lệ: {FormatAddress(tableBase)}"
			);
		}

		if (!TryCalculateEntityBase(tableBase, index, out IntPtr entityBase)) {
			return CreateFail(
				processId,
				index,
				$"Không thể tính EntityBase. TableBase={FormatAddress(tableBase)} Index={index}"
			);
		}

		return new EntitySnapshot {
			Success = true,
			ProcessId = processId,
			Index = index,
			TableBase = tableBase,
			EntityBase = entityBase,

			Handle = reader.ReadInt32(IntPtr.Add(entityBase, HandleOffset)),
			SlotIndex = reader.ReadInt32(IntPtr.Add(entityBase, SlotIndexOffset)),
			MirrorIndex = reader.ReadInt32(IntPtr.Add(entityBase, MirrorIndexOffset)),

			ClassPointer = reader.ReadInt32(IntPtr.Add(entityBase, ClassPointerOffset)),
			PrimaryPointer = reader.ReadInt32(IntPtr.Add(entityBase, PrimaryPointerOffset)),
			SecondaryPointer = reader.ReadInt32(IntPtr.Add(entityBase, SecondaryPointerOffset)),

			ActiveFlag = reader.ReadInt32(IntPtr.Add(entityBase, ActiveFlagOffset)),
			Level = reader.ReadInt32(IntPtr.Add(entityBase, LevelOffset)),

			Hp = reader.ReadInt32(IntPtr.Add(entityBase, HpOffset)),
			MaxHp = reader.ReadInt32(IntPtr.Add(entityBase, MaxHpOffset)),
			Mp = reader.ReadInt32(IntPtr.Add(entityBase, MpOffset)),
			MaxMp = reader.ReadInt32(IntPtr.Add(entityBase, MaxMpOffset)),

			MoveTargetRawXCandidate = reader.ReadInt32(
				IntPtr.Add(entityBase, MoveTargetXCandidateOffset)
			),
			MoveTargetRawYCandidate = reader.ReadInt32(
				IntPtr.Add(entityBase, MoveTargetYCandidateOffset)
			),

			AnchorRawXCandidate = reader.ReadInt32(
				IntPtr.Add(entityBase, AnchorXCandidateOffset)
			),
			AnchorRawYCandidate = reader.ReadInt32(
				IntPtr.Add(entityBase, AnchorYCandidateOffset)
			),

			RawX = reader.ReadInt32(IntPtr.Add(entityBase, RawXOffset)),
			RawY = reader.ReadInt32(IntPtr.Add(entityBase, RawYOffset)),

			RawXMirror = reader.ReadInt32(
				IntPtr.Add(entityBase, RawXMirrorOffset)
			),
			RawYMirror = reader.ReadInt32(
				IntPtr.Add(entityBase, RawYMirrorOffset)
			)
		};
	}

	private static bool TryCalculateEntityBase(
		IntPtr tableBase,
		int index,
		out IntPtr entityBase
	) {
		long tableBaseValue = unchecked((uint)tableBase.ToInt64());
		long entityOffset = (long)index * EntityStride;
		long entityBaseValue = tableBaseValue + entityOffset;

		if (entityBaseValue < MinimumLikelyAddress || entityBaseValue > MaximumUserModeAddress) {
			entityBase = IntPtr.Zero;
			return false;
		}

		entityBase = new IntPtr((int)entityBaseValue);
		return true;
	}

	private static bool IsLikelyAddress(IntPtr address) {
		long value = unchecked((uint)address.ToInt64());

		return value >= MinimumLikelyAddress && value <= MaximumUserModeAddress;
	}

	private static EntitySnapshot CreateFail(int processId, int index, string reason) {
		return new EntitySnapshot {
			Success = false,
			ProcessId = processId,
			Index = index,
			FailReason = reason
		};
	}

	private static string FormatAddress(IntPtr address) {
		return unchecked((uint)address.ToInt64()).ToString("X8");
	}

	private static string FormatDword(int value) {
		return unchecked((uint)value).ToString("X8");
	}

	private static string FormatLifecycleRecord(EntitySnapshot entity) {
		if (!entity.Success) return $"Index={entity.Index}/FAIL:{entity.FailReason}";
		return $"Index={entity.Index};Base={FormatAddress(entity.EntityBase)};Handle={entity.Handle};Slot={entity.SlotIndex};Mirror={entity.MirrorIndex};Active={entity.ActiveFlag};Level={entity.Level};HP={entity.Hp}/{entity.MaxHp};Class=0x{FormatDword(entity.ClassPointer)};Primary=0x{FormatDword(entity.PrimaryPointer)};Secondary=0x{FormatDword(entity.SecondaryPointer)};Raw={entity.RawX}/{entity.RawY};RawMirror={entity.RawXMirror}/{entity.RawYMirror};Usable={entity.IsUsableCommandEntity};Alive={entity.IsAlive}";
	}
}
