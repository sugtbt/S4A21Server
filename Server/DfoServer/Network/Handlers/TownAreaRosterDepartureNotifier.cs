using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    /// 城镇区域离开投影（城镇残留白影修复）。
    /// A21 客户端 AREA_USERS(0x0018) 会 setDrawLoadingMode 并把模块切到 TOWN，
    /// 只能发给正在进场景的本人。向已在场玩家发 0x18 会关掉对方界面、丢掉特效。
    /// 离开者对旧区域用 USER_AREA(0x0017) 且 town/area 与接收者当前区域不同：
    /// 远程分支只移除该 UID 场景投影，不碰队伍槽。
    /// 不要用 USER_LEAVE(0x0006)：会销毁队伍成员对象、组队进本时队友客户端闪退。
    ///
    /// 进本路径复用：DungeonEntryHandler 在 RegisterActiveParticipant 后调用。
    /// 城镇切图由 TownHandler 直接向旧区域发离开者 USER_AREA，不再走这里。
    ///
    /// env DFO_AREA_LEAVE_NOTIFY：
    ///   0 = 不通知
    ///   1（默认）= 向旧区域广播离开者 USER_AREA(0x0017) 远程移除
    ///   2 = 诊断：广播离开者当前投影（进本时仍是旧城镇，可能不会移除）
    /// </summary>
    internal static class TownAreaRosterDepartureNotifier
    {
        internal static async Task NotifyOldAreaDepartureAsync(
            ISessionDirectory sessions,
            EnhancedClientSession session,
            byte oldTownId,
            byte oldAreaId)
        {
            if (_areaLeaveNotifyMode == 0 || sessions == null)
                return;
            if (session?.Player == null || session.Player.CharacterId <= 0)
                return;

            var remaining = sessions.GetSessionsInArea(
                oldTownId,
                oldAreaId,
                session.Player.CharacterId,
                session.ListenerPort);
            if (remaining.Count == 0)
                return;

            if (_areaLeaveNotifyMode == 1)
            {
                await sessions.BroadcastToAreaAsync(
                    oldTownId,
                    oldAreaId,
                    session.Player.CharacterId,
                    GamePacketEnvelopeBuilder.Build(
                        0x00,
                        0x0017,
                        TownAreaNotificationBuilder.BuildUserArea(
                            TownAreaNotificationBuilder.CreateRemoteDepartureSnapshot(
                                session.Player,
                                oldTownId,
                                oldAreaId))),
                    session.ListenerPort);
            }
            else if (_areaLeaveNotifyMode == 2)
            {
                // 诊断：广播离开者当前投影。进本时 CurTown/CurArea 仍是旧城镇。
                await sessions.BroadcastToAreaAsync(
                    oldTownId,
                    oldAreaId,
                    session.Player.CharacterId,
                    GamePacketEnvelopeBuilder.Build(0x00, 0x0017,
                        TownAreaNotificationBuilder.BuildUserArea(
                            TownAreaNotificationBuilder.CreateCurrentSnapshot(session.Player))),
                    session.ListenerPort);
            }

            FileLogger.Log(
                $"[{ProtocolName}] AREA_LEAVE_NOTIFY mode={_areaLeaveNotifyMode} " +
                $"cid={session.Player.CharacterId} from={oldTownId}:{oldAreaId} " +
                $"remaining={remaining.Count}");
        }

        private const string ProtocolName = "AreaRoster";

        private static readonly int _areaLeaveNotifyMode =
            int.TryParse(System.Environment.GetEnvironmentVariable("DFO_AREA_LEAVE_NOTIFY"), out var alm) ? alm : 1;
    }
}
