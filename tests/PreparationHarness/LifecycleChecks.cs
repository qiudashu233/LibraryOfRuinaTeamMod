using System;
using RuinaCoop;

internal static class LifecycleChecks
{
    private sealed class Stage
    {
        internal readonly int Id;
        internal Stage(int id) { Id = id; }
        public override bool Equals(object other) { return other is Stage && ((Stage)other).Id == Id; }
        public override int GetHashCode() { return Id; }
    }

    internal static void Run(Action<bool, string> check)
    {
        var state = new PreparationLifecycle<Stage>();
        var first = new Stage(10001);
        check(state.RoomId == 0 && state.Stage == null && state.ContextId == 0,
            "Lifecycle starts outside a reception");
        check(!state.Matches(0, null), "No empty reception identity can match");

        check(state.ObserveRoom(11, first, true), "Joining a new room observes a room change");
        var firstContext = state.ContextId;
        check(firstContext != 0 && state.RoomId == 11 && ReferenceEquals(state.Stage, first),
            "Joining while preparation is open adopts its native stage");
        check(state.Matches(11, first) && !state.Matches(12, first) && !state.Matches(11, null),
            "Reception identity includes room and non-null native stage");
        check(!state.Begin(11, first) && state.ContextId == firstContext,
            "Prepare callback following implicit adoption retains the same context");

        var sameId = new Stage(10001);
        check(first.Equals(sameId) && !state.Matches(11, sameId),
            "Equal stage IDs do not replace native reference identity");
        check(!state.ObserveRoom(11, sameId, true) && state.Matches(11, first),
            "Periodic observation cannot silently replace an active reception");
        check(!state.ObserveRoom(11, first, false) && state.ContextId == firstContext,
            "Temporary UI phases and canceled confirmation do not end an active reception");

        state.End();
        check(state.RoomId == 11 && state.Stage == null && state.ContextId == 0,
            "Confirmed exit clears reception while retaining the room");
        check(!state.Matches(11, first), "Exited reception cannot authorize old input");
        check(!state.ObserveRoom(11, first, true) && state.Stage == null && state.ContextId == 0,
            "Old phase five during exit animation cannot resurrect an ended reception");
        check(!state.ObserveRoom(11, sameId, true) && state.Stage == null,
            "Same-room observation cannot create a new invitation after exit");

        check(state.Begin(11, sameId), "Explicit reinvitation begins a fresh native stage");
        var secondContext = state.ContextId;
        check(secondContext > firstContext && state.Matches(11, sameId),
            "Reinvitation of the same stage ID receives a distinct context");
        check(!state.Begin(11, sameId) && state.ContextId == secondContext,
            "Repeated prepare callbacks do not issue another context");
        check(state.Begin(11, first) && state.ContextId > secondContext,
            "Replacing an active native stage creates a fresh reception");
        var thirdContext = state.ContextId;

        check(state.ObserveRoom(12, first, false) && state.RoomId == 12 && state.Stage == null && state.ContextId == 0,
            "Joining another room outside preparation clears the old reception");
        check(!state.ObserveRoom(12, first, true) && state.Stage == null,
            "Entering preparation later in the same room requires an explicit begin");
        check(state.Begin(12, first) && state.ContextId > thirdContext,
            "Context IDs remain unique across room changes");
        var fourthContext = state.ContextId;
        check(state.ObserveRoom(13, sameId, true) && state.ContextId > fourthContext && state.Matches(13, sameId),
            "Late joining a different prepared room adopts exactly one fresh context");
        var fifthContext = state.ContextId;
        check(!state.ObserveRoom(13, sameId, true) && state.ContextId == fifthContext,
            "Repeated new-room observation cannot repeatedly adopt");

        ExpectNull(() => state.Begin(13, null), check,
            "Explicit preparation rejects a null native stage");
        check(state.ContextId == fifthContext && state.Matches(13, sameId),
            "A null callback in the current room cannot replace its active reception");
        check(state.ObserveRoom(14, null, true) && state.Stage == null && state.ContextId == 0,
            "New room with no native stage does not adopt a preparation");
        ExpectNull(() => state.Begin(15, null), check,
            "A null callback in a new room still rejects a missing native stage");
        check(state.RoomId == 15 && state.Stage == null && state.ContextId == 0,
            "Room change is observed before an invalid begin is rejected");
        check(state.Begin(15, first) && state.ContextId > fifthContext,
            "Rejected callbacks do not permit reuse of an old context");
        var sixthContext = state.ContextId;
        check(state.ObserveRoom(0, first, true) && state.RoomId == 0 && state.Stage == null && state.ContextId == 0,
            "Leaving the room clears preparation without adopting a stale stage");
        ExpectInvalidRoom(() => state.Begin(0, first), check,
            "Explicit preparation rejects an absent room");
        check(state.Stage == null && state.ContextId == 0,
            "Absent-room rejection leaves no active reception");
        check(state.Begin(15, first) && state.ContextId > sixthContext,
            "Rejoining a room never reuses pre-disconnect input identity");
        state.End(); state.End();
        check(state.RoomId == 15 && state.Stage == null && state.ContextId == 0,
            "Repeated exit remains harmless and cannot reset context history");
    }

    private static void ExpectNull(Action action, Action<bool, string> check, string name)
    {
        try { action(); }
        catch (ArgumentNullException) { check(true, name); return; }
        check(false, name);
    }

    private static void ExpectInvalidRoom(Action action, Action<bool, string> check, string name)
    {
        try { action(); }
        catch (ArgumentOutOfRangeException) { check(true, name); return; }
        check(false, name);
    }
}
