using System;
using System.IO;
using System.Security.Cryptography;

namespace RuinaCoop
{
    internal enum BattleMessageType : byte
    {
        Offer = 1, OfferAck = 2, Cancel = 3, Commit = 4,
        InitialState = 5, StateAck = 6, Initialized = 7, Failed = 8, Finished = 9
    }

    // The envelope only transports bounded, explicitly encoded bytes. It never
    // serializes game objects, CLR type metadata, or arbitrary object graphs.
    internal sealed class BattleMessage
    {
        internal BattleMessageType Type;
        internal ulong RoomId;
        internal ulong BattleSessionId;
        internal ulong PreparationContext;
        internal uint PreparationRevision;
        internal byte[] Digest;
        internal byte[] Payload;
    }

    internal static class BattleProtocol
    {
        internal const int MaxPacketBytes = 65536;
        internal const int MaxPayloadBytes = 60000;
        internal const int DigestBytes = 32;
        internal const int HeaderBytes = 70;
        private const uint Magic = 0x42504352;
        private const byte WireVersion = 1;

        internal static bool IsBattlePacket(byte[] packet)
        {
            return packet != null && packet.Length >= 4 &&
                packet[0] == (byte)(Magic & 255) && packet[1] == (byte)((Magic >> 8) & 255) &&
                packet[2] == (byte)((Magic >> 16) & 255) && packet[3] == (byte)(Magic >> 24);
        }

        internal static byte[] Encode(BattleMessage message)
        {
            if (!Valid(message)) throw new ArgumentException("Invalid battle envelope.", "message");
            var payload = message.Payload ?? new byte[0];
            using (var stream = new MemoryStream(HeaderBytes + payload.Length))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic); writer.Write(WireVersion); writer.Write((byte)message.Type);
                writer.Write(message.RoomId); writer.Write(message.BattleSessionId);
                writer.Write(message.PreparationContext); writer.Write(message.PreparationRevision);
                writer.Write(message.Digest); writer.Write(payload.Length); writer.Write(payload);
                return stream.ToArray();
            }
        }

        internal static bool TryDecode(byte[] packet, ulong expectedRoomId, out BattleMessage message)
        {
            message = null;
            if (expectedRoomId == 0 || packet == null || packet.Length < HeaderBytes ||
                packet.Length > MaxPacketBytes || !IsBattlePacket(packet)) return false;
            using (var reader = new BinaryReader(new MemoryStream(packet, false)))
            {
                if (reader.ReadUInt32() != Magic || reader.ReadByte() != WireVersion) return false;
                var parsed = new BattleMessage
                {
                    Type = (BattleMessageType)reader.ReadByte(), RoomId = reader.ReadUInt64(),
                    BattleSessionId = reader.ReadUInt64(), PreparationContext = reader.ReadUInt64(),
                    PreparationRevision = reader.ReadUInt32(), Digest = reader.ReadBytes(DigestBytes)
                };
                var payloadLength = reader.ReadInt32();
                // Check bounds and the exact packet length before allocating the payload.
                if (parsed.RoomId != expectedRoomId || payloadLength < 0 || payloadLength > MaxPayloadBytes ||
                    packet.Length != HeaderBytes + payloadLength) return false;
                parsed.Payload = reader.ReadBytes(payloadLength);
                if (!Valid(parsed)) return false;
                message = parsed;
                return true;
            }
        }

        internal static byte[] ComputeDigest(byte[] payload)
        {
            if (payload == null || payload.Length > MaxPayloadBytes)
                throw new ArgumentException("Invalid battle payload.", "payload");
            using (var sha = SHA256.Create()) return sha.ComputeHash(payload);
        }

        internal static bool DigestEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != DigestBytes || right.Length != DigestBytes) return false;
            var difference = 0;
            for (var i = 0; i < DigestBytes; i++) difference |= left[i] ^ right[i];
            return difference == 0;
        }

        private static bool Valid(BattleMessage message)
        {
            if (message == null || message.Type < BattleMessageType.Offer || message.Type > BattleMessageType.Finished ||
                message.RoomId == 0 || message.BattleSessionId == 0 || message.PreparationContext == 0 ||
                message.PreparationRevision == 0 || message.Digest == null || message.Digest.Length != DigestBytes) return false;
            var length = message.Payload == null ? 0 : message.Payload.Length;
            if (length > MaxPayloadBytes) return false;
            if (message.Type == BattleMessageType.Offer || message.Type == BattleMessageType.InitialState)
                return length > 0 && DigestEquals(message.Digest, ComputeDigest(message.Payload));
            return length == 0;
        }
    }
}
