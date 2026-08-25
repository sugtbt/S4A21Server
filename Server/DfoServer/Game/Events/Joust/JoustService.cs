using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Game.Inventory;
using DfoServer.Game.Mailbox;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Events.Joust
{
    internal sealed class JoustService
    {
        private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);
        private static readonly object ClockRegistrationSync = new object();
        private static bool _clockRegistered;

        private readonly IGameDatabase _database;
        private readonly MailboxService _mailbox;
        private readonly JoustConfigProvider _configProvider;
        private readonly JoustRepository _repository;
        private readonly Func<DateTimeOffset> _nowProvider;
        private readonly Func<int, int> _next;

        internal JoustService(
            IGameDatabase database,
            MailboxService mailbox,
            JoustConfigProvider configProvider = null,
            Func<DateTimeOffset> nowProvider = null,
            Func<int, int> next = null)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
            _configProvider = configProvider ?? JoustConfigProvider.Instance;
            _repository = new JoustRepository(_database);
            _nowProvider = nowProvider ?? (() => DateTimeOffset.UtcNow);
            _next = next ?? ServerRandom.Next;
        }

        internal void Initialize()
        {
            var config = _configProvider.Current;
            _repository.EnsureStaticConfigRows(config);
        }

        internal void RegisterClock(ClockService clock)
        {
            if (clock == null)
                return;

            lock (ClockRegistrationSync)
            {
                if (_clockRegistered)
                    return;

                _clockRegistered = true;
                clock.RegisterMinuteTick(
                    "event:joust",
                    utcNow =>
                    {
                        try
                        {
                            Tick(utcNow);
                        }
                        catch (Exception ex)
                        {
                            FileLogger.Log("[Joust] minute tick failed: " + ex);
                        }
                    });
            }
        }

        internal void Tick(DateTime utcNow)
        {
            var now = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
            _database.Write((connection, transaction) =>
            {
                PrepareCurrentRound(
                    connection,
                    transaction,
                    _configProvider.Current,
                    now,
                    characterId: 0,
                    out _);
            });
        }

        internal bool TryGetStateSnapshot(out JoustStateSnapshot state)
            => TryGetStateSnapshot(_nowProvider(), out state);

        internal bool TryGetStateSnapshot(
            DateTime utcNow,
            out JoustStateSnapshot state)
            => TryGetStateSnapshot(ToUtcOffset(utcNow), out state);

        private bool TryGetStateSnapshot(
            DateTimeOffset now,
            out JoustStateSnapshot state)
        {
            state = null;
            try
            {
                JoustStateSnapshot local = null;
                _database.Write((connection, transaction) =>
                {
                    var config = _configProvider.Current;
                    var schedule = PrepareCurrentRound(
                        connection,
                        transaction,
                        config,
                        now,
                        characterId: 0,
                        out var rule);
                    if (schedule?.IsOpen == true)
                    {
                        local = new JoustStateSnapshot
                        {
                            RoundNo = ToProtocolRound(rule.CurrentRound),
                            Phase = schedule.Phase,
                            CurrentRaceStage = schedule.CurrentRaceStage,
                        };
                    }
                });

                state = local;
                return state != null;
            }
            catch (Exception ex)
            {
                FileLogger.Log("[Joust] state snapshot failed: " + ex);
                return false;
            }
        }

        internal bool TryGetSnapshot(int characterId, out JoustSnapshot snapshot)
            => TryGetSnapshot(characterId, _nowProvider(), out snapshot);

        internal bool TryGetSnapshotAt(
            int characterId,
            DateTime utcNow,
            out JoustSnapshot snapshot)
            => TryGetSnapshot(characterId, ToUtcOffset(utcNow), out snapshot);

        private bool TryGetSnapshot(
            int characterId,
            DateTimeOffset now,
            out JoustSnapshot snapshot)
        {
            snapshot = null;
            if (characterId <= 0)
                return false;

            try
            {
                JoustSnapshot local = null;
                _database.Write((connection, transaction) =>
                {
                    var config = _configProvider.Current;
                    var schedule = PrepareCurrentRound(
                        connection,
                        transaction,
                        config,
                        now,
                        characterId,
                        out var rule);
                    if (schedule?.IsOpen == true)
                    {
                        local = LoadSnapshot(
                            connection,
                            transaction,
                            characterId,
                            rule,
                            schedule);
                    }
                });

                snapshot = local;
                return snapshot != null;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[Joust] snapshot failed cid={characterId}: {ex}");
                return false;
            }
        }

        internal IReadOnlyList<JoustHistoryEntry> LoadHistory(int limit = 500)
        {
            try
            {
                return _database.Read(connection =>
                    _repository.LoadHistory(connection, null, limit));
            }
            catch (Exception ex)
            {
                FileLogger.Log("[Joust] load history failed: " + ex);
                return Array.Empty<JoustHistoryEntry>();
            }
        }

        internal JoustBetResult PlaceBet(
            InventoryLease lease,
            int characterLevel,
            JoustBetCommand command)
        {
            if (lease == null || lease.Inventory == null)
                return JoustBetResult.Fail(JoustBetStatus.InventoryUnavailable);
            if (command == null || command.Amount <= 0)
                return JoustBetResult.Fail(JoustBetStatus.InvalidRequest);

            var status = JoustBetStatus.InvalidRequest;
            JoustSnapshot snapshot = null;
            var consumed = new List<InventoryMaterialConsumptionEntry>();
            var committed = OnlineInventoryMutationCommitCoordinator.TryCommit(
                lease,
                "event-joust-bet",
                (connection, transaction) =>
                {
                    var config = _configProvider.Current;
                    if (characterLevel < config.MinLevel)
                    {
                        status = JoustBetStatus.LevelTooLow;
                        return true;
                    }

                    var schedule = PrepareCurrentRound(
                        connection,
                        transaction,
                        config,
                        _nowProvider(),
                        lease.CharacterId,
                        out var rule);
                    if (schedule?.IsOpen != true)
                    {
                        status = JoustBetStatus.Closed;
                        return true;
                    }

                    if (schedule.Phase != JoustPhase.Betting)
                    {
                        status = JoustBetStatus.NotBettingPhase;
                        return true;
                    }

                    var slots = _repository.LoadRoundSlots(
                        connection,
                        transaction,
                        rule.CurrentRound);
                    var slot = slots.FirstOrDefault(candidate =>
                        candidate.KnightIndex == command.HorseId);
                    if (slot == null)
                    {
                        status = JoustBetStatus.InvalidRequest;
                        return true;
                    }

                    var currentTotal = _repository.LoadCharacterBetTotal(
                        connection,
                        transaction,
                        rule.CurrentRound,
                        lease.CharacterId);
                    if ((long)currentTotal + command.Amount > config.MaxBetting)
                    {
                        status = JoustBetStatus.BetLimitExceeded;
                        return true;
                    }

                    if (!TryConsumeBetMaterial(
                            lease.Inventory,
                            config,
                            command,
                            consumed))
                    {
                        status = JoustBetStatus.InsufficientMaterial;
                        return true;
                    }

                    _repository.AddBet(
                        connection,
                        transaction,
                        rule.CurrentRound,
                        lease.CharacterId,
                        slot,
                        command.Amount,
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                    var refreshedSchedule = CalculateSchedule(rule, true, _nowProvider());
                    refreshedSchedule.RoundNo = rule.CurrentRound;
                    snapshot = LoadSnapshot(
                        connection,
                        transaction,
                        lease.CharacterId,
                        rule,
                        refreshedSchedule);
                    status = JoustBetStatus.Success;
                    return true;
                });

            if (!committed)
            {
                return JoustBetResult.Fail(
                    status == JoustBetStatus.Success
                        ? JoustBetStatus.CommitFailed
                        : status);
            }

            return new JoustBetResult
            {
                Status = status,
                Snapshot = snapshot,
                Consumed = consumed,
            };
        }

        internal static JoustScheduleSnapshot CalculateSchedule(
            JoustRule rule,
            bool eventEnabled,
            DateTimeOffset now)
        {
            var snapshot = new JoustScheduleSnapshot
            {
                EventEnabled = eventEnabled,
                Phase = JoustPhase.Closed,
                RoundNo = Math.Max(1, rule?.CurrentRound ?? 1),
            };
            if (!eventEnabled || rule == null)
                return snapshot;

            var startHour = Math.Max(0, Math.Min(23, rule.StartHour));
            var roundsPerDay = Math.Max(1, rule.RoundsPerDay);
            var intervalMinutes = Math.Max(1, rule.RoundIntervalMinutes);
            var bettingMinutes = Math.Max(1, rule.BettingDurationMinutes);
            var stopMinutes = Math.Max(0, rule.StopBettingMinutes);
            var stageCount = Math.Max(1, rule.ResultStageCount);
            var stageSeconds = Math.Max(1, rule.ResultStageIntervalSeconds);

            var local = now.ToOffset(BeijingOffset);
            var dayStart = new DateTimeOffset(
                local.Year,
                local.Month,
                local.Day,
                startHour,
                0,
                0,
                BeijingOffset);
            snapshot.DayId = BuildDayId(local);

            if (local < dayStart)
                return snapshot;

            var activeWindow = TimeSpan.FromMinutes((long)roundsPerDay * intervalMinutes);
            if (local >= dayStart.Add(activeWindow))
                return snapshot;

            var elapsedSinceStart = local - dayStart;
            var scheduleIndex = (int)(elapsedSinceStart.TotalMinutes / intervalMinutes);
            if (scheduleIndex < 0 || scheduleIndex >= roundsPerDay)
                return snapshot;

            var roundStart = dayStart.AddMinutes((long)scheduleIndex * intervalMinutes);
            var elapsed = local - roundStart;
            var bettingEnd = TimeSpan.FromMinutes(bettingMinutes);
            var stopEnd = bettingEnd.Add(TimeSpan.FromMinutes(stopMinutes));
            var raceDuration = TimeSpan.FromSeconds((long)stageCount * stageSeconds);
            var raceEnd = stopEnd.Add(raceDuration);

            snapshot.RoundStartLocal = roundStart;
            snapshot.ScheduleIndex = scheduleIndex;
            if (elapsed < bettingEnd)
            {
                snapshot.Phase = JoustPhase.Betting;
                return snapshot;
            }

            if (elapsed < stopEnd)
            {
                snapshot.Phase = JoustPhase.StopBetting;
                return snapshot;
            }

            if (elapsed < raceEnd)
            {
                snapshot.Phase = JoustPhase.Racing;
                snapshot.CurrentRaceStage = Math.Max(
                    0,
                    Math.Min(
                        stageCount - 1,
                        (int)((elapsed - stopEnd).TotalSeconds / stageSeconds)));
                return snapshot;
            }

            snapshot.Phase = JoustPhase.ResultReview;
            snapshot.CurrentRaceStage = stageCount - 1;
            return snapshot;
        }

        private JoustScheduleSnapshot PrepareCurrentRound(
            SqliteConnection connection,
            SqliteTransaction transaction,
            JoustConfig config,
            DateTimeOffset now,
            int characterId,
            out JoustRule rule)
        {
            _repository.EnsureStaticConfigRows(connection, transaction, config);
            var eventEnabled = GameEventRepository.IsEnabled(
                connection,
                transaction,
                JoustConfig.EventId);
            rule = _repository.LoadRule(connection, transaction);
            var schedule = CalculateSchedule(rule, eventEnabled, now);
            if (!eventEnabled || rule == null)
                return schedule;

            if (!schedule.IsOpen)
            {
                ResolveCurrentRoundIfElapsed(
                    connection,
                    transaction,
                    rule,
                    config,
                    now);
                return schedule;
            }

            if (rule.CurrentDayId != schedule.DayId
                || rule.CurrentScheduleIndex != schedule.ScheduleIndex)
            {
                ResolveCurrentRoundIfElapsed(
                    connection,
                    transaction,
                    rule,
                    config,
                    now);
            }

            rule = _repository.AdvanceRuleForSchedule(
                connection,
                transaction,
                rule,
                schedule);
            schedule.RoundNo = rule.CurrentRound;
            _repository.EnsureRoundSlots(
                connection,
                transaction,
                rule.CurrentRound,
                schedule.DayId,
                schedule.ScheduleIndex,
                schedule.RoundStartLocal.ToUnixTimeSeconds(),
                config,
                _next);
            ResolveVisibleStages(
                connection,
                transaction,
                rule,
                schedule,
                config);
            return schedule;
        }

        private JoustSnapshot LoadSnapshot(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            JoustRule rule,
            JoustScheduleSnapshot schedule)
        {
            var slots = _repository.LoadRoundSlots(
                connection,
                transaction,
                rule.CurrentRound);
            var bets = _repository.LoadCharacterBets(
                connection,
                transaction,
                rule.CurrentRound,
                characterId);
            var bracket = _repository.LoadBracketSlots(
                connection,
                transaction,
                rule.CurrentRound);
            var resultStage = _repository.LoadResultStageIndex(
                connection,
                transaction,
                rule.CurrentRound);
            return new JoustSnapshot
            {
                RoundNo = ToProtocolRound(rule.CurrentRound),
                Phase = schedule.Phase,
                CharacterId = characterId,
                CharacterTotalBet = bets.Sum(bet => Math.Max(0, bet.BetAmount)),
                CurrentResultStageIndex = resultStage,
                Slots = slots,
                Bets = bets,
                BracketSlots = bracket,
            };
        }

        private void ResolveVisibleStages(
            SqliteConnection connection,
            SqliteTransaction transaction,
            JoustRule rule,
            JoustScheduleSnapshot schedule,
            JoustConfig config)
        {
            if (schedule.Phase != JoustPhase.Racing
                && schedule.Phase != JoustPhase.ResultReview)
            {
                return;
            }

            var maxStage = schedule.Phase == JoustPhase.ResultReview
                ? Math.Max(1, rule.ResultStageCount) - 1
                : schedule.CurrentRaceStage;
            ResolveStagesThrough(
                connection,
                transaction,
                rule.CurrentRound,
                maxStage,
                Math.Max(1, rule.ResultStageCount),
                config);
        }

        private void ResolveCurrentRoundIfElapsed(
            SqliteConnection connection,
            SqliteTransaction transaction,
            JoustRule rule,
            JoustConfig config,
            DateTimeOffset now)
        {
            if (rule == null
                || rule.CurrentRound <= 0
                || rule.CurrentDayId <= 0
                || rule.CurrentScheduleIndex < 0)
            {
                return;
            }

            var roundStart = TryBuildRoundStart(rule);
            if (!roundStart.HasValue)
                return;

            var finalEnd = roundStart.Value
                .AddMinutes(rule.BettingDurationMinutes)
                .AddMinutes(rule.StopBettingMinutes)
                .AddSeconds((long)rule.ResultStageCount * rule.ResultStageIntervalSeconds);
            if (now.ToOffset(BeijingOffset) < finalEnd)
                return;

            ResolveStagesThrough(
                connection,
                transaction,
                rule.CurrentRound,
                Math.Max(1, rule.ResultStageCount) - 1,
                Math.Max(1, rule.ResultStageCount),
                config);
        }

        private void ResolveStagesThrough(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int roundNo,
            int maxStage,
            int resultStageCount,
            JoustConfig config)
        {
            resultStageCount = Math.Max(1, resultStageCount);
            maxStage = Math.Max(0, Math.Min(resultStageCount - 1, maxStage));
            var slots = _repository.LoadRoundSlots(connection, transaction, roundNo);
            if (slots.Count != 8)
                return;

            var bySlot = slots.ToDictionary(slot => slot.SlotNo);
            var byKnight = slots.ToDictionary(slot => slot.KnightIndex);
            var bracket = _repository.LoadBracketSlots(connection, transaction, roundNo);
            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            for (var stage = 0; stage <= maxStage; stage++)
            {
                var matchCount = stage == 0 ? 4 : stage == 1 ? 2 : 1;
                for (var match = 0; match < matchCount; match++)
                {
                    if (_repository.IsMatchResolved(
                            connection,
                            transaction,
                            roundNo,
                            stage,
                            match))
                    {
                        continue;
                    }

                    if (!TryResolveMatchInputs(
                            stage,
                            match,
                            bySlot,
                            byKnight,
                            bracket,
                            out var left,
                            out var right))
                    {
                        return;
                    }

                    var winner = _next(2) == 0 ? left : right;
                    var loser = ReferenceEquals(winner, left) ? right : left;
                    if (_repository.InsertMatchResultIfNew(
                            connection,
                            transaction,
                            roundNo,
                            stage,
                            match,
                            winner,
                            loser,
                            nowUnix))
                    {
                        WriteBracketMatch(stage, match, winner, loser, bracket);
                        _repository.SaveBracketSlots(
                            connection,
                            transaction,
                            roundNo,
                            stage,
                            bracket,
                            nowUnix);
                    }
                }
            }

            if (maxStage >= resultStageCount - 1)
            {
                EnsureSettlement(
                    connection,
                    transaction,
                    roundNo,
                    config,
                    bracket,
                    nowUnix);
            }
        }

        private void EnsureSettlement(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int roundNo,
            JoustConfig config,
            ushort[] bracket,
            long nowUnix)
        {
            if (bracket == null || bracket.Length < 13)
                return;

            var championHorseId = bracket[12];
            var slots = _repository.LoadRoundSlots(connection, transaction, roundNo);
            var champion = slots.FirstOrDefault(slot => slot.KnightIndex == championHorseId);
            if (champion == null)
                return;

            _repository.InsertHistoryIfMissing(
                connection,
                transaction,
                roundNo,
                champion.KnightIndex,
                champion.OddsX10,
                nowUnix);

            var recipients = _repository.LoadUnsettledRewardRecipients(
                connection,
                transaction,
                roundNo);
            foreach (var recipient in recipients)
            {
                var bets = _repository.LoadCharacterBets(
                    connection,
                    transaction,
                    roundNo,
                    recipient.CharacterId);
                var winBet = bets
                    .Where(bet => bet.KnightIndex == champion.KnightIndex)
                    .Sum(bet => Math.Max(0, bet.BetAmount));
                var mails = BuildRewardMails(
                    recipient,
                    roundNo,
                    config,
                    winBet,
                    champion.OddsX10);
                if (mails.Count == 0)
                    continue;

                var result = _mailbox.SendSystemMails(
                    connection,
                    transaction,
                    mails);
                if (!result.Success)
                {
                    throw new InvalidOperationException(
                        $"joust reward mail failed cid={recipient.CharacterId} "
                        + $"round={roundNo} error={result.Error}");
                }

                _repository.MarkRewardsSent(
                    connection,
                    transaction,
                    roundNo,
                    recipient.CharacterId,
                    nowUnix);
            }
        }

        private static List<MailboxSendRequest> BuildRewardMails(
            JoustRewardRecipient recipient,
            int roundNo,
            JoustConfig config,
            int winBet,
            int championOddsX10)
        {
            var mails = new List<MailboxSendRequest>();
            if (recipient.TotalBetAmount > 0)
            {
                mails.Add(CreateRewardMail(
                    recipient,
                    roundNo,
                    "fixed",
                    "骑士马战参与奖励",
                    "骑士马战竞猜参与奖励已发放。",
                    config.BettingRewardItemId,
                    recipient.TotalBetAmount));
            }

            var winRewardCount = Math.Max(0, winBet * championOddsX10 / 10);
            if (winRewardCount > 0)
            {
                mails.Add(CreateRewardMail(
                    recipient,
                    roundNo,
                    "winner",
                    "骑士马战竞猜奖励",
                    "恭喜押中骑士马战冠军，奖励已发放。",
                    config.RewardItemId,
                    winRewardCount));
            }

            return mails;
        }

        private static MailboxSendRequest CreateRewardMail(
            JoustRewardRecipient recipient,
            int roundNo,
            string kind,
            string title,
            string text,
            int itemId,
            int itemCount)
        {
            return new MailboxSendRequest
            {
                SenderCharacterId = recipient.CharacterId,
                SenderAccountId = recipient.AccountId,
                SenderName = "DNFadmin",
                ReceiverCharacterId = recipient.CharacterId,
                ReceiverAccountId = recipient.AccountId,
                ReceiverName = recipient.Name ?? string.Empty,
                SenderLevel = recipient.Level,
                ReceiverLevel = recipient.Level,
                Gold = 0,
                Title = title,
                Text = text,
                MailType = 1,
                SourceProtocol = 0,
                IdempotencyKey = $"event-joust:{roundNo}:{recipient.CharacterId}:{kind}",
                AuditActor = "event-joust",
                AuditReason = $"joust round {roundNo} {kind} reward",
                Attachments = new[]
                {
                    new MailboxSendAttachmentRequest
                    {
                        ItemType = ResolveMailboxItemType(itemId),
                        ItemId = itemId,
                        ItemCount = itemCount,
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

        private static bool TryResolveMatchInputs(
            int stage,
            int match,
            IReadOnlyDictionary<int, JoustRoundSlot> bySlot,
            IReadOnlyDictionary<int, JoustRoundSlot> byKnight,
            ushort[] bracket,
            out JoustRoundSlot left,
            out JoustRoundSlot right)
        {
            left = null;
            right = null;
            if (stage == 0)
            {
                return bySlot.TryGetValue(match * 2, out left)
                    && bySlot.TryGetValue(match * 2 + 1, out right);
            }

            if (stage == 1)
            {
                return byKnight.TryGetValue(bracket[match * 4], out left)
                    && byKnight.TryGetValue(bracket[match * 4 + 2], out right);
            }

            return byKnight.TryGetValue(bracket[8], out left)
                && byKnight.TryGetValue(bracket[10], out right);
        }

        private static void WriteBracketMatch(
            int stage,
            int match,
            JoustRoundSlot winner,
            JoustRoundSlot loser,
            ushort[] bracket)
        {
            var offset = stage == 0
                ? match * 2
                : stage == 1
                    ? 8 + match * 2
                    : 12;
            bracket[offset] = (ushort)winner.KnightIndex;
            bracket[offset + 1] = (ushort)loser.KnightIndex;
        }

        private static bool TryConsumeBetMaterial(
            InventoryService inventory,
            JoustConfig config,
            JoustBetCommand command,
            List<InventoryMaterialConsumptionEntry> consumed)
        {
            if (inventory == null || config == null || command == null)
                return false;

            var accepted = new HashSet<int>(config.MaterialItemIds ?? Array.Empty<int>());
            if (command.MaterialSlotIndex >= 0
                && inventory.TryGetItem(
                    InventoryListType.Main,
                    command.MaterialSlotIndex,
                    out var source)
                && source != null
                && accepted.Contains(source.ItemId))
            {
                var available = InventoryStackRuleService.IsStackable(source)
                    ? Math.Max(0, source.Count)
                    : 1;
                if (available < command.Amount)
                    return false;

                if (!InventoryDeleteService.TryConsumeFromSlot(
                        inventory,
                        InventoryListType.Main,
                        command.MaterialSlotIndex,
                        source.ItemId,
                        command.Amount,
                        out var delete)
                    || !delete.Success)
                {
                    return false;
                }

                consumed.Add(new InventoryMaterialConsumptionEntry
                {
                    SlotIndex = command.MaterialSlotIndex,
                    ItemTemplateId = source.ItemId,
                    Count = delete.DeletedCount,
                    RemainingCount = delete.RemainingCount,
                    SourceSnapshot = delete.SourceSnapshot?.Copy(),
                });
                return true;
            }

            foreach (var materialItemId in accepted.OrderBy(item => item))
            {
                if (inventory.CountMainItem(materialItemId) < command.Amount)
                    continue;

                return InventoryMaterialConsumptionService.TryConsume(
                    inventory,
                    new[]
                    {
                        new InventoryMaterialRequirement(
                            materialItemId,
                            command.Amount),
                    },
                    consumed);
            }

            return false;
        }

        private static DateTimeOffset? TryBuildRoundStart(JoustRule rule)
        {
            var day = rule.CurrentDayId;
            var year = day / 10000;
            var month = day / 100 % 100;
            var date = day % 100;
            try
            {
                return new DateTimeOffset(
                    year,
                    month,
                    date,
                    Math.Max(0, Math.Min(23, rule.StartHour)),
                    0,
                    0,
                    BeijingOffset).AddMinutes(
                    (long)Math.Max(0, rule.CurrentScheduleIndex)
                    * Math.Max(1, rule.RoundIntervalMinutes));
            }
            catch
            {
                return null;
            }
        }

        private static int BuildDayId(DateTimeOffset local)
        {
            return local.Year * 10000 + local.Month * 100 + local.Day;
        }

        private static ushort ToProtocolRound(int roundNo)
        {
            return (ushort)Math.Max(0, Math.Min(ushort.MaxValue, roundNo));
        }

        private static DateTimeOffset ToUtcOffset(DateTime utcNow)
        {
            if (utcNow.Kind == DateTimeKind.Local)
                utcNow = utcNow.ToUniversalTime();
            else if (utcNow.Kind == DateTimeKind.Unspecified)
                utcNow = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);

            return new DateTimeOffset(utcNow);
        }
    }
}
