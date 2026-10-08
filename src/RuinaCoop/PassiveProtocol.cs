using System;
using System.Collections.Generic;
using System.IO;

namespace RuinaCoop
{
    internal enum PassiveSelectionMode : byte { RestoreNative = 0, Inherit = 1 }
    internal enum PassiveResultCode : byte
    {
        Accepted = 0, NotReady = 1, StaleClaim = 2, StaleDeck = 3, NotOwner = 4, InvalidRequest = 5,
        Frozen = 6, UnsupportedInventory = 7, UnsupportedTarget = 8, UnknownSource = 9,
        SourceOccupied = 10, UnsupportedSource = 11, Failed = 12, VanillaRejected = 13,
        CostExceeded = 14, SourceLimit = 15, IncompatiblePassive = 16, LockedSlot = 17
    }
    internal struct PassiveSelection
    {
        internal PassiveSelectionMode Mode;
        internal ulong SourceBookToken;
        internal byte SourceSlotIndex;
        internal int ExpectedOriginPassiveId;
    }
    internal struct PassiveRequest
    {
        internal uint RequestId;
        internal int StageId;
        internal byte FloorId;
        internal byte UnitIndex;
        internal ulong UnitIdentity;
        internal ulong BookToken;
        internal uint ClaimRevision;
        internal uint DeckRevision;
        internal PassiveSelection[] Slots;
        internal ulong[] SourceBookTokens;
    }
    internal struct PassiveReply
    {
        internal uint RequestId;
        internal PassiveResultCode Result;
        internal uint DeckRevision;
    }
    internal static class PassiveProtocol
    {
        private const uint RequestMagic = 0x52505351;
        private const uint ReplyMagic = 0x52505341;
        private const byte WireVersion = 1;
        private const int MaxRequestBytes = 977;
        internal static byte[] EncodeRequest(ulong roomId, PassiveRequest request)
        {
            if (roomId == 0 || !ValidRequest(request)) throw new ArgumentException("Invalid passive request.");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(RequestMagic); writer.Write(WireVersion); writer.Write(roomId);
                writer.Write(request.RequestId); writer.Write(request.StageId); writer.Write(request.FloorId); writer.Write(request.UnitIndex);
                writer.Write(request.UnitIdentity); writer.Write(request.BookToken); writer.Write(request.ClaimRevision); writer.Write(request.DeckRevision);
                writer.Write((byte)request.Slots.Length);
                foreach (var slot in request.Slots)
                { writer.Write((byte)slot.Mode); writer.Write(slot.SourceBookToken); writer.Write(slot.SourceSlotIndex); writer.Write(slot.ExpectedOriginPassiveId); }
                writer.Write((byte)request.SourceBookTokens.Length);
                foreach (var token in request.SourceBookTokens) writer.Write(token);
                return stream.ToArray();
            }
        }
        internal static bool TryDecodeRequest(byte[] packet, ulong roomId, out PassiveRequest request)
        {
            request = default(PassiveRequest);
            if (roomId == 0 || packet == null || packet.Length < 63 || packet.Length > MaxRequestBytes) return false;
            try
            {
                using (var reader = new BinaryReader(new MemoryStream(packet, false)))
                {
                    if (reader.ReadUInt32() != RequestMagic || reader.ReadByte() != WireVersion || reader.ReadUInt64() != roomId) return false;
                    var parsed = new PassiveRequest
                    {
                        RequestId = reader.ReadUInt32(), StageId = reader.ReadInt32(), FloorId = reader.ReadByte(), UnitIndex = reader.ReadByte(),
                        UnitIdentity = reader.ReadUInt64(), BookToken = reader.ReadUInt64(), ClaimRevision = reader.ReadUInt32(), DeckRevision = reader.ReadUInt32()
                    };
                    var count = reader.ReadByte(); if (count == 0 || count > 64) return false;
                    parsed.Slots = new PassiveSelection[count];
                    for (var i = 0; i < count; i++) parsed.Slots[i] = new PassiveSelection
                    { Mode = (PassiveSelectionMode)reader.ReadByte(), SourceBookToken = reader.ReadUInt64(), SourceSlotIndex = reader.ReadByte(), ExpectedOriginPassiveId = reader.ReadInt32() };
                    count = reader.ReadByte(); if (count > 4) return false;
                    parsed.SourceBookTokens = new ulong[count];
                    for (var i = 0; i < count; i++) parsed.SourceBookTokens[i] = reader.ReadUInt64();
                    if (reader.BaseStream.Position != reader.BaseStream.Length || !ValidRequest(parsed)) return false;
                    request = parsed; return true;
                }
            }
            catch (EndOfStreamException) { return false; }
        }
        internal static bool ValidRequest(PassiveRequest request)
        {
            if (request.RequestId == 0 || request.StageId <= 0 || request.FloorId < 1 || request.FloorId > 10 || request.UnitIndex >= 5 ||
                request.UnitIdentity == 0 || request.BookToken == 0 || request.Slots == null || request.Slots.Length == 0 || request.Slots.Length > 64 ||
                request.SourceBookTokens == null || request.SourceBookTokens.Length > 4) return false;
            var sources = new HashSet<ulong>();
            foreach (var token in request.SourceBookTokens) if (token == 0 || token == request.BookToken || !sources.Add(token)) return false;
            foreach (var slot in request.Slots)
            {
                if (slot.ExpectedOriginPassiveId <= 0 || slot.Mode > PassiveSelectionMode.Inherit) return false;
                if (slot.Mode == PassiveSelectionMode.RestoreNative)
                { if (slot.SourceBookToken != 0 || slot.SourceSlotIndex != byte.MaxValue) return false; }
                else if (slot.SourceBookToken == 0 || slot.SourceBookToken == request.BookToken || slot.SourceSlotIndex >= 64 ||
                    slot.ExpectedOriginPassiveId == 9999999 || !sources.Contains(slot.SourceBookToken)) return false;
            }
            return true;
        }
        internal static byte[] EncodeReply(ulong roomId, PassiveReply reply)
        {
            if (roomId == 0 || reply.RequestId == 0 || reply.Result > PassiveResultCode.LockedSlot) throw new ArgumentException("Invalid passive reply.");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            { writer.Write(ReplyMagic); writer.Write(WireVersion); writer.Write(roomId); writer.Write(reply.RequestId); writer.Write((byte)reply.Result); writer.Write(reply.DeckRevision); return stream.ToArray(); }
        }
        internal static bool TryDecodeReply(byte[] packet, ulong roomId, out PassiveReply reply)
        {
            reply = default(PassiveReply);
            if (roomId == 0 || packet == null || packet.Length != 22) return false;
            using (var reader = new BinaryReader(new MemoryStream(packet, false)))
            {
                if (reader.ReadUInt32() != ReplyMagic || reader.ReadByte() != WireVersion || reader.ReadUInt64() != roomId) return false;
                var parsed = new PassiveReply { RequestId = reader.ReadUInt32(), Result = (PassiveResultCode)reader.ReadByte(), DeckRevision = reader.ReadUInt32() };
                if (parsed.RequestId == 0 || parsed.Result > PassiveResultCode.LockedSlot) return false;
                reply = parsed; return true;
            }
        }
    }
}
