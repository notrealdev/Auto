namespace Auto.Market;

using System.Text;
using Auto.Runtime;
using Auto.Utils;

// Chat lines other players sent to this character, pushed to the chat box above the loot box (owner 2026-10-15).
//
// Found with DebugTools/IncomingChatProbe.cs on PID 12972 MaiAnhNhe, messages AUTOIN1..AUTOIN7 sent by XinLỗiEm:
//   - Each line is a heap record: +0x00 row count (1 on these short lines) | +0x04 sender name, zero padded | +0x48 colour prefix ending in
//     "03 00 00 00 FF" | "Sender:" 02 04 message | 00. Bytes after the 00 are leftovers of an older line. 6/6 records matched.
//   - [[[[Game.exe + ChannelManagerRva] + 0x22C] + 0x73C] + 0x47C] is an object whose words hold the line records in
//     arrival order: 09:45 run, AUTOIN3 +0x394, AUTOIN4 +0x398, AUTOIN5 +0x410, AUTOIN6 +0x414, AUTOIN7 +0x464 (other chat
//     lines in between). In the 09:41 run AUTOIN6 sat two slots further back, so the list shifts as new lines arrive.
//   - That path did not survive a new client: PID 24192 (10:39) never logged CHAT_INBOX_LIST. Its probe (10:43) found every
//     AUTOIN record one step from the root instead: [[Game.exe + ChannelManagerRva] + 0x19DC] + 0xE0/+0xEC (one copy of
//     AUTOIN1/2) and +0x20C/+0x210 (the other copy, after XIN CHAO, CHAO BAN TRE... at +0x1FC..). The old client also had
//     records there (+0x0/+0x4 at 09:45 and 09:49), so both lists are read; each message sits in several records, so a line
//     whose text was shown in the last minute is not shown again.
// HYPOTHESIS, not verified: that the 2B 01 colour prefix marks only player lines (other chat kinds with another prefix are
// skipped), and that either list survives relogging. Every new line, the list it came from and
// the lists read go to chat.log (CHAT_INBOX*) so both can be checked.
internal sealed class ChatInboxReader {
	private const int PollMilliseconds = 500;
	private const int DirectListOffset = GameAddresses.Chat.InboxDirectList;
	private static readonly int[] ListPath = GameAddresses.Chat.InboxListPath;
	private const int DuplicateWindowSeconds = 60;
	// Covers the offsets measured (up to +0x464) with room to spare.
	private const int ListReadLength = 0x800;
	// Two items and a map link already reach +0x1BB; a third link would pass 0x200.
	private const int RecordReadLength = 0x400;
	// +0x00 is the number of rows the line takes in the chat box, not a fixed 1 (beta chat.log 11:17, CHAT_INBOX_UNPARSED):
	// 2 on a line of two items, of an item plus a map link and of 29 letters; the layout from +0x04 on was unchanged. System
	// notices (4 and 5 there) are told apart by their colour prefix 13 01 at +0x48, not by this number.
	private const int MaximumRows = 16;
	private const int SenderOffset = GameAddresses.Chat.InboxSender;
	private const int SenderLength = GameAddresses.Chat.InboxSenderLength;
	private const int LinePrefixOffset = GameAddresses.Chat.InboxLinePrefix;
	private static readonly byte[] LinePrefixTail = [0x03, 0x00, 0x00, 0x00, 0xFF];
	private static readonly byte[] MessageSeparator = [0x02, 0x04];
	private const byte ItemLinkMarker = 0x0B;
	private const byte MapLinkMarker = 0x0C;
	private static readonly byte[] ChatLineMarker = [0x3A, 0x02, 0x04];
	private const int UnparsedDumpLength = 0x400;

	// Lines already on the list when reading starts are history, not live chat (owner 2026-10-15): everything read during this
	// window is marked seen without being shown. It restarts whenever Tick has not run for a while (master switch was off).
	private const int WarmupMilliseconds = 3000;
	private const int ResumeGapMilliseconds = 2000;
	private const int MaximumSeen = 4096;

	private DateTime nextPollUtc = DateTime.MinValue;
	private DateTime lastTickUtc = DateTime.MinValue;
	private DateTime warmupUntilUtc = DateTime.MinValue;
	// Every line ever shown or skipped, keyed by record address and content: records are reused for new lines (0x176B6050 held
	// AUTOIN1, 2, 3, 5 and 7 in turn), so the address alone does not identify a line. Never rebuilt from one poll: chat.log
	// 09:52-09:53 shows the path resolving every other poll to an unrelated address (0x0019F4F0, 0x77707B9F, 0xEC) or to 0,
	// and rebuilding from those empty polls made every old line show again on the next good poll.
	private readonly HashSet<(int Record, int ContentHash)> seen = [];
	private readonly Queue<(int Record, int ContentHash)> seenOrder = new();
	private string lastStatus = "";
	// Lists that held lines at the last poll, read first because the paths to them flicker (see Poll).
	private Dictionary<IntPtr, string> knownLists = [];
	// Text hash of each line shown (or skipped during warmup) and when: the copies of one message live in different records.
	private readonly Dictionary<int, DateTime> recentLines = [];
	private readonly HashSet<(int Record, int ContentHash)> unparsedSeen = [];
	private DateTime lastGoodPollUtc = DateTime.MinValue;
	private int badPollsSinceGood;
	private double slowestTickGapMilliseconds;

	public void Tick(GameWindow game, Action<string> log) {
		DateTime now = DateTime.UtcNow;
		double tickGapMilliseconds = (now - lastTickUtc).TotalMilliseconds;
		if (tickGapMilliseconds > ResumeGapMilliseconds) warmupUntilUtc = now.AddMilliseconds(WarmupMilliseconds);
		else slowestTickGapMilliseconds = Math.Max(slowestTickGapMilliseconds, tickGapMilliseconds);
		lastTickUtc = now;
		if (now < nextPollUtc) return;
		nextPollUtc = now.AddMilliseconds(PollMilliseconds);
		try {
			Poll(game, log, now < warmupUntilUtc, now);
		} catch (Exception ex) {
			LogStatus(log, $"CHAT_INBOX_ERROR | {ex.GetType().Name}: {ex.Message}");
		}
	}

	// The list object itself stays put (every CHAT_INBOX_LIST line from 09:52 to 10:24 read 0x0EDEFC58), but the path to it
	// does not: release chat.log 10:30:40 had 55 bad polls in a row, a line shown 34.6 s late. So the last list that held
	// lines is read first. Fresh lookups of both lists follow, for a new client and for lines only one list holds.
	// Polls that find no list are skipped silently, or a status line would be written every 500 ms.
	private void Poll(GameWindow game, Action<string> log, bool warmup, DateTime now) {
		using MemoryReader reader = new(game.ProcessId);
		List<(IntPtr Address, string Name)> candidates = [.. knownLists.Select(entry => (entry.Key, entry.Value))];
		IntPtr moduleBase = reader.GetModuleBase(GameAddresses.ModuleName);
		IntPtr root = reader.ReadPointer32(IntPtr.Add(moduleBase, ChatEngine.ChannelManagerRva));
		if (root != IntPtr.Zero) {
			candidates.Add((reader.ReadPointer32(IntPtr.Add(root, DirectListOffset)), "Gốc+0x19DC"));
			IntPtr node = root;
			foreach (int offset in ListPath) {
				if (node == IntPtr.Zero) break;
				node = reader.ReadPointer32(IntPtr.Add(node, offset));
			}
			candidates.Add((node, "Gốc+0x22C+0x73C+0x47C"));
		}
		Dictionary<IntPtr, string> good = [];
		foreach ((IntPtr address, string name) in candidates) {
			if (address == IntPtr.Zero || good.ContainsKey(address)) continue;
			if (ReadList(game, reader, address, name, log, warmup, now)) good[address] = name;
		}
		if (good.Count == 0) {
			badPollsSinceGood++;
			return;
		}
		knownLists = good;
		lastGoodPollUtc = now;
		badPollsSinceGood = 0;
		slowestTickGapMilliseconds = 0;
		LogStatus(log, $"CHAT_INBOX_LIST | {string.Join(" ; ", good.OrderBy(entry => entry.Value).Select(entry => $"{entry.Value}=0x{entry.Key.ToInt64():X8}"))}");
	}

	// False when the list holds no chat line.
	private bool ReadList(GameWindow game, MemoryReader reader, IntPtr node, string listName, Action<string> log, bool warmup, DateTime now) {
		byte[] list = reader.ReadBytes(node, ListReadLength);
		bool anyLine = false;
		for (int offset = 0; offset + 4 <= list.Length; offset += 4) {
			int pointer = BitConverter.ToInt32(list, offset);
			if (pointer == 0) continue;
			byte[] record = reader.ReadBytes(new IntPtr(pointer), RecordReadLength);
			if (! TryParse(record, out string sender, out byte[] line, out string message, out bool hasLink)) {
				LogUnparsed(reader, pointer, record, listName, offset, log, warmup);
				continue;
			}
			anyLine = true;
			int contentHash = ComputeHash(line);
			if (! MarkSeen((pointer, contentHash))) continue;
			bool copy = recentLines.TryGetValue(contentHash, out DateTime shownUtc) && (now - shownUtc).TotalSeconds < DuplicateWindowSeconds;
			if (! copy) RememberLine(contentHash, now);
			if (copy && hasLink && ! warmup) log($"CHAT_INBOX_COPY | DanhSách={listName} | Ô=+0x{offset:X} | BảnGhi=0x{pointer:X8} | NgườiGửi={sender} | {message} | ĐãHiệnCáchĐâyMs={(now - shownUtc).TotalMilliseconds:0}");
			if (warmup || copy) continue;
			if (string.Equals(sender, game.CharacterName, StringComparison.Ordinal)) continue;
			string raw = hasLink ? $" | DòngHex={Convert.ToHexString(line)}" : "";
			log($"CHAT_INBOX | DanhSách={listName} | Ô=+0x{offset:X} | BảnGhi=0x{pointer:X8} | NgườiGửi={sender} | {message} | {DescribeTiming(now)}{raw}");
			ChatFeed.Add(game.ProcessId, sender, message);
		}
		return anyLine;
	}

	// Owner 2026-10-15 (beta 11:08): a message of one or two items plus a map link never showed, while single items and a map
	// link alone did. Records that look like a chat line (":" 02 04 near the start) but fail TryParse are dumped once, with
	// 0x400 bytes in case the line runs past RecordReadLength.
	private void LogUnparsed(MemoryReader reader, int pointer, byte[] record, string listName, int offset, Action<string> log, bool warmup) {
		// Only the 2B 01 colour of private lines: channel lines (05 .. 01 DC DC DC) filled chat.log every few seconds (16:32).
		if (warmup || record.Length < 0x100 || record[LinePrefixOffset] != 0x2B || record.AsSpan(0, 0x100).IndexOf(ChatLineMarker) < 0) return;
		if (! unparsedSeen.Add((pointer, ComputeHash(record)))) return;
		if (unparsedSeen.Count > MaximumSeen) unparsedSeen.Clear();
		byte[] wide = reader.ReadBytes(new IntPtr(pointer), UnparsedDumpLength);
		log($"CHAT_INBOX_UNPARSED | DanhSách={listName} | Ô=+0x{offset:X} | BảnGhi=0x{pointer:X8} | +0x00={BitConverter.ToInt32(record, 0)} | Hex={Convert.ToHexString(wide)}");
	}

	private void RememberLine(int contentHash, DateTime now) {
		recentLines[contentHash] = now;
		if (recentLines.Count <= MaximumSeen) return;
		foreach (int stale in recentLines.Where(entry => (now - entry.Value).TotalSeconds >= DuplicateWindowSeconds).Select(entry => entry.Key).ToList()) {
			recentLines.Remove(stale);
		}
	}

	// Owner 2026-10-15: some lines showed up a while after they were sent. These say whether the list was unreadable for a
	// stretch (bad polls) or this Tick was not called (master tick held up elsewhere).
	private string DescribeTiming(DateTime now) {
		string previousGood = lastGoodPollUtc == DateTime.MinValue ? "chưa có" : $"{(now - lastGoodPollUtc).TotalMilliseconds:0}";
		return $"TừLầnĐọcĐượcTrướcMs={previousGood} | LầnĐọcHỏngXen={badPollsSinceGood} | NhịpGọiChậmNhấtMs={slowestTickGapMilliseconds:0}";
	}

	// False when the line was already seen.
	private bool MarkSeen((int Record, int ContentHash) key) {
		if (! seen.Add(key)) return false;
		seenOrder.Enqueue(key);
		while (seenOrder.Count > MaximumSeen) seen.Remove(seenOrder.Dequeue());
		return true;
	}

	// Sender from the name field; message = the part of the line after 02 04 (after "Sender:" when 02 04 is missing).
	internal static bool TryParse(byte[] record, out string sender, out byte[] line, out string message, out bool hasLink) {
		sender = "";
		line = [];
		message = "";
		hasLink = false;
		if (record.Length < LinePrefixOffset + 2 || BitConverter.ToInt32(record, 0) is < 1 or > MaximumRows) return false;
		if (record[LinePrefixOffset] != 0x2B || record[LinePrefixOffset + 1] != 0x01) return false;
		int prefixEnd = record.AsSpan(LinePrefixOffset).IndexOf(LinePrefixTail);
		if (prefixEnd < 0) return false;
		int lineStart = LinePrefixOffset + prefixEnd + LinePrefixTail.Length;
		int lineEnd = FindLineEnd(record, lineStart);
		if (lineEnd <= lineStart) return false;
		line = record[lineStart..lineEnd];
		int nameEnd = Array.IndexOf(record, (byte)0, SenderOffset, SenderLength);
		sender = LegacyVietnameseText.Decode(record[SenderOffset..(nameEnd < 0 ? SenderOffset + SenderLength : nameEnd)]);
		int separator = line.AsSpan().IndexOf(MessageSeparator);
		int messageStart = separator >= 0 ? separator + MessageSeparator.Length : Array.IndexOf(line, (byte)':') + 1;
		// Links are shown by kind or name only (owner 2026-10-15): "[Vật phẩm]" for an item, the map text for a map link,
		// "[Dữ liệu động]" for anything else. The CHAT_INBOX log line carries the raw bytes when a link is present.
		StringBuilder builder = new();
		foreach (ChatHistorySegment segment in ChatHistoryReader.Split(line[messageStart..])) {
			// The 0x0B marker alone decides, so an item link cut short by the end of the read record is still named.
			string label = segment.IsLink && segment.Bytes[0] == ItemLinkMarker ? "Vật phẩm" : segment.Label;
			builder.Append(segment.IsLink ? $"[{label}]" : label);
			hasLink |= segment.IsLink;
		}
		message = builder.ToString().Trim();
		return message.Length > 0;
	}

	// The line ends at the first 00 outside a link. Links carry 00 bytes inside them and keep the layout of sent links
	// (beta chat.log 10:54:38: item id at link byte 31 and the 1 at byte 35, as in ChatItemLink; 10:55:50: map link
	// "0C 20 00 0F 03 Ngọc Tuyền[0,0] 20 00 00 00 ..." = 32 bytes, map 32), so they are skipped whole. Stopping at their
	// first 00 cut every item link whose second byte is 0 down to the same "0B": two different items from one sender then
	// had the same text and the second was dropped as a copy of the first.
	private static int FindLineEnd(byte[] record, int lineStart) {
		int index = lineStart;
		while (index < record.Length && record[index] != 0) {
			if (record[index] == ItemLinkMarker) {
				index += ChatItemLink.Length;
			} else if (record[index] == MapLinkMarker && index + 3 <= record.Length) {
				index += Math.Max((int)BitConverter.ToUInt16(record, index + 1), 5);
			} else {
				index++;
			}
		}
		return Math.Min(index, record.Length);
	}

	private static int ComputeHash(byte[] bytes) {
		HashCode hash = new();
		hash.AddBytes(bytes);
		return hash.ToHashCode();
	}

	// Status lines only when they change: this runs every 500 ms.
	private void LogStatus(Action<string> log, string status) {
		if (string.Equals(status, lastStatus, StringComparison.Ordinal)) return;
		lastStatus = status;
		log(status);
	}
}
