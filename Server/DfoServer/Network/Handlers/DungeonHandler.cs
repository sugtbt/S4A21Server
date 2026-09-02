using DfoServer.Game.Inventory;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events.DailyAttendanceAnytime;
using DfoServer.Game.Events.RecommendedDungeons;
using DfoServer.Game.Events.TotalAttendance;
using DfoServer.Game.Mercenary;
using DfoServer.Game.Quests;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Dungeon;
using DfoServer.Network.Parsers.Dungeon;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class DungeonHandler
    {
        public string ProtocolName => "GameProtocol";

        private readonly DungeonSharedServices _services;
        private readonly DungeonEntryHandler _entry;
        private readonly DungeonMapHandler _map;
        private readonly DungeonCombatHandler _combat;
        private readonly DungeonSettlementHandler _settlement;
        private readonly TournamentDungeonCoordinator _tournament;
        private readonly BloodAltarDungeonCoordinator _bloodAltar;
        private readonly DungeonTutorialHandler _tutorial;

        public DungeonHandler(
            Game.ReviveCoin.ReviveCoinService reviveCoinService,
            Game.Characters.SqliteCharacterRepository characterRepository,
            SqliteSelectCharacterDataSource selectCharacterDataSource,
            IRentalTimeProvider rentalTimeProvider,
            string connectionString,
            InventoryRefreshSender inventoryRefresh,
            Game.Party.PartyManager partyManager = null,
            Game.Session.ISessionDirectory sessionDirectory = null,
            Game.Quests.QuestDropService questDropService = null,
            Game.Accounts.AccountExperienceProgressService accountExperience = null,
            IMercenaryRestrictionService mercenaryRestrictions = null,
            IGameDatabase database = null)
            : this(
                null,
                reviveCoinService,
                characterRepository,
                selectCharacterDataSource,
                rentalTimeProvider,
                connectionString,
                inventoryRefresh,
                partyManager,
                sessionDirectory,
                questDropService,
                accountExperience,
                mercenaryRestrictions,
                null,
                null,
                null,
                null,
                null,
                database)
        {
        }

        internal DungeonHandler(
            Game.Dungeon.DungeonPersistentEffectApplicationService persistentEffects,
            Game.ReviveCoin.ReviveCoinService reviveCoinService,
            Game.Characters.SqliteCharacterRepository characterRepository,
            SqliteSelectCharacterDataSource selectCharacterDataSource,
            IRentalTimeProvider rentalTimeProvider,
            string connectionString,
            InventoryRefreshSender inventoryRefresh,
            Game.Party.PartyManager partyManager = null,
            Game.Session.ISessionDirectory sessionDirectory = null,
            Game.Quests.QuestDropService questDropService = null,
            Game.Accounts.AccountExperienceProgressService accountExperience = null,
            IMercenaryRestrictionService mercenaryRestrictions = null,
            RecommendDungeonClearStatsService recommendDungeonClears = null,
            DailyAttendanceAnytimeService dailyAttendanceAnytime = null,
            TotalAttendanceService totalAttendance = null,
            Game.Dungeon.DungeonInstanceRegistry instanceRegistry = null,
            Game.Raid.RaidManager raidManager = null,
            IGameDatabase database = null)
        {
            _services = new DungeonSharedServices(
                reviveCoinService,
                characterRepository,
                selectCharacterDataSource,
                rentalTimeProvider,
                connectionString,
                inventoryRefresh,
                partyManager,
                sessionDirectory,
                questDropService,
                accountExperience,
                mercenaryRestrictions,
                recommendDungeonClears,
                dailyAttendanceAnytime,
                totalAttendance,
                persistentEffects,
                instanceRegistry,
                raidManager,
                database);
            _map = new DungeonMapHandler(_services);
            _entry = new DungeonEntryHandler(_services, _map);
            _settlement = new DungeonSettlementHandler(_services, _entry);
            _tournament = new TournamentDungeonCoordinator(
                _services,
                _settlement);
            _bloodAltar = new BloodAltarDungeonCoordinator(
                _services,
                _settlement);
            _combat = new DungeonCombatHandler(
                _services,
                _settlement,
                _tournament,
                _bloodAltar);
            _bloodAltar.ConfigureKillProcessor(
                _combat.ProcessMechanismKillAsync);
            _settlement.ConfigureBloodAltarPresentation(
                _bloodAltar.OnParticipantClearedAsync,
                _bloodAltar.TryHandleEplpCommandAsync);
            _tutorial = new DungeonTutorialHandler(_services, _settlement, _entry);
        }

        public static async Task ResetDungeonStateAsync(EnhancedClientSession session)
            => await Dungeon.DungeonRunLifecycle.EndRunAsync(
                session,
                Game.Dungeon.DungeonRunEndReason.ReturnToTown);

        public Task Handle_ENUM_CMDPACKET_ENTER_SELECT_DUNGEON(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _entry.HandleEnterSelectDungeon(session, header, body);

        public Task Handle_ENUM_CMDPACKET_SELECT_DUNGEON(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _entry.HandleSelectDungeon(session, header, body);

        public Task Handle_ENUM_CMDPACKET_CRACK_OF_DIMENSION(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _entry.HandleCrackOfDimension(session, header, body);

        public Task<bool> TryHandleAnotherAradQuestAcceptAsync(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _entry.TryHandleAnotherAradQuestAcceptAsync(session, header, body);

        public Task<bool> TryHandleAnotherAradQuestSetTriggerAsync(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _entry.TryHandleAnotherAradQuestSetTriggerAsync(session, header, body);

        public Task<bool> TryHandleAnotherAradQuestFinishAsync(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _entry.TryHandleAnotherAradQuestFinishAsync(session, header, body);

        public Task Handle_ENUM_CMDPACKET_REQUEST_CIRCLE_ENTER(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _entry.HandleRequestCircleEnter(session, header, body);

        public Task Handle_ENUM_CMDPACKET_SEQUENTIAL_DUNGEON_INFO(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _entry.HandleSequentialDungeonInfo(session, header, body);

        public Task Handle_ENUM_CMDPACKET_GORGEOUS_CHALLENGE_TOGGLE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _entry.HandleGorgeousChallengeToggle(session, header, body);

        public Task Handle_ENUM_CMDPACKET_MOVE_MAP(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _map.HandleMoveMap(session, header, body);

        public Task Handle_ENUM_CMDPACKET_HELLPARTY_START(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _map.HandleHellPartyStart(session, header, body);

        public Task Handle_ENUM_CMDPACKET_DIE_MONSTER(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _combat.HandleDieMonster(session, header, body);

        public Task Handle_ENUM_CMDPACKET_DIE_CHARACTER(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _combat.HandleDieCharacter(session, header, body);

        public Task Handle_ENUM_CMDPACKET_DEATH_RESPAWN(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _combat.HandleDeathRespawn(session, header, body);

        public Task Handle_ENUM_CMDPACKET_USE_COIN(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _combat.HandleUseCoin(session, header, body);

        public Task<bool> HandleUseCoinWithResultAsync(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _combat.HandleUseCoin(session, header, body);

        public Task Handle_ENUM_CMDPACKET_GET_ITEM(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _combat.HandleGetItem(session, header, body);

        public Task Handle_ENUM_CMDPACKET_DROP_ITEM(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _combat.HandleDropItem(session, header, body);

        public Task Handle_BOSS_DIE_CHECK(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _combat.HandleBossDieCheck(session, header, body);

        public Task Handle_ENUM_CMDPACKET_SELECT_CARD(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _settlement.HandleSelectCard(session, header, body);

        public Task Handle_ENUM_CMDPACKET_EPLP_COMMAND(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _settlement.HandleEplpCommand(session, header, body);

        public Task Handle_CARD_START_REQUEST(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _settlement.HandleCardStartRequest(session, header, body);

        public Task Handle_SET_PLAY_RESULT(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _settlement.HandleSetPlayResult(session, header, body);

        public Task Handle_LICENSE_DUNGEON_PLAY_RESULT(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
            => _settlement.HandleLicensedDungeonPlayResult(
                session,
                header,
                body);

        public Task Handle_LICENSE_DUNGEON_REQUEST_REWARD(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
            => _settlement.HandleLicensedDungeonRequestReward(
                session,
                header,
                body);

        public Task Handle_ENUM_CMDPACKET_DUNGEON_EVENT_STORY_PAUSE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _tutorial.HandleStoryPause(session, header, body);

        public Task Handle_ENUM_CMDPACKET_CHANGE_TUTORIAL_FLAG(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _tutorial.HandleChangeTutorialFlag(session, header, body);

        public Task Handle_ENUM_CMDPACKET_TUTORIAL_LEVEL_UP(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _tutorial.HandleTutorialLevelUp(session, header, body);

        public Task Handle_BACK_2_VILLAGE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _tutorial.HandleBack2Village(session, header, body);

        public Task Handle_ENUM_CMDPACKET_DEATH_TOWER_STAGE_CMD(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _services.DeathTower.HandleStageCommand(session, header, body);

        public Task<bool> TryHandleDeathTowerUseStackable(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _services.DeathTower.TryHandleUseStackable(session, header, body);

        public Task<bool> TryHandleDeathTowerMoveItem(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _services.DeathTower.TryHandleMoveItem(session, header, body);

        public Task<bool> TryHandleDeathTowerSortItem(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _services.DeathTower.TryHandleSortItem(session, header, body);

        public Task<bool> TryHandleDeathTowerDeleteItem(EnhancedClientSession session, GamePacketHeader header, byte[] body)
            => _services.DeathTower.TryHandleDeleteItem(session, header, body);

        public Task HandleDungeonMechanismCommand(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            if (!DungeonCommandParser.TryParse(
                    header.type,
                    body,
                    out var command,
                    out var error))
            {
                FileLogger.Log(
                    $"[DungeonCommand] parse rejected type=0x{header.type:X4} " +
                    $"cid={session?.Player?.CharacterId ?? 0} error={error} " +
                    $"body={(body == null ? "null" : BitConverter.ToString(body))}");
                return Task.CompletedTask;
            }

            return Dungeon.DungeonMechanismCoordinator.OnCommandReceivedAsync(
                session,
                command,
                _services.Drops,
                _tournament,
                _bloodAltar,
                _services.Database);
        }

        internal Task HandleQuestSetTriggerResultAsync(
            EnhancedClientSession session,
            QuestSetTriggerResult result,
            DungeonEventEnvelope sourceEvent)
            => _settlement.TryClearQuestNpcDungeonAsync(
                session,
                result,
                sourceEvent);

        internal async Task RecoverDungeonParticipantEffectsAsync(
            EnhancedClientSession session)
        {
            await _combat.RecoverParticipantEffectsAsync(session);
            await _settlement.RecoverParticipantClearEffectsAsync(session);
            await Dungeon.SpecialDungeonNotifier
                .RecoverPendingEffectPlansAsync(session);
            await _services.DeathTower.RecoverSettlementAsync(session);
            await _settlement.RecoverPendingSettlementPresentationAsync(session);
            await _bloodAltar.RecoverAsync(session);
            await _tournament.RecoverAsync(session);
            _services.CardRewards.RecoverTimer(session);
            _combat.RecoverDeathRespawnTimer(session);
            Dungeon.DungeonMechanismTimerCoordinator.Recover(session);
        }

        public Task HandleDungeonSceneUniqueIdReport(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (session?.Player != null && body != null && body.Length >= 2)
            {
                var raw = body.Length >= 4
                    ? BitConverter.ToUInt32(body, 0)
                    : BitConverter.ToUInt16(body, 0);
                var sceneUniqueId = (ushort)(raw & 0xFFFF);

                if (sceneUniqueId != 0)
                {
                    session.Player.DungeonSceneUniqueId = sceneUniqueId;
                    FileLogger.Log($"[DungeonHandler] DUNGEON_SCENE_UID: cid={session.Player.CharacterId} baseUid={session.Player.UserId} sceneUid={sceneUniqueId} raw=0x{raw:X8} body={BitConverter.ToString(body)}");
                }
                else
                {
                    FileLogger.Log($"[DungeonHandler] DUNGEON_SCENE_UID: ignored zero cid={session.Player.CharacterId} baseUid={session.Player.UserId} body={BitConverter.ToString(body)}");
                }
            }

            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x01DE, CommonPacketBodyBuilder.BuildSuccessAck()));
        }
    }
}
