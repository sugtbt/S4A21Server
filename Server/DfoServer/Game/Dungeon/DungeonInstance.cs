using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DfoServer.Game.Dungeon
{
    public sealed class DungeonSelectionSnapshot
    {
        private int[] _bossMapPosition;
        private IReadOnlyList<RidableObjectSpawnEntry> _ridableObjects =
            Array.Empty<RidableObjectSpawnEntry>();
        private ClearConditionState _clearConditionTemplate;

        public int MazeIndex { get; init; } = -1;
        internal bool AnotherAradActive { get; init; }
        internal int AnotherAradWrapperDungeonId { get; init; }
        internal int AnotherAradHistoricalDungeonId { get; init; }
        internal int AnotherAradCrackQuestId { get; init; }
        internal bool AnotherAradQuestAccepted { get; init; }
        internal GameWorld.AnotherAradQuestDefinition AnotherAradQuestDefinition
        {
            get;
            init;
        }
        public bool MazeQuestConnected { get; init; }
        public int ActiveQuestMazeQuestId { get; init; }
        public int MazeStartMapId { get; init; }
        public int MazeStartX { get; init; } = -1;
        public int MazeStartY { get; init; } = -1;
        public int TotalRoomCount { get; init; } = 1;
        public int PartyMemberCount { get; init; } = 1;
        public int[] BossMapPosition
        {
            get => _bossMapPosition == null
                ? null
                : (int[])_bossMapPosition.Clone();
            init => _bossMapPosition = value == null
                ? null
                : (int[])value.Clone();
        }
        public IReadOnlyList<RidableObjectSpawnEntry> RidableObjects
        {
            get => _ridableObjects;
            init
            {
                if (value == null || value.Count == 0)
                {
                    _ridableObjects = Array.Empty<RidableObjectSpawnEntry>();
                    return;
                }

                var copy = new RidableObjectSpawnEntry[value.Count];
                for (var i = 0; i < value.Count; i++)
                    copy[i] = value[i];
                _ridableObjects = new ReadOnlyCollection<RidableObjectSpawnEntry>(copy);
            }
        }
        public ClearConditionState ClearConditionTemplate
        {
            get => _clearConditionTemplate?.CloneFresh();
            init => _clearConditionTemplate = value?.CloneFresh();
        }

        internal bool TryGetFrozenRoomMapId(int x, int y, out int mapId)
        {
            mapId = 0;
            if (MazeStartMapId <= 0 || x != MazeStartX || y != MazeStartY)
                return false;

            mapId = MazeStartMapId;
            return true;
        }

        internal void ApplyTo(DungeonRun run)
        {
            if (run == null)
                throw new ArgumentNullException(nameof(run));

            run.MazeIndex = MazeIndex;
            run.AnotherAradActive = AnotherAradActive;
            run.AnotherAradWrapperDungeonId = AnotherAradWrapperDungeonId;
            run.AnotherAradHistoricalDungeonId = AnotherAradHistoricalDungeonId;
            run.AnotherAradCrackQuestId = AnotherAradCrackQuestId;
            run.AnotherAradQuest = AnotherAradQuestDefinition == null
                ? null
                : new AnotherAradQuestRuntime(AnotherAradQuestDefinition);
            run.AnotherAradQuestAccepted = false;
            run.MazeQuestConnected = MazeQuestConnected;
            run.ActiveQuestMazeQuestId = ActiveQuestMazeQuestId;
            run.MazeStartMapId = MazeStartMapId;
            run.MazeStartX = MazeStartX;
            run.MazeStartY = MazeStartY;
            run.TotalRoomCount = Math.Max(1, TotalRoomCount);
            run.EntryPartyMemberCount = Math.Max(1, Math.Min(4, PartyMemberCount));
            run.BossMapPos = _bossMapPosition == null
                ? null
                : (int[])_bossMapPosition.Clone();
            run.RidableObjects = _ridableObjects == null
                ? new List<RidableObjectSpawnEntry>()
                : new List<RidableObjectSpawnEntry>(_ridableObjects);
            run.ClearCondition = _clearConditionTemplate?.CloneFresh();
        }
    }

    public readonly struct DungeonKillStatistics
    {
        internal DungeonKillStatistics(
            int normalKillCount,
            int championKillCount,
            int bossKillCount)
        {
            NormalKillCount = normalKillCount;
            ChampionKillCount = championKillCount;
            BossKillCount = bossKillCount;
        }

        public int NormalKillCount { get; }
        public int ChampionKillCount { get; }
        public int BossKillCount { get; }
        public int TotalKillCount
        {
            get
            {
                var total = (long)NormalKillCount
                    + ChampionKillCount
                    + BossKillCount;
                return total >= int.MaxValue ? int.MaxValue : (int)total;
            }
        }
    }

    public enum DungeonActorDeathKind
    {
        Defeated = 0,
        Captured = 1,
    }

    public sealed class DungeonActorDeathFact
    {
        internal DungeonActorDeathFact(
            DungeonEventEnvelope source,
            ushort sequenceId,
            int actorCode,
            byte actorType,
            DungeonActorDeathKind deathKind)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            SequenceId = sequenceId;
            ActorCode = actorCode;
            ActorType = actorType;
            DeathKind = deathKind;
        }

        public DungeonEventEnvelope Source { get; }
        public Guid SourceEventId => Source.SourceEventId;
        public ushort SequenceId { get; }
        public int ActorCode { get; }
        public byte ActorType { get; }
        public DungeonActorDeathKind DeathKind { get; }
    }

    internal readonly struct DungeonRoomActorDeathApplication
    {
        internal DungeonRoomActorDeathApplication(
            bool accepted,
            bool created,
            DungeonActorDeathFact fact)
        {
            Accepted = accepted;
            Created = created;
            Fact = fact;
        }

        internal bool Accepted { get; }
        internal bool Created { get; }
        internal DungeonActorDeathFact Fact { get; }
    }

    internal readonly struct DungeonRoomClearCommit
    {
        internal DungeonRoomClearCommit(
            bool isCleared,
            bool transitioned,
            int blockingCount,
            int killedBlockingCount,
            DungeonEventEnvelope source)
        {
            IsCleared = isCleared;
            Transitioned = transitioned;
            BlockingCount = blockingCount;
            KilledBlockingCount = killedBlockingCount;
            Source = source;
        }

        internal bool IsCleared { get; }
        internal bool Transitioned { get; }
        internal int BlockingCount { get; }
        internal int KilledBlockingCount { get; }
        internal DungeonEventEnvelope Source { get; }
    }

    public sealed class DungeonInstanceRoom
    {
        private const byte SpecialPassiveObjectActorType = 9;

        private readonly object _syncRoot = new object();
        private readonly Dictionary<string, DungeonEncounterRuntime> _encounters =
            new Dictionary<string, DungeonEncounterRuntime>(StringComparer.Ordinal);
        private readonly Dictionary<ushort, DungeonActorDeathFact> _actorDeaths =
            new Dictionary<ushort, DungeonActorDeathFact>();
        private readonly Dictionary<int, DungeonActorDeathFact>
            _mapOwnedActorDeaths =
                new Dictionary<int, DungeonActorDeathFact>();
        private readonly Dictionary<int, DungeonActorDeathFact>
            _ordinaryMapOwnedActorDeaths =
                new Dictionary<int, DungeonActorDeathFact>();
        private Lazy<PassiveObjectDropPlan> _passiveObjectDropPlan;
        private DungeonRoomState _state = DungeonRoomState.Created;
        private long _partyDungeonInstanceId;
        private DungeonEventEnvelope _clearSource;

        internal DungeonInstanceRoom(
            long roomInstanceId,
            RoomKey key,
            GameWorld.Dungeon.MazeSumInfo maze,
            uint seed,
            ushort firstActorSequenceId = 1)
        {
            RoomInstanceId = roomInstanceId;
            Key = key;
            Maze = maze;
            Seed = seed;
            FirstActorSequenceId = firstActorSequenceId;
        }

        public long RoomInstanceId { get; }
        public long PartyDungeonInstanceId
        {
            get
            {
                lock (_syncRoot)
                    return _partyDungeonInstanceId;
            }
        }
        public DungeonRoomIdentity Identity
        {
            get
            {
                lock (_syncRoot)
                {
                    return new DungeonRoomIdentity(
                        new DungeonInstanceIdentity(_partyDungeonInstanceId),
                        RoomInstanceId);
                }
            }
        }
        public RoomKey Key { get; }
        public GameWorld.Dungeon.MazeSumInfo Maze { get; }
        public uint Seed { get; }
        public ushort FirstActorSequenceId { get; }
        public DungeonEffectLedger Effects { get; } = new DungeonEffectLedger();
        public DungeonRoomState State { get { lock (_syncRoot) return _state; } }
        public DungeonEncounterState EncounterState
        {
            get
            {
                lock (_syncRoot)
                {
                    return _encounters.TryGetValue(
                        DungeonEncounterDirective.DefaultEncounterKey,
                        out var runtime)
                        ? runtime.State
                        : DungeonEncounterState.NotStarted;
                }
            }
        }

        internal void AttachToInstance(long partyDungeonInstanceId)
        {
            if (partyDungeonInstanceId <= 0)
                throw new ArgumentOutOfRangeException(nameof(partyDungeonInstanceId));

            lock (_syncRoot)
            {
                if (_partyDungeonInstanceId == 0)
                {
                    _partyDungeonInstanceId = partyDungeonInstanceId;
                    return;
                }
                if (_partyDungeonInstanceId != partyDungeonInstanceId)
                {
                    throw new InvalidOperationException(
                        "A dungeon room cannot be attached to multiple instances.");
                }
            }
        }

        internal PassiveObjectDropPlan GetOrCreatePassiveObjectDropPlan(
            Func<PassiveObjectDropPlan> factory)
        {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));

            Lazy<PassiveObjectDropPlan> lazy;
            lock (_syncRoot)
            {
                if (_passiveObjectDropPlan == null)
                {
                    _passiveObjectDropPlan = new Lazy<PassiveObjectDropPlan>(
                        () => factory() ?? PassiveObjectDropPlan.Empty,
                        isThreadSafe: true);
                }
                lazy = _passiveObjectDropPlan;
            }

            return lazy.Value;
        }

        internal DungeonRoomActorDeathApplication TryRecordActorDeath(
            DungeonEventEnvelope source,
            ushort sequenceId,
            int actorCode,
            byte actorType,
            DungeonActorDeathKind deathKind = DungeonActorDeathKind.Defeated)
        {
            if (!MatchesSource(source) || sequenceId == 0)
                return default;

            lock (_syncRoot)
            {
                if (_state == DungeonRoomState.Closed)
                    return default;
                if (_actorDeaths.TryGetValue(sequenceId, out var existing))
                {
                    return new DungeonRoomActorDeathApplication(
                        accepted: true,
                        created: false,
                        existing);
                }

                var fact = new DungeonActorDeathFact(
                    source,
                    sequenceId,
                    actorCode,
                    actorType,
                    deathKind);
                _actorDeaths.Add(sequenceId, fact);
                return new DungeonRoomActorDeathApplication(
                    accepted: true,
                    created: true,
                    fact);
            }
        }

        internal DungeonRoomActorDeathApplication
            TryRecordNextMapOwnedPassiveObjectDeath(
                DungeonEventEnvelope source,
                int actorCode,
                out bool actorDefined)
        {
            actorDefined = false;
            if (!MatchesSource(source) || actorCode <= 0)
                return default;

            lock (_syncRoot)
            {
                if (_state == DungeonRoomState.Closed)
                    return default;

                var ordinaryActors = Maze.PassiveObjectCodes;
                if (ordinaryActors != null)
                {
                    for (var index = 0; index < ordinaryActors.Count; index++)
                    {
                        if (ordinaryActors[index] != actorCode)
                            continue;

                        actorDefined = true;
                        if (_ordinaryMapOwnedActorDeaths.ContainsKey(index))
                            continue;

                        var fact = new DungeonActorDeathFact(
                            source,
                            sequenceId: 0,
                            actorCode,
                            SpecialPassiveObjectActorType,
                            DungeonActorDeathKind.Defeated);
                        _ordinaryMapOwnedActorDeaths.Add(index, fact);
                        return new DungeonRoomActorDeathApplication(
                            accepted: true,
                            created: true,
                            fact);
                    }
                }

                var actors = Maze.SpecialPassiveObjects;
                if (actors == null)
                    return default;

                for (var index = 0; index < actors.Count; index++)
                {
                    var actor = actors[index];
                    if (actor?.ObjectCode != actorCode)
                        continue;

                    actorDefined = true;
                    if (_mapOwnedActorDeaths.ContainsKey(index))
                        continue;

                    // MAP-owned actors have no START_MAP wire sequence.
                    var fact = new DungeonActorDeathFact(
                        source,
                        sequenceId: 0,
                        actor.ObjectCode,
                        SpecialPassiveObjectActorType,
                        DungeonActorDeathKind.Defeated);
                    _mapOwnedActorDeaths.Add(index, fact);
                    return new DungeonRoomActorDeathApplication(
                        accepted: true,
                        created: true,
                        fact);
                }

                return default;
            }
        }

        internal DungeonRoomClearCommit TryCommitClearFromActorDeaths(
            Func<GameWorld.Dungeon.MonsterSumInfo, bool> isBlocking,
            DungeonEventEnvelope fallbackSource,
            ushort completingSequenceId)
        {
            if (isBlocking == null)
                throw new ArgumentNullException(nameof(isBlocking));
            if (!MatchesSource(fallbackSource))
                return default;

            lock (_syncRoot)
            {
                var blockingCount = 0;
                var killedBlockingCount = 0;
                var monsters = Maze.Monsters;
                if (monsters != null)
                {
                    for (var index = 0; index < monsters.Count; index++)
                    {
                        if (!isBlocking(monsters[index]))
                            continue;

                        blockingCount++;
                        var sequenceId = unchecked((ushort)(FirstActorSequenceId + index));
                        if (_actorDeaths.ContainsKey(sequenceId))
                            killedBlockingCount++;
                    }
                }

                var cleared = killedBlockingCount >= blockingCount;
                if (!cleared || _state == DungeonRoomState.Closed)
                {
                    return new DungeonRoomClearCommit(
                        isCleared: false,
                        transitioned: false,
                        blockingCount,
                        killedBlockingCount,
                        source: null);
                }

                var transitioned = false;
                if (_state != DungeonRoomState.Cleared)
                {
                    if (_state != DungeonRoomState.Active)
                    {
                        return new DungeonRoomClearCommit(
                            isCleared: false,
                            transitioned: false,
                            blockingCount,
                            killedBlockingCount,
                            source: null);
                    }

                    _state = DungeonRoomState.Cleared;
                    transitioned = true;
                    _clearSource = _actorDeaths.TryGetValue(
                        completingSequenceId,
                        out var completingDeath)
                            ? completingDeath.Source
                            : fallbackSource;
                }

                return new DungeonRoomClearCommit(
                    isCleared: true,
                    transitioned,
                    blockingCount,
                    killedBlockingCount,
                    _clearSource ?? fallbackSource);
            }
        }

        internal HashSet<ushort> CaptureKilledActorSequenceIds()
        {
            lock (_syncRoot)
                return new HashSet<ushort>(_actorDeaths.Keys);
        }

        internal bool TryGetActorDeathFact(
            ushort sequenceId,
            out DungeonActorDeathFact fact)
        {
            lock (_syncRoot)
                return _actorDeaths.TryGetValue(sequenceId, out fact);
        }

        internal bool HasPendingHostileApcBoss()
        {
            lock (_syncRoot)
            {
                var actors = Maze.Monsters;
                if (actors == null)
                    return false;

                for (var index = 0; index < actors.Count; index++)
                {
                    var actor = actors[index];
                    if (!actor.IsHostileApcBoss)
                        continue;

                    var sequenceValue = (int)FirstActorSequenceId + index;
                    if (sequenceValue <= 0 || sequenceValue > ushort.MaxValue)
                        continue;

                    var candidate = (ushort)sequenceValue;
                    if (_actorDeaths.ContainsKey(candidate))
                        continue;

                    return true;
                }
                return false;
            }
        }

        internal void CopyKilledActorSequenceIdsTo(
            ISet<ushort> destination,
            Func<DungeonActorDeathFact, bool> include = null)
        {
            if (destination == null)
                throw new ArgumentNullException(nameof(destination));

            lock (_syncRoot)
            {
                destination.Clear();
                foreach (var death in _actorDeaths)
                {
                    if (include == null || include(death.Value))
                        destination.Add(death.Key);
                }
            }
        }

        public bool TryActivate()
        {
            lock (_syncRoot)
            {
                if (_state == DungeonRoomState.Active)
                    return false;
                if (_state != DungeonRoomState.Created)
                    return false;
                _state = DungeonRoomState.Active;
                return true;
            }
        }

        public bool TryClear()
        {
            lock (_syncRoot)
            {
                if (_state == DungeonRoomState.Cleared)
                    return false;
                if (_state != DungeonRoomState.Active)
                    return false;
                _state = DungeonRoomState.Cleared;
                return true;
            }
        }

        public bool TryClose()
        {
            lock (_syncRoot)
            {
                if (_state == DungeonRoomState.Closed)
                    return false;
                _state = DungeonRoomState.Closed;
                return true;
            }
        }

        public bool TryStartEncounter()
        {
            lock (_syncRoot)
                return GetOrCreateEncounterLocked(
                    DungeonEncounterDirective.DefaultEncounterKey)
                    .TryApplyLegacy(DungeonEncounterDirectiveKind.Start);
        }

        public bool TryCompleteEncounter(bool succeeded)
        {
            lock (_syncRoot)
                return GetOrCreateEncounterLocked(
                    DungeonEncounterDirective.DefaultEncounterKey)
                    .TryApplyLegacy(
                        succeeded
                            ? DungeonEncounterDirectiveKind.Succeed
                            : DungeonEncounterDirectiveKind.Fail);
        }

        internal DungeonEncounterTransition ApplyEncounterDirective(
            DungeonEncounterDirective directive)
        {
            if (directive == null)
                throw new ArgumentNullException(nameof(directive));
            lock (_syncRoot)
                return GetOrCreateEncounterLocked(directive.EncounterKey)
                    .Apply(directive);
        }

        private DungeonEncounterRuntime GetOrCreateEncounterLocked(string key)
        {
            if (!_encounters.TryGetValue(key, out var runtime))
            {
                runtime = new DungeonEncounterRuntime();
                _encounters.Add(key, runtime);
            }
            return runtime;
        }

        private bool MatchesSource(DungeonEventEnvelope source)
        {
            if (source == null
                || !source.RoomInstanceId.HasValue
                || source.RoomInstanceId.Value != RoomInstanceId)
            {
                return false;
            }

            lock (_syncRoot)
            {
                return _partyDungeonInstanceId > 0
                    && source.PartyDungeonInstanceId == _partyDungeonInstanceId;
            }
        }
    }

    public sealed class DungeonInstance
    {
        private readonly object _syncRoot = new object();
        private readonly Dictionary<RoomKey, DungeonInstanceRoom> _rooms =
            new Dictionary<RoomKey, DungeonInstanceRoom>();
        private readonly HashSet<(
            long RoomInstanceId,
            RoomKey RoomKey,
            ushort SequenceId)> _recordedKillActors =
                new HashSet<(long, RoomKey, ushort)>();
        private DungeonSelectionSnapshot _selection;
        private DungeonClearedFact _clearedFact;
        private DungeonInstanceState _state = DungeonInstanceState.Created;
        private int _normalKillCount;
        private int _championKillCount;
        private int _bossKillCount;

        public DungeonInstance(short dungeonId, byte difficulty)
            : this(
                dungeonId,
                difficulty,
                DungeonRewardPolicy.Standard,
                DungeonDropDefinition.CreateStandard(dungeonId),
                GameWorld.DungeonExperienceDefinitionCatalog.Resolve(dungeonId))
        {
        }

        internal DungeonInstance(
            short dungeonId,
            byte difficulty,
            DungeonRewardPolicy rewardPolicy)
            : this(
                dungeonId,
                difficulty,
                rewardPolicy,
                DungeonDropDefinition.CreateStandard(dungeonId),
                GameWorld.DungeonExperienceDefinitionCatalog.Resolve(dungeonId))
        {
        }

        internal DungeonInstance(
            short dungeonId,
            byte difficulty,
            DungeonRewardPolicy rewardPolicy,
            DungeonDropDefinition dropDefinition)
            : this(
                dungeonId,
                difficulty,
                rewardPolicy,
                dropDefinition,
                GameWorld.DungeonExperienceDefinitionCatalog.Resolve(dungeonId))
        {
        }

        internal DungeonInstance(
            short dungeonId,
            byte difficulty,
            DungeonRewardPolicy rewardPolicy,
            DungeonDropDefinition dropDefinition,
            GameWorld.DungeonExperienceDefinition experienceDefinition)
        {
            PartyDungeonInstanceId = DungeonIdentityGenerator.NextInstanceId();
            DungeonId = dungeonId;
            Difficulty = difficulty;
            RewardPolicy = rewardPolicy ?? throw new ArgumentNullException(nameof(rewardPolicy));
            DropDefinition = dropDefinition
                ?? throw new ArgumentNullException(nameof(dropDefinition));
            ExperienceDefinition = experienceDefinition
                ?? throw new ArgumentNullException(nameof(experienceDefinition));
            CreatedUtc = DateTime.UtcNow;
        }

        public long PartyDungeonInstanceId { get; }
        public DungeonInstanceIdentity Identity =>
            new DungeonInstanceIdentity(PartyDungeonInstanceId);
        public short DungeonId { get; }
        public byte Difficulty { get; }
        public DungeonRewardPolicy RewardPolicy { get; }
        public DungeonDropDefinition DropDefinition { get; }
        internal GameWorld.DungeonExperienceDefinition ExperienceDefinition
        {
            get;
        }
        public DateTime CreatedUtc { get; }
        public DungeonEffectLedger Effects { get; } = new DungeonEffectLedger();
        public DungeonParticipantEffectJournal ParticipantEffects { get; } =
            new DungeonParticipantEffectJournal();
        internal DungeonInstanceMechanismRuntimeSet Mechanisms { get; } =
            new DungeonInstanceMechanismRuntimeSet();
        internal DungeonDiagnosticJournal Diagnostics { get; } =
            new DungeonDiagnosticJournal();
        public DungeonSelectionSnapshot Selection { get { lock (_syncRoot) return _selection; } }
        public DungeonClearedFact ClearedFact { get { lock (_syncRoot) return _clearedFact; } }
        public DungeonInstanceState State { get { lock (_syncRoot) return _state; } }
        public int VisitedRoomCount { get { lock (_syncRoot) return _rooms.Count; } }
        public DungeonKillStatistics KillStatistics
        {
            get
            {
                lock (_syncRoot)
                {
                    return new DungeonKillStatistics(
                        _normalKillCount,
                        _championKillCount,
                        _bossKillCount);
                }
            }
        }

        public bool TryFreezeSelection(DungeonSelectionSnapshot selection)
        {
            if (selection == null)
                throw new ArgumentNullException(nameof(selection));

            lock (_syncRoot)
            {
                if (_selection != null)
                    return false;
                _selection = selection;
                return true;
            }
        }

        public DungeonInstanceRoom GetOrCreateRoom(
            RoomKey key,
            Func<long, DungeonInstanceRoom> factory,
            out bool created)
        {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));

            lock (_syncRoot)
            {
                if (_rooms.TryGetValue(key, out var existing))
                {
                    created = false;
                    return existing;
                }

                var room = factory(DungeonIdentityGenerator.NextRoomId());
                if (room == null || !room.Key.Equals(key))
                    throw new InvalidOperationException("Dungeon room factory returned an invalid room.");
                room.AttachToInstance(PartyDungeonInstanceId);
                _rooms.Add(key, room);
                if (_state == DungeonInstanceState.Created)
                    _state = DungeonInstanceState.Active;
                created = true;
                return room;
            }
        }

        public bool TryGetRoom(RoomKey key, out DungeonInstanceRoom room)
        {
            lock (_syncRoot)
                return _rooms.TryGetValue(key, out room);
        }

        internal bool TryGetRoom(
            long roomInstanceId,
            out DungeonInstanceRoom room)
        {
            lock (_syncRoot)
            {
                foreach (var candidate in _rooms.Values)
                {
                    if (candidate.RoomInstanceId == roomInstanceId)
                    {
                        room = candidate;
                        return true;
                    }
                }
            }

            room = null;
            return false;
        }

        internal bool TryGetActorDeathFact(
            long roomInstanceId,
            ushort sequenceId,
            out DungeonActorDeathFact fact)
        {
            fact = null;
            return TryGetRoom(roomInstanceId, out var room)
                && room.TryGetActorDeathFact(sequenceId, out fact);
        }

        public bool IsRoomCleared(int x, int y)
        {
            lock (_syncRoot)
            {
                foreach (var pair in _rooms)
                {
                    if (pair.Key.X == x
                        && pair.Key.Y == y
                        && pair.Value.State == DungeonRoomState.Cleared)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        internal bool TryRecordMonsterKill(
            long roomInstanceId,
            RoomKey roomKey,
            ushort sequenceId,
            byte actorType)
        {
            if (sequenceId == 0 || actorType == 9)
                return false;

            // START_MAP uses 0..3 for monster kinds and 5..8 for APC/AI
            // types. Normalize the shared statistics owner before counting so
            // APC champions are not downgraded to normal kills.
            actorType = (byte)DungeonExperienceCalculator
                .ResolveMonsterKind(actorType);

            lock (_syncRoot)
            {
                if (!_recordedKillActors.Add((
                        roomInstanceId,
                        roomKey,
                        sequenceId)))
                {
                    return false;
                }

                if (actorType == 3)
                    _bossKillCount++;
                else if (actorType == 1)
                    _championKillCount++;
                else
                    _normalKillCount++;
                return true;
            }
        }

        public DungeonClearedFact GetOrCreateClearedFact(
            DungeonClearIntent intent,
            out bool created)
        {
            if (intent == null)
                throw new ArgumentNullException(nameof(intent));
            if (intent.Source.PartyDungeonInstanceId != PartyDungeonInstanceId)
                throw new InvalidOperationException(
                    "A clear intent must belong to this dungeon instance.");

            lock (_syncRoot)
            {
                if (_clearedFact != null)
                {
                    created = false;
                    return _clearedFact;
                }
                if (_state == DungeonInstanceState.Ending
                    || _state == DungeonInstanceState.Ended)
                {
                    throw new InvalidOperationException(
                        "A terminal dungeon instance cannot accept a clear intent.");
                }

                _clearedFact = new DungeonClearedFact(intent);
                _state = DungeonInstanceState.Cleared;
                created = true;
                return _clearedFact;
            }
        }

        internal bool TryBeginEnding()
        {
            lock (_syncRoot)
            {
                if (_state == DungeonInstanceState.Ending
                    || _state == DungeonInstanceState.Ended)
                {
                    return false;
                }

                _state = DungeonInstanceState.Ending;
            }

            Mechanisms.OnInstanceEnding();
            return true;
        }

        internal bool TryMarkEnded()
        {
            lock (_syncRoot)
            {
                if (_state == DungeonInstanceState.Ended)
                    return false;
                if (_state != DungeonInstanceState.Ending)
                    return false;

                _state = DungeonInstanceState.Ended;
                return true;
            }
        }
    }
}
