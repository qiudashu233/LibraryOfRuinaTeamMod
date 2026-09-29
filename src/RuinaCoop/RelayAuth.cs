using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RuinaCoop
{
    // Binds an otherwise anonymous P2P socket to Steam's authenticated lobby-chat sender.
    internal static class RelayAuth
    {
        private const string ChallengePrefix = "RC5C:";
        private const string ProofPrefix = "RC5P:";
        private const string AcceptedPrefix = "RC5A:";

        internal static string NewChallenge()
        {
            var bytes = new byte[16];
            using (var random = RandomNumberGenerator.Create())
            {
                random.GetBytes(bytes);
            }
            return ToHex(bytes);
        }

        internal static byte[] ChallengePacket(string challenge)
        {
            return Encoding.ASCII.GetBytes(ChallengePrefix + challenge);
        }

        internal static byte[] AcceptedPacket(string challenge)
        {
            return Encoding.ASCII.GetBytes(AcceptedPrefix + challenge);
        }

        internal static bool TryReadChallenge(byte[] packet, out string challenge)
        {
            return TryReadToken(packet, ChallengePrefix, out challenge);
        }

        internal static bool TryReadAccepted(byte[] packet, out string challenge)
        {
            return TryReadToken(packet, AcceptedPrefix, out challenge);
        }

        internal static string ProofMessage(string challenge, ulong memberId, ulong roomId)
        {
            var memberText = memberId.ToString(CultureInfo.InvariantCulture);
            var roomText = roomId.ToString(CultureInfo.InvariantCulture);
            var input = Encoding.ASCII.GetBytes(challenge + ":" + memberText + ":" + roomText);
            using (var sha = SHA256.Create())
            {
                return ProofPrefix + roomText + ":" + ToHex(sha.ComputeHash(input));
            }
        }

        internal static bool MatchesProof(string message, string challenge, ulong memberId, ulong roomId)
        {
            var expected = ProofMessage(challenge, memberId, roomId);
            if (message == null || message.Length != expected.Length)
            {
                return false;
            }
            var different = 0;
            for (var i = 0; i < expected.Length; i++)
            {
                different |= message[i] ^ expected[i];
            }
            return different == 0;
        }

        internal static bool IsProofMessage(string message)
        {
            return message != null && message.StartsWith(ProofPrefix, StringComparison.Ordinal);
        }

        private static bool TryReadToken(byte[] packet, string prefix, out string token)
        {
            token = null;
            if (packet == null || packet.Length != prefix.Length + 32)
            {
                return false;
            }
            var text = Encoding.ASCII.GetString(packet);
            if (!text.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }
            for (var i = prefix.Length; i < text.Length; i++)
            {
                var c = text[i];
                if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F')))
                {
                    return false;
                }
            }
            token = text.Substring(prefix.Length);
            return true;
        }

        private static string ToHex(byte[] bytes)
        {
            var chars = new char[bytes.Length * 2];
            const string digits = "0123456789ABCDEF";
            for (var i = 0; i < bytes.Length; i++)
            {
                chars[i * 2] = digits[bytes[i] >> 4];
                chars[i * 2 + 1] = digits[bytes[i] & 15];
            }
            return new string(chars);
        }
    }
}
