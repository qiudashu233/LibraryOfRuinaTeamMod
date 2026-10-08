using System;
using System.Collections.Generic;
using System.Reflection;

// Small, deterministic failure-injection models. They preserve the verified
// vanilla mutation order, not Unity, networking, or Harmony's patch machinery.
public sealed class LorId
{
    internal int Id;
    public LorId(int id) { Id = id; }
}

public enum CardEquipState
{
    Equippable = 0, LackOfCards = 1, FullOfDeck = 2, OverCardLimit = 3,
    OverFloorLimit = 4, FarTypeLimit = 5, NearTypeLimit = 6, OnlyPageLimit = 7, ERROR = 8
}

namespace LOR_DiceSystem
{
    public sealed class DiceCardXmlInfo
    {
        internal LorId Id;
        internal bool Infinite;
        internal DiceCardXmlInfo(int id, bool infinite = false) { Id = new LorId(id); Infinite = infinite; }
    }
}

public sealed class DiceCardItemModel
{
    internal LOR_DiceSystem.DiceCardXmlInfo Card;
    public int num;
    internal DiceCardItemModel(LOR_DiceSystem.DiceCardXmlInfo card, int count) { Card = card; num = count; }
}

public sealed class InventoryModel
{
    public static InventoryModel Instance = new InventoryModel();
    internal readonly List<DiceCardItemModel> Items = new List<DiceCardItemModel>();
    public List<DiceCardItemModel> GetCardListOrigin() { return Items; }
    internal DiceCardItemModel Find(LorId id) { return Items.Find(item => item.Card.Id.Id == id.Id); }
}

public sealed class DeckModel
{
    private readonly List<LOR_DiceSystem.DiceCardXmlInfo> _cards = new List<LOR_DiceSystem.DiceCardXmlInfo>();
    internal bool ThrowAfterInventoryRemoval;
    internal bool ThrowAfterDeckRemoval;
    internal bool ReturnRejectedAfterMutation;
    internal LOR_DiceSystem.DiceCardXmlInfo InfiniteCard;
    public List<LOR_DiceSystem.DiceCardXmlInfo> GetCardList_nocopy() { return _cards; }

    public CardEquipState AddCardFromInventory(LorId id)
    {
        var item = InventoryModel.Instance.Find(id);
        var card = item == null ? InfiniteCard : item.Card;
        if (card == null || card.Id.Id != id.Id || !card.Infinite && item.num <= 0)
            return CardEquipState.LackOfCards;
        if (!card.Infinite) item.num--;
        if (ThrowAfterInventoryRemoval) throw new InvalidOperationException("after inventory removal");
        _cards.Add(card);
        return ReturnRejectedAfterMutation ? CardEquipState.ERROR : CardEquipState.Equippable;
    }

    public bool MoveCardToInventory(LorId id)
    {
        var card = _cards.Find(candidate => candidate.Id.Id == id.Id);
        if (card == null) return false;
        _cards.Remove(card);
        if (ThrowAfterDeckRemoval) throw new InvalidOperationException("after deck removal");
        if (!card.Infinite)
        {
            var item = InventoryModel.Instance.Find(id);
            if (item == null)
            {
                InventoryModel.Instance.Items.Add(new DiceCardItemModel(card, 1));
                InventoryModel.Instance.Items.Sort((left, right) => left.Card.Id.Id.CompareTo(right.Card.Id.Id));
            }
            else item.num++;
        }
        return !ReturnRejectedAfterMutation;
    }

    public void SetDeck(LorId id) { }
    public void AddCardForLoading(LorId id) { }
    public void LoadFromSaveData(GameSave.SaveData data) { }
    public void EmptyDeckToInventory() { }
    public void RemoveAllErrorCard() { }
}

public sealed class BookModel
{
    private readonly DeckModel _deck;
    public BookModel(DeckModel deck) { _deck = deck; }
    public CardEquipState AddCardFromInventoryToCurrentDeck(LorId id) { return _deck.AddCardFromInventory(id); }
    public bool MoveCardFromCurrentDeckToInventory(LorId id) { return _deck.MoveCardToInventory(id); }
    public void ChangeDeck(int index) { }
    public void EmptyDeckToInventory() { }
    public void EmptyDeckToInventoryAll() { }
    public void CreateDeckByDeckInfo() { }
    public void ApplyPassiveSuccession() { }
}

public sealed class UnitDataModel
{
    public BookModel bookItem;
    public CardEquipState AddCardFromInventory(LorId id) { return bookItem.AddCardFromInventoryToCurrentDeck(id); }
    public CardEquipState AddCardInDeckFromInventory(LOR_DiceSystem.DiceCardXmlInfo card) { return AddCardFromInventory(card.Id); }
    public bool MoveCardToInventory(LorId id) { return bookItem.MoveCardFromCurrentDeckToInventory(id); }
    public bool RemoveCardInDeck(LOR_DiceSystem.DiceCardXmlInfo card) { return MoveCardToInventory(card.Id); }
    public bool EquipBook(BookModel book, bool keep, bool force) { return false; }
    public bool EquipBookForUI(BookModel book, bool keep, bool force) { return false; }
    public void EquipCustomCoreBook(BookModel book) { }
    public void EmptyDeckToInventory() { }
    public void EmptyDeckToInventoryAll() { }
    public void ReEquipDeck() { }
    public void CreateDeckByDeckInfo() { }
}

public sealed class LibraryModel { public void LoadFromSaveData(GameSave.SaveData data) { } }
public sealed class LatestDataModel { }
public sealed class GlobalGameManager { public void LoadBattleScene() { } }
public sealed class PlatformManager { public void SavePlayData(int slot, GameSave.SaveData data) { } }
public sealed class PlatformCore_default { public void SavePlayData(int slot, GameSave.SaveData data, Action<bool> callback) { } }

namespace GameSave
{
    public sealed class SaveData { }
    public sealed class SaveManager
    {
        public bool SavePlayData(int slot, bool force) { return true; }
        public void SaveLatestData(LatestDataModel data) { }
    }
}

namespace UI
{
    public sealed class UIController
    {
        public static UIController Instance = new UIController();
        public UnitDataModel CurrentUnit;
    }
    public sealed class UIOriginCardSlot { }
    public sealed class UIInvenCardSlot { }
    public sealed class UIEquipDeckCardList
    {
        public UnitDataModel currentunit;
        public void OnClickCardSlotByDeck(UIOriginCardSlot slot) { }
        public void RemoveCardSlot(UIOriginCardSlot slot) { }
        public void OnClickCardSlotByInven(UIInvenCardSlot slot) { }
        public void InsertCardSlot(UIInvenCardSlot slot) { }
        public void OnChangeDeckTab() { }
    }
    public sealed class UILibrarianEquipDeckPanel
    {
        internal UnitDataModel _unitdata;
        public void OnClickClearDeckButton() { }
    }
    public sealed class UIDeckCardList { public void SetDeckCheck(DeckModel deck, UnitDataModel unit) { } }
    public sealed class UIDeckInfoPopup { public void OnclickApplyDeckButton() { } }
    public sealed class UICardEquipInfoPanel
    {
        public void DeleteCardFrom(UnitDataModel unit) { }
        public void DeleteCardFrom(BookModel book) { }
    }
    public class UIOriginEquipPageSlot { internal BookModel _bookDataModel; }
    public sealed class UIInvenEquipPageSlot : UIOriginEquipPageSlot
    {
        public void OnClickEquipButton() { }
        public void OnClickEmptyDeckButton() { }
    }
    public sealed class UIInvenLeftEquipPageSlot : UIOriginEquipPageSlot
    {
        public void OnClickEquipButton() { }
        public void OnClickEmptyDeckButton() { }
    }
    public sealed class UISettingInvenEquipPageSlot : UIOriginEquipPageSlot
    {
        public void OnClickEquipButton() { }
        public void OnClickEmptyDeckButton() { }
    }
    public sealed class UISettingInvenEquipPageLeftSlot : UIOriginEquipPageSlot
    {
        public void OnClickEquipButton() { }
        public void OnClickEmptyDeckButton() { }
    }
    public sealed class UILibrarianInfoInCardPhase { public void OnClickReleaseToggle() { } }
    public sealed class UIBattleSettingLibrarianInfoPanel { public void OnClickReleaseToggle() { } }
    public sealed class UIBattleSettingPanel { public void OnClickBattleStart() { } }
}

namespace UnityEngine
{
    public static class Debug
    {
        internal static int Errors;
        internal static int Warnings;
        public static void LogError(object value) { Errors++; }
        public static void LogWarning(object value) { Warnings++; }
    }
}

namespace HarmonyLib
{
    public sealed class HarmonyMethod
    {
        internal MethodInfo Method;
        public HarmonyMethod(MethodInfo method) { Method = method; }
    }
    public sealed class Harmony
    {
        internal readonly Dictionary<MethodInfo, HarmonyMethod> Prefixes = new Dictionary<MethodInfo, HarmonyMethod>();
        internal readonly Dictionary<MethodInfo, HarmonyMethod> Finalizers = new Dictionary<MethodInfo, HarmonyMethod>();
        public void Patch(MethodInfo original, HarmonyMethod prefix = null, HarmonyMethod finalizer = null)
        {
            if (prefix != null) Prefixes.Add(original, prefix);
            if (finalizer != null) Finalizers.Add(original, finalizer);
        }
    }
    public static class AccessTools
    {
        public static FieldInfo Field(Type type, string name)
        {
            while (type != null)
            {
                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null) return field;
                type = type.BaseType;
            }
            return null;
        }
    }
}

namespace RuinaCoop
{
    internal static class NativeDeckModels
    {
        internal static bool IsConstructingMirrors;
        internal static UnitDataModel MirrorUnit;
        internal static BookModel MirrorBook;
        internal static DeckModel MirrorDeck;
        internal static bool IsMirrorUnit(UnitDataModel value) { return value != null && ReferenceEquals(value, MirrorUnit); }
        internal static bool IsMirrorBook(BookModel value) { return value != null && ReferenceEquals(value, MirrorBook); }
        internal static bool IsMirrorDeck(DeckModel value) { return value != null && ReferenceEquals(value, MirrorDeck); }
    }
    internal static class NativeEquipmentEditor
    {
        internal static bool ConsumeClick;
        internal static bool TryHandleCorePageClick(object instance) { return ConsumeClick; }
    }

    internal static class NativeDeckEditor
    {
        internal static bool ConsumeClick;
        internal static bool TryHandleDeckUi(object instance, MethodBase method, object[] args) { return ConsumeClick; }
    }
    internal sealed class RelaySession
    {
        internal bool IsGuestSession;
        internal bool PreparationFrozen;
        internal bool OwnerMayEdit;
        internal bool CanEditLocalUnit(UnitDataModel unit) { return OwnerMayEdit && !IsGuestSession && !PreparationFrozen; }
        internal bool CanEditLocalBook(BookModel book) { return OwnerMayEdit && !IsGuestSession && !PreparationFrozen; }
        internal bool CanEditLocalDeck(DeckModel deck) { return OwnerMayEdit && !IsGuestSession && !PreparationFrozen; }
    }
}
