using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RuinaCoop;
using UI;
using UnityEngine;

internal static class DisplayChecks
{
    private static int _checks;
    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new Exception("FAIL display metadata: " + message);
    }

    internal static void Run(ulong room)
    {
        RoundTrip(room);
        Malformed(room);
        Capture(room);
        Console.WriteLine("PASS: " + _checks + " native display metadata checks.");
    }

    private static ProgressSnapshot Sample()
    {
        var snapshot = new ProgressSnapshot
        {
            SelectedStageId = 101, SelectedFloorId = (byte)SephirahType.Malkuth,
            ClaimRevision = 3, DeckRevision = 5
        };
        snapshot.Stages.Add(new ProgressSnapshot.StageEntry { Id = 101, State = StoryState.Open, Name = "Stage" });
        var floor = new ProgressSnapshot.FloorEntry { Sephirah = SephirahType.Malkuth };
        snapshot.Floors.Add(floor);
        for (var i = 0; i < 2; i++)
        {
            floor.Units.Add("Host librarian " + i);
            snapshot.ClaimOwners.Add((ulong)(123 + i));
            var deck = new ProgressSnapshot.UnitDeckEntry
            {
                UnitIdentity = (ulong)(100 + i), BookId = 200 + i, BookInstanceId = 300 + i, Capacity = 9
            };
            deck.Cards.Add(400);
            var display = deck.Display;
            display.Available = true;
            display.MaxHp = 177;
            display.Break = 83;
            display.PassiveIds.AddRange(new[] { 10, 11 });
            display.AppearanceAvailable = true;
            display.DefaultBookId = 1;
            display.CustomBookId = 201;
            display.IsSephirah = true;
            display.Gender = 1;
            display.AppearanceType = 2;
            display.CharacterSkin = "Malkuth_1";
            display.UseCustom = true;
            display.SpecialCustomId = -1;
            display.FrontHair = 1;
            display.BackHair = 2;
            display.Eye = 3;
            display.Brow = 4;
            display.Mouth = 5;
            display.Head = 6;
            display.HairColor = 0x12345678;
            display.EyeColor = 0xFEDCBA98;
            display.SkinColor = 0xFFFFFFFF;
            display.Height = 185;
            snapshot.UnitDecks.Add(deck);
        }
        snapshot.CardStock.Add(new ProgressSnapshot.CardStockEntry { Id = 400, Count = 20 });
        return snapshot;
    }

    private static void RoundTrip(ulong room)
    {
        var snapshot = Sample();
        var bytes = snapshot.Encode(room);
        Check(bytes[4] == 7, "wire7 tag");
        Check(ProgressSnapshot.TryDecode(bytes, room, out var parsed), "wire7 round trip");
        var deck = parsed.UnitDecks[0];
        var display = deck.Display;
        Check(deck.UnitIdentity == 100 && parsed.UnitDecks[1].UnitIdentity == 101 &&
            display.Available && display.MaxHp == 177 && display.Break == 83 &&
            display.PassiveIds.SequenceEqual(new[] { 10, 11 }), "identity, actual stats and effective passives");
        Check(display.AppearanceAvailable && display.DefaultBookId == 1 && display.CustomBookId == 201 &&
            display.IsSephirah && display.Gender == 1 && display.AppearanceType == 2 &&
            display.CharacterSkin == "Malkuth_1" && display.UseCustom && display.SpecialCustomId == -1 &&
            display.FrontHair == 1 && display.BackHair == 2 && display.Eye == 3 && display.Brow == 4 &&
            display.Mouth == 5 && display.Head == 6 && display.Height == 185 &&
            display.HairColor == 0x12345678 && display.EyeColor == 0xFEDCBA98 && display.SkinColor == uint.MaxValue,
            "bounded appearance metadata and RGBA byte order");
        Check(parsed.Encode(room).SequenceEqual(bytes), "decode/encode preserves complete metadata");
        var content = DeckMirror.EncodeContent(snapshot);
        snapshot.Sequence++; snapshot.ClaimRevision++; snapshot.DeckRevision++;
        snapshot.UnitDecks[0].Display.PassiveIds.Reverse();
        Check(DeckMirror.EncodeContent(snapshot).SequenceEqual(content), "canonical metadata ignores revisions and passive order");
        snapshot.UnitDecks[0].Display.MaxHp++;
        Check(!DeckMirror.EncodeContent(snapshot).SequenceEqual(content), "actual stats participate in content revision");
        snapshot = Sample(); content = DeckMirror.EncodeContent(snapshot);
        snapshot.UnitDecks[0].Display.CharacterSkin = "Malkuth_2";
        Check(!DeckMirror.EncodeContent(snapshot).SequenceEqual(content), "appearance participates in content revision");
        snapshot = Sample(); content = DeckMirror.EncodeContent(snapshot);
        snapshot.UnitDecks[0].UnitIdentity++;
        snapshot.UnitDecks[1].UnitIdentity++;
        Check(!DeckMirror.EncodeContent(snapshot).SequenceEqual(content), "roster tokens participate in content revision");
        snapshot = Sample();
        foreach (var item in snapshot.UnitDecks)
        {
            item.UnitIdentity = 0;
            item.Display.Available = item.Display.AppearanceAvailable = false;
            item.Display.MaxHp = -100; item.Display.CharacterSkin = "../omitted";
        }
        bytes = snapshot.Encode(room);
        Check(ProgressSnapshot.TryDecode(bytes, room, out parsed) && !parsed.UnitDecks[0].Display.Available &&
            !parsed.UnitDecks[0].Display.AppearanceAvailable && parsed.UnitDecks[0].Display.MaxHp == 0 &&
            parsed.UnitDecks[0].Display.CharacterSkin == "", "unavailable data is omitted and never invented");
        snapshot = Sample(); display = snapshot.UnitDecks[0].Display;
        display.MaxHp = DeckMirror.MaxDisplayStat; display.Break = 0;
        display.PassiveIds.Clear(); display.PassiveIds.AddRange(Enumerable.Range(1, DeckMirror.MaxPassiveIds));
        display.Gender = display.AppearanceType = 4;
        display.FrontHair = -1; display.BackHair = DeckMirror.MaxPartId; display.Height = 3000;
        display.SpecialCustomId = int.MaxValue; display.CharacterSkin = new string('a', DeckMirror.MaxSkinBytes);
        Check(ProgressSnapshot.TryDecode(snapshot.Encode(room), room, out parsed) &&
            parsed.UnitDecks[0].Display.PassiveIds.Count == 64, "maximum bounded metadata round trip");
        display.Height = 140; display.CharacterSkin = new string('图', 42) + "ab";
        Check(ProgressSnapshot.TryDecode(snapshot.Encode(room), room, out parsed) &&
            parsed.UnitDecks[0].Display.CharacterSkin == display.CharacterSkin, "128-byte UTF8 skin boundary");
    }

    private static void Malformed(ulong room)
    {
        var bytes = Sample().Encode(room);
        var offsets = Locate(bytes);
        Reject(Change(bytes, 4, new byte[] { 3 }), room, "wire3 is incompatible");
        Reject(Change(bytes, offsets.Count, new byte[] { 1 }), room, "metadata count mismatch");
        Reject(Change(bytes, offsets.Unit[0], BitConverter.GetBytes(0UL)), room, "zero display unit identity");
        Reject(Change(bytes, offsets.Unit[1], BitConverter.GetBytes(100UL)), room, "duplicate roster token");
        for (var flag = 2; flag <= byte.MaxValue; flag++)
        {
            Reject(Change(bytes, offsets.Available, new[] { (byte)flag }), room, "unknown stats flag " + flag);
            Reject(Change(bytes, offsets.Appearance, new[] { (byte)flag }), room, "unknown appearance flag " + flag);
        }
        foreach (var hp in new[] { int.MinValue, -1, 0, 1000001, int.MaxValue })
            Reject(Change(bytes, offsets.Hp, BitConverter.GetBytes(hp)), room, "invalid HP " + hp);
        foreach (var bp in new[] { int.MinValue, -1, 1000001, int.MaxValue })
            Reject(Change(bytes, offsets.Break, BitConverter.GetBytes(bp)), room, "invalid break " + bp);
        Reject(Change(bytes, offsets.PassiveCount, new byte[] { 65 }), room, "too many passives");
        foreach (var id in new[] { int.MinValue, -1, 0, 9999999 })
            Reject(Change(bytes, offsets.Passives, BitConverter.GetBytes(id)), room, "invalid passive ID " + id);
        Reject(Change(bytes, offsets.Passives + 4, BitConverter.GetBytes(10)), room, "duplicate passives");
        Reject(Change(bytes, offsets.DefaultBook, BitConverter.GetBytes(0)), room, "missing default page");
        Reject(Change(bytes, offsets.CustomBook, BitConverter.GetBytes(-1)), room, "negative appearance page");
        Reject(Change(bytes, offsets.Flags, new byte[] { 4 }), room, "unknown appearance bit");
        for (var gender = 5; gender <= byte.MaxValue; gender++)
        {
            Reject(Change(bytes, offsets.Gender, new[] { (byte)gender }), room, "unknown gender " + gender);
            Reject(Change(bytes, offsets.AppearanceType, new[] { (byte)gender }), room, "unknown appearance type " + gender);
        }
        Reject(Change(bytes, offsets.SkinLength, BitConverter.GetBytes((ushort)129)), room, "oversize skin bytes");
        Reject(Change(bytes, offsets.Skin, new byte[] { 0xFF }), room, "invalid skin UTF8");
        foreach (var c in new[] { (char)47, (char)92, (char)58, (char)0 })
            Reject(Change(bytes, offsets.Skin, new[] { (byte)c }), room, "unsafe resource name " + (int)c);
        Reject(Change(bytes, offsets.Skin, new byte[] { 46, 46 }), room, "resource traversal");
        Reject(Change(bytes, offsets.Special, BitConverter.GetBytes(0)), room, "invalid special customization zero");
        Reject(Change(bytes, offsets.Special, BitConverter.GetBytes(-2)), room, "invalid special customization negative");
        foreach (var part in new[] { -2, 1024, int.MaxValue })
            Reject(Change(bytes, offsets.Part, BitConverter.GetBytes(part)), room, "invalid appearance part " + part);
        foreach (var height in new[] { 139, 3001, int.MinValue, int.MaxValue })
            Reject(Change(bytes, offsets.Height, BitConverter.GetBytes(height)), room, "invalid height " + height);
        for (var size = offsets.Count; size < bytes.Length; size++)
            Reject(bytes.Take(size).ToArray(), room, "metadata truncation " + size);
        Reject(bytes.Concat(new byte[] { 0 }).ToArray(), room, "trailing metadata bytes");
        BadEncode(room, s => s.UnitDecks[1].UnitIdentity = s.UnitDecks[0].UnitIdentity, "duplicate unit token encoder");
        BadEncode(room, s => s.UnitDecks[0].Display.MaxHp = 0, "invalid HP encoder");
        BadEncode(room, s => s.UnitDecks[0].Display.PassiveIds.Add(10), "duplicate passives encoder");
        BadEncode(room, s => s.UnitDecks[0].Display.PassiveIds.AddRange(Enumerable.Range(20, 63)), "passive bound encoder");
        BadEncode(room, s => s.UnitDecks[0].Display.CharacterSkin = new string('a', 129), "skin bound encoder");
        BadEncode(room, s => s.UnitDecks[0].Display.CharacterSkin = "\uD800", "malformed Unicode encoder");
        BadEncode(room, s => s.UnitDecks[0].Display.SpecialCustomId = 0, "invalid special ID encoder");
    }

    private static void Capture(ulong room)
    {
        var snapshot = Sample();
        var floor = snapshot.Floors[0];
        floor.Units.Clear(); floor.UnitReferences.Clear(); snapshot.ClaimOwners.Clear();
        var book = new BookModel { BookId = new LorId { id = 200 }, instanceId = 3 };
        var appearance = new BookModel { BookId = new LorId { id = 201 }, CharacterSkin = "Malkuth_1" };
        book.Passives.Add(Passive(10)); book.Passives.Add(Passive(11)); book.Passives.Add(Passive(10));
        var custom = new UnitCustomizingData
        {
            UseCustomData = true, height = 185, frontHairID = 1, backHairID = 2, eyeID = 3,
            browID = 4, mouthID = 5, headID = 6,
            hairColor = new Color(0x12 / 255f, 0x34 / 255f, 0x56 / 255f, 0x78 / 255f)
        };
        var unit = new UnitDataModel
        {
            name = "Host name", bookItem = book, defaultBook = book, AppearanceBook = appearance,
            MaxHp = 177, Break = 83, customizeData = custom, gender = Gender.M,
            appearanceType = Gender.N, isSephirah = true
        };
        floor.Units.Add(unit.name); floor.UnitReferences.Add(unit); snapshot.ClaimOwners.Add(123);
        InventoryModel.Instance.Cards.Clear();
        DeckMirror.Capture(snapshot);
        var entry = snapshot.UnitDecks[0]; var display = entry.Display; var token = entry.UnitIdentity;
        Check(token != 0 && display.Available && display.MaxHp == unit.MaxHp && display.Break == unit.Break &&
            display.PassiveIds.SequenceEqual(new[] { 10, 11 }), "captures actual totals and deduplicated effective IDs");
        Check(display.AppearanceAvailable && display.DefaultBookId == 200 && display.CustomBookId == 201 &&
            display.CharacterSkin == "Malkuth_1" && display.SpecialCustomId == -1 && display.HairColor == 0x12345678,
            "captures effective custom page, actual skin, None and RGBA");
        Check(ReferenceEquals(unit.bookItem, book) && ReferenceEquals(unit.customizeData, custom) &&
            book.Passives.Count == 3 && custom.height == 185, "capture does not mutate host models");
        Check(ProgressSnapshot.TryDecode(snapshot.Encode(room), room, out _), "capture is valid wire5 metadata");
        DeckMirror.Capture(snapshot);
        Check(snapshot.UnitDecks[0].UnitIdentity == token, "same host unit keeps its token");
        var replacement = new UnitDataModel { name = unit.name, bookItem = book, defaultBook = book };
        floor.UnitReferences[0] = replacement;
        DeckMirror.Capture(snapshot);
        Check(snapshot.UnitDecks[0].UnitIdentity != token, "replacement unit with identical name/page gets a new token");
        floor.UnitReferences[0] = unit; unit.workshopSkin = "Workshop";
        DeckMirror.Capture(snapshot);
        Check(!snapshot.UnitDecks[0].Display.AppearanceAvailable && snapshot.UnitDecks[0].Display.Available,
            "workshop appearance disables only unsupported presentation");
        unit.workshopSkin = null; custom.hairColor = new Color(float.NaN, 0, 0);
        DeckMirror.Capture(snapshot);
        Check(!snapshot.UnitDecks[0].Display.AppearanceAvailable, "invalid color cannot enter presentation data");
        custom.hairColor = new Color(0, 0, 0); book.Passives.Add(Passive(12, "Workshop"));
        DeckMirror.Capture(snapshot);
        Check(!snapshot.UnitDecks[0].Display.Available, "unsupported effective passive prevents invented stat/passive display");
        book.Passives.RemoveAt(book.Passives.Count - 1); unit.MaxHp = 1000001;
        DeckMirror.Capture(snapshot);
        Check(!snapshot.UnitDecks[0].Display.Available, "unsupported stat bounds disable display");
        unit.MaxHp = 177; custom.specialCustomID = new LorId { id = 1, packageId = "Workshop" };
        DeckMirror.Capture(snapshot);
        Check(!snapshot.UnitDecks[0].Display.AppearanceAvailable, "workshop special appearance cannot masquerade as vanilla");
    }

    private static BookPassiveInfo Passive(int id, string package = null) => new BookPassiveInfo
    { passive = new PassiveXmlInfo { id = new LorId { id = id, packageId = package } } };

    private static void BadEncode(ulong room, Action<ProgressSnapshot> mutate, string message)
    {
        var sample = Sample(); mutate(sample);
        try { sample.Encode(room); }
        catch (InvalidOperationException) { Check(true, message); return; }
        Check(false, message);
    }

    private static void Reject(byte[] bytes, ulong room, string message) =>
        Check(!ProgressSnapshot.TryDecode(bytes, room, out var rejected) && rejected == null, message);
    private static byte[] Change(byte[] bytes, int offset, byte[] value)
    {
        var copy = (byte[])bytes.Clone(); Array.Copy(value, 0, copy, offset, value.Length); return copy;
    }

    private sealed class Offsets
    {
        internal int Count, Available, Hp, Break, PassiveCount, Passives, Appearance, DefaultBook, CustomBook,
            Flags, Gender, AppearanceType, SkinLength, Skin, Special, Part, Height;
        internal readonly List<int> Unit = new List<int>();
    }
    private static Offsets Locate(byte[] bytes)
    {
        var o = new Offsets();
        using (var r = new BinaryReader(new MemoryStream(bytes)))
        {
            r.BaseStream.Position = 29;
            var stages = r.ReadUInt16();
            for (var i = 0; i < stages; i++) { r.ReadBytes(9); r.ReadBytes(r.ReadUInt16()); }
            var floors = r.ReadByte();
            for (var i = 0; i < floors; i++)
            { r.ReadBytes(5); var units = r.ReadByte(); for (var j = 0; j < units; j++) r.ReadBytes(r.ReadUInt16()); }
            r.ReadBytes(5); r.ReadBytes(8 * r.ReadByte()); r.ReadBytes(4);
            r.ReadByte(); var decks = r.ReadByte();
            for (var i = 0; i < decks; i++) { r.ReadBytes(10); r.ReadBytes(4 * r.ReadByte()); }
            r.ReadBytes(8 * r.ReadUInt16());
            o.Count = (int)r.BaseStream.Position; var displayCount = r.ReadByte();
            for (var i = 0; i < displayCount; i++)
            {
                o.Unit.Add((int)r.BaseStream.Position); r.ReadUInt64();
                var available = (int)r.BaseStream.Position; var yes = r.ReadByte();
                if (yes == 1)
                {
                    var hp = (int)r.BaseStream.Position; r.ReadInt32();
                    var bp = (int)r.BaseStream.Position; r.ReadInt32();
                    var pc = (int)r.BaseStream.Position; var count = r.ReadByte();
                    var p = (int)r.BaseStream.Position; r.ReadBytes(count * 4);
                    if (i == 0) { o.Available = available; o.Hp = hp; o.Break = bp; o.PassiveCount = pc; o.Passives = p; }
                }
                var appearance = (int)r.BaseStream.Position; yes = r.ReadByte();
                if (yes != 1) continue;
                var def = (int)r.BaseStream.Position; r.ReadInt32();
                var custom = (int)r.BaseStream.Position; r.ReadInt32();
                var flags = (int)r.BaseStream.Position; r.ReadByte();
                var gender = (int)r.BaseStream.Position; r.ReadByte();
                var type = (int)r.BaseStream.Position; r.ReadByte();
                var len = (int)r.BaseStream.Position; var skinLen = r.ReadUInt16();
                var skin = (int)r.BaseStream.Position; r.ReadBytes(skinLen);
                var special = (int)r.BaseStream.Position; r.ReadInt32();
                var part = (int)r.BaseStream.Position; r.ReadBytes(36);
                var height = (int)r.BaseStream.Position; r.ReadInt32();
                if (i == 0)
                {
                    o.Appearance = appearance; o.DefaultBook = def; o.CustomBook = custom; o.Flags = flags;
                    o.Gender = gender; o.AppearanceType = type; o.SkinLength = len; o.Skin = skin;
                    o.Special = special; o.Part = part; o.Height = height;
                }
            }
        }
        return o;
    }
}
