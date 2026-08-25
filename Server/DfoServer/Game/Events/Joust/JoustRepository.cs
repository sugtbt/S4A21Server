using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Events.Joust
{
    internal sealed class JoustRepository
    {
        private const string JoustTitle = "骑士马战大竞猜";
        private const string JoustStartNotice =
            "活动 - 正在进行[骑士马战大竞猜]活动！";
        private const string JoustEndNotice =
            "活动 - [骑士马战大竞猜]活动已结束！";
        private const string JoustDescription =
            "               [骑士马战大竞猜]              "
            + "*骑士马战大竞猜活动规则：                  "
            + "-活动时间每天10：00开始，共7期。             "
            + "-倍率随动。                                   "
            + "-所有骑士均分胜负率。(取消胜负率加成项) 。                                            ";
        private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

        private readonly IGameDatabase _database;

        internal JoustRepository(IGameDatabase database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        internal void EnsureStaticConfigRows(JoustConfig config)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));

            _database.Write((connection, transaction) =>
            {
                EnsureStaticConfigRows(connection, transaction, config);
            });
        }

        internal void EnsureStaticConfigRows(
            SqliteConnection connection,
            SqliteTransaction transaction,
            JoustConfig config)
        {
            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT OR IGNORE INTO game_event_state(event_id, state)
VALUES(@eventId, 1);",
                ("@eventId", JoustConfig.EventId));

            var calendarWindow = GetCalendarWindowUnix();
            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT INTO game_event_info_details (
    event_id, unknown0, start_notice, end_notice, detail_flag,
    flag_a, flag_b, title, short_name, reserved_or_icon,
    start_unix_time, end_unix_time, link_key, description,
    detail_enabled, sort_order
) VALUES (
    @eventId, 0, @startNotice, @endNotice, 1,
    0, 5, @title, @title, '',
    @startUnixTime, @endUnixTime, '', @description,
    1, 10
)
ON CONFLICT(event_id) DO UPDATE SET
    unknown0 = excluded.unknown0,
    start_notice = excluded.start_notice,
    end_notice = excluded.end_notice,
    detail_flag = excluded.detail_flag,
    flag_a = excluded.flag_a,
    flag_b = excluded.flag_b,
    title = excluded.title,
    short_name = excluded.short_name,
    reserved_or_icon = excluded.reserved_or_icon,
    start_unix_time = excluded.start_unix_time,
    end_unix_time = excluded.end_unix_time,
    link_key = excluded.link_key,
    description = excluded.description,
    detail_enabled = excluded.detail_enabled,
    sort_order = excluded.sort_order,
    updated_at = CURRENT_TIMESTAMP;",
                ("@eventId", JoustConfig.EventId),
                ("@startNotice", JoustStartNotice),
                ("@endNotice", JoustEndNotice),
                ("@title", JoustTitle),
                ("@startUnixTime", calendarWindow.StartUnixTime),
                ("@endUnixTime", calendarWindow.EndUnixTime),
                ("@description", JoustDescription));

            var defaultRule = new JoustRule();
            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT INTO event_joust_rules (
    event_id, start_hour, rounds_per_day, round_interval_minutes,
    betting_duration_minutes, stop_betting_minutes,
    result_stage_count, result_stage_interval_seconds
) VALUES (
    @eventId, @startHour, @roundsPerDay, @roundIntervalMinutes,
    @bettingDurationMinutes, @stopBettingMinutes,
    @resultStageCount, @resultStageIntervalSeconds
)
ON CONFLICT(event_id) DO UPDATE SET
    start_hour = excluded.start_hour,
    rounds_per_day = excluded.rounds_per_day,
    round_interval_minutes = excluded.round_interval_minutes,
    betting_duration_minutes = excluded.betting_duration_minutes,
    stop_betting_minutes = excluded.stop_betting_minutes,
    result_stage_count = excluded.result_stage_count,
    result_stage_interval_seconds = excluded.result_stage_interval_seconds,
    updated_at = CURRENT_TIMESTAMP;",
                ("@eventId", JoustConfig.EventId),
                ("@startHour", defaultRule.StartHour),
                ("@roundsPerDay", defaultRule.RoundsPerDay),
                ("@roundIntervalMinutes", defaultRule.RoundIntervalMinutes),
                ("@bettingDurationMinutes", defaultRule.BettingDurationMinutes),
                ("@stopBettingMinutes", defaultRule.StopBettingMinutes),
                ("@resultStageCount", defaultRule.ResultStageCount),
                ("@resultStageIntervalSeconds", defaultRule.ResultStageIntervalSeconds));

            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var knight in config.Knights)
            {
                ExecuteNonQuery(
                    connection,
                    transaction,
                    @"
INSERT OR IGNORE INTO event_joust_knight_stats (
    knight_index, win_count, loss_count, updated_at_unix
) VALUES (
    @knightIndex, 0, 0, @nowUnix
);",
                    ("@knightIndex", knight.Index),
                    ("@nowUnix", nowUnix));
            }
        }

        private static (uint StartUnixTime, uint EndUnixTime) GetCalendarWindowUnix()
        {
            var now = DateTimeOffset.UtcNow.ToOffset(BeijingOffset);
            var start = new DateTimeOffset(
                now.Year,
                1,
                1,
                0,
                0,
                0,
                BeijingOffset);
            var end = new DateTimeOffset(
                now.Year,
                12,
                31,
                23,
                59,
                59,
                BeijingOffset);
            return ((uint)start.ToUnixTimeSeconds(), (uint)end.ToUnixTimeSeconds());
        }

        internal JoustRule LoadRule(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT event_id,
       current_round,
       current_day_id,
       current_schedule_index,
       start_hour,
       rounds_per_day,
       round_interval_minutes,
       betting_duration_minutes,
       stop_betting_minutes,
       result_stage_count,
       result_stage_interval_seconds
FROM event_joust_rules
WHERE event_id=@eventId;";
                command.Parameters.AddWithValue("@eventId", JoustConfig.EventId);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return null;

                    return new JoustRule
                    {
                        EventId = reader.GetInt32(0),
                        CurrentRound = reader.GetInt32(1),
                        CurrentDayId = reader.GetInt32(2),
                        CurrentScheduleIndex = reader.GetInt32(3),
                        StartHour = reader.GetInt32(4),
                        RoundsPerDay = reader.GetInt32(5),
                        RoundIntervalMinutes = reader.GetInt32(6),
                        BettingDurationMinutes = reader.GetInt32(7),
                        StopBettingMinutes = reader.GetInt32(8),
                        ResultStageCount = reader.GetInt32(9),
                        ResultStageIntervalSeconds = reader.GetInt32(10),
                    };
                }
            }
        }

        internal JoustRule AdvanceRuleForSchedule(
            SqliteConnection connection,
            SqliteTransaction transaction,
            JoustRule rule,
            JoustScheduleSnapshot schedule)
        {
            if (rule == null || schedule == null || !schedule.IsOpen)
                return rule;

            if (rule.CurrentDayId == schedule.DayId
                && rule.CurrentScheduleIndex == schedule.ScheduleIndex)
            {
                return rule;
            }

            var nextRound = rule.CurrentDayId == 0
                && rule.CurrentScheduleIndex < 0
                ? Math.Max(1, rule.CurrentRound)
                : Math.Max(1, rule.CurrentRound + 1);
            nextRound = Math.Max(nextRound, LoadMaxKnownRound(connection, transaction) + 1);

            ExecuteNonQuery(
                connection,
                transaction,
                @"
UPDATE event_joust_rules
SET current_round=@roundNo,
    current_day_id=@dayId,
    current_schedule_index=@scheduleIndex,
    updated_at=CURRENT_TIMESTAMP
WHERE event_id=@eventId;",
                ("@roundNo", nextRound),
                ("@dayId", schedule.DayId),
                ("@scheduleIndex", schedule.ScheduleIndex),
                ("@eventId", JoustConfig.EventId));

            var updated = rule.Copy();
            updated.CurrentRound = nextRound;
            updated.CurrentDayId = schedule.DayId;
            updated.CurrentScheduleIndex = schedule.ScheduleIndex;
            return updated;
        }

        internal void EnsureRoundSlots(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int roundNo,
            int dayId,
            int scheduleIndex,
            long roundStartUnix,
            JoustConfig config,
            Func<int, int> next)
        {
            var existing = CountRoundSlots(connection, transaction, roundNo);
            if (existing == 8)
                return;

            if (existing != 0)
            {
                throw new InvalidOperationException(
                    $"joust round {roundNo} has partial slot state: {existing}");
            }

            next ??= ServerRandom.Next;
            var normal = config.Knights
                .Where(knight => knight.Index >= 0 && knight.Index <= 7)
                .ToList();
            var black = config.Knights
                .Where(knight => knight.Index >= 8 && knight.Index <= 11)
                .ToList();
            Shuffle(normal, next);
            Shuffle(black, next);

            var selected = normal.Take(7).Concat(black.Take(1)).ToList();
            if (selected.Count != 8)
                throw new InvalidOperationException("joust config cannot create 8 slots.");
            Shuffle(selected, next);

            var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            for (var slotNo = 0; slotNo < selected.Count; slotNo++)
            {
                var knight = selected[slotNo];
                ExecuteNonQuery(
                    connection,
                    transaction,
                    @"
INSERT INTO event_joust_round_slots (
    round_no, slot_no, knight_index, is_black, attack_type,
    condition_index, global_bet_amount, round_day_id, schedule_index,
    round_start_unix_time, created_at_unix, updated_at_unix
) VALUES (
    @roundNo, @slotNo, @knightIndex, @isBlack, @attackType,
    @conditionIndex, 0, @dayId, @scheduleIndex,
    @roundStartUnix, @nowUnix, @nowUnix
);",
                    ("@roundNo", roundNo),
                    ("@slotNo", slotNo),
                    ("@knightIndex", knight.Index),
                    ("@isBlack", knight.Index >= 8 ? 1 : 0),
                    ("@attackType", knight.AttackType),
                    ("@conditionIndex", next(5)),
                    ("@dayId", dayId),
                    ("@scheduleIndex", scheduleIndex),
                    ("@roundStartUnix", roundStartUnix),
                    ("@nowUnix", nowUnix));
            }
        }

        internal IReadOnlyList<JoustRoundSlot> LoadRoundSlots(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int roundNo)
        {
            var slots = new List<JoustRoundSlot>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT s.round_no,
       s.slot_no,
       s.knight_index,
       s.is_black,
       s.attack_type,
       s.condition_index,
       s.global_bet_amount,
       COALESCE(k.win_count, 0),
       COALESCE(k.loss_count, 0)
FROM event_joust_round_slots s
LEFT JOIN event_joust_knight_stats k ON k.knight_index = s.knight_index
WHERE s.round_no=@roundNo
ORDER BY s.slot_no;";
                command.Parameters.AddWithValue("@roundNo", roundNo);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        slots.Add(new JoustRoundSlot
                        {
                            RoundNo = reader.GetInt32(0),
                            SlotNo = reader.GetInt32(1),
                            KnightIndex = reader.GetInt32(2),
                            IsBlack = reader.GetInt32(3) != 0,
                            AttackType = reader.GetInt32(4),
                            ConditionIndex = reader.GetInt32(5),
                            GlobalBetAmount = reader.GetInt32(6),
                            WinCount = reader.GetInt32(7),
                            LossCount = reader.GetInt32(8),
                        });
                    }
                }
            }

            ApplyOdds(slots);
            return slots;
        }

        internal IReadOnlyList<JoustCharacterBet> LoadCharacterBets(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int roundNo,
            int characterId)
        {
            var bets = new List<JoustCharacterBet>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT slot_no, knight_index, bet_amount
FROM event_joust_character_bets
WHERE round_no=@roundNo AND character_id=@characterId
ORDER BY slot_no;";
                command.Parameters.AddWithValue("@roundNo", roundNo);
                command.Parameters.AddWithValue("@characterId", characterId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        bets.Add(new JoustCharacterBet
                        {
                            SlotNo = reader.GetInt32(0),
                            KnightIndex = reader.GetInt32(1),
                            BetAmount = reader.GetInt32(2),
                        });
                    }
                }
            }

            return bets;
        }

        internal int LoadCharacterBetTotal(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int roundNo,
            int characterId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT COALESCE(SUM(bet_amount), 0)
FROM event_joust_character_bets
WHERE round_no=@roundNo AND character_id=@characterId;";
                command.Parameters.AddWithValue("@roundNo", roundNo);
                command.Parameters.AddWithValue("@characterId", characterId);
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        internal void AddBet(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int roundNo,
            int characterId,
            JoustRoundSlot slot,
            int amount,
            long nowUnix)
        {
            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT INTO event_joust_character_bets (
    round_no, character_id, slot_no, knight_index, bet_amount,
    reward_mail_sent, created_at_unix, updated_at_unix
) VALUES (
    @roundNo, @characterId, @slotNo, @knightIndex, @amount,
    0, @nowUnix, @nowUnix
)
ON CONFLICT(round_no, character_id, slot_no) DO UPDATE SET
    bet_amount = event_joust_character_bets.bet_amount + excluded.bet_amount,
    updated_at_unix = excluded.updated_at_unix;",
                ("@roundNo", roundNo),
                ("@characterId", characterId),
                ("@slotNo", slot.SlotNo),
                ("@knightIndex", slot.KnightIndex),
                ("@amount", amount),
                ("@nowUnix", nowUnix));

            ExecuteNonQuery(
                connection,
                transaction,
                @"
UPDATE event_joust_round_slots
SET global_bet_amount = global_bet_amount + @amount,
    updated_at_unix = @nowUnix
WHERE round_no=@roundNo AND slot_no=@slotNo;",
                ("@amount", amount),
                ("@nowUnix", nowUnix),
                ("@roundNo", roundNo),
                ("@slotNo", slot.SlotNo));
        }

        internal ushort[] LoadBracketSlots(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int roundNo)
        {
            var slots = new ushort[14];
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT slot0, slot1, slot2, slot3, slot4, slot5, slot6,
       slot7, slot8, slot9, slot10, slot11, slot12, slot13
FROM event_joust_results
WHERE round_no=@roundNo;";
                command.Parameters.AddWithValue("@roundNo", roundNo);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return slots;

                    for (var index = 0; index < slots.Length; index++)
                        slots[index] = (ushort)reader.GetInt32(index);
                }
            }

            return slots;
        }

        internal int LoadResultStageIndex(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int roundNo)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT stage_index
FROM event_joust_results
WHERE round_no=@roundNo;";
                command.Parameters.AddWithValue("@roundNo", roundNo);
                var value = command.ExecuteScalar();
                return value == null || value == DBNull.Value
                    ? -1
                    : Convert.ToInt32(value);
            }
        }

        internal void SaveBracketSlots(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int roundNo,
            int stageIndex,
            ushort[] bracketSlots,
            long nowUnix)
        {
            if (bracketSlots == null || bracketSlots.Length != 14)
                throw new ArgumentException("bracket must contain 14 slots.", nameof(bracketSlots));

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO event_joust_results (
    round_no, stage_index,
    slot0, slot1, slot2, slot3, slot4, slot5, slot6,
    slot7, slot8, slot9, slot10, slot11, slot12, slot13,
    updated_at_unix
) VALUES (
    @roundNo, @stageIndex,
    @slot0, @slot1, @slot2, @slot3, @slot4, @slot5, @slot6,
    @slot7, @slot8, @slot9, @slot10, @slot11, @slot12, @slot13,
    @nowUnix
)
ON CONFLICT(round_no) DO UPDATE SET
    stage_index = CASE
        WHEN excluded.stage_index > event_joust_results.stage_index
        THEN excluded.stage_index
        ELSE event_joust_results.stage_index
    END,
    slot0=excluded.slot0,
    slot1=excluded.slot1,
    slot2=excluded.slot2,
    slot3=excluded.slot3,
    slot4=excluded.slot4,
    slot5=excluded.slot5,
    slot6=excluded.slot6,
    slot7=excluded.slot7,
    slot8=excluded.slot8,
    slot9=excluded.slot9,
    slot10=excluded.slot10,
    slot11=excluded.slot11,
    slot12=excluded.slot12,
    slot13=excluded.slot13,
    updated_at_unix=excluded.updated_at_unix;";
                command.Parameters.AddWithValue("@roundNo", roundNo);
                command.Parameters.AddWithValue("@stageIndex", stageIndex);
                command.Parameters.AddWithValue("@nowUnix", nowUnix);
                for (var index = 0; index < bracketSlots.Length; index++)
                    command.Parameters.AddWithValue("@slot" + index, (int)bracketSlots[index]);
                command.ExecuteNonQuery();
            }
        }

        internal bool IsMatchResolved(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int roundNo,
            int stageIndex,
            int matchIndex)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT COUNT(*)
FROM event_joust_match_results
WHERE round_no=@roundNo
  AND stage_index=@stageIndex
  AND match_index=@matchIndex;";
                command.Parameters.AddWithValue("@roundNo", roundNo);
                command.Parameters.AddWithValue("@stageIndex", stageIndex);
                command.Parameters.AddWithValue("@matchIndex", matchIndex);
                return Convert.ToInt32(command.ExecuteScalar()) != 0;
            }
        }

        internal bool InsertMatchResultIfNew(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int roundNo,
            int stageIndex,
            int matchIndex,
            JoustRoundSlot winner,
            JoustRoundSlot loser,
            long nowUnix)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT OR IGNORE INTO event_joust_match_results (
    round_no, stage_index, match_index,
    winner_slot_no, loser_slot_no,
    winner_knight_index, loser_knight_index,
    resolved_at_unix
) VALUES (
    @roundNo, @stageIndex, @matchIndex,
    @winnerSlotNo, @loserSlotNo,
    @winnerKnightIndex, @loserKnightIndex,
    @nowUnix
);";
                command.Parameters.AddWithValue("@roundNo", roundNo);
                command.Parameters.AddWithValue("@stageIndex", stageIndex);
                command.Parameters.AddWithValue("@matchIndex", matchIndex);
                command.Parameters.AddWithValue("@winnerSlotNo", winner.SlotNo);
                command.Parameters.AddWithValue("@loserSlotNo", loser.SlotNo);
                command.Parameters.AddWithValue("@winnerKnightIndex", winner.KnightIndex);
                command.Parameters.AddWithValue("@loserKnightIndex", loser.KnightIndex);
                command.Parameters.AddWithValue("@nowUnix", nowUnix);
                if (command.ExecuteNonQuery() != 1)
                    return false;
            }

            UpdateKnightRecord(connection, transaction, winner.KnightIndex, winDelta: 1, lossDelta: 0, nowUnix);
            UpdateKnightRecord(connection, transaction, loser.KnightIndex, winDelta: 0, lossDelta: 1, nowUnix);
            return true;
        }

        internal void InsertHistoryIfMissing(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int roundNo,
            int winnerHorseId,
            int oddsX10,
            long nowUnix)
        {
            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT OR IGNORE INTO event_joust_history (
    round_no, winner_horse_id, odds_x10, settled_at_unix
) VALUES (
    @roundNo, @winnerHorseId, @oddsX10, @nowUnix
);",
                ("@roundNo", roundNo),
                ("@winnerHorseId", winnerHorseId),
                ("@oddsX10", oddsX10),
                ("@nowUnix", nowUnix));
        }

        internal IReadOnlyList<JoustHistoryEntry> LoadHistory(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int limit)
        {
            var history = new List<JoustHistoryEntry>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT round_no, winner_horse_id, odds_x10
FROM (
    SELECT round_no, winner_horse_id, odds_x10
    FROM event_joust_history
    ORDER BY round_no DESC
    LIMIT @limit
)
ORDER BY round_no ASC;";
                command.Parameters.AddWithValue("@limit", Math.Max(1, limit));
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        history.Add(new JoustHistoryEntry
                        {
                            RoundNo = (ushort)reader.GetInt32(0),
                            WinnerHorseId = (byte)reader.GetInt32(1),
                            OddsX10 = reader.GetInt32(2),
                        });
                    }
                }
            }

            return history;
        }

        internal IReadOnlyList<JoustRewardRecipient> LoadUnsettledRewardRecipients(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int roundNo)
        {
            var recipients = new List<JoustRewardRecipient>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT b.character_id,
       c.account_id,
       c.name,
       c.level,
       SUM(b.bet_amount)
FROM event_joust_character_bets b
JOIN characters c ON c.character_id = b.character_id
WHERE b.round_no=@roundNo
  AND b.reward_mail_sent=0
  AND b.bet_amount > 0
  AND c.delete_flag=0
GROUP BY b.character_id, c.account_id, c.name, c.level
ORDER BY b.character_id;";
                command.Parameters.AddWithValue("@roundNo", roundNo);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        recipients.Add(new JoustRewardRecipient
                        {
                            CharacterId = reader.GetInt32(0),
                            AccountId = reader.GetInt32(1),
                            Name = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                            Level = reader.GetInt32(3),
                            TotalBetAmount = Convert.ToInt32(reader.GetInt64(4)),
                        });
                    }
                }
            }

            return recipients;
        }

        internal void MarkRewardsSent(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int roundNo,
            int characterId,
            long nowUnix)
        {
            ExecuteNonQuery(
                connection,
                transaction,
                @"
UPDATE event_joust_character_bets
SET reward_mail_sent=1,
    reward_mail_sent_at=@nowUnix,
    updated_at_unix=@nowUnix
WHERE round_no=@roundNo AND character_id=@characterId;",
                ("@nowUnix", nowUnix),
                ("@roundNo", roundNo),
                ("@characterId", characterId));
        }

        private static void ApplyOdds(List<JoustRoundSlot> slots)
        {
            var total = slots.Sum(slot => Math.Max(0, slot.GlobalBetAmount));
            foreach (var slot in slots)
                slot.OddsX10 = CalculateOddsX10(total, slot.GlobalBetAmount);
        }

        internal static int CalculateOddsX10(int totalBetAmount, int horseBetAmount)
        {
            if (totalBetAmount <= 0 || horseBetAmount <= 0)
                return 80;

            return Math.Max(
                1,
                (int)Math.Round(
                    totalBetAmount * 10d / horseBetAmount,
                    MidpointRounding.AwayFromZero));
        }

        private int LoadMaxKnownRound(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT COALESCE(MAX(round_no), 0)
FROM (
    SELECT round_no FROM event_joust_round_slots
    UNION ALL
    SELECT round_no FROM event_joust_character_bets
    UNION ALL
    SELECT round_no FROM event_joust_results
    UNION ALL
    SELECT round_no FROM event_joust_match_results
    UNION ALL
    SELECT round_no FROM event_joust_history
);";
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private int CountRoundSlots(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int roundNo)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT COUNT(*)
FROM event_joust_round_slots
WHERE round_no=@roundNo;";
                command.Parameters.AddWithValue("@roundNo", roundNo);
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private void UpdateKnightRecord(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int knightIndex,
            int winDelta,
            int lossDelta,
            long nowUnix)
        {
            ExecuteNonQuery(
                connection,
                transaction,
                @"
INSERT OR IGNORE INTO event_joust_knight_stats (
    knight_index, win_count, loss_count, updated_at_unix
) VALUES (
    @knightIndex, 0, 0, @nowUnix
);",
                ("@knightIndex", knightIndex),
                ("@nowUnix", nowUnix));

            ExecuteNonQuery(
                connection,
                transaction,
                @"
UPDATE event_joust_knight_stats
SET win_count = win_count + @winDelta,
    loss_count = loss_count + @lossDelta,
    updated_at_unix = @nowUnix
WHERE knight_index=@knightIndex;",
                ("@winDelta", winDelta),
                ("@lossDelta", lossDelta),
                ("@nowUnix", nowUnix),
                ("@knightIndex", knightIndex));
        }

        private static void Shuffle<T>(IList<T> values, Func<int, int> next)
        {
            for (var index = values.Count - 1; index > 0; index--)
            {
                var other = next(index + 1);
                (values[index], values[other]) = (values[other], values[index]);
            }
        }

        private static void ExecuteNonQuery(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                foreach (var parameter in parameters)
                    command.Parameters.AddWithValue(parameter.Name, parameter.Value);
                command.ExecuteNonQuery();
            }
        }
    }
}
