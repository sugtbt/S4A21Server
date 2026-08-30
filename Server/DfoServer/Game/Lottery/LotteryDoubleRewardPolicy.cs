using DfoServer.Game.DailyReset;
using DfoServer.Game.Premium;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Lottery
{
    public sealed class LotteryDoubleRewardPolicy
    {
        public const int PremiumServiceSlot = DevilContractUsagePolicy.DoubleLotterySlot;
        public const int DailyLimit = DevilContractUsagePolicy.DoubleLotteryDailyLimit;
        public const string CounterKey = DevilContractUsagePolicy.DoubleLotteryCounterKey;

        private readonly DailyResetService _dailyResetService;
        private readonly string _connectionString;

        public LotteryDoubleRewardPolicy(DailyResetService dailyResetService, string connectionString)
        {
            _dailyResetService = dailyResetService
                ?? throw new ArgumentNullException(nameof(dailyResetService));
            _connectionString = connectionString
                ?? throw new ArgumentNullException(nameof(connectionString));
        }

        public int GetUsedCount(int characterId)
        {
            if (characterId <= 0)
                return 0;

            return Clamp(_dailyResetService.GetCounter(characterId, CounterKey));
        }

        public bool HasActiveBenefit(int accountId)
        {
            if (accountId <= 0)
                return false;

            var premiumType = DevilContractCatalog.SlotToPremiumType(PremiumServiceSlot);
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                return HasActiveBenefit(connection, null, accountId, premiumType);
            }
        }

        internal int GetUsedCount(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            if (connection == null || characterId <= 0)
                return 0;

            return Clamp(_dailyResetService.GetCounter(
                connection,
                transaction,
                characterId,
                CounterKey));
        }

        internal bool TryConsume(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int accountId)
        {
            if (connection == null
                || characterId <= 0
                || accountId <= 0)
            {
                return false;
            }

            var premiumType = DevilContractCatalog.SlotToPremiumType(PremiumServiceSlot);
            if (!HasActiveBenefit(
                    connection,
                    transaction,
                    accountId,
                    premiumType))
            {
                return false;
            }

            return _dailyResetService.TryIncrementCounter(
                connection,
                transaction,
                characterId,
                CounterKey,
                DailyLimit);
        }

        public IReadOnlyDictionary<int, int> BuildPremiumServiceUsage(int characterId)
        {
            return new DevilContractUsagePolicy(
                GameDatabase.AttachInitialized(_connectionString),
                _dailyResetService).BuildPremiumServiceUsage(characterId);
        }

        private static bool HasActiveBenefit(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int accountId,
            int premiumType)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT 1
FROM account_premiums
WHERE account_id=@accountId
  AND premium_type=@premiumType
  AND end_time>@now
LIMIT 1;";
                command.Parameters.AddWithValue("@accountId", accountId);
                command.Parameters.AddWithValue("@premiumType", premiumType);
                command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                return command.ExecuteScalar() != null;
            }
        }

        private static int Clamp(long count)
            => (int)Math.Max(0, Math.Min(DailyLimit, count));
    }
}
