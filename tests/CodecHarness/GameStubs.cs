using System.Collections.Generic;

namespace UI
{
    internal enum StoryState { FirstOpen = 0, Open = 1, Clear = 2, Close = 3 }
}

namespace LOR_DiceSystem
{
    internal sealed class DiceCardXmlInfo { internal RuinaCoop.LorId id; }
}

namespace RuinaCoop
{
    internal enum SephirahType
    {
        None = 0, Malkuth = 1, Yesod = 2, Hod = 3, Netzach = 4,
        Tiphereth = 5, Gebura = 6, Chesed = 7, Binah = 8, Hokma = 9, Keter = 10, ETC = 11
    }

    internal sealed class LibraryModel
    {
        internal static LibraryModel Instance = new LibraryModel();
        internal List<LibraryFloorModel> OpenedFloors = new List<LibraryFloorModel>();
        internal List<LibraryFloorModel> GetOpenedFloorList() => OpenedFloors;
        internal int GetChapter() => 0;
        internal int GetLibraryLevel() => 0;
    }

    internal sealed class LibraryFloorModel
    {
        internal SephirahType Sephirah;
        internal int Level;
        internal List<UnitDataModel> Units = new List<UnitDataModel>();
        internal List<UnitDataModel> GetUnitDataList() => Units;
    }

    internal sealed class UnitDataModel
    {
        internal string name;
        internal BookModel bookItem;
        internal bool Locked;
        internal bool IsChangeItemLock() => Locked;
    }

    internal sealed class BookModel
    {
        internal LorId BookId = new LorId();
        internal int instanceId;
        internal int DeckSize = 9;
        internal bool Fixed;
        internal bool MultiDeck;
        internal bool Locked;
        internal readonly List<LOR_DiceSystem.DiceCardXmlInfo> Cards =
            new List<LOR_DiceSystem.DiceCardXmlInfo>();
        internal int GetDeckSize() => DeckSize;
        internal bool IsFixedDeck() => Fixed;
        internal bool IsMultiDeck() => MultiDeck;
        internal bool IsDeckLocked() => Locked;
        internal List<LOR_DiceSystem.DiceCardXmlInfo> GetCardListFromCurrentDeck() => Cards;
    }

    internal class LorId
    {
        internal int id;
        internal string packageId;
        internal bool IsBasic() => string.IsNullOrEmpty(packageId);
    }

    internal sealed class StageId : LorId { }
    internal sealed class StageData
    {
        internal LorId id;
        internal int chapter;
        internal UI.StoryState currentState;
        internal string stageName;
    }

    internal sealed class StageClassInfoList
    {
        internal static StageClassInfoList Instance = new StageClassInfoList();
        internal List<StageData> Stages = new List<StageData>();
        internal List<StageData> GetAllDataList() => Stages;
    }

    internal sealed class DiceCardItemModel
    {
        internal LorId Id;
        internal int num;
        internal LorId GetID() => Id;
    }

    internal sealed class InventoryModel
    {
        internal static InventoryModel Instance = new InventoryModel();
        internal readonly List<DiceCardItemModel> Cards = new List<DiceCardItemModel>();
        internal List<DiceCardItemModel> GetCardList() => Cards;
    }
}
