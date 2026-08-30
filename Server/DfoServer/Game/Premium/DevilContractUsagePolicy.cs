using DfoServer.Game.DailyReset;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Premium
{
    /// <summary>
    /// 魔王契约中带每日次数上限的服务。次数按角色、北京时间 06:00 切日，
    /// 并可与实际业务写入放进同一 SQLite 事务。
    /// </summary>
    public sealed class DevilContractUsagePolicy
    {
        public const int GoldCardSlot = 0;
        public const int DoubleLotterySlot = 1;
        public const int QuestAssistantSlot = 2;
        public const int DungeonBuffSlot = 3;
        public const int HpMpRecoverySlot = 4;
        public const int WeaknessRecoverySlot = 5;
        public const int AutoRepairSlot = 6;
        public const int EfficientLotterySlot = 7;

        public const int GoldCardDailyLimit = 10;
        public const int DoubleLotteryDailyLimit = 8;
        public const int DungeonBuffDailyLimit = 10;
        public const int WeaknessRecoveryDailyLimit = 10;
        public const int AutoRepairDailyLimit = 6;

        public const string GoldCardCounterKey = "devil_contract_gold_card_used";
        public const string DoubleLotteryCounterKey = "lottery_double_reward_used";
        public const string DungeonBuffCounterKey = "devil_contract_dungeon_buff_used";
        public const string WeaknessRecoveryCounterKey = "devil_contract_weakness_recovery_used";
        public const string AutoRepairCounterKey = "devil_contract_auto_repair_used";

        private readonly IGameDatabase _database;
        private readonly DailyResetService _dailyReset;

        public DevilContractUsagePolicy(
            IGameDatabase database,
            DailyResetService dailyReset = null)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _dailyReset = dailyReset ?? new DailyResetService(database);
        }

        public int GetUsedCount(int characterId, int slotIndex)
        {
            if (characterId <= 0
                || !TryGetDailyUsage(slotIndex, out var key, out var limit))
            {
                return 0;
            }

            return Clamp(_dailyReset.GetCounter(characterId, key), limit);
        }

        public bool HasAvailableBenefit(
            int characterId,
            int accountId,
            int slotIndex)
        {
            if (characterId <= 0
                || accountId <= 0
                || !TryGetDailyUsage(slotIndex, out var key, out var limit))
            {
                return false;
            }

            using (var connection = _database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var active = PremiumService.HasActiveDevilContract(
                    connection,
                    transaction,
                    accountId,
                    slotIndex);
                var used = active
                    ? _dailyReset.GetCounter(
                        connection,
                        transaction,
                        characterId,
                        key)
                    : limit;
                transaction.Commit();
                return active && used < limit;
            }
        }

        public bool TryConsume(
            int characterId,
            int accountId,
            int slotIndex)
        {
            using (var connection = _database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                var consumed = TryConsume(
                    connection,
                    transaction,
                    characterId,
                    accountId,
                    slotIndex);
                transaction.Commit();
                return consumed;
            }
        }

        internal bool TryConsume(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int accountId,
            int slotIndex)
        {
            if (connection == null
                || transaction == null
                || characterId <= 0
                || accountId <= 0
                || !TryGetDailyUsage(slotIndex, out var key, out var limit)
                || !PremiumService.HasActiveDevilContract(
                    connection,
                    transaction,
                    accountId,
                    slotIndex))
            {
                return false;
            }

            return _dailyReset.TryIncrementCounter(
                connection,
                transaction,
                characterId,
                key,
                limit);
        }

        public IReadOnlyDictionary<int, int> BuildPremiumServiceUsage(
            int characterId)
        {
            var usage = new Dictionary<int, int>();
            if (characterId <= 0)
                return usage;

            using (var connection = _database.OpenConnection())
            using (var transaction = connection.BeginTransaction())
            {
                for (var slotIndex = 0;
                     slotIndex < DevilContractCatalog.SlotCount;
                     slotIndex++)
                {
                    if (!TryGetDailyUsage(
                            slotIndex,
                            out var key,
                            out var limit))
                    {
                        continue;
                    }

                    usage[slotIndex] = Clamp(
                        _dailyReset.GetCounter(
                            connection,
                            transaction,
                            characterId,
                            key),
                        limit);
                }
                transaction.Commit();
            }
            return usage;
        }

        internal static bool TryGetDailyUsage(
            int slotIndex,
            out string counterKey,
            out int dailyLimit)
        {
            switch (slotIndex)
            {
                case GoldCardSlot:
                    counterKey = GoldCardCounterKey;
                    dailyLimit = GoldCardDailyLimit;
                    return true;
                case DoubleLotterySlot:
                    counterKey = DoubleLotteryCounterKey;
                    dailyLimit = DoubleLotteryDailyLimit;
                    return true;
                case DungeonBuffSlot:
                    counterKey = DungeonBuffCounterKey;
                    dailyLimit = DungeonBuffDailyLimit;
                    return true;
                case WeaknessRecoverySlot:
                    counterKey = WeaknessRecoveryCounterKey;
                    dailyLimit = WeaknessRecoveryDailyLimit;
                    return true;
                case AutoRepairSlot:
                    counterKey = AutoRepairCounterKey;
                    dailyLimit = AutoRepairDailyLimit;
                    return true;
                default:
                    counterKey = null;
                    dailyLimit = 0;
                    return false;
            }
        }

        private static int Clamp(long count, int limit)
            => (int)Math.Max(0, Math.Min(limit, count));
    }
}
