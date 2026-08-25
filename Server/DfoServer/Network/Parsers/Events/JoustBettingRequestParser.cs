using System;
using DfoServer.Game.Events.Joust;

namespace DfoServer.Network.Parsers.Events
{
    internal static class JoustBettingRequestParser
    {
        internal static bool TryParse(byte[] body, out JoustBetCommand command)
        {
            return TryRead(body, horseOffset: 14, slotOffset: 15, amountOffset: 19, out command)
                || TryRead(body, horseOffset: 13, slotOffset: 14, amountOffset: 18, out command);
        }

        private static bool TryRead(
            byte[] body,
            int horseOffset,
            int slotOffset,
            int amountOffset,
            out JoustBetCommand command)
        {
            command = null;
            if (body == null
                || horseOffset < 0
                || slotOffset < 0
                || amountOffset < 0
                || body.Length < amountOffset + sizeof(int))
            {
                return false;
            }

            var amount = BitConverter.ToInt32(body, amountOffset);
            if (amount <= 0 || amount > 1000000)
                return false;

            var horseId = body[horseOffset];
            if (horseId > 11)
                return false;

            command = new JoustBetCommand
            {
                HorseId = horseId,
                MaterialSlotIndex = body.Length >= slotOffset + sizeof(short)
                    ? BitConverter.ToInt16(body, slotOffset)
                    : (short)-1,
                Amount = amount,
            };
            return true;
        }
    }
}
