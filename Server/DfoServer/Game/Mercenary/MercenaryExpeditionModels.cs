using System;
using System.Collections.Generic;

namespace DfoServer.Game.Mercenary
{
    public enum MercenaryExpeditionState : byte
    {
        Waiting = 0,
        InProgress = 1,
        Complete = 2,
    }

    public enum MercenaryOperationStatus
    {
        Success = 0,
        InvalidRequest,
        NotAuthenticated,
        CharacterNotFound,
        CharacterNotOwned,
        ActiveCharacter,
        CharacterDeleted,
        LevelTooLow,
        AlreadyAssigned,
        NotAssigned,
        InvalidArea,
        InvalidPeriod,
        PersistenceFailure,
    }

    public sealed class MercenaryLevelReward
    {
        public int MinimumLevel { get; set; }
        public int BaseGoldPerHour { get; set; }
        public int ItemProbabilityPerHour { get; set; }
    }

    public sealed class MercenaryPeriodOption
    {
        public byte Index { get; set; }
        public int Hours { get; set; }
        public double BonusMultiplier { get; set; }
    }

    public sealed class MercenaryCriticalOption
    {
        public int Weight { get; set; }
        public double Multiplier { get; set; }
    }

    public sealed class MercenaryWeightedEntry
    {
        public int Value { get; set; }
        public int Weight { get; set; }
    }

    public sealed class MercenaryRewardGroup
    {
        public int Weight { get; set; }
        public string MessageKey { get; set; }
        public List<MercenaryWeightedEntry> Items { get; } = new List<MercenaryWeightedEntry>();
        public List<MercenaryWeightedEntry> Monsters { get; } = new List<MercenaryWeightedEntry>();
    }

    public sealed class MercenaryCompetitionArea
    {
        public byte Index { get; set; }
        public int WorldMapId { get; set; }
        public bool Visible { get; set; } = true;
        public int MinimumLevel { get; set; }
        public List<MercenaryRewardGroup> RewardGroups { get; } = new List<MercenaryRewardGroup>();

        public bool IsRandom => WorldMapId < 0;
    }

    public sealed class MercenaryConfig
    {
        public int BaseTimeUnitSeconds { get; set; }
        public int DefaultDropRatePerHour { get; set; }
        public List<MercenaryLevelReward> LevelRewards { get; } = new List<MercenaryLevelReward>();
        public List<MercenaryPeriodOption> Periods { get; } = new List<MercenaryPeriodOption>();
        public SortedDictionary<int, double> AvatarBonuses { get; } = new SortedDictionary<int, double>();
        public List<MercenaryCriticalOption> CriticalOptions { get; } = new List<MercenaryCriticalOption>();
        public List<MercenaryCompetitionArea> Areas { get; } = new List<MercenaryCompetitionArea>();

        public int MinimumCharacterLevel => LevelRewards.Count == 0 ? int.MaxValue : LevelRewards[0].MinimumLevel;

        public MercenaryPeriodOption GetPeriod(byte index)
            => index < Periods.Count ? Periods[index] : null;

        public MercenaryCompetitionArea GetArea(byte index)
            => index < Areas.Count ? Areas[index] : null;

        public MercenaryLevelReward GetLevelReward(int level)
        {
            MercenaryLevelReward selected = null;
            foreach (var entry in LevelRewards)
            {
                if (entry.MinimumLevel > level)
                    break;
                selected = entry;
            }
            return selected;
        }

        public double GetAvatarMultiplier(int tier)
        {
            var selected = 1.0;
            foreach (var entry in AvatarBonuses)
            {
                if (entry.Key > tier)
                    break;
                selected = entry.Value;
            }
            return selected;
        }

        public int ClampAvatarTier(int tier)
        {
            var selected = 0;
            foreach (var entry in AvatarBonuses)
            {
                if (entry.Key > tier)
                    break;
                selected = entry.Key;
            }
            return selected;
        }
    }

    public sealed class MercenaryAssignment
    {
        public long AssignmentId { get; set; }
        public int AccountId { get; set; }
        public int CharacterId { get; set; }
        public int CharacterLevel { get; set; }
        public int StartTime { get; set; }
        public int FinishTime { get; set; }
        public byte AreaIndex { get; set; }
        public byte PeriodIndex { get; set; }
        public int AvatarBonusTier { get; set; }
        public int Status { get; set; } = 1;
        public int Version { get; set; } = 1;

        public MercenaryExpeditionState GetState(int nowUnixSeconds)
            => FinishTime > nowUnixSeconds
                ? MercenaryExpeditionState.InProgress
                : MercenaryExpeditionState.Complete;
    }

    public sealed class MercenaryReward
    {
        public int BaseGold { get; set; }
        public int BonusGold { get; set; }
        public int ItemTemplateId { get; set; }
        public int ItemCount { get; set; }
        public List<MercenaryRewardItem> Items { get; } = new List<MercenaryRewardItem>();
        public string MailTitleKey { get; set; } = "game_server_msg_225";
        public string MailMessageKey { get; set; } = "game_server_msg_221";
        public int CompletedHours { get; set; }
        public bool IsEarlyReturn { get; set; }
        public double CriticalMultiplier { get; set; } = 1.0;
    }

    public sealed class MercenaryRewardItem
    {
        public int ItemTemplateId { get; set; }
        public int ItemCount { get; set; }
    }

    public sealed class MercenaryRewardOutboxEntry
    {
        public long OutboxId { get; set; }
        public long AssignmentId { get; set; }
        public long MailboxMessageId { get; set; }
        public int AccountId { get; set; }
        public int CharacterId { get; set; }
        public byte AreaIndex { get; set; }
        public byte PeriodIndex { get; set; }
        public int CompletedHours { get; set; }
        public bool IsEarlyReturn { get; set; }
        public byte ReturnPurpose { get; set; }
        public int BaseGold { get; set; }
        public int BonusGold { get; set; }
        public int ItemTemplateId { get; set; }
        public int ItemCount { get; set; }
        public List<MercenaryRewardItem> Items { get; } = new List<MercenaryRewardItem>();
        public string MailTitleKey { get; set; }
        public string MailMessageKey { get; set; }
        public double CriticalMultiplier { get; set; } = 1.0;
        public string DeliveryStatus { get; set; }
        public int DeliveryAttempts { get; set; }

        public bool HasMailReward
            => BaseGold > 0 || BonusGold > 0 || Items.Count > 0
                || (ItemTemplateId > 0 && ItemCount > 0);
    }

    public sealed class MercenaryInfoSnapshot
    {
        public byte ManageLevel { get; set; }
        public int ManagePoint { get; set; }
        public List<MercenaryCharacterInfo> Records { get; } = new List<MercenaryCharacterInfo>();
    }

    public sealed class MercenaryCharacterInfo
    {
        public const byte WaitingAreaIndex = 0;
        public const byte UnassignedPeriodIndex = byte.MaxValue;

        public int CharacterId { get; set; }
        public byte[] Name { get; set; }
        public MercenaryExpeditionState State { get; set; }
        public int RemainingSeconds { get; set; }
        public byte AreaIndex { get; set; } = WaitingAreaIndex;
        public byte PeriodIndex { get; set; } = UnassignedPeriodIndex;
        public byte AvatarBonusTier { get; set; }
    }

    public sealed class MercenaryDispatchResult
    {
        public MercenaryOperationStatus Status { get; set; }
        public MercenaryAssignment Assignment { get; set; }
        public MercenaryRewardOutboxEntry SettledPreviousReward { get; set; }
        public bool Success => Status == MercenaryOperationStatus.Success;
    }

    public sealed class MercenaryReturnResult
    {
        public MercenaryOperationStatus Status { get; set; }
        public int CharacterId { get; set; }
        public byte Purpose { get; set; }
        public MercenaryRewardOutboxEntry Reward { get; set; }
        public bool Success => Status == MercenaryOperationStatus.Success;
    }

    public interface IMercenaryTimeProvider
    {
        int GetUnixTimeSeconds();
    }

    public sealed class SystemMercenaryTimeProvider : IMercenaryTimeProvider
    {
        public static readonly SystemMercenaryTimeProvider Instance = new SystemMercenaryTimeProvider();

        private SystemMercenaryTimeProvider()
        {
        }

        public int GetUnixTimeSeconds()
            => checked((int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }
}
