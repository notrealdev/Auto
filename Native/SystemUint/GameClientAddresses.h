#pragma once

#include <windows.h>
#include <stdint.h>

namespace GameClientAddresses {
	constexpr uint32_t MinimumSupportedImageSize = 0x01FC0000;
	constexpr uintptr_t AttackManagerRva = 0x004E0640;
	constexpr uintptr_t ExpectedManagerVtableRva = 0x00476618;
	constexpr int CoordinateOpcode = 0x9F;
	constexpr size_t CoordinateDispatcherMethodVtableOffset = 0x10;
	constexpr size_t DialogOptionMethodVtableOffset = 0x10;
	constexpr int DialogOptionOpcode = 9;
	constexpr uintptr_t ModalStateRva = 0x004ECF68;
	constexpr size_t ModalEventMethodVtableOffset = 0x10;
	constexpr uintptr_t NpcConfirmModalVtableRva = 0x00469E34;
	constexpr uintptr_t NpcConfirmControlOffset = 0x00000248;
	constexpr uintptr_t RepairConfirmModalVtableRva = 0x004721B4;
	constexpr uintptr_t RepairConfirmControlOffset = 0x00001174;
	constexpr int ModalConfirmEvent = 0x565;
	constexpr size_t PrepareMethodVtableOffset = 0x74;
	constexpr size_t AttackMethodVtableOffset = 0x40;
	constexpr int AttackPrepareOpcode = 9;
	constexpr size_t SelectGroundItemMethodVtableOffset = 0x48;
	constexpr size_t SaleMethodVtableOffset = 0x10;
	constexpr int SaleOpcode = 0x19;
	constexpr uintptr_t InventoryRootRva = 0x00A7DE04;
	constexpr uintptr_t InventoryObjectOffset = 0x0004B7BC;
	constexpr uintptr_t InventorySlotListPointerOffset = 0x00000000;
	constexpr int InventorySlotCount = 35;
	constexpr uintptr_t InventoryQuickSlotListPointerOffset = 0x00000140;
	constexpr int InventoryQuickSlotCount = 4;
	constexpr uintptr_t InventoryExtendedSlotListPointerOffset = 0x00000208;
	constexpr int InventoryExtendedSlotCount = 35;
	constexpr int InventoryContainerType = 3;
	constexpr int InventoryUseOpcode = 10;
	constexpr int InventoryUsePrepareOpcode = 0x12A;
	constexpr uintptr_t InventoryCoordinateFunctionRva = 0x00044C70;
	constexpr uintptr_t InventoryCoordinateCallRva = 0x001B0F5B;
	constexpr uintptr_t EntityTableRva = 0x0095DF40;
	constexpr uintptr_t PlayerEntityIndex = 1;
	constexpr uintptr_t EntityStride = 0x0000D87C;
	constexpr uintptr_t GroundRecordTablePointerRva = 0x0054BCA0;
	constexpr uintptr_t GroundRecordStride = 0x000003A4;
	constexpr uintptr_t GroundRecordIdOffset = 0x00000014;
	constexpr uintptr_t GroundRecordStateOffset = 0x0000001C;
	constexpr uintptr_t GroundCoordinateConverterRva = 0x002F9E00;
	constexpr uintptr_t ResetPickupFunctionRva = 0x00345020;
	constexpr uintptr_t PickupMovementFunctionRva = 0x0031CE80;
	constexpr uintptr_t BuffActionFunctionRva = 0x0031CE80;
	constexpr uintptr_t BuffActionCallRva = 0x002C9A2E;
	constexpr uintptr_t CastSkillFunctionRva = 0x003AA360;
	constexpr uintptr_t CastSkillCallRva = 0x002C9A46;
	constexpr uintptr_t PickupFunctionRva = 0x003AA0F0;
	constexpr uintptr_t ReturnToTownObjectRva = 0x004FCD38;
	constexpr uintptr_t ReturnToTownObjectVtableRva = 0x004685D8;
	constexpr uintptr_t ReturnToTownFunctionRva = 0x001A74E0;
	constexpr uintptr_t ScriptExecuteFunctionRva = 0x000710D0;
	constexpr uintptr_t ScriptExecuteCallRva = 0x000B3A76;
	constexpr uintptr_t ScriptContextRva = 0x004ED6B8;
	constexpr uintptr_t ChatSendFunctionRva = 0x000B85D0;
}
