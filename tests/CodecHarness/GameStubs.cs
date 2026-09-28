using System.Collections.Generic;

namespace UI
{
    internal enum StoryState { Close, Open, Clear }
}

namespace RuinaCoop
{
    internal enum SephirahType { Malkuth, Yesod, Hod, Netzach, Tiphereth, Gebura, Chesed, Binah, Hokma, Keter }

    internal sealed class LibraryModel
    {
        internal static LibraryModel Instance = new LibraryModel();
        internal List<LibraryFloorModel> GetOpenedFloorList() => new List<LibraryFloorModel>();
        internal int GetChapter() => 0;
        internal int GetLibraryLevel() => 0;
    }

    internal sealed class LibraryFloorModel
    {
        internal SephirahType Sephirah;
        internal int Level;
        internal List<UnitData> GetUnitDataList() => new List<UnitData>();
    }

    internal sealed class UnitData { internal string name; }
    internal sealed class StageId
    {
        internal int id;
        internal bool IsBasic() => true;
    }
    internal sealed class StageData
    {
        internal StageId id;
        internal int chapter;
        internal UI.StoryState currentState;
        internal string stageName;
    }
    internal sealed class StageClassInfoList
    {
        internal static StageClassInfoList Instance = new StageClassInfoList();
        internal List<StageData> GetAllDataList() => new List<StageData>();
    }
}
