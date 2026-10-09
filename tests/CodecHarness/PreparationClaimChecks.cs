using System;
using RuinaCoop;

internal static class PreparationClaimChecks
{
    internal static void Run()
    {
        var checks = 0;
        Action<bool, string> check = (ok, message) => { if (!ok) throw new Exception("FAIL preparation claims: " + message); checks++; };
        var snapshot = new ProgressSnapshot { SelectedStageId = 101 };
        var floor = new ProgressSnapshot.FloorEntry { Sephirah = SephirahType.Malkuth };
        floor.Units.Add("A"); floor.Units.Add("B");
        floor.UnitReferences.Add(new object()); floor.UnitReferences.Add(new object());
        snapshot.Floors.Add(floor);
        var claims = new PrepClaims();
        claims.Reconcile(snapshot);
        check(claims.SelectFloor(snapshot, 1), "actual floor can bind");
        var prep = new PreparationSnapshot { Available = true, Reason = PreparationReason.None,
            Phase = PreparationPhase.Editing, ContextId = 1, StageId = 101, FloorId = 1 };
        prep.Participants.Add(new PreparationUnitEntry { UnitIndex = 0, CanParticipate = true, Participating = true });
        prep.Participants.Add(new PreparationUnitEntry { UnitIndex = 1, CanParticipate = true });
        claims.ReconcilePreparation(prep);
        check(claims.Apply(10, 101, 1, 1, claims.Revision, ClaimAction.Claim) == ClaimResultCode.InvalidSlot, "nonparticipant cannot claim");
        check(claims.Apply(10, 101, 1, 0, claims.Revision, ClaimAction.Claim) == ClaimResultCode.Accepted, "participant can claim");
        var revision = claims.Revision;
        claims.ReconcilePreparation(prep);
        check(claims.Revision == revision && claims.OwnerAt(0) == 10, "identical capture preserves claim");
        prep.Participants[1].Participating = true;
        claims.ReconcilePreparation(prep);
        check(claims.Revision > revision && claims.OwnerAt(0) == 10, "roster expansion invalidates events and preserves valid owner");
        check(claims.Apply(20, 101, 1, 1, claims.Revision, ClaimAction.Claim) == ClaimResultCode.Accepted, "new participant can claim");
        prep.Participants[0].Participating = false;
        claims.ReconcilePreparation(prep);
        check(claims.OwnerAt(0) == 0 && claims.OwnerAt(1) == 20, "removal clears only invalid claim");
        prep.ContextId++;
        revision = claims.Revision;
        claims.ReconcilePreparation(prep);
        check(claims.Revision > revision && claims.OwnerAt(1) == 0, "same-stage new reception clears old claims");
        claims.ReconcilePreparation(new PreparationSnapshot());
        claims.Reconcile(snapshot);
        check(snapshot.SelectedFloorId == PrepClaims.NoFloor && snapshot.ClaimOwners.Count == 0, "return to selection clears floor and owners");
        Console.WriteLine("PASS preparation claim reconciliation " + checks + " checks.");
    }
}
