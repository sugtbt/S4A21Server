using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    public sealed class EpicBuffPotionInitBodyBuilder : IInitPacketBuilder
    {
        public ushort NotiType => (ushort)NotiPacketTypeA21.CHARACTER_ADD_BUFF;

        public bool TryBuild(
            SelectCharacterDataSnapshot snapshot,
            int occurrenceIndex,
            out byte[] body)
        {
            body = null;
            var items = snapshot?.InitializationSnapshot?.EffectItemStates;
            if (items == null)
                return false;

            for (var index = 0; index < items.Count; index++)
            {
                if (!EpicBuffPotionDefinition.IsItem(items[index]?.ItemId ?? 0))
                    continue;

                body = EpicBuffPotionPacketBuilder.BuildAddBuffBody();
                return true;
            }

            return false;
        }
    }
}
