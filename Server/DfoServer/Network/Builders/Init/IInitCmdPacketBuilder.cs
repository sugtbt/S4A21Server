using DfoServer.Game.SelectCharacter;

namespace DfoServer.Network.Builders
{
    public interface IInitCmdPacketBuilder
    {
        ushort CmdType { get; }

        bool TryBuild(SelectCharacterDataSnapshot snapshot, out byte[] body);
    }
}
