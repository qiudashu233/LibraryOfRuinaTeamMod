using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace RuinaCoop
{
    internal enum BattleCardZone : byte { Deck = 0, Hand = 1, Used = 2, Discarded = 3, Reserved = 4 }

    // Runtime data, independent of preparation display models. This version
    // supports a fresh stage 3 at the first input boundary only. The zero effect
    // queues below are captured and validated, never silently omitted.
    internal sealed class BattleInitialState
    {
        internal int StageId, WaveIndex, Round, Phase;
        internal byte FloorId;
        internal string Map = "";
        internal int StageStorageCount;
        internal readonly int[] StageCounters = new int[3];
        internal byte StageFlags;
        internal readonly int[] LibrarianTeamEmotion = new int[14];
        internal readonly int[] EnemyTeamEmotion = new int[14];
        internal readonly List<BattleActorState> Actors = new List<BattleActorState>();
    }
    internal sealed class BattleActorState
    {
        internal uint ActorId;
        internal ulong ConfigurationIdentity;
        internal int NativeUnitId, NativeIndex, BookId, BookInstanceId, TurnState;
        internal bool Enemy;
        internal float Hp, StageHp;
        internal int MaxHp, BreakGauge, MaxBreakGauge, BreakLife, MaxBreakLife;
        internal bool NextTurnBreak, BlockBreakRecovery, Dead, Extinct, Knockout;
        internal int PlayPoint, MaxPlayPoint, ReservedPlayPoint, LostPlayPoint, NextRoundPlayPoint;
        internal int StartingPlayPoint, DefaultRecoverPoint, RecoverPoint, MaxHand, MaxDrawHand;
        internal int EmotionLevel, MaxEmotionLevel, MaxEmotionCoins, MaxEgoCoins, MentalState, EmotionSkillPoint, ForcedLevelUps;
        internal readonly List<byte> EmotionCoins = new List<byte>();
        internal readonly List<byte> TotalEmotionCoins = new List<byte>();
        internal readonly List<byte> EgoEmotionCoins = new List<byte>();
        internal readonly int[] Resistances = new int[6];
        internal readonly int[] EmotionStatBonus = new int[7];
        // Current, next, next-next buffs; current and ready passives; emotion
        // passives; connected buffs; personal EGO; a saved replacement deck.
        internal readonly int[] EffectQueueCounts = new int[9];
        internal readonly List<BattleCardState> Cards = new List<BattleCardState>();
        // The native Used and Reserved lists can share the same physical card.
        // Cards is a unique instance catalogue; memberships preserve all five
        // ordered lists without inventing extra copies of that card.
        internal readonly List<uint>[] CardZones = { new List<uint>(), new List<uint>(), new List<uint>(), new List<uint>(), new List<uint>() };
        internal IEnumerable<BattleCardState> CardsInZone(BattleCardZone zone)
        { return CardZones[(int)zone].Select(id => Cards.Find(card => card.InstanceId == id)); }
        internal readonly List<BattleSpeedDieState> SpeedDice = new List<BattleSpeedDieState>();
        internal readonly List<BattleCardIntentState> Intent = new List<BattleCardIntentState>();
    }
    internal sealed class BattleCardState
    {
        internal uint InstanceId;
        internal int CardId, Cost, CurrentCost, OriginalCost, CostAdder, PriorityAdder, Priority;
        internal BattleCardZone Zone;
        internal byte Position, Flags;
        internal float MaxCooltime, CurrentCooltime;
        internal int BufCount;
    }
    internal sealed class BattleSpeedDieState
    {
        internal int Min, Max, Value;
        internal bool Broken, Controllable;
    }
    internal sealed class BattleCardIntentState
    {
        internal byte Slot;
        internal uint CardInstanceId, TargetActorId, EarlyTargetActorId;
        internal int TargetSlot, EarlyTargetSlot, Speed, EmotionMultiplier;
        internal bool FirstAction, IgnorePower;
        internal int BehaviourQueueCount, SubTargetCount, ExcludedDiceCount;
    }

    internal static class BattleInitialStateCodec
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        internal const int MaximumBytes = 32768;
        internal static string Digest(byte[] bytes)
        { using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", ""); }

        internal static byte[] Encode(BattleInitialState value)
        {
            string reason;
            if (!Validate(value, out reason)) throw new ArgumentException(reason);
            using (var stream = new MemoryStream()) using (var w = new BinaryWriter(stream))
            {
                w.Write((byte)1); w.Write(value.StageId); w.Write(value.FloorId); w.Write(value.WaveIndex); w.Write(value.Round); w.Write(value.Phase);
                var map = Utf8.GetBytes(value.Map); w.Write((ushort)map.Length); w.Write(map);
                w.Write(value.StageStorageCount); WriteInts(w, value.StageCounters); w.Write(value.StageFlags);
                WriteInts(w, value.LibrarianTeamEmotion); WriteInts(w, value.EnemyTeamEmotion);
                w.Write((byte)value.Actors.Count);
                foreach (var a in value.Actors)
                {
                    w.Write(a.ActorId); w.Write(a.ConfigurationIdentity); w.Write(a.NativeUnitId); w.Write(a.NativeIndex); w.Write(a.Enemy); w.Write(a.BookId); w.Write(a.BookInstanceId); w.Write(a.TurnState);
                    w.Write(a.Hp); w.Write(a.StageHp); w.Write(a.MaxHp); w.Write(a.BreakGauge); w.Write(a.MaxBreakGauge); w.Write(a.BreakLife); w.Write(a.MaxBreakLife);
                    w.Write(a.NextTurnBreak); w.Write(a.BlockBreakRecovery); w.Write(a.Dead); w.Write(a.Extinct); w.Write(a.Knockout);
                    w.Write(a.PlayPoint); w.Write(a.MaxPlayPoint); w.Write(a.ReservedPlayPoint); w.Write(a.LostPlayPoint); w.Write(a.NextRoundPlayPoint);
                    w.Write(a.StartingPlayPoint); w.Write(a.DefaultRecoverPoint); w.Write(a.RecoverPoint); w.Write(a.MaxHand); w.Write(a.MaxDrawHand);
                    w.Write(a.EmotionLevel); w.Write(a.MaxEmotionLevel); w.Write(a.MaxEmotionCoins); w.Write(a.MaxEgoCoins); w.Write(a.MentalState); w.Write(a.EmotionSkillPoint); w.Write(a.ForcedLevelUps);
                    WriteBytes(w, a.EmotionCoins); WriteBytes(w, a.TotalEmotionCoins); WriteBytes(w, a.EgoEmotionCoins);
                    WriteInts(w, a.Resistances); WriteInts(w, a.EmotionStatBonus); WriteInts(w, a.EffectQueueCounts);
                    w.Write((byte)a.Cards.Count);
                    foreach (var c in a.Cards)
                    {
                        w.Write(c.InstanceId); w.Write(c.CardId); w.Write((byte)c.Zone); w.Write(c.Position); w.Write(c.Flags);
                        w.Write(c.Cost); w.Write(c.CurrentCost); w.Write(c.OriginalCost); w.Write(c.CostAdder); w.Write(c.PriorityAdder); w.Write(c.Priority);
                        w.Write(c.MaxCooltime); w.Write(c.CurrentCooltime); w.Write(c.BufCount);
                    }
                    foreach (var zone in a.CardZones) { w.Write((byte)zone.Count); foreach (var instance in zone) w.Write(instance); }
                    w.Write((byte)a.SpeedDice.Count);
                    foreach (var d in a.SpeedDice) { w.Write(d.Min); w.Write(d.Max); w.Write(d.Value); w.Write(d.Broken); w.Write(d.Controllable); }
                    w.Write((byte)a.Intent.Count);
                    foreach (var i in a.Intent)
                    {
                        w.Write(i.Slot); w.Write(i.CardInstanceId); w.Write(i.TargetActorId); w.Write(i.TargetSlot); w.Write(i.EarlyTargetActorId); w.Write(i.EarlyTargetSlot);
                        w.Write(i.Speed); w.Write(i.EmotionMultiplier); w.Write(i.FirstAction); w.Write(i.IgnorePower);
                        w.Write(i.BehaviourQueueCount); w.Write(i.SubTargetCount); w.Write(i.ExcludedDiceCount);
                    }
                }
                if (stream.Length > MaximumBytes) throw new ArgumentException("Initial state exceeds the packet limit.");
                return stream.ToArray();
            }
        }

        internal static bool TryDecode(byte[] bytes, out BattleInitialState value)
        {
            value = null;
            if (bytes == null || bytes.Length < 100 || bytes.Length > MaximumBytes) return false;
            try
            {
                using (var r = new BinaryReader(new MemoryStream(bytes, false)))
                {
                    if (r.ReadByte() != 1) return false;
                    var s = new BattleInitialState { StageId = r.ReadInt32(), FloorId = r.ReadByte(), WaveIndex = r.ReadInt32(), Round = r.ReadInt32(), Phase = r.ReadInt32() };
                    var size = r.ReadUInt16(); if (size > 256) return false; var map = r.ReadBytes(size); if (map.Length != size) return false; s.Map = Utf8.GetString(map);
                    s.StageStorageCount = r.ReadInt32(); ReadInts(r, s.StageCounters); s.StageFlags = r.ReadByte();
                    ReadInts(r, s.LibrarianTeamEmotion); ReadInts(r, s.EnemyTeamEmotion);
                    var actors = r.ReadByte(); if (actors != 4) return false;
                    for (var n = 0; n < actors; n++)
                    {
                        var a = new BattleActorState { ActorId = r.ReadUInt32(), ConfigurationIdentity = r.ReadUInt64(), NativeUnitId = r.ReadInt32(), NativeIndex = r.ReadInt32(), Enemy = ReadBoolean(r),
                            BookId = r.ReadInt32(), BookInstanceId = r.ReadInt32(), TurnState = r.ReadInt32(), Hp = r.ReadSingle(), StageHp = r.ReadSingle(), MaxHp = r.ReadInt32(),
                            BreakGauge = r.ReadInt32(), MaxBreakGauge = r.ReadInt32(), BreakLife = r.ReadInt32(), MaxBreakLife = r.ReadInt32(),
                            NextTurnBreak = ReadBoolean(r), BlockBreakRecovery = ReadBoolean(r), Dead = ReadBoolean(r), Extinct = ReadBoolean(r), Knockout = ReadBoolean(r),
                            PlayPoint = r.ReadInt32(), MaxPlayPoint = r.ReadInt32(), ReservedPlayPoint = r.ReadInt32(), LostPlayPoint = r.ReadInt32(), NextRoundPlayPoint = r.ReadInt32(),
                            StartingPlayPoint = r.ReadInt32(), DefaultRecoverPoint = r.ReadInt32(), RecoverPoint = r.ReadInt32(), MaxHand = r.ReadInt32(), MaxDrawHand = r.ReadInt32(),
                            EmotionLevel = r.ReadInt32(), MaxEmotionLevel = r.ReadInt32(), MaxEmotionCoins = r.ReadInt32(), MaxEgoCoins = r.ReadInt32(), MentalState = r.ReadInt32(), EmotionSkillPoint = r.ReadInt32(), ForcedLevelUps = r.ReadInt32() };
                        ReadBytes(r, a.EmotionCoins); ReadBytes(r, a.TotalEmotionCoins); ReadBytes(r, a.EgoEmotionCoins); ReadInts(r, a.Resistances); ReadInts(r, a.EmotionStatBonus); ReadInts(r, a.EffectQueueCounts);
                        var count = r.ReadByte(); if (count > 64) return false;
                        for (var c = 0; c < count; c++) a.Cards.Add(new BattleCardState { InstanceId = r.ReadUInt32(), CardId = r.ReadInt32(), Zone = (BattleCardZone)r.ReadByte(), Position = r.ReadByte(), Flags = r.ReadByte(),
                            Cost = r.ReadInt32(), CurrentCost = r.ReadInt32(), OriginalCost = r.ReadInt32(), CostAdder = r.ReadInt32(), PriorityAdder = r.ReadInt32(), Priority = r.ReadInt32(),
                            MaxCooltime = r.ReadSingle(), CurrentCooltime = r.ReadSingle(), BufCount = r.ReadInt32() });
                        foreach (var zone in a.CardZones) { count = r.ReadByte(); if (count > 64) return false; for (var p = 0; p < count; p++) zone.Add(r.ReadUInt32()); }
                        count = r.ReadByte(); if (count < 1 || count > 8) return false;
                        for (var d = 0; d < count; d++) a.SpeedDice.Add(new BattleSpeedDieState { Min = r.ReadInt32(), Max = r.ReadInt32(), Value = r.ReadInt32(), Broken = ReadBoolean(r), Controllable = ReadBoolean(r) });
                        count = r.ReadByte(); if (count > 8) return false;
                        for (var i = 0; i < count; i++) a.Intent.Add(new BattleCardIntentState { Slot = r.ReadByte(), CardInstanceId = r.ReadUInt32(), TargetActorId = r.ReadUInt32(), TargetSlot = r.ReadInt32(),
                            EarlyTargetActorId = r.ReadUInt32(), EarlyTargetSlot = r.ReadInt32(), Speed = r.ReadInt32(), EmotionMultiplier = r.ReadInt32(), FirstAction = ReadBoolean(r), IgnorePower = ReadBoolean(r),
                            BehaviourQueueCount = r.ReadInt32(), SubTargetCount = r.ReadInt32(), ExcludedDiceCount = r.ReadInt32() });
                        s.Actors.Add(a);
                    }
                    string reason;
                    if (r.BaseStream.Position != bytes.Length || !Validate(s, out reason)) return false;
                    value = s; return true;
                }
            }
            catch (IOException) { return false; }
            catch (ArgumentException) { return false; }
        }

        internal static bool Validate(BattleInitialState s, out string reason)
        {
            reason = "Unsupported or invalid first-round runtime state.";
            if (s == null || s.StageId != 3 || s.FloorId < 1 || s.FloorId > 10 || s.WaveIndex != 0 || s.Round != 1 || s.Phase != 5 ||
                !ValidMap(s.Map) || s.StageStorageCount != 0 || s.StageCounters.Any(x => x != 0) || (s.StageFlags & ~8) != 0 ||
                s.Actors.Count != 4 || s.Actors.Any(a => a == null) || s.LibrarianTeamEmotion.Any(x => x < 0 || x > 10000) || s.EnemyTeamEmotion.Any(x => x < 0 || x > 10000)) return false;
            var nativeIds = new HashSet<int>(); var nativeIndices = new HashSet<int>(); var cardIds = new HashSet<uint>(); var identities = new[] { new HashSet<ulong>(), new HashSet<ulong>() };
            for (var n = 0; n < s.Actors.Count; n++)
            {
                var a = s.Actors[n];
                if (a == null || a.ActorId != (n < 2 ? n + 1 : 101 + n - 2) || a.ConfigurationIdentity == 0 || !identities[n < 2 ? 0 : 1].Add(a.ConfigurationIdentity) || a.Enemy != (n >= 2) || a.NativeUnitId < 0 || !nativeIds.Add(a.NativeUnitId) || a.NativeIndex < 0 || a.NativeIndex > 4 || !nativeIndices.Add((a.Enemy ? 5 : 0) + a.NativeIndex) || a.BookId <= 0 ||
                    !Finite(a.Hp) || !Finite(a.StageHp) || a.Hp <= 0 || a.Hp > a.MaxHp || a.StageHp <= 0 || a.StageHp > a.MaxHp || a.MaxHp > 10000 ||
                    a.BreakGauge < 0 || a.BreakGauge > a.MaxBreakGauge || a.MaxBreakGauge > 10000 || a.BreakLife < 0 || a.BreakLife > a.MaxBreakLife || a.MaxBreakLife > 10 ||
                    a.Dead || a.Extinct || a.Knockout || a.NextTurnBreak || a.BlockBreakRecovery || a.TurnState < 0 || a.TurnState > 32 ||
                    a.PlayPoint < 0 || a.PlayPoint > a.MaxPlayPoint || a.MaxPlayPoint > 20 || a.ReservedPlayPoint < 0 || a.ReservedPlayPoint > 20 ||
                    !Small(a.LostPlayPoint) || !Small(a.NextRoundPlayPoint) || !Small(a.StartingPlayPoint) || !Small(a.DefaultRecoverPoint) || !Small(a.RecoverPoint) || a.MaxHand < 1 || a.MaxHand > 64 || a.MaxDrawHand < 1 || a.MaxDrawHand > 64 ||
                    a.EmotionLevel < 0 || a.EmotionLevel > a.MaxEmotionLevel || a.MaxEmotionLevel > 5 || a.MaxEmotionCoins < 0 || a.MaxEmotionCoins > 100 || a.MaxEgoCoins < 0 || a.MaxEgoCoins > 100 || a.MentalState < 0 || a.MentalState > 4 ||
                    a.EmotionSkillPoint < 0 || a.EmotionSkillPoint > 10 || a.ForcedLevelUps != 0 || !ValidCoins(a.EmotionCoins) || !ValidCoins(a.TotalEmotionCoins) || !ValidCoins(a.EgoEmotionCoins) ||
                    a.Resistances.Any(x => x < 0 || x > 16) || a.EmotionStatBonus.Any(x => x != 0) || a.EffectQueueCounts.Any(x => x != 0) || a.Cards.Count < 1 || a.Cards.Count > 64 || a.SpeedDice.Count < 1 || a.SpeedDice.Count > 8 || a.Intent.Count > a.SpeedDice.Count) return false;
                var positions = new HashSet<int>(); var previousZone = -1;
                foreach (var c in a.Cards)
                {
                    if (c == null || c.InstanceId == 0 || !cardIds.Add(c.InstanceId) || c.CardId < 1 || c.CardId > 5 || (byte)c.Zone > 4 || (int)c.Zone < previousZone ||
                        c.Flags > 31 || c.Cost < 0 || c.Cost > 20 || c.CurrentCost < 0 || c.CurrentCost > 20 || c.OriginalCost < 0 || c.OriginalCost > 20 || Math.Abs((long)c.CostAdder) > 20 || Math.Abs((long)c.PriorityAdder) > 1000 || Math.Abs((long)c.Priority) > 10000 ||
                        !Finite(c.MaxCooltime) || !Finite(c.CurrentCooltime) || c.MaxCooltime < 0 || c.MaxCooltime > 1000 || c.CurrentCooltime < 0 || c.CurrentCooltime > c.MaxCooltime || c.BufCount != 0 || !positions.Add((int)c.Zone * 256 + c.Position)) return false;
                    previousZone = (int)c.Zone;
                }
                var memberships = new Dictionary<uint, int>();
                for (var zone = 0; zone < a.CardZones.Length; zone++)
                {
                    if (a.CardZones[zone] == null || a.CardZones[zone].Count > 64 || a.CardZones[zone].Distinct().Count() != a.CardZones[zone].Count) return false;
                    foreach (var instance in a.CardZones[zone])
                    {
                        if (!a.Cards.Any(c => c.InstanceId == instance)) return false;
                        int mask; memberships.TryGetValue(instance, out mask); memberships[instance] = mask | (1 << zone);
                    }
                }
                foreach (var c in a.Cards)
                {
                    int mask;
                    if (!memberships.TryGetValue(c.InstanceId, out mask) || (mask != (1 << (int)c.Zone) && !(c.Zone == BattleCardZone.Used && mask == ((1 << 2) | (1 << 4)))) ||
                        c.Position >= a.CardZones[(int)c.Zone].Count || a.CardZones[(int)c.Zone][c.Position] != c.InstanceId) return false;
                }
                foreach (var d in a.SpeedDice) if (d == null || d.Min < 0 || d.Max < d.Min || d.Max > 1000 || d.Value < 0 || d.Value > 1000) return false;
                var slots = new HashSet<byte>(); var assigned = new HashSet<uint>();
                foreach (var i in a.Intent)
                {
                    if (i == null || !a.Enemy || i.Slot >= a.SpeedDice.Count || !slots.Add(i.Slot) || !assigned.Add(i.CardInstanceId) ||
                        !a.Cards.Any(c => c.InstanceId == i.CardInstanceId) || i.TargetActorId == 0 || i.EarlyTargetActorId == 0 || i.BehaviourQueueCount != 0 || i.SubTargetCount != 0 || i.ExcludedDiceCount != 0 ||
                        i.Speed < 0 || i.Speed > 1000 || i.EmotionMultiplier < 0 || i.EmotionMultiplier > 10) return false;
                    var target = s.Actors.Find(x => x.ActorId == i.TargetActorId); var early = s.Actors.Find(x => x.ActorId == i.EarlyTargetActorId);
                    if (target == null || target.Enemy || early == null || early.Enemy || i.TargetSlot < 0 || i.TargetSlot >= target.SpeedDice.Count || i.EarlyTargetSlot < 0 || i.EarlyTargetSlot >= early.SpeedDice.Count) return false;
                }
            }
            reason = null; return true;
        }
        internal static bool ValidateAgainstManifest(BattleInitialState value, BattleManifest manifest, out string reason)
        {
            if (!Validate(value, out reason) || !BattleManifestCodec.Validate(manifest, out reason)) return false;
            reason = "Runtime actors or card instances do not match the frozen manifest.";
            if (value.StageId != manifest.StageId || value.FloorId != manifest.FloorId || manifest.Enemies.Count != 2) return false;
            for (var i = 0; i < value.Actors.Count; i++)
            {
                var a = value.Actors[i]; var book = i < 2 ? manifest.Librarians[i].BookId : manifest.Enemies[i - 2].BookId;
                var cards = i < 2 ? manifest.Librarians[i].Cards : manifest.Enemies[i - 2].Cards;
                if (a.ConfigurationIdentity != (i < 2 ? manifest.Librarians[i].UnitIdentity : manifest.Enemies[i - 2].EnemyIdentity) || a.BookId != book ||
                    (i < 2 && (a.BookInstanceId != manifest.Librarians[i].BookInstanceId || a.NativeIndex != manifest.Librarians.Count(other => other.RosterIndex < manifest.Librarians[i].RosterIndex))) ||
                    !a.Cards.Select(c => c.CardId).OrderBy(x => x).SequenceEqual(cards.OrderBy(x => x))) return false;
            }
            reason = null; return true;
        }
        private static bool Finite(float x) { return !float.IsNaN(x) && !float.IsInfinity(x); }
        private static bool ValidMap(string value) { try { return value != null && Utf8.GetByteCount(value) <= 256 && !value.Any(char.IsControl); } catch (ArgumentException) { return false; } }
        private static bool Small(int x) { return x >= -20 && x <= 20; }
        private static bool ValidCoins(List<byte> coins) { return coins.Count <= 128 && coins.All(x => x <= 2); }
        private static bool ReadBoolean(BinaryReader r) { var b = r.ReadByte(); if (b > 1) throw new ArgumentException(); return b == 1; }
        private static void WriteInts(BinaryWriter w, int[] values) { foreach (var x in values) w.Write(x); }
        private static void ReadInts(BinaryReader r, int[] values) { for (var i = 0; i < values.Length; i++) values[i] = r.ReadInt32(); }
        private static void WriteBytes(BinaryWriter w, List<byte> values) { w.Write((byte)values.Count); foreach (var x in values) w.Write(x); }
        private static void ReadBytes(BinaryReader r, List<byte> values) { var count = r.ReadByte(); if (count > 128) throw new ArgumentException(); for (var i = 0; i < count; i++) values.Add(r.ReadByte()); }
    }
}
