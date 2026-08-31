using System;
using System.Collections.Generic;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Events.RecommendedDungeons;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Events.TotalAttendance
{
    internal sealed class TotalAttendanceService
    {
        private readonly IGameDatabase _database;
        private readonly MailboxService _mailbox;
        private readonly TotalAttendanceConfigProvider _configProvider;
        private readonly TotalAttendanceConfig _configOverride;
        private readonly TotalAttendanceRepository _repository;
        private readonly RecommendDungeonClearStatsRepository _recommendStats;
        private readonly Func<DateTimeOffset> _nowProvider;

        internal TotalAttendanceService(
            IGameDatabase database,
            MailboxService mailbox,
            TotalAttendanceConfigProvider configProvider = null,
            TotalAttendanceConfig config = null,
            Func<DateTimeOffset> nowProvider = null,
            RecommendDungeonClearStatsRepository recommendStats = null)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
            _configProvider = configProvider ?? TotalAttendanceConfigProvider.Instance;
            _configOverride = config;
            _repository = new TotalAttendanceRepository(_database);
            _recommendStats = recommendStats
                ?? new RecommendDungeonClearStatsRepository(_database);
            _nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
        }

        private TotalAttendanceConfig CurrentConfig =>
            _configOverride ?? _configProvider.Current;

        internal void Initialize()
        {
            _recommendStats.EnsureSchema();
            _repository.EnsureStaticConfigRows(CurrentConfig);
        }

        internal bool TryGetSnapshot(
            int accountId,
            int characterId,
            out TotalAttendanceSnapshot snapshot)
        {
            snapshot = null;
            if (accountId <= 0 || characterId <= 0)
                return false;

            try
            {
                var now = _nowProvider();
                var utcNow = now.UtcDateTime;
                var weekId = DailyResetService.WeekId(utcNow);
                var nowUnix = now.ToUniversalTime().ToUnixTimeSeconds();
                TotalAttendanceSnapshot local = null;
                _database.Write((connection, transaction) =>
                {
                    var config = CurrentConfig;
                    if (!_repository.IsEnabled(connection, transaction))
                        return;

                    var weeklyRecommendClearCount =
                        _recommendStats.LoadWeeklyCount(
                            connection,
                            transaction,
                            accountId,
                            weekId);
                    local = _repository.LoadSnapshot(
                        connection,
                        transaction,
                        accountId,
                        characterId,
                        config,
                        weekId,
                        weeklyRecommendClearCount,
                        nowUnix,
                        eventEnabled: true);
                });

                snapshot = local;
                return snapshot != null;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[TotalAttendance] snapshot failed "
                    + $"account_id={accountId} cid={characterId}: {ex}");
                return false;
            }
        }

        internal TotalAttendanceClearResult ApplyRecommendedDungeonClear(
            int accountId,
            int characterId,
            int dungeonId,
            int weeklyRecommendClearCount,
            Guid sourceEventId)
        {
            if (accountId <= 0 || characterId <= 0)
            {
                return new TotalAttendanceClearResult
                {
                    Status = TotalAttendanceClearStatus.CharacterUnavailable,
                };
            }

            try
            {
                var now = _nowProvider();
                var utcNow = now.UtcDateTime;
                var weekId = DailyResetService.WeekId(utcNow);
                var nowUnix = now.ToUniversalTime().ToUnixTimeSeconds();
                TotalAttendanceClearResult result = null;
                _database.Write((connection, transaction) =>
                {
                    var config = CurrentConfig;
                    if (!_repository.IsEnabled(connection, transaction))
                    {
                        result = new TotalAttendanceClearResult
                        {
                            Status = TotalAttendanceClearStatus.EventClosed,
                        };
                        return;
                    }

                    _repository.EnsureStateRows(
                        connection,
                        transaction,
                        accountId,
                        config,
                        weekId,
                        nowUnix);
                    var account = _repository.LoadAccountProgress(
                        connection,
                        transaction,
                        accountId,
                        config);
                    if (account.TotalAttendanceWeekCount
                        >= config.EventDurationWeeks)
                    {
                        result = WithSnapshot(
                            connection,
                            transaction,
                            accountId,
                            characterId,
                            config,
                            weekId,
                            weeklyRecommendClearCount,
                            nowUnix,
                            TotalAttendanceClearStatus
                                .AttendanceLimitReached);
                        return;
                    }

                    var weekly = _repository.LoadWeeklyProgress(
                        connection,
                        transaction,
                        accountId,
                        config,
                        weekId);
                    if (weekly.Checked)
                    {
                        result = WithSnapshot(
                            connection,
                            transaction,
                            accountId,
                            characterId,
                            config,
                            weekId,
                            weeklyRecommendClearCount,
                            nowUnix,
                            TotalAttendanceClearStatus.AlreadyChecked);
                        return;
                    }

                    var target = _repository.LoadRecommendClearTarget(
                        connection,
                        transaction,
                        config);
                    result = WithSnapshot(
                        connection,
                        transaction,
                        accountId,
                        characterId,
                        config,
                        weekId,
                        weeklyRecommendClearCount,
                        nowUnix,
                        weeklyRecommendClearCount >= target
                            ? TotalAttendanceClearStatus.ReadyToCheck
                            : TotalAttendanceClearStatus.Progressed);
                });

                LogClearResult(
                    result,
                    accountId,
                    characterId,
                    dungeonId,
                    sourceEventId);
                return result ?? new TotalAttendanceClearResult
                {
                    Status = TotalAttendanceClearStatus.PersistenceFailed,
                };
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[TotalAttendance] recommended clear failed "
                    + $"account_id={accountId} cid={characterId} "
                    + $"dungeon={dungeonId} event={sourceEventId:N}: {ex}");
                return new TotalAttendanceClearResult
                {
                    Status = TotalAttendanceClearStatus.PersistenceFailed,
                };
            }
        }

        internal TotalAttendanceCheckResult CheckThisWeek(
            int accountId,
            int characterId,
            string characterName,
            int characterLevel)
        {
            if (accountId <= 0 || characterId <= 0)
            {
                return new TotalAttendanceCheckResult
                {
                    Status = TotalAttendanceCheckStatus.CharacterUnavailable,
                };
            }

            try
            {
                var now = _nowProvider();
                var utcNow = now.UtcDateTime;
                var weekId = DailyResetService.WeekId(utcNow);
                var nowUnix = now.ToUniversalTime().ToUnixTimeSeconds();
                TotalAttendanceCheckResult result = null;
                _database.Write((connection, transaction) =>
                {
                    var config = CurrentConfig;
                    if (!_repository.IsEnabled(connection, transaction))
                    {
                        result = new TotalAttendanceCheckResult
                        {
                            Status = TotalAttendanceCheckStatus.EventClosed,
                        };
                        return;
                    }

                    _repository.EnsureStateRows(
                        connection,
                        transaction,
                        accountId,
                        config,
                        weekId,
                        nowUnix);
                    var weeklyRecommendClearCount =
                        _recommendStats.LoadWeeklyCount(
                            connection,
                            transaction,
                            accountId,
                            weekId);
                    var target = _repository.LoadRecommendClearTarget(
                        connection,
                        transaction,
                        config);
                    var account = _repository.LoadAccountProgress(
                        connection,
                        transaction,
                        accountId,
                        config);
                    if (account.TotalAttendanceWeekCount
                        >= config.EventDurationWeeks)
                    {
                        result = WithCheckSnapshot(
                            connection,
                            transaction,
                            accountId,
                            characterId,
                            config,
                            weekId,
                            weeklyRecommendClearCount,
                            nowUnix,
                            TotalAttendanceCheckStatus
                                .AttendanceLimitReached);
                        return;
                    }

                    var weekly = _repository.LoadWeeklyProgress(
                        connection,
                        transaction,
                        accountId,
                        config,
                        weekId);
                    if (weekly.Checked)
                    {
                        result = WithCheckSnapshot(
                            connection,
                            transaction,
                            accountId,
                            characterId,
                            config,
                            weekId,
                            weeklyRecommendClearCount,
                            nowUnix,
                            TotalAttendanceCheckStatus.AlreadyChecked);
                        return;
                    }

                    if (weeklyRecommendClearCount < target)
                    {
                        result = WithCheckSnapshot(
                            connection,
                            transaction,
                            accountId,
                            characterId,
                            config,
                            weekId,
                            weeklyRecommendClearCount,
                            nowUnix,
                            TotalAttendanceCheckStatus.NotReady);
                        return;
                    }

                    var nextWeekCount =
                        account.TotalAttendanceWeekCount + 1;
                    var weeklyReward =
                        config.GetWeeklyRewardByAttendanceCount(nextWeekCount);
                    if (weeklyReward == null)
                    {
                        throw new TotalAttendanceCheckRollbackException(
                            WithCheckSnapshot(
                                connection,
                                transaction,
                                accountId,
                                characterId,
                                config,
                                weekId,
                                weeklyRecommendClearCount,
                                nowUnix,
                                TotalAttendanceCheckStatus.RewardUnavailable));
                    }

                    var totalRewardMaskToSet = ComputeNewTotalRewardMask(
                        config,
                        account.TotalAttendanceWeekCount,
                        nextWeekCount,
                        account.TotalRewardSentMask);
                    var mails = BuildRewardMails(
                        accountId,
                        characterId,
                        characterName,
                        characterLevel,
                        config,
                        weekId,
                        weeklyReward,
                        totalRewardMaskToSet);
                    var mailResult = _mailbox.SendSystemMails(
                        connection,
                        transaction,
                        mails);
                    if (!mailResult.Success)
                    {
                        throw new TotalAttendanceCheckRollbackException(
                            WithCheckSnapshot(
                                connection,
                                transaction,
                                accountId,
                                characterId,
                                config,
                                weekId,
                                weeklyRecommendClearCount,
                                nowUnix,
                                TotalAttendanceCheckStatus.MailFailed));
                    }

                    if (!_repository.TryCompleteWeeklyAttendance(
                            connection,
                            transaction,
                            accountId,
                            config,
                            weekId,
                            nextWeekCount,
                            totalRewardMaskToSet,
                            nowUnix))
                    {
                        throw new InvalidOperationException(
                            "Total attendance completion was not recorded.");
                    }

                    result = WithCheckSnapshot(
                        connection,
                        transaction,
                        accountId,
                        characterId,
                        config,
                        weekId,
                        weeklyRecommendClearCount,
                        nowUnix,
                        TotalAttendanceCheckStatus.Checked,
                        mailDelivered: mails.Count > 0,
                        mailedRewardCount: mails.Count);
                });

                LogCheckResult(result, accountId, characterId);
                return result ?? new TotalAttendanceCheckResult
                {
                    Status = TotalAttendanceCheckStatus.PersistenceFailed,
                };
            }
            catch (TotalAttendanceCheckRollbackException ex)
            {
                return ex.Result ?? new TotalAttendanceCheckResult
                {
                    Status = TotalAttendanceCheckStatus.PersistenceFailed,
                };
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[TotalAttendance] check this week failed "
                    + $"account_id={accountId} cid={characterId}: {ex}");
                return new TotalAttendanceCheckResult
                {
                    Status = TotalAttendanceCheckStatus.PersistenceFailed,
                };
            }
        }

        private TotalAttendanceClearResult WithSnapshot(
            Microsoft.Data.Sqlite.SqliteConnection connection,
            Microsoft.Data.Sqlite.SqliteTransaction transaction,
            int accountId,
            int characterId,
            TotalAttendanceConfig config,
            int weekId,
            int weeklyRecommendClearCount,
            long nowUnix,
            TotalAttendanceClearStatus status)
        {
            return new TotalAttendanceClearResult
            {
                Status = status,
                Snapshot = _repository.LoadSnapshot(
                    connection,
                    transaction,
                    accountId,
                    characterId,
                    config,
                    weekId,
                    weeklyRecommendClearCount,
                    nowUnix,
                    eventEnabled: true),
            };
        }

        private TotalAttendanceCheckResult WithCheckSnapshot(
            Microsoft.Data.Sqlite.SqliteConnection connection,
            Microsoft.Data.Sqlite.SqliteTransaction transaction,
            int accountId,
            int characterId,
            TotalAttendanceConfig config,
            int weekId,
            int weeklyRecommendClearCount,
            long nowUnix,
            TotalAttendanceCheckStatus status,
            bool mailDelivered = false,
            int mailedRewardCount = 0)
        {
            return new TotalAttendanceCheckResult
            {
                Status = status,
                Snapshot = _repository.LoadSnapshot(
                    connection,
                    transaction,
                    accountId,
                    characterId,
                    config,
                    weekId,
                    weeklyRecommendClearCount,
                    nowUnix,
                    eventEnabled: true),
                MailDelivered = mailDelivered,
                MailedRewardCount = mailedRewardCount,
            };
        }

        private static int ComputeNewTotalRewardMask(
            TotalAttendanceConfig config,
            int previousWeekCount,
            int newWeekCount,
            int currentSentMask)
        {
            if (config?.TotalRewards == null)
                return 0;

            var mask = 0;
            foreach (var reward in config.TotalRewards)
            {
                if (reward == null)
                    continue;
                var bit = StageBit(reward.StageIndex);
                if (bit == 0 || (currentSentMask & bit) != 0)
                    continue;
                if (previousWeekCount < reward.RequiredAttendanceCount
                    && newWeekCount >= reward.RequiredAttendanceCount)
                {
                    mask |= bit;
                }
            }

            return mask & 0x07;
        }

        private static IReadOnlyList<MailboxSendRequest> BuildRewardMails(
            int accountId,
            int characterId,
            string characterName,
            int characterLevel,
            TotalAttendanceConfig config,
            int weekId,
            TotalAttendanceReward weeklyReward,
            int totalRewardMaskToSet)
        {
            var mails = new List<MailboxSendRequest>();
            mails.Add(CreateRewardMail(
                accountId,
                characterId,
                characterName,
                characterLevel,
                config.SeasonId,
                weekId,
                weeklyReward,
                "week",
                weeklyReward.RequiredAttendanceCount));

            foreach (var reward in config.TotalRewards)
            {
                if (reward == null
                    || (totalRewardMaskToSet & StageBit(reward.StageIndex)) == 0)
                {
                    continue;
                }

                mails.Add(CreateRewardMail(
                    accountId,
                    characterId,
                    characterName,
                    characterLevel,
                    config.SeasonId,
                    weekId,
                    reward,
                    "total",
                    reward.RequiredAttendanceCount));
            }

            return mails;
        }

        private static MailboxSendRequest CreateRewardMail(
            int accountId,
            int characterId,
            string characterName,
            int characterLevel,
            int seasonId,
            int weekId,
            TotalAttendanceReward reward,
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
                Title = "Weekly attendance reward",
                Text = "Weekly attendance reward has been delivered.",
                MailType = 1,
                SourceProtocol = (ushort)DfoServer.Network.NotiPacketTypeA21
                    .EVENT_TOTAL_ATTENDANCE,
                Unlimited = true,
                IdempotencyKey =
                    $"event-total-attendance:{seasonId}:{weekId}:"
                    + $"{accountId}:{rewardKind}:{rewardIndex}",
                AuditActor = "event-total-attendance",
                AuditReason =
                    $"totalattendance {rewardKind} reward {rewardIndex}",
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

        private static void LogClearResult(
            TotalAttendanceClearResult result,
            int accountId,
            int characterId,
            int dungeonId,
            Guid sourceEventId)
        {
            if (result == null)
                return;
            if (result.Status != TotalAttendanceClearStatus.Progressed
                && result.Status != TotalAttendanceClearStatus.ReadyToCheck)
            {
                return;
            }

            FileLogger.Log(
                "[TotalAttendance] recommended clear "
                + $"account_id={accountId} cid={characterId} "
                + $"dungeon={dungeonId} event={sourceEventId:N} "
                + $"status={result.Status} "
                + $"total={result.Snapshot?.TotalAttendanceWeekCount ?? 0} "
                + $"week={result.Snapshot?.ThisWeekRecommendClearCount ?? 0}/"
                + $"{result.Snapshot?.RecommendClearTarget ?? 0}");
        }

        private static void LogCheckResult(
            TotalAttendanceCheckResult result,
            int accountId,
            int characterId)
        {
            if (result == null || result.Status != TotalAttendanceCheckStatus.Checked)
                return;

            FileLogger.Log(
                "[TotalAttendance] check this week "
                + $"account_id={accountId} cid={characterId} "
                + $"total={result.Snapshot?.TotalAttendanceWeekCount ?? 0} "
                + $"mask={result.Snapshot?.TotalRewardSentMask ?? 0} "
                + $"mails={result.MailedRewardCount}");
        }

        private sealed class TotalAttendanceCheckRollbackException
            : Exception
        {
            internal TotalAttendanceCheckRollbackException(
                TotalAttendanceCheckResult result)
                : base(result?.Status.ToString())
            {
                Result = result;
            }

            internal TotalAttendanceCheckResult Result { get; }
        }
    }
}
