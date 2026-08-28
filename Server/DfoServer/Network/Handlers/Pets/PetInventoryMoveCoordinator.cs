using DfoServer.Game.Inventory;
using DfoServer.Infrastructure;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Pets
{
    internal static class PetInventoryMoveCoordinator
    {
        private const string ProtocolName = "GameProtocol";

        internal static bool Begin(
            EnhancedClientSession session,
            InventoryLease lease,
            InventoryMoveRequest request,
            out bool trackedRuntimeMove)
        {
            return PetCreatureRuntimeService.BeginInventoryMoveMutation(
                session,
                lease,
                request,
                out trackedRuntimeMove);
        }

        internal static async Task CompleteAsync(
            EnhancedClientSession session,
            InventoryMoveResult result,
            bool trackedRuntimeMove,
            InventoryRefreshSender refresh)
        {
            await PetCreatureRuntimeService.CompleteInventoryMoveMutationAsync(session, result, trackedRuntimeMove);
            await SendCreatureStateRefreshAsync(session, result, refresh);
            await SendPetItemRefreshAsync(session, result, refresh);
        }

        internal static bool HandlesDefaultAppearanceRefresh(InventoryMoveResult result)
        {
            return result != null && (result.PetCreatureStateChanged || result.PetItemStateChanged);
        }

        private static async Task SendCreatureStateRefreshAsync(
            EnhancedClientSession session,
            InventoryMoveResult result,
            InventoryRefreshSender refresh)
        {
            if (session == null || result == null || refresh == null || !result.PetCreatureStateChanged)
                return;

            refresh.ReloadSubtype0Tail(session);
            await refresh.SendCreatureItemListRefresh(session);
            await refresh.SendNoti2AppearanceUpdate(session);
            // USERINFO0 重建后客户端会重置名誉/婚礼 UI 状态，按选角序列
            // 「USERINFO0 在前、补偿包在后」的相对顺序补发 0x0289 与婚礼回放三包。
            await refresh.SendHonorLevelInfoRefresh(session, "pet creature switch");
            await InventoryRefreshSender.SendWeddingReplayRefresh(session);
            FileLogger.Log($"[{ProtocolName}] pet creature switch: 0x0069 + NOTI2 mode0 + HONOR_LEVEL_INFO + wedding replay sent");
        }

        private static async Task SendPetItemRefreshAsync(
            EnhancedClientSession session,
            InventoryMoveResult result,
            InventoryRefreshSender refresh)
        {
            if (session == null || result == null || refresh == null)
                return;

            if (!result.PetItemStateChanged
                && !result.PetItemFullRefresh
                && result.PetCreatureRefreshSlots.Count <= 0
                && result.EquipmentRefreshSlots.Count <= 0)
            {
                return;
            }

            if (result.PetItemFullRefresh)
                await refresh.SendItemListRefresh(session, InventoryListType.Pet);
            else if (result.PetCreatureRefreshSlots.Count > 0)
                await refresh.SendUpdateItemList(session, InventoryListType.Pet, result.PetCreatureRefreshSlots);

            if (result.EquipmentRefreshSlots.Count > 0)
                await refresh.SendUpdateItemList(session, InventoryListType.Equipment, result.EquipmentRefreshSlots);
        }
    }
}
