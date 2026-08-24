using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    /// <summary>
    /// 城镇区域离开名册对账（城镇残留白影修复，参照 86JP 已知协议验证）。
    /// 玩家离开某 (oldTownId, oldAreaId) 后，向该区域广播【不含离开者】的 AREA_USERS(0x0018)
    /// 权威名册，客户端按名册重建实体列表、移除残留白影。不依赖 USER_LEAVE，故不触碰
    /// 队伍成员对象（USER_LEAVE 会销毁队伍成员对象、组队进本时队友客户端闪退）。
    ///
    /// 使用方两处复用同一机制，不复制逻辑：
    ///   - TownHandler.SetUserAreaCoreAsync（城镇内切区域/切城镇）
    ///   - DungeonEntryHandler（进本提交后离开城镇，含队伍队员）
    ///
    /// env DFO_AREA_LEAVE_NOTIFY 三档：
    ///   0 = 不通知（保持旧行为，残留白影仍会残留）
    ///   1（默认）= 名册对账：向旧区域广播不含离开者的 AREA_USERS(0x0018)
    ///   2 = 位置迁移备选：向旧区域广播 USER_AREA(0x0017) 离开者当前投影（仅诊断用）
    /// ⚠️不要向旧区域广播 USER_LEAVE(0x0006)：会销毁队伍成员对象、组队进本时队友客户端闪退。
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
                var roster = new List<TownUserSnapshot>(remaining.Count);
                foreach (var o in remaining)
                    roster.Add(TownAreaNotificationBuilder.CreateCurrentSnapshot(o.Player));
                await sessions.BroadcastToAreaAsync(
                    oldTownId,
                    oldAreaId,
                    session.Player.CharacterId,
                    GamePacketEnvelopeBuilder.Build(0x00, 0x0018,
                        TownAreaNotificationBuilder.BuildAreaUsers(oldTownId, oldAreaId, roster)),
                    session.ListenerPort);
            }
            else if (_areaLeaveNotifyMode == 2)
            {
                // 位置迁移备选（仅诊断）：广播离开者当前投影。对进本场景 CurTownId/CurAreaId
                // 仍是进本前城镇值；对 SET_USER_AREA 场景已是新区域值 —— 两种都有诊断意义。
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
