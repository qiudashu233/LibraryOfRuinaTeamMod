using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RuinaCoop
{
    internal static class PreparationMirror
    {
        internal const int MaxSectionBytes = 32768;
        internal const int MaxWaves = 16;
        internal const int MaxEnemiesPerWave = 32;
        internal const int MaxEnemies = 128;
        private const int MaxTextBytes = 256;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        internal static void WriteData(BinaryWriter writer, PreparationSnapshot preparation, bool canonical = false)
        {
            var value = preparation ?? new PreparationSnapshot();
            string reason;
            if (!Validate(value, out reason)) throw new ArgumentException(reason);
            using (var stream = new MemoryStream()) using (var body = new BinaryWriter(stream))
            {
                WriteBody(body, value, canonical);
                if (stream.Length > MaxSectionBytes) throw new ArgumentException("Preparation metadata exceeds the section limit.");
                writer.Write((ushort)stream.Length); writer.Write(stream.ToArray());
            }
        }
        internal static bool TryReadData(BinaryReader reader, out PreparationSnapshot preparation, out string reason)
        {
            preparation = null; reason = null;
            try
            {
                var length = reader.ReadUInt16();
                if (length < 33 || length > MaxSectionBytes || length > reader.BaseStream.Length - reader.BaseStream.Position)
                    return Reject(out reason, "Preparation section length is invalid.");
                var bytes = reader.ReadBytes(length);
                using (var body = new BinaryReader(new MemoryStream(bytes, false)))
                {
                    var value = new PreparationSnapshot
                    {
                        Available = ReadBool(body), Reason = (PreparationReason)body.ReadByte(), Phase = (PreparationPhase)body.ReadByte(),
                        Revision = body.ReadUInt32(), ContextId = body.ReadUInt64(), StageId = body.ReadInt32(), FloorId = body.ReadByte(),
                        MaxUnits = body.ReadByte(), CurrentWaveIndex = body.ReadByte(), ClaimRevision = body.ReadUInt32(), DeckRevision = body.ReadUInt32()
                    };
                    var count = body.ReadByte(); if (count > 10) return Reject(out reason, "Too many preparation floors.");
                    for (var i = 0; i < count; i++) value.Floors.Add(new PreparationFloorEntry
                    { FloorId = body.ReadByte(), CanParticipate = ReadBool(body), Reason = (PreparationUnitReason)body.ReadByte() });
                    count = body.ReadByte(); if (count > 5) return Reject(out reason, "Too many preparation units.");
                    for (var i = 0; i < count; i++) value.Participants.Add(new PreparationUnitEntry
                    { UnitIndex = body.ReadByte(), UnitIdentity = body.ReadUInt64(), CanParticipate = ReadBool(body), Participating = ReadBool(body), Reason = (PreparationUnitReason)body.ReadByte() });
                    count = body.ReadByte(); if (count > MaxWaves) return Reject(out reason, "Too many preparation waves.");
                    var total = 0;
                    for (var i = 0; i < count; i++)
                    {
                        var wave = new PreparationWaveEntry { WaveIndex = body.ReadByte() };
                        var enemies = body.ReadByte(); total += enemies;
                        if (enemies > MaxEnemiesPerWave || total > MaxEnemies) return Reject(out reason, "Too many preparation enemies.");
                        for (var j = 0; j < enemies; j++)
                        {
                            var enemy = new PreparationEnemyEntry
                            { EnemyIdentity = body.ReadUInt64(), EnemyId = body.ReadInt32(), BookId = body.ReadInt32(), Name = ReadText(body), Unknown = ReadBool(body), CardsVisible = ReadBool(body) };
                            var cards = body.ReadByte(); if (cards > 64) return Reject(out reason, "Too many enemy cards.");
                            for (var card = 0; card < cards; card++) enemy.Cards.Add(body.ReadInt32());
                            ReadDisplay(body, enemy.Display);
                            wave.Enemies.Add(enemy);
                        }
                        value.Waves.Add(wave);
                    }
                    count = body.ReadByte(); if (count > 5) return Reject(out reason, "Too many preparation controllers.");
                    for (var i = 0; i < count; i++) value.Controllers.Add(new PreparationControllerEntry
                    { PlayerId = body.ReadUInt64(), Connected = ReadBool(body), Ready = ReadBool(body) });
                    if (body.BaseStream.Position != body.BaseStream.Length) return Reject(out reason, "Preparation section has trailing data.");
                    if (!Validate(value, out reason)) return false;
                    preparation = value; return true;
                }
            }
            catch (EndOfStreamException) { return Reject(out reason, "Preparation section is truncated."); }
            catch (IOException) { return Reject(out reason, "Preparation section is invalid."); }
            catch (ArgumentException) { return Reject(out reason, "Preparation section contains invalid data."); }
        }
        internal static byte[] EncodeContent(PreparationSnapshot value)
        {
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            { WriteData(writer, value, true); return stream.ToArray(); }
        }
        internal static PreparationSnapshot Clone(PreparationSnapshot value)
        {
            if (value == null) return new PreparationSnapshot();
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                WriteData(writer, value); stream.Position = 0;
                PreparationSnapshot copy; string reason;
                using (var reader = new BinaryReader(stream))
                    if (!TryReadData(reader, out copy, out reason)) throw new ArgumentException(reason);
                return copy;
            }
        }
        internal static bool Validate(PreparationSnapshot value, out string reason)
        {
            reason = null;
            if (value == null) return true;
            if (value.Reason > PreparationReason.PacketLimit || value.Phase > PreparationPhase.Return || value.StageId < 0 ||
                (value.FloorId != byte.MaxValue && (value.FloorId < 1 || value.FloorId > 10)) || value.MaxUnits > 5 ||
                value.Floors.Count > 10 || value.Participants.Count > 5 || value.Waves.Count > MaxWaves || value.Controllers.Count > 5)
                return Reject(out reason, "Preparation context or count is invalid.");
            if (value.Available != (value.Reason == PreparationReason.None)) return Reject(out reason, "Preparation availability and reason disagree.");
            if (value.Phase == PreparationPhase.Selection && (value.ContextId != 0 || value.FloorId != byte.MaxValue || value.MaxUnits != 0 || value.Participants.Count != 0 || value.Controllers.Count != 0))
                return Reject(out reason, "Selection carries an active reception roster.");
            if (value.Phase != PreparationPhase.Selection && (value.ContextId == 0 || value.StageId <= 0))
                return Reject(out reason, "Reception has no valid context identity.");
            if (!value.Available)
            {
                if (value.MaxUnits != 0 || value.CurrentWaveIndex != byte.MaxValue || value.Floors.Count != 0 || value.Participants.Count != 0 || value.Waves.Count != 0 || value.Controllers.Count != 0)
                    return Reject(out reason, "Unavailable preparation carries partial metadata.");
                return true;
            }
            if (value.StageId <= 0) return Reject(out reason, "Available preparation has no stage.");
            var floors = new HashSet<byte>();
            foreach (var floor in value.Floors)
                if (floor == null || floor.FloorId < 1 || floor.FloorId > 10 || !floors.Add(floor.FloorId) || !ValidPermission(floor.CanParticipate, floor.Reason))
                    return Reject(out reason, "Preparation floor is invalid or duplicated.");
            if (value.Phase != PreparationPhase.Selection && (value.MaxUnits == 0 || value.Participants.Count == 0 || !floors.Contains(value.FloorId)))
                return Reject(out reason, "Reception roster does not match a challenge floor.");
            var units = new HashSet<ulong>(); var participating = 0;
            for (var i = 0; i < value.Participants.Count; i++)
            {
                var unit = value.Participants[i];
                if (unit == null || unit.UnitIndex != i || unit.UnitIdentity == 0 || !units.Add(unit.UnitIdentity) || !ValidPermission(unit.CanParticipate, unit.Reason) ||
                    (unit.Participating && !unit.CanParticipate)) return Reject(out reason, "Preparation roster identity or permission is invalid.");
                if (unit.Participating) participating++;
            }
            if (participating > value.MaxUnits) return Reject(out reason, "Preparation exceeds the stage unit limit.");
            if (participating > 0 && !value.Floors.Find(row => row.FloorId == value.FloorId).CanParticipate)
                return Reject(out reason, "An unavailable challenge floor has selected participants.");
            var enemyIds = new HashSet<ulong>(); var total = 0;
            for (var i = 0; i < value.Waves.Count; i++)
            {
                var wave = value.Waves[i];
                if (wave == null || wave.WaveIndex != i || wave.Enemies.Count > MaxEnemiesPerWave || (total += wave.Enemies.Count) > MaxEnemies)
                    return Reject(out reason, "Preparation wave index or enemy count is invalid.");
                foreach (var enemy in wave.Enemies)
                {
                    if (enemy == null || enemy.EnemyIdentity == 0 || !enemyIds.Add(enemy.EnemyIdentity) || !ValidText(enemy.Name) || enemy.Cards.Count > 64 || enemy.Cards.Any(id => id <= 0))
                        return Reject(out reason, "Enemy identity, name, or cards are invalid.");
                    if (enemy.Unknown)
                    {
                        if (enemy.EnemyId != 0 || enemy.BookId != 0 || enemy.CardsVisible || enemy.Cards.Count != 0 ||
                            (enemy.Name != "" && enemy.Name != "???") || enemy.Display.Available || enemy.Display.AppearanceAvailable || !EmptyStats(enemy.Display))
                            return Reject(out reason, "Unknown enemy leaks hidden metadata.");
                    }
                    else if (enemy.EnemyId <= 0 || enemy.BookId <= 0) return Reject(out reason, "Visible enemy has no XML identity.");
                    if (!enemy.CardsVisible && enemy.Cards.Count != 0) return Reject(out reason, "Hidden enemy deck carries cards.");
                    if (!ValidDisplay(enemy.Display)) return Reject(out reason, "Enemy display metadata is invalid.");
                }
            }
            if ((value.Waves.Count == 0 && value.CurrentWaveIndex != byte.MaxValue) || (value.Waves.Count > 0 && value.CurrentWaveIndex >= value.Waves.Count))
                return Reject(out reason, "Current preview wave is invalid.");
            var controllers = new HashSet<ulong>();
            foreach (var row in value.Controllers)
                if (row == null || row.PlayerId == 0 || !controllers.Add(row.PlayerId) || (row.Ready && (!row.Connected || value.Phase != PreparationPhase.Editing)))
                    return Reject(out reason, "Preparation controller or ready state is invalid.");
            if (value.Controllers.Count > participating) return Reject(out reason, "Observers occur in the readiness controller list.");
            return true;
        }
        internal static bool Validate(ProgressSnapshot snapshot, out string reason)
        {
            reason = null; if (snapshot == null) return Reject(out reason, "Missing progress snapshot.");
            var value = snapshot.Preparation;
            if (!Validate(value, out reason) || value == null || !value.Available) return reason == null;
            if (value.Revision == 0 || value.StageId != snapshot.SelectedStageId || value.ClaimRevision != snapshot.ClaimRevision || value.DeckRevision != snapshot.DeckRevision)
                return Reject(out reason, "Preparation dependencies do not match the progress snapshot.");
            if (value.Phase == PreparationPhase.Selection) return true;
            if (value.FloorId != snapshot.SelectedFloorId || value.Participants.Count != snapshot.UnitDecks.Count || value.Participants.Count != snapshot.ClaimOwners.Count)
                return Reject(out reason, "Preparation and equipment rosters disagree.");
            for (var i = 0; i < value.Participants.Count; i++)
            {
                if (snapshot.UnitDecks[i] == null || value.Participants[i].UnitIdentity != snapshot.UnitDecks[i].UnitIdentity)
                    return Reject(out reason, "Preparation and equipment unit identities disagree.");
                if (!value.Participants[i].Participating && snapshot.ClaimOwners[i] != 0)
                    return Reject(out reason, "An unselected librarian still has a preparation claim.");
            }
            return true;
        }
        internal static bool CanUseUnit(ProgressSnapshot snapshot, int index)
        {
            if (snapshot == null || index < 0 || index >= snapshot.UnitDecks.Count) return false;
            var value = snapshot.Preparation;
            // Existing codec fixtures and the selection page have no active reception.
            if (value == null || value.Phase == PreparationPhase.Selection) return true;
            string reason;
            return value.Available && value.Phase == PreparationPhase.Editing && Validate(snapshot, out reason) &&
                index < value.Participants.Count && value.Participants[index].CanParticipate && value.Participants[index].Participating;
        }
        private static void WriteBody(BinaryWriter writer, PreparationSnapshot value, bool canonical)
        {
            writer.Write((byte)(value.Available ? 1 : 0)); writer.Write((byte)value.Reason); writer.Write((byte)value.Phase); writer.Write(canonical ? 0u : value.Revision);
            writer.Write(value.ContextId); writer.Write(value.StageId); writer.Write(value.FloorId); writer.Write(value.MaxUnits); writer.Write(value.CurrentWaveIndex);
            writer.Write(value.ClaimRevision); writer.Write(value.DeckRevision);
            writer.Write((byte)value.Floors.Count);
            foreach (var floor in value.Floors.OrderBy(row => row.FloorId)) { writer.Write(floor.FloorId); writer.Write((byte)(floor.CanParticipate ? 1 : 0)); writer.Write((byte)floor.Reason); }
            writer.Write((byte)value.Participants.Count);
            foreach (var unit in value.Participants) { writer.Write(unit.UnitIndex); writer.Write(unit.UnitIdentity); writer.Write((byte)(unit.CanParticipate ? 1 : 0)); writer.Write((byte)(unit.Participating ? 1 : 0)); writer.Write((byte)unit.Reason); }
            writer.Write((byte)value.Waves.Count);
            foreach (var wave in value.Waves)
            {
                writer.Write(wave.WaveIndex); writer.Write((byte)wave.Enemies.Count);
                foreach (var enemy in wave.Enemies)
                {
                    writer.Write(enemy.EnemyIdentity); writer.Write(enemy.EnemyId); writer.Write(enemy.BookId); WriteText(writer, enemy.Name);
                    writer.Write((byte)(enemy.Unknown ? 1 : 0)); writer.Write((byte)(enemy.CardsVisible ? 1 : 0)); writer.Write((byte)enemy.Cards.Count);
                    foreach (var card in enemy.Cards) writer.Write(card);
                    WriteDisplay(writer, enemy.Display);
                }
            }
            writer.Write((byte)value.Controllers.Count);
            foreach (var row in value.Controllers.OrderBy(row => row.PlayerId))
            { writer.Write(row.PlayerId); writer.Write((byte)(row.Connected ? 1 : 0)); writer.Write((byte)(!canonical && row.Ready ? 1 : 0)); }
        }
        private static void WriteDisplay(BinaryWriter writer, ProgressSnapshot.UnitDisplayEntry value)
        {
            writer.Write((byte)(value.Available ? 1 : 0));
            if (value.Available) { writer.Write(value.MaxHp); writer.Write(value.Break); writer.Write((byte)value.PassiveIds.Count); foreach (var id in value.PassiveIds) writer.Write(id); }
            writer.Write((byte)(value.AppearanceAvailable ? 1 : 0));
            if (!value.AppearanceAvailable) return;
            writer.Write(value.DefaultBookId); writer.Write(value.CustomBookId); writer.Write((byte)(value.IsSephirah ? 1 : 0)); writer.Write(value.Gender); writer.Write(value.AppearanceType);
            WriteText(writer, value.CharacterSkin); writer.Write((byte)(value.UseCustom ? 1 : 0)); writer.Write(value.SpecialCustomId);
            foreach (var part in new[] { value.FrontHair, value.BackHair, value.Eye, value.Brow, value.Mouth, value.Head }) writer.Write(part);
            writer.Write(value.HairColor); writer.Write(value.EyeColor); writer.Write(value.SkinColor); writer.Write(value.Height);
        }
        private static void ReadDisplay(BinaryReader reader, ProgressSnapshot.UnitDisplayEntry value)
        {
            value.Available = ReadBool(reader);
            if (value.Available) { value.MaxHp = reader.ReadInt32(); value.Break = reader.ReadInt32(); var count = reader.ReadByte(); if (count > 64) throw new ArgumentException("Too many enemy passives."); for (var i = 0; i < count; i++) value.PassiveIds.Add(reader.ReadInt32()); }
            value.AppearanceAvailable = ReadBool(reader); if (!value.AppearanceAvailable) return;
            value.DefaultBookId = reader.ReadInt32(); value.CustomBookId = reader.ReadInt32(); value.IsSephirah = ReadBool(reader); value.Gender = reader.ReadByte(); value.AppearanceType = reader.ReadByte();
            value.CharacterSkin = ReadText(reader); value.UseCustom = ReadBool(reader); value.SpecialCustomId = reader.ReadInt32();
            value.FrontHair = reader.ReadInt32(); value.BackHair = reader.ReadInt32(); value.Eye = reader.ReadInt32(); value.Brow = reader.ReadInt32(); value.Mouth = reader.ReadInt32(); value.Head = reader.ReadInt32();
            value.HairColor = reader.ReadUInt32(); value.EyeColor = reader.ReadUInt32(); value.SkinColor = reader.ReadUInt32(); value.Height = reader.ReadInt32();
        }
        private static bool ValidDisplay(ProgressSnapshot.UnitDisplayEntry value)
        {
            if (value == null || (!value.Available && !EmptyStats(value))) return false;
            if (value.Available && (value.MaxHp <= 0 || value.MaxHp > 1000000 || value.Break < 0 || value.Break > 1000000 || value.PassiveIds.Count > 64 ||
                value.PassiveIds.Any(id => id <= 0 || id == 9999999) || value.PassiveIds.Distinct().Count() != value.PassiveIds.Count)) return false;
            return !value.AppearanceAvailable || (value.DefaultBookId > 0 && value.CustomBookId >= 0 && value.Gender <= 4 && value.AppearanceType <= 4 &&
                (value.SpecialCustomId == -1 || value.SpecialCustomId > 0) && ValidText(value.CharacterSkin) && value.Height >= 140 && value.Height <= 3000 &&
                new[] { value.FrontHair, value.BackHair, value.Eye, value.Brow, value.Mouth, value.Head }.All(part => part >= -1 && part <= 100000));
        }
        private static bool EmptyStats(ProgressSnapshot.UnitDisplayEntry value) { return value.MaxHp == 0 && value.Break == 0 && value.PassiveIds.Count == 0; }
        private static bool ValidPermission(bool available, PreparationUnitReason reason) { return reason <= PreparationUnitReason.FloorUnavailable && available == (reason == PreparationUnitReason.None); }
        private static bool ValidText(string value) { try { return value != null && Utf8.GetByteCount(value) <= MaxTextBytes && !value.Any(char.IsControl); } catch (ArgumentException) { return false; } }
        private static void WriteText(BinaryWriter writer, string value) { var bytes = Utf8.GetBytes(value); writer.Write((ushort)bytes.Length); writer.Write(bytes); }
        private static string ReadText(BinaryReader reader) { var count = reader.ReadUInt16(); if (count > MaxTextBytes || count > reader.BaseStream.Length - reader.BaseStream.Position) throw new ArgumentException("Invalid preparation text length."); return Utf8.GetString(reader.ReadBytes(count)); }
        private static bool ReadBool(BinaryReader reader) { var value = reader.ReadByte(); if (value > 1) throw new ArgumentException("Invalid preparation boolean."); return value != 0; }
        private static bool Reject(out string reason, string message) { reason = message; return false; }
    }
}
