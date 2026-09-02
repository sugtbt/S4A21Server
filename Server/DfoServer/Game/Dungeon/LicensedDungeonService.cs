using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Dungeon
{
    internal readonly struct LicensedDungeonPeriod
    {
        internal LicensedDungeonPeriod(
            int dayId,
            int monthId,
            byte dayIndex)
        {
            DayId = dayId;
            MonthId = monthId;
            DayIndex = dayIndex;
        }

        internal int DayId { get; }
        internal int MonthId { get; }
        internal byte DayIndex { get; }

        internal static LicensedDungeonPeriod FromUtc(DateTime utcNow)
        {
            var normalized = utcNow.Kind == DateTimeKind.Utc
                ? utcNow
                : utcNow.ToUniversalTime();
            // 北京时间06:00切日：UTC时间先加8小时，再回拨6小时。
            var gameDay = normalized.AddHours(2);
            return new LicensedDungeonPeriod(
                gameDay.Year * 10000 + gameDay.Month * 100 + gameDay.Day,
                gameDay.Year * 100 + gameDay.Month,
                (byte)gameDay.DayOfWeek);
        }
    }

    internal readonly struct LicensedDungeonStatus
    {
        internal LicensedDungeonStatus(
            int dayId,
            int dailyEntryCount,
            int monthId,
            int monthlyEntryCount,
            int monthlyGroupAppearCount)
        {
            DayId = dayId;
            DailyEntryCount = dailyEntryCount;
            MonthId = monthId;
            MonthlyEntryCount = monthlyEntryCount;
            MonthlyGroupAppearCount = monthlyGroupAppearCount;
        }

        internal int DayId { get; }
        internal int DailyEntryCount { get; }
        internal int MonthId { get; }
        internal int MonthlyEntryCount { get; }
        internal int MonthlyGroupAppearCount { get; }
        internal int RemainingDailyEntries =>
            !LicensedDungeonService.EnforceDailyEntryLimit
                ? LicensedDungeonCatalog.DailyEnterCount
                : Math.Max(
                    0,
                    LicensedDungeonCatalog.DailyEnterCount - DailyEntryCount);
    }

    internal sealed class LicensedDungeonEntryPlan
    {
        private readonly object _syncRoot = new object();
        private bool _committed;
        private LicensedDungeonStatus _committedStatus;

        internal LicensedDungeonEntryPlan(
            int characterId,
            LicensedDungeonDefinition definition,
            LicensedDungeonPeriod period,
            LicensedDungeonStatus expectedStatus,
            int mazeIndex,
            bool groupBossPresent,
            int groupAppearRate)
        {
            CharacterId = characterId;
            Definition = definition
                ?? throw new ArgumentNullException(nameof(definition));
            Period = period;
            ExpectedStatus = expectedStatus;
            MazeIndex = mazeIndex;
            GroupBossPresent = groupBossPresent;
            GroupAppearRate = groupAppearRate;
        }

        internal int CharacterId { get; }
        internal LicensedDungeonDefinition Definition { get; }
        internal LicensedDungeonPeriod Period { get; }
        internal LicensedDungeonStatus ExpectedStatus { get; }
        internal int MazeIndex { get; }
        internal bool GroupBossPresent { get; }
        internal int GroupAppearRate { get; }

        internal bool TryMarkCommitted(LicensedDungeonStatus status)
        {
            lock (_syncRoot)
            {
                if (_committed)
                    return false;
                _committed = true;
                _committedStatus = status;
                return true;
            }
        }

        internal bool TryCaptureCommittedStatus(
            out LicensedDungeonStatus status)
        {
            lock (_syncRoot)
            {
                status = _committedStatus;
                return _committed;
            }
        }

        internal void MarkRolledBack()
        {
            lock (_syncRoot)
            {
                _committed = false;
                _committedStatus = default;
            }
        }
    }

    internal sealed class LicensedDungeonService
    {
        // Temporary test mode: keep recording entries, but do not reject a
        // licensed-dungeon entry only because the configured daily cap was
        // reached. Restore this to true before production rollout.
        internal const bool EnforceDailyEntryLimit = false;

        private readonly LicensedDungeonStateRepository _repository;

        internal LicensedDungeonService(IGameDatabase database)
        {
            _repository = new LicensedDungeonStateRepository(
                database ?? throw new ArgumentNullException(nameof(database)));
        }

        internal bool IsLicensedDungeon(int dungeonId) =>
            LicensedDungeonCatalog.TryGetDefinition(dungeonId, out _);

        internal bool TryGetLicenseProjection(
            int characterId,
            out IReadOnlyList<LicensedDungeonPermissionRecord> records,
            out string failureReason)
        {
            records = Array.Empty<LicensedDungeonPermissionRecord>();
            failureReason = string.Empty;
            if (characterId <= 0)
            {
                failureReason = "character id is invalid";
                return false;
            }

            try
            {
                records = LicensedDungeonCatalog.GetLicenseRecords(
                    _repository.LoadLicenseLevels(characterId));
                return true;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                return false;
            }
        }

        internal bool TryGetLicenseProjection(
            int characterId,
            int groupId,
            out LicensedDungeonPermissionRecord record,
            out string failureReason)
        {
            record = default;
            failureReason = string.Empty;
            if (characterId <= 0 || groupId <= 0)
            {
                failureReason = "character or group id is invalid";
                return false;
            }

            try
            {
                var levels = _repository.LoadLicenseLevels(characterId);
                var level = levels.TryGetValue(groupId, out var unlocked)
                    ? unlocked
                    : LicensedDungeonCatalog.GetInitialLicenseLevel(groupId);
                if (!LicensedDungeonCatalog.TryCreatePermissionRecord(
                        groupId,
                        level,
                        out record))
                {
                    failureReason =
                        $"licensed group {groupId} has no projection for level {level}";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                return false;
            }
        }

        internal bool TryAdvanceLicenseOnClear(
            int characterId,
            int dungeonId,
            bool reviveUsed,
            out bool advanced,
            out int noReviveClearCount,
            out string failureReason)
        {
            advanced = false;
            noReviveClearCount = 0;
            failureReason = string.Empty;
            if (characterId <= 0)
            {
                failureReason = "character id is invalid";
                return false;
            }
            if (!LicensedDungeonCatalog.TryGetDefinition(
                    dungeonId,
                    out var definition))
            {
                return true;
            }

            try
            {
                return _repository.TryAdvanceLicense(
                    characterId,
                    definition,
                    reviveUsed,
                    out advanced,
                    out noReviveClearCount,
                    out failureReason);
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                return false;
            }
        }

        internal bool TryGetSelectionProjection(
            int characterId,
            DateTime utcNow,
            out byte dayIndex,
            out byte remainingEnterCount,
            out string failureReason)
        {
            var period = LicensedDungeonPeriod.FromUtc(utcNow);
            dayIndex = period.DayIndex;
            remainingEnterCount = 0;
            failureReason = string.Empty;
            if (characterId <= 0)
            {
                failureReason = "character id is invalid";
                return false;
            }

            try
            {
                var status = _repository.Load(characterId, period);
                remainingEnterCount = (byte)Math.Min(
                    byte.MaxValue,
                    status.RemainingDailyEntries);
                return true;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                return false;
            }
        }

        internal bool TryPrepareEntry(
            int characterId,
            int dungeonId,
            DateTime utcNow,
            Func<int, int> nextRandom,
            out LicensedDungeonEntryPlan plan,
            out string failureReason)
        {
            plan = null;
            failureReason = string.Empty;
            if (!LicensedDungeonCatalog.TryGetDefinition(
                    dungeonId,
                    out var definition))
            {
                failureReason = "dungeon is not licensed content";
                return false;
            }

            var period = LicensedDungeonPeriod.FromUtc(utcNow);
            if (!definition.IsOpenOn(period.DayIndex))
            {
                failureReason =
                    $"dungeon is closed on day index {period.DayIndex}";
                return false;
            }

            LicensedDungeonStatus status;
            try
            {
                status = _repository.Load(characterId, period);
            }
            catch (Exception ex)
            {
                failureReason = "period state load failed: " + ex.Message;
                return false;
            }

            IReadOnlyDictionary<int, int> licenseLevels;
            try
            {
                licenseLevels = _repository.LoadLicenseLevels(characterId);
            }
            catch (Exception ex)
            {
                failureReason = "license level load failed: " + ex.Message;
                return false;
            }

            var currentLicenseLevel = licenseLevels.TryGetValue(
                definition.GroupId,
                out var unlockedLevel)
                ? unlockedLevel
                : LicensedDungeonCatalog.GetInitialLicenseLevel(
                    definition.GroupId);
            if (definition.LicenseLevel > currentLicenseLevel)
            {
                failureReason =
                    $"licensed dungeon level {definition.LicenseLevel} " +
                    $"is not unlocked (current={currentLicenseLevel})";
                return false;
            }

            if (EnforceDailyEntryLimit
                && status.DailyEntryCount
                    >= LicensedDungeonCatalog.DailyEnterCount)
            {
                failureReason = "daily entry count is exhausted";
                return false;
            }
            if (!EnforceDailyEntryLimit
                && status.DailyEntryCount
                    >= LicensedDungeonCatalog.DailyEnterCount)
            {
                FileLogger.Log(
                    $"[LicensedDungeon] daily entry limit bypassed for test: " +
                    $"cid={characterId} dungeon={dungeonId} " +
                    $"count={status.DailyEntryCount}/" +
                    $"{LicensedDungeonCatalog.DailyEnterCount}");
            }

            nextRandom ??= ServerRandom.Next;
            var bossRule = definition.BossRule;
            var mazeIndex = bossRule?.OrdinaryMazeIndex ?? 0;
            var groupBossPresent = false;
            var groupAppearRate = 0;
            if (bossRule != null
                && status.MonthlyGroupAppearCount
                    < LicensedDungeonCatalog.GroupAppearCountPerMonth)
            {
                groupAppearRate = LicensedDungeonCatalog
                    .ResolveGroupAppearRate(status.MonthlyEntryCount + 1);
                if (groupAppearRate > 0
                    && nextRandom(10000) < groupAppearRate)
                {
                    groupBossPresent = true;
                    mazeIndex = bossRule.BossMazeIndices[
                        nextRandom(bossRule.BossMazeIndices.Count)];
                }
            }

            plan = new LicensedDungeonEntryPlan(
                characterId,
                definition,
                period,
                status,
                mazeIndex,
                groupBossPresent,
                groupAppearRate);
            return true;
        }

        internal bool TryCommitEntry(
            LicensedDungeonEntryPlan plan,
            out LicensedDungeonStatus committedStatus,
            out string failureReason)
        {
            committedStatus = default;
            failureReason = string.Empty;
            if (plan == null)
            {
                failureReason = "licensed dungeon entry plan is missing";
                return false;
            }
            if (plan.TryCaptureCommittedStatus(out committedStatus))
                return true;

            try
            {
                if (!_repository.TryCommit(
                        plan,
                        out committedStatus,
                        out failureReason))
                {
                    return false;
                }
                if (!plan.TryMarkCommitted(committedStatus))
                {
                    failureReason = "licensed dungeon plan commit raced";
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                return false;
            }
        }

        internal bool TryRollbackEntry(
            LicensedDungeonEntryPlan plan,
            out string failureReason)
        {
            failureReason = string.Empty;
            if (plan == null
                || !plan.TryCaptureCommittedStatus(out var committedStatus))
            {
                return true;
            }

            try
            {
                if (!_repository.TryRollback(
                        plan,
                        committedStatus,
                        out failureReason))
                {
                    return false;
                }
                plan.MarkRolledBack();
                return true;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                return false;
            }
        }
    }

    internal sealed class LicensedDungeonStateRepository
    {
        private readonly IGameDatabase _database;

        internal LicensedDungeonStateRepository(IGameDatabase database)
        {
            _database = database
                ?? throw new ArgumentNullException(nameof(database));
        }

        internal LicensedDungeonStatus Load(
            int characterId,
            LicensedDungeonPeriod period)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            Normalize(connection, transaction, characterId, period);
            var status = Read(connection, transaction, characterId);
            transaction.Commit();
            return status;
        }

        internal IReadOnlyDictionary<int, int> LoadLicenseLevels(
            int characterId)
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            NormalizeLicenseProgress(connection, transaction, characterId);
            var result = ReadLicenseLevels(
                connection,
                transaction,
                characterId);
            transaction.Commit();
            return result;
        }

        internal bool TryAdvanceLicense(
            int characterId,
            LicensedDungeonDefinition definition,
            bool reviveUsed,
            out bool advanced,
            out int noReviveClearCount,
            out string failureReason)
        {
            advanced = false;
            noReviveClearCount = 0;
            failureReason = string.Empty;
            if (definition == null)
            {
                failureReason = "licensed dungeon definition is missing";
                return false;
            }

            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            NormalizeLicenseProgress(connection, transaction, characterId);
            var currentLevel = ReadLicenseLevel(
                connection,
                transaction,
                characterId,
                definition.GroupId);
            var currentNoReviveClearCount = ReadNoReviveClearCount(
                connection,
                transaction,
                characterId,
                definition.GroupId);
            noReviveClearCount = currentNoReviveClearCount;
            if (currentLevel != definition.LicenseLevel
                || !LicensedDungeonCatalog.TryGetNextLicenseLevel(
                    definition.GroupId,
                    currentLevel,
                    out var nextLevel))
            {
                transaction.Commit();
                return true;
            }

            // The PVF has no named shot/no-revive field. The A21 licensed
            // dungeon rule is tier-wide: unlocking 2/3/4 stars requires
            // respectively 1/3/7 consecutive clears without a revive coin.
            var requiredNoReviveClears =
                RequiredNoReviveClears(currentLevel, nextLevel);
            if (requiredNoReviveClears <= 0)
            {
                transaction.Commit();
                return true;
            }

            var nextNoReviveClearCount = reviveUsed
                ? 0
                : Math.Min(
                    requiredNoReviveClears,
                    currentNoReviveClearCount + 1);
            noReviveClearCount = nextNoReviveClearCount;
            if (nextNoReviveClearCount < requiredNoReviveClears)
            {
                using var countCommand = connection.CreateCommand();
                countCommand.Transaction = transaction;
                countCommand.CommandText = @"
UPDATE character_license_dungeon_progress
SET no_revive_clear_count = @nextCount,
    updated_at = CURRENT_TIMESTAMP
WHERE character_id = @cid
  AND group_id = @groupId
  AND license_level = @currentLevel
  AND no_revive_clear_count = @currentCount;";
                countCommand.Parameters.AddWithValue(
                    "@nextCount",
                    nextNoReviveClearCount);
                countCommand.Parameters.AddWithValue("@cid", characterId);
                countCommand.Parameters.AddWithValue(
                    "@groupId",
                    definition.GroupId);
                countCommand.Parameters.AddWithValue(
                    "@currentLevel",
                    currentLevel);
                countCommand.Parameters.AddWithValue(
                    "@currentCount",
                    currentNoReviveClearCount);
                if (countCommand.ExecuteNonQuery() != 1)
                {
                    failureReason =
                        "licensed dungeon no-revive progress lost CAS";
                    transaction.Rollback();
                    return false;
                }

                transaction.Commit();
                return true;
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE character_license_dungeon_progress
SET license_level = @nextLevel,
    no_revive_clear_count = 0,
    updated_at = CURRENT_TIMESTAMP
WHERE character_id = @cid
  AND group_id = @groupId
  AND license_level = @currentLevel
  AND no_revive_clear_count = @currentCount;";
                command.Parameters.AddWithValue("@nextLevel", nextLevel);
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@groupId", definition.GroupId);
                command.Parameters.AddWithValue("@currentLevel", currentLevel);
                command.Parameters.AddWithValue(
                    "@currentCount",
                    currentNoReviveClearCount);
                if (command.ExecuteNonQuery() != 1)
                {
                    failureReason = "licensed dungeon level advance lost CAS";
                    transaction.Rollback();
                    return false;
                }
            }

            transaction.Commit();
            advanced = true;
            noReviveClearCount = 0;
            return true;
        }

        private static int RequiredNoReviveClears(
            int currentLevel,
            int nextLevel)
        {
            if (nextLevel <= currentLevel)
                return 0;

            switch (nextLevel)
            {
                case 2: return 1;
                case 3: return 3;
                case 4: return 7;
                default: return 0;
            }
        }

        internal bool TryCommit(
            LicensedDungeonEntryPlan plan,
            out LicensedDungeonStatus committedStatus,
            out string failureReason)
        {
            committedStatus = default;
            failureReason = string.Empty;
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            Normalize(connection, transaction, plan.CharacterId, plan.Period);
            var current = Read(connection, transaction, plan.CharacterId);
            if (!Matches(current, plan.ExpectedStatus))
            {
                failureReason = "licensed dungeon period state changed";
                transaction.Rollback();
                return false;
            }
            if (LicensedDungeonService.EnforceDailyEntryLimit
                && current.DailyEntryCount
                    >= LicensedDungeonCatalog.DailyEnterCount)
            {
                failureReason = "daily entry count is exhausted";
                transaction.Rollback();
                return false;
            }
            if (!LicensedDungeonService.EnforceDailyEntryLimit
                && current.DailyEntryCount
                    >= LicensedDungeonCatalog.DailyEnterCount)
            {
                FileLogger.Log(
                    $"[LicensedDungeon] daily entry limit bypassed at commit " +
                    $"for test: cid={plan.CharacterId} " +
                    $"dungeon={plan.Definition.DungeonId} " +
                    $"count={current.DailyEntryCount}/" +
                    $"{LicensedDungeonCatalog.DailyEnterCount}");
            }
            if (plan.GroupBossPresent
                && current.MonthlyGroupAppearCount
                    >= LicensedDungeonCatalog.GroupAppearCountPerMonth)
            {
                failureReason = "monthly group boss count is exhausted";
                transaction.Rollback();
                return false;
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE character_license_dungeon_period_state
SET daily_entry_count = daily_entry_count + 1,
    monthly_entry_count = monthly_entry_count + 1,
    monthly_groop_appear_count = monthly_groop_appear_count + @groupDelta,
    updated_at = CURRENT_TIMESTAMP
WHERE character_id = @cid
  AND day_id = @dayId
  AND daily_entry_count = @dailyCount
  AND month_id = @monthId
  AND monthly_entry_count = @monthlyCount
  AND monthly_groop_appear_count = @groupCount;";
                command.Parameters.AddWithValue("@cid", plan.CharacterId);
                command.Parameters.AddWithValue("@dayId", current.DayId);
                command.Parameters.AddWithValue(
                    "@dailyCount",
                    current.DailyEntryCount);
                command.Parameters.AddWithValue("@monthId", current.MonthId);
                command.Parameters.AddWithValue(
                    "@monthlyCount",
                    current.MonthlyEntryCount);
                command.Parameters.AddWithValue(
                    "@groupCount",
                    current.MonthlyGroupAppearCount);
                command.Parameters.AddWithValue(
                    "@groupDelta",
                    plan.GroupBossPresent ? 1 : 0);
                if (command.ExecuteNonQuery() != 1)
                {
                    failureReason = "licensed dungeon period commit lost CAS";
                    transaction.Rollback();
                    return false;
                }
            }

            committedStatus = new LicensedDungeonStatus(
                current.DayId,
                current.DailyEntryCount + 1,
                current.MonthId,
                current.MonthlyEntryCount + 1,
                current.MonthlyGroupAppearCount
                    + (plan.GroupBossPresent ? 1 : 0));
            transaction.Commit();
            return true;
        }

        internal bool TryRollback(
            LicensedDungeonEntryPlan plan,
            LicensedDungeonStatus committedStatus,
            out string failureReason)
        {
            failureReason = string.Empty;
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            Normalize(connection, transaction, plan.CharacterId, plan.Period);
            var current = Read(connection, transaction, plan.CharacterId);
            if (!Matches(current, committedStatus))
            {
                failureReason =
                    "licensed dungeon rollback rejected after state advanced";
                transaction.Rollback();
                return false;
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE character_license_dungeon_period_state
SET daily_entry_count = @dailyCount,
    monthly_entry_count = @monthlyCount,
    monthly_groop_appear_count = @groupCount,
    updated_at = CURRENT_TIMESTAMP
WHERE character_id = @cid
  AND day_id = @dayId
  AND daily_entry_count = @committedDailyCount
  AND month_id = @monthId
  AND monthly_entry_count = @committedMonthlyCount
  AND monthly_groop_appear_count = @committedGroupCount;";
                command.Parameters.AddWithValue("@cid", plan.CharacterId);
                command.Parameters.AddWithValue(
                    "@dayId",
                    plan.ExpectedStatus.DayId);
                command.Parameters.AddWithValue(
                    "@dailyCount",
                    plan.ExpectedStatus.DailyEntryCount);
                command.Parameters.AddWithValue(
                    "@monthlyCount",
                    plan.ExpectedStatus.MonthlyEntryCount);
                command.Parameters.AddWithValue(
                    "@groupCount",
                    plan.ExpectedStatus.MonthlyGroupAppearCount);
                command.Parameters.AddWithValue(
                    "@committedDailyCount",
                    committedStatus.DailyEntryCount);
                command.Parameters.AddWithValue(
                    "@monthId",
                    plan.ExpectedStatus.MonthId);
                command.Parameters.AddWithValue(
                    "@committedMonthlyCount",
                    committedStatus.MonthlyEntryCount);
                command.Parameters.AddWithValue(
                    "@committedGroupCount",
                    committedStatus.MonthlyGroupAppearCount);
                if (command.ExecuteNonQuery() != 1)
                {
                    failureReason = "licensed dungeon rollback lost CAS";
                    transaction.Rollback();
                    return false;
                }
            }

            transaction.Commit();
            return true;
        }

        private static void Normalize(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            LicensedDungeonPeriod period)
        {
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
INSERT OR IGNORE INTO character_license_dungeon_period_state (
    character_id, day_id, daily_entry_count, month_id,
    monthly_entry_count, monthly_groop_appear_count
) VALUES (
    @cid, @dayId, 0, @monthId, 0, 0
);
UPDATE character_license_dungeon_period_state
SET daily_entry_count = CASE WHEN day_id <> @dayId THEN 0 ELSE daily_entry_count END,
    monthly_entry_count = CASE WHEN month_id <> @monthId THEN 0 ELSE monthly_entry_count END,
    monthly_groop_appear_count = CASE WHEN month_id <> @monthId THEN 0 ELSE monthly_groop_appear_count END,
    day_id = @dayId,
    month_id = @monthId,
    updated_at = CURRENT_TIMESTAMP
WHERE character_id = @cid
  AND (day_id <> @dayId OR month_id <> @monthId);";
            command.Parameters.AddWithValue("@cid", characterId);
            command.Parameters.AddWithValue("@dayId", period.DayId);
            command.Parameters.AddWithValue("@monthId", period.MonthId);
            command.ExecuteNonQuery();
        }

        private static void NormalizeLicenseProgress(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));

            foreach (var group in LicensedDungeonCatalog.Definitions
                         .GroupBy(definition => definition.GroupId))
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR IGNORE INTO character_license_dungeon_progress (
    character_id, group_id, license_level, no_revive_clear_count)
VALUES (@cid, @groupId, @licenseLevel, 0);";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@groupId", group.Key);
                command.Parameters.AddWithValue(
                    "@licenseLevel",
                    group.Min(definition => definition.LicenseLevel));
                command.ExecuteNonQuery();
            }
        }

        private static Dictionary<int, int> ReadLicenseLevels(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            var result = new Dictionary<int, int>();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
SELECT group_id, license_level
FROM character_license_dungeon_progress
WHERE character_id = @cid;";
            command.Parameters.AddWithValue("@cid", characterId);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var groupId = reader.GetInt32(0);
                var level = reader.GetInt32(1);
                if (groupId > 0 && level > 0)
                    result[groupId] = level;
            }
            return result;
        }

        private static int ReadLicenseLevel(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int groupId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
SELECT license_level
FROM character_license_dungeon_progress
WHERE character_id = @cid AND group_id = @groupId;";
            command.Parameters.AddWithValue("@cid", characterId);
            command.Parameters.AddWithValue("@groupId", groupId);
            var value = command.ExecuteScalar();
            return value == null || value == DBNull.Value
                ? LicensedDungeonCatalog.GetInitialLicenseLevel(groupId)
                : Convert.ToInt32(value);
        }

        private static int ReadNoReviveClearCount(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int groupId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
SELECT no_revive_clear_count
FROM character_license_dungeon_progress
WHERE character_id = @cid AND group_id = @groupId;";
            command.Parameters.AddWithValue("@cid", characterId);
            command.Parameters.AddWithValue("@groupId", groupId);
            var value = command.ExecuteScalar();
            return value == null || value == DBNull.Value
                ? 0
                : Math.Max(0, Convert.ToInt32(value));
        }

        private static LicensedDungeonStatus Read(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = @"
SELECT day_id, daily_entry_count, month_id, monthly_entry_count,
       monthly_groop_appear_count
FROM character_license_dungeon_period_state
WHERE character_id = @cid;";
            command.Parameters.AddWithValue("@cid", characterId);
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidOperationException(
                    "licensed dungeon period state is missing after normalize");
            }
            return new LicensedDungeonStatus(
                reader.GetInt32(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4));
        }

        private static bool Matches(
            LicensedDungeonStatus left,
            LicensedDungeonStatus right)
            => left.DayId == right.DayId
               && left.DailyEntryCount == right.DailyEntryCount
               && left.MonthId == right.MonthId
               && left.MonthlyEntryCount == right.MonthlyEntryCount
               && left.MonthlyGroupAppearCount
                    == right.MonthlyGroupAppearCount;
    }
}
