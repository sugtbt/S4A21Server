using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Friends;
using DfoServer.Game.Inventory;
using DfoServer.Game.Progression;
using DfoServer.Game.Quests;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class DungeonProgressNotificationProjector
    {
        private readonly string _connectionString;
        private readonly SqliteCharacterRepository _characterRepository;
        private readonly SqliteSubtype1Repository _subtype1Repository;
        private readonly SqliteCharacterProgressRepository _progressRepository;
        private readonly SqliteSubtype0FieldsRepository _subtype0Repository;
        private readonly HonorLevelSyncService _honorLevel;
        private readonly AccountExperienceProgressService _accountExperience;
        private readonly ISessionDirectory _sessions;

        internal DungeonProgressNotificationProjector(
            string connectionString,
            SqliteCharacterRepository characterRepository,
            SqliteSubtype1Repository subtype1Repository,
            SqliteCharacterProgressRepository progressRepository,
            SqliteSubtype0FieldsRepository subtype0Repository,
            HonorLevelSyncService honorLevel,
            AccountExperienceProgressService accountExperience,
            ISessionDirectory sessions = null)
        {
            _connectionString = connectionString;
            _characterRepository = characterRepository;
            _subtype1Repository = subtype1Repository;
            _progressRepository = progressRepository;
            _subtype0Repository = subtype0Repository;
            _honorLevel = honorLevel;
            _accountExperience = accountExperience;
            _sessions = sessions;
        }

        internal HonorLevelSummary ResolveHonorLevelForExp(
            EnhancedClientSession session,
            HonorLevelSummary summary = null)
        {
            var tail = session?.Player?.Subtype0Tail;
            if (summary == null && tail != null)
            {
                return new HonorLevelSummary
                {
                    HonorLevel = (byte)Math.Min(byte.MaxValue, tail.ProgressA),
                    HonorExp = tail.ProgressB,
                };
            }

            summary ??= _honorLevel.LoadSummary(session?.Account?.AccountId ?? 0);
            if (session?.Player != null)
            {
                tail ??= new UserInfoMinimumTailSnapshot();
                HonorLevelDataProvider.ApplyToSubtype0Tail(tail, summary);
                session.Player.Subtype0Tail = tail;
            }
            return summary;
        }

        internal GrowthCapsuleSummary ResolveGrowthCapsuleForExp(
            EnhancedClientSession session,
            GrowthCapsuleSummary summary = null)
        {
            if ((session?.Player?.Level ?? 0) < Game.Dungeon.ExpTableProvider.MaxLevel)
                return summary ?? GrowthCapsuleDataProvider.Calculate(0);
            return summary
                ?? _accountExperience.LoadGrowthCapsule(
                    session?.Account?.AccountId ?? 0);
        }

        internal (SkillInfoSnapshot Skills, SkillPointState Points)
            LoadSyncedSkillState(
                int characterId,
                byte currentLevel,
                bool persist = false)
        {
            var record = _characterRepository.GetById(characterId);
            if (record == null)
                return (_progressRepository.LoadSkills(characterId), null);

            CharacterStatComputer.DecodeGrowType(
                record.GrowType,
                out var firstGrow,
                out var secondGrow);
            return SkillStateService.LoadAndSync(
                _progressRepository,
                characterId,
                record.Job,
                currentLevel > 0 ? currentLevel : record.Level,
                record.BonusSp,
                record.BonusTp,
                persist,
                firstGrow,
                secondGrow);
        }

        internal bool TryGetSkillPointProtocolState(
            EnhancedClientSession session,
            bool persist,
            string logTag,
            out SkillPointProtocolState skillPoints)
        {
            skillPoints = default;
            try
            {
                var synced = LoadSyncedSkillState(
                    session.Player.CharacterId,
                    session.Player.Level,
                    persist);
                if (synced.Points != null)
                {
                    skillPoints = SkillStateService.GetProtocolState(
                        synced.Skills,
                        synced.Points);
                    return true;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DungeonProgressNotificationProjector] {logTag} ERROR: " +
                    $"skill-point protocol state refresh failed: {ex.Message}");
                return false;
            }

            FileLogger.Log(
                $"[DungeonProgressNotificationProjector] {logTag} ERROR: " +
                "no verified skill-point protocol state is available");
            return false;
        }

        internal async Task SendExpGrantNotificationAsync(
            EnhancedClientSession session,
            ExperienceGrantResult grant,
            string logTag,
            uint growthContractBonusExp = 0,
            uint eliteMonsterKillBonusExp = 0,
            bool reloadMissingAccountProgress = false)
        {
            if (grant == null
                || (grant.NormalExpGain == 0
                    && grant.HonorExpGain == 0
                    && !grant.LeveledUp))
            {
                return;
            }

            var honor = grant.Honor;
            var capsule = grant.GrowthCapsule;
            if (reloadMissingAccountProgress)
            {
                var accountId = session?.Account?.AccountId ?? 0;
                honor ??= _honorLevel.LoadSummary(accountId);
                capsule ??= _accountExperience.LoadGrowthCapsule(accountId);
            }
            honor = ResolveHonorLevelForExp(session, honor);
            capsule = ResolveGrowthCapsuleForExp(session, capsule);
            if (!TryGetSkillPointProtocolState(
                    session,
                    persist: grant.LeveledUp,
                    logTag,
                    out var skillPoints))
            {
                return;
            }

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.EXP,
                ExpNotificationBuilder.Build(
                    session.Player.Level,
                    session.Player.Exp,
                    skillPoints,
                    honor,
                    growthContractBonusExp: growthContractBonusExp,
                    eliteMonsterKillBonusExp: eliteMonsterKillBonusExp,
                    growthCapsuleExp: GrowthCapsuleDataProvider.GetDisplayProgress(
                        session.Player.Level,
                        capsule))));
        }

        internal async Task SendInDungeonLevelUpFollowups(
            EnhancedClientSession session)
        {
            await SendQuestListRefresh(session);
            await SendUserInfoBroadcast(session);
            // 副本结算升级：把升级者加为好友的在线会话重推好友列表（节点数据，跨频道）。
            if (_sessions != null)
                await UnitedFriendSystem.NotifyFriendListInfoChanged(
                    session, _sessions);
        }

        internal async Task SendQuestListRefresh(EnhancedClientSession session)
        {
            try
            {
                var record = _characterRepository.GetById(
                    session.Player.CharacterId);
                if (record == null)
                    return;

                var clearedFlags = new QuestRepository(_connectionString)
                    .LoadClearedFlags(session.Player.CharacterId);
                var allowedCreatureKinds = InventoryContext.TryGetLease(
                    session.Player.CharacterId,
                    out var lease)
                    ? PetCreatureEvolutionRuntimeService
                        .LoadEligiblePetCreatureEvolutionQuestKinds(lease.Inventory)
                    : new HashSet<int>();
                var body = QuestListBodyBuilder.BuildBody(
                    session.Player.Level,
                    record.Job,
                    record.GrowType,
                    clearedFlags,
                    allowedCreatureKinds);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.ACCEPTABLE_QUEST_LIST,
                    body));
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[GameProtocol] SendQuestListRefresh ERROR: {ex.Message}");
            }
        }

        internal async Task SendUserInfoBroadcast(EnhancedClientSession session)
        {
            try
            {
                var characterId = session.Player.CharacterId;
                var record = _characterRepository.GetById(characterId);
                var addition = _subtype1Repository.HasData(characterId)
                    ? _subtype1Repository.Load(characterId)
                    : null;
                if (record == null || addition == null)
                    return;

                var accountId = session.Account?.AccountId ?? record.AccountId;
                var accountCharacters = _characterRepository.ListByAccount(accountId);
                var honor = _honorLevel.LoadSummary(accountId, accountCharacters);
                var skills = LoadSyncedSkillState(
                    characterId,
                    session.Player.Level).Skills;
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x0002,
                    UserInfoBroadcastService.BuildSubtype1Body(
                        record,
                        addition,
                        accountCharacters,
                        honor,
                        skills)));
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DungeonProgressNotificationProjector] " +
                    $"SendUserInfoBroadcast ERROR: {ex.Message}");
            }
        }

        internal Task SendUserInfoSubtype0Broadcast(
            EnhancedClientSession session)
            => UserInfoBroadcastService.SendSubtype0Async(
                session,
                _characterRepository,
                _subtype0Repository,
                _honorLevel,
                "DungeonProgressNotificationProjector subtype0");
    }
}
