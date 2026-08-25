using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Game.Events.Joust;

namespace DfoServer.Network.Builders.Events
{
    internal static class JoustPacketBuilder
    {
        internal const int InfoBodyLength = 90;
        internal const int BettingInfoBodyLength = 46;
        internal const int MatchResultBodyLength = 31;
        internal const int MatchHistoryBodyLength = 3505;

        internal static byte[] BuildState(JoustStateSnapshot state)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(state?.RoundNo ?? 0);
            writer.WriteByte((byte)(state?.Phase ?? JoustPhase.Closed));
            return writer.ToArray();
        }

        internal static byte[] BuildInfo(JoustSnapshot snapshot)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(snapshot?.RoundNo ?? 0);

            var slots = (snapshot?.Slots ?? Array.Empty<JoustRoundSlot>())
                .OrderBy(slot => slot.SlotNo)
                .ToList();
            for (var index = 0; index < 8; index++)
            {
                var slot = index < slots.Count ? slots[index] : null;
                if (slot == null)
                {
                    writer.WriteByte(0xFF);
                    writer.WriteByte(0);
                    writer.WriteSingle(0);
                    writer.WriteUInt16(0);
                    writer.WriteUInt16(0);
                    writer.WriteByte(0);
                    continue;
                }

                writer.WriteByte((byte)Math.Max(0, Math.Min(byte.MaxValue, slot.KnightIndex)));
                writer.WriteByte((byte)Math.Max(0, Math.Min(byte.MaxValue, slot.ConditionIndex)));
                writer.WriteSingle(slot.OddsX10 / 10f);
                writer.WriteUInt16((ushort)Math.Max(0, Math.Min(ushort.MaxValue, slot.WinCount)));
                writer.WriteUInt16((ushort)Math.Max(0, Math.Min(ushort.MaxValue, slot.LossCount)));
                writer.WriteByte(ShouldHideBlackHorse(snapshot.Phase, slot) ? (byte)1 : (byte)0);
            }

            return writer.ToArray();
        }

        internal static byte[] BuildBettingInfo(JoustSnapshot snapshot)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(snapshot?.RoundNo ?? 0);
            writer.WriteInt32(Math.Max(0, snapshot?.CharacterTotalBet ?? 0));

            var betsByHorse = (snapshot?.Bets ?? Array.Empty<JoustCharacterBet>())
                .GroupBy(bet => bet.KnightIndex)
                .ToDictionary(
                    group => group.Key,
                    group => group.Sum(bet => Math.Max(0, bet.BetAmount)));
            var slots = (snapshot?.Slots ?? Array.Empty<JoustRoundSlot>())
                .OrderBy(slot => slot.SlotNo)
                .ToList();
            for (var index = 0; index < 8; index++)
            {
                var horseId = index < slots.Count ? slots[index].KnightIndex : 0;
                writer.WriteByte((byte)Math.Max(0, Math.Min(byte.MaxValue, horseId)));
                writer.WriteInt32(
                    betsByHorse.TryGetValue(horseId, out var amount)
                        ? Math.Max(0, amount)
                        : 0);
            }

            return writer.ToArray();
        }

        internal static byte[] BuildMatchResult(JoustSnapshot snapshot)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(0);
            writer.WriteByte((byte)Math.Max(0, Math.Min(byte.MaxValue, snapshot?.CurrentResultStageIndex ?? 0)));

            var bracket = snapshot?.BracketSlots ?? Array.Empty<ushort>();
            for (var index = 0; index < 14; index++)
                writer.WriteUInt16(index < bracket.Length ? bracket[index] : (ushort)0);
            return writer.ToArray();
        }

        internal static byte[] BuildMatchHistoryAck(
            IReadOnlyList<JoustHistoryEntry> history)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt32(0);

            var entries = history ?? Array.Empty<JoustHistoryEntry>();
            for (var index = 0; index < 500; index++)
            {
                var entry = index < entries.Count ? entries[index] : null;
                writer.WriteUInt16(entry?.RoundNo ?? 0);
                writer.WriteByte(entry?.WinnerHorseId ?? 0);
                writer.WriteSingle((entry?.OddsX10 ?? 0) / 10f);
            }

            return writer.ToArray();
        }

        internal static byte[] BuildJoustInfoClosedAck()
        {
            return new byte[] { 1, 6, 0, 0, 0 };
        }

        internal static byte[] BuildJoustBettingAck(bool success)
        {
            return new byte[] { 1, success ? (byte)0 : (byte)6, 0, 0, 0 };
        }

        private static bool ShouldHideBlackHorse(
            JoustPhase phase,
            JoustRoundSlot slot)
        {
            return slot.IsBlack
                && phase != JoustPhase.Racing
                && phase != JoustPhase.ResultReview;
        }
    }
}
