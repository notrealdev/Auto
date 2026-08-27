namespace Auto.Market;

using Auto.Attack;
using Auto.Utils;

internal sealed class AutoAdvertiseEngine {
	private const int InterChannelDelayMilliseconds = 5000;
	private const int MaximumScriptLength = 199;
	private const int ChannelManagerRva = 0x004F3154;
	private const int ChannelCodeTableRva = 0x004F3198;
	private const int ChannelCountOffset = 0x00008E4C;
	private const int ChannelListOffset = 0x00008E50;
	private const int ChannelEntryStride = 0x00002818;
	private const int ChannelNameIndexOffset = 0x00000008;
	private const int ChannelCodeStride = 0x00000264;
	private const int MaximumChannelCount = 64;
	private const string NearbyChannelCode = "áẵẵỹ";
	private const string AreaChannelCode = "±ắàỉ";
	private const string TradeChannelCode = "ẵằềì";
	private const string TerritoryChannelCode = "ạỳẳề";
	private readonly Settings settings;
	private readonly AutoFsAttackTransport transport;
	private readonly DateTime?[] lastSentUtc = new DateTime?[4];
	private DateTime nextChannelUtc = DateTime.MinValue;
	private string lastFailure = "";

	public bool IsConfigured => settings.AutoAdvertise && ! string.IsNullOrWhiteSpace(settings.AdvertiseText) &&
		(settings.NearbyEnabled || settings.AreaEnabled || settings.TradeEnabled || settings.TerritoryEnabled);

	public AutoAdvertiseEngine(Settings settings, AutoFsAttackTransport transport) {
		this.settings = settings;
		this.transport = transport;
	}

	// Xóa toàn bộ lịch cũ để thao tác toggle Tự Rao khởi động lại đúng một chu kỳ mới.
	public void RestartSchedule() {
		Array.Clear(lastSentUtc);
		nextChannelUtc = DateTime.MinValue;
		lastFailure = "";
	}

	// Chạy bốn bộ đếm độc lập theo đúng thứ tự Cận, Khu vực, Giao và Lãnh địa của AutoFS.
	public void Tick(int processId, IntPtr gameWindowHandle, Action<string>? log) {
		if (! settings.AutoAdvertise || string.IsNullOrWhiteSpace(settings.AdvertiseText)) {
			RestartSchedule();
			return;
		}
		DateTime now = DateTime.UtcNow;
		if (now < nextChannelUtc) return;
		AdvertiseChannel[] channels = GetChannels();
		for (int index = 0; index < channels.Length; index++) {
			AdvertiseChannel channel = channels[index];
			if (! channel.Enabled) {
				lastSentUtc[index] = null;
				continue;
			}
			DateTime? lastSent = lastSentUtc[index];
			if (lastSent.HasValue && now < lastSent.Value.AddSeconds(channel.DelaySeconds)) continue;
			if (! TryResolveRuntimeChannel(processId, channel.Code, out RuntimeChannel runtimeChannel, out string resolveError)) {
				LogFailureOnce(log, $"ADVERTISE_CHANNEL_NOT_FOUND | Channel={channel.Name} | Reason={resolveError}");
				return;
			}
			if (! TryBuildContent(settings.AdvertiseText, out byte[] content, out string buildError)) {
				LogFailureOnce(log, $"ADVERTISE_BUILD_FAILED | Channel={channel.Name} | Reason={buildError}");
				return;
			}
			if (! transport.TrySendChat(gameWindowHandle, runtimeChannel.Id, content, out string sendError)) {
				LogFailureOnce(log, $"ADVERTISE_SEND_FAILED | Channel={channel.Name} | RuntimeId={runtimeChannel.Id} | ContentBytes={content.Length} | ContentHex={Convert.ToHexString(content)} | Reason={sendError}");
				return;
			}
			// Tính chu kỳ từ thời điểm native đã xử lý xong để hai lần gửi thực tế không ngắn hơn delay cấu hình.
			DateTime dispatchedUtc = DateTime.UtcNow;
			lastFailure = "";
			lastSentUtc[index] = dispatchedUtc;
			nextChannelUtc = dispatchedUtc.AddMilliseconds(InterChannelDelayMilliseconds);
			log?.Invoke($"ADVERTISE_NETWORK_DISPATCHED | Channel={channel.Name} | RuntimeIndex={runtimeChannel.Index} | RuntimeId={runtimeChannel.Id} | RuntimeCodeHex={Convert.ToHexString(runtimeChannel.Code)} | DelaySeconds={channel.DelaySeconds} | ContentBytes={content.Length} | ContentHex={Convert.ToHexString(content)} | NativeAccepted=True | Source=FUN_004B85D0");
			return;
		}
	}

	// Mã hóa riêng nội dung sang legacy; native sẽ gọi trực tiếp hàm gửi mạng thay vì thực thi Lua Chat.
	private static bool TryBuildContent(string content, out byte[] encodedContent, out string error) {
		string escapedContent = content.Replace("\r\n", "\\n", StringComparison.Ordinal)
			.Replace("\n", "\\n", StringComparison.Ordinal)
			.Replace("\r", "\\n", StringComparison.Ordinal);
		if (! LegacyVietnameseText.TryEncode(escapedContent, out byte[] contentBytes, out char unsupportedCharacter)) {
			encodedContent = [];
			error = $"Ký tự không được client hỗ trợ: U+{(int)unsupportedCharacter:X4}.";
			return false;
		}
		encodedContent = contentBytes;
		if (encodedContent.Length > MaximumScriptLength) {
			error = $"Nội dung dài {encodedContent.Length} byte, giới hạn AutoFS là {MaximumScriptLength} byte.";
			return false;
		}
		error = "";
		return true;
	}

	// Đọc đúng bảng kênh mà hàm FUN_004af950 của client dùng để ánh xạ chuỗi Chat sang channel ID.
	private static bool TryResolveRuntimeChannel(int processId, string configuredCode, out RuntimeChannel runtimeChannel, out string error) {
		runtimeChannel = default;
		error = "";
		if (! LegacyVietnameseText.TryEncode(configuredCode, out byte[] expectedCode, out char unsupportedCharacter)) {
			error = $"Mã kênh chứa ký tự không hỗ trợ U+{(int)unsupportedCharacter:X4}.";
			return false;
		}
		try {
			using MemoryReader reader = new(processId);
			IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
			IntPtr manager = reader.ReadPointer32(IntPtr.Add(moduleBase, ChannelManagerRva));
			if (manager == IntPtr.Zero) {
				error = "ChannelManager bằng 0.";
				return false;
			}
			int count = reader.ReadInt32(IntPtr.Add(manager, ChannelCountOffset));
			IntPtr entries = reader.ReadPointer32(IntPtr.Add(manager, ChannelListOffset));
			if (count <= 0 || count > MaximumChannelCount || entries == IntPtr.Zero) {
				error = $"Bảng kênh không hợp lệ: Count={count}, Entries=0x{entries.ToInt64():X8}.";
				return false;
			}
			for (int index = 0; index < count; index++) {
				IntPtr entry = new(entries.ToInt64() + (long)index * ChannelEntryStride);
				int nameIndex = reader.ReadInt32(IntPtr.Add(entry, ChannelNameIndexOffset));
				if (nameIndex < 0 || nameIndex >= MaximumChannelCount) continue;
				IntPtr codeAddress = new(moduleBase.ToInt64() + ChannelCodeTableRva + (long)nameIndex * ChannelCodeStride);
				byte[] loadedCode = ReadNullTerminatedBytes(reader, codeAddress, 32);
				if (! loadedCode.AsSpan().SequenceEqual(expectedCode)) continue;
				runtimeChannel = new RuntimeChannel(index, reader.ReadInt32(entry), loadedCode);
				return true;
			}
			error = $"Client không nạp mã kênh {Convert.ToHexString(expectedCode)} trong {count} channel.";
			return false;
		} catch (Exception ex) {
			error = $"{ex.GetType().Name}: {ex.Message}";
			return false;
		}
	}

	// Đọc chuỗi byte client đến byte 0 để giữ nguyên mã kênh legacy.
	private static byte[] ReadNullTerminatedBytes(MemoryReader reader, IntPtr address, int maximumLength) {
		byte[] bytes = reader.ReadBytes(address, maximumLength);
		int length = Array.IndexOf(bytes, (byte)0);
		return length < 0 ? bytes : bytes[..length];
	}

	private AdvertiseChannel[] GetChannels() {
		return [
			new AdvertiseChannel("Cận", NearbyChannelCode, settings.NearbyEnabled, Math.Max(1, settings.NearbyDelaySeconds)),
			new AdvertiseChannel("Khu vực", AreaChannelCode, settings.AreaEnabled, Math.Max(1, settings.AreaDelaySeconds)),
			new AdvertiseChannel("Giao", TradeChannelCode, settings.TradeEnabled, Math.Max(1, settings.TradeDelaySeconds)),
			new AdvertiseChannel("Lãnh địa", TerritoryChannelCode, settings.TerritoryEnabled, Math.Max(1, settings.TerritoryDelaySeconds))
		];
	}

	private void LogFailureOnce(Action<string>? log, string failure) {
		if (string.Equals(lastFailure, failure, StringComparison.Ordinal)) return;
		lastFailure = failure;
		log?.Invoke(failure);
	}

	private sealed record AdvertiseChannel(string Name, string Code, bool Enabled, int DelaySeconds);
	private readonly record struct RuntimeChannel(int Index, int Id, byte[] Code);
}
