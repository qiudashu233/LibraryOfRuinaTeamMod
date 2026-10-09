using System;
using System.IO;

namespace RuinaCoop
{
    internal enum PreparationReadyResultCode : byte
    { Accepted = 0, NotReady = 1, StaleRevision = 2, Observer = 3, Disconnected = 4, InvalidRequest = 5, Failed = 6, Frozen = 7 }
    internal struct PreparationReadyRequest
    { internal uint RequestId; internal uint Revision; internal bool Ready; }
    internal struct PreparationReadyReply
    { internal uint RequestId; internal PreparationReadyResultCode Result; internal uint Revision; }
    internal static class PreparationProtocol
    {
        private const uint RequestMagic = 0x52505251;
        private const uint ReplyMagic = 0x52505241;
        private const byte WireVersion = 1;
        internal static byte[] EncodeRequest(ulong roomId, PreparationReadyRequest request)
        {
            if (roomId == 0 || request.RequestId == 0 || request.Revision == 0) throw new ArgumentException("Invalid preparation ready request.");
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            { writer.Write(RequestMagic); writer.Write(WireVersion); writer.Write(roomId); writer.Write(request.RequestId); writer.Write(request.Revision); writer.Write((byte)(request.Ready ? 1 : 0)); return stream.ToArray(); }
        }
        internal static bool TryDecodeRequest(byte[] packet, ulong roomId, out PreparationReadyRequest request)
        {
            request = default(PreparationReadyRequest);
            if (roomId == 0 || packet == null || packet.Length != 22) return false;
            using (var reader = new BinaryReader(new MemoryStream(packet, false)))
            {
                if (reader.ReadUInt32() != RequestMagic || reader.ReadByte() != WireVersion || reader.ReadUInt64() != roomId) return false;
                var parsed = new PreparationReadyRequest { RequestId = reader.ReadUInt32(), Revision = reader.ReadUInt32() };
                var ready = reader.ReadByte();
                if (parsed.RequestId == 0 || parsed.Revision == 0 || ready > 1) return false;
                parsed.Ready = ready != 0; request = parsed; return true;
            }
        }
        internal static byte[] EncodeReply(ulong roomId, PreparationReadyReply reply)
        {
            if (roomId == 0 || reply.RequestId == 0 || reply.Result > PreparationReadyResultCode.Frozen ||
                (reply.Result == PreparationReadyResultCode.Accepted && reply.Revision == 0)) throw new ArgumentException("Invalid preparation ready reply.");
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            { writer.Write(ReplyMagic); writer.Write(WireVersion); writer.Write(roomId); writer.Write(reply.RequestId); writer.Write((byte)reply.Result); writer.Write(reply.Revision); return stream.ToArray(); }
        }
        internal static bool TryDecodeReply(byte[] packet, ulong roomId, out PreparationReadyReply reply)
        {
            reply = default(PreparationReadyReply);
            if (roomId == 0 || packet == null || packet.Length != 22) return false;
            using (var reader = new BinaryReader(new MemoryStream(packet, false)))
            {
                if (reader.ReadUInt32() != ReplyMagic || reader.ReadByte() != WireVersion || reader.ReadUInt64() != roomId) return false;
                var parsed = new PreparationReadyReply { RequestId = reader.ReadUInt32(), Result = (PreparationReadyResultCode)reader.ReadByte(), Revision = reader.ReadUInt32() };
                if (parsed.RequestId == 0 || parsed.Result > PreparationReadyResultCode.Frozen ||
                    (parsed.Result == PreparationReadyResultCode.Accepted && parsed.Revision == 0)) return false;
                reply = parsed; return true;
            }
        }
    }
}
