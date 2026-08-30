using DfoServer.Game.Inventory;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        // 请求 body(剥头后, 实测): [inven_type:1][slot:2 LE][repair_item_slot:2 LE][auto:1][pad:1][quick:1]
        //   inven_type: 0=背包/快捷栏, 3=穿戴装备, 2=货柜
        //   slot: 0xFFFF(-1)=全部修理, 否则指定槽
        //   body[5] auto: 1=魔王契约"自动修理"触发(耐久到0系统自动修, 免费), 0=手动
        //   body[7] quick: 1=侧边栏快速修理(费用×1.5), 0=普通商店价  (自动修理包只有7字节, 无此字节)
        // 回包 body(9B): [01成功标志][剩余金币:4 LE][inven_type:1][slot:2 LE][00 00]
        public async Task Handle_ENUM_CMDPACKET_REPAIR_EQUIPMENT(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 5)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0017, RepairEquipmentAckBuilder.BuildError(0x0A)));
                return;
            }

            var invenType = body[0];
            var slot = BitConverter.ToInt16(body, 1);
            var autoRepair = body.Length >= 6 && body[5] == 1;   // 魔王契约自动修理触发
            var quickRepair = body.Length >= 8 && body[7] == 1;  // 侧边栏快速修理

            var (cid, aid) = ResolveOwner(session);
            var freeRepair = false;
            FileLogger.Log($"[{ProtocolName}] REPAIR_EQUIPMENT raw body({body.Length}B): {BitConverter.ToString(body)} auto={autoRepair} quick={quickRepair}");

            var listType = MapInvenTypeToListType(invenType);
            if (listType == null)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0017, RepairEquipmentAckBuilder.BuildError(0x11)));
                return;
            }

            RepairEquipmentResult result = null;
            bool ok;
            if (TryGetOwnedInventoryLease(session, cid, out var lease))
            {
                ok = OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "repair-equipment",
                    (connection, transaction) =>
                    {
                        if (autoRepair)
                        {
                            var usage = new Game.Premium.DevilContractUsagePolicy(
                                _database);
                            freeRepair = usage.TryConsume(
                                connection,
                                transaction,
                                cid,
                                aid,
                                Game.Premium.DevilContractUsagePolicy.AutoRepairSlot);
                            if (!freeRepair)
                                return false;
                        }

                        return InventoryRepairService.TryRepairEquipment(
                            lease.Inventory,
                            listType.Value,
                            slot,
                            quickRepair,
                            freeRepair,
                            out result);
                    });
            }
            else
            {
                ok = false;
            }

            if (!ok)
            {
                FileLogger.Log($"[{ProtocolName}] REPAIR_EQUIPMENT: FAILED inven_type={invenType} slot={slot}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0017, RepairEquipmentAckBuilder.BuildError(0x0A)));
                return;
            }

            // 全部修理只回一个 slot=0xFFFF 的 ACK, 客户端据此自己把全身耐久本地拉满(客户端 handler sub_CD7C50 逻辑)。
            short ackSlot = (slot == -1) ? unchecked((short)0xFFFF) : result.SlotIndex;
            var ackBody = RepairEquipmentAckBuilder.Build(invenType, ackSlot, result.UpdatedGold);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0017, ackBody));
            if (freeRepair)
            {
                await Game.Premium.PremiumService.SendPremiumServiceRefresh(
                    session,
                    aid,
                    _database);
            }

            FileLogger.Log($"[{ProtocolName}] REPAIR_EQUIPMENT: OK inven_type={invenType} ackSlot=0x{(ushort)ackSlot:X4} cost={result.Cost} freeContract={freeRepair} remainGold={result.UpdatedGold}");
        }

        private static InventoryListType? MapInvenTypeToListType(byte invenType)
        {
            switch (invenType)
            {
                case 0: return InventoryListType.Main;          // 背包/快捷栏
                case 3: return InventoryListType.Equipment;     // 穿戴装备
                case 2: return InventoryListType.PersonalCargo; // 货柜
                default: return null;
            }
        }
    }
}
