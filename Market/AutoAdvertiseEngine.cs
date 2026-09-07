namespace Auto.Market;

using Auto.Attack;
using Auto.Utils;

internal sealed class AutoAdvertiseEngine {
	private const int InterChannelDelayMilliseconds = 5000;
	private const int MaximumMessageLength = 199;
	// Cập nhật sau bản game 2026-08-28 (PE TimeDateStamp 0x6A8DD698): xác nhận bằng đọc byte thật qua ChannelManagerProbe (PID=26056) — delta +0x2020 khớp Count=7 hợp lệ và cả 4 mã kênh (Cận/Giao/Khu vực/Lãnh địa) khớp tuyệt đối với hằng số đã có sẵn bên dưới.
	private const int ChannelManagerRva = 0x004F5174;
	private const int ChannelCodeTableRva = 0x004F51B8;
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
	private string pendingResetReason = "";
	private string lastResetLine = "";

	public bool IsConfigured => settings.AutoAdvertise && ! string.IsNullOrWhiteSpace(settings.AdvertiseText) &&
		(settings.NearbyEnabled || settings.AreaEnabled || settings.TradeEnabled || settings.TerritoryEnabled);

	public AutoAdvertiseEngine(Settings settings, AutoFsAttackTransport transport) {
		this.settings = settings;
		this.transport = transport;
	}

	// Xóa toàn bộ lịch cũ và giữ nguyên cổng chờ giữa hai kênh để lần gửi kế tiếp không dồn dập.
	public void RestartSchedule() {
		Array.Clear(lastSentUtc);
		nextChannelUtc = DateTime.UtcNow.AddMilliseconds(InterChannelDelayMilliseconds);
		lastFailure = "";
	}

	// Ghi nhận lý do reset do người dùng đổi cấu hình để Tick ghi log đúng một lần thay vì mỗi vòng lặp.
	public void RestartScheduleFromSettings(string reason) {
		RestartSchedule();
		pendingResetReason = reason;
	}

	// Chạy bốn bộ đếm độc lập theo đúng thứ tự Cận, Khu vực, Giao và Lãnh địa của AutoFS.
	public void Tick(int processId, IntPtr gameWindowHandle, Action<string>? log) {
		if (pendingResetReason.Length > 0) {
			// Ô nội dung bắn sự kiện theo từng ký tự gõ, nên chỉ ghi khi trạng thái mô tả thực sự khác lần trước.
			string resetLine = $"CHAT_SCHEDULE_RESET | Reason={pendingResetReason} | AutoAdvertise={settings.AutoAdvertise} | {DescribeChannels()}";
			pendingResetReason = "";
			if (! string.Equals(lastResetLine, resetLine, StringComparison.Ordinal)) {
				lastResetLine = resetLine;
				log?.Invoke(resetLine);
			}
		}
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
				LogFailureOnce(log, $"CHAT_CHANNEL_NOT_FOUND | Channel={channel.Name} | Reason={resolveError}");
				return;
			}
			if (! TryBuildMessage(settings.AdvertiseText, out byte[] message, out string buildError)) {
				LogFailureOnce(log, $"CHAT_BUILD_FAILED | Channel={channel.Name} | Reason={buildError}");
				return;
			}
			if (! transport.TrySendChat(gameWindowHandle, message, runtimeChannel.Index, out string sendError)) {
				LogFailureOnce(log, $"CHAT_SEND_FAILED | Channel={channel.Name} | RuntimeIndex={runtimeChannel.Index} | RuntimeId={runtimeChannel.Id} | MessageBytes={message.Length} | MessageHex={Convert.ToHexString(message)} | Reason={sendError}");
				return;
			}
			// Tính chu kỳ từ thời điểm native đã xử lý xong để hai lần gửi thực tế không ngắn hơn delay cấu hình.
			DateTime dispatchedUtc = DateTime.UtcNow;
			lastFailure = "";
			lastSentUtc[index] = dispatchedUtc;
			nextChannelUtc = dispatchedUtc.AddMilliseconds(InterChannelDelayMilliseconds);
			log?.Invoke($"CHAT_DISPATCHED | Channel={channel.Name} | RuntimeIndex={runtimeChannel.Index} | RuntimeId={runtimeChannel.Id} | DelaySeconds={channel.DelaySeconds} | MessageBytes={message.Length} | MessageHex={Convert.ToHexString(message)} | NativeAccepted=True | Source=FUN_004637c0_chain_via_34_312");
			return;
		}
	}

	// Native tự lấy mã kênh và channel type từ chỉ số kênh rồi chạy đúng chuỗi gửi chat của client,
	// nên ở đây chỉ cần mã hóa nội dung thô sang bảng mã legacy mà client dùng cho khung nhập chat.
	private static bool TryBuildMessage(string content, out byte[] message, out string error) {
		string singleLineContent = content.Replace("\r\n", " ", StringComparison.Ordinal)
			.Replace("\n", " ", StringComparison.Ordinal)
			.Replace("\r", " ", StringComparison.Ordinal);
		if (! LegacyVietnameseText.TryEncode(singleLineContent, out byte[] messageBytes, out char unsupportedCharacter)) {
			message = [];
			error = $"Ký tự không được client hỗ trợ: U+{(int)unsupportedCharacter:X4}.";
			return false;
		}
		message = messageBytes;
		if (message.Length > MaximumMessageLength) {
			error = $"Nội dung dài {message.Length} byte, giới hạn AutoFS là {MaximumMessageLength} byte.";
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

	// Mô tả trạng thái bật/tắt và delay của bốn kênh để log reset tự giải thích được các khoảng trống về sau.
	private string DescribeChannels() {
		AdvertiseChannel[] channels = GetChannels();
		string[] parts = new string[channels.Length];
		for (int index = 0; index < channels.Length; index++) {
			parts[index] = $"{channels[index].Name}={channels[index].Enabled}/{channels[index].DelaySeconds}s";
		}
		return $"Channels=[{string.Join(", ", parts)}]";
	}

	private void LogFailureOnce(Action<string>? log, string failure) {
		if (string.Equals(lastFailure, failure, StringComparison.Ordinal)) return;
		lastFailure = failure;
		log?.Invoke(failure);
	}

	private sealed record AdvertiseChannel(string Name, string Code, bool Enabled, int DelaySeconds);
	private readonly record struct RuntimeChannel(int Index, int Id, byte[] Code);
}
