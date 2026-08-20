using System;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Handlers
{
    internal sealed class ExpertJobGiveupNotificationProjector
    {
        private readonly ICharacterRepository _characters;
        private readonly SqliteSubtype0FieldsRepository _subtype0;
        private readonly HonorLevelSyncService _honorLevel;
        private readonly SqliteSelectCharacterDataSource _dataSource;
        private readonly InventoryRefreshSender _inventoryRefresh;

        internal ExpertJobGiveupNotificationProjector(
            ICharacterRepository characters,
            SqliteSubtype0FieldsRepository subtype0,
            HonorLevelSyncService honorLevel,
            SqliteSelectCharacterDataSource dataSource,
            InventoryRefreshSender inventoryRefresh)
        {
            _characters = characters ?? throw new ArgumentNullException(nameof(characters));
            _subtype0 = subtype0 ?? throw new ArgumentNullException(nameof(subtype0));
            _honorLevel = honorLevel ?? throw new ArgumentNullException(nameof(honorLevel));
            _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
            _inventoryRefresh = inventoryRefresh ?? throw new ArgumentNullException(nameof(inventoryRefresh));
        }

        internal async Task ProjectAsync(
            EnhancedClientSession session,
            ExpertJobGiveupResult result)
        {
            var characterId = session?.Player?.CharacterId ?? 0;
            if (characterId <= 0 || result == null || !result.Success)
                return;

            var subtype0 = session.Player.Subtype0Tail;
            if (subtype0 != null)
            {
                subtype0.ExpertJobType = 0;
                subtype0.ExpertJobExp = 0;
            }

            await TryProjectAsync(
                async () =>
                {
                    var changedMainSlots = result.InventoryChanges.Slots
                        .Where(change => change.ListType == InventoryListType.Main)
                        .Select(change => change.SlotIndex)
                        .Distinct()
                        .ToArray();
                    if (changedMainSlots.Length > 0)
                    {
                        await _inventoryRefresh.SendUpdateItemList(
                            session,
                            InventoryListType.Main,
                            changedMainSlots);
                    }
                },
                characterId,
                "inventory");

            await TryProjectAsync(
                () => SendSkillInfoRefreshAsync(session, characterId),
                characterId,
                "skills");
            await TryProjectAsync(
                async () =>
                {
                    var state = new ExpertJobState
                    {
                        GiveUpCount = result.GiveupCount,
                    };
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x00,
                        0x00CD,
                        ExpertJobInfoBodyBuilder.BuildProjectedBody(0, state, 0)));
                },
                characterId,
                "expert-job info");

            var questManager = session.GameSession?.QuestManager;
            if (questManager != null)
            {
                await TryProjectAsync(
                    questManager.SendActiveQuestListAsync,
                    characterId,
                    "active quests");
                await TryProjectAsync(
                    questManager.SendAcceptableQuestListAsync,
                    characterId,
                    "acceptable quests");
            }

            await TryProjectAsync(
                async () =>
                {
                    var sent = await UserInfoBroadcastService.SendSubtype0Async(
                        session,
                        _characters,
                        _subtype0,
                        _honorLevel,
                        "EXPERT_JOB_GIVEUP");
                    if (!sent)
                        throw new InvalidOperationException(
                            "userinfo subtype0 projection returned false");
                },
                characterId,
                "userinfo");
        }

        private async Task SendSkillInfoRefreshAsync(
            EnhancedClientSession session,
            int characterId)
        {
            var accountId = session.Account?.AccountId ?? 0;
            _dataSource.PrepareForSkillSynchronization(characterId, accountId);
            var snapshot = _dataSource.Load(characterId, accountId);
            var body = SkillInfoBodyBuilder.BuildFrom(
                snapshot.InitializationSnapshot.SkillInfo);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0013,
                body));
        }

        private static async Task TryProjectAsync(
            Func<Task> projection,
            int characterId,
            string name)
        {
            try
            {
                await projection();
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[ExpertJobGiveup] {name} projection failed " +
                    $"cid={characterId}: {ex.Message}");
            }
        }
    }
}
