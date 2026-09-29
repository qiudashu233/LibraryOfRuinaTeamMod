using System;
using System.Linq;
using System.Reflection;
using Steamworks;
using Steamworks.Data;
using UI;
using UnityEngine;

namespace RuinaCoop
{
    // Diagnostic only: a socket pair does not join a Steam lobby or exercise relay authentication.
    internal sealed class LocalSelfTest
    {
        private const float TimeoutSeconds = 10f;
        private readonly Connection[] _first = new Connection[1];
        private readonly Connection[] _second = new Connection[1];
        private LocalReceiver _firstReceiver;
        private LocalReceiver _secondReceiver;
        private ProgressSnapshot _expected;
        private byte[] _packet;
        private ulong _roomId;
        private float _deadline;
        private bool _networkLoopback;
        private bool _waitingForReply;

        internal string Status { get; private set; } = "Not run.";
        internal bool Running { get; private set; }

        internal void Start(ProgressSnapshot liveSnapshot, ulong roomId)
        {
            Stop();
            try
            {
                if (!SteamClient.IsValid || liveSnapshot == null || roomId == 0)
                {
                    throw new InvalidOperationException("Load the host library save and create a room first.");
                }
                VerifyCodec(BuildSample(), roomId, "Unicode sample");
                VerifyCodec(liveSnapshot, roomId, "host progress");
                _expected = liveSnapshot;
                _roomId = roomId;
                _packet = liveSnapshot.Encode(roomId);
                Running = true;
                BeginPair(false);
                Debug.Log("[RuinaCoop] Local self-test: snapshot codec passed; starting socket pairs.");
            }
            catch (Exception exception)
            {
                Fail(exception.Message);
            }
        }

        internal void Tick()
        {
            if (!Running)
            {
                return;
            }
            try
            {
                if (!SteamClient.IsValid)
                {
                    throw new InvalidOperationException("Steam became unavailable.");
                }
                if (Time.realtimeSinceStartup > _deadline)
                {
                    throw new TimeoutException((_networkLoopback ? "127.0.0.1" : "memory") +
                        " socket pair did not receive the snapshot within 10 seconds.");
                }
                _firstReceiver.Receive(8);
                _secondReceiver.Receive(8);
                if (!_waitingForReply && _secondReceiver.Packet != null)
                {
                    VerifyReceived(_secondReceiver.Packet);
                    var result = _second[0].SendMessage(_packet, SendType.Reliable);
                    if (result != Steamworks.Result.OK)
                    {
                        throw new InvalidOperationException("Socket pair reply send failed: " + result);
                    }
                    _waitingForReply = true;
                    Status = (_networkLoopback ? "127.0.0.1" : "Memory") +
                        " pair: host-to-guest received; waiting for reply.";
                }
                if (_waitingForReply && _firstReceiver.Packet != null)
                {
                    VerifyReceived(_firstReceiver.Packet);
                    EndPair();
                    if (_networkLoopback)
                    {
                        Running = false;
                        Status = "PASS: codec + memory pair + 127.0.0.1 pair, both directions.";
                        Debug.Log("[RuinaCoop] Local self-test PASS: " + Status +
                            " Snapshot bytes: " + _packet.Length + ".");
                    }
                    else
                    {
                        BeginPair(true);
                    }
                }
            }
            catch (Exception exception)
            {
                Fail(exception.Message);
            }
        }

        internal void Stop()
        {
            Running = false;
            EndPair();
        }

        private void BeginPair(bool networkLoopback)
        {
            _networkLoopback = networkLoopback;
            _waitingForReply = false;
            _firstReceiver = new LocalReceiver();
            _secondReceiver = new LocalReceiver();
            var property = typeof(SteamNetworkingSockets).GetProperty("Internal",
                BindingFlags.Static | BindingFlags.NonPublic);
            if (property == null)
            {
                throw new MissingMemberException("SteamNetworkingSockets.Internal is unavailable.");
            }
            var sockets = property.GetValue(null, null);
            var method = sockets.GetType().GetMethod("CreateSocketPair",
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null)
            {
                throw new MissingMemberException("SteamNetworkingSockets.CreateSocketPair is unavailable.");
            }
            var arguments = new object[]
            {
                _first, _second, networkLoopback, default(NetIdentity), default(NetIdentity)
            };
            bool created;
            try
            {
                created = (bool)method.Invoke(sockets, arguments);
            }
            catch (TargetInvocationException exception)
            {
                throw new InvalidOperationException("CreateSocketPair failed: " +
                    (exception.InnerException == null ? exception.Message : exception.InnerException.Message));
            }
            if (!created || _first[0].Id == 0 || _second[0].Id == 0)
            {
                throw new InvalidOperationException("CreateSocketPair returned no connected pair.");
            }
            _firstReceiver.Connection = _first[0];
            _secondReceiver.Connection = _second[0];
            _deadline = Time.realtimeSinceStartup + TimeoutSeconds;
            var sendResult = _first[0].SendMessage(_packet, SendType.Reliable);
            if (sendResult != Steamworks.Result.OK)
            {
                throw new InvalidOperationException("Socket pair send failed: " + sendResult);
            }
            Status = (networkLoopback ? "127.0.0.1" : "Memory") +
                " pair: sent " + _packet.Length + " bytes; waiting for receive.";
            Debug.Log("[RuinaCoop] Local self-test " + Status);
        }

        private void EndPair()
        {
            Close(_first[0]);
            Close(_second[0]);
            _first[0] = default(Connection);
            _second[0] = default(Connection);
            _firstReceiver = null;
            _secondReceiver = null;
        }

        private static void Close(Connection connection)
        {
            if (connection.Id == 0 || !SteamClient.IsValid)
            {
                return;
            }
            try
            {
                connection.Close(false, 0, "Local self-test complete");
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[RuinaCoop] Local self-test socket cleanup: " + exception.Message);
            }
        }

        private void VerifyReceived(byte[] received)
        {
            ProgressSnapshot decoded;
            if (!_packet.SequenceEqual(received) ||
                !ProgressSnapshot.TryDecode(received, _roomId, out decoded) ||
                !SameSnapshot(_expected, decoded))
            {
                throw new InvalidOperationException("Socket pair changed the snapshot payload.");
            }
        }

        private void Fail(string reason)
        {
            Stop();
            Status = "FAIL: " + reason;
            Debug.LogError("[RuinaCoop] Local self-test " + Status);
        }

        private static void VerifyCodec(ProgressSnapshot value, ulong roomId, string name)
        {
            var bytes = value.Encode(roomId);
            ProgressSnapshot decoded;
            if (!ProgressSnapshot.TryDecode(bytes, roomId, out decoded) || !SameSnapshot(value, decoded))
            {
                throw new InvalidOperationException(name + " snapshot round-trip failed.");
            }
            if (ProgressSnapshot.TryDecode(bytes, roomId + 1, out decoded))
            {
                throw new InvalidOperationException(name + " accepted another room ID.");
            }
            var damaged = (byte[])bytes.Clone();
            damaged[0] ^= 0xff;
            if (ProgressSnapshot.TryDecode(damaged, roomId, out decoded))
            {
                throw new InvalidOperationException(name + " accepted bad magic.");
            }
            damaged = (byte[])bytes.Clone();
            damaged[4] ^= 0xff;
            if (ProgressSnapshot.TryDecode(damaged, roomId, out decoded))
            {
                throw new InvalidOperationException(name + " accepted bad wire version.");
            }
            if (ProgressSnapshot.TryDecode(bytes.Take(bytes.Length - 1).ToArray(), roomId, out decoded) ||
                ProgressSnapshot.TryDecode(bytes.Concat(new byte[] { 0 }).ToArray(), roomId, out decoded) ||
                ProgressSnapshot.TryDecode(new byte[65537], roomId, out decoded))
            {
                throw new InvalidOperationException(name + " accepted an invalid packet length.");
            }
        }

        private static ProgressSnapshot BuildSample()
        {
            var sample = new ProgressSnapshot
            {
                Sequence = 17,
                Chapter = 5,
                LibraryLevel = 42,
                SelectedStageId = 101,
                SelectedFloorId = (byte)SephirahType.Malkuth,
                ClaimRevision = 3
            };
            sample.Stages.Add(new ProgressSnapshot.StageEntry
            {
                Id = 101, Chapter = 5, State = StoryState.Clear, Name = "测试 무대 stage"
            });
            sample.Stages.Add(new ProgressSnapshot.StageEntry
            {
                Id = 102, Chapter = 5, State = StoryState.Open, Name = "第二关"
            });
            var floor = new ProgressSnapshot.FloorEntry { Sephirah = SephirahType.Malkuth, Level = 6 };
            floor.Units.Add("馆员一");
            floor.Units.Add("Roland 롤랑");
            sample.Floors.Add(floor);
            sample.ClaimOwners.Add(0);
            sample.ClaimOwners.Add(76561199548728145UL);
            return sample;
        }

        private static bool SameSnapshot(ProgressSnapshot left, ProgressSnapshot right)
        {
            if (left.Sequence != right.Sequence || left.Chapter != right.Chapter ||
                left.LibraryLevel != right.LibraryLevel ||
                left.SelectedStageId != right.SelectedStageId ||
                left.SelectedFloorId != right.SelectedFloorId ||
                left.ClaimRevision != right.ClaimRevision ||
                !left.ClaimOwners.SequenceEqual(right.ClaimOwners) ||
                left.Stages.Count != right.Stages.Count || left.Floors.Count != right.Floors.Count)
            {
                return false;
            }
            for (var i = 0; i < left.Stages.Count; i++)
            {
                var a = left.Stages[i];
                var b = right.Stages[i];
                if (a.Id != b.Id || a.Chapter != b.Chapter || a.State != b.State || a.Name != b.Name)
                {
                    return false;
                }
            }
            for (var i = 0; i < left.Floors.Count; i++)
            {
                var a = left.Floors[i];
                var b = right.Floors[i];
                if (a.Sephirah != b.Sephirah || a.Level != b.Level || !a.Units.SequenceEqual(b.Units))
                {
                    return false;
                }
            }
            return true;
        }

        private sealed class LocalReceiver : ConnectionManager
        {
            internal byte[] Packet;

            public override void OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel)
            {
                if (Packet == null)
                {
                    Packet = RelaySession.CopyMessage(data, size);
                }
            }
        }
    }
}
