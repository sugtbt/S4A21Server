using DfoServer.Game.Quests;
using DfoServer.Game.Dungeon.Tournament;
using System;
using System.Collections.Generic;
using System.Threading;

namespace DfoServer.Game.Dungeon
{
    internal readonly struct DungeonCaptureDropReservation
    {
        internal DungeonCaptureDropReservation(Guid sourceEventId, Guid leaseId)
        {
            SourceEventId = sourceEventId;
            LeaseId = leaseId;
        }

        internal Guid SourceEventId { get; }
        internal Guid LeaseId { get; }
        internal bool IsValid => SourceEventId != Guid.Empty && LeaseId != Guid.Empty;
    }

    internal sealed class DungeonCaptureDropJournal
    {
        private sealed class Entry
        {
            internal Guid LeaseId;
            internal IReadOnlyList<DropInfo> Drops;
            internal bool Committed;
        }

        private readonly object _syncRoot = new object();
        private readonly Dictionary<Guid, Entry> _entries =
            new Dictionary<Guid, Entry>();

        internal bool TryBegin(
            Guid sourceEventId,
            out DungeonCaptureDropReservation reservation,
            out IReadOnlyList<DropInfo> committedDrops)
        {
            lock (_syncRoot)
            {
                if (sourceEventId == Guid.Empty)
                {
                    reservation = default;
                    committedDrops = null;
                    return false;
                }

                if (_entries.TryGetValue(sourceEventId, out var existing))
                {
                    reservation = default;
                    committedDrops = existing.Committed
                        ? existing.Drops
                        : null;
                    return false;
                }

                var leaseId = Guid.NewGuid();
                _entries[sourceEventId] = new Entry { LeaseId = leaseId };
                reservation = new DungeonCaptureDropReservation(
                    sourceEventId,
                    leaseId);
                committedDrops = null;
                return true;
            }
        }

        internal bool TryCommit(
            DungeonCaptureDropReservation reservation,
            IReadOnlyList<DropInfo> drops)
        {
            lock (_syncRoot)
            {
                if (!TryGetOwned(reservation, out var entry))
                    return false;

                entry.Drops = drops == null || drops.Count == 0
                    ? Array.Empty<DropInfo>()
                    : new List<DropInfo>(drops).AsReadOnly();
                entry.Committed = true;
                entry.LeaseId = Guid.Empty;
                return true;
            }
        }

        internal bool TryFail(DungeonCaptureDropReservation reservation)
        {
            lock (_syncRoot)
            {
                if (!TryGetOwned(reservation, out _))
                    return false;

                _entries.Remove(reservation.SourceEventId);
                return true;
            }
        }

        private bool TryGetOwned(
            DungeonCaptureDropReservation reservation,
            out Entry entry)
        {
            entry = null;
            return reservation.IsValid
                && _entries.TryGetValue(reservation.SourceEventId, out entry)
                && !entry.Committed
                && entry.LeaseId == reservation.LeaseId;
        }
    }

    internal sealed class DungeonRunSelectionState
    {
        internal bool IsA21TutorialEntry { get; set; }
        internal int MazeIndex { get; set; } = -1;
        internal int LayeredMapIndex { get; set; } = -1;
        internal bool MazeQuestConnected { get; set; }
        internal int ActiveQuestMazeQuestId { get; set; }
        internal int MazeStartMapId { get; set; }
        internal int MazeStartX { get; set; } = -1;
        internal int MazeStartY { get; set; } = -1;
        internal int TotalRoomCount { get; set; } = 1;
        internal int EntryPartyMemberCount { get; set; } = 1;
        internal int ChronicleDropJobGroup { get; set; } = -1;
        internal int DimensionDropJob { get; set; } = -1;
        internal int DimensionDropGrowType { get; set; } = -1;
        internal int LinkedDungeonNextId { get; set; }
        internal int LinkedDungeonNextRate { get; set; }
        internal int LinkedDungeonNextCondition { get; set; }

        internal bool HellMode { get; set; }
        internal byte HellPartyMode { get; set; }
        internal bool VeryDifficultHell { get; set; }
        internal bool HellGorgeousChallenge { get; set; }
        internal int HellMapId { get; set; } = -1;
        internal byte HellMapX { get; set; } = 0xFF;
        internal byte HellMapY { get; set; } = 0xFF;
        internal GameWorld.Dungeon.HellPartyRoomInfo HellRoomInfo { get; set; }

        // A21 首次教程会先收到 CHANGE_TUTORIAL_FLAG，再允许投影
        // ENTER_SELECT_DUNGEON/ DUNGEON_INFO/START_MAP。该状态只属于当前
        // DungeonRun，避免旧 session 或旧 run 回调污染后续副本。
        internal bool TutorialEntryProjectionPending { get; set; }
        internal bool TutorialEntryProjectionSent { get; set; }
        internal bool TutorialEntryUsesInitialLayout { get; set; }
    }

    internal sealed class DungeonRunCombatState
    {
        internal ushort MonsterCount { get; set; }
        internal ushort RoomStartSequence { get; set; }
        internal IReadOnlyList<GameWorld.Dungeon.MonsterSumInfo> RoomMonsters { get; set; }
            = Array.Empty<GameWorld.Dungeon.MonsterSumInfo>();
        internal HashSet<ushort> RoomKilledSeqIds { get; set; }
            = new HashSet<ushort>();
        internal RoomKey RoomKey { get; set; }
        internal Dictionary<RoomKey, RoomState> RoomStates { get; set; }
            = new Dictionary<RoomKey, RoomState>();
        internal uint Seed { get; set; }
        internal DnfLcg RoomLcg { get; set; }
        internal List<RidableObjectSpawnEntry> RidableObjects { get; set; }
            = new List<RidableObjectSpawnEntry>();

        internal ClearConditionState ClearCondition { get; set; }
        internal int BossCode { get; set; }
        internal int[] BossMapPos { get; set; }
        internal int SelectedBossMapId { get; set; } = -1;

        internal DungeonParticipantExperienceRuntime Experience { get; } =
            new DungeonParticipantExperienceRuntime();
        internal uint TotalExp
        {
            get => Experience.MonsterTotalExperience;
            set => Experience.SetMonsterTotalForCompatibility(value);
        }
        internal uint BossTotalExp
        {
            get => Experience.BossBaseExperience;
            set => Experience.SetBossBaseForCompatibility(value);
        }
        internal uint ChampionTotalExp
        {
            get => Experience.ChampionBaseExperience;
            set => Experience.SetChampionBaseForCompatibility(value);
        }
        internal uint SuperChampionTotalExp
        {
            get => Experience.SuperChampionBaseExperience;
            set => Experience.SetSuperChampionBaseForCompatibility(value);
        }
        internal uint NamedMonsterTotalExp
        {
            get => Experience.NamedMonsterBaseExperience;
            set => Experience.SetNamedMonsterBaseForCompatibility(value);
        }
        internal uint MonsterGrowthContractBonusExp
        {
            get => Experience.MonsterGrowthContractBonusExperience;
            set => Experience.SetGrowthContractBonusForCompatibility(value);
        }
        internal int TotalGold { get; set; }

        internal ushort SceneSlotCounter { get; set; }
        internal Dictionary<ushort, DropInfo> Drops { get; set; }
            = new Dictionary<ushort, DropInfo>();
        internal bool IsWaitingDeathRespawn { get; set; }
        internal DateTime DeathRespawnAvailableAt { get; set; } = DateTime.MinValue;
    }

    internal sealed class DungeonRunSettlementData
    {
        internal SemaphoreSlim CardProjectionGate { get; } =
            new SemaphoreSlim(1, 1);
        internal SemaphoreSlim DeathTowerProjectionGate { get; } =
            new SemaphoreSlim(1, 1);
        internal SemaphoreSlim BloodAltarProjectionGate { get; } =
            new SemaphoreSlim(1, 1);
        internal DungeonSettlementRuntime Runtime { get; set; }
        internal DeathTower.DeathTowerSettlementRuntime DeathTower { get; set; }
        internal SecretShop.SecretShopOffer SecretShopOffer { get; set; }
        internal List<ClearRewardGenerator.CardReward> CardRewards { get; set; }
        internal int PaidCardCost { get; set; }
        internal int CardFlipCount { get; set; }
        internal byte[] FreeCardSlots { get; set; } = { 0xFF, 0xFF, 0xFF, 0xFF };
        internal byte[] PaidCardSlots { get; set; } = { 0xFF, 0xFF, 0xFF, 0xFF };
        internal bool FreeCardRewardDelivered { get; set; }
        internal bool PaidCardRewardDelivered { get; set; }
        internal int CardAutoFlipDelayMs { get; set; }
        internal int? CapturedPresentationRankPoint { get; set; }
        internal int? PendingPresentationRankPoint { get; set; }
        internal TournamentParticipantRewardState Tournament { get; set; }
    }

    internal sealed class DungeonRunQuestBridgeState
    {
        private readonly HashSet<(int DungeonId, int MapId)> _syncedClearMapTargets =
            new HashSet<(int DungeonId, int MapId)>();
        private readonly Dictionary<(ushort QuestId, int ChannelIndex), int>
            _pendingServerTriggerEchoes =
                new Dictionary<(ushort QuestId, int ChannelIndex), int>();
        private readonly HashSet<QuestActivationId> _npcItemDropActivations =
            new HashSet<QuestActivationId>();

        internal QuestRunSnapshot Snapshot { get; set; } = QuestRunSnapshot.Empty;
        internal DungeonCaptureDropJournal CaptureDrops { get; }
            = new DungeonCaptureDropJournal();

        internal bool TryMarkClearMapSynced(int dungeonId, int mapId)
            => _syncedClearMapTargets.Add((dungeonId, mapId));

        internal void UnmarkClearMapSynced(int dungeonId, int mapId)
            => _syncedClearMapTargets.Remove((dungeonId, mapId));

        internal void MarkServerTrigger(ushort questId, int channelIndex)
        {
            var key = (questId, channelIndex);
            _pendingServerTriggerEchoes.TryGetValue(key, out var pending);
            _pendingServerTriggerEchoes[key] = pending + 1;
        }

        internal bool TryConsumeServerTrigger(
            ushort questId,
            int channelMask)
        {
            for (var channelIndex = 0; channelIndex < 3; channelIndex++)
            {
                if ((channelMask & (1 << channelIndex)) == 0)
                    continue;

                if (!_pendingServerTriggerEchoes.TryGetValue(
                        (questId, channelIndex),
                        out var pending)
                    || pending <= 0)
                {
                    return false;
                }
            }

            for (var channelIndex = 0; channelIndex < 3; channelIndex++)
            {
                if ((channelMask & (1 << channelIndex)) == 0)
                    continue;

                var key = (questId, channelIndex);
                var pending = _pendingServerTriggerEchoes[key];
                if (pending == 1)
                    _pendingServerTriggerEchoes.Remove(key);
                else
                    _pendingServerTriggerEchoes[key] = pending - 1;
            }

            return true;
        }

        internal bool HasPendingServerTriggers()
            => _pendingServerTriggerEchoes.Count > 0;

        internal bool TryMarkNpcItemDropGenerated(
            QuestActivationId activationId)
            => activationId.IsValid
                && _npcItemDropActivations.Add(activationId);

        internal void UnmarkNpcItemDropGenerated(
            QuestActivationId activationId)
        {
            if (activationId.IsValid)
                _npcItemDropActivations.Remove(activationId);
        }
    }

    internal sealed class DungeonMechanismRuntimeSet
    {
        internal SpecialDungeonRuntime SpecialDungeon { get; set; }
        internal bool IgnoreDefaultDungeonClear { get; set; }
        internal IReadOnlyList<IReadOnlyList<(byte X, byte Y)>>
            SpecialMinimapIconGroups { get; set; }
        internal List<BossEntranceConditionTargetState>
            BossEntranceConditionTargets { get; set; }
                = new List<BossEntranceConditionTargetState>();
        internal List<int> BossEntranceConditionalSummonCodes { get; set; }
            = new List<int>();
        internal bool BossEntranceConditionComplete { get; set; }
        internal bool ConditionalBossSpawned { get; set; }
        internal int ConditionalBossCode { get; set; }
        internal ScriptedFatalEndpointRuntime ScriptedFatalEndpoint { get; set; }

        internal bool TimeSpiralTeleportPending { get; set; }
        internal int TimeSpiralTrapMapId { get; set; }
        internal bool TimeSpiralTargetActive { get; set; }
        internal int TimeSpiralTargetX { get; set; } = -1;
        internal int TimeSpiralTargetY { get; set; } = -1;
        internal int TimeSpiralTargetFlag { get; set; } = -1;
        internal int TimeSpiralTargetWeight { get; set; }
        internal bool TimeSpiralHiddenBossActive { get; set; }
        internal ushort TimeSpiralHiddenBossSeqId { get; set; }
        internal int TimeSpiralHiddenBossCode { get; set; }
        internal int TimeSpiralHiddenBossMapId { get; set; }
        internal int TimeSpiralHiddenBossX { get; set; } = -1;
        internal int TimeSpiralHiddenBossY { get; set; } = -1;
        internal string TimeSpiralHiddenBossSource { get; set; }

        internal DeathTower.DeathTowerSession Tower { get; set; }

        internal bool HasBossEntranceConditionalSummon =>
            BossEntranceConditionTargets != null
            && BossEntranceConditionTargets.Count > 0
            && BossEntranceConditionalSummonCodes != null
            && BossEntranceConditionalSummonCodes.Count > 0;
    }
}
