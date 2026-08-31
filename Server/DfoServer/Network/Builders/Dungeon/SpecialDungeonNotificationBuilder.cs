using DfoServer.Network;
using System.Collections.Generic;

namespace DfoServer.Network.Builders
{
    internal static class SpecialDungeonNotificationBuilder
    {
        // NOTI 0x022D / GAUGE_OBJECT_BAR_DATA.
        // Client handler 0x00D0E340 reads one int32 and stores it as the special dungeon gauge value.
        internal static byte[] BuildGaugeObjectBarData(int value)
        {
            var writer = new GamePacketWriter();
            writer.WriteInt32(value);
            return writer.ToArray();
        }

        // NOTI 0x01EA / CHARACTER_BUFF_DUNGEON.
        // Client handler 0x00919F10 reads: u16 count, then repeated u32 buffId.
        // It clears current dungeon buff active flags first, so callers must send
        // the full active buff list instead of only the latest buff.
        internal static byte[] BuildCharacterBuffDungeon(IReadOnlyList<int> buffIds)
        {
            var writer = new GamePacketWriter();
            var count = buffIds?.Count ?? 0;
            writer.WriteUInt16((ushort)count);
            for (var i = 0; i < count; i++)
                writer.WriteInt32(buffIds[i]);

            return writer.ToArray();
        }

        // NOTI 0x01E8 / CHARACTER_ADD_BUFF.
        // A21 client handler 0x00D08EF0 reads: u8 count, then repeated
        // i32 buffId and three int32 state fields.
        // This creates or updates runtime buff entries; 0x01EA only toggles existing entries active.
        internal static byte[] BuildCharacterAddBuff(
            int buffId,
            int field1,
            int field2,
            int field3)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(1);
            writer.WriteInt32(buffId);
            writer.WriteInt32(field1);
            writer.WriteInt32(field2);
            writer.WriteInt32(field3);
            return writer.ToArray();
        }

        // NOTI 0x01E9 / CHARACTER_REMOVE_BUFF.
        // Client handler 0x0091B710 reads: u8 count, then repeated i32 buffId.
        internal static byte[] BuildCharacterRemoveBuff(IReadOnlyList<int> buffIds)
        {
            var writer = new GamePacketWriter();
            var count = buffIds?.Count ?? 0;
            writer.WriteByte((byte)count);
            for (var i = 0; i < count; i++)
                writer.WriteInt32(buffIds[i]);

            return writer.ToArray();
        }

        // NOTI 0x022F / MINIMAP_ICON_INFO.
        // Client handler 0x00CFACF0 reads: u16 count, then repeated u8 x, u8 y, i32 monsterCode.
        // The monster code is matched against the dungeon's [dungeon minimap icon setting] entries.
        internal static byte[] BuildMinimapIconInfo(IReadOnlyList<(byte X, byte Y, int MonsterCode)> entries)
        {
            var writer = new GamePacketWriter();
            var count = entries?.Count ?? 0;
            writer.WriteUInt16((ushort)count);
            for (var i = 0; i < count; i++)
            {
                writer.WriteByte(entries[i].X);
                writer.WriteByte(entries[i].Y);
                writer.WriteInt32(entries[i].MonsterCode);
            }

            return writer.ToArray();
        }

        // NOTI 0x0138 / COMPLETE_CONDITION_PASS_GATE.
        // Client handler 0x00D3A090 consumes i32 + u8. In the current A14
        // function boundary they are not directly used as gate/map ids; the
        // visible effect comes from client-local condition and scene containers.
        internal static byte[] BuildCompleteConditionPassGateTrigger()
        {
            var writer = new GamePacketWriter();
            writer.WriteInt32(0);
            writer.WriteByte(0);
            return writer.ToArray();
        }

        // NOTI-group 0x0211 / SUMMON_MONSTER.
        // S4A14 client handler 0x00CC2C70 reads exactly this prefix for count=1.
        // The command-response group has another 0x0211 handler (0x00D0E790)
        // that consumes only one byte; sending this 14-byte body there triggers
        // OVERFLOW_INFO with body 01-11-02.
        // A21 MeltdownHelpus captures may append a 9-byte compatibility tail, but
        // the current S4A14 client does not consume that tail.
        // i32 state, u8 count, then count entries of u16 key, i32 monsterCode, u8 mode, u16 paramA.
        // In S4A14 0x00CC2C70 routes mode=0 to the object creation path (0x00A2D510);
        // this byte is not the PVF monster type.
        // The older NOTI summon-list experiment needed a next-state value. The
        // verified cmd=1 response is different: it echoes the request state.
        private static byte[] BuildSummonMonsterResponsePrefix(
            int state,
            byte count,
            ushort key,
            int monsterCode,
            byte mode,
            ushort paramA,
            byte[] tail = null)
        {
            var writer = new GamePacketWriter();
            writer.WriteInt32(state);
            writer.WriteByte(count);
            writer.WriteUInt16(key);
            writer.WriteInt32(monsterCode);
            writer.WriteByte(mode);
            writer.WriteUInt16(paramA);
            writer.WriteBytes(tail);
            return writer.ToArray();
        }

        // CMD 0x0211 confirmed by MeltdownHelpus S4A14 testing:
        // u8 result, then the same i32 state/u8 count/summon-record payload.
        internal static byte[] BuildSummonMonsterCommandCreateResponse(
            byte result,
            int state,
            byte count,
            ushort key,
            int monsterCode,
            byte mode,
            ushort paramA)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(result);
            writer.WriteBytes(BuildSummonMonsterResponsePrefix(
                state,
                count,
                key,
                monsterCode,
                mode,
                paramA));
            return writer.ToArray();
        }

    }
}
