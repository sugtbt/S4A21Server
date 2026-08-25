using DfoServer.Game.Dungeon;
using DfoServer.Game.Dungeon.Tournament;
using System;

namespace DfoServer.Network.Builders
{
    internal static class TournamentPacketBuilder
    {
        internal static byte[] BuildTournamentInfo(
            TournamentDungeonRuntime runtime,
            byte difficulty,
            ushort firstMonsterSequence)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt32((uint)runtime.Definition.DungeonId);
            writer.WriteByte(difficulty);
            writer.WriteByte((byte)runtime.Definition.PartyLimit);
            foreach (var round in runtime.Rounds)
            {
                writer.WriteByte(round.Number);
                writer.WriteByte((byte)round.Teams.Count);
                foreach (var team in round.Teams)
                {
                    writer.WriteByte(team.Position);
                    foreach (var member in team.Members)
                    {
                        writer.WriteInt32(member.Code);
                        writer.WriteUInt16((ushort)Math.Max(
                            0,
                            Math.Min(ushort.MaxValue, member.Strength)));
                    }
                }
            }

            var sequence = firstMonsterSequence;
            var actorIndex = 0;
            foreach (var round in runtime.Rounds)
            {
                writer.WriteByte(round.Number);
                for (var member = 0;
                    member < runtime.Definition.PartyLimit;
                    member++)
                {
                    var actor = runtime.PathActors[actorIndex++];
                    writer.WriteUInt16(sequence++);
                    writer.WriteInt32(actor.Code);
                    writer.WriteByte(actor.Level);
                    writer.WriteByte(actor.Type);
                }
            }
            return writer.ToArray();
        }

        internal static byte[] BuildTournamentMapInfo(
            byte x,
            byte y,
            uint seed,
            uint mapId,
            bool revisit)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(x);
            writer.WriteByte(y);
            writer.WriteUInt32(seed);
            writer.WriteByte(0);
            writer.WriteByte(revisit ? (byte)0 : (byte)1);
            if (!revisit)
            {
                writer.WriteUInt32(mapId);
                writer.WriteByte(0);
                writer.WriteByte(0);
                writer.WriteByte(0);
            }
            return writer.ToArray();
        }

        internal static byte[] BuildTournamentClearReward(
            TournamentParticipantRewardState rewards)
        {
            if (rewards == null)
                throw new ArgumentNullException(nameof(rewards));

            var writer = new GamePacketWriter();
            var runtimeExperience = rewards.RewardExperience;
            writer.WriteByte(rewards.CompletedRounds);
            writer.WriteUInt32(runtimeExperience);
            writer.WriteByte(0);
            for (var type = 0;
                type < TournamentParticipantRewardState.CardTypeCount;
                type++)
            {
                var cardCount = rewards.GetCardCount(type);
                writer.WriteByte(cardCount);
                for (var index = 0; index < cardCount; index++)
                {
                    var reward = rewards.GetReward(type, index);
                    writer.WriteInt32(reward.IsGold ? 0 : reward.ItemId);
                    writer.WriteInt32(reward.IsGold
                        ? reward.GoldAmount
                        : reward.StackCount);
                    writer.WriteUInt16(0);
                }
            }
            return writer.ToArray();
        }

        internal static byte[] BuildTournamentRewardSelectState(
            TournamentParticipantRewardState rewards)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            for (var type = 0;
                type < TournamentParticipantRewardState.CardTypeCount;
                type++)
            {
                for (var slot = 0;
                    slot < TournamentParticipantRewardState.PartySlotCount;
                    slot++)
                {
                    writer.WriteByte(rewards.IsCardTypeEnabled(type)
                        && rewards.IsPartySlotPresent(slot)
                        ? (byte)1
                        : byte.MaxValue);
                }
            }
            return writer.ToArray();
        }

        internal static byte[] BuildTournamentRewardSelection(
            TournamentParticipantRewardState rewards)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            for (var type = 0;
                type < TournamentParticipantRewardState.CardTypeCount;
                type++)
            {
                var cardCount = rewards.GetCardCount(type);
                writer.WriteByte(cardCount);
                for (var index = 0; index < cardCount; index++)
                    writer.WriteByte(rewards.GetSelection(type, index));
            }
            return writer.ToArray();
        }
    }
}
