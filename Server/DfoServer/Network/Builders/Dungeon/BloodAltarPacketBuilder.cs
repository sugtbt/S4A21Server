using DfoServer.Game.Dungeon;
using DfoServer.Game.Dungeon.BloodAltar;
using DfoServer.GameWorld;
using System;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    internal static class BloodAltarPacketBuilder
    {
        internal const int MaxStandardRewardCards = 10;
        internal const int MaxRewardCards = MaxStandardRewardCards + 1;

        internal static byte[] BuildInfo(
            int dungeonId,
            BloodAltarDungeonKind kind)
        {
            if (dungeonId <= 0)
                throw new ArgumentOutOfRangeException(nameof(dungeonId));

            var writer = new GamePacketWriter();
            writer.WriteUInt32((uint)dungeonId);
            writer.WriteUInt16(0);
            writer.WriteUInt16(
                kind == BloodAltarDungeonKind.Endless
                    ? (ushort)2
                    : (ushort)0);
            writer.WriteUInt32(0);
            return writer.ToArray();
        }

        internal static byte[] BuildStartMap(
            byte x,
            byte y,
            uint seed,
            uint mapId)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(x);
            writer.WriteByte(y);
            writer.WriteInt32(unchecked((int)seed));
            writer.WriteByte(0);
            writer.WriteByte(1);
            writer.WriteUInt32(mapId);
            writer.WriteByte(0);
            writer.WriteByte(0);
            writer.WriteByte(0);
            return writer.ToArray();
        }

        internal static byte[] BuildMonsterSpawn(BloodAltarWave wave)
        {
            var writer = new GamePacketWriter();
            var monsters = wave?.Monsters;
            var count = Math.Min(ushort.MaxValue, monsters?.Count ?? 0);
            writer.WriteUInt16((ushort)count);
            for (var index = 0; index < count; index++)
            {
                var spawn = monsters[index];
                writer.WriteByte(spawn.Variant);
                writer.WriteUInt16(spawn.SequenceId);
                writer.WriteUInt32((uint)spawn.MonsterCode);
                writer.WriteByte(spawn.MonsterType);
                writer.WriteByte(spawn.Level);
                writer.WriteUInt16(spawn.Scale);
                writer.WriteUInt16(spawn.X);
                writer.WriteUInt16(spawn.Y);
                writer.WriteUInt16(spawn.Z);
            }
            writer.WriteUInt16(unchecked((ushort)(wave?.TailValue ?? 0)));
            return writer.ToArray();
        }

        internal static byte[] BuildRoundInterval(
            int zeroBasedRound,
            int intervalMilliseconds)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte((byte)Math.Min(
                byte.MaxValue,
                Math.Max(0, zeroBasedRound + 1)));
            writer.WriteUInt32((uint)Math.Max(0, intervalMilliseconds));
            return writer.ToArray();
        }

        internal static byte[] BuildUltimateDifficultyPrompt(
            int round,
            int timeoutSeconds)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0);
            writer.WriteByte((byte)Math.Min(byte.MaxValue, Math.Max(0, round)));
            writer.WriteUInt16((ushort)Math.Min(
                ushort.MaxValue,
                Math.Max(0, timeoutSeconds)));
            return writer.ToArray();
        }

        internal static byte[] BuildUltimateDifficultyResolved(byte difficulty)
            => new[] { difficulty };

        internal static byte[] BuildUltimateDifficultyConfirmed(byte difficulty)
            => new[] { (byte)1, difficulty };

        internal static byte[] BuildRanking(
            int playTimeMilliseconds,
            int currentRound,
            int bestTimeMilliseconds,
            int bestRound,
            int maxRound,
            uint rewardExperience)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt32((uint)Math.Max(0, playTimeMilliseconds));
            writer.WriteUInt32((uint)Math.Max(0, currentRound));
            writer.WriteUInt32((uint)Math.Max(0, bestTimeMilliseconds));
            writer.WriteUInt32((uint)Math.Max(0, bestRound));
            writer.WriteUInt32((uint)Math.Max(0, maxRound));
            writer.WriteUInt32(rewardExperience);
            return writer.ToArray();
        }

        internal static byte[] BuildReward(
            int currentRound,
            int maxRound,
            IReadOnlyList<ClearRewardGenerator.CardReward> rewards)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte((byte)Math.Min(byte.MaxValue, Math.Max(0, currentRound)));
            writer.WriteByte((byte)Math.Min(byte.MaxValue, Math.Max(0, maxRound)));
            var count = Math.Min(
                MaxRewardCards,
                rewards?.Count ?? 0);
            writer.WriteByte((byte)count);
            for (var index = 0; index < count; index++)
            {
                var reward = rewards[index];
                if (reward.IsGold)
                {
                    writer.WriteInt32(0);
                    writer.WriteInt32(Math.Max(0, reward.GoldAmount));
                }
                else if (reward.ItemId > 0 && reward.StackCount > 0)
                {
                    writer.WriteInt32(reward.ItemId);
                    writer.WriteInt32(reward.StackCount);
                }
                else
                {
                    writer.WriteInt32(-1);
                    writer.WriteInt32(0);
                }
            }
            for (var group = 1; group < 4; group++)
                writer.WriteByte(0);
            return writer.ToArray();
        }

        internal static byte[] BuildExitReady() => new byte[1];

        internal static byte[] BuildEplpCommandAck(byte state, byte option)
            => new[] { (byte)1, state, option };
    }
}
