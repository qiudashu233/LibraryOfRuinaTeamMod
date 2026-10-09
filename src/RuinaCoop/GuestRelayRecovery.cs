namespace RuinaCoop
{
    // First-connection recovery only. A previously authenticated session may
    // contain an unacknowledged edit and must require an explicit room rejoin.
    internal sealed class GuestRelayRecovery
    {
        internal const int MaxRetries = 3;
        internal const float DeadlineSeconds = 30f;
        internal uint Generation { get; private set; }
        internal int RetryCount { get; private set; }
        internal bool RetryPending { get; private set; }
        internal bool Failed { get; private set; }
        internal bool Stopped { get; private set; }
        internal bool EverAuthenticated { get; private set; }
        internal float RetryAt { get; private set; }
        private float _startedAt;

        internal void Begin(float now)
        {
            _startedAt = now;
            Generation++;
        }

        internal bool ScheduleFailure(float now, bool temporary)
        {
            // Invalidate all callbacks from the closed attempt immediately.
            Generation++;
            RetryPending = false;
            if (Stopped || Failed) return false;
            if (EverAuthenticated || !temporary || RetryCount >= MaxRetries || now >= _startedAt + DeadlineSeconds)
            { Failed = true; return false; }
            RetryAt = now + (1 << RetryCount);
            if (RetryAt >= _startedAt + DeadlineSeconds) { Failed = true; return false; }
            RetryCount++;
            RetryPending = true;
            return true;
        }

        internal bool TryBeginRetry(float now, bool relayReady)
        {
            if (Stopped || Failed || EverAuthenticated || !RetryPending) return false;
            if (Expire(now)) return false;
            if (!relayReady || now < RetryAt) return false;
            RetryPending = false;
            return true;
        }

        internal bool Expire(float now)
        {
            if (Stopped || Failed || EverAuthenticated || now < _startedAt + DeadlineSeconds) return false;
            Failed = true; RetryPending = false; Generation++;
            return true;
        }

        internal void Authenticated() { EverAuthenticated = true; RetryPending = false; }
        internal void Stop() { Stopped = true; RetryPending = false; Generation++; }
    }
}
