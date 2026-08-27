using System;
using System.Text;

namespace Auto.Utils;

public sealed class CombatSnapshot {
	private const int TargetRootPointerOffset = GameAddresses.Globals.CombatTargetRoot;

	private const int HpAt82COffset = GameAddresses.Combat.HpAt82C;
	private const int CurrentHpOffset = GameAddresses.Combat.CurrentHp;
	private const int MaxHpCandidateOffset = GameAddresses.Combat.MaximumHp;

	public bool Success { get; init; }
	public string ErrorMessage { get; init; } = string.Empty;

	public int ProcessId { get; init; }

	public IntPtr ModuleBase { get; init; }
	public IntPtr TargetRootPointerAddress { get; init; }
	public IntPtr TargetRootAddress { get; init; }

	public IntPtr HpAt82CAddress { get; init; }
	public IntPtr CurrentHpAddress { get; init; }
	public IntPtr MaxHpCandidateAddress { get; init; }

	public int HpAt82C { get; init; }
	public int CurrentHp { get; init; }
	public int MaxHpCandidate { get; init; }

	public bool HasTarget {
		get {
			return Success && TargetRootAddress != IntPtr.Zero;
		}
	}

	public bool IsAlive {
		get {
			return HasTarget && CurrentHp > 0;
		}
	}

	public bool IsDead {
		get {
			return HasTarget && CurrentHp == 0;
		}
	}

	public static CombatSnapshot Read(int processId) {
		try {
			using MemoryReader reader = new MemoryReader(processId);

			IntPtr moduleBase = reader.GetModuleBase("Game.exe");
			IntPtr targetRootPointerAddress = IntPtr.Add(moduleBase, TargetRootPointerOffset);
			IntPtr targetRootAddress = reader.ReadPointer32(targetRootPointerAddress);

			if (targetRootAddress == IntPtr.Zero) {
				return CreateFail(
					processId,
					moduleBase,
					targetRootPointerAddress,
					"Target root pointer is NULL."
				);
			}

			IntPtr hpAt82CAddress = IntPtr.Add(targetRootAddress, HpAt82COffset);
			IntPtr currentHpAddress = IntPtr.Add(targetRootAddress, CurrentHpOffset);
			IntPtr maxHpCandidateAddress = IntPtr.Add(targetRootAddress, MaxHpCandidateOffset);

			return new CombatSnapshot {
				Success = true,
				ProcessId = processId,

				ModuleBase = moduleBase,
				TargetRootPointerAddress = targetRootPointerAddress,
				TargetRootAddress = targetRootAddress,

				HpAt82CAddress = hpAt82CAddress,
				CurrentHpAddress = currentHpAddress,
				MaxHpCandidateAddress = maxHpCandidateAddress,

				HpAt82C = reader.ReadInt32(hpAt82CAddress),
				CurrentHp = reader.ReadInt32(currentHpAddress),
				MaxHpCandidate = reader.ReadInt32(maxHpCandidateAddress)
			};
		} catch (Exception ex) {
			return new CombatSnapshot {
				Success = false,
				ProcessId = processId,
				ErrorMessage = ex.ToString()
			};
		}
	}

	public static string Debug(int processId) {
		CombatSnapshot snapshot = Read(processId);
		return snapshot.ToDebugText();
	}

	public string ToDebugText() {
		var sb = new StringBuilder();

		sb.AppendLine("===== Combat Snapshot =====");
		sb.AppendLine($"Success = {Success}");
		sb.AppendLine($"ProcessId = {ProcessId}");

		if (!Success) {
			sb.AppendLine($"Error = {ErrorMessage}");
			return sb.ToString();
		}

		sb.AppendLine($"ModuleBase = {ModuleBase.ToInt32():X8}");
		sb.AppendLine($"TargetRootPointer = {TargetRootPointerAddress.ToInt32():X8}");
		sb.AppendLine($"TargetRoot = {TargetRootAddress.ToInt32():X8}");
		sb.AppendLine();

		sb.AppendLine($"HP +82C = {HpAt82C} | Address={HpAt82CAddress.ToInt32():X8}");
		sb.AppendLine($"CurrentHP +CE4 = {CurrentHp} | Address={CurrentHpAddress.ToInt32():X8}");
		sb.AppendLine($"MaxHPCandidate +CE8 = {MaxHpCandidate} | Address={MaxHpCandidateAddress.ToInt32():X8}");
		sb.AppendLine();

		if (IsAlive) {
			sb.AppendLine("TargetState = Alive");
		} else if (IsDead) {
			sb.AppendLine("TargetState = Dead");
		} else {
			sb.AppendLine("TargetState = Unknown");
		}

		return sb.ToString();
	}

	private static CombatSnapshot CreateFail(
		int processId,
		IntPtr moduleBase,
		IntPtr targetRootPointerAddress,
		string errorMessage
	) {
		return new CombatSnapshot {
			Success = false,
			ProcessId = processId,

			ModuleBase = moduleBase,
			TargetRootPointerAddress = targetRootPointerAddress,

			ErrorMessage = errorMessage
		};
	}
}
