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
    public enum CardOption { Basic=0,OnlyPage=1,EGO=2,EgoPersonal=3,Personal=4,NoInventory=5 }
    public sealed class DiceCardXmlInfo
    {
        public LorId id;
        internal bool Basic;
        public List<CardOption> optionList=new List<CardOption>();
        public DiceCardXmlInfo(int value, bool basic = false) { id = new LorId(value); Basic = basic;if(basic)optionList.Add(CardOption.Basic); }
    }
}
public sealed class DiceCardItemModel
{
    internal LOR_DiceSystem.DiceCardXmlInfo Card;
    public int num;
    public LOR_DiceSystem.DiceCardXmlInfo ClassInfo { get { return Card; } }
    internal DiceCardItemModel(LOR_DiceSystem.DiceCardXmlInfo card, int count) { Card = card; num = count; }
}
public sealed class InventoryModel
{
    public static InventoryModel Instance = new InventoryModel();
    private List<DiceCardItemModel> _cardList = new List<DiceCardItemModel>();
    public List<DiceCardItemModel> GetCardListOrigin() { return _cardList; }
    internal void Add(LOR_DiceSystem.DiceCardXmlInfo card)
    {
        var item = _cardList.Find(row => row.Card.id == card.id);
        if(item!=null){item.num++;return;}
        if(card.optionList.Contains(LOR_DiceSystem.CardOption.Basic)||card.optionList.Contains(LOR_DiceSystem.CardOption.NoInventory))return;
        _cardList.Add(new DiceCardItemModel(card,1));
        _cardList.Sort((left,right)=>right.Card.id.id.CompareTo(left.Card.id.id)); // exercise reorder when a new row appears
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
    public List<PassiveModel> GetPassiveModelList() { return _activatedAllPassives; }
    public int GetMaxPassiveCost() { return LibraryModel.Instance.MaxCost; }
    public int GetCurrentPassiveCost(bool origin)
    {
        var cost=0;
        foreach(var passive in _activatedAllPassives)
        { var data=origin?passive.originData:passive.reservedData; if(data!=null&&data.currentpassive!=null&&data.givePassiveBookId!=data.receivepassivebookId)cost+=data.currentpassive.cost; }
        return cost;
    }
    public void InitReservedDataForPassiveSuccession()
    {
        reservedData=new BookEquipedBookSavedData { equipedPassiveBookInstanceId=originData.equipedPassiveBookInstanceId };
        reservedData.equipedBookIdListInPassive.AddRange(originData.equipedBookIdListInPassive);
        TransactionHooks.Call("book-init",this);
    }
    public void UnEquipGivePassiveBook(BookModel source,bool origin)
    {
        var data=origin?originData:reservedData;
        foreach(var p in source.GetPassiveModelList())p.ReleaseSuccesionGivePassive(origin);
        foreach(var p in _activatedAllPassives)
        { var saved=origin?p.originData:p.reservedData;if(saved.receivepassivebookId==source.instanceId)p.ReleaseSuccesionReceivePassive(origin); }
        data.equipedBookIdListInPassive.Remove(source.instanceId);
        (origin?source.originData:source.reservedData).equipedPassiveBookInstanceId=-1;
        TransactionHooks.Call("source-release",source);
    }
    public bool EquipGivePassiveBook(BookModel source)
    {
        if(reservedData.equipedBookIdListInPassive.Count>=4)return false;
        source.reservedData.equipedPassiveBookInstanceId=instanceId;
        reservedData.equipedBookIdListInPassive.Add(source.instanceId);
        TransactionHooks.Call("source-equip",source);
        return true;
    }
    public bool CanSuccessionPassive(PassiveModel source,out GivePassiveState state)
    {
        state=GivePassiveState.None;
        if(!source.originpassive.CanGivePassive){state=GivePassiveState.Unique;return false;}
        foreach(var model in _activatedAllPassives)
        {
            var xml=model.reservedData.currentpassive;
            if(xml.id==source.originpassive.id||xml.InnerTypeId!=-1&&xml.InnerTypeId==source.originpassive.InnerTypeId)
            { state=source.CanToGivePassive?GivePassiveState.Lock:GivePassiveState.Using;return false; }
        }
        return true;
    }
    public bool CanSuccessionPassiveByCost(PassiveModel target,PassiveModel source,bool origin)
    {
        var old=(origin?target.originData:target.reservedData).currentpassive.cost;
        var next=(origin?source.originData:source.reservedData).currentpassive.cost;
        return next<=GetMaxPassiveCost()-(GetCurrentPassiveCost(false)-old);
    }
    public void ChangePassive(PassiveModel target,PassiveModel source)
    { if(_activatedAllPassives.Contains(target))target.SuccessionPassiveForReserved(source);TransactionHooks.Call("change",this); }
    public void ApplyPassiveSuccession()
    {
        TransactionHooks.CommittedBooks.Add(this);
        originData.equipedBookIdListInPassive.Clear();originData.equipedBookIdListInPassive.AddRange(reservedData.equipedBookIdListInPassive);
        originData.equipedPassiveBookInstanceId=reservedData.equipedPassiveBookInstanceId;
        TransactionHooks.Call("book-apply-links",this);
        if(originData.equipedPassiveBookInstanceId!=-1&&!IsEmptyDeckAll())
        {
            Action<DeckModel> empty=deck=>
            {
                foreach(var card in new List<LOR_DiceSystem.DiceCardXmlInfo>(deck.GetCardList_nocopy()))
                { InventoryModel.Instance.Add(card);deck.GetCardList_nocopy().Remove(card);TransactionHooks.Call("card-return",this); }
            };
            foreach(var deck in _deckList)empty(deck);
            empty(_deck);
        }
        foreach(var model in _activatedAllPassives)model.ApplyReserved();
        TransactionHooks.Call("book-apply",this);
    }
    public bool IsEmptyDeckAll()
    {
        if(!IsMultiDeck())return _deck.GetCardList_nocopy().Count==0;
        foreach(var deck in _deckList)if(deck.GetCardList_nocopy().Count!=0)return false;
        return true;
    }
}
public enum Rarity { None, Common, Rare }
public sealed class PassiveXmlInfo
{
    public LorId id;
    public bool isNegative;
    public Rarity rare;
    public bool CanGivePassive=true,CanReceivePassive=true,isLock,isHide;
    public int cost,InnerTypeId=-1;
    private static readonly Dictionary<int,PassiveXmlInfo> Xml=new Dictionary<int,PassiveXmlInfo>();
    internal PassiveXmlInfo(int value) { id = new LorId(value);Xml[value]=this; }
    private PassiveXmlInfo() { }
    internal static PassiveXmlInfo Resolve(LorId id) { return Xml[id.id]; }
    // Real PassiveModel.DeepCopy intentionally copies no flags or InnerTypeId.
    internal PassiveXmlInfo PartialCopy()
    { return new PassiveXmlInfo { id=id,isNegative=isNegative,rare=rare,cost=cost,CanGivePassive=false,CanReceivePassive=false }; }
}
public enum GivePassiveState { None,Unique,Lock,Using }
internal static class TransactionHooks
{
    internal static Action<string,BookModel,PassiveModel> Hook;
    internal static readonly List<BookModel> CommittedBooks=new List<BookModel>();
    internal static void Call(string point,BookModel book=null,PassiveModel passive=null)
    {
        if(!RuinaCoop.DeckGuard.Authorized)throw new InvalidOperationException("missing passive authority scope");
        if(Hook!=null)Hook(point,book,passive);
    }
    internal static void Reset(){Hook=null;CommittedBooks.Clear();}
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
    public int BookInstanceId { get { return _bookInstanceId; } }
    public bool CanToGivePassive { get { return reservedData.givePassiveBookId==_bookInstanceId; } }
    public void InitReservedData()
    {
        reservedData=new PassiveModelSavedData {currentpassive=PassiveXmlInfo.Resolve(originData.currentpassive.id),
            receivepassivebookId=originData.receivepassivebookId,givePassiveBookId=originData.givePassiveBookId};
        TransactionHooks.Call("passive-init",null,this);
    }
    public void ReleaseSuccesionReceivePassive(bool origin)
    { var data=origin?originData:reservedData;if(data==null)return;data.currentpassive=originpassive;data.receivepassivebookId=_bookInstanceId;TransactionHooks.Call("receive-release",null,this); }
    public void ReleaseSuccesionGivePassive(bool origin)
    { var data=origin?originData:reservedData;if(data==null)return;data.givePassiveBookId=_bookInstanceId;TransactionHooks.Call("give-release",null,this); }
    public void SuccessionPassiveForReserved(PassiveModel source)
    {
        source.reservedData.currentpassive=source.originpassive.PartialCopy();source.reservedData.receivepassivebookId=source.BookInstanceId;
        source.reservedData.givePassiveBookId=BookInstanceId;
        TransactionHooks.Call("give-set",null,source);
        reservedData.currentpassive=source.originpassive;reservedData.receivepassivebookId=source.BookInstanceId;
    }
    public void ApplyReserved()
    {
        originData.currentpassive=PassiveXmlInfo.Resolve(reservedData.currentpassive.id);
        originData.receivepassivebookId=reservedData.receivepassivebookId;originData.givePassiveBookId=reservedData.givePassiveBookId;
        TransactionHooks.Call("passive-apply",null,this);
    }
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
    internal int MaxCost=6;
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
