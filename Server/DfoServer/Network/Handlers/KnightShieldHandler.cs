using DfoServer.Game.Characters;
using DfoServer.Game.Appearance;
using DfoServer.Game.Inventory;
using DfoServer.Game.KnightShield;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class KnightShieldHandler
    {
        private const int MinMoveRequestLength = 14;
        private const int ChangeDeckRequestLength = KnightShieldDeckSnapshot.SlotCount * sizeof(int);
        private readonly KnightShieldService _service;
        private readonly ICharacterRepository _characterRepository;
        private readonly IGameDatabase _database;

        public KnightShieldHandler(
            KnightShieldService service,
            ICharacterRepository characterRepository,
            IGameDatabase database = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _characterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
            _database = database;
        }

        public async Task<bool> TryHandleMoveItemSpace(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!MentionsShieldSpace(body, out var sourceListType, out var destinationListType))
                return false;

            if (body == null || body.Length < MinMoveRequestLength)
            {
                await SendMoveError(session, sourceListType, destinationListType, "malformed body");
                return true;
            }

            // A21 实机盾牌 MOVE 为 28B；与普通背包相同，只消费已证实的前 14B。
            var sourceSlot = BitConverter.ToInt16(body, 0x01);          // +0x01 [i16]
            var sourceShieldItemId = BitConverter.ToInt32(body, 0x03); // +0x03 [i32]
            var moveValue = BitConverter.ToInt32(body, 0x07);          // +0x07 [i32]
            var destinationSlot = BitConverter.ToInt16(body, 0x0C);    // +0x0C [i16]
            // +0x00/+0x0B 分别是来源/目标空间；+0x0E 之后的语义尚未证明，不参与写入。
            var character = ResolveCharacter(session);
            KnightShieldDeckSnapshot deck;
            string rejectReason;
            bool success;
            if (sourceListType == InventoryListType.KnightShieldCatalog
                && destinationListType == InventoryListType.KnightShieldEquipped
                && IsDeckSlotIndex(destinationSlot))
            {
                success = _service.TryEquipSlot(
                    character,
                    destinationSlot,
                    sourceShieldItemId,
                    out deck,
                    out rejectReason);
            }
            else if (sourceListType == InventoryListType.KnightShieldEquipped
                && destinationListType == InventoryListType.KnightShieldCatalog
                && IsDeckSlotIndex(sourceSlot))
            {
                // 客户端卸下时 source item id 固定为 0，权威状态必须从持久化 deck 读取。
                success = _service.TryUnequipSlot(character, sourceSlot, out deck, out rejectReason);
            }
            else if (sourceListType == InventoryListType.KnightShieldEquipped
                && destinationListType == InventoryListType.KnightShieldEquipped)
            {
                // 主盾/备用盾切换的真实请求不携带 item id，只提供两个 deck 槽位。
                success = _service.TryMoveDeckSlot(
                    character,
                    sourceSlot,
                    destinationSlot,
                    out deck,
                    out rejectReason);
            }
            else
            {
                success = false;
                deck = null;
                rejectReason = "unsupported shield-space direction or slot";
            }

            if (!success)
            {
                await SendMoveError(session, sourceListType, destinationListType, rejectReason);
                return true;
            }

            var moveResult = new InventoryMoveResult
            {
                SourceListType = sourceListType,
                SourceSlotIndex = sourceSlot,
                MoveValue32 = moveValue,
                DestinationListType = destinationListType,
                DestinationSlotIndex = destinationSlot,
                Mutated = true,
            };
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketTypeA21.MOVE_ITEMSPACE,
                MoveItemSpaceAckBuilder.Build(moveResult)));
            await SendDeckNotification(session, deck);
            await SendAppearanceIfMainShieldChanged(session, deck);

            FileLogger.Log(
                $"[GameProtocol] KNIGHT_SHIELD MOVE OK src=({sourceListType},{sourceSlot},item={sourceShieldItemId}) "
                + $"dst=({destinationListType},{destinationSlot}) deck=[{string.Join(",", deck.ShieldItemIds)}]");
            return true;
        }

        public async Task HandleChangeDeckInfo(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var character = ResolveCharacter(session);
            if (body == null || body.Length != ChangeDeckRequestLength)
            {
                await SendRejectedDeckChange(session, character, $"malformed body length={body?.Length ?? 0}");
                return;
            }

            var values = new int[KnightShieldDeckSnapshot.SlotCount];
            for (var slotIndex = 0; slotIndex < values.Length; slotIndex++)
                values[slotIndex] = BitConverter.ToInt32(body, slotIndex * sizeof(int));

            if (!_service.TrySaveDeck(
                character,
                values,
                out var deck,
                out var rejectReason))
            {
                await SendRejectedDeckChange(session, character, rejectReason);
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                KnightShieldDeckBodyBuilder.ChangeDeckCommandType,
                KnightShieldDeckBodyBuilder.BuildChangeDeckAck(deck)));
            await SendDeckNotification(session, deck);
            await SendAppearanceIfMainShieldChanged(session, deck);
            FileLogger.Log($"[GameProtocol] CHANGE_DECK_INFO OK deck=[{string.Join(",", deck.ShieldItemIds)}]");
        }

        private async Task SendAppearanceIfMainShieldChanged(
            EnhancedClientSession session,
            KnightShieldDeckSnapshot deck)
        {
            if (session?.Player == null || deck == null)
                return;

            var displayedMainShield = 0;
            if (session.Player.AppearanceEntries != null)
            {
                foreach (var entry in session.Player.AppearanceEntries)
                {
                    if (entry != null
                        && entry.Slot == KnightShieldEquipmentSnapshotSynchronizer.SupportWeaponSlot)
                    {
                        displayedMainShield = entry.DisplayItemId;
                        break;
                    }
                }
            }

            if (displayedMainShield == deck.MainShieldItemId)
                return;

            // 直接使用本次变更后的权威 deck，避免刷新外观时再次读取数据库。
            var body = AppearanceService.RefreshPlayerAppearance(
                session.Player,
                _characterRepository,
                deck,
                _database);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0002, body));
            FileLogger.Log(
                $"[GameProtocol] KNIGHT_SHIELD appearance refresh: "
                + $"main={deck.MainShieldItemId} entries={session.Player.AppearanceEntries?.Length ?? 0}");
        }

        private async Task SendRejectedDeckChange(
            EnhancedClientSession session,
            CharacterRecord character,
            string rejectReason)
        {
            var authoritativeDeck = KnightShieldDataProvider.IsEligibleCharacter(character)
                && character.CharacterId > 0
                ? _service.Load(character.CharacterId)
                : new KnightShieldDeckSnapshot();
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                KnightShieldDeckBodyBuilder.ChangeDeckCommandType,
                KnightShieldDeckBodyBuilder.BuildChangeDeckAck(authoritativeDeck)));

            FileLogger.Log($"[GameProtocol] CHANGE_DECK_INFO REJECT: {rejectReason}");
        }

        private static Task SendDeckNotification(
            EnhancedClientSession session,
            KnightShieldDeckSnapshot deck)
        {
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                KnightShieldDeckBodyBuilder.DeckNotificationType,
                KnightShieldDeckBodyBuilder.BuildDeck(deck)));
        }

        private static async Task SendMoveError(
            EnhancedClientSession session,
            InventoryListType sourceListType,
            InventoryListType destinationListType,
            string rejectReason)
        {
            // 0x04 会被客户端解释为“仓库/金库空间不足”；盾牌校验失败使用通用无效操作码。
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketTypeA21.MOVE_ITEMSPACE,
                MoveItemSpaceAckBuilder.BuildError(
                    MoveItemSpaceAckBuilder.InvalidOperationErrorCode,
                    (byte)sourceListType,
                    (byte)destinationListType)));
            FileLogger.Log(
                $"[GameProtocol] KNIGHT_SHIELD MOVE REJECT: {rejectReason}");
        }

        private CharacterRecord ResolveCharacter(EnhancedClientSession session)
        {
            var characterId = session?.Player?.CharacterId ?? 0;
            return characterId > 0 ? _characterRepository.GetById(characterId) : null;
        }

        private static bool MentionsShieldSpace(
            byte[] body,
            out InventoryListType sourceListType,
            out InventoryListType destinationListType)
        {
            sourceListType = body != null && body.Length > 0
                ? (InventoryListType)body[0]
                : InventoryListType.Main;
            destinationListType = body != null && body.Length > 11
                ? (InventoryListType)body[11]
                : InventoryListType.Main;

            return IsShieldSpace(sourceListType) || IsShieldSpace(destinationListType);
        }

        private static bool IsShieldSpace(InventoryListType listType)
        {
            return listType == InventoryListType.KnightShieldEquipped
                || listType == InventoryListType.KnightShieldCatalog;
        }

        private static bool IsDeckSlotIndex(int slotIndex)
        {
            return slotIndex >= KnightShieldDeckSnapshot.MainSlotIndex
                && slotIndex < KnightShieldDeckSnapshot.SlotCount;
        }
    }
}
