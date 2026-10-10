using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RuinaCoop
{
    // Configuration only. The independently encoded first-round state contains
    // runtime card instances, dice and queued effects; this is never that state.
    internal sealed class BattleManifest
    {
        internal ulong HostId;
        internal int StageId;
        internal byte FloorId;
        internal readonly List<int> InvitationBooks = new List<int>();
        internal readonly List<BattleActorConfiguration> Librarians = new List<BattleActorConfiguration>();
        internal readonly List<BattleEnemyConfiguration> Enemies = new List<BattleEnemyConfiguration>();
    }
    internal sealed class BattleActorConfiguration
    {
        internal uint ActorId;
        internal byte RosterIndex;
        internal ulong UnitIdentity, ControllerId, BookToken;
        internal int BookId, BookInstanceId;
        internal string Name = "";
        internal readonly List<int> Cards = new List<int>();
        internal readonly List<int> Passives = new List<int>();
    }
    internal sealed class BattleEnemyConfiguration
    {
        internal uint ActorId;
        internal ulong EnemyIdentity;
        internal int EnemyId, BookId;
        internal string Name = "";
        internal readonly List<int> Cards = new List<int>();
        internal readonly List<int> Passives = new List<int>();
    }
    internal static class BattleManifestCodec
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        internal static byte[] Encode(BattleManifest value)
        {
            string reason;
            if (!Validate(value, out reason)) throw new ArgumentException(reason);
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                writer.Write((byte)1); writer.Write(value.HostId); writer.Write(value.StageId); writer.Write(value.FloorId);
                WriteIds(writer, value.InvitationBooks, 3);
                writer.Write((byte)value.Librarians.Count);
                foreach (var actor in value.Librarians)
                {
                    writer.Write(actor.ActorId); writer.Write(actor.RosterIndex); writer.Write(actor.UnitIdentity);
                    writer.Write(actor.ControllerId); writer.Write(actor.BookToken); writer.Write(actor.BookId); writer.Write(actor.BookInstanceId);
                    WriteName(writer, actor.Name); WriteIds(writer, actor.Cards, 9); WriteIds(writer, actor.Passives, 32);
                }
                writer.Write((byte)value.Enemies.Count);
                foreach (var actor in value.Enemies)
                {
                    writer.Write(actor.ActorId); writer.Write(actor.EnemyIdentity); writer.Write(actor.EnemyId); writer.Write(actor.BookId);
                    WriteName(writer, actor.Name); WriteIds(writer, actor.Cards, 64); WriteIds(writer, actor.Passives, 32);
                }
                return stream.ToArray();
            }
        }
        internal static bool TryDecode(byte[] bytes, out BattleManifest value)
        {
            value = null;
            if (bytes == null || bytes.Length < 20 || bytes.Length > 8192) return false;
            try
            {
                using (var reader = new BinaryReader(new MemoryStream(bytes, false)))
                {
                    if (reader.ReadByte() != 1) return false;
                    var parsed = new BattleManifest { HostId = reader.ReadUInt64(), StageId = reader.ReadInt32(), FloorId = reader.ReadByte() };
                    ReadIds(reader, parsed.InvitationBooks, 3);
                    var count = reader.ReadByte(); if (count != 2) return false;
                    for (var i = 0; i < count; i++)
                    {
                        var actor = new BattleActorConfiguration { ActorId = reader.ReadUInt32(), RosterIndex = reader.ReadByte(),
                            UnitIdentity = reader.ReadUInt64(), ControllerId = reader.ReadUInt64(), BookToken = reader.ReadUInt64(),
                            BookId = reader.ReadInt32(), BookInstanceId = reader.ReadInt32(), Name = ReadName(reader) };
                        ReadIds(reader, actor.Cards, 9); ReadIds(reader, actor.Passives, 32); parsed.Librarians.Add(actor);
                    }
                    count = reader.ReadByte(); if (count < 1 || count > 5) return false;
                    for (var i = 0; i < count; i++)
                    {
                        var actor = new BattleEnemyConfiguration { ActorId = reader.ReadUInt32(), EnemyIdentity = reader.ReadUInt64(),
                            EnemyId = reader.ReadInt32(), BookId = reader.ReadInt32(), Name = ReadName(reader) };
                        ReadIds(reader, actor.Cards, 64); ReadIds(reader, actor.Passives, 32); parsed.Enemies.Add(actor);
                    }
                    string reason;
                    if (reader.BaseStream.Position != bytes.Length || !Validate(parsed, out reason)) return false;
                    value = parsed; return true;
                }
            }
            catch (IOException) { return false; }
            catch (ArgumentException) { return false; }
        }
        internal static bool Validate(BattleManifest value, out string reason)
        {
            reason = "Invalid battle manifest.";
            if (value == null || value.HostId == 0 || value.StageId <= 0 || value.FloorId < 1 || value.FloorId > 10 ||
                value.Librarians.Count != 2 || value.Enemies.Count < 1 || value.Enemies.Count > 5 || !ValidIds(value.InvitationBooks, 3, false)) return false;
            var identities = new HashSet<ulong>(); var controllers = new HashSet<ulong>(); var roster = new HashSet<byte>(); var books = new HashSet<ulong>();
            for (var i = 0; i < value.Librarians.Count; i++)
            {
                var actor = value.Librarians[i];
                if (actor == null || actor.ActorId != i + 1 || actor.RosterIndex >= 5 || !roster.Add(actor.RosterIndex) ||
                    actor.UnitIdentity == 0 || !identities.Add(actor.UnitIdentity) || actor.ControllerId == 0 ||
                    !controllers.Add(actor.ControllerId) || actor.BookToken == 0 || !books.Add(actor.BookToken) || actor.BookId <= 0 || !ValidName(actor.Name) ||
                    !ValidIds(actor.Cards, 9, true) || !ValidIds(actor.Passives, 32, false) || actor.Passives.Distinct().Count() != actor.Passives.Count) return false;
            }
            if (!controllers.Contains(value.HostId)) return false;
            identities.Clear();
            for (var i = 0; i < value.Enemies.Count; i++)
            {
                var actor = value.Enemies[i];
                if (actor == null || actor.ActorId != 101 + i || actor.EnemyIdentity == 0 || !identities.Add(actor.EnemyIdentity) ||
                    actor.EnemyId <= 0 || actor.BookId <= 0 || !ValidName(actor.Name) ||
                    !ValidIds(actor.Cards, 64, true) || !ValidIds(actor.Passives, 32, false) || actor.Passives.Distinct().Count() != actor.Passives.Count) return false;
            }
            reason = null; return true;
        }
        internal static BattleManifest FromPreparation(ProgressSnapshot snapshot, ulong hostId, IEnumerable<int> invitationBooks)
        {
            string reason;
            if (snapshot == null || !PreparationMirror.Validate(snapshot, out reason) || !snapshot.Preparation.Available ||
                snapshot.Preparation.CurrentWaveIndex != 0 || snapshot.Preparation.Waves.Count != 1)
                throw new InvalidOperationException("4A requires a fresh single-wave reception.");
            var result = new BattleManifest { HostId = hostId, StageId = snapshot.SelectedStageId, FloorId = snapshot.SelectedFloorId };
            result.InvitationBooks.AddRange(invitationBooks);
            var floor = snapshot.Floors.Find(row => (byte)row.Sephirah == result.FloorId);
            if (floor == null || floor.Units.Count != snapshot.Preparation.Participants.Count)
                throw new InvalidOperationException("The preparation floor does not match its roster.");
            foreach (var participant in snapshot.Preparation.Participants.Where(row => row.Participating))
            {
                var index = participant.UnitIndex; var deck = snapshot.UnitDecks[index];
                if (deck.Fixed || deck.MultiDeck || !deck.Display.Available) throw new InvalidOperationException("4A requires ordinary supported key pages.");
                var actor = new BattleActorConfiguration { ActorId = (uint)result.Librarians.Count + 1, RosterIndex = index,
                    UnitIdentity = deck.UnitIdentity, ControllerId = snapshot.ClaimOwners[index] == 0 ? hostId : snapshot.ClaimOwners[index],
                    BookToken = deck.BookToken, BookId = deck.BookId, BookInstanceId = deck.BookInstanceId, Name = floor.Units[index] };
                actor.Cards.AddRange(deck.Cards); actor.Passives.AddRange(deck.Display.PassiveIds); result.Librarians.Add(actor);
            }
            foreach (var enemy in snapshot.Preparation.Waves[0].Enemies)
            {
                if (enemy.Unknown || !enemy.CardsVisible || !enemy.Display.Available) throw new InvalidOperationException("Enemy configuration is not available.");
                var actor = new BattleEnemyConfiguration { ActorId = (uint)result.Enemies.Count + 101, EnemyIdentity = enemy.EnemyIdentity,
                    EnemyId = enemy.EnemyId, BookId = enemy.BookId, Name = enemy.Name };
                actor.Cards.AddRange(enemy.Cards); actor.Passives.AddRange(enemy.Display.PassiveIds); result.Enemies.Add(actor);
            }
            if (!Validate(result, out reason)) throw new InvalidOperationException("4A requires exactly two librarians, one host and one guest controller, and at most five enemies.");
            return result;
        }
        private static bool ValidIds(List<int> ids, int maximum, bool requireAny)
        { return ids != null && ids.Count <= maximum && (!requireAny || ids.Count > 0) && ids.All(id => id > 0); }
        private static bool ValidName(string name)
        { try { return name != null && Utf8.GetByteCount(name) <= 256 && !name.Any(char.IsControl); } catch (ArgumentException) { return false; } }
        private static void WriteName(BinaryWriter writer, string name)
        { var bytes = Utf8.GetBytes(name); writer.Write((ushort)bytes.Length); writer.Write(bytes); }
        private static string ReadName(BinaryReader reader)
        { var count = reader.ReadUInt16(); if (count > 256) throw new ArgumentException(); var bytes = reader.ReadBytes(count); if (bytes.Length != count) throw new EndOfStreamException(); return Utf8.GetString(bytes); }
        private static void WriteIds(BinaryWriter writer, List<int> ids, int maximum)
        { writer.Write((byte)ids.Count); foreach (var id in ids) writer.Write(id); }
        private static void ReadIds(BinaryReader reader, List<int> ids, int maximum)
        { var count = reader.ReadByte(); if (count > maximum) throw new ArgumentException(); for (var i = 0; i < count; i++) ids.Add(reader.ReadInt32()); }
    }
}
