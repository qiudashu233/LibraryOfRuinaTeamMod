using System;
using System.Collections.Generic;
using System.Reflection;
using LOR_DiceSystem;
using RuinaCoop;

internal static class Program
{
    private static int _checks;
    private const BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
    private static object Field(object value,string name){return value.GetType().GetField(name,Fields).GetValue(value);}
    private static void Check(bool value,string label){_checks++;if(!value)throw new Exception(label);}
    private sealed class Fixture
    {
        internal readonly BookModel Target=new BookModel(100,1),Old=new BookModel(101,2),Next=new BookModel(102,3),Unrelated=new BookModel(103,4);
        internal readonly UnitDataModel Unit;
        internal readonly PassiveXmlInfo Native=new PassiveXmlInfo(1001){isLock=true,CanGivePassive=false,cost=8};
        internal readonly PassiveXmlInfo Empty=new PassiveXmlInfo(9999999){CanGivePassive=false};
        internal readonly PassiveXmlInfo OldXml=new PassiveXmlInfo(2001){cost=2,InnerTypeId=20};
        internal readonly PassiveXmlInfo Positive=new PassiveXmlInfo(3001){cost=8,InnerTypeId=30};
        internal readonly PassiveXmlInfo Negative=new PassiveXmlInfo(3002){cost=-2,isNegative=true,InnerTypeId=31};
        internal readonly DiceCardXmlInfo CardA=new DiceCardXmlInfo(10),CardB=new DiceCardXmlInfo(11),Basic=new DiceCardXmlInfo(12,true);
        internal readonly List<BookModel> Books;
        internal Fixture(bool inherited=true)
        {
            InventoryModel.Instance=new InventoryModel();BookInventoryModel.Instance=new BookInventoryModel();LibraryModel.Instance=new LibraryModel();
            NativeDeckModels.Mirror=null;DeckGuard.Authorized=false;DeckGuard.Denied=false;TransactionHooks.Reset();
            var basic=new BookModel(999,0);basic.ClassInfo.optionList.Add(BookOption.Basic);
            Unit=new UnitDataModel(Target,basic);LibraryModel.Instance.Floor.Units.Add(Unit);
            Books=new List<BookModel>{Target,Old,Next,Unrelated};foreach(var book in Books)BookInventoryModel.Instance.Register(book);
            Add(Target,Native);Add(Target,Empty);Add(Target,Empty);Add(Old,OldXml);Add(Next,Positive);Add(Next,Negative);Add(Unrelated,new PassiveXmlInfo(4001){cost=3});
            if(inherited)
            {
                Target.originData.equipedBookIdListInPassive.Add(Old.instanceId);Old.originData.equipedPassiveBookInstanceId=Target.instanceId;
                Target.GetPassiveModelList()[1].originData.currentpassive=OldXml;
                Target.GetPassiveModelList()[1].originData.receivepassivebookId=Old.instanceId;
                Old.GetPassiveModelList()[0].originData.givePassiveBookId=Target.instanceId;
            }
            Target.CurrentDeck.GetCardList_nocopy().Add(CardB);
            Next.GetDeckAll_nocopy()[0].GetCardList_nocopy().Add(CardA);Next.GetDeckAll_nocopy()[1].GetCardList_nocopy().Add(CardA);
            Next.GetDeckAll_nocopy()[2].GetCardList_nocopy().Add(CardB);Next.GetDeckAll_nocopy()[3].GetCardList_nocopy().Add(Basic);
            InventoryModel.Instance.Add(CardA);InventoryModel.Instance.Add(CardA);
        }
        private static void Add(BookModel book,PassiveXmlInfo xml){book.GetPassiveModelList().Add(new PassiveModel(book.instanceId,xml));}
        internal ProgressSnapshot Capture()
        {
            var snapshot=new ProgressSnapshot();snapshot.ClaimOwners.Add(77);
            var floor=new ProgressSnapshot.FloorEntry{Sephirah=Unit.OwnerSephirah};floor.Units.Add("unit");floor.UnitReferences.Add(Unit);snapshot.Floors.Add(floor);
            snapshot.UnitDecks.Add(new ProgressSnapshot.UnitDeckEntry{UnitIdentity=90,BookToken=Token(Target),BookId=Target.BookId.id,BookInstanceId=Target.instanceId});
            foreach(var book in Books)
            {
                var row=new ProgressSnapshot.PassiveBookEntry{BookToken=Token(book),MaxCost=book.GetMaxPassiveCost()};
                var bound=book.originData.equipedPassiveBookInstanceId!=-1;
                snapshot.CoreBooks.Add(new ProgressSnapshot.CoreBookEntry{BookToken=Token(book),BookId=book.BookId.id,BookInstanceId=book.instanceId,
                    BookReference=book,Flags=book.owner!=null?CoreBookFlags.Equipped:bound?CoreBookFlags.PassiveBound:CoreBookFlags.None,
                    OccupiedFloorId=book.owner!=null?(byte)1:(byte)0,OccupiedUnitIndex=book.owner!=null?(byte)0:(byte)255});
                row.Flags=book==Target?PassiveBookFlags.ReceiverAllowed:PassiveBookFlags.SourceAllowed;
                if(bound)row.ReceiverBookToken=Token(Books.Find(b=>b.instanceId==book.originData.equipedPassiveBookInstanceId));
                foreach(var id in book.originData.equipedBookIdListInPassive)row.SourceTokens.Add(Token(Books.Find(b=>b.instanceId==id)));
                foreach(var model in book.GetPassiveModelList())
                {
                    var native=model.originpassive;var current=model.originData.currentpassive;
                    var slot=new ProgressSnapshot.PassiveSlotEntry{OriginId=native.id.id,CurrentId=current.id.id,Cost=native.cost,CurrentCost=current.cost,
                        InnerTypeId=native.InnerTypeId,OriginRarity=(byte)native.rare,CurrentRarity=(byte)current.rare,CurrentNegative=current.isNegative};
                    slot.Flags=(native.CanGivePassive?PassiveSlotFlags.CanGive:0)|(native.CanReceivePassive?PassiveSlotFlags.CanReceive:0)|
                        (native.isLock?PassiveSlotFlags.Locked:0)|(native.isHide?PassiveSlotFlags.Hidden:0)|(native.isNegative?PassiveSlotFlags.Negative:0)|
                        (model.originData.givePassiveBookId!=book.instanceId?PassiveSlotFlags.Given:0);
                    if(model.originData.receivepassivebookId!=book.instanceId)
                    {
                        var source=Books.Find(b=>b.instanceId==model.originData.receivepassivebookId);slot.SourceBookToken=Token(source);
                        slot.SourceSlotIndex=(byte)source.GetPassiveModelList().FindIndex(p=>p.originpassive.id==current.id&&p.originData.givePassiveBookId==book.instanceId);
                    }
                    row.Slots.Add(slot);
                }
                snapshot.PassiveBooks.Add(row);
            }
            return snapshot;
        }
        internal PassiveRequest Request(ProgressSnapshot snapshot,bool restore=false,bool emptySource=false)
        {
            var slots=new PassiveSelection[Target.GetPassiveModelList().Count];
            for(var i=0;i<slots.Length;i++)slots[i]=Restore(Target.GetPassiveModelList()[i].originpassive.id.id);
            if(!restore&&!emptySource)
            { slots[1]=Import(Next,0,Positive.id.id);slots[2]=Import(Next,1,Negative.id.id); }
            return new PassiveRequest{RequestId=1,StageId=snapshot.SelectedStageId,FloorId=1,UnitIndex=0,UnitIdentity=90,BookToken=Token(Target),
                ClaimRevision=snapshot.ClaimRevision,DeckRevision=snapshot.DeckRevision,Slots=slots,SourceBookTokens=restore?new ulong[0]:new[]{Token(Next)}};
        }
    }
    private static ulong Token(BookModel book){return (ulong)book.instanceId*100;}
    private static PassiveSelection Restore(int id){return new PassiveSelection{Mode=PassiveSelectionMode.RestoreNative,ExpectedOriginPassiveId=id,SourceSlotIndex=255};}
    private static PassiveSelection Import(BookModel book,byte index,int id){return new PassiveSelection{Mode=PassiveSelectionMode.Inherit,SourceBookToken=Token(book),SourceSlotIndex=index,ExpectedOriginPassiveId=id};}

    // Capture reference identity, nullable lifecycle buffers, order, and values.
    // This checks the real shared savepoint, rather than another rollback routine.
    private sealed class Before
    {
        private readonly Fixture _fixture;
        private readonly List<Action> _checks=new List<Action>();
        internal Before(Fixture fixture)
        {
            _fixture=fixture;var cards=InventoryModel.Instance.GetCardListOrigin();var cardRows=cards.ToArray();var nums=new int[cardRows.Length];
            for(var i=0;i<nums.Length;i++)nums[i]=cardRows[i].num;
            _checks.Add(()=>{Check(ReferenceEquals(cards,InventoryModel.Instance.GetCardListOrigin()),"inventory list identity");Check(cards.Count==cardRows.Length,"inventory count");
                for(var i=0;i<cardRows.Length;i++){Check(ReferenceEquals(cards[i],cardRows[i]),"inventory order/row identity");Check(cards[i].num==nums[i],"inventory quantity");}});
            var bookList=(List<BookModel>)Field(BookInventoryModel.Instance,"_bookList");var bookRows=bookList.ToArray();
            _checks.Add(()=>{Check(ReferenceEquals(bookList,Field(BookInventoryModel.Instance,"_bookList")),"book inventory identity");Check(bookList.Count==bookRows.Length,"book inventory count");
                for(var i=0;i<bookRows.Length;i++)Check(ReferenceEquals(bookList[i],bookRows[i]),"book inventory order");});
            var unitRaw=Field(fixture.Unit,"_bookItem");var appearance=Field(fixture.Unit,"_CustomBookItem");var gender=fixture.Unit.appearanceType;
            _checks.Add(()=>{Check(ReferenceEquals(unitRaw,Field(fixture.Unit,"_bookItem")),"unit equipment binding");Check(ReferenceEquals(appearance,Field(fixture.Unit,"_CustomBookItem")),"unit appearance binding");Check(gender==fixture.Unit.appearanceType,"unit appearance mode");});
            foreach(var book in fixture.Books)
            {
                var captured=book;var owner=book.owner;var id=book.instanceId;var current=book.CurrentDeck;
                var decks=book.GetDeckAll_nocopy();var deckRows=decks.ToArray();
                _checks.Add(()=>{Check(captured.owner==owner&&captured.instanceId==id,"book owner/instance");Check(ReferenceEquals(captured.CurrentDeck,current),"current deck pointer");
                    Check(ReferenceEquals(captured.GetDeckAll_nocopy(),decks)&&decks.Count==deckRows.Length,"deck collection identity");
                    for(var i=0;i<deckRows.Length;i++)Check(ReferenceEquals(decks[i],deckRows[i]),"deck identity/order");});
                foreach(var deck in decks)
                { var capturedDeck=deck;var list=deck.GetCardList_nocopy();var rows=list.ToArray();
                    _checks.Add(()=>{Check(ReferenceEquals(capturedDeck.GetCardList_nocopy(),list)&&list.Count==rows.Length,"deck card list identity/count");for(var i=0;i<rows.Length;i++)Check(ReferenceEquals(list[i],rows[i]),"deck card order");}); }
                CaptureBookData(book,false);CaptureBookData(book,true);
                var models=book.GetPassiveModelList();var modelRows=models.ToArray();
                _checks.Add(()=>{Check(ReferenceEquals(captured.GetPassiveModelList(),models)&&models.Count==modelRows.Length,"passive list identity/count");for(var i=0;i<modelRows.Length;i++)Check(ReferenceEquals(models[i],modelRows[i]),"passive list order");});
                foreach(var model in models)
                { var capturedModel=model;var native=model.originpassive;var instance=model.BookInstanceId;
                    _checks.Add(()=>{Check(ReferenceEquals(capturedModel.originpassive,native),"native XML identity");Check(capturedModel.BookInstanceId==instance,"passive instance");});CapturePassive(model,false);CapturePassive(model,true); }
            }
        }
        private void CaptureBookData(BookModel book,bool reserved)
        {
            var data=reserved?book.reservedData:book.originData;var list=data.equipedBookIdListInPassive;var ids=list.ToArray();var receiver=data.equipedPassiveBookInstanceId;
            _checks.Add(()=>{var actual=reserved?book.reservedData:book.originData;Check(ReferenceEquals(actual,data),"book saved buffer identity");Check(ReferenceEquals(actual.equipedBookIdListInPassive,list),"book source list identity");
                Check(actual.equipedPassiveBookInstanceId==receiver&&list.Count==ids.Length,"book source relation/count");for(var i=0;i<ids.Length;i++)Check(list[i]==ids[i],"book source order");});
        }
        private void CapturePassive(PassiveModel model,bool reserved)
        {
            var data=reserved?model.reservedData:model.originData;var xml=data==null?null:data.currentpassive;var give=data==null?-1:data.givePassiveBookId;var receive=data==null?-1:data.receivepassivebookId;
            _checks.Add(()=>{var actual=reserved?model.reservedData:model.originData;Check(ReferenceEquals(actual,data),"passive saved buffer identity including null");
                if(actual!=null){Check(ReferenceEquals(actual.currentpassive,xml),"current XML identity");Check(actual.givePassiveBookId==give&&actual.receivepassivebookId==receive,"passive relation values");}});
        }
        internal void Unchanged(){foreach(var check in _checks)check();Check(!DeckGuard.Authorized,"authority scope restored");}
    }

    private static void Success()
    {
        var f=new Fixture();var snapshot=f.Capture();var request=f.Request(snapshot);var published=0;string reason;
        var result=PassiveTransaction.Apply(f.Unit,snapshot,request,()=>{published++;return true;},out reason);
        Check(result==EquipmentTransactionResult.Accepted,"valid whole set accepted: "+reason);Check(published==1,"one authority publication");
        Check(f.Target.originData.equipedBookIdListInPassive.Count==1&&f.Target.originData.equipedBookIdListInPassive[0]==f.Next.instanceId,"final source replaces old");
        Check(f.Old.originData.equipedPassiveBookInstanceId==-1&&f.Old.GetPassiveModelList()[0].originData.givePassiveBookId==f.Old.instanceId,"old donor unlocked");
        Check(f.Target.GetPassiveModelList()[0].originData.currentpassive.id==f.Native.id,"locked native retained free");
        Check(f.Target.GetPassiveModelList()[1].originData.currentpassive.id==f.Positive.id&&f.Target.GetPassiveModelList()[2].originData.currentpassive.id==f.Negative.id,"final imports exact target order");
        Check(f.Target.GetCurrentPassiveCost(true)==6,"negative cost included");
        foreach(var p in f.Next.GetPassiveModelList())Check(p.originData.givePassiveBookId==f.Target.instanceId,"selected source passive bound");
        foreach(var deck in f.Next.GetDeckAll_nocopy())Check(deck.GetCardList_nocopy().Count==0,"all donor decks returned");
        var inventory=InventoryModel.Instance.GetCardListOrigin();Check(inventory.Find(r=>r.Card==f.CardA).num==4,"existing stock incremented twice");
        Check(inventory.Find(r=>r.Card==f.CardB).num==1&&inventory.Find(r=>r.Card==f.Basic)==null,"new stock added and basic infinite omitted");
        Check(f.Target.CurrentDeck.GetCardList_nocopy().Count==1&&f.Target.CurrentDeck.GetCardList_nocopy()[0]==f.CardB,"receiver combat deck retained");
        Check(TransactionHooks.CommittedBooks.Count==3&&!TransactionHooks.CommittedBooks.Contains(f.Unrelated),"only relation union commits");
        Check(f.Unrelated.GetPassiveModelList()[0].reservedData==null,"unrelated loaded passive remains uninitialized");
        Check(!EquipmentTransaction.HasPassiveDraft(f.Target)&&!EquipmentTransaction.HasPassiveDraft(f.Next)&&!EquipmentTransaction.HasPassiveDraft(f.Old),"committed buffers consistent");
        Check(!DeckGuard.Authorized,"success authority restored");
        snapshot=f.Capture();request=f.Request(snapshot,true);result=PassiveTransaction.Apply(f.Unit,snapshot,request,()=>true,out reason);
        Check(result==EquipmentTransactionResult.Accepted,"withdraw all accepted: "+reason);Check(f.Target.originData.equipedBookIdListInPassive.Count==0,"all sources withdrawn");
        for(var i=1;i<3;i++)Check(f.Target.GetPassiveModelList()[i].originData.currentpassive.id.id==9999999,"native empty restored");
        Check(f.Next.originData.equipedPassiveBookInstanceId==-1,"withdrawn source free");
        Check(InventoryModel.Instance.GetCardListOrigin().Find(r=>r.Card==f.CardA).num==4,"withdraw does not recreate donor deck or cards");

        f=new Fixture(false);snapshot=f.Capture();request=f.Request(snapshot,false,true);
        result=PassiveTransaction.Apply(f.Unit,snapshot,request,()=>true,out reason);
        Check(result==EquipmentTransactionResult.Accepted,"unused source selection accepted: "+reason);
        Check(f.Next.originData.equipedPassiveBookInstanceId==f.Target.instanceId&&f.Next.GetPassiveModelList()[0].originData.givePassiveBookId==f.Next.instanceId,"unused source book bound but slots not borrowed");
        Check(f.Next.CurrentDeck.GetCardList_nocopy().Count==0,"unused source still returns deck");
    }

    private static void Failure(string point,int occurrence=1)
    {
        var f=new Fixture();var snapshot=f.Capture();var request=f.Request(snapshot);var before=new Before(f);var seen=0;var published=0;
        TransactionHooks.Hook=(p,b,m)=>{if(p==point&&++seen==occurrence)throw new Exception("injected "+point);};
        string reason;var result=PassiveTransaction.Apply(f.Unit,snapshot,request,()=>{published++;return true;},out reason);
        Check(result==EquipmentTransactionResult.Failed,"fault "+point+" returns failed: "+reason);Check(seen>=occurrence,"fault point reached "+point);
        Check(published==0,"fault never published "+point);before.Unchanged();
    }
    private static void PublicationFailures()
    {
        for(var mode=0;mode<3;mode++)
        {
            var f=new Fixture();var snapshot=f.Capture();var request=f.Request(snapshot);var before=new Before(f);var selected=mode;
            string reason;var result=PassiveTransaction.Apply(f.Unit,snapshot,request,()=>
            {
                if(selected==1)throw new Exception("capture failed");
                if(selected==2)
                {
                    typeof(BookModel).GetField("_activatedAllPassives",Fields).SetValue(f.Next,new List<PassiveModel>());
                    f.Target.originData.equipedBookIdListInPassive=new List<int>{909};f.Target.owner=null;f.Next.instanceId=909;
                    typeof(InventoryModel).GetField("_cardList",Fields).SetValue(InventoryModel.Instance,new List<DiceCardItemModel>());
                    typeof(BookInventoryModel).GetField("_bookList",Fields).SetValue(BookInventoryModel.Instance,new List<BookModel>());
                    typeof(DeckModel).GetField("_deck",Fields).SetValue(f.Target.CurrentDeck,new List<DiceCardXmlInfo>());
                }
                return false;
            },out reason);
            Check(result==EquipmentTransactionResult.Failed,"failed publication rollback "+mode);before.Unchanged();
        }
    }
    private static void InventoryEdges()
    {
        var f=new Fixture(false);var noStock=new DiceCardXmlInfo(20);noStock.optionList.Add(CardOption.NoInventory);
        var existingNoStock=new DiceCardXmlInfo(21);existingNoStock.optionList.Add(CardOption.NoInventory);
        InventoryModel.Instance.GetCardListOrigin().Add(new DiceCardItemModel(f.Basic,5));
        InventoryModel.Instance.GetCardListOrigin().Add(new DiceCardItemModel(existingNoStock,2));
        f.Next.GetDeckAll_nocopy()[3].GetCardList_nocopy().Add(noStock);
        f.Next.GetDeckAll_nocopy()[1].GetCardList_nocopy().Add(existingNoStock);
        f.Next.GetDeckAll_nocopy()[2].GetCardList_nocopy().Add(f.CardB);
        var original=InventoryModel.Instance.GetCardListOrigin().ToArray();var snapshot=f.Capture();var request=f.Request(snapshot,false,true);string reason;
        var result=PassiveTransaction.Apply(f.Unit,snapshot,request,()=>true,out reason);
        Check(result==EquipmentTransactionResult.Accepted,"basic/no-inventory existing rows and new-row sorting accepted: "+reason);
        var inventory=InventoryModel.Instance.GetCardListOrigin();
        Check(inventory.Find(row=>row.Card==f.Basic).num==6,"existing Basic stock increments before option check");
        Check(inventory.Find(row=>row.Card==existingNoStock).num==3,"existing NoInventory stock increments before option check");
        Check(inventory.Find(row=>row.Card==noStock)==null,"absent NoInventory row remains absent");
        Check(inventory.Find(row=>row.Card==f.CardB).num==2,"repeated new combat page creates one row with exact quantity");
        foreach(var row in original)Check(inventory.Contains(row),"old inventory row identity retained after sort");
        Check(!ReferenceEquals(inventory[0],original[0]),"new ordinary row triggered inventory reorder");

        f=new Fixture(false);f.Next.CurrentDeck.GetCardList_nocopy().Clear();var hidden=f.Next.GetDeckAll_nocopy()[2].GetCardList_nocopy();var hiddenCard=hidden[0];
        var beforeInventory=InventoryModel.Instance.GetCardListOrigin().ToArray();snapshot=f.Capture();request=f.Request(snapshot,false,true);
        result=PassiveTransaction.Apply(f.Unit,snapshot,request,()=>true,out reason);
        Check(result==EquipmentTransactionResult.Accepted,"ordinary current empty with nonempty hidden deck accepted: "+reason);
        Check(f.Next.originData.equipedPassiveBookInstanceId==f.Target.instanceId,"hidden-deck source still bound");
        Check(hidden.Count==1&&ReferenceEquals(hidden[0],hiddenCard),"native empty-current condition preserves hidden stored deck");
        Check(InventoryModel.Instance.GetCardListOrigin().Count==beforeInventory.Length&&InventoryModel.Instance.GetCardListOrigin()[0].num==2,"empty-current source transfers no inventory");

        f=new Fixture(false);var outside=new DeckModel();outside.GetCardList_nocopy().Add(f.CardA);
        typeof(BookModel).GetField("_deck",Fields).SetValue(f.Next,outside);
        snapshot=f.Capture();request=f.Request(snapshot,false,true);result=PassiveTransaction.Apply(f.Unit,snapshot,request,()=>true,out reason);
        Check(result==EquipmentTransactionResult.Accepted,"current deck outside stored list returns exact cards: "+reason);
        Check(outside.GetCardList_nocopy().Count==0&&InventoryModel.Instance.GetCardListOrigin().Find(row=>row.Card==f.CardA).num==5,"external current deck transfer counted once");

        f=new Fixture(false);f.Next.GetDeckAll_nocopy()[1]=f.Next.CurrentDeck;
        snapshot=f.Capture();request=f.Request(snapshot,false,true);result=PassiveTransaction.Apply(f.Unit,snapshot,request,()=>true,out reason);
        Check(result==EquipmentTransactionResult.Accepted,"repeated current/stored deck references accepted: "+reason);
        Check(InventoryModel.Instance.GetCardListOrigin().Find(row=>row.Card==f.CardA).num==3,"aliased deck returns card once despite multiple native visits");

        f=new Fixture(false);
        typeof(DeckModel).GetField("_deck",Fields).SetValue(f.Next.GetDeckAll_nocopy()[1],f.Next.CurrentDeck.GetCardList_nocopy());
        snapshot=f.Capture();request=f.Request(snapshot,false,true);result=PassiveTransaction.Apply(f.Unit,snapshot,request,()=>true,out reason);
        Check(result==EquipmentTransactionResult.Accepted,"distinct deck objects sharing one backing list accepted: "+reason);
        Check(InventoryModel.Instance.GetCardListOrigin().Find(row=>row.Card==f.CardA).num==3,"shared backing list quantity counted once");
    }
    private static void SilentInventoryFailures()
    {
        for(var mode=0;mode<6;mode++)
        {
            var f=new Fixture();var snapshot=f.Capture();var request=f.Request(snapshot);var before=new Before(f);var selected=mode;var changed=false;var published=0;
            TransactionHooks.Hook=(point,book,passive)=>
            {
                var stage=selected==5?"change":"book-apply";
                if(changed||point!=stage)return;
                if(selected!=5&&!ReferenceEquals(book,f.Next))return;
                changed=true;var inventory=InventoryModel.Instance.GetCardListOrigin();
                if(selected==0)inventory.Find(row=>row.Card==f.CardA).num--;
                if(selected==1)inventory.Find(row=>row.Card==f.CardB).num++;
                if(selected==2)inventory.RemoveAt(0);
                if(selected==3){var index=inventory.FindIndex(candidate=>candidate.Card==f.CardA);var row=inventory[index];inventory[index]=new DiceCardItemModel(row.Card,row.num);}
                if(selected==4)inventory.Add(new DiceCardItemModel(new DiceCardXmlInfo(999),1));
                if(selected==5)inventory[0].num++;
            };
            string reason;var result=PassiveTransaction.Apply(f.Unit,snapshot,request,()=>{published++;return true;},out reason);
            Check(changed&&result==EquipmentTransactionResult.Failed,"silent inventory change rejected by postcondition "+mode+": "+reason);
            Check(published==0,"silent inventory change never published "+mode);before.Unchanged();
        }
    }
    private static void Rejection(Action<Fixture,ProgressSnapshot,PassiveRequest> mutate,string label)
    {
        var f=new Fixture();var snapshot=f.Capture();var request=f.Request(snapshot);mutate(f,snapshot,request);var before=new Before(f);var published=0;
        string reason;var result=PassiveTransaction.Apply(f.Unit,snapshot,request,()=>{published++;return true;},out reason);
        Check(result==EquipmentTransactionResult.Rejected,"reject "+label+": "+reason);Check(published==0,"rejection no publish "+label);before.Unchanged();
    }
    private static void Rejections()
    {
        Rejection((f,s,r)=>f.Unit.Locked=true,"equipment lock");
        Rejection((f,s,r)=>{LibraryModel.Instance.BlueClear=true;LibraryModel.Instance.PlayHistory.Start_TheBlueReverberationPrimaryBattle=1;},"blue lock");
        Rejection((f,s,r)=>f.Target.owner=null,"owner inconsistent");
        Rejection((f,s,r)=>LibraryModel.Instance.Floor.Units.Clear(),"roster removed");
        Rejection((f,s,r)=>f.Next.owner=new UnitDataModel(null,null),"selected source newly equipped");
        Rejection((f,s,r)=>f.Old.owner=new UnitDataModel(null,null),"withdrawn old source newly equipped");
        Rejection((f,s,r)=>{var other=new UnitDataModel(f.Old,null);f.Old.owner=null;LibraryModel.Instance.Floor.Units.Add(other);},"ownerless old source still used");
        Rejection((f,s,r)=>{var other=new UnitDataModel(null,null);typeof(UnitDataModel).GetField("_CustomBookItem",Fields).SetValue(other,f.Old);LibraryModel.Instance.Floor.Units.Add(other);},"old source used as appearance");
        Rejection((f,s,r)=>f.Old.originData.equipedPassiveBookInstanceId=99,"old source changed receiver");
        Rejection((f,s,r)=>f.Next.originData.equipedBookIdListInPassive.Add(f.Unrelated.instanceId),"source is receiver");
        Rejection((f,s,r)=>f.Next.reservedData.equipedPassiveBookInstanceId=42,"book-only pending draft");
        Rejection((f,s,r)=>{var p=f.Old.GetPassiveModelList()[0];p.reservedData=new PassiveModel.PassiveModelSavedData{currentpassive=p.originpassive,receivepassivebookId=f.Old.instanceId,givePassiveBookId=55};},"withdraw old source pending draft");
        Rejection((f,s,r)=>BookInventoryModel.Instance.Register(new BookModel(999,f.Next.instanceId)),"duplicate instance");
        Rejection((f,s,r)=>f.Next.GetPassiveModelList().Reverse(),"stale source slot ordering");
        Rejection((f,s,r)=>f.Target.GetPassiveModelList()[1].originData.currentpassive=f.Positive,"old inherited changed");
        Rejection((f,s,r)=>f.Old.GetPassiveModelList()[0].originData.givePassiveBookId=f.Old.instanceId,"broken reverse link");
        Rejection((f,s,r)=>f.Next.Fixed=true,"fixed source");
        Rejection((f,s,r)=>f.Next.BlueLocked=true,"blue locked source");
        Rejection((f,s,r)=>f.Next.BookId.Workshop=true,"workshop source");
        Rejection((f,s,r)=>NativeDeckModels.Mirror=f.Target,"mirror receiver");
        Rejection((f,s,r)=>f.Positive.CanGivePassive=false,"non inheritable source");
        Rejection((f,s,r)=>f.Positive.isHide=true,"hidden source");
        Rejection((f,s,r)=>f.Positive.InnerTypeId=31,"duplicate inner type");
        Rejection((f,s,r)=>LibraryModel.Instance.MaxCost=5,"host chapter cost changed");
        Rejection((f,s,r)=>{s.DecksFrozen=true;},"reception frozen");
        Rejection((f,s,r)=>{s.PreparationParticipationAllowed=false;
            Check(PassiveAuthority.Validate(s,77,r)==PassiveResultCode.NotReady,"unselected librarian denied by production passive authority");},"librarian removed from preparation roster");
        Rejection((f,s,r)=>r.Slots[0]=Import(f.Next,0,f.Positive.id.id),"locked native import");
        Rejection((f,s,r)=>r.Slots[2]=Import(f.Next,0,f.Positive.id.id),"source slot borrowed twice");
    }
    private static void Main()
    {
        Success();
        foreach(var point in new[]{"book-init","passive-init","give-release","receive-release","source-release","source-equip","give-set","change","book-apply-links","card-return","passive-apply","book-apply"})Failure(point);
        Failure("card-return",3);Failure("passive-apply",4);Failure("book-apply",3);
        PublicationFailures();InventoryEdges();SilentInventoryFailures();Rejections();
        var f=new Fixture();var snapshot=f.Capture();var request=f.Request(snapshot);var before=new Before(f);DeckGuard.Denied=true;string reason;
        Check(PassiveTransaction.Apply(f.Unit,snapshot,request,()=>true,out reason)==EquipmentTransactionResult.Failed,"authority denial fails before mutation");before.Unchanged();
        Console.WriteLine("PASS: "+_checks+" passive transaction checks (production transaction/savepoint/protocol/authority; no real game methods). ");
    }
}
