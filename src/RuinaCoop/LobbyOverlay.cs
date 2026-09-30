using System;
using System.Collections.Generic;
using Steamworks;
using Steamworks.Data;
using UnityEngine;

namespace RuinaCoop
{
    public sealed class LobbyOverlay : MonoBehaviour
    {
        private readonly Queue<Action> _pending = new Queue<Action>();
        private Lobby? _currentLobby;
        private Lobby[] _discovered = new Lobby[0];
        private RelaySession _relay;
        private LocalSelfTest _localSelfTest = new LocalSelfTest();
        private ProgressSnapshot _progress;
        private Vector2 _progressScroll;
        private Vector2 _claimScroll;
        private Vector2 _deckScroll;
        private readonly Dictionary<int, string> _cardNames = new Dictionary<int, string>();
        private string _cardSearch = "";
        private int _selectedDeckUnitIndex;
        private int _rightTab;
        private SteamId _hostId;
        private string _joinId = "";
        private string _status = "Press F9 to open the room panel.";
        private bool _isHost;
        private bool _busy;
        private bool _visible;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            SteamFriends.OnGameLobbyJoinRequested += HandleInvite;
            SteamMatchmaking.OnLobbyMemberLeave += HandleMemberLeft;
            SteamMatchmaking.OnLobbyMemberDisconnected += HandleMemberDisconnected;
        }

        private void OnDestroy()
        {
            SteamFriends.OnGameLobbyJoinRequested -= HandleInvite;
            SteamMatchmaking.OnLobbyMemberLeave -= HandleMemberLeft;
            SteamMatchmaking.OnLobbyMemberDisconnected -= HandleMemberDisconnected;
            _localSelfTest.Stop();
            LeaveRoom();
        }

        private void Update()
        {
            if (Input.GetKeyDown(KeyCode.F9))
            {
                _visible = !_visible;
            }

            while (true)
            {
                Action action;
                lock (_pending)
                {
                    if (_pending.Count == 0)
                    {
                        break;
                    }
                    action = _pending.Dequeue();
                }
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    _busy = false;
                    _status = "Room operation failed: " + exception.Message;
                    Debug.LogError("[RuinaCoop] Room operation failed: " + exception);
                }
            }

            if (_relay != null)
            {
                _relay.Tick();
            }
            _localSelfTest.Tick();
        }

        private void OnGUI()
        {
            if (!_visible)
            {
                return;
            }

            GUILayout.BeginArea(new Rect(20, 20, 460, 590), GUI.skin.box);
            GUILayout.Label("Ruina Coop - room prototype (F9 to hide)");
            GUILayout.Label("Steam: " + (SteamClient.IsValid ? "ready" : "unavailable"));
            GUILayout.Label(_status);

            if (_currentLobby.HasValue)
            {
                var room = _currentLobby.Value;
                GUILayout.Label("Room ID: " + room.Id);
                GUILayout.Label("Members: " + room.MemberCount + "/" + room.MaxMembers);
                GUILayout.Label(_isHost ? "You are the host" : "Host: " + _hostId);
                foreach (var member in room.Members)
                {
                    GUILayout.Label("  " + member.Name + " (" + member.Id + ")");
                }

                if (_isHost && GUILayout.Button("Invite Steam friends"))
                {
                    SteamFriends.OpenGameInviteOverlay(room.Id);
                }
                if (_isHost)
                {
                    GUI.enabled = _progress != null && !_localSelfTest.Running;
                    if (GUILayout.Button("Run local snapshot + loopback self-test"))
                    {
                        _localSelfTest.Start(_progress, room.Id.Value);
                    }
                    GUI.enabled = true;
                    GUILayout.Label("Local self-test: " + _localSelfTest.Status);
                    if (_relay != null)
                    {
                        if (GUILayout.Button("Test Steam room messages"))
                        {
                            _relay.StartLobbyEchoTest();
                        }
                        GUILayout.Label("Room message test: " + _relay.LobbyEchoStatus);
                    }
                }
                if (GUILayout.Button("Leave room"))
                {
                    LeaveRoom();
                    _status = "Left the room.";
                }
            }
            else
            {
                GUI.enabled = SteamClient.IsValid && !_busy;
                if (GUILayout.Button("Create public room (max 5)"))
                {
                    CreateRoom(true);
                }
                if (GUILayout.Button("Create friends-only room (max 5)"))
                {
                    CreateRoom(false);
                }

                GUILayout.BeginHorizontal();
                _joinId = GUILayout.TextField(_joinId, 32);
                if (GUILayout.Button("Join ID", GUILayout.Width(100)))
                {
                    JoinId(_joinId);
                }
                GUILayout.EndHorizontal();

                if (GUILayout.Button("Refresh public rooms"))
                {
                    RefreshRooms();
                }
                GUI.enabled = true;

                if (_discovered.Length == 0)
                {
                    GUILayout.Label("No public rooms listed.");
                }
                else
                {
                    for (var i = 0; i < _discovered.Length && i < 10; i++)
                    {
                        var room = _discovered[i];
                        GUILayout.BeginHorizontal();
                        GUILayout.Label(room.Id + " (" + room.MemberCount + "/" + room.MaxMembers + ")");
                        GUI.enabled = SteamClient.IsValid && !_busy;
                        if (GUILayout.Button("Join", GUILayout.Width(70)))
                        {
                            JoinRoom(room.Id);
                        }
                        GUI.enabled = true;
                        GUILayout.EndHorizontal();
                    }
                }
            }

            GUILayout.EndArea();
            if (_currentLobby.HasValue)
            {
                DrawProgressPanel();
            }
        }

        private void DrawProgressPanel()
        {
            GUILayout.BeginArea(new Rect(500, 20, 550, 590), GUI.skin.box);
            _rightTab = GUILayout.Toolbar(_rightTab, new[] { "Host progress", "Role claims", "Deck editor" });
            GUILayout.Label(_relay == null ? "Relay unavailable." : _relay.Status);
            if (_progress == null)
            {
                if (_isHost)
                {
                    GUILayout.Label("Load a library save to share host progress.");
                }
                else if (_relay != null && _relay.Status.StartsWith("Host progress relay disconnected:",
                    StringComparison.Ordinal))
                {
                    GUILayout.Label("Host connection closed; leave and rejoin after checking both logs.");
                }
                else
                {
                    GUILayout.Label("Waiting for host progress and room verification.");
                }
                GUILayout.EndArea();
                return;
            }

            if (_rightTab == 1)
            {
                DrawClaimsPanel();
                GUILayout.EndArea();
                return;
            }
            if (_rightTab == 2)
            {
                DrawDeckPanel();
                GUILayout.EndArea();
                return;
            }

            GUILayout.Label("Chapter " + _progress.Chapter + " | Library level " +
                _progress.LibraryLevel + " | Snapshot " + _progress.Sequence);
            GUILayout.Label("Selected next stage: " +
                (_progress.SelectedStageId == 0 ? "none" : _progress.SelectedStageId.ToString()));
            GUILayout.Label("Opened floors and librarians:");
            foreach (var floor in _progress.Floors)
            {
                GUILayout.Label("  " + floor.Sephirah + " Lv." + floor.Level + ": " +
                    string.Join(", ", floor.Units.ToArray()));
            }

            GUILayout.Label(_isHost ? "Choose the next stage (planning only):" : "Host-available stages:");
            _progressScroll = GUILayout.BeginScrollView(_progressScroll);
            foreach (var stage in _progress.Stages)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(stage.Id + " [" + stage.State + "] " + stage.Name);
                if (_isHost && GUILayout.Button("Select", GUILayout.Width(65)))
                {
                    if (_relay != null && _relay.SelectStage(stage.Id))
                    {
                        _status = "Selected stage " + stage.Id + " for the room.";
                    }
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private void DrawClaimsPanel()
        {
            // Host actions can synchronously replace _progress. Keep roster and
            // owner indices from one snapshot for the whole IMGUI pass.
            var snapshot = _progress;
            GUILayout.Label("Reception preparation (planning only)");
            GUILayout.Label("Selected stage: " +
                (snapshot.SelectedStageId == 0 ? "none" : snapshot.SelectedStageId.ToString()));
            GUILayout.Label("Claim revision: " + snapshot.ClaimRevision);
            GUILayout.Label(_relay == null ? "Relay unavailable." : _relay.ClaimStatus);
            if (snapshot.SelectedStageId == 0)
            {
                GUILayout.Label("The host must choose a stage on the Host progress tab.");
                return;
            }

            _claimScroll = GUILayout.BeginScrollView(_claimScroll);
            if (_isHost)
            {
                GUILayout.Label("Host: choose one opened floor for this reception:");
                foreach (var floor in snapshot.Floors)
                {
                    if (floor.Units.Count == 0)
                    {
                        continue;
                    }
                    if (GUILayout.Button("Use " + floor.Sephirah + " (" + floor.Units.Count +
                        " librarians)"))
                    {
                        _relay.SelectFloor((byte)floor.Sephirah);
                    }
                }
            }

            ProgressSnapshot.FloorEntry selected = null;
            foreach (var floor in snapshot.Floors)
            {
                if ((byte)floor.Sephirah == snapshot.SelectedFloorId)
                {
                    selected = floor;
                    break;
                }
            }
            if (selected == null)
            {
                GUILayout.Label("Waiting for the host to choose a floor.");
                GUILayout.EndScrollView();
                return;
            }

            GUILayout.Label("Selected floor: " + selected.Sephirah);
            var localId = SteamClient.SteamId.Value;
            for (var i = 0; i < selected.Units.Count; i++)
            {
                var owner = snapshot.ClaimOwners[i];
                GUILayout.BeginHorizontal();
                GUILayout.Label((i + 1) + ". " + selected.Units[i] + " — " + OwnerLabel(owner));
                if (_relay != null && !snapshot.DecksFrozen && (owner == 0 || owner == localId))
                {
                    var action = owner == 0 ? ClaimAction.Claim : ClaimAction.Release;
                    if (GUILayout.Button(action.ToString(), GUILayout.Width(75)))
                    {
                        _relay.RequestClaim((byte)i, action);
                    }
                }
                GUILayout.EndHorizontal();
            }
            if (_isHost && _relay != null && !snapshot.DecksFrozen &&
                GUILayout.Button("Release claims of disconnected members"))
            {
                _relay.ReleaseAbsentClaims();
            }
            GUILayout.EndScrollView();
        }

        private void DrawDeckPanel()
        {
            GUILayout.Label("Host deck inventory (standard vanilla decks)");
            GUILayout.Label(_relay == null ? "Relay unavailable." : _relay.DeckStatus);
            if (_progress.SelectedFloorId == PrepClaims.NoFloor ||
                _progress.UnitDecks.Count == 0)
            {
                GUILayout.Label("Choose a stage and floor on the other tabs first.");
                return;
            }
            ProgressSnapshot.FloorEntry floor = null;
            foreach (var candidate in _progress.Floors)
            {
                if ((byte)candidate.Sephirah == _progress.SelectedFloorId)
                {
                    floor = candidate;
                    break;
                }
            }
            if (floor == null)
            {
                GUILayout.Label("Waiting for selected floor data.");
                return;
            }
            if (_selectedDeckUnitIndex >= floor.Units.Count)
            {
                _selectedDeckUnitIndex = 0;
            }
            GUILayout.Label("Floor " + floor.Sephirah + " | Deck revision " + _progress.DeckRevision);
            for (var i = 0; i < floor.Units.Count; i++)
            {
                var label = (i == _selectedDeckUnitIndex ? "> " : "") +
                    (i + 1) + ". " + floor.Units[i] + " — " + OwnerLabel(_progress.ClaimOwners[i]);
                if (GUILayout.Button(label))
                {
                    _selectedDeckUnitIndex = i;
                }
            }
            if (_selectedDeckUnitIndex >= _progress.UnitDecks.Count)
            {
                return;
            }
            var deck = _progress.UnitDecks[_selectedDeckUnitIndex];
            var owner = _progress.ClaimOwners[_selectedDeckUnitIndex];
            var mayEdit = owner != 0 && owner == SteamClient.SteamId.Value &&
                !_progress.DecksFrozen && !deck.Fixed && !deck.MultiDeck &&
                _relay != null && !_relay.DeckRequestPending;
            if (_progress.DecksFrozen)
            {
                GUILayout.Label("Decks are frozen while a reception is in progress.");
            }
            GUILayout.Label(deck.Fixed || deck.MultiDeck
                ? "This special key page is view only in this test build."
                : owner == 0 ? "Claim this librarian before editing."
                : mayEdit ? "You can edit this librarian's deck."
                : "Only the claimant can edit this deck.");
            GUILayout.Label("Deck cards: " + deck.Cards.Count + "/" + deck.Capacity);
            _deckScroll = GUILayout.BeginScrollView(_deckScroll);
            foreach (var cardId in deck.Cards.ToArray())
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(CardName(cardId) + " (#" + cardId + ")");
                if (mayEdit && GUILayout.Button("Remove", GUILayout.Width(75)))
                {
                    _relay.RequestDeckEdit((byte)_selectedDeckUnitIndex, cardId, DeckAction.Remove);
                    GUILayout.EndHorizontal();
                    break;
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.Label("Host-owned inventory (available copies):");
            _cardSearch = GUILayout.TextField(_cardSearch ?? "", 64);
            var shown = 0;
            foreach (var card in _progress.CardStock)
            {
                var name = CardName(card.Id);
                if (_cardSearch.Length != 0 &&
                    name.IndexOf(_cardSearch, StringComparison.OrdinalIgnoreCase) < 0 &&
                    card.Id.ToString().IndexOf(_cardSearch, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                GUILayout.BeginHorizontal();
                GUILayout.Label(name + " (#" + card.Id + ") x" + card.Count);
                if (mayEdit && GUILayout.Button("Add", GUILayout.Width(75)))
                {
                    _relay.RequestDeckEdit((byte)_selectedDeckUnitIndex, card.Id, DeckAction.Add);
                    GUILayout.EndHorizontal();
                    break;
                }
                GUILayout.EndHorizontal();
                shown++;
                if (shown >= 50)
                {
                    GUILayout.Label("Showing first 50 matches. Narrow the search to see more.");
                    break;
                }
            }
            GUILayout.EndScrollView();
        }

        private string CardName(int cardId)
        {
            string name;
            if (_cardNames.TryGetValue(cardId, out name))
            {
                return name;
            }
            try
            {
                var card = ItemXmlDataList.instance.GetCardItem(new LorId(cardId), false);
                name = card == null || string.IsNullOrEmpty(card.Name)
                    ? "Card " + cardId : card.Name;
            }
            catch (Exception)
            {
                name = "Card " + cardId;
            }
            _cardNames[cardId] = name;
            return name;
        }

        private string OwnerLabel(ulong owner)
        {
            if (owner == 0)
            {
                return "unclaimed";
            }
            if (_currentLobby.HasValue)
            {
                foreach (var member in _currentLobby.Value.Members)
                {
                    if (member.Id.Value == owner)
                    {
                        return member.Name;
                    }
                }
            }
            return owner + " (left room)";
        }

        private void OnProgressSnapshot(ProgressSnapshot snapshot)
        {
            _progress = snapshot;
        }

        private void CreateRoom(bool isPublic)
        {
            if (!SteamClient.IsValid || _busy || _currentLobby.HasValue)
            {
                return;
            }

            _busy = true;
            _status = "Creating room...";
            CreateRoomAsync(isPublic);
        }

        private async void CreateRoomAsync(bool isPublic)
        {
            try
            {
                var result = await SteamMatchmaking.CreateLobbyAsync(ProtocolInfo.MaxPlayers);
                Enqueue(() =>
                {
                    _busy = false;
                    if (!result.HasValue)
                    {
                        _status = "Steam could not create a room.";
                        return;
                    }

                    var room = result.Value;
                    RelaySession relay;
                    try
                    {
                        relay = RelaySession.StartHost(room, Enqueue, OnProgressSnapshot);
                    }
                    catch (Exception exception)
                    {
                        room.Leave();
                        _status = "Relay listener failed: " + exception.Message;
                        Debug.LogError("[RuinaCoop] Relay listener failed: " + exception);
                        return;
                    }
                    var configured = room.SetData(ProtocolInfo.ProtocolKey, ProtocolInfo.Version) &&
                        room.SetData(ProtocolInfo.GameHashKey, ProtocolInfo.GameHash) &&
                        room.SetData(ProtocolInfo.HostKey, SteamClient.SteamId.ToString()) &&
                        room.SetData(ProtocolInfo.StateKey, "lobby") &&
                        room.SetJoinable(true);
                    var published = configured && (isPublic ? room.SetPublic() : room.SetFriendsOnly());
                    if (!published)
                    {
                        relay.Stop();
                        room.Leave();
                        _status = "Room created but its settings could not be published.";
                        return;
                    }

                    _currentLobby = room;
                    _hostId = SteamClient.SteamId;
                    _isHost = true;
                    _relay = relay;
                    _status = "Room ready. Share its ID or invite a friend.";
                    Debug.Log("[RuinaCoop] Created room " + room.Id + ", max " + room.MaxMembers + ".");
                });
            }
            catch (Exception exception)
            {
                Enqueue(() =>
                {
                    _busy = false;
                    _status = "Create failed: " + exception.Message;
                    Debug.LogError("[RuinaCoop] Create lobby failed: " + exception);
                });
            }
        }

        private void JoinId(string text)
        {
            ulong value;
            if (!ulong.TryParse(text, out value) || value == 0)
            {
                _status = "Enter a valid numeric Steam room ID.";
                return;
            }

            JoinRoom((SteamId)value);
        }

        private void JoinRoom(SteamId id)
        {
            if (!SteamClient.IsValid || _busy || _currentLobby.HasValue)
            {
                return;
            }

            _busy = true;
            _status = "Joining room...";
            JoinRoomAsync(id);
        }

        private async void JoinRoomAsync(SteamId id)
        {
            var room = new Lobby(id);
            try
            {
                var result = await room.Join();
                Enqueue(() =>
                {
                    _busy = false;
                    if (result != RoomEnter.Success)
                    {
                        _status = "Join failed: " + result;
                        return;
                    }

                    if (room.GetData(ProtocolInfo.ProtocolKey) != ProtocolInfo.Version ||
                        room.GetData(ProtocolInfo.GameHashKey) != ProtocolInfo.GameHash)
                    {
                        room.Leave();
                        _status = "Room protocol or game version does not match.";
                        return;
                    }

                    ulong hostValue;
                    if (!ulong.TryParse(room.GetData(ProtocolInfo.HostKey), out hostValue) || hostValue == 0)
                    {
                        room.Leave();
                        _status = "Room host metadata is invalid.";
                        return;
                    }
                    _currentLobby = room;
                    _hostId = (SteamId)hostValue;
                    _isHost = false;
                    try
                    {
                        _relay = RelaySession.StartGuest(room, _hostId, Enqueue, OnProgressSnapshot);
                    }
                    catch (Exception exception)
                    {
                        LeaveRoom();
                        _status = "Host relay connection failed: " + exception.Message;
                        Debug.LogError("[RuinaCoop] Guest relay failed: " + exception);
                        return;
                    }
                    _status = "Joined room.";
                    Debug.Log("[RuinaCoop] Joined room " + room.Id + ".");
                });
            }
            catch (Exception exception)
            {
                Enqueue(() =>
                {
                    _busy = false;
                    _status = "Join failed: " + exception.Message;
                    Debug.LogError("[RuinaCoop] Join lobby failed: " + exception);
                });
            }
        }

        private void RefreshRooms()
        {
            if (!SteamClient.IsValid || _busy || _currentLobby.HasValue)
            {
                return;
            }

            _busy = true;
            _status = "Searching public rooms...";
            RefreshRoomsAsync();
        }

        private async void RefreshRoomsAsync()
        {
            try
            {
                var query = SteamMatchmaking.LobbyList
                    .WithKeyValue(ProtocolInfo.ProtocolKey, ProtocolInfo.Version)
                    .WithSlotsAvailable(1)
                    .WithMaxResults(30)
                    .FilterDistanceWorldwide();
                var rooms = await query.RequestAsync();
                Enqueue(() =>
                {
                    _busy = false;
                    _discovered = rooms ?? new Lobby[0];
                    _status = "Found " + _discovered.Length + " public room(s).";
                });
            }
            catch (Exception exception)
            {
                Enqueue(() =>
                {
                    _busy = false;
                    _status = "Search failed: " + exception.Message;
                    Debug.LogError("[RuinaCoop] Search lobbies failed: " + exception);
                });
            }
        }

        private void LeaveRoom()
        {
            _localSelfTest.Stop();
            _localSelfTest = new LocalSelfTest();
            if (_relay != null)
            {
                _relay.Stop();
                _relay = null;
            }
            _progress = null;
            var room = _currentLobby;
            _currentLobby = null;
            _hostId = default(SteamId);
            _isHost = false;
            if (!room.HasValue || !SteamClient.IsValid)
            {
                return;
            }

            try
            {
                room.Value.Leave();
                Debug.Log("[RuinaCoop] Left room " + room.Value.Id + ".");
            }
            catch (Exception exception)
            {
                Debug.LogError("[RuinaCoop] Leave lobby failed: " + exception);
            }
        }

        private void HandleInvite(Lobby room, SteamId friend)
        {
            Enqueue(() =>
            {
                if (!_currentLobby.HasValue)
                {
                    _visible = true;
                    JoinRoom(room.Id);
                }
            });
        }

        private void HandleMemberLeft(Lobby room, Friend member)
        {
            HandleDeparture(room, member);
        }

        private void HandleMemberDisconnected(Lobby room, Friend member)
        {
            HandleDeparture(room, member);
        }

        private void HandleDeparture(Lobby room, Friend member)
        {
            Enqueue(() =>
            {
                if (!_isHost && _currentLobby.HasValue &&
                    _currentLobby.Value.Id == room.Id && member.Id == _hostId)
                {
                    LeaveRoom();
                    _status = "Host left; room closed.";
                }
            });
        }

        private void Enqueue(Action action)
        {
            lock (_pending)
            {
                _pending.Enqueue(action);
            }
        }
    }
}
