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
        private const byte WireVersion = 1;
        private const int MaxPacketBytes = 65536;
        private const int MaxStages = 512;
        private const int MaxFloors = 12;
        private const int MaxUnitsPerFloor = 5;
        private const int MaxNameBytes = 256;

        internal uint Sequence;
        internal int Chapter;
        internal int LibraryLevel;
        internal int SelectedStageId;
        internal readonly List<StageEntry> Stages = new List<StageEntry>();
        internal readonly List<FloorEntry> Floors = new List<FloorEntry>();

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
            if (packet == null || packet.Length > MaxPacketBytes || packet.Length < 30)
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
                        if (entry.State == StoryState.Close)
                        {
                            return Reject(out reason, "Stage " + i + " is closed.");
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
                    if (stream.Position != stream.Length)
                    {
                        return Reject(out reason, "Packet has trailing bytes.");
                    }
                    if (result.SelectedStageId != 0 && !stageIds.Contains(result.SelectedStageId))
                    {
                        return Reject(out reason, "Selected stage ID is not in the stage list: " +
                            result.SelectedStageId + ".");
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
            return Encoding.UTF8.GetString(reader.ReadBytes(byteLength));
        }
    }
}
