using System;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Events.RecommendedDungeons;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Events.DailyAttendanceAnytime
{
    internal sealed class DailyAttendanceAnytimeService
    {
        private readonly IGameDatabase _database;
        private readonly MailboxService _mailbox;
        private readonly DailyAttendanceAnytimeConfigProvider _configProvider;
        private readonly DailyAttendanceAnytimeConfig _configOverride;
        private readonly DailyAttendanceAnytimeRepository _repository;
        private readonly RecommendDungeonClearStatsRepository _recommendStats;
        private readonly Func<DateTimeOffset> _nowProvider;

        internal DailyAttendanceAnytimeService(
            IGameDatabase database,
            MailboxService mailbox,
            DailyAttendanceAnytimeConfigProvider configProvider = null,
            DailyAttendanceAnytimeConfig config = null,
            Func<DateTimeOffset> nowProvider = null,
            RecommendDungeonClearStatsRepository recommendStats = null)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
            _configProvider = configProvider
                ?? DailyAttendanceAnytimeConfigProvider.Instance;
            _configOverride = config;
            _repository = new DailyAttendanceAnytimeRepository(_database);
            _recommendStats = recommendStats
                ?? new RecommendDungeonClearStatsRepository(_database);
            _nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
        }

        private DailyAttendanceAnytimeConfig CurrentConfig =>
            _configOverride ?? _configProvider.Current;

        internal void Initialize()
        {
            _recommendStats.EnsureSchema();
            _repository.EnsureStaticConfigRows(CurrentConfig);
        }

        internal bool TryGetSnapshot(
            int accountId,
            int characterId,
            out DailyAttendanceAnytimeSnapshot snapshot)
        {
            snapshot = null;
            if (accountId <= 0 || characterId <= 0)
                return false;

            try
            {
                var now = NormalizeUtc(_nowProvider());
                var nowUnix = new DateTimeOffset(now, TimeSpan.Zero)
                    .ToUnixTimeSeconds();
                var dayId = DailyResetService.TodayId(now);
                DailyAttendanceAnytimeSnapshot local = null;
                _database.Write((connection, transaction) =>
                {
                    var config = CurrentConfig;
                    if (!_repository.IsEnabled(connection, transaction))
                        return;

                    var todayRecommendClearCount =
                        _recommendStats.LoadDailyCount(
                            connection,
                            transaction,
                            accountId,
                            dayId);
                    local = _repository.LoadSnapshot(
                        connection,
                        transaction,
                        accountId,
                        characterId,
                        config,
                        dayId,
                        todayRecommendClearCount,
                        nowUnix,
                        eventEnabled: true);
                });

                snapshot = local;
                return snapshot != null;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[DailyAttendanceAnytime] snapshot failed "
                    + $"account_id={accountId} cid={characterId}: {ex}");
                return false;
            }
        }

        internal DailyAttendanceAnytimeClearResult ApplyRecommendedDungeonClear(
            int accountId,
            int characterId,
            string characterName,
            int characterLevel,
            int dungeonId,
            int todayRecommendClearCount,
            Guid sourceEventId)
        {
            if (accountId <= 0 || characterId <= 0)
            {
                return new DailyAttendanceAnytimeClearResult
                {
                    Status = DailyAttendanceAnytimeClearStatus
                        .CharacterUnavailable,
                };
            }

            try
            {
                var now = _nowProvider();
                var utcNow = NormalizeUtc(now);
                var dayId = DailyResetService.TodayId(utcNow);
                var nowUnix = now.ToUniversalTime().ToUnixTimeSeconds();
                DailyAttendanceAnytimeClearResult result = null;
                _database.Write((connection, transaction) =>
                {
                    var config = CurrentConfig;
                    var enabled = _repository.IsEnabled(connection, transaction);
                    if (!enabled)
                    {
                        result = new DailyAttendanceAnytimeClearResult
                        {
                            Status = DailyAttendanceAnytimeClearStatus
                                .EventClosed,
                        };
                        return;
                    }

                    _repository.EnsureStateRows(
                        connection,
                        transaction,
                        accountId,
                        config,
                        dayId,
                        nowUnix);
                    var account = _repository.LoadAccountProgress(
                        connection,
                        transaction,
                        accountId,
                        config);
                    if (account.TotalAttendanceCount >= config.MaxAttendanceDays)
                    {
                        result = WithSnapshot(
                            connection,
                            transaction,
                            accountId,
                            characterId,
                            config,
                            dayId,
                            todayRecommendClearCount,
                            nowUnix,
                            DailyAttendanceAnytimeClearStatus
                                .AttendanceLimitReached);
                        return;
                    }

                    var target = _repository.LoadRecommendClearTarget(
                        connection,
                        transaction);
                    var visibleRecommendClearCount = Math.Min(
                        target,
                        Math.Max(0, todayRecommendClearCount));
                    var daily = _repository.LoadDailyProgress(
                        connection,
                        transaction,
                        accountId,
                        config,
                        dayId);
                    if (daily.Attended)
                    {
                        result = WithSnapshot(
                            connection,
                            transaction,
                            accountId,
                            characterId,
                            config,
                            dayId,
                            visibleRecommendClearCount,
                            nowUnix,
                            DailyAttendanceAnytimeClearStatus.AlreadyAttended);
                        return;
                    }

                    if (visibleRecommendClearCount < target)
                    {
                        _repository.TrySetRecommendClearCount(
                            connection,
                            transaction,
                            accountId,
                            config,
                            dayId,
                            visibleRecommendClearCount,
                            nowUnix);
                        result = WithSnapshot(
                            connection,
                            transaction,
                            accountId,
                            characterId,
                            config,
                            dayId,
                            visibleRecommendClearCount,
                            nowUnix,
                            DailyAttendanceAnytimeClearStatus.Progressed);
                        return;
                    }

                    var rewardDayIndex = account.TotalAttendanceCount;
                    var reward = config.GetDailyRewardByDayIndex(rewardDayIndex);
                    if (reward == null)
                    {
                        throw new DailyAttendanceAnytimeRollbackException(
                            WithSnapshot(
                                connection,
                                transaction,
                                accountId,
                                characterId,
                                config,
                                dayId,
                                visibleRecommendClearCount,
                                nowUnix,
                                DailyAttendanceAnytimeClearStatus
                                    .RewardUnavailable));
                    }

                    var mail = CreateRewardMail(
                        accountId,
                        characterId,
                        characterName,
                        characterLevel,
                        config.SeasonId,
                        dayId,
                        reward,
                        "daily",
                        rewardDayIndex);
                    var mailResult = _mailbox.SendSystemMails(
                        connection,
                        transaction,
                        new[] { mail });
                    if (!mailResult.Success)
                    {
                        throw new DailyAttendanceAnytimeRollbackException(
                            WithSnapshot(
                                connection,
                                transaction,
                                accountId,
                                characterId,
                                config,
                                dayId,
                                visibleRecommendClearCount,
                                nowUnix,
                                DailyAttendanceAnytimeClearStatus.MailFailed));
                    }

                    if (!_repository.TryCompleteDailyAttendance(
                            connection,
                            transaction,
                            accountId,
                            config,
                            dayId,
                            target,
                            rewardDayIndex,
                            ComputeNewAccumulateClaimMask(
                                config,
                                account.TotalAttendanceCount,
                                account.TotalAttendanceCount + 1),
                            nowUnix))
                    {
                        throw new InvalidOperationException(
                            "Daily attendance completion was not recorded.");
                    }

                    result = WithSnapshot(
                        connection,
                        transaction,
                        accountId,
                        characterId,
                        config,
                        dayId,
                        visibleRecommendClearCount,
                        nowUnix,
                        DailyAttendanceAnytimeClearStatus.Attended,
                        mailDelivered: true);
                });

                LogClearResult(result, accountId, characterId, dungeonId, sourceEventId);
                return result ?? new DailyAttendanceAnytimeClearResult
                {
                    Status = DailyAttendanceAnytimeClearStatus
                        .PersistenceFailed,
                };
            }
            catch (DailyAttendanceAnytimeRollbackException ex)
            {
                return ex.Result ?? new DailyAttendanceAnytimeClearResult
                {
                    Status = DailyAttendanceAnytimeClearStatus
                        .PersistenceFailed,
                };
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[DailyAttendanceAnytime] recommended clear failed "
                    + $"account_id={accountId} cid={characterId} "
                    + $"dungeon={dungeonId} level={characterLevel} "
                    + $"event={sourceEventId:N}: {ex}");
                return new DailyAttendanceAnytimeClearResult
                {
                    Status = DailyAttendanceAnytimeClearStatus.PersistenceFailed,
                };
            }
        }

        internal DailyAttendanceAnytimeClaimResult ClaimAccumulateReward(
            int accountId,
            int characterId,
            string characterName,
            int characterLevel)
        {
            if (accountId <= 0 || characterId <= 0)
            {
                return new DailyAttendanceAnytimeClaimResult
                {
                    Status = DailyAttendanceAnytimeClaimStatus
                        .CharacterUnavailable,
                };
            }

            try
            {
                var now = _nowProvider();
                var utcNow = NormalizeUtc(now);
                var dayId = DailyResetService.TodayId(utcNow);
                var nowUnix = now.ToUniversalTime().ToUnixTimeSeconds();
                DailyAttendanceAnytimeClaimResult result = null;
                _database.Write((connection, transaction) =>
                {
                    var config = CurrentConfig;
                    var enabled = _repository.IsEnabled(connection, transaction);
                    if (!enabled)
                    {
                        result = new DailyAttendanceAnytimeClaimResult
                        {
                            Status = DailyAttendanceAnytimeClaimStatus
                                .EventClosed,
                        };
                        return;
                    }

                    _repository.EnsureStateRows(
                        connection,
                        transaction,
                        accountId,
                        config,
                        dayId,
                        nowUnix);
                    var account = _repository.LoadAccountProgress(
                        connection,
                        transaction,
                        accountId,
                        config);
                    var reward = FindFirstClaimableAccumulateReward(
                        config,
                        account.TotalAttendanceCount,
                        account.AccumulateClaimedMask);
                    if (reward == null)
                    {
                        result = WithClaimSnapshot(
                            connection,
                            transaction,
                            accountId,
                            characterId,
                            config,
                            dayId,
                            nowUnix,
                            DailyAttendanceAnytimeClaimStatus
                                .NoClaimableReward);
                        return;
                    }

                    var mail = CreateRewardMail(
                        accountId,
                        characterId,
                        characterName,
                        characterLevel,
                        config.SeasonId,
                        dayId,
                        reward,
                        "accumulate",
                        reward.StageIndex);
                    var mailResult = _mailbox.SendSystemMails(
                        connection,
                        transaction,
                        new[] { mail });
                    if (!mailResult.Success)
                    {
                        throw new DailyAttendanceAnytimeClaimRollbackException(
                            WithClaimSnapshot(
                                connection,
                                transaction,
                                accountId,
                                characterId,
                                config,
                                dayId,
                                nowUnix,
                                DailyAttendanceAnytimeClaimStatus.MailFailed,
                                reward));
                    }

                    if (!_repository.TryConsumeAccumulateClaimMask(
                            connection,
                            transaction,
                            accountId,
                            config,
                            reward,
                            nowUnix))
                    {
                        throw new DailyAttendanceAnytimeClaimRollbackException(
                            WithClaimSnapshot(
                                connection,
                                transaction,
                                accountId,
                                characterId,
                                config,
                                dayId,
                                nowUnix,
                                DailyAttendanceAnytimeClaimStatus
                                    .NoClaimableReward));
                    }

                    result = WithClaimSnapshot(
                        connection,
                        transaction,
                        accountId,
                        characterId,
                        config,
                        dayId,
                        nowUnix,
                        DailyAttendanceAnytimeClaimStatus.Claimed,
                        reward,
                        mailDelivered: true);
                });

                LogClaimResult(result, accountId, characterId);
                return result ?? new DailyAttendanceAnytimeClaimResult
                {
                    Status = DailyAttendanceAnytimeClaimStatus
                        .PersistenceFailed,
                };
            }
            catch (DailyAttendanceAnytimeClaimRollbackException ex)
            {
                return ex.Result ?? new DailyAttendanceAnytimeClaimResult
                {
                    Status = DailyAttendanceAnytimeClaimStatus
                        .PersistenceFailed,
                };
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[DailyAttendanceAnytime] accumulate claim failed "
                    + $"account_id={accountId} cid={characterId}: {ex}");
                return new DailyAttendanceAnytimeClaimResult
                {
                    Status = DailyAttendanceAnytimeClaimStatus.PersistenceFailed,
                };
            }
        }

        private DailyAttendanceAnytimeClearResult WithSnapshot(
            Microsoft.Data.Sqlite.SqliteConnection connection,
            Microsoft.Data.Sqlite.SqliteTransaction transaction,
            int accountId,
            int characterId,
            DailyAttendanceAnytimeConfig config,
            int dayId,
            int todayRecommendClearCount,
            long nowUnix,
            DailyAttendanceAnytimeClearStatus status,
            bool mailDelivered = false)
        {
            return new DailyAttendanceAnytimeClearResult
            {
                Status = status,
                Snapshot = _repository.LoadSnapshot(
                    connection,
                    transaction,
                    accountId,
                    characterId,
                    config,
                    dayId,
                    todayRecommendClearCount,
                    nowUnix,
                    eventEnabled: true),
                MailDelivered = mailDelivered,
            };
        }

        private DailyAttendanceAnytimeClaimResult WithClaimSnapshot(
            Microsoft.Data.Sqlite.SqliteConnection connection,
            Microsoft.Data.Sqlite.SqliteTransaction transaction,
            int accountId,
            int characterId,
            DailyAttendanceAnytimeConfig config,
            int dayId,
            long nowUnix,
            DailyAttendanceAnytimeClaimStatus status,
            DailyAttendanceAnytimeReward reward = null,
            bool mailDelivered = false)
        {
            var todayRecommendClearCount = _recommendStats.LoadDailyCount(
                connection,
                transaction,
                accountId,
                dayId);
            return new DailyAttendanceAnytimeClaimResult
            {
                Status = status,
                Snapshot = _repository.LoadSnapshot(
                    connection,
                    transaction,
                    accountId,
                    characterId,
                    config,
                    dayId,
                    todayRecommendClearCount,
                    nowUnix,
                    eventEnabled: true),
                MailDelivered = mailDelivered,
                ClaimedStageIndex = reward?.StageIndex ?? -1,
                ItemId = reward?.ItemId ?? 0,
                ItemCount = reward?.ItemCount ?? 0,
            };
        }

        private static DailyAttendanceAnytimeReward
            FindFirstClaimableAccumulateReward(
                DailyAttendanceAnytimeConfig config,
                int totalAttendanceCount,
                int claimedMask)
        {
            if (config?.AccumulateRewards == null)
                return null;

            foreach (var reward in config.AccumulateRewards)
            {
                if (reward == null)
                    continue;
                var bit = StageBit(reward.StageIndex);
                if (bit == 0)
                    continue;
                if (totalAttendanceCount >= reward.RequiredAttendanceCount
                    && (claimedMask & bit) != 0)
                {
                    return reward;
                }
            }

            return null;
        }

        private static int ComputeNewAccumulateClaimMask(
            DailyAttendanceAnytimeConfig config,
            int previousTotalAttendanceCount,
            int newTotalAttendanceCount)
        {
            if (config?.AccumulateRewards == null)
                return 0;

            var mask = 0;
            foreach (var reward in config.AccumulateRewards)
            {
                if (reward == null)
                    continue;
                if (previousTotalAttendanceCount < reward.RequiredAttendanceCount
                    && newTotalAttendanceCount >= reward.RequiredAttendanceCount)
                {
                    mask |= StageBit(reward.StageIndex);
                }
            }

            return mask & 0x07;
        }

        private static MailboxSendRequest CreateRewardMail(
            int accountId,
            int characterId,
            string characterName,
            int characterLevel,
            int seasonId,
            int dayId,
            DailyAttendanceAnytimeReward reward,
            string rewardKind,
            int rewardIndex)
        {
            return new MailboxSendRequest
            {
                SenderCharacterId = characterId,
                SenderAccountId = accountId,
                SenderName = "DNFadmin",
                ReceiverCharacterId = characterId,
                ReceiverAccountId = accountId,
                ReceiverName = characterName ?? string.Empty,
                SenderLevel = characterLevel,
                ReceiverLevel = characterLevel,
                Gold = 0,
                Title = "Daily attendance reward",
                Text = "Daily attendance reward has been delivered.",
                MailType = 1,
                SourceProtocol = (ushort)DfoServer.Network.NotiPacketTypeA21
                    .INTEGRATE_EVENT_DATA,
                Unlimited = true,
                IdempotencyKey =
                    $"event-daily-attendance-anytime:{seasonId}:{dayId}:"
                    + $"{accountId}:{rewardKind}:{rewardIndex}",
                AuditActor = "event-daily-attendance-anytime",
                AuditReason =
                    $"dailyattendanceanytime {rewardKind} reward {rewardIndex}",
                Attachments = new[]
                {
                    new MailboxSendAttachmentRequest
                    {
                        ItemType = ResolveMailboxItemType(reward.ItemId),
                        ItemId = reward.ItemId,
                        ItemCount = reward.ItemCount,
                    },
                },
            };
        }

        private static byte ResolveMailboxItemType(int itemId)
        {
            if (!ItemMetadataResolver.TryResolveItemKind(itemId, out var itemKind))
                return 0;

            switch (itemKind)
            {
                case ItemCore.KindAvatar:
                    return 1;
                case ItemCore.KindCreature:
                case ItemCore.KindCreatureEquipment:
                case ItemCore.KindCreatureConsumable:
                    return 3;
                default:
                    return 0;
            }
        }

        private static int StageBit(int stageIndex)
            => stageIndex >= 0 && stageIndex < 3 ? 1 << stageIndex : 0;

        private static DateTime NormalizeUtc(DateTimeOffset time)
            => time.UtcDateTime;

        private static void LogClearResult(
            DailyAttendanceAnytimeClearResult result,
            int accountId,
            int characterId,
            int dungeonId,
            Guid sourceEventId)
        {
            if (result == null)
                return;
            if (result.Status != DailyAttendanceAnytimeClearStatus.Progressed
                && result.Status != DailyAttendanceAnytimeClearStatus.Attended)
            {
                return;
            }

            FileLogger.Log(
                "[DailyAttendanceAnytime] recommended clear "
                + $"account_id={accountId} cid={characterId} "
                + $"dungeon={dungeonId} event={sourceEventId:N} "
                + $"status={result.Status} "
                + $"total={result.Snapshot?.TotalAttendanceCount ?? 0} "
                + $"today={result.Snapshot?.TodayRecommendClearCount ?? 0}/"
                + $"{result.Snapshot?.RecommendClearTarget ?? 0} "
                + $"mail={(result.MailDelivered ? 1 : 0)}");
        }

        private static void LogClaimResult(
            DailyAttendanceAnytimeClaimResult result,
            int accountId,
            int characterId)
        {
            if (result == null
                || result.Status != DailyAttendanceAnytimeClaimStatus.Claimed)
            {
                return;
            }

            FileLogger.Log(
                "[DailyAttendanceAnytime] accumulate claim "
                + $"account_id={accountId} cid={characterId} "
                + $"stage={result.ClaimedStageIndex} "
                + $"item={result.ItemId} count={result.ItemCount} "
                + $"total={result.Snapshot?.TotalAttendanceCount ?? 0} "
                + $"mask={result.Snapshot?.AccumulateClaimedMask ?? 0}");
        }

        private sealed class DailyAttendanceAnytimeRollbackException
            : Exception
        {
            internal DailyAttendanceAnytimeRollbackException(
                DailyAttendanceAnytimeClearResult result)
                : base(result?.Status.ToString())
            {
                Result = result;
            }

            internal DailyAttendanceAnytimeClearResult Result { get; }
        }

        private sealed class DailyAttendanceAnytimeClaimRollbackException
            : Exception
        {
            internal DailyAttendanceAnytimeClaimRollbackException(
                DailyAttendanceAnytimeClaimResult result)
                : base(result?.Status.ToString())
            {
                Result = result;
            }

            internal DailyAttendanceAnytimeClaimResult Result { get; }
        }
    }
}
