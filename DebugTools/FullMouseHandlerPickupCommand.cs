namespace Auto.DebugTools;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Auto.Loot;

public static class FullMouseHandlerPickupCommand {
	private const int ProcessVmOperation = 0x0008;
	private const int ProcessVmRead = 0x0010;
	private const int ProcessVmWrite = 0x0020;
	private const int ProcessQueryInformation = 0x0400;
	private const int ThreadSuspendResume = 0x0002;
	private const int ThreadGetContext = 0x0008;
	private const int ThreadSetContext = 0x0010;
	private const int ThreadQueryInformation = 0x0040;
	private const int MemCommit = 0x1000;
	private const int MemReserve = 0x2000;
	private const int MemRelease = 0x8000;
	private const int PageExecuteReadWrite = 0x40;
	private const int AllocationSize = 0x1000;
	private const int MarkerOffset = 0x100;
	private const int ResultOffset = 0x104;
	private const int MarkerValue = unchecked((int)0x61C0FFEE);
	private const uint Wow64ContextFull = 0x00010007;
	private const uint SuspendFailed = 0xFFFFFFFF;
	// Cập nhật sau bản game 2026-08-28: xác nhận bằng BSim (similarity=1.0, hàm handler+constructor khớp duy nhất) + byte thật
	// (đối tượng tại VA mới 0x8FE850 chứa đúng con trỏ vtable 0x00867F20 do constructor mới ghi; slot thứ 5 trong vtable mới
	// trỏ đúng 0x005836D0, khớp vị trí slot thứ 5 = 0x005833A0 trong vtable cũ). Chưa build/test runtime.
	private const int HandlerRva = 0x1836D0;
	private const int InputObjectRva = 0x4FE850;
	private const int ExpectedVtableRva = 0x467F20;
	private const int InputClientXRva = 0x3BACA4;
	private const int InputClientYRva = 0x3BACA8;
	private const int InputLeftStateOffset = 0xFC;
	private const int WmMouseMove = 0x0200;
	private const int WmLeftButtonDown = 0x0201;
	private const int WmLeftButtonUp = 0x0202;
	private const int NoButtons = 0;
	private const int LeftButtonHeld = 1;
	private const int MoveDownDelayMilliseconds = 40;
	private const int DownUpDelayMilliseconds = 60;
	private const int PollMilliseconds = 1000;
	private const int PollIntervalMilliseconds = 5;
	private const int LootClientYOffset = -21;
	private const int AttackClientXScale = 1;
	private const int AttackClientYOffset = -70;
	// Cập nhật sau bản game 2026-08-28: cùng hàm đã xác nhận qua BSim+byte thật cho RuntimeLayoutResolver.RepairConfirmationSignature. Chưa build/test runtime.
	private const int RepairConfirmationFunctionRva = 0x2935A0;
	private const uint MinimumSystemModuleEip = 0x70000000;
	private const int SafePointAttempts = 100;
	private const int SafePointRetryMilliseconds = 2;
	private const uint MemCommitState = 0x1000;
	private const uint PageExecuteMask = 0xF0;
	// Cập nhật sau bản game 2026-08-28: 2 byte thứ 7-8 là con trỏ SEH nội bộ đổi theo build, đã xác nhận bằng byte thật khớp cùng chữ ký RuntimeLayoutResolver.RepairConfirmationSignature. Chưa build/test runtime.
	private static readonly byte[] RepairConfirmationFunctionSignature = {
		0x55, 0x8B, 0xEC, 0x6A, 0xFF, 0x68, 0xB7, 0x3B,
		0x84, 0x00, 0x64, 0xA1, 0x00, 0x00, 0x00, 0x00
	};

	public static int VariantCount => 1;

	public static bool TryOpenRepairAllConfirmation(int processId, IntPtr gameWindowHandle, out string result) {
		result = "";
		if (processId <= 0 || gameWindowHandle == IntPtr.Zero) {
			result = $"PID hoặc HWND không hợp lệ | PID={processId} | HWND=0x{gameWindowHandle.ToInt64():X8}";
			return false;
		}

		GetWindowThreadProcessId(gameWindowHandle, out int windowProcessId);
		if (windowProcessId != processId) {
			result = $"HWND thuộc PID khác | WindowPID={windowProcessId}.";
			return false;
		}

		try {
			using Process process = Process.GetProcessById(processId);
			IntPtr moduleBase = process.MainModule?.BaseAddress ?? IntPtr.Zero;
			if (process.HasExited || moduleBase == IntPtr.Zero) {
				result = "Game process hoặc module base không hợp lệ.";
				return false;
			}

			IntPtr functionAddress = IntPtr.Add(moduleBase, RepairConfirmationFunctionRva);
			IntPtr processHandle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, processId);
			if (processHandle == IntPtr.Zero) {
				result = $"OpenProcess read failed. Win32Error={Marshal.GetLastWin32Error()}";
				return false;
			}

			try {
				if (!TryValidateRepairConfirmationFunction(processHandle, functionAddress, out string validationResult)) {
					result = validationResult;
					return false;
				}
			} finally {
				CloseHandle(processHandle);
			}

			bool invoked = TryInvokeCdeclFourInt32OnWindowThread(processId, gameWindowHandle, functionAddress, 0, 0, 2, 0, out int returnValue, out string invokeResult);
			result = $"Repair all internal command | Function=Game.exe+0x{RepairConfirmationFunctionRva:X} | Arguments=0/0/2/0 | ReturnEax={returnValue}/0x{unchecked((uint)returnValue):X8} | {invokeResult}";
			return invoked && returnValue != 0;
		} catch (Exception ex) {
			result = ex.Message;
			return false;
		}
	}

	public static bool TryClickRawPosition(int processId, IntPtr gameWindowHandle, int playerRawX, int playerRawY, int targetRawX, int targetRawY, out bool projectionOutsideClient, out string result) {
		LootSnapshot target = new LootSnapshot { RawX = targetRawX, RawY = targetRawY };
		return TryPickupCore(processId, gameWindowHandle, playerRawX, playerRawY, target, AttackClientXScale, AttackClientYOffset, true, false, true, out projectionOutsideClient, out result);
	}

	public static bool TryPickup(int processId, IntPtr gameWindowHandle, int playerRawX, int playerRawY, LootSnapshot item, out string result) {
		return TryPickupCore(processId, gameWindowHandle, playerRawX, playerRawY, item, 1, LootClientYOffset, false, false, false, out _, out result);
	}

	private static bool TryPickupCore(int processId, IntPtr gameWindowHandle, int playerRawX, int playerRawY, LootSnapshot item, int clientXScale, int clientYOffset, bool useTwoPhaseClick, bool includeMoveBeforeClick, bool rejectOutsideClient, out bool projectionOutsideClient, out string result) {
		projectionOutsideClient = false;
		result = "";

		if (processId <= 0 || gameWindowHandle == IntPtr.Zero) {
			result = $"Tham số không hợp lệ | PID={processId} | HWND=0x{gameWindowHandle.ToInt64():X8}";
			return false;
		}

		if (!GetClientRect(gameWindowHandle, out NativeRect clientRect)) {
			result = $"GetClientRect failed. Win32Error={Marshal.GetLastWin32Error()}";
			return false;
		}

		int clientWidth = clientRect.Right - clientRect.Left;
		int clientHeight = clientRect.Bottom - clientRect.Top;

		if (clientWidth <= 0 || clientHeight <= 0) {
			result = $"Client size không hợp lệ: {clientWidth}x{clientHeight}.";
			return false;
		}

		int centerX = Math.Max(0, clientWidth / 2 - 1);
		int centerY = Math.Max(0, clientHeight / 2 - 1);
		int rawDeltaX = (item.RawX - playerRawX) * clientXScale;
		int rawDeltaY = item.RawY - playerRawY;
		int projectedClientX = centerX + rawDeltaX;
		int projectedClientY = centerY + (rawDeltaY / 2) + clientYOffset;

		if (rejectOutsideClient && (projectedClientX < 0 || projectedClientY < 0 || projectedClientX >= clientWidth || projectedClientY >= clientHeight)) {
			projectionOutsideClient = true;
			result = $"Attack projection ngoài client | Projected={projectedClientX}/{projectedClientY} | ClientSize={clientWidth}x{clientHeight} | RawDelta={rawDeltaX}/{rawDeltaY}";
			return false;
		}

		int clientX = Math.Clamp(projectedClientX, 0, clientWidth - 1);
		int clientY = Math.Clamp(projectedClientY, 0, clientHeight - 1);
		Process? process = null;

		try {
			process = Process.GetProcessById(processId);
			IntPtr moduleBase = process.MainModule?.BaseAddress ?? IntPtr.Zero;

			if (process.HasExited || moduleBase == IntPtr.Zero) {
				result = "Game process hoặc module base không hợp lệ.";
				return false;
			}

			IntPtr handlerAddress = IntPtr.Add(moduleBase, HandlerRva);
			IntPtr inputObjectAddress = IntPtr.Add(moduleBase, InputObjectRva);
			IntPtr expectedVtableAddress = IntPtr.Add(moduleBase, ExpectedVtableRva);

			if (!TryValidateInputObject(processId, inputObjectAddress, expectedVtableAddress, out string objectResult)) {
				result = "Full mouse handler validation FAIL | " + objectResult;
				return false;
			}

			if (useTwoPhaseClick) {
				if (includeMoveBeforeClick) {
					bool preMoveSent = TryInvokeOnWindowThread(processId, gameWindowHandle, handlerAddress, inputObjectAddress, WmMouseMove, NoButtons, clientX, clientY, out string preMoveResult);
					if (!preMoveSent) {
						result = $"Full mouse handler two-phase FAIL | XScale={clientXScale} | YOffset={clientYOffset} | RawDelta={rawDeltaX}/{rawDeltaY} | Client={clientX}/{clientY} | Move={preMoveResult}";
						return false;
					}
					Thread.Sleep(MoveDownDelayMilliseconds);
				}
				bool clickSent = TryInvokeClickPairOnWindowThread(processId, gameWindowHandle, handlerAddress, inputObjectAddress, clientX, clientY, out string clickResult);
				result = $"Full mouse handler virtual click | PreMove={(includeMoveBeforeClick ? "YES" : "NO")} | XScale={clientXScale} | YOffset={clientYOffset} | RawDelta={rawDeltaX}/{rawDeltaY} | Client={clientX}/{clientY} | Center={centerX}/{centerY} | Handler=Game.exe+{HandlerRva:X8} | Object={objectResult} | ClickPair={clickSent}({clickResult})";
				return clickSent;
			}

			bool moveSent = TryInvokeOnWindowThread(processId, gameWindowHandle, handlerAddress, inputObjectAddress, WmMouseMove, NoButtons, clientX, clientY, out string moveResult);

			if (!moveSent) {
				result = $"Full mouse handler FAIL | Client={clientX}/{clientY} | Object={objectResult} | Move={moveResult}";
				return false;
			}

			Thread.Sleep(MoveDownDelayMilliseconds);
			bool downSent = TryInvokeOnWindowThread(processId, gameWindowHandle, handlerAddress, inputObjectAddress, WmLeftButtonDown, LeftButtonHeld, clientX, clientY, out string downResult);

			if (!downSent) {
				result = $"Full mouse handler FAIL | Client={clientX}/{clientY} | Object={objectResult} | Move={moveResult} | Down={downResult}";
				return false;
			}

			Thread.Sleep(DownUpDelayMilliseconds);
			bool upSent = TryInvokeOnWindowThread(processId, gameWindowHandle, handlerAddress, inputObjectAddress, WmLeftButtonUp, NoButtons, clientX, clientY, out string upResult);
			bool cursorRestored = TryRestorePhysicalCursor(processId, gameWindowHandle, handlerAddress, inputObjectAddress, clientWidth, clientHeight, out string restoreResult);
			result = $"Full mouse handler sequence | XScale={clientXScale} | RawDelta={rawDeltaX}/{rawDeltaY} | Client={clientX}/{clientY} | Center={centerX}/{centerY} | Handler=Game.exe+{HandlerRva:X8} | Object={objectResult} | Move={moveSent}({moveResult}) | Down={downSent}({downResult}) | Up={upSent}({upResult}) | Restore={cursorRestored}({restoreResult}) | Delays={MoveDownDelayMilliseconds}/{DownUpDelayMilliseconds}ms";
			return moveSent && downSent && upSent;
		} catch (Exception ex) {
			result = ex.Message;
			return false;
		} finally {
			process?.Dispose();
		}
	}

	private static bool TryRestorePhysicalCursor(int processId, IntPtr gameWindowHandle, IntPtr handlerAddress, IntPtr inputObjectAddress, int clientWidth, int clientHeight, out string result) {
		result = "SKIP";
		if (!GetCursorPos(out NativePoint point) || !ScreenToClient(gameWindowHandle, ref point)) return false;
		if (point.X < 0 || point.Y < 0 || point.X >= clientWidth || point.Y >= clientHeight) {
			result = $"outside {point.X}/{point.Y}";
			return false;
		}
		return TryInvokeOnWindowThread(processId, gameWindowHandle, handlerAddress, inputObjectAddress, WmMouseMove, NoButtons, point.X, point.Y, out result);
	}

	private static bool TryValidateInputObject(int processId, IntPtr inputObjectAddress, IntPtr expectedVtableAddress, out string result) {
		IntPtr processHandle = OpenProcess(ProcessVmRead | ProcessQueryInformation, false, processId);

		if (processHandle == IntPtr.Zero) {
			result = $"OpenProcess read failed. Win32Error={Marshal.GetLastWin32Error()}";
			return false;
		}

		try {
			int observedVtable = ReadInt32(processHandle, inputObjectAddress);
			bool valid = observedVtable == unchecked((int)expectedVtableAddress.ToInt64());
			result = $"Address=0x{inputObjectAddress.ToInt64():X8} Vtable=0x{unchecked((uint)observedVtable):X8} Expected=0x{expectedVtableAddress.ToInt64():X8} Valid={valid}";
			return valid;
		} finally {
			CloseHandle(processHandle);
		}
	}

	private static bool TryInvokeOnWindowThread(int processId, IntPtr gameWindowHandle, IntPtr handlerAddress, IntPtr inputObjectAddress, int message, int wParam, int clientX, int clientY, out string result) {
		return TryInvokeOnWindowThreadCore(processId, gameWindowHandle, handlerAddress, inputObjectAddress, message, wParam, clientX, clientY, false, out result);
	}

	private static bool TryInvokeClickPairOnWindowThread(int processId, IntPtr gameWindowHandle, IntPtr handlerAddress, IntPtr inputObjectAddress, int clientX, int clientY, out string result) {
		return TryInvokeOnWindowThreadCore(processId, gameWindowHandle, handlerAddress, inputObjectAddress, 0, 0, clientX, clientY, true, out result);
	}

	public static bool TryInvokeThisCallTwoInt32OnWindowThread(int processId, IntPtr gameWindowHandle, IntPtr functionAddress, IntPtr thisAddress, int argument1, int argument2, out int returnValue, out string result) {
		if (functionAddress == IntPtr.Zero || thisAddress == IntPtr.Zero) {
			returnValue = 0;
			result = "Function hoặc this address không hợp lệ.";
			return false;
		}
		return TryInvokeFunctionOnWindowThread(processId, gameWindowHandle, functionAddress, thisAddress, argument1, argument2, true, out returnValue, out result);
	}

	public static bool TryInvokeColdStartAttackSequenceOnWindowThread(int processId, IntPtr gameWindowHandle, IntPtr managerAddress, IntPtr selectFunction, IntPtr prepareFunction, int targetIndex, out int returnValue, out string result) {
		if (managerAddress == IntPtr.Zero || selectFunction == IntPtr.Zero || prepareFunction == IntPtr.Zero || targetIndex <= 1) {
			returnValue = 0;
			result = "Cold-start attack sequence có tham số không hợp lệ.";
			return false;
		}
		ColdStartAttackSequence sequence = new(managerAddress, selectFunction, prepareFunction, targetIndex);
		return TryInvokeFunctionOnWindowThread(processId, gameWindowHandle, selectFunction, IntPtr.Zero, 0, 0, false, out returnValue, out result, coldStartAttackSequence: sequence);
	}

	public static bool TryInvokeStdCallThreeInt32OnWindowThread(int processId, IntPtr gameWindowHandle, IntPtr functionAddress, int argument1, int argument2, int argument3, out int returnValue, out string result) {
		if (functionAddress == IntPtr.Zero) {
			returnValue = 0;
			result = "Function address không hợp lệ.";
			return false;
		}
		StdCallThreeArgumentCall call = new(functionAddress, argument1, argument2, argument3);
		return TryInvokeFunctionOnWindowThread(processId, gameWindowHandle, functionAddress, IntPtr.Zero, 0, 0, false, out returnValue, out result, stdCallThreeArgumentCall: call);
	}

	private static bool TryInvokeCdeclFourInt32OnWindowThread(int processId, IntPtr gameWindowHandle, IntPtr functionAddress, int argument1, int argument2, int argument3, int argument4, out int returnValue, out string result) {
		if (functionAddress == IntPtr.Zero) {
			returnValue = 0;
			result = "Function address không hợp lệ.";
			return false;
		}
		CdeclFourArgumentCall call = new(functionAddress, argument1, argument2, argument3, argument4);
		return TryInvokeFunctionOnWindowThread(processId, gameWindowHandle, functionAddress, IntPtr.Zero, 0, 0, false, out returnValue, out result, cdeclFourArgumentCall: call);
	}

	public static bool TryInvokeThisCallOneInt32OnWindowThread(int processId, IntPtr gameWindowHandle, IntPtr functionAddress, IntPtr thisAddress, int argument, out int returnValue, out string result) {
		if (functionAddress == IntPtr.Zero || thisAddress == IntPtr.Zero) {
			returnValue = 0;
			result = "Function hoặc this address không hợp lệ.";
			return false;
		}
		return TryInvokeFunctionOnWindowThread(processId, gameWindowHandle, functionAddress, thisAddress, 0, 0, false, out returnValue, out result, oneArgument: argument);
	}

	public static bool TryInvokeThisCallNoArgumentsOnWindowThread(int processId, IntPtr gameWindowHandle, IntPtr functionAddress, IntPtr thisAddress, out int returnValue, out string result) {
		if (functionAddress == IntPtr.Zero || thisAddress == IntPtr.Zero) {
			returnValue = 0;
			result = "Function hoặc this address không hợp lệ.";
			return false;
		}
		return TryInvokeFunctionOnWindowThread(processId, gameWindowHandle, functionAddress, IntPtr.Zero, 0, 0, false, out returnValue, out result, thisAddress);
	}

	public static bool TryInvokeThisCallFourInt32OnWindowThread(int processId, IntPtr gameWindowHandle, IntPtr functionAddress, IntPtr thisAddress, int argument1, int argument2, int argument3, int argument4, out int returnValue, out string result) {
		if (functionAddress == IntPtr.Zero || thisAddress == IntPtr.Zero) {
			returnValue = 0;
			result = "Function hoặc this address không hợp lệ.";
			return false;
		}
		FourArgumentThisCall call = new(functionAddress, thisAddress, argument1, argument2, argument3, argument4);
		return TryInvokeFunctionOnWindowThread(processId, gameWindowHandle, functionAddress, thisAddress, 0, 0, false, out returnValue, out result, fourArgumentThisCall: call);
	}

	private static bool TryInvokeFunctionOnWindowThread(int processId, IntPtr gameWindowHandle, IntPtr functionAddress, IntPtr thisAddress, int argument1, int argument2, bool hasThisAndTwoArguments, out int returnValue, out string result, IntPtr noArgumentThisAddress = default, int? oneArgument = null, FourArgumentThisCall? fourArgumentThisCall = null, StdCallThreeArgumentCall? stdCallThreeArgumentCall = null, ColdStartAttackSequence? coldStartAttackSequence = null, CdeclFourArgumentCall? cdeclFourArgumentCall = null) {
		returnValue = 0;
		result = "";
		int windowThreadId = GetWindowThreadProcessId(gameWindowHandle, out int windowProcessId);
		if (windowProcessId != processId || windowThreadId <= 0) {
			result = $"Window thread không hợp lệ | WindowPID={windowProcessId} | ThreadId={windowThreadId}";
			return false;
		}

		IntPtr processHandle = IntPtr.Zero;
		IntPtr threadHandle = IntPtr.Zero;
		IntPtr remoteBlock = IntPtr.Zero;
		bool threadSuspended = false;
		bool contextRedirected = false;
		bool markerExecuted = false;
		bool threadLeftRemoteBlock = false;

		try {
			processHandle = OpenProcess(ProcessVmOperation | ProcessVmRead | ProcessVmWrite | ProcessQueryInformation, false, processId);
			threadHandle = OpenThread(ThreadSuspendResume | ThreadGetContext | ThreadSetContext | ThreadQueryInformation, false, windowThreadId);
			if (processHandle == IntPtr.Zero || threadHandle == IntPtr.Zero) {
				result = $"Open handle failed | Process=0x{processHandle.ToInt64():X8} Thread=0x{threadHandle.ToInt64():X8} Win32Error={Marshal.GetLastWin32Error()}";
				return false;
			}

			remoteBlock = VirtualAllocEx(processHandle, IntPtr.Zero, new UIntPtr(AllocationSize), MemCommit | MemReserve, PageExecuteReadWrite);
			if (remoteBlock == IntPtr.Zero) {
				result = $"VirtualAllocEx failed. Win32Error={Marshal.GetLastWin32Error()}";
				return false;
			}

			if (SuspendThread(threadHandle) == SuspendFailed) {
				result = $"SuspendThread failed. Win32Error={Marshal.GetLastWin32Error()}";
				return false;
			}

			threadSuspended = true;
			Wow64Context context = CreateWow64Context();
			if (!Wow64GetThreadContext(threadHandle, ref context)) {
				result = $"Wow64GetThreadContext failed. Win32Error={Marshal.GetLastWin32Error()}";
				return false;
			}

			if (!TryWaitForSafeRedirectPoint(threadHandle, ref context, ref threadSuspended, out string safePointResult)) {
				result = safePointResult;
				return false;
			}
			if (!TryRefreshProcessHandle(processId, ref processHandle, out string refreshResult)) {
				result = refreshResult;
				return false;
			}

			IntPtr markerAddress = IntPtr.Add(remoteBlock, MarkerOffset);
			IntPtr resultAddress = IntPtr.Add(remoteBlock, ResultOffset);
			byte[] stub = cdeclFourArgumentCall != null
				? BuildCdeclFourInt32Stub(markerAddress, resultAddress, context.Eip, cdeclFourArgumentCall)
				: coldStartAttackSequence != null
				? BuildColdStartAttackSequenceStub(markerAddress, resultAddress, context.Eip, coldStartAttackSequence)
				: stdCallThreeArgumentCall != null
				? BuildStdCallThreeInt32Stub(markerAddress, resultAddress, context.Eip, stdCallThreeArgumentCall)
				: fourArgumentThisCall != null
				? BuildThisCallFourInt32Stub(markerAddress, resultAddress, context.Eip, fourArgumentThisCall)
				: oneArgument.HasValue
					? BuildThisCallOneInt32Stub(markerAddress, resultAddress, context.Eip, functionAddress, thisAddress, oneArgument.Value)
				: hasThisAndTwoArguments
					? BuildThisCallTwoInt32Stub(markerAddress, resultAddress, context.Eip, functionAddress, thisAddress, argument1, argument2)
					: BuildNoArgumentFunctionStub(markerAddress, resultAddress, context.Eip, functionAddress, noArgumentThisAddress);
			bool stubWritten = TryWrite(processHandle, remoteBlock, stub, out string stubError);
			bool markerWritten = TryWrite(processHandle, markerAddress, BitConverter.GetBytes(0), out string markerError);
			bool resultWritten = TryWrite(processHandle, resultAddress, new byte[4], out string resultError);
			if (!stubWritten || !markerWritten || !resultWritten) {
				result = $"Write stub failed | Stub={stubError} | Marker={markerError} | Result={resultError}";
				return false;
			}

			FlushInstructionCache(processHandle, remoteBlock, new UIntPtr((uint)stub.Length));
			uint originalEip = context.Eip;
			context.Eip = unchecked((uint)remoteBlock.ToInt64());
			if (!Wow64SetThreadContext(threadHandle, ref context)) {
				result = $"Wow64SetThreadContext failed. Win32Error={Marshal.GetLastWin32Error()}";
				return false;
			}

			contextRedirected = true;
			if (ResumeThread(threadHandle) == SuspendFailed) {
				result = $"ResumeThread failed. Win32Error={Marshal.GetLastWin32Error()}";
				return false;
			}

			threadSuspended = false;
			int polls = Math.Max(1, PollMilliseconds / PollIntervalMilliseconds);
			for (int index = 0; index < polls; index++) {
				if (ReadInt32(processHandle, markerAddress) == MarkerValue) {
					markerExecuted = true;
					break;
				}
				Thread.Sleep(PollIntervalMilliseconds);
			}

			if (markerExecuted) threadLeftRemoteBlock = TryWaitUntilThreadLeavesRemoteBlock(threadHandle, remoteBlock, AllocationSize);
			returnValue = ReadInt32(processHandle, resultAddress);
			result = $"ThreadId={windowThreadId} OriginalEip=0x{originalEip:X8} Redirected={contextRedirected} Marker={markerExecuted} Returned={threadLeftRemoteBlock}";
			return contextRedirected && markerExecuted && threadLeftRemoteBlock;
		} finally {
			if (threadSuspended && threadHandle != IntPtr.Zero) ResumeThread(threadHandle);
			bool safeToFreeRemoteBlock = !contextRedirected || (markerExecuted && threadLeftRemoteBlock);
			if (safeToFreeRemoteBlock && remoteBlock != IntPtr.Zero && processHandle != IntPtr.Zero) VirtualFreeEx(processHandle, remoteBlock, UIntPtr.Zero, MemRelease);
			if (threadHandle != IntPtr.Zero) CloseHandle(threadHandle);
			if (processHandle != IntPtr.Zero) CloseHandle(processHandle);
		}
	}

	private static bool TryInvokeOnWindowThreadCore(int processId, IntPtr gameWindowHandle, IntPtr handlerAddress, IntPtr inputObjectAddress, int message, int wParam, int clientX, int clientY, bool sendClickPair, out string result) {
		result = "";
		int windowThreadId = GetWindowThreadProcessId(gameWindowHandle, out int windowProcessId);

		if (windowProcessId != processId || windowThreadId <= 0) {
			result = $"Window thread không hợp lệ | WindowPID={windowProcessId} | ThreadId={windowThreadId}";
			return false;
		}

		IntPtr processHandle = IntPtr.Zero;
		IntPtr threadHandle = IntPtr.Zero;
		IntPtr remoteBlock = IntPtr.Zero;
		bool threadSuspended = false;
		bool contextRedirected = false;
		bool markerExecuted = false;
		bool threadLeftRemoteBlock = false;

		try {
			processHandle = OpenProcess(ProcessVmOperation | ProcessVmRead | ProcessVmWrite | ProcessQueryInformation, false, processId);
			threadHandle = OpenThread(ThreadSuspendResume | ThreadGetContext | ThreadSetContext | ThreadQueryInformation, false, windowThreadId);

			if (processHandle == IntPtr.Zero || threadHandle == IntPtr.Zero) {
				result = $"Open handle failed | Process=0x{processHandle.ToInt64():X8} Thread=0x{threadHandle.ToInt64():X8} Win32Error={Marshal.GetLastWin32Error()}";
				return false;
			}

			remoteBlock = VirtualAllocEx(processHandle, IntPtr.Zero, new UIntPtr(AllocationSize), MemCommit | MemReserve, PageExecuteReadWrite);

			if (remoteBlock == IntPtr.Zero) {
				result = $"VirtualAllocEx failed. Win32Error={Marshal.GetLastWin32Error()}";
				return false;
			}

			uint suspendResult = SuspendThread(threadHandle);

			if (suspendResult == SuspendFailed) {
				result = $"SuspendThread failed. Win32Error={Marshal.GetLastWin32Error()}";
				return false;
			}

			threadSuspended = true;
			Wow64Context context = CreateWow64Context();

			if (!Wow64GetThreadContext(threadHandle, ref context)) {
				result = $"Wow64GetThreadContext failed. Win32Error={Marshal.GetLastWin32Error()}";
				return false;
			}

			if (!TryWaitForSafeRedirectPoint(threadHandle, ref context, ref threadSuspended, out string safePointResult)) {
				result = safePointResult;
				return false;
			}
			if (!TryRefreshProcessHandle(processId, ref processHandle, out string refreshResult)) {
				result = refreshResult;
				return false;
			}

			IntPtr markerAddress = IntPtr.Add(remoteBlock, MarkerOffset);
			IntPtr resultAddress = IntPtr.Add(remoteBlock, ResultOffset);
			byte[] stub = sendClickPair
				? BuildHandlerClickPairStub(markerAddress, resultAddress, context.Eip, handlerAddress, inputObjectAddress, clientX, clientY)
				: BuildHandlerStub(markerAddress, resultAddress, context.Eip, handlerAddress, inputObjectAddress, message, wParam, clientX, clientY);
			bool stubWritten = TryWrite(processHandle, remoteBlock, stub, out string stubError);
			bool markerWritten = TryWrite(processHandle, markerAddress, BitConverter.GetBytes(0), out string markerError);
			bool resultWritten = TryWrite(processHandle, resultAddress, new byte[sendClickPair ? 8 : 4], out string resultError);

			if (!stubWritten || !markerWritten || !resultWritten) {
				result = $"Write stub failed | Stub={stubError} | Marker={markerError} | Result={resultError}";
				return false;
			}

			FlushInstructionCache(processHandle, remoteBlock, new UIntPtr((uint)stub.Length));
			uint originalEip = context.Eip;
			context.Eip = unchecked((uint)remoteBlock.ToInt64());

			if (!Wow64SetThreadContext(threadHandle, ref context)) {
				result = $"Wow64SetThreadContext failed. Win32Error={Marshal.GetLastWin32Error()}";
				return false;
			}

			contextRedirected = true;
			uint resumeResult = ResumeThread(threadHandle);

			if (resumeResult == SuspendFailed) {
				result = $"ResumeThread failed. Win32Error={Marshal.GetLastWin32Error()}";
				return false;
			}

			threadSuspended = false;
			int polls = Math.Max(1, PollMilliseconds / PollIntervalMilliseconds);

			for (int index = 0; index < polls; index++) {
				if (ReadInt32(processHandle, markerAddress) == MarkerValue) {
					markerExecuted = true;
					break;
				}

				Thread.Sleep(PollIntervalMilliseconds);
			}

			if (markerExecuted) threadLeftRemoteBlock = TryWaitUntilThreadLeavesRemoteBlock(threadHandle, remoteBlock, AllocationSize);

			IntPtr moduleBase = IntPtr.Add(handlerAddress, -HandlerRva);
			int inputX = ReadInt32(processHandle, IntPtr.Add(moduleBase, InputClientXRva));
			int inputY = ReadInt32(processHandle, IntPtr.Add(moduleBase, InputClientYRva));
			int leftState = ReadInt32(processHandle, IntPtr.Add(inputObjectAddress, InputLeftStateOffset));

			if (sendClickPair) {
				int downResult = ReadInt32(processHandle, resultAddress);
				int upResult = ReadInt32(processHandle, IntPtr.Add(resultAddress, 4));
				result = $"ThreadId={windowThreadId} Atomic=Down/Up OriginalEip=0x{originalEip:X8} Redirected={contextRedirected} Marker={markerExecuted} Returned={threadLeftRemoteBlock} HandlerEax={downResult}/{upResult} Input={inputX}/{inputY} LeftState={leftState}";
			} else {
				int handlerResult = ReadInt32(processHandle, resultAddress);
				result = $"ThreadId={windowThreadId} Message=0x{message:X4} WParam=0x{wParam:X4} OriginalEip=0x{originalEip:X8} Redirected={contextRedirected} Marker={markerExecuted} Returned={threadLeftRemoteBlock} HandlerEax={handlerResult}/0x{unchecked((uint)handlerResult):X8} Input={inputX}/{inputY} LeftState={leftState}";
			}

			return contextRedirected && markerExecuted && threadLeftRemoteBlock;
		} finally {
			if (threadSuspended && threadHandle != IntPtr.Zero) {
				ResumeThread(threadHandle);
			}

			bool safeToFreeRemoteBlock = !contextRedirected || (markerExecuted && threadLeftRemoteBlock);
			if (safeToFreeRemoteBlock && remoteBlock != IntPtr.Zero && processHandle != IntPtr.Zero) VirtualFreeEx(processHandle, remoteBlock, UIntPtr.Zero, MemRelease);

			if (threadHandle != IntPtr.Zero) {
				CloseHandle(threadHandle);
			}

			if (processHandle != IntPtr.Zero) {
				CloseHandle(processHandle);
			}
		}
	}

	private static bool TryWaitForSafeRedirectPoint(IntPtr threadHandle, ref Wow64Context context, ref bool threadSuspended, out string result) {
		for (int attempt = 0; attempt < SafePointAttempts; attempt++) {
			if (context.Eip >= MinimumSystemModuleEip) {
				result = $"SafePoint EIP=0x{context.Eip:X8} Attempt={attempt + 1}";
				return true;
			}
			if (ResumeThread(threadHandle) == SuspendFailed) {
				result = $"SafePoint resume failed | EIP=0x{context.Eip:X8} Win32Error={Marshal.GetLastWin32Error()}";
				return false;
			}
			threadSuspended = false;
			Thread.Sleep(SafePointRetryMilliseconds);
			if (SuspendThread(threadHandle) == SuspendFailed) {
				result = $"SafePoint suspend failed | Win32Error={Marshal.GetLastWin32Error()}";
				return false;
			}
			threadSuspended = true;
			context = CreateWow64Context();
			if (!Wow64GetThreadContext(threadHandle, ref context)) {
				result = $"SafePoint context failed | Win32Error={Marshal.GetLastWin32Error()}";
				return false;
			}
		}
		result = $"Không tìm thấy safe point sau {SafePointAttempts} lần | LastEIP=0x{context.Eip:X8}";
		return false;
	}

	private static bool TryRefreshProcessHandle(int processId, ref IntPtr processHandle, out string result) {
		IntPtr refreshedHandle = OpenProcess(ProcessVmOperation | ProcessVmRead | ProcessVmWrite | ProcessQueryInformation, false, processId);
		if (refreshedHandle == IntPtr.Zero) {
			result = $"Refresh process handle failed | Win32Error={Marshal.GetLastWin32Error()}";
			return false;
		}
		if (processHandle != IntPtr.Zero) CloseHandle(processHandle);
		processHandle = refreshedHandle;
		result = "";
		return true;
	}

	private static bool TryWaitUntilThreadLeavesRemoteBlock(IntPtr threadHandle, IntPtr remoteBlock, int remoteBlockSize) {
		long blockStart = unchecked((uint)remoteBlock.ToInt64());
		long blockEnd = blockStart + remoteBlockSize;
		for (int attempt = 0; attempt < 100; attempt++) {
			uint suspendResult = SuspendThread(threadHandle);
			if (suspendResult == SuspendFailed) return false;
			try {
				Wow64Context context = CreateWow64Context();
				if (!Wow64GetThreadContext(threadHandle, ref context)) return false;
				long eip = context.Eip;
				if (eip < blockStart || eip >= blockEnd) return true;
			} finally {
				ResumeThread(threadHandle);
			}
			Thread.Sleep(1);
		}
		return false;
	}

	private static Wow64Context CreateWow64Context() {
		return new Wow64Context {
			ContextFlags = Wow64ContextFull,
			FloatSave = new Wow64FloatingSaveArea { RegisterArea = new byte[80] },
			ExtendedRegisters = new byte[512]
		};
	}

	private static byte[] BuildHandlerStub(IntPtr markerAddress, IntPtr resultAddress, uint originalEip, IntPtr handlerAddress, IntPtr inputObjectAddress, int message, int wParam, int clientX, int clientY) {
		List<byte> code = new();
		int lParam = (clientY << 16) | (clientX & 0xFFFF);
		code.Add(0x9C);
		code.Add(0x60);
		AppendPushInt32(code, lParam);
		AppendPushInt32(code, wParam);
		AppendPushInt32(code, message);
		code.Add(0xB9);
		code.AddRange(BitConverter.GetBytes(unchecked((int)inputObjectAddress.ToInt64())));
		code.Add(0xB8);
		code.AddRange(BitConverter.GetBytes(unchecked((int)handlerAddress.ToInt64())));
		code.Add(0xFF);
		code.Add(0xD0);
		code.Add(0xA3);
		code.AddRange(BitConverter.GetBytes(unchecked((int)resultAddress.ToInt64())));
		code.AddRange(new byte[] { 0xC7, 0x05 });
		code.AddRange(BitConverter.GetBytes(unchecked((int)markerAddress.ToInt64())));
		code.AddRange(BitConverter.GetBytes(MarkerValue));
		code.Add(0x61);
		code.Add(0x9D);
		code.Add(0x68);
		code.AddRange(BitConverter.GetBytes(originalEip));
		code.Add(0xC3);
		return code.ToArray();
	}

	private static byte[] BuildHandlerClickPairStub(IntPtr markerAddress, IntPtr resultAddress, uint originalEip, IntPtr handlerAddress, IntPtr inputObjectAddress, int clientX, int clientY) {
		List<byte> code = new();
		int lParam = (clientY << 16) | (clientX & 0xFFFF);
		code.Add(0x9C);
		code.Add(0x60);
		AppendHandlerCall(code, handlerAddress, inputObjectAddress, WmLeftButtonDown, LeftButtonHeld, lParam, resultAddress);
		AppendHandlerCall(code, handlerAddress, inputObjectAddress, WmLeftButtonUp, NoButtons, lParam, IntPtr.Add(resultAddress, 4));
		code.AddRange(new byte[] { 0xC7, 0x05 });
		code.AddRange(BitConverter.GetBytes(unchecked((int)markerAddress.ToInt64())));
		code.AddRange(BitConverter.GetBytes(MarkerValue));
		code.Add(0x61);
		code.Add(0x9D);
		code.Add(0x68);
		code.AddRange(BitConverter.GetBytes(originalEip));
		code.Add(0xC3);
		return code.ToArray();
	}

	private static byte[] BuildNoArgumentFunctionStub(IntPtr markerAddress, IntPtr resultAddress, uint originalEip, IntPtr functionAddress, IntPtr thisAddress = default) {
		List<byte> code = new();
		code.Add(0x9C);
		code.Add(0x60);
		if (thisAddress != IntPtr.Zero) {
			code.Add(0xB9);
			code.AddRange(BitConverter.GetBytes(unchecked((int)thisAddress.ToInt64())));
		}
		code.Add(0xB8);
		code.AddRange(BitConverter.GetBytes(unchecked((int)functionAddress.ToInt64())));
		code.Add(0xFF);
		code.Add(0xD0);
		code.Add(0xA3);
		code.AddRange(BitConverter.GetBytes(unchecked((int)resultAddress.ToInt64())));
		code.AddRange(new byte[] { 0xC7, 0x05 });
		code.AddRange(BitConverter.GetBytes(unchecked((int)markerAddress.ToInt64())));
		code.AddRange(BitConverter.GetBytes(MarkerValue));
		code.Add(0x61);
		code.Add(0x9D);
		code.Add(0x68);
		code.AddRange(BitConverter.GetBytes(originalEip));
		code.Add(0xC3);
		return code.ToArray();
	}

	private static byte[] BuildThisCallTwoInt32Stub(IntPtr markerAddress, IntPtr resultAddress, uint originalEip, IntPtr functionAddress, IntPtr thisAddress, int argument1, int argument2) {
		List<byte> code = new();
		code.Add(0x9C);
		code.Add(0x60);
		AppendPushInt32(code, argument2);
		AppendPushInt32(code, argument1);
		code.Add(0xB9);
		code.AddRange(BitConverter.GetBytes(unchecked((int)thisAddress.ToInt64())));
		code.Add(0xB8);
		code.AddRange(BitConverter.GetBytes(unchecked((int)functionAddress.ToInt64())));
		code.Add(0xFF);
		code.Add(0xD0);
		code.Add(0xA3);
		code.AddRange(BitConverter.GetBytes(unchecked((int)resultAddress.ToInt64())));
		code.AddRange(new byte[] { 0xC7, 0x05 });
		code.AddRange(BitConverter.GetBytes(unchecked((int)markerAddress.ToInt64())));
		code.AddRange(BitConverter.GetBytes(MarkerValue));
		code.Add(0x61);
		code.Add(0x9D);
		code.Add(0x68);
		code.AddRange(BitConverter.GetBytes(originalEip));
		code.Add(0xC3);
		return code.ToArray();
	}

	private static byte[] BuildColdStartAttackSequenceStub(IntPtr markerAddress, IntPtr resultAddress, uint originalEip, ColdStartAttackSequence sequence) {
		List<byte> code = [0x9C, 0x60];
		code.AddRange([0x83, 0xEC, 0x30]);
		code.AddRange([0x33, 0xC0]);
		code.AddRange([0x8D, 0x3C, 0x24]);
		code.AddRange([0xB9, 0x0C, 0x00, 0x00, 0x00]);
		code.AddRange([0xF3, 0xAB]);
		code.AddRange([0x8D, 0x04, 0x24]);
		AppendPushInt32(code, 0);
		code.Add(0x50);
		AppendPushInt32(code, 9);
		code.Add(0xB9);
		code.AddRange(BitConverter.GetBytes(unchecked((int)sequence.ManagerAddress.ToInt64())));
		code.Add(0xB8);
		code.AddRange(BitConverter.GetBytes(unchecked((int)sequence.PrepareFunction.ToInt64())));
		code.AddRange([0xFF, 0xD0]);
		code.AddRange([0x8B, 0x44, 0x24, 0x24]);
		code.Add(0x50);
		AppendPushInt32(code, sequence.TargetIndex);
		code.Add(0xB9);
		code.AddRange(BitConverter.GetBytes(unchecked((int)sequence.ManagerAddress.ToInt64())));
		code.Add(0xB8);
		code.AddRange(BitConverter.GetBytes(unchecked((int)sequence.SelectFunction.ToInt64())));
		code.AddRange([0xFF, 0xD0, 0xA3]);
		code.AddRange(BitConverter.GetBytes(unchecked((int)resultAddress.ToInt64())));
		code.AddRange([0x83, 0xC4, 0x30]);
		code.AddRange([0xC7, 0x05]);
		code.AddRange(BitConverter.GetBytes(unchecked((int)markerAddress.ToInt64())));
		code.AddRange(BitConverter.GetBytes(MarkerValue));
		code.AddRange([0x61, 0x9D, 0x68]);
		code.AddRange(BitConverter.GetBytes(originalEip));
		code.Add(0xC3);
		return code.ToArray();
	}

	private static byte[] BuildStdCallThreeInt32Stub(IntPtr markerAddress, IntPtr resultAddress, uint originalEip, StdCallThreeArgumentCall call) {
		List<byte> code = [0x9C, 0x60];
		AppendPushInt32(code, call.Argument3);
		AppendPushInt32(code, call.Argument2);
		AppendPushInt32(code, call.Argument1);
		code.Add(0xB8);
		code.AddRange(BitConverter.GetBytes(unchecked((int)call.FunctionAddress.ToInt64())));
		code.AddRange([0xFF, 0xD0, 0xA3]);
		code.AddRange(BitConverter.GetBytes(unchecked((int)resultAddress.ToInt64())));
		code.AddRange([0xC7, 0x05]);
		code.AddRange(BitConverter.GetBytes(unchecked((int)markerAddress.ToInt64())));
		code.AddRange(BitConverter.GetBytes(MarkerValue));
		code.AddRange([0x61, 0x9D, 0x68]);
		code.AddRange(BitConverter.GetBytes(originalEip));
		code.Add(0xC3);
		return code.ToArray();
	}

	private static byte[] BuildCdeclFourInt32Stub(IntPtr markerAddress, IntPtr resultAddress, uint originalEip, CdeclFourArgumentCall call) {
		List<byte> code = [0x9C, 0x60];
		AppendPushInt32(code, call.Argument4);
		AppendPushInt32(code, call.Argument3);
		AppendPushInt32(code, call.Argument2);
		AppendPushInt32(code, call.Argument1);
		code.Add(0xB8);
		code.AddRange(BitConverter.GetBytes(unchecked((int)call.FunctionAddress.ToInt64())));
		code.AddRange([0xFF, 0xD0, 0x83, 0xC4, 0x10, 0xA3]);
		code.AddRange(BitConverter.GetBytes(unchecked((int)resultAddress.ToInt64())));
		code.AddRange([0xC7, 0x05]);
		code.AddRange(BitConverter.GetBytes(unchecked((int)markerAddress.ToInt64())));
		code.AddRange(BitConverter.GetBytes(MarkerValue));
		code.AddRange([0x61, 0x9D, 0x68]);
		code.AddRange(BitConverter.GetBytes(originalEip));
		code.Add(0xC3);
		return code.ToArray();
	}

	private static byte[] BuildThisCallFourInt32Stub(IntPtr markerAddress, IntPtr resultAddress, uint originalEip, FourArgumentThisCall call) {
		List<byte> code = [0x9C, 0x60];
		AppendPushInt32(code, call.Argument4);
		AppendPushInt32(code, call.Argument3);
		AppendPushInt32(code, call.Argument2);
		AppendPushInt32(code, call.Argument1);
		code.Add(0xB9);
		code.AddRange(BitConverter.GetBytes(unchecked((int)call.ThisAddress.ToInt64())));
		code.Add(0xB8);
		code.AddRange(BitConverter.GetBytes(unchecked((int)call.FunctionAddress.ToInt64())));
		code.AddRange([0xFF, 0xD0, 0xA3]);
		code.AddRange(BitConverter.GetBytes(unchecked((int)resultAddress.ToInt64())));
		code.AddRange([0xC7, 0x05]);
		code.AddRange(BitConverter.GetBytes(unchecked((int)markerAddress.ToInt64())));
		code.AddRange(BitConverter.GetBytes(MarkerValue));
		code.AddRange([0x61, 0x9D, 0x68]);
		code.AddRange(BitConverter.GetBytes(originalEip));
		code.Add(0xC3);
		return code.ToArray();
	}

	private static byte[] BuildThisCallOneInt32Stub(IntPtr markerAddress, IntPtr resultAddress, uint originalEip, IntPtr functionAddress, IntPtr thisAddress, int argument) {
		List<byte> code = new();
		code.Add(0x9C);
		code.Add(0x60);
		AppendPushInt32(code, argument);
		code.Add(0xB9);
		code.AddRange(BitConverter.GetBytes(unchecked((int)thisAddress.ToInt64())));
		code.Add(0xB8);
		code.AddRange(BitConverter.GetBytes(unchecked((int)functionAddress.ToInt64())));
		code.Add(0xFF);
		code.Add(0xD0);
		code.Add(0xA3);
		code.AddRange(BitConverter.GetBytes(unchecked((int)resultAddress.ToInt64())));
		code.AddRange(new byte[] { 0xC7, 0x05 });
		code.AddRange(BitConverter.GetBytes(unchecked((int)markerAddress.ToInt64())));
		code.AddRange(BitConverter.GetBytes(MarkerValue));
		code.Add(0x61);
		code.Add(0x9D);
		code.Add(0x68);
		code.AddRange(BitConverter.GetBytes(originalEip));
		code.Add(0xC3);
		return code.ToArray();
	}

	private static bool TryValidateRepairConfirmationFunction(IntPtr processHandle, IntPtr functionAddress, out string result) {
		result = "";
		if (VirtualQueryEx(processHandle, functionAddress, out MemoryBasicInformation memory, new UIntPtr((uint)Marshal.SizeOf<MemoryBasicInformation>())) == UIntPtr.Zero) {
			result = $"VirtualQueryEx repair function failed | Address=0x{functionAddress.ToInt64():X8} | Win32Error={Marshal.GetLastWin32Error()}";
			return false;
		}
		if (memory.State != MemCommitState || (memory.Protect & PageExecuteMask) == 0) {
			result = $"Repair function không thuộc vùng executable | Address=0x{functionAddress.ToInt64():X8} | State=0x{memory.State:X} | Protect=0x{memory.Protect:X}";
			return false;
		}

		byte[] observed = new byte[RepairConfirmationFunctionSignature.Length];
		bool read = ReadProcessMemory(processHandle, functionAddress, observed, observed.Length, out IntPtr bytesRead);
		if (!read || bytesRead.ToInt64() != observed.Length) {
			result = $"Không đọc được chữ ký repair function | Address=0x{functionAddress.ToInt64():X8} | Read={bytesRead.ToInt64()}/{observed.Length}";
			return false;
		}
		if (!observed.SequenceEqual(RepairConfirmationFunctionSignature)) {
			result = $"Chữ ký repair function không khớp | Address=0x{functionAddress.ToInt64():X8} | Observed={Convert.ToHexString(observed)}";
			return false;
		}

		result = $"Repair function hợp lệ | Address=0x{functionAddress.ToInt64():X8}";
		return true;
	}

	private static void AppendHandlerCall(List<byte> code, IntPtr handlerAddress, IntPtr inputObjectAddress, int message, int wParam, int lParam, IntPtr resultAddress) {
		AppendPushInt32(code, lParam);
		AppendPushInt32(code, wParam);
		AppendPushInt32(code, message);
		code.Add(0xB9);
		code.AddRange(BitConverter.GetBytes(unchecked((int)inputObjectAddress.ToInt64())));
		code.Add(0xB8);
		code.AddRange(BitConverter.GetBytes(unchecked((int)handlerAddress.ToInt64())));
		code.Add(0xFF);
		code.Add(0xD0);
		code.Add(0xA3);
		code.AddRange(BitConverter.GetBytes(unchecked((int)resultAddress.ToInt64())));
	}

	private static void AppendPushInt32(List<byte> code, int value) {
		code.Add(0x68);
		code.AddRange(BitConverter.GetBytes(value));
	}

	private static bool TryWrite(IntPtr processHandle, IntPtr address, byte[] bytes, out string error) {
		error = "";
		bool success = WriteProcessMemory(processHandle, address, bytes, bytes.Length, out IntPtr bytesWritten);

		if (!success || bytesWritten.ToInt64() != bytes.Length) {
			error = $"Address=0x{address.ToInt64():X8} Written={bytesWritten.ToInt64()}/{bytes.Length} Win32Error={Marshal.GetLastWin32Error()}";
			return false;
		}

		return true;
	}

	private static int ReadInt32(IntPtr processHandle, IntPtr address) {
		byte[] bytes = new byte[4];
		bool success = ReadProcessMemory(processHandle, address, bytes, bytes.Length, out IntPtr bytesRead);
		return success && bytesRead.ToInt64() == bytes.Length ? BitConverter.ToInt32(bytes, 0) : 0;
	}

	private sealed record ColdStartAttackSequence(IntPtr ManagerAddress, IntPtr SelectFunction, IntPtr PrepareFunction, int TargetIndex);
	private sealed record FourArgumentThisCall(IntPtr FunctionAddress, IntPtr ThisAddress, int Argument1, int Argument2, int Argument3, int Argument4);
	private sealed record StdCallThreeArgumentCall(IntPtr FunctionAddress, int Argument1, int Argument2, int Argument3);
	private sealed record CdeclFourArgumentCall(IntPtr FunctionAddress, int Argument1, int Argument2, int Argument3, int Argument4);

	[StructLayout(LayoutKind.Sequential)]
	private struct NativeRect {
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct NativePoint {
		public int X;
		public int Y;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct MemoryBasicInformation {
		public IntPtr BaseAddress;
		public IntPtr AllocationBase;
		public uint AllocationProtect;
		public UIntPtr RegionSize;
		public uint State;
		public uint Protect;
		public uint Type;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Wow64FloatingSaveArea {
		public uint ControlWord;
		public uint StatusWord;
		public uint TagWord;
		public uint ErrorOffset;
		public uint ErrorSelector;
		public uint DataOffset;
		public uint DataSelector;
		[MarshalAs(UnmanagedType.ByValArray, SizeConst = 80)]
		public byte[] RegisterArea;
		public uint Cr0NpxState;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct Wow64Context {
		public uint ContextFlags;
		public uint Dr0;
		public uint Dr1;
		public uint Dr2;
		public uint Dr3;
		public uint Dr6;
		public uint Dr7;
		public Wow64FloatingSaveArea FloatSave;
		public uint SegGs;
		public uint SegFs;
		public uint SegEs;
		public uint SegDs;
		public uint Edi;
		public uint Esi;
		public uint Ebx;
		public uint Edx;
		public uint Ecx;
		public uint Eax;
		public uint Ebp;
		public uint Eip;
		public uint SegCs;
		public uint EFlags;
		public uint Esp;
		public uint SegSs;
		[MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)]
		public byte[] ExtendedRegisters;
	}

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool GetClientRect(IntPtr windowHandle, out NativeRect rect);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool GetCursorPos(out NativePoint point);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool ScreenToClient(IntPtr windowHandle, ref NativePoint point);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern int GetWindowThreadProcessId(IntPtr windowHandle, out int processId);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenProcess(int desiredAccess, bool inheritHandle, int processId);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr OpenThread(int desiredAccess, bool inheritHandle, int threadId);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern uint SuspendThread(IntPtr threadHandle);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern uint ResumeThread(IntPtr threadHandle);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool Wow64GetThreadContext(IntPtr threadHandle, ref Wow64Context context);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool Wow64SetThreadContext(IntPtr threadHandle, ref Wow64Context context);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr VirtualAllocEx(IntPtr processHandle, IntPtr address, UIntPtr size, int allocationType, int protection);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool VirtualFreeEx(IntPtr processHandle, IntPtr address, UIntPtr size, int freeType);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool WriteProcessMemory(IntPtr processHandle, IntPtr baseAddress, byte[] buffer, int size, out IntPtr bytesWritten);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool ReadProcessMemory(IntPtr processHandle, IntPtr baseAddress, byte[] buffer, int size, out IntPtr bytesRead);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern UIntPtr VirtualQueryEx(IntPtr processHandle, IntPtr address, out MemoryBasicInformation buffer, UIntPtr length);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool FlushInstructionCache(IntPtr processHandle, IntPtr baseAddress, UIntPtr size);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr handle);
}
