using System;
using System.Collections.Generic;
using System.Reflection;
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
        private static readonly FieldInfo CurrentBookDeckField = typeof(BookModel).GetField(
            "_deck", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly Lobby _room;
        private readonly SteamId _hostId;
        private readonly bool _isHost;
        private readonly Action<Action> _onMainThread;
        private readonly Action<ProgressSnapshot> _onSnapshot;
        private readonly Dictionary<ulong, Connection> _guests = new Dictionary<ulong, Connection>();
        private readonly Dictionary<ulong, uint> _lastClaimRequests = new Dictionary<ulong, uint>();
        private readonly Dictionary<ulong, uint> _lastDeckRequests = new Dictionary<ulong, uint>();
        private readonly Dictionary<ulong, CorePageReceipt> _corePageReceipts = new Dictionary<ulong, CorePageReceipt>();
        private sealed class CorePageReceipt
        {
            internal CorePageRequest Request;
            internal CorePageReply Reply;
        }
        private readonly PrepClaims _claims = new PrepClaims();
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
        private uint _nextClaimRequest;
        private uint _lastClaimReply;
        private uint _nextDeckRequest;
        private uint _lastDeckReply;
        private uint _deckRevision;
        private uint _pendingDeckRequest;
        private float _deckRequestDeadline;
        private DeckReply? _deferredDeckReply;
        private uint _nextCorePageRequest;
        private uint _lastCorePageReply;
        private uint _pendingCorePageRequest;
        private CorePageReply? _deferredCorePageReply;
        private readonly Dictionary<ulong, float> _nextDeckRequestTime = new Dictionary<ulong, float>();
        private readonly HashSet<uint> _snapshotRetries = new HashSet<uint>();

        internal string Status { get; private set; }
        internal string LobbyEchoStatus { get; private set; } = "Not run.";
        internal string ClaimStatus { get; private set; } = "Choose a stage, then the host chooses a floor.";
        private string _deckStatus = "Choose a stage and floor, then claim a librarian.";
        internal event Action DeckStateChanged;
        internal string DeckStatus
        {
            get { return _deckStatus; }
            private set
            {
                if (_deckStatus == value) return;
                _deckStatus = value;
                NotifyDeckStateChanged();
            }
        }
        internal ProgressSnapshot LatestSnapshot { get; private set; }
        internal ulong RoomId { get { return _room.Id.Value; } }
        internal bool IsHost { get { return _isHost; } }
        internal bool IsActive { get { return !_stopped; } }
        internal bool IsReadyForDeck
        {
            get { return !_stopped && (_isHost || _guestAuthenticated && _guestConnection != null); }
        }
        internal bool IsGuestSession { get { return !_stopped && !_isHost; } }
        internal bool DeckRequestPending { get { return _pendingDeckRequest != 0 || _pendingCorePageRequest != 0; } }

        private void NotifyDeckStateChanged()
        {
            var handlers = DeckStateChanged;
            if (handlers == null) return;
            foreach (Action handler in handlers.GetInvocationList())
            {
                try { handler(); }
                catch (Exception exception)
                {
                    Debug.LogError("[RuinaCoop] Deck state display failed: " + exception);
                }
            }
        }
        internal bool PreparationFrozen
        {
            get
            {
                var scenes = GameSceneManager.Instance;
                return !_stopped && scenes != null && scenes.battleScene != null &&
                    scenes.battleScene.gameObject.activeSelf;
            }
        }

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
            DeckGuard.Session = session;
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
            DeckGuard.Session = session;
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
                    if (DeckRequestPending && Time.realtimeSinceStartup >= _deckRequestDeadline)
                    {
                        // Keep editing locked until a reply arrives or the guest rejoins:
                        // an unacknowledged request may already have changed the host deck.
                        DeckStatus = "Deck reply timed out; leave and rejoin to refresh host state.";
                    }
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
            if (!_isHost || PreparationFrozen || LatestSnapshot == null ||
                !LatestSnapshot.Stages.Exists(stage => stage.Id == stageId))
            {
                return false;
            }
            _selectedStageId = stageId;
            CaptureAndBroadcast();
            return true;
        }

        internal bool CanEditLocalUnit(UnitDataModel unit)
        {
            if (_stopped)
            {
                return true;
            }
            if (!_isHost)
            {
                // A guest's local save is never the authoritative deck source.
                return false;
            }
            var snapshot = LatestSnapshot;
            if (snapshot == null || snapshot.SelectedFloorId == PrepClaims.NoFloor)
            {
                return true;
            }
            foreach (var floor in snapshot.Floors)
            {
                if ((byte)floor.Sephirah != snapshot.SelectedFloorId)
                {
                    continue;
                }
                for (var i = 0; i < floor.UnitReferences.Count; i++)
                {
                    if (ReferenceEquals(floor.UnitReferences[i], unit))
                    {
                        var owner = _claims.OwnerAt((byte)i);
                        return !PreparationFrozen && (owner == 0 || owner == _hostId.Value);
                    }
                }
                break;
            }
            return true;
        }

        internal bool CanEditLocalBook(BookModel book)
        {
            if (_stopped || _isHost && book == null)
            {
                return true;
            }
            if (!_isHost)
            {
                return false;
            }
            var snapshot = LatestSnapshot;
            if (snapshot == null || snapshot.SelectedFloorId == PrepClaims.NoFloor)
            {
                return true;
            }
            foreach (var floor in snapshot.Floors)
            {
                if ((byte)floor.Sephirah != snapshot.SelectedFloorId)
                {
                    continue;
                }
                foreach (var reference in floor.UnitReferences)
                {
                    var unit = reference as UnitDataModel;
                    if (unit != null && ReferenceEquals(unit.bookItem, book))
                    {
                        return CanEditLocalUnit(unit);
                    }
                }
                break;
            }
            return true;
        }

        internal bool CanEditLocalDeck(DeckModel deck)
        {
            if (_stopped || _isHost && deck == null)
            {
                return true;
            }
            if (!_isHost)
            {
                return false;
            }
            var snapshot = LatestSnapshot;
            if (snapshot == null || snapshot.SelectedFloorId == PrepClaims.NoFloor)
            {
                return true;
            }
            foreach (var floor in snapshot.Floors)
            {
                if ((byte)floor.Sephirah != snapshot.SelectedFloorId)
                {
                    continue;
                }
                foreach (var reference in floor.UnitReferences)
                {
                    var unit = reference as UnitDataModel;
                    var book = unit == null ? null : unit.bookItem;
                    if (book == null)
                    {
                        continue;
                    }
                    if (CurrentBookDeckField != null &&
                        ReferenceEquals(CurrentBookDeckField.GetValue(book), deck))
                    {
                        return CanEditLocalUnit(unit);
                    }
                    var decks = book.GetDeckAll_nocopy();
                    if (decks != null && decks.Contains(deck))
                    {
                        return CanEditLocalUnit(unit);
                    }
                }
                break;
            }
            return true;
        }

        internal bool SelectFloor(byte floorId)
        {
            if (!_isHost || PreparationFrozen || !_claims.SelectFloor(LatestSnapshot, floorId))
            {
                return false;
            }
            ClaimStatus = "Floor selected; players may claim librarians.";
            CaptureAndBroadcast();
            return true;
        }

        internal void RequestClaim(byte unitIndex, ClaimAction action)
        {
            RequestClaim(LatestSnapshot, unitIndex, action);
        }

        internal bool RequestClaim(ProgressSnapshot expected, byte unitIndex, ClaimAction action)
        {
            if (_stopped || LatestSnapshot == null || LatestSnapshot.SelectedStageId == 0 ||
                LatestSnapshot.SelectedFloorId == PrepClaims.NoFloor)
            {
                ClaimStatus = "Select a stage and floor first.";
                return false;
            }
            if (!ReferenceEquals(expected, LatestSnapshot))
            {
                ClaimStatus = "The displayed librarian list changed; refresh before claiming.";
                return false;
            }
            var snapshot = expected;
            var request = new ClaimRequest
            {
                RequestId = ++_nextClaimRequest,
                StageId = snapshot.SelectedStageId,
                FloorId = snapshot.SelectedFloorId,
                UnitIndex = unitIndex,
                ExpectedRevision = snapshot.ClaimRevision,
                Action = action
            };
            if (_isHost)
            {
                if (PreparationFrozen || !CaptureAndBroadcast())
                {
                    ClaimStatus = "Claims are unavailable during reception or before progress is ready.";
                    return false;
                }
                var result = _claims.Apply(SteamClient.SteamId.Value, request.StageId,
                    request.FloorId, request.UnitIndex, request.ExpectedRevision, action);
                ClaimStatus = "Claim " + action + ": " + result + ".";
                if (result == ClaimResultCode.Accepted)
                {
                    CaptureAndBroadcast();
                }
                return result == ClaimResultCode.Accepted;
            }
            if (!_guestAuthenticated || _guestConnection == null)
            {
                ClaimStatus = "Waiting for host room verification.";
                return false;
            }
            var send = _guestConnection.Connection.SendMessage(
                ClaimProtocol.EncodeRequest(_room.Id.Value, request), SendType.Reliable);
            ClaimStatus = send == Steamworks.Result.OK
                ? "Claim request sent; waiting for host."
                : "Claim request send failed: " + send + ".";
            return send == Steamworks.Result.OK;
        }

        internal void RequestDeckEdit(byte unitIndex, int cardId, DeckAction action)
        {
            RequestDeckEdit(LatestSnapshot, unitIndex, cardId, action);
        }

        // Native controls pass the state they actually display. Never combine
        // an old control's slot index with a newer room/floor/revision.
        internal bool ValidateDisplayedDeckRequest(ProgressSnapshot expected)
        {
            if (DeckRequestPending)
            {
                DeckStatus = "Waiting for the previous deck edit to finish.";
                return false;
            }
            if (_stopped || LatestSnapshot == null || LatestSnapshot.SelectedStageId == 0 ||
                LatestSnapshot.SelectedFloorId == PrepClaims.NoFloor)
            {
                DeckStatus = "Choose a stage and floor first.";
                return false;
            }
            if (!ReferenceEquals(expected, LatestSnapshot))
            {
                DeckStatus = "The displayed deck changed; refresh before editing.";
                return false;
            }
            return true;
        }

        internal bool RequestDeckEdit(ProgressSnapshot expected, byte unitIndex, int cardId, DeckAction action)
        {
            if (!ValidateDisplayedDeckRequest(expected)) return false;
            var snapshot = expected;
            var request = new DeckRequest
            {
                RequestId = ++_nextDeckRequest,
                StageId = snapshot.SelectedStageId,
                FloorId = snapshot.SelectedFloorId,
                UnitIndex = unitIndex,
                ClaimRevision = snapshot.ClaimRevision,
                DeckRevision = snapshot.DeckRevision,
                CardId = cardId,
                Action = action
            };
            if (_isHost)
            {
                byte vanillaState;
                var result = ApplyDeckRequest(_hostId.Value, request, out vanillaState);
                DeckStatus = DeckResultText(result, vanillaState);
                return result == DeckResultCode.Accepted;
            }
            if (!_guestAuthenticated || _guestConnection == null)
            {
                DeckStatus = "Waiting for host room verification.";
                return false;
            }
            var permission = DeckAuthority.Validate(snapshot, SteamClient.SteamId.Value, request);
            if (permission != DeckResultCode.Accepted)
            {
                DeckStatus = DeckResultText(permission, 0);
                return false;
            }
            var send = _guestConnection.Connection.SendMessage(
                DeckProtocol.EncodeRequest(_room.Id.Value, request), SendType.Reliable);
            if (send == Steamworks.Result.OK)
            {
                _pendingDeckRequest = request.RequestId;
                _deckRequestDeadline = Time.realtimeSinceStartup + 10f;
            }
            DeckStatus = send == Steamworks.Result.OK
                ? "Deck edit sent; waiting for host."
                : "Deck edit send failed: " + send + ".";
            return send == Steamworks.Result.OK;
        }

        private DeckResultCode ApplyDeckRequest(ulong sender, DeckRequest request,
            out byte vanillaState)
        {
            vanillaState = 0;
            if (!_isHost || !CaptureAndBroadcast())
            {
                return DeckResultCode.NotReady;
            }
            var snapshot = LatestSnapshot;
            var validation = DeckAuthority.Validate(snapshot, sender, request);
            if (validation != DeckResultCode.Accepted)
            {
                return validation;
            }
            UnitDataModel unit = null;
            foreach (var floor in snapshot.Floors)
            {
                if ((byte)floor.Sephirah == snapshot.SelectedFloorId &&
                    request.UnitIndex < floor.UnitReferences.Count)
                {
                    unit = floor.UnitReferences[request.UnitIndex] as UnitDataModel;
                    break;
                }
            }
            if (unit == null || unit.bookItem == null)
            {
                return DeckResultCode.NotReady;
            }
            return DeckGuard.EditWithRollback(unit, new LorId(request.CardId),
                request.Action, CaptureAndBroadcast, out vanillaState);
        }

        private static string DeckResultText(DeckResultCode result, byte vanillaState)
        {
            return result == DeckResultCode.VanillaRejected
                ? "Deck edit rejected by game rules: " + (CardEquipState)vanillaState + "."
                : "Deck edit: " + result + ".";
        }

        internal bool RequestCorePageEdit(ProgressSnapshot expected, byte unitIndex, ulong bookToken)
        {
            if (!ValidateDisplayedDeckRequest(expected)) return false;
            if (unitIndex >= expected.UnitDecks.Count) return false;
            var unit = expected.UnitDecks[unitIndex];
            var request = new CorePageRequest
            {
                RequestId = ++_nextCorePageRequest,
                StageId = expected.SelectedStageId,
                FloorId = expected.SelectedFloorId,
                UnitIndex = unitIndex,
                UnitIdentity = unit.UnitIdentity,
                OldBookToken = unit.BookToken,
                TargetBookToken = bookToken,
                ClaimRevision = expected.ClaimRevision,
                DeckRevision = expected.DeckRevision
            };
            if (_isHost)
            {
                var result = ApplyCorePageRequest(_hostId.Value, request);
                DeckStatus = CorePageResultText(result);
                return result == CorePageResultCode.Accepted;
            }
            if (!_guestAuthenticated || _guestConnection == null)
            {
                DeckStatus = "Waiting for host room verification.";
                return false;
            }
            var permission = EquipmentAuthority.Validate(expected, SteamClient.SteamId.Value, request);
            if (permission != CorePageResultCode.Accepted)
            {
                DeckStatus = CorePageResultText(permission);
                return false;
            }
            var send = _guestConnection.Connection.SendMessage(
                EquipmentProtocol.EncodeRequest(_room.Id.Value, request), SendType.Reliable);
            if (send == Steamworks.Result.OK)
            {
                _pendingCorePageRequest = request.RequestId;
                _deckRequestDeadline = Time.realtimeSinceStartup + 10f;
            }
            DeckStatus = send == Steamworks.Result.OK
                ? "Core page edit sent; waiting for host."
                : "Core page edit send failed: " + send + ".";
            return send == Steamworks.Result.OK;
        }

        private CorePageResultCode ApplyCorePageRequest(ulong sender, CorePageRequest request)
        {
            if (!_isHost) return CorePageResultCode.NotReady;
            if (PreparationFrozen) return CorePageResultCode.Frozen;
            if (!CaptureAndBroadcast()) return CorePageResultCode.NotReady;
            var snapshot = LatestSnapshot;
            var validation = EquipmentAuthority.Validate(snapshot, sender, request);
            if (validation != CorePageResultCode.Accepted) return validation;
            var floor = snapshot.Floors.Find(candidate => (byte)candidate.Sephirah == snapshot.SelectedFloorId);
            var unit = floor == null || request.UnitIndex >= floor.UnitReferences.Count
                ? null : floor.UnitReferences[request.UnitIndex] as UnitDataModel;
            BookModel target;
            if (unit == null || !EquipmentMirror.TryResolveBook(snapshot, request.TargetBookToken, out target))
                return CorePageResultCode.UnknownTarget;
            string reason;
            var result = EquipmentTransaction.Equip(unit, target, CaptureCorePageAndBroadcast, out reason);
            if (result != EquipmentTransactionResult.Accepted)
                Debug.LogWarning("[RuinaCoop] Core page transaction: " + reason);
            return result == EquipmentTransactionResult.Accepted ? CorePageResultCode.Accepted :
                result == EquipmentTransactionResult.Rejected ? CorePageResultCode.VanillaRejected : CorePageResultCode.Failed;
        }

        private static string CorePageResultText(CorePageResultCode result)
        {
            return "Core page edit: " + result + ".";
        }

        internal void ReleaseAbsentClaims()
        {
            if (_isHost && !PreparationFrozen && _claims.ReleaseAbsent(id =>
                id == _hostId.Value || IsLobbyMember((SteamId)id) && _guests.ContainsKey(id)))
            {
                ClaimStatus = "Released claims held by disconnected members; claim a slot to take control.";
                CaptureAndBroadcast();
            }
        }

        internal void StartLobbyEchoTest()
        {
            if (!_isHost || _stopped || !SteamClient.IsValid)
            {
                return;
            }
            _lobbyEchoToken = "RC7T:" + RelayAuth.NewChallenge();
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
            _pendingDeckRequest = 0;
            _deferredDeckReply = null;
            _pendingCorePageRequest = 0;
            _deferredCorePageReply = null;
            DeckStatus = "Room session closed.";
            if (ReferenceEquals(DeckGuard.Session, this))
            {
                DeckGuard.Session = null;
            }
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
                _lastClaimRequests.Clear();
                _lastDeckRequests.Clear();
                _corePageReceipts.Clear();
                _nextDeckRequestTime.Clear();
                _snapshotRetries.Clear();
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

        private bool CaptureAndBroadcast()
        {
            return CaptureAndBroadcast(false);
        }

        private bool CaptureCorePageAndBroadcast()
        {
            return CaptureAndBroadcast(true);
        }

        private bool CaptureAndBroadcast(bool requireCoreBooks)
        {
            try
            {
                var snapshot = ProgressSnapshot.Capture(_selectedStageId);
                _selectedStageId = snapshot.SelectedStageId;
                _claims.Reconcile(snapshot);
                DeckMirror.Capture(snapshot);
                EquipmentMirror.Capture(snapshot);
                if (requireCoreBooks && !snapshot.CoreBooksAvailable)
                    throw new InvalidOperationException("Cannot publish the complete core page inventory: " + snapshot.CoreBooksReason);
                snapshot.DecksFrozen = PreparationFrozen;
                if (!SameDeckData(snapshot, LatestSnapshot))
                {
                    _deckRevision++;
                }
                snapshot.DeckRevision = _deckRevision;
                var content = snapshot.Encode(_room.Id.Value);
                if (SameBytes(content, _latestContent))
                {
                    foreach (var connection in _guests.Values)
                    {
                        if (_snapshotRetries.Contains(connection.Id))
                        {
                            SendSnapshot(connection);
                        }
                    }
                    return true;
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
                try
                {
                    _onSnapshot(snapshot);
                }
                catch (Exception exception)
                {
                    Debug.LogError("[RuinaCoop] Snapshot display failed: " + exception);
                }
                foreach (var connection in _guests.Values)
                {
                    SendSnapshot(connection);
                }
                Status = "Sharing host progress with " + _guests.Count + " guest(s).";
                Debug.Log("[RuinaCoop] Progress snapshot " + snapshot.Sequence + ": " +
                    snapshot.Stages.Count + " stages, " + snapshot.Floors.Count +
                    " floors, " + packet.Length + " bytes; local decode PASS.");
                return true;
            }
            catch (Exception exception)
            {
                var nextStatus = "Progress waiting: " + exception.Message;
                if (Status != nextStatus)
                {
                    Debug.LogWarning("[RuinaCoop] " + nextStatus);
                }
                Status = nextStatus;
                return false;
            }
        }

        private static bool SameDeckData(ProgressSnapshot left, ProgressSnapshot right)
        {
            return right != null && SameBytes(DeckMirror.EncodeContent(left), DeckMirror.EncodeContent(right)) &&
                SameBytes(EquipmentMirror.EncodeContent(left), EquipmentMirror.EncodeContent(right));
        }

        private void SendSnapshot(Connection connection)
        {
            if (_latestPacket == null)
            {
                return;
            }
            try
            {
                var result = connection.SendMessage(_latestPacket, SendType.Reliable);
                if (result != Steamworks.Result.OK)
                {
                    _snapshotRetries.Add(connection.Id);
                    Debug.LogWarning("[RuinaCoop] Snapshot send failed: " + result + ".");
                }
                else
                {
                    _snapshotRetries.Remove(connection.Id);
                }
            }
            catch (Exception exception)
            {
                // A transport failure does not roll back a locally committed edit.
                // The latest packet remains available for reconnection.
                _snapshotRetries.Add(connection.Id);
                Debug.LogWarning("[RuinaCoop] Snapshot send failed: " + exception.Message);
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
                    _snapshotRetries.Remove(pair.Value.Id);
                    departed.Add(pair.Key);
                    ClaimStatus = "A member left; their claims remain locked until the host releases them.";
                }
            }
            foreach (var id in departed)
            {
                _guests.Remove(id);
                _lastClaimRequests.Remove(id);
                _lastDeckRequests.Remove(id);
                _corePageReceipts.Remove(id);
                _nextDeckRequestTime.Remove(id);
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
                _snapshotRetries.Remove(connection.Id);
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
                    _lastClaimRequests.Remove(guestId);
                    _lastDeckRequests.Remove(guestId);
                    _corePageReceipts.Remove(guestId);
                    _nextDeckRequestTime.Remove(guestId);
                    ClaimStatus = "A member disconnected; their claims remain locked until the host releases them.";
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
                    _guestAuthenticated = false;
                    DeckStatus = "Host connection closed; leave and rejoin to edit decks.";
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
                try
                {
                    _onSnapshot(snapshot);
                }
                catch (Exception exception)
                {
                    // Display failures cannot prevent transport acknowledgments
                    // from completing against the state already accepted above.
                    Debug.LogError("[RuinaCoop] Snapshot display failed: " + exception);
                }
                if (_deferredDeckReply.HasValue)
                {
                    CompleteDeckReply(_deferredDeckReply.Value);
                }
                if (_deferredCorePageReply.HasValue) CompleteCorePageReply(_deferredCorePageReply.Value);
                Status = "Host progress received: " + snapshot.Stages.Count + " stages, " +
                    snapshot.Floors.Count + " floors.";
                Debug.Log("[RuinaCoop] Received host progress snapshot " + snapshot.Sequence + ".");
            });
        }

        private void ReceiveClaimReply(ClaimReply reply)
        {
            _onMainThread(() =>
            {
                if (_stopped || _isHost || !_guestAuthenticated ||
                    reply.RequestId <= _lastClaimReply)
                {
                    return;
                }
                _lastClaimReply = reply.RequestId;
                ClaimStatus = "Claim request " + reply.RequestId + ": " + reply.Result +
                    " (revision " + reply.Revision + ").";
                if (reply.Result != ClaimResultCode.Accepted)
                {
                    Debug.LogWarning("[RuinaCoop] Claim request rejected: " + reply.Result +
                        "; host revision " + reply.Revision + ".");
                }
            });
        }

        private void ReceiveDeckReply(DeckReply reply)
        {
            _onMainThread(() => CompleteDeckReply(reply));
        }

        private void CompleteDeckReply(DeckReply reply)
        {
            if (_stopped || _isHost || !_guestAuthenticated ||
                reply.RequestId != _pendingDeckRequest || reply.RequestId <= _lastDeckReply)
            {
                return;
            }
            // A failed snapshot send can be retried after its reply arrives.
            // Keep the reply until the corresponding authoritative state is visible.
            if (LatestSnapshot == null || LatestSnapshot.DeckRevision < reply.DeckRevision)
            {
                _deferredDeckReply = reply;
                DeckStatus = "Waiting for host deck state; rejoin if this message persists.";
                return;
            }
            _deferredDeckReply = null;
            _lastDeckReply = reply.RequestId;
            _pendingDeckRequest = 0;
            DeckStatus = DeckResultText(reply.Result, reply.VanillaState);
            if (reply.Result != DeckResultCode.Accepted)
            {
                Debug.LogWarning("[RuinaCoop] Deck request " + reply.RequestId +
                    " rejected: " + reply.Result + "; host revision " + reply.DeckRevision + ".");
            }
        }

        private void ReceiveGuestMessage(Connection connection, byte[] bytes)
        {
            _onMainThread(() =>
            {
                if (_stopped || !_isHost)
                {
                    return;
                }
                ulong sender = 0;
                foreach (var pair in _guests)
                {
                    if (pair.Value.Id == connection.Id)
                    {
                        sender = pair.Key;
                        break;
                    }
                }
                if (sender == 0 || !IsLobbyMember((SteamId)sender))
                {
                    Debug.LogWarning("[RuinaCoop] Request from an unverified room member ignored.");
                    return;
                }
                CorePageRequest corePageRequest;
                if (EquipmentProtocol.TryDecodeRequest(bytes, _room.Id.Value, out corePageRequest))
                {
                    HandleCorePageRequest(connection, sender, corePageRequest);
                    return;
                }
                DeckRequest deckRequest;
                if (DeckProtocol.TryDecodeRequest(bytes, _room.Id.Value, out deckRequest))
                {
                    HandleDeckRequest(connection, sender, deckRequest);
                    return;
                }
                ClaimRequest request;
                if (!ClaimProtocol.TryDecodeRequest(bytes, _room.Id.Value, out request))
                {
                    Debug.LogWarning("[RuinaCoop] Invalid guest command packet ignored.");
                    return;
                }
                uint lastRequest;
                ClaimResultCode result;
                if (_lastClaimRequests.TryGetValue(sender, out lastRequest) &&
                    request.RequestId <= lastRequest)
                {
                    result = ClaimResultCode.StaleRevision;
                }
                else
                {
                    _lastClaimRequests[sender] = request.RequestId;
                    result = PreparationFrozen || !CaptureAndBroadcast()
                        ? ClaimResultCode.NotReady
                        : _claims.Apply(sender, request.StageId, request.FloorId,
                            request.UnitIndex, request.ExpectedRevision, request.Action);
                    if (result == ClaimResultCode.Accepted)
                    {
                        CaptureAndBroadcast();
                    }
                }
                var reply = new ClaimReply
                {
                    RequestId = request.RequestId,
                    Result = result,
                    Revision = _claims.Revision
                };
                var send = connection.SendMessage(
                    ClaimProtocol.EncodeReply(_room.Id.Value, reply), SendType.Reliable);
                if (send != Steamworks.Result.OK)
                {
                    Debug.LogWarning("[RuinaCoop] Claim reply send failed: " + send + ".");
                }
            });
        }

        private void HandleDeckRequest(Connection connection, ulong sender, DeckRequest request)
        {
            uint lastRequest;
            float nextRequestTime;
            byte vanillaState = 0;
            DeckResultCode result;
            if (_lastDeckRequests.TryGetValue(sender, out lastRequest) && request.RequestId <= lastRequest)
            {
                result = DeckResultCode.InvalidRequest;
            }
            else
            {
                _lastDeckRequests[sender] = request.RequestId;
                if (_nextDeckRequestTime.TryGetValue(sender, out nextRequestTime) &&
                    Time.realtimeSinceStartup < nextRequestTime)
                {
                    result = DeckResultCode.InvalidRequest;
                }
                else
                {
                    _nextDeckRequestTime[sender] = Time.realtimeSinceStartup + 0.1f;
                    result = ApplyDeckRequest(sender, request, out vanillaState);
                }
            }
            // Also resend on rejection: the guest restores/displays the current
            // host state and can retry with fresh claim and inventory revisions.
            SendSnapshot(connection);
            var reply = new DeckReply
            {
                RequestId = request.RequestId,
                Result = result,
                DeckRevision = LatestSnapshot == null ? 0 : LatestSnapshot.DeckRevision,
                VanillaState = vanillaState
            };
            var send = connection.SendMessage(DeckProtocol.EncodeReply(_room.Id.Value, reply), SendType.Reliable);
            if (send != Steamworks.Result.OK)
            {
                Debug.LogWarning("[RuinaCoop] Deck reply send failed: " + send + ".");
            }
        }

        private void ReceiveCorePageReply(CorePageReply reply)
        {
            _onMainThread(() => CompleteCorePageReply(reply));
        }

        private void CompleteCorePageReply(CorePageReply reply)
        {
            if (_stopped || _isHost || !_guestAuthenticated ||
                reply.RequestId != _pendingCorePageRequest || reply.RequestId <= _lastCorePageReply) return;
            if (LatestSnapshot == null || LatestSnapshot.DeckRevision < reply.DeckRevision)
            {
                _deferredCorePageReply = reply;
                DeckStatus = "Waiting for host equipment state; rejoin if this message persists.";
                return;
            }
            _deferredCorePageReply = null;
            _lastCorePageReply = reply.RequestId;
            _pendingCorePageRequest = 0;
            DeckStatus = CorePageResultText(reply.Result);
            if (reply.Result != CorePageResultCode.Accepted)
                Debug.LogWarning("[RuinaCoop] Core page request " + reply.RequestId + " rejected: " + reply.Result + ".");
        }

        private void HandleCorePageRequest(Connection connection, ulong sender, CorePageRequest request)
        {
            CorePageReceipt receipt;
            CorePageReply reply;
            if (_corePageReceipts.TryGetValue(sender, out receipt) && request.RequestId <= receipt.Request.RequestId)
            {
                var sameRequest = CorePageRequestsMatch(request, receipt.Request);
                reply = sameRequest ? receipt.Reply : new CorePageReply
                {
                    RequestId = request.RequestId, Result = CorePageResultCode.InvalidRequest,
                    DeckRevision = LatestSnapshot == null ? 0 : LatestSnapshot.DeckRevision
                };
            }
            else
            {
                float nextTime;
                var tooFast = _nextDeckRequestTime.TryGetValue(sender, out nextTime) && Time.realtimeSinceStartup < nextTime;
                _nextDeckRequestTime[sender] = Time.realtimeSinceStartup + 0.1f;
                var result = tooFast ? CorePageResultCode.InvalidRequest : ApplyCorePageRequest(sender, request);
                reply = new CorePageReply
                {
                    RequestId = request.RequestId, Result = result,
                    DeckRevision = LatestSnapshot == null ? 0 : LatestSnapshot.DeckRevision
                };
                _corePageReceipts[sender] = new CorePageReceipt { Request = request, Reply = reply };
            }
            SendSnapshot(connection);
            var send = connection.SendMessage(EquipmentProtocol.EncodeReply(_room.Id.Value, reply), SendType.Reliable);
            if (send != Steamworks.Result.OK) Debug.LogWarning("[RuinaCoop] Core page reply send failed: " + send + ".");
        }

        private static bool CorePageRequestsMatch(CorePageRequest left, CorePageRequest right)
        {
            return left.RequestId == right.RequestId && left.StageId == right.StageId &&
                left.FloorId == right.FloorId && left.UnitIndex == right.UnitIndex && left.UnitIdentity == right.UnitIdentity &&
                left.OldBookToken == right.OldBookToken && left.TargetBookToken == right.TargetBookToken &&
                left.ClaimRevision == right.ClaimRevision && left.DeckRevision == right.DeckRevision;
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
                if (Owner == null)
                {
                    return;
                }
                var bytes = CopyMessage(data, size);
                if (bytes != null)
                {
                    Owner.ReceiveGuestMessage(connection, bytes);
                }
                else if (size > 0)
                {
                    Debug.LogWarning("[RuinaCoop] Oversized guest relay message ignored.");
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
                    else if (ClaimProtocol.TryDecodeReply(bytes, Owner._room.Id.Value,
                        out ClaimReply reply))
                    {
                        Owner.ReceiveClaimReply(reply);
                    }
                    else if (DeckProtocol.TryDecodeReply(bytes, Owner._room.Id.Value,
                        out DeckReply deckReply))
                    {
                        Owner.ReceiveDeckReply(deckReply);
                    }
                    else if (EquipmentProtocol.TryDecodeReply(bytes, Owner._room.Id.Value,
                        out CorePageReply coreReply))
                    {
                        Owner.ReceiveCorePageReply(coreReply);
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
