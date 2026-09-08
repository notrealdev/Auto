namespace Auto.DebugTools;

using System.Diagnostics;
using System.Text;
using Auto.Utils;

// Chụp modal đang mở để khôi phục ba hằng số mà bộ kiểm địa chỉ báo lệch sau bản game 2026-08-28:
// ReturnToTownObjectRva, ReturnToTownObjectVtableRva và NpcConfirmModalVtableRva.
//
// Cơ sở: handler lệnh 38 gốc của AutoFS (AUTOFS-SYSTEMUINT-DISPATCHER-MANIFEST.log dòng 695) nạp this bằng một địa chỉ
// tĩnh rồi gọi thẳng một hàm tĩnh, tức object Về thành nằm cố định trong ảnh và hàm là method của chính class đó.
// Vì vậy khi popup đang mở, con trỏ modal chính là object cần tìm, và hàm cần tìm nằm trong bảng hàm ảo của nó.
//
// Ứng viên hàm Về thành được xác định bằng giao của hai tập độc lập, không suy ra từ độ dịch RVA:
// tập ô trong bảng hàm ảo của object, và tập vị trí trong ảnh khớp chữ ký hàm Về thành cũ.
// Chỉ đọc bộ nhớ, không gọi hàm nào của game.
public static class ModalVtableProbe {
	// Chữ ký hàm Về thành đang dùng trong Native\SystemUint\SystemUint.cpp (ReturnToTownFunctionSignature).
	private static readonly byte[] ReturnToTownFunctionSignature = [0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x0C, 0x89, 0x4D, 0xFC];
	private const int VtableSlotCount = 64;
	private const int FunctionPreviewBytes = 12;
	// Các hằng số hiện hành để đối chiếu ngay trong báo cáo, lấy từ GameClientAddresses.h.
	private const int CurrentReturnToTownObjectRva = 0x004FCD38;
	private const int CurrentReturnToTownObjectVtableRva = 0x004685D8;
	private const int CurrentReturnToTownFunctionRva = 0x001A74E0;
	private const int CurrentNpcConfirmModalVtableRva = 0x00469E34;
	private const int CurrentRepairConfirmModalVtableRva = 0x004732BC;

	public static string Run(int processId) {
		StringBuilder output = new();
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			if (moduleBase == IntPtr.Zero) return "MODAL_VTABLE_FAIL | Reason=MODULE_NOT_FOUND";
			int moduleSize = GetModuleSize(processId);
			if (moduleSize <= 0) return "MODAL_VTABLE_FAIL | Reason=MODULE_SIZE_UNAVAILABLE";

			IntPtr modal = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.ModalState));
			int shopState = reader.ReadInt32(IntPtr.Add(moduleBase, GameAddresses.Globals.ShopState));
			IntPtr dialog = reader.ReadPointer32(IntPtr.Add(moduleBase, GameAddresses.Globals.DialogPointer));
			output.AppendLine($"MODAL_VTABLE_START | PID={processId} | ModuleBase=0x{moduleBase.ToInt64():X8} | ModuleSize=0x{moduleSize:X} | ModalStateRva=0x{GameAddresses.Globals.ModalState:X} | Modal=0x{modal.ToInt64():X8} | ShopState={shopState} | DialogPointer=0x{dialog.ToInt64():X8}");
			if (modal == IntPtr.Zero) return output + "MODAL_VTABLE_FAIL | Reason=NO_MODAL_OPEN | Action=Mở popup rồi chạy lại; không đóng popup trong lúc chạy.";

			long modalRva = modal.ToInt64() - moduleBase.ToInt64();
			bool modalInModule = modalRva >= 0 && modalRva < moduleSize;
			output.AppendLine($"MODAL_VTABLE_OBJECT | InModule={modalInModule} | ObjectRva=0x{modalRva:X} | CurrentReturnToTownObjectRva=0x{CurrentReturnToTownObjectRva:X} | Matches={(modalInModule && modalRva == CurrentReturnToTownObjectRva)}");

			IntPtr vtable = reader.ReadPointer32(modal);
			if (vtable == IntPtr.Zero) return output + "MODAL_VTABLE_FAIL | Reason=VTABLE_NULL";
			long vtableRva = vtable.ToInt64() - moduleBase.ToInt64();
			string vtableLabel = vtableRva == CurrentReturnToTownObjectVtableRva ? "RETURN_TO_TOWN"
				: vtableRva == CurrentNpcConfirmModalVtableRva ? "NPC_CONFIRM"
				: vtableRva == CurrentRepairConfirmModalVtableRva ? "REPAIR_CONFIRM"
				: "UNKNOWN";
			output.AppendLine($"MODAL_VTABLE_TABLE | VtableRva=0x{vtableRva:X} | MatchesKnown={vtableLabel} | ReturnToTown=0x{CurrentReturnToTownObjectVtableRva:X} | NpcConfirm=0x{CurrentNpcConfirmModalVtableRva:X} | RepairConfirm=0x{CurrentRepairConfirmModalVtableRva:X}");

			byte[] image = reader.ReadBytes(moduleBase, moduleSize);
			if (image.Length != moduleSize) output.AppendLine($"MODAL_VTABLE_IMAGE_PARTIAL | Read={image.Length}/{moduleSize} | Note=Phần quét chữ ký chỉ phủ tới đây");
			List<long> signatureMatches = FindSignature(image, ReturnToTownFunctionSignature);
			output.AppendLine($"MODAL_VTABLE_SIGNATURE_SCAN | Pattern=55 8B EC 83 EC 0C 89 4D FC | MatchesInImage={signatureMatches.Count} | CurrentReturnToTownFunctionRva=0x{CurrentReturnToTownFunctionRva:X} | CurrentStillMatches={signatureMatches.Contains(CurrentReturnToTownFunctionRva)}");

			HashSet<long> signatureSet = [.. signatureMatches];
			List<long> intersection = [];
			ReportSlots(reader, moduleBase, moduleSize, vtable, image, signatureSet, intersection, output);
			output.AppendLine($"MODAL_VTABLE_INTERSECTION | Count={intersection.Count} | Rvas=[{string.Join(",", intersection.Select(rva => $"0x{rva:X}"))}] | Note=Ô vừa nằm trong bảng hàm ảo vừa khớp chữ ký hàm Về thành cũ");
			return output.ToString();
		} catch (Exception ex) {
			return output + $"MODAL_VTABLE_FAIL | {ex.GetType().Name}: {ex.Message}";
		}
	}

	// In từng ô của bảng hàm ảo kèm byte mở đầu để đối chiếu thủ công khi không ô nào khớp chữ ký.
	private static void ReportSlots(MemoryReader reader, IntPtr moduleBase, int moduleSize, IntPtr vtable, byte[] image, HashSet<long> signatureSet, List<long> intersection, StringBuilder output) {
		for (int slot = 0; slot < VtableSlotCount; slot++) {
			int slotOffset = slot * sizeof(int);
			IntPtr method = reader.ReadPointer32(IntPtr.Add(vtable, slotOffset));
			if (method == IntPtr.Zero) {
				output.AppendLine($"MODAL_VTABLE_SLOT | Slot=+0x{slotOffset:X} | Method=NULL");
				continue;
			}
			long methodRva = method.ToInt64() - moduleBase.ToInt64();
			if (methodRva < 0 || methodRva >= moduleSize) {
				output.AppendLine($"MODAL_VTABLE_SLOT | Slot=+0x{slotOffset:X} | Method=0x{method.ToInt64():X8} | Verdict=OUTSIDE_MODULE");
				continue;
			}
			bool matchesSignature = signatureSet.Contains(methodRva);
			if (matchesSignature) intersection.Add(methodRva);
			string preview = methodRva + FunctionPreviewBytes <= image.Length
				? Convert.ToHexString(image, (int)methodRva, FunctionPreviewBytes)
				: "";
			output.AppendLine($"MODAL_VTABLE_SLOT | Slot=+0x{slotOffset:X} | MethodRva=0x{methodRva:X} | Bytes={preview} | MatchesReturnToTownSignature={matchesSignature}");
		}
	}

	private static List<long> FindSignature(byte[] image, byte[] signature) {
		List<long> matches = [];
		for (int offset = 0; offset <= image.Length - signature.Length; offset++) {
			if (image.AsSpan(offset, signature.Length).SequenceEqual(signature)) matches.Add(offset);
		}
		return matches;
	}

	private static int GetModuleSize(int processId) {
		try {
			using Process process = Process.GetProcessById(processId);
			foreach (ProcessModule module in process.Modules) {
				if (string.Equals(module.ModuleName, GameAddresses.ModuleName, StringComparison.OrdinalIgnoreCase)) return module.ModuleMemorySize;
			}
			return 0;
		} catch {
			return 0;
		}
	}
}
