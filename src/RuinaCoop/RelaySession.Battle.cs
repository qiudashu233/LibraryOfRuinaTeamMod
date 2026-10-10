using System;
using System.Linq;
using System.Threading;
using Steamworks;
using Steamworks.Data;
using UnityEngine;

namespace RuinaCoop
{
    internal sealed partial class RelaySession
    {
        private static long _battleSerial = DateTime.UtcNow.Ticks;
        private BattleStartCoordinator _battle = new BattleStartCoordinator();
        private BattleMessage _battleOffer, _battleStateMessage, _battleTerminal;
        private BattleManifest _battleManifest;
        private BattleInitialState _battleInitialState;
        private byte[] _battleStateDigest;
        private float _nextBattleBroadcast, _terminalUntil;
        private bool _validatingBattleConfiguration, _guestBattleCommitted;
        private BattleStartPhase _guestBattlePhase;
        private ulong _lastGuestBattleId;
        private float _guestBattleDeadline;
        internal string BattleStatus { get; private set; } = "4A：尹事务所、两位馆员、基础书页、无被动。";
        internal BattleManifest BattleConfiguration { get { return _battleManifest; } }
        internal BattleInitialState InitialBattleState { get { return _battleInitialState; } }
        internal bool BattleActive { get { return _isHost ? _battle.Phase != BattleStartPhase.Idle && _battle.Phase != BattleStartPhase.Cancelled :
            _guestBattlePhase != BattleStartPhase.Idle && _guestBattlePhase != BattleStartPhase.Cancelled; } }
        internal bool BattleCommitted { get { return _isHost ? _battle.Committed : _guestBattleCommitted; } }
        internal bool BattlePaused { get { return _battleInitialState != null || (_isHost ? _battle.Frozen : _guestBattlePhase == BattleStartPhase.Frozen); } }
        internal bool BattleInitialized { get { return _isHost ? _battle.Phase == BattleStartPhase.Initialized : _guestBattlePhase == BattleStartPhase.Initialized; } }

        internal bool RequestBattleStart()
        {
            if (!_isHost || _stopped || BattleActive || NativeDeckEditor.Active || DeckRequestPending || PreparationReadyPending) return false;
            try
            {
                if (!CaptureAndBroadcast() || !_preparation.AllControllersReady)
                    throw new InvalidOperationException("所有实际控制者必须确认当前配置就绪。");
                var snapshot = LatestSnapshot;
                var manifest = BattleManifestCodec.FromPreparation(snapshot, _hostId.Value, NativeBattleBridge.GetInvitationBooks());
                string reason;
                if (!NativeBattleStateAdapter.ValidateManifest(manifest, out reason)) throw new InvalidOperationException(reason);
                if (manifest.Librarians.Any(row => row.ControllerId != _hostId.Value && !_guests.ContainsKey(row.ControllerId)))
                    throw new InvalidOperationException("控制者连接尚未验证。");
                var payload = BattleManifestCodec.Encode(manifest);
                var battleId = (ulong)Interlocked.Increment(ref _battleSerial);
                if (!_battle.Begin(RoomId, battleId, snapshot.Preparation.ContextId, snapshot.Preparation.Revision,
                    BattleProtocol.ComputeDigest(payload), _hostId.Value, manifest.Librarians.Select(row => row.ControllerId), Time.realtimeSinceStartup, 60))
                    throw new InvalidOperationException("无法创建本次初始化会话。");
                _battleManifest = manifest; _battleInitialState = null; _battleStateDigest = null; _battleStateMessage = null;
                _battleOffer = BattleEnvelope(BattleMessageType.Offer, _battle.Identity.ManifestDigest, payload);
                _room.SetJoinable(false);
                NativePreparation.Close();
                BattleStatus = "出战配置已冻结，等待控制者确认…";
                BroadcastBattleCatchup(); _nextBattleBroadcast = Time.realtimeSinceStartup + 2f;
                LogBattle("configuration offered");
                return true;
            }
            catch (Exception error)
            {
                if (BattleActive) FailBattle(error.GetBaseException().Message);
                else BattleStatus = error.GetBaseException().Message;
                Debug.LogWarning("[RuinaCoop] Battle initialization not started: " + BattleStatus);
                return false;
            }
        }
        private void TickBattle()
        {
            if (_stopped) return;
            if (_isHost && Time.realtimeSinceStartup >= _nextBattleBroadcast)
            {
                if (_battleTerminal != null && Time.realtimeSinceStartup < _terminalUntil) BroadcastBattle(_battleTerminal);
                if (BattleActive) BroadcastBattleCatchup();
                _nextBattleBroadcast = Time.realtimeSinceStartup + 2f;
            }
            if (!BattleActive) return;
            if (!_isHost)
            {
                if (_guestBattlePhase == BattleStartPhase.Frozen) return;
                if (!_guestAuthenticated || (_guestBattlePhase != BattleStartPhase.Initialized && _guestBattlePhase != BattleStartPhase.Frozen && Time.realtimeSinceStartup >= _guestBattleDeadline))
                    FailBattle("初始化连接中断或超时；已提交的验证需退出游戏后重新开始。");
                return;
            }
            foreach (var actor in _battleManifest.Librarians)
                if (actor.ControllerId != _hostId.Value && (!_guests.ContainsKey(actor.ControllerId) || !IsLobbyMember((SteamId)actor.ControllerId)))
                    _battle.ParticipantLeft(actor.ControllerId);
            _battle.Tick(Time.realtimeSinceStartup);
            if (_battle.CanReturnToPreparation) { ReturnAfterCancelledBattle(); return; }
            if (_battle.Frozen)
            {
                BattleStatus = "初始化已暂停：" + _battle.FailureReason + "；请退出游戏后重新开始。";
                return;
            }
            if (_battle.CanCommit)
            {
                if (_battle.TryCommit(ValidateFinalBattleConfiguration, Time.realtimeSinceStartup))
                {
                    BroadcastBattleCatchup();
                    BattleStatus = "双方配置确认完成，房主正在初始化首幕…";
                    LogBattle("committed");
                    try { NativeBattleBridge.StartHost(this); }
                    catch (Exception error) { FailBattle("原版初始化失败：" + error.GetBaseException().Message); }
                }
                else if (_battle.CanReturnToPreparation) ReturnAfterCancelledBattle();
            }
        }
        private bool ValidateFinalBattleConfiguration()
        {
            _validatingBattleConfiguration = true;
            try
            {
                if (!CaptureAndBroadcast() || LatestSnapshot.Preparation.ContextId != _battle.Identity.PreparationContext ||
                    LatestSnapshot.Preparation.Revision != _battle.Identity.PreparationRevision || !_preparation.AllControllersReady) return false;
                var current = BattleManifestCodec.FromPreparation(LatestSnapshot, _hostId.Value, NativeBattleBridge.GetInvitationBooks());
                string reason;
                return NativeBattleStateAdapter.ValidateManifest(current, out reason) &&
                    BattleProtocol.DigestEquals(BattleProtocol.ComputeDigest(BattleManifestCodec.Encode(current)), _battle.Identity.ManifestDigest);
            }
            finally { _validatingBattleConfiguration = false; }
        }
        internal void OnNativeBattleInitialBoundary()
        {
            if (!_isHost || !_battle.Committed || _battleInitialState != null || _battle.Frozen) return;
            try
            {
                var state = NativeBattleStateAdapter.Capture(_battleManifest);
                string reason;
                if (!BattleInitialStateCodec.ValidateAgainstManifest(state, _battleManifest, out reason)) throw new InvalidOperationException(reason);
                var payload = BattleInitialStateCodec.Encode(state);
                BattleInitialState decoded;
                if (!BattleInitialStateCodec.TryDecode(payload, out decoded)) throw new InvalidOperationException("首幕状态本地解码失败。");
                _battleInitialState = decoded; _battleStateDigest = BattleProtocol.ComputeDigest(payload);
                if (!_battle.PublishInitialState(_battleStateDigest, Time.realtimeSinceStartup)) throw new InvalidOperationException("首幕状态确认期限已结束。");
                _battle.AcknowledgeState(_hostId.Value, BattleEnvelope(BattleMessageType.StateAck, _battleStateDigest), Time.realtimeSinceStartup);
                _battleStateMessage = BattleEnvelope(BattleMessageType.InitialState, _battleStateDigest, payload);
                BroadcastBattleCatchup();
                BattleStatus = "首幕已暂停，等待客端校验单位、手牌和速度骰…";
                LogBattle("initial state " + BattleInitialStateCodec.Digest(payload));
            }
            catch (Exception error) { FailBattle("首幕状态不支持：" + error.GetBaseException().Message); }
        }
        internal void FailBattle(string reason)
        {
            if (!BattleActive) return;
            if (_isHost)
            {
                _battle.Fail(reason);
                if (_battle.CanReturnToPreparation) ReturnAfterCancelledBattle();
                else { BattleStatus = "初始化已暂停：" + reason; BroadcastBattle(BattleEnvelope(BattleMessageType.Failed, _battle.Identity.ManifestDigest)); }
            }
            else
            {
                _guestBattlePhase = BattleStartPhase.Frozen; BattleStatus = "初始化已暂停：" + reason;
                SendBattleToHost(BattleEnvelope(BattleMessageType.Failed, _battleOffer.Digest));
            }
            LogBattle("failed: " + reason);
        }
        internal void CancelBattleInitialization()
        {
            if (_isHost && BattleActive && !_battle.Committed) { _battle.Cancel("房主取消开始。"); ReturnAfterCancelledBattle(); }
        }
        private void ReturnAfterCancelledBattle()
        {
            RememberTerminal(BattleEnvelope(BattleMessageType.Cancel, _battle.Identity.ManifestDigest));
            _battleManifest = null; _battleInitialState = null; _battleStateDigest = null; _battleStateMessage = null;
            _preparation.ClearReadiness(); _room.SetJoinable(true);
            BattleStatus = "开始已取消：" + _battle.FailureReason + "；请重新确认就绪。";
            CaptureAndBroadcast();
            LogBattle("cancelled before commit");
        }
        internal void EndBattleVerification()
        {
            if (!_isHost || !BattleActive) return;
            if (!_battle.Committed) { CancelBattleInitialization(); return; }
            if (!NativeBattleBridge.EndVerification(this)) { FailBattle("此4A验证版需退出游戏后重新开始，不执行胜负或奖励结算。"); return; }
            RememberTerminal(BattleEnvelope(BattleMessageType.Finished, _battle.Identity.ManifestDigest));
            _battle = new BattleStartCoordinator(); _battleOffer = null;
            _battleManifest = null; _battleInitialState = null; _battleStateDigest = null; _battleStateMessage = null;
            _preparation.ClearReadiness(); _room.SetJoinable(true);
            BattleStatus = "初始化验证已结束，未执行战斗胜负或奖励结算。";
            CaptureAndBroadcast();
        }
        private void StopBattle()
        {
            if (_isHost && BattleActive)
            {
                if (_battle.Committed && !NativeBattleBridge.EndVerification(this))
                    Debug.LogError("[RuinaCoop] Battle verification cleanup failed while leaving; native battle remains frozen. Exit the game before continuing.");
                else BroadcastBattle(BattleEnvelope(BattleMessageType.Cancel, _battle.Identity.ManifestDigest));
            }
            _battleInitialState = null;
        }
        private bool HandleBattleGuestPacket(Connection connection, ulong sender, byte[] bytes)
        {
            if (!BattleProtocol.IsBattlePacket(bytes)) return false;
            BattleMessage message;
            if (!BattleProtocol.TryDecode(bytes, RoomId, out message) || !BattleActive) return true;
            if (message.Type == BattleMessageType.OfferAck) _battle.AcknowledgeOffer(sender, message, Time.realtimeSinceStartup);
            else if (message.Type == BattleMessageType.StateAck && _battle.AcknowledgeState(sender, message, Time.realtimeSinceStartup) && BattleInitialized)
            {
                BattleStatus = "首幕状态校验通过；4A验证完成，出牌与结算将在4B/4C接入。";
                BroadcastBattleCatchup();
                LogBattle("all controlling players validated initial state");
            }
            else if (message.Type == BattleMessageType.Failed && _battleManifest.Librarians.Any(row => row.ControllerId == sender) &&
                _battle.Identity.Matches(message, _battle.Identity.ManifestDigest)) FailBattle("控制者未能接受或校验初始化。");
            return true;
        }
        private void ReceiveBattleMessage(GuestRelayConnection source, byte[] bytes)
        {
            PostGuest(source, () =>
            {
                if (_stopped || _isHost || !_guestAuthenticated) return;
                BattleMessage message;
                if (!BattleProtocol.TryDecode(bytes, RoomId, out message)) { if (BattleActive) FailBattle("无效战斗数据包。"); return; }
                if (message.Type == BattleMessageType.Offer) { AcceptBattleOffer(message); return; }
                if (!MatchesGuestBattle(message)) return;
                if (message.Type == BattleMessageType.Commit && BattleProtocol.DigestEquals(message.Digest, _battleOffer.Digest))
                {
                    if (_guestBattlePhase == BattleStartPhase.Offering)
                    { _guestBattleCommitted = true; _guestBattlePhase = BattleStartPhase.Committed; _guestBattleDeadline = Time.realtimeSinceStartup + 60f; BattleStatus = "房主正在初始化首幕，等待权威状态…"; }
                }
                else if (message.Type == BattleMessageType.InitialState && (_guestBattlePhase == BattleStartPhase.Committed || _guestBattlePhase == BattleStartPhase.AwaitingStateAcks || _guestBattlePhase == BattleStartPhase.Initialized))
                {
                    BattleInitialState state; string reason;
                    if (!BattleInitialStateCodec.TryDecode(message.Payload, out state) || !BattleInitialStateCodec.ValidateAgainstManifest(state, _battleManifest, out reason))
                    { FailBattle("首幕状态与出战配置不一致。"); return; }
                    if (_battleStateDigest != null && !BattleProtocol.DigestEquals(_battleStateDigest, message.Digest)) { FailBattle("同一次首幕状态发生变化。"); return; }
                    if (_battleStateDigest == null) _guestBattleDeadline = Time.realtimeSinceStartup + 60f;
                    _battleInitialState = state; _battleStateDigest = message.Digest;
                    if (_guestBattlePhase != BattleStartPhase.Initialized)
                    { _guestBattlePhase = BattleStartPhase.AwaitingStateAcks; BattleStatus = "首幕已显示，等待双方摘要确认…"; }
                    SendBattleToHost(BattleEnvelope(BattleMessageType.StateAck, _battleStateDigest));
                }
                else if (message.Type == BattleMessageType.Initialized && _battleInitialState != null && BattleProtocol.DigestEquals(message.Digest, _battleStateDigest) && _guestBattlePhase == BattleStartPhase.AwaitingStateAcks)
                { _guestBattlePhase = BattleStartPhase.Initialized; BattleStatus = "首幕状态校验通过；4A验证完成，等待4B/4C出牌同步。"; LogBattle("guest confirmed shared initial state"); }
                else if (message.Type == BattleMessageType.Finished && BattleProtocol.DigestEquals(message.Digest, _battleOffer.Digest)) ClearGuestBattle("房主已结束初始化验证。");
                else if (message.Type == BattleMessageType.Cancel && !_guestBattleCommitted && BattleProtocol.DigestEquals(message.Digest, _battleOffer.Digest)) ClearGuestBattle("房主已取消开始，请重新就绪。");
                else if (message.Type == BattleMessageType.Failed && BattleProtocol.DigestEquals(message.Digest, _battleOffer.Digest))
                { _guestBattlePhase = BattleStartPhase.Frozen; BattleStatus = "房主已暂停初始化；本次验证请退出游戏后重新开始。"; }
            });
        }
        private void AcceptBattleOffer(BattleMessage offer)
        {
            if (BattleActive)
            {
                if (MatchesGuestBattle(offer) && _guestBattlePhase == BattleStartPhase.Offering && BattleProtocol.DigestEquals(offer.Digest, _battleOffer.Digest))
                    SendBattleToHost(BattleEnvelope(BattleMessageType.OfferAck, offer.Digest));
                return;
            }
            if (offer.BattleSessionId <= _lastGuestBattleId) return;
            _lastGuestBattleId = offer.BattleSessionId;
            _battleOffer = offer; _guestBattlePhase = BattleStartPhase.Offering; _guestBattleCommitted = false; _guestBattleDeadline = Time.realtimeSinceStartup + 60f;
            try
            {
                BattleManifest manifest; string reason;
                if (!BattleManifestCodec.TryDecode(offer.Payload, out manifest) || manifest.HostId != _hostId.Value || !NativeBattleStateAdapter.ValidateManifest(manifest, out reason))
                    throw new InvalidOperationException("本次配置不在4A支持范围。");
                if (LatestSnapshot == null || LatestSnapshot.Preparation.ContextId != offer.PreparationContext || LatestSnapshot.Preparation.Revision != offer.PreparationRevision ||
                    DeckRequestPending || PreparationReadyPending || NativeDeckEditor.Active)
                    throw new InvalidOperationException("准备配置已过期或配装尚未完成。");
                var local = BattleManifestCodec.FromPreparation(LatestSnapshot, _hostId.Value, manifest.InvitationBooks);
                if (!BattleProtocol.DigestEquals(BattleProtocol.ComputeDigest(BattleManifestCodec.Encode(local)), offer.Digest))
                    throw new InvalidOperationException("双方出战配置不一致。");
                _battleManifest = manifest; _battleInitialState = null; _battleStateDigest = null;
                NativePreparation.Close();
                SendBattleToHost(BattleEnvelope(BattleMessageType.OfferAck, offer.Digest));
                BattleStatus = "已确认出战配置，等待房主初始化首幕…";
                LogBattle("guest accepted configuration");
            }
            catch (Exception error) { FailBattle(error.GetBaseException().Message); }
        }
        private bool MatchesGuestBattle(BattleMessage message)
        { return _battleOffer != null && message.BattleSessionId == _battleOffer.BattleSessionId && message.PreparationContext == _battleOffer.PreparationContext && message.PreparationRevision == _battleOffer.PreparationRevision; }
        private void ClearGuestBattle(string status)
        { _guestBattlePhase = BattleStartPhase.Idle; _guestBattleCommitted = false; _battleInitialState = null; _battleManifest = null; _battleStateDigest = null; _battleOffer = null; BattleStatus = status; }
        private BattleMessage BattleEnvelope(BattleMessageType type, byte[] digest, byte[] payload = null)
        {
            var identity = _isHost ? _battle.Identity : null;
            return new BattleMessage { Type = type, RoomId = RoomId, BattleSessionId = identity == null ? _battleOffer.BattleSessionId : identity.BattleSessionId,
                PreparationContext = identity == null ? _battleOffer.PreparationContext : identity.PreparationContext,
                PreparationRevision = identity == null ? _battleOffer.PreparationRevision : identity.PreparationRevision, Digest = digest, Payload = payload };
        }
        private void RememberTerminal(BattleMessage message)
        { _battleTerminal = message; _terminalUntil = Time.realtimeSinceStartup + 60f; BroadcastBattle(message); }
        private void BroadcastBattleCatchup()
        {
            if (_battleOffer == null) return;
            BroadcastBattle(_battleOffer);
            if (_battle.Committed) BroadcastBattle(BattleEnvelope(BattleMessageType.Commit, _battle.Identity.ManifestDigest));
            if (_battleStateMessage != null) BroadcastBattle(_battleStateMessage);
            if (BattleInitialized) BroadcastBattle(BattleEnvelope(BattleMessageType.Initialized, _battleStateDigest));
            if (_battle.Frozen) BroadcastBattle(BattleEnvelope(BattleMessageType.Failed, _battle.Identity.ManifestDigest));
        }
        private void BroadcastBattle(BattleMessage message)
        {
            var packet = BattleProtocol.Encode(message);
            foreach (var connection in _guests.Values)
                try { connection.SendMessage(packet, SendType.Reliable); }
                catch (Exception error) { Debug.LogWarning("[RuinaCoop] Battle send failed: " + error.GetType().Name); }
        }
        private void SendBattleToHost(BattleMessage message)
        {
            if (_guestConnection == null) return;
            try { _guestConnection.Connection.SendMessage(BattleProtocol.Encode(message), SendType.Reliable); }
            catch (Exception error) { Debug.LogWarning("[RuinaCoop] Battle acknowledgment send failed: " + error.GetType().Name); }
        }
        private void LogBattle(string detail)
        { Debug.Log("[RuinaCoop] Battle init " + (_battleOffer == null ? 0 : _battleOffer.BattleSessionId) + ": " + detail + "."); }
    }
}
