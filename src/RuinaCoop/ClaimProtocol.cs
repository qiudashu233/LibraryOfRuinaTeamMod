using System;
using System.IO;

namespace RuinaCoop
{
    internal struct ClaimRequest
    {
        internal uint RequestId;
        internal int StageId;
        internal byte FloorId;
        internal byte UnitIndex;
        internal uint ExpectedRevision;
        internal ClaimAction Action;
    }

    internal struct ClaimReply
    {
        internal uint RequestId;
        internal ClaimResultCode Result;
        internal uint Revision;
    }

    internal static class ClaimProtocol
    {
        private const uint RequestMagic = 0x52434351;
        private const uint ReplyMagic = 0x52434341;
        private const byte Version = 1;
        private const int RequestLength = 28;
        private const int ReplyLength = 22;

        internal static byte[] EncodeRequest(ulong roomId, ClaimRequest request)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(RequestMagic);
                writer.Write(Version);
                writer.Write(roomId);
                writer.Write(request.RequestId);
                writer.Write(request.StageId);
                writer.Write(request.FloorId);
                writer.Write(request.UnitIndex);
                writer.Write(request.ExpectedRevision);
                writer.Write((byte)request.Action);
                return stream.ToArray();
            }
        }

        internal static bool TryDecodeRequest(byte[] packet, ulong roomId, out ClaimRequest request)
        {
            request = default(ClaimRequest);
            if (packet == null || packet.Length != RequestLength)
            {
                return false;
            }
            using (var reader = new BinaryReader(new MemoryStream(packet, false)))
            {
                if (reader.ReadUInt32() != RequestMagic || reader.ReadByte() != Version ||
                    reader.ReadUInt64() != roomId)
                {
                    return false;
                }
                request.RequestId = reader.ReadUInt32();
                request.StageId = reader.ReadInt32();
                request.FloorId = reader.ReadByte();
                request.UnitIndex = reader.ReadByte();
                request.ExpectedRevision = reader.ReadUInt32();
                request.Action = (ClaimAction)reader.ReadByte();
                return request.RequestId != 0 &&
                    (request.Action == ClaimAction.Claim || request.Action == ClaimAction.Release);
            }
        }

        internal static byte[] EncodeReply(ulong roomId, ClaimReply reply)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(ReplyMagic);
                writer.Write(Version);
                writer.Write(roomId);
                writer.Write(reply.RequestId);
                writer.Write((byte)reply.Result);
                writer.Write(reply.Revision);
                return stream.ToArray();
            }
        }

        internal static bool TryDecodeReply(byte[] packet, ulong roomId, out ClaimReply reply)
        {
            reply = default(ClaimReply);
            if (packet == null || packet.Length != ReplyLength)
            {
                return false;
            }
            using (var reader = new BinaryReader(new MemoryStream(packet, false)))
            {
                if (reader.ReadUInt32() != ReplyMagic || reader.ReadByte() != Version ||
                    reader.ReadUInt64() != roomId)
                {
                    return false;
                }
                reply.RequestId = reader.ReadUInt32();
                reply.Result = (ClaimResultCode)reader.ReadByte();
                reply.Revision = reader.ReadUInt32();
                return reply.RequestId != 0 && reply.Result <= ClaimResultCode.InvalidRequest;
            }
        }
    }
}
