using System;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Friends;
using DfoServer.Game.Session;
using DfoServer.Network;

namespace DfoServer.SelfTests
{
    public static partial class UnitedFriendSystemSelfTest
    {
        private static async Task RunPvpFriendDepartureTest()
        {
            // Production friend logout precedes PvP's SessionEnding callback.
            // Run the real friend relation, socket and generation checks.
            var sessions = new SessionDirectory();
            using var observer = await FriendPeer.Create(sessions,101,"甲",GameNetworkConfig.FreeDuelGamePort);
            using var leaving = await FriendPeer.Create(sessions,102,"乙",GameNetworkConfig.FreeDuelGamePort);
            observer.Session.Player.UserState=2;
            leaving.Session.Player.UserState=2;
            await UnitedFriendSystem.NotifyPlayerDisconnected(leaving.Session,sessions);
            var logout=observer.Drain();
            Check("PvP friend logout retains offline notice but delegates native UID deletion to PvP lifecycle",
                logout.Any(p=>BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.INOUT_UNITED_SERVER_FRIEND)
                && !logout.Any(p=>BitConverter.ToUInt16(p,1)==(ushort)NotiPacketTypeA21.USER_LEAVE));
        }
    }
}
