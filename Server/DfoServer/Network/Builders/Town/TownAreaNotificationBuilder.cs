using System.Collections.Generic;
using DfoServer.Game.Session;
using DfoServer.Network;

namespace DfoServer.Network.Builders
{
    public static class TownAreaNotificationBuilder
    {
        public static TownUserSnapshot CreateCurrentSnapshot(PlayerContext player)
        {
            return new TownUserSnapshot
            {
                UserId = player.UserId,
                TownId = player.CurTownId,
                AreaId = player.CurAreaId,
                PosX = player.CurPosX,
                PosY = player.CurPosY,
                Direction = player.CurDirection,
                State = player.CurAreaState,
            };
        }

        public static byte[] BuildUserArea(TownUserSnapshot snapshot)
        {
            var writer = new GamePacketWriter();

            writer.WriteUInt16(snapshot.UserId);
            writer.WriteByte(snapshot.TownId);
            writer.WriteByte(snapshot.AreaId);
            writer.WriteInt16(snapshot.PosX);
            writer.WriteInt16(snapshot.PosY);
            writer.WriteByte(snapshot.Direction);
            writer.WriteByte(snapshot.State);
            return writer.ToArray();
        }

        // 单人重载: 保持既有(已验证的单机)行为——AREA_USERS 里含自己 count=1。
        public static byte[] BuildAreaUsers(TownUserSnapshot snapshot)
        {
            return BuildAreaUsers(snapshot.TownId, snapshot.AreaId, new[] { snapshot });
        }

        /// <summary>
        /// AREA_USERS(0x0018): 区域内玩家名册。字节布局与既有单人版一致
        /// (townId, areaId, uint16 count, 每人[uint16 userId, int16 x, int16 y, byte dir, byte state]),
        /// 仅把 count 从写死 1 改为可变、追加多个 per-user 块 —— 是已验证格式的自然扩展。
        /// </summary>
        public static byte[] BuildAreaUsers(byte townId, byte areaId, IReadOnlyList<TownUserSnapshot> users)
        {
            var writer = new GamePacketWriter();

            writer.WriteByte(townId);
            writer.WriteByte(areaId);
            writer.WriteUInt16((ushort)users.Count);
            foreach (var u in users)
            {
                writer.WriteUInt16(u.UserId);
                writer.WriteInt16(u.PosX);
                writer.WriteInt16(u.PosY);
                writer.WriteByte(u.Direction);
                writer.WriteByte(u.State);
            }
            return writer.ToArray();
        }

        /// <summary>
        /// USER_POSITION(0x0016) NOTI: 把某玩家的移动广播给同区域其它人。
        /// A21 body: userId + x + y + direction + uint16 motion state.
        /// </summary>
        public static byte[] BuildUserPosition(TownUserSnapshot snapshot)
            => BuildUserPosition(snapshot, 0x0064);

        public static byte[] BuildUserPosition(
            TownUserSnapshot snapshot,
            ushort motionState)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(snapshot.UserId);
            writer.WriteInt16(snapshot.PosX);
            writer.WriteInt16(snapshot.PosY);
            writer.WriteByte(snapshot.Direction);
            writer.WriteUInt16(motionState);
            return writer.ToArray();
        }

        /// <summary>
        /// USER_LEAVE(0x0006) NOTI: 会话真正离开时广播给同区域其它人。
        /// A21 body 已由当前客户端消费者确认是 uint16 userId；消费者还会清同 UID 的队伍槽，
        /// 所以普通区域切换不能复用该通知。
        /// </summary>
        public static byte[] BuildUserLeave(ushort userId)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(userId);
            return writer.ToArray();
        }
    }
}
