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
        private const int VirtualPort = 0;
        private const int MaxMessageBytes = 65536;
        private const float SnapshotIntervalSeconds = 2f;

        private readonly Lobby _room;
        private readonly SteamId _hostId;
        private readonly bool _isHost;
        private readonly Action<Action> _onMainThread;
        private readonly Action<ProgressSnapshot> _onSnapshot;
        private readonly Dictionary<ulong, Connection> _guests = new Dictionary<ulong, Connection>();
        private readonly Dictionary<uint, PendingGuest> _pendingGuests = new Dictionary<uint, PendingGuest>();
        private sealed class PendingGuest
        {
            internal Connection Connection;
            internal float Deadline;
            internal string Challenge;
        }
        private HostRelaySocket _hostSocket;
        private GuestRelayConnection _guestConnection;
        private byte[] _latestPacket;
        private byte[] _latestContent;
        private float _nextCaptureTime;
        private uint _sequence;
        private bool _stopped;
        private int _selectedStageId;
        private string _guestChallenge;
        private float _nextProofTime;
        private bool _guestAuthenticated;
        private string _lobbyEchoToken;
        private float _lobbyEchoDeadline;

        internal string Status { get; private set; }
        internal string LobbyEchoStatus { get; private set; } = "Not run.";
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
            SteamMatchmaking.OnChatMessage += session.HandleLobbyChat;
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
                    ExpirePendingGuests();
                    RemoveDepartedGuests();
                    if (_lobbyEchoToken != null && Time.realtimeSinceStartup >= _lobbyEchoDeadline)
                    {
                        _lobbyEchoToken = null;
                        LobbyEchoStatus = "FAIL: room message echo timed out.";
                        Debug.LogWarning("[RuinaCoop] " + LobbyEchoStatus);
                    }
                    if (Time.realtimeSinceStartup >= _nextCaptureTime)
                    {
                        _nextCaptureTime = Time.realtimeSinceStartup + SnapshotIntervalSeconds;
                        CaptureAndBroadcast();
                    }
                }
                else
                {
                    _guestConnection.Receive(32);
                    if (_guestChallenge != null && !_guestAuthenticated &&
                        Time.realtimeSinceStartup >= _nextProofTime)
                    {
                        SendLobbyProof();
                    }
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

        internal void StartLobbyEchoTest()
        {
            if (!_isHost || _stopped || !SteamClient.IsValid)
            {
                return;
            }
            _lobbyEchoToken = "RC6T:" + RelayAuth.NewChallenge();
            _lobbyEchoDeadline = Time.realtimeSinceStartup + 10f;
            if (!_room.SendChatString(_lobbyEchoToken))
            {
                _lobbyEchoToken = null;
                LobbyEchoStatus = "FAIL: Steam rejected the room message.";
                return;
            }
            LobbyEchoStatus = "Waiting for Steam room message echo...";
            Debug.Log("[RuinaCoop] Steam room message self-test sent.");
        }

        internal void Stop()
        {
            if (_stopped)
            {
                return;
            }
            _stopped = true;
            if (_isHost)
            {
                SteamMatchmaking.OnChatMessage -= HandleLobbyChat;
            }
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

                snapshot.Sequence = ++_sequence;
                var packet = snapshot.Encode(_room.Id.Value);
                ProgressSnapshot decoded;
                string decodeReason;
                if (!ProgressSnapshot.TryDecode(packet, _room.Id.Value, out decoded, out decodeReason))
                {
                    throw new InvalidOperationException("Host snapshot failed local validation: " + decodeReason);
                }
                _latestContent = content;
                _latestPacket = packet;
                LatestSnapshot = snapshot;
                _onSnapshot(snapshot);
                foreach (var connection in _guests.Values)
                {
                    SendSnapshot(connection);
                }
                Status = "Sharing host progress with " + _guests.Count + " guest(s).";
                Debug.Log("[RuinaCoop] Progress snapshot " + snapshot.Sequence + ": " +
                    snapshot.Stages.Count + " stages, " + snapshot.Floors.Count +
                    " floors, " + packet.Length + " bytes; local decode PASS.");
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

        private void ExpirePendingGuests()
        {
            if (_pendingGuests.Count == 0)
            {
                return;
            }

            var resolved = new List<uint>();
            foreach (var pair in _pendingGuests)
            {
                if (Time.realtimeSinceStartup >= pair.Value.Deadline)
                {
                    pair.Value.Connection.Close(false, 2001, "Lobby proof not received");
                    Debug.LogWarning("[RuinaCoop] Relay challenge timed out for connection " +
                        pair.Key + "; lobby members " + _room.MemberCount + ".");
                    resolved.Add(pair.Key);
                }
            }
            foreach (var id in resolved)
            {
                _pendingGuests.Remove(id);
            }
        }

        private void RemoveDepartedGuests()
        {
            var departed = new List<ulong>();
            foreach (var pair in _guests)
            {
                if (!IsLobbyMember((SteamId)pair.Key))
                {
                    pair.Value.Close(false, 2001, "Member left the Steam room");
                    departed.Add(pair.Key);
                }
            }
            foreach (var id in departed)
            {
                _guests.Remove(id);
                Debug.Log("[RuinaCoop] Relay member left the room: " + id + ".");
            }
        }

        private void GuestConnected(Connection connection, ConnectionInfo info)
        {
            _onMainThread(() =>
            {
                if (_stopped)
                {
                    connection.Close(false, 2001, "Room closed");
                    return;
                }
                var challenge = RelayAuth.NewChallenge();
                _pendingGuests[connection.Id] = new PendingGuest
                {
                    Connection = connection,
                    Deadline = Time.realtimeSinceStartup + 15f,
                    Challenge = challenge
                };
                var result = connection.SendMessage(RelayAuth.ChallengePacket(challenge), SendType.Reliable);
                if (result != Steamworks.Result.OK)
                {
                    _pendingGuests.Remove(connection.Id);
                    connection.Close(false, 2001, "Could not send lobby challenge");
                    Debug.LogWarning("[RuinaCoop] Relay challenge send failed on connection " +
                        connection.Id + ": " + result + ".");
                    return;
                }
                Status = "Verifying " + _pendingGuests.Count + " relay guest(s) in the Steam room...";
                Debug.Log("[RuinaCoop] Relay challenge sent on connection " + connection.Id +
                    "; callback identity " + info.Identity + "; lobby members " +
                    _room.MemberCount + ".");
            });
        }

        private void GuestDisconnected(Connection connection, ConnectionInfo info)
        {
            _onMainThread(() =>
            {
                if (_stopped)
                {
                    return;
                }
                _pendingGuests.Remove(connection.Id);
                ulong guestId = 0;
                foreach (var pair in _guests)
                {
                    if (pair.Value.Id == connection.Id)
                    {
                        guestId = pair.Key;
                        break;
                    }
                }
                if (guestId != 0)
                {
                    _guests.Remove(guestId);
                }
                Status = "Sharing host progress with " + _guests.Count + " guest(s).";
                Debug.Log("[RuinaCoop] Guest relay disconnected: connection " + connection.Id +
                    ", member " + guestId + ", reason " + info.EndReason + ".");
            });
        }

        private void HandleLobbyChat(Lobby room, Friend sender, string message)
        {
            if (!_isHost || room.Id != _room.Id)
            {
                return;
            }
            if (_lobbyEchoToken != null && sender.Id == _hostId && message == _lobbyEchoToken)
            {
                _onMainThread(() =>
                {
                    if (_stopped || _lobbyEchoToken != message)
                    {
                        return;
                    }
                    _lobbyEchoToken = null;
                    LobbyEchoStatus = "PASS: Steam room message and sender ID.";
                    Debug.Log("[RuinaCoop] " + LobbyEchoStatus);
                });
                return;
            }
            if (!RelayAuth.IsProofMessage(message))
            {
                return;
            }
            _onMainThread(() =>
            {
                if (_stopped || sender.Id == _hostId || !IsLobbyMember(sender.Id))
                {
                    return;
                }
                uint connectionId = 0;
                PendingGuest pending = null;
                foreach (var pair in _pendingGuests)
                {
                    if (RelayAuth.MatchesProof(message, pair.Value.Challenge,
                        sender.Id.Value, _room.Id.Value))
                    {
                        connectionId = pair.Key;
                        pending = pair.Value;
                        break;
                    }
                }
                if (pending == null)
                {
                    if (!_guests.ContainsKey(sender.Id.Value))
                    {
                        Debug.LogWarning("[RuinaCoop] Lobby proof did not match a pending relay connection from " +
                            sender.Id + ".");
                    }
                    return;
                }
                _pendingGuests.Remove(connectionId);
                if (_guests.Count >= ProtocolInfo.MaxPlayers - 1 || _guests.ContainsKey(sender.Id.Value))
                {
                    pending.Connection.Close(false, 2001, "Room is full or member already connected");
                    return;
                }
                _guests.Add(sender.Id.Value, pending.Connection);
                var accepted = pending.Connection.SendMessage(
                    RelayAuth.AcceptedPacket(pending.Challenge), SendType.Reliable);
                if (accepted != Steamworks.Result.OK)
                {
                    _guests.Remove(sender.Id.Value);
                    pending.Connection.Close(false, 2001, "Could not acknowledge lobby proof");
                    Debug.LogWarning("[RuinaCoop] Relay proof acknowledgment failed: " + accepted + ".");
                    return;
                }
                SendSnapshot(pending.Connection);
                Status = "Sharing host progress with " + _guests.Count + " guest(s).";
                Debug.Log("[RuinaCoop] Relay member authorized by lobby proof: " +
                    sender.Id + ", connection " + connectionId + ".");
            });
        }

        private void ReceiveChallenge(string challenge)
        {
            _onMainThread(() =>
            {
                if (_stopped || _isHost || _guestAuthenticated)
                {
                    return;
                }
                if (_guestChallenge != null && _guestChallenge != challenge)
                {
                    _guestConnection.Close();
                    Status = "Host sent conflicting relay challenges.";
                    return;
                }
                _guestChallenge = challenge;
                SendLobbyProof();
            });
        }

        private void SendLobbyProof()
        {
            _nextProofTime = Time.realtimeSinceStartup + 1f;
            var message = RelayAuth.ProofMessage(_guestChallenge,
                SteamClient.SteamId.Value, _room.Id.Value);
            if (!_room.SendChatString(message))
            {
                Status = "Could not verify room membership; retrying...";
                Debug.LogWarning("[RuinaCoop] Lobby proof send failed; retrying.");
                return;
            }
            Status = "Verifying room membership with host...";
            Debug.Log("[RuinaCoop] Lobby proof sent for room " + _room.Id + ".");
        }

        private void ReceiveAccepted(string challenge)
        {
            _onMainThread(() =>
            {
                if (_stopped || _isHost || challenge != _guestChallenge)
                {
                    return;
                }
                _guestAuthenticated = true;
                Status = "Room membership verified; waiting for host progress.";
                Debug.Log("[RuinaCoop] Host confirmed lobby proof for room " + _room.Id + ".");
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
                if (!_guestAuthenticated)
                {
                    Debug.LogWarning("[RuinaCoop] Snapshot arrived before lobby proof confirmation.");
                    return;
                }
                ProgressSnapshot snapshot;
                string decodeReason;
                if (!ProgressSnapshot.TryDecode(bytes, _room.Id.Value, out snapshot, out decodeReason))
                {
                    Status = "Rejected host progress: " + decodeReason;
                    Debug.LogWarning("[RuinaCoop] Invalid progress packet rejected (" +
                        bytes.Length + " bytes): " + decodeReason);
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
                if (Owner == null || Owner._stopped || Owner._guests.Count + Owner._pendingGuests.Count >= 8)
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
                    Owner.GuestDisconnected(connection, info);
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
                    string challenge;
                    if (RelayAuth.TryReadChallenge(bytes, out challenge))
                    {
                        Owner.ReceiveChallenge(challenge);
                    }
                    else if (RelayAuth.TryReadAccepted(bytes, out challenge))
                    {
                        Owner.ReceiveAccepted(challenge);
                    }
                    else
                    {
                        Owner.ReceiveHostMessage(bytes);
                    }
                }
            }
        }
    }
}
