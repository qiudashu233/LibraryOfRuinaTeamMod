using System.Collections.Generic;

namespace UnityEngine
{
    internal struct Color
    {
        internal float r, g, b, a;
        internal Color(float red, float green, float blue, float alpha = 1f)
        { r = red; g = green; b = blue; a = alpha; }
    }
}

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
        private List<LibraryFloorModel> _floorList;
        internal LibraryModel() { _floorList = OpenedFloors; }
        internal List<LibraryFloorModel> GetOpenedFloorList() => OpenedFloors;
        internal int Chapter;
        internal int GetChapter() => Chapter;
        internal int GetLibraryLevel() => 0;
    }

    internal sealed class LibraryFloorModel
    {
        internal SephirahType Sephirah;
        internal int Level;
        internal List<UnitDataModel> Units = new List<UnitDataModel>();
        internal List<UnitDataModel> GetUnitDataList() => Units;
    }

    internal enum Gender { F = 0, M = 1, N = 2, Creature = 3, EGO = 4 }

    internal sealed class UnitCustomizingData
    {
        internal bool UseCustomData;
        internal int height = 170;
        internal LorId specialCustomID = new LorId { id = -1 };
        internal int frontHairID = -1, backHairID = -1, eyeID = -1,
            browID = -1, mouthID = -1, headID = -1;
        internal UnityEngine.Color hairColor = new UnityEngine.Color(0, 0, 0);
        internal UnityEngine.Color eyeColor = new UnityEngine.Color(0, 0, 0);
        internal UnityEngine.Color skinColor = new UnityEngine.Color(1, 1, 1);
    }

    internal sealed class PassiveXmlInfo
    { internal LorId id; internal bool isNegative, isHide, isLock; internal bool CanGivePassive = true, CanReceivePassive = true; internal byte rare; internal int cost, InnerTypeId = -1; }
    internal sealed class BookPassiveInfo { internal PassiveXmlInfo passive; }
    internal sealed class PassiveModel
    {
        internal sealed class PassiveModelSavedData
        {
            internal PassiveXmlInfo currentpassive;
            internal int receivepassivebookId = -1, givePassiveBookId = -1;
        }
        internal PassiveXmlInfo originpassive;
        internal PassiveModelSavedData originData;
        internal PassiveModelSavedData reservedData;
        // Match the real game's lazy reserve lifecycle, including save-loader ctor.
        internal PassiveModel() { }
        internal PassiveModel(int instance) { }
        internal PassiveModel(LorId id, int instance, int slot)
        {
            originData = new PassiveModelSavedData
            {
                currentpassive = new PassiveXmlInfo { id = slot == 0 ? id : new LorId { id = 9999999 } },
                receivepassivebookId = instance, givePassiveBookId = instance
            };
            originpassive = originData.currentpassive;
        }
        internal void InitReservedData()
        {
            var xml = originData.currentpassive;
            reservedData = new PassiveModelSavedData
            {
                currentpassive = xml == null ? null : new PassiveXmlInfo
                { id = new LorId { id = xml.id.id, packageId = xml.id.packageId }, isNegative = xml.isNegative, rare = xml.rare },
                receivepassivebookId = originData.receivepassivebookId,
                givePassiveBookId = originData.givePassiveBookId
            };
        }
    }

    internal sealed class UnitDataModel
    {
        internal string name;
        internal BookModel bookItem;
        internal bool Locked;
        internal int MaxHp = 100;
        internal int Break = 50;
        internal BookModel defaultBook;
        internal BookModel AppearanceBook;
        internal BookModel CustomBookItem => AppearanceBook ?? bookItem;
        internal UnitCustomizingData customizeData = new UnitCustomizingData();
        internal bool isSephirah;
        internal Gender gender;
        internal Gender appearanceType;
        internal string workshopSkin;
        internal BookModel GetCustomBookItemData() => AppearanceBook;
        internal bool IsChangeItemLock() => Locked;
    }

    internal sealed class BookXmlInfo { internal bool canNotEquip, isError; }

    internal sealed class BookInventoryModel
    {
        internal static BookInventoryModel Instance = new BookInventoryModel();
        internal readonly List<BookModel> Books = new List<BookModel>();
        internal BookModel BlackSilence;
        internal List<BookModel> GetBookListAll() => Books;
        internal List<BookModel> GetBookList_equip() => Books;
        internal BookModel GetBlackSilenceBook() => BlackSilence;
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
        internal string CharacterSkin = "";
        internal readonly List<BookPassiveInfo> Passives = new List<BookPassiveInfo>();
        internal string GetCharacterName() => CharacterSkin;
        internal List<BookPassiveInfo> GetPassiveInfoList(bool ignored) => Passives;
        internal sealed class BookEquipedBookSavedData
        {
            internal int equipedPassiveBookInstanceId = -1;
            internal List<int> equipedBookIdListInPassive = new List<int>();
        }
        internal BookEquipedBookSavedData originData = new BookEquipedBookSavedData();
        internal BookEquipedBookSavedData reservedData = new BookEquipedBookSavedData();
        internal BookXmlInfo ClassInfo = new BookXmlInfo();
        internal UnitDataModel owner;
        internal int HP = 100, Break = 50;
        internal bool Basic, BlueLocked;
        internal bool IsBasicBook() => Basic;
        internal bool IsLockByBluePrimary() => BlueLocked;
        internal readonly List<PassiveModel> PassiveModels = new List<PassiveModel>();
        internal List<PassiveModel> GetPassiveModelList() => PassiveModels;
        internal int GetMaxPassiveCost() { var chapter = LibraryModel.Instance.GetChapter(); return chapter <= 3 ? 0 : chapter == 4 ? 6 : chapter == 5 ? 8 : chapter == 6 ? 10 : 12; }
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
