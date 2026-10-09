using System;
using RuinaCoop;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string name) { _checks++; if (!value) throw new Exception(name); }
    private static GuestRelayRecovery New(float now = 0f) { var state = new GuestRelayRecovery(); state.Begin(now); return state; }
    private static void Main()
    {
        var state = New();
        Check(state.Generation == 1 && state.RetryCount == 0 && !state.RetryPending, "Initial attempt begins without a retry");
        uint old = state.Generation;
        Check(state.ScheduleFailure(0, true) && state.Generation != old && state.RetryCount == 1, "Initial BadCert schedules first retry and revokes old callbacks");
        Check(state.RetryAt == 1f && !state.TryBeginRetry(.99f, true), "First retry waits one second");
        Check(state.TryBeginRetry(1f, true) && !state.TryBeginRetry(1f, true), "Retry is taken exactly once");
        Check(state.ScheduleFailure(1f, true) && state.RetryAt == 3f && state.RetryCount == 2, "Second failure backs off two seconds");
        Check(!state.TryBeginRetry(2.99f, true) && state.TryBeginRetry(3f, true), "Second retry respects deadline");
        Check(state.ScheduleFailure(3f, true) && state.RetryAt == 7f && state.RetryCount == 3, "Third failure backs off four seconds");
        Check(!state.TryBeginRetry(6.99f, true) && state.TryBeginRetry(7f, true), "Third retry respects deadline");
        Check(!state.ScheduleFailure(7f, true) && state.Failed && !state.RetryPending, "Fourth failed attempt exhausts the three-retry budget");
        Check(!state.TryBeginRetry(8f, true) && !state.ScheduleFailure(8f, true), "Exhausted state cannot restart");
        state = New(); state.ScheduleFailure(0f, true); state.TryBeginRetry(1f, true); state.Authenticated();
        Check(state.EverAuthenticated && !state.RetryPending && !state.Expire(100f), "Successful verified retry ends initial connection timeout");
        Check(!state.ScheduleFailure(101f, true) && state.Failed && state.EverAuthenticated, "Authenticated disconnect never auto-retries an uncertain edit");
        state = New();
        Check(!state.ScheduleFailure(0f, false) && state.Failed, "Permanent failure does not retry");
        state = New(); state.ScheduleFailure(0f, true); old = state.Generation; state.Stop();
        Check(state.Stopped && !state.RetryPending && state.Generation != old, "Leaving cancels retry and revokes all callback generations");
        Check(!state.TryBeginRetry(1f, true) && !state.ScheduleFailure(1f, true), "Stopped session cannot reconnect");
        state = New(100f);
        Check(!state.Expire(129.99f) && state.Expire(130f), "Initial connection and verification share a thirty-second deadline");
        Check(state.Failed && !state.ScheduleFailure(131f, true), "Timed-out session remains terminal");
        state = New();
        Check(!state.ScheduleFailure(29.5f, true) && state.Failed, "Retry due after overall deadline is refused");
        state = New(); state.ScheduleFailure(0f, true);
        Check(!state.TryBeginRetry(31f, true) && state.Failed && !state.RetryPending, "Delayed frame cannot restart beyond deadline");
        state = New(); state.Authenticated(); state.Stop();
        Check(state.Stopped && !state.TryBeginRetry(1f, true), "Stopping an authenticated session remains final");
        state = New(); state.ScheduleFailure(0f, true); old = state.Generation;
        foreach(float now in new float[] { 1f, 3f, 7f, 12f })
            Check(!state.TryBeginRetry(now, false) && state.RetryPending && state.RetryCount == 1 && state.Generation == old, "Unknown relay preserves pending retry without consuming attempts at " + now);
        Check(state.TryBeginRetry(13f, true) && !state.RetryPending && state.RetryCount == 1, "Current relay permits the pending retry after asynchronous warmup");
        state = New(); state.ScheduleFailure(0f, true);
        Check(!state.TryBeginRetry(30f, false) && state.Failed && !state.RetryPending, "Unknown relay still expires after thirty seconds without leaking pending retry");
        Check(!state.TryBeginRetry(31f, true), "Late Current relay cannot restart timed-out session");
        Console.WriteLine("PASS relay recovery " + _checks + " checks");
    }
}
