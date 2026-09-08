using System;
using System.Threading.Tasks;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Friends;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class DungeonTownReturnCoordinator
    {
        private readonly DungeonInstanceRegistry _instanceRegistry;
        private readonly DungeonProgressNotificationProjector
            _progressNotifications;
        private readonly ISessionDirectory _sessions;
        private Func<EnhancedClientSession, DungeonRunIdentity, Task>
            _projectTownPresence;

        internal DungeonTownReturnCoordinator(
            DungeonInstanceRegistry instanceRegistry,
            DungeonProgressNotificationProjector progressNotifications,
            ISessionDirectory sessions = null)
        {
            _instanceRegistry = instanceRegistry
                ?? throw new ArgumentNullException(nameof(instanceRegistry));
            _progressNotifications = progressNotifications
                ?? throw new ArgumentNullException(nameof(progressNotifications));
            _sessions = sessions;
        }

        internal void ConfigureTownPresenceProjection(
            Func<EnhancedClientSession, DungeonRunIdentity, Task> projection)
        {
            _projectTownPresence = projection
                ?? throw new ArgumentNullException(nameof(projection));
        }

        internal async Task<bool> ReturnAsync(
            EnhancedClientSession session,
            DungeonRunIdentity runIdentity,
            DungeonRunEndReason reason = DungeonRunEndReason.ReturnToTown,
            ushort? successAckPacketType = null)
        {
            var sourceRun = session?.Player?.CurrentRun;
            if (sourceRun == null || !sourceRun.Matches(runIdentity))
                return false;
            var returnAnchor = sourceRun.TownReturnAnchor;
            if (!await DungeonRunLifecycle.EndRunAsync(
                    session,
                    reason,
                    runIdentity,
                    _instanceRegistry))
            {
                return false;
            }

            return await ProjectEndedRunAsync(
                session,
                runIdentity,
                returnAnchor,
                successAckPacketType);
        }

        internal async Task<bool> ProjectEndedRunAsync(
            EnhancedClientSession session,
            DungeonRunIdentity runIdentity,
            DungeonTownReturnAnchor returnAnchor,
            ushort? successAckPacketType = null)
        {
            if (!CanProjectEndedTownState(session, runIdentity))
                return true;

            DungeonRunLifecycle.ApplyTownReturnAnchor(
                session.Player,
                returnAnchor,
                session.ListenerPort);
            session.Player.UserState = 0x00;
            // 回城 → 状态回空闲：同频道在线好友推 USERINFO(0x0002) 更新场景实体状态。
            if (_sessions != null)
                await UnitedFriendSystem.NotifyUserStateChanged(
                    session, _sessions);
            var snapshot = TownAreaNotificationBuilder.CreateCurrentSnapshot(
                session.Player);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0003,
                EnterSelectDungeonStateBuilder.BuildUserState(session.Player)));
            if (!CanProjectEndedTownState(session, runIdentity))
                return true;
            var projectTownPresence = _projectTownPresence;
            var projectedTownPresence = false;
            if (projectTownPresence != null)
            {
                try
                {
                    await projectTownPresence(session, runIdentity);
                    projectedTownPresence = true;
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[{DungeonSharedServices.ProtocolLogName}] " +
                        $"town presence projection failed; using self-only fallback " +
                        $"cid={session.Player.CharacterId} " +
                        $"run={runIdentity.RunId}/{runIdentity.RunGeneration}: " +
                        ex.Message);
                }
            }
            if (!projectedTownPresence)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x0017,
                    TownAreaNotificationBuilder.BuildUserArea(snapshot)));
                if (!CanProjectEndedTownState(
                        session,
                        runIdentity))
                {
                    return true;
                }
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x0018,
                    TownAreaNotificationBuilder.BuildAreaUsers(snapshot)));
            }
            if (!CanProjectEndedTownState(session, runIdentity))
                return true;
            if (successAckPacketType.HasValue)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    successAckPacketType.Value,
                    CommonPacketBodyBuilder.BuildSuccessAck()));
                if (!CanProjectEndedTownState(session, runIdentity))
                    return true;
            }
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x00CA,
                new byte[] { 0x00 }));
            if (!CanProjectEndedTownState(session, runIdentity))
                return true;
            await _progressNotifications.SendUserInfoSubtype0Broadcast(session);
            // 回城过图后客户端重置结婚属性 UI：subtype0 广播之后补发婚礼回放三包（与选角序列同包体）。
            await InventoryRefreshSender.SendWeddingReplayRefresh(session);
            FileLogger.Log(
                $"[{DungeonSharedServices.ProtocolLogName}] " +
                $"ReturnToVillage: town state + subtype0 sent " +
                $"run={runIdentity.RunId}/{runIdentity.RunGeneration}");
            return true;
        }

        private static bool CanProjectEndedTownState(
            EnhancedClientSession session,
            DungeonRunIdentity runIdentity)
            => DungeonRunLifecycle.CanProjectTownState(session, runIdentity)
               && session.Player.CurrentDungeonSelection == null;
    }
}
