namespace Auto.DebugTools;

using System.Text;
using Auto.Runtime;
using Auto.Utils;

// Đọc thẳng từng địa chỉ đăng nhập của AutoFS trên client hiện tại và IN RA GIÁ TRỊ THẬT, không trả mã pass/fail.
//
// Vì sao có: audit 2026-09-12 PID=11704 chỉ trả "FAIL_UNREADABLE code 13" cho LoginNoticeDialogRva và
// LoginServerDialogRva. Mã đó gộp hai trường hợp rất khác nhau — ô không đọc được, và ô đọc được nhưng giá trị
// không trỏ vào đâu — nên không đủ để kết luận địa chỉ sai. Probe này in nguyên giá trị và từng mắt xích để
// biết chính xác chuỗi gãy ở đâu.
//
// CHỈ ĐỌC bộ nhớ, không gửi lệnh nào vào client.
internal static class LoginAutoFsAddressProbe {
	private const string BuildStamp = "LOGIN-AUTOFS-ADDRESS-20260912-01";
	private const int FrameOffset = 0x54;
	private const int ReceiverOffset = 0x58;
	private const int ServerListOffset = 0x970;
	private const int EnterButtonOffset = 0x106C;

	// Nguyên văn các địa chỉ AutoFS dùng, ở dạng VA tuyệt đối như trong DLL của nó.
	private static readonly (string Name, long Va, string Source)[] DialogGlobals = [
		("Khuyến cáo (lệnh 280)", 0x754D5C, "SystemUint.dll AutoFS, handler 0x100039E2: mov esi, ds:[0x754D5C]"),
		("Thông tin phiên bản (lệnh 281)", 0x754C50, "handler 0x10003A1D: mov esi, ds:[0x754C50]"),
		("Chọn máy chủ (lệnh 282)", 0x754DD4, "handler 0x10003A58: mov ecx, ds:[0x754DD4]")
	];

	private static readonly (string Name, long Va, string Source)[] StateGlobals = [
		("Trạng thái đăng nhập", 0x774FA8, "DiskConverter.cs:859 — chờ ==2 rồi gõ, ==5 là xong"),
		("Cổng client sẵn sàng", 0x754A9C, "DiskConverter.cs:869 — đọc con trỏ rồi +84 rồi deref, khác 0 là sẵn sàng"),
		("Ngữ cảnh lệnh 284", 0x774140, "handler 0x10003B63: mov ecx, 0x774140")
	];

	public static string Run(GameWindow game) {
		StringBuilder output = new();
		output.AppendLine($"{BuildStamp} | PID={game.ProcessId} | Mode=CHỈ_ĐỌC");
		try {
			using MemoryReader reader = new(game.ProcessId);
			IntPtr moduleBase = reader.GetModuleBase("Game.exe");
			long imageBase = moduleBase.ToInt64();
			output.AppendLine($"ImageBase=0x{imageBase:X8}");
			if (imageBase != 0x400000) {
				output.AppendLine("CẢNH BÁO | ImageBase khác 0x400000, mọi VA tuyệt đối của AutoFS đều lệch theo.");
			}
			output.AppendLine();

			output.AppendLine("===== Ba con trỏ hộp thoại =====");
			foreach ((string name, long va, string source) in DialogGlobals) {
				output.AppendLine($"--- {name} | VA=0x{va:X8} ---");
				output.AppendLine($"    Nguồn: {source}");
				long dialog = ReadDword(reader, va, out bool slotOk);
				if (! slotOk) {
					output.AppendLine("    Ô KHÔNG ĐỌC ĐƯỢC.");
					continue;
				}
				output.AppendLine($"    Giá trị trong ô = 0x{dialog:X8}");
				if (dialog == 0) {
					output.AppendLine("    Bằng 0 — hộp thoại chưa dựng, hoặc đã đóng, hoặc sai ô.");
					continue;
				}
				output.AppendLine($"    32 byte đầu của đối tượng: {Dump(reader, dialog, 32)}");
				DumpChain(output, reader, imageBase, dialog, FrameOffset, "+0x54 khung");
				DumpChain(output, reader, imageBase, dialog, ServerListOffset, "+0x970 danh sách máy chủ");
				DumpChain(output, reader, imageBase, dialog, EnterButtonOffset, "+0x106C nút Vào trò chơi");
			}

			output.AppendLine();
			output.AppendLine("===== Ba ô trạng thái =====");
			foreach ((string name, long va, string source) in StateGlobals) {
				long value = ReadDword(reader, va, out bool ok);
				output.AppendLine($"{name,-24} | VA=0x{va:X8} | {(ok ? $"Giá trị=0x{value:X8} ({value})" : "KHÔNG ĐỌC ĐƯỢC")}");
				output.AppendLine($"    Nguồn: {source}");
			}

			long gate = ReadDword(reader, 0x754A9C, out bool gateOk);
			if (gateOk && gate != 0) {
				long gateLevelTwo = ReadDword(reader, gate + 84, out bool levelTwoOk);
				output.AppendLine($"Cổng sẵn sàng | [0x754A9C]=0x{gate:X8} | +84 => {(levelTwoOk ? $"0x{gateLevelTwo:X8}" : "KHÔNG ĐỌC ĐƯỢC")}");
				if (levelTwoOk && gateLevelTwo != 0) {
					long gateValue = ReadDword(reader, gateLevelTwo, out bool valueOk);
					output.AppendLine($"              | deref => {(valueOk ? $"0x{gateValue:X8} => {(gateValue != 0 ? "SẴN SÀNG" : "chưa sẵn sàng")}" : "KHÔNG ĐỌC ĐƯỢC")}");
				}
			}
		} catch (Exception ex) {
			output.AppendLine($"Lỗi: {ex.GetType().Name}: {ex.Message}");
		}
		return output.ToString();
	}

	private static void DumpChain(StringBuilder output, MemoryReader reader, long imageBase, long dialog, int controlOffset, string label) {
		long control = ReadDword(reader, dialog + controlOffset, out bool controlOk);
		if (! controlOk) {
			output.AppendLine($"    {label}: KHÔNG ĐỌC ĐƯỢC");
			return;
		}
		if (control == 0) {
			output.AppendLine($"    {label}: 0");
			return;
		}
		long receiver = ReadDword(reader, control + ReceiverOffset, out bool receiverOk);
		if (! receiverOk || receiver == 0) {
			output.AppendLine($"    {label}: control=0x{control:X8} | +0x58 = {(receiverOk ? "0" : "KHÔNG ĐỌC ĐƯỢC")}");
			return;
		}
		long vtable = ReadDword(reader, receiver, out bool vtableOk);
		if (! vtableOk) {
			output.AppendLine($"    {label}: control=0x{control:X8} | receiver=0x{receiver:X8} | vtable KHÔNG ĐỌC ĐƯỢC");
			return;
		}
		string inModule = vtable >= imageBase && vtable < imageBase + 0x1F83000 ? $"RVA=0x{vtable - imageBase:X8} TRONG Game.exe" : "NGOÀI Game.exe";
		long method = ReadDword(reader, vtable + 0x10, out bool methodOk);
		output.AppendLine($"    {label}: control=0x{control:X8} | receiver=0x{receiver:X8} | vtable=0x{vtable:X8} ({inModule}) | vtable[+0x10]={(methodOk ? $"0x{method:X8}" : "KHÔNG ĐỌC ĐƯỢC")}");
	}

	private static long ReadDword(MemoryReader reader, long address, out bool success) {
		byte[] bytes = reader.ReadMemory((IntPtr)address, 4);
		success = bytes.Length == 4;
		return success ? (uint)BitConverter.ToInt32(bytes, 0) : 0;
	}

	private static string Dump(MemoryReader reader, long address, int length) {
		byte[] bytes = reader.ReadMemory((IntPtr)address, length);
		return bytes.Length == 0 ? "KHÔNG ĐỌC ĐƯỢC" : BitConverter.ToString(bytes).Replace('-', ' ');
	}
}
