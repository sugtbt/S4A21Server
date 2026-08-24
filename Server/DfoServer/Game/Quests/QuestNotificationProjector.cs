using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Friends;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Pets;

namespace DfoServer.Game.Quests
{
    // Projects committed quest facts to this session. It owns no quest mutation:
    // QuestService completes the database/inventory transaction before this class runs.
    internal sealed class QuestNotificationProjector
    {
        private readonly ISessionPacketSender _sender;
        private readonly string _connectionString;
        private readonly ICharacterRepository _characterRepository;
        private readonly SqliteCharacterProgressRepository _progressRepository;
        private readonly HonorLevelSyncService _honorLevel;
        private readonly SqliteSubtype0FieldsRepository _subtype0Repository;
        private readonly GrowthCapsuleProgressRepository _growthCapsuleRepository;
        private readonly IExpertJobStateRepository _expertJobStateRepository;
        private readonly SqliteSubtype1Repository _subtype1Repository;
        private readonly SqliteSelectCharacterDataSource
            _selectCharacterDataSource;
        private readonly ISessionDirectory _sessionDirectory;

        internal QuestNotificationProjector(
            ISessionPacketSender sender,
            IGameDatabase database,
            ICharacterRepository characterRepository,
            SqliteCharacterProgressRepository progressRepository,
            HonorLevelSyncService honorLevel,
            SqliteSubtype0FieldsRepository subtype0Repository,
            GrowthCapsuleProgressRepository growthCapsuleRepository,
            IExpertJobStateRepository expertJobStateRepository,
            SqliteSubtype1Repository subtype1Repository,
            SqliteSelectCharacterDataSource selectCharacterDataSource,
            ISessionDirectory sessionDirectory = null)
        {
            _sender = sender ?? throw new ArgumentNullException(nameof(sender));
            _connectionString = (database
                ?? throw new ArgumentNullException(nameof(database)))
                .ConnectionString;
            _characterRepository = characterRepository
                ?? throw new ArgumentNullException(nameof(characterRepository));
            _progressRepository = progressRepository
                ?? throw new ArgumentNullException(nameof(progressRepository));
            _honorLevel = honorLevel ?? throw new ArgumentNullException(nameof(honorLevel));
            _subtype0Repository = subtype0Repository
                ?? throw new ArgumentNullException(nameof(subtype0Repository));
            _growthCapsuleRepository = growthCapsuleRepository
                ?? throw new ArgumentNullException(nameof(growthCapsuleRepository));
            _expertJobStateRepository = expertJobStateRepository
                ?? throw new ArgumentNullException(nameof(expertJobStateRepository));
            _subtype1Repository = subtype1Repository
                ?? throw new ArgumentNullException(nameof(subtype1Repository));
            _selectCharacterDataSource = selectCharacterDataSource
                ?? throw new ArgumentNullException(nameof(selectCharacterDataSource));
            _sessionDirectory = sessionDirectory;
        }

        // Chain 1/2 在 ACK 前刷新 SKILLINFO；chain 20 在 ACK 前发 USERINFO0
        // 和登录布局 0x00CD。
        internal async Task SendPreFinishAckNotificationsAsync(
            int characterId,
            QuestFinishResult result)
        {
            if (result == null || !result.Success)
                return;

            if (result.ChainType == 1 || result.ChainType == 2)
                await SendSkillInfoRefreshAsync(characterId);
            else if (result.ChainType == 20)
                await SendExpertJobChangeNotificationAsync(characterId, result.GrowNumber);
        }

        internal async Task SendGrowupChangeRefreshAsync(int characterId)
        {
            if (characterId <= 0)
                return;

            await SendSkillInfoRefreshAsync(characterId);
            await SendJobChangeNotificationAsync(characterId);
            await SendUserInfoBroadcastAsync(characterId);
        }

        // Must be called after FINISH_QUEST ACK. This is intentionally a projector,
        // not another reward/completion entry point.
        internal async Task ProjectFinishedQuestAsync(
            int characterId,
            QuestFinishResult result,
            bool sendAcceptableQuestList = true)
        {
            if (result == null || !result.Success)
                return;

            var player = _sender.Player;
            if (player == null)
                return;

            var previousLevel = player.Level;
            if (result.Exp > 0)
            {
                player.Exp = result.NewExp;
                player.Level = result.NewLevel;
            }

            var leveledUp = player.Level > previousLevel;
            var inDungeon = player.CurrentRun != null;
            var needsExpNotification = result.Exp > 0 || leveledUp;
            SkillPointProtocolState? skillPoints = null;
            if (needsExpNotification)
            {
                try
                {
                    var record = _characterRepository.GetById(characterId);
                    if (record != null)
                    {
                        CharacterStatComputer.DecodeGrowType(
                            record.GrowType,
                            out var firstGrow,
                            out var secondGrow);
                        skillPoints = SkillStateService.LoadProtocolState(
                            _progressRepository,
                            characterId,
                            record.Job,
                            player.Level,
                            record.BonusSp,
                            record.BonusTp,
                            persist: leveledUp,
                            growType: firstGrow,
                            secondGrowType: secondGrow);
                    }
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"[QuestNotificationProjector] SP calc ERROR: {ex.Message}");
                }
            }

            var refreshesCharacterState = leveledUp
                || result.ChainType == 1
                || result.ChainType == 2
                || result.ChainType == 20
                || result.ChainType == GameWorld.QuestData.ChainTypeSlotExpansion;
            HonorLevelSummary honorLevel = null;
            GrowthCapsuleSummary growthCapsule = null;
            if (result.HonorExp > 0)
            {
                honorLevel = HonorLevelDataProvider.CalculateFromHonorExp(
                    result.TotalHonorExp,
                    0);
                growthCapsule = GrowthCapsuleDataProvider.Calculate(
                    result.TotalGrowthCapsuleExp);
            }
            else if (needsExpNotification || refreshesCharacterState)
            {
                honorLevel = ResolveHonorLevelForExp();
            }

            if (needsExpNotification
                && player.Level >= ExpTableProvider.MaxLevel
                && growthCapsule == null)
            {
                growthCapsule = _growthCapsuleRepository.LoadSummary(_sender.AccountId);
            }

            if (result.HonorExp > 0 && player.Subtype0Tail != null)
                HonorLevelDataProvider.ApplyToSubtype0Tail(player.Subtype0Tail, honorLevel);

            // Never send subtype0 in a dungeon on level up; the client can lose
            // its room state. Preserve the original town-only projection order.
            if (leveledUp && !inDungeon)
            {
                await SendUserInfoSubtype0BroadcastAsync("LevelUp", honorLevel);
                await SendUserInfoBroadcastAsync(characterId, honorLevel);
            }

            if (needsExpNotification && skillPoints.HasValue)
            {
                await _sender.SendNotiAsync(
                    (ushort)NotiPacketTypeA21.EXP,
                    ExpNotificationBuilder.Build(
                        player.Level,
                        player.Exp,
                        skillPoints.Value,
                        honorLevel,
                        growthCapsuleExp: GrowthCapsuleDataProvider.GetDisplayProgress(
                            player.Level,
                            growthCapsule)));
            }
            else if (needsExpNotification)
            {
                FileLogger.Log(
                    $"[QuestNotificationProjector] EXP notification skipped: " +
                    $"skill-point protocol state unavailable for cid={characterId}");
            }

            if (leveledUp)
            {
                FileLogger.Log(
                    $"[QuestNotificationProjector] LEVEL UP from quest: " +
                    $"cid={characterId} {previousLevel}->{player.Level} " +
                    $"exp={player.Exp} inDungeon={inDungeon}");
                if (inDungeon)
                    await SendUserInfoBroadcastAsync(characterId, honorLevel);
            }

            if (result.HonorExp > 0)
            {
                FileLogger.Log(
                    $"[QuestNotificationProjector] HONOR_EXP_GAIN quest: " +
                    $"account={_sender.AccountId} cid={characterId} " +
                    $"gain={result.HonorExp} total={result.TotalHonorExp}");
                FileLogger.Log(
                    $"[QuestNotificationProjector] GROWTH_CAPSULE_EXP_GAIN quest: " +
                    $"account={_sender.AccountId} cid={characterId} " +
                    $"gain={result.GrowthCapsuleExp} total={result.TotalGrowthCapsuleExp}");
            }

            if (result.ChainType == 1 || result.ChainType == 2)
            {
                await SendJobChangeNotificationAsync(characterId, honorLevel);
                await SendUserInfoBroadcastAsync(characterId, honorLevel);
            }
            else if (result.ChainType == GameWorld.QuestData.ChainTypeSlotExpansion)
            {
                await SendUserInfoBroadcastAsync(characterId, honorLevel);
            }
            else if ((result.ChainType == 10 || result.ChainType == 25)
                     && result.PetCreatureEvolution.Changed)
            {
                await PetCreatureRuntimeService.SendPetCreatureEvolutionAsync(
                    _sender,
                    result.PetCreatureEvolution);
            }

            // 等级/职业变化 → 向把 self 加为好友的人重推好友列表（节点数据，跨频道
            // 不分频道，见设计文档 §4.7）。leveledUp 含副本内任务升级；副本结算升级
            // 由 SendInDungeonLevelUpFollowups 覆盖，两者路径互斥不重复推。
            if ((leveledUp || result.ChainType == 1 || result.ChainType == 2)
                && _sessionDirectory != null)
            {
                await UnitedFriendSystem.NotifyFriendListInfoChanged(
                    player, _sessionDirectory);
            }

            if (sendAcceptableQuestList)
                await SendAcceptableQuestListAsync();
        }

        internal async Task SendActiveQuestListAsync(int characterId)
        {
            if (characterId <= 0)
                return;
            await _sender.SendNotiAsync(0x023F, BuildAcceptedQuestNoti(characterId));
        }

        internal async Task SendTriggerChangesAsync(
            IEnumerable<QuestSetTriggerResult> changes)
        {
            if (changes == null)
                return;

            foreach (var change in changes)
            {
                if (change == null
                    || !change.Success
                    || change.PreviousTriggerValue == change.TriggerValue)
                {
                    continue;
                }

                await _sender.SendCmdAckAsync(
                    (ushort)Network.CmdPacketType.SET_QUEST_TRIGGER,
                    QuestAckBuilder.BuildSetTrigger(change));
            }
        }

        internal async Task SendAcceptableQuestListAsync()
        {
            var characterId = _sender.CharacterId;
            if (characterId <= 0)
                return;

            var character = _sender.Player;
            var record = _characterRepository.GetById(characterId);
            int level = record != null
                ? record.Level
                : character != null ? character.Level : 1;
            int job = record != null
                ? record.Job
                : character != null ? character.Job : -1;
            int growType = record != null
                ? record.GrowType
                : character != null ? character.GrowType : -1;
            if (record != null && character != null)
            {
                // GM tools may update characters directly in SQLite while the
                // session remains online. Keep the projection and all later
                // task refreshes on the same persisted identity snapshot.
                character.Level = record.Level;
                character.Job = record.Job;
                character.GrowType = record.GrowType;
                character.GrowupChangeCount = record.GrowupChangeCount;
            }
            var clearedFlags = new QuestRepository(_connectionString)
                .LoadClearedFlags(characterId);
            var allowedCreatureKinds = InventoryContext.TryGetLease(characterId, out var lease)
                ? PetCreatureEvolutionRuntimeService
                    .LoadEligiblePetCreatureEvolutionQuestKinds(lease.Inventory)
                : new HashSet<int>();
            await _sender.SendNotiAsync(
                (ushort)NotiPacketTypeA21.ACCEPTABLE_QUEST_LIST,
                QuestListBodyBuilder.BuildBody(
                    level,
                    job,
                    growType,
                    clearedFlags,
                    allowedCreatureKinds));
        }

        internal async Task SendClearQuestListAsync()
        {
            var characterId = _sender.CharacterId;
            if (characterId <= 0)
                return;

            var clearedFlags = new QuestRepository(_connectionString)
                .LoadClearedFlags(characterId);
            await _sender.SendNotiAsync(
                (ushort)NotiPacketTypeA21.CLEAR_QUEST_LIST,
                ClearQuestListBodyBuilder.BuildBody(clearedFlags));
        }

        private async Task SendUserInfoBroadcastAsync(
            int characterId,
            HonorLevelSummary honorLevel = null)
        {
            try
            {
                var record = _characterRepository.GetById(characterId);
                var addition = _subtype1Repository.Load(characterId);
                if (record == null || addition == null)
                    return;

                var accountCharacters = _characterRepository.ListByAccount(record.AccountId);
                honorLevel ??= _honorLevel.LoadSummary(record.AccountId, accountCharacters);
                CharacterStatComputer.DecodeGrowType(
                    record.GrowType,
                    out var firstGrow,
                    out var secondGrow);
                var synced = SkillStateService.LoadAndSync(
                    _progressRepository,
                    characterId,
                    record.Job,
                    record.Level,
                    record.BonusSp,
                    record.BonusTp,
                    persist: false,
                    growType: firstGrow,
                    secondGrowType: secondGrow);
                await _sender.SendNotiAsync(
                    (ushort)NotiPacketTypeA21.USERINFO,
                    Network.Handlers.UserInfoBroadcastService.BuildSubtype1Body(
                        record,
                        addition,
                        accountCharacters,
                        honorLevel,
                        synced.Skills));
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[QuestNotificationProjector] SendUserInfoBroadcast ERROR: {ex.Message}");
            }
        }

        private async Task SendUserInfoSubtype0BroadcastAsync(
            string reason,
            HonorLevelSummary honorLevel = null)
        {
            var sent = await Network.Handlers.UserInfoBroadcastService.SendSubtype0Async(
                _sender.Player,
                _sender.AccountId,
                body => _sender.SendNotiAsync(
                    (ushort)NotiPacketTypeA21.USERINFO,
                    body),
                _characterRepository,
                _subtype0Repository,
                _honorLevel,
                "QuestNotificationProjector subtype0",
                honorLevel);
            if (sent)
            {
                FileLogger.Log(
                    $"[QuestNotificationProjector] {reason} NOTI 2 subtype0 sent: " +
                    $"cid={_sender.CharacterId}");
            }
        }

        private async Task SendSkillInfoRefreshAsync(int characterId)
        {
            try
            {
                _selectCharacterDataSource.PrepareForSkillSynchronization(
                    characterId,
                    _sender.AccountId);
                var snapshot = _selectCharacterDataSource.Load(
                    characterId,
                    _sender.AccountId);
                var skillBytes = SkillInfoBodyBuilder.BuildFrom(
                    snapshot.InitializationSnapshot.SkillInfo);
                await _sender.SendNotiAsync(
                    (ushort)NotiPacketTypeA21.SKILLINFO,
                    skillBytes);
                FileLogger.Log(
                    $"[QuestNotificationProjector] JobChange skill info refresh sent: " +
                    $"cid={characterId} len={skillBytes.Length}");
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[QuestNotificationProjector] SendSkillInfoRefresh ERROR: {ex.Message}");
            }
        }

        private async Task SendJobChangeNotificationAsync(
            int characterId,
            HonorLevelSummary honorLevel = null)
        {
            try
            {
                var record = _characterRepository.GetById(characterId);
                if (record == null)
                    return;
                record.Subtype0Tail = _subtype0Repository.Load(characterId)
                    ?? new UserInfoMinimumTailSnapshot();
                honorLevel ??= _honorLevel.LoadSummary(record.AccountId);
                HonorLevelDataProvider.ApplyToSubtype0Tail(record.Subtype0Tail, honorLevel);
                _sender.Player.GrowType = record.GrowType;

                await _sender.SendNotiAsync(
                    (ushort)NotiPacketTypeA21.USERINFO,
                    UserInfoSubtype0Builder.BuildNotificationBody(record));
                FileLogger.Log(
                    $"[QuestNotificationProjector] JobChange NOTI 2 subtype0 sent: " +
                    $"cid={characterId} growType=0x{record.GrowType:X2}");
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[QuestNotificationProjector] SendJobChangeNotification ERROR: {ex.Message}");
            }
        }

        internal static byte[] BuildExpertJobChangeUserInfoBody(CharacterRecord record)
        {
            return UserInfoSubtype0Builder.BuildNotificationBody(record);
        }

        private async Task SendExpertJobChangeNotificationAsync(
            int characterId,
            int expertJobType,
            HonorLevelSummary honorLevel = null)
        {
            try
            {
                var record = _characterRepository.GetById(characterId);
                if (record == null || _sender.Player == null)
                    return;

                var tail = _subtype0Repository.Load(characterId)
                    ?? _sender.Player.Subtype0Tail
                    ?? new UserInfoMinimumTailSnapshot();
                tail.ExpertJobType = (byte)expertJobType;
                _sender.Player.Subtype0Tail = tail;
                honorLevel ??= _honorLevel.LoadSummary(record.AccountId);
                HonorLevelDataProvider.ApplyToSubtype0Tail(tail, honorLevel);
                record.Subtype0Tail = tail;

                await _sender.SendNotiAsync(
                    (ushort)NotiPacketTypeA21.USERINFO,
                    BuildExpertJobChangeUserInfoBody(record));
                FileLogger.Log(
                    $"[QuestNotificationProjector] ExpertJobChange NOTI sent: " +
                    $"cid={characterId} expertJobType={expertJobType}");

                await SendExpertJobChangeInfoAsync(
                    characterId,
                    expertJobType,
                    tail);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[QuestNotificationProjector] SendExpertJobChangeNotification ERROR: {ex.Message}");
            }
        }

        // 登录布局 0x00CD 同步已学习配方和附魔师等级/耐久。
        private async Task SendExpertJobChangeInfoAsync(
            int characterId,
            int expertJobType,
            UserInfoMinimumTailSnapshot tail)
        {
            if (expertJobType <= 0)
                return;

            try
            {
                var state = _expertJobStateRepository.Load(characterId, expertJobType);
                var infoBody = ExpertJobInfoBodyBuilder.BuildProjectedBody(
                    expertJobType,
                    state,
                    UserInfoSubtype0Builder.ProjectA21ExpertJobExp(tail));
                await _sender.SendNotiAsync(
                    (ushort)NotiPacketTypeA21.EXPERT_JOB_INFO,
                    infoBody);
                FileLogger.Log(
                    $"[QuestNotificationProjector] ExpertJobChange 0x00CD sent: " +
                    $"cid={characterId} expertJobType={expertJobType} " +
                    $"recipes={state?.LearnedRecipeIds.Count ?? 0} " +
                    $"len={infoBody.Length}");
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[QuestNotificationProjector] ExpertJobChange 0x00CD ERROR: " +
                    $"cid={characterId} {ex.Message}");
            }
        }

        private HonorLevelSummary ResolveHonorLevelForExp()
        {
            var tail = _sender.Player?.Subtype0Tail;
            if (tail != null)
            {
                return new HonorLevelSummary
                {
                    HonorLevel = (byte)Math.Min(byte.MaxValue, tail.ProgressA),
                    HonorExp = tail.ProgressB,
                };
            }

            return _honorLevel.LoadSummary(_sender.AccountId);
        }

        private byte[] BuildAcceptedQuestNoti(int characterId)
        {
            var active = QuestService.LoadActiveQuests(_connectionString, characterId);
            active = QuestDungeonPresentationPlanner
                .ProjectActiveQuests(active)
                .ToList();
            var writer = new Network.GamePacketWriter();
            writer.WriteUInt32((uint)active.Count);
            foreach (var quest in active)
            {
                writer.WriteUInt16(quest.QuestId);
                writer.WriteUInt32(quest.TriggerValue);
            }
            return writer.ToArray();
        }
    }
}
