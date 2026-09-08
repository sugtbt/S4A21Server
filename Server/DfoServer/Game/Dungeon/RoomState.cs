using System;
using System.Collections.Generic;
using System.Threading;

namespace DfoServer.Game.Dungeon
{
    public enum HellPartyPhase
    {
        None = 0,
        WaitingStart = 1,
        Started = 2,
        Complete = 3,
    }

    public struct RoomKey : IEquatable<RoomKey>
    {
        public int X;
        public int Y;
        public int OverrideMapId;

        public RoomKey(int x, int y, int overrideMapId)
        {
            X = x;
            Y = y;
            OverrideMapId = overrideMapId;
        }

        public bool Equals(RoomKey other) =>
            X == other.X && Y == other.Y && OverrideMapId == other.OverrideMapId;

        public override bool Equals(object obj) => obj is RoomKey other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = X * 397;
                hash = (hash ^ Y) * 397;
                hash ^= OverrideMapId;
                return hash;
            }
        }
    }

    public class RoomState
    {
        private readonly object _stateSync = new object();
        private DungeonRoomState _state = DungeonRoomState.Created;
        private DungeonEncounterState _encounterState = DungeonEncounterState.NotStarted;
        private int _petExperienceGrantState;
        private int _storyPauseClearApplied;

        public DungeonInstanceRoom InstanceRoom;
        public long RoomInstanceId => InstanceRoom?.RoomInstanceId ?? 0;
        public DungeonRoomState State { get { lock (_stateSync) return _state; } }
        public DungeonEncounterState EncounterState
        {
            get
            {
                if (InstanceRoom != null)
                    return InstanceRoom.EncounterState;
                lock (_stateSync)
                    return _encounterState;
            }
        }
        public GameWorld.Dungeon.MazeSumInfo Maze;
        public ushort FirstSeqId;
        public ushort MonsterCount;
        public HashSet<ushort> KilledSeqIds;
        public uint Seed;
        public DnfLcg Lcg;
        internal DnfLcg ParticipantDropLcg;
        internal IReadOnlyList<PassiveObjectDropEntry> PassiveObjectDropEntries;
        public bool IsHellPartyRoom;
        public bool HellPartyVeryDifficult;
        public int HellPartyPillarObjectCode;
        public int HellPartySpawnX;
        public int HellPartySpawnY;
        public bool PetExperienceGranted =>
            Volatile.Read(ref _petExperienceGrantState) == 2;
        public List<GameWorld.Dungeon.HellPartyWaveInfo> HellPartyWaves;
        public HellPartyPhase HellPartyPhase;
        // 深渊小队剩余成员数。key 为 group index，value 为该 group 尚未收到死亡包的成员数。
        public Dictionary<int, int> HellPartyGroupRemaining;
        public bool TimeSpiralHiddenBossActive;
        public ushort TimeSpiralHiddenBossSeqId;
        public int TimeSpiralHiddenBossCode;
        public string TimeSpiralHiddenBossSource;
        public bool EventMonsterConditionAdvanced;

        public bool IsCleared => KilledSeqIds.Count >= MonsterCount && MonsterCount > 0;

        internal bool TryBeginPetExperienceGrant()
        {
            return Interlocked.CompareExchange(
                ref _petExperienceGrantState,
                1,
                0) == 0;
        }

        internal void CompletePetExperienceGrant()
        {
            Interlocked.CompareExchange(
                ref _petExperienceGrantState,
                2,
                1);
        }

        internal void CancelPetExperienceGrant()
        {
            Interlocked.CompareExchange(
                ref _petExperienceGrantState,
                0,
                1);
        }

        internal bool TryBeginStoryPauseClear()
        {
            if (State != DungeonRoomState.Active)
                return false;

            return Interlocked.CompareExchange(
                ref _storyPauseClearApplied,
                1,
                0) == 0;
        }

        internal void CancelStoryPauseClear()
        {
            Interlocked.CompareExchange(
                ref _storyPauseClearApplied,
                0,
                1);
        }

        public bool TryActivate()
        {
            lock (_stateSync)
            {
                if (_state == DungeonRoomState.Active)
                    return false;
                if (_state != DungeonRoomState.Created)
                    return false;
                _state = DungeonRoomState.Active;
                InstanceRoom?.TryActivate();
                return true;
            }
        }

        public bool TryClear()
        {
            lock (_stateSync)
            {
                if (_state == DungeonRoomState.Cleared)
                    return false;
                if (_state != DungeonRoomState.Active)
                    return false;
                _state = DungeonRoomState.Cleared;
                InstanceRoom?.TryClear();
                return true;
            }
        }

        public bool TryClose()
        {
            lock (_stateSync)
            {
                if (_state == DungeonRoomState.Closed)
                    return false;
                _state = DungeonRoomState.Closed;
                return true;
            }
        }

        public bool TryStartEncounter()
        {
            if (InstanceRoom != null)
                return InstanceRoom.TryStartEncounter();
            lock (_stateSync)
            {
                if (_encounterState == DungeonEncounterState.Active)
                    return false;
                if (_encounterState != DungeonEncounterState.NotStarted)
                    return false;
                _encounterState = DungeonEncounterState.Active;
                InstanceRoom?.TryStartEncounter();
                return true;
            }
        }

        public bool TryCompleteEncounter(bool succeeded)
        {
            if (InstanceRoom != null)
                return InstanceRoom.TryCompleteEncounter(succeeded);
            lock (_stateSync)
            {
                if (_encounterState != DungeonEncounterState.Active)
                    return false;
                _encounterState = succeeded
                    ? DungeonEncounterState.Succeeded
                    : DungeonEncounterState.Failed;
                InstanceRoom?.TryCompleteEncounter(succeeded);
                return true;
            }
        }
    }
}
