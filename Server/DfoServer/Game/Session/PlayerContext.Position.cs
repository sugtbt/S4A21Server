using System;
using System.Threading;

namespace DfoServer.Game.Session
{
    public partial class PlayerContext
    {
        private int _townPresenceReady = 1;
        private int _dungeonSelectionPending;
        private long _townMovementSequence;

        public byte CurTownId { get; set; }
        public byte CurAreaId { get; set; }
        public short CurPosX { get; set; }
        public short CurPosY { get; set; }
        public byte CurDirection { get; set; } = 0x05;
        public byte CurAreaState { get; set; } = 0x03;

        // Kept separate from the protocol-level UserState. A replacement
        // session is published to the character directory before asynchronous
        // teardown of the old generation completes, but must not enter a town
        // roster until the old avatar's USER_LEAVE has been delivered.
        public bool TownPresenceReady
        {
            get => Volatile.Read(ref _townPresenceReady) != 0;
            set => Volatile.Write(
                ref _townPresenceReady, value ? 1 : 0);
        }

        // Party dungeon selection keeps UserState at town until every member
        // has received the mandatory shared-roster packets. This independent
        // gate prevents a queued town-arrival task from republishing a member
        // during that window.
        public bool DungeonSelectionPending
        {
            get => Volatile.Read(ref _dungeonSelectionPending) != 0;
            internal set => Volatile.Write(
                ref _dungeonSelectionPending, value ? 1 : 0);
        }

        internal long NextTownMovementSequence()
            => Interlocked.Increment(ref _townMovementSequence);

        internal long LatestTownMovementSequence
            => Interlocked.Read(ref _townMovementSequence);

        internal void ResetTownMovementSequence()
            => Interlocked.Exchange(ref _townMovementSequence, 0);

        /// <summary>
        /// </summary>
        public DateTime LastPositionPersistAt { get; set; } = DateTime.MinValue;
    }
}
