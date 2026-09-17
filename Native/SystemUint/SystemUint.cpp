#include <windows.h>
#include <stdint.h>
#include "GameClientAddresses.h"

namespace {
	constexpr char WriteMessageName[] = "WM_HOOK_WRITE";
	constexpr char HookMessageName[] = "WM_HOOKEX_RK";
	constexpr WPARAM MovementXCommand = 0;
	constexpr WPARAM MovementYCommand = 5;
	constexpr WPARAM DialogOptionCommand = 7;
	constexpr WPARAM SelectGroundItemCommand = 9;
	constexpr WPARAM PortalCommand = 27;
	constexpr WPARAM ResetActionCommand = 32;
	constexpr WPARAM ReturnToTownCommand = 38;
	constexpr WPARAM SaleCommand = 47;
	constexpr WPARAM PickupCommand = 78;
	constexpr WPARAM CastSkillCommand = 85;
	constexpr WPARAM AttackCommand = 300;
	constexpr WPARAM UseInventoryItemCommand = 310;
	constexpr WPARAM BeginScriptCommand = 311;
	constexpr WPARAM SendChatCommand = 312;
	constexpr WPARAM AppendScriptByteCommand = 34;
	// AutoFS dùng cặp 310/311 cho gói skill bị động, nhưng DEV auto đã cấp hai số đó cho dùng vật phẩm và reset chat,
	// nên tính năng này lấy số mới 313 để không phá Hồi thành phù lẫn Tự Rao.
	constexpr WPARAM PassiveBuffCommand = 313;
	// Kiểm tra toàn bộ địa chỉ client trong một lượt. lParam = -1 trả về số mục, lParam = chỉ số trả mã kết quả của mục đó.
	// Chỉ đọc bộ nhớ và so chữ ký, không gọi hàm nào của game nên an toàn để chạy lúc khởi động.
	constexpr WPARAM AuditAddressCommand = 320;
	// Đi bộ tới toạ độ raw bằng đúng nguyên thủy mà luồng nhặt đồ dùng (PickupMovementFunction mode 3), thay vì
	// CoordinateOpcode 0x9F của lệnh 0/5/32 — 0x9F gắn cờ lên màn hình, mode 3 thì không (chủ dự án xác nhận
	// 2026-09-06: nhân vật đi tới chỗ đồ rơi lúc nhặt không hiện cờ).
	// Toạ độ X gửi trước qua MovementXCommand ở dạng RAW (không chia 32), rồi chốt bằng lệnh này với Y raw.
	constexpr WPARAM WalkToCommand = 321;
	// Dấu phiên bản của chính DLL này, trả về cho phía C# đọc.
	//
	// Lý do có: DLL được inject vào tiến trình game và SỐNG LÂU HƠN một phiên chạy Auto. Sau khi build lại
	// native, không có cách nào biết tiến trình game đang chạy bản cũ hay bản mới — mà đo trên bản cũ thì mọi
	// kết luận đều vô nghĩa. Bump số này MỖI LẦN sửa native.
	constexpr WPARAM NativeBuildStampCommand = 322;
	// Năm lệnh đăng nhập giữ NGUYÊN số hiệu của AutoFS vì chúng không đụng số nào đang dùng ở trên.
	constexpr WPARAM LoginNoticeCommand = 280;
	constexpr WPARAM LoginVersionCommand = 281;
	constexpr WPARAM LoginSelectServerCommand = 282;
	constexpr WPARAM LoginTypeCharacterCommand = 283;
	constexpr WPARAM LoginSubmitCommand = 284;
	// Chỉ dùng để DÒ: bấm nút xác nhận của hộp thoại mà con trỏ nằm ở RVA truyền trong lParam. Địa chỉ hộp thoại của
	// AutoFS sai trên client 1.28 (audit 2026-09-12 PID=11704 trả FAIL_UNREADABLE), nên phải thử từng ứng viên.
	constexpr WPARAM LoginProbeDialogCommand = 285;
	constexpr WPARAM LoginDiagnoseChainCommand = 286;
	constexpr WPARAM LoginScanOffsetCommand = 287;
	constexpr WPARAM LoginReadDialogFieldCommand = 288;
	constexpr WPARAM LoginScanDispatcherCommand = 289;
	constexpr WPARAM LoginScanEmbeddedControlCommand = 290;
	constexpr WPARAM LoginSimpleModalConfirmCommand = 291;
	constexpr WPARAM LoginSimpleModalConfirmVersionCommand = 292;
	constexpr WPARAM ProbeGlobalPointerCommand = 293;
	constexpr WPARAM DiagnoseSimpleModalCommand = 294;
	constexpr WPARAM SimpleModalConfirmAnyCommand = 295;
	constexpr WPARAM ReadAnyDialogFieldCommand = 296;
	constexpr WPARAM SetProbeDialogRvaCommand = 297;
	constexpr WPARAM ProbeDispatchOffsetCommand = 298;
	constexpr WPARAM ProbeListSelectOffsetCommand = 299;
	constexpr WPARAM GetDialogVtableRvaCommand = 301;
	constexpr WPARAM GetDialogDispatcherRvaCommand = 302;
	constexpr WPARAM ReadServerListCountCommand = 303;
	constexpr WPARAM ProbeServerListSelectCommand = 304;
	constexpr WPARAM ScanDialogByEventCommand = 306;
	constexpr WPARAM CheckDialogEventPatternCommand = 307;
	// Bấm nút "Bắt đầu trò chơi" trên màn đăng nhập cuối. Client tự đọc tài khoản/mật khẩu từ ô nhập của nó.
	constexpr WPARAM LoginStartButtonCommand = 308;
	// Bam vao o "Dong y Dieu khoan" (dao trang thai) va doc lai trang thai o do.
	constexpr WPARAM LoginToggleAgreeTermsCommand = 309;
	// 310..313 da co chu khac (UseInventoryItem/BeginScript/SendChat/PassiveBuff), khong dung lai.
	constexpr WPARAM LoginReadAgreeTermsCommand = 314;
	// Do chuoi khong gui qua wParam/lParam duoc, van dung lenh 283 de bom tung ky tu vao hai vung dem cua DLL,
	// roi lenh 315 moi ghi ca hai vung do vao hai o nhap tren giao dien. Lenh 316 xoa sach vung dem truoc khi bom.
	constexpr WPARAM LoginWriteCredentialFieldsCommand = 315;
	constexpr WPARAM LoginClearPendingCredentialsCommand = 316;
	// Doc tung byte cau thong bao dang hien o man dang nhap (lParam = chi so byte).
	constexpr WPARAM LoginReadStatusMessageByteCommand = 317;
	// CHƯA CÓ cách đọc "đang ở bước đăng nhập nào". Đã thử dựa vào ba ô con trỏ hộp thoại nhưng ĐO RA LÀ SAI:
	// lúc đang hiện hộp "Khuyến cáo" thì ô của hộp "Thông tin phiên bản" đã khác 0, và sau khi lệnh 282 chạy xong
	// (màn hình đã sang trang đăng nhập, có ảnh chụp) ô của hộp "Chọn máy chủ" vẫn khác 0. Ba ô này KHÔNG loại trừ
	// nhau nên không dùng làm chỉ báo bước được. Xem ghi chú ở Login/LoginAutomation.cs về hệ quả còn lại.
	constexpr int NativeBuildStamp = 99990003;
	constexpr int AuditEntryCount = 31;
	constexpr uint16_t AttackTargetType = 0x87;
	constexpr size_t MaximumScriptLength = 199;

	HMODULE moduleHandle = nullptr;
	HHOOK injectionHook = nullptr;
	HWND hookedWindow = nullptr;
	WNDPROC originalWindowProcedure = nullptr;
	UINT writeMessage = 0;
	UINT hookMessage = 0;
	bool retainedInGameProcess = false;
	int pendingMovementX = 0;
	char pendingScript[MaximumScriptLength + 1]{};
	size_t pendingScriptLength = 0;
	// Tài khoản và mật khẩu gom dần qua lệnh 283, giống hai vùng đệm 0x1008B678 / 0x1008B1B0 của DLL AutoFS.
	// Chi dung cho cac lenh chan doan 297-299: giu RVA dialog dang do de lParam con cho offset (32-bit khong du
	// cho ca RVA lan offset trong mot lan gui).
	uintptr_t probeDialogRva = 0;
	char pendingLoginUser[GameClientAddresses::LoginCredentialBufferSize]{};
	char pendingLoginPassword[GameClientAddresses::LoginCredentialBufferSize]{};

	using PrepareFunction = void(__thiscall*)(void*, int, void*, int);
	using DialogOptionFunction = int(__thiscall*)(void*, int, int, int);
	using ModalEventFunction = int(__thiscall*)(void*, int, void*, int);
	using AttackFunction = int(__thiscall*)(void*, int, uintptr_t);
	using SelectGroundItemFunction = int(__thiscall*)(void*, int);
	// Lệnh 284 của AutoFS: thiscall 4 đối số tự dọn stack, rồi ba hàm cdecl (tổng cộng add esp, 0x18).
	using LoginSubmitFunction = int(__thiscall*)(void*, const char*, const char*, int, int);
	using LoginCleanupFunction4 = void(__cdecl*)(int, int, int, int);
	using LoginCleanupFunction1 = void(__cdecl*)(int);
	using GroundCoordinateConverterFunction = void(__thiscall*)(void*, int*, int*);
	using ResetPickupFunction = void(__thiscall*)(void*);
	using PickupMovementFunction = void(__thiscall*)(void*, int, int, int, int);
	using CastSkillFunction = void(__cdecl*)(int, int, int);
	using PassiveBuffFunction = void(__thiscall*)(void*, int);
	using PickupFunction = void(__cdecl*)(int, int);
	using ReturnToTownFunction = void(__thiscall*)(void*, int);
	using ListSetSelectionFunction = int(__thiscall*)(void*, int);
	using SaleFunction = int(__thiscall*)(void*, int, void*, int);
	using CoordinateDispatcherFunction = int(__thiscall*)(void*, int, int, int);
	using GenericDispatchFunction = int(__thiscall*)(void*, int, void*, int);
	using QueryFunction = int(__thiscall*)(void*, int, uintptr_t, uintptr_t);
	using InventoryCoordinateFunction = void(__cdecl*)(int*, int*);
	using ChannelCodeFromIndexFunction = const char*(__cdecl*)(int);
	using ChannelTypeFromIndexFunction = int(__cdecl*)(int);
	using ChatGateFunction = int(__cdecl*)(const char*, int, const char*, int);
	using ChatPackFunction = int(__cdecl*)(char*, int, const char*, int);
	using ChatEncodeFunction = int(__cdecl*)(char*, int);
	using ChannelActivateFunction = void(__cdecl*)(int, int);
	using ChatSendFunction = void(__cdecl*)(int, const char*, int, int);
	constexpr uint8_t AttackWriterSignature[] = {
		0xC7, 0x81, 0x94, 0xD7, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
		0x89, 0xB9, 0x98, 0xD7, 0x00, 0x00,
		0xC6, 0x81, 0xA0, 0xD7, 0x00, 0x00, 0x01
	};
	// Sửa: byte thật tại RVA đã xác nhận (cả dump 2026-08-12 và 2026-08-28) có prologue 55 8B EC đứng trước, chữ ký cũ thiếu 3 byte này nên luôn safe-reject kể cả trên client trước bản cập nhật hôm nay.
	constexpr uint8_t ResetPickupFunctionSignature[] = {
		0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x08, 0x56, 0x8B, 0xF1
	};
	constexpr uint8_t GroundCoordinateConverterSignature[] = {
		0x55, 0x8B, 0xEC, 0xFF, 0x75, 0x0C, 0xFF, 0x75, 0x08, 0xFF, 0x71, 0x3C
	};
	constexpr uint8_t PickupMovementFunctionSignature[] = {
		0x55, 0x8B, 0xEC, 0x81, 0xEC, 0x4C, 0x04, 0x00, 0x00
	};
	constexpr uint8_t PickupFunctionSignature[] = {
		0x55, 0x8B, 0xEC, 0x8B, 0x0D
	};
	// 9 byte đầu chỉ là prologue chung nên không phân biệt được hàm nào; nay lấy trọn 0x27 byte đầu của
	// 0x001A7810, gồm cả hai CALL tương đối và lệnh cmp [ecx+0xC40], 0 đặc trưng. Đoạn này khớp đúng 1 lần
	// trong toàn bộ vùng ảnh Game.exe đã map của crash dump 2026-09-06.
	constexpr uint8_t ReturnToTownFunctionSignature[] = {
		0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x0C, 0x89, 0x4D, 0xFC,
		0x8B, 0x45, 0xFC, 0x50, 0xE8, 0xFE, 0xE6, 0xE9, 0xFF,
		0x83, 0xC4, 0x04, 0x8B, 0x4D, 0xFC, 0xE8, 0x43, 0x0B, 0xEA, 0xFF,
		0x8B, 0x4D, 0xFC, 0x83, 0xB9, 0x40, 0x0C, 0x00, 0x00, 0x00
	};
	// Chữ ký cũ chỉ có 3 byte prologue nên không phân biệt được hàm nào; việc xác thực thật nằm ở call site,
	// mà call site lại lệch sau mỗi lần client cập nhật. Nay dùng 28 byte cố định đầu hàm, đã kiểm là duy nhất.
	constexpr uint8_t InventoryCoordinateFunctionSignature[] = {
		0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x08,
		0xC7, 0x45, 0xFC, 0x00, 0x00, 0x00, 0x00,
		0xC7, 0x45, 0xF8, 0x00, 0x00, 0x00, 0x00,
		0x8D, 0x45, 0xF8, 0x50,
		0x8D, 0x4D, 0xFC, 0x51
	};
	// Đoạn dựng và gửi gói cast opcode 0xB5 độ dài 11, nằm ở CastSendSiteOffset trong hàm cast.
	constexpr uint8_t CastSendSiteSignature[] = {
		0xC6, 0x45, 0xF0, 0xB5,
		0x89, 0x45, 0xF7,
		0x85, 0xC9, 0x74, 0x15,
		0x8B, 0x01, 0x8D, 0x55, 0xEC, 0x52, 0x8D, 0x55, 0xF0,
		0xC7, 0x45, 0xEC, 0x0B, 0x00, 0x00, 0x00,
		0x52, 0x51, 0xFF, 0x50, 0x20
	};
	// Byte thật đọc từ dump 2026-08-28 đã phân tích bằng Ghidra cho từng hàm trong chuỗi gửi chat của client.
	constexpr uint8_t ChannelCodeFromIndexSignature[] = {
		0x55, 0x8B, 0xEC, 0x83, 0x3D, 0x74, 0x51, 0x8F, 0x00, 0x00, 0x74, 0x50
	};
	constexpr uint8_t ChannelTypeFromIndexSignature[] = {
		0x55, 0x8B, 0xEC, 0x83, 0x3D, 0x74, 0x51, 0x8F, 0x00, 0x00, 0x74, 0x2D
	};
	constexpr uint8_t ChatGateFunctionSignature[] = {
		0x55, 0x8B, 0xEC, 0x81, 0xEC, 0x08, 0x04, 0x00, 0x00
	};
	constexpr uint8_t ChatPackFunctionSignature[] = {
		0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x1C, 0x83, 0x7D, 0x08, 0x00
	};
	constexpr uint8_t ChatEncodeFunctionSignature[] = {
		0xFF, 0x25, 0x4C, 0x31, 0x85, 0x00
	};
	constexpr uint8_t ChannelActivateFunctionSignature[] = {
		0x55, 0x8B, 0xEC, 0x6A, 0xFF, 0x68, 0xAD, 0xAC, 0x82, 0x00
	};
	constexpr uint8_t ChatSendFunctionSignature[] = {
		0x55, 0x8B, 0xEC, 0x81, 0xEC, 0x74, 0x01, 0x00, 0x00
	};
	// Đoạn dựng và gửi gói opcode 0x72 độ dài 5 nằm ở PassiveBuffSendSiteOffset trong hàm skill bị động.
	// Bốn byte toán hạng global đứng ngay trước đoạn này nên không đưa vào chữ ký; phần dưới đây là mã cố định.
	constexpr uint8_t PassiveBuffSendSiteSignature[] = {
		0x89, 0x75, 0xF9,
		0xC6, 0x45, 0xF8, 0x72,
		0x5F, 0x5E,
		0x85, 0xC9, 0x74, 0x15,
		0x8B, 0x01, 0x8D, 0x55, 0x08, 0x52, 0x8D, 0x55, 0xF8,
		0xC7, 0x45, 0x08, 0x05, 0x00, 0x00, 0x00,
		0x52, 0x51, 0xFF, 0x50, 0x20
	};

	bool IsExecutableAddress(const void* address) {
		MEMORY_BASIC_INFORMATION information{};
		if (address == nullptr || VirtualQuery(address, &information, sizeof(information)) != sizeof(information)) {
			return false;
		}
		if (information.State != MEM_COMMIT || (information.Protect & (PAGE_GUARD | PAGE_NOACCESS)) != 0) {
			return false;
		}
		DWORD executable = information.Protect & 0xFF;
		return executable == PAGE_EXECUTE ||
			executable == PAGE_EXECUTE_READ ||
			executable == PAGE_EXECUTE_READWRITE ||
			executable == PAGE_EXECUTE_WRITECOPY;
	}


	// Địa chỉ có đọc được không, dùng cho các mục audit là con trỏ dữ liệu chứ không phải mã lệnh.
	bool IsReadableAddress(const void* address) {
		MEMORY_BASIC_INFORMATION information{};
		if (address == nullptr || VirtualQuery(address, &information, sizeof(information)) != sizeof(information)) {
			return false;
		}
		if (information.State != MEM_COMMIT || (information.Protect & (PAGE_GUARD | PAGE_NOACCESS)) != 0) {
			return false;
		}
		DWORD readable = information.Protect & 0xFF;
		return readable == PAGE_READONLY ||
			readable == PAGE_READWRITE ||
			readable == PAGE_WRITECOPY ||
			readable == PAGE_EXECUTE_READ ||
			readable == PAGE_EXECUTE_READWRITE ||
			readable == PAGE_EXECUTE_WRITECOPY;
	}

	bool HasSupportedGameImage(uint8_t* gameBase) {
		if (gameBase == nullptr) {
			return false;
		}
		auto dosHeader = reinterpret_cast<IMAGE_DOS_HEADER*>(gameBase);
		if (dosHeader->e_magic != IMAGE_DOS_SIGNATURE || dosHeader->e_lfanew <= 0) {
			return false;
		}
		auto ntHeaders = reinterpret_cast<IMAGE_NT_HEADERS32*>(gameBase + dosHeader->e_lfanew);
		return ntHeaders->Signature == IMAGE_NT_SIGNATURE &&
			ntHeaders->FileHeader.Machine == IMAGE_FILE_MACHINE_I386 &&
			ntHeaders->OptionalHeader.Magic == IMAGE_NT_OPTIONAL_HDR32_MAGIC &&
			ntHeaders->OptionalHeader.SizeOfImage >= GameClientAddresses::MinimumSupportedImageSize;
	}

	bool ContainsAttackWriterSignature(const uint8_t* function) {
		constexpr size_t ScanLength = 0x100;
		if (!IsExecutableAddress(function)) {
			return false;
		}
		for (size_t offset = 0; offset + sizeof(AttackWriterSignature) <= ScanLength; offset++) {
			if (memcmp(function + offset, AttackWriterSignature, sizeof(AttackWriterSignature)) == 0) {
				return true;
			}
		}
		return false;
	}

	bool TryDispatchAttack(uint16_t targetIndex, uint16_t targetType) {
		if (targetIndex <= 1 || targetType != AttackTargetType) {
			return false;
		}

		auto gameBase = reinterpret_cast<uint8_t*>(GetModuleHandleA("Game.exe"));
		if (!HasSupportedGameImage(gameBase)) {
			return false;
		}

		auto managerSlot = reinterpret_cast<void**>(gameBase + GameClientAddresses::AttackManagerRva);
		void* manager = *managerSlot;
		if (manager == nullptr) {
			return false;
		}

		auto vtable = *reinterpret_cast<uint8_t***>(manager);
		if (vtable == nullptr) {
			return false;
		}

		auto prepare = reinterpret_cast<PrepareFunction>(*reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(vtable) + GameClientAddresses::PrepareMethodVtableOffset));
		auto attack = reinterpret_cast<AttackFunction>(*reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(vtable) + GameClientAddresses::AttackMethodVtableOffset));
		if (!IsExecutableAddress(reinterpret_cast<void*>(prepare)) || !ContainsAttackWriterSignature(reinterpret_cast<uint8_t*>(attack))) {
			return false;
		}

		uint32_t buffer[12]{};
		prepare(manager, GameClientAddresses::AttackPrepareOpcode, buffer, 0);
		attack(manager, targetIndex, buffer[9]);
		return true;
	}

	bool TryGetGameContext(uint8_t*& gameBase, void*& manager) {
		gameBase = reinterpret_cast<uint8_t*>(GetModuleHandleA("Game.exe"));
		if (!HasSupportedGameImage(gameBase)) {
			return false;
		}
		manager = *reinterpret_cast<void**>(gameBase + GameClientAddresses::AttackManagerRva);
		return manager != nullptr;
	}

	bool TrySelectGroundItem(int itemIndex) {
		if (itemIndex < 1 || itemIndex > 127) {
			return false;
		}
		uint8_t* gameBase = nullptr;
		void* manager = nullptr;
		if (!TryGetGameContext(gameBase, manager)) {
			return false;
		}
		auto managerVtable = *reinterpret_cast<uint8_t***>(manager);
		if (managerVtable == nullptr || *reinterpret_cast<void**>(manager) != gameBase + GameClientAddresses::ExpectedManagerVtableRva) {
			return false;
		}
		auto selectGroundItem = reinterpret_cast<SelectGroundItemFunction>(
			*reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(managerVtable) + GameClientAddresses::SelectGroundItemMethodVtableOffset));
		if (!IsExecutableAddress(reinterpret_cast<void*>(selectGroundItem))) {
			return false;
		}
		selectGroundItem(manager, itemIndex);
		return true;
	}

	// Upper bound used to be 9 with no recorded reason, most likely copied from the 8-slot NPC menu layout.
	// The Di ngoai phu destination menu has 23 rows, so that bound silently rejected rows 10-22: the C# side
	// only checks that PostMessageA succeeded, so it reported success while nothing reached the client.
	// Measured 2026-09-10 on PID 22056 with TalismanTravelProbe: indices 0, 1, 7 and 8 teleported correctly,
	// indices 16, 18, 22 and 23 produced "KHONG doi map sau 15000ms" even after scrolling the menu to the bottom.
	// The new bound is still a sanity guard - the client function is called with a raw index and nothing here
	// knows how it validates - it is just wide enough for every menu observed so far.
	constexpr int MaximumDialogOptionIndex = 63;

	bool TrySelectDialogOption(int optionIndex) {
		if (optionIndex < -1 || optionIndex > MaximumDialogOptionIndex) {
			return false;
		}
		uint8_t* gameBase = nullptr;
		void* manager = nullptr;
		if (!TryGetGameContext(gameBase, manager)) {
			return false;
		}
		void* expectedVtable = gameBase + GameClientAddresses::ExpectedManagerVtableRva;
		auto managerVtable = *reinterpret_cast<uint8_t***>(manager);
		if (managerVtable == nullptr || *reinterpret_cast<void**>(manager) != expectedVtable) {
			return false;
		}
		auto selectDialogOption = reinterpret_cast<DialogOptionFunction>(
			*reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(managerVtable) + GameClientAddresses::DialogOptionMethodVtableOffset));
		if (!IsExecutableAddress(reinterpret_cast<void*>(selectDialogOption))) {
			return false;
		}
		if (optionIndex >= 0) {
			selectDialogOption(manager, GameClientAddresses::DialogOptionOpcode, 0, optionIndex);
			return true;
		}
		void* modal = *reinterpret_cast<void**>(gameBase + GameClientAddresses::ModalStateRva);
		if (modal == nullptr) {
			selectDialogOption(manager, GameClientAddresses::DialogOptionOpcode, 0, optionIndex);
			return true;
		}
		auto modalVtable = *reinterpret_cast<uint8_t***>(modal);
		if (modalVtable == nullptr) {
			return false;
		}
		uintptr_t confirmControlOffset = 0;
		void* modalVtableAddress = *reinterpret_cast<void**>(modal);
		if (modalVtableAddress == gameBase + GameClientAddresses::NpcConfirmModalVtableRva) {
			confirmControlOffset = GameClientAddresses::NpcConfirmControlOffset;
		} else if (modalVtableAddress == gameBase + GameClientAddresses::RepairConfirmModalVtableRva) {
			confirmControlOffset = GameClientAddresses::RepairConfirmControlOffset;
		} else {
			return false;
		}
		auto dispatchModalEvent = reinterpret_cast<ModalEventFunction>(
			*reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(modalVtable) + GameClientAddresses::ModalEventMethodVtableOffset));
		if (!IsExecutableAddress(reinterpret_cast<void*>(dispatchModalEvent))) {
			return false;
		}
		void* confirmControl = reinterpret_cast<uint8_t*>(modal) + confirmControlOffset;
		dispatchModalEvent(modal, GameClientAddresses::ModalConfirmEvent, confirmControl, 0);
		return true;
	}

	bool TryDispatchResetPickup() {
		uint8_t* gameBase = nullptr;
		void* manager = nullptr;
		if (!TryGetGameContext(gameBase, manager)) {
			return false;
		}
		void* inventoryRoot = *reinterpret_cast<void**>(gameBase + GameClientAddresses::InventoryRootRva);
		auto resetPickup = reinterpret_cast<ResetPickupFunction>(gameBase + GameClientAddresses::ResetPickupFunctionRva);
		if (inventoryRoot == nullptr || !IsExecutableAddress(reinterpret_cast<void*>(resetPickup)) ||
			memcmp(reinterpret_cast<void*>(resetPickup), ResetPickupFunctionSignature, sizeof(ResetPickupFunctionSignature)) != 0) {
			return false;
		}
		void* inventoryObject = reinterpret_cast<uint8_t*>(inventoryRoot) + GameClientAddresses::InventoryObjectOffset;
		resetPickup(inventoryObject);
		return true;
	}

	bool TryGetCoordinateDispatcher(uint8_t*& gameBase, void*& manager, CoordinateDispatcherFunction& dispatcher) {
		if (!TryGetGameContext(gameBase, manager)) {
			return false;
		}
		void* expectedVtable = gameBase + GameClientAddresses::ExpectedManagerVtableRva;
		if (*reinterpret_cast<void**>(manager) != expectedVtable) {
			return false;
		}
		auto managerVtable = *reinterpret_cast<uint8_t***>(manager);
		if (managerVtable == nullptr) {
			return false;
		}
		dispatcher = reinterpret_cast<CoordinateDispatcherFunction>(
			*reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(managerVtable) + GameClientAddresses::CoordinateDispatcherMethodVtableOffset));
		return IsExecutableAddress(reinterpret_cast<void*>(dispatcher));
	}

	bool TryDispatchMovementReset() {
		uint8_t* gameBase = nullptr;
		void* manager = nullptr;
		CoordinateDispatcherFunction dispatcher = nullptr;
		if (!TryGetCoordinateDispatcher(gameBase, manager, dispatcher)) {
			return false;
		}
		dispatcher(manager, GameClientAddresses::CoordinateOpcode, -1, -1);
		pendingMovementX = 0;
		return true;
	}


	bool TryDispatchMovementY(int dispatchY) {
		int dispatchX = pendingMovementX;
		if (dispatchX <= 0 || dispatchY <= 0) {
			return false;
		}
		uint8_t* gameBase = nullptr;
		void* manager = nullptr;
		CoordinateDispatcherFunction dispatcher = nullptr;
		if (!TryGetCoordinateDispatcher(gameBase, manager, dispatcher)) {
			return false;
		}
		dispatcher(manager, GameClientAddresses::CoordinateOpcode, dispatchX, dispatchY);
		pendingMovementX = 0;
		return true;
	}

	// Đi bộ thường tới một toạ độ raw. Dùng chung hàm và cùng chữ ký với luồng nhặt đồ, chỉ khác là không chọn
	// vật phẩm và không gọi pickup sau đó.
	// Trả false khi bất kỳ chốt an toàn nào không đạt, để phía C# ghi log đúng lý do thay vì báo thành công suông.
	bool TryDispatchWalkTo(int rawY) {
		int rawX = pendingMovementX;
		if (rawX <= 0 || rawY <= 0) {
			return false;
		}
		auto gameBase = reinterpret_cast<uint8_t*>(GetModuleHandleA("Game.exe"));
		if (!HasSupportedGameImage(gameBase)) {
			return false;
		}
		auto movement = reinterpret_cast<PickupMovementFunction>(gameBase + GameClientAddresses::PickupMovementFunctionRva);
		auto clickGround = reinterpret_cast<PickupFunction>(gameBase + GameClientAddresses::PickupFunctionRva);
		void* entityTable = *reinterpret_cast<void**>(gameBase + GameClientAddresses::EntityTableRva);
		if (!IsExecutableAddress(reinterpret_cast<void*>(movement)) ||
			!IsExecutableAddress(reinterpret_cast<void*>(clickGround)) ||
			entityTable == nullptr ||
			memcmp(reinterpret_cast<void*>(movement), PickupMovementFunctionSignature, sizeof(PickupMovementFunctionSignature)) != 0 ||
			memcmp(reinterpret_cast<void*>(clickGround), PickupFunctionSignature, sizeof(PickupFunctionSignature)) != 0) {
			return false;
		}
		// PHẢI khớp ĐÚNG trình tự ba bước của nhánh nhặt đồ (TryDispatchPickupMove ngay bên dưới). Bản trước chỉ gọi
		// mỗi movement(...) ở giữa, thiếu cả reset lẫn clickGround — và đó là toàn bộ khác biệt so với nhánh nhặt vốn
		// chạy tốt (loot-drops.log có LOOT_PICKED_UP kèm bằng chứng TúiTrước/TúiSau).
		//
		// Hậu quả đo được trên Release/Diagnostics/movement.log ngày 2026-09-15, 849 lệnh ELITE_RETREAT theo dõi
		// tiếp 15 giây sau khi gửi:
		//   TỚI ĐƯỢC đích  =  22 (2,6%)
		//   đi được một phần= 168 (19,8%)
		//   KHÔNG tiến được = 659 (77,6%)
		// Chủ dự án mô tả đúng hiện tượng này: nhân vật nhảy sang chỗ mới rồi bị giật ngược về ngay lập tức, tức
		// client dịch cục bộ nhưng server không nhận -> rubber-band.
		//
		// reset dùng CoordinateOpcode với (-1,-1) là XOÁ trạng thái di chuyển cũ, không phải đặt đích mới, nên KHÔNG
		// sinh lá cờ trên màn hình — nhánh nhặt vẫn gọi nó và chủ dự án xác nhận nhặt đồ không hiện cờ.
		TryDispatchMovementReset();
		void* playerEntity = reinterpret_cast<uint8_t*>(entityTable) +
			GameClientAddresses::PlayerEntityIndex * GameClientAddresses::EntityStride;
		movement(playerEntity, 3, rawX, rawY, 0);
		clickGround(rawX, rawY);
		pendingMovementX = 0;
		return true;
	}

	bool TryDispatchPortal(int rawY) {
		int rawX = pendingMovementX;
		if (rawX <= 0 || rawY <= 0) {
			return false;
		}
		uint8_t* gameBase = nullptr;
		void* manager = nullptr;
		if (!TryGetGameContext(gameBase, manager)) {
			return false;
		}
		auto movement = reinterpret_cast<PickupMovementFunction>(gameBase + GameClientAddresses::PickupMovementFunctionRva);
		auto clickGround = reinterpret_cast<PickupFunction>(gameBase + GameClientAddresses::PickupFunctionRva);
		void* entityTable = *reinterpret_cast<void**>(gameBase + GameClientAddresses::EntityTableRva);
		if (!IsExecutableAddress(reinterpret_cast<void*>(movement)) ||
			!IsExecutableAddress(reinterpret_cast<void*>(clickGround)) ||
			entityTable == nullptr ||
			memcmp(reinterpret_cast<void*>(movement), PickupMovementFunctionSignature, sizeof(PickupMovementFunctionSignature)) != 0 ||
			memcmp(reinterpret_cast<void*>(clickGround), PickupFunctionSignature, sizeof(PickupFunctionSignature)) != 0) {
			return false;
		}
		TryDispatchMovementReset();
		void* playerEntity = reinterpret_cast<uint8_t*>(entityTable) +
			GameClientAddresses::PlayerEntityIndex * GameClientAddresses::EntityStride;
		movement(playerEntity, 3, rawX, rawY, 0);
		clickGround(rawX, rawY);
		pendingMovementX = 0;
		return true;
	}

	bool TryDispatchPickup(int itemIndex) {
		if (itemIndex < 1 || itemIndex > 127) {
			return false;
		}

		auto gameBase = reinterpret_cast<uint8_t*>(GetModuleHandleA("Game.exe"));
		if (!HasSupportedGameImage(gameBase)) {
			return false;
		}
		auto groundTable = *reinterpret_cast<uint8_t**>(gameBase + GameClientAddresses::GroundRecordTablePointerRva);
		if (groundTable == nullptr) {
			return false;
		}
		auto groundRecord = groundTable +
			static_cast<uintptr_t>(itemIndex) * GameClientAddresses::GroundRecordStride;
		int groundId = *reinterpret_cast<int*>(groundRecord + GameClientAddresses::GroundRecordIdOffset);
		int groundState = *reinterpret_cast<int*>(groundRecord + GameClientAddresses::GroundRecordStateOffset);
		if (groundId <= 0 || (groundState != 3 && groundState != 4)) {
			return false;
		}

		auto coordinateConverter = reinterpret_cast<GroundCoordinateConverterFunction>(gameBase + GameClientAddresses::GroundCoordinateConverterRva);
		auto movement = reinterpret_cast<PickupMovementFunction>(gameBase + GameClientAddresses::PickupMovementFunctionRva);
		auto pickup = reinterpret_cast<PickupFunction>(gameBase + GameClientAddresses::PickupFunctionRva);
		void* entityTable = *reinterpret_cast<void**>(gameBase + GameClientAddresses::EntityTableRva);
		if (!IsExecutableAddress(reinterpret_cast<void*>(coordinateConverter)) ||
			!IsExecutableAddress(reinterpret_cast<void*>(movement)) ||
			!IsExecutableAddress(reinterpret_cast<void*>(pickup)) ||
			entityTable == nullptr ||
			memcmp(reinterpret_cast<void*>(coordinateConverter), GroundCoordinateConverterSignature, sizeof(GroundCoordinateConverterSignature)) != 0 ||
			memcmp(reinterpret_cast<void*>(movement), PickupMovementFunctionSignature, sizeof(PickupMovementFunctionSignature)) != 0 ||
			memcmp(reinterpret_cast<void*>(pickup), PickupFunctionSignature, sizeof(PickupFunctionSignature)) != 0) {
			return false;
		}
		int rawX = 0;
		int rawY = 0;
		coordinateConverter(groundRecord, &rawX, &rawY);
		if (rawX <= 0 || rawY <= 0 || !TrySelectGroundItem(itemIndex)) {
			return false;
		}
		void* playerEntity = reinterpret_cast<uint8_t*>(entityTable) +
			GameClientAddresses::PlayerEntityIndex * GameClientAddresses::EntityStride;
		movement(playerEntity, 3, rawX, rawY, 0);
		pickup(rawX, rawY);
		return true;
	}

	// Thi triển skill bằng chuỗi action mode 5 và cast wrapper tương ứng AutoFS.
	// Trả 1 khi đã gọi được. Các mã 30..35 tách riêng từng bước kiểm tra vì runtime 2026-09-03 trả 0 chung chung
	// nên không phân biệt được RVA nào đã lệch sau bản client 2026-08-28.
	int TryDispatchCastSkill(int skillId) {
		if (skillId <= 0 || skillId > UINT16_MAX) {
			return 30;
		}

		auto gameBase = reinterpret_cast<uint8_t*>(GetModuleHandleA("Game.exe"));
		if (!HasSupportedGameImage(gameBase)) {
			return 31;
		}
		auto buffAction = reinterpret_cast<PickupMovementFunction>(gameBase + GameClientAddresses::BuffActionFunctionRva);
		auto castSkill  = reinterpret_cast<CastSkillFunction>(gameBase + GameClientAddresses::CastSkillFunctionRva);
		void* entityTable = *reinterpret_cast<void**>(gameBase + GameClientAddresses::EntityTableRva);
		if (!IsExecutableAddress(reinterpret_cast<void*>(buffAction)) ||
			!IsExecutableAddress(reinterpret_cast<void*>(castSkill))) {
			return 32;
		}
		if (entityTable == nullptr) {
			return 33;
		}

		// Xác thực bằng chữ ký byte của chính hai hàm thay vì call site, vì call site lệch sau mỗi lần client cập nhật.
		if (memcmp(reinterpret_cast<void*>(buffAction), PickupMovementFunctionSignature, sizeof(PickupMovementFunctionSignature)) != 0) {
			return 34;
		}
		auto castSendSite = gameBase + GameClientAddresses::CastSkillFunctionRva + GameClientAddresses::CastSendSiteOffset;
		if (memcmp(castSendSite, CastSendSiteSignature, sizeof(CastSendSiteSignature)) != 0) {
			return 35;
		}
		void* playerEntity = reinterpret_cast<uint8_t*>(entityTable) +
			GameClientAddresses::PlayerEntityIndex * GameClientAddresses::EntityStride;
		buffAction(playerEntity, 5, skillId, 0, 0);
		castSkill(skillId, 0, 0);
		return 1;
	}

	// Bật một skill hỗ trợ bị động của hệ Dị Nhân bằng đúng hàm client dựng gói opcode 0x72 độ dài 5.
	// Trả 1 khi đã gọi được, các giá trị 20..24 chỉ đúng bước bị từ chối để log phía C# khoanh vùng được nguyên nhân.
	int TryDispatchPassiveBuff(int skillId) {
		if (skillId <= 0 || skillId > GameClientAddresses::PassiveBuffMaximumSkillId) {
			return 20;
		}

		auto gameBase = reinterpret_cast<uint8_t*>(GetModuleHandleA("Game.exe"));
		if (!HasSupportedGameImage(gameBase)) {
			return 21;
		}
		auto sender = reinterpret_cast<PassiveBuffFunction>(gameBase + GameClientAddresses::PassiveBuffFunctionRva);
		if (!IsExecutableAddress(reinterpret_cast<void*>(sender))) {
			return 22;
		}
		// Xác nhận đúng hàm bằng chính đoạn dựng gói bên trong nó, tránh gọi nhầm sau khi client đổi bố cục.
		auto sendSite = gameBase + GameClientAddresses::PassiveBuffFunctionRva + GameClientAddresses::PassiveBuffSendSiteOffset;
		if (memcmp(sendSite, PassiveBuffSendSiteSignature, sizeof(PassiveBuffSendSiteSignature)) != 0) {
			return 23;
		}
		void* entityTable = *reinterpret_cast<void**>(gameBase + GameClientAddresses::EntityTableRva);
		if (entityTable == nullptr) {
			return 24;
		}

		void* playerEntity = reinterpret_cast<uint8_t*>(entityTable) +
			GameClientAddresses::PlayerEntityIndex * GameClientAddresses::EntityStride;
		sender(playerEntity, skillId);
		return 1;
	}

	// Chuyển lựa chọn xử lý khi chết của AutoFS vào cùng handler popup của game.
	bool TryDispatchReturnToTown(int option) {
		if (option < 0 || option > 2) {
			return false;
		}
		auto gameBase = reinterpret_cast<uint8_t*>(GetModuleHandleA("Game.exe"));
		if (!HasSupportedGameImage(gameBase)) {
			return false;
		}
		void* returnToTownObject = gameBase + GameClientAddresses::ReturnToTownObjectRva;
		void* expectedVtable = gameBase + GameClientAddresses::ReturnToTownObjectVtableRva;
		if (*reinterpret_cast<void**>(returnToTownObject) != expectedVtable) {
			return false;
		}
		auto returnToTown = reinterpret_cast<ReturnToTownFunction>(gameBase + GameClientAddresses::ReturnToTownFunctionRva);
		if (!IsExecutableAddress(reinterpret_cast<void*>(returnToTown)) ||
			memcmp(reinterpret_cast<void*>(returnToTown), ReturnToTownFunctionSignature, sizeof(ReturnToTownFunctionSignature)) != 0) {
			return false;
		}
		returnToTown(returnToTownObject, option);
		return true;
	}

	bool TryDispatchSale(int itemId) {
		if (itemId <= 0) {
			return false;
		}
		uint8_t* gameBase = nullptr;
		void* manager = nullptr;
		if (!TryGetGameContext(gameBase, manager)) {
			return false;
		}
		void* expectedVtable = gameBase + GameClientAddresses::ExpectedManagerVtableRva;
		auto managerVtable = *reinterpret_cast<uint8_t***>(manager);
		if (managerVtable == nullptr || *reinterpret_cast<void**>(manager) != expectedVtable) {
			return false;
		}
		auto sale = reinterpret_cast<SaleFunction>(
			*reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(managerVtable) + GameClientAddresses::SaleMethodVtableOffset));
		if (!IsExecutableAddress(reinterpret_cast<void*>(sale))) {
			return false;
		}
		uint32_t buffer[2]{ 5, static_cast<uint32_t>(itemId) };
		sale(manager, GameClientAddresses::SaleOpcode, buffer, 0);
		return true;
	}

	// Dùng item bằng bảy trường descriptor mà dispatcher client hiện tại đọc, gồm loại inventory ở trường cuối.
	// Trả mã lý do riêng cho từng nhánh từ chối để log C# chỉ ra đúng bước hỏng thay vì chỉ thấy Return=0.
	// 1 = đã gửi, 10 = mô tả vật phẩm sai, 11 = chưa lấy được context game, 12 = vtable manager lệch,
	// 13 = InventoryRoot bằng 0, 14 = slot không còn đúng vật phẩm, 15 = chữ ký hàm tọa độ lệch, 16 = bước prepare bị từ chối.
	int TryDispatchInventoryItem(uint32_t packedItem) {
		int memoryIndex = static_cast<int>(packedItem & 0x3F);
		int container = static_cast<int>((packedItem >> 6) & 0x1F);
		int expectedItemId = static_cast<int>(packedItem >> 11);
		uintptr_t slotListPointerOffset = 0;
		int slotCount = 0;
		// Mã container mà CHÍNH CLIENT dùng, khác hẳn số 11/3/16 thừa hưởng từ AutoFS.
		//
		// Dịch ngược hàm điều phối container tại RVA 0x3714D0 trên PID 22056 ngày 2026-09-09:
		//     mov cl,[edx+1]                  ; mã container
		//     test cl,cl / je   -> lea ecx,[ecx+0x4B7BC]      (mã 0)
		//     cmp cl,0x0E / je  -> lea ecx,[ecx+0x4B9EC]      (mã 14)
		//     lea eax,[ecx-0x13] / cmp al,9 / ja              (mã 19..28)
		//     lea ecx,[eax+eax*4] / lea ecx,[ecx*8+0x4B7BC]   ; = 40*mã + 0x4B7BC
		// Nghĩa là container là MẢNG phần tử 40 byte tại gốc+0x4B7BC, và ba offset danh sách ô mà Auto đã
		// xác minh chính là 40*mã: 0x0000=40*0, 0x230=40*14, 0x2F8=40*19. Mã hợp lệ duy nhất là 0, 14, 19..28.
		//
		// Vì vậy số 11 mà descriptor cũ gửi đi không trỏ tới container nào.
		//
		// ĐÃ THỬ VÀ BỊ BÁC BỎ 2026-09-09: đổi sang mã client rồi đo lại trên PID 14236, bùa thường trong túi
		// (Container=3, Slot=27, SốLượng=4), với dấu phiên bản native xác nhận bản mới đang chạy
		// (BuildStamp=20260909). Kết quả vẫn SốLượng 4 -> 4 và Map ID không đổi. Mã container KHÔNG phải nguyên
		// nhân. Giữ lại ánh xạ này vì nó khớp cấu trúc client đã dịch ngược, nhưng nó không sửa được gì.
		//
		// TOÀN BỘ ĐƯỜNG LỆNH 310 HIỆN COI NHƯ CHẾT. Luồng thật dùng phím tắt ô trang bị nhanh
		// (AutoFsAttackTransport.TryUseQuickSlotHotkey), đã đo được MapId 37 -> 21 và SốLượng 1 -> 0.
		//
		// Những gì đã loại, để người sau khỏi dò lại:
		//   - InventoryUsePrepareOpcode 0x12A KHÔNG có trong DLL native của AutoFS (quét toàn .text, 0 chỗ).
		//     Nó do DEV auto tự nghĩ ra, không phải port từ AutoFS.
		//   - AutoFS không gửi một lệnh gói. Nó gửi BỐN message tới hook của chính nó (WindowQueue.cs:23971):
		//     cmd 0=itemId, cmd 1=container, cmd 2=slot/5, cmd 10=slot%5 kèm kích hoạt.
		//   - Handler thật trong DLL AutoFS ở 0x1000196F, gọi trực tiếp:
		//         this = *(Game.exe + 0x5EB2FC) + 0x2E5E4
		//         FUN_005B64D0(this, itemId, container, cot, hang, 0)
		//   - Hai địa chỉ đó KHÔNG port sang client hiện tại: *(0x5EB2FC) và *(0x5ED31C) đều đọc ra NULL, còn
		//     RVA 0x1B64D0 rơi vào giữa hàm chứ không phải mở đầu. DLL AutoFS đề Nov 2024, client là 2026-08.
		//   - Không trích được chữ ký từ file: mọi Game.exe đều bị nén (section TML có RawSize=0), code chỉ tồn
		//     tại sau khi giải nén trong bộ nhớ. Muốn dò tiếp buộc phải quét trên tiến trình đang chạy.
		int clientContainerCode = -1;
		if (container == 11) {
			slotListPointerOffset = GameClientAddresses::InventoryQuickSlotListPointerOffset;
			slotCount = GameClientAddresses::InventoryQuickSlotCount;
			clientContainerCode = 14;
		} else if (container == 3) {
			slotListPointerOffset = GameClientAddresses::InventorySlotListPointerOffset;
			slotCount = GameClientAddresses::InventorySlotCount;
			clientContainerCode = 0;
		} else if (container == 16) {
			slotListPointerOffset = GameClientAddresses::InventoryExtendedSlotListPointerOffset;
			slotCount = GameClientAddresses::InventoryExtendedSlotCount;
			clientContainerCode = 19;
		}
		if (memoryIndex < 0 || memoryIndex >= slotCount || expectedItemId <= 0) {
			return 10;
		}
		uint8_t* gameBase = nullptr;
		void* manager = nullptr;
		if (!TryGetGameContext(gameBase, manager)) {
			return 11;
		}
		auto managerVtable = *reinterpret_cast<uint8_t***>(manager);
		if (managerVtable == nullptr || *reinterpret_cast<void**>(manager) != gameBase + GameClientAddresses::ExpectedManagerVtableRva) {
			return 12;
		}
		void* inventoryRoot = *reinterpret_cast<void**>(gameBase + GameClientAddresses::InventoryRootRva);
		if (inventoryRoot == nullptr) {
			return 13;
		}
		void* inventoryObject = reinterpret_cast<uint8_t*>(inventoryRoot) + GameClientAddresses::InventoryObjectOffset;
		auto slotList = *reinterpret_cast<int**>(reinterpret_cast<uint8_t*>(inventoryObject) + slotListPointerOffset);
		if (slotList == nullptr || slotList[memoryIndex] != expectedItemId) {
			return 14;
		}
		auto query = reinterpret_cast<QueryFunction>(
			*reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(managerVtable) + GameClientAddresses::PrepareMethodVtableOffset));
		auto dispatch = reinterpret_cast<GenericDispatchFunction>(
			*reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(managerVtable) + GameClientAddresses::DialogOptionMethodVtableOffset));
		auto getCoordinates = reinterpret_cast<InventoryCoordinateFunction>(gameBase + GameClientAddresses::InventoryCoordinateFunctionRva);
		if (!IsExecutableAddress(reinterpret_cast<void*>(query)) ||
			!IsExecutableAddress(reinterpret_cast<void*>(dispatch)) ||
			!IsExecutableAddress(reinterpret_cast<void*>(getCoordinates)) ||
			memcmp(reinterpret_cast<void*>(getCoordinates), InventoryCoordinateFunctionSignature, sizeof(InventoryCoordinateFunctionSignature)) != 0) {
			return 15;
		}
		// Bước prepare bị client từ chối. Mã 16 cũ gộp mọi lý do nên không biết client trả giá trị gì;
		// nay trả 4096 + giá trị thật để log phía C# phân biệt được "không cho dùng lúc này" với "sai vtable slot".
		int prepareResult = query(manager, GameClientAddresses::InventoryUsePrepareOpcode, container, 0);
		if (prepareResult != 0) {
			return 4096 + (prepareResult & 0xFFF);
		}
		int coordinateX = 0;
		int coordinateY = 0;
		getCoordinates(&coordinateX, &coordinateY);
		uint32_t descriptor[7]{
			static_cast<uint32_t>(expectedItemId),
			static_cast<uint32_t>(clientContainerCode),
			static_cast<uint32_t>(memoryIndex / 5),
			static_cast<uint32_t>(memoryIndex % 5),
			0,
			0,
			container == 11 ? 3U : 5U
		};
		int packedCoordinates = static_cast<int>((static_cast<uint32_t>(coordinateX) & 0xFFFFU) |
			((static_cast<uint32_t>(coordinateY) & 0xFFFFU) << 16));
		dispatch(manager, GameClientAddresses::InventoryUseOpcode, descriptor, packedCoordinates);
		return 1;
	}

	// Xóa nội dung chat đang chờ sau mỗi lần gửi để lần gửi sau luôn bắt đầu từ buffer rỗng.
	void ResetPendingScript() {
		pendingScriptLength = 0;
		pendingScript[0] = '\0';
	}

	// Gửi chat theo đúng chuỗi hàm mà binding Chat(kênh, nội dung) FUN_004637c0 của client tự thực hiện.
	bool TryDispatchChat(int channelIndex) {
		if (channelIndex < 0 || pendingScriptLength == 0 || pendingScriptLength > MaximumScriptLength) {
			ResetPendingScript();
			return false;
		}
		auto gameBase = reinterpret_cast<uint8_t*>(GetModuleHandleA("Game.exe"));
		if (!HasSupportedGameImage(gameBase)) {
			ResetPendingScript();
			return false;
		}
		auto channelCodeFromIndex = reinterpret_cast<ChannelCodeFromIndexFunction>(gameBase + GameClientAddresses::ChannelCodeFromIndexRva);
		auto channelTypeFromIndex = reinterpret_cast<ChannelTypeFromIndexFunction>(gameBase + GameClientAddresses::ChannelTypeFromIndexRva);
		auto chatGate = reinterpret_cast<ChatGateFunction>(gameBase + GameClientAddresses::ChatGateFunctionRva);
		auto chatPack = reinterpret_cast<ChatPackFunction>(gameBase + GameClientAddresses::ChatPackFunctionRva);
		auto chatEncode = reinterpret_cast<ChatEncodeFunction>(gameBase + GameClientAddresses::ChatEncodeFunctionRva);
		auto channelActivate = reinterpret_cast<ChannelActivateFunction>(gameBase + GameClientAddresses::ChannelActivateFunctionRva);
		auto sendChat = reinterpret_cast<ChatSendFunction>(gameBase + GameClientAddresses::ChatSendFunctionRva);
		if (!IsExecutableAddress(reinterpret_cast<void*>(channelCodeFromIndex)) ||
			!IsExecutableAddress(reinterpret_cast<void*>(channelTypeFromIndex)) ||
			!IsExecutableAddress(reinterpret_cast<void*>(chatGate)) ||
			!IsExecutableAddress(reinterpret_cast<void*>(chatPack)) ||
			!IsExecutableAddress(reinterpret_cast<void*>(chatEncode)) ||
			!IsExecutableAddress(reinterpret_cast<void*>(channelActivate)) ||
			!IsExecutableAddress(reinterpret_cast<void*>(sendChat))) {
			ResetPendingScript();
			return false;
		}
		if (memcmp(reinterpret_cast<void*>(channelCodeFromIndex), ChannelCodeFromIndexSignature, sizeof(ChannelCodeFromIndexSignature)) != 0 ||
			memcmp(reinterpret_cast<void*>(channelTypeFromIndex), ChannelTypeFromIndexSignature, sizeof(ChannelTypeFromIndexSignature)) != 0 ||
			memcmp(reinterpret_cast<void*>(chatGate), ChatGateFunctionSignature, sizeof(ChatGateFunctionSignature)) != 0 ||
			memcmp(reinterpret_cast<void*>(chatPack), ChatPackFunctionSignature, sizeof(ChatPackFunctionSignature)) != 0 ||
			memcmp(reinterpret_cast<void*>(chatEncode), ChatEncodeFunctionSignature, sizeof(ChatEncodeFunctionSignature)) != 0 ||
			memcmp(reinterpret_cast<void*>(channelActivate), ChannelActivateFunctionSignature, sizeof(ChannelActivateFunctionSignature)) != 0 ||
			memcmp(reinterpret_cast<void*>(sendChat), ChatSendFunctionSignature, sizeof(ChatSendFunctionSignature)) != 0) {
			ResetPendingScript();
			return false;
		}
		pendingScript[pendingScriptLength] = '\0';
		int messageLength = static_cast<int>(pendingScriptLength);
		const char* channelCode = channelCodeFromIndex(channelIndex);
		if (channelCode == nullptr || channelCode[0] == '\0') {
			ResetPendingScript();
			return false;
		}
		int channelType = channelTypeFromIndex(channelIndex);
		if (channelType == -1) {
			ResetPendingScript();
			return false;
		}
		if (chatGate(pendingScript, messageLength, channelCode, channelType) == 0) {
			ResetPendingScript();
			return false;
		}
		char packet[GameClientAddresses::ChatPacketBufferSize]{};
		int packetLength = chatPack(packet, static_cast<int>(GameClientAddresses::ChatPacketBufferSize), pendingScript, messageLength);
		packetLength = chatEncode(packet, packetLength);
		if (packetLength <= 0) {
			ResetPendingScript();
			return false;
		}
		channelActivate(channelIndex, 1);
		sendChat(channelType, packet, packetLength, -1);
		ResetPendingScript();
		return true;
	}

	// Mã kết quả của một mục audit. Ba giá trị đầu là đạt, giá trị 4 là chưa kết luận được, còn lại là lệch.
	// 1 = khớp chữ ký byte, 2 = con trỏ dữ liệu hợp lệ, 3 = vtable khớp, 4 = con trỏ đang rỗng nên chưa kết luận,
	// 10 = vùng nhớ không thực thi được, 11 = chữ ký byte lệch, 12 = con trỏ rỗng, 13 = vùng nhớ không đọc được,
	// 14 = vtable không đúng địa chỉ mong đợi, 20 = ảnh Game.exe không được hỗ trợ, 0 = chỉ số ngoài phạm vi.
	uint32_t GetGameImageSize(uint8_t* gameBase) {
		auto dosHeader = reinterpret_cast<IMAGE_DOS_HEADER*>(gameBase);
		auto ntHeaders = reinterpret_cast<IMAGE_NT_HEADERS32*>(gameBase + dosHeader->e_lfanew);
		return ntHeaders->OptionalHeader.SizeOfImage;
	}

	int AuditCodeSignature(uint8_t* gameBase, uintptr_t functionRva, uintptr_t siteOffset, const uint8_t* signature, size_t length) {
		uint8_t* function = gameBase + functionRva;
		uint8_t* site = function + siteOffset;
		if (!IsExecutableAddress(function) || !IsExecutableAddress(site)) {
			return 10;
		}
		return memcmp(site, signature, length) == 0 ? 1 : 11;
	}

	int AuditGlobalPointer(uint8_t* gameBase, uintptr_t rva, bool nullIsInconclusive) {
		void* slot = gameBase + rva;
		if (!IsReadableAddress(slot)) {
			return 13;
		}
		void* value = *reinterpret_cast<void**>(slot);
		if (value == nullptr) {
			return nullIsInconclusive ? 4 : 12;
		}
		return IsReadableAddress(value) ? 2 : 13;
	}

	// Bảng hàm ảo nằm tĩnh trong ảnh: bốn ô đầu phải là con trỏ mã lệnh nằm trong chính Game.exe.
	int AuditStaticVtable(uint8_t* gameBase, uintptr_t rva, uint32_t imageSize) {
		auto slots = reinterpret_cast<uint8_t**>(gameBase + rva);
		if (!IsReadableAddress(slots)) {
			return 13;
		}
		for (int slot = 0; slot < 4; slot++) {
			uint8_t* target = slots[slot];
			if (target < gameBase || target >= gameBase + imageSize) {
				return 14;
			}
			if (!IsExecutableAddress(target)) {
				return 10;
			}
		}
		return 3;
	}

	int AuditStaticObjectVtable(uint8_t* gameBase, uintptr_t objectRva, uintptr_t vtableRva) {
		void* object = gameBase + objectRva;
		if (!IsReadableAddress(object)) {
			return 13;
		}
		void* actual = *reinterpret_cast<void**>(object);
		if (actual == nullptr) {
			return 12;
		}
		return actual == gameBase + vtableRva ? 3 : 14;
	}

	// Lấy vtable của manager và đồng thời xác nhận AttackManagerRva lẫn ExpectedManagerVtableRva còn đúng.
	int TryGetManagerVtable(uint8_t* gameBase, uint8_t*& managerVtable) {
		managerVtable = nullptr;
		void* managerSlot = gameBase + GameClientAddresses::AttackManagerRva;
		if (!IsReadableAddress(managerSlot)) {
			return 13;
		}
		void* manager = *reinterpret_cast<void**>(managerSlot);
		if (manager == nullptr) {
			return 12;
		}
		if (!IsReadableAddress(manager)) {
			return 13;
		}
		void* actualVtable = *reinterpret_cast<void**>(manager);
		if (actualVtable != gameBase + GameClientAddresses::ExpectedManagerVtableRva) {
			return 14;
		}
		managerVtable = reinterpret_cast<uint8_t*>(actualVtable);
		return 3;
	}

	int AuditManagerVtableSlot(uint8_t* gameBase, size_t vtableOffset) {
		uint8_t* managerVtable = nullptr;
		int managerResult = TryGetManagerVtable(gameBase, managerVtable);
		if (managerResult != 3) {
			return managerResult;
		}
		void* method = *reinterpret_cast<void**>(managerVtable + vtableOffset);
		return IsExecutableAddress(method) ? 2 : 10;
	}

	// Ô vtable +0x40 là hàm ATTACK, kiểm được bằng chính chữ ký ghi ba trường lệnh của AutoFS.
	int AuditAttackWriter(uint8_t* gameBase) {
		uint8_t* managerVtable = nullptr;
		int managerResult = TryGetManagerVtable(gameBase, managerVtable);
		if (managerResult != 3) {
			return managerResult;
		}
		auto attack = *reinterpret_cast<uint8_t**>(managerVtable + GameClientAddresses::AttackMethodVtableOffset);
		if (!IsExecutableAddress(attack)) {
			return 10;
		}
		return ContainsAttackWriterSignature(attack) ? 1 : 11;
	}

	// Bốn hàm của lệnh 284 nằm tĩnh trong ảnh, không qua con trỏ nào, nên chỉ cần kiểm chúng có phải vùng mã lệnh không.
	// CẢNH BÁO: hàm này chỉ hỏi "địa chỉ có nằm trong trang thực thi không", KHÔNG so chữ ký byte như các
	// AuditCodeSignature khác. Mọi địa chỉ nằm giữa .text đều đạt, kể cả khi trỏ vào giữa thân một hàm khác —
	// đúng trường hợp LoginSubmitFunctionRva trên client 1.28 (xem ghi chú ở GameClientAddresses.h). Vì vậy kết quả
	// 1 của hàm này KHÔNG được coi là bằng chứng địa chỉ đúng.
	int AuditLoginSubmitFunctions(uint8_t* gameBase) {
		const uintptr_t functionRvas[] = {
			GameClientAddresses::LoginSubmitFunctionRva,
			GameClientAddresses::LoginAfterSubmitFunctionARva,
			GameClientAddresses::LoginAfterSubmitFunctionBRva,
			GameClientAddresses::LoginAfterSubmitFunctionCRva
		};
		for (uintptr_t functionRva : functionRvas) {
			if (!IsExecutableAddress(gameBase + functionRva)) {
				return 10;
			}
		}
		return IsReadableAddress(gameBase + GameClientAddresses::LoginSubmitContextRva) ? 1 : 13;
	}

	int AuditAddress(int index) {
		auto gameBase = reinterpret_cast<uint8_t*>(GetModuleHandleA("Game.exe"));
		if (!HasSupportedGameImage(gameBase)) {
			return 20;
		}
		uint32_t imageSize = GetGameImageSize(gameBase);
		switch (index) {
			case 0: {
				uint8_t* managerVtable = nullptr;
				return TryGetManagerVtable(gameBase, managerVtable);
			}
			case 1: return AuditManagerVtableSlot(gameBase, GameClientAddresses::DialogOptionMethodVtableOffset);
			case 2: return AuditAttackWriter(gameBase);
			case 3: return AuditManagerVtableSlot(gameBase, GameClientAddresses::SelectGroundItemMethodVtableOffset);
			case 4: return AuditManagerVtableSlot(gameBase, GameClientAddresses::PrepareMethodVtableOffset);
			case 5: return AuditGlobalPointer(gameBase, GameClientAddresses::EntityTableRva, false);
			case 6: return AuditGlobalPointer(gameBase, GameClientAddresses::GroundRecordTablePointerRva, false);
			// InventoryRoot và ModalState chỉ được game gán khi cần nên giá trị 0 không phải bằng chứng RVA lệch.
			case 7: return AuditGlobalPointer(gameBase, GameClientAddresses::InventoryRootRva, true);
			case 8: return AuditGlobalPointer(gameBase, GameClientAddresses::ModalStateRva, true);
			case 9: return AuditStaticVtable(gameBase, GameClientAddresses::NpcConfirmModalVtableRva, imageSize);
			case 10: return AuditStaticVtable(gameBase, GameClientAddresses::RepairConfirmModalVtableRva, imageSize);
			case 11: return AuditStaticObjectVtable(gameBase, GameClientAddresses::ReturnToTownObjectRva, GameClientAddresses::ReturnToTownObjectVtableRva);
			case 12: return AuditCodeSignature(gameBase, GameClientAddresses::ReturnToTownFunctionRva, 0, ReturnToTownFunctionSignature, sizeof(ReturnToTownFunctionSignature));
			case 13: return AuditCodeSignature(gameBase, GameClientAddresses::ResetPickupFunctionRva, 0, ResetPickupFunctionSignature, sizeof(ResetPickupFunctionSignature));
			case 14: return AuditCodeSignature(gameBase, GameClientAddresses::GroundCoordinateConverterRva, 0, GroundCoordinateConverterSignature, sizeof(GroundCoordinateConverterSignature));
			case 15: return AuditCodeSignature(gameBase, GameClientAddresses::PickupMovementFunctionRva, 0, PickupMovementFunctionSignature, sizeof(PickupMovementFunctionSignature));
			case 16: return AuditCodeSignature(gameBase, GameClientAddresses::BuffActionFunctionRva, 0, PickupMovementFunctionSignature, sizeof(PickupMovementFunctionSignature));
			case 17: return AuditCodeSignature(gameBase, GameClientAddresses::PickupFunctionRva, 0, PickupFunctionSignature, sizeof(PickupFunctionSignature));
			case 18: return AuditCodeSignature(gameBase, GameClientAddresses::InventoryCoordinateFunctionRva, 0, InventoryCoordinateFunctionSignature, sizeof(InventoryCoordinateFunctionSignature));
			case 19: return AuditCodeSignature(gameBase, GameClientAddresses::CastSkillFunctionRva, GameClientAddresses::CastSendSiteOffset, CastSendSiteSignature, sizeof(CastSendSiteSignature));
			case 20: return AuditCodeSignature(gameBase, GameClientAddresses::PassiveBuffFunctionRva, GameClientAddresses::PassiveBuffSendSiteOffset, PassiveBuffSendSiteSignature, sizeof(PassiveBuffSendSiteSignature));
			case 21: return AuditCodeSignature(gameBase, GameClientAddresses::ChannelCodeFromIndexRva, 0, ChannelCodeFromIndexSignature, sizeof(ChannelCodeFromIndexSignature));
			case 22: return AuditCodeSignature(gameBase, GameClientAddresses::ChannelTypeFromIndexRva, 0, ChannelTypeFromIndexSignature, sizeof(ChannelTypeFromIndexSignature));
			case 23: return AuditCodeSignature(gameBase, GameClientAddresses::ChatGateFunctionRva, 0, ChatGateFunctionSignature, sizeof(ChatGateFunctionSignature));
			case 24: return AuditCodeSignature(gameBase, GameClientAddresses::ChatPackFunctionRva, 0, ChatPackFunctionSignature, sizeof(ChatPackFunctionSignature));
			case 25: return AuditCodeSignature(gameBase, GameClientAddresses::ChatEncodeFunctionRva, 0, ChatEncodeFunctionSignature, sizeof(ChatEncodeFunctionSignature));
			case 26: return AuditCodeSignature(gameBase, GameClientAddresses::ChannelActivateFunctionRva, 0, ChannelActivateFunctionSignature, sizeof(ChannelActivateFunctionSignature));
			case 27: return AuditCodeSignature(gameBase, GameClientAddresses::ChatSendFunctionRva, 0, ChatSendFunctionSignature, sizeof(ChatSendFunctionSignature));
			// Ba mục đăng nhập. Ba hộp thoại chỉ tồn tại ở màn đăng nhập nên lúc đã vào game chúng trả 4 (chưa kết luận
			// được), không phải hỏng — chạy audit ngay sau khi client mở lên mới đọc ra kết quả thật.
			case 28: return AuditGlobalPointer(gameBase, GameClientAddresses::LoginNoticeDialogRva, true);
			case 29: return AuditGlobalPointer(gameBase, GameClientAddresses::LoginServerDialogRva, true);
			case 30: return AuditLoginSubmitFunctions(gameBase);
			default: return 0;
		}
	}

	// Khuôn chung của cả ba hộp thoại đăng nhập, đọc từ handler 280/281/282 của DLL AutoFS:
	//   dialog   = *(gameBase + dialogRva)
	//   control  = *(dialog + controlOffset)          (+0x54 khung chính, +0x970 danh sách máy chủ, +0x106C nút vào game)
	//   receiver = *(control + 0x58)
	//   receiver->vtable[0x10](receiver, eventId, control, parameter)
	// Với sự kiện chọn dòng (0x691) thì AutoFS ghi thẳng chỉ số vào control+0x88 trước khi gọi.
	// Chẩn đoán từng mắt xích của TryDispatchLoginControlEvent thay vì chỉ trả true/false gộp chung.
	// Trả về bitmask; dừng ngay ở mắt xích đầu tiên không đọc được để phân biệt "RVA gốc sai" với
	// "offset +0x54/+0x58/vtable bên dưới sai" - hai loại lỗi khác nhau nhưng cùng biểu hiện KetQua=0.
	//   bit0 (0x01) dialog  != null            bit1 (0x02) dialog  đọc được
	//   bit2 (0x04) control != null            bit3 (0x08) control đọc được
	//   bit4 (0x10) receiver != null           bit5 (0x20) receiver đọc được
	//   bit6 (0x40) receiverVtable đọc được    bit7 (0x80) dispatchEvent thực thi được
	int DiagnoseLoginControlChain(uintptr_t dialogRva, size_t controlOffset, size_t dispatcherOffset = GameClientAddresses::LoginDialogDispatcherOffset) {
		auto gameBase = reinterpret_cast<uint8_t*>(GetModuleHandleA("Game.exe"));
		if (!HasSupportedGameImage(gameBase)) {
			return -1;
		}
		int flags = 0;
		void* dialog = *reinterpret_cast<void**>(gameBase + dialogRva);
		if (dialog != nullptr) flags |= 0x01;
		if (dialog == nullptr || !IsReadableAddress(dialog)) return flags;
		flags |= 0x02;
		void* control = *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(dialog) + controlOffset);
		if (control != nullptr) flags |= 0x04;
		if (control == nullptr || !IsReadableAddress(control)) return flags;
		flags |= 0x08;
		void* receiver = *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(control) + dispatcherOffset);
		if (receiver != nullptr) flags |= 0x10;
		if (receiver == nullptr || !IsReadableAddress(receiver)) return flags;
		flags |= 0x20;
		auto receiverVtable = *reinterpret_cast<uint8_t**>(receiver);
		if (!IsReadableAddress(receiverVtable)) return flags;
		flags |= 0x40;
		auto dispatchEvent = *reinterpret_cast<void**>(receiverVtable + GameClientAddresses::ModalEventMethodVtableOffset);
		if (IsExecutableAddress(dispatchEvent)) flags |= 0x80;
		return flags;
	}

	// Đọc thẳng dword tại (dialog + offset) không diễn giải gì - dùng để quan sát giá trị thật khi
	// DiagnoseLoginControlChain báo một offset ứng viên có vẻ hợp lệ (khác NULL, đọc được).
	// Trả về -1 (int64_t) nếu dialog/gameBase/địa chỉ đích không đọc được - PHÂN BIỆT RÕ với giá trị
	// thật = 0, vì object "Chọn máy chủ" có nhiều vùng đọc ra đúng 0 thật (không phải lỗi đọc).
	int64_t ReadLoginDialogField(uintptr_t dialogRva, size_t offset) {
		auto gameBase = reinterpret_cast<uint8_t*>(GetModuleHandleA("Game.exe"));
		if (!HasSupportedGameImage(gameBase)) {
			return -1;
		}
		void* dialog = *reinterpret_cast<void**>(gameBase + dialogRva);
		if (dialog == nullptr || !IsReadableAddress(dialog)) {
			return -1;
		}
		void* fieldAddress = reinterpret_cast<uint8_t*>(dialog) + offset;
		if (!IsReadableAddress(fieldAddress)) {
			return -1;
		}
		return static_cast<int64_t>(*reinterpret_cast<uint32_t*>(fieldAddress));
	}

	// Giả thuyết thay thế: hộp thoại đăng nhập dùng ĐÚNG khuôn đã xác nhận với modal Npc/Repair
	// (TrySelectDialogOption) - dispatchEvent lấy từ vtable CỦA CHÍNH dialog, gọi thẳng
	// dispatchEvent(dialog, eventId, dialog+controlOffset, 0), không có "receiver" trung gian.
	// Không phụ thuộc controlOffset để hợp lệ (offset chỉ là con trỏ đối số truyền cho hàm game,
	// không bị ta dereference trước) - chỉ cần vtable[0x10] của dialog là địa chỉ thực thi được.
	//   bit0 (0x01) dialogVtable đọc được    bit1 (0x02) dispatchEvent thực thi được
	int DiagnoseSimpleModalDispatch(uintptr_t dialogRva) {
		auto gameBase = reinterpret_cast<uint8_t*>(GetModuleHandleA("Game.exe"));
		if (!HasSupportedGameImage(gameBase)) {
			return -1;
		}
		void* dialog = *reinterpret_cast<void**>(gameBase + dialogRva);
		if (dialog == nullptr || !IsReadableAddress(dialog)) {
			return -2;
		}
		int flags = 0;
		auto dialogVtable = *reinterpret_cast<uint8_t**>(dialog);
		if (!IsReadableAddress(dialogVtable)) return flags;
		flags |= 0x01;
		auto dispatchEvent = *reinterpret_cast<void**>(dialogVtable + GameClientAddresses::ModalEventMethodVtableOffset);
		if (IsExecutableAddress(dispatchEvent)) flags |= 0x02;
		return flags;
	}

	// Gọi thật theo giả thuyết đã xác nhận bằng DiagnoseSimpleModalDispatch: dispatchEvent lấy từ
	// vtable CỦA CHÍNH dialog (giống hệt TrySelectDialogOption cho modal Npc/Repair), không qua
	// "control"/"receiver" trung gian như TryDispatchLoginControlEvent (mô hình đó đã chứng minh sai
	// vì control tại +0x54 luôn đọc ra NULL - xem DiagnoseLoginControlChain KetQua=3 ở lệnh 286).
	bool IsReadableRange(const void* address, size_t length);

	// Ham xu ly cu bam cua CHINH control (RVA 0x24930, __thiscall, 2 tham so, ket thuc bang "ret 8").
	// Truyen toa do = 0 de bo qua hit-test; xem ghi chu day du o GameClientAddresses.h.
	using ControlClickFunction = int(__thiscall*)(void* self, int flag, int packedPoint);

	// Bat/tat o "Dong y Dieu khoan" bang chinh duong xu ly cua client, KHONG gia lap chuot va KHONG ghi thang bo nho.
	bool TryToggleLoginAgreeTerms() {
		auto gameBase = reinterpret_cast<uint8_t*>(GetModuleHandleA("Game.exe"));
		if (!HasSupportedGameImage(gameBase)) {
			return false;
		}
		void* dialog = *reinterpret_cast<void**>(gameBase + GameClientAddresses::LoginCredentialDialogRva);
		if (dialog == nullptr || !IsReadableAddress(dialog)) {
			return false;
		}
		void* control = reinterpret_cast<uint8_t*>(dialog) + GameClientAddresses::LoginAgreeTermsControlOffset;
		if (!IsReadableRange(control, 0x250)) {
			return false;
		}
		auto click = reinterpret_cast<ControlClickFunction>(gameBase + GameClientAddresses::ControlClickFunctionRva);
		if (!IsExecutableAddress(reinterpret_cast<void*>(click))) {
			return false;
		}
		click(control, 1, 0);
		return true;
	}

	// SetText cua lop o nhap (RVA 0x2BEB0). length = -1 de ham tu goi strlen.
	using FieldSetTextFunction = int(__thiscall*)(void* self, const char* text, int length, int flag);

	// Ghi thang noi dung vao HAI O NHAP tren giao dien bang chinh ham cua client, roi de client tu doc lai
	// khi bam "Bat dau tro choi". Khong gia lap ban phim, khong tu dung buffer rieng nhu AutoFS.
	// flag duoc truyen nguyen si xuong ham 0x2C150 ma client goi sau khi ghi chuoi.
	bool TryWriteLoginCredentialFields(const char* account, const char* password, int flag) {
		auto gameBase = reinterpret_cast<uint8_t*>(GetModuleHandleA("Game.exe"));
		if (!HasSupportedGameImage(gameBase)) {
			return false;
		}
		void* dialog = *reinterpret_cast<void**>(gameBase + GameClientAddresses::LoginCredentialDialogRva);
		if (dialog == nullptr || !IsReadableRange(dialog, GameClientAddresses::LoginPasswordFieldOffset + 0x2A0)) {
			return false;
		}
		auto setText = reinterpret_cast<FieldSetTextFunction>(gameBase + GameClientAddresses::FieldSetTextFunctionRva);
		if (!IsExecutableAddress(reinterpret_cast<void*>(setText))) {
			return false;
		}
		void* accountField = reinterpret_cast<uint8_t*>(dialog) + GameClientAddresses::LoginAccountFieldOffset;
		void* passwordField = reinterpret_cast<uint8_t*>(dialog) + GameClientAddresses::LoginPasswordFieldOffset;
		setText(accountField, account, -1, flag);
		setText(passwordField, password, -1, flag);
		return true;
	}

	// Doc MOT byte cua cau thong bao client dang hien o man dang nhap. Tra ve 0..255, 0 la het chuoi,
	// -1 khi chua doc duoc. Doc tung byte vi giao thuc chi tra ve duoc mot so nguyen moi lan goi.
	int ReadLoginStatusMessageByte(int byteIndex) {
		if (byteIndex < 0 || byteIndex >= GameClientAddresses::LoginStatusMessageMaxLength) {
			return -1;
		}
		auto gameBase = reinterpret_cast<uint8_t*>(GetModuleHandleA("Game.exe"));
		if (!HasSupportedGameImage(gameBase)) {
			return -1;
		}
		void* holder = *reinterpret_cast<void**>(gameBase + GameClientAddresses::LoginStatusMessageObjectRva);
		if (holder == nullptr || !IsReadableAddress(holder)) {
			return -1;
		}
		auto text = reinterpret_cast<uint8_t*>(holder) + GameClientAddresses::LoginStatusMessageOffset;
		if (!IsReadableRange(text, GameClientAddresses::LoginStatusMessageMaxLength)) {
			return -1;
		}
		return text[byteIndex];
	}

	// Tra ve 1 neu o "Dong y Dieu khoan" dang duoc tich, 0 neu khong, -1 neu chua doc duoc.
	int ReadLoginAgreeTermsFlag() {
		auto gameBase = reinterpret_cast<uint8_t*>(GetModuleHandleA("Game.exe"));
		if (!HasSupportedGameImage(gameBase)) {
			return -1;
		}
		void* dialog = *reinterpret_cast<void**>(gameBase + GameClientAddresses::LoginCredentialDialogRva);
		if (dialog == nullptr || !IsReadableAddress(dialog)) {
			return -1;
		}
		void* flagSlot = reinterpret_cast<uint8_t*>(dialog) + GameClientAddresses::LoginAgreeTermsFlagOffset;
		if (!IsReadableRange(flagSlot, sizeof(uint16_t))) {
			return -1;
		}
		uint16_t flags = *reinterpret_cast<uint16_t*>(flagSlot);
		return (flags & GameClientAddresses::LoginAgreeTermsFlagMask) != 0 ? 1 : 0;
	}

	bool TryDispatchSimpleModalEvent(uintptr_t dialogRva, int eventId, size_t controlOffset, int parameter,
		bool writeSelection = false) {
		auto gameBase = reinterpret_cast<uint8_t*>(GetModuleHandleA("Game.exe"));
		if (!HasSupportedGameImage(gameBase)) {
			return false;
		}
		void* dialog = *reinterpret_cast<void**>(gameBase + dialogRva);
		if (dialog == nullptr || !IsReadableAddress(dialog)) {
			return false;
		}
		auto dialogVtable = *reinterpret_cast<uint8_t**>(dialog);
		if (!IsReadableAddress(dialogVtable)) {
			return false;
		}
		auto dispatchEvent = reinterpret_cast<ModalEventFunction>(
			*reinterpret_cast<void**>(dialogVtable + GameClientAddresses::ModalEventMethodVtableOffset));
		if (!IsExecutableAddress(reinterpret_cast<void*>(dispatchEvent))) {
			return false;
		}
		void* confirmControl = reinterpret_cast<uint8_t*>(dialog) + controlOffset;
		// AutoFS ghi thang chi so dong dang chon vao control+0x88 truoc khi ban su kien chon (0x691).
		if (writeSelection) {
			void* selectionSlot = reinterpret_cast<uint8_t*>(confirmControl) + GameClientAddresses::LoginListSelectionOffset;
			if (!IsReadableAddress(selectionSlot)) {
				return false;
			}
			*reinterpret_cast<int*>(selectionSlot) = parameter;
		}
		dispatchEvent(dialog, eventId, confirmControl, parameter);
		return true;
	}

	bool TryDispatchLoginControlEvent(uintptr_t dialogRva, size_t controlOffset, int eventId, int parameter, bool writeSelection) {
		auto gameBase = reinterpret_cast<uint8_t*>(GetModuleHandleA("Game.exe"));
		if (!HasSupportedGameImage(gameBase)) {
			return false;
		}
		void* dialog = *reinterpret_cast<void**>(gameBase + dialogRva);
		if (dialog == nullptr || !IsReadableAddress(dialog)) {
			return false;
		}
		void* control = *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(dialog) + controlOffset);
		if (control == nullptr || !IsReadableAddress(control)) {
			return false;
		}
		void* receiver = *reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(control) + GameClientAddresses::LoginDialogDispatcherOffset);
		if (receiver == nullptr || !IsReadableAddress(receiver)) {
			return false;
		}
		auto receiverVtable = *reinterpret_cast<uint8_t**>(receiver);
		if (!IsReadableAddress(receiverVtable)) {
			return false;
		}
		auto dispatchEvent = reinterpret_cast<ModalEventFunction>(
			*reinterpret_cast<void**>(receiverVtable + GameClientAddresses::ModalEventMethodVtableOffset));
		if (!IsExecutableAddress(reinterpret_cast<void*>(dispatchEvent))) {
			return false;
		}
		if (writeSelection) {
			*reinterpret_cast<int*>(reinterpret_cast<uint8_t*>(control) + GameClientAddresses::LoginListSelectionOffset) = parameter;
		}
		dispatchEvent(receiver, eventId, control, parameter);
		return true;
	}

	// Quét toàn ảnh tìm ô toàn cục trỏ tới một đối tượng giao diện mà bộ điều phối sự kiện của nó (vtable[0x10])
	// có chứa phép so sánh với mã sự kiện eventCode. Dùng để tìm hộp thoại mới mà không phải đoán từng ứng viên:
	// mọi hộp đã biết đều so mã sự kiện bằng "cmp dword [ebp+disp8], imm32" = 81 7D ?? <imm32>.
	// Trả về RVA đầu tiên tìm được kể từ startRva, hoặc 0 nếu không còn.
	// Kiểm tra cả một khoảng [address, address+length) có nằm trọn trong một vùng đã cấp phát và đọc được không.
	// Cần thiết vì IsReadableAddress chỉ xét trang chứa byte đầu: đọc 4 byte ở cuối trang mà trang kế chưa cấp phát
	// sẽ gây lỗi truy cập và giết luôn tiến trình game (đã xảy ra thật khi quét cả ảnh 33MB).
	bool IsReadableRange(const void* address, size_t length) {
		MEMORY_BASIC_INFORMATION information{};
		if (address == nullptr || VirtualQuery(address, &information, sizeof(information)) != sizeof(information)) {
			return false;
		}
		if (information.State != MEM_COMMIT || (information.Protect & (PAGE_GUARD | PAGE_NOACCESS)) != 0) {
			return false;
		}
		auto regionEnd = reinterpret_cast<const uint8_t*>(information.BaseAddress) + information.RegionSize;
		return reinterpret_cast<const uint8_t*>(address) + length <= regionEnd;
	}

	uintptr_t ScanForDialogByEvent(uintptr_t startRva, int eventCode) {
		auto gameBase = reinterpret_cast<uint8_t*>(GetModuleHandleA("Game.exe"));
		if (!HasSupportedGameImage(gameBase)) {
			return 0;
		}
		uint32_t imageSize = GetGameImageSize(gameBase);
		constexpr uintptr_t DispatcherSearchLength = 0x300;
		uint8_t* imageEnd = gameBase + imageSize;
		// Duyệt theo VÙNG chứ không gọi VirtualQuery cho từng ô: cả ảnh có 8,2 triệu ô, hỏi từng ô thì một lượt
		// quét vượt quá thời gian chờ của message và làm treo cửa sổ game (đã đo được).
		uintptr_t rva = startRva;
		while (rva + 4 <= imageSize) {
			uint8_t* regionStart = gameBase + rva;
			MEMORY_BASIC_INFORMATION information{};
			if (VirtualQuery(regionStart, &information, sizeof(information)) != sizeof(information)) {
				break;
			}
			uint8_t* regionEnd = reinterpret_cast<uint8_t*>(information.BaseAddress) + information.RegionSize;
			if (regionEnd <= regionStart) {
				break;
			}
			if (information.State != MEM_COMMIT || (information.Protect & (PAGE_GUARD | PAGE_NOACCESS)) != 0) {
				rva = static_cast<uintptr_t>(regionEnd - gameBase);
				continue;
			}
			uint8_t* scanEnd = regionEnd < imageEnd ? regionEnd : imageEnd;
			for (uint8_t* slot = regionStart; slot + 4 <= scanEnd; slot += 4, rva += 4) {
			void* object = *reinterpret_cast<void**>(slot);
			if (object == nullptr || !IsReadableRange(object, 4)) {
				continue;
			}
			// Đối tượng giao diện luôn được cấp phát trên heap; loại các ô trỏ vào trong chính ảnh module,
			// nếu không vùng header/import sẽ cho hàng loạt kết quả trùng giả.
			auto objectBytes = reinterpret_cast<uint8_t*>(object);
			if (objectBytes >= gameBase && objectBytes < gameBase + imageSize) {
				continue;
			}
			auto vtable = *reinterpret_cast<uint8_t**>(object);
			if (vtable < gameBase || vtable + GameClientAddresses::ModalEventMethodVtableOffset + 4 >= gameBase + imageSize) {
				continue;
			}
			if (!IsReadableRange(vtable, GameClientAddresses::ModalEventMethodVtableOffset + 4)) {
				continue;
			}
			auto dispatch = *reinterpret_cast<uint8_t**>(vtable + GameClientAddresses::ModalEventMethodVtableOffset);
			if (dispatch < gameBase || dispatch + DispatcherSearchLength >= gameBase + imageSize) {
				continue;
			}
			if (!IsExecutableAddress(dispatch) || !IsReadableRange(dispatch, DispatcherSearchLength + 8)) {
				continue;
			}
			for (uintptr_t offset = 0; offset < DispatcherSearchLength; offset++) {
				if (dispatch[offset] != 0x81 || dispatch[offset + 1] != 0x7D) {
					continue;
				}
				if (*reinterpret_cast<int*>(dispatch + offset + 3) == eventCode) {
					return rva;
				}
			}
			}
		}
		return 0;
	}

	// Lấy con trỏ danh sách thứ listIndex của hộp "Chọn máy chủ", kèm chính con trỏ hộp thoại.
	bool TryGetLoginServerList(int listIndex, void*& dialog, void*& control) {
		if (listIndex < 0 || listIndex >= GameClientAddresses::LoginServerListCount) {
			return false;
		}
		auto gameBase = reinterpret_cast<uint8_t*>(GetModuleHandleA("Game.exe"));
		if (!HasSupportedGameImage(gameBase)) {
			return false;
		}
		dialog = *reinterpret_cast<void**>(gameBase + GameClientAddresses::LoginServerDialogRva);
		if (dialog == nullptr || !IsReadableAddress(dialog)) {
			return false;
		}
		control = reinterpret_cast<uint8_t*>(dialog) + GameClientAddresses::LoginServerListArrayOffset +
			GameClientAddresses::LoginServerListStride * static_cast<size_t>(listIndex);
		return IsReadableAddress(control);
	}

	// Số dòng hiện có của một danh sách; -1 nếu không đọc được.
	int ReadLoginServerListItemCount(int listIndex) {
		void* dialog = nullptr;
		void* control = nullptr;
		if (!TryGetLoginServerList(listIndex, dialog, control)) {
			return -1;
		}
		void* countSlot = reinterpret_cast<uint8_t*>(control) + GameClientAddresses::ListItemCountOffset;
		if (!IsReadableAddress(countSlot)) {
			return -1;
		}
		return *reinterpret_cast<int*>(countSlot);
	}

	// Chọn một dòng: gọi đúng hàm đặt lựa chọn của client rồi bắn sự kiện chọn dòng cho hộp thoại,
	// giống hệt thứ tự client tự làm khi người chơi bấm chuột (đặt lựa chọn trước, báo cho cha sau).
	bool TryDispatchLoginServerListSelect(int listIndex, int rowIndex) {
		void* dialog = nullptr;
		void* control = nullptr;
		if (!TryGetLoginServerList(listIndex, dialog, control)) {
			return false;
		}
		auto gameBase = reinterpret_cast<uint8_t*>(GetModuleHandleA("Game.exe"));
		auto setSelection = reinterpret_cast<ListSetSelectionFunction>(
			gameBase + GameClientAddresses::ListSetSelectionFunctionRva);
		if (!IsExecutableAddress(reinterpret_cast<void*>(setSelection))) {
			return false;
		}
		auto dialogVtable = *reinterpret_cast<uint8_t**>(dialog);
		if (!IsReadableAddress(dialogVtable)) {
			return false;
		}
		auto dispatchEvent = reinterpret_cast<ModalEventFunction>(
			*reinterpret_cast<void**>(dialogVtable + GameClientAddresses::ModalEventMethodVtableOffset));
		if (!IsExecutableAddress(reinterpret_cast<void*>(dispatchEvent))) {
			return false;
		}
		setSelection(control, rowIndex);
		dispatchEvent(dialog, GameClientAddresses::LoginListSelectEvent, control, rowIndex);
		return true;
	}

	// Lệnh 282 làm ba việc liên tiếp: chọn phân vùng, chọn máy chủ, rồi bấm "Vào trò chơi".
	// Danh sách cụm là số 0 và danh sách máy chủ là số 4 - cả hai đều đã kiểm chứng trên client thật:
	// chọn dòng 2 của danh sách 0 ("Cụm hồi ức 2008") làm màn hình hiện đúng ba máy chủ của cụm đó, và ô đếm
	// của danh sách 4 đổi từ 0 thành 3 đúng lúc ấy.
	bool TryDispatchLoginSelectServer(int partitionIndex, int serverIndex) {
		if (!TryDispatchLoginServerListSelect(GameClientAddresses::LoginPartitionListIndex, partitionIndex)) {
			return false;
		}
		if (!TryDispatchLoginServerListSelect(GameClientAddresses::LoginServerListIndex, serverIndex)) {
			return false;
		}
		return TryDispatchSimpleModalEvent(GameClientAddresses::LoginServerDialogRva,
			GameClientAddresses::ModalConfirmEvent, GameClientAddresses::LoginServerEnterButtonOffset, 0, false);
	}

	// Lệnh 283 KHÔNG gọi game: AutoFS chỉ nối ký tự vào vùng đệm của chính DLL rồi tới lệnh 284 mới gửi cả chuỗi.
	bool TryAppendLoginCharacter(int fieldIndex, char value) {
		char* target = fieldIndex == 0 ? pendingLoginUser : pendingLoginPassword;
		size_t length = 0;
		while (length < GameClientAddresses::LoginCredentialBufferSize && target[length] != '\0') {
			length++;
		}
		if (length + 1 >= GameClientAddresses::LoginCredentialBufferSize) {
			return false;
		}
		target[length] = value;
		target[length + 1] = '\0';
		return true;
	}

	bool TryDispatchLoginSubmit() {
		auto gameBase = reinterpret_cast<uint8_t*>(GetModuleHandleA("Game.exe"));
		if (!HasSupportedGameImage(gameBase)) {
			return false;
		}
		if (AuditLoginSubmitFunctions(gameBase) != 1) {
			return false;
		}
		auto submitLogin = reinterpret_cast<LoginSubmitFunction>(gameBase + GameClientAddresses::LoginSubmitFunctionRva);
		auto cleanupA = reinterpret_cast<LoginCleanupFunction4>(gameBase + GameClientAddresses::LoginAfterSubmitFunctionARva);
		auto cleanupB = reinterpret_cast<LoginCleanupFunction1>(gameBase + GameClientAddresses::LoginAfterSubmitFunctionBRva);
		auto cleanupC = reinterpret_cast<LoginCleanupFunction1>(gameBase + GameClientAddresses::LoginAfterSubmitFunctionCRva);
		submitLogin(gameBase + GameClientAddresses::LoginSubmitContextRva, pendingLoginUser, pendingLoginPassword, 1, 0);
		cleanupA(1, 5, 0, 0);
		cleanupB(0);
		cleanupC(1);
		// AutoFS xoá sạch hai vùng đệm ngay sau khi gửi, không giữ mật khẩu lại trong tiến trình game.
		for (size_t index = 0; index < GameClientAddresses::LoginCredentialBufferSize; index++) {
			pendingLoginUser[index] = '\0';
			pendingLoginPassword[index] = '\0';
		}
		return true;
	}

	LRESULT CALLBACK ReceiverWindowProcedure(HWND window, UINT message, WPARAM wParam, LPARAM lParam) {
		if (message == writeMessage) {
			if (wParam == BeginScriptCommand) {
				pendingScriptLength = 0;
				pendingScript[0] = '\0';
				return 1;
			}
			if (wParam == AppendScriptByteCommand) {
				if (pendingScriptLength >= MaximumScriptLength) return 0;
				pendingScript[pendingScriptLength++] = static_cast<char>(static_cast<uint8_t>(static_cast<uintptr_t>(lParam)));
				pendingScript[pendingScriptLength] = '\0';
				return 1;
			}
			if (wParam == SendChatCommand) {
				return TryDispatchChat(static_cast<int>(lParam)) ? 1 : 0;
			}
			if (wParam == UseInventoryItemCommand) {
				return TryDispatchInventoryItem(static_cast<uint32_t>(lParam));
			}
			if (wParam == PassiveBuffCommand) {
				return TryDispatchPassiveBuff(static_cast<int>(lParam));
			}
			if (wParam == NativeBuildStampCommand) {
				return NativeBuildStamp;
			}
			if (wParam == AuditAddressCommand) {
				int index = static_cast<int>(lParam);
				return index < 0 ? AuditEntryCount : AuditAddress(index);
			}
			if (wParam == MovementXCommand) {
				int dispatchX = static_cast<int>(lParam);
				if (dispatchX <= 0) return 0;
				pendingMovementX = dispatchX;
				return 1;
			}
			if (wParam == MovementYCommand) {
				return TryDispatchMovementY(static_cast<int>(lParam)) ? 1 : 0;
			}
			if (wParam == WalkToCommand) {
				return TryDispatchWalkTo(static_cast<int>(lParam)) ? 1 : 0;
			}
			if (wParam == DialogOptionCommand) {
				return TrySelectDialogOption(static_cast<int>(lParam)) ? 1 : 0;
			}
			if (wParam == SelectGroundItemCommand) {
				return TrySelectGroundItem(static_cast<int>(lParam)) ? 1 : 0;
			}
			if (wParam == PortalCommand) {
				return TryDispatchPortal(static_cast<int>(lParam)) ? 1 : 0;
			}
			if (wParam == ResetActionCommand) {
				bool movementReset = TryDispatchMovementReset();
				bool pickupReset = TryDispatchResetPickup();
				return movementReset || pickupReset ? 1 : 0;
			}
			if (wParam == ReturnToTownCommand) {
				return TryDispatchReturnToTown(static_cast<int>(lParam)) ? 1 : 0;
			}
			if (wParam == SaleCommand) {
				return TryDispatchSale(static_cast<int>(lParam)) ? 1 : 0;
			}
			if (wParam == PickupCommand) {
				return TryDispatchPickup(static_cast<int>(lParam)) ? 1 : 0;
			}
			if (wParam == CastSkillCommand) {
				return TryDispatchCastSkill(static_cast<int>(lParam));
			}
			if (wParam == LoginNoticeCommand) {
				return TryDispatchSimpleModalEvent(GameClientAddresses::LoginNoticeDialogRva,
					GameClientAddresses::ModalConfirmEvent, GameClientAddresses::LoginConfirmControlOffset, 0) ? 1 : 0;
			}
			if (wParam == LoginVersionCommand) {
				return TryDispatchSimpleModalEvent(GameClientAddresses::LoginVersionDialogRva,
					GameClientAddresses::ModalConfirmEvent, GameClientAddresses::LoginConfirmControlOffset, 0) ? 1 : 0;
			}
			if (wParam == LoginSelectServerCommand) {
				uint32_t packedServer = static_cast<uint32_t>(lParam);
				int partitionIndex = static_cast<int16_t>(packedServer & 0xFFFF);
				int serverIndex = static_cast<int16_t>(packedServer >> 16);
				return TryDispatchLoginSelectServer(partitionIndex, serverIndex) ? 1 : 0;
			}
			if (wParam == LoginTypeCharacterCommand) {
				uint32_t packedCharacter = static_cast<uint32_t>(lParam);
				int fieldIndex = static_cast<int16_t>(packedCharacter & 0xFFFF);
				char value = static_cast<char>(static_cast<uint8_t>(packedCharacter >> 16));
				return TryAppendLoginCharacter(fieldIndex, value) ? 1 : 0;
			}
			if (wParam == LoginSubmitCommand) {
				return TryDispatchLoginSubmit() ? 1 : 0;
			}
			if (wParam == LoginProbeDialogCommand) {
				return TryDispatchLoginControlEvent(static_cast<uintptr_t>(static_cast<uint32_t>(lParam)),
					GameClientAddresses::LoginDialogFrameOffset, GameClientAddresses::ModalConfirmEvent, 0, false) ? 1 : 0;
			}
			if (wParam == LoginDiagnoseChainCommand) {
				return static_cast<LRESULT>(DiagnoseLoginControlChain(
					static_cast<uintptr_t>(static_cast<uint32_t>(lParam)), GameClientAddresses::LoginDialogFrameOffset));
			}
			if (wParam == LoginScanOffsetCommand) {
				return static_cast<LRESULT>(DiagnoseLoginControlChain(
					GameClientAddresses::LoginNoticeDialogRva, static_cast<size_t>(static_cast<uint32_t>(lParam))));
			}
			if (wParam == LoginReadDialogFieldCommand) {
				return static_cast<LRESULT>(ReadLoginDialogField(
					GameClientAddresses::LoginNoticeDialogRva, static_cast<size_t>(static_cast<uint32_t>(lParam))));
			}
			if (wParam == LoginScanDispatcherCommand) {
				// controlOffset = 0x278 la ung vien tim duoc bang LoginScanOffsetCommand (287) tren dialog Khuyen cao,
				// thay cho 0x54 cua AutoFS da loi thoi. Quet dispatcherOffset thay cho 0x58 co dinh.
				return static_cast<LRESULT>(DiagnoseLoginControlChain(
					GameClientAddresses::LoginNoticeDialogRva, 0x278, static_cast<size_t>(static_cast<uint32_t>(lParam))));
			}
			if (wParam == LoginScanEmbeddedControlCommand) {
				return static_cast<LRESULT>(DiagnoseSimpleModalDispatch(GameClientAddresses::LoginNoticeDialogRva));
			}
			if (wParam == LoginSimpleModalConfirmCommand) {
				// lParam = controlOffset dung de thu (confirmControl = dialog + controlOffset). Dung de do
				// tim offset dung bang thu nghiem that, khong phai gia dinh - quan sat man hinh sau moi lan goi.
				return TryDispatchSimpleModalEvent(GameClientAddresses::LoginNoticeDialogRva,
					GameClientAddresses::ModalConfirmEvent, static_cast<size_t>(static_cast<uint32_t>(lParam)), 0) ? 1 : 0;
			}
			if (wParam == LoginSimpleModalConfirmVersionCommand) {
				// Thu nghiem cung mo hinh/offset 0x278 tren dialog 2 (Thong tin phien ban). lParam = controlOffset.
				return TryDispatchSimpleModalEvent(GameClientAddresses::LoginVersionDialogRva,
					GameClientAddresses::ModalConfirmEvent, static_cast<size_t>(static_cast<uint32_t>(lParam)), 0) ? 1 : 0;
			}
			if (wParam == DiagnoseSimpleModalCommand) {
				return static_cast<LRESULT>(DiagnoseSimpleModalDispatch(static_cast<uintptr_t>(static_cast<uint32_t>(lParam))));
			}
			if (wParam == SimpleModalConfirmAnyCommand) {
				// controlOffset co dinh = 0x278 (da xac nhan dung cho dialog 1/2), lParam = dialogRva can thu.
				return TryDispatchSimpleModalEvent(static_cast<uintptr_t>(static_cast<uint32_t>(lParam)),
					GameClientAddresses::ModalConfirmEvent, GameClientAddresses::LoginConfirmControlOffset, 0) ? 1 : 0;
			}
			if (wParam == LoginStartButtonCommand) {
				return TryDispatchSimpleModalEvent(GameClientAddresses::LoginCredentialDialogRva,
					GameClientAddresses::ModalConfirmEvent, GameClientAddresses::LoginStartButtonOffset, 0, false) ? 1 : 0;
			}
			if (wParam == LoginToggleAgreeTermsCommand) {
				return TryToggleLoginAgreeTerms() ? 1 : 0;
			}
			if (wParam == LoginWriteCredentialFieldsCommand) {
				return TryWriteLoginCredentialFields(pendingLoginUser, pendingLoginPassword,
					static_cast<int>(lParam)) ? 1 : 0;
			}
			if (wParam == LoginClearPendingCredentialsCommand) {
				for (size_t index = 0; index < GameClientAddresses::LoginCredentialBufferSize; index++) {
					pendingLoginUser[index] = '\0';
					pendingLoginPassword[index] = '\0';
				}
				return 1;
			}
			if (wParam == LoginReadStatusMessageByteCommand) {
				return static_cast<LRESULT>(ReadLoginStatusMessageByte(static_cast<int>(lParam)));
			}
			if (wParam == LoginReadAgreeTermsCommand) {
				return static_cast<LRESULT>(ReadLoginAgreeTermsFlag());
			}
			if (wParam == CheckDialogEventPatternCommand) {
				// Chay dung cac buoc cua ScanForDialogByEvent nhung chi cho MOT rva, tra ve bitmask de biet
				// mat xich nao hong: 1=slot doc duoc, 2=object khac null+doc duoc, 4=object nam ngoai anh (heap),
				// 8=vtable trong anh, 0x10=dispatch trong anh va thuc thi duoc, 0x20=tim thay mau so ma su kien.
				auto gameBase = reinterpret_cast<uint8_t*>(GetModuleHandleA("Game.exe"));
				if (!HasSupportedGameImage(gameBase)) return -1;
				uint32_t imageSize = GetGameImageSize(gameBase);
				uintptr_t rva = static_cast<uintptr_t>(static_cast<uint32_t>(lParam));
				int flags = 0;
				void* slot = gameBase + rva;
				if (!IsReadableAddress(slot)) return flags;
				flags |= 1;
				void* object = *reinterpret_cast<void**>(slot);
				if (object == nullptr || !IsReadableAddress(object)) return flags;
				flags |= 2;
				auto objectBytes = reinterpret_cast<uint8_t*>(object);
				if (objectBytes >= gameBase && objectBytes < gameBase + imageSize) return flags;
				flags |= 4;
				auto vtable = *reinterpret_cast<uint8_t**>(object);
				if (vtable < gameBase || vtable + GameClientAddresses::ModalEventMethodVtableOffset + 4 >= gameBase + imageSize) return flags;
				if (!IsReadableAddress(vtable)) return flags;
				flags |= 8;
				auto dispatch = *reinterpret_cast<uint8_t**>(vtable + GameClientAddresses::ModalEventMethodVtableOffset);
				if (dispatch < gameBase || dispatch + 0x300 >= gameBase + imageSize || !IsExecutableAddress(dispatch)) return flags;
				flags |= 0x10;
				for (uintptr_t offset = 0; offset < 0x300; offset++) {
					if (dispatch[offset] == 0x81 && dispatch[offset + 1] == 0x7D &&
						*reinterpret_cast<int*>(dispatch + offset + 3) == GameClientAddresses::ModalConfirmEvent) {
						flags |= 0x20;
						break;
					}
				}
				return flags;
			}
			if (wParam == ScanDialogByEventCommand) {
				return static_cast<LRESULT>(ScanForDialogByEvent(
					static_cast<uintptr_t>(static_cast<uint32_t>(lParam)), GameClientAddresses::ModalConfirmEvent));
			}
			if (wParam == ReadServerListCountCommand) {
				return static_cast<LRESULT>(ReadLoginServerListItemCount(static_cast<int>(lParam)));
			}
			if (wParam == ProbeServerListSelectCommand) {
				// lParam dong goi: (listIndex & 0xFFFF) | (rowIndex << 16).
				uint32_t packed = static_cast<uint32_t>(lParam);
				int listIndex = static_cast<int16_t>(packed & 0xFFFF);
				int rowIndex = static_cast<int16_t>(packed >> 16);
				return TryDispatchLoginServerListSelect(listIndex, rowIndex) ? 1 : 0;
			}
			if (wParam == GetDialogVtableRvaCommand || wParam == GetDialogDispatcherRvaCommand) {
				// Tra ve RVA cua vtable (301) hoac cua vtable[0x10] (302) cho dialog tai lParam.
				// Dung de nhan dien LOP: hai object cung lop se cung vtable/dispatcher -> dung chung kieu offset.
				// Tra -1 neu khong doc duoc, -2 neu dia chi nam ngoai anh Game.exe.
				auto gameBase = reinterpret_cast<uint8_t*>(GetModuleHandleA("Game.exe"));
				if (!HasSupportedGameImage(gameBase)) return -1;
				void* dialog = *reinterpret_cast<void**>(gameBase + static_cast<uintptr_t>(static_cast<uint32_t>(lParam)));
				if (dialog == nullptr || !IsReadableAddress(dialog)) return -1;
				auto dialogVtable = *reinterpret_cast<uint8_t**>(dialog);
				if (!IsReadableAddress(dialogVtable)) return -1;
				uint8_t* target = wParam == GetDialogVtableRvaCommand
					? dialogVtable
					: *reinterpret_cast<uint8_t**>(dialogVtable + GameClientAddresses::ModalEventMethodVtableOffset);
				uint32_t imageSize = GetGameImageSize(gameBase);
				if (target < gameBase || target >= gameBase + imageSize) return -2;
				return static_cast<LRESULT>(target - gameBase);
			}
			if (wParam == SetProbeDialogRvaCommand) {
				probeDialogRva = static_cast<uintptr_t>(static_cast<uint32_t>(lParam));
				return 1;
			}
			if (wParam == ProbeDispatchOffsetCommand) {
				// Ban su kien "bam nut" (0x565) voi control = probeDialog + lParam. Quan sat man hinh sau moi lan.
				if (probeDialogRva == 0) return 0;
				return TryDispatchSimpleModalEvent(probeDialogRva, GameClientAddresses::ModalConfirmEvent,
					static_cast<size_t>(static_cast<uint32_t>(lParam)), 0, false) ? 1 : 0;
			}
			if (wParam == ProbeListSelectOffsetCommand) {
				// Ban su kien "chon dong" (0x691): lParam dong goi (offset & 0xFFFF) | (rowIndex << 16).
				if (probeDialogRva == 0) return 0;
				uint32_t packed = static_cast<uint32_t>(lParam);
				size_t controlOffset = static_cast<size_t>(packed & 0xFFFF);
				int rowIndex = static_cast<int16_t>(packed >> 16);
				return TryDispatchSimpleModalEvent(probeDialogRva, GameClientAddresses::LoginListSelectEvent,
					controlOffset, rowIndex, true) ? 1 : 0;
			}
			if (wParam == ReadAnyDialogFieldCommand) {
				// dialogRva co dinh = 0x4EEFAC (LoginServerDialogRva moi, da xac nhan bang trang thai
				// mo/dong that ngay 2026-09-16). lParam = offset can doc de xem cau truc that cua object.
				return static_cast<LRESULT>(ReadLoginDialogField(0x4EEFAC, static_cast<size_t>(static_cast<uint32_t>(lParam))));
			}
			if (wParam == ProbeGlobalPointerCommand) {
				// lParam = RVA bat ky can kiem tra (chi doc, khong dispatch gi). Dung AuditGlobalPointer da co san:
				// 2=hop le, 4=NULL (chua ket luan), 13=khong doc duoc, 20=anh khong ho tro.
				auto gameBase = reinterpret_cast<uint8_t*>(GetModuleHandleA("Game.exe"));
				if (!HasSupportedGameImage(gameBase)) {
					return 20;
				}
				return AuditGlobalPointer(gameBase, static_cast<uintptr_t>(static_cast<uint32_t>(lParam)), true);
			}
			if (wParam == AttackCommand) {
				uint32_t packedTarget = static_cast<uint32_t>(lParam);
				uint16_t targetIndex = static_cast<uint16_t>(packedTarget & 0xFFFF);
				uint16_t targetType = static_cast<uint16_t>(packedTarget >> 16);
				return TryDispatchAttack(targetIndex, targetType) ? 1 : 0;
			}
		}
		return originalWindowProcedure != nullptr
			? CallWindowProcA(originalWindowProcedure, window, message, wParam, lParam)
			: DefWindowProcA(window, message, wParam, lParam);
	}

	bool InstallReceiver(HWND window) {
		if (!IsWindow(window)) {
			return false;
		}
		if (hookedWindow == window && originalWindowProcedure != nullptr) {
			return true;
		}
		writeMessage = RegisterWindowMessageA(WriteMessageName);
		WNDPROC previous = reinterpret_cast<WNDPROC>(SetWindowLongPtrA(window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(ReceiverWindowProcedure)));
		if (previous == nullptr) {
			return false;
		}
		hookedWindow = window;
		originalWindowProcedure = previous;
		pendingScriptLength = 0;
		pendingScript[0] = '\0';
		return true;
	}

	bool RemoveReceiver(HWND window) {
		if (window == nullptr || window != hookedWindow || originalWindowProcedure == nullptr || !IsWindow(window)) {
			return false;
		}
		SetWindowLongPtrA(window, GWLP_WNDPROC, reinterpret_cast<LONG_PTR>(originalWindowProcedure));
		pendingScriptLength = 0;
		pendingScript[0] = '\0';
		hookedWindow = nullptr;
		originalWindowProcedure = nullptr;
		return true;
	}

	LRESULT CALLBACK InjectionHookProcedure(int code, WPARAM wParam, LPARAM lParam) {
		if (code >= 0 && lParam != 0) {
			auto message = reinterpret_cast<CWPSTRUCT*>(lParam);
			if (message->message == RegisterWindowMessageA(HookMessageName)) {
				HHOOK hook = reinterpret_cast<HHOOK>(message->wParam);
				if (hook != nullptr) {
					UnhookWindowsHookEx(hook);
				}
				if (message->lParam != 0) {
					if (!retainedInGameProcess) {
						char modulePath[MAX_PATH]{};
						if (GetModuleFileNameA(moduleHandle, modulePath, MAX_PATH) != 0 && LoadLibraryA(modulePath) != nullptr) {
							retainedInGameProcess = true;
						}
					}
					InstallReceiver(message->hwnd);
				} else {
					RemoveReceiver(message->hwnd);
				}
				return 1;
			}
		}
		return CallNextHookEx(nullptr, code, wParam, lParam);
	}
}

extern "C" __declspec(dllexport) UINT __stdcall GetMsg() {
	if (writeMessage == 0) {
		writeMessage = RegisterWindowMessageA(WriteMessageName);
	}
	return writeMessage;
}

extern "C" __declspec(dllexport) int __stdcall InjectDll(HWND gameWindow) {
	if (!IsWindow(gameWindow)) {
		return 0;
	}
	DWORD processId = 0;
	DWORD threadId = GetWindowThreadProcessId(gameWindow, &processId);
	if (threadId == 0 || processId == 0) {
		return 0;
	}
	hookMessage = RegisterWindowMessageA(HookMessageName);
	injectionHook = SetWindowsHookExA(WH_CALLWNDPROC, InjectionHookProcedure, moduleHandle, threadId);
	if (injectionHook == nullptr) {
		return 0;
	}
	SendMessageA(gameWindow, hookMessage, reinterpret_cast<WPARAM>(injectionHook), 1);
	injectionHook = nullptr;
	return 1;
}

extern "C" __declspec(dllexport) int __stdcall UnmapDll(HWND gameWindow) {
	if (!IsWindow(gameWindow)) {
		return 0;
	}
	DWORD processId = 0;
	DWORD threadId = GetWindowThreadProcessId(gameWindow, &processId);
	if (threadId == 0 || processId == 0) {
		return 0;
	}
	hookMessage = RegisterWindowMessageA(HookMessageName);
	injectionHook = SetWindowsHookExA(WH_CALLWNDPROC, InjectionHookProcedure, moduleHandle, threadId);
	if (injectionHook == nullptr) {
		return 0;
	}
	SendMessageA(gameWindow, hookMessage, reinterpret_cast<WPARAM>(injectionHook), 0);
	injectionHook = nullptr;
	return 1;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
	if (reason == DLL_PROCESS_ATTACH) {
		moduleHandle = instance;
		DisableThreadLibraryCalls(instance);
	}
	return TRUE;
}
