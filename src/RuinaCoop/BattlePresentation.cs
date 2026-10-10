using System;
using System.Linq;
using UnityEngine;
using Steamworks;

namespace RuinaCoop
{
    // A read-only session view of authoritative runtime DTOs. No guest battle
    // model, library model, native scene, or single-player stage is created.
    internal static class BattlePresentation
    {
        private static Vector2 _scroll;
        internal static void Draw(RelaySession session)
        {
            if (session == null || !session.BattleActive) return;
            var enabled = GUI.enabled;
            GUILayout.BeginArea(new Rect(15, 15, Math.Max(350, Screen.width - 30), Math.Max(250, Screen.height - 30)), GUI.skin.box);
            try
            {
                GUI.enabled = true;
                GUILayout.Label("联机首幕 · 初始化验证");
                GUILayout.Label(session.BattleStatus);
                GUILayout.Label("本版本停在首幕，不开放出牌、开始本幕或胜负结算。验证完成后请退出游戏再重新开始。");
                var manifest = session.BattleConfiguration;
                if (manifest != null) GUILayout.Label("尹事务所 · " + ((SephirahType)manifest.FloorId) + " · 两位馆员");
                if (session.IsHost && !session.BattleCommitted && GUILayout.Button("取消开始，返回准备")) session.CancelBattleInitialization();
                var state = session.InitialBattleState;
                if (state == null) { GUILayout.Label("等待房主生成完整首幕状态…"); return; }
                GUILayout.Label("第 " + state.Round + " 幕 · " + (session.BattleInitialized ? "双方状态一致" : "正在确认状态") + " · 地图 " + state.Map);
                _scroll = GUILayout.BeginScrollView(_scroll);
                try
                {
                    foreach (var actor in state.Actors)
                    {
                        var librarian = manifest.Librarians.Find(row => row.ActorId == actor.ActorId);
                        var enemy = manifest.Enemies.Find(row => row.ActorId == actor.ActorId);
                        var name = librarian == null ? enemy.Name : librarian.Name;
                        var controller = librarian == null ? "敌方" : librarian.ControllerId == SteamClient.SteamId.Value ? "我控制" : "另一位玩家控制";
                        GUILayout.Label(name + " · " + controller + " · 单位 " + actor.ActorId);
                        GUILayout.Label("体力 " + actor.Hp.ToString("R") + "/" + actor.MaxHp + " · 混乱 " + actor.BreakGauge + "/" + actor.MaxBreakGauge +
                            " · 光芒 " + actor.PlayPoint + "/" + actor.MaxPlayPoint + "（预留 " + actor.ReservedPlayPoint + "） · 情绪 " + actor.EmotionLevel);
                        GUILayout.Label("速度骰：" + string.Join(" / ", actor.SpeedDice.Select((die, i) => (i + 1) + ": " + die.Value + (die.Broken ? "（损坏）" : "")).ToArray()));
                        foreach (BattleCardZone zone in Enum.GetValues(typeof(BattleCardZone)))
                        {
                            var cards = actor.CardsInZone(zone).ToArray();
                            var zoneName = zone == BattleCardZone.Hand ? "手牌" : zone == BattleCardZone.Deck ? "牌库" : zone == BattleCardZone.Used ? "已用" : zone == BattleCardZone.Discarded ? "弃牌" : "保留";
                            GUILayout.Label(zoneName + "（" + cards.Length + "）：" + string.Join("，", cards.Select(card => CardName(card.CardId) + " #" + card.InstanceId + " 费用" + card.CurrentCost).ToArray()));
                        }
                        foreach (var intent in actor.Intent)
                        {
                            var card = actor.Cards.Find(row => row.InstanceId == intent.CardInstanceId);
                            var target = manifest.Librarians.Find(row => row.ActorId == intent.TargetActorId);
                            GUILayout.Label("敌方意图 · 骰槽" + (intent.Slot + 1) + "：" + CardName(card.CardId) + " #" + card.InstanceId + " → " + target.Name + " 骰槽" + (intent.TargetSlot + 1));
                        }
                        GUILayout.Space(14);
                    }
                }
                finally { GUILayout.EndScrollView(); }
            }
            finally { GUI.enabled = enabled; GUILayout.EndArea(); }
        }
        private static string CardName(int id)
        {
            try { var xml = ItemXmlDataList.instance.GetCardItem(new LorId(id), false); return xml == null ? id.ToString() : xml.Name; }
            catch { return id.ToString(); }
        }
    }
}
