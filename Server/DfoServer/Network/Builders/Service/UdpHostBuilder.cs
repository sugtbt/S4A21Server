using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class UdpHostBuilder
    {
        public static byte[] BuildHostSlot(byte slotIndex)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(slotIndex);
            return writer.ToArray();
        }
    }
}
