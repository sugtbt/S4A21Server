using System;
using System.Collections.Generic;
using DfoServer.Game.DailyReset;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Dungeon
{
    internal enum DungeonLimitScopeType
    {
        Character = 0,
        Account = 1,
    }

    internal readonly struct SpecialDungeonEntryLimitDefault
    {
        internal SpecialDungeonEntryLimitDefault(
            int dungeonId,
            byte currentCount,
            int sortOrder)
        {
            DungeonId = dungeonId;
            CurrentCount = currentCount;
            SortOrder = sortOrder;
        }

        internal int DungeonId { get; }

        internal byte CurrentCount { get; }

        internal int SortOrder { get; }
    }

    internal static class SpecialDungeonEntryLimitDefaults
    {
        internal static readonly SpecialDungeonEntryLimitDefault[] Entries =
        {
            new SpecialDungeonEntryLimitDefault(11006, 3, 0),
            new SpecialDungeonEntryLimitDefault(11007, 3, 1),
            new SpecialDungeonEntryLimitDefault(3054, 3, 2),
            new SpecialDungeonEntryLimitDefault(3056, 3, 3),
            new SpecialDungeonEntryLimitDefault(3057, 1, 4),
            new SpecialDungeonEntryLimitDefault(122, 9, 5),
            new SpecialDungeonEntryLimitDefault(4000, 1, 6),
            new SpecialDungeonEntryLimitDefault(3706, 3, 7),
            new SpecialDungeonEntryLimitDefault(4108, 1, 8),
            new SpecialDungeonEntryLimitDefault(4109, 1, 9),
            new SpecialDungeonEntryLimitDefault(4110, 1, 10),
            new SpecialDungeonEntryLimitDefault(4111, 1, 11),
            new SpecialDungeonEntryLimitDefault(4103, 3, 12),
            new SpecialDungeonEntryLimitDefault(4114, 3, 13),
            new SpecialDungeonEntryLimitDefault(4115, 3, 14),
            new SpecialDungeonEntryLimitDefault(4116, 3, 15),
            new SpecialDungeonEntryLimitDefault(4117, 3, 16),
            new SpecialDungeonEntryLimitDefault(4118, 3, 17),
            new SpecialDungeonEntryLimitDefault(4130, 3, 18),
            new SpecialDungeonEntryLimitDefault(3900, 3, 19),
            new SpecialDungeonEntryLimitDefault(4124, 1, 20),
            new SpecialDungeonEntryLimitDefault(4125, 1, 21),
            new SpecialDungeonEntryLimitDefault(4126, 1, 22),
            new SpecialDungeonEntryLimitDefault(4127, 1, 23),
            new SpecialDungeonEntryLimitDefault(4128, 1, 24),
            new SpecialDungeonEntryLimitDefault(4123, 3, 25),
        };
    }

    internal sealed class SpecialDungeonEntryLimitSnapshot
    {
        internal int DungeonId { get; set; }

        internal DungeonLimitScopeType ScopeType { get; set; }

        internal byte CurrentCount { get; set; }

        internal int ExtraCount { get; set; }

        internal int UsedCount { get; set; }

        internal int LimitCount { get; set; }

        internal int DayId { get; set; }
    }

    internal sealed class DimensionGateEntryLimitSnapshot
    {
        internal int CharacterId { get; set; }

        internal int DayId { get; set; }

        internal int CurrentCount { get; set; }

        internal int ExtraCount { get; set; }

        internal int UsedCount { get; set; }
    }

    internal sealed class DungeonEntryLimitConsumeResult
    {
        private DungeonEntryLimitConsumeResult()
        {
        }

        internal bool Allowed { get; private set; }

        internal bool IsLimited { get; private set; }

        internal string Reason { get; private set; }

        internal int DungeonId { get; private set; }

        internal int CurrentCount { get; private set; }

        internal int ExtraCount { get; private set; }

        internal int UsedCount { get; private set; }

        internal static DungeonEntryLimitConsumeResult AllowUnlimited(
            int dungeonId)
            => new DungeonEntryLimitConsumeResult
            {
                Allowed = true,
                IsLimited = false,
                Reason = "not_configured",
                DungeonId = dungeonId,
            };

        internal static DungeonEntryLimitConsumeResult Allow(
            int dungeonId,
            int currentCount,
            int extraCount,
            int usedCount)
            => new DungeonEntryLimitConsumeResult
            {
                Allowed = true,
                IsLimited = true,
                Reason = "allowed",
                DungeonId = dungeonId,
                CurrentCount = currentCount,
                ExtraCount = extraCount,
                UsedCount = usedCount,
            };

        internal static DungeonEntryLimitConsumeResult Reject(
            int dungeonId,
            string reason,
            int currentCount = 0,
            int extraCount = 0,
            int usedCount = 0)
            => new DungeonEntryLimitConsumeResult
            {
                Allowed = false,
                IsLimited = true,
                Reason = reason ?? "rejected",
                DungeonId = dungeonId,
                CurrentCount = currentCount,
                ExtraCount = extraCount,
                UsedCount = usedCount,
            };
    }

    internal sealed class DungeonEntryLimitService
    {
        private const string ScopeCharacter = "charac";
        private const string ScopeAccount = "account";

        private readonly string _connectionString;
        private readonly Func<DateTime> _utcNowProvider;

        internal DungeonEntryLimitService(
            IGameDatabase database,
            Func<DateTime> utcNowProvider = null)
            : this(
                (database ?? throw new ArgumentNullException(nameof(database)))
                    .ConnectionString,
                utcNowProvider)
        {
        }

        internal DungeonEntryLimitService(
            string connectionString,
            Func<DateTime> utcNowProvider = null)
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new ArgumentException(
                    "connectionString is empty",
                    nameof(connectionString));

            _connectionString = connectionString;
            _utcNowProvider = utcNowProvider ?? (() => DateTime.UtcNow);
        }

        internal IReadOnlyList<SpecialDungeonEntryLimitSnapshot>
            LoadSpecialDungeonLimits(int accountId, int characterId)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                return LoadSpecialDungeonLimits(
                    connection,
                    null,
                    accountId,
                    characterId);
            }
        }

        internal IReadOnlyList<SpecialDungeonEntryLimitSnapshot>
            LoadSpecialDungeonLimits(
                SqliteConnection connection,
                SqliteTransaction transaction,
                int accountId,
                int characterId)
        {
            if (connection == null || accountId <= 0 || characterId <= 0)
                return Array.Empty<SpecialDungeonEntryLimitSnapshot>();

            var dayId = CurrentDayId();
            var result = new List<SpecialDungeonEntryLimitSnapshot>();
            foreach (var config in LoadSpecialConfigs(connection, transaction))
            {
                var targetCharacterId = ResolveRecordCharacterId(
                    config.ScopeType,
                    characterId);
                if (TryLoadSpecialRecord(
                        connection,
                        transaction,
                        accountId,
                        targetCharacterId,
                        config.DungeonId,
                        dayId,
                        out var state))
                {
                    result.Add(new SpecialDungeonEntryLimitSnapshot
                    {
                        DungeonId = config.DungeonId,
                        ScopeType = config.ScopeType,
                        CurrentCount = ToByteCount(state.CurrentCount),
                        ExtraCount = state.ExtraCount,
                        UsedCount = state.UsedCount,
                        LimitCount = config.LimitCount,
                        DayId = dayId,
                    });
                    continue;
                }

                result.Add(new SpecialDungeonEntryLimitSnapshot
                {
                    DungeonId = config.DungeonId,
                    ScopeType = config.ScopeType,
                    CurrentCount = ToByteCount(config.LimitCount),
                    ExtraCount = 0,
                    UsedCount = 0,
                    LimitCount = config.LimitCount,
                    DayId = dayId,
                });
            }

            return result;
        }

        internal bool TryConsumeSpecialDungeonLimit(
            int accountId,
            int characterId,
            int dungeonId,
            int consumeCount,
            out DungeonEntryLimitConsumeResult result)
        {
            DungeonEntryLimitConsumeResult local = null;
            var allowed = false;
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    allowed = TryConsumeSpecialDungeonLimit(
                        connection,
                        transaction,
                        accountId,
                        characterId,
                        dungeonId,
                        consumeCount,
                        out local);
                    transaction.Commit();
                }
            }

            result = local;
            return allowed;
        }

        internal bool TryCheckSpecialDungeonLimit(
            int accountId,
            int characterId,
            int dungeonId,
            int consumeCount,
            out DungeonEntryLimitConsumeResult result)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                return TryCheckSpecialDungeonLimit(
                    connection,
                    null,
                    accountId,
                    characterId,
                    dungeonId,
                    consumeCount,
                    out result);
            }
        }

        internal bool TryCheckSpecialDungeonLimit(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int characterId,
            int dungeonId,
            int consumeCount,
            out DungeonEntryLimitConsumeResult result)
        {
            result = null;
            if (connection == null
                || accountId <= 0
                || characterId <= 0
                || dungeonId <= 0
                || consumeCount <= 0)
            {
                result = DungeonEntryLimitConsumeResult.Reject(
                    dungeonId,
                    "invalid_request");
                return false;
            }

            if (!TryLoadSpecialConfig(
                    connection,
                    transaction,
                    dungeonId,
                    out var config))
            {
                result = DungeonEntryLimitConsumeResult.AllowUnlimited(
                    dungeonId);
                return true;
            }

            var dayId = CurrentDayId();
            var targetCharacterId = ResolveRecordCharacterId(
                config.ScopeType,
                characterId);
            if (!TryLoadSpecialRecord(
                    connection,
                    transaction,
                    accountId,
                    targetCharacterId,
                    dungeonId,
                    dayId,
                    out var state))
            {
                state = new MutableEntryLimitState
                {
                    CurrentCount = config.LimitCount,
                    ExtraCount = 0,
                    UsedCount = 0,
                    DayId = dayId,
                };
            }

            if (state.CurrentCount + state.ExtraCount < consumeCount)
            {
                result = DungeonEntryLimitConsumeResult.Reject(
                    dungeonId,
                    "entry_limit_exhausted",
                    state.CurrentCount,
                    state.ExtraCount,
                    state.UsedCount);
                return false;
            }

            result = DungeonEntryLimitConsumeResult.Allow(
                dungeonId,
                state.CurrentCount,
                state.ExtraCount,
                state.UsedCount);
            return true;
        }

        internal bool TryConsumeSpecialDungeonLimit(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int characterId,
            int dungeonId,
            int consumeCount,
            out DungeonEntryLimitConsumeResult result)
        {
            result = null;
            if (connection == null
                || accountId <= 0
                || characterId <= 0
                || dungeonId <= 0
                || consumeCount <= 0)
            {
                result = DungeonEntryLimitConsumeResult.Reject(
                    dungeonId,
                    "invalid_request");
                return false;
            }

            if (!TryLoadSpecialConfig(
                    connection,
                    transaction,
                    dungeonId,
                    out var config))
            {
                result = DungeonEntryLimitConsumeResult.AllowUnlimited(
                    dungeonId);
                return true;
            }

            var dayId = CurrentDayId();
            var targetCharacterId = ResolveRecordCharacterId(
                config.ScopeType,
                characterId);
            if (!TryLoadSpecialRecord(
                    connection,
                    transaction,
                    accountId,
                    targetCharacterId,
                    dungeonId,
                    dayId,
                    out var state))
            {
                state = new MutableEntryLimitState
                {
                    CurrentCount = config.LimitCount,
                    ExtraCount = 0,
                    UsedCount = 0,
                    DayId = dayId,
                };
            }

            if (!TryConsume(ref state, consumeCount))
            {
                result = DungeonEntryLimitConsumeResult.Reject(
                    dungeonId,
                    "entry_limit_exhausted",
                    state.CurrentCount,
                    state.ExtraCount,
                    state.UsedCount);
                return false;
            }

            UpsertSpecialRecord(
                connection,
                transaction,
                accountId,
                targetCharacterId,
                dungeonId,
                state);
            result = DungeonEntryLimitConsumeResult.Allow(
                dungeonId,
                state.CurrentCount,
                state.ExtraCount,
                state.UsedCount);
            return true;
        }

        internal DimensionGateEntryLimitSnapshot LoadDimensionGateLimit(
            int characterId,
            int defaultCurrentCount,
            int defaultExtraCount)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                return LoadDimensionGateLimit(
                    connection,
                    null,
                    characterId,
                    defaultCurrentCount,
                    defaultExtraCount);
            }
        }

        internal DimensionGateEntryLimitSnapshot LoadDimensionGateLimit(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int defaultCurrentCount,
            int defaultExtraCount)
        {
            if (connection == null
                || characterId <= 0
                || defaultCurrentCount < 0
                || defaultExtraCount < 0)
            {
                return null;
            }

            var dayId = CurrentDayId();
            if (TryLoadDimensionGateRecord(
                    connection,
                    transaction,
                    characterId,
                    dayId,
                    out var state))
            {
                return new DimensionGateEntryLimitSnapshot
                {
                    CharacterId = characterId,
                    DayId = dayId,
                    CurrentCount = state.CurrentCount,
                    ExtraCount = state.ExtraCount,
                    UsedCount = state.UsedCount,
                };
            }

            return new DimensionGateEntryLimitSnapshot
            {
                CharacterId = characterId,
                DayId = dayId,
                CurrentCount = defaultCurrentCount,
                ExtraCount = defaultExtraCount,
                UsedCount = 0,
            };
        }

        internal bool TryConsumeDimensionGateLimit(
            int characterId,
            int defaultCurrentCount,
            int defaultExtraCount,
            int consumeCount,
            out DungeonEntryLimitConsumeResult result)
        {
            DungeonEntryLimitConsumeResult local = null;
            var allowed = false;
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    allowed = TryConsumeDimensionGateLimit(
                        connection,
                        transaction,
                        characterId,
                        defaultCurrentCount,
                        defaultExtraCount,
                        consumeCount,
                        out local);
                    transaction.Commit();
                }
            }

            result = local;
            return allowed;
        }

        internal bool TryCheckDimensionGateLimit(
            int characterId,
            int defaultCurrentCount,
            int defaultExtraCount,
            int consumeCount,
            out DungeonEntryLimitConsumeResult result)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                return TryCheckDimensionGateLimit(
                    connection,
                    null,
                    characterId,
                    defaultCurrentCount,
                    defaultExtraCount,
                    consumeCount,
                    out result);
            }
        }

        internal bool TryCheckDimensionGateLimit(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int defaultCurrentCount,
            int defaultExtraCount,
            int consumeCount,
            out DungeonEntryLimitConsumeResult result)
        {
            result = null;
            if (connection == null
                || characterId <= 0
                || defaultCurrentCount < 0
                || defaultExtraCount < 0
                || consumeCount <= 0)
            {
                result = DungeonEntryLimitConsumeResult.Reject(
                    0,
                    "invalid_request");
                return false;
            }

            var dayId = CurrentDayId();
            if (!TryLoadDimensionGateRecord(
                    connection,
                    transaction,
                    characterId,
                    dayId,
                    out var state))
            {
                state = new MutableEntryLimitState
                {
                    CurrentCount = defaultCurrentCount,
                    ExtraCount = defaultExtraCount,
                    UsedCount = 0,
                    DayId = dayId,
                };
            }

            if (state.CurrentCount + state.ExtraCount < consumeCount)
            {
                result = DungeonEntryLimitConsumeResult.Reject(
                    0,
                    "entry_limit_exhausted",
                    state.CurrentCount,
                    state.ExtraCount,
                    state.UsedCount);
                return false;
            }

            result = DungeonEntryLimitConsumeResult.Allow(
                0,
                state.CurrentCount,
                state.ExtraCount,
                state.UsedCount);
            return true;
        }

        internal bool TryConsumeDimensionGateLimit(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int defaultCurrentCount,
            int defaultExtraCount,
            int consumeCount,
            out DungeonEntryLimitConsumeResult result)
        {
            result = null;
            if (connection == null
                || characterId <= 0
                || defaultCurrentCount < 0
                || defaultExtraCount < 0
                || consumeCount <= 0)
            {
                result = DungeonEntryLimitConsumeResult.Reject(
                    0,
                    "invalid_request");
                return false;
            }

            var dayId = CurrentDayId();
            if (!TryLoadDimensionGateRecord(
                    connection,
                    transaction,
                    characterId,
                    dayId,
                    out var state))
            {
                state = new MutableEntryLimitState
                {
                    CurrentCount = defaultCurrentCount,
                    ExtraCount = defaultExtraCount,
                    UsedCount = 0,
                    DayId = dayId,
                };
            }

            if (!TryConsume(ref state, consumeCount))
            {
                result = DungeonEntryLimitConsumeResult.Reject(
                    0,
                    "entry_limit_exhausted",
                    state.CurrentCount,
                    state.ExtraCount,
                    state.UsedCount);
                return false;
            }

            UpsertDimensionGateRecord(
                connection,
                transaction,
                characterId,
                state);
            result = DungeonEntryLimitConsumeResult.Allow(
                0,
                state.CurrentCount,
                state.ExtraCount,
                state.UsedCount);
            return true;
        }

        private int CurrentDayId()
            => DailyResetService.TodayId(_utcNowProvider());

        private static int ResolveRecordCharacterId(
            DungeonLimitScopeType scopeType,
            int characterId)
            => scopeType == DungeonLimitScopeType.Account ? 0 : characterId;

        private static bool TryConsume(
            ref MutableEntryLimitState state,
            int consumeCount)
        {
            if (consumeCount <= 0)
                return false;

            var available = state.CurrentCount + state.ExtraCount;
            if (available < consumeCount)
                return false;

            var consumeCurrent = Math.Min(state.CurrentCount, consumeCount);
            state.CurrentCount -= consumeCurrent;
            var remaining = consumeCount - consumeCurrent;
            if (remaining > 0)
                state.ExtraCount -= remaining;
            state.UsedCount += consumeCount;
            return true;
        }

        private static byte ToByteCount(int value)
        {
            if (value <= 0)
                return 0;
            if (value >= byte.MaxValue)
                return byte.MaxValue;
            return (byte)value;
        }

        private static DungeonLimitScopeType ScopeFromDatabase(string value)
            => string.Equals(value, ScopeAccount, StringComparison.Ordinal)
                ? DungeonLimitScopeType.Account
                : DungeonLimitScopeType.Character;

        private static List<SpecialDungeonConfig> LoadSpecialConfigs(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            var result = new List<SpecialDungeonConfig>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT dgn_id, scope_type, limit_count, sort_order
FROM dungeon_limit_config
WHERE enabled = 1
ORDER BY sort_order, dgn_id;";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new SpecialDungeonConfig
                        {
                            DungeonId = reader.GetInt32(0),
                            ScopeType = ScopeFromDatabase(reader.GetString(1)),
                            LimitCount = reader.GetInt32(2),
                            SortOrder = reader.GetInt32(3),
                        });
                    }
                }
            }

            return result;
        }

        private static bool TryLoadSpecialConfig(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int dungeonId,
            out SpecialDungeonConfig config)
        {
            config = null;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT dgn_id, scope_type, limit_count, sort_order
FROM dungeon_limit_config
WHERE dgn_id = @dgnId
  AND enabled = 1;";
                command.Parameters.AddWithValue("@dgnId", dungeonId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return false;

                    config = new SpecialDungeonConfig
                    {
                        DungeonId = reader.GetInt32(0),
                        ScopeType = ScopeFromDatabase(reader.GetString(1)),
                        LimitCount = reader.GetInt32(2),
                        SortOrder = reader.GetInt32(3),
                    };
                    return true;
                }
            }
        }

        private static bool TryLoadSpecialRecord(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int recordCharacterId,
            int dungeonId,
            int dayId,
            out MutableEntryLimitState state)
        {
            state = default;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT current_count, extra_count, used_count, day_id
FROM dungeon_limit_records
WHERE account_id = @accountId
  AND character_id = @characterId
  AND dgn_id = @dgnId;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@characterId", recordCharacterId);
                command.Parameters.AddWithValue("@dgnId", dungeonId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return false;

                    var recordDayId = reader.GetInt32(3);
                    if (recordDayId != dayId)
                        return false;

                    state = new MutableEntryLimitState
                    {
                        CurrentCount = reader.GetInt32(0),
                        ExtraCount = reader.GetInt32(1),
                        UsedCount = reader.GetInt32(2),
                        DayId = recordDayId,
                    };
                    return true;
                }
            }
        }

        private static void UpsertSpecialRecord(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int recordCharacterId,
            int dungeonId,
            MutableEntryLimitState state)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO dungeon_limit_records (
    account_id, character_id, dgn_id, day_id,
    current_count, extra_count, used_count, updated_at
) VALUES (
    @accountId, @characterId, @dgnId, @dayId,
    @currentCount, @extraCount, @usedCount, CURRENT_TIMESTAMP
)
ON CONFLICT(account_id, character_id, dgn_id) DO UPDATE SET
    day_id = excluded.day_id,
    current_count = excluded.current_count,
    extra_count = excluded.extra_count,
    used_count = excluded.used_count,
    updated_at = CURRENT_TIMESTAMP;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@characterId", recordCharacterId);
                command.Parameters.AddWithValue("@dgnId", dungeonId);
                command.Parameters.AddWithValue("@dayId", state.DayId);
                command.Parameters.AddWithValue("@currentCount", state.CurrentCount);
                command.Parameters.AddWithValue("@extraCount", state.ExtraCount);
                command.Parameters.AddWithValue("@usedCount", state.UsedCount);
                command.ExecuteNonQuery();
            }
        }

        private static bool TryLoadDimensionGateRecord(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int dayId,
            out MutableEntryLimitState state)
        {
            state = default;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT current_count, extra_count, used_count, day_id
FROM character_dimensiongate_records
WHERE character_id = @characterId;";
                command.Parameters.AddWithValue("@characterId", characterId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return false;

                    var recordDayId = reader.GetInt32(3);
                    if (recordDayId != dayId)
                        return false;

                    state = new MutableEntryLimitState
                    {
                        CurrentCount = reader.GetInt32(0),
                        ExtraCount = reader.GetInt32(1),
                        UsedCount = reader.GetInt32(2),
                        DayId = recordDayId,
                    };
                    return true;
                }
            }
        }

        private static void UpsertDimensionGateRecord(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            MutableEntryLimitState state)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO character_dimensiongate_records (
    character_id, day_id, current_count, extra_count,
    used_count, updated_at
) VALUES (
    @characterId, @dayId, @currentCount, @extraCount,
    @usedCount, CURRENT_TIMESTAMP
)
ON CONFLICT(character_id) DO UPDATE SET
    day_id = excluded.day_id,
    current_count = excluded.current_count,
    extra_count = excluded.extra_count,
    used_count = excluded.used_count,
    updated_at = CURRENT_TIMESTAMP;";
                command.Parameters.AddWithValue("@characterId", characterId);
                command.Parameters.AddWithValue("@dayId", state.DayId);
                command.Parameters.AddWithValue("@currentCount", state.CurrentCount);
                command.Parameters.AddWithValue("@extraCount", state.ExtraCount);
                command.Parameters.AddWithValue("@usedCount", state.UsedCount);
                command.ExecuteNonQuery();
            }
        }

        private sealed class SpecialDungeonConfig
        {
            internal int DungeonId { get; set; }

            internal DungeonLimitScopeType ScopeType { get; set; }

            internal int LimitCount { get; set; }

            internal int SortOrder { get; set; }
        }

        private struct MutableEntryLimitState
        {
            internal int CurrentCount;

            internal int ExtraCount;

            internal int UsedCount;

            internal int DayId;
        }
    }
}
