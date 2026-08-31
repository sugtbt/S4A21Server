using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Game.ReviveCoin;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed partial class InventoryHandler
    {
        private const byte IncreaseStatusUnknownErrorCode = 0x01;
        private const byte IncreaseStatusMissingSourceItemErrorCode = 0x11;
        private const byte LevelUpTicketUnknownErrorCode = 0x01;
        private const byte LevelUpTicketMissingSourceItemErrorCode = 0x11;
        private const byte LevelUpTicketLevelRestrictedErrorCode = 0x16;
        private const byte LevelUpTicketUnsupportedItemErrorCode = 0x17;
        private const byte LevelUpTicketPersistenceErrorCode = 0x63;

        public async Task Handle_ENUM_CMDPACKET_INCREASE_STATUS(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (session == null)
            {
                FileLogger.Log($"[{ProtocolName}] INCREASE_STATUS rejected without a session");
                return;
            }

            if (!IncreaseStatusRequest.TryParse(body, out var request))
            {
                await SendIncreaseStatusFailureAsync(
                    session,
                    IncreaseStatusUnknownErrorCode);
                FileLogger.Log(
                    $"[{ProtocolName}] INCREASE_STATUS rejected malformed body({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");
                return;
            }

            if (session.Player == null || session.Player.CharacterId <= 0)
            {
                await SendIncreaseStatusFailureAsync(
                    session,
                    IncreaseStatusUnknownErrorCode);
                FileLogger.Log(
                    $"[{ProtocolName}] INCREASE_STATUS rejected without an active player: slot={request.SlotIndex}");
                return;
            }

            var (characterId, accountId) = ResolveOwner(session);
            ExperienceItemUseResult result;
            try
            {
                result = _experienceItemUseService.UseBySlot(
                    characterId,
                    accountId,
                    InventoryListType.Main,
                    request.SlotIndex,
                    session.Player.CurrentRun == null
                        ? ExperienceItemUseLocation.Town
                        : ExperienceItemUseLocation.Dungeon);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] INCREASE_STATUS failed unexpectedly: cid={characterId} slot={request.SlotIndex} error={ex}");
                await SendIncreaseStatusFailureAsync(
                    session,
                    IncreaseStatusUnknownErrorCode);
                return;
            }

            if (result.Success)
            {
                if (result.ItemTemplateId != ReviveCoinService.ConsumableItemId)
                {
                    session.Player.Level = result.NewLevel;
                    session.Player.Exp = result.NewExp;
                }
            }

            var ackBody = result.Success
                ? IncreaseStatusAckBuilder.BuildExperienceSuccess(session.Player.UserId)
                : IncreaseStatusAckBuilder.BuildError(
                    GetExperienceItemFailureErrorCode(result.Status));
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.INCREASE_STATUS,
                ackBody));

            if (!result.Success)
            {
                if (result.Status == ExperienceItemUseStatus.NotApplicable
                    || result.Status == ExperienceItemUseStatus.ConsumeFailed
                    || result.Status == ExperienceItemUseStatus.Expired)
                {
                    await RefreshExperienceSourceSlotAsync(
                        session,
                        characterId,
                        request.SlotIndex,
                        "rejected-source");
                }

                FileLogger.Log(
                    $"[{ProtocolName}] INCREASE_STATUS rejected: status={result.Status} item={result.ItemTemplateId} slot={request.SlotIndex} detail={result.Detail}");
                return;
            }

            // 客户端先结束 0x001E 指令，再消费背包变更，
            // 最后应用 EXP/SP/TP 的绝对状态快照。
            await RefreshExperienceSourceSlotAsync(
                session,
                characterId,
                request.SlotIndex,
                "post-commit");

            if (result.ItemTemplateId == ReviveCoinService.ConsumableItemId)
            {
                await _refresh.SendUpdateItemList(
                    session,
                    InventoryListType.Main,
                    ReviveCoinService.WalletSlot);
            }
            else
            {
                await _experienceItemNotifications.SendAsync(session, result);
            }

            if (result.UsableCountState != null)
                await SendUsableCountLimitUpdateAsync(session, result.UsableCountState);

            var useKind = result.ItemTemplateId
                    == ExperienceItemUseService.SkillPointBook5ItemId
                || result.ItemTemplateId
                    == ExperienceItemUseService.SkillPointBook20ItemId
                ? "skill-point"
                : "experience";
            FileLogger.Log(
                $"[{ProtocolName}] INCREASE_STATUS {useKind}: item={result.ItemTemplateId} slot={request.SlotIndex} remaining={result.ConsumedItem?.RemainingStackCount ?? 0} grantExp={result.GrantedExp} level={result.PreviousLevel}->{result.NewLevel} exp={result.PreviousExp}->{result.NewExp} detail={result.Detail}");
        }

        public async Task Handle_REQUEST_EVENT_SERVER_LEVEL_UP(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (session == null)
            {
                FileLogger.Log($"[{ProtocolName}] LEVEL_UP_TICKET rejected without a session");
                return;
            }

            if (!LevelUpTicketRequest.TryParse(body, out var request))
            {
                await SendLevelUpTicketFailureAsync(
                    session,
                    LevelUpTicketUnknownErrorCode);
                FileLogger.Log(
                    $"[{ProtocolName}] LEVEL_UP_TICKET rejected malformed body({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");
                return;
            }

            if (session.Player == null || session.Player.CharacterId <= 0)
            {
                await SendLevelUpTicketFailureAsync(
                    session,
                    LevelUpTicketUnknownErrorCode);
                FileLogger.Log(
                    $"[{ProtocolName}] LEVEL_UP_TICKET rejected without an active player: slot={request.SlotIndex}");
                return;
            }

            var (characterId, accountId) = ResolveOwner(session);
            ExperienceItemUseResult result;
            try
            {
                result = _experienceItemUseService.UseLevelUpTicketBySlot(
                    characterId,
                    accountId,
                    request.SlotIndex,
                    session.Player.CurrentRun == null
                        ? ExperienceItemUseLocation.Town
                        : ExperienceItemUseLocation.Dungeon);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] LEVEL_UP_TICKET failed unexpectedly: cid={characterId} slot={request.SlotIndex} error={ex}");
                await SendLevelUpTicketFailureAsync(
                    session,
                    LevelUpTicketUnknownErrorCode);
                return;
            }

            if (!result.Success)
            {
                await SendLevelUpTicketFailureAsync(
                    session,
                    GetLevelUpTicketFailureErrorCode(result.Status));
                if (result.Status == ExperienceItemUseStatus.NotApplicable
                    || result.Status == ExperienceItemUseStatus.ConsumeFailed
                    || result.Status == ExperienceItemUseStatus.Expired)
                {
                    await RefreshLevelUpTicketSourceSlotAsync(
                        session,
                        characterId,
                        request.SlotIndex,
                        "rejected-source");
                }

                FileLogger.Log(
                    $"[{ProtocolName}] LEVEL_UP_TICKET rejected: status={result.Status} item={result.ItemTemplateId} slot={request.SlotIndex} detail={result.Detail}");
                return;
            }

            session.Player.Level = result.NewLevel;
            session.Player.Exp = result.NewExp;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketTypeA21.REQUEST_EVENT_SERVER_LEVEL_UP,
                LevelUpTicketAckBuilder.BuildSuccess()));

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.EVENT_SERVER_AUTO_QUEST_CLEAR_REWARD_DATA,
                CommonPacketBodyBuilder.BuildZeroBytes(10)));

            await _experienceItemNotifications.SendAsync(
                session,
                result,
                sendQuestList: false);
            await SendLevelUpTicketQuestStateAsync(session, characterId);
            await RefreshLevelUpTicketSourceSlotAsync(
                session,
                characterId,
                request.SlotIndex,
                "post-commit");

            FileLogger.Log(
                $"[{ProtocolName}] LEVEL_UP_TICKET success: item={result.ItemTemplateId} slot={request.SlotIndex} remaining={result.ConsumedItem?.RemainingStackCount ?? 0} level={result.PreviousLevel}->{result.NewLevel} exp={result.PreviousExp}->{result.NewExp} autoQuestCount={result.AutoCompletedQuestIds?.Count ?? 0}");
        }

        private static Task SendIncreaseStatusFailureAsync(
            EnhancedClientSession session,
            byte errorCode)
            => session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketType.INCREASE_STATUS,
                IncreaseStatusAckBuilder.BuildError(errorCode)));

        private static Task SendLevelUpTicketFailureAsync(
            EnhancedClientSession session,
            byte errorCode)
            => session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketTypeA21.REQUEST_EVENT_SERVER_LEVEL_UP,
                LevelUpTicketAckBuilder.BuildError(errorCode)));

        private async Task SendLevelUpTicketQuestStateAsync(
            EnhancedClientSession session,
            int characterId)
        {
            var questManager = session?.GameSession?.QuestManager;
            if (questManager == null || characterId <= 0)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] LEVEL_UP_TICKET quest state refresh skipped: cid={characterId} questManager={(questManager == null ? "null" : "ok")}");
                return;
            }

            await TrySendLevelUpTicketQuestStatePartAsync(
                "acceptable",
                () => questManager.SendAcceptableQuestListAsync());
            await TrySendLevelUpTicketQuestStatePartAsync(
                "active",
                () => questManager.SendActiveQuestListAsync());
            await TrySendLevelUpTicketQuestStatePartAsync(
                "clear",
                () => SendLevelUpTicketClearQuestListAsync(session, characterId));
        }

        private async Task SendLevelUpTicketClearQuestListAsync(
            EnhancedClientSession session,
            int characterId)
        {
            var clearedFlags = new QuestRepository(_database.ConnectionString)
                .LoadClearedFlags(characterId);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.CLEAR_QUEST_LIST,
                ClearQuestListBodyBuilder.BuildBody(clearedFlags)));
        }

        private static async Task TrySendLevelUpTicketQuestStatePartAsync(
            string name,
            Func<Task> send)
        {
            try
            {
                await send();
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[GameProtocol] LEVEL_UP_TICKET {name} quest-state refresh failed: {ex.Message}");
            }
        }

        private async Task RefreshExperienceSourceSlotAsync(
            EnhancedClientSession session,
            int characterId,
            short slotIndex,
            string reason)
        {
            try
            {
                await _refresh.SendUpdateItemList(
                    session,
                    InventoryListType.Main,
                    slotIndex);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] INCREASE_STATUS {reason} slot refresh failed: cid={characterId} slot={slotIndex} error={ex.Message}; falling back to the full Main list");
                try
                {
                    await _refresh.SendItemListRefresh(session, InventoryListType.Main);
                }
                catch (Exception fallbackEx)
                {
                    // 事务提交后的背包刷新是辅助通知，
                    // 权威 EXP 快照仍必须送达客户端。
                    FileLogger.Log(
                        $"[{ProtocolName}] INCREASE_STATUS {reason} full Main refresh failed: cid={characterId} slot={slotIndex} error={fallbackEx.Message}");
                }
            }
        }

        internal static byte GetExperienceItemFailureErrorCode(
            ExperienceItemUseStatus status)
        {
            switch (status)
            {
                case ExperienceItemUseStatus.NotApplicable:
                case ExperienceItemUseStatus.Expired:
                case ExperienceItemUseStatus.ConsumeFailed:
                    return IncreaseStatusMissingSourceItemErrorCode;
                default:
                    return IncreaseStatusUnknownErrorCode;
            }
        }

        private async Task RefreshLevelUpTicketSourceSlotAsync(
            EnhancedClientSession session,
            int characterId,
            short slotIndex,
            string reason)
        {
            try
            {
                await _refresh.SendUpdateItemList(
                    session,
                    InventoryListType.Main,
                    slotIndex);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] LEVEL_UP_TICKET {reason} slot refresh failed: cid={characterId} slot={slotIndex} error={ex.Message}; falling back to the full Main list");
                try
                {
                    await _refresh.SendItemListRefresh(session, InventoryListType.Main);
                }
                catch (Exception fallbackEx)
                {
                    FileLogger.Log(
                        $"[{ProtocolName}] LEVEL_UP_TICKET {reason} full Main refresh failed: cid={characterId} slot={slotIndex} error={fallbackEx.Message}");
                }
            }
        }

        internal static byte GetLevelUpTicketFailureErrorCode(
            ExperienceItemUseStatus status)
        {
            switch (status)
            {
                case ExperienceItemUseStatus.NotApplicable:
                case ExperienceItemUseStatus.Expired:
                case ExperienceItemUseStatus.ConsumeFailed:
                    return LevelUpTicketMissingSourceItemErrorCode;
                case ExperienceItemUseStatus.LevelRestricted:
                    return LevelUpTicketLevelRestrictedErrorCode;
                case ExperienceItemUseStatus.UnsupportedDefinition:
                    return LevelUpTicketUnsupportedItemErrorCode;
                case ExperienceItemUseStatus.PersistenceFailed:
                    return LevelUpTicketPersistenceErrorCode;
                default:
                    return LevelUpTicketUnknownErrorCode;
            }
        }
    }
}
