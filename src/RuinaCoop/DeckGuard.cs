using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LOR_DiceSystem;
using UnityEngine;

namespace RuinaCoop
{
    // Prefixes are installed against the verified vanilla method signatures. A
    // guest edits DTOs in F9 only; its loaded library never becomes the mirror.
    internal static class DeckGuard
    {
        [ThreadStatic]
        private static int _authorizedDepth;
        [ThreadStatic]
        private static int _libraryLoadDepth;

        private static readonly FieldInfo CurrentDeckField = typeof(BookModel).GetField(
            "_deck", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static RelaySession Session { get; set; }

        internal static T RunAuthorized<T>(Func<T> action)
        {
            if (action == null)
            {
                throw new ArgumentNullException("action");
            }
            var session = Session;
            if (session != null && (session.IsGuestSession || session.PreparationFrozen))
            {
                throw new InvalidOperationException("The session cannot modify host preparation now.");
            }
            _authorizedDepth++;
            try
            {
                return action();
            }
            finally
            {
                _authorizedDepth--;
            }
        }

        // DeckModel removes inventory before adding to the deck, and removes
        // from the deck before returning inventory. An exception between these
        // steps must restore both collections in place (including item counts).
        // SaveData loading is deliberately avoided: vanilla migrates card IDs.
        internal static DeckResultCode EditWithRollback(UnitDataModel unit, LorId card,
            DeckAction action, Func<bool> publish, out byte vanillaState)
        {
            vanillaState = (byte)CardEquipState.ERROR;
            if (unit == null || unit.bookItem == null || CurrentDeckField == null ||
                (action != DeckAction.Add && action != DeckAction.Remove))
            {
                return DeckResultCode.InvalidRequest;
            }

            var deck = CurrentDeckField.GetValue(unit.bookItem) as DeckModel;
            if (deck == null)
            {
                return DeckResultCode.NotReady;
            }
            var cards = deck.GetCardList_nocopy();
            var inventory = InventoryModel.Instance.GetCardListOrigin();
            if (cards == null || inventory == null)
            {
                return DeckResultCode.NotReady;
            }

            var originalCards = new List<DiceCardXmlInfo>(cards);
            var originalInventory = new List<DiceCardItemModel>(inventory);
            var originalCounts = new int[originalInventory.Count];
            for (var i = 0; i < originalInventory.Count; i++)
            {
                originalCounts[i] = originalInventory[i].num;
            }

            try
            {
                var accepted = false;
                if (action == DeckAction.Add)
                {
                    var state = RunAuthorized(() => unit.AddCardFromInventory(card));
                    vanillaState = (byte)state;
                    accepted = state == CardEquipState.Equippable;
                }
                else
                {
                    accepted = RunAuthorized(() => unit.MoveCardToInventory(card));
                    vanillaState = (byte)(accepted ? CardEquipState.Equippable : CardEquipState.ERROR);
                }
                if (!accepted)
                {
                    Restore(cards, originalCards, inventory, originalInventory, originalCounts);
                    return DeckResultCode.VanillaRejected;
                }
                if (publish != null && !publish())
                {
                    Restore(cards, originalCards, inventory, originalInventory, originalCounts);
                    return DeckResultCode.Failed;
                }
                return DeckResultCode.Accepted;
            }
            catch (Exception exception)
            {
                Restore(cards, originalCards, inventory, originalInventory, originalCounts);
                Debug.LogError("[RuinaCoop] Deck edit rolled back: " + exception);
                return DeckResultCode.Failed;
            }
        }

        private static void Restore(List<DiceCardXmlInfo> cards, List<DiceCardXmlInfo> originalCards,
            List<DiceCardItemModel> inventory, List<DiceCardItemModel> originalInventory,
            int[] originalCounts)
        {
            cards.Clear();
            cards.AddRange(originalCards);
            inventory.Clear();
            for (var i = 0; i < originalInventory.Count; i++)
            {
                originalInventory[i].num = originalCounts[i];
                inventory.Add(originalInventory[i]);
            }
        }

        internal static void Install(Harmony harmony)
        {
            // Joining from the title screen must not break Continue. This is
            // the game's own disk-to-memory load, never a network DTO load.
            var loadLibrary = typeof(LibraryModel).GetMethod("LoadFromSaveData",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, new[] { typeof(GameSave.SaveData) }, null);
            if (loadLibrary == null)
            {
                throw new MissingMethodException("Missing verified vanilla library load target.");
            }
            harmony.Patch(loadLibrary,
                prefix: new HarmonyMethod(typeof(DeckGuard).GetMethod("LibraryLoadPrefix", BindingFlags.Static | BindingFlags.NonPublic)),
                finalizer: new HarmonyMethod(typeof(DeckGuard).GetMethod("LibraryLoadFinalizer", BindingFlags.Static | BindingFlags.NonPublic)));

            Patch(harmony, typeof(UnitDataModel), "AddCardFromInventory", new[] { typeof(LorId) }, "UnitAddPrefix");
            Patch(harmony, typeof(UnitDataModel), "AddCardInDeckFromInventory", new[] { typeof(DiceCardXmlInfo) }, "UnitAddPrefix");
            Patch(harmony, typeof(UnitDataModel), "MoveCardToInventory", new[] { typeof(LorId) }, "UnitBoolPrefix");
            Patch(harmony, typeof(UnitDataModel), "RemoveCardInDeck", new[] { typeof(DiceCardXmlInfo) }, "UnitBoolPrefix");
            Patch(harmony, typeof(UnitDataModel), "EquipBook", new[] { typeof(BookModel), typeof(bool), typeof(bool) }, "UnitBoolPrefix");
            Patch(harmony, typeof(UnitDataModel), "EquipBookForUI", new[] { typeof(BookModel), typeof(bool), typeof(bool) }, "UnitBoolPrefix");
            Patch(harmony, typeof(UnitDataModel), "EquipCustomCoreBook", new[] { typeof(BookModel) }, "UnitVoidPrefix");
            foreach (var name in new[] { "EmptyDeckToInventory", "EmptyDeckToInventoryAll", "ReEquipDeck", "CreateDeckByDeckInfo" })
            {
                Patch(harmony, typeof(UnitDataModel), name, Type.EmptyTypes, "UnitVoidPrefix");
            }

            Patch(harmony, typeof(BookModel), "AddCardFromInventoryToCurrentDeck", new[] { typeof(LorId) }, "BookAddPrefix");
            Patch(harmony, typeof(BookModel), "MoveCardFromCurrentDeckToInventory", new[] { typeof(LorId) }, "BookBoolPrefix");
            Patch(harmony, typeof(BookModel), "ChangeDeck", new[] { typeof(int) }, "BookVoidPrefix");
            foreach (var name in new[] { "EmptyDeckToInventory", "EmptyDeckToInventoryAll", "CreateDeckByDeckInfo", "ApplyPassiveSuccession" })
            {
                Patch(harmony, typeof(BookModel), name, Type.EmptyTypes, "BookVoidPrefix");
            }

            Patch(harmony, typeof(DeckModel), "AddCardFromInventory", new[] { typeof(LorId) }, "DeckAddPrefix");
            Patch(harmony, typeof(DeckModel), "MoveCardToInventory", new[] { typeof(LorId) }, "DeckBoolPrefix");
            Patch(harmony, typeof(DeckModel), "SetDeck", new[] { typeof(LorId) }, "DeckVoidPrefix");
            Patch(harmony, typeof(DeckModel), "AddCardForLoading", new[] { typeof(LorId) }, "DeckVoidPrefix");
            Patch(harmony, typeof(DeckModel), "LoadFromSaveData", new[] { typeof(GameSave.SaveData) }, "DeckVoidPrefix");
            foreach (var name in new[] { "EmptyDeckToInventory", "RemoveAllErrorCard" })
            {
                Patch(harmony, typeof(DeckModel), name, Type.EmptyTypes, "DeckVoidPrefix");
            }

            var game = typeof(UnitDataModel).Assembly;
            var uiDeckList = RequiredType(game, "UI.UIEquipDeckCardList");
            foreach (var name in new[] { "OnClickCardSlotByDeck", "RemoveCardSlot" })
            {
                Patch(harmony, uiDeckList, name, new[] { RequiredType(game, "UI.UIOriginCardSlot") }, "UiUnitFieldPrefix");
            }
            foreach (var name in new[] { "OnClickCardSlotByInven", "InsertCardSlot" })
            {
                Patch(harmony, uiDeckList, name, new[] { RequiredType(game, "UI.UIInvenCardSlot") }, "UiUnitFieldPrefix");
            }
            Patch(harmony, uiDeckList, "OnChangeDeckTab", Type.EmptyTypes, "UiUnitFieldPrefix");
            Patch(harmony, RequiredType(game, "UI.UILibrarianEquipDeckPanel"), "OnClickClearDeckButton", Type.EmptyTypes, "UiUnitFieldPrefix");
            Patch(harmony, RequiredType(game, "UI.UIDeckCardList"), "SetDeckCheck", new[] { typeof(DeckModel), typeof(UnitDataModel) }, "UiDeckApplyPrefix");
            Patch(harmony, RequiredType(game, "UI.UIDeckInfoPopup"), "OnclickApplyDeckButton", Type.EmptyTypes, "UiCurrentUnitPrefix");

            var deletePanel = RequiredType(game, "UI.UICardEquipInfoPanel");
            Patch(harmony, deletePanel, "DeleteCardFrom", new[] { typeof(UnitDataModel) }, "UiDeleteUnitPrefix");
            Patch(harmony, deletePanel, "DeleteCardFrom", new[] { typeof(BookModel) }, "UiDeleteBookPrefix");
            foreach (var typeName in new[] { "UI.UIInvenEquipPageSlot", "UI.UIInvenLeftEquipPageSlot", "UI.UISettingInvenEquipPageSlot", "UI.UISettingInvenEquipPageLeftSlot" })
            {
                var type = RequiredType(game, typeName);
                Patch(harmony, type, "OnClickEquipButton", Type.EmptyTypes, "UiEquipBookPrefix");
                Patch(harmony, type, "OnClickEmptyDeckButton", Type.EmptyTypes, "UiBookFieldPrefix");
            }
            foreach (var typeName in new[] { "UI.UILibrarianInfoInCardPhase", "UI.UIBattleSettingLibrarianInfoPanel" })
            {
                Patch(harmony, RequiredType(game, typeName), "OnClickReleaseToggle", Type.EmptyTypes, "UiCurrentUnitPrefix");
            }

            // UICardPanel, battle autosaves and shutdown all reach these entry
            // points. Also guard the concrete writer before it creates files.
            Patch(harmony, typeof(GameSave.SaveManager), "SavePlayData", new[] { typeof(int), typeof(bool) }, "SaveBoolPrefix");
            // SaveLatestData writes latest.dat directly, including during old
            // save migration; it does not pass through the play-save pipeline.
            Patch(harmony, typeof(GameSave.SaveManager), "SaveLatestData", new[] { typeof(LatestDataModel) }, "SaveVoidPrefix");
            Patch(harmony, typeof(PlatformManager), "SavePlayData", new[] { typeof(int), typeof(GameSave.SaveData) }, "SaveVoidPrefix");
            Patch(harmony, RequiredType(game, "PlatformCore_default"), "SavePlayData", new[] { typeof(int), typeof(GameSave.SaveData), typeof(Action<bool>) }, "SaveCorePrefix");
            Patch(harmony, typeof(GlobalGameManager), "LoadBattleScene", Type.EmptyTypes, "GuestBattlePrefix");
            Patch(harmony, RequiredType(game, "UI.UIBattleSettingPanel"), "OnClickBattleStart", Type.EmptyTypes, "GuestBattlePrefix");
        }

        private static Type RequiredType(Assembly assembly, string name)
        {
            var type = assembly.GetType(name);
            if (type == null)
            {
                throw new TypeLoadException("Missing verified vanilla guard type: " + name);
            }
            return type;
        }

        private static void Patch(Harmony harmony, Type type, string name, Type[] parameters, string prefixName)
        {
            var original = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Static |
                BindingFlags.Public | BindingFlags.NonPublic, null, parameters, null);
            var prefix = typeof(DeckGuard).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic);
            if (original == null || prefix == null)
            {
                throw new MissingMethodException("Missing verified vanilla guard target: " + type.FullName + "." + name);
            }
            harmony.Patch(original, prefix: new HarmonyMethod(prefix));
        }

        private static bool MayEdit(UnitDataModel unit)
        {
            return Session == null || _authorizedDepth > 0 || _libraryLoadDepth > 0 || Session.CanEditLocalUnit(unit);
        }

        private static bool MayEdit(BookModel book)
        {
            return Session == null || _authorizedDepth > 0 || _libraryLoadDepth > 0 || Session.CanEditLocalBook(book);
        }

        private static bool MayEdit(DeckModel deck)
        {
            return Session == null || _authorizedDepth > 0 || _libraryLoadDepth > 0 || Session.CanEditLocalDeck(deck);
        }

        private static bool UnitAddPrefix(UnitDataModel __instance, ref CardEquipState __result)
        {
            if (MayEdit(__instance)) return true;
            __result = CardEquipState.ERROR;
            return false;
        }

        private static bool UnitBoolPrefix(UnitDataModel __instance, ref bool __result)
        {
            if (MayEdit(__instance)) return true;
            __result = false;
            return false;
        }

        private static bool UnitVoidPrefix(UnitDataModel __instance) { return MayEdit(__instance); }

        private static bool BookAddPrefix(BookModel __instance, ref CardEquipState __result)
        {
            if (MayEdit(__instance)) return true;
            __result = CardEquipState.ERROR;
            return false;
        }

        private static bool BookBoolPrefix(BookModel __instance, ref bool __result)
        {
            if (MayEdit(__instance)) return true;
            __result = false;
            return false;
        }

        private static bool BookVoidPrefix(BookModel __instance) { return MayEdit(__instance); }

        private static bool DeckAddPrefix(DeckModel __instance, ref CardEquipState __result)
        {
            if (MayEdit(__instance)) return true;
            __result = CardEquipState.ERROR;
            return false;
        }

        private static bool DeckBoolPrefix(DeckModel __instance, ref bool __result)
        {
            if (MayEdit(__instance)) return true;
            __result = false;
            return false;
        }

        private static bool DeckVoidPrefix(DeckModel __instance) { return MayEdit(__instance); }

        private static bool UiUnitFieldPrefix(object __instance)
        {
            if (Session == null) return true;
            var name = __instance.GetType().FullName == "UI.UIEquipDeckCardList" ? "currentunit" : "_unitdata";
            var field = AccessTools.Field(__instance.GetType(), name);
            return field != null && MayEdit(field.GetValue(__instance) as UnitDataModel);
        }

        private static bool UiBookFieldPrefix(object __instance)
        {
            if (Session == null) return true;
            var field = AccessTools.Field(__instance.GetType(), "_bookDataModel");
            return field != null && MayEdit(field.GetValue(__instance) as BookModel);
        }

        private static bool UiCurrentUnitPrefix()
        {
            return Session == null || MayEdit(UI.UIController.Instance.CurrentUnit);
        }

        private static bool UiEquipBookPrefix(object __instance)
        {
            return Session == null || UiCurrentUnitPrefix() && UiBookFieldPrefix(__instance);
        }

        private static bool UiDeckApplyPrefix(UnitDataModel __1) { return MayEdit(__1); }
        private static bool UiDeleteUnitPrefix(UnitDataModel __0) { return MayEdit(__0); }
        private static bool UiDeleteBookPrefix(BookModel __0) { return MayEdit(__0); }

        private static bool SaveBoolPrefix(ref bool __result)
        {
            if (Session == null || !Session.IsGuestSession) return true;
            __result = false;
            return false;
        }

        private static bool SaveVoidPrefix() { return Session == null || !Session.IsGuestSession; }

        private static void LibraryLoadPrefix(out bool __state)
        {
            __state = Session != null && Session.IsGuestSession;
            if (__state) _libraryLoadDepth++;
        }

        private static Exception LibraryLoadFinalizer(Exception __exception, bool __state)
        {
            if (__state) _libraryLoadDepth--;
            return __exception;
        }

        private static bool GuestBattlePrefix()
        {
            if (Session == null || !Session.IsGuestSession) return true;
            Debug.LogWarning("[RuinaCoop] Guest single-player reception is blocked while sharing host preparation.");
            return false;
        }

        private static bool SaveCorePrefix(Action<bool> __2)
        {
            if (Session == null || !Session.IsGuestSession) return true;
            if (__2 != null) __2(false);
            return false;
        }
    }
}
