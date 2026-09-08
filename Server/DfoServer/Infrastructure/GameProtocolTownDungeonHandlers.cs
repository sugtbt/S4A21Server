using DfoServer.Game.ReviveCoin;
using DfoServer.Network.Handlers;
using System;

namespace DfoServer.Infrastructure
{
    // 城镇与地下城共享同一在线世界状态和复活币服务。
    internal sealed class GameProtocolTownDungeonHandlers
    {
        internal GameProtocolTownDungeonHandlers(
            ReviveCoinService reviveCoin,
            TownHandler town,
            DungeonHandler dungeon)
        {
            ReviveCoin = reviveCoin
                ?? throw new ArgumentNullException(nameof(reviveCoin));
            Town = town ?? throw new ArgumentNullException(nameof(town));
            Dungeon = dungeon ?? throw new ArgumentNullException(nameof(dungeon));
            Dungeon.ConfigureTownPresenceProjection(
                Town.ProjectDungeonTownPresenceAsync);
            Dungeon.ConfigureRejectedPartySelectionReturn(
                Town.ReturnRejectedPartySelectionToTownAsync);
        }

        internal ReviveCoinService ReviveCoin { get; }

        internal TownHandler Town { get; }

        internal DungeonHandler Dungeon { get; }
    }
}
