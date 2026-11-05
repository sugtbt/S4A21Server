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
            await DungeonQuestBridge.ApplyAsync(
                session,
                DungeonQuestProgressEvent.ActorDeath(
                    sourceEvent,
                    run.DungeonId,
                    run.Difficulty,
                    actorCode,
                    actorType,
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
            else if (!run.Matches(sourceEvent.RoomIdentity))
            {
                // 扇出/重放投影的事件可能已不指向当前房间：AnotherArad 写入前
                // 复验房间，避免把进度记进旧房间的观察记录。
                return;
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
