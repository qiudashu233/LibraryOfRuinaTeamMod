using System;
using System.Collections.Generic;
using System.Reflection;
using LOR_DiceSystem;
using RuinaCoop;

internal static class Program
{
    private static int _checks;
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private sealed class PassiveCapture
    {
        private readonly BookModel _book;
        private readonly BookModel.BookEquipedBookSavedData _origin, _reserved;
        private readonly List<int> _originIds, _reservedIds;
        private readonly int[] _originValues, _reservedValues;
        private readonly int _originReceiver, _reservedReceiver;
        private readonly List<PassiveModel> _active;
        private readonly PassiveModel[] _models;
        private readonly PassiveModel.PassiveModelSavedData[] _passiveOrigins, _passiveReserved;
        private readonly PassiveXmlInfo[] _natural, _currentOrigin, _currentReserved;
        private readonly int[] _instanceIds, _giveOrigins, _giveReserved, _receiveOrigins, _receiveReserved;
        internal PassiveCapture(BookModel book)
        {
            _book = book; _origin = book.originData; _reserved = book.reservedData;
            _originIds = _origin.equipedBookIdListInPassive; _reservedIds = _reserved.equipedBookIdListInPassive;
            _originValues = _originIds.ToArray(); _reservedValues = _reservedIds.ToArray();
            _originReceiver = _origin.equipedPassiveBookInstanceId; _reservedReceiver = _reserved.equipedPassiveBookInstanceId;
            _active = (List<PassiveModel>)Get(book, "_activatedAllPassives"); _models = _active.ToArray();
            var count = _models.Length;
            _passiveOrigins = new PassiveModel.PassiveModelSavedData[count]; _passiveReserved = new PassiveModel.PassiveModelSavedData[count];
            _natural = new PassiveXmlInfo[count]; _currentOrigin = new PassiveXmlInfo[count]; _currentReserved = new PassiveXmlInfo[count];
            _instanceIds = new int[count]; _giveOrigins = new int[count]; _giveReserved = new int[count];
            _receiveOrigins = new int[count]; _receiveReserved = new int[count];
            for (var i = 0; i < count; i++)
            {
                var model = _models[i];
                _passiveOrigins[i] = model.originData; _passiveReserved[i] = model.reservedData;
                _natural[i] = model.originpassive; _currentOrigin[i] = model.originData.currentpassive; _currentReserved[i] = model.reservedData.currentpassive;
                _instanceIds[i] = (int)Get(model, "_bookInstanceId");
                _giveOrigins[i] = model.originData.givePassiveBookId; _giveReserved[i] = model.reservedData.givePassiveBookId;
                _receiveOrigins[i] = model.originData.receivepassivebookId; _receiveReserved[i] = model.reservedData.receivepassivebookId;
            }
        }
        internal void CheckUnchanged()
        {
            Check(ReferenceEquals(_book.originData, _origin) && ReferenceEquals(_book.reservedData, _reserved), "book passive saved-data object references restored");
            Check(ReferenceEquals(_origin.equipedBookIdListInPassive, _originIds) && ReferenceEquals(_reserved.equipedBookIdListInPassive, _reservedIds), "book passive source-list identities restored");
            Check(_origin.equipedPassiveBookInstanceId == _originReceiver && _reserved.equipedPassiveBookInstanceId == _reservedReceiver, "book donor links restored");
            CheckSequence(_originIds, _originValues); CheckSequence(_reservedIds, _reservedValues);
            var active = (List<PassiveModel>)Get(_book, "_activatedAllPassives");
            Check(ReferenceEquals(active, _active) && active.Count == _models.Length, "active passive list identity/count restored");
            for (var i = 0; i < _models.Length; i++)
            {
                var model = _models[i];
                Check(ReferenceEquals(active[i], model), "active passive identity/order restored");
                Check(ReferenceEquals(model.originData, _passiveOrigins[i]) && ReferenceEquals(model.reservedData, _passiveReserved[i]), "passive origin/reserved references restored");
                Check(ReferenceEquals(model.originpassive, _natural[i]) && ReferenceEquals(model.originData.currentpassive, _currentOrigin[i]) && ReferenceEquals(model.reservedData.currentpassive, _currentReserved[i]), "passive XML references restored");
                Check((int)Get(model, "_bookInstanceId") == _instanceIds[i], "passive owning instance restored");
                Check(model.originData.givePassiveBookId == _giveOrigins[i] && model.reservedData.givePassiveBookId == _giveReserved[i] &&
                    model.originData.receivepassivebookId == _receiveOrigins[i] && model.reservedData.receivepassivebookId == _receiveReserved[i], "passive source/recipient ids restored");
            }
        }
        private static void CheckSequence(List<int> actual, int[] expected)
        {
            Check(actual.Count == expected.Length, "passive source-list count restored");
            for (var i = 0; i < expected.Length; i++) Check(actual[i] == expected[i], "passive source-list order restored");
        }
    }
    private sealed class Fixture
    {
        internal BookModel Old = new BookModel(100, 1);
        internal BookModel Target = new BookModel(101, 2);
        internal BookModel Appearance = new BookModel(102, 3);
        internal BookModel Default = new BookModel(200, 0);
        internal UnitDataModel Unit;
        internal DiceCardXmlInfo A = new DiceCardXmlInfo(10);
        internal DiceCardXmlInfo B = new DiceCardXmlInfo(11);
        internal DiceCardXmlInfo Infinite = new DiceCardXmlInfo(12, true);
        internal List<DiceCardItemModel> Inventory;
        internal List<DiceCardItemModel> Items;
        internal int[] Counts;
        internal readonly List<DeckModel> Decks = new List<DeckModel>();
        internal readonly List<List<DiceCardXmlInfo>> Lists = new List<List<DiceCardXmlInfo>>();
        internal readonly List<List<DiceCardXmlInfo>> Originals = new List<List<DiceCardXmlInfo>>();
        internal readonly List<object> DeckCollections = new List<object>();
        internal object BookInventory;
        internal List<BookModel> BookOrder;
        internal readonly List<PassiveCapture> PassiveStates = new List<PassiveCapture>();
        internal Fixture(bool fromDefault = false)
        {
            InventoryModel.Instance = new InventoryModel();
            BookInventoryModel.Instance = new BookInventoryModel();
            LibraryModel.Instance = new LibraryModel();
            NativeDeckModels.Mirror = null; DeckGuard.Denied = false; DeckGuard.Authorized = false;
            Default.ClassInfo.optionList.Add(BookOption.Basic);
            Unit = new UnitDataModel(fromDefault ? null : Old, Default);
            LibraryModel.Instance.Floor.Units.Add(Unit);
            Set(Unit, "_CustomBookItem", Appearance);
            BookInventoryModel.Instance.Register(Old);
            BookInventoryModel.Instance.Register(Target);
            BookInventoryModel.Instance.Register(Appearance);
            Old.GetDeckAll_nocopy()[0].GetCardList_nocopy().Add(B);
            Target.GetDeckAll_nocopy()[0].GetCardList_nocopy().Add(A);
            Target.GetDeckAll_nocopy()[1].GetCardList_nocopy().Add(B);
            Target.GetDeckAll_nocopy()[2].GetCardList_nocopy().Add(Infinite);
            Target.ChangeDeck(2);
            Appearance.GetDeckAll_nocopy()[3].GetCardList_nocopy().Add(B);
            Inventory = InventoryModel.Instance.GetCardListOrigin();
            Inventory.Add(new DiceCardItemModel(B, 4));
            Inventory.Add(new DiceCardItemModel(A, 3));
            Inventory.Add(new DiceCardItemModel(Infinite, 5));
            Items = new List<DiceCardItemModel>(Inventory);
            Counts = new int[Items.Count];
            for (var i = 0; i < Items.Count; i++) Counts[i] = Items[i].num;
            foreach (var book in new[] { Old, Target, Appearance, Default })
            {
                ((List<PassiveModel>)Get(book, "_activatedAllPassives")).Add(new PassiveModel(book.instanceId, new PassiveXmlInfo(1000 + book.BookId.id)));
                PassiveStates.Add(new PassiveCapture(book));
                DeckCollections.Add(Get(book, "_deckList"));
                foreach (var deck in book.GetDeckAll_nocopy())
                {
                    Decks.Add(deck); Lists.Add(deck.GetCardList_nocopy());
                    Originals.Add(new List<DiceCardXmlInfo>(deck.GetCardList_nocopy()));
                }
            }
            BookInventory = Get(BookInventoryModel.Instance, "_bookList");
            BookOrder = BookInventoryModel.Instance.GetBookList_equip();
        }
        internal void Restored(bool fromDefault = false)
        {
            Check(ReferenceEquals(Get(Unit, "_bookItem"), fromDefault ? null : Old), "raw core reference restored");
            Check(ReferenceEquals(Unit.bookItem, fromDefault ? Default : Old), "effective core restored");
            Check(ReferenceEquals(Get(Unit, "_CustomBookItem"), Appearance), "appearance book restored");
            Check(Unit.appearanceType == Gender.M, "appearance mode restored");
            Check(ReferenceEquals(Old.owner, fromDefault ? null : Unit) && Target.owner == null, "both owner references restored");
            Check(Target.instanceId == 2 && Old.instanceId == 1, "instance ids restored");
            Check(ReferenceEquals(Get(Target, "_deck"), Target.GetDeckAll_nocopy()[0]), "current deck pointer restored");
            Check(ReferenceEquals(InventoryModel.Instance.GetCardListOrigin(), Inventory), "card inventory list identity restored");
            Check(Inventory.Count == Items.Count, "card inventory count restored");
            for (var i = 0; i < Items.Count; i++) Check(ReferenceEquals(Inventory[i], Items[i]) && Inventory[i].num == Counts[i], "card item identity/order/count restored");
            Check(ReferenceEquals(Get(BookInventoryModel.Instance, "_bookList"), BookInventory), "book inventory list identity restored");
            var books = BookInventoryModel.Instance.GetBookList_equip();
            Check(books.Count == BookOrder.Count, "book inventory count restored");
            for (var i = 0; i < books.Count; i++) Check(ReferenceEquals(books[i], BookOrder[i]), "book inventory order restored");
            var bookIndex = 0;
            foreach (var book in new[] { Old, Target, Appearance, Default }) Check(ReferenceEquals(Get(book, "_deckList"), DeckCollections[bookIndex++]), "book deck collection identity restored");
            for (var i = 0; i < Decks.Count; i++)
            {
                var cards = Decks[i].GetCardList_nocopy();
                Check(ReferenceEquals(cards, Lists[i]) && cards.Count == Originals[i].Count, "deck list identity/count restored");
                for (var j = 0; j < cards.Count; j++) Check(ReferenceEquals(cards[j], Originals[i][j]), "deck card identity/order restored");
            }
            foreach (var passive in PassiveStates) passive.CheckUnchanged();
            Check(!DeckGuard.Authorized, "authorization finally restored");
        }
    }
    private static int Main()
    {
        try
        {
            string reason;
            var success = new Fixture();
            var published = false;
            Check(EquipmentTransaction.Equip(success.Unit, success.Target, () =>
            { published = true; Check(DeckGuard.Authorized, "publish inside authorization"); Check(success.Unit.appearanceType == Gender.N, "published neutral appearance"); return true; }, out reason) == EquipmentTransactionResult.Accepted,
                "vanilla false return still accepted by postconditions");
            Check(published && ReferenceEquals(success.Unit.bookItem, success.Target) && success.Target.owner == success.Unit && success.Old.owner == null, "successful relationship commit");
            Check(success.Inventory.Find(item => item.Card == success.Infinite).num == 5, "unlimited basic cards do not alter saved stock");
            Check(success.Target.GetDeckAll_nocopy()[0].GetCardList_nocopy()[0] == success.A && !DeckGuard.Authorized, "successful deck retained and authorization restored");
            Check(success.Target.GetDeckAll_nocopy()[0].GetCardList_nocopy().Count == 3 && success.Target.GetDeckAll_nocopy()[1].GetCardList_nocopy().Count == 0 &&
                success.Target.GetDeckAll_nocopy()[2].GetCardList_nocopy().Count == 0, "ordinary ChangeDeck is a no-op and vanilla migration collects stored cards into active deck");

            foreach (var point in new[] { "owned", "returned", "deducted", "completed" })
            {
                var fixture = new Fixture();
                fixture.Unit.Hook = (stage, unit, target) => { if (stage == point) throw new InvalidOperationException(point); };
                Check(EquipmentTransaction.Equip(fixture.Unit, fixture.Target, () => { throw new Exception("must not publish failed edit"); }, out reason) == EquipmentTransactionResult.Failed, "exception rolls back " + point);
                fixture.Restored();
            }
            var reject = new Fixture();
            reject.Unit.Hook = (stage, unit, target) => { if (stage == "completed") Set(unit, "_bookItem", reject.Old); };
            Check(EquipmentTransaction.Equip(reject.Unit, reject.Target, null, out reason) == EquipmentTransactionResult.Rejected, "postcondition rejection rolls back");
            reject.Restored();

            var publishFailure = new Fixture();
            Check(EquipmentTransaction.Equip(publishFailure.Unit, publishFailure.Target, () =>
            {
                Set(InventoryModel.Instance, "_cardList", new List<DiceCardItemModel>());
                Set(BookInventoryModel.Instance, "_bookList", new List<BookModel>());
                Set(publishFailure.Target.GetDeckAll_nocopy()[1], "_deck", new List<DiceCardXmlInfo>());
                Set(publishFailure.Old, "_deckList", new List<DeckModel>());
                publishFailure.Old.instanceId = 666;
                publishFailure.Appearance.GetDeckAll_nocopy()[3].GetCardList_nocopy().Clear();
                return false;
            }, out reason) == EquipmentTransactionResult.Failed, "publish false rolls back replaced collections and all captured books");
            publishFailure.Restored();
            var publishThrow = new Fixture();
            Check(EquipmentTransaction.Equip(publishThrow.Unit, publishThrow.Target, () => { throw new InvalidOperationException("publish"); }, out reason) == EquipmentTransactionResult.Failed, "publish exception rolls back");
            publishThrow.Restored();
            var basic = new Fixture(true);
            Check(EquipmentTransaction.Equip(basic.Unit, basic.Target, () => false, out reason) == EquipmentTransactionResult.Failed, "default-book rollback case");
            basic.Restored(true);
            var basicCommit = new Fixture(true);
            Check(EquipmentTransaction.Equip(basicCommit.Unit, basicCommit.Target, () => true, out reason) == EquipmentTransactionResult.Accepted &&
                ReferenceEquals(basicCommit.Unit.bookItem, basicCommit.Target) && ReferenceEquals(basicCommit.Unit.defaultBook, basicCommit.Default), "own default page can be replaced without rewriting default-book reference");

            foreach (var currentReceiver in new[] { false, true })
            {
                var receiver = new Fixture();
                var source = AddCommittedReceiver(receiver, currentReceiver ? receiver.Old : receiver.Target);
                Check(EquipmentTransaction.CanEquip(receiver.Unit, receiver.Target, out reason), "committed receiver can change equipment");
                Check(EquipmentTransaction.Equip(receiver.Unit, receiver.Target, () => true, out reason) == EquipmentTransactionResult.Accepted, "committed receiver equipment transaction accepted");
                foreach (var saved in receiver.PassiveStates) saved.CheckUnchanged();
                Check(source.owner == null && source.originData.equipedPassiveBookInstanceId == (currentReceiver ? receiver.Old.instanceId : receiver.Target.instanceId), "committed passive donor stays reserved for original recipient");
            }
            var passiveRollback = new Fixture();
            var donor = AddCommittedReceiver(passiveRollback, passiveRollback.Target);
            var donorDeck = donor.GetDeckAll_nocopy()[0];
            var donorCards = donorDeck.GetCardList_nocopy();
            donorCards.Add(passiveRollback.A);
            Check(EquipmentTransaction.Equip(passiveRollback.Unit, passiveRollback.Target, () =>
            {
                foreach (var book in new[] { passiveRollback.Old, passiveRollback.Target, passiveRollback.Appearance, donor }) CorruptPassives(book);
                donor.owner = passiveRollback.Unit; donor.instanceId = 888;
                Set(donorDeck, "_deck", new List<DiceCardXmlInfo>());
                return false;
            }, out reason) == EquipmentTransactionResult.Failed, "publish failure rolls back full committed passive graph");
            passiveRollback.Restored();
            Check(donor.owner == null && donor.instanceId == 20 && ReferenceEquals(donorDeck.GetCardList_nocopy(), donorCards) && donorCards.Count == 1 && ReferenceEquals(donorCards[0], passiveRollback.A), "related donor owner/id/deck restored");

            var donorOwnerReject = new Fixture();
            var ownerSource = AddCommittedReceiver(donorOwnerReject, donorOwnerReject.Target);
            donorOwnerReject.Unit.Hook = (stage, unit, target) => { if (stage == "completed") ownerSource.owner = unit; };
            Check(EquipmentTransaction.Equip(donorOwnerReject.Unit, donorOwnerReject.Target, null, out reason) == EquipmentTransactionResult.Rejected, "unexpected source ownership change fails postcondition");
            donorOwnerReject.Restored(); Check(ownerSource.owner == null, "unexpected source ownership rolled back");

            var repeatedLink = new Fixture();
            var repeatedDonor = AddCommittedReceiver(repeatedLink, repeatedLink.Target);
            repeatedLink.Target.originData.equipedBookIdListInPassive.Add(repeatedDonor.instanceId);
            repeatedLink.Target.reservedData.equipedBookIdListInPassive.Add(repeatedDonor.instanceId);
            repeatedLink.PassiveStates.Clear();
            foreach (var book in new[] { repeatedLink.Old, repeatedLink.Target, repeatedLink.Appearance, repeatedLink.Default, repeatedDonor }) repeatedLink.PassiveStates.Add(new PassiveCapture(book));
            Check(EquipmentTransaction.Equip(repeatedLink.Unit, repeatedLink.Target, () => { CorruptPassives(repeatedDonor); return false; }, out reason) == EquipmentTransactionResult.Failed, "repeated source references and reciprocal cycle are captured once without recursion failure");
            repeatedLink.Restored();

            var equalXml = new Fixture();
            var equalModel = ((List<PassiveModel>)Get(equalXml.Target, "_activatedAllPassives"))[0];
            equalModel.reservedData.currentpassive = new PassiveXmlInfo(equalModel.originData.currentpassive.id.id);
            Check(!EquipmentTransaction.HasPassiveDraft(equalXml.Target) && EquipmentTransaction.CanEquip(equalXml.Unit, equalXml.Target, out reason), "XML clones with same LorId are committed metadata");
            equalModel.originData.currentpassive = null; equalModel.reservedData.currentpassive = null;
            Check(!EquipmentTransaction.HasPassiveDraft(equalXml.Target) && EquipmentTransaction.CanEquip(equalXml.Unit, equalXml.Target, out reason), "both null passive slots match committed state");
            equalModel.reservedData.currentpassive = new PassiveXmlInfo(1000);
            Check(EquipmentTransaction.HasPassiveDraft(equalXml.Target), "only one null passive slot remains an unfinished draft");
            foreach (var duplicate in new[] { false, true })
            {
                var brokenGraph = new Fixture();
                var graphSource = AddCommittedReceiver(brokenGraph, brokenGraph.Target);
                if (duplicate) BookInventoryModel.Instance.Register(new BookModel(501, graphSource.instanceId));
                else { brokenGraph.Target.originData.equipedBookIdListInPassive[0] = 777; brokenGraph.Target.reservedData.equipedBookIdListInPassive[0] = 777; }
                var originalCore = brokenGraph.Unit.bookItem;
                var vanillaCalls = 0; brokenGraph.Unit.Hook = (stage, unit, target) => vanillaCalls++;
                Check(EquipmentTransaction.Equip(brokenGraph.Unit, brokenGraph.Target, null, out reason) == EquipmentTransactionResult.Failed && vanillaCalls == 0 &&
                    ReferenceEquals(brokenGraph.Unit.bookItem, originalCore), "missing or ambiguous passive instance prevents mutation");
            }
            var denied = new Fixture(); DeckGuard.Denied = true;
            Check(EquipmentTransaction.Equip(denied.Unit, denied.Target, null, out reason) == EquipmentTransactionResult.Failed, "authorization boundary failure leaves state untouched");
            denied.Restored();

            Action<Fixture>[] invalid = {
                f => f.Unit.Locked = true,
                f => LibraryModel.Instance.Floor.Units.Clear(),
                f => f.Old.owner = new UnitDataModel(null, f.Default),
                f => f.Target.owner = new UnitDataModel(null, f.Default),
                f => f.Target.Fixed = true,
                f => f.Target.Locked = true,
                f => f.Target.BlueLocked = true,
                f => f.Target.ClassInfo.optionList.Add(BookOption.MultiDeck),
                f => f.Target.ClassInfo.optionList.Add(BookOption.Basic),
                f => f.Target.ClassInfo.canNotEquip = true,
                f => f.Target.ClassInfo.isError = true,
                f => f.Target.BookId.Workshop = true,
                f => f.Target.BookId.id = 250022,
                f => BookInventoryModel.Instance.Black = f.Target,
                f => f.Target.originData.equipedPassiveBookInstanceId = 30,
                f => f.Target.reservedData.equipedPassiveBookInstanceId = 30,
                f => f.Target.originData.equipedBookIdListInPassive.Add(30),
                f => f.Target.reservedData.equipedBookIdListInPassive.Add(30),
                f => ((List<PassiveModel>)Get(f.Target, "_activatedAllPassives"))[0].reservedData.currentpassive = new PassiveXmlInfo(999),
                f => ((List<PassiveModel>)Get(f.Target, "_activatedAllPassives"))[0].reservedData.receivepassivebookId = 99,
                f => ((List<PassiveModel>)Get(f.Target, "_activatedAllPassives"))[0].reservedData.givePassiveBookId = 99,
                f => NativeDeckModels.Mirror = f.Target,
                f => NativeDeckModels.Mirror = f.Unit,
                f => f.Old.Fixed = true,
                f => f.Old.BlueLocked = true,
                f => f.Old.ClassInfo.optionList.Add(BookOption.MultiDeck),
                f => { LibraryModel.Instance.BlueClear = true; LibraryModel.Instance.PlayHistory.Start_TheBlueReverberationPrimaryBattle = 1; },
                f => f.Target.GetDeckAll_nocopy()[3].GetCardList_nocopy().Add(null)
            };
            foreach (var change in invalid)
            {
                var fixture = new Fixture(); change(fixture);
                var calls = 0; fixture.Unit.Hook = (stage, unit, target) => calls++;
                Check(EquipmentTransaction.Equip(fixture.Unit, fixture.Target, () => { throw new Exception("invalid must not publish"); }, out reason) == EquipmentTransactionResult.Rejected, "invalid live state rejected");
                Check(calls == 0 && !string.IsNullOrEmpty(reason), "rejection happens before vanilla mutation");
            }
            var missing = new Fixture();
            Check(!EquipmentTransaction.CanUseTarget(new BookModel(101, 2), out reason), "same numeric instance id on detached object rejected");
            Check(!EquipmentTransaction.CanEquip(missing.Unit, missing.Old, out reason), "same-book operation rejected");
            Console.WriteLine("PASS: " + _checks + " production equipment adapter validation, authority, postcondition and rollback checks.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
    }
    private static object Get(object value, string name) { return value.GetType().GetField(name, Fields).GetValue(value); }
    private static void Set(object value, string name, object data) { value.GetType().GetField(name, Fields).SetValue(value, data); }
    private static BookModel AddCommittedReceiver(Fixture fixture, BookModel receiver)
    {
        var source = new BookModel(500, 20);
        ((List<PassiveModel>)Get(source, "_activatedAllPassives")).Add(new PassiveModel(source.instanceId, new PassiveXmlInfo(1500)));
        BookInventoryModel.Instance.Register(source);
        receiver.originData.equipedBookIdListInPassive.Add(source.instanceId);
        receiver.reservedData.equipedBookIdListInPassive.Add(source.instanceId);
        source.originData.equipedPassiveBookInstanceId = receiver.instanceId;
        source.reservedData.equipedPassiveBookInstanceId = receiver.instanceId;
        var inherited = ((List<PassiveModel>)Get(receiver, "_activatedAllPassives"))[0];
        inherited.originData.currentpassive = new PassiveXmlInfo(1500); inherited.reservedData.currentpassive = inherited.originData.currentpassive;
        inherited.originData.receivepassivebookId = source.instanceId; inherited.reservedData.receivepassivebookId = source.instanceId;
        var donating = ((List<PassiveModel>)Get(source, "_activatedAllPassives"))[0];
        donating.originData.givePassiveBookId = receiver.instanceId; donating.reservedData.givePassiveBookId = receiver.instanceId;
        fixture.BookOrder = BookInventoryModel.Instance.GetBookList_equip();
        fixture.PassiveStates.Clear();
        foreach (var book in new[] { fixture.Old, fixture.Target, fixture.Appearance, fixture.Default, source }) fixture.PassiveStates.Add(new PassiveCapture(book));
        return source;
    }
    private static void CorruptPassives(BookModel book)
    {
        var active = (List<PassiveModel>)Get(book, "_activatedAllPassives");
        foreach (var model in active)
        {
            model.originpassive = new PassiveXmlInfo(666);
            model.originData.currentpassive = new PassiveXmlInfo(667); model.reservedData.currentpassive = new PassiveXmlInfo(668);
            model.originData.givePassiveBookId = 41; model.originData.receivepassivebookId = 42;
            model.reservedData.givePassiveBookId = 43; model.reservedData.receivepassivebookId = 44;
            Set(model, "_bookInstanceId", 10000);
            model.originData = new PassiveModel.PassiveModelSavedData(); model.reservedData = new PassiveModel.PassiveModelSavedData();
        }
        active.Clear(); Set(book, "_activatedAllPassives", new List<PassiveModel>());
        book.originData.equipedBookIdListInPassive.Clear(); book.reservedData.equipedBookIdListInPassive.Add(80);
        book.originData.equipedBookIdListInPassive = new List<int> { 81 }; book.reservedData.equipedBookIdListInPassive = new List<int> { 82 };
        book.originData.equipedPassiveBookInstanceId = 83; book.reservedData.equipedPassiveBookInstanceId = 84;
        book.originData = new BookModel.BookEquipedBookSavedData(); book.reservedData = new BookModel.BookEquipedBookSavedData();
    }
    private static void Check(bool value, string message) { _checks++; if (!value) throw new InvalidOperationException("FAILED: " + message); }
}
