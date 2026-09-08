using DfoServer.Game.Dungeon;
using DfoServer.Game.Quests;
using DfoServer.Infrastructure;
using DfoServer.Network.Parsers.Dungeon;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    // Thin lifecycle facade. Protocol handlers call this class instead of keeping
    // their own list of enabled dungeon mechanisms.
    internal static class DungeonMechanismCoordinator
    {
        internal sealed class MoveMapContext
        {
            internal MoveMapContext(object state)
            {
                State = state;
            }

            internal object State { get; }
        }

        internal readonly struct ClearRequest
        {
            internal ClearRequest(
                bool shouldClearDungeon,
                string clearReason,
                int bossCode)
            {
                ShouldClearDungeon = shouldClearDungeon;
                ClearReason = clearReason ?? string.Empty;
                BossCode = bossCode;
            }

            internal bool ShouldClearDungeon { get; }
            internal string ClearReason { get; }
            internal int BossCode { get; }
        }

        internal readonly struct CharacterDeathResolution
        {
            internal CharacterDeathResolution(
                bool suppressRespawn,
                ClearRequest clearRequest)
            {
                SuppressRespawn = suppressRespawn;
                ClearRequest = clearRequest;
            }

            internal bool SuppressRespawn { get; }
            internal ClearRequest ClearRequest { get; }
        }

        internal static void OnRunCreated(
            EnhancedClientSession session,
            DungeonRun run,
            string source)
            => SpecialDungeonRunCoordinator.InitializeRuntime(
                session,
                run,
                source);

        internal static Task ClearRunEffectsAsync(
            EnhancedClientSession session,
            string reason)
            => SpecialDungeonNotifier.ClearRunBuffsAsync(session, reason);

        internal static Task ClearRunEffectsAsync(
            EnhancedClientSession session,
            DungeonRun run,
            string reason)
            => SpecialDungeonNotifier.ClearRunBuffsAsync(session, run, reason);

        internal static void CancelRunTimers(EnhancedClientSession session)
            => DungeonMechanismTimerCoordinator.Cancel(session);

        internal static void CancelRunTimers(DungeonRun run)
            => DungeonMechanismTimerCoordinator.Cancel(run);

        internal static void ConfigureSelection(
            EnhancedClientSession session,
            MazeInfo maze,
            int[] bossPosition,
            IReadOnlyList<ActiveQuest> activeQuests,
            string source)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null)
                return;

            SpecialDungeonRunCoordinator.ConfigureSelection(
                run,
                maze,
                bossPosition,
                activeQuests);
            ScriptedFatalEndpointCoordinator.ConfigureSelection(
                run,
                maze,
                bossPosition,
                activeQuests);
            DungeonMechanismTimerCoordinator.Start(session, source);
        }

        internal static void CloneSelection(
            EnhancedClientSession session,
            DungeonRun sourceRun,
            DungeonRun targetRun,
            string source)
        {
            SpecialDungeonRunCoordinator.CloneSelectionState(sourceRun, targetRun);
            ScriptedFatalEndpointCoordinator.CloneSelection(sourceRun, targetRun);
            DungeonMechanismTimerCoordinator.Start(session, source);
        }

        internal static IReadOnlyList<IReadOnlyList<(byte, byte)>>
            ResolveSelectionMinimapIconGroups(
                DungeonRun run,
                int dungeonId,
                int mazeIndex)
            => DungeonMinimapProjectionService.Resolve(
                run?.SpecialMinimapIconGroups,
                run?.RidableObjects);

        internal static Task SendSelectionStateAsync(
            EnhancedClientSession session,
            string reason)
            => SpecialDungeonNotifier.SendBossEntranceMinimapIconInfoAsync(
                session,
                reason);

        internal static MoveMapContext ApplyMoveTargetOverride(
            EnhancedClientSession session,
            DungeonRun run,
            int requestedX,
            int requestedY,
            ref DungeonRoomPoint moveTarget)
        {
            var timeSpiral = TimeSpiralDungeonCoordinator.ApplyTeleportOverride(
                session,
                run,
                requestedX,
                requestedY,
                ref moveTarget);
            return timeSpiral == null ? null : new MoveMapContext(timeSpiral);
        }

        internal static void ApplyMapOverride(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRoomPoint moveTarget,
            ref int overrideMapId)
        {
            SpecialDungeonRunCoordinator.TryApplyBossRouteOverride(
                run,
                moveTarget,
                ref overrideMapId);
            SpecialDungeonRunCoordinator.TryApplyGentWarpOverride(
                session,
                run,
                moveTarget,
                ref overrideMapId);
        }

        internal static void CopyMoveStateForParty(
            DungeonRun leaderRun,
            DungeonRun memberRun)
        {
            TimeSpiralDungeonCoordinator.CopyTeleportStateForPartyMove(
                leaderRun,
                memberRun);
            SpecialDungeonRunCoordinator.CopyBossRouteStateForPartyMove(
                leaderRun,
                memberRun);
        }

        internal static void OnMoveMapCompleted(
            EnhancedClientSession session,
            MoveMapContext context,
            string source)
            => TimeSpiralDungeonCoordinator.LogDeferredBuff(
                session,
                context?.State as TimeSpiralDungeonCoordinator.TeleportMoveContext,
                source);

        internal static int ResolveStartMapOverride(
            DungeonRun run,
            int nextX,
            int nextY,
            int requestedOverrideMapId)
            => SpecialDungeonRunCoordinator.ResolveStartMapOverride(
                run,
                nextX,
                nextY,
                requestedOverrideMapId);

        internal static void RestoreRoomState(
            DungeonRun run,
            RoomState roomState)
            => TimeSpiralDungeonCoordinator.RestoreHiddenBoss(run, roomState);

        internal static void AppendStartMapActors(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonData.MazeSumInfo maze)
            => SpecialDungeonRunCoordinator.AppendStartMapActors(
                session,
                run,
                maze);

        internal static void OnRoomStateCreated(
            EnhancedClientSession session,
            DungeonRun run,
            RoomState roomState)
            => TimeSpiralDungeonCoordinator.RegisterHiddenBossAfterStartMap(
                session,
                run,
                roomState);

        internal static async Task OnStartMapSentAsync(
            EnhancedClientSession session,
            DungeonParticipantRoomIdentity roomIdentity,
            Func<byte[], Func<bool>, Action, Task<bool>>
                trySendPacketAsync,
            Func<bool> canProject)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null
                || !run.Matches(roomIdentity)
                || (canProject != null && !canProject()))
                return;

            // Preserve the established order: gauge state first, then the
            // scene condition that depends on client START_MAP actors.
            await SpecialDungeonNotifier.SendStartMapStateAsync(
                session,
                run,
                packet => trySendPacketAsync(
                    packet,
                    null,
                    null));
            if (!session.Player.IsCurrentDungeonParticipantRoom(roomIdentity)
                || (canProject != null && !canProject()))
                return;
            await EventMonsterConditionCoordinator.AdvanceAfterStartMapAsync(
                session,
                run,
                roomIdentity,
                trySendPacketAsync,
                canProject);
        }

        internal static async Task<ClearRequest> OnMonsterKilledAsync(
            EnhancedClientSession session,
            DungeonEventEnvelope killEvent,
            ushort sequenceId,
            int monsterCode,
            byte monsterType)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null
                || killEvent == null
                || !run.Matches(killEvent.RunIdentity)
                || (killEvent.RoomInstanceId.HasValue
                    && killEvent.RoomInstanceId.Value
                        != run.CurrentRoomInstanceId))
            {
                return default;
            }

            ScriptedFatalEndpointCoordinator.OnMonsterKilled(
                session,
                run,
                monsterCode);
            await SpecialDungeonNotifier.ObserveMonsterKilledAsync(
                session,
                run,
                killEvent,
                monsterCode,
                monsterType);

            if (!session.Player.IsCurrentDungeonRun(killEvent.RunIdentity)
                || (killEvent.RoomInstanceId.HasValue
                    && run.CurrentRoomInstanceId
                        != killEvent.RoomInstanceId.Value))
                return default;

            RoomState roomState;
            bool hiddenBossKilled;
            lock (run.SyncRoot)
            {
                run.RoomStates.TryGetValue(run.RoomKey, out roomState);
                hiddenBossKilled = TimeSpiralDungeonCoordinator.IsTrackedHiddenBossKill(
                    run,
                    roomState,
                    sequenceId,
                    monsterCode);
            }

            if (!hiddenBossKilled)
                return default;

            var roomX = roomState != null ? roomState.Maze.X : 0;
            var roomY = roomState != null ? roomState.Maze.Y : 0;
            var mapId = roomState != null ? roomState.Maze.Index : 0;
            var reason =
                $"TimeSpiral hidden boss seq={run.TimeSpiralHiddenBossSeqId} " +
                $"code={run.TimeSpiralHiddenBossCode}";
            FileLogger.Log(
                $"[DungeonMechanism] MonsterKilled produced clear request: " +
                $"mechanism=time-spiral-hidden-boss cid={session.Player.CharacterId} " +
                $"dungeon={run.DungeonId} room=({roomX},{roomY}) map={mapId} " +
                $"seq={sequenceId} code={monsterCode}");

            return new ClearRequest(
                shouldClearDungeon: true,
                clearReason: reason,
                bossCode: monsterCode);
        }

        internal static void OnPassiveObjectDestroyed(
            EnhancedClientSession session,
            int objectCode)
            => OnPassiveObjectDestroyed(
                session,
                session?.Player?.CurrentRun,
                objectCode);

        internal static void OnPassiveObjectDestroyed(
            EnhancedClientSession session,
            DungeonRun run,
            int objectCode)
            => ScriptedFatalEndpointCoordinator.OnPassiveObjectDestroyed(
                session,
                run,
                objectCode);

        internal static ClearRequest OnStoryPause(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity expectedRun,
            byte pauseFlag)
        {
            if (pauseFlag != 1
                || session?.Player == null
                || run == null
                || !run.Matches(expectedRun)
                || run.Phase != DungeonRunPhase.InProgress
                || run.ClearCondition == null
                || !run.ClearCondition.HasConditions)
            {
                return default;
            }

            if (!run.TryCaptureCurrentRoomSnapshot(out var roomSnapshot)
                || roomSnapshot?.RoomState == null)
            {
                return default;
            }

            var roomState = roomSnapshot.RoomState;
            var passiveObjectCodes = roomState.Maze.PassiveObjectCodes;
            if (passiveObjectCodes == null || passiveObjectCodes.Count == 0)
                return default;

            if (!roomState.TryBeginStoryPauseClear())
                return default;

            var conditionCleared = run.ClearCondition.TryCheckAny(
                    type: 0,
                    targetIds: passiveObjectCodes,
                    out var matchedObjectCode);
            if (matchedObjectCode == 0)
            {
                roomState.CancelStoryPauseClear();
                return default;
            }

            var roomX = roomState.Maze.X;
            var roomY = roomState.Maze.Y;
            var mapId = roomState.Maze.Index;
            FileLogger.Log(
                $"[DungeonMechanism] STORY_PAUSE clear condition matched: " +
                $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                $"room=({roomX},{roomY}) map={mapId} " +
                $"object={matchedObjectCode} cleared={run.ClearCondition.IsCleared}");

            return new ClearRequest(
                shouldClearDungeon: conditionCleared,
                clearReason:
                    $"story pause destroy-object clear: room=({roomX},{roomY}) " +
                    $"map={mapId} object={matchedObjectCode}",
                bossCode: 0);
        }

        internal static ClearRequest OnQuestProgressCompleted(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity expectedRun,
            QuestSetTriggerResult result)
        {
            if (session?.Player == null
                || run == null
                || result == null
                || !result.Success
                || result.PreviousTriggerValue == 0
                || result.TriggerValue != 0
                || !run.MazeQuestConnected
                || result.QuestId == 0
                || result.QuestId != run.ActiveQuestMazeQuestId
                || !run.Matches(expectedRun)
                || run.Phase != DungeonRunPhase.InProgress
                || run.ClearCondition == null
                || !run.ClearCondition.HasConditions)
            {
                return default;
            }

            if (!run.TryCaptureCurrentRoomSnapshot(out var roomSnapshot)
                || roomSnapshot?.RoomState == null)
            {
                return default;
            }

            var roomState = roomSnapshot.RoomState;
            if (run.BossMapPos == null
                || run.BossMapPos.Length < 2
                || roomState.Maze.X != run.BossMapPos[0]
                || roomState.Maze.Y != run.BossMapPos[1])
            {
                return default;
            }

            var passiveObjectCodes = roomState.Maze.PassiveObjectCodes;
            if (passiveObjectCodes == null || passiveObjectCodes.Count == 0)
                return default;

            if (!roomState.TryBeginStoryPauseClear())
                return default;

            var conditionCleared = run.ClearCondition.TryCheckAny(
                type: 0,
                targetIds: passiveObjectCodes,
                out var matchedObjectCode);
            if (matchedObjectCode == 0)
            {
                roomState.CancelStoryPauseClear();
                return default;
            }

            var roomX = roomState.Maze.X;
            var roomY = roomState.Maze.Y;
            var mapId = roomState.Maze.Index;
            FileLogger.Log(
                $"[DungeonMechanism] quest completion clear condition matched: " +
                $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                $"maze={run.MazeIndex} quest={result.QuestId} " +
                $"room=({roomX},{roomY}) map={mapId} " +
                $"object={matchedObjectCode} cleared={run.ClearCondition.IsCleared}");

            return new ClearRequest(
                shouldClearDungeon: conditionCleared,
                clearReason:
                    $"quest completion destroy-object clear: quest={result.QuestId} " +
                    $"room=({roomX},{roomY}) map={mapId} object={matchedObjectCode}",
                bossCode: 0);
        }

        internal static CharacterDeathResolution OnCharacterDied(
            EnhancedClientSession session)
            => OnCharacterDied(
                session,
                session?.Player?.CurrentRun);

        internal static CharacterDeathResolution OnCharacterDied(
            EnhancedClientSession session,
            DungeonRun run)
        {
            var result =
                ScriptedFatalEndpointCoordinator.OnCharacterDied(session, run);
            return new CharacterDeathResolution(
                result.SuppressRespawn,
                result.ShouldClearDungeon
                    ? new ClearRequest(
                        shouldClearDungeon: true,
                        clearReason: result.Reason,
                        bossCode: 0)
                    : default);
        }

        internal static ClearRequest OnBossDieCheck(
            EnhancedClientSession session,
            BossDieCheckRequest request)
            => OnBossDieCheck(
                session,
                session?.Player?.CurrentRun,
                request);

        internal static ClearRequest OnBossDieCheck(
            EnhancedClientSession session,
            DungeonRun run,
            BossDieCheckRequest request)
        {
            if (session?.Player == null
                || run == null
                || !session.Player.IsCurrentDungeonRun(run.CaptureIdentity()))
            {
                return default;
            }

            run.SpecialDungeon?.NoteSeizeMoneyBossSeq(request.BossSequence);
            FileLogger.Log(
                $"[DungeonMechanism] BOSS_DIE_CHECK observed: " +
                $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                $"kind={run.SpecialDungeon?.Kind.ToString() ?? "none"} " +
                $"uid={request.UserId} bossSeq={request.BossSequence}");

            if (!run.HasBossEntranceConditionalSummon
                || run.Phase != DungeonRunPhase.InProgress
                || !run.ConditionalBossSpawned
                || request.BossSequence != SpecialDungeonNotifier.BossSummonRuntimeKey)
            {
                return default;
            }

            var bossCode = run.ConditionalBossCode;
            if (bossCode <= 0)
                return default;

            return new ClearRequest(
                shouldClearDungeon: true,
                clearReason:
                    $"conditional boss die check " +
                    $"uid={request.UserId} bossSeq={request.BossSequence}",
                bossCode: bossCode);
        }

        internal static Task OnCommandReceivedAsync(
            EnhancedClientSession session,
            DungeonCommand command,
            DropService drops,
            TournamentDungeonCoordinator tournaments,
            BloodAltarDungeonCoordinator bloodAltars,
            IGameDatabase database)
            => DungeonCommandReceivedDispatcher.DispatchAsync(
                session,
                command,
                drops,
                tournaments,
                bloodAltars,
                database);

        internal static Task OnDungeonClearedAsync(
            EnhancedClientSession session,
            DungeonRun run)
            => SpecialDungeonSettlementCoordinator.OnDungeonClearedAsync(
                session,
                run);
    }
}
