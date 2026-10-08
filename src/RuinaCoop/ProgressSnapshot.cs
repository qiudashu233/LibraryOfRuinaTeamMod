using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UI;

namespace RuinaCoop
{
    internal sealed class ProgressSnapshot
    {
        private const uint Magic = 0x52435053;
        private const byte WireVersion = 4;
        private const int MaxPacketBytes = 65536;
        private const int MaxStages = 512;
        private const int MaxFloors = 12;
        private const int MaxUnitsPerFloor = 5;
        private const int MaxNameBytes = 256;

        internal uint Sequence;
        internal int Chapter;
        internal int LibraryLevel;
        internal int SelectedStageId;
        internal byte SelectedFloorId = PrepClaims.NoFloor;
        internal uint ClaimRevision;
        internal uint DeckRevision;
        internal bool DecksFrozen;
        internal readonly List<UnitDeckEntry> UnitDecks = new List<UnitDeckEntry>();
        internal readonly List<CardStockEntry> CardStock = new List<CardStockEntry>();
        internal readonly List<ulong> ClaimOwners = new List<ulong>();
        internal readonly List<StageEntry> Stages = new List<StageEntry>();
        internal readonly List<FloorEntry> Floors = new List<FloorEntry>();

        internal sealed class UnitDeckEntry
        {
            // Stable host unit token; zero means no supported editable identity.
            internal ulong UnitIdentity;
            internal readonly UnitDisplayEntry Display = new UnitDisplayEntry();
            // Zero identifies an absent or unsupported key page, which is view only.
            internal int BookId;
            internal int BookInstanceId;
            internal int Capacity;
            internal bool Fixed;
            internal bool MultiDeck;
            internal readonly List<int> Cards = new List<int>();
        }

        // Read-only presentation data for isolated native editor models. No save graph,
        // inventory references, arbitrary resource path, or render texture index crosses the wire.
        internal sealed class UnitDisplayEntry
        {
            internal bool Available;
            internal int MaxHp;
            internal int Break;
            internal readonly List<int> PassiveIds = new List<int>();
            internal bool AppearanceAvailable;
            internal int DefaultBookId;
            internal int CustomBookId;
            internal bool IsSephirah;
            internal byte Gender;
            internal byte AppearanceType;
            internal string CharacterSkin = "";
            internal bool UseCustom;
            internal int SpecialCustomId = -1;
            internal int FrontHair = -1;
            internal int BackHair = -1;
            internal int Eye = -1;
            internal int Brow = -1;
            internal int Mouth = -1;
            internal int Head = -1;
            // RGBA, most significant byte R, least significant byte A.
            internal uint HairColor;
            internal uint EyeColor;
            internal uint SkinColor;
            internal int Height = 170;
        }

        internal sealed class CardStockEntry
        {
            internal int Id;
            internal int Count;
        }

        internal sealed class StageEntry
        {
            internal int Id;
            internal int Chapter;
            internal StoryState State;
            internal string Name;
        }

        internal sealed class FloorEntry
        {
            internal SephirahType Sephirah;
            internal int Level;
            internal readonly List<string> Units = new List<string>();
            internal readonly List<object> UnitReferences = new List<object>();
        }

        internal static ProgressSnapshot Capture(int selectedStageId)
        {
            var library = LibraryModel.Instance;
            var openedFloors = library.GetOpenedFloorList();
            if (openedFloors == null || openedFloors.Count == 0)
            {
                throw new InvalidOperationException("The host must load a library save before sharing progress.");
            }

            var result = new ProgressSnapshot
            {
                Chapter = library.GetChapter(),
                LibraryLevel = library.GetLibraryLevel(),
                SelectedStageId = selectedStageId
            };

            var stageIds = new HashSet<int>();
            foreach (var stage in StageClassInfoList.Instance.GetAllDataList())
            {
                if (stage == null || !stage.id.IsBasic())
                {
                    continue;
                }

                var state = stage.currentState;
                var id = stage.id.id;
                if (state == StoryState.Close || id <= 0 || !stageIds.Add(id))
                {
                    continue;
                }

                if (result.Stages.Count >= MaxStages)
                {
                    throw new InvalidOperationException("The vanilla stage list exceeds the supported snapshot limit.");
                }

                result.Stages.Add(new StageEntry
                {
                    Id = id,
                    Chapter = stage.chapter,
                    State = state,
                    Name = stage.stageName ?? ""
                });
            }
            result.Stages.Sort((left, right) => left.Id.CompareTo(right.Id));

            var floorIds = new HashSet<byte>();
            foreach (var floor in openedFloors)
            {
                if (floor == null || !floorIds.Add((byte)floor.Sephirah))
                {
                    continue;
                }
                if (result.Floors.Count >= MaxFloors)
                {
                    throw new InvalidOperationException("The opened floor list exceeds the supported snapshot limit.");
                }

                var floorEntry = new FloorEntry { Sephirah = floor.Sephirah, Level = floor.Level };
                var units = floor.GetUnitDataList();
                if (units.Count > MaxUnitsPerFloor)
                {
                    throw new InvalidOperationException("An opened floor has more than five units.");
                }
                foreach (var unit in units)
                {
                    floorEntry.Units.Add(unit == null ? "" : unit.name ?? "");
                    floorEntry.UnitReferences.Add(unit);
                }
                result.Floors.Add(floorEntry);
            }
            result.Floors.Sort((left, right) => left.Sephirah.CompareTo(right.Sephirah));

            if (selectedStageId != 0 && !result.Stages.Any(stage => stage.Id == selectedStageId))
            {
                result.SelectedStageId = 0;
            }
            return result;
        }

        internal byte[] Encode(ulong roomId)
        {
            if (Stages.Count > MaxStages || Floors.Count > MaxFloors ||
                ClaimOwners.Count > MaxUnitsPerFloor ||
                Floors.Any(floor => floor == null || floor.Units.Count > MaxUnitsPerFloor))
            {
                throw new InvalidOperationException("Progress snapshot entry count exceeds the limit.");
            }
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream, Encoding.UTF8))
            {
                writer.Write(Magic);
                writer.Write(WireVersion);
                writer.Write(roomId);
                writer.Write(Sequence);
                writer.Write(Chapter);
                writer.Write(LibraryLevel);
                writer.Write(SelectedStageId);
                writer.Write((ushort)Stages.Count);
                foreach (var stage in Stages)
                {
                    writer.Write(stage.Id);
                    writer.Write(stage.Chapter);
                    writer.Write((byte)stage.State);
                    WriteName(writer, stage.Name);
                }
                writer.Write((byte)Floors.Count);
                foreach (var floor in Floors)
                {
                    writer.Write((byte)floor.Sephirah);
                    writer.Write(floor.Level);
                    writer.Write((byte)floor.Units.Count);
                    foreach (var unitName in floor.Units)
                    {
                        WriteName(writer, unitName);
                    }
                }
                writer.Write(SelectedFloorId);
                writer.Write(ClaimRevision);
                writer.Write((byte)ClaimOwners.Count);
                foreach (var owner in ClaimOwners)
                {
                    writer.Write(owner);
                }
                writer.Write(DeckRevision);
                DeckMirror.WriteData(writer, this, false);
                writer.Flush();
                if (stream.Length > MaxPacketBytes)
                {
                    throw new InvalidOperationException("Progress snapshot exceeds the packet size limit.");
                }
                return stream.ToArray();
            }
        }

        internal static bool TryDecode(byte[] packet, ulong expectedRoomId, out ProgressSnapshot snapshot)
        {
            string reason;
            return TryDecode(packet, expectedRoomId, out snapshot, out reason);
        }

        internal static bool TryDecode(byte[] packet, ulong expectedRoomId,
            out ProgressSnapshot snapshot, out string reason)
        {
            snapshot = null;
            reason = null;
            if (packet == null || packet.Length > MaxPacketBytes || packet.Length < 47)
            {
                return Reject(out reason, "Packet size is invalid.");
            }

            try
            {
                using (var stream = new MemoryStream(packet, false))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (reader.ReadUInt32() != Magic)
                    {
                        return Reject(out reason, "Packet magic does not match.");
                    }
                    if (reader.ReadByte() != WireVersion)
                    {
                        return Reject(out reason, "Snapshot wire version does not match.");
                    }
                    if (reader.ReadUInt64() != expectedRoomId)
                    {
                        return Reject(out reason, "Snapshot room ID does not match.");
                    }

                    var result = new ProgressSnapshot
                    {
                        Sequence = reader.ReadUInt32(),
                        Chapter = reader.ReadInt32(),
                        LibraryLevel = reader.ReadInt32(),
                        SelectedStageId = reader.ReadInt32()
                    };
                    var stageCount = reader.ReadUInt16();
                    if (stageCount > MaxStages)
                    {
                        return Reject(out reason, "Stage count exceeds the limit: " + stageCount + ".");
                    }
                    var stageIds = new HashSet<int>();
                    for (var i = 0; i < stageCount; i++)
                    {
                        var entry = new StageEntry
                        {
                            Id = reader.ReadInt32(),
                            Chapter = reader.ReadInt32(),
                            State = (StoryState)reader.ReadByte(),
                            Name = ReadName(reader)
                        };
                        if (entry.Id <= 0)
                        {
                            return Reject(out reason, "Stage " + i + " has a nonpositive ID: " + entry.Id + ".");
                        }
                        if (!Enum.IsDefined(typeof(StoryState), entry.State) || entry.State == StoryState.Close)
                        {
                            return Reject(out reason, "Stage " + i + " has a closed or invalid state.");
                        }
                        if (!stageIds.Add(entry.Id))
                        {
                            return Reject(out reason, "Stage " + i + " repeats ID " + entry.Id + ".");
                        }
                        result.Stages.Add(entry);
                    }
                    var floorCount = reader.ReadByte();
                    if (floorCount > MaxFloors)
                    {
                        return Reject(out reason, "Floor count exceeds the limit: " + floorCount + ".");
                    }
                    var floorIds = new HashSet<SephirahType>();
                    for (var i = 0; i < floorCount; i++)
                    {
                        var entry = new FloorEntry
                        {
                            Sephirah = (SephirahType)reader.ReadByte(),
                            Level = reader.ReadInt32()
                        };
                        if (!Enum.IsDefined(typeof(SephirahType), entry.Sephirah) ||
                            entry.Sephirah == SephirahType.None || entry.Sephirah == SephirahType.ETC)
                        {
                            return Reject(out reason, "Floor " + i + " has an invalid ID.");
                        }
                        if (!floorIds.Add(entry.Sephirah))
                        {
                            return Reject(out reason, "Floor " + i + " repeats ID " + entry.Sephirah + ".");
                        }
                        var unitCount = reader.ReadByte();
                        if (unitCount > MaxUnitsPerFloor)
                        {
                            return Reject(out reason, "Floor " + i + " has too many librarians: " + unitCount + ".");
                        }
                        for (var unit = 0; unit < unitCount; unit++)
                        {
                            entry.Units.Add(ReadName(reader));
                        }
                        result.Floors.Add(entry);
                    }
                    result.SelectedFloorId = reader.ReadByte();
                    result.ClaimRevision = reader.ReadUInt32();
                    var ownerCount = reader.ReadByte();
                    if (ownerCount > MaxUnitsPerFloor)
                    {
                        return Reject(out reason, "Claim owner count exceeds the limit: " + ownerCount + ".");
                    }
                    for (var i = 0; i < ownerCount; i++)
                    {
                        result.ClaimOwners.Add(reader.ReadUInt64());
                    }
                    result.DeckRevision = reader.ReadUInt32();
                    if (!DeckMirror.TryReadData(reader, result, out reason))
                    {
                        return false;
                    }
                    if (stream.Position != stream.Length)
                    {
                        return Reject(out reason, "Packet has trailing bytes.");
                    }
                    if (result.SelectedStageId != 0 && !stageIds.Contains(result.SelectedStageId))
                    {
                        return Reject(out reason, "Selected stage ID is not in the stage list: " +
                            result.SelectedStageId + ".");
                    }
                    if (result.SelectedFloorId == PrepClaims.NoFloor)
                    {
                        if (ownerCount != 0)
                        {
                            return Reject(out reason, "Claims exist without a selected floor.");
                        }
                    }
                    else
                    {
                        var selectedFloor = result.Floors.Find(floor =>
                            (byte)floor.Sephirah == result.SelectedFloorId);
                        if (result.SelectedStageId == 0 || selectedFloor == null ||
                            ownerCount != selectedFloor.Units.Count)
                        {
                            return Reject(out reason, "Claims do not match the selected stage and floor.");
                        }
                    }
                    snapshot = result;
                    return true;
                }
            }
            catch (Exception exception)
            {
                reason = "Malformed packet: " + exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }

        private static bool Reject(out string reason, string message)
        {
            reason = message;
            return false;
        }

        private static void WriteName(BinaryWriter writer, string name)
        {
            var safeName = name ?? "";
            var bytes = Encoding.UTF8.GetBytes(safeName);
            while (bytes.Length > MaxNameBytes)
            {
                safeName = safeName.Substring(0, safeName.Length - 1);
                if (safeName.Length != 0 && char.IsHighSurrogate(safeName[safeName.Length - 1]))
                {
                    safeName = safeName.Substring(0, safeName.Length - 1);
                }
                bytes = Encoding.UTF8.GetBytes(safeName);
            }
            writer.Write((ushort)bytes.Length);
            writer.Write(bytes);
        }

        private static string ReadName(BinaryReader reader)
        {
            var byteLength = reader.ReadUInt16();
            if (byteLength > MaxNameBytes || byteLength > reader.BaseStream.Length - reader.BaseStream.Position)
            {
                throw new InvalidDataException("Invalid name length in progress snapshot.");
            }
            return new UTF8Encoding(false, true).GetString(reader.ReadBytes(byteLength));
        }
    }
}
