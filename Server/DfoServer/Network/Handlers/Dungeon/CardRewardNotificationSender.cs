using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal readonly struct CardRewardPartySlotProjection
    {
        internal CardRewardPartySlotProjection(
            short eligibility,
            byte freeSelectorPartySlot,
            byte paidSelectorPartySlot,
            int paidGold,
            int paidItemId,
            int paidItemCount)
        {
            Eligibility = eligibility;
            FreeSelectorPartySlot = freeSelectorPartySlot;
            PaidSelectorPartySlot = paidSelectorPartySlot;
            PaidGold = paidGold;
            PaidItemId = paidItemId;
            PaidItemCount = paidItemCount;
        }

        internal short Eligibility { get; }
        internal byte FreeSelectorPartySlot { get; }
        internal byte PaidSelectorPartySlot { get; }
        internal int PaidGold { get; }
        internal int PaidItemId { get; }
        internal int PaidItemCount { get; }
    }

    internal sealed class CardRewardPartyProjection
    {
        internal const int WireSlotCount = 8;
        private readonly CardRewardPartySlotProjection[] _slots;

        internal CardRewardPartyProjection(
            IReadOnlyList<CardRewardPartySlotProjection> slots)
        {
            if (slots == null || slots.Count != WireSlotCount)
                throw new System.ArgumentOutOfRangeException(nameof(slots));
            _slots = slots.ToArray();
        }

        internal CardRewardPartySlotProjection GetSlot(int index) =>
            _slots[index];
    }

    internal interface ICardRewardNotificationSender
    {
        Task SendLayoutAsync(
            EnhancedClientSession session,
            CardRewardPartyProjection projection);
        Task SendCardInfoAsync(
            EnhancedClientSession session,
            CardRewardPartyProjection projection);
        Task SendExitAsync(
            EnhancedClientSession session,
            byte state,
            byte option);
        Task SendItemUpdatesAsync(
            EnhancedClientSession session,
            IReadOnlyList<InventorySlotMutation> changes);
    }

    internal sealed class CardRewardNotificationSender
        : ICardRewardNotificationSender
    {
        public async Task SendLayoutAsync(
            EnhancedClientSession session,
            CardRewardPartyProjection projection)
        {
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x0045,
                new byte[] { 0x01 }));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x0046,
                BuildCardLayoutAck(projection)));
        }

        public Task SendCardInfoAsync(
            EnhancedClientSession session,
            CardRewardPartyProjection projection)
            => session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x0047,
                BuildCardInfoAck(projection)));

        public Task SendExitAsync(
            EnhancedClientSession session,
            byte state,
            byte option)
            => session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x0048,
                new byte[] { 0x01, state, option }));

        public async Task SendItemUpdatesAsync(
            EnhancedClientSession session,
            IReadOnlyList<InventorySlotMutation> changes)
        {
            if (changes == null || changes.Count == 0)
                return;
            foreach (var group in changes.GroupBy(change => change.ListType))
            {
                await InventoryRefreshSender.SendOnlineUpdateItemList(
                    session,
                    group.Key,
                    group.Select(change => change.SlotIndex).ToList());
            }
        }

        internal static byte[] BuildCardInfoAck(
            CardRewardPartyProjection projection)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            for (var index = 0;
                 index < CardRewardPartyProjection.WireSlotCount;
                 index++)
            {
                var slot = projection.GetSlot(index);
                if (slot.Eligibility < 0 && index >= 4)
                {
                    writer.WriteByte(0xFF);
                    writer.WriteByte(0xFF);
                    writer.WriteByte(0xFF);
                    writer.WriteByte(0xFF);
                    continue;
                }

                writer.WriteByte(slot.FreeSelectorPartySlot);
                writer.WriteByte(slot.PaidSelectorPartySlot);
                if (slot.PaidSelectorPartySlot != 0xFF)
                {
                    writer.WriteByte(2);
                    writer.WriteUInt32(0);
                    writer.WriteInt32(slot.PaidGold);
                    writer.WriteUInt32((uint)Math.Max(0, slot.PaidItemId));
                    writer.WriteInt32(Math.Max(0, slot.PaidItemCount));
                }
                else
                {
                    writer.WriteByte(0x00);
                }
                writer.WriteByte(0x00);
            }
            return writer.ToArray();
        }

        internal static byte[] BuildCardLayoutAck(
            CardRewardPartyProjection projection)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            for (var index = 0;
                 index < CardRewardPartyProjection.WireSlotCount;
                 index++)
            {
                writer.WriteUInt16(unchecked((ushort)projection
                    .GetSlot(index).Eligibility));
            }
            return writer.ToArray();
        }
    }
}
