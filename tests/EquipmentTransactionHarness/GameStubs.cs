using System;
using System.Collections.Generic;
using System.Reflection;

public enum Gender { F, M, N, Creature, EGO }
public enum SephirahType { Malkuth = 1 }
public enum BookOption { None, Basic, MultiDeck }
public sealed class LorId
{
    public int id;
    internal bool Workshop;
    public LorId(int value) { id = value; }
    public bool IsBasic() { return !Workshop; }
    public static bool operator ==(LorId a, LorId b) { return ReferenceEquals(a, b) || !ReferenceEquals(a, null) && !ReferenceEquals(b, null) && a.id == b.id && a.Workshop == b.Workshop; }
    public static bool operator !=(LorId a, LorId b) { return !(a == b); }
    public override bool Equals(object other) { return this == other as LorId; }
    public override int GetHashCode() { return id; }
}
namespace LOR_DiceSystem
{
    public sealed class DiceCardXmlInfo
    {
        public LorId id;
        internal bool Basic;
        public DiceCardXmlInfo(int value, bool basic = false) { id = new LorId(value); Basic = basic; }
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
    private List<DiceCardItemModel> _cardList = new List<DiceCardItemModel>();
    public List<DiceCardItemModel> GetCardListOrigin() { return _cardList; }
    internal void Add(LOR_DiceSystem.DiceCardXmlInfo card)
    {
        if (card.Basic) return;
        var item = _cardList.Find(row => ReferenceEquals(row.Card, card));
        if (item == null) _cardList.Insert(0, new DiceCardItemModel(card, 1)); else item.num++;
    }
    internal bool Remove(LOR_DiceSystem.DiceCardXmlInfo card)
    {
        if (card.Basic) return true;
        var item = _cardList.Find(row => ReferenceEquals(row.Card, card));
        if (item == null || item.num <= 0) return false;
        if (--item.num == 0) _cardList.Remove(item);
        return true;
    }
}
public sealed class BookInventoryModel
{
    public static BookInventoryModel Instance = new BookInventoryModel();
    private List<BookModel> _bookList = new List<BookModel>();
    internal BookModel Black;
    public List<BookModel> GetBookList_equip() { return new List<BookModel>(_bookList); }
    public BookModel GetBlackSilenceBook() { return Black; }
    internal void Register(BookModel book) { _bookList.Add(book); }
}
public sealed class DeckModel
{
    private List<LOR_DiceSystem.DiceCardXmlInfo> _deck = new List<LOR_DiceSystem.DiceCardXmlInfo>();
    public List<LOR_DiceSystem.DiceCardXmlInfo> GetCardList_nocopy() { return _deck; }
}
public sealed class BookXmlInfo
{
    public bool isError;
    public bool canNotEquip;
    public List<BookOption> optionList = new List<BookOption>();
}
public sealed class BookModel
{
    public sealed class BookEquipedBookSavedData
    {
        public List<int> equipedBookIdListInPassive = new List<int>();
        public int equipedPassiveBookInstanceId = -1;
    }
    public BookEquipedBookSavedData originData = new BookEquipedBookSavedData();
    public BookEquipedBookSavedData reservedData = new BookEquipedBookSavedData();
    private List<PassiveModel> _activatedAllPassives = new List<PassiveModel>();
    private DeckModel _deck;
    private List<DeckModel> _deckList = new List<DeckModel>();
    public UnitDataModel owner;
    public int instanceId;
    public LorId BookId;
    public BookXmlInfo ClassInfo = new BookXmlInfo();
    internal bool Fixed;
    internal bool Locked;
    internal bool BlueLocked;
    public BookModel(int id, int instance)
    {
        BookId = new LorId(id); instanceId = instance;
        for (var i = 0; i < 4; i++) _deckList.Add(new DeckModel());
        _deck = _deckList[0];
    }
    public List<DeckModel> GetDeckAll_nocopy() { return _deckList; }
    internal DeckModel CurrentDeck { get { return _deck; } }
    public bool IsFixedDeck() { return Fixed; }
    public bool IsDeckLocked() { return Locked; }
    public bool IsLockByBluePrimary() { return BlueLocked; }
    public bool IsMultiDeck() { return ClassInfo.optionList.Contains(BookOption.MultiDeck); }
    public bool CanEquipBookByGivePassive() { return originData.equipedPassiveBookInstanceId == -1; }
    internal void ChangeDeck(int index) { if (IsMultiDeck()) _deck = _deckList[index]; }
}
public enum Rarity { None, Common, Rare }
public sealed class PassiveXmlInfo
{
    public LorId id;
    public bool isNegative;
    public Rarity rare;
    internal PassiveXmlInfo(int value) { id = new LorId(value); }
}
public sealed class PassiveModel
{
    public sealed class PassiveModelSavedData
    {
        public PassiveXmlInfo currentpassive;
        public int receivepassivebookId = -1;
        public int givePassiveBookId = -1;
    }
    public PassiveXmlInfo originpassive;
    public PassiveModelSavedData originData;
    public PassiveModelSavedData reservedData;
    private int _bookInstanceId;
    // The real native constructor/load initializes origin only. Its reserved
    // buffer remains null until the passive succession popup initializes it.
    internal PassiveModel(int id, PassiveXmlInfo xml)
    { _bookInstanceId = id; originpassive = xml; originData = new PassiveModelSavedData { currentpassive = xml, receivepassivebookId = id, givePassiveBookId = id }; }
}
public sealed class UnitDataModel
{
    private BookModel _bookItem;
    private BookModel _CustomBookItem;
    public BookModel defaultBook;
    public BookModel bookItem { get { return _bookItem ?? defaultBook; } }
    public Gender appearanceType = Gender.M;
    public SephirahType OwnerSephirah = SephirahType.Malkuth;
    internal bool Locked;
    internal Action<string, UnitDataModel, BookModel> Hook;
    public UnitDataModel(BookModel current, BookModel basic)
    { _bookItem = current; defaultBook = basic; if (current != null) current.owner = this; }
    public bool IsChangeItemLock() { return Locked; }
    public bool EquipBookForUI(BookModel target, bool isEnemySetting, bool force)
    {
        if (!RuinaCoop.DeckGuard.Authorized) throw new InvalidOperationException("missing authority scope");
        if (isEnemySetting || force) throw new InvalidOperationException("unsafe vanilla flags");
        if (Hook != null) Hook("before", this, target);
        if (target.owner != null) return false;
        var old = _bookItem;
        _bookItem = target; target.owner = this;
        if (Hook != null) Hook("owned", this, target);
        var copies = new List<List<LOR_DiceSystem.DiceCardXmlInfo>>();
        foreach (var deck in target.GetDeckAll_nocopy()) copies.Add(new List<LOR_DiceSystem.DiceCardXmlInfo>(deck.GetCardList_nocopy()));
        foreach (var deck in target.GetDeckAll_nocopy())
        {
            foreach (var card in new List<LOR_DiceSystem.DiceCardXmlInfo>(deck.GetCardList_nocopy()))
            { deck.GetCardList_nocopy().Remove(card); InventoryModel.Instance.Add(card); if (Hook != null) Hook("returned", this, target); }
        }
        for (var i = 0; i < copies.Count; i++)
        {
            target.ChangeDeck(i);
            foreach (var card in copies[i])
            {
                if (InventoryModel.Instance.Remove(card))
                { if (Hook != null) Hook("deducted", this, target); target.CurrentDeck.GetCardList_nocopy().Add(card); }
            }
        }
        target.ChangeDeck(0);
        if (old != null) old.owner = null;
        _CustomBookItem = null;
        if (Hook != null) Hook("completed", this, target);
        return false; // Verified vanilla return, including successful equipment.
    }
}
public sealed class PlayHistoryModel { public int Start_TheBlueReverberationPrimaryBattle; }
public sealed class LibraryFloorModel
{
    private readonly List<UnitDataModel> _unitDataList = new List<UnitDataModel>();
    internal List<UnitDataModel> Units { get { return _unitDataList; } }
    public SephirahType Sephirah = SephirahType.Malkuth;
    public List<UnitDataModel> GetUnitDataList() { return Units; }
}
public sealed class LibraryModel
{
    public static LibraryModel Instance = new LibraryModel();
    public PlayHistoryModel PlayHistory = new PlayHistoryModel();
    internal bool BlueClear;
    internal LibraryFloorModel Floor = new LibraryFloorModel();
    private List<LibraryFloorModel> _floorList;
    public LibraryModel() { _floorList = new List<LibraryFloorModel> { Floor }; }
    public LibraryFloorModel GetFloor(SephirahType floor) { return Floor; }
    public bool IsClearTheBlueReverberationPrimary(SephirahType floor) { return BlueClear; }
}
namespace UnityEngine { public static class Debug { public static void LogError(object value) { } } }
namespace RuinaCoop
{
    internal static class NativeDeckModels
    {
        internal static object Mirror;
        internal static bool IsMirrorBook(BookModel book) { return ReferenceEquals(book, Mirror); }
        internal static bool IsMirrorUnit(UnitDataModel unit) { return ReferenceEquals(unit, Mirror); }
    }
    internal static class DeckGuard
    {
        internal static bool Authorized;
        internal static bool Denied;
        internal static T RunAuthorized<T>(Func<T> action)
        { if (Denied) throw new InvalidOperationException("guest or frozen"); var old = Authorized; Authorized = true; try { return action(); } finally { Authorized = old; } }
    }
}
