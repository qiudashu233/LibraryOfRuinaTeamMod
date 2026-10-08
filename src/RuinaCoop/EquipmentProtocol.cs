using System;
using System.IO;

namespace RuinaCoop
{
    internal enum CorePageResultCode : byte
    {
        Accepted = 0, NotReady = 1, StaleClaim = 2, StaleDeck = 3, NotOwner = 4,
        InvalidRequest = 5, Frozen = 6, UnsupportedInventory = 7, UnsupportedCurrentBook = 8,
        UnknownTarget = 9, TargetOccupied = 10, UnsupportedTarget = 11, Failed = 12, VanillaRejected = 13
    }

    internal struct CorePageRequest
    {
        internal uint RequestId;
        internal int StageId;
        internal byte FloorId;
        internal byte UnitIndex;
        internal ulong UnitIdentity;
        internal ulong OldBookToken;
        internal uint ClaimRevision;
        internal uint DeckRevision;
        internal ulong TargetBookToken;
    }

    internal struct CorePageReply
    {
        internal uint RequestId;
        internal CorePageResultCode Result;
        internal uint DeckRevision;
    }

    internal static class EquipmentProtocol
    {
        private const uint RequestMagic = 0x52434551;
        private const uint ReplyMagic = 0x52434541;
        private const byte WireVersion = 1;
        private const int RequestLength = 55;
        private const int ReplyLength = 22;

        internal static byte[] EncodeRequest(ulong roomId, CorePageRequest request)
        {
            if (roomId == 0 || !ValidRequest(request)) throw new ArgumentException("Invalid core page request.");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(RequestMagic); writer.Write(WireVersion); writer.Write(roomId);
                writer.Write(request.RequestId); writer.Write(request.StageId);
                writer.Write(request.FloorId); writer.Write(request.UnitIndex);
                writer.Write(request.UnitIdentity); writer.Write(request.OldBookToken);
                writer.Write(request.ClaimRevision); writer.Write(request.DeckRevision);
                writer.Write(request.TargetBookToken);
                return stream.ToArray();
            }
        }

        internal static bool TryDecodeRequest(byte[] packet, ulong roomId, out CorePageRequest request)
        {
            request = default(CorePageRequest);
            if (roomId == 0 || packet == null || packet.Length != RequestLength) return false;
            using (var reader = new BinaryReader(new MemoryStream(packet, false)))
            {
                if (reader.ReadUInt32() != RequestMagic || reader.ReadByte() != WireVersion ||
                    reader.ReadUInt64() != roomId) return false;
                var parsed = new CorePageRequest
                {
                    RequestId = reader.ReadUInt32(), StageId = reader.ReadInt32(),
                    FloorId = reader.ReadByte(), UnitIndex = reader.ReadByte(),
                    UnitIdentity = reader.ReadUInt64(), OldBookToken = reader.ReadUInt64(),
                    ClaimRevision = reader.ReadUInt32(), DeckRevision = reader.ReadUInt32(),
                    TargetBookToken = reader.ReadUInt64()
                };
                if (!ValidRequest(parsed)) return false;
                request = parsed;
                return true;
            }
        }

        private static bool ValidRequest(CorePageRequest request)
        {
            return request.RequestId != 0 && request.StageId > 0 && request.FloorId >= 1 &&
                request.FloorId <= 10 && request.UnitIndex < 5 && request.UnitIdentity != 0 &&
                request.OldBookToken != 0 && request.TargetBookToken != 0 &&
                request.TargetBookToken != request.OldBookToken;
        }

        internal static byte[] EncodeReply(ulong roomId, CorePageReply reply)
        {
            if (roomId == 0 || reply.RequestId == 0 || reply.Result > CorePageResultCode.VanillaRejected)
                throw new ArgumentException("Invalid core page reply.");
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(ReplyMagic); writer.Write(WireVersion); writer.Write(roomId);
                writer.Write(reply.RequestId); writer.Write((byte)reply.Result); writer.Write(reply.DeckRevision);
                return stream.ToArray();
            }
        }

        internal static bool TryDecodeReply(byte[] packet, ulong roomId, out CorePageReply reply)
        {
            reply = default(CorePageReply);
            if (roomId == 0 || packet == null || packet.Length != ReplyLength) return false;
            using (var reader = new BinaryReader(new MemoryStream(packet, false)))
            {
                if (reader.ReadUInt32() != ReplyMagic || reader.ReadByte() != WireVersion ||
                    reader.ReadUInt64() != roomId) return false;
                var parsed = new CorePageReply
                {
                    RequestId = reader.ReadUInt32(), Result = (CorePageResultCode)reader.ReadByte(),
                    DeckRevision = reader.ReadUInt32()
                };
                if (parsed.RequestId == 0 || parsed.Result > CorePageResultCode.VanillaRejected) return false;
                reply = parsed;
                return true;
            }
        }
    }
}
