using DfoServer.Game.Dungeon;
using DfoServer.Game.Quests;
using DfoServer.GameWorld;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal static class DungeonActorQuestSync
    {
        internal static async Task SyncAsync(
            EnhancedClientSession session,
            int actorCode,
            byte actorType,
            DungeonEventEnvelope sourceEvent = null)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null
                || actorCode <= 0
                || !TryGetEnemyType(actorType, out var enemyType))
            {
                return;
            }
            if (sourceEvent != null
                && !run.Matches(sourceEvent.RunIdentity))
            {
                return;
            }

            sourceEvent ??= DungeonEventEnvelope.Create(
                run,
                session.Player.CharacterId,
                "actor-death-quest",
                sourceActorCode: actorCode);
            ApplyAnotherAradActorDeath(
                run,
                actorCode,
                actorType,
                enemyType,
                sourceEvent);
            if (enemyType == QuestDropProvider.EnemyTypeMonster)
            {
                await DungeonQuestBridge.ApplyAsync(
                    session,
                    DungeonQuestProgressEvent.HuntMonster(
                        sourceEvent,
                        run.DungeonId,
                        run.Difficulty,
                        actorCode,
                        actorType));
                if (!run.Matches(sourceEvent.RunIdentity))
                    return;
            }

            if (!QuestData.IsServerDrivenHuntEnemyActorType(enemyType))
                return;

            await DungeonQuestBridge.ApplyAsync(
                session,
                DungeonQuestProgressEvent.HuntEnemy(
                    sourceEvent,
                    run.DungeonId,
                    run.Difficulty,
                        actorCode,
                        enemyType));
        }

        private static void ApplyAnotherAradActorDeath(
            DungeonRun run,
            int actorCode,
            byte actorType,
            int enemyType,
            DungeonEventEnvelope sourceEvent)
        {
            var runtime = run?.AnotherAradQuest;
            if (runtime == null
                || !run.AnotherAradActive
                || sourceEvent == null
                || sourceEvent.SourceEventId == System.Guid.Empty)
            {
                return;
            }

            var isBlocking = true;
            var isHostile = true;
            if (run.TryCaptureCurrentRoomSnapshot(
                    sourceEvent.RoomIdentity,
                    out var roomSnapshot))
            {
                if (sourceEvent.SourceActorId.HasValue)
                {
                    var localIndex = checked((int)(
                        sourceEvent.SourceActorId.Value
                        - roomSnapshot.RoomStartSequence));
                    if (localIndex >= 0
                        && localIndex < roomSnapshot.Monsters.Count)
                    {
                        var actor = roomSnapshot.Monsters[localIndex];
                        isBlocking = actor.IsBlocking;
                        isHostile = actor.Type <= 3
                            || actor.Faction == PvfLib.ApcFaction.Monster;
                    }
                }
            }

            runtime.TryRecordActorDeath(
                sourceEvent.SourceEventId,
                run.AnotherAradHistoricalDungeonId > 0
                    ? run.AnotherAradHistoricalDungeonId
                    : run.DungeonId,
                run.Difficulty,
                actorCode,
                actorType,
                enemyType,
                isHostile,
                isBlocking,
                out _);
        }

        internal static bool TryGetEnemyType(
            byte actorType,
            out int enemyType)
        {
            if (actorType <= 3)
            {
                enemyType = QuestDropProvider.EnemyTypeMonster;
                return true;
            }
            if (DungeonCombatHandler.IsAiCharacterActorType(actorType))
            {
                enemyType = QuestDropProvider.EnemyTypeAiCharacter;
                return true;
            }
            if (actorType == 9)
            {
                enemyType = QuestDropProvider.EnemyTypePassiveObject;
                return true;
            }

            enemyType = 0;
            return false;
        }
    }
}
