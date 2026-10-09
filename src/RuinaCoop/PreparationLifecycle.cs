using System;

namespace RuinaCoop
{
    // A retained vanilla StageModel does not imply that its reception is still
    // active. Only a new room may adopt an already-open preparation implicitly;
    // after End, another explicit invitation is required in that same room.
    internal sealed class PreparationLifecycle<T> where T : class
    {
        private ulong _nextContext;

        internal ulong RoomId { get; private set; }
        internal T Stage { get; private set; }
        internal ulong ContextId { get; private set; }

        internal bool ObserveRoom(ulong room, T stage, bool inPreparation)
        {
            if (RoomId == room) return false;
            End();
            RoomId = room;
            if (room != 0 && inPreparation && stage != null) Adopt(stage);
            return true;
        }

        internal bool Begin(ulong room, T stage)
        {
            ObserveRoom(room, null, false);
            if (stage == null) throw new ArgumentNullException("stage");
            if (room == 0) throw new ArgumentOutOfRangeException("room");
            if (Matches(room, stage)) return false;
            Adopt(stage);
            return true;
        }

        internal void End()
        {
            Stage = null;
            ContextId = 0;
        }

        internal bool Matches(ulong room, T stage)
        {
            return room != 0 && RoomId == room && ContextId != 0 &&
                stage != null && ReferenceEquals(Stage, stage);
        }

        private void Adopt(T stage)
        {
            if (_nextContext == ulong.MaxValue)
                throw new InvalidOperationException("Reception context exhausted; restart the game.");
            ContextId = ++_nextContext;
            Stage = stage;
        }
    }
}
