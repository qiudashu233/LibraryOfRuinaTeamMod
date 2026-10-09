using System.Collections.Generic;

namespace RuinaCoop
{
    // This module never invokes game singletons, constructors, or saves.
    internal sealed class ProgressSnapshot
    {
        internal PreparationSnapshot Preparation;
        internal int SelectedStageId;
        internal byte SelectedFloorId = 255;
        internal uint ClaimRevision;
        internal uint DeckRevision;
        internal readonly List<UnitDeckEntry> UnitDecks = new List<UnitDeckEntry>();
        internal readonly List<ulong> ClaimOwners = new List<ulong>();
        internal sealed class UnitDeckEntry { internal ulong UnitIdentity; }
        internal sealed class UnitDisplayEntry
        {
            internal bool Available; internal int MaxHp; internal int Break;
            internal readonly List<int> PassiveIds = new List<int>();
            internal bool AppearanceAvailable; internal int DefaultBookId; internal int CustomBookId;
            internal bool IsSephirah; internal byte Gender; internal byte AppearanceType; internal string CharacterSkin = "";
            internal bool UseCustom; internal int SpecialCustomId = -1;
            internal int FrontHair = -1; internal int BackHair = -1; internal int Eye = -1; internal int Brow = -1; internal int Mouth = -1; internal int Head = -1;
            internal uint HairColor; internal uint EyeColor; internal uint SkinColor; internal int Height = 170;
        }
    }
}
