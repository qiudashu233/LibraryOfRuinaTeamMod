using System.IO;

namespace RuinaCoop
{
    internal enum DeckAction : byte { Add = 1, Remove = 2 }

    internal enum DeckResultCode : byte
    {
        Accepted = 0,
        NotReady = 1,
        StaleClaim = 2,
        StaleDeck = 3,
        NotOwner = 4,
        InvalidCard = 5,
        UnsupportedBook = 6,
        VanillaRejected = 7,
        InvalidRequest = 8,
        Frozen = 9,
        Failed = 10
    }

    internal struct DeckRequest
    {
        internal uint RequestId;
        internal int StageId;
        internal byte FloorId;
        internal byte UnitIndex;
        internal uint ClaimRevision;
        internal uint DeckRevision;
        internal int CardId;
        internal DeckAction Action;
    }

    internal struct DeckReply
    {
        internal uint RequestId;
        internal DeckResultCode Result;
        internal uint DeckRevision;
        internal byte VanillaState;
    }

    internal static class DeckProtocol
    {
        private const uint RequestMagic = 0x52434451;
        private const uint ReplyMagic = 0x52434441;
        private const byte WireVersion = 1;
        private const int RequestLength = 36;
        private const int ReplyLength = 23;

        internal static byte[] EncodeRequest(ulong roomId, DeckRequest request)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(RequestMagic);
                writer.Write(WireVersion);
                writer.Write(roomId);
                writer.Write(request.RequestId);
                writer.Write(request.StageId);
                writer.Write(request.FloorId);
                writer.Write(request.UnitIndex);
                writer.Write(request.ClaimRevision);
                writer.Write(request.DeckRevision);
                writer.Write(request.CardId);
                writer.Write((byte)request.Action);
                return stream.ToArray();
            }
        }

        internal static bool TryDecodeRequest(byte[] packet, ulong roomId, out DeckRequest request)
        {
            request = default(DeckRequest);
            if (packet == null || packet.Length != RequestLength)
            {
                return false;
            }
            using (var reader = new BinaryReader(new MemoryStream(packet, false)))
            {
                if (reader.ReadUInt32() != RequestMagic || reader.ReadByte() != WireVersion ||
                    reader.ReadUInt64() != roomId)
                {
                    return false;
                }
                request.RequestId = reader.ReadUInt32();
                request.StageId = reader.ReadInt32();
                request.FloorId = reader.ReadByte();
                request.UnitIndex = reader.ReadByte();
                request.ClaimRevision = reader.ReadUInt32();
                request.DeckRevision = reader.ReadUInt32();
                request.CardId = reader.ReadInt32();
                request.Action = (DeckAction)reader.ReadByte();
                return request.RequestId != 0 && request.CardId > 0 &&
                    (request.Action == DeckAction.Add || request.Action == DeckAction.Remove);
            }
        }

        internal static byte[] EncodeReply(ulong roomId, DeckReply reply)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(ReplyMagic);
                writer.Write(WireVersion);
                writer.Write(roomId);
                writer.Write(reply.RequestId);
                writer.Write((byte)reply.Result);
                writer.Write(reply.DeckRevision);
                writer.Write(reply.VanillaState);
                return stream.ToArray();
            }
        }

        internal static bool TryDecodeReply(byte[] packet, ulong roomId, out DeckReply reply)
        {
            reply = default(DeckReply);
            if (packet == null || packet.Length != ReplyLength)
            {
                return false;
            }
            using (var reader = new BinaryReader(new MemoryStream(packet, false)))
            {
                if (reader.ReadUInt32() != ReplyMagic || reader.ReadByte() != WireVersion ||
                    reader.ReadUInt64() != roomId)
                {
                    return false;
                }
                reply.RequestId = reader.ReadUInt32();
                reply.Result = (DeckResultCode)reader.ReadByte();
                reply.DeckRevision = reader.ReadUInt32();
                reply.VanillaState = reader.ReadByte();
                return reply.RequestId != 0 && reply.Result <= DeckResultCode.Failed;
            }
        }
    }
}
