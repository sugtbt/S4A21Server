using System;
using System.Collections.Generic;
using System.Text.Json;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeon.BloodAltar;
using DfoServer.Game.Inventory;
using DfoServer.Game.Progression;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Game.Dungeon
{
    internal static class DungeonPersistentEffectKinds
    {
        internal const string SettlementExperienceGrant =
            "settlement-experience-grant";
        internal const string SettlementScoreExperienceAdjustment =
            "settlement-score-experience-adjustment";
        internal const string SuitableDungeonLuckyStar =
            "suitable-dungeon-lucky-star";
        internal const string SuitableDungeonDailyChallenge =
            "suitable-dungeon-daily-challenge";
        internal const string TowerOfDespairSettlementCommit =
            "tower-of-despair-settlement-commit";
        internal const string CardRewardFreeCommit =
            "card-reward-free";
        internal const string CardRewardPaidCommit =
            "card-reward-paid";
        internal const string BloodAltarRewardCommit =
            "blood-altar-reward";
    }

    internal sealed class SuitableDungeonLuckyStarResult
    {
        internal bool Granted { get; set; }
        internal ushort NewTotal { get; set; }
    }

    internal sealed class DungeonPersistentEffectRecoveryResult
    {
        internal ExperienceGrantResult LatestExperienceGrant { get; set; }
        internal int CommittedCount { get; set; }
        internal int DeadLetterCount { get; set; }
        internal int FailedCount { get; set; }
        internal int PagesScanned { get; set; }
        internal int RecordsScanned { get; set; }
        internal int RemainingCount { get; set; }
        internal bool ReachedPageLimit { get; set; }
        internal bool ReachedTimeLimit { get; set; }
        internal DungeonPersistentEffectRecoveryCursor? Continuation { get; set; }
        internal bool HasRemaining => RemainingCount > 0;
    }

    internal sealed class DungeonPersistentEffectRecoveryOptions
    {
        internal DungeonPersistentEffectRecoveryOptions(
            int pageSize = 64,
            int maximumPages = 4,
            TimeSpan? maximumDuration = null)
        {
            if (pageSize <= 0 || pageSize > 1024)
                throw new ArgumentOutOfRangeException(nameof(pageSize));
            if (maximumPages <= 0 || maximumPages > 128)
                throw new ArgumentOutOfRangeException(nameof(maximumPages));

            var duration = maximumDuration ?? TimeSpan.FromSeconds(3);
            if (duration <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(maximumDuration));
            }

            PageSize = pageSize;
            MaximumPages = maximumPages;
            MaximumDuration = duration;
        }

        internal int PageSize { get; }
        internal int MaximumPages { get; }
        internal TimeSpan MaximumDuration { get; }
    }

    internal sealed class SettlementExperienceEffectPayload
    {
        public int CharacterId { get; set; }
        public int AccountId { get; set; }
        public byte PreviousLevel { get; set; }
        public uint PreviousExp { get; set; }
        public uint RawGain { get; set; }
        public bool NormalizeMaxLevelExp { get; set; }
        public byte ExpectedDatabaseLevel { get; set; }
        public uint ExpectedDatabaseExp { get; set; }
    }

    internal sealed class SettlementExperienceEffectResult
    {
        public uint RawGain { get; set; }
        public uint HonorExpGain { get; set; }
        public uint NormalExpGain { get; set; }
        public byte PreviousLevel { get; set; }
        public uint PreviousExp { get; set; }
        public byte NewLevel { get; set; }
        public uint NewExp { get; set; }
        public bool NormalizedMaxLevelExp { get; set; }
        public bool Persisted { get; set; }
        public uint GrowthCapsuleExpGain { get; set; }
        public ulong TotalHonorExp { get; set; }
        public uint TotalGrowthCapsuleExp { get; set; }
    }

    internal sealed class SuitableDungeonLuckyStarEffectPayload
    {
        public int CharacterId { get; set; }
        public int AccountId { get; set; }
        public int DungeonId { get; set; }
        public int ClearLevel { get; set; }
        public int Amount { get; set; }
    }

    internal sealed class SuitableDungeonLuckyStarEffectResult
    {
        public bool Granted { get; set; }
        public ushort NewTotal { get; set; }
    }

    internal sealed class TowerOfDespairSettlementCommitResult
    {
        internal int NextFloor { get; set; }
        internal IReadOnlyList<TowerOfDespairGrantedReward> GrantedRewards { get; set; }
            = Array.Empty<TowerOfDespairGrantedReward>();
    }

    internal sealed class TowerOfDespairSettlementEffectReward
    {
        public int ItemId { get; set; }
        public int StackCount { get; set; }
    }

    internal sealed class TowerOfDespairSettlementEffectPayload
    {
        public int CharacterId { get; set; }
        public int AccountId { get; set; }
        public int ClearedDungeonId { get; set; }
        public int ClearedFloor { get; set; }
        public List<TowerOfDespairSettlementEffectReward> Rewards { get; set; }
            = new List<TowerOfDespairSettlementEffectReward>();
    }

    internal sealed class TowerOfDespairSettlementEffectGrantedReward
    {
        public int ItemId { get; set; }
        public int StackCount { get; set; }
        public int ListType { get; set; }
        public short Slot { get; set; }
    }

    internal sealed class TowerOfDespairSettlementEffectResult
    {
        public int NextFloor { get; set; }
        public List<TowerOfDespairSettlementEffectGrantedReward> Rewards { get; set; }
            = new List<TowerOfDespairSettlementEffectGrantedReward>();
    }

    internal sealed class CardRewardPersistentCommitResult
    {
        internal IReadOnlyList<InventorySlotMutation> Changes { get; set; }
            = Array.Empty<InventorySlotMutation>();
    }

    internal sealed class CardRewardEffectPayload
    {
        public int CharacterId { get; set; }
        public int AccountId { get; set; }
        public int Side { get; set; }
        public int PaidGoldCost { get; set; }
        public int RequestedGold { get; set; }
        public int ItemId { get; set; }
        public int StackCount { get; set; }
    }

    internal sealed class CardRewardEffectMutation
    {
        public int ListType { get; set; }
        public short Slot { get; set; }
    }

    internal sealed class CardRewardEffectResult
    {
        public List<CardRewardEffectMutation> Changes { get; set; }
            = new List<CardRewardEffectMutation>();
    }

    internal sealed class BloodAltarRewardEffectItem
    {
        public int ItemId { get; set; }
        public int StackCount { get; set; }
    }

    internal sealed class BloodAltarRewardEffectPayload
    {
        public int CharacterId { get; set; }
        public int AccountId { get; set; }
        public int CompletedRounds { get; set; }
        public int MaxRounds { get; set; }
        public int RequestedGold { get; set; }
        public List<BloodAltarRewardEffectItem> Items { get; set; }
            = new List<BloodAltarRewardEffectItem>();
    }

    internal sealed class BloodAltarRewardEffectMutation
    {
        public int ListType { get; set; }
        public short Slot { get; set; }
    }

    internal sealed class BloodAltarRewardEffectResult
    {
        public int RequestedGold { get; set; }
        public int GrantedGold { get; set; }
        public int FinalGold { get; set; }
        public int MailedRewardCount { get; set; }
        public List<BloodAltarRewardEffectMutation> Changes { get; set; }
            = new List<BloodAltarRewardEffectMutation>();
    }

    // Typed persistent effect dispatcher. Only registered payload kinds can
    // mutate state; unknown versions are moved to dead-letter without execution.
    internal sealed class DungeonPersistentEffectApplicationService
    {
        private const int PayloadVersion = 1;
        private const int ResultVersion = 1;
        private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);
        private static readonly JsonSerializerOptions JsonOptions =
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = false,
            };

        private readonly string _connectionString;
        private readonly IGameDatabase _database;
        private readonly DungeonPersistentEffectOutbox _outbox;
        private readonly DungeonPersistentEffectRecoveryOptions
            _recoveryOptions;
        private readonly Func<long> _monotonicMilliseconds;
        private readonly object _dependencySync = new object();
        private IInventoryOverflowRewardSink _overflowRewardSink;

        internal DungeonPersistentEffectApplicationService(
            string connectionString,
            DungeonPersistentEffectOutbox outbox = null,
            DungeonPersistentEffectRecoveryOptions recoveryOptions = null,
            Func<long> monotonicMilliseconds = null,
            IGameDatabase database = null,
            IInventoryOverflowRewardSink overflowRewardSink = null)
        {
            _connectionString = !string.IsNullOrWhiteSpace(connectionString)
                ? connectionString
                : throw new ArgumentException(
                    "A database connection string is required.",
                    nameof(connectionString));
            _outbox = outbox
                ?? new DungeonPersistentEffectOutbox(connectionString);
            _database = database
                ?? GameDatabase.AttachInitialized(connectionString);
            _recoveryOptions = recoveryOptions
                ?? new DungeonPersistentEffectRecoveryOptions();
            _monotonicMilliseconds = monotonicMilliseconds
                ?? (() => Environment.TickCount64);
            _overflowRewardSink = overflowRewardSink
                ?? RejectingInventoryOverflowRewardSink.Instance;
        }

        internal DungeonPersistentEffectOutbox Outbox => _outbox;

        internal void BindOverflowRewardSink(
            IInventoryOverflowRewardSink overflowRewardSink)
        {
            if (overflowRewardSink == null)
                throw new ArgumentNullException(nameof(overflowRewardSink));
            lock (_dependencySync)
            {
                if (ReferenceEquals(_overflowRewardSink, overflowRewardSink))
                    return;
                if (!ReferenceEquals(
                        _overflowRewardSink,
                        RejectingInventoryOverflowRewardSink.Instance))
                {
                    throw new InvalidOperationException(
                        "Persistent dungeon effect overflow sink is already bound.");
                }
                _overflowRewardSink = overflowRewardSink;
            }
        }

        internal bool TryApplySettlementExperience(
            DungeonEffectId effectId,
            int characterId,
            int accountId,
            byte previousLevel,
            uint previousExp,
            uint rawGain,
            out ExperienceGrantResult result,
            out string error)
            => TryApplySettlementExperienceCore(
                DungeonPersistentEffectKinds.SettlementExperienceGrant,
                effectId,
                characterId,
                accountId,
                previousLevel,
                previousExp,
                rawGain,
                out result,
                out error);

        internal bool TryApplySettlementScoreExperienceAdjustment(
            DungeonEffectId effectId,
            int characterId,
            int accountId,
            byte previousLevel,
            uint previousExp,
            uint rawGain,
            out ExperienceGrantResult result,
            out string error)
            => TryApplySettlementExperienceCore(
                DungeonPersistentEffectKinds.SettlementScoreExperienceAdjustment,
                effectId,
                characterId,
                accountId,
                previousLevel,
                previousExp,
                rawGain,
                out result,
                out error);

        private bool TryApplySettlementExperienceCore(
            string effectKind,
            DungeonEffectId effectId,
            int characterId,
            int accountId,
            byte previousLevel,
            uint previousExp,
            uint rawGain,
            out ExperienceGrantResult result,
            out string error)
        {
            result = null;
            error = null;
            try
            {
                ValidateEffectIdentity(
                    effectId,
                    effectKind,
                    characterId);
                var record = _outbox.Get(effectId);
                if (record == null)
                {
                    LoadCharacterProgress(
                        characterId,
                        out var expectedLevel,
                        out var expectedExp);
                    var payload = new SettlementExperienceEffectPayload
                    {
                        CharacterId = characterId,
                        AccountId = accountId,
                        PreviousLevel = previousLevel,
                        PreviousExp = previousExp,
                        RawGain = rawGain,
                        NormalizeMaxLevelExp = true,
                        ExpectedDatabaseLevel = expectedLevel,
                        ExpectedDatabaseExp = expectedExp,
                    };
                    _outbox.Enqueue(CreateDefinition(
                        effectId,
                        characterId,
                        accountId,
                        payload));
                    record = _outbox.Get(effectId);
                }

                var storedPayload = DeserializePayload<SettlementExperienceEffectPayload>(
                    record,
                    effectKind);
                if (storedPayload.CharacterId != characterId
                    || storedPayload.AccountId != accountId
                    || storedPayload.PreviousLevel != previousLevel
                    || storedPayload.PreviousExp != previousExp
                    || storedPayload.RawGain != rawGain)
                {
                    throw new InvalidOperationException(
                        "Settlement experience effect was retried with different inputs.");
                }

                return TryExecuteSettlementExperience(
                    record,
                    effectKind,
                    out result,
                    out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal bool TryApplySuitableDungeonLuckyStar(
            DungeonEffectId effectId,
            int characterId,
            int accountId,
            int dungeonId,
            int clearLevel,
            out SuitableDungeonLuckyStarResult result,
            out string error)
        {
            result = null;
            error = null;
            try
            {
                ValidateEffectIdentity(
                    effectId,
                    DungeonPersistentEffectKinds.SuitableDungeonLuckyStar,
                    characterId);
                var payload = new SuitableDungeonLuckyStarEffectPayload
                {
                    CharacterId = characterId,
                    AccountId = accountId,
                    DungeonId = dungeonId,
                    ClearLevel = clearLevel,
                    Amount = 1,
                };
                _outbox.Enqueue(CreateDefinition(
                    effectId,
                    characterId,
                    accountId,
                    payload));
                var record = _outbox.Get(effectId);
                var storedPayload = DeserializePayload<SuitableDungeonLuckyStarEffectPayload>(
                    record,
                    DungeonPersistentEffectKinds.SuitableDungeonLuckyStar);
                if (storedPayload.CharacterId != characterId
                    || storedPayload.AccountId != accountId
                    || storedPayload.DungeonId != dungeonId
                    || storedPayload.ClearLevel != clearLevel
                    || storedPayload.Amount != 1)
                {
                    throw new InvalidOperationException(
                        "Suitable-dungeon lucky-star effect was retried with different inputs.");
                }

                return TryExecuteSuitableDungeonLuckyStar(
                    record,
                    out result,
                    out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal bool TryApplyTowerOfDespairSettlement(
            DungeonEffectId effectId,
            InventoryLease lease,
            Guid ownerSessionId,
            int clearedDungeonId,
            IReadOnlyList<ClearRewardGenerator.CardReward> rewards,
            out TowerOfDespairSettlementCommitResult result,
            out string error)
        {
            result = null;
            error = null;
            var characterId = lease?.CharacterId ?? 0;
            var accountId = lease?.AccountId ?? 0;
            try
            {
                ValidateEffectIdentity(
                    effectId,
                    DungeonPersistentEffectKinds
                        .TowerOfDespairSettlementCommit,
                    characterId);
                if (!InventoryContext.IsCurrentLease(
                        lease,
                        ownerSessionId,
                        characterId))
                {
                    throw new InvalidOperationException(
                        "Tower of Despair settlement requires the current " +
                        "owned inventory lease.");
                }
                if (!DungeonData.TryGetTowerOfDespairFloor(
                        clearedDungeonId,
                        out var clearedFloor))
                {
                    throw new InvalidOperationException(
                        $"Dungeon {clearedDungeonId} is not a Tower of " +
                        "Despair floor.");
                }

                var payload = new TowerOfDespairSettlementEffectPayload
                {
                    CharacterId = characterId,
                    AccountId = accountId,
                    ClearedDungeonId = clearedDungeonId,
                    ClearedFloor = clearedFloor,
                    Rewards = NormalizeTowerRewards(rewards),
                };
                _outbox.Enqueue(CreateDefinition(
                    effectId,
                    characterId,
                    accountId,
                    payload));
                var record = _outbox.Get(effectId);
                return TryExecuteTowerOfDespairSettlement(
                    record,
                    lease,
                    ownerSessionId,
                    requireOwnedLease: true,
                    out result,
                    out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal bool TryApplyCardReward(
            DungeonEffectId effectId,
            InventoryLease lease,
            Guid ownerSessionId,
            CardRewardSide side,
            int paidGoldCost,
            IReadOnlyList<ClearRewardGenerator.CardReward> cards,
            out CardRewardPersistentCommitResult result,
            out string error)
        {
            result = null;
            error = null;
            var characterId = lease?.CharacterId ?? 0;
            var accountId = lease?.AccountId ?? 0;
            try
            {
                var effectKind = GetCardRewardEffectKind(side);
                ValidateEffectIdentity(effectId, effectKind, characterId);
                if (!InventoryContext.IsCurrentLease(
                        lease,
                        ownerSessionId,
                        characterId))
                {
                    throw new InvalidOperationException(
                        "Card reward requires the current owned inventory lease.");
                }

                var payload = BuildCardRewardPayload(
                    characterId,
                    accountId,
                    side,
                    paidGoldCost,
                    cards);
                _outbox.Enqueue(CreateDefinition(
                    effectId,
                    characterId,
                    accountId,
                    payload));
                var record = _outbox.Get(effectId);
                return TryExecuteCardReward(
                    record,
                    lease,
                    ownerSessionId,
                    requireOwnedLease: true,
                    out result,
                    out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal bool TryApplyBloodAltarReward(
            DungeonEffectId effectId,
            InventoryLease lease,
            Guid ownerSessionId,
            BloodAltarSettlementPlan settlement,
            out BloodAltarRewardCommitResult result,
            out string error)
        {
            result = null;
            error = null;
            var characterId = lease?.CharacterId ?? 0;
            var accountId = lease?.AccountId ?? 0;
            try
            {
                ValidateEffectIdentity(
                    effectId,
                    DungeonPersistentEffectKinds.BloodAltarRewardCommit,
                    characterId);
                if (!InventoryContext.IsCurrentLease(
                        lease,
                        ownerSessionId,
                        characterId))
                {
                    throw new InvalidOperationException(
                        "Blood altar reward requires the current owned " +
                        "inventory lease.");
                }

                var payload = BuildBloodAltarRewardPayload(
                    characterId,
                    accountId,
                    settlement);
                _outbox.Enqueue(CreateDefinition(
                    effectId,
                    characterId,
                    accountId,
                    payload));
                var record = _outbox.Get(effectId);
                return TryExecuteBloodAltarReward(
                    record,
                    lease,
                    ownerSessionId,
                    requireOwnedLease: true,
                    out result,
                    out error);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        internal DungeonPersistentEffectRecoveryResult RecoverCharacter(
            int characterId)
        {
            var result = new DungeonPersistentEffectRecoveryResult();
            if (characterId <= 0)
                return result;

            var startedAt = _monotonicMilliseconds();
            DungeonPersistentEffectRecoveryCursor? cursor = null;
            var stop = false;
            for (var pageIndex = 0;
                pageIndex < _recoveryOptions.MaximumPages && !stop;
                pageIndex++)
            {
                if (pageIndex > 0 && IsRecoveryTimeLimitReached(startedAt))
                {
                    result.ReachedTimeLimit = true;
                    break;
                }

                var page = _outbox.LoadRecoverableForCharacter(
                    characterId,
                    cursor,
                    _recoveryOptions.PageSize);
                if (page.Count == 0)
                    break;

                result.PagesScanned++;
                foreach (var record in page)
                {
                    RecoverRecord(record, result);
                    result.RecordsScanned++;
                    cursor = DungeonPersistentEffectRecoveryCursor.From(
                        record);
                    result.Continuation = cursor;

                    if (IsRecoveryTimeLimitReached(startedAt))
                    {
                        result.ReachedTimeLimit = true;
                        stop = true;
                        break;
                    }
                }

                if (page.Count < _recoveryOptions.PageSize)
                    break;
            }

            result.RemainingCount = _outbox.CountRecoverableForCharacter(
                characterId);
            result.ReachedPageLimit = !result.ReachedTimeLimit
                && result.RemainingCount > 0
                && result.PagesScanned >= _recoveryOptions.MaximumPages;
            return result;
        }

        private void RecoverRecord(
            DungeonPersistentEffectRecord record,
            DungeonPersistentEffectRecoveryResult result)
        {
            try
            {
                switch (record.EffectId.EffectKind)
                {
                    case DungeonPersistentEffectKinds.SettlementExperienceGrant:
                    case DungeonPersistentEffectKinds.SettlementScoreExperienceAdjustment:
                        if (TryExecuteSettlementExperience(
                                record,
                                record.EffectId.EffectKind,
                                out var experience,
                                out var experienceError))
                        {
                            result.LatestExperienceGrant = experience;
                            result.CommittedCount++;
                        }
                        else
                        {
                            result.FailedCount++;
                            LogRecoveryFailure(record, experienceError);
                        }
                        break;
                    case DungeonPersistentEffectKinds.SuitableDungeonLuckyStar:
                        if (TryExecuteSuitableDungeonLuckyStar(
                                record,
                                out _,
                                out var luckyStarError))
                        {
                            result.CommittedCount++;
                        }
                        else
                        {
                            result.FailedCount++;
                            LogRecoveryFailure(record, luckyStarError);
                        }
                        break;
                    case DungeonPersistentEffectKinds
                        .TowerOfDespairSettlementCommit:
                        if (TryExecuteTowerOfDespairSettlement(
                                record,
                                lease: null,
                                ownerSessionId: Guid.Empty,
                                requireOwnedLease: false,
                                out _,
                                out var towerError))
                        {
                            result.CommittedCount++;
                        }
                        else
                        {
                            result.FailedCount++;
                            LogRecoveryFailure(record, towerError);
                        }
                        break;
                    case DungeonPersistentEffectKinds.CardRewardFreeCommit:
                    case DungeonPersistentEffectKinds.CardRewardPaidCommit:
                        if (TryExecuteCardReward(
                                record,
                                lease: null,
                                ownerSessionId: Guid.Empty,
                                requireOwnedLease: false,
                                out _,
                                out var cardRewardError))
                        {
                            result.CommittedCount++;
                        }
                        else
                        {
                            result.FailedCount++;
                            LogRecoveryFailure(record, cardRewardError);
                        }
                        break;
                    case DungeonPersistentEffectKinds.BloodAltarRewardCommit:
                        if (TryExecuteBloodAltarReward(
                                record,
                                lease: null,
                                ownerSessionId: Guid.Empty,
                                requireOwnedLease: false,
                                out _,
                                out var bloodAltarError))
                        {
                            result.CommittedCount++;
                        }
                        else
                        {
                            result.FailedCount++;
                            LogRecoveryFailure(record, bloodAltarError);
                        }
                        break;
                    default:
                        if (TryDeadLetterUnknown(record))
                            result.DeadLetterCount++;
                        else
                            result.FailedCount++;
                        break;
                }
            }
            catch (Exception ex)
            {
                result.FailedCount++;
                LogRecoveryFailure(record, ex.Message);
            }
        }

        private bool IsRecoveryTimeLimitReached(long startedAt)
        {
            var elapsed = _monotonicMilliseconds() - startedAt;
            return elapsed >= 0
                && elapsed >= (long)Math.Ceiling(
                    _recoveryOptions.MaximumDuration.TotalMilliseconds);
        }

        private bool TryExecuteSettlementExperience(
            DungeonPersistentEffectRecord initialRecord,
            string effectKind,
            out ExperienceGrantResult result,
            out string error)
        {
            result = null;
            error = null;
            var claim = _outbox.TryClaim(
                initialRecord.EffectId,
                LeaseDuration,
                out var reservation,
                out var record);
            if (claim == DungeonPersistentEffectClaimResult.Committed)
                return TryReadExperienceResult(record, out result, out error);
            if (claim != DungeonPersistentEffectClaimResult.Claimed)
            {
                error = $"Persistent effect claim returned {claim}.";
                return false;
            }

            try
            {
                var payload = DeserializePayload<SettlementExperienceEffectPayload>(
                    record,
                    effectKind);
                using (var connection = new SqliteConnection(_connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction(deferred: false))
                    {
                        LoadCharacterProgress(
                            connection,
                            transaction,
                            payload.CharacterId,
                            out var currentLevel,
                            out var currentExp);
                        if (currentLevel != payload.ExpectedDatabaseLevel
                            || currentExp != payload.ExpectedDatabaseExp)
                        {
                            throw new PermanentPersistentEffectException(
                                $"Settlement experience expected database " +
                                $"{payload.ExpectedDatabaseLevel}/{payload.ExpectedDatabaseExp} " +
                                $"but found {currentLevel}/{currentExp}.");
                        }

                        result = CharacterExperienceService.GrantInTransaction(
                            connection,
                            transaction,
                            payload.CharacterId,
                            payload.AccountId,
                            payload.PreviousLevel,
                            payload.PreviousExp,
                            payload.RawGain,
                            payload.NormalizeMaxLevelExp);
                        if ((result.LeveledUp
                                || result.NormalExpGain > 0
                                || result.NormalizedMaxLevelExp)
                            && !result.Persisted)
                        {
                            throw new InvalidOperationException(
                                "Settlement experience did not persist character progress.");
                        }
                        var persistedResult = FromExperienceGrant(result);
                        if (!_outbox.TryCommitInTransaction(
                                connection,
                                transaction,
                                reservation,
                                ResultVersion,
                                Serialize(persistedResult),
                                _outbox.UtcNowMilliseconds))
                        {
                            throw new InvalidOperationException(
                                "Settlement experience effect lease was lost before commit.");
                        }
                        transaction.Commit();
                    }
                }
                return true;
            }
            catch (PermanentPersistentEffectException ex)
            {
                _outbox.TryDeadLetter(reservation, ex.Message);
                error = ex.Message;
                result = null;
                return false;
            }
            catch (Exception ex)
            {
                if (TryReadCommittedExperienceAfterError(
                        initialRecord.EffectId,
                        out result))
                {
                    return true;
                }
                _outbox.TryFail(reservation, ex.Message);
                error = ex.Message;
                result = null;
                return false;
            }
        }

        private bool TryExecuteSuitableDungeonLuckyStar(
            DungeonPersistentEffectRecord initialRecord,
            out SuitableDungeonLuckyStarResult result,
            out string error)
        {
            result = null;
            error = null;
            var claim = _outbox.TryClaim(
                initialRecord.EffectId,
                LeaseDuration,
                out var reservation,
                out var record);
            if (claim == DungeonPersistentEffectClaimResult.Committed)
                return TryReadLuckyStarResult(record, out result, out error);
            if (claim != DungeonPersistentEffectClaimResult.Claimed)
            {
                error = $"Persistent effect claim returned {claim}.";
                return false;
            }

            try
            {
                var payload = DeserializePayload<SuitableDungeonLuckyStarEffectPayload>(
                    record,
                    DungeonPersistentEffectKinds.SuitableDungeonLuckyStar);
                if (payload.Amount != 1)
                    throw new PermanentPersistentEffectException(
                        "Suitable-dungeon lucky-star amount is not supported.");

                using (var connection = new SqliteConnection(_connectionString))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction(deferred: false))
                    {
                        var wallet = CurrencyService.LoadWallet(
                            connection,
                            transaction,
                            payload.CharacterId);
                        var granted = wallet.LuckyStar
                            < RentalCatalogCodec.MaxLuckyStar;
                        if (granted)
                        {
                            CurrencyService.GrantLuckyStar(
                                connection,
                                transaction,
                                payload.AccountId,
                                payload.Amount);
                        }
                        var newTotal = (ushort)Math.Min(
                            RentalCatalogCodec.MaxLuckyStar,
                            wallet.LuckyStar + (granted ? payload.Amount : 0));
                        result = new SuitableDungeonLuckyStarResult
                        {
                            Granted = granted,
                            NewTotal = newTotal,
                        };
                        var persistedResult = new SuitableDungeonLuckyStarEffectResult
                        {
                            Granted = result.Granted,
                            NewTotal = result.NewTotal,
                        };
                        if (!_outbox.TryCommitInTransaction(
                                connection,
                                transaction,
                                reservation,
                                ResultVersion,
                                Serialize(persistedResult),
                                _outbox.UtcNowMilliseconds))
                        {
                            throw new InvalidOperationException(
                                "Lucky-star effect lease was lost before commit.");
                        }
                        transaction.Commit();
                    }
                }
                return true;
            }
            catch (PermanentPersistentEffectException ex)
            {
                _outbox.TryDeadLetter(reservation, ex.Message);
                error = ex.Message;
                result = null;
                return false;
            }
            catch (Exception ex)
            {
                if (TryReadCommittedLuckyStarAfterError(
                        initialRecord.EffectId,
                        out result))
                {
                    return true;
                }
                _outbox.TryFail(reservation, ex.Message);
                error = ex.Message;
                result = null;
                return false;
            }
        }

        private bool TryExecuteTowerOfDespairSettlement(
            DungeonPersistentEffectRecord initialRecord,
            InventoryLease lease,
            Guid ownerSessionId,
            bool requireOwnedLease,
            out TowerOfDespairSettlementCommitResult result,
            out string error)
        {
            result = null;
            error = null;
            var claim = _outbox.TryClaim(
                initialRecord.EffectId,
                LeaseDuration,
                out var reservation,
                out var record);
            if (claim == DungeonPersistentEffectClaimResult.Committed)
                return TryReadTowerOfDespairResult(record, out result, out error);
            if (claim != DungeonPersistentEffectClaimResult.Claimed)
            {
                error = $"Persistent effect claim returned {claim}.";
                return false;
            }

            InventoryService inventory = null;
            DungeonItemGrantBatchPlan snapshotPlan = null;
            DungeonItemGrantMutationSnapshot rollback = null;
            var inventoryMutated = false;
            var committed = false;
            try
            {
                var payload = DeserializePayload<TowerOfDespairSettlementEffectPayload>(
                    record,
                    DungeonPersistentEffectKinds
                        .TowerOfDespairSettlementCommit);
                ValidateTowerOfDespairPayload(payload, record);

                var effectiveLease = lease;
                if (effectiveLease == null)
                {
                    using (var connection = _database.OpenConnection())
                    {
                        inventory = InventoryService.LoadFromDb(
                            connection,
                            payload.CharacterId,
                            payload.AccountId,
                            _database);
                    }
                    effectiveLease = new InventoryLease(
                        Guid.NewGuid(),
                        payload.CharacterId,
                        inventory,
                        version: 1);
                }
                else
                {
                    inventory = effectiveLease.Inventory;
                }

                lock (effectiveLease.SyncRoot)
                {
                    if (effectiveLease.CharacterId != payload.CharacterId
                        || effectiveLease.AccountId != payload.AccountId
                        || (requireOwnedLease
                            && !InventoryContext.IsCurrentLease(
                                effectiveLease,
                                ownerSessionId,
                                payload.CharacterId)))
                    {
                        throw new InvalidOperationException(
                            "Tower of Despair inventory lease changed before " +
                            "settlement commit.");
                    }

                    if (!TryBuildTowerOfDespairRewardPlan(
                            inventory,
                            payload.Rewards,
                            out var inventoryPlan,
                            out var planError))
                    {
                        throw new InvalidOperationException(
                            "Tower of Despair reward planning failed: " +
                            planError);
                    }
                    snapshotPlan = new DungeonItemGrantBatchPlan
                    {
                        Success = true,
                        InventoryPlan = inventoryPlan,
                    };
                    if (!DungeonItemGrantMutationSnapshot.TryCapture(
                            inventory,
                            snapshotPlan,
                            out rollback))
                    {
                        throw new InvalidOperationException(
                            "Tower of Despair reward snapshot failed.");
                    }
                    var applied = InventoryRewardGrantService
                        .TryApplyPreparedBatch(
                            inventory,
                            inventoryPlan,
                            out var grant);
                    inventoryMutated = grant?.Results.Count > 0;
                    if (!applied
                        || !grant.Success
                        || grant.Results.Count != payload.Rewards.Count)
                    {
                        throw new InvalidOperationException(
                            "Tower of Despair reward application failed: " +
                            (grant?.Error.ToString() ?? "unknown"));
                    }

                    using (var connection = _database.OpenConnection())
                    using (var transaction = connection.BeginTransaction(
                               deferred: false))
                    {
                        var nextFloor = TowerOfDespairProgressRepository
                            .RecordClearInTransaction(
                                connection,
                                transaction,
                                payload.CharacterId,
                                payload.ClearedFloor);
                        if (!InventoryPersistenceService.SaveDirtyInTransaction(
                                connection,
                                transaction,
                                effectiveLease))
                        {
                            throw new InvalidOperationException(
                                "Tower of Despair inventory persistence " +
                                "returned false.");
                        }
                        if (requireOwnedLease
                            && !InventoryContext.IsCurrentLease(
                                effectiveLease,
                                ownerSessionId,
                                payload.CharacterId))
                        {
                            throw new InvalidOperationException(
                                "Tower of Despair inventory lease changed " +
                                "before transaction commit.");
                        }

                        var persistedResult = BuildTowerOfDespairEffectResult(
                            nextFloor,
                            grant.Results);
                        if (!_outbox.TryCommitInTransaction(
                                connection,
                                transaction,
                                reservation,
                                ResultVersion,
                                Serialize(persistedResult),
                                _outbox.UtcNowMilliseconds))
                        {
                            throw new InvalidOperationException(
                                "Tower of Despair effect lease was lost " +
                                "before commit.");
                        }

                        transaction.Commit();
                        committed = true;
                        inventory.ClearDirtyState();
                        result = ToTowerOfDespairCommitResult(
                            persistedResult);
                    }
                }

                return true;
            }
            catch (PermanentPersistentEffectException ex)
            {
                if (!committed)
                {
                    RecoverTowerOfDespairInventoryAfterFailure(
                        lease,
                        inventory,
                        snapshotPlan,
                        rollback,
                        inventoryMutated);
                }
                _outbox.TryDeadLetter(reservation, ex.Message);
                error = ex.Message;
                result = null;
                return false;
            }
            catch (Exception ex)
            {
                if (TryReadCommittedTowerOfDespairAfterError(
                        initialRecord.EffectId,
                        out result))
                {
                    return true;
                }
                if (!committed)
                {
                    RecoverTowerOfDespairInventoryAfterFailure(
                        lease,
                        inventory,
                        snapshotPlan,
                        rollback,
                        inventoryMutated);
                }
                _outbox.TryFail(reservation, ex.Message);
                error = ex.Message;
                result = null;
                return false;
            }
        }

        private bool TryExecuteCardReward(
            DungeonPersistentEffectRecord initialRecord,
            InventoryLease lease,
            Guid ownerSessionId,
            bool requireOwnedLease,
            out CardRewardPersistentCommitResult result,
            out string error)
        {
            result = null;
            error = null;
            var claim = _outbox.TryClaim(
                initialRecord.EffectId,
                LeaseDuration,
                out var reservation,
                out var record);
            if (claim == DungeonPersistentEffectClaimResult.Committed)
                return TryReadCardRewardResult(record, out result, out error);
            if (claim != DungeonPersistentEffectClaimResult.Claimed)
            {
                error = $"Persistent effect claim returned {claim}.";
                return false;
            }

            InventoryService inventory = null;
            InventoryRewardGrantBatchPlan inventoryPlan = null;
            CardRewardInventoryMutationSnapshot rollback = null;
            var inventoryMutated = false;
            var committed = false;
            try
            {
                var payload = DeserializePayload<CardRewardEffectPayload>(
                    record,
                    record.EffectId.EffectKind);
                ValidateCardRewardPayload(payload, record);

                var effectiveLease = lease;
                if (effectiveLease == null)
                {
                    using (var connection = _database.OpenConnection())
                    {
                        inventory = InventoryService.LoadFromDb(
                            connection,
                            payload.CharacterId,
                            payload.AccountId,
                            _database);
                    }
                    effectiveLease = new InventoryLease(
                        Guid.NewGuid(),
                        payload.CharacterId,
                        inventory,
                        version: 1);
                }
                else
                {
                    inventory = effectiveLease.Inventory;
                }

                lock (effectiveLease.SyncRoot)
                {
                    if (effectiveLease.CharacterId != payload.CharacterId
                        || effectiveLease.AccountId != payload.AccountId
                        || (requireOwnedLease
                            && !InventoryContext.IsCurrentLease(
                                effectiveLease,
                                ownerSessionId,
                                payload.CharacterId)))
                    {
                        throw new InvalidOperationException(
                            "Card reward inventory lease changed before commit.");
                    }

                    if (!TryBuildCardRewardPlan(
                            inventory,
                            payload,
                            out inventoryPlan,
                            out var planError))
                    {
                        throw new InvalidOperationException(
                            "Card reward planning failed: " + planError);
                    }
                    rollback = CardRewardInventoryMutationSnapshot.Capture(
                        inventory,
                        inventoryPlan,
                        includeGold: payload.PaidGoldCost > 0);
                    inventoryMutated = payload.PaidGoldCost > 0
                        || inventoryPlan.Entries.Count > 0;

                    var changes = new List<InventorySlotMutation>();
                    if (payload.PaidGoldCost > 0)
                    {
                        if (!inventory.TryConsumeMainItem(
                                InventoryService.MainVirtualCurrencySlotStart,
                                payload.PaidGoldCost,
                                out var consumeResult)
                            || !consumeResult.Success)
                        {
                            throw new InvalidOperationException(
                                "Card reward paid-card gold is insufficient.");
                        }
                        AddCardRewardChanges(changes, consumeResult.Changes);
                        inventoryMutated = true;
                    }

                    if (!InventoryRewardGrantService.TryApplyPreparedBatch(
                            inventory,
                            inventoryPlan,
                            out var grantBatch)
                        || !grantBatch.Success)
                    {
                        throw new InvalidOperationException(
                            "Card reward application failed: " +
                            (grantBatch?.Error.ToString() ?? "unknown"));
                    }
                    AddCardRewardChanges(changes, grantBatch.Changes);
                    inventoryMutated = inventoryMutated
                        || grantBatch.Changes.HasChanges;

                    using (var connection = _database.OpenConnection())
                    using (var transaction = connection.BeginTransaction(
                               deferred: false))
                    {
                        if (!InventoryPersistenceService.SaveDirtyInTransaction(
                                connection,
                                transaction,
                                effectiveLease))
                        {
                            throw new InvalidOperationException(
                                "Card reward inventory persistence returned false.");
                        }
                        if (requireOwnedLease
                            && !InventoryContext.IsCurrentLease(
                                effectiveLease,
                                ownerSessionId,
                                payload.CharacterId))
                        {
                            throw new InvalidOperationException(
                                "Card reward inventory lease changed before " +
                                "transaction commit.");
                        }

                        var persistedResult = BuildCardRewardEffectResult(changes);
                        if (!_outbox.TryCommitInTransaction(
                                connection,
                                transaction,
                                reservation,
                                ResultVersion,
                                Serialize(persistedResult),
                                _outbox.UtcNowMilliseconds))
                        {
                            throw new InvalidOperationException(
                                "Card reward effect lease was lost before commit.");
                        }

                        transaction.Commit();
                        committed = true;
                        inventory.ClearDirtyState();
                        result = ToCardRewardCommitResult(persistedResult);
                    }
                }

                return true;
            }
            catch (PermanentPersistentEffectException ex)
            {
                if (!committed)
                {
                    RecoverCardRewardInventoryAfterFailure(
                        lease,
                        inventory,
                        inventoryPlan,
                        rollback,
                        inventoryMutated);
                }
                _outbox.TryDeadLetter(reservation, ex.Message);
                error = ex.Message;
                result = null;
                return false;
            }
            catch (Exception ex)
            {
                if (TryReadCommittedCardRewardAfterError(
                        initialRecord.EffectId,
                        out result))
                {
                    inventory?.ClearDirtyState();
                    return true;
                }
                if (!committed)
                {
                    RecoverCardRewardInventoryAfterFailure(
                        lease,
                        inventory,
                        inventoryPlan,
                        rollback,
                        inventoryMutated);
                }
                _outbox.TryFail(reservation, ex.Message);
                error = ex.Message;
                result = null;
                return false;
            }
        }

        private bool TryExecuteBloodAltarReward(
            DungeonPersistentEffectRecord initialRecord,
            InventoryLease lease,
            Guid ownerSessionId,
            bool requireOwnedLease,
            out BloodAltarRewardCommitResult result,
            out string error)
        {
            result = null;
            error = null;
            var claim = _outbox.TryClaim(
                initialRecord.EffectId,
                LeaseDuration,
                out var reservation,
                out var record);
            if (claim == DungeonPersistentEffectClaimResult.Committed)
                return TryReadBloodAltarRewardResult(record, out result, out error);
            if (claim != DungeonPersistentEffectClaimResult.Claimed)
            {
                error = $"Persistent effect claim returned {claim}.";
                return false;
            }

            InventoryService inventory = null;
            InventoryRewardGrantBatchPlan inventoryPlan = null;
            DungeonItemGrantBatchPlan snapshotPlan = null;
            DungeonItemGrantMutationSnapshot rollback = null;
            var inventoryMutated = false;
            var committed = false;
            try
            {
                var payload = DeserializePayload<BloodAltarRewardEffectPayload>(
                    record,
                    DungeonPersistentEffectKinds.BloodAltarRewardCommit);
                ValidateBloodAltarRewardPayload(payload, record);

                var effectiveLease = lease;
                if (effectiveLease == null)
                {
                    using (var connection = _database.OpenConnection())
                    {
                        inventory = InventoryService.LoadFromDb(
                            connection,
                            payload.CharacterId,
                            payload.AccountId,
                            _database);
                    }
                    effectiveLease = new InventoryLease(
                        Guid.NewGuid(),
                        payload.CharacterId,
                        inventory,
                        version: 1);
                }
                else
                {
                    inventory = effectiveLease.Inventory;
                }

                lock (effectiveLease.SyncRoot)
                {
                    if (effectiveLease.CharacterId != payload.CharacterId
                        || effectiveLease.AccountId != payload.AccountId
                        || (requireOwnedLease
                            && !InventoryContext.IsCurrentLease(
                                effectiveLease,
                                ownerSessionId,
                                payload.CharacterId)))
                    {
                        throw new InvalidOperationException(
                            "Blood altar inventory lease changed before commit.");
                    }

                    if (!TryBuildBloodAltarRewardPlan(
                            inventory,
                            payload,
                            out inventoryPlan,
                            out var overflowRewards,
                            out var grantedGold,
                            out var planError))
                    {
                        throw new InvalidOperationException(
                            "Blood altar reward planning failed: " + planError);
                    }

                    snapshotPlan = new DungeonItemGrantBatchPlan
                    {
                        Success = true,
                        InventoryPlan = inventoryPlan,
                    };
                    if (!DungeonItemGrantMutationSnapshot.TryCapture(
                            inventory,
                            snapshotPlan,
                            out rollback))
                    {
                        throw new InvalidOperationException(
                            "Blood altar reward snapshot failed.");
                    }
                    inventoryMutated = inventoryPlan.Entries.Count > 0;

                    if (!InventoryRewardGrantService.TryApplyPreparedBatch(
                            inventory,
                            inventoryPlan,
                            out var grantBatch)
                        || !grantBatch.Success)
                    {
                        throw new InvalidOperationException(
                            "Blood altar reward application failed: " +
                            (grantBatch?.Error.ToString() ?? "unknown"));
                    }
                    inventoryMutated = inventoryMutated
                        || grantBatch.Changes.HasChanges;

                    using (var connection = _database.OpenConnection())
                    using (var transaction = connection.BeginTransaction(
                               deferred: false))
                    {
                        if (overflowRewards.Count > 0)
                        {
                            var transactionSink =
                                new TransactionBoundInventoryOverflowRewardSink(
                                    connection,
                                    transaction,
                                    GetOverflowRewardSink());
                            if (!transactionSink.TryDeliver(
                                    inventory,
                                    overflowRewards,
                                    out _))
                            {
                                throw new InvalidOperationException(
                                    "Blood altar overflow reward delivery failed.");
                            }
                        }
                        if (!InventoryPersistenceService.SaveDirtyInTransaction(
                                connection,
                                transaction,
                                effectiveLease))
                        {
                            throw new InvalidOperationException(
                                "Blood altar inventory persistence returned false.");
                        }
                        if (requireOwnedLease
                            && !InventoryContext.IsCurrentLease(
                                effectiveLease,
                                ownerSessionId,
                                payload.CharacterId))
                        {
                            throw new InvalidOperationException(
                                "Blood altar inventory lease changed before " +
                                "transaction commit.");
                        }

                        var persistedResult = BuildBloodAltarRewardEffectResult(
                            payload.RequestedGold,
                            grantedGold,
                            inventory.CountMainItem(
                                InventoryService.MainVirtualCurrencySlotStart),
                            overflowRewards.Count,
                            grantBatch.Changes);
                        if (!_outbox.TryCommitInTransaction(
                                connection,
                                transaction,
                                reservation,
                                ResultVersion,
                                Serialize(persistedResult),
                                _outbox.UtcNowMilliseconds))
                        {
                            throw new InvalidOperationException(
                                "Blood altar reward effect lease was lost " +
                                "before commit.");
                        }

                        transaction.Commit();
                        committed = true;
                        inventory.ClearDirtyState();
                        result = ToBloodAltarRewardCommitResult(
                            persistedResult);
                    }
                }

                return true;
            }
            catch (PermanentPersistentEffectException ex)
            {
                if (!committed)
                {
                    RecoverBloodAltarInventoryAfterFailure(
                        lease,
                        inventory,
                        snapshotPlan,
                        rollback,
                        inventoryMutated);
                }
                _outbox.TryDeadLetter(reservation, ex.Message);
                error = ex.Message;
                result = null;
                return false;
            }
            catch (Exception ex)
            {
                if (TryReadCommittedBloodAltarRewardAfterError(
                        initialRecord.EffectId,
                        out result))
                {
                    inventory?.ClearDirtyState();
                    return true;
                }
                if (!committed)
                {
                    RecoverBloodAltarInventoryAfterFailure(
                        lease,
                        inventory,
                        snapshotPlan,
                        rollback,
                        inventoryMutated);
                }
                _outbox.TryFail(reservation, ex.Message);
                error = ex.Message;
                result = null;
                return false;
            }
        }

        private bool TryDeadLetterUnknown(DungeonPersistentEffectRecord record)
        {
            var claim = _outbox.TryClaim(
                record.EffectId,
                LeaseDuration,
                out var reservation,
                out _);
            return claim == DungeonPersistentEffectClaimResult.DeadLetter
                || (claim == DungeonPersistentEffectClaimResult.Claimed
                    && _outbox.TryDeadLetter(
                        reservation,
                        $"Unknown persistent dungeon effect kind " +
                        $"'{record.EffectId.EffectKind}' version " +
                        $"{record.PayloadVersion}."));
        }

        private bool TryReadCommittedExperienceAfterError(
            DungeonEffectId effectId,
            out ExperienceGrantResult result)
        {
            result = null;
            try
            {
                var record = _outbox.Get(effectId);
                return record?.State == DungeonPersistentEffectState.Committed
                    && TryReadExperienceResult(record, out result, out _);
            }
            catch
            {
                return false;
            }
        }

        private bool TryReadCommittedLuckyStarAfterError(
            DungeonEffectId effectId,
            out SuitableDungeonLuckyStarResult result)
        {
            result = null;
            try
            {
                var record = _outbox.Get(effectId);
                return record?.State == DungeonPersistentEffectState.Committed
                    && TryReadLuckyStarResult(record, out result, out _);
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadExperienceResult(
            DungeonPersistentEffectRecord record,
            out ExperienceGrantResult result,
            out string error)
        {
            result = null;
            error = null;
            if (!TryDeserializeResult(
                    record,
                    out SettlementExperienceEffectResult persisted,
                    out error))
            {
                return false;
            }
            result = ToExperienceGrant(persisted);
            return true;
        }

        private static bool TryReadLuckyStarResult(
            DungeonPersistentEffectRecord record,
            out SuitableDungeonLuckyStarResult result,
            out string error)
        {
            result = null;
            error = null;
            if (!TryDeserializeResult(
                    record,
                    out SuitableDungeonLuckyStarEffectResult persisted,
                    out error))
            {
                return false;
            }
            result = new SuitableDungeonLuckyStarResult
            {
                Granted = persisted.Granted,
                NewTotal = persisted.NewTotal,
            };
            return true;
        }

        private static List<TowerOfDespairSettlementEffectReward>
            NormalizeTowerRewards(
                IReadOnlyList<ClearRewardGenerator.CardReward> rewards)
        {
            var normalized = new List<TowerOfDespairSettlementEffectReward>();
            if (rewards == null)
                return normalized;

            foreach (var reward in rewards)
            {
                if (reward.IsGold
                    || reward.ItemId <= 0
                    || reward.StackCount <= 0)
                {
                    continue;
                }

                normalized.Add(new TowerOfDespairSettlementEffectReward
                {
                    ItemId = reward.ItemId,
                    StackCount = reward.StackCount,
                });
            }
            return normalized;
        }

        private static BloodAltarRewardEffectPayload
            BuildBloodAltarRewardPayload(
                int characterId,
                int accountId,
                BloodAltarSettlementPlan settlement)
        {
            if (settlement == null)
                throw new ArgumentNullException(nameof(settlement));
            var payload = new BloodAltarRewardEffectPayload
            {
                CharacterId = characterId,
                AccountId = accountId,
                CompletedRounds = settlement.CompletedRounds,
                MaxRounds = settlement.MaxRounds,
                RequestedGold = settlement.TotalGold,
            };
            foreach (var reward in settlement.Rewards)
            {
                if (reward.IsGold
                    || reward.ItemId <= 0
                    || reward.StackCount <= 0)
                {
                    continue;
                }
                payload.Items.Add(new BloodAltarRewardEffectItem
                {
                    ItemId = reward.ItemId,
                    StackCount = reward.StackCount,
                });
            }
            return payload;
        }

        private static void ValidateBloodAltarRewardPayload(
            BloodAltarRewardEffectPayload payload,
            DungeonPersistentEffectRecord record)
        {
            if (payload == null
                || record == null
                || payload.CharacterId != record.CharacterId
                || payload.AccountId != record.AccountId
                || payload.CharacterId <= 0
                || payload.AccountId < 0
                || payload.CompletedRounds < 0
                || payload.MaxRounds < 0
                || payload.CompletedRounds > payload.MaxRounds
                || payload.RequestedGold < 0
                || payload.Items == null)
            {
                throw new PermanentPersistentEffectException(
                    "Blood altar reward payload is invalid.");
            }
            foreach (var item in payload.Items)
            {
                if (item == null
                    || item.ItemId <= 0
                    || item.StackCount <= 0)
                {
                    throw new PermanentPersistentEffectException(
                        "Blood altar reward item payload is invalid.");
                }
            }
        }

        private static bool TryBuildBloodAltarRewardPlan(
            InventoryService inventory,
            BloodAltarRewardEffectPayload payload,
            out InventoryRewardGrantBatchPlan plan,
            out List<InventoryRewardGrantRequest> overflowRewards,
            out int grantedGold,
            out string error)
        {
            plan = null;
            overflowRewards = new List<InventoryRewardGrantRequest>();
            grantedGold = 0;
            error = null;
            if (inventory == null || payload == null)
            {
                error = "inventory or payload is missing";
                return false;
            }

            var goldRequests = new List<InventoryRewardGrantRequest>();
            if (payload.RequestedGold > 0)
            {
                var currentGold = inventory.CountMainItem(
                    InventoryService.MainVirtualCurrencySlotStart);
                var carryLimit = Math.Max(
                    0,
                    InventoryGoldCarryLimitLoader.Load(inventory));
                grantedGold = (int)Math.Min(
                    payload.RequestedGold,
                    Math.Max(0L, (long)carryLimit - currentGold));
                if (grantedGold > 0)
                {
                    goldRequests.Add(InventoryRewardGrantRequest.Create(
                        InventoryService.MainVirtualCurrencySlotStart,
                        grantedGold,
                        ItemCreateReason.DungeonDrop));
                }
            }

            var itemRequests = new List<InventoryRewardGrantRequest>(
                payload.Items.Count);
            foreach (var item in payload.Items)
            {
                itemRequests.Add(InventoryRewardGrantRequest.Create(
                    item.ItemId,
                    item.StackCount,
                    ItemCreateReason.DungeonDrop));
            }

            var combined = new List<InventoryRewardGrantRequest>(
                goldRequests.Count + itemRequests.Count);
            combined.AddRange(goldRequests);
            combined.AddRange(itemRequests);
            if (InventoryRewardGrantService.TryPlanBatch(
                    inventory,
                    combined,
                    out plan)
                && IsSupportedBloodAltarInventoryPlan(plan, out error))
            {
                return true;
            }

            if (!InventoryRewardGrantService.TryPlanBatch(
                    inventory,
                    goldRequests,
                    out plan)
                || !IsSupportedBloodAltarInventoryPlan(plan, out error))
            {
                error = "gold-only fallback failed: " +
                    (error ?? plan?.Error.ToString() ?? "unknown");
                return false;
            }
            overflowRewards.AddRange(itemRequests);
            return true;
        }

        private static bool IsSupportedBloodAltarInventoryPlan(
            InventoryRewardGrantBatchPlan plan,
            out string error)
        {
            error = null;
            if (plan == null || !plan.Success)
            {
                error = plan?.Error.ToString() ?? "unknown";
                return false;
            }
            foreach (var entry in plan.Entries)
            {
                if ((entry.Kind != InventoryRewardGrantKind.InventoryItem
                        && entry.Kind
                            != InventoryRewardGrantKind.MainVirtualCount)
                    || entry.ListType != InventoryListType.Main)
                {
                    error = $"unsupported reward kind {entry.Kind}/" +
                        entry.ListType;
                    return false;
                }
            }
            return true;
        }

        private static BloodAltarRewardEffectResult
            BuildBloodAltarRewardEffectResult(
                int requestedGold,
                int grantedGold,
                int finalGold,
                int mailedRewardCount,
                InventoryMutationSet changes)
        {
            var result = new BloodAltarRewardEffectResult
            {
                RequestedGold = Math.Max(0, requestedGold),
                GrantedGold = Math.Max(0, grantedGold),
                FinalGold = Math.Max(0, finalGold),
                MailedRewardCount = Math.Max(0, mailedRewardCount),
            };
            if (changes == null)
                return result;
            foreach (var change in changes.Slots)
            {
                result.Changes.Add(new BloodAltarRewardEffectMutation
                {
                    ListType = (int)change.ListType,
                    Slot = change.SlotIndex,
                });
            }
            return result;
        }

        private static BloodAltarRewardCommitResult
            ToBloodAltarRewardCommitResult(
                BloodAltarRewardEffectResult persisted)
        {
            if (persisted?.Changes == null
                || persisted.RequestedGold < 0
                || persisted.GrantedGold < 0
                || persisted.FinalGold < 0
                || persisted.MailedRewardCount < 0)
            {
                return null;
            }
            var changes = new List<InventorySlotMutation>(
                persisted.Changes.Count);
            foreach (var mutation in persisted.Changes)
            {
                if (mutation == null
                    || mutation.ListType != (int)InventoryListType.Main
                    || mutation.Slot
                        < InventoryService.MainVirtualCurrencySlotStart
                    || mutation.Slot > InventoryService.MainSlotEnd)
                {
                    return null;
                }
                AddCardRewardChange(
                    changes,
                    InventoryListType.Main,
                    mutation.Slot);
            }
            return new BloodAltarRewardCommitResult(
                persisted.RequestedGold,
                persisted.GrantedGold,
                persisted.FinalGold,
                changes,
                persisted.MailedRewardCount);
        }

        private static bool TryReadBloodAltarRewardResult(
            DungeonPersistentEffectRecord record,
            out BloodAltarRewardCommitResult result,
            out string error)
        {
            result = null;
            error = null;
            if (!TryDeserializeResult(
                    record,
                    out BloodAltarRewardEffectResult persisted,
                    out error))
            {
                return false;
            }
            result = ToBloodAltarRewardCommitResult(persisted);
            if (result != null)
                return true;
            error = "Committed blood altar reward effect result is invalid.";
            return false;
        }

        private bool TryReadCommittedBloodAltarRewardAfterError(
            DungeonEffectId effectId,
            out BloodAltarRewardCommitResult result)
        {
            result = null;
            try
            {
                var record = _outbox.Get(effectId);
                return record?.State == DungeonPersistentEffectState.Committed
                    && TryReadBloodAltarRewardResult(
                        record,
                        out result,
                        out _);
            }
            catch
            {
                return false;
            }
        }

        private void RecoverBloodAltarInventoryAfterFailure(
            InventoryLease lease,
            InventoryService inventory,
            DungeonItemGrantBatchPlan plan,
            DungeonItemGrantMutationSnapshot rollback,
            bool inventoryMutated)
        {
            if (!inventoryMutated || inventory == null)
                return;
            if (lease == null)
            {
                rollback?.Restore(inventory, plan);
                inventory.ClearDirtyState();
                return;
            }
            if (!InventoryContext.IsCurrentLease(
                    lease,
                    lease.SessionId,
                    lease.CharacterId))
            {
                rollback?.Restore(inventory, plan);
                inventory.ClearDirtyState();
                return;
            }

            try
            {
                InventoryRollbackRecoveryService.ReloadOnlineInventory(
                    _connectionString,
                    lease);
                if (ReferenceEquals(lease.Inventory, inventory))
                {
                    throw new InvalidOperationException(
                        "current inventory lease was not replaced");
                }
            }
            catch (Exception ex)
            {
                rollback?.Restore(inventory, plan);
                inventory.ClearDirtyState();
                FileLogger.Log(
                    $"[BloodAltar] inventory reload failed after rollback: " +
                    $"cid={lease.CharacterId} error={ex.Message}");
            }
        }

        private IInventoryOverflowRewardSink GetOverflowRewardSink()
        {
            lock (_dependencySync)
                return _overflowRewardSink;
        }

        private static string GetCardRewardEffectKind(CardRewardSide side)
            => side switch
            {
                CardRewardSide.Free =>
                    DungeonPersistentEffectKinds.CardRewardFreeCommit,
                CardRewardSide.Paid =>
                    DungeonPersistentEffectKinds.CardRewardPaidCommit,
                _ => throw new ArgumentOutOfRangeException(nameof(side)),
            };

        private static CardRewardEffectPayload BuildCardRewardPayload(
            int characterId,
            int accountId,
            CardRewardSide side,
            int paidGoldCost,
            IReadOnlyList<ClearRewardGenerator.CardReward> cards)
        {
            if (cards == null)
                throw new ArgumentNullException(nameof(cards));
            if (paidGoldCost < 0)
                throw new ArgumentOutOfRangeException(nameof(paidGoldCost));

            var payload = new CardRewardEffectPayload
            {
                CharacterId = characterId,
                AccountId = accountId,
                Side = (int)side,
                PaidGoldCost = side == CardRewardSide.Paid
                    ? paidGoldCost
                    : 0,
            };
            if (side == CardRewardSide.Free)
            {
                if (cards.Count > 0
                    && cards[0].IsGold
                    && cards[0].GoldAmount > 0)
                {
                    payload.RequestedGold = cards[0].GoldAmount;
                }
                CopyCardRewardItem(cards, index: 1, payload);
            }
            else if (side == CardRewardSide.Paid)
            {
                CopyCardRewardItem(cards, index: 5, payload);
                if (payload.ItemId <= 0 || payload.StackCount <= 0)
                {
                    throw new InvalidOperationException(
                        "Paid card reward payload has no valid item.");
                }
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(side));
            }
            return payload;
        }

        private static void CopyCardRewardItem(
            IReadOnlyList<ClearRewardGenerator.CardReward> cards,
            int index,
            CardRewardEffectPayload payload)
        {
            if (cards.Count <= index
                || cards[index].IsGold
                || cards[index].ItemId <= 0
                || cards[index].StackCount <= 0)
            {
                return;
            }
            payload.ItemId = cards[index].ItemId;
            payload.StackCount = cards[index].StackCount;
        }

        private static void ValidateCardRewardPayload(
            CardRewardEffectPayload payload,
            DungeonPersistentEffectRecord record)
        {
            var free = payload?.Side == (int)CardRewardSide.Free;
            var paid = payload?.Side == (int)CardRewardSide.Paid;
            var itemEmpty = payload != null
                && payload.ItemId == 0
                && payload.StackCount == 0;
            var itemValid = payload != null
                && payload.ItemId > 0
                && payload.StackCount > 0;
            if (payload == null
                || record == null
                || payload.CharacterId != record.CharacterId
                || payload.AccountId != record.AccountId
                || payload.CharacterId <= 0
                || payload.AccountId < 0
                || (!free && !paid)
                || payload.PaidGoldCost < 0
                || payload.RequestedGold < 0
                || (!itemEmpty && !itemValid)
                || (free
                    && (!string.Equals(
                            record.EffectId.EffectKind,
                            DungeonPersistentEffectKinds.CardRewardFreeCommit,
                            StringComparison.Ordinal)
                        || payload.PaidGoldCost != 0))
                || (paid
                    && (!string.Equals(
                            record.EffectId.EffectKind,
                            DungeonPersistentEffectKinds.CardRewardPaidCommit,
                            StringComparison.Ordinal)
                        || payload.RequestedGold != 0
                        || !itemValid)))
            {
                throw new PermanentPersistentEffectException(
                    "Card reward payload is invalid.");
            }
        }

        private static bool TryBuildCardRewardPlan(
            InventoryService inventory,
            CardRewardEffectPayload payload,
            out InventoryRewardGrantBatchPlan plan,
            out string error)
        {
            plan = null;
            error = null;
            if (inventory == null || payload == null)
            {
                error = "inventory or payload is missing";
                return false;
            }
            if (payload.PaidGoldCost > inventory.CountMainItem(
                    InventoryService.MainVirtualCurrencySlotStart))
            {
                error = "paid-card gold is insufficient";
                return false;
            }

            var requests = new List<InventoryRewardGrantRequest>();
            if (payload.RequestedGold > 0)
            {
                var currentGold = inventory.CountMainItem(
                    InventoryService.MainVirtualCurrencySlotStart);
                var carryLimit = Math.Max(
                    0,
                    InventoryGoldCarryLimitLoader.Load(inventory));
                var grantedGold = (int)Math.Min(
                    payload.RequestedGold,
                    Math.Max(0L, (long)carryLimit - currentGold));
                if (grantedGold > 0)
                {
                    requests.Add(InventoryRewardGrantRequest.Create(
                        InventoryService.MainVirtualCurrencySlotStart,
                        grantedGold,
                        ItemCreateReason.DungeonDrop));
                }
            }
            if (payload.ItemId > 0 && payload.StackCount > 0)
            {
                requests.Add(InventoryRewardGrantRequest.Create(
                    payload.ItemId,
                    payload.StackCount,
                    ItemCreateReason.DungeonDrop));
            }

            if (!InventoryRewardGrantService.TryPlanBatch(
                    inventory,
                    requests,
                    out plan)
                || plan == null
                || !plan.Success)
            {
                error = plan?.Error.ToString() ?? "unknown";
                return false;
            }
            foreach (var entry in plan.Entries)
            {
                if ((entry.Kind != InventoryRewardGrantKind.InventoryItem
                        && entry.Kind
                            != InventoryRewardGrantKind.MainVirtualCount)
                    || entry.ListType != InventoryListType.Main)
                {
                    error = $"unsupported reward kind {entry.Kind}/" +
                        entry.ListType;
                    plan = null;
                    return false;
                }
            }
            return true;
        }

        private static CardRewardEffectResult BuildCardRewardEffectResult(
            IReadOnlyList<InventorySlotMutation> changes)
        {
            var result = new CardRewardEffectResult();
            if (changes == null)
                return result;
            foreach (var change in changes)
            {
                result.Changes.Add(new CardRewardEffectMutation
                {
                    ListType = (int)change.ListType,
                    Slot = change.SlotIndex,
                });
            }
            return result;
        }

        private static CardRewardPersistentCommitResult
            ToCardRewardCommitResult(CardRewardEffectResult persisted)
        {
            if (persisted?.Changes == null)
                return null;
            var changes = new List<InventorySlotMutation>(
                persisted.Changes.Count);
            foreach (var mutation in persisted.Changes)
            {
                if (mutation == null
                    || mutation.ListType != (int)InventoryListType.Main
                    || mutation.Slot
                        < InventoryService.MainVirtualCurrencySlotStart
                    || mutation.Slot > InventoryService.MainSlotEnd)
                {
                    return null;
                }
                AddCardRewardChange(
                    changes,
                    InventoryListType.Main,
                    mutation.Slot);
            }
            return new CardRewardPersistentCommitResult
            {
                Changes = changes,
            };
        }

        private static bool TryReadCardRewardResult(
            DungeonPersistentEffectRecord record,
            out CardRewardPersistentCommitResult result,
            out string error)
        {
            result = null;
            error = null;
            if (!TryDeserializeResult(
                    record,
                    out CardRewardEffectResult persisted,
                    out error))
            {
                return false;
            }
            result = ToCardRewardCommitResult(persisted);
            if (result != null)
                return true;
            error = "Committed card reward effect result is invalid.";
            return false;
        }

        private bool TryReadCommittedCardRewardAfterError(
            DungeonEffectId effectId,
            out CardRewardPersistentCommitResult result)
        {
            result = null;
            try
            {
                var record = _outbox.Get(effectId);
                return record?.State == DungeonPersistentEffectState.Committed
                    && TryReadCardRewardResult(record, out result, out _);
            }
            catch
            {
                return false;
            }
        }

        private void RecoverCardRewardInventoryAfterFailure(
            InventoryLease lease,
            InventoryService inventory,
            InventoryRewardGrantBatchPlan plan,
            CardRewardInventoryMutationSnapshot rollback,
            bool inventoryMutated)
        {
            if (!inventoryMutated || inventory == null)
                return;
            if (lease == null)
            {
                rollback?.Restore(inventory, plan);
                inventory.ClearDirtyState();
                return;
            }
            if (!InventoryContext.IsCurrentLease(
                    lease,
                    lease.SessionId,
                    lease.CharacterId))
            {
                rollback?.Restore(inventory, plan);
                inventory.ClearDirtyState();
                return;
            }

            try
            {
                InventoryRollbackRecoveryService.ReloadOnlineInventory(
                    _connectionString,
                    lease);
                if (ReferenceEquals(lease.Inventory, inventory))
                {
                    throw new InvalidOperationException(
                        "current inventory lease was not replaced");
                }
            }
            catch (Exception ex)
            {
                rollback?.Restore(inventory, plan);
                inventory.ClearDirtyState();
                FileLogger.Log(
                    $"[CardReward] inventory reload failed after rollback: " +
                    $"cid={lease.CharacterId} error={ex.Message}");
            }
        }

        private static void AddCardRewardChanges(
            ICollection<InventorySlotMutation> target,
            InventoryMutationSet changes)
        {
            if (changes == null)
                return;
            foreach (var change in changes.Slots)
                AddCardRewardChange(target, change.ListType, change.SlotIndex);
        }

        private static void AddCardRewardChange(
            ICollection<InventorySlotMutation> target,
            InventoryListType listType,
            short slot)
        {
            foreach (var current in target)
            {
                if (current.ListType == listType
                    && current.SlotIndex == slot)
                {
                    return;
                }
            }
            target.Add(new InventorySlotMutation(listType, slot));
        }

        private static void ValidateTowerOfDespairPayload(
            TowerOfDespairSettlementEffectPayload payload,
            DungeonPersistentEffectRecord record)
        {
            if (payload == null
                || record == null
                || payload.CharacterId != record.CharacterId
                || payload.AccountId != record.AccountId
                || payload.CharacterId <= 0
                || payload.AccountId < 0
                || !DungeonData.TryGetTowerOfDespairFloor(
                    payload.ClearedDungeonId,
                    out var clearedFloor)
                || clearedFloor != payload.ClearedFloor
                || payload.Rewards == null)
            {
                throw new PermanentPersistentEffectException(
                    "Tower of Despair settlement payload is invalid.");
            }

            foreach (var reward in payload.Rewards)
            {
                if (reward == null
                    || reward.ItemId <= 0
                    || reward.StackCount <= 0)
                {
                    throw new PermanentPersistentEffectException(
                        "Tower of Despair reward payload is invalid.");
                }
            }
        }

        private static bool TryBuildTowerOfDespairRewardPlan(
            InventoryService inventory,
            IReadOnlyList<TowerOfDespairSettlementEffectReward> rewards,
            out InventoryRewardGrantBatchPlan plan,
            out string error)
        {
            plan = null;
            error = null;
            if (inventory == null || rewards == null)
            {
                error = "inventory or rewards are missing";
                return false;
            }

            var requests = new List<InventoryRewardGrantRequest>(rewards.Count);
            foreach (var reward in rewards)
            {
                requests.Add(InventoryRewardGrantRequest.Create(
                    reward.ItemId,
                    reward.StackCount,
                    ItemCreateReason.DungeonDrop));
            }
            if (!InventoryRewardGrantService.TryPlanBatch(
                    inventory,
                    requests,
                    out plan)
                || plan == null
                || !plan.Success)
            {
                error = plan?.Error.ToString() ?? "unknown";
                return false;
            }
            foreach (var entry in plan.Entries)
            {
                if (entry.Kind != InventoryRewardGrantKind.InventoryItem
                    || entry.ListType != InventoryListType.Main)
                {
                    error = $"unsupported reward kind {entry.Kind}/" +
                        entry.ListType;
                    plan = null;
                    return false;
                }
            }
            return true;
        }

        private static TowerOfDespairSettlementEffectResult
            BuildTowerOfDespairEffectResult(
                int nextFloor,
                IReadOnlyList<InventoryRewardGrantResult> grants)
        {
            var result = new TowerOfDespairSettlementEffectResult
            {
                NextFloor = nextFloor,
            };
            if (grants == null)
                return result;

            foreach (var grant in grants)
            {
                if (grant == null
                    || !grant.Success
                    || grant.Kind != InventoryRewardGrantKind.InventoryItem
                    || grant.ListType != InventoryListType.Main
                    || grant.SlotIndex < InventoryService.MainSlotStart
                    || grant.ItemTemplateId <= 0
                    || grant.GrantedCount <= 0)
                {
                    throw new InvalidOperationException(
                        "Tower of Despair reward result is invalid.");
                }

                result.Rewards.Add(
                    new TowerOfDespairSettlementEffectGrantedReward
                    {
                        ItemId = grant.ItemTemplateId,
                        StackCount = grant.GrantedCount,
                        ListType = (int)grant.ListType,
                        Slot = grant.SlotIndex,
                    });
            }
            return result;
        }

        private static TowerOfDespairSettlementCommitResult
            ToTowerOfDespairCommitResult(
                TowerOfDespairSettlementEffectResult persisted)
        {
            if (persisted == null || persisted.Rewards == null)
                return null;

            var granted = new List<TowerOfDespairGrantedReward>(
                persisted.Rewards.Count);
            foreach (var reward in persisted.Rewards)
            {
                if (reward == null
                    || reward.ItemId <= 0
                    || reward.StackCount <= 0
                    || reward.ListType != (int)InventoryListType.Main
                    || reward.Slot < InventoryService.MainSlotStart)
                {
                    return null;
                }

                granted.Add(new TowerOfDespairGrantedReward(
                    new ClearRewardGenerator.CardReward
                    {
                        ItemId = reward.ItemId,
                        StackCount = reward.StackCount,
                    },
                    InventoryListType.Main,
                    reward.Slot));
            }
            return new TowerOfDespairSettlementCommitResult
            {
                NextFloor = persisted.NextFloor,
                GrantedRewards = granted,
            };
        }

        private static bool TryReadTowerOfDespairResult(
            DungeonPersistentEffectRecord record,
            out TowerOfDespairSettlementCommitResult result,
            out string error)
        {
            result = null;
            error = null;
            if (!TryDeserializeResult(
                    record,
                    out TowerOfDespairSettlementEffectResult persisted,
                    out error))
            {
                return false;
            }

            result = ToTowerOfDespairCommitResult(persisted);
            if (result != null)
                return true;

            error = "Committed Tower of Despair effect result is invalid.";
            return false;
        }

        private bool TryReadCommittedTowerOfDespairAfterError(
            DungeonEffectId effectId,
            out TowerOfDespairSettlementCommitResult result)
        {
            result = null;
            try
            {
                var record = _outbox.Get(effectId);
                return record?.State == DungeonPersistentEffectState.Committed
                    && TryReadTowerOfDespairResult(
                        record,
                        out result,
                        out _);
            }
            catch
            {
                return false;
            }
        }

        private void RecoverTowerOfDespairInventoryAfterFailure(
            InventoryLease lease,
            InventoryService inventory,
            DungeonItemGrantBatchPlan snapshotPlan,
            DungeonItemGrantMutationSnapshot rollback,
            bool inventoryMutated)
        {
            if (!inventoryMutated || inventory == null)
                return;
            if (lease == null)
            {
                rollback?.Restore(inventory, snapshotPlan);
                inventory.ClearDirtyState();
                return;
            }

            if (!InventoryContext.IsCurrentLease(
                    lease,
                    lease.SessionId,
                    lease.CharacterId))
            {
                rollback?.Restore(inventory, snapshotPlan);
                inventory.ClearDirtyState();
                return;
            }

            try
            {
                InventoryRollbackRecoveryService.ReloadOnlineInventory(
                    _connectionString,
                    lease);
                if (ReferenceEquals(lease.Inventory, inventory))
                {
                    throw new InvalidOperationException(
                        "current inventory lease was not replaced");
                }
            }
            catch (Exception ex)
            {
                rollback?.Restore(inventory, snapshotPlan);
                inventory.ClearDirtyState();
                FileLogger.Log(
                    $"[TowerOfDespair] inventory reload failed after " +
                    $"settlement rollback: cid={lease.CharacterId} " +
                    $"error={ex.Message}");
            }
        }

        private static T DeserializePayload<T>(
            DungeonPersistentEffectRecord record,
            string expectedKind)
        {
            if (record == null)
                throw new InvalidOperationException(
                    "Persistent dungeon effect record is missing.");
            if (!string.Equals(
                    record.EffectId.EffectKind,
                    expectedKind,
                    StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Persistent dungeon effect kind does not match its dispatcher.");
            if (record.PayloadVersion != PayloadVersion)
                throw new PermanentPersistentEffectException(
                    $"Unsupported {expectedKind} payload version " +
                    $"{record.PayloadVersion}.");
            try
            {
                return JsonSerializer.Deserialize<T>(
                           record.PayloadJson,
                           JsonOptions)
                       ?? throw new InvalidOperationException(
                           "Persistent dungeon effect payload is empty.");
            }
            catch (JsonException ex)
            {
                throw new PermanentPersistentEffectException(
                    "Persistent dungeon effect payload is invalid JSON.",
                    ex);
            }
        }

        private static bool TryDeserializeResult<T>(
            DungeonPersistentEffectRecord record,
            out T result,
            out string error)
        {
            result = default;
            error = null;
            if (record == null
                || record.State != DungeonPersistentEffectState.Committed
                || record.ResultVersion != ResultVersion
                || string.IsNullOrWhiteSpace(record.ResultJson))
            {
                error = "Committed persistent effect has no supported result.";
                return false;
            }
            try
            {
                result = JsonSerializer.Deserialize<T>(
                    record.ResultJson,
                    JsonOptions);
                if (result == null)
                {
                    error = "Committed persistent effect result is empty.";
                    return false;
                }
                return true;
            }
            catch (JsonException ex)
            {
                error = "Committed persistent effect result is invalid: " +
                    ex.Message;
                return false;
            }
        }

        private static DungeonPersistentEffectDefinition CreateDefinition<T>(
            DungeonEffectId effectId,
            int characterId,
            int accountId,
            T payload)
            => new DungeonPersistentEffectDefinition
            {
                EffectId = effectId,
                CharacterId = characterId,
                AccountId = Math.Max(0, accountId),
                PayloadVersion = PayloadVersion,
                PayloadJson = Serialize(payload),
            };

        private static string Serialize<T>(T value)
            => JsonSerializer.Serialize(value, JsonOptions);

        private void LoadCharacterProgress(
            int characterId,
            out byte level,
            out uint exp)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                LoadCharacterProgress(
                    connection,
                    transaction: null,
                    characterId,
                    out level,
                    out exp);
            }
        }

        private static void LoadCharacterProgress(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            out byte level,
            out uint exp)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT level, exp
FROM characters
WHERE character_id = @characterId;";
                command.Parameters.AddWithValue("@characterId", characterId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        throw new InvalidOperationException(
                            $"Character {characterId} does not exist.");
                    level = (byte)Math.Max(0, Math.Min(255, reader.GetInt32(0)));
                    exp = (uint)Math.Min(
                        uint.MaxValue,
                        Math.Max(0L, reader.GetInt64(1)));
                }
            }
        }

        private static SettlementExperienceEffectResult FromExperienceGrant(
            ExperienceGrantResult result)
            => new SettlementExperienceEffectResult
            {
                RawGain = result.RawGain,
                HonorExpGain = result.HonorExpGain,
                NormalExpGain = result.NormalExpGain,
                PreviousLevel = result.PreviousLevel,
                PreviousExp = result.PreviousExp,
                NewLevel = result.NewLevel,
                NewExp = result.NewExp,
                NormalizedMaxLevelExp = result.NormalizedMaxLevelExp,
                Persisted = result.Persisted,
                GrowthCapsuleExpGain = result.GrowthCapsuleExpGain,
                TotalHonorExp = result.TotalHonorExp,
                TotalGrowthCapsuleExp = result.TotalGrowthCapsuleExp,
            };

        private static ExperienceGrantResult ToExperienceGrant(
            SettlementExperienceEffectResult result)
            => new ExperienceGrantResult
            {
                RawGain = result.RawGain,
                HonorExpGain = result.HonorExpGain,
                NormalExpGain = result.NormalExpGain,
                PreviousLevel = result.PreviousLevel,
                PreviousExp = result.PreviousExp,
                NewLevel = result.NewLevel,
                NewExp = result.NewExp,
                NormalizedMaxLevelExp = result.NormalizedMaxLevelExp,
                Persisted = result.Persisted,
                GrowthCapsuleExpGain = result.GrowthCapsuleExpGain,
                TotalHonorExp = result.TotalHonorExp,
                TotalGrowthCapsuleExp = result.TotalGrowthCapsuleExp,
            };

        private static void ValidateEffectIdentity(
            DungeonEffectId effectId,
            string expectedKind,
            int characterId)
        {
            if (!string.Equals(
                    effectId.EffectKind,
                    expectedKind,
                    StringComparison.Ordinal)
                || effectId.Scope != DungeonEffectScope.Player
                || effectId.ScopeTarget <= 0
                || characterId <= 0)
            {
                throw new ArgumentException(
                    "Persistent dungeon effect identity is invalid.",
                    nameof(effectId));
            }
        }

        private static void LogRecoveryFailure(
            DungeonPersistentEffectRecord record,
            string error)
            => FileLogger.Log(
                $"[DungeonPersistentEffect] recovery failed: " +
                $"cid={record?.CharacterId ?? 0} " +
                $"kind={record?.EffectId.EffectKind ?? "unknown"} " +
                $"event={record?.EffectId.SourceEventId.ToString("N") ?? "none"} " +
                $"error={error ?? "unknown"}");

        private sealed class PermanentPersistentEffectException : Exception
        {
            internal PermanentPersistentEffectException(string message)
                : base(message)
            {
            }

            internal PermanentPersistentEffectException(
                string message,
                Exception innerException)
                : base(message, innerException)
            {
            }
        }
    }
}
