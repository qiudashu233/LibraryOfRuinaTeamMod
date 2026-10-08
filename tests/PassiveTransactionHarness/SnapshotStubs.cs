using System;
using System.Collections.Generic;
namespace RuinaCoop
{
    internal enum CoreBooksReason : byte { None, NotCaptured }
    internal enum PassivesReason : byte { None, NotCaptured }
    internal enum CoreBookKind : byte { Ordinary, Default, Special }
    [Flags] internal enum CoreBookFlags : ushort { None=0, Equipped=1, PassiveBound=2, OwnerMismatch=256 }
    [Flags] internal enum PassiveBookFlags : byte { None=0, ReceiverAllowed=1, SourceAllowed=2, Unsupported=4 }
    [Flags] internal enum PassiveSlotFlags : byte { None=0, CanGive=1, Locked=2, Negative=4, Hidden=8, CanReceive=16, Given=32 }
    internal static class PrepClaims { internal const byte NoFloor=0; }
    internal static class PassiveMirror { internal const int EmptyId=9999999, MaxSlots=64; }
    internal static class EquipmentMirror
    {
        internal static bool TryResolveBook(ProgressSnapshot snapshot, ulong token, out BookModel book)
        { var row=snapshot.CoreBooks.Find(value=>value.BookToken==token);book=row==null?null:row.BookReference as BookModel;return book!=null; }
    }
    internal sealed class ProgressSnapshot
    {
        internal int SelectedStageId=10; internal byte SelectedFloorId=1;
        internal uint ClaimRevision=1, DeckRevision=2;
        internal bool DecksFrozen, CoreBooksAvailable=true, PassivesAvailable=true;
        internal CoreBooksReason CoreBooksReason; internal PassivesReason PassivesReason;
        internal readonly List<CoreBookEntry> CoreBooks=new List<CoreBookEntry>();
        internal readonly List<PassiveBookEntry> PassiveBooks=new List<PassiveBookEntry>();
        internal readonly List<UnitDeckEntry> UnitDecks=new List<UnitDeckEntry>();
        internal readonly List<FloorEntry> Floors=new List<FloorEntry>();
        internal readonly List<ulong> ClaimOwners=new List<ulong>();
        internal sealed class CoreBookEntry
        {
            internal ulong BookToken; internal int BookId,BookInstanceId; internal CoreBookKind Kind;
            internal CoreBookFlags Flags; internal byte OccupiedFloorId,OccupiedUnitIndex=255; internal object BookReference;
        }
        internal sealed class PassiveBookEntry
        {
            internal ulong BookToken, ReceiverBookToken; internal PassiveBookFlags Flags;
            internal int MaxCost; internal byte MaxSources=4;
            internal readonly List<ulong> SourceTokens=new List<ulong>();
            internal readonly List<PassiveSlotEntry> Slots=new List<PassiveSlotEntry>();
        }
        internal sealed class PassiveSlotEntry
        {
            internal int OriginId,CurrentId,Cost,CurrentCost,InnerTypeId=-1; internal PassiveSlotFlags Flags;
            internal byte OriginRarity, CurrentRarity; internal bool CurrentNegative;
            internal ulong SourceBookToken; internal byte SourceSlotIndex=255;
        }
        internal sealed class UnitDeckEntry
        { internal ulong UnitIdentity,BookToken; internal int BookId,BookInstanceId; internal bool Fixed,MultiDeck; }
        internal sealed class FloorEntry
        { internal SephirahType Sephirah; internal readonly List<string> Units=new List<string>(); internal readonly List<object> UnitReferences=new List<object>(); }
    }
}
