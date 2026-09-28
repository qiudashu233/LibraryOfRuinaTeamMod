using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Steamworks;
using Steamworks.Data;
using UnityEngine;

namespace RuinaCoop
{
    internal sealed class RelaySession
    {
        private const int VirtualPort = 17617;
        private const int MaxMessageBytes = 65536;
        private const float SnapshotIntervalSeconds = 2f;

        private readonly Lobby _room;
        private readonly SteamId _hostId;
        private readonly bool _isHost;
        private readonly Action<Action> _onMainThread;
        private readonly Action<ProgressSnapshot> _onSnapshot;
        private readonly Dictionary<ulong, Connection> _guests = new Dictionary<ulong, Connection>();
        private readonly Dictionary<ulong, PendingGuest> _pendingGuests = new Dictionary<ulong, PendingGuest>();
        private sealed class PendingGuest
        {
            internal Connection Connection;
            internal float Deadline;
        }
        private HostRelaySocket _hostSocket;
        private GuestRelayConnection _guestConnection;
        private byte[] _latestPacket;
        private byte[] _latestContent;
        private float _nextCaptureTime;
        private uint _sequence;
        private bool _stopped;
        private int _selectedStageId;

        internal string Status { get; private set; }
        internal ProgressSnapshot LatestSnapshot { get; private set; }

        private RelaySession(Lobby room, SteamId hostId, bool isHost,
            Action<Action> onMainThread, Action<ProgressSnapshot> onSnapshot)
        {
            _room = room;
            _hostId = hostId;
            _isHost = isHost;
            _onMainThread = onMainThread;
            _onSnapshot = onSnapshot;
            Status = "Relay starting...";
        }

        internal static RelaySession StartHost(Lobby room, Action<Action> onMainThread,
            Action<ProgressSnapshot> onSnapshot)
        {
            SteamNetworkingUtils.InitRelayNetworkAccess();
            var session = new RelaySession(room, SteamClient.SteamId, true, onMainThread, onSnapshot);
            session._hostSocket = SteamNetworkingSockets.CreateRelaySocket<HostRelaySocket>(VirtualPort);
            session._hostSocket.Owner = session;
            session.Status = "Relay listening; load a library save to share progress.";
            Debug.Log("[RuinaCoop] Relay listener ready on virtual port " + VirtualPort +
                "; Steam relay status: " + SteamNetworkingUtils.Status + ".");
            return session;
        }

        internal static RelaySession StartGuest(Lobby room, SteamId hostId,
            Action<Action> onMainThread, Action<ProgressSnapshot> onSnapshot)
        {
            SteamNetworkingUtils.InitRelayNetworkAccess();
            var session = new RelaySession(room, hostId, false, onMainThread, onSnapshot);
            session._guestConnection = SteamNetworkingSockets.ConnectRelay<GuestRelayConnection>(hostId, VirtualPort);
            session._guestConnection.Owner = session;
            session.Status = "Connecting to host progress relay...";
            Debug.Log("[RuinaCoop] Connecting to host relay " + hostId +
                ", connection " + session._guestConnection.Connection.Id +
                "; Steam relay status: " + SteamNetworkingUtils.Status + ".");
            return session;
        }

        internal void Tick()
        {
            if (_stopped || !SteamClient.IsValid)
            {
                return;
            }

            try
            {
                if (_isHost)
                {
                    _hostSocket.Receive(32);
                    AuthorizePendingGuests();
                    if (Time.realtimeSinceStartup >= _nextCaptureTime)
                    {
                        _nextCaptureTime = Time.realtimeSinceStartup + SnapshotIntervalSeconds;
                        CaptureAndBroadcast();
                    }
                }
                else
                {
                    _guestConnection.Receive(32);
                }
            }
            catch (Exception exception)
            {
                Status = "Relay error: " + exception.Message;
                Debug.LogError("[RuinaCoop] Relay tick failed: " + exception);
            }
        }

        internal bool SelectStage(int stageId)
        {
            if (!_isHost || LatestSnapshot == null ||
                !LatestSnapshot.Stages.Exists(stage => stage.Id == stageId))
            {
                return false;
            }
            _selectedStageId = stageId;
            CaptureAndBroadcast();
            return true;
        }

        internal void Stop()
        {
            if (_stopped)
            {
                return;
            }
            _stopped = true;
            if (!SteamClient.IsValid)
            {
                return;
            }
            try
            {
                if (_guestConnection != null)
                {
                    _guestConnection.Close();
                }
                foreach (var pending in _pendingGuests.Values)
                {
                    pending.Connection.Close(false, 2001, "Room membership not verified");
                }
                _pendingGuests.Clear();
                foreach (var connection in _guests.Values)
                {
                    connection.Close(false, 0, "Room closed");
                }
                _guests.Clear();
                if (_hostSocket != null)
                {
                    _hostSocket.Close();
                }
            }
            catch (Exception exception)
            {
                Debug.LogError("[RuinaCoop] Relay shutdown failed: " + exception);
            }
        }

        private void CaptureAndBroadcast()
        {
            try
            {
                var snapshot = ProgressSnapshot.Capture(_selectedStageId);
                _selectedStageId = snapshot.SelectedStageId;
                var content = snapshot.Encode(_room.Id.Value);
                if (SameBytes(content, _latestContent))
                {
                    return;
                }

                _latestContent = content;
                snapshot.Sequence = ++_sequence;
                _latestPacket = snapshot.Encode(_room.Id.Value);
                LatestSnapshot = snapshot;
                _onSnapshot(snapshot);
                foreach (var connection in _guests.Values)
                {
                    SendSnapshot(connection);
                }
                Status = "Sharing host progress with " + _guests.Count + " guest(s).";
                Debug.Log("[RuinaCoop] Progress snapshot " + snapshot.Sequence + ": " +
                    snapshot.Stages.Count + " stages, " + snapshot.Floors.Count + " floors.");
            }
            catch (Exception exception)
            {
                var nextStatus = "Progress waiting: " + exception.Message;
                if (Status != nextStatus)
                {
                    Debug.LogWarning("[RuinaCoop] " + nextStatus);
                }
                Status = nextStatus;
            }
        }

        private void SendSnapshot(Connection connection)
        {
            if (_latestPacket == null)
            {
                return;
            }
            var result = connection.SendMessage(_latestPacket, SendType.Reliable);
            if (result != Steamworks.Result.OK)
            {
                Debug.LogWarning("[RuinaCoop] Snapshot send failed: " + result + ".");
            }
        }

        private bool IsLobbyMember(SteamId guestId)
        {
            foreach (var member in _room.Members)
            {
                if (member.Id == guestId)
                {
                    return true;
                }
            }
            return false;
        }

        private void AuthorizePendingGuests()
        {
            if (_pendingGuests.Count == 0)
            {
                return;
            }

            var resolved = new List<ulong>();
            foreach (var pair in _pendingGuests)
            {
                if (IsLobbyMember((SteamId)pair.Key) && _guests.Count < ProtocolInfo.MaxPlayers - 1)
                {
                    _guests[pair.Key] = pair.Value.Connection;
                    SendSnapshot(pair.Value.Connection);
                    Status = "Sharing host progress with " + _guests.Count + " guest(s).";
                    Debug.Log("[RuinaCoop] Relay member authorized: " + pair.Key + ".");
                    resolved.Add(pair.Key);
                }
                else if (Time.realtimeSinceStartup >= pair.Value.Deadline)
                {
                    pair.Value.Connection.Close(false, 2001, "Not in the Steam room");
                    Debug.LogWarning("[RuinaCoop] Relay member rejected after waiting: " + pair.Key +
                        "; lobby members " + _room.MemberCount + ".");
                    resolved.Add(pair.Key);
                }
            }
            foreach (var id in resolved)
            {
                _pendingGuests.Remove(id);
            }
        }

        private void GuestConnected(Connection connection, ConnectionInfo info)
        {
            _onMainThread(() =>
            {
                if (_stopped || !info.Identity.IsSteamId || info.Identity.SteamId.Value == 0 ||
                    info.Identity.SteamId == _hostId)
                {
                    Debug.LogWarning("[RuinaCoop] Relay peer rejected: invalid identity " +
                        info.Identity + ".");
                    connection.Close(false, 2001, "Invalid room identity");
                    return;
                }
                var guestId = info.Identity.SteamId.Value;
                _pendingGuests[guestId] = new PendingGuest
                {
                    Connection = connection,
                    Deadline = Time.realtimeSinceStartup + 5f
                };
                Status = "Verifying " + _pendingGuests.Count + " relay guest(s) against room members...";
                Debug.Log("[RuinaCoop] Relay peer connected pending room check: " + guestId +
                    "; lobby members " + _room.MemberCount + ".");
            });
        }

        private void GuestDisconnected(ConnectionInfo info)
        {
            _onMainThread(() =>
            {
                if (_stopped || !info.Identity.IsSteamId)
                {
                    return;
                }
                var guestId = info.Identity.SteamId.Value;
                _pendingGuests.Remove(guestId);
                _guests.Remove(guestId);
                Status = "Relay connected to " + _guests.Count + " guest(s).";
                Debug.Log("[RuinaCoop] Guest relay disconnected: " + guestId +
                    ", reason " + info.EndReason + ".");
            });
        }

        private void HostConnected(ConnectionInfo info)
        {
            _onMainThread(() =>
            {
                if (_stopped)
                {
                    return;
                }
                Debug.Log("[RuinaCoop] Host relay connected callback: identity " +
                    info.Identity + ", expected " + _hostId + ".");
                if (info.Identity.IsSteamId && info.Identity.SteamId != _hostId)
                {
                    _guestConnection.Close();
                    Status = "Relay peer was not the room host.";
                    return;
                }
                Status = "Connected to host; waiting for progress.";
            });
        }

        private void HostDisconnected(ConnectionInfo info)
        {
            _onMainThread(() =>
            {
                if (!_stopped)
                {
                    Status = "Host progress relay disconnected: " + info.EndReason;
                    Debug.LogWarning("[RuinaCoop] Host relay disconnected: " + info.EndReason + ".");
                }
            });
        }

        private void ReceiveHostMessage(byte[] bytes)
        {
            _onMainThread(() =>
            {
                if (_stopped)
                {
                    return;
                }
                ProgressSnapshot snapshot;
                if (!ProgressSnapshot.TryDecode(bytes, _room.Id.Value, out snapshot))
                {
                    Status = "Rejected an invalid host progress packet.";
                    Debug.LogWarning("[RuinaCoop] Invalid progress packet rejected.");
                    return;
                }
                if (LatestSnapshot != null && snapshot.Sequence <= LatestSnapshot.Sequence)
                {
                    return;
                }
                LatestSnapshot = snapshot;
                _onSnapshot(snapshot);
                Status = "Host progress received: " + snapshot.Stages.Count + " stages, " +
                    snapshot.Floors.Count + " floors.";
                Debug.Log("[RuinaCoop] Received host progress snapshot " + snapshot.Sequence + ".");
            });
        }

        private static bool SameBytes(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }
            for (var i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }
            return true;
        }

        internal static byte[] CopyMessage(IntPtr pointer, int length)
        {
            if (length <= 0 || length > MaxMessageBytes)
            {
                return null;
            }
            var bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            return bytes;
        }

        public sealed class HostRelaySocket : SocketManager
        {
            internal RelaySession Owner;

            public override void OnConnecting(Connection connection, ConnectionInfo info)
            {
                Debug.Log("[RuinaCoop] Incoming relay connection " + connection.Id +
                    ": identity " + info.Identity + ".");
                if (Owner == null || Owner._stopped || Owner._guests.Count + Owner._pendingGuests.Count >= 8 ||
                    info.Identity.IsSteamId && (info.Identity.SteamId.Value == 0 ||
                    info.Identity.SteamId == Owner._hostId))
                {
                    Debug.LogWarning("[RuinaCoop] Incoming relay connection rejected before accept.");
                    connection.Close(false, 2001, "Invalid or excessive relay peer");
                    return;
                }
                base.OnConnecting(connection, info);
            }

            public override void OnConnected(Connection connection, ConnectionInfo info)
            {
                Debug.Log("[RuinaCoop] Host socket connected " + connection.Id +
                    ": identity " + info.Identity + ".");
                base.OnConnected(connection, info);
                if (Owner != null)
                {
                    Owner.GuestConnected(connection, info);
                }
            }

            public override void OnDisconnected(Connection connection, ConnectionInfo info)
            {
                Debug.LogWarning("[RuinaCoop] Host socket disconnected " + connection.Id +
                    ": identity " + info.Identity + ", reason " + info.EndReason + ".");
                base.OnDisconnected(connection, info);
                if (Owner != null)
                {
                    Owner.GuestDisconnected(info);
                }
            }

            public override void OnMessage(Connection connection, NetIdentity identity, IntPtr data,
                int size, long messageNum, long recvTime, int channel)
            {
                // Stage 2 transport is intentionally host-to-guest only.
                if (size > 0)
                {
                    Debug.LogWarning("[RuinaCoop] Unexpected guest relay message ignored.");
                }
            }
        }

        public sealed class GuestRelayConnection : ConnectionManager
        {
            internal RelaySession Owner;

            public override void OnConnected(ConnectionInfo info)
            {
                Debug.Log("[RuinaCoop] Guest socket connected: identity " + info.Identity + ".");
                base.OnConnected(info);
                if (Owner != null)
                {
                    Owner.HostConnected(info);
                }
            }

            public override void OnDisconnected(ConnectionInfo info)
            {
                Debug.LogWarning("[RuinaCoop] Guest socket disconnected: identity " +
                    info.Identity + ", reason " + info.EndReason + ".");
                base.OnDisconnected(info);
                if (Owner != null)
                {
                    Owner.HostDisconnected(info);
                }
            }

            public override void OnMessage(IntPtr data, int size, long messageNum, long recvTime, int channel)
            {
                if (Owner == null)
                {
                    return;
                }
                var bytes = CopyMessage(data, size);
                if (bytes != null)
                {
                    Owner.ReceiveHostMessage(bytes);
                }
            }
        }
    }
}