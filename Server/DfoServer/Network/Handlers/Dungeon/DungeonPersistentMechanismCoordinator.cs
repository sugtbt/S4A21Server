using DfoServer.Game.CharacterData;
using DfoServer.Game.Dungeon;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Dungeon
{
    // Cross-run dungeon mechanisms use an instance coordinator because their
    // state belongs to repositories, not to DungeonRun. Anton normal conquest
    // is the first such mechanism; ordinary one-run mechanisms remain static.
    internal sealed class DungeonPersistentMechanismCoordinator
    {
        private readonly AntonNormalConquestNotifier _antonNormal;

        internal DungeonPersistentMechanismCoordinator(
            SqliteCharacterStateRepository characterStateRepository)
        {
            _antonNormal = new AntonNormalConquestNotifier(
                characterStateRepository);
        }

        internal Task RestoreBeforeSelectionAsync(
            EnhancedClientSession session)
            => _antonNormal.RestoreBeforeSelectAsync(session);

        internal void ConfigureLinkedChallenge(DungeonRun run)
            => _antonNormal.ConfigureLinkedChallenge(run);

        internal byte ResolveSequentialProgress(int characterId, int configKey)
            => _antonNormal.ResolveSequentialProgress(characterId, configKey);

        internal Task ApplyDungeonClearAsync(
            EnhancedClientSession session,
            DungeonRun run)
            => _antonNormal.ApplyClearAsync(session, run);
    }
}
