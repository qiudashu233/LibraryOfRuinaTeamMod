using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace RuinaCoop
{
    // Committed origin data only. None of the reserved-state UI getters is safe here.
    internal static class PassiveMirror
    {
        internal const int MaxSlots = 64;
        internal const int EmptyId = 9999999;
        private const int MaxStat = 1000;
        private const int MaxPacketBytes = 65536;
        internal const int MaxExpandedBytes = 4 * 1024 * 1024;
        private const int CompressedHeaderBytes = 12;
        private const int MaxCompressedBytes = MaxPacketBytes - CompressedHeaderBytes;
        private const PassiveSlotFlags AllSlotFlags = PassiveSlotFlags.CanGive | PassiveSlotFlags.Locked |
            PassiveSlotFlags.Negative | PassiveSlotFlags.Hidden | PassiveSlotFlags.CanReceive | PassiveSlotFlags.Given;
        private const PassiveBookFlags AllBookFlags = PassiveBookFlags.ReceiverAllowed | PassiveBookFlags.SourceAllowed | PassiveBookFlags.Unsupported;
        private sealed class CaptureRow
        {
            internal ProgressSnapshot.PassiveBookEntry Entry;
            internal ProgressSnapshot.CoreBookEntry Core;
            internal BookModel Book;
            internal List<PassiveModel> Models;
        }
        private static void Unavailable(ProgressSnapshot snapshot, PassivesReason reason)
        { snapshot.PassiveBooks.Clear(); snapshot.PassivesAvailable = false; snapshot.PassivesReason = reason; }
        private static void Unsupported(ProgressSnapshot.PassiveBookEntry entry)
        { entry.Flags = PassiveBookFlags.Unsupported; entry.MaxCost = 0; entry.Slots.Clear(); entry.SourceTokens.Clear(); entry.ReceiverBookToken = 0; }
        private static bool ValidSlotBounds(ProgressSnapshot.PassiveSlotEntry slot)
        {
            return slot != null && slot.OriginId > 0 && slot.CurrentId > 0 && (slot.Flags & ~AllSlotFlags) == 0 &&
                slot.Cost >= -MaxStat && slot.Cost <= MaxStat && slot.CurrentCost >= -MaxStat && slot.CurrentCost <= MaxStat &&
                slot.InnerTypeId >= -1 && slot.InnerTypeId <= 1000000 && slot.OriginRarity <= 4 && slot.CurrentRarity <= 4;
        }
        private static int Id(PassiveXmlInfo xml)
        {
            var id = xml == null ? null : xml.id;
            return id != null && id.IsBasic() && id.id > 0 ? id.id : 0;
        }
        internal static void Capture(ProgressSnapshot snapshot)
        {
            if (snapshot == null) throw new ArgumentNullException("snapshot");
            Unavailable(snapshot, PassivesReason.NotCaptured);
            if (!snapshot.CoreBooksAvailable) { Unavailable(snapshot, PassivesReason.CoreInventoryUnavailable); return; }
            try
            {
                var instances = new Dictionary<int, ulong>(); var duplicates = new HashSet<int>();
                foreach (var core in snapshot.CoreBooks)
                    if (core.BookInstanceId > 0)
                    { if (instances.ContainsKey(core.BookInstanceId)) duplicates.Add(core.BookInstanceId); else instances.Add(core.BookInstanceId, core.BookToken); }
                foreach (var duplicate in duplicates) instances.Remove(duplicate);
                var rows = new Dictionary<ulong, CaptureRow>();
                foreach (var core in snapshot.CoreBooks)
                {
                    var entry = new ProgressSnapshot.PassiveBookEntry { BookToken = core.BookToken };
                    var row = new CaptureRow { Core = core, Entry = entry, Book = core.BookReference as BookModel };
                    rows.Add(core.BookToken, row);
                    try
                    {
                        var book = row.Book;
                        if (book == null || book.ClassInfo == null || book.ClassInfo.isError || core.BookId <= 0 || book.originData == null ||
                            book.originData.equipedBookIdListInPassive == null) { Unsupported(entry); continue; }
                        entry.MaxCost = book.GetMaxPassiveCost();
                        if (entry.MaxCost < 0 || entry.MaxCost > 12) { Unsupported(entry); continue; }
                        row.Models = book.GetPassiveModelList();
                        if (row.Models == null || row.Models.Count > MaxSlots) { Unsupported(entry); continue; }
                        if (book.originData.equipedBookIdListInPassive.Count > 4) { Unsupported(entry); continue; }
                        if (book.originData.equipedPassiveBookInstanceId != -1)
                        { if (!instances.TryGetValue(book.originData.equipedPassiveBookInstanceId, out entry.ReceiverBookToken)) throw new InvalidOperationException(); }
                        foreach (var instance in book.originData.equipedBookIdListInPassive)
                        { ulong token; if (!instances.TryGetValue(instance, out token)) throw new InvalidOperationException(); entry.SourceTokens.Add(token); }
                        if (entry.ReceiverBookToken == entry.BookToken || entry.SourceTokens.Contains(entry.BookToken) ||
                            entry.SourceTokens.Distinct().Count() != entry.SourceTokens.Count) throw new InvalidOperationException();
                        foreach (var model in row.Models)
                        {
                            if (model == null || model.originData == null || Id(model.originpassive) == 0 || Id(model.originData.currentpassive) == 0)
                                throw new InvalidOperationException();
                            var native = model.originpassive; var current = model.originData.currentpassive;
                            var slot = new ProgressSnapshot.PassiveSlotEntry
                            {
                                OriginId = Id(native), CurrentId = Id(current), Cost = native.cost, CurrentCost = current.cost,
                                InnerTypeId = native.InnerTypeId, OriginRarity = (byte)native.rare, CurrentRarity = (byte)current.rare,
                                CurrentNegative = current.isNegative
                            };
                            if (native.CanGivePassive) slot.Flags |= PassiveSlotFlags.CanGive;
                            if (native.isLock) slot.Flags |= PassiveSlotFlags.Locked;
                            if (native.isNegative) slot.Flags |= PassiveSlotFlags.Negative;
                            if (native.isHide) slot.Flags |= PassiveSlotFlags.Hidden;
                            if (native.CanReceivePassive) slot.Flags |= PassiveSlotFlags.CanReceive;
                            if (model.originData.givePassiveBookId != book.instanceId)
                            {
                                if (book.originData.equipedPassiveBookInstanceId == -1 ||
                                    model.originData.givePassiveBookId != book.originData.equipedPassiveBookInstanceId)
                                    throw new InvalidOperationException();
                                slot.Flags |= PassiveSlotFlags.Given;
                            }
                            if (model.originData.receivepassivebookId != book.instanceId)
                            { if (!instances.TryGetValue(model.originData.receivepassivebookId, out slot.SourceBookToken)) throw new InvalidOperationException(); }
                            if (!ValidSlotBounds(slot) || slot.SourceBookToken == 0 && slot.CurrentId != slot.OriginId ||
                                slot.SourceBookToken != 0 && !entry.SourceTokens.Contains(slot.SourceBookToken)) throw new InvalidOperationException();
                            entry.Slots.Add(slot);
                        }
                    }
                    catch { Unsupported(entry); }
                }
                // Resolve an inherited slot by the source's original slot and committed
                // recipient, never by the flattened effective-passive presentation list.
                foreach (var row in rows.Values)
                {
                    if ((row.Entry.Flags & PassiveBookFlags.Unsupported) != 0) continue;
                    foreach (var slot in row.Entry.Slots)
                    {
                        if (slot.SourceBookToken == 0) continue;
                        CaptureRow source;
                        if (!rows.TryGetValue(slot.SourceBookToken, out source) || source.Models == null ||
                            (source.Entry.Flags & PassiveBookFlags.Unsupported) != 0) { Unsupported(row.Entry); break; }
                        var matches = new List<int>();
                        for (var i = 0; i < source.Entry.Slots.Count; i++)
                            if (source.Entry.Slots[i].OriginId == slot.CurrentId && source.Models[i].originData != null &&
                                source.Models[i].originData.givePassiveBookId == row.Book.instanceId) matches.Add(i);
                        if (matches.Count != 1) { Unsupported(row.Entry); break; }
                        slot.SourceSlotIndex = (byte)matches[0];
                        var native = source.Entry.Slots[matches[0]];
                        if (slot.CurrentCost != native.Cost || slot.CurrentRarity != native.OriginRarity ||
                            slot.CurrentNegative != ((native.Flags & PassiveSlotFlags.Negative) != 0)) { Unsupported(row.Entry); break; }
                    }
                }
                // Propagate unsupported relation endpoints instead of inventing a partial graph.
                var changed = true;
                while (changed)
                {
                    changed = false;
                    foreach (var row in rows.Values)
                    {
                        var entry = row.Entry;
                        if ((entry.Flags & PassiveBookFlags.Unsupported) != 0) continue;
                        if (entry.SourceTokens.Any(token => !rows.ContainsKey(token) || (rows[token].Entry.Flags & PassiveBookFlags.Unsupported) != 0 ||
                                rows[token].Entry.ReceiverBookToken != entry.BookToken) ||
                            entry.ReceiverBookToken != 0 && (!rows.ContainsKey(entry.ReceiverBookToken) ||
                                (rows[entry.ReceiverBookToken].Entry.Flags & PassiveBookFlags.Unsupported) != 0 ||
                                !rows[entry.ReceiverBookToken].Entry.SourceTokens.Contains(entry.BookToken)))
                        { Unsupported(entry); changed = true; }
                        if ((entry.Flags & PassiveBookFlags.Unsupported) != 0) continue;
                        for (var i = 0; i < entry.Slots.Count; i++)
                        {
                            if ((entry.Slots[i].Flags & PassiveSlotFlags.Given) == 0) continue;
                            CaptureRow receiver;
                            var sourceIndex = i;
                            if (entry.ReceiverBookToken == 0 || !rows.TryGetValue(entry.ReceiverBookToken, out receiver) ||
                                receiver.Entry.Slots.Count(slot => slot.SourceBookToken == entry.BookToken && slot.SourceSlotIndex == sourceIndex) != 1)
                            { Unsupported(entry); changed = true; break; }
                        }
                    }
                }
                foreach (var row in rows.Values)
                {
                    var entry = row.Entry; if ((entry.Flags & PassiveBookFlags.Unsupported) != 0) continue;
                    var allowed = row.Core.Kind == CoreBookKind.Ordinary && entry.Slots.Count > 0;
                    if (allowed && entry.ReceiverBookToken == 0 && (row.Core.Flags & ~CoreBookFlags.Equipped) == 0)
                        entry.Flags |= PassiveBookFlags.ReceiverAllowed;
                    if (allowed && entry.SourceTokens.Count == 0 && (row.Core.Flags & ~CoreBookFlags.PassiveBound) == 0)
                        entry.Flags |= PassiveBookFlags.SourceAllowed;
                }
                var baseLength = snapshot.Encode(1).Length;
                snapshot.PassiveBooks.AddRange(rows.Values.Select(row => row.Entry).OrderBy(entry => entry.BookToken));
                snapshot.PassivesAvailable = true; snapshot.PassivesReason = PassivesReason.None;
                if (baseLength + EncodeWireData(snapshot).Length - 6 > MaxPacketBytes) Unavailable(snapshot, PassivesReason.PacketLimit);
            }
            catch { Unavailable(snapshot, PassivesReason.CaptureFailed); }
        }
        internal static byte[] EncodeContent(ProgressSnapshot snapshot)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                string reason; if (!ValidateData(snapshot, out reason)) throw new InvalidOperationException(reason);
                // Revision comparison stays independent of the runtime's Deflate encoder.
                writer.Write((byte)(snapshot.PassivesAvailable ? 1 : 0)); writer.Write((byte)snapshot.PassivesReason);
                writer.Write((ushort)0); WriteBooks(writer, snapshot, true); writer.Flush(); return stream.ToArray();
            }
        }
        internal static byte[] EncodeWireData(ProgressSnapshot snapshot)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            { WriteData(writer, snapshot, false); writer.Flush(); return stream.ToArray(); }
        }
        internal static void WriteData(BinaryWriter writer, ProgressSnapshot snapshot, bool canonical)
        {
            string reason; if (!ValidateData(snapshot, out reason)) throw new InvalidOperationException(reason);
            writer.Write((byte)(snapshot.PassivesAvailable ? 1 : 0)); writer.Write((byte)snapshot.PassivesReason);
            if (!snapshot.PassivesAvailable) { writer.Write((ushort)0); writer.Write((ushort)0); return; }
            byte[] raw;
            using (var stream = new MemoryStream()) using (var body = new BinaryWriter(stream))
            { WriteBooks(body, snapshot, canonical); body.Flush(); raw = stream.ToArray(); }
            if (raw.Length > MaxExpandedBytes) throw new InvalidOperationException("Passive expanded payload exceeds bounds.");
            byte[] packed;
            using (var stream = new MemoryStream())
            {
                using (var compressor = new DeflateStream(stream, CompressionLevel.Optimal, true))
                    compressor.Write(raw, 0, raw.Length);
                packed = stream.ToArray();
            }
            writer.Write((byte)1); writer.Write((byte)0); // Deflate codec; reserved.
            writer.Write(raw.Length); writer.Write(packed.Length); writer.Write(packed);
        }
        private static void WriteBooks(BinaryWriter writer, ProgressSnapshot snapshot, bool canonical)
        {
            writer.Write((ushort)snapshot.PassiveBooks.Count);
            var entries = canonical ? snapshot.PassiveBooks.OrderBy(entry => entry.BookToken) : (IEnumerable<ProgressSnapshot.PassiveBookEntry>)snapshot.PassiveBooks;
            foreach (var entry in entries)
            {
                writer.Write(entry.BookToken); writer.Write((byte)((byte)entry.Flags | (entry.ReceiverBookToken != 0 ? 128 : 0)));
                writer.Write((byte)entry.MaxCost); writer.Write(entry.MaxSources);
                if (entry.ReceiverBookToken != 0) writer.Write(entry.ReceiverBookToken);
                writer.Write((byte)entry.SourceTokens.Count);
                var sources = canonical ? entry.SourceTokens.OrderBy(token => token) : (IEnumerable<ulong>)entry.SourceTokens;
                foreach (var source in sources) writer.Write(source);
                writer.Write((byte)entry.Slots.Count);
                foreach (var slot in entry.Slots)
                {
                    var differs = slot.CurrentId != slot.OriginId || slot.CurrentCost != slot.Cost || slot.CurrentRarity != slot.OriginRarity ||
                        slot.CurrentNegative != ((slot.Flags & PassiveSlotFlags.Negative) != 0);
                    writer.Write(slot.OriginId); writer.Write((short)slot.Cost); writer.Write(slot.InnerTypeId);
                    writer.Write((byte)((byte)slot.Flags | (differs ? 64 : 0) | (slot.SourceBookToken != 0 ? 128 : 0))); writer.Write(slot.OriginRarity);
                    if (differs) { writer.Write(slot.CurrentId); writer.Write((short)slot.CurrentCost); writer.Write((byte)(slot.CurrentNegative ? 1 : 0)); writer.Write(slot.CurrentRarity); }
                    if (slot.SourceBookToken != 0) { writer.Write(slot.SourceBookToken); writer.Write(slot.SourceSlotIndex); }
                }
            }
        }
        internal static bool TryReadData(BinaryReader reader, ProgressSnapshot snapshot, out string reason)
        {
            reason = null; var available = reader.ReadByte(); if (available > 1) return Reject(out reason, "Passive availability flag is invalid.");
            snapshot.PassivesAvailable = available != 0; snapshot.PassivesReason = (PassivesReason)reader.ReadByte();
            if (!snapshot.PassivesAvailable)
            {
                if (reader.ReadUInt16() != 0 || reader.ReadUInt16() != 0) return Reject(out reason, "Unavailable passive payload is invalid.");
                return ValidateData(snapshot, out reason);
            }
            if (snapshot.PassivesReason != PassivesReason.None || reader.ReadByte() != 1 || reader.ReadByte() != 0)
                return Reject(out reason, "Passive compression header is invalid.");
            var expandedLength = reader.ReadInt32(); var compressedLength = reader.ReadInt32();
            if (expandedLength < 2 || expandedLength > MaxExpandedBytes || compressedLength <= 0 || compressedLength > MaxCompressedBytes ||
                compressedLength > reader.BaseStream.Length - reader.BaseStream.Position)
                return Reject(out reason, "Passive compression lengths exceed bounds.");
            var compressed = reader.ReadBytes(compressedLength);
            if (compressed.Length != compressedLength) return Reject(out reason, "Passive compression payload is truncated.");
            var raw = new byte[expandedLength];
            try
            {
                using (var input = new ExactDeflateInput(compressed))
                using (var decompressor = new DeflateStream(input, CompressionMode.Decompress, true))
                {
                    var offset = 0;
                    while (offset < raw.Length)
                    {
                        var read = decompressor.Read(raw, offset, raw.Length - offset);
                        if (read == 0) return Reject(out reason, "Passive expanded payload is truncated.");
                        offset += read;
                    }
                    // An extra output byte rejects expansion beyond the declared bound.
                    // ReadPastEnd also catches a missing Deflate terminator when some
                    // runtimes return EOF silently after producing the expected bytes.
                    if (decompressor.ReadByte() != -1 || input.ReadPastEnd || input.Position != input.Length)
                        return Reject(out reason, "Passive compressed stream is incomplete or contains trailing data.");
                }
                using (var body = new BinaryReader(new MemoryStream(raw, false)))
                {
                    if (!TryReadBooks(body, snapshot, out reason)) return false;
                    if (body.BaseStream.Position != body.BaseStream.Length)
                        return Reject(out reason, "Passive expanded stream contains trailing data.");
                }
                return ValidateData(snapshot, out reason);
            }
            catch (IOException) { return Reject(out reason, "Passive compressed or expanded stream is invalid."); }
        }
        // Limit read-ahead so Deflate cannot silently consume an appended second
        // stream or trailing bytes. At most 64 KiB of input is read one byte at a time.
        private sealed class ExactDeflateInput : Stream
        {
            private readonly MemoryStream _input;
            internal bool ReadPastEnd;
            internal ExactDeflateInput(byte[] data) { _input = new MemoryStream(data, false); }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return _input.Length; } }
            public override long Position { get { return _input.Position; } set { throw new NotSupportedException(); } }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (count == 0) return 0;
                var read = _input.Read(buffer, offset, Math.Min(count, 1));
                if (read == 0) ReadPastEnd = true;
                return read;
            }
            public override int ReadByte()
            {
                var value = _input.ReadByte(); if (value == -1) ReadPastEnd = true; return value;
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            protected override void Dispose(bool disposing) { if (disposing) _input.Dispose(); base.Dispose(disposing); }
        }
        private static bool TryReadBooks(BinaryReader reader, ProgressSnapshot snapshot, out string reason)
        {
            reason = null;
            var count = reader.ReadUInt16(); if (count > EquipmentMirror.MaxBooks) return Reject(out reason, "Passive book count exceeds bounds.");
            for (var i = 0; i < count; i++)
            {
                var entry = new ProgressSnapshot.PassiveBookEntry { BookToken = reader.ReadUInt64() };
                var flags = reader.ReadByte(); if ((flags & 120) != 0) return Reject(out reason, "Passive book flags are invalid.");
                entry.Flags = (PassiveBookFlags)(flags & 7); entry.MaxCost = reader.ReadByte(); entry.MaxSources = reader.ReadByte();
                if ((flags & 128) != 0) entry.ReceiverBookToken = reader.ReadUInt64();
                var sourceCount = reader.ReadByte(); if (sourceCount > 4) return Reject(out reason, "Passive source count exceeds bounds.");
                for (var s = 0; s < sourceCount; s++) entry.SourceTokens.Add(reader.ReadUInt64());
                var slotCount = reader.ReadByte(); if (slotCount > MaxSlots) return Reject(out reason, "Passive slot count exceeds bounds.");
                for (var s = 0; s < slotCount; s++)
                {
                    var slot = new ProgressSnapshot.PassiveSlotEntry { OriginId = reader.ReadInt32(), Cost = reader.ReadInt16(), InnerTypeId = reader.ReadInt32() };
                    flags = reader.ReadByte(); slot.Flags = (PassiveSlotFlags)(flags & 63); slot.OriginRarity = reader.ReadByte();
                    slot.CurrentId = slot.OriginId; slot.CurrentCost = slot.Cost; slot.CurrentNegative = (slot.Flags & PassiveSlotFlags.Negative) != 0; slot.CurrentRarity = slot.OriginRarity;
                    if ((flags & 64) != 0)
                    {
                        slot.CurrentId = reader.ReadInt32(); slot.CurrentCost = reader.ReadInt16(); var negative = reader.ReadByte();
                        if (negative > 1) return Reject(out reason, "Passive negative flag is invalid.");
                        slot.CurrentNegative = negative != 0; slot.CurrentRarity = reader.ReadByte();
                    }
                    if ((flags & 128) != 0) { slot.SourceBookToken = reader.ReadUInt64(); slot.SourceSlotIndex = reader.ReadByte(); }
                    entry.Slots.Add(slot);
                }
                snapshot.PassiveBooks.Add(entry);
            }
            return true;
        }
        internal static bool ValidateData(ProgressSnapshot snapshot, out string reason)
        {
            reason = null;
            if (snapshot == null || snapshot.PassivesReason > PassivesReason.CoreInventoryUnavailable ||
                snapshot.PassivesAvailable != (snapshot.PassivesReason == PassivesReason.None) ||
                snapshot.PassiveBooks.Count > EquipmentMirror.MaxBooks || !snapshot.PassivesAvailable && snapshot.PassiveBooks.Count != 0 ||
                snapshot.PassivesAvailable && (!snapshot.CoreBooksAvailable || snapshot.CoreBooksReason != CoreBooksReason.None || snapshot.PassiveBooks.Count != snapshot.CoreBooks.Count))
                return Reject(out reason, "Passive inventory availability or count is invalid.");
            var entries = new Dictionary<ulong, ProgressSnapshot.PassiveBookEntry>();
            foreach (var entry in snapshot.PassiveBooks)
            {
                if (entry == null || entry.BookToken == 0 || entries.ContainsKey(entry.BookToken) || (entry.Flags & ~AllBookFlags) != 0 ||
                    entry.MaxCost < 0 || entry.MaxCost > 12 || entry.MaxSources != 4 || entry.SourceTokens.Count > entry.MaxSources || entry.Slots.Count > MaxSlots ||
                    entry.SourceTokens.Any(token => token == 0 || token == entry.BookToken) || entry.SourceTokens.Distinct().Count() != entry.SourceTokens.Count ||
                    entry.ReceiverBookToken == entry.BookToken || !snapshot.CoreBooks.Any(core => core.BookToken == entry.BookToken))
                    return Reject(out reason, "Passive book identity or bounds are invalid.");
                if ((entry.Flags & PassiveBookFlags.Unsupported) != 0 && (entry.Flags != PassiveBookFlags.Unsupported || entry.Slots.Count != 0 ||
                    entry.SourceTokens.Count != 0 || entry.ReceiverBookToken != 0)) return Reject(out reason, "Unsupported passive books cannot contain partial metadata.");
                entries.Add(entry.BookToken, entry);
                foreach (var slot in entry.Slots)
                    if (!ValidSlotBounds(slot) ||
                        slot.SourceBookToken == 0 && (slot.SourceSlotIndex != byte.MaxValue || slot.CurrentId != slot.OriginId) ||
                        slot.SourceBookToken != 0 && (slot.SourceBookToken == entry.BookToken || slot.SourceSlotIndex >= MaxSlots || !entry.SourceTokens.Contains(slot.SourceBookToken)))
                        return Reject(out reason, "Passive slot identity, origin relation or metadata is invalid.");
            }
            foreach (var entry in entries.Values)
            {
                if (entry.ReceiverBookToken != 0 && (!entries.ContainsKey(entry.ReceiverBookToken) || !entries[entry.ReceiverBookToken].SourceTokens.Contains(entry.BookToken)) ||
                    entry.SourceTokens.Any(token => !entries.ContainsKey(token) || entries[token].ReceiverBookToken != entry.BookToken))
                    return Reject(out reason, "Passive committed book relationships are inconsistent.");
                foreach (var slot in entry.Slots)
                {
                    if (slot.SourceBookToken == 0) continue;
                    ProgressSnapshot.PassiveBookEntry source;
                    if (!entries.TryGetValue(slot.SourceBookToken, out source) || slot.SourceSlotIndex >= source.Slots.Count ||
                        source.Slots[slot.SourceSlotIndex].OriginId != slot.CurrentId || (source.Slots[slot.SourceSlotIndex].Flags & PassiveSlotFlags.Given) == 0 ||
                        slot.CurrentCost != source.Slots[slot.SourceSlotIndex].Cost || slot.CurrentRarity != source.Slots[slot.SourceSlotIndex].OriginRarity ||
                        slot.CurrentNegative != ((source.Slots[slot.SourceSlotIndex].Flags & PassiveSlotFlags.Negative) != 0))
                        return Reject(out reason, "Passive committed source slot does not match its recipient.");
                }
                for (var i = 0; i < entry.Slots.Count; i++)
                {
                    if ((entry.Slots[i].Flags & PassiveSlotFlags.Given) == 0) continue;
                    ProgressSnapshot.PassiveBookEntry receiver;
                    var sourceIndex = i;
                    if (entry.ReceiverBookToken == 0 || !entries.TryGetValue(entry.ReceiverBookToken, out receiver) ||
                        receiver.Slots.Count(slot => slot.SourceBookToken == entry.BookToken && slot.SourceSlotIndex == sourceIndex) != 1)
                        return Reject(out reason, "A given passive needs exactly one committed recipient slot.");
                }
            }
            return true;
        }
        private static bool Reject(out string reason, string message) { reason = message; return false; }
    }
}
