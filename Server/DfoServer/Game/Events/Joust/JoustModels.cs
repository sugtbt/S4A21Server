using System;
using System.Collections.Generic;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.Events.Joust
{
    internal enum JoustPhase : byte
    {
        Closed = 0,
        Betting = 1,
        ResultReview = 2,
        StopBetting = 3,
        Racing = 4,
    }

    internal sealed class JoustRule
    {
        public int EventId { get; set; } = JoustConfig.EventId;

        public int CurrentRound { get; set; } = 1;

        public int CurrentDayId { get; set; }

        public int CurrentScheduleIndex { get; set; } = -1;

        public int StartHour { get; set; } = 10;

        public int RoundsPerDay { get; set; } = 7;

        public int RoundIntervalMinutes { get; set; } = 120;

        public int BettingDurationMinutes { get; set; } = 90;

        public int StopBettingMinutes { get; set; } = 10;

        public int ResultStageCount { get; set; } = 3;

        public int ResultStageIntervalSeconds { get; set; } = 200;

        public JoustRule Copy()
        {
            return (JoustRule)MemberwiseClone();
        }
    }

    internal sealed class JoustScheduleSnapshot
    {
        public bool EventEnabled { get; set; }

        public JoustPhase Phase { get; set; }

        public int RoundNo { get; set; }

        public int DayId { get; set; }

        public int ScheduleIndex { get; set; } = -1;

        public DateTimeOffset RoundStartLocal { get; set; }

        public int CurrentRaceStage { get; set; } = -1;

        public bool IsOpen => EventEnabled && Phase != JoustPhase.Closed;
    }

    internal sealed class JoustStateSnapshot
    {
        public ushort RoundNo { get; set; }

        public JoustPhase Phase { get; set; }

        public int CurrentRaceStage { get; set; } = -1;
    }

    internal sealed class JoustRoundSlot
    {
        public int RoundNo { get; set; }

        public int SlotNo { get; set; }

        public int KnightIndex { get; set; }

        public bool IsBlack { get; set; }

        public int AttackType { get; set; }

        public int ConditionIndex { get; set; }

        public int GlobalBetAmount { get; set; }

        public int OddsX10 { get; set; } = 80;

        public int WinCount { get; set; }

        public int LossCount { get; set; }
    }

    internal sealed class JoustCharacterBet
    {
        public int SlotNo { get; set; }

        public int KnightIndex { get; set; }

        public int BetAmount { get; set; }
    }

    internal sealed class JoustHistoryEntry
    {
        public ushort RoundNo { get; set; }

        public byte WinnerHorseId { get; set; }

        public int OddsX10 { get; set; } = 80;
    }

    internal sealed class JoustSnapshot
    {
        public ushort RoundNo { get; set; }

        public JoustPhase Phase { get; set; }

        public int CharacterId { get; set; }

        public int CharacterTotalBet { get; set; }

        public int CurrentResultStageIndex { get; set; } = -1;

        public IReadOnlyList<JoustRoundSlot> Slots { get; set; } =
            Array.Empty<JoustRoundSlot>();

        public IReadOnlyList<JoustCharacterBet> Bets { get; set; } =
            Array.Empty<JoustCharacterBet>();

        public ushort[] BracketSlots { get; set; } = new ushort[14];
    }

    internal sealed class JoustBetCommand
    {
        public byte HorseId { get; set; }

        public short MaterialSlotIndex { get; set; } = -1;

        public int Amount { get; set; }
    }

    internal enum JoustBetStatus
    {
        Success,
        InvalidRequest,
        Closed,
        NotBettingPhase,
        LevelTooLow,
        BetLimitExceeded,
        InsufficientMaterial,
        InventoryUnavailable,
        CommitFailed,
    }

    internal sealed class JoustBetResult
    {
        public JoustBetStatus Status { get; set; } = JoustBetStatus.InvalidRequest;

        public JoustSnapshot Snapshot { get; set; }

        public IReadOnlyList<InventoryMaterialConsumptionEntry> Consumed { get; set; } =
            Array.Empty<InventoryMaterialConsumptionEntry>();

        public bool Success => Status == JoustBetStatus.Success;

        public static JoustBetResult Fail(JoustBetStatus status)
        {
            return new JoustBetResult { Status = status };
        }
    }

    internal sealed class JoustRewardRecipient
    {
        public int CharacterId { get; set; }

        public int AccountId { get; set; }

        public string Name { get; set; } = string.Empty;

        public int Level { get; set; }

        public int TotalBetAmount { get; set; }
    }
}
