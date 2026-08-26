using System;
using System.Collections.Generic;
using DfoServer.Game.Quests;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Dungeon
{
    // 一局副本(从选本进入到返城/断线/换角色)的全部会话内状态。
    // PlayerContext.CurrentRun 持有当前局, null 表示不在副本中:
    // 进本 new 一个实例, 结束置 null, 字段随对象一起消失 -- 不存在"漏重置"。
    // 字段默认值即"返城重置后"的取值, 与旧版逐字段清零清单一致。
    //
    // 跨局存活的状态不放这里(它们留在 PlayerContext 上):
    // - 深渊华丽挑战 UI 开关(选图界面在进本之前切换);
    // - 宠物城镇恢复锚点(副本之间持续计时);
    // - 宠物死亡定时器版本号(单调递增, 用于让过期的延迟回调失效, 归零会让旧回调复活)。
    public sealed class DungeonRun
    {
        // 组队副本联机: 击杀 relay(BroadcastMonsterDieToPartyAsync→PropagateKillForClearAsync)在【击杀者线程】
        // 写/读【队友 run】的 RoomKilledSeqIds(HashSet)与 RoomStates(Dict), 而队友自己线程也在改同一结构 →
        // 跨线程并发改集合会崩/CPU 空转/丢结算。所有对 RoomKilledSeqIds / RoomStates 的读改写都必须在此锁下,
        // 且【锁内绝不 await】(只护同步的集合访问, await 一律在锁外)。单人副本无 relay, 锁基本无竞争、开销可忽略。
        public readonly object SyncRoot = new object();

        public DungeonRun(short dungeonId, byte difficulty)
            : this(
                new DungeonInstance(dungeonId, difficulty),
                DungeonIdentityGenerator.NextRunId(),
                runGeneration: 1,
                DungeonRunState.Active)
        {
        }

        internal DungeonRun(
            DungeonInstance instance,
            long runId,
            long runGeneration,
            DungeonRunState initialState)
        {
            Instance = instance ?? throw new ArgumentNullException(nameof(instance));
            DungeonId = instance.DungeonId;
            Difficulty = instance.Difficulty;
            RunId = runId;
            RunGeneration = runGeneration;
            _runState = initialState;
            StartedUtc = DateTime.UtcNow;
        }

        // 自测用: 构造一个字段全为默认值的空局。
        public DungeonRun()
        {
        }

        private DungeonRunState _runState;
        private DungeonSettlementState _settlementState;
        private DungeonClearedFact _clearedFact;
        private Guid _settlementSourceEventId;
        private Guid _endSourceEventId;

        public DungeonInstance Instance { get; }
        public DungeonRewardPolicy RewardPolicy =>
            Instance?.RewardPolicy ?? DungeonRewardPolicy.Standard;
        public DungeonDropDefinition DropDefinition =>
            Instance?.DropDefinition ?? DungeonDropDefinition.Standard;
        internal GameWorld.DungeonExperienceDefinition ExperienceDefinition =>
            Instance?.ExperienceDefinition;
        public DungeonDropPolicy DropPolicy => DropDefinition.Policy;
        public long PartyDungeonInstanceId => Instance?.PartyDungeonInstanceId ?? 0;
        public long RunId { get; }
        public long RunGeneration { get; }
        public long CurrentRoomInstanceId { get; private set; }
        public DungeonEffectLedger Effects { get; } = new DungeonEffectLedger();
        internal SpecialDungeonEffectPlanJournal SpecialDungeonEffectPlans
        {
            get;
        } = new SpecialDungeonEffectPlanJournal();
        public RunTimerRegistry Timers { get; } = new RunTimerRegistry();
        public DungeonRunState RunState { get { lock (SyncRoot) return _runState; } }
        public DungeonSettlementState SettlementState { get { lock (SyncRoot) return _settlementState; } }
        public DungeonClearedFact ClearedFact { get { lock (SyncRoot) return _clearedFact; } }
        internal DungeonRunSelectionState Selection { get; } =
            new DungeonRunSelectionState();
        internal DungeonRunCombatState Combat { get; } =
            new DungeonRunCombatState();
        internal DungeonRunSettlementData Settlement { get; } =
            new DungeonRunSettlementData();
        internal DungeonRunQuestBridgeState QuestBridge { get; } =
            new DungeonRunQuestBridgeState();
        internal DungeonCaptureDropJournal CaptureDrops =>
            QuestBridge.CaptureDrops;
        internal DungeonMechanismRuntimeSet Mechanisms { get; } =
            new DungeonMechanismRuntimeSet();

        public QuestRunSnapshot QuestSnapshot
        {
            get => QuestBridge.Snapshot;
            internal set => QuestBridge.Snapshot = value ?? QuestRunSnapshot.Empty;
        }
        internal DungeonTownReturnAnchor TownReturnAnchor { get; set; }
        internal DungeonSettlementRuntime SettlementRuntime
        {
            get => Settlement.Runtime;
            set => Settlement.Runtime = value;
        }

        public short DungeonId;
        public byte Difficulty;
        public DungeonRunPhase Phase
        {
            get
            {
                lock (SyncRoot)
                {
                    if (_runState == DungeonRunState.None || _runState == DungeonRunState.Ended)
                        return DungeonRunPhase.None;
                    if (_settlementState == DungeonSettlementState.Completed)
                        return CardRewards == null
                            ? DungeonRunPhase.ResultShown
                            : DungeonRunPhase.CardsRevealed;
                    if (_settlementState == DungeonSettlementState.CardsRevealed)
                        return DungeonRunPhase.CardsRevealed;
                    if (_settlementState == DungeonSettlementState.ResultShown)
                        return DungeonRunPhase.ResultShown;
                    if (_runState == DungeonRunState.ClearCommitting
                        || _runState == DungeonRunState.Cleared
                        || _runState == DungeonRunState.Ending)
                        return DungeonRunPhase.Cleared;
                    return DungeonRunPhase.InProgress;
                }
            }
            set
            {
                lock (SyncRoot)
                {
                    switch (value)
                    {
                        case DungeonRunPhase.None:
                            _runState = DungeonRunState.None;
                            _settlementState = DungeonSettlementState.NotStarted;
                            break;
                        case DungeonRunPhase.InProgress:
                            _runState = DungeonRunState.Active;
                            _settlementState = DungeonSettlementState.NotStarted;
                            break;
                        case DungeonRunPhase.Cleared:
                            _runState = DungeonRunState.Cleared;
                            _settlementState = DungeonSettlementState.NotStarted;
                            break;
                        case DungeonRunPhase.ResultShown:
                            _runState = DungeonRunState.Cleared;
                            _settlementState = DungeonSettlementState.ResultShown;
                            break;
                        case DungeonRunPhase.CardsRevealed:
                            _runState = DungeonRunState.Cleared;
                            _settlementState = DungeonSettlementState.CardsRevealed;
                            break;
                    }
                }
            }
        }
        public DateTime StartedUtc;

        internal int CalculateElapsedMilliseconds(DateTime observedUtc)
        {
            if (StartedUtc == DateTime.MinValue)
                return 0;

            var elapsed = observedUtc - StartedUtc;
            if (elapsed <= TimeSpan.Zero)
                return 0;
            if (elapsed.TotalMilliseconds >= int.MaxValue)
                return int.MaxValue;
            return (int)Math.Round(elapsed.TotalMilliseconds);
        }

        // Compatibility projections. New production code should address the
        // composed state objects above instead of adding fields to this root.
        internal SpecialDungeonRuntime SpecialDungeon { get => Mechanisms.SpecialDungeon; set => Mechanisms.SpecialDungeon = value; }
        public bool IgnoreDefaultDungeonClear { get => Mechanisms.IgnoreDefaultDungeonClear; set => Mechanisms.IgnoreDefaultDungeonClear = value; }
        public IReadOnlyList<IReadOnlyList<(byte X, byte Y)>> SpecialMinimapIconGroups { get => Mechanisms.SpecialMinimapIconGroups; set => Mechanisms.SpecialMinimapIconGroups = value; }
        internal List<BossEntranceConditionTargetState> BossEntranceConditionTargets { get => Mechanisms.BossEntranceConditionTargets; set => Mechanisms.BossEntranceConditionTargets = value; }
        internal List<int> BossEntranceConditionalSummonCodes { get => Mechanisms.BossEntranceConditionalSummonCodes; set => Mechanisms.BossEntranceConditionalSummonCodes = value; }
        internal bool BossEntranceConditionComplete { get => Mechanisms.BossEntranceConditionComplete; set => Mechanisms.BossEntranceConditionComplete = value; }
        internal bool ConditionalBossSpawned { get => Mechanisms.ConditionalBossSpawned; set => Mechanisms.ConditionalBossSpawned = value; }
        internal int ConditionalBossCode { get => Mechanisms.ConditionalBossCode; set => Mechanisms.ConditionalBossCode = value; }
        internal ScriptedFatalEndpointRuntime ScriptedFatalEndpoint { get => Mechanisms.ScriptedFatalEndpoint; set => Mechanisms.ScriptedFatalEndpoint = value; }
        internal bool HasBossEntranceConditionalSummon => Mechanisms.HasBossEntranceConditionalSummon;

        public int MazeIndex { get => Selection.MazeIndex; set => Selection.MazeIndex = value; }
        public int LayeredMapIndex { get => Selection.LayeredMapIndex; set => Selection.LayeredMapIndex = value; }
        public bool MazeQuestConnected { get => Selection.MazeQuestConnected; set => Selection.MazeQuestConnected = value; }
        public int ActiveQuestMazeQuestId { get => Selection.ActiveQuestMazeQuestId; set => Selection.ActiveQuestMazeQuestId = value; }
        public int MazeStartMapId { get => Selection.MazeStartMapId; set => Selection.MazeStartMapId = value; }
        public int MazeStartX { get => Selection.MazeStartX; set => Selection.MazeStartX = value; }
        public int MazeStartY { get => Selection.MazeStartY; set => Selection.MazeStartY = value; }
        public int TotalRoomCount { get => Selection.TotalRoomCount; set => Selection.TotalRoomCount = value; }
        public int EntryPartyMemberCount { get => Selection.EntryPartyMemberCount; set => Selection.EntryPartyMemberCount = value; }
        internal int ChronicleDropJobGroup { get => Selection.ChronicleDropJobGroup; set => Selection.ChronicleDropJobGroup = value; }
        internal int DimensionDropJob { get => Selection.DimensionDropJob; set => Selection.DimensionDropJob = value; }
        internal int DimensionDropGrowType { get => Selection.DimensionDropGrowType; set => Selection.DimensionDropGrowType = value; }
        public int LinkedDungeonNextId { get => Selection.LinkedDungeonNextId; set => Selection.LinkedDungeonNextId = value; }
        public int LinkedDungeonNextRate { get => Selection.LinkedDungeonNextRate; set => Selection.LinkedDungeonNextRate = value; }
        public int LinkedDungeonNextCondition { get => Selection.LinkedDungeonNextCondition; set => Selection.LinkedDungeonNextCondition = value; }

        internal bool TimeSpiralTeleportPending { get => Mechanisms.TimeSpiralTeleportPending; set => Mechanisms.TimeSpiralTeleportPending = value; }
        internal int TimeSpiralTrapMapId { get => Mechanisms.TimeSpiralTrapMapId; set => Mechanisms.TimeSpiralTrapMapId = value; }
        internal bool TimeSpiralTargetActive { get => Mechanisms.TimeSpiralTargetActive; set => Mechanisms.TimeSpiralTargetActive = value; }
        internal int TimeSpiralTargetX { get => Mechanisms.TimeSpiralTargetX; set => Mechanisms.TimeSpiralTargetX = value; }
        internal int TimeSpiralTargetY { get => Mechanisms.TimeSpiralTargetY; set => Mechanisms.TimeSpiralTargetY = value; }
        internal int TimeSpiralTargetFlag { get => Mechanisms.TimeSpiralTargetFlag; set => Mechanisms.TimeSpiralTargetFlag = value; }
        internal int TimeSpiralTargetWeight { get => Mechanisms.TimeSpiralTargetWeight; set => Mechanisms.TimeSpiralTargetWeight = value; }
        internal bool TimeSpiralHiddenBossActive { get => Mechanisms.TimeSpiralHiddenBossActive; set => Mechanisms.TimeSpiralHiddenBossActive = value; }
        internal ushort TimeSpiralHiddenBossSeqId { get => Mechanisms.TimeSpiralHiddenBossSeqId; set => Mechanisms.TimeSpiralHiddenBossSeqId = value; }
        internal int TimeSpiralHiddenBossCode { get => Mechanisms.TimeSpiralHiddenBossCode; set => Mechanisms.TimeSpiralHiddenBossCode = value; }
        internal int TimeSpiralHiddenBossMapId { get => Mechanisms.TimeSpiralHiddenBossMapId; set => Mechanisms.TimeSpiralHiddenBossMapId = value; }
        internal int TimeSpiralHiddenBossX { get => Mechanisms.TimeSpiralHiddenBossX; set => Mechanisms.TimeSpiralHiddenBossX = value; }
        internal int TimeSpiralHiddenBossY { get => Mechanisms.TimeSpiralHiddenBossY; set => Mechanisms.TimeSpiralHiddenBossY = value; }
        internal string TimeSpiralHiddenBossSource { get => Mechanisms.TimeSpiralHiddenBossSource; set => Mechanisms.TimeSpiralHiddenBossSource = value; }

        public bool HellMode { get => Selection.HellMode; set => Selection.HellMode = value; }
        internal bool TutorialEntryProjectionPending
        {
            get => Selection.TutorialEntryProjectionPending;
            set => Selection.TutorialEntryProjectionPending = value;
        }
        internal bool TutorialEntryProjectionSent
        {
            get => Selection.TutorialEntryProjectionSent;
            set => Selection.TutorialEntryProjectionSent = value;
        }
        internal bool TutorialEntryUsesInitialLayout
        {
            get => Selection.TutorialEntryUsesInitialLayout;
            set => Selection.TutorialEntryUsesInitialLayout = value;
        }

        internal bool IsA21TutorialEntry
        {
            get => Selection.IsA21TutorialEntry;
            set => Selection.IsA21TutorialEntry = value;
        }
        public byte HellPartyMode { get => Selection.HellPartyMode; set => Selection.HellPartyMode = value; }
        public bool VeryDifficultHell { get => Selection.VeryDifficultHell; set => Selection.VeryDifficultHell = value; }
        public bool HellGorgeousChallenge { get => Selection.HellGorgeousChallenge; set => Selection.HellGorgeousChallenge = value; }
        public int HellMapId { get => Selection.HellMapId; set => Selection.HellMapId = value; }
        public byte HellMapX { get => Selection.HellMapX; set => Selection.HellMapX = value; }
        public byte HellMapY { get => Selection.HellMapY; set => Selection.HellMapY = value; }
        public GameWorld.Dungeon.HellPartyRoomInfo HellRoomInfo { get => Selection.HellRoomInfo; set => Selection.HellRoomInfo = value; }

        public ushort MonsterCount { get => Combat.MonsterCount; set => Combat.MonsterCount = value; }
        public ushort RoomStartSequence { get => Combat.RoomStartSequence; set => Combat.RoomStartSequence = value; }
        public IReadOnlyList<GameWorld.Dungeon.MonsterSumInfo> RoomMonsters { get => Combat.RoomMonsters; set => Combat.RoomMonsters = value; }
        public HashSet<ushort> RoomKilledSeqIds { get => Combat.RoomKilledSeqIds; set => Combat.RoomKilledSeqIds = value; }
        public RoomKey RoomKey { get => Combat.RoomKey; set => Combat.RoomKey = value; }
        public Dictionary<RoomKey, RoomState> RoomStates { get => Combat.RoomStates; set => Combat.RoomStates = value; }
        public uint Seed { get => Combat.Seed; set => Combat.Seed = value; }
        public DnfLcg RoomLcg { get => Combat.RoomLcg; set => Combat.RoomLcg = value; }
        public List<RidableObjectSpawnEntry> RidableObjects { get => Combat.RidableObjects; set => Combat.RidableObjects = value; }
        public ClearConditionState ClearCondition { get => Combat.ClearCondition; set => Combat.ClearCondition = value; }
        public int BossCode { get => Combat.BossCode; set => Combat.BossCode = value; }
        public int[] BossMapPos { get => Combat.BossMapPos; set => Combat.BossMapPos = value; }
        public int SelectedBossMapId { get => Combat.SelectedBossMapId; set => Combat.SelectedBossMapId = value; }
        public uint TotalExp { get => Combat.TotalExp; set => Combat.TotalExp = value; }
        public uint BossTotalExp { get => Combat.BossTotalExp; set => Combat.BossTotalExp = value; }
        public uint ChampionTotalExp { get => Combat.ChampionTotalExp; set => Combat.ChampionTotalExp = value; }
        public uint SuperChampionTotalExp { get => Combat.SuperChampionTotalExp; set => Combat.SuperChampionTotalExp = value; }
        public uint NamedMonsterTotalExp { get => Combat.NamedMonsterTotalExp; set => Combat.NamedMonsterTotalExp = value; }
        public uint MonsterGrowthContractBonusExp { get => Combat.MonsterGrowthContractBonusExp; set => Combat.MonsterGrowthContractBonusExp = value; }
        public int TotalGold { get => Combat.TotalGold; set => Combat.TotalGold = value; }
        public ushort SceneSlotCounter { get => Combat.SceneSlotCounter; set => Combat.SceneSlotCounter = value; }
        public Dictionary<ushort, DropInfo> Drops { get => Combat.Drops; set => Combat.Drops = value; }

        internal DungeonParticipantExperienceSnapshot CaptureExperienceSnapshot()
        {
            lock (SyncRoot)
                return Combat.Experience.Capture();
        }

        internal bool TryFreezeExperienceBonusSnapshot(
            DungeonParticipantExperienceBonusSnapshot snapshot)
        {
            lock (SyncRoot)
                return Combat.Experience.TryFreezeBonusSnapshot(snapshot);
        }

        internal bool TryFreezeStoryExperienceProfile(
            int ratePercent,
            int experienceDifficulty)
        {
            lock (SyncRoot)
                return Combat.Experience.TryFreezeStoryExperienceProfile(
                    ratePercent,
                    experienceDifficulty);
        }

        internal DungeonParticipantExperienceBonusSnapshot
            CaptureExperienceBonusSnapshot()
        {
            lock (SyncRoot)
                return Combat.Experience.CaptureBonusSnapshot();
        }

        internal SecretShop.SecretShopOffer SecretShopOffer { get => Settlement.SecretShopOffer; set => Settlement.SecretShopOffer = value; }
        public List<ClearRewardGenerator.CardReward> CardRewards { get => Settlement.CardRewards; set => Settlement.CardRewards = value; }
        public int PaidCardCost { get => Settlement.PaidCardCost; set => Settlement.PaidCardCost = Math.Max(0, value); }
        public int CardFlipCount { get => Settlement.CardFlipCount; set => Settlement.CardFlipCount = value; }
        public byte[] FreeCardSlots { get => Settlement.FreeCardSlots; set => Settlement.FreeCardSlots = value; }
        public byte[] PaidCardSlots { get => Settlement.PaidCardSlots; set => Settlement.PaidCardSlots = value; }
        public bool FreeCardRewardDelivered { get => Settlement.FreeCardRewardDelivered; set => Settlement.FreeCardRewardDelivered = value; }
        public bool PaidCardRewardDelivered { get => Settlement.PaidCardRewardDelivered; set => Settlement.PaidCardRewardDelivered = value; }
        public DeathTower.DeathTowerSession Tower { get => Mechanisms.Tower; set => Mechanisms.Tower = value; }
        public bool IsWaitingDeathRespawn { get => Combat.IsWaitingDeathRespawn; set => Combat.IsWaitingDeathRespawn = value; }
        public DateTime DeathRespawnAvailableAt { get => Combat.DeathRespawnAvailableAt; set => Combat.DeathRespawnAvailableAt = value; }

        public DungeonRunIdentity CaptureIdentity() =>
            new DungeonRunIdentity(PartyDungeonInstanceId, RunId, RunGeneration);

        public DungeonInstanceIdentity CaptureInstanceIdentity() =>
            new DungeonInstanceIdentity(PartyDungeonInstanceId);

        public DungeonParticipantRunIdentity CaptureParticipantIdentity() =>
            new DungeonParticipantRunIdentity(
                CaptureInstanceIdentity(),
                RunId,
                RunGeneration);

        public DungeonRoomIdentity CaptureRoomIdentity() =>
            new DungeonRoomIdentity(
                CaptureInstanceIdentity(),
                CurrentRoomInstanceId);

        public DungeonParticipantRoomIdentity CaptureParticipantRoomIdentity() =>
            new DungeonParticipantRoomIdentity(
                CaptureIdentity(),
                CaptureRoomIdentity());

        public Guid GetSettlementSourceEventId()
        {
            lock (SyncRoot)
            {
                if (_clearedFact != null)
                    return _clearedFact.SourceEventId;
                if (_settlementSourceEventId == Guid.Empty)
                    _settlementSourceEventId = Guid.NewGuid();
                return _settlementSourceEventId;
            }
        }

        public bool Matches(DungeonRunIdentity identity) =>
            identity.IsValid && CaptureIdentity().Equals(identity);

        public bool Matches(DungeonRoomIdentity identity) =>
            identity.IsValid
            && CaptureInstanceIdentity().Equals(identity.Instance)
            && CurrentRoomInstanceId == identity.RoomInstanceId;

        public bool Matches(DungeonParticipantRoomIdentity identity) =>
            identity.IsValid
            && CaptureIdentity().Equals(identity.Run)
            && Matches(identity.Room);

        public bool SharesPhysicalInstanceWith(DungeonRun other) =>
            other != null
            && PartyDungeonInstanceId > 0
            && PartyDungeonInstanceId == other.PartyDungeonInstanceId;

        public bool SharesCurrentRoomWith(DungeonRun other) =>
            SharesPhysicalInstanceWith(other)
            && CurrentRoomInstanceId > 0
            && CaptureRoomIdentity().Equals(other.CaptureRoomIdentity());

        internal bool TryCaptureCurrentRoomSnapshot(
            DungeonRoomIdentity expectedRoom,
            out DungeonRunRoomSnapshot snapshot)
        {
            lock (SyncRoot)
            {
                snapshot = null;
                var roomIdentity = new DungeonRoomIdentity(
                    CaptureInstanceIdentity(),
                    CurrentRoomInstanceId);
                if (!expectedRoom.IsValid
                    || !roomIdentity.Equals(expectedRoom)
                    || !TryCreateCurrentRoomSnapshotLocked(
                        roomIdentity,
                        out snapshot))
                {
                    return false;
                }

                return true;
            }
        }

        internal Guid GetEndSourceEventId()
        {
            lock (SyncRoot)
            {
                if (_endSourceEventId == Guid.Empty)
                    _endSourceEventId = Guid.NewGuid();
                return _endSourceEventId;
            }
        }

        internal bool TryCaptureCurrentRoomSnapshot(
            out DungeonRunRoomSnapshot snapshot)
        {
            lock (SyncRoot)
            {
                snapshot = null;
                var roomIdentity = new DungeonRoomIdentity(
                    CaptureInstanceIdentity(),
                    CurrentRoomInstanceId);
                return roomIdentity.IsValid
                    && TryCreateCurrentRoomSnapshotLocked(
                        roomIdentity,
                        out snapshot);
            }
        }

        private bool TryCreateCurrentRoomSnapshotLocked(
            DungeonRoomIdentity roomIdentity,
            out DungeonRunRoomSnapshot snapshot)
        {
            if (!RoomStates.TryGetValue(RoomKey, out var roomState))
            {
                snapshot = null;
                return false;
            }

            var sourceMonsters = RoomMonsters
                ?? Array.Empty<GameWorld.Dungeon.MonsterSumInfo>();
            var monsters = new GameWorld.Dungeon.MonsterSumInfo[
                sourceMonsters.Count];
            for (var index = 0; index < sourceMonsters.Count; index++)
                monsters[index] = sourceMonsters[index];

            snapshot = new DungeonRunRoomSnapshot(
                CaptureIdentity(),
                roomIdentity,
                RoomKey,
                RoomStartSequence,
                monsters,
                roomState);
            return true;
        }

        public bool TryBeginSelecting()
        {
            lock (SyncRoot)
            {
                if (_runState == DungeonRunState.Selecting)
                    return false;
                if (_runState != DungeonRunState.Created)
                    return false;
                _runState = DungeonRunState.Selecting;
                return true;
            }
        }

        public bool TryActivate()
        {
            lock (SyncRoot)
            {
                if (_runState == DungeonRunState.Active)
                    return false;
                if (_runState != DungeonRunState.Created
                    && _runState != DungeonRunState.Selecting)
                    return false;
                _runState = DungeonRunState.Active;
                return true;
            }
        }

        public bool TryBeginClearCommit(DungeonClearedFact fact)
        {
            if (fact == null)
                throw new ArgumentNullException(nameof(fact));

            lock (SyncRoot)
            {
                if (_runState == DungeonRunState.ClearCommitting
                    && ReferenceEquals(_clearedFact, fact))
                    return false;
                if (_runState != DungeonRunState.Active)
                    return false;
                _clearedFact = fact;
                _runState = DungeonRunState.ClearCommitting;
                return true;
            }
        }

        public bool CanResumeClearCommit(DungeonClearedFact fact)
        {
            lock (SyncRoot)
            {
                return fact != null
                    && _runState == DungeonRunState.ClearCommitting
                    && ReferenceEquals(_clearedFact, fact);
            }
        }

        public bool TryCompleteClearCommit(DungeonClearedFact fact)
        {
            lock (SyncRoot)
            {
                if (_runState == DungeonRunState.Cleared
                    && ReferenceEquals(_clearedFact, fact))
                    return false;
                if (_runState != DungeonRunState.ClearCommitting
                    || !ReferenceEquals(_clearedFact, fact))
                    return false;
                _runState = DungeonRunState.Cleared;
                return true;
            }
        }

        public bool TryBeginSettlementPreparation()
        {
            lock (SyncRoot)
            {
                if (_runState != DungeonRunState.Cleared)
                    return false;
                if (_settlementState == DungeonSettlementState.Preparing)
                    return false;
                if (_settlementState != DungeonSettlementState.NotStarted)
                    return false;
                _settlementState = DungeonSettlementState.Preparing;
                return true;
            }
        }

        public bool TryBeginSettlementPreparationFromClear(
            DungeonClearedFact fact)
        {
            if (fact == null)
                throw new ArgumentNullException(nameof(fact));

            lock (SyncRoot)
            {
                if (_runState != DungeonRunState.ClearCommitting
                    || !ReferenceEquals(_clearedFact, fact)
                    || _settlementState != DungeonSettlementState.NotStarted)
                {
                    return false;
                }

                _settlementState = DungeonSettlementState.Preparing;
                return true;
            }
        }

        public bool CanResumeSettlementPreparationFromClear(
            DungeonClearedFact fact)
        {
            lock (SyncRoot)
            {
                return fact != null
                    && _runState == DungeonRunState.ClearCommitting
                    && ReferenceEquals(_clearedFact, fact)
                    && _settlementState == DungeonSettlementState.Preparing;
            }
        }

        internal bool TryQueueSettlementPresentation(int rankPoint)
        {
            lock (SyncRoot)
            {
                var canCaptureBeforePresentation =
                    _runState == DungeonRunState.ClearCommitting
                    && (_settlementState == DungeonSettlementState.NotStarted
                        || _settlementState == DungeonSettlementState.Preparing);
                var canCaptureAfterClear =
                    _runState == DungeonRunState.Cleared
                    && _settlementState == DungeonSettlementState.Preparing;
                if ((!canCaptureBeforePresentation && !canCaptureAfterClear)
                    || !RewardPolicy.AllowsSettlement)
                {
                    return false;
                }

                var normalizedRank = Math.Max(0, Math.Min(255, rankPoint));
                if (Settlement.CapturedPresentationRankPoint.HasValue)
                {
                    // SET_PLAY_RESULT is first-write-wins for the run. A
                    // replay with a different rank cannot rewrite rewards.
                    return true;
                }

                Settlement.CapturedPresentationRankPoint = normalizedRank;
                if (!Settlement.PendingPresentationRankPoint.HasValue)
                    Settlement.PendingPresentationRankPoint = normalizedRank;
                return true;
            }
        }

        internal bool TryGetCapturedSettlementRank(out int rankPoint)
        {
            lock (SyncRoot)
            {
                rankPoint = 0;
                if (!Settlement.CapturedPresentationRankPoint.HasValue)
                    return false;

                rankPoint = Settlement.CapturedPresentationRankPoint.Value;
                return true;
            }
        }

        internal bool TryGetPendingSettlementPresentation(out int rankPoint)
        {
            lock (SyncRoot)
            {
                rankPoint = 0;
                if (_runState != DungeonRunState.Cleared
                    || _settlementState != DungeonSettlementState.Preparing
                    || !Settlement.PendingPresentationRankPoint.HasValue)
                {
                    return false;
                }

                rankPoint = Settlement.PendingPresentationRankPoint.Value;
                return true;
            }
        }

        internal bool TryAcknowledgePendingSettlementPresentation(
            int rankPoint)
        {
            lock (SyncRoot)
            {
                if (!Settlement.PendingPresentationRankPoint.HasValue
                    || Settlement.PendingPresentationRankPoint.Value
                        != Math.Max(0, Math.Min(255, rankPoint)))
                {
                    return false;
                }

                Settlement.PendingPresentationRankPoint = null;
                return true;
            }
        }

        public bool CanResumeSettlementPreparation()
        {
            lock (SyncRoot)
            {
                return _runState == DungeonRunState.Cleared
                    && _settlementState == DungeonSettlementState.Preparing;
            }
        }

        public bool TryMarkResultShown()
        {
            lock (SyncRoot)
            {
                if (_settlementState == DungeonSettlementState.ResultShown)
                    return false;
                if (_settlementState != DungeonSettlementState.Preparing)
                    return false;
                _settlementState = DungeonSettlementState.ResultShown;
                return true;
            }
        }

        public bool TryMarkCardsRevealed()
        {
            lock (SyncRoot)
            {
                if (_settlementState == DungeonSettlementState.CardsRevealed)
                    return false;
                if (_settlementState != DungeonSettlementState.ResultShown)
                    return false;
                _settlementState = DungeonSettlementState.CardsRevealed;
                return true;
            }
        }

        public bool TryCompleteSettlement()
        {
            lock (SyncRoot)
            {
                if (_settlementState == DungeonSettlementState.Completed)
                    return false;
                if (_settlementState != DungeonSettlementState.ResultShown
                    && _settlementState != DungeonSettlementState.CardsRevealed)
                    return false;
                _settlementState = DungeonSettlementState.Completed;
                return true;
            }
        }

        public bool TryBeginEnding()
        {
            lock (SyncRoot)
            {
                if (_runState == DungeonRunState.Ending
                    || _runState == DungeonRunState.Ended)
                    return false;
                _runState = DungeonRunState.Ending;
                return true;
            }
        }

        public bool TryMarkEnded()
        {
            lock (SyncRoot)
            {
                if (_runState == DungeonRunState.Ended)
                    return false;
                if (_runState != DungeonRunState.Ending)
                    return false;
                _runState = DungeonRunState.Ended;
                return true;
            }
        }

        public void SetCurrentRoom(DungeonInstanceRoom room)
        {
            if (room == null)
                throw new ArgumentNullException(nameof(room));
            if (Instance == null)
                throw new InvalidOperationException("A room cannot be attached without a dungeon instance.");

            lock (SyncRoot)
                CurrentRoomInstanceId = room.RoomInstanceId;
        }

        internal bool TryMarkClearMapQuestSynced(int dungeonId, int mapId)
        {
            lock (SyncRoot)
                return QuestBridge.TryMarkClearMapSynced(dungeonId, mapId);
        }

        internal void UnmarkClearMapQuestSynced(int dungeonId, int mapId)
        {
            lock (SyncRoot)
                QuestBridge.UnmarkClearMapSynced(dungeonId, mapId);
        }

        internal void MarkServerDrivenQuestTrigger(
            ushort questId,
            int channelIndex)
        {
            if (questId == 0 || channelIndex < 0 || channelIndex > 2)
                return;

            lock (SyncRoot)
                QuestBridge.MarkServerTrigger(questId, channelIndex);
        }

        internal bool TryConsumeServerDrivenQuestTrigger(
            ushort questId,
            byte triggerType)
        {
            if (questId == 0)
                return false;

            var channelMask = triggerType == 0
                ? 0x01
                : (triggerType & 0x0F) == 0
                    ? (triggerType >> 4) & 0x07
                    : 0;
            if (channelMask == 0 || (triggerType & 0x80) != 0)
                return false;

            lock (SyncRoot)
                return QuestBridge.TryConsumeServerTrigger(
                    questId,
                    channelMask);
        }

        internal bool HasPendingServerDrivenQuestTriggers()
        {
            lock (SyncRoot)
                return QuestBridge.HasPendingServerTriggers();
        }

        internal bool TryMarkNpcItemDropGenerated(
            QuestActivationId activationId)
        {
            if (!activationId.IsValid)
                return false;

            lock (SyncRoot)
                return QuestBridge.TryMarkNpcItemDropGenerated(activationId);
        }

        internal void UnmarkNpcItemDropGenerated(
            QuestActivationId activationId)
        {
            if (!activationId.IsValid)
                return;

            lock (SyncRoot)
                QuestBridge.UnmarkNpcItemDropGenerated(activationId);
        }
    }

    internal sealed class BossEntranceConditionTargetState
    {
        internal int MonsterCode { get; set; }
        internal byte X { get; set; }
        internal byte Y { get; set; }
        internal bool Completed { get; set; }
    }
}
