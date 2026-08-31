using System;
using System.Collections.Generic;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Events.PcRoomTimePoint
{
    internal sealed class PcRoomTimePointService
    {
        private sealed class SessionOnlineTracker
        {
            public Guid SessionId;
            public int AccountId;
            public int CharacterId;
        }

        private sealed class AccountOnlineTracker
        {
            public int AccountId;
            public DateTime LastFlushUtc;
            public HashSet<Guid> SessionIds { get; } = new HashSet<Guid>();
        }

        private readonly IGameDatabase _database;
        private readonly MailboxService _mailbox;
        private readonly PcRoomTimePointConfigProvider _configProvider;
        private readonly PcRoomTimePointConfig _configOverride;
        private readonly PcRoomTimePointRepository _repository;
        private readonly Func<DateTimeOffset> _nowProvider;
        private readonly object _sync = new object();
        private readonly Dictionary<Guid, SessionOnlineTracker> _sessionsById =
            new Dictionary<Guid, SessionOnlineTracker>();
        private readonly Dictionary<int, AccountOnlineTracker> _trackersByAccount =
            new Dictionary<int, AccountOnlineTracker>();

        internal PcRoomTimePointService(
            IGameDatabase database,
            MailboxService mailbox,
            PcRoomTimePointConfigProvider configProvider = null,
            PcRoomTimePointConfig config = null,
            Func<DateTimeOffset> nowProvider = null)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
            _configProvider = configProvider ?? PcRoomTimePointConfigProvider.Instance;
            _configOverride = config;
            _repository = new PcRoomTimePointRepository(_database);
            _nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
        }

        private PcRoomTimePointConfig CurrentConfig =>
            _configOverride ?? _configProvider.Current;

        internal void Initialize()
        {
            _repository.EnsureStaticConfigRows(CurrentConfig);
        }

        internal void BeginSession(
            Guid sessionId,
            int accountId,
            int characterId)
        {
            if (sessionId == Guid.Empty || accountId <= 0 || characterId <= 0)
                return;

            lock (_sync)
            {
                if (_sessionsById.TryGetValue(sessionId, out var existing))
                {
                    if (existing.AccountId == accountId)
                    {
                        existing.CharacterId = characterId;
                        return;
                    }

                    RemoveSessionWithoutFlushLocked(existing);
                }

                var nowUtc = NormalizeUtc(_nowProvider());
                if (!_trackersByAccount.TryGetValue(
                        accountId,
                        out var accountTracker))
                {
                    accountTracker = new AccountOnlineTracker
                    {
                        AccountId = accountId,
                        LastFlushUtc = nowUtc,
                    };
                    _trackersByAccount[accountId] = accountTracker;
                }

                accountTracker.SessionIds.Add(sessionId);
                _sessionsById[sessionId] = new SessionOnlineTracker
                {
                    SessionId = sessionId,
                    AccountId = accountId,
                    CharacterId = characterId,
                };
            }
        }

        internal void EndSession(Guid sessionId)
        {
            if (sessionId == Guid.Empty)
                return;

            lock (_sync)
            {
                if (!_sessionsById.TryGetValue(sessionId, out var tracker))
                    return;

                _sessionsById.Remove(sessionId);
                if (!_trackersByAccount.TryGetValue(
                        tracker.AccountId,
                        out var accountTracker))
                {
                    return;
                }

                accountTracker.SessionIds.Remove(sessionId);
                if (accountTracker.SessionIds.Count > 0)
                    return;

                try
                {
                    var now = _nowProvider();
                    _database.Write((connection, transaction) =>
                    {
                        var config = CurrentConfig;
                        if (_repository.IsEnabled(connection, transaction))
                        {
                            FlushElapsedLocked(
                                connection,
                                transaction,
                                accountTracker,
                                config,
                                now);
                        }
                    });
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        "[PcRoomTimePoint] end-session flush failed "
                        + $"account_id={tracker.AccountId}: {ex}");
                }
                finally
                {
                    _trackersByAccount.Remove(tracker.AccountId);
                }
            }
        }

        internal bool TryGetSnapshotForSession(
            Guid sessionId,
            int accountId,
            int characterId,
            out PcRoomTimePointSnapshot snapshot)
            => TryGetSnapshotForSessionAt(
                sessionId,
                accountId,
                characterId,
                NormalizeUtc(_nowProvider()),
                out snapshot);

        internal bool TryGetSnapshotForSessionAt(
            Guid sessionId,
            int accountId,
            int characterId,
            DateTime utcNow,
            out PcRoomTimePointSnapshot snapshot)
        {
            snapshot = null;
            if (sessionId == Guid.Empty || accountId <= 0 || characterId <= 0)
                return false;

            try
            {
                lock (_sync)
                {
                    EnsureSessionLocked(sessionId, accountId, characterId, utcNow);

                    PcRoomTimePointSnapshot local = null;
                    _database.Write((connection, transaction) =>
                    {
                        var config = CurrentConfig;
                        var enabled = _repository.IsEnabled(connection, transaction);
                        var tracker = _trackersByAccount[accountId];
                        if (!enabled)
                        {
                            tracker.LastFlushUtc = utcNow;
                            return;
                        }

                        FlushElapsedLocked(
                            connection,
                            transaction,
                            tracker,
                            config,
                            new DateTimeOffset(utcNow, TimeSpan.Zero));

                        local = _repository.LoadSnapshot(
                            connection,
                            transaction,
                            accountId,
                            characterId,
                            config,
                            TodayId(utcNow),
                            eventEnabled: true);
                    });

                    snapshot = local;
                    return snapshot != null;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[PcRoomTimePoint] snapshot failed "
                    + $"account_id={accountId} cid={characterId}: {ex}");
                return false;
            }
        }

        internal PcRoomTimePointClaimResult Claim(
            Guid sessionId,
            int accountId,
            int characterId,
            string characterName,
            int characterLevel,
            PcRoomTimePointClaimCommand command)
        {
            if (accountId <= 0 || characterId <= 0)
            {
                return new PcRoomTimePointClaimResult
                {
                    Status = PcRoomTimePointClaimStatus.CharacterUnavailable,
                };
            }

            if (command == null)
            {
                return new PcRoomTimePointClaimResult
                {
                    Status = PcRoomTimePointClaimStatus.InvalidRequest,
                };
            }

            try
            {
                lock (_sync)
                {
                    var now = _nowProvider();
                    var utcNow = NormalizeUtc(now);
                    EnsureSessionLocked(sessionId, accountId, characterId, utcNow);

                    PcRoomTimePointClaimResult result = null;
                    _database.Write((connection, transaction) =>
                    {
                        var config = CurrentConfig;
                        var enabled = _repository.IsEnabled(connection, transaction);
                        var accountTracker = _trackersByAccount[accountId];
                        if (!enabled)
                        {
                            accountTracker.LastFlushUtc = utcNow;
                            result = new PcRoomTimePointClaimResult
                            {
                                Status = PcRoomTimePointClaimStatus.EventClosed,
                            };
                            return;
                        }

                        FlushElapsedLocked(
                            connection,
                            transaction,
                            accountTracker,
                            config,
                            now);

                        var dayId = TodayId(utcNow);
                        var snapshot = _repository.LoadSnapshot(
                            connection,
                            transaction,
                            accountId,
                            characterId,
                            config,
                            dayId,
                            eventEnabled: true);

                        if (command.Kind == PcRoomTimePointRequestKind.Query)
                        {
                            result = new PcRoomTimePointClaimResult
                            {
                                Status = PcRoomTimePointClaimStatus.Query,
                                Snapshot = snapshot,
                            };
                            return;
                        }

                        var reward = ResolveReward(config, command);
                        if (reward == null)
                        {
                            result = new PcRoomTimePointClaimResult
                            {
                                Status = PcRoomTimePointClaimStatus.InvalidRequest,
                                Snapshot = snapshot,
                            };
                            return;
                        }

                        var bit = StageBit(command.StageIndex);
                        if (command.Kind == PcRoomTimePointRequestKind.DailyReward)
                        {
                            if ((snapshot.DailyClaimMask & bit) != 0)
                            {
                                result = AlreadyClaimed(snapshot);
                                return;
                            }

                            if ((snapshot.DailyAvailableMask & bit) == 0)
                            {
                                result = NotReady(snapshot);
                                return;
                            }
                        }
                        else
                        {
                            if ((snapshot.PeriodAvailableMask & bit) == 0)
                            {
                                result = NotReady(snapshot);
                                return;
                            }

                            if ((snapshot.PeriodClaimMask & bit) == 0)
                            {
                                result = AlreadyClaimed(snapshot);
                                return;
                            }
                        }

                        var mail = CreateRewardMail(
                            accountId,
                            characterId,
                            characterName,
                            characterLevel,
                            command,
                            reward,
                            config.SeasonId,
                            dayId);
                        var mailResult = _mailbox.SendSystemMails(
                            connection,
                            transaction,
                            new[] { mail });
                        if (!mailResult.Success)
                        {
                            result = new PcRoomTimePointClaimResult
                            {
                                Status = PcRoomTimePointClaimStatus.MailFailed,
                                Snapshot = snapshot,
                            };
                            return;
                        }

                        var claimed = command.Kind == PcRoomTimePointRequestKind.DailyReward
                            ? _repository.TrySetDailyClaimed(
                                connection,
                                transaction,
                                accountId,
                                config,
                                dayId,
                                command.StageIndex,
                                now.ToUnixTimeSeconds())
                            : _repository.TryClearPeriodClaimable(
                                connection,
                                transaction,
                                accountId,
                                config,
                                command.StageIndex,
                                now.ToUnixTimeSeconds());

                        snapshot = _repository.LoadSnapshot(
                            connection,
                            transaction,
                            accountId,
                            characterId,
                            config,
                            dayId,
                            eventEnabled: true);

                        result = new PcRoomTimePointClaimResult
                        {
                            Status = claimed
                                ? PcRoomTimePointClaimStatus.Success
                                : PcRoomTimePointClaimStatus.AlreadyClaimed,
                            Snapshot = snapshot,
                            MailDelivered = claimed,
                        };
                    });

                    return result ?? new PcRoomTimePointClaimResult
                    {
                        Status = PcRoomTimePointClaimStatus.InvalidRequest,
                    };
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    "[PcRoomTimePoint] claim failed "
                    + $"account_id={accountId} cid={characterId} "
                    + $"kind={command.Kind} stage={command.StageIndex}: {ex}");
                return new PcRoomTimePointClaimResult
                {
                    Status = PcRoomTimePointClaimStatus.MailFailed,
                };
            }
        }

        private void EnsureSessionLocked(
            Guid sessionId,
            int accountId,
            int characterId,
            DateTime utcNow)
        {
            if (!_sessionsById.TryGetValue(sessionId, out var sessionTracker))
            {
                sessionTracker = new SessionOnlineTracker
                {
                    SessionId = sessionId,
                    AccountId = accountId,
                    CharacterId = characterId,
                };
                _sessionsById[sessionId] = sessionTracker;
            }
            else if (sessionTracker.AccountId != accountId)
            {
                RemoveSessionWithoutFlushLocked(sessionTracker);
                sessionTracker = new SessionOnlineTracker
                {
                    SessionId = sessionId,
                    AccountId = accountId,
                    CharacterId = characterId,
                };
                _sessionsById[sessionId] = sessionTracker;
            }
            else
            {
                sessionTracker.CharacterId = characterId;
            }

            if (!_trackersByAccount.TryGetValue(accountId, out var accountTracker))
            {
                accountTracker = new AccountOnlineTracker
                {
                    AccountId = accountId,
                    LastFlushUtc = utcNow,
                };
                _trackersByAccount[accountId] = accountTracker;
            }

            accountTracker.SessionIds.Add(sessionId);
        }

        private void RemoveSessionWithoutFlushLocked(
            SessionOnlineTracker tracker)
        {
            _sessionsById.Remove(tracker.SessionId);
            if (!_trackersByAccount.TryGetValue(
                    tracker.AccountId,
                    out var accountTracker))
            {
                return;
            }

            accountTracker.SessionIds.Remove(tracker.SessionId);
            if (accountTracker.SessionIds.Count == 0)
                _trackersByAccount.Remove(tracker.AccountId);
        }

        private void FlushElapsedLocked(
            Microsoft.Data.Sqlite.SqliteConnection connection,
            Microsoft.Data.Sqlite.SqliteTransaction transaction,
            AccountOnlineTracker accountTracker,
            PcRoomTimePointConfig config,
            DateTimeOffset now)
        {
            var endUtc = NormalizeUtc(now);
            if (endUtc <= accountTracker.LastFlushUtc)
                return;

            var nowUnix = now.ToUnixTimeSeconds();
            var cursor = accountTracker.LastFlushUtc;
            while (cursor < endUtc)
            {
                var nextBoundary = NextDailyResetBoundaryAfter(cursor);
                var segmentEnd = nextBoundary < endUtc ? nextBoundary : endUtc;
                var deltaMillis = (long)(segmentEnd - cursor).TotalMilliseconds;
                if (deltaMillis > 0)
                {
                    _repository.AddOnlineMillis(
                        connection,
                        transaction,
                        accountTracker.AccountId,
                        config,
                        TodayId(cursor),
                        deltaMillis,
                        nowUnix);
                }

                cursor = segmentEnd;
            }

            accountTracker.LastFlushUtc = endUtc;
        }

        private static DateTime NextDailyResetBoundaryAfter(DateTime utcTime)
        {
            var boundary = DailyResetService.GetDailyResetBoundaryUtc(utcTime);
            return utcTime < boundary ? boundary : boundary.AddDays(1);
        }

        private static int TodayId(DateTime utcTime)
            => DailyResetService.TodayId(utcTime);

        private static DateTime NormalizeUtc(DateTimeOffset time)
            => time.UtcDateTime;

        private static PcRoomTimePointRewardStage ResolveReward(
            PcRoomTimePointConfig config,
            PcRoomTimePointClaimCommand command)
        {
            return command.Kind == PcRoomTimePointRequestKind.DailyReward
                ? config.GetDailyReward(command.StageIndex)
                : config.GetPeriodReward(command.StageIndex);
        }

        private static PcRoomTimePointClaimResult AlreadyClaimed(
            PcRoomTimePointSnapshot snapshot)
            => new PcRoomTimePointClaimResult
            {
                Status = PcRoomTimePointClaimStatus.AlreadyClaimed,
                Snapshot = snapshot,
            };

        private static PcRoomTimePointClaimResult NotReady(
            PcRoomTimePointSnapshot snapshot)
            => new PcRoomTimePointClaimResult
            {
                Status = PcRoomTimePointClaimStatus.NotReady,
                Snapshot = snapshot,
            };

        private static MailboxSendRequest CreateRewardMail(
            int accountId,
            int characterId,
            string characterName,
            int characterLevel,
            PcRoomTimePointClaimCommand command,
            PcRoomTimePointRewardStage reward,
            int seasonId,
            int dayId)
        {
            var kind = command.Kind == PcRoomTimePointRequestKind.DailyReward
                ? "daily"
                : "period";
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
                Title = "PC room time point reward",
                Text = "PC room time point reward has been delivered.",
                MailType = 1,
                SourceProtocol = (ushort)DfoServer.Network.CmdPacketTypeA21
                    .GET_PCROOM_TIME_POINT_ITEM,
                Unlimited = true,
                IdempotencyKey =
                    $"event-pcroom-timepoint:{seasonId}:{dayId}:"
                    + $"{accountId}:{kind}:{command.StageIndex}",
                AuditActor = "event-pcroom-timepoint",
                AuditReason =
                    $"pcroomtimepoint {kind} reward stage {command.StageIndex}",
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
        {
            return stageIndex >= 1 && stageIndex <= 4
                ? 1 << (stageIndex - 1)
                : 0;
        }
    }
}
