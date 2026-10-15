namespace Auto.Market;

using System.Security.Cryptography;
using System.Text;
using Auto.Utils;

// The client's sent-message history, the one the Up arrow brings back into the chat input. Found 2026-10-13 on PID 4832
// MaiAnhNhe after the owner typed and sent "AUTOMARK-A", "AUTOMARK-B" + an item link and "AUTOMARK-C" + a map link:
//   [[Game.exe + ChannelManagerRva] + 0x438] = chat input control (vtable 0x00879988, same on PIDs 4832/6596/12520)
//   control + 0x25E + i * 0x200 = message i, raw bytes as sent (links included, zeros inside them kept), 8 slots
//   control + 0x1260 + i * 4 = length of message i (0 = empty slot); the bytes after the length are stale leftovers
// Lengths read back exactly: "AUTOMARK-A" = 10, "AUTOMARK-B" + 152-byte item link = 162, "AUTOMARK-C" + map link = 42.
internal static class ChatHistoryReader {
	private const int ChatInputControlOffset = GameAddresses.Chat.InputControl;
	private const int EntriesOffset = GameAddresses.Chat.HistoryEntries;
	private const int NextSlotOffset = GameAddresses.Chat.HistoryNextSlot;
	private const int EntryStride = GameAddresses.Chat.HistoryEntryStride;
	private const int EntryCount = 8;
	private const int LengthsOffset = GameAddresses.Chat.HistoryLengths;
	private const int ChatInputControlVtableRva = GameAddresses.Vtables.ChatInputControl;

	// Messages in the history, newest first. The 8 slots are a ring: the byte at control + 0x25D is the slot the next
	// message goes to. Measured 2026-10-13: PID 4832 held 0 while slot 7 had the newest ("AUTOMARK-C"); with all 8 slots
	// full, the owner's next message "eeee" went to slot 2 and the byte became 3. Other clients: 4 messages -> 4 (13904,
	// 12520), 2 -> 2 (12336), empty -> 0 (6596).
	public static IReadOnlyList<byte[]> ReadEntries(int processId) {
		List<byte[]> entries = [];
		using MemoryReader reader = new(processId);
		IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
		if (moduleBase == IntPtr.Zero) return entries;
		IntPtr manager = reader.ReadPointer32(IntPtr.Add(moduleBase, ChatEngine.ChannelManagerRva));
		if (manager == IntPtr.Zero) return entries;
		IntPtr control = reader.ReadPointer32(IntPtr.Add(manager, ChatInputControlOffset));
		if (control == IntPtr.Zero) return entries;
		if (reader.ReadInt32(control) != moduleBase.ToInt32() + ChatInputControlVtableRva) return entries;
		byte[] lengths = reader.ReadBytes(IntPtr.Add(control, LengthsOffset), EntryCount * sizeof(int));
		int nextSlot = reader.ReadBytes(IntPtr.Add(control, NextSlotOffset), 1)[0] % EntryCount;
		for (int age = 1; age <= EntryCount; age++) {
			int index = (nextSlot - age + EntryCount) % EntryCount;
			int length = BitConverter.ToInt32(lengths, index * sizeof(int));
			if (length <= 0 || length > Math.Min(EntryStride, ChatEngine.MaximumMessageLength)) continue;
			entries.Add(reader.ReadBytes(IntPtr.Add(control, EntriesOffset + index * EntryStride), length));
		}
		return entries;
	}

	// Splits a sent message into text and links. Item links (0x0B, 152 bytes) and map links (0x0C, length in bytes 1-2) are
	// cut exactly; any other control byte starts a format nobody has measured, so the rest of the message is kept as one
	// block rather than guessing where it ends.
	public static IReadOnlyList<ChatHistorySegment> Split(byte[] message) {
		List<ChatHistorySegment> segments = [];
		int textStart = 0;
		int index = 0;
		while (index < message.Length) {
			if (message[index] >= 0x20) {
				index++;
				continue;
			}
			if (index > textStart) segments.Add(ChatHistorySegment.Text(message[textStart..index]));
			int linkLength = message.Length - index;
			string label = "Dữ liệu động";
			if (message[index] == 0x0B && linkLength >= ChatItemLink.Length) {
				linkLength = ChatItemLink.Length;
				label = "Vật phẩm";
			} else if (message[index] == 0x0C && linkLength >= 5) {
				int mapLinkLength = BitConverter.ToUInt16(message, index + 1);
				int textLength = message[index + 3];
				if (mapLinkLength >= textLength + 5 && mapLinkLength <= linkLength) {
					linkLength = mapLinkLength;
					label = LegacyVietnameseText.Decode(message[(index + 5)..(index + 5 + textLength)]);
				}
			}
			segments.Add(ChatHistorySegment.Link(message[index..(index + linkLength)], label));
			index += linkLength;
			textStart = index;
		}
		if (index > textStart) segments.Add(ChatHistorySegment.Text(message[textStart..index]));
		return segments;
	}

	// Key of a captured link inside ChatSettings.CapturedLinks and its "{link:KEY:label}" placeholder: equal bytes share a key.
	public static string KeyOf(byte[] link) => Convert.ToHexString(SHA1.HashData(link), 0, 4);

	public static string ToPlaceholder(string key, string label) => $"{{link:{key}:{label.Replace("}", "", StringComparison.Ordinal)}}}";

	// CapturedLinks holds "KEY=base64;KEY=base64" so the scalar profile copy (SettingsCopier.CopyScalars) saves it.
	public static Dictionary<string, byte[]> ParseCaptured(string captured) {
		Dictionary<string, byte[]> links = new(StringComparer.OrdinalIgnoreCase);
		foreach (string entry in captured.Split(';', StringSplitOptions.RemoveEmptyEntries)) {
			int separator = entry.IndexOf('=');
			if (separator <= 0) continue;
			try {
				links[entry[..separator]] = Convert.FromBase64String(entry[(separator + 1)..]);
			} catch (FormatException) {
				// A hand-edited profile line that is not base64 is skipped; its placeholder then reports the missing link.
			}
		}
		return links;
	}

	public static string FormatCaptured(IReadOnlyDictionary<string, byte[]> links) {
		StringBuilder builder = new();
		foreach ((string key, byte[] bytes) in links) builder.Append(key).Append('=').Append(Convert.ToBase64String(bytes)).Append(';');
		return builder.ToString();
	}
}

internal sealed record ChatHistorySegment(byte[] Bytes, bool IsLink, string Label) {
	public static ChatHistorySegment Text(byte[] bytes) => new(bytes, false, LegacyVietnameseText.Decode(bytes));

	public static ChatHistorySegment Link(byte[] bytes, string label) => new(bytes, true, label);
}

// One line of the "Lịch sử" list on the Chat tab.
public sealed record ChatHistoryOption(int Number, string Preview, byte[] Message) {
	public override string ToString() => Preview;
}
