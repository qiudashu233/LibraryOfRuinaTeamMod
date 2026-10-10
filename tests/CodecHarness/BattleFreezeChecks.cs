using System;
using System.Linq;
using RuinaCoop;

internal static class BattleFreezeChecks
{
    internal static void Run()
    {
        var count = 0;
        Action<bool, string> check = (valid, name) => { count++; if (!valid) throw new Exception("FAIL battle freeze: " + name); };
        var state = new PreparationState();
        var published = Fixture();
        published.Preparation = state.Preview(published.Preparation, published.ClaimRevision, published.DeckRevision, 1, new ulong[] { 2 }, published.ClaimOwners);
        check(state.Commit(published.Preparation), "publish supported reception");
        foreach (var controller in new ulong[] { 1, 2 })
        {
            PreparationReadyResultCode result;
            var ready = state.PreviewReady(controller, state.Current.Revision, true, true, out result);
            check(result == PreparationReadyResultCode.Accepted && state.Commit(ready), "controller readiness committed");
        }
        published.Preparation = state.Current;
        var offeredRevision = published.Preparation.Revision;
        var canonical = DeckMirror.EncodeContent(published);
        check(state.AllControllersReady, "offer begins with all actual controllers ready");

        // Reproduce the real integration hazard, using the production deck
        // canonical encoder rather than a test-side reimplementation.
        var erroneous = Fixture(); erroneous.DecksFrozen = true;
        check(!canonical.SequenceEqual(DeckMirror.EncodeContent(erroneous)), "battle lock metadata changes the deck canonical bytes");
        erroneous.DeckRevision++;
        var invalidated = state.Preview(erroneous.Preparation, erroneous.ClaimRevision, erroneous.DeckRevision, 1, new ulong[] { 2 }, erroneous.ClaimOwners);
        check(invalidated.Revision != offeredRevision && invalidated.Controllers.All(row => !row.Ready),
            "publishing the lock bit as equipment invalidates the offered revision and readiness");

        // Final configuration recapture retains the published metadata while
        // RelaySession.BattleActive independently enforces the edit lock.
        var recaptured = Fixture(); recaptured.DecksFrozen = published.DecksFrozen;
        check(canonical.SequenceEqual(DeckMirror.EncodeContent(recaptured)), "unchanged final equipment retains canonical bytes");
        recaptured.DeckRevision = canonical.SequenceEqual(DeckMirror.EncodeContent(recaptured)) ? published.DeckRevision : published.DeckRevision + 1;
        var validated = state.Preview(recaptured.Preparation, recaptured.ClaimRevision, recaptured.DeckRevision, 1, new ulong[] { 2 }, recaptured.ClaimOwners);
        check(validated.Revision == offeredRevision && validated.Controllers.All(row => row.Ready), "final recapture preserves offered revision and all acknowledgements");
        check(state.Commit(validated) && state.AllControllersReady, "successful validation remains eligible for commit");
        recaptured.UnitDecks[0].Cards[0] = 2;
        check(!canonical.SequenceEqual(DeckMirror.EncodeContent(recaptured)), "real equipment changes remain visible during final validation");
        recaptured.DeckRevision = published.DeckRevision + 1;
        var changed = state.Preview(recaptured.Preparation, recaptured.ClaimRevision, recaptured.DeckRevision, 1, new ulong[] { 2 }, recaptured.ClaimOwners);
        check(changed.Revision > offeredRevision && changed.Controllers.All(row => !row.Ready), "real card changes invalidate the exact offer and require fresh readiness");
        var disconnected = state.Preview(Fixture().Preparation, published.ClaimRevision, published.DeckRevision, 1, new ulong[0], published.ClaimOwners);
        check(disconnected.Revision > offeredRevision && disconnected.Controllers.All(row => !row.Ready), "controller disconnect cannot retain offered readiness");
        Console.WriteLine("PASS battle freeze/canonical readiness " + count + " checks.");
    }

    private static ProgressSnapshot Fixture()
    {
        var value = new ProgressSnapshot { SelectedStageId = 3, SelectedFloorId = 1, ClaimRevision = 4, DeckRevision = 5,
            Preparation = new PreparationSnapshot { Available = true, Reason = PreparationReason.None, Phase = PreparationPhase.Editing,
                ContextId = 20, StageId = 3, FloorId = 1, MaxUnits = 2, CurrentWaveIndex = 0 } };
        value.Preparation.Floors.Add(new PreparationFloorEntry { FloorId = 1, CanParticipate = true });
        var floor = new ProgressSnapshot.FloorEntry { Sephirah = SephirahType.Malkuth }; value.Floors.Add(floor);
        for (byte index = 0; index < 2; index++)
        {
            var deck = new ProgressSnapshot.UnitDeckEntry { UnitIdentity = (ulong)(10 + index), BookToken = (ulong)(30 + index), BookId = index + 1, Capacity = 9 };
            deck.Cards.AddRange(new[] { 1, 1, 2 }); value.UnitDecks.Add(deck); floor.Units.Add("A"); value.ClaimOwners.Add((ulong)(index + 1));
            value.Preparation.Participants.Add(new PreparationUnitEntry { UnitIndex = index, UnitIdentity = deck.UnitIdentity, CanParticipate = true, Participating = true });
        }
        value.Preparation.Waves.Add(new PreparationWaveEntry { WaveIndex = 0 });
        return value;
    }
}
