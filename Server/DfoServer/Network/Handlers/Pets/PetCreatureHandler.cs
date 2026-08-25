using DfoServer.Game.Inventory;
using DfoServer.Game.Names;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Pets
{
    public sealed class PetCreatureHandler
    {
        private readonly SqliteSelectCharacterDataSource _selectCharacterDataSource;
        private readonly InventoryRefreshSender _refresh;

        public string ProtocolName => "GameProtocol";

        public PetCreatureHandler(
            SqliteSelectCharacterDataSource selectCharacterDataSource,
            InventoryRefreshSender refresh)
        {
            _selectCharacterDataSource = selectCharacterDataSource ?? throw new ArgumentNullException(nameof(selectCharacterDataSource));
            _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        }

        public async Task<bool> TryHandleUseStackable(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 7)
                return false;

            var slotIndex = BitConverter.ToInt16(body, 0);
            var listType = (InventoryListType)body[2];
            if (!IsPetConsumableSlot(listType, slotIndex))
                return false;

            var instanceValue = BitConverter.ToInt32(body, 3);
            var itemCode = body.Length >= 11 ? BitConverter.ToInt32(body, 7) : 0;
            InventoryMutationResult result = null;
            byte[] creatureStateBody = null;
            var consumed = TryGetInventoryLease(session, out var lease)
                && PetCreatureRuntimeService.TryCommitDungeonElapsedBeforeMutation(
                    session,
                    lease,
                    "pet_consumable_before",
                    continueTiming: true)
                && PetConsumableCommitService.TryCommit(
                    lease,
                    listType,
                    slotIndex,
                    itemCode,
                    out result);
            if (consumed && result.PetSatietyChanged)
            {
                lock (lease.SyncRoot)
                {
                    creatureStateBody = BuildCreatureStateRefreshBody(
                        lease.Inventory,
                        result.PetCreatureKey);
                }
            }

            var ackBody = consumed
                ? UseStackableAckBuilder.BuildSuccess(slotIndex, (byte)listType, instanceValue, itemCode)
                : UseStackableAckBuilder.BuildError((byte)listType, instanceValue, itemCode);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, ackBody));

            if (!consumed)
            {
                FileLogger.Log($"[{ProtocolName}] PET_USE_STACKABLE: stale or failed pet consumable item=0x{itemCode:X8} listType={listType} slot={slotIndex}");
                return true;
            }

            if (result.ListType == InventoryListType.Pet)
                await _refresh.SendUpdateItemList(session, InventoryListType.Pet, result.SlotIndex);

            if (result.PetSatietyChanged)
            {
                PetCreatureRuntimeService.HandlePetSatietyChangedAfterFeed(
                    session,
                    result.PetCreatureKey,
                    result.PetSatietyAfter,
                    "pet_feed_after");

                if (creatureStateBody != null)
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0067, creatureStateBody));

                await _refresh.SendCreatureItemListRefresh(session);
            }

            var petSatietyLog = result.PetSatietyChanged
                ? $" petSatiety key={result.PetCreatureKey} {result.PetSatietyBefore}->{result.PetSatietyAfter}"
                : string.Empty;
            FileLogger.Log($"[{ProtocolName}] PET_USE_STACKABLE: consumed item=0x{itemCode:X8} slot={slotIndex} remaining={result.RemainingStackCount}{petSatietyLog}");
            return true;
        }

        public async Task HandleHatchCreatureEgg(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!TryParseCreatureEggHatchRequest(body, out var listType, out var slotIndex, out var expectedItemTemplateId))
            {
                FileLogger.Log($"[{ProtocolName}] HATCH_CREATURE_EGG: parse failed type=0x{header.type:X4} body({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00 }));
                return;
            }

            CreatureHatchResult result = null;
            var committed = TryGetInventoryLease(session, out var lease)
                && PetCreatureRuntimeService.TryCommitDungeonElapsedBeforeMutation(
                    session,
                    lease,
                    "pet_hatch_before",
                    continueTiming: true)
                && PetCreatureMutationCommitService.TryCommitHatch(
                    lease,
                    listType,
                    slotIndex,
                    expectedItemTemplateId,
                    out result);

            if (!committed || result == null)
            {
                FileLogger.Log($"[{ProtocolName}] HATCH_CREATURE_EGG: failed type=0x{header.type:X4} list={listType} slot={slotIndex} expected=0x{expectedItemTemplateId:X8}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00 }));
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, CommonPacketBodyBuilder.BuildSuccessAck()));
            await _refresh.SendUpdateItemList(session, InventoryListType.Pet, result.SlotIndex);
            await _refresh.SendCreatureItemListRefresh(session);

            FileLogger.Log($"[{ProtocolName}] HATCH_CREATURE_EGG: OK type=0x{header.type:X4} slot={result.SlotIndex} egg=0x{result.EggItemTemplateId:X8} -> pet=0x{result.HatchedItemTemplateId:X8} serial={result.PetSerialOrHandle}");
        }

        public async Task HandleRequestHatchedCreature(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, CommonPacketBodyBuilder.BuildSuccessAck()));
            await _refresh.SendCreatureItemListRefresh(session);
            FileLogger.Log($"[{ProtocolName}] REQUEST_HATCHED_CREATURE: refreshed creature list type=0x{header.type:X4} body({body?.Length ?? 0}B)");
        }

        public async Task HandleVerifyCreatureQuest(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] VERIFY_CREATURE_QUEST raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");
            await PetCreatureRuntimeService.VerifyCreatureEvolutionQuestAsync(session);
        }

        public async Task HandleCreatureScriptMessage(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!PetCreatureScript.TryParseMessageRequest(body, out var request))
            {
                FileLogger.Log($"[{ProtocolName}] CREATURE_SCRIPT_MESSAGE invalid body({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");
                return;
            }

            // 旧服通过 GameWorld::send_chat_msg(..., NOTI 0x0077) 广播。
            // 当前单机服没有同屏广播集合，先回发给自己。
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0077,
                PetCreatureScript.BuildNotiBody(
                    request.Mode,
                    session?.Player?.UserId ?? 0,
                    serverGroup: 0,
                    request.MessageBytes)));

            FileLogger.Log(
                $"[{ProtocolName}] CREATURE_SCRIPT_MESSAGE mode={request.Mode} target={request.TargetUniqueId} " +
                $"char={request.CharacterId} len={request.MessageBytes.Length} text={DecodePetCreatureNameForLog(request.MessageBytes)}");
        }

        public async Task HandleRenameCreature(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{ProtocolName}] RENAME_CREATURE raw({body?.Length ?? 0}B): {(body != null ? BitConverter.ToString(body) : "null")}");
            if (!TryParsePetCreatureRenameRequest(body, out var request))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00 }));
                return;
            }

            if (!NameInputValidator.TryValidateRawName(request.NameBytes, minBytes: 0, maxBytes: 13, out _, out var nameFailure))
            {
                FileLogger.Log($"[{ProtocolName}] RENAME_CREATURE invalid name reason={nameFailure} name={DecodePetCreatureNameForLog(request.NameBytes)}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    header.type,
                    CommonPacketBodyBuilder.BuildCmdError(NameInputValidator.InvalidNameErrorCode)));
                return;
            }

            PetCreatureRenameResult result = null;
            var committed = TryGetInventoryLease(session, out var lease)
                && PetCreatureRuntimeService.TryCommitDungeonElapsedBeforeMutation(
                    session,
                    lease,
                    "pet_rename_before",
                    continueTiming: true)
                && PetCreatureMutationCommitService.TryCommitRename(
                    lease,
                    request,
                    out result);

            if (!committed || result == null)
            {
                FileLogger.Log($"[{ProtocolName}] RENAME_CREATURE failed source=({request.SourceListType},{request.SourceSlotIndex}) name={DecodePetCreatureNameForLog(request.NameBytes)}");
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, header.type, new byte[] { 0x00 }));
                return;
            }

            await SendCreatureRenameNoti(session, result);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                header.type,
                BuildCreatureRenameAckBody(result)));
            await _refresh.SendUpdateItemList(session, InventoryListType.Pet, result.SourceSlotIndex);
        }

        private byte[] BuildCreatureStateRefreshBody(InventoryService inventory, int creatureKey)
        {
            if (creatureKey <= 0)
                return null;

            if (!PetInventoryAccessor.TryBuildCreatureItemEntry(inventory, creatureKey, out var entry))
                return null;

            return entry != null ? CreatureListBodyBuilder.BuildCreatureStateBody(entry) : null;
        }

        private static bool IsPetConsumableSlot(InventoryListType listType, short slotIndex)
        {
            return PetConsumableService.IsPetConsumableSlot(listType, slotIndex);
        }

        private static bool TryGetInventoryLease(EnhancedClientSession session, out InventoryLease lease)
        {
            lease = null;
            return session?.Player != null
                && session.Player.CharacterId > 0
                && InventoryContext.TryGetLease(session.Player.CharacterId, out lease);
        }

        private static bool TryParseCreatureEggHatchRequest(byte[] body, out InventoryListType listType, out short slotIndex, out int expectedItemTemplateId)
        {
            listType = InventoryListType.Pet;
            slotIndex = 0;
            expectedItemTemplateId = 0;

            if (body == null || body.Length < 2)
                return false;

            if (body[0] == (byte)InventoryListType.Pet && body.Length >= 3)
            {
                slotIndex = BitConverter.ToInt16(body, 1);
            }
            else
            {
                slotIndex = BitConverter.ToInt16(body, 0);
                if (body.Length >= 3 && body[2] == (byte)InventoryListType.Pet)
                    listType = InventoryListType.Pet;
            }

            foreach (var offset in new[] { 7, 2, 3 })
            {
                if (body.Length < offset + 4)
                    continue;

                var candidate = BitConverter.ToInt32(body, offset);
                if (candidate > 0 && CreatureEggResolver.TryResolveHatchedCreatureItemId(candidate, out _))
                {
                    expectedItemTemplateId = candidate;
                    break;
                }
            }

            return slotIndex >= 0;
        }

        private static bool TryParsePetCreatureRenameRequest(byte[] body, out PetCreatureRenameRequest request)
        {
            request = null;
            if (body == null || body.Length < 7)
                return false;

            var sourceSlot = BitConverter.ToInt16(body, 0);
            var sourceListType = (InventoryListType)body[2];
            var nameLength = BitConverter.ToInt32(body, 3);
            if (nameLength < 0 || nameLength > 13 || body.Length < 7 + nameLength)
                return false;

            var nameBytes = new byte[nameLength];
            if (nameLength > 0)
                Buffer.BlockCopy(body, 7, nameBytes, 0, nameLength);

            request = new PetCreatureRenameRequest
            {
                SourceListType = sourceListType,
                SourceSlotIndex = sourceSlot,
                NameBytes = nameBytes,
            };
            return true;
        }

        private static async Task SendCreatureRenameNoti(EnhancedClientSession session, PetCreatureRenameResult result)
        {
            var writer = new GamePacketWriter();
            writer.WriteUInt16(session?.Player?.UserId ?? 0);
            writer.WriteDstr(result?.NameBytes);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0065, writer.ToArray()));
        }

        private static byte[] BuildCreatureRenameAckBody(PetCreatureRenameResult result)
        {
            var writer = new GamePacketWriter();
            writer.WriteByte(0x01);
            writer.WriteInt16(result.SourceSlotIndex);
            writer.WriteByte((byte)result.SourceListType);
            return writer.ToArray();
        }

        private static string DecodePetCreatureNameForLog(byte[] nameBytes)
        {
            if (nameBytes == null || nameBytes.Length == 0)
                return string.Empty;

            try
            {
                return ClientTextEncoding.GetString(nameBytes);
            }
            catch
            {
                return BitConverter.ToString(nameBytes);
            }
        }
    }
}
