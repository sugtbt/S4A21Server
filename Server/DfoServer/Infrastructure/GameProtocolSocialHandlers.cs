using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Dungeon;
using System;

namespace DfoServer.Infrastructure
{
    // 与会话目录、队伍状态和 UDP Relay 绑定的社交/PvP 协议模块。
    internal sealed class GameProtocolSocialHandlers : IDisposable
    {
        private bool _disposed;

        internal GameProtocolSocialHandlers(
            PartyHandler party,
            RaidHandler raid,
            ChatHandler chat,
            DungeonLoadingCoordinator dungeonLoading,
            DungeonRejoinCoordinator dungeonRejoin,
            PvpChannelInfoHandler pvpChannelInfo,
            PvpRoomHandler pvpRoom)
        {
            Party = party ?? throw new ArgumentNullException(nameof(party));
            Raid = raid ?? throw new ArgumentNullException(nameof(raid));
            Chat = chat ?? throw new ArgumentNullException(nameof(chat));
            DungeonLoading = dungeonLoading
                ?? throw new ArgumentNullException(nameof(dungeonLoading));
            DungeonRejoin = dungeonRejoin
                ?? throw new ArgumentNullException(nameof(dungeonRejoin));
            PvpChannelInfo = pvpChannelInfo
                ?? throw new ArgumentNullException(nameof(pvpChannelInfo));
            PvpRoom = pvpRoom
                ?? throw new ArgumentNullException(nameof(pvpRoom));
        }

        internal PartyHandler Party { get; }

        internal RaidHandler Raid { get; }

        internal ChatHandler Chat { get; }

        internal DungeonLoadingCoordinator DungeonLoading { get; }

        internal DungeonRejoinCoordinator DungeonRejoin { get; }

        internal PvpChannelInfoHandler PvpChannelInfo { get; }

        internal PvpRoomHandler PvpRoom { get; }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            Chat.Dispose();
            PvpRoom.Dispose();
            Party.Dispose();
        }
    }
}
