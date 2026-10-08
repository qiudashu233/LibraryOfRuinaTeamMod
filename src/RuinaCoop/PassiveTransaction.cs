using System;
using System.Collections.Generic;
using System.Reflection;
using LOR_DiceSystem;

namespace RuinaCoop
{
    // The session authenticates the requester first. All objects here are host
    // inventory instances; the vanilla popup and its whole-inventory Apply are
    // deliberately absent from this transaction.
    internal static class PassiveTransaction
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo Floors = Field(typeof(LibraryModel), "_floorList");
        private static readonly FieldInfo Units = Field(typeof(LibraryFloorModel), "_unitDataList");
        private static readonly FieldInfo RawBook = Field(typeof(UnitDataModel), "_bookItem");
        private static readonly FieldInfo AppearanceBook = Field(typeof(UnitDataModel), "_CustomBookItem");

        internal static EquipmentTransactionResult Apply(UnitDataModel unit, ProgressSnapshot snapshot,
            PassiveRequest request, Func<bool> publish, out string reason)
        {
            reason = "";
            EquipmentTransaction.EditState savepoint = null;
            try
            {
                Plan plan;
                if (!TryPlan(unit, snapshot, request, out plan, out reason)) return EquipmentTransactionResult.Rejected;
                savepoint = new EquipmentTransaction.EditState(unit, plan.Target, plan.AllSources);
                var inventoryTransfer = new InventoryTransfer(plan.AllSources, plan.FinalSources);
                var transaction = DeckGuard.RunAuthorized(() =>
                {
                    // Native loaded passives have no reserved buffer. Initialize
                    // only the explicitly validated, captured relation endpoints.
                    foreach (var book in plan.AllBooks)
                    {
                        book.InitReservedDataForPassiveSuccession();
                        foreach (var passive in book.GetPassiveModelList()) passive.InitReservedData();
                    }
                    foreach (var source in plan.OldSources) plan.Target.UnEquipGivePassiveBook(source, false);
                    foreach (var passive in plan.Target.GetPassiveModelList())
                    {
                        passive.ReleaseSuccesionReceivePassive(false);
                        passive.ReleaseSuccesionGivePassive(false);
                    }
                    foreach (var source in plan.FinalSources)
                        if (!plan.Target.EquipGivePassiveBook(source)) throw new InvalidOperationException("Vanilla rejected a passive source book.");

                    // Apply negative-cost selections first. This lets the native
                    // per-step cost rule accept a valid final set without a
                    // temporary over-budget intermediate set.
                    var imports = new List<int>();
                    for (var i = 0; i < request.Slots.Length; i++)
                        if (request.Slots[i].Mode == PassiveSelectionMode.Inherit) imports.Add(i);
                    imports.Sort((left, right) => plan.ImportModels[left].originpassive.cost.CompareTo(plan.ImportModels[right].originpassive.cost));
                    var targetModels = plan.Target.GetPassiveModelList();
                    foreach (var index in imports)
                    {
                        var source = plan.ImportModels[index];
                        GivePassiveState state;
                        if (!source.CanToGivePassive || !plan.Target.CanSuccessionPassive(source, out state) ||
                            !plan.Target.CanSuccessionPassiveByCost(targetModels[index], source, false))
                            throw new InvalidOperationException("Vanilla rejected a passive selection.");
                        plan.Target.ChangePassive(targetModels[index], source);
                    }
                    if (!MatchesPlan(plan, false) || !savepoint.CheckOriginalBindings() ||
                        !savepoint.CheckDeckContents(plan.Target) || !inventoryTransfer.Check(false) ||
                        plan.Target.GetCurrentPassiveCost(false) > plan.Target.GetMaxPassiveCost())
                        throw new InvalidOperationException("The native passive draft did not match the requested final set.");

                    // A newly selected source becomes a donor even if none of its
                    // passives is selected. Vanilla returns its combat pages to
                    // inventory here; the shared savepoint covers every deck.
                    foreach (var book in plan.AllBooks) book.ApplyPassiveSuccession();
                    if (!MatchesPlan(plan, true) || !savepoint.CheckOriginalBindings() ||
                        !savepoint.CheckDeckContents(plan.Target) || !inventoryTransfer.Check(true) ||
                        plan.Target.GetCurrentPassiveCost(true) > plan.Target.GetMaxPassiveCost())
                        throw new InvalidOperationException("The committed passive graph did not match the requested final set.");
                    if (publish != null && !publish()) throw new InvalidOperationException("The resulting passive state could not be published.");
                    // A true publish has already sent the new authority revision.
                    // It is the commit boundary: never roll back after it.
                    return EquipmentTransactionResult.Accepted;
                });
                return transaction;
            }
            catch (Exception error)
            {
                reason = error.GetBaseException().Message;
                if (savepoint != null)
                {
                    try { savepoint.Restore(); }
                    catch (Exception rollback) { reason += " Rollback failed: " + rollback.GetBaseException().Message; }
                }
                return EquipmentTransactionResult.Failed;
            }
        }

        private sealed class Plan
        {
            internal BookModel Target;
            internal readonly List<BookModel> OldSources = new List<BookModel>();
            internal readonly List<BookModel> FinalSources = new List<BookModel>();
            internal readonly List<BookModel> AllSources = new List<BookModel>();
            internal readonly List<BookModel> AllBooks = new List<BookModel>();
            internal PassiveModel[] ImportModels;
            internal PassiveRequest Request;
        }

        // Inventory.AddCard preserves existing rows, but sorts the whole list
        // after inserting a new ordinary row. Basic/NoInventory pages are skipped
        // only if no row exists; an existing row is incremented before that test.
        private sealed class InventoryTransfer
        {
            private readonly List<DiceCardItemModel> _inventory;
            private readonly List<DiceCardItemModel> _original;
            private readonly DiceCardXmlInfo[] _xml;
            private readonly int[] _originalCounts;
            private readonly long[] _expectedCounts;
            private readonly Dictionary<int, long> _added = new Dictionary<int, long>();
            private readonly List<SourceDeck> _decks = new List<SourceDeck>();
            private sealed class SourceDeck
            {
                internal List<DiceCardXmlInfo> Cards;
                internal List<DiceCardXmlInfo> Original;
                internal bool Return;
            }
            internal InventoryTransfer(List<BookModel> affected, List<BookModel> selected)
            {
                _inventory = InventoryModel.Instance.GetCardListOrigin();
                if (_inventory == null) throw new InvalidOperationException("The host combat-page inventory is unavailable.");
                _original = new List<DiceCardItemModel>(_inventory);
                _xml = new DiceCardXmlInfo[_original.Count];
                _originalCounts = new int[_original.Count]; _expectedCounts = new long[_original.Count];
                for (var i = 0; i < _original.Count; i++)
                {
                    if (_original[i] == null || _original[i].ClassInfo == null || _original[i].num < 0)
                        throw new InvalidOperationException("The host combat-page inventory contains an invalid row.");
                    _xml[i] = _original[i].ClassInfo;
                    _originalCounts[i] = _original[i].num; _expectedCounts[i] = _original[i].num;
                }
                foreach (var book in affected)
                {
                    // IsEmptyDeckAll checks only the current deck on an ordinary
                    // page. ApplyPassiveSuccession skips hidden stored decks if
                    // that current deck is empty; preserve that native behavior.
                    var returns = selected.Contains(book) && !book.IsEmptyDeckAll();
                    var unique = new HashSet<DeckModel>(book.GetDeckAll_nocopy());
                    var uniqueCards = new HashSet<List<DiceCardXmlInfo>>();
                    unique.Add(Field(typeof(BookModel), "_deck").GetValue(book) as DeckModel);
                    foreach (var deck in unique)
                    {
                        if (deck == null) throw new InvalidOperationException("The source key page has an invalid current deck.");
                        var cards = deck.GetCardList_nocopy();
                        // Different deck objects may share a backing collection.
                        // The first native visit empties it; later visits add no
                        // cards. Preserve duplicate cards inside that collection.
                        if (!uniqueCards.Add(cards)) continue;
                        _decks.Add(new SourceDeck { Cards = cards, Original = new List<DiceCardXmlInfo>(cards), Return = returns });
                        if (returns) foreach (var card in cards) AddExpected(card);
                    }
                }
            }
            private void AddExpected(DiceCardXmlInfo card)
            {
                var existing = -1;
                for (var i = 0; i < _original.Count; i++)
                    if (_xml[i].id == card.id)
                    {
                        if (existing != -1) throw new InvalidOperationException("A returned combat-page inventory id is ambiguous.");
                        existing = i;
                    }
                if (existing != -1) { _expectedCounts[existing]++; return; }
                if (card.optionList.Contains(CardOption.Basic) || card.optionList.Contains(CardOption.NoInventory)) return;
                long count; _added.TryGetValue(card.id.id, out count); _added[card.id.id] = count + 1;
            }
            internal bool Check(bool committed)
            {
                if (!ReferenceEquals(InventoryModel.Instance.GetCardListOrigin(), _inventory) ||
                    _inventory.Count != _original.Count + (committed ? _added.Count : 0)) return false;
                var seen = new HashSet<DiceCardItemModel>(); var added = new HashSet<int>();
                for (var i = 0; i < _inventory.Count; i++)
                {
                    var row = _inventory[i];
                    if (row == null || !seen.Add(row)) return false;
                    var index = _original.IndexOf(row);
                    if (index != -1)
                    {
                        if (!ReferenceEquals(row.ClassInfo, _xml[index]) ||
                            row.num != (committed ? _expectedCounts[index] : _originalCounts[index]) ||
                            !committed && !ReferenceEquals(row, _original[i])) return false;
                    }
                    else
                    {
                        var xml = row.ClassInfo; long count;
                        if (!committed || xml == null || xml.id == null || !xml.id.IsBasic() ||
                            !_added.TryGetValue(xml.id.id, out count) || row.num != count || !added.Add(xml.id.id)) return false;
                    }
                }
                foreach (var deck in _decks)
                {
                    if (committed && deck.Return) { if (deck.Cards.Count != 0) return false; continue; }
                    if (deck.Cards.Count != deck.Original.Count) return false;
                    for (var i = 0; i < deck.Cards.Count; i++) if (!ReferenceEquals(deck.Cards[i], deck.Original[i])) return false;
                }
                return added.Count == (committed ? _added.Count : 0);
            }
        }

        private static bool TryPlan(UnitDataModel unit, ProgressSnapshot snapshot, PassiveRequest request, out Plan plan, out string reason)
        {
            plan = null; reason = "";
            if (unit == null || NativeDeckModels.IsMirrorUnit(unit) || snapshot == null ||
                request.UnitIndex >= snapshot.ClaimOwners.Count ||
                PassiveAuthority.Validate(snapshot, snapshot.ClaimOwners[request.UnitIndex], request) != PassiveResultCode.Accepted)
                return Reject("The displayed passive plan is no longer valid.", out reason);
            var library = LibraryModel.Instance;
            var floors = library == null ? null : Floors.GetValue(library) as List<LibraryFloorModel>;
            var floor = floors == null ? null : floors.Find(value => value != null && (byte)value.Sephirah == request.FloorId);
            var units = floor == null ? null : Units.GetValue(floor) as List<UnitDataModel>;
            var displayedFloor = snapshot.Floors.Find(value => (byte)value.Sephirah == request.FloorId);
            if (units == null || request.UnitIndex >= units.Count || !ReferenceEquals(units[request.UnitIndex], unit) ||
                unit.OwnerSephirah != floor.Sephirah || displayedFloor == null || request.UnitIndex >= displayedFloor.UnitReferences.Count ||
                !ReferenceEquals(displayedFloor.UnitReferences[request.UnitIndex], unit))
                return Reject("The host librarian binding changed.", out reason);
            if (unit.IsChangeItemLock() || library.PlayHistory.Start_TheBlueReverberationPrimaryBattle == 1 &&
                library.IsClearTheBlueReverberationPrimary(unit.OwnerSephirah)) return Reject("The host librarian's equipment is locked.", out reason);
            BookModel target;
            if (!EquipmentMirror.TryResolveBook(snapshot, request.BookToken, out target) ||
                NativeDeckModels.IsMirrorBook(target) ||
                !ReferenceEquals(RawBook.GetValue(unit), target) || !ReferenceEquals(unit.bookItem, target) || !ReferenceEquals(target.owner, unit))
                return Reject("The equipped host key page changed.", out reason);
            var inventory = BookInventoryModel.Instance;
            var books = inventory == null ? null : inventory.GetBookList_equip();
            if (books == null || !books.Contains(target) || ReferenceEquals(target, inventory.GetBlackSilenceBook()) ||
                !EquipmentTransaction.IsOrdinaryBook(target, false, out reason) || EquipmentTransaction.HasPassiveDraft(target))
                return Reject("The receiving key page is unsupported or has a pending native draft.", out reason);
            if (target.originData == null || target.originData.equipedPassiveBookInstanceId != -1)
                return Reject("A passive donor cannot receive passives.", out reason);

            var candidate = new Plan { Target = target, Request = request, ImportModels = new PassiveModel[request.Slots.Length] };
            candidate.AllBooks.Add(target);
            var targetRow = snapshot.PassiveBooks.Find(value => value.BookToken == request.BookToken);
            if (!MatchesSnapshot(snapshot, request.BookToken, target, targetRow, books, out reason)) return false;
            foreach (var token in targetRow.SourceTokens)
            {
                BookModel source;
                if (!EquipmentMirror.TryResolveBook(snapshot, token, out source) || candidate.OldSources.Contains(source))
                    return Reject("An existing passive source is missing or ambiguous.", out reason);
                candidate.OldSources.Add(source);
            }
            foreach (var token in request.SourceBookTokens)
            {
                BookModel source;
                if (!EquipmentMirror.TryResolveBook(snapshot, token, out source) || candidate.FinalSources.Contains(source))
                    return Reject("A selected passive source is missing or ambiguous.", out reason);
                candidate.FinalSources.Add(source);
            }
            foreach (var source in candidate.OldSources) if (!candidate.AllSources.Contains(source)) candidate.AllSources.Add(source);
            foreach (var source in candidate.FinalSources) if (!candidate.AllSources.Contains(source)) candidate.AllSources.Add(source);
            foreach (var source in candidate.AllSources)
            {
                var core = snapshot.CoreBooks.Find(value => ReferenceEquals(value.BookReference, source));
                var row = core == null ? null : snapshot.PassiveBooks.Find(value => value.BookToken == core.BookToken);
                if (ReferenceEquals(source, target) || source.owner != null || NativeDeckModels.IsMirrorBook(source) ||
                    ReferenceEquals(source, inventory.GetBlackSilenceBook()) || !EquipmentTransaction.IsOrdinaryBook(source, false, out reason) ||
                    EquipmentTransaction.HasPassiveDraft(source) || !MatchesSnapshot(snapshot, core == null ? 0 : core.BookToken, source, row, books, out reason))
                    return Reject("An affected source key page is unavailable, occupied or has a pending draft.", out reason);
                foreach (var existingFloor in floors)
                {
                    if (existingFloor == null) continue;
                    var roster = Units.GetValue(existingFloor) as List<UnitDataModel>;
                    if (roster == null) return Reject("The host roster is unavailable.", out reason);
                    foreach (var librarian in roster)
                        if (librarian != null && (ReferenceEquals(librarian.bookItem, source) || ReferenceEquals(AppearanceBook.GetValue(librarian), source)))
                            return Reject("An affected passive source is used by a host librarian.", out reason);
                }
                if (source.originData.equipedBookIdListInPassive.Count != 0 ||
                    source.originData.equipedPassiveBookInstanceId != (candidate.OldSources.Contains(source) ? target.instanceId : -1))
                    return Reject("An affected source has another passive relation.", out reason);
                candidate.AllBooks.Add(source);
            }
            if (!ValidCommittedGraph(candidate)) return Reject("The current passive graph is incomplete or inconsistent.", out reason);
            var targetModels = target.GetPassiveModelList();
            var ids = new HashSet<int>(); var innerTypes = new HashSet<int>(); var used = new HashSet<PassiveModel>();
            var cost = 0;
            for (var i = 0; i < request.Slots.Length; i++)
                if (request.Slots[i].Mode == PassiveSelectionMode.RestoreNative && Id(targetModels[i].originpassive) != PassiveMirror.EmptyId)
                { ids.Add(Id(targetModels[i].originpassive)); if (targetModels[i].originpassive.InnerTypeId != -1) innerTypes.Add(targetModels[i].originpassive.InnerTypeId); }
            for (var i = 0; i < request.Slots.Length; i++)
            {
                var selection = request.Slots[i]; var native = targetModels[i].originpassive;
                if (selection.Mode == PassiveSelectionMode.RestoreNative)
                { if (selection.ExpectedOriginPassiveId != Id(native)) return Reject("The native passive slot changed.", out reason); continue; }
                BookModel source;
                if (!EquipmentMirror.TryResolveBook(snapshot, selection.SourceBookToken, out source) || !candidate.FinalSources.Contains(source))
                    return Reject("A passive source was not selected.", out reason);
                var models = source.GetPassiveModelList();
                if (selection.SourceSlotIndex >= models.Count) return Reject("The source passive slot changed.", out reason);
                var model = models[selection.SourceSlotIndex]; var xml = model.originpassive;
                if (Id(native) != PassiveMirror.EmptyId || native.isLock || !native.CanReceivePassive ||
                    Id(xml) != selection.ExpectedOriginPassiveId || Id(xml) == PassiveMirror.EmptyId || !xml.CanGivePassive || xml.isHide ||
                    !used.Add(model) || !ids.Add(Id(xml)) || xml.InnerTypeId != -1 && !innerTypes.Add(xml.InnerTypeId))
                    return Reject("A passive selection violates the native slot or compatibility rules.", out reason);
                candidate.ImportModels[i] = model;
                cost = checked(cost + xml.cost);
            }
            if (request.SourceBookTokens.Length > 4 || cost > target.GetMaxPassiveCost()) return Reject("The passive source count or cost exceeds the host limit.", out reason);
            plan = candidate;
            return true;
        }

        private static bool MatchesSnapshot(ProgressSnapshot snapshot, ulong token, BookModel book,
            ProgressSnapshot.PassiveBookEntry row, List<BookModel> inventory, out string reason)
        {
            reason = "";
            var core = snapshot.CoreBooks.Find(value => value.BookToken == token);
            if (token == 0 || book == null || row == null || core == null || !ReferenceEquals(core.BookReference, book) ||
                book.instanceId <= 0 || !inventory.Contains(book) || core.BookInstanceId != book.instanceId || core.BookId != book.BookId.id ||
                book.originData == null || book.originData.equipedBookIdListInPassive == null || row.MaxCost != book.GetMaxPassiveCost())
                return Reject("The host passive book binding changed.", out reason);
            var occurrences = 0;
            foreach (var other in inventory) if (other != null && other.instanceId == book.instanceId) occurrences++;
            if (occurrences != 1) return Reject("A host key-page instance id is ambiguous.", out reason);
            BookModel receiver = null;
            if (row.ReceiverBookToken != 0 && !EquipmentMirror.TryResolveBook(snapshot, row.ReceiverBookToken, out receiver))
                return Reject("The passive recipient is unavailable.", out reason);
            if (book.originData.equipedPassiveBookInstanceId != (receiver == null ? -1 : receiver.instanceId) ||
                row.SourceTokens.Count != book.originData.equipedBookIdListInPassive.Count)
                return Reject("The committed passive book relations changed.", out reason);
            for (var i = 0; i < row.SourceTokens.Count; i++)
            {
                BookModel source;
                if (!EquipmentMirror.TryResolveBook(snapshot, row.SourceTokens[i], out source) ||
                    source.instanceId != book.originData.equipedBookIdListInPassive[i]) return Reject("The committed passive source list changed.", out reason);
            }
            var models = book.GetPassiveModelList(); var unique = new HashSet<PassiveModel>();
            if (models == null || models.Count != row.Slots.Count || models.Count > PassiveMirror.MaxSlots)
                return Reject("The host passive slot layout changed.", out reason);
            for (var i = 0; i < models.Count; i++)
            {
                var model = models[i]; var slot = row.Slots[i];
                if (model == null || !unique.Add(model) || model.BookInstanceId != book.instanceId || model.originData == null || slot == null ||
                    Id(model.originpassive) != slot.OriginId || Id(model.originData.currentpassive) != slot.CurrentId)
                    return Reject("The host passive slot identity changed.", out reason);
                var native = model.originpassive; var current = model.originData.currentpassive;
                var flags = (native.CanGivePassive ? PassiveSlotFlags.CanGive : 0) | (native.isLock ? PassiveSlotFlags.Locked : 0) |
                    (native.isNegative ? PassiveSlotFlags.Negative : 0) | (native.isHide ? PassiveSlotFlags.Hidden : 0) |
                    (native.CanReceivePassive ? PassiveSlotFlags.CanReceive : 0) |
                    (model.originData.givePassiveBookId != book.instanceId ? PassiveSlotFlags.Given : 0);
                if (slot.Flags != flags || slot.Cost != native.cost || slot.CurrentCost != current.cost || slot.InnerTypeId != native.InnerTypeId ||
                    slot.OriginRarity != (byte)native.rare || slot.CurrentRarity != (byte)current.rare || slot.CurrentNegative != current.isNegative)
                    return Reject("The host passive metadata changed.", out reason);
                BookModel originSource = null;
                if (slot.SourceBookToken != 0 && !EquipmentMirror.TryResolveBook(snapshot, slot.SourceBookToken, out originSource))
                    return Reject("The inherited passive source is unavailable.", out reason);
                if (model.originData.receivepassivebookId != (originSource == null ? book.instanceId : originSource.instanceId) ||
                    model.originData.givePassiveBookId != ((slot.Flags & PassiveSlotFlags.Given) == 0 ? book.instanceId : receiver == null ? -1 : receiver.instanceId))
                    return Reject("The committed passive slot relations changed.", out reason);
                if (originSource != null)
                {
                    var originModels = originSource.GetPassiveModelList();
                    if (slot.SourceSlotIndex >= originModels.Count || Id(originModels[slot.SourceSlotIndex].originpassive) != slot.CurrentId)
                        return Reject("The inherited source slot changed.", out reason);
                }
            }
            return true;
        }

        private static bool ValidCommittedGraph(Plan plan)
        {
            var target = plan.Target; var targetModels = target.GetPassiveModelList(); var used = new HashSet<PassiveModel>();
            foreach (var model in targetModels)
            {
                if (model.originData.givePassiveBookId != target.instanceId) return false;
                if (model.originData.receivepassivebookId == target.instanceId)
                { if (!SameXml(model.originData.currentpassive, model.originpassive)) return false; continue; }
                var source = plan.OldSources.Find(value => value.instanceId == model.originData.receivepassivebookId);
                if (source == null) return false;
                PassiveModel donor = null;
                foreach (var candidate in source.GetPassiveModelList())
                    if (candidate.originData.givePassiveBookId == target.instanceId && Id(candidate.originpassive) == Id(model.originData.currentpassive))
                    { if (donor != null) return false; donor = candidate; }
                if (donor == null || !used.Add(donor) || !SameXml(model.originData.currentpassive, donor.originpassive)) return false;
            }
            foreach (var source in plan.AllSources)
                foreach (var passive in source.GetPassiveModelList())
                {
                    if (passive.originData.receivepassivebookId != source.instanceId || !SameXml(passive.originData.currentpassive, passive.originpassive)) return false;
                    if (passive.originData.givePassiveBookId != source.instanceId &&
                        (passive.originData.givePassiveBookId != target.instanceId || !plan.OldSources.Contains(source) || !used.Contains(passive))) return false;
                }
            return true;
        }

        private static bool MatchesPlan(Plan plan, bool origin)
        {
            var target = plan.Target; var targetData = origin ? target.originData : target.reservedData;
            if (targetData == null || targetData.equipedPassiveBookInstanceId != -1 ||
                targetData.equipedBookIdListInPassive == null || targetData.equipedBookIdListInPassive.Count != plan.FinalSources.Count) return false;
            for (var i = 0; i < plan.FinalSources.Count; i++)
                if (targetData.equipedBookIdListInPassive[i] != plan.FinalSources[i].instanceId) return false;
            var models = target.GetPassiveModelList();
            if (models.Count != plan.Request.Slots.Length) return false;
            var borrowed = new HashSet<PassiveModel>();
            for (var i = 0; i < models.Count; i++)
            {
                var passive = models[i]; var data = origin ? passive.originData : passive.reservedData; var donor = plan.ImportModels[i];
                if (data == null || data.givePassiveBookId != target.instanceId ||
                    data.receivepassivebookId != (donor == null ? target.instanceId : donor.BookInstanceId) ||
                    !SameXml(data.currentpassive, donor == null ? passive.originpassive : donor.originpassive)) return false;
                if (donor != null) borrowed.Add(donor);
            }
            foreach (var source in plan.AllSources)
            {
                var data = origin ? source.originData : source.reservedData;
                if (data == null || data.equipedBookIdListInPassive == null || data.equipedBookIdListInPassive.Count != 0 ||
                    data.equipedPassiveBookInstanceId != (plan.FinalSources.Contains(source) ? target.instanceId : -1)) return false;
                foreach (var passive in source.GetPassiveModelList())
                {
                    var saved = origin ? passive.originData : passive.reservedData;
                    if (saved == null || saved.receivepassivebookId != source.instanceId ||
                        saved.givePassiveBookId != (borrowed.Contains(passive) ? target.instanceId : source.instanceId) ||
                        !SameXml(saved.currentpassive, passive.originpassive)) return false;
                }
            }
            return true;
        }

        private static int Id(PassiveXmlInfo xml)
        { return xml != null && xml.id != null && xml.id.IsBasic() && xml.id.id > 0 ? xml.id.id : 0; }
        private static bool SameXml(PassiveXmlInfo left, PassiveXmlInfo right)
        {
            return left != null && right != null && Id(left) != 0 && left.id == right.id && left.cost == right.cost &&
                left.rare == right.rare && left.isNegative == right.isNegative;
            // SetGiveBookId uses PassiveModel.DeepCopy, which copies only XML
            // id/package, cost, rarity, negativity and param. Give/receive flags,
            // locks and inner type belong to immutable originpassive metadata.
        }
        private static FieldInfo Field(Type type, string name)
        { var field = type.GetField(name, Fields); if (field == null) throw new MissingFieldException(type.FullName, name); return field; }
        private static bool Reject(string message, out string reason) { reason = message; return false; }
    }
}
