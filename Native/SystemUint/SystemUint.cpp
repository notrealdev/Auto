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
	constexpr WPARAM ExecuteScriptCommand = 22;
	constexpr WPARAM AppendScriptByteCommand = 34;
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

	using PrepareFunction = void(__thiscall*)(void*, int, void*, int);
	using DialogOptionFunction = int(__thiscall*)(void*, int, int, int);
	using ModalEventFunction = int(__thiscall*)(void*, int, void*, int);
	using AttackFunction = int(__thiscall*)(void*, int, uintptr_t);
	using SelectGroundItemFunction = int(__thiscall*)(void*, int);
	using GroundCoordinateConverterFunction = void(__thiscall*)(void*, int*, int*);
	using ResetPickupFunction = void(__thiscall*)(void*);
	using PickupMovementFunction = void(__thiscall*)(void*, int, int, int, int);
	using CastSkillFunction = void(__cdecl*)(int, int, int);
	using PickupFunction = void(__cdecl*)(int, int);
	using ReturnToTownFunction = void(__thiscall*)(void*, int);
	using SaleFunction = int(__thiscall*)(void*, int, void*, int);
	using CoordinateDispatcherFunction = int(__thiscall*)(void*, int, int, int);
	using GenericDispatchFunction = int(__thiscall*)(void*, int, void*, int);
	using QueryFunction = int(__thiscall*)(void*, int, uintptr_t, uintptr_t);
	using InventoryCoordinateFunction = void(__cdecl*)(int*, int*);
	using ScriptExecuteFunction = int(__thiscall*)(void*, const char*);
	using ChatSendFunction = int(__cdecl*)(int, const char*, int, int);
	constexpr uint8_t AttackWriterSignature[] = {
		0xC7, 0x81, 0x94, 0xD7, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
		0x89, 0xB9, 0x98, 0xD7, 0x00, 0x00,
		0xC6, 0x81, 0xA0, 0xD7, 0x00, 0x00, 0x01
	};
	constexpr uint8_t ResetPickupFunctionSignature[] = {
		0x83, 0xEC, 0x08, 0x56, 0x8B, 0xF1
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
	constexpr uint8_t ReturnToTownFunctionSignature[] = {
		0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x0C, 0x89, 0x4D, 0xFC
	};
	constexpr uint8_t InventoryCoordinateFunctionSignature[] = {
		0x55, 0x8B, 0xEC
	};
	constexpr uint8_t ScriptExecuteFunctionSignature[] = {
		0x55, 0x8B, 0xEC, 0x81, 0xEC, 0x88, 0x00, 0x00, 0x00, 0x89, 0x4D, 0xF0
	};
	constexpr uint8_t ChatSendFunctionSignature[] = {
		0x55, 0x8B, 0xEC, 0x81, 0xEC, 0x74, 0x01, 0x00, 0x00
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

	// Xác nhận một call E8 module-relative vẫn trỏ tới đúng hàm đã phân tích trên client hiện tại.
	bool IsRelativeCallTarget(uint8_t* callSite, const void* target) {
		return callSite != nullptr && target != nullptr && *callSite == 0xE8 &&
			reinterpret_cast<const void*>(callSite + 5 + *reinterpret_cast<int32_t*>(callSite + 1)) == target;
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

	bool TrySelectDialogOption(int optionIndex) {
		if (optionIndex < -1 || optionIndex > 9) {
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
	bool TryDispatchCastSkill(int skillId) {
		if (skillId <= 0 || skillId > UINT16_MAX) {
			return false;
		}

		auto gameBase = reinterpret_cast<uint8_t*>(GetModuleHandleA("Game.exe"));
		if (!HasSupportedGameImage(gameBase)) {
			return false;
		}
		auto buffAction = reinterpret_cast<PickupMovementFunction>(gameBase + GameClientAddresses::BuffActionFunctionRva);
		auto castSkill  = reinterpret_cast<CastSkillFunction>(gameBase + GameClientAddresses::CastSkillFunctionRva);
		void* entityTable = *reinterpret_cast<void**>(gameBase + GameClientAddresses::EntityTableRva);
		if (!IsExecutableAddress(reinterpret_cast<void*>(buffAction)) ||
			!IsExecutableAddress(reinterpret_cast<void*>(castSkill)) ||
			entityTable == nullptr) {
			return false;
		}

		// Xác nhận hai RVA bằng chính call graph của function skill cấp cao đã phân tích.
		auto buffActionCall = gameBase + GameClientAddresses::BuffActionCallRva;
		auto castSkillCall  = gameBase + GameClientAddresses::CastSkillCallRva;
		if (*buffActionCall != 0xE8 || *castSkillCall != 0xE8 ||
			buffActionCall + 5 + *reinterpret_cast<int32_t*>(buffActionCall + 1) != reinterpret_cast<uint8_t*>(buffAction) ||
			castSkillCall + 5 + *reinterpret_cast<int32_t*>(castSkillCall + 1) != reinterpret_cast<uint8_t*>(castSkill)) {
			return false;
		}
		void* playerEntity = reinterpret_cast<uint8_t*>(entityTable) +
			GameClientAddresses::PlayerEntityIndex * GameClientAddresses::EntityStride;
		buffAction(playerEntity, 5, skillId, 0, 0);
		castSkill(skillId, 0, 0);
		return true;
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
	bool TryDispatchInventoryItem(uint32_t packedItem) {
		int memoryIndex = static_cast<int>(packedItem & 0x3F);
		int container = static_cast<int>((packedItem >> 6) & 0x1F);
		int expectedItemId = static_cast<int>(packedItem >> 11);
		uintptr_t slotListPointerOffset = 0;
		int slotCount = 0;
		if (container == 11) {
			slotListPointerOffset = GameClientAddresses::InventoryQuickSlotListPointerOffset;
			slotCount = GameClientAddresses::InventoryQuickSlotCount;
		} else if (container == 3) {
			slotListPointerOffset = GameClientAddresses::InventorySlotListPointerOffset;
			slotCount = GameClientAddresses::InventorySlotCount;
		} else if (container == 16) {
			slotListPointerOffset = GameClientAddresses::InventoryExtendedSlotListPointerOffset;
			slotCount = GameClientAddresses::InventoryExtendedSlotCount;
		}
		if (memoryIndex < 0 || memoryIndex >= slotCount || expectedItemId <= 0) {
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
		void* inventoryRoot = *reinterpret_cast<void**>(gameBase + GameClientAddresses::InventoryRootRva);
		if (inventoryRoot == nullptr) {
			return false;
		}
		void* inventoryObject = reinterpret_cast<uint8_t*>(inventoryRoot) + GameClientAddresses::InventoryObjectOffset;
		auto slotList = *reinterpret_cast<int**>(reinterpret_cast<uint8_t*>(inventoryObject) + slotListPointerOffset);
		if (slotList == nullptr || slotList[memoryIndex] != expectedItemId) {
			return false;
		}
		auto query = reinterpret_cast<QueryFunction>(
			*reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(managerVtable) + GameClientAddresses::PrepareMethodVtableOffset));
		auto dispatch = reinterpret_cast<GenericDispatchFunction>(
			*reinterpret_cast<void**>(reinterpret_cast<uint8_t*>(managerVtable) + GameClientAddresses::DialogOptionMethodVtableOffset));
		auto getCoordinates = reinterpret_cast<InventoryCoordinateFunction>(gameBase + GameClientAddresses::InventoryCoordinateFunctionRva);
		auto coordinateCall = gameBase + GameClientAddresses::InventoryCoordinateCallRva;
		if (!IsExecutableAddress(reinterpret_cast<void*>(query)) ||
			!IsExecutableAddress(reinterpret_cast<void*>(dispatch)) ||
			!IsExecutableAddress(reinterpret_cast<void*>(getCoordinates)) ||
			memcmp(reinterpret_cast<void*>(getCoordinates), InventoryCoordinateFunctionSignature, sizeof(InventoryCoordinateFunctionSignature)) != 0 ||
			!IsRelativeCallTarget(coordinateCall, reinterpret_cast<void*>(getCoordinates))) {
			return false;
		}
		if (query(manager, GameClientAddresses::InventoryUsePrepareOpcode, container, 0) != 0) {
			return false;
		}
		int coordinateX = 0;
		int coordinateY = 0;
		getCoordinates(&coordinateX, &coordinateY);
		uint32_t descriptor[7]{
			static_cast<uint32_t>(expectedItemId),
			static_cast<uint32_t>(container),
			static_cast<uint32_t>(memoryIndex / 5),
			static_cast<uint32_t>(memoryIndex % 5),
			0,
			0,
			container == 11 ? 3U : 5U
		};
		int packedCoordinates = static_cast<int>((static_cast<uint32_t>(coordinateX) & 0xFFFFU) |
			((static_cast<uint32_t>(coordinateY) & 0xFFFFU) << 16));
		dispatch(manager, GameClientAddresses::InventoryUseOpcode, descriptor, packedCoordinates);
		return true;
	}

	// Thực thi script bằng cùng context và call graph mà hàm Chat của client đang dùng.
	bool TryExecutePendingScript() {
		if (pendingScriptLength == 0 || pendingScriptLength > MaximumScriptLength) {
			pendingScriptLength = 0;
			pendingScript[0] = '\0';
			return false;
		}
		auto gameBase = reinterpret_cast<uint8_t*>(GetModuleHandleA("Game.exe"));
		if (!HasSupportedGameImage(gameBase)) {
			pendingScriptLength = 0;
			pendingScript[0] = '\0';
			return false;
		}
		auto executeScript = reinterpret_cast<ScriptExecuteFunction>(gameBase + GameClientAddresses::ScriptExecuteFunctionRva);
		auto executeCall = gameBase + GameClientAddresses::ScriptExecuteCallRva;
		if (!IsExecutableAddress(reinterpret_cast<void*>(executeScript)) ||
			memcmp(reinterpret_cast<void*>(executeScript), ScriptExecuteFunctionSignature, sizeof(ScriptExecuteFunctionSignature)) != 0 ||
			!IsRelativeCallTarget(executeCall, reinterpret_cast<void*>(executeScript))) {
			pendingScriptLength = 0;
			pendingScript[0] = '\0';
			return false;
		}
		pendingScript[pendingScriptLength] = '\0';
		int result = executeScript(gameBase + GameClientAddresses::ScriptContextRva, pendingScript);
		pendingScriptLength = 0;
		pendingScript[0] = '\0';
		return result != 0;
	}

	// Gửi nội dung qua đúng FUN_004b85d0 mà handler chat hiện tại gọi sau bước Lua.
	bool TryDispatchChat(int channelId) {
		if (channelId < 0 || pendingScriptLength == 0 || pendingScriptLength > MaximumScriptLength) {
			pendingScriptLength = 0;
			pendingScript[0] = '\0';
			return false;
		}
		auto gameBase = reinterpret_cast<uint8_t*>(GetModuleHandleA("Game.exe"));
		if (!HasSupportedGameImage(gameBase)) {
			pendingScriptLength = 0;
			pendingScript[0] = '\0';
			return false;
		}
		auto sendChat = reinterpret_cast<ChatSendFunction>(gameBase + GameClientAddresses::ChatSendFunctionRva);
		if (!IsExecutableAddress(reinterpret_cast<void*>(sendChat)) ||
			memcmp(reinterpret_cast<void*>(sendChat), ChatSendFunctionSignature, sizeof(ChatSendFunctionSignature)) != 0) {
			pendingScriptLength = 0;
			pendingScript[0] = '\0';
			return false;
		}
		pendingScript[pendingScriptLength] = '\0';
		int result = sendChat(channelId, pendingScript, static_cast<int>(pendingScriptLength), -1);
		pendingScriptLength = 0;
		pendingScript[0] = '\0';
		return result >= 0;
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
			if (wParam == ExecuteScriptCommand) {
				return TryExecutePendingScript() ? 1 : 0;
			}
			if (wParam == SendChatCommand) {
				return TryDispatchChat(static_cast<int>(lParam)) ? 1 : 0;
			}
			if (wParam == UseInventoryItemCommand) {
				return TryDispatchInventoryItem(static_cast<uint32_t>(lParam)) ? 1 : 0;
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
				return TryDispatchCastSkill(static_cast<int>(lParam)) ? 1 : 0;
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
