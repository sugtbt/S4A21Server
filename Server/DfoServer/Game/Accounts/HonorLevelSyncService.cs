using DfoServer.Game.Characters;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Game.Accounts
{
    public sealed class HonorLevelSyncService
    {
        private readonly HonorLevelProgressRepository _repository;

        public HonorLevelSyncService(ICharacterRepository characterRepository)
            : this(characterRepository, ServerPaths.DatabasePath, ServerPaths.SchemaFilePath)
        {
        }

        public HonorLevelSyncService(
            ICharacterRepository characterRepository,
            string databasePath,
            string schemaFilePath)
            : this(
                characterRepository,
                new GameDatabase(databasePath, schemaFilePath))
        {
        }

        public HonorLevelSyncService(
            ICharacterRepository characterRepository,
            IGameDatabase database)
        {
            _repository = new HonorLevelProgressRepository(
                database,
                characterRepository);
        }

        public HonorLevelSummary LoadSummary(int accountId)
        {
            return _repository.LoadSummary(accountId);
        }

        public HonorLevelSummary LoadSummary(int accountId, IEnumerable<CharacterRecord> accountCharacters)
        {
            return _repository.LoadSummary(accountId, accountCharacters);
        }

        public async Task SendInfoAsync(EnhancedClientSession session, string protocolName, string reason, HonorLevelSummary summary = null)
        {
            var accountId = session?.Account?.AccountId ?? 0;
            if (accountId <= 0)
                return;

            var characterLevel = session?.Player?.Level ?? 0;
            if (!HonorLevelDataProvider.ShouldSendHonorLevelUpBanner(characterLevel))
            {
                FileLogger.Log($"[{protocolName}] HONOR_LEVEL_INFO skipped {reason}: account={accountId} cid={session?.Player?.CharacterId ?? 0} level={characterLevel}");
                return;
            }

            summary = summary ?? LoadSummary(accountId);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0289,
                HonorLevelPacketBuilder.BuildInfoBody(summary)));
            LogInfo(protocolName, reason, accountId, summary);
        }

        public void ApplyToUserInfoAddition(UserInfoAdditionSnapshot addition, int accountId, IEnumerable<CharacterRecord> accountCharacters, HonorLevelSummary summary = null)
        {
            HonorLevelDataProvider.ApplyToUserInfoAddition(addition,
                summary ?? LoadSummary(accountId, accountCharacters));
        }

        public void ApplyToSubtype0Tail(UserInfoMinimumTailSnapshot tail, int accountId, IEnumerable<CharacterRecord> accountCharacters, HonorLevelSummary summary = null)
        {
            HonorLevelDataProvider.ApplyToSubtype0Tail(tail,
                summary ?? LoadSummary(accountId, accountCharacters));
        }

        public void ApplyToCharacterRecord(CharacterRecord record, HonorLevelSummary summary)
        {
            HonorLevelDataProvider.ApplyToCharacterRecord(record, summary);
        }

        private static void LogInfo(string protocolName, string reason, int accountId, HonorLevelSummary summary)
        {
            var suffix = string.IsNullOrWhiteSpace(reason) ? string.Empty : " " + reason;
            FileLogger.Log($"[{protocolName}] HONOR_LEVEL_INFO{suffix}: account={accountId} exp={summary.HonorExp} level={summary.HonorLevel} grade={summary.HonorGrade} fullLevelChars={summary.FullLevelCharacterCount}");
        }
    }
}
