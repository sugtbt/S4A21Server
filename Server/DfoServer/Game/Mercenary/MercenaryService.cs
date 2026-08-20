using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Infrastructure;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DfoServer.Game.Mercenary
{
    public sealed class MercenaryService
    {
        public const byte RedeployReturnPurpose = 0xFF;

        private readonly MercenaryRepository _repository;
        private readonly ICharacterRepository _characters;
        private readonly IMercenaryAvatarBonusTierProvider _avatarBonus;
        private readonly MercenaryRewardCalculator _rewards;
        private readonly IMercenaryMailDelivery _mailDelivery;
        private readonly IMercenaryTimeProvider _time;
        private readonly Func<MercenaryConfig> _getConfig;
        private readonly ConcurrentDictionary<int, object> _accountLocks = new ConcurrentDictionary<int, object>();
        private int _deliveryClockRegistered;
        private int _deliveryWorkerRunning;

        public MercenaryService(
            MercenaryRepository repository,
            ICharacterRepository characters,
            IMercenaryAvatarBonusTierProvider avatarBonus,
            MercenaryRewardCalculator rewards = null,
            IMercenaryMailDelivery mailDelivery = null,
            IMercenaryTimeProvider time = null,
            Func<MercenaryConfig> getConfig = null)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _characters = characters ?? throw new ArgumentNullException(nameof(characters));
            _avatarBonus = avatarBonus ?? throw new ArgumentNullException(nameof(avatarBonus));
            _rewards = rewards ?? new MercenaryRewardCalculator();
            _mailDelivery = mailDelivery ?? PendingMercenaryMailDelivery.Instance;
            _time = time ?? SystemMercenaryTimeProvider.Instance;
            _getConfig = getConfig ?? (() => MercenaryConfigProvider.Current);
        }

        public MercenaryInfoSnapshot GetInfo(int accountId)
        {
            var snapshot = new MercenaryInfoSnapshot();
            if (accountId <= 0)
                return snapshot;

            try
            {
                DeliverPendingRewardsForAccount(accountId, 20);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[Mercenary] Account reward reconciliation failed account={accountId}: {ex}");
            }
            var now = _time.GetUnixTimeSeconds();
            var characters = _characters.ListByAccount(accountId);
            var adventureGroup = AdventureGroupDataProvider.Calculate(characters);
            snapshot.ManageLevel = adventureGroup.ManageLevel;
            snapshot.ManagePoint = adventureGroup.TotalPoint;

            var assignments = _repository.ListAssignments(accountId)
                .ToDictionary(assignment => assignment.CharacterId);
            var config = _getConfig();
            foreach (var character in characters
                .Where(character => character.Level >= config.MinimumCharacterLevel)
                .Take(byte.MaxValue))
            {
                if (assignments.TryGetValue(character.CharacterId, out var assignment))
                {
                    snapshot.Records.Add(new MercenaryCharacterInfo
                    {
                        CharacterId = character.CharacterId,
                        Name = character.Name,
                        State = assignment.GetState(now),
                        RemainingSeconds = ClampToInt((long)assignment.FinishTime - now),
                        AreaIndex = assignment.AreaIndex,
                        PeriodIndex = assignment.PeriodIndex,
                        AvatarBonusTier = (byte)Math.Max(0, Math.Min(byte.MaxValue, assignment.AvatarBonusTier)),
                    });
                    continue;
                }

                snapshot.Records.Add(new MercenaryCharacterInfo
                {
                    CharacterId = character.CharacterId,
                    Name = character.Name,
                    State = MercenaryExpeditionState.Waiting,
                    RemainingSeconds = 0,
                    AreaIndex = MercenaryCharacterInfo.WaitingAreaIndex,
                    PeriodIndex = MercenaryCharacterInfo.UnassignedPeriodIndex,
                    AvatarBonusTier = (byte)Math.Max(
                        0,
                        Math.Min(byte.MaxValue, _avatarBonus.ResolveTier(character.CharacterId, now, config))),
                });
            }
            return snapshot;
        }

        private static int ClampToInt(long value)
            => value < int.MinValue ? int.MinValue : value > int.MaxValue ? int.MaxValue : (int)value;

        public MercenaryDispatchResult Dispatch(
            int accountId,
            int activeCharacterId,
            int characterId,
            byte requestedAreaIndex,
            byte periodIndex)
        {
            if (accountId <= 0)
                return DispatchFailure(MercenaryOperationStatus.NotAuthenticated);
            if (characterId <= 0)
                return DispatchFailure(MercenaryOperationStatus.InvalidRequest);

            lock (_accountLocks.GetOrAdd(accountId, _ => new object()))
            {
                var character = _characters.GetById(characterId);
                if (character == null)
                    return DispatchFailure(MercenaryOperationStatus.CharacterNotFound);
                if (character.AccountId != accountId)
                    return DispatchFailure(MercenaryOperationStatus.CharacterNotOwned);
                if (character.Deleted)
                    return DispatchFailure(MercenaryOperationStatus.CharacterDeleted);
                if (characterId == activeCharacterId)
                    return DispatchFailure(MercenaryOperationStatus.ActiveCharacter);

                var config = _getConfig();
                if (character.Level < config.MinimumCharacterLevel)
                    return DispatchFailure(MercenaryOperationStatus.LevelTooLow);

                var now = _time.GetUnixTimeSeconds();
                var period = config.GetPeriod(periodIndex);
                if (period == null)
                    return DispatchFailure(MercenaryOperationStatus.InvalidPeriod);

                var requestedArea = config.GetArea(requestedAreaIndex);
                if (requestedArea == null || !requestedArea.Visible)
                    return DispatchFailure(MercenaryOperationStatus.InvalidArea);

                var area = requestedArea.IsRandom
                    ? ResolveRandomArea(config, character)
                    : requestedArea;
                if (area == null
                    || area.IsRandom
                    || area.MinimumLevel <= 0
                    || character.Level < area.MinimumLevel
                    || area.RewardGroups.Count == 0)
                    return DispatchFailure(MercenaryOperationStatus.InvalidArea);

                long finishTime = now + (long)period.Hours * config.BaseTimeUnitSeconds;
                if (finishTime > int.MaxValue)
                    return DispatchFailure(MercenaryOperationStatus.InvalidPeriod);

                var existing = _repository.GetAssignment(accountId, characterId);
                var assignment = new MercenaryAssignment
                {
                    AccountId = accountId,
                    CharacterId = characterId,
                    CharacterLevel = character.Level,
                    StartTime = now,
                    FinishTime = (int)finishTime,
                    AreaIndex = area.Index,
                    PeriodIndex = period.Index,
                    AvatarBonusTier = _avatarBonus.ResolveTier(characterId, now, config),
                };

                MercenaryRewardOutboxEntry previousReward = null;
                try
                {
                    var created = existing == null
                        ? _repository.TryCreateAssignment(assignment)
                        : _repository.TryReplaceAssignment(
                            existing,
                            _rewards.Calculate(existing, config, now),
                            RedeployReturnPurpose,
                            assignment,
                            out previousReward);
                    if (!created)
                        return DispatchFailure(MercenaryOperationStatus.AlreadyAssigned, previousReward);
                    if (previousReward != null)
                        AttemptDelivery(previousReward);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[Mercenary] Dispatch persistence failed account={accountId} char={characterId}: {ex}");
                    return DispatchFailure(MercenaryOperationStatus.PersistenceFailure, previousReward);
                }

                FileLogger.Log(
                    $"[Mercenary] Dispatch account={accountId} char={characterId} assignment={assignment.AssignmentId} "
                    + $"area={area.Index} requestedArea={requestedAreaIndex} period={period.Index} "
                    + $"start={assignment.StartTime} finish={assignment.FinishTime} avatarTier={assignment.AvatarBonusTier}");
                return new MercenaryDispatchResult
                {
                    Status = MercenaryOperationStatus.Success,
                    Assignment = assignment,
                    SettledPreviousReward = previousReward,
                };
            }
        }

        public MercenaryReturnResult Return(int accountId, int characterId, byte purpose)
        {
            if (accountId <= 0)
                return ReturnFailure(MercenaryOperationStatus.NotAuthenticated, characterId, purpose);
            if (characterId <= 0)
                return ReturnFailure(MercenaryOperationStatus.InvalidRequest, characterId, purpose);

            lock (_accountLocks.GetOrAdd(accountId, _ => new object()))
            {
                var assignment = _repository.GetAssignment(accountId, characterId);
                if (assignment == null)
                    return ReturnFailure(MercenaryOperationStatus.NotAssigned, characterId, purpose);

                try
                {
                    var outbox = Settle(assignment, _getConfig(), _time.GetUnixTimeSeconds(), purpose);
                    if (outbox == null)
                        return ReturnFailure(MercenaryOperationStatus.PersistenceFailure, characterId, purpose);

                    FileLogger.Log(
                        $"[Mercenary] Return account={accountId} char={characterId} assignment={assignment.AssignmentId} "
                        + $"purpose={purpose} hours={outbox.CompletedHours} early={outbox.IsEarlyReturn} "
                        + $"gold={outbox.BaseGold}+{outbox.BonusGold} item={outbox.ItemTemplateId}x{outbox.ItemCount}");
                    return new MercenaryReturnResult
                    {
                        Status = MercenaryOperationStatus.Success,
                        CharacterId = characterId,
                        Purpose = purpose,
                        Reward = outbox,
                    };
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[Mercenary] Return persistence failed account={accountId} char={characterId}: {ex}");
                    return ReturnFailure(MercenaryOperationStatus.PersistenceFailure, characterId, purpose);
                }
            }
        }

        public int DeliverPendingRewards(int limit = 100)
            => DeliverPendingRewards(_repository.ListPendingOutbox(limit));

        public int DeliverPendingRewardsForAccount(int accountId, int limit = 100)
        {
            if (accountId <= 0)
                return 0;
            return DeliverPendingRewards(_repository.ListPendingOutboxForAccount(accountId, limit));
        }

        public void RegisterDeliveryClock(ClockService clock)
        {
            if (clock == null)
                throw new ArgumentNullException(nameof(clock));
            if (Interlocked.Exchange(ref _deliveryClockRegistered, 1) != 0)
                return;

            clock.RegisterMinuteTick("mercenary-mail-delivery", tickTime =>
            {
                if (Interlocked.CompareExchange(ref _deliveryWorkerRunning, 1, 0) != 0)
                    return;

                _ = Task.Run(() =>
                {
                    try
                    {
                        DeliverPendingRewards(100);
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log($"[Mercenary] Pending reward delivery sweep failed: {ex}");
                    }
                    finally
                    {
                        Volatile.Write(ref _deliveryWorkerRunning, 0);
                    }
                });
            });
        }

        private int DeliverPendingRewards(
            System.Collections.Generic.IReadOnlyList<MercenaryRewardOutboxEntry> entries)
        {
            var processed = 0;
            foreach (var entry in entries)
            {
                AttemptDelivery(entry);
                processed++;
            }
            return processed;
        }

        private MercenaryRewardOutboxEntry Settle(
            MercenaryAssignment assignment,
            MercenaryConfig config,
            int now,
            byte purpose)
        {
            var reward = _rewards.Calculate(assignment, config, now);
            var outbox = _repository.Settle(assignment, reward, purpose);
            if (outbox != null)
                AttemptDelivery(outbox);
            return outbox;
        }

        private void AttemptDelivery(MercenaryRewardOutboxEntry entry)
        {
            MercenaryMailDeliveryResult result;
            try
            {
                result = _mailDelivery.Deliver(entry);
            }
            catch (Exception ex)
            {
                _repository.MarkDeliveryFailed(entry.OutboxId, ex.Message);
                return;
            }

            if (result == null || result.Disposition == MercenaryMailDeliveryDisposition.Pending)
                return;
            if (result.Disposition == MercenaryMailDeliveryDisposition.Delivered)
            {
                _repository.MarkDelivered(entry.OutboxId, result.MailboxMessageId);
                entry.MailboxMessageId = result.MailboxMessageId;
                entry.DeliveryStatus = "delivered";
                entry.DeliveryAttempts++;
            }
            else
            {
                _repository.MarkDeliveryFailed(entry.OutboxId, result.Error);
                entry.DeliveryAttempts++;
            }
        }

        private static MercenaryCompetitionArea ResolveRandomArea(
            MercenaryConfig config,
            CharacterRecord character)
        {
            var candidates = config.Areas
                .Where(area => !area.IsRandom
                    && area.MinimumLevel > 0
                    && area.MinimumLevel <= character.Level
                    && area.RewardGroups.Count > 0)
                .ToArray();
            if (candidates.Length == 0)
                return null;

            return candidates[ServerRandom.Next(candidates.Length)];
        }

        private static MercenaryDispatchResult DispatchFailure(
            MercenaryOperationStatus status,
            MercenaryRewardOutboxEntry previousReward = null)
            => new MercenaryDispatchResult { Status = status, SettledPreviousReward = previousReward };

        private static MercenaryReturnResult ReturnFailure(
            MercenaryOperationStatus status,
            int characterId,
            byte purpose)
            => new MercenaryReturnResult { Status = status, CharacterId = characterId, Purpose = purpose };
    }
}
