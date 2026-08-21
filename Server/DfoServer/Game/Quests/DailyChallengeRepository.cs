using System;
using System.Collections.Generic;
using DfoServer.Game.DailyReset;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Quests
{
    internal sealed class DailyChallengeRepository
    {
        private const string GeneratedTodayCounterKey = "daily_challenge_generated";

        private readonly string _connectionString;
        private readonly DailyResetService _dailyReset;

        internal DailyChallengeRepository(
            string connectionString,
            DailyResetService dailyReset)
        {
            _connectionString = connectionString;
            _dailyReset = dailyReset ?? throw new ArgumentNullException(nameof(dailyReset));
        }

        internal DailyChallengeInitializationResult EnsureInitialized(
            int characterId,
            int characterLevel,
            DailyChallengeGenerationPlan plan)
        {
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));
            if (plan == null)
                throw new ArgumentNullException(nameof(plan));
            if (plan.Groups.Count == 0)
            {
                throw new InvalidOperationException(
                    "Daily challenge PVF produced no eligible groups; existing ledger was preserved.");
            }

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    var firstInitializationToday = _dailyReset.TryClaimFlag(
                        connection,
                        transaction,
                        characterId,
                        GeneratedTodayCounterKey);
                    var refreshed = firstInitializationToday
                        || CountGroups(connection, transaction, characterId) == 0
                        || HasLockedEntries(
                            connection,
                            transaction,
                            characterId,
                            characterLevel);
                    var entryCount = 0;
                    if (refreshed)
                    {
                        ClearLedger(connection, transaction, characterId);
                        foreach (var group in plan.Groups)
                        {
                            InsertGroup(connection, transaction, characterId, group);
                            foreach (var entry in group.Entries)
                            {
                                InsertEntry(
                                    connection,
                                    transaction,
                                    characterId,
                                    group.GroupIndex,
                                    entry);
                                entryCount++;
                            }
                        }
                    }

                    var snapshot = LoadSnapshot(connection, transaction, characterId);
                    transaction.Commit();
                    return new DailyChallengeInitializationResult(
                        refreshed,
                        snapshot.RacingDungeonGroups.Count,
                        refreshed ? entryCount : CountEntries(snapshot),
                        snapshot);
                }
            }
        }

        private static bool HasLockedEntries(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int characterLevel)
        {
            using (var command = new SqliteCommand(@"
SELECT track_like_id
FROM character_daily_challenge_entries
WHERE character_id = @cid;", connection, transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (DailyChallengeData.IsQuestLockedAtLevel(
                                reader.GetInt32(0),
                                characterLevel))
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        internal DailyChallengeStoreResult ApplyMutation(
            int characterId,
            ushort questId,
            Func<uint, uint, uint> mutation)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    var entry = FindEntry(connection, transaction, characterId, questId);
                    if (entry == null)
                    {
                        var missingSnapshot = LoadSnapshot(connection, transaction, characterId);
                        transaction.Commit();
                        return DailyChallengeStoreResult.Missing(missingSnapshot);
                    }

                    var nextValue = mutation(entry.ValueA, entry.ValueB);
                    if (nextValue != entry.ValueB)
                    {
                        using (var command = new SqliteCommand(@"
UPDATE character_daily_challenge_entries
SET value_b = @next
WHERE character_id = @cid
  AND group_index = @groupIndex
  AND entry_index = @entryIndex
  AND track_like_id = @questId
  AND value_b = @expected;", connection, transaction))
                        {
                            command.Parameters.AddWithValue("@next", (long)nextValue);
                            command.Parameters.AddWithValue("@cid", characterId);
                            command.Parameters.AddWithValue("@groupIndex", entry.GroupIndex);
                            command.Parameters.AddWithValue("@entryIndex", entry.EntryIndex);
                            command.Parameters.AddWithValue("@questId", (int)questId);
                            command.Parameters.AddWithValue("@expected", (long)entry.ValueB);
                            if (command.ExecuteNonQuery() != 1)
                                throw new InvalidOperationException("DailyChallenge value_b CAS failed inside immediate transaction.");
                        }
                    }

                    var snapshot = LoadSnapshot(connection, transaction, characterId);
                    transaction.Commit();
                    return new DailyChallengeStoreResult(
                        found: true,
                        entry.GroupIndex,
                        entry.EntryIndex,
                        entry.ValueA,
                        entry.ValueB,
                        nextValue,
                        snapshot);
                }
            }
        }

        internal DailyChallengeResetResult ResetCharacter(int characterId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    ClearEntryQuestCompletionFlags(
                        connection,
                        transaction,
                        characterId);
                    using (var command = new SqliteCommand(@"
DELETE FROM character_daily_challenge_progress_events
WHERE character_id = @cid;", connection, transaction))
                    {
                        command.Parameters.AddWithValue("@cid", characterId);
                        command.ExecuteNonQuery();
                    }
                    int changedEntries;
                    using (var command = new SqliteCommand(@"
UPDATE character_daily_challenge_entries
SET value_b = value_a
WHERE character_id = @cid
  AND value_b <> value_a;", connection, transaction))
                    {
                        command.Parameters.AddWithValue("@cid", characterId);
                        changedEntries = command.ExecuteNonQuery();
                    }

                    int clearedClaims;
                    using (var command = new SqliteCommand(@"
DELETE FROM character_daily_challenge_claims
WHERE character_id = @cid;", connection, transaction))
                    {
                        command.Parameters.AddWithValue("@cid", characterId);
                        clearedClaims = command.ExecuteNonQuery();
                    }

                    using (var command = new SqliteCommand(@"
DELETE FROM character_daily_challenge_entry_claims
WHERE character_id = @cid;", connection, transaction))
                    {
                        command.Parameters.AddWithValue("@cid", characterId);
                        clearedClaims += command.ExecuteNonQuery();
                    }

                    var snapshot = LoadSnapshot(connection, transaction, characterId);
                    transaction.Commit();
                    return new DailyChallengeResetResult(changedEntries, clearedClaims, snapshot);
                }
            }
        }

        internal DailyChallengeRewardStoreState LoadRewardState(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int groupIndex)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));

            var state = new DailyChallengeRewardStoreState
            {
                GroupIndex = groupIndex,
            };

            using (var command = new SqliteCommand(@"
SELECT group_id
FROM character_daily_challenge_groups
WHERE character_id = @cid AND group_index = @groupIndex;", connection, transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@groupIndex", groupIndex);
                var value = command.ExecuteScalar();
                if (value == null || value == DBNull.Value)
                    return state;

                state.Found = true;
                state.GroupId = Convert.ToInt32(value);
            }

            using (var command = new SqliteCommand(@"
SELECT COUNT(*),
       COALESCE(SUM(CASE WHEN value_b = 0 THEN 1 ELSE 0 END), 0)
FROM character_daily_challenge_entries
WHERE character_id = @cid AND group_index = @groupIndex;", connection, transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@groupIndex", groupIndex);
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        state.EntryCount = reader.GetInt32(0);
                        state.CompletedEntryCount = reader.GetInt32(1);
                    }
                }
            }

            using (var command = new SqliteCommand(@"
SELECT 1
FROM character_daily_challenge_claims
WHERE character_id = @cid AND group_index = @groupIndex;", connection, transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@groupIndex", groupIndex);
                state.Claimed = command.ExecuteScalar() != null;
            }

            return state;
        }

        internal bool TryMarkRewardClaimed(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int groupIndex)
        {
            using (var command = new SqliteCommand(@"
INSERT INTO character_daily_challenge_claims (character_id, group_index)
VALUES (@cid, @groupIndex)
ON CONFLICT(character_id, group_index) DO NOTHING;", connection, transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@groupIndex", groupIndex);
                return command.ExecuteNonQuery() == 1;
            }
        }

        private static int CountGroups(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            using (var command = new SqliteCommand(@"
SELECT COUNT(*)
FROM character_daily_challenge_groups
WHERE character_id = @cid;", connection, transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private static int CountEntries(SelectCharacterInitializationSnapshot snapshot)
        {
            var count = 0;
            foreach (var group in snapshot.RacingDungeonGroups)
                count += group.Entries.Count;
            return count;
        }

        private static void ClearLedger(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            ClearEntryQuestCompletionFlags(connection, transaction, characterId);
            foreach (var table in new[]
            {
                "character_daily_challenge_progress_events",
                "character_daily_challenge_entry_claims",
                "character_daily_challenge_entries",
                "character_daily_challenge_claims",
                "character_daily_challenge_tail_ids",
                "character_daily_challenge_groups",
            })
            {
                using (var command = new SqliteCommand(
                    $"DELETE FROM {table} WHERE character_id = @cid;",
                    connection,
                    transaction))
                {
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static void ClearEntryQuestCompletionFlags(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            using (var command = new SqliteCommand(@"
DELETE FROM character_quest_completions
WHERE character_id = @cid
  AND quest_id IN (
      SELECT track_like_id
      FROM character_daily_challenge_entries
      WHERE character_id = @cid
  );", connection, transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                command.ExecuteNonQuery();
            }
        }

        private static void InsertGroup(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            DailyChallengeGenerationGroup group)
        {
            using (var command = new SqliteCommand(@"
INSERT INTO character_daily_challenge_groups
    (character_id, group_index, group_id)
VALUES (@cid, @groupIndex, @groupId);", connection, transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@groupIndex", group.GroupIndex);
                command.Parameters.AddWithValue("@groupId", group.GroupId);
                command.ExecuteNonQuery();
            }
        }

        private static void InsertEntry(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int groupIndex,
            DailyChallengeGenerationEntry entry)
        {
            using (var command = new SqliteCommand(@"
INSERT INTO character_daily_challenge_entries
    (character_id, group_index, entry_index, track_like_id, value_a, value_b)
VALUES (@cid, @groupIndex, @entryIndex, @questId, @target, @target);",
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@groupIndex", groupIndex);
                command.Parameters.AddWithValue("@entryIndex", entry.EntryIndex);
                command.Parameters.AddWithValue("@questId", entry.QuestId);
                command.Parameters.AddWithValue("@target", (long)entry.TargetValue);
                command.ExecuteNonQuery();
            }
        }

        private static DailyChallengeEntryRecord FindEntry(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            ushort questId)
        {
            using (var command = new SqliteCommand(@"
SELECT group_index, entry_index, value_a, value_b
FROM character_daily_challenge_entries
WHERE character_id = @cid AND track_like_id = @questId
ORDER BY group_index, entry_index
LIMIT 1;", connection, transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@questId", (int)questId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    return new DailyChallengeEntryRecord
                    {
                        GroupIndex = reader.GetInt32(0),
                        EntryIndex = reader.GetInt32(1),
                        ValueA = (uint)reader.GetInt64(2),
                        ValueB = (uint)reader.GetInt64(3),
                    };
                }
            }
        }

        private static List<DailyChallengeEntryRecord> LoadEntryRecords(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            var entries = new List<DailyChallengeEntryRecord>();
            using (var command = new SqliteCommand(@"
SELECT group_index, entry_index, track_like_id, value_a, value_b
FROM character_daily_challenge_entries
WHERE character_id = @cid
ORDER BY group_index, entry_index;", connection, transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        entries.Add(new DailyChallengeEntryRecord
                        {
                            GroupIndex = reader.GetInt32(0),
                            EntryIndex = reader.GetInt32(1),
                            QuestId = reader.GetInt32(2),
                            ValueA = (uint)reader.GetInt64(3),
                            ValueB = (uint)reader.GetInt64(4),
                        });
                    }
                }
            }
            return entries;
        }

        private static bool TryClaimProgressEvent(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            Guid sourceEventId,
            DailyChallengeEntryRecord entry)
        {
            using (var command = new SqliteCommand(@"
INSERT OR IGNORE INTO character_daily_challenge_progress_events
    (character_id, source_event_id, group_index, entry_index, quest_id)
VALUES (@cid, @eventId, @groupIndex, @entryIndex, @questId);",
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@eventId", sourceEventId.ToString("N"));
                command.Parameters.AddWithValue("@groupIndex", entry.GroupIndex);
                command.Parameters.AddWithValue("@entryIndex", entry.EntryIndex);
                command.Parameters.AddWithValue("@questId", entry.QuestId);
                return command.ExecuteNonQuery() == 1;
            }
        }

        private static bool HasProgressEvent(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            Guid sourceEventId,
            DailyChallengeEntryRecord entry)
        {
            using (var command = new SqliteCommand(@"
SELECT 1
FROM character_daily_challenge_progress_events
WHERE character_id = @cid
  AND source_event_id = @eventId
  AND group_index = @groupIndex
  AND entry_index = @entryIndex
  AND quest_id = @questId;", connection, transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@eventId", sourceEventId.ToString("N"));
                command.Parameters.AddWithValue("@groupIndex", entry.GroupIndex);
                command.Parameters.AddWithValue("@entryIndex", entry.EntryIndex);
                command.Parameters.AddWithValue("@questId", entry.QuestId);
                return command.ExecuteScalar() != null;
            }
        }

        internal DailyChallengeDungeonClearResult ApplySuitableDungeonClear(
            int characterId,
            int dungeonId,
            int difficulty,
            int characterLevel,
            Guid sourceEventId)
        {
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));
            if (dungeonId <= 0)
                throw new ArgumentOutOfRangeException(nameof(dungeonId));
            if (sourceEventId == Guid.Empty)
                throw new ArgumentException("A stable dungeon-clear event id is required.", nameof(sourceEventId));

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    var relevantEntries = 0;
                    var changedEntries = 0;
                    if (GameWorld.Dungeon.IsSuitableLevelDungeon(dungeonId, characterLevel))
                    {
                        foreach (var entry in LoadEntryRecords(
                            connection,
                            transaction,
                            characterId))
                        {
                            if (!QuestData.TryGetSuitableDungeonClearChallengeRule(
                                    entry.QuestId,
                                    out var minimumDifficulty)
                                || (minimumDifficulty >= 0 && difficulty < minimumDifficulty))
                            {
                                continue;
                            }

                            if (entry.ValueB == 0)
                            {
                                // Only a replay of the event that completed the
                                // entry needs a recovery snapshot. A new clear
                                // after completion must not keep emitting 0x0286.
                                if (HasProgressEvent(
                                        connection,
                                        transaction,
                                        characterId,
                                        sourceEventId,
                                        entry))
                                {
                                    relevantEntries++;
                                }
                                continue;
                            }

                            relevantEntries++;
                            if (!TryClaimProgressEvent(
                                    connection,
                                    transaction,
                                    characterId,
                                    sourceEventId,
                                    entry))
                            {
                                continue;
                            }

                            using (var command = new SqliteCommand(@"
UPDATE character_daily_challenge_entries
SET value_b = value_b - 1
WHERE character_id = @cid
  AND group_index = @groupIndex
  AND entry_index = @entryIndex
  AND track_like_id = @questId
  AND value_b > 0;", connection, transaction))
                            {
                                command.Parameters.AddWithValue("@cid", characterId);
                                command.Parameters.AddWithValue("@groupIndex", entry.GroupIndex);
                                command.Parameters.AddWithValue("@entryIndex", entry.EntryIndex);
                                command.Parameters.AddWithValue("@questId", entry.QuestId);
                                if (command.ExecuteNonQuery() != 1)
                                    throw new InvalidOperationException("Daily challenge suitable-dungeon progress CAS failed.");
                                changedEntries++;
                            }
                        }
                    }

                    var snapshot = LoadSnapshot(connection, transaction, characterId);
                    transaction.Commit();
                    return new DailyChallengeDungeonClearResult(
                        relevantEntries,
                        changedEntries,
                        snapshot);
                }
            }
        }

        internal DailyChallengeMonsterKillResult ApplySuitableDungeonBossKill(
            int characterId,
            int dungeonId,
            int difficulty,
            int characterLevel,
            int monsterCode,
            byte monsterType,
            Guid sourceEventId)
        {
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));
            if (dungeonId <= 0)
                throw new ArgumentOutOfRangeException(nameof(dungeonId));
            if (monsterCode <= 0)
                throw new ArgumentOutOfRangeException(nameof(monsterCode));
            if (sourceEventId == Guid.Empty)
            {
                throw new ArgumentException(
                    "A stable boss-kill event id is required.",
                    nameof(sourceEventId));
            }

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    var relevantEntries = 0;
                    var changedEntries = 0;
                    if (monsterType == 3
                        && GameWorld.Dungeon.IsSuitableLevelDungeon(
                            dungeonId,
                            characterLevel))
                    {
                        foreach (var entry in LoadEntryRecords(
                            connection,
                            transaction,
                            characterId))
                        {
                            if (!QuestData
                                    .TryGetSuitableDungeonBossKillChallengeRule(
                                        entry.QuestId,
                                        out var minimumDifficulty,
                                        out _)
                                || (minimumDifficulty >= 0
                                    && difficulty < minimumDifficulty))
                            {
                                continue;
                            }

                            if (entry.ValueB == 0)
                            {
                                if (HasProgressEvent(
                                        connection,
                                        transaction,
                                        characterId,
                                        sourceEventId,
                                        entry))
                                {
                                    relevantEntries++;
                                }
                                continue;
                            }

                            relevantEntries++;
                            if (!TryClaimProgressEvent(
                                    connection,
                                    transaction,
                                    characterId,
                                    sourceEventId,
                                    entry))
                            {
                                continue;
                            }

                            using (var command = new SqliteCommand(@"
UPDATE character_daily_challenge_entries
SET value_b = value_b - 1
WHERE character_id = @cid
  AND group_index = @groupIndex
  AND entry_index = @entryIndex
  AND track_like_id = @questId
  AND value_b > 0;", connection, transaction))
                            {
                                command.Parameters.AddWithValue("@cid", characterId);
                                command.Parameters.AddWithValue(
                                    "@groupIndex",
                                    entry.GroupIndex);
                                command.Parameters.AddWithValue(
                                    "@entryIndex",
                                    entry.EntryIndex);
                                command.Parameters.AddWithValue(
                                    "@questId",
                                    entry.QuestId);
                                if (command.ExecuteNonQuery() != 1)
                                {
                                    throw new InvalidOperationException(
                                        "Daily challenge boss-kill progress CAS failed.");
                                }
                                changedEntries++;
                            }
                        }
                    }

                    var snapshot = LoadSnapshot(connection, transaction, characterId);
                    transaction.Commit();
                    return new DailyChallengeMonsterKillResult(
                        relevantEntries,
                        changedEntries,
                        snapshot);
                }
            }
        }

        internal static DailyChallengeQuestCompletionResult ApplyQuestCompletion(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            string completedQuestGrade,
            int completionCount)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (transaction == null)
                throw new ArgumentNullException(nameof(transaction));
            if (characterId <= 0 || completionCount <= 0)
                return new DailyChallengeQuestCompletionResult(0, 0, null);

            var relevantEntries = 0;
            var changedEntries = 0;
            foreach (var entry in LoadEntryRecords(connection, transaction, characterId))
            {
                if (!QuestData.TryGetQuestCompletionChallengeRule(
                        entry.QuestId,
                        out var gradeSelector,
                        out _)
                    || !QuestData.MatchesQuestGradeSelector(
                        gradeSelector,
                        completedQuestGrade))
                {
                    continue;
                }

                relevantEntries++;
                if (entry.ValueB == 0)
                    continue;

                var decrement = Math.Min((uint)completionCount, entry.ValueB);
                using (var command = new SqliteCommand(@"
UPDATE character_daily_challenge_entries
SET value_b = value_b - @decrement
WHERE character_id = @cid
  AND group_index = @groupIndex
  AND entry_index = @entryIndex
  AND track_like_id = @questId
  AND value_b = @expected;", connection, transaction))
                {
                    command.Parameters.AddWithValue("@decrement", (long)decrement);
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@groupIndex", entry.GroupIndex);
                    command.Parameters.AddWithValue("@entryIndex", entry.EntryIndex);
                    command.Parameters.AddWithValue("@questId", entry.QuestId);
                    command.Parameters.AddWithValue("@expected", (long)entry.ValueB);
                    if (command.ExecuteNonQuery() != 1)
                        throw new InvalidOperationException("Daily challenge quest-completion progress CAS failed.");
                    changedEntries++;
                }
            }

            var snapshot = changedEntries > 0
                ? LoadSnapshot(connection, transaction, characterId)
                : null;
            return new DailyChallengeQuestCompletionResult(
                relevantEntries,
                changedEntries,
                snapshot);
        }

        internal static DailyChallengeEntryRewardState LoadEntryRewardState(
            string connectionString,
            int characterId,
            ushort questId)
        {
            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var state = LoadEntryRewardState(
                        connection,
                        transaction,
                        characterId,
                        questId);
                    transaction.Commit();
                    return state;
                }
            }
        }

        internal static DailyChallengeEntryRewardState LoadEntryRewardState(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            ushort questId)
        {
            var state = new DailyChallengeEntryRewardState { QuestId = questId };
            using (var command = new SqliteCommand(@"
SELECT e.group_index,
       e.entry_index,
       e.value_a,
       e.value_b,
       CASE WHEN c.character_id IS NULL THEN 0 ELSE 1 END
FROM character_daily_challenge_entries AS e
LEFT JOIN character_daily_challenge_entry_claims AS c
  ON c.character_id = e.character_id
 AND c.group_index = e.group_index
 AND c.entry_index = e.entry_index
 AND c.quest_id = e.track_like_id
WHERE e.character_id = @cid
  AND e.track_like_id = @questId
ORDER BY e.group_index, e.entry_index
LIMIT 1;", connection, transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@questId", (int)questId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return state;

                    state.Found = true;
                    state.GroupIndex = reader.GetInt32(0);
                    state.EntryIndex = reader.GetInt32(1);
                    state.TargetValue = (uint)reader.GetInt64(2);
                    state.RemainingValue = (uint)reader.GetInt64(3);
                    state.Claimed = reader.GetInt32(4) != 0;
                }
            }
            return state;
        }

        internal static bool TryMarkEntryRewardClaimed(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            DailyChallengeEntryRewardState expected)
        {
            if (expected == null || !expected.Found || !expected.Completed)
                return false;

            using (var command = new SqliteCommand(@"
INSERT INTO character_daily_challenge_entry_claims
    (character_id, group_index, entry_index, quest_id)
SELECT e.character_id, e.group_index, e.entry_index, e.track_like_id
FROM character_daily_challenge_entries AS e
WHERE e.character_id = @cid
  AND e.group_index = @groupIndex
  AND e.entry_index = @entryIndex
  AND e.track_like_id = @questId
  AND e.value_a = @target
  AND e.value_b = 0
ON CONFLICT(character_id, group_index, entry_index) DO NOTHING;",
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@groupIndex", expected.GroupIndex);
                command.Parameters.AddWithValue("@entryIndex", expected.EntryIndex);
                command.Parameters.AddWithValue("@questId", (int)expected.QuestId);
                command.Parameters.AddWithValue("@target", (long)expected.TargetValue);
                return command.ExecuteNonQuery() == 1;
            }
        }

        internal static SelectCharacterInitializationSnapshot LoadSnapshot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            var snapshot = new SelectCharacterInitializationSnapshot();
            using (var command = new SqliteCommand(@"
SELECT level
FROM characters
WHERE character_id = @cid;", connection, transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                var value = command.ExecuteScalar();
                snapshot.DailyChallengeCharacterLevel = value == null || value == DBNull.Value
                    ? 1u
                    : (uint)Convert.ToInt64(value);
            }

            var groupsByIndex = new Dictionary<int, RacingDungeonGroupSnapshot>();
            using (var command = new SqliteCommand(@"
SELECT group_index, group_id
FROM character_daily_challenge_groups
WHERE character_id = @cid
ORDER BY group_index;", connection, transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var group = new RacingDungeonGroupSnapshot
                        {
                            GroupId = (uint)reader.GetInt64(1),
                        };
                        groupsByIndex[reader.GetInt32(0)] = group;
                        snapshot.RacingDungeonGroups.Add(group);
                    }
                }
            }

            using (var command = new SqliteCommand(@"
SELECT group_index, track_like_id, value_a, value_b
FROM character_daily_challenge_entries
WHERE character_id = @cid
ORDER BY group_index, entry_index;", connection, transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (!groupsByIndex.TryGetValue(reader.GetInt32(0), out var group))
                            continue;

                        group.Entries.Add(new RacingDungeonEntrySnapshot
                        {
                            TrackLikeId = (uint)reader.GetInt64(1),
                            ValueA = (uint)reader.GetInt64(2),
                            ValueB = (uint)reader.GetInt64(3),
                        });
                    }
                }
            }

            snapshot.DailyChallengeRewardClaimFlags = new byte[
                SelectCharacterInitializationSnapshot.DailyChallengeClaimFlagCount];
            using (var command = new SqliteCommand(@"
SELECT group_index
FROM character_daily_challenge_claims
WHERE character_id = @cid
ORDER BY group_index;", connection, transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var groupIndex = reader.GetInt32(0);
                        if (groupIndex >= 0 && groupIndex < snapshot.DailyChallengeRewardClaimFlags.Length)
                            snapshot.DailyChallengeRewardClaimFlags[groupIndex] = 1;
                    }
                }
            }

            using (var command = new SqliteCommand(@"
SELECT id_value
FROM character_daily_challenge_tail_ids
WHERE character_id = @cid
ORDER BY sort_order;", connection, transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                        snapshot.RacingDungeonTailIds.Add((uint)reader.GetInt64(0));
                }
            }

            return snapshot;
        }

        private sealed class DailyChallengeEntryRecord
        {
            internal int GroupIndex;
            internal int EntryIndex;
            internal int QuestId;
            internal uint ValueA;
            internal uint ValueB;
        }
    }

    internal sealed class DailyChallengeInitializationResult
    {
        internal DailyChallengeInitializationResult(
            bool refreshed,
            int groupCount,
            int entryCount,
            SelectCharacterInitializationSnapshot snapshot)
        {
            Refreshed = refreshed;
            GroupCount = groupCount;
            EntryCount = entryCount;
            Snapshot = snapshot;
        }

        internal bool Refreshed { get; }
        internal int GroupCount { get; }
        internal int EntryCount { get; }
        internal SelectCharacterInitializationSnapshot Snapshot { get; }
    }

    internal sealed class DailyChallengeRewardStoreState
    {
        internal bool Found { get; set; }
        internal int GroupIndex { get; set; }
        internal int GroupId { get; set; }
        internal int EntryCount { get; set; }
        internal int CompletedEntryCount { get; set; }
        internal bool Claimed { get; set; }
    }

    internal sealed class DailyChallengeEntryRewardState
    {
        internal bool Found { get; set; }
        internal int GroupIndex { get; set; }
        internal int EntryIndex { get; set; }
        internal ushort QuestId { get; set; }
        internal uint TargetValue { get; set; }
        internal uint RemainingValue { get; set; }
        internal bool Claimed { get; set; }
        internal bool Completed => Found && RemainingValue == 0;
        internal bool CanClaim => Completed && !Claimed;
    }

    internal sealed class DailyChallengeStoreResult
    {
        internal DailyChallengeStoreResult(
            bool found,
            int groupIndex,
            int entryIndex,
            uint targetValue,
            uint previousValue,
            uint currentValue,
            SelectCharacterInitializationSnapshot snapshot)
        {
            Found = found;
            GroupIndex = groupIndex;
            EntryIndex = entryIndex;
            TargetValue = targetValue;
            PreviousValue = previousValue;
            CurrentValue = currentValue;
            Snapshot = snapshot;
        }

        internal bool Found { get; }
        internal int GroupIndex { get; }
        internal int EntryIndex { get; }
        internal uint TargetValue { get; }
        internal uint PreviousValue { get; }
        internal uint CurrentValue { get; }
        internal SelectCharacterInitializationSnapshot Snapshot { get; }
        internal bool Changed => Found && PreviousValue != CurrentValue;

        internal static DailyChallengeStoreResult Missing(
            SelectCharacterInitializationSnapshot snapshot) =>
            new DailyChallengeStoreResult(false, -1, -1, 0, uint.MaxValue, uint.MaxValue, snapshot);
    }

    internal sealed class DailyChallengeResetResult
    {
        internal DailyChallengeResetResult(
            int changedEntries,
            int clearedClaims,
            SelectCharacterInitializationSnapshot snapshot)
        {
            ChangedEntries = changedEntries;
            ClearedClaims = clearedClaims;
            Snapshot = snapshot;
        }

        internal int ChangedEntries { get; }
        internal int ClearedClaims { get; }
        internal SelectCharacterInitializationSnapshot Snapshot { get; }
    }

    internal sealed class DailyChallengeDungeonClearResult
    {
        internal DailyChallengeDungeonClearResult(
            int relevantEntries,
            int changedEntries,
            SelectCharacterInitializationSnapshot snapshot)
        {
            RelevantEntries = relevantEntries;
            ChangedEntries = changedEntries;
            Snapshot = snapshot;
        }

        internal int RelevantEntries { get; }
        internal int ChangedEntries { get; }
        internal bool HasRelevantProgress => RelevantEntries > 0;
        internal SelectCharacterInitializationSnapshot Snapshot { get; }
    }

    internal sealed class DailyChallengeQuestCompletionResult
    {
        internal DailyChallengeQuestCompletionResult(
            int relevantEntries,
            int changedEntries,
            SelectCharacterInitializationSnapshot snapshot)
        {
            RelevantEntries = relevantEntries;
            ChangedEntries = changedEntries;
            Snapshot = snapshot;
        }

        internal int RelevantEntries { get; }
        internal int ChangedEntries { get; }
        internal SelectCharacterInitializationSnapshot Snapshot { get; }
    }

    internal sealed class DailyChallengeMonsterKillResult
    {
        internal DailyChallengeMonsterKillResult(
            int relevantEntries,
            int changedEntries,
            SelectCharacterInitializationSnapshot snapshot)
        {
            RelevantEntries = relevantEntries;
            ChangedEntries = changedEntries;
            Snapshot = snapshot;
        }

        internal int RelevantEntries { get; }
        internal int ChangedEntries { get; }
        internal bool HasRelevantProgress => RelevantEntries > 0;
        internal SelectCharacterInitializationSnapshot Snapshot { get; }
    }
}
