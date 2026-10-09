using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using LOR_DiceSystem;
using RuinaCoop;

internal static class Program
{
    private static int _checks;

    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new InvalidOperationException(message);
    }

    private static object Invoke(string method, params object[] arguments)
    {
        return typeof(DeckGuard).GetMethod(method, BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, arguments);
    }

    private static UnitDataModel MakeUnit(out DeckModel deck)
    {
        InventoryModel.Instance = new InventoryModel();
        InventoryModel.Instance.Items.Add(new DiceCardItemModel(new DiceCardXmlInfo(10), 2));
        InventoryModel.Instance.Items.Add(new DiceCardItemModel(new DiceCardXmlInfo(20), 0));
        deck = new DeckModel();
        deck.GetCardList_nocopy().Add(InventoryModel.Instance.Items[1].Card);
        return new UnitDataModel { bookItem = new BookModel(deck) };
    }

    private static void CheckOriginal(DeckModel deck, List<DiceCardXmlInfo> cardList,
        List<DiceCardItemModel> inventoryList, DiceCardItemModel first, DiceCardItemModel second)
    {
        Check(ReferenceEquals(deck.GetCardList_nocopy(), cardList), "Deck list identity changed during rollback.");
        Check(cardList.Count == 1 && ReferenceEquals(cardList[0], second.Card), "Deck content did not roll back.");
        Check(ReferenceEquals(InventoryModel.Instance.Items, inventoryList), "Inventory list identity changed during rollback.");
        Check(inventoryList.Count == 2 && ReferenceEquals(inventoryList[0], first) && ReferenceEquals(inventoryList[1], second), "Inventory order/entry identity did not roll back.");
        Check(first.num == 2 && second.num == 0, "Inventory counts did not roll back.");
    }

    private static void PrefixChecks()
    {
        DeckModel deck;
        var unit = MakeUnit(out deck);
        DeckGuard.Session = new RelaySession();
        foreach (var item in new[] { Tuple.Create("Unit", (object)unit), Tuple.Create("Book", (object)unit.bookItem), Tuple.Create("Deck", (object)deck) })
        {
            var addArgs = new object[] { item.Item2, CardEquipState.Equippable };
            Check(!(bool)Invoke(item.Item1 + "AddPrefix", addArgs), "Non-owner add prefix allowed original.");
            Check((CardEquipState)addArgs[1] == CardEquipState.ERROR, "Non-owner add prefix returned success.");
            var removeArgs = new object[] { item.Item2, true };
            Check(!(bool)Invoke(item.Item1 + "BoolPrefix", removeArgs), "Non-owner remove prefix allowed original.");
            Check(!(bool)removeArgs[1], "Non-owner bool prefix returned success.");
            Check(!(bool)Invoke(item.Item1 + "VoidPrefix", item.Item2), "Non-owner void prefix allowed original.");
        }
        var list = new UI.UIEquipDeckCardList { currentunit = unit };
        var panel = new UI.UILibrarianEquipDeckPanel { _unitdata = unit };
        var slot = new UI.UIInvenEquipPageSlot { _bookDataModel = unit.bookItem };
        UI.UIController.Instance.CurrentUnit = unit;
        Check(!(bool)Invoke("UiUnitFieldPrefix", list, null, new object[0]), "UI currentunit bypassed non-owner lock.");
        Check(!(bool)Invoke("UiUnitFieldPrefix", panel, null, new object[0]), "UI _unitdata bypassed non-owner lock.");
        Check(!(bool)Invoke("UiBookFieldPrefix", slot), "UI inherited book field bypassed lock.");
        Check(!(bool)Invoke("UiCurrentUnitPrefix"), "UI current unit bypassed lock.");
        Check(!(bool)Invoke("UiEquipBookPrefix", slot), "UI book equipment bypassed lock.");
        Check(!(bool)Invoke("UiDeckApplyPrefix", unit), "Saved deck application bypassed lock.");
        Check(!(bool)Invoke("UiDeleteUnitPrefix", unit), "Unit card deletion bypassed lock.");
        Check(!(bool)Invoke("UiDeleteBookPrefix", unit.bookItem), "Book card deletion bypassed lock.");

        DeckGuard.Session.OwnerMayEdit = true;
        Check((bool)Invoke("UnitVoidPrefix", unit), "Owner cannot edit unit.");
        Check((bool)Invoke("BookVoidPrefix", unit.bookItem), "Owner cannot edit book.");
        Check((bool)Invoke("DeckVoidPrefix", deck), "Owner cannot edit deck.");
        NativeDeckModels.IsConstructingMirrors = true;
        Check(!(bool)Invoke("UnitVoidPrefix", unit) && !(bool)Invoke("BookVoidPrefix", unit.bookItem) &&
            !(bool)Invoke("DeckVoidPrefix", deck), "Host display construction ran vanilla default equipment/deck mutations.");
        Check(!DeckGuard.RunAuthorized(() => (bool)Invoke("UnitVoidPrefix", unit)), "Host authorization bypassed display construction isolation.");
        NativeDeckModels.IsConstructingMirrors = false;
        NativeDeckModels.MirrorUnit = unit;
        NativeDeckModels.MirrorBook = unit.bookItem;
        NativeDeckModels.MirrorDeck = deck;
        Check(!(bool)Invoke("UnitVoidPrefix", unit), "Host mirror unit was treated as a real editable unit.");
        Check(!(bool)Invoke("BookVoidPrefix", unit.bookItem), "Host mirror book was treated as a real editable book.");
        Check(!(bool)Invoke("DeckVoidPrefix", deck), "Host mirror deck was treated as a real editable deck.");
        Check(!DeckGuard.RunAuthorized(() => (bool)Invoke("DeckVoidPrefix", deck)), "Host authorization escaped into a display mirror.");
        NativeDeckEditor.ConsumeClick = true;
        Check(!(bool)Invoke("UiUnitFieldPrefix", list, null, new object[0]), "Routed native card click also ran vanilla mutation.");
        NativeDeckEditor.ConsumeClick = false;
        NativeEquipmentEditor.ConsumeClick = true;
        Check(!(bool)Invoke("UiEquipBookPrefix", slot), "Routed core page click also ran vanilla equipment mutation.");
        NativeEquipmentEditor.ConsumeClick = false;
        NativeDeckModels.MirrorUnit = null;
        NativeDeckModels.MirrorBook = null;
        NativeDeckModels.MirrorDeck = null;
        DeckGuard.Session.PreparationFrozen = true;
        Check(!(bool)Invoke("UnitVoidPrefix", unit), "Frozen preparation still editable.");

        DeckGuard.Session = new RelaySession { IsGuestSession = true };
        var saveArgs = new object[] { true };
        Check(!(bool)Invoke("SaveBoolPrefix", saveArgs) && !(bool)saveArgs[0], "Guest save did not return false.");
        Check(!(bool)Invoke("SaveVoidPrefix"), "Guest platform save allowed.");
        var callbackCalled = false;
        var callbackResult = true;
        Action<bool> callback = result => { callbackCalled = true; callbackResult = result; };
        Check(!(bool)Invoke("SaveCorePrefix", callback), "Guest concrete writer allowed.");
        Check(callbackCalled && !callbackResult, "Suppressed writer did not complete callback with false.");
        Check(!(bool)Invoke("GuestBattlePrefix"), "Guest single-player battle allowed.");
        DeckGuard.Session.IsGuestSession = false;
        Check(!(bool)Invoke("GuestBattlePrefix"), "Host entered unsynchronized battle before stage 4.");
        DeckGuard.Session.IsActive = false;
        Check((bool)Invoke("GuestBattlePrefix"), "Closed room blocked single-player battle.");
        DeckGuard.Session.IsActive = true;
        DeckGuard.Session.IsGuestSession = true;
        Check(!(bool)Invoke("UnitVoidPrefix", unit), "Guest changed local library.");

        DeckGuard.Session = null;
        Check((bool)Invoke("UnitVoidPrefix", unit), "Single-player edits remained locked.");
        Check((bool)Invoke("SaveVoidPrefix"), "Single-player save remained locked.");
        Check((bool)Invoke("GuestBattlePrefix"), "Single-player battle remained blocked.");
    }

    private static void AuthorizationChecks()
    {
        DeckModel deck;
        var unit = MakeUnit(out deck);
        DeckGuard.Session = new RelaySession();
        Check(DeckGuard.RunAuthorized(() =>
        {
            Check((bool)Invoke("UnitVoidPrefix", unit), "Authorized edit blocked.");
            Check(DeckGuard.RunAuthorized(() => (bool)Invoke("DeckVoidPrefix", deck)), "Nested authorization blocked.");
            return (bool)Invoke("BookVoidPrefix", unit.bookItem);
        }), "Outer authorization lost after nested call.");
        Check(!(bool)Invoke("UnitVoidPrefix", unit), "Authorization escaped its scope.");
        try
        {
            DeckGuard.RunAuthorized<int>(() => DeckGuard.RunAuthorized<int>(() => { throw new InvalidOperationException("injected"); }));
            throw new Exception("Expected injected authorization failure.");
        }
        catch (InvalidOperationException) { }
        Check(!(bool)Invoke("DeckVoidPrefix", deck), "Exception leaked authorization.");
        DeckGuard.Session.IsGuestSession = true;
        var guestRejected = false;
        try { DeckGuard.RunAuthorized(() => 1); }
        catch (InvalidOperationException) { guestRejected = true; }
        Check(guestRejected, "Guest acquired host authorization.");
        DeckGuard.Session.IsGuestSession = false;
        DeckGuard.Session.PreparationFrozen = true;
        var frozenRejected = false;
        try { DeckGuard.RunAuthorized(() => 1); }
        catch (InvalidOperationException) { frozenRejected = true; }
        Check(frozenRejected, "Frozen session acquired edit authorization.");

        DeckGuard.Session = new RelaySession { IsGuestSession = true };
        var loadArgs = new object[] { false };
        Invoke("LibraryLoadPrefix", loadArgs);
        Check((bool)loadArgs[0] && (bool)Invoke("UnitVoidPrefix", unit), "Guest Continue load was blocked.");
        var injected = new InvalidOperationException("load failure");
        Check(ReferenceEquals(Invoke("LibraryLoadFinalizer", injected, loadArgs[0]), injected), "Load finalizer swallowed error.");
        Check(!(bool)Invoke("UnitVoidPrefix", unit), "Load failure leaked model initialization permission.");
    }

    private static void TransactionChecks()
    {
        foreach (var failure in new[] { "add", "remove", "publish", "publish-throw", "reject-after-change", "new-inventory-item" })
        {
            DeckModel deck;
            var unit = MakeUnit(out deck);
            var cardList = deck.GetCardList_nocopy();
            var inventoryList = InventoryModel.Instance.Items;
            var first = inventoryList[0];
            var second = inventoryList[1];
            DeckGuard.Session = new RelaySession();
            deck.ThrowAfterInventoryRemoval = failure == "add";
            deck.ThrowAfterDeckRemoval = failure == "remove";
            deck.ReturnRejectedAfterMutation = failure == "reject-after-change";
            var action = failure == "remove" || failure == "new-inventory-item" ? DeckAction.Remove : DeckAction.Add;
            Func<bool> publish = () => true;
            if (failure == "publish") publish = () => false;
            if (failure == "publish-throw") publish = () => { throw new InvalidOperationException("publishing"); };
            if (failure == "new-inventory-item")
            {
                // Remove creates and sorts a previously absent inventory entry.
                inventoryList.Remove(second);
                publish = () => false;
            }
            byte vanillaState;
            var result = DeckGuard.EditWithRollback(unit, new LorId(action == DeckAction.Add ? 10 : 20), action, publish, out vanillaState);
            Check(result == (failure == "reject-after-change" ? DeckResultCode.VanillaRejected : DeckResultCode.Failed), "Transaction failure result mismatch: " + failure);
            if (failure == "new-inventory-item")
            {
                Check(inventoryList.Count == 1 && ReferenceEquals(inventoryList[0], first) && first.num == 2,
                    "New inventory item survived rollback.");
                Check(cardList.Count == 1 && ReferenceEquals(cardList[0], second.Card), "New-entry removal did not restore deck.");
            }
            else CheckOriginal(deck, cardList, inventoryList, first, second);
            Check(!(bool)Invoke("UnitVoidPrefix", unit), "Failed transaction leaked authorization.");
        }

        DeckModel acceptedDeck;
        var acceptedUnit = MakeUnit(out acceptedDeck);
        DeckGuard.Session = new RelaySession();
        byte state;
        var published = false;
        Check(DeckGuard.EditWithRollback(acceptedUnit, new LorId(10), DeckAction.Add,
            () => { published = true; return true; }, out state) == DeckResultCode.Accepted, "Valid add was not committed.");
        Check(published && state == (byte)CardEquipState.Equippable, "Accepted add was not published correctly.");
        Check(acceptedDeck.GetCardList_nocopy().Count == 2 && InventoryModel.Instance.Items[0].num == 1, "Accepted add did not transfer card.");
        Check(DeckGuard.EditWithRollback(acceptedUnit, new LorId(10), DeckAction.Remove, () => true, out state) == DeckResultCode.Accepted,
            "Valid remove was not committed.");
        Check(acceptedDeck.GetCardList_nocopy().Count == 1 && InventoryModel.Instance.Items[0].num == 2, "Accepted remove did not return inventory.");

        acceptedDeck.InfiniteCard = new DiceCardXmlInfo(30, true);
        Check(DeckGuard.EditWithRollback(acceptedUnit, new LorId(30), DeckAction.Add, () => true, out state) == DeckResultCode.Accepted,
            "Basic infinite card rejected.");
        Check(InventoryModel.Instance.Items.Count == 2 && InventoryModel.Instance.Items[0].num == 2,
            "Basic infinite card deducted real inventory.");
        Check(DeckGuard.EditWithRollback(acceptedUnit, new LorId(999), DeckAction.Remove, () => true, out state) == DeckResultCode.VanillaRejected,
            "Missing-card removal was not rejected.");
    }

    private static void Main()
    {
        var harmony = new Harmony();
        DeckGuard.Install(harmony);
        Check(harmony.Prefixes.Count >= 50 && harmony.Finalizers.Count == 1, "Guard registration is incomplete.");
        var latestSave = typeof(GameSave.SaveManager).GetMethod("SaveLatestData", new[] { typeof(LatestDataModel) });
        Check(harmony.Prefixes.ContainsKey(latestSave) && harmony.Prefixes[latestSave].Method.Name == "SaveVoidPrefix",
            "latest.dat direct progress save is not guarded.");
        foreach (var prefix in harmony.Prefixes.Values) Check(prefix.Method != null, "Null Harmony prefix registered.");
        PrefixChecks();
        AuthorizationChecks();
        TransactionChecks();
        DeckGuard.Session = null;
        Console.WriteLine("PASS: DeckGuard prefixes, guest saves, authorization scopes, rollback, and infinite inventory; " + _checks + " checks.");
        Console.WriteLine("Model stubs and Harmony registration are tested; Unity/Harmony runtime and two-player behavior still require game validation.");
    }
}
