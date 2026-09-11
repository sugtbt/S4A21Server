using DfoServer.Game.Dungeon;
using DfoServer.Game.Quests;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Linq;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal static class SpecialDungeonRunCoordinator
    {
        internal static void InitializeRuntime(
            EnhancedClientSession session,
            DungeonRun run,
            string source)
        {
            if (run == null
                || session?.Player == null
                || !session.Player.IsCurrentDungeonRun(run.CaptureIdentity()))
                return;

            var dungeonId = run.DungeonId;
            run.SpecialDungeon =
                SpecialDungeonDefinitionCatalog.TryGet(
                    dungeonId,
                    out var definition)
                    ? new SpecialDungeonRuntime(definition)
                    : null;
            var special = run.SpecialDungeon;
            if (special == null)
                return;

            FileLogger.Log(
                $"[SpecialDungeonModule] runtime init source={source} " +
                $"cid={session.Player.CharacterId} dungeon={dungeonId} kind={special.Kind}");
        }

        internal static void ConfigureSelection(
            DungeonRun run,
            MazeInfo maze,
            int[] bossPos,
            IReadOnlyList<ActiveQuest> activeQuests)
        {
            if (run == null)
                return;

            DungeonFile dungeonFile;
            try
            {
                dungeonFile = DungeonData.GetDungeonFile(run.DungeonId);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[SpecialDungeonModule] selection config failed: " +
                    $"dungeon={run.DungeonId} maze={run.MazeIndex} error={ex.Message}");
                return;
            }

            run.IgnoreDefaultDungeonClear =
                dungeonFile.IgnoreDefaultDungeonClear
                || TimeSpiralDungeonCoordinator.IsDungeon(run.DungeonId);
            run.Instance?.Mechanisms.ResetConditionalBossSpawn();
            run.BossEntranceConditionTargets.Clear();
            run.BossEntranceConditionalSummonCodes.Clear();
            run.BossEntranceConditionComplete = false;
            run.ConditionalBossSpawned = false;
            run.ConditionalBossCode = 0;
            run.SpecialMinimapIconGroups = null;

            var targetCodes = DungeonConditionDefinitionParser.ParseMonsterCodes(
                dungeonFile.BossRoomEntranceCondition,
                "[hunt monster]");
            var summonCodes = DungeonConditionDefinitionParser.ParseMonsterCodes(
                dungeonFile.BossRoomEntranceCondition,
                "[summon monster]");
            if (targetCodes.Count > 0 && summonCodes.Count > 0)
            {
                run.BossEntranceConditionTargets = BuildBossEntranceConditionTargets(
                    run.DungeonId,
                    run.MazeIndex,
                    maze,
                    bossPos,
                    targetCodes);
                if (run.BossEntranceConditionTargets.Count > 0)
                {
                    run.BossEntranceConditionalSummonCodes.AddRange(summonCodes);
                    run.SpecialMinimapIconGroups =
                        BuildMinimapIconGroupsFromTargets(
                            run.BossEntranceConditionTargets);
                }
            }

            var special = run.SpecialDungeon;
            if (special?.Kind == SpecialDungeonKind.GentInfiltrate)
            {
                special.ConfigureGentInfiltrateBossEntrance(
                    SpecialDungeonDefinitionCatalog
                        .ParseGentInfiltrateTowerRequirements(
                            dungeonFile.BossRoomEntranceCondition),
                    special.Definition.TimerSeconds);
                run.SpecialMinimapIconGroups = BuildGentTowerIconGroups(
                    run.DungeonId,
                    run.MazeIndex,
                    maze,
                    bossPos,
                    special);
            }

            // Keep the existing quest/random choice as a fallback. When the PVF
            // candidate MAP greed masks differ by entrance, the shared instance
            // runtime commits the matching pool when that entrance is traversed.
            var fallbackBossMapId = ResolveSelectedBossMapId(
                run.DungeonId,
                run.MazeIndex,
                maze,
                bossPos,
                activeQuests,
                run.Difficulty);
            run.SelectedBossMapId = fallbackBossMapId;
            var bossRouteDefinition =
                DungeonBossRouteDefinitionProjector.Project(
                    run.DungeonId,
                    run.MazeIndex,
                    maze,
                    bossPos);
            if (bossRouteDefinition != null
                && fallbackBossMapId > 0
                && bossRouteDefinition.ContainsMapId(fallbackBossMapId))
            {
                run.Instance.Mechanisms.TryAttachBossRoute(
                    new DungeonBossRouteRuntime(
                        bossRouteDefinition,
                        fallbackBossMapId));
            }

            FileLogger.Log(
                $"[SpecialDungeonModule] selection configured: " +
                $"dungeon={run.DungeonId} maze={run.MazeIndex} " +
                $"kind={special?.Kind.ToString() ?? "none"} " +
                $"ignoreDefault={run.IgnoreDefaultDungeonClear} " +
                $"conditionTargets={run.BossEntranceConditionTargets.Count} " +
                $"conditionalBosses={run.BossEntranceConditionalSummonCodes.Count} " +
                $"iconGroups={run.SpecialMinimapIconGroups?.Count ?? 0} " +
                $"timer={special?.GentInfiltrateTimerSeconds ?? 0} " +
                $"bossRouteCount={run.Instance.Mechanisms.BossRoute?.Definition.Routes.Count ?? 0} " +
                $"bossFallback={fallbackBossMapId}");
        }

        internal static void CloneSelectionState(DungeonRun source, DungeonRun target)
        {
            if (source == null || target == null)
                return;

            target.SpecialDungeon = source.SpecialDungeon?.CloneFresh();
            target.IgnoreDefaultDungeonClear = source.IgnoreDefaultDungeonClear;
            target.SpecialMinimapIconGroups = CloneMinimapIconGroups(
                source.SpecialMinimapIconGroups);
            target.BossEntranceConditionTargets = CloneBossEntranceConditionTargets(
                source.BossEntranceConditionTargets);
            target.BossEntranceConditionalSummonCodes =
                source.BossEntranceConditionalSummonCodes == null
                    ? new List<int>()
                    : new List<int>(source.BossEntranceConditionalSummonCodes);
            target.SelectedBossMapId = source.SelectedBossMapId;
        }

        internal static int ResolveStartMapOverride(
            DungeonRun run,
            int nextX,
            int nextY,
            int requestedOverrideMapId)
        {
            if (requestedOverrideMapId > 0
                || run == null
                || run.BossMapPos == null
                || run.BossMapPos.Length < 2
                || nextX != run.BossMapPos[0]
                || nextY != run.BossMapPos[1])
            {
                return requestedOverrideMapId;
            }

            var bossRoute = run.Instance?.Mechanisms.BossRoute;
            if (bossRoute != null)
            {
                var selectedMapId = bossRoute.ResolveForStartMap(
                    out var fallbackCommitted);
                run.SelectedBossMapId = selectedMapId;
                if (fallbackCommitted)
                {
                    FileLogger.Log(
                        $"[SpecialDungeonModule] boss route fallback committed: " +
                        $"instance={run.PartyDungeonInstanceId} dungeon={run.DungeonId} " +
                        $"boss=({nextX},{nextY}) map={selectedMapId}");
                }
                return selectedMapId;
            }

            if (run.SelectedBossMapId <= 0)
                return requestedOverrideMapId;
            return run.SelectedBossMapId;
        }

        internal static bool TryApplyBossRouteOverride(
            DungeonRun run,
            DungeonRoomPoint moveTarget,
            ref int overrideMapId)
        {
            var bossRoute = run?.Instance?.Mechanisms.BossRoute;
            if (bossRoute == null || overrideMapId > 0)
                return false;

            if (!bossRoute.TrySelectForMove(
                    run.RoomKey.X,
                    run.RoomKey.Y,
                    moveTarget.X,
                    moveTarget.Y,
                    ServerRandom.Next,
                    out var selectedMapId,
                    out var transitioned))
            {
                return false;
            }

            run.SelectedBossMapId = selectedMapId;
            overrideMapId = selectedMapId;
            if (transitioned)
            {
                FileLogger.Log(
                    $"[SpecialDungeonModule] boss route committed: " +
                    $"instance={run.PartyDungeonInstanceId} dungeon={run.DungeonId} " +
                    $"source=({run.RoomKey.X},{run.RoomKey.Y}) " +
                    $"boss=({moveTarget.X},{moveTarget.Y}) " +
                    $"direction={bossRoute.SelectedDirection} map={selectedMapId}");
            }
            return true;
        }

        internal static void CopyBossRouteStateForPartyMove(
            DungeonRun leaderRun,
            DungeonRun memberRun)
        {
            if (leaderRun == null
                || memberRun == null
                || leaderRun.PartyDungeonInstanceId
                    != memberRun.PartyDungeonInstanceId)
            {
                return;
            }

            var selectedMapId = leaderRun.Instance.Mechanisms.BossRoute?.SelectedMapId
                ?? leaderRun.SelectedBossMapId;
            if (selectedMapId > 0)
                memberRun.SelectedBossMapId = selectedMapId;
        }

        internal static void AppendStartMapActors(
            EnhancedClientSession session,
            DungeonData.MazeSumInfo maze)
            => AppendStartMapActors(
                session,
                session?.Player?.CurrentRun,
                maze);

        internal static void AppendStartMapActors(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonData.MazeSumInfo maze)
        {
            if (session?.Player == null
                || run == null
                || !session.Player.IsCurrentDungeonRun(run.CaptureIdentity())
                || !run.HasBossEntranceConditionalSummon
                || maze.Monsters == null)
            {
                return;
            }

            AppendHiddenBossTemplates(run, maze);
            AppendConditionActors(run, maze);
        }

        internal static bool TryApplyGentWarpOverride(
            EnhancedClientSession session,
            DungeonRoomPoint moveTarget,
            ref int overrideMapId)
            => TryApplyGentWarpOverride(
                session,
                session?.Player?.CurrentRun,
                moveTarget,
                ref overrideMapId);

        internal static bool TryApplyGentWarpOverride(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRoomPoint moveTarget,
            ref int overrideMapId)
        {
            var special = run?.SpecialDungeon;
            if (session?.Player == null
                || run == null
                || !session.Player.IsCurrentDungeonRun(run.CaptureIdentity())
                || special == null
                || special.Kind != SpecialDungeonKind.GentInfiltrate
                || !special.GentInfiltrateConditionComplete)
            {
                return false;
            }

            if (!DungeonData.TryGetWarpMapOverride(
                    run.DungeonId,
                    run.MazeIndex,
                    moveTarget.X,
                    moveTarget.Y,
                    out var sourceX,
                    out var sourceY,
                    out var destX,
                    out var destY,
                    out var warpMapId))
            {
                return false;
            }

            overrideMapId = warpMapId;
            FileLogger.Log(
                $"[SpecialDungeonModule] GENT_INFILTRATE warp override: " +
                $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                $"target=({moveTarget.X},{moveTarget.Y}) source=({sourceX},{sourceY}) " +
                $"dest=({destX},{destY}) map={warpMapId} " +
                $"strongWarlord={special.GentInfiltrateStrongWarlord} " +
                $"completion={special.GentInfiltrateCompletionSource}");
            return true;
        }

        internal static int ResolveSelectedBossMapId(
            int dungeonId,
            int mazeIndex,
            MazeInfo maze,
            int[] bossPos,
            IReadOnlyList<ActiveQuest> activeQuests,
            int difficulty = -1)
        {
            if (bossPos == null
                || bossPos.Length < 2
                || !GameWorld.DungeonMapResolver.HasExplicitBossCandidatePool(
                    maze,
                    bossPos[0],
                    bossPos[1]))
            {
                return -1;
            }

            var questMapId = ResolveQuestBoundBossMapId(
                dungeonId,
                maze,
                bossPos,
                activeQuests,
                difficulty);
            if (questMapId > 0)
                return questMapId;

            try
            {
                return GameWorld.DungeonMapResolver
                    .ResolveExplicitBossCandidateMapId(
                    maze,
                    bossPos[0],
                    bossPos[1]);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[SpecialDungeonModule] boss map selection failed: " +
                    $"dungeon={dungeonId} maze={mazeIndex} error={ex.Message}");
                return -1;
            }
        }

        internal static int ResolveQuestBoundBossMapId(
            int dungeonId,
            MazeInfo maze,
            int[] bossPos,
            IReadOnlyList<ActiveQuest> activeQuests,
            int difficulty = -1)
        {
            if (bossPos == null
                || bossPos.Length < 2
                || activeQuests == null
                || activeQuests.Count == 0)
            {
                return -1;
            }

            var candidateMapIds =
                GameWorld.DungeonMapResolver.GetExplicitBossCandidateMapIds(
                    maze,
                    bossPos[0],
                    bossPos[1]);
            if (candidateMapIds.Count == 0)
                return -1;

            var matchesByMap =
                new Dictionary<int, List<(ActiveQuest Quest, GameWorld.DungeonQuestActorTarget Target)>>();
            foreach (var activeQuest in activeQuests)
            {
                if (activeQuest == null || activeQuest.TriggerValue == 0)
                    continue;

                foreach (var target in
                    GameWorld.QuestData.GetUnfinishedDungeonActorTargets(
                        activeQuest.QuestId,
                        activeQuest.TriggerValue,
                        dungeonId,
                        difficulty))
                {
                    foreach (var candidateMapId in candidateMapIds)
                    {
                        if (target.MapId > 0
                            && target.MapId != candidateMapId)
                        {
                            continue;
                        }

                        if (!GameWorld.DungeonMapResolver.MapContainsMonsterCode(
                                candidateMapId,
                                target.ActorCode))
                        {
                            continue;
                        }

                        if (!matchesByMap.TryGetValue(
                            candidateMapId,
                            out var matches))
                        {
                            matches =
                                new List<(ActiveQuest, GameWorld.DungeonQuestActorTarget)>();
                            matchesByMap[candidateMapId] = matches;
                        }
                        matches.Add((activeQuest, target));
                    }
                }
            }

            var bestScore = 0;
            var bestMapIds = new List<int>();
            foreach (var candidateMapId in candidateMapIds)
            {
                if (!matchesByMap.TryGetValue(
                        candidateMapId,
                        out var matches))
                {
                    continue;
                }

                if (matches.Count > bestScore)
                {
                    bestScore = matches.Count;
                    bestMapIds.Clear();
                    bestMapIds.Add(candidateMapId);
                }
                else if (matches.Count == bestScore)
                {
                    bestMapIds.Add(candidateMapId);
                }
            }

            if (bestMapIds.Count == 0)
                return -1;

            var selectedMapId = bestMapIds.Count == 1
                ? bestMapIds[0]
                : bestMapIds[ServerRandom.Next(bestMapIds.Count)];
            var sourceSummary = string.Join(",", matchesByMap[selectedMapId]
                .Select(match =>
                    $"{match.Quest.QuestId}:{match.Target.Source}:{match.Target.ActorCode}"));
            FileLogger.Log(
                $"[SpecialDungeonModule] quest-bound boss map: " +
                $"dungeon={dungeonId} map={selectedMapId} " +
                $"matches={matchesByMap[selectedMapId].Count} " +
                $"sources={sourceSummary}");
            return selectedMapId;
        }

        private static void AppendConditionActors(
            DungeonRun run,
            DungeonData.MazeSumInfo maze)
        {
            var codes = new List<int>();
            foreach (var target in run.BossEntranceConditionTargets)
            {
                if (target != null
                    && !target.Completed
                    && target.X == maze.X
                    && target.Y == maze.Y)
                {
                    codes.Add(target.MonsterCode);
                }
            }

            if (codes.Count == 0)
                return;

            var actors = DungeonData.GetMapMonsterConditionSummaryInformation(
                maze.Index,
                run.DungeonId,
                maze.X,
                maze.Y,
                codes);
            maze.Monsters.AddRange(actors);
            FileLogger.Log(
                $"[SpecialDungeonModule] condition actors added: " +
                $"dungeon={run.DungeonId} mechanism=boss-entrance-condition " +
                $"room=({maze.X},{maze.Y}) map={maze.Index} " +
                $"codes={string.Join(",", codes)} count={actors.Count}");
        }

        private static void AppendHiddenBossTemplates(
            DungeonRun run,
            DungeonData.MazeSumInfo maze)
        {
            var bossCodes = run.BossEntranceConditionalSummonCodes;
            if (bossCodes.Count == 0)
                return;

            var actors = DungeonData.GetMapConditionalSummonSummaryInformation(
                maze.Index,
                run.DungeonId,
                maze.X,
                maze.Y,
                bossCodes);
            var added = 0;
            foreach (var actor in actors)
            {
                if (maze.Monsters.Any(monster => monster.Code == actor.Code))
                    continue;

                var template = actor;
                template.Flag0 = 1;
                template.IsBlocking = false;
                maze.Monsters.Add(template);
                added++;
            }

            if (added > 0)
            {
                FileLogger.Log(
                    $"[SpecialDungeonModule] hidden boss templates added: " +
                    $"dungeon={run.DungeonId} mechanism=boss-entrance-condition " +
                    $"room=({maze.X},{maze.Y}) map={maze.Index} count={added}");
            }
        }

        private static List<BossEntranceConditionTargetState> BuildBossEntranceConditionTargets(
            int dungeonId,
            int mazeIndex,
            MazeInfo maze,
            int[] bossPos,
            IReadOnlyCollection<int> monsterCodes)
        {
            var targets = new List<BossEntranceConditionTargetState>();
            if (maze?.MapSpecifications == null
                || monsterCodes == null
                || monsterCodes.Count == 0)
                return targets;

            var candidates = new List<(byte X, byte Y, int MapId)>();
            foreach (var spec in maze.MapSpecifications)
            {
                if (!string.Equals(spec.Type, "map", StringComparison.OrdinalIgnoreCase)
                    || IsMazePoint(spec.X, spec.Y, maze.StartMap)
                    || IsMazePoint(spec.X, spec.Y, bossPos))
                {
                    continue;
                }

                if (DungeonData.GetMapMonsterConditionSummaryInformation(
                        spec.Index,
                        dungeonId,
                        spec.X,
                        spec.Y,
                        monsterCodes.ToList()).Count > 0)
                {
                    candidates.Add(((byte)spec.X, (byte)spec.Y, spec.Index));
                }
            }

            if (candidates.Count == 0)
                return targets;

            var available = new List<(byte X, byte Y, int MapId)>(candidates);
            var logParts = new List<string>();
            foreach (var monsterCode in monsterCodes)
            {
                if (available.Count == 0)
                    available.AddRange(candidates);

                var pick = ServerRandom.Next(available.Count);
                var point = available[pick];
                available.RemoveAt(pick);
                targets.Add(new BossEntranceConditionTargetState
                {
                    MonsterCode = monsterCode,
                    X = point.X,
                    Y = point.Y,
                });
                logParts.Add($"{monsterCode}@({point.X},{point.Y})#{point.MapId}");
            }

            FileLogger.Log(
                $"[SpecialDungeonModule] condition assignments: " +
                $"dungeon={dungeonId} maze={mazeIndex} " +
                $"assignments={string.Join(",", logParts)}");
            return targets;
        }

        private static IReadOnlyList<IReadOnlyList<(byte, byte)>>
            BuildGentTowerIconGroups(
                int dungeonId,
                int mazeIndex,
                MazeInfo maze,
                int[] bossPos,
                SpecialDungeonRuntime special)
        {
            if (maze?.MapSpecifications == null
                || special?.GentInfiltrateTowerRequired == null
                || special.GentInfiltrateTowerRequired.Count == 0)
            {
                return null;
            }

            var targetCodes = new HashSet<int>(
                special.GentInfiltrateTowerRequired.Keys);
            var points = new List<(byte, byte)>();
            var seen = new HashSet<int>();
            foreach (var spec in maze.MapSpecifications)
            {
                if (!string.Equals(spec.Type, "map", StringComparison.OrdinalIgnoreCase))
                    continue;

                var summary = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    dungeonId,
                    spec.X,
                    spec.Y,
                    mazeIndex,
                    spec.Index,
                    bossPos);
                if (!summary.Monsters.Any(monster => targetCodes.Contains(monster.Code)))
                    continue;

                var key = (spec.X << 16) ^ (spec.Y & 0xFFFF);
                if (seen.Add(key))
                    points.Add(((byte)spec.X, (byte)spec.Y));
            }

            return points.Count > 0
                ? new List<IReadOnlyList<(byte, byte)>> { points }
                : null;
        }

        private static IReadOnlyList<IReadOnlyList<(byte, byte)>>
            BuildMinimapIconGroupsFromTargets(
                IReadOnlyList<BossEntranceConditionTargetState> targets)
        {
            if (targets == null || targets.Count == 0)
                return null;

            var points = new List<(byte, byte)>();
            foreach (var target in targets)
            {
                if (target != null)
                    points.Add((target.X, target.Y));
            }

            return points.Count > 0
                ? new List<IReadOnlyList<(byte, byte)>> { points }
                : null;
        }

        private static IReadOnlyList<IReadOnlyList<(byte, byte)>>
            CloneMinimapIconGroups(
                IReadOnlyList<IReadOnlyList<(byte, byte)>> source)
        {
            if (source == null)
                return null;

            var result = new List<IReadOnlyList<(byte, byte)>>();
            foreach (var group in source)
                result.Add(group == null
                    ? Array.Empty<(byte, byte)>()
                    : new List<(byte, byte)>(group));
            return result;
        }

        private static List<BossEntranceConditionTargetState>
            CloneBossEntranceConditionTargets(
                IReadOnlyList<BossEntranceConditionTargetState> source)
        {
            var result = new List<BossEntranceConditionTargetState>();
            if (source == null)
                return result;

            foreach (var item in source)
            {
                if (item == null)
                    continue;

                result.Add(new BossEntranceConditionTargetState
                {
                    MonsterCode = item.MonsterCode,
                    X = item.X,
                    Y = item.Y,
                    Completed = item.Completed,
                });
            }
            return result;
        }

        private static bool IsMazePoint(int x, int y, int[] point)
            => point != null
                && point.Length >= 2
                && point[0] == x
                && point[1] == y;
    }
}
