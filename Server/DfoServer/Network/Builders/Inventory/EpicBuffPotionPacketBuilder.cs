using DfoServer.Game.Inventory;

namespace DfoServer.Network.Builders
{
    internal static class EpicBuffPotionPacketBuilder
    {
        internal static byte[] BuildAddBuffBody()
        {
            return SpecialDungeonNotificationBuilder.BuildCharacterAddBuff(
                EpicBuffPotionDefinition.BuffId,
                0,
                0,
                0);
        }

        internal static byte[] BuildRemoveBuffBody()
        {
            return SpecialDungeonNotificationBuilder.BuildCharacterRemoveBuff(
                new[] { EpicBuffPotionDefinition.BuffId });
        }
    }
}
