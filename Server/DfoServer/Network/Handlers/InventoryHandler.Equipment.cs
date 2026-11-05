using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        public async Task Handle_ENCHANT_3RD_CHRONICLE_ITEM(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            // The 86 client dispatches the refine result on the request opcode.
            // 0x0173 is the response used by the older 70 protocol table.
            var responseType = header.type;
            if (!ChronicleRefineRequest.TryParse(body, out var request))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, responseType,
                    ChronicleRefineAckBuilder.BuildError(ChronicleRefineResult.ErrorInvalidMaterial)));
                return;
            }

            var (cid, _) = ResolveOwner(session);
            var command = request.ToCommand(session.Player?.Job ?? 0, session.Player?.GrowType ?? 0);
            FileLogger.Log($"[{ProtocolName}] ENCHANT_3RD_CHRONICLE_ITEM material=({request.MaterialSlotIndex},0x{request.MaterialItemTemplateId:X8}) target=({request.TargetSlotIndex},0x{request.TargetItemTemplateId:X8}) option={request.OptionNo} job={command.CharacterJob} grow={command.FirstGrowType} pad=0x{request.MaterialPadding:X2}");

            ChronicleRefineResult result = null;
            InventoryLease lease = null;
            var ok = TryGetOwnedInventoryLease(session, cid, out lease)
                && OnlineInventoryMutationCommitCoordinator.TryCommit(
                    lease,
                    "chronicle-refine",
                    (connection, transaction) =>
                    {
                        var refined = ChronicleRefineService.TryRefine(
                            lease.Inventory,
                            command,
                            out result);
                        return refined && result != null && result.Success;
                    });

            if (!ok)
            {
                var errorCode = result != null ? result.ErrorCode : ChronicleRefineResult.ErrorInvalidMaterial;
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, responseType,
                    ChronicleRefineAckBuilder.BuildError(errorCode)));
                FileLogger.Log($"[{ProtocolName}] ENCHANT_3RD_CHRONICLE_ITEM: FAILED error=0x{errorCode:X2}");
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, responseType,
                ChronicleRefineAckBuilder.BuildSuccess(result)));
            await _refresh.SendUpdateItemList(session, InventoryListType.Main,
                new[] { result.Command.TargetSlotIndex, result.Command.MaterialSlotIndex });
            foreach (var reward in result.FailureRewards)
            {
                if (reward.SlotIndex != result.Command.TargetSlotIndex
                    && reward.SlotIndex != result.Command.MaterialSlotIndex)
                    await _refresh.SendUpdateItemList(session, InventoryListType.Main, reward.SlotIndex);
            }

            FileLogger.Log($"[{ProtocolName}] ENCHANT_3RD_CHRONICLE_ITEM: OK target={result.Command.TargetSlotIndex} option={result.Command.OptionNo} success={result.RefineSucceeded} destroyed={result.TargetDestroyed} probability={result.SuccessProbability} roll={result.ProbabilityRoll} count={result.OptionCount} materialLeft={result.MaterialRemainingStackCount} rewards={result.FailureRewards.Count}");
        }

        public async Task Handle_ENUM_CMDPACKET_ENCHANT_BY_BEAD(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!EnchantByBeadRequest.TryParse(body, out var request))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0110, EnchantByBeadAckBuilder.BuildError(EnchantByBeadResult.ErrorInvalidBead)));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] ENCHANT_BY_BEAD raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")} bead=({request.BeadListType},{request.BeadSlotIndex}) target=({request.TargetListType},{request.TargetSlotIndex})");

            var (cid, _) = ResolveOwner(session);
            var command = request.ToCommand();
            if (command.TargetListType == InventoryListType.Pet)
            {
                await HandlePetCreatureEnchantByBead(session, command, cid);
                return;
            }

            EnchantByBeadResult result;
            bool ok;
            if (TryGetOwnedInventoryLease(session, cid, out var lease))
            {
                ok = InventoryEquipmentAugmentationCommitService.TryCommitEnchant(
                    lease, command, false, out result, out _);
            }
            else
            {
                ok = false;
                result = EnchantByBeadResult.Error(command, EnchantByBeadResult.ErrorInvalidTarget);
            }

            if (!ok)
            {
                var errorCode = result != null ? result.ErrorCode : EnchantByBeadResult.ErrorInvalidBead;
                FileLogger.Log($"[{ProtocolName}] ENCHANT_BY_BEAD: FAILED error=0x{errorCode:X2}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0110, EnchantByBeadAckBuilder.BuildError(errorCode)));
                return;
            }

            if (result.TargetListType == result.BeadListType)
                await _refresh.SendUpdateItemList(session, result.TargetListType, new[] { result.TargetSlotIndex, result.BeadSlotIndex });
            else
            {
                await _refresh.SendUpdateItemList(session, result.TargetListType, result.TargetSlotIndex);
                await _refresh.SendUpdateItemList(session, result.BeadListType, result.BeadSlotIndex);
            }
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0110, EnchantByBeadAckBuilder.BuildSuccess(result)));

            FileLogger.Log($"[{ProtocolName}] ENCHANT_BY_BEAD: OK target=({request.TargetListType},{request.TargetSlotIndex}) enchantCard=0x{result.EnchantCardItemId:X8}");
        }

        public async Task Handle_USE_GEM(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var commandType = (ushort)CmdPacketType.USE_GEM;
            if (!UseGuardianGemRequest.TryParse(body, out var request))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, commandType, new byte[] { 0x00, 0x04 }));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] USE_GEM raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")} medal=0x{request.EquippedMedalItemTemplateId:X8} slot={request.MaterialSlotIndex} gem=0x{request.GuardianGemItemTemplateId:X8} socket={request.SocketIndex}");

            var (cid, _) = ResolveOwner(session);
            GuardianGemUseResult result;
            var persistenceFailed = false;
            var ok = false;
            if (TryGetOwnedInventoryLease(session, cid, out var lease))
            {
                ok = InventoryEquipmentAugmentationCommitService.TryCommitGuardianGem(
                    lease,
                    request.ToCommand(),
                    out result,
                    out persistenceFailed);
            }
            else
            {
                result = GuardianGemUseResult.Error(request.ToCommand(), GuardianGemUseResult.ErrorInvalidRequest);
            }

            if (!ok)
            {
                var errorCode = persistenceFailed
                    ? GuardianGemUseResult.ErrorInvalidRequest
                    : result != null
                        ? result.ErrorCode
                        : GuardianGemUseResult.ErrorInvalidRequest;
                FileLogger.Log($"[{ProtocolName}] USE_GEM: FAILED error=0x{errorCode:X2} persistenceFailed={persistenceFailed} medal=0x{request.EquippedMedalItemTemplateId:X8} slot={request.MaterialSlotIndex} gem=0x{request.GuardianGemItemTemplateId:X8} socket={request.SocketIndex}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, commandType, new byte[] { 0x00, errorCode }));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, commandType, new byte[] { 0x01 }));
            await _refresh.SendUpdateItemList(session, InventoryListType.GuildMedal, result.MaterialSlotIndex);
            await _refresh.SendUpdateItemList(session, InventoryListType.Equipment, result.TargetSlotIndex);

            FileLogger.Log($"[{ProtocolName}] USE_GEM: OK medal=0x{result.TargetItemTemplateId:X8} slot={result.MaterialSlotIndex} gem=0x{result.MaterialItemTemplateId:X8} socket={result.SocketIndex} old=0x{result.PreviousGuardianGemItemId:X8} remaining={result.MaterialRemainingCount}");
        }

        private async Task HandlePetCreatureEnchantByBead(
            EnhancedClientSession session,
            EnchantByBeadCommand command,
            int characterId)
        {
            EnchantByBeadResult result;
            var ok = false;
            if (InventoryContext.TryGetLease(characterId, out var lease) && lease.IsOwnedBy(session.SessionId))
            {
                ok = InventoryEquipmentAugmentationCommitService.TryCommitEnchant(
                    lease, command, true, out result, out _);
            }
            else
            {
                result = EnchantByBeadResult.Error(command, EnchantByBeadResult.ErrorInvalidTarget);
            }

            if (!ok)
            {
                var errorCode = result != null ? result.ErrorCode : EnchantByBeadResult.ErrorInvalidTarget;
                FileLogger.Log($"[{ProtocolName}] ENCHANT_BY_BEAD pet: FAILED error=0x{errorCode:X2}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0110, EnchantByBeadAckBuilder.BuildError(errorCode)));
                return;
            }

            await _refresh.SendUpdateItemList(session, result.TargetListType, result.TargetSlotIndex);
            await _refresh.SendUpdateItemList(session, result.BeadListType, result.BeadSlotIndex);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0110, EnchantByBeadAckBuilder.BuildSuccess(result)));

            FileLogger.Log($"[{ProtocolName}] ENCHANT_BY_BEAD pet: OK target=({command.TargetListType},{command.TargetSlotIndex}) enchantCard=0x{result.EnchantCardItemId:X8}");
        }

        public async Task Handle_ENUM_CMDPACKET_UPGRADE_ITEM(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!ItemUpgradeRequest.TryParse(body, out var request))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0050,
                    ItemUpgradeAckBuilder.BuildError(ItemUpgradeResult.ErrorInvalidTarget)));
                return;
            }

            FileLogger.Log($"[{ProtocolName}] UPGRADE_ITEM raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")} method={request.Method} mode={request.Mode} target=({request.TargetListType},{request.TargetSlotIndex},0x{request.TargetItemTemplateId:X8}) materialSlot={request.MaterialSlotIndex} optSlot={request.OptionalTicketSlotIndex} name={request.TargetItemName}");

            var (cid, _) = ResolveOwner(session);
            var command = request.ToCommand();
            if (!TryGetOwnedInventoryLease(session, cid, out var lease))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0050,
                    ItemUpgradeAckBuilder.BuildError(ItemUpgradeResult.ErrorInvalidTarget)));
                return;
            }

            bool hasRewardSpace;
            int freeMaterialSlots;
            ItemSlotRange materialRange;
            ItemUpgradeResult result = null;
            lock (lease.SyncRoot)
            {
                hasRewardSpace = InventorySpaceCheckService.HasEnoughMaterialFreeSlots(
                    lease.Inventory,
                    out freeMaterialSlots,
                    out materialRange);
            }

            if (!hasRewardSpace)
            {
                FileLogger.Log($"[{ProtocolName}] UPGRADE_ITEM: FAILED material free slots insufficient free={freeMaterialSlots} required={InventorySpaceCheckService.RequiredFreeMaterialRewardSlots} range={materialRange.Start}-{materialRange.End}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0050,
                    ItemUpgradeAckBuilder.BuildError(ItemUpgradeResult.ErrorInventoryFull)));
                return;
            }

            var ok = InventoryItemUpgradeCommitService.TryCommit(
                lease,
                command,
                out result,
                out var persistenceFailed);

            if (!ok)
            {
                var errorCode = persistenceFailed
                    ? ItemUpgradeResult.ErrorInvalidTarget
                    : result != null
                        ? result.ErrorCode
                        : ItemUpgradeResult.ErrorInvalidTarget;
                FileLogger.Log($"[{ProtocolName}] UPGRADE_ITEM: FAILED error={errorCode} persistenceFailed={persistenceFailed} method={request.Method} mode={request.Mode} targetSlot={request.TargetSlotIndex} materialSlot={request.MaterialSlotIndex}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0050, ItemUpgradeAckBuilder.BuildError(errorCode)));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x0050, ItemUpgradeAckBuilder.BuildSuccess(result)));

            if (result.TargetListType == InventoryListType.GuildMedal)
                await _refresh.SendUpdateItemList(session, result.TargetListType, result.TargetSlotIndex);

            if (result.MainRefreshSlots.Count > 0)
            {
                await _refresh.SendUpdateItemList(session, InventoryListType.Main, result.MainRefreshSlots);
                FileLogger.Log($"[{ProtocolName}] UPGRADE_ITEM: item refresh queued slots={string.Join(",", result.MainRefreshSlots)}");
            }

            if (result.GoldCost > 0)
            {
                await _refresh.SendUpdateItemList(session, InventoryListType.Main, InventoryService.MainVirtualCurrencySlotStart);
                FileLogger.Log($"[{ProtocolName}] UPGRADE_ITEM: gold refresh queued gold={result.UpdatedGold}");
            }

            if (result.NoticeRequired)
                await BroadcastItemUpgradeNotice(session, result);

            FileLogger.Log($"[{ProtocolName}] UPGRADE_ITEM: OK scene={result.Scene} method={result.Method} mode={result.Mode} targetSlot={result.TargetSlotIndex} level={result.OldLevel}->{result.NewLevel} success={result.UpgradeSucceeded} resultCode={result.ResultCode} rate={result.FinalSuccessWeight} gold={result.UpdatedGold}");
        }

        private async Task BroadcastItemUpgradeNotice(EnhancedClientSession session, ItemUpgradeResult result)
        {
            if (result == null)
                return;

            await BroadcastItemNotice(
                session,
                "UPGRADE_ITEM",
                userUniqueId => ItemUpgradeNoticeBuilder.Build(result, userUniqueId),
                $"item=0x{result.TargetItemTemplateId:X8} level={result.NewLevel} mode={result.Mode}");
        }

        internal Task BroadcastCommittedUpgradeNotice(EnhancedClientSession session, ItemUpgradeResult result)
            => BroadcastItemUpgradeNotice(session, result);

        public async Task Handle_EQUIPMENT_SOCKET_OPEN(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var commandType = (ushort)CmdPacketType.ADD_EQUIPMENT_SOCKET;
            FileLogger.Log($"[{ProtocolName}] EQUIP_SOCKET_OPEN type=0x{commandType:X4} raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            if (!TryParseSocketOpenBody(body, out var targetSlot, out var targetItemId, out var materialSlot))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, commandType, new byte[] { 0x00 }));
                return;
            }

            var (cid, _) = ResolveOwner(session);
            EquipmentSocketMutationResult result;
            bool ok;
            if (TryGetOwnedInventoryLease(session, cid, out var lease))
            {
                ok = InventoryEquipmentAugmentationCommitService.TryCommitEquipmentSocket(
                    lease, targetSlot, targetItemId, materialSlot, out result, out _);
            }
            else
            {
                ok = false;
                result = null;
            }

            if (!ok)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, commandType, new byte[] { 0x00, 0x04 }));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, commandType, BuildSocketOpenAck(targetSlot, targetItemId, materialSlot)));

            await _refresh.SendUpdateItemList(session, InventoryListType.Main, targetSlot);
            if (result.MaterialConsumed && result.MaterialItem != null)
                await SendCommonMaterialRefresh(session, result.MaterialItem);

            if (result.MaterialConsumed && result.MaterialItem != null)
                FileLogger.Log($"[{ProtocolName}] EQUIP_SOCKET_OPEN: OK targetSlot={targetSlot} item=0x{targetItemId:X8} materialSlot={materialSlot} left={result.MaterialItem.RemainingStackCount}");
            else
                FileLogger.Log($"[{ProtocolName}] EQUIP_SOCKET_OPEN: OK targetSlot={targetSlot} item=0x{targetItemId:X8} already-open repaired without consuming material");
        }

        public async Task Handle_EQUIPMENT_EMBLEM_ATTACH(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var commandType = (ushort)CmdPacketType.USE_EMBLEM_FOR_EQUIPMENT;
            FileLogger.Log($"[{ProtocolName}] EQUIP_EMBLEM_ATTACH type=0x{commandType:X4} raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            if (!TryParseEmblemAttachBody(body, out var targetSlot, out var targetItemId, out var emblems))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, commandType, new byte[] { 0x00 }));
                return;
            }

            var (cid, _) = ResolveOwner(session);
            EquipmentEmblemMutationResult result;
            bool ok;
            var persistenceFailed = false;
            if (TryGetOwnedInventoryLease(session, cid, out var lease))
            {
                ok = InventoryEquipmentAugmentationCommitService.TryCommitEquipmentEmblems(
                    lease, targetSlot, targetItemId, emblems, out result, out persistenceFailed);
            }
            else
            {
                ok = false;
                result = null;
            }

            if (!ok)
            {
                if (!persistenceFailed
                    && await TryHandleAvatarEmblemAttach(session, commandType, targetSlot, targetItemId, emblems, cid))
                    return;

                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, commandType, new byte[] { 0x00, 0x04 }));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, commandType, BuildEmblemAttachAck(targetSlot, targetItemId, emblems.Count)));
            if (!result.TargetEquipped)
                await _refresh.SendUpdateItemList(session, result.TargetListType, result.TargetSlotIndex);
            FileLogger.Log($"[{ProtocolName}] EQUIP_EMBLEM_ATTACH: OK targetSlot={targetSlot} item=0x{targetItemId:X8} emblems={emblems.Count}");
        }

        public async Task Handle_AVATAR_SOCKET_OPEN(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] AVATAR_SOCKET_OPEN 0x00CE raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            if (!TryParseSocketOpenBody(body, out var targetSlot, out var targetItemId, out var materialSlot))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00CE, new byte[] { 0x00 }));
                return;
            }

            var (cid, _) = ResolveOwner(session);
            AvatarSocketMutationResult result;
            bool ok;
            if (TryGetOwnedInventoryLease(session, cid, out var lease))
            {
                ok = InventoryEquipmentAugmentationCommitService.TryCommitAvatarSocket(
                    lease, targetSlot, targetItemId, materialSlot, out result, out _);
            }
            else
            {
                ok = false;
                result = null;
            }

            if (!ok)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00CE, new byte[] { 0x00, 0x04 }));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x00CE, BuildSocketOpenAck(targetSlot, targetItemId, materialSlot)));

            if (result.MaterialConsumed && result.MaterialItem != null)
                await SendCommonMaterialRefresh(session, result.MaterialItem);

            await _refresh.SendUpdateItemList(session, InventoryListType.Avatar, targetSlot);

            if (result.MaterialConsumed && result.MaterialItem != null)
                FileLogger.Log($"[{ProtocolName}] AVATAR_SOCKET_OPEN: OK targetSlot={targetSlot} item=0x{targetItemId:X8} materialSlot={materialSlot} left={result.MaterialItem.RemainingStackCount}");
            else
                FileLogger.Log($"[{ProtocolName}] AVATAR_SOCKET_OPEN: OK targetSlot={targetSlot} item=0x{targetItemId:X8} already-open repaired without consuming material");
        }

        public async Task Handle_AVATAR_EMBLEM_ATTACH(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] AVATAR_EMBLEM_ATTACH 0x{header.type:X4} raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");

            if (!TryParseAvatarEmblemAttachBody(body, out var targetSlot, out var targetItemId, out var emblems))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00 }));
                return;
            }

            var (cid, _) = ResolveOwner(session);
            if (!await TryHandleAvatarEmblemAttach(session, header.type, targetSlot, targetItemId, emblems, cid))
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00, 0x04 }));
        }

        private async Task<bool> TryHandleAvatarEmblemAttach(EnhancedClientSession session, ushort ackType, short targetSlot, int targetItemId, IReadOnlyList<EquipmentEmblemApplyRequest> emblems, int cid)
        {
            AvatarEmblemMutationResult result;
            bool ok;
            if (TryGetOwnedInventoryLease(session, cid, out var lease))
            {
                ok = InventoryEquipmentAugmentationCommitService.TryCommitAvatarEmblems(
                    lease, targetSlot, targetItemId, emblems, out result, out _);
            }
            else
            {
                ok = false;
                result = null;
            }

            if (!ok)
                return false;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, ackType, BuildEmblemAttachAck(targetSlot, targetItemId, emblems.Count)));
            if (!result.TargetEquipped)
                await _refresh.SendUpdateItemList(session, result.TargetListType, result.TargetSlotIndex);

            FileLogger.Log($"[{ProtocolName}] AVATAR_EMBLEM_ATTACH: OK targetSlot={targetSlot} item=0x{targetItemId:X8} emblems={emblems.Count} ack=0x{ackType:X4}");
            return true;
        }

        private async Task SendCommonMaterialRefresh(EnhancedClientSession session, InventoryMutationResult material)
        {
            if (material == null)
                return;

            await _refresh.SendUpdateItemList(session, material.ListType, material.SlotIndex);
        }

        private static bool TryParseSocketOpenBody(byte[] body, out short targetSlot, out int targetItemId, out short materialSlot)
        {
            targetSlot = 0;
            targetItemId = 0;
            materialSlot = 0;
            if (body == null || body.Length < 8)
                return false;

            targetSlot = BitConverter.ToInt16(body, 0);
            targetItemId = BitConverter.ToInt32(body, 2);
            materialSlot = BitConverter.ToInt16(body, 6);
            return true;
        }

        private static bool TryParseEmblemAttachBody(byte[] body, out short targetSlot, out int targetItemId, out List<EquipmentEmblemApplyRequest> emblems)
        {
            targetSlot = 0;
            targetItemId = 0;
            emblems = null;
            if (body == null || body.Length < 7)
                return false;

            targetSlot = BitConverter.ToInt16(body, 0);
            targetItemId = BitConverter.ToInt32(body, 2);
            var count = body[6];
            var offset = 7;
            emblems = new List<EquipmentEmblemApplyRequest>();
            for (var index = 0; index < count; index++)
            {
                if (offset + 7 > body.Length)
                    return false;

                emblems.Add(new EquipmentEmblemApplyRequest
                {
                    EmblemSlot = BitConverter.ToInt16(body, offset),
                    EmblemItemTemplateId = BitConverter.ToInt32(body, offset + 2),
                    SocketIndex = body[offset + 6],
                });
                offset += 7;
            }
            return true;
        }

        private static bool TryParseAvatarEmblemAttachBody(byte[] body, out short targetSlot, out int targetItemId, out List<EquipmentEmblemApplyRequest> emblems)
        {
            targetSlot = 0;
            targetItemId = 0;
            emblems = null;
            if (body == null)
                return false;

            if (body.Length >= 8 && body[0] == (byte)InventoryListType.Avatar)
                return TryParseEmblemAttachBodyAt(body, 1, out targetSlot, out targetItemId, out emblems);

            return TryParseEmblemAttachBody(body, out targetSlot, out targetItemId, out emblems);
        }

        private static bool TryParseEmblemAttachBodyAt(byte[] body, int startOffset, out short targetSlot, out int targetItemId, out List<EquipmentEmblemApplyRequest> emblems)
        {
            targetSlot = 0;
            targetItemId = 0;
            emblems = null;
            if (body == null || startOffset < 0 || body.Length < startOffset + 7)
                return false;

            targetSlot = BitConverter.ToInt16(body, startOffset);
            targetItemId = BitConverter.ToInt32(body, startOffset + 2);
            var count = body[startOffset + 6];
            var offset = startOffset + 7;
            emblems = new List<EquipmentEmblemApplyRequest>();
            for (var index = 0; index < count; index++)
            {
                if (offset + 7 > body.Length)
                    return false;

                emblems.Add(new EquipmentEmblemApplyRequest
                {
                    EmblemSlot = BitConverter.ToInt16(body, offset),
                    EmblemItemTemplateId = BitConverter.ToInt32(body, offset + 2),
                    SocketIndex = body[offset + 6],
                });
                offset += 7;
            }
            return true;
        }

        private static byte[] BuildSocketOpenAck(short targetSlot, int targetItemId, short materialSlot)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt16(targetSlot);
            writer.WriteInt32(targetItemId);
            writer.WriteInt16(materialSlot);
            return writer.ToArray();
        }

        private static byte[] BuildEmblemAttachAck(short targetSlot, int targetItemId, int emblemCount)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt16(targetSlot);
            writer.WriteInt32(targetItemId);
            writer.WriteByte((byte)Math.Max(0, Math.Min(255, emblemCount)));
            return writer.ToArray();
        }
    }
}
