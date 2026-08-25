using DfoServer.Game.Dungeon;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Dungeon;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class DungeonMapHandler
    {
        private const byte HellPartyHiddenTemplateFlag = 1;
        private const byte HellPartyAttachAllWavesSelector = 0xFF;

        // A21 START_MAP +2 is consumed as the room's layered-map state, not as
        // a generic "map override exists" flag. Hell-party and mechanism maps
        // may override the MAP id while remaining ordinary rooms; only an
        // active layered-map progression may select the client layer branch.
        internal static byte ResolveStartMapLayeredFlag(int layeredMapIndex)
            => layeredMapIndex >= 0 ? (byte)1 : (byte)0;

        private readonly DungeonSharedServices _svc;

        internal DungeonMapHandler(DungeonSharedServices svc) => _svc = svc;

        internal async Task HandleMoveMap(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var run = session.Player.CurrentRun;
            if (run == null) return;
            var leaderRunIdentity = run.CaptureIdentity();

            // 塔内分流: 在塔中时 MOVE_MAP = 推进下一层(不走普通地图切换)
            if (run.Tower != null)
            {
                if (await _svc.DeathTower.TryHandleMoveMap(session))
                    return;
                if (!session.Player.IsCurrentDungeonRun(leaderRunIdentity))
                    return;
            }
            var leaderPreviousRoomInstanceId = run.CurrentRoomInstanceId;

            if (!MoveMapRequest.TryParse(body, out var req))
            {
                FileLogger.Log(
                    $"[DungeonHandler] MOVE_MAP ignored truncated body: " +
                    $"length={body?.Length ?? 0} " +
                    $"minimum={MoveMapRequest.BodyLength}");
                return;
            }

            if (run.Phase >= DungeonRunPhase.Cleared)
            {
                FileLogger.Log($"[DungeonHandler] MOVE_MAP ignored after dungeon clear: current=({run.RoomKey.X},{run.RoomKey.Y}) next=({req.NextX},{req.NextY})");
                return;
            }

            if (_svc.BloodAltars.BlocksMapMove(run))
            {
                var altar = _svc.BloodAltars.GetRuntime(run);
                FileLogger.Log(
                    $"[BloodAltar] MOVE_MAP blocked before map completion: " +
                    $"cid={session.Player.CharacterId} " +
                    $"instance={run.PartyDungeonInstanceId} " +
                    $"room={run.CurrentRoomInstanceId} " +
                    $"map={altar?.CurrentMapId ?? 0}");
                return;
            }

            if (IsHellPartyLocked(run))
            {
                FileLogger.Log($"[DungeonHandler] MOVE_MAP blocked by active hell party: current=({run.RoomKey.X},{run.RoomKey.Y}) next=({req.NextX},{req.NextY})");
                return;
            }

            if (!DungeonRoomTopology.TryResolveMoveTarget(
                run.DungeonId,
                run.MazeIndex,
                run.RoomKey,
                req.NextX,
                req.NextY,
                run.BossMapPos,
                out var moveTarget,
                out var targetReason))
            {
                FileLogger.Log($"[DungeonHandler] MOVE_MAP blocked outside maze: current=({run.RoomKey.X},{run.RoomKey.Y}) requested=({req.NextX},{req.NextY}) dungeon={run.DungeonId} maze={run.MazeIndex}");
                return;
            }

            if (moveTarget.X != req.NextX || moveTarget.Y != req.NextY)
                FileLogger.Log($"[DungeonHandler] MOVE_MAP normalized: current=({run.RoomKey.X},{run.RoomKey.Y}) requested=({req.NextX},{req.NextY}) target=({moveTarget.X},{moveTarget.Y}) reason={targetReason}");

            var mechanismMove =
                DungeonMechanismCoordinator.ApplyMoveTargetOverride(
                    session,
                    run,
                    req.NextX,
                    req.NextY,
                    ref moveTarget);

            int overrideMapId = -1;

            if (req.MoveMode == 1)
            {
                var layeredIds = DungeonData.GetLayeredMapIds(run.DungeonId, moveTarget.X, moveTarget.Y, run.MazeIndex);
                if (layeredIds != null && layeredIds.Length > 0)
                {
                    var nextLayer = run.LayeredMapIndex + 1;
                    if (nextLayer < layeredIds.Length)
                    {
                        run.LayeredMapIndex = nextLayer;
                        overrideMapId = layeredIds[nextLayer];
                    }
                }
            }
            else
            {
                run.LayeredMapIndex = -1;
            }

            DungeonMechanismCoordinator.ApplyMapOverride(
                session,
                run,
                moveTarget,
                ref overrideMapId);
            var leaderRoomIdentity = await SendStartMapAsync(
                session,
                run,
                moveTarget.X,
                moveTarget.Y,
                overrideMapId);
            if (!leaderRoomIdentity.HasValue
                || !session.Player.IsCurrentDungeonParticipantRoom(
                    leaderRoomIdentity.Value))
                return;
            DungeonMechanismCoordinator.OnMoveMapCompleted(
                session,
                mechanismMove,
                "leader_START_MAP");

            // ★组队副本联机: 队长移动到下一房间时, 带同队队员一起换图(队员是follower、不自发MOVE_MAP)。
            await BroadcastMoveMapToPartyAsync(
                session,
                moveTarget.X,
                moveTarget.Y,
                overrideMapId,
                mechanismMove,
                leaderRunIdentity,
                leaderRoomIdentity.Value,
                leaderPreviousRoomInstanceId);
        }

        // 队长换图时把同队【在副本里】的成员也移到同一房间(服务端驱动, 队员副本=队长迷宫拷贝)。⚠️待真机验证。
        private async Task BroadcastMoveMapToPartyAsync(
            EnhancedClientSession leader,
            int nextX,
            int nextY,
            int overrideMapId,
            DungeonMechanismCoordinator.MoveMapContext mechanismMove,
            DungeonRunIdentity leaderRunIdentity,
            DungeonParticipantRoomIdentity leaderRoomIdentity,
            long leaderPreviousRoomInstanceId)
        {
            var pm = _svc.PartyManager;
            var sessions = _svc.Sessions;
            if (pm == null || sessions == null || leader?.Player == null) return;
            var leaderUid = (ushort)leader.Player.CharacterId;
            var party = pm.GetPartyByUser(leaderUid);
            if (party == null || party.Count <= 1 || !party.IsLeader(leaderUid)) return;   // 只有队长换图带全队

            foreach (var m in party.MembersBySlot())
            {
                if (m.UserId == leaderUid) continue;
                sessions.TryGet(m.CharacterId, out var bs);
                var memberRun = bs?.Player?.CurrentRun;
                if (memberRun == null
                    || bs.TcpClient == null
                    || !bs.TcpClient.Connected
                    || memberRun.PartyDungeonInstanceId != leaderRunIdentity.PartyDungeonInstanceId
                    || memberRun.CurrentRoomInstanceId != leaderPreviousRoomInstanceId
                    || memberRun.RunState != DungeonRunState.Active)
                {
                    continue;
                }
                try
                {
                    if (!leader.Player.IsCurrentDungeonParticipantRoom(
                            leaderRoomIdentity))
                        return;
                    var leaderRun = leader.Player.CurrentRun;
                    if (leaderRun == null)
                        return;
                    memberRun.LayeredMapIndex = leaderRun.LayeredMapIndex;
                    DungeonMechanismCoordinator.CopyMoveStateForParty(
                        leaderRun,
                        memberRun);
                    var memberRoomIdentity = await SendStartMapAsync(
                        bs,
                        memberRun,
                        nextX,
                        nextY,
                        overrideMapId);
                    if (!leader.Player.IsCurrentDungeonParticipantRoom(
                            leaderRoomIdentity))
                        return;
                    if (!memberRoomIdentity.HasValue
                        || !bs.Player.IsCurrentDungeonParticipantRoom(
                            memberRoomIdentity.Value))
                        continue;
                    DungeonMechanismCoordinator.OnMoveMapCompleted(
                        bs,
                        mechanismMove,
                        $"party_START_MAP leader={leader.Player.CharacterId}");
                    FileLogger.Log($"[DungeonHandler] PARTY_MOVE_MAP: 带队员 cid={bs.Player.CharacterId} 到 ({nextX},{nextY})");
                }
                catch (System.Exception ex)
                {
                    FileLogger.Log($"[DungeonHandler] PARTY_MOVE_MAP ERROR: member uid={m.UserId}: {ex.Message}");
                }
            }
        }

        internal Task<DungeonParticipantRoomIdentity?> SendStartMapAsync(
            EnhancedClientSession session,
            int nextX,
            int nextY,
            int overrideMapId)
            => SendStartMapAsync(
                session,
                session?.Player?.CurrentRun,
                nextX,
                nextY,
                overrideMapId);

        internal async Task<DungeonParticipantRoomIdentity?> SendStartMapAsync(
            EnhancedClientSession session,
            DungeonRun run,
            int nextX,
            int nextY,
            int overrideMapId)
        {
            if (session?.Player == null
                || run == null
                || run.Instance.State == DungeonInstanceState.Ending
                || run.Instance.State == DungeonInstanceState.Ended
                || !session.Player.IsCurrentDungeonRun(run.CaptureIdentity()))
            {
                return null;
            }
            var runIdentity = run.CaptureIdentity();

            var effectiveOverrideMapId =
                DungeonMechanismCoordinator.ResolveStartMapOverride(
                    run,
                    nextX,
                    nextY,
                    overrideMapId);
            var templateMapId = effectiveOverrideMapId;
            if (templateMapId <= 0
                && run.Instance.Selection?.TryGetFrozenRoomMapId(
                    nextX,
                    nextY,
                    out var frozenMapId) == true)
            {
                templateMapId = frozenMapId;
            }
            var maze = DungeonData.GetDungeonMapMonsterSummaryInformation(
                run.DungeonId,
                nextX,
                nextY,
                run.MazeIndex,
                templateMapId,
                run.BossMapPos);
            if (overrideMapId <= 0
                && run.HellMode
                && run.HellMapId > 0
                && maze.X == run.HellMapX
                && maze.Y == run.HellMapY)
            {
                var hellMapId = run.HellMapId;
                effectiveOverrideMapId = hellMapId;
                if (hellMapId != maze.Index)
                    maze = DungeonData.GetDungeonMapMonsterSummaryInformation(run.DungeonId, maze.X, maze.Y, run.MazeIndex, hellMapId, run.BossMapPos);
                FileLogger.Log($"[DungeonHandler] START_MAP hell override: room=({maze.X},{maze.Y}) map={maze.Index}");
            }

            var isTournamentMap = _svc.Tournaments.TryProjectStartMap(
                run,
                maze,
                out var tournamentMaze);
            if (isTournamentMap)
                maze = tournamentMaze;
            var isBloodAltarMap = _svc.BloodAltars.IsBloodAltar(run);

            var roomKey = new RoomKey(maze.X, maze.Y, effectiveOverrideMapId);

            byte[] startMapBody;
            List<KeyValuePair<int, int>> hellPartyMonsterInfoAfterStartMap = null;
            var isFirstRunStartMap = false;
            var sentMapId = maze.Index;
            var sentMapX = maze.X;
            var sentMapY = maze.Y;
            var sentActorCount = 0;
            var sentTrackedCount = 0;
            DungeonInstanceRoom pendingStandardRoom = null;
            DungeonData.MazeSumInfo pendingStandardMaze = default;
            ushort pendingFirstActorSequence = 0;
            byte pendingLayeredFlag = 0;
            byte pendingHellPartyMode = 0;
            byte pendingHellPartyFogFlag = 0;
            IReadOnlyList<RidableObjectSpawnEntry> pendingRidableEntries = null;

            // 锁内绝不 await: 把 START_MAP 对 run 房间态(RoomKey/RoomStates/RoomKilledSeqIds/RoomMonsters/
            // MonsterCount)的整段读改写与队友击杀 relay(PropagateKillForClearAsync 在别的线程读这些结构)互斥,
            // 防 Dict/HashSet 跨线程并发改崩。此块 138-241 全为同步逻辑, 所有 await 发包都在 lock 之外。
            lock (run.SyncRoot)
            {
            isFirstRunStartMap = run.RoomStates.Count == 0;
            run.RoomKey = roomKey;
            if (run.RoomStates.TryGetValue(roomKey, out var cached))
            {
                if (run.Tower == null && cached.InstanceRoom != null)
                {
                    cached.InstanceRoom.CopyKilledActorSequenceIdsTo(
                        cached.KilledSeqIds,
                        death => DungeonRoomTopology.IsTrackedForRoomProgress(
                            death.ActorType));
                }
                run.RoomMonsters = cached.Maze.Monsters;
                run.RoomStartSequence = cached.FirstSeqId;
                run.RoomKilledSeqIds = cached.KilledSeqIds;
                run.RoomLcg = cached.Lcg;
                run.Seed = cached.Seed;
                run.RoomKey = roomKey;
                if (isTournamentMap
                    && !_svc.Tournaments.TryBindFirstActorSequence(
                        run,
                        cached.FirstSeqId))
                {
                    throw new InvalidOperationException(
                        "Tournament actor sequence changed on room revisit.");
                }
                if (cached.InstanceRoom != null)
                    run.SetCurrentRoom(cached.InstanceRoom);
                DungeonMechanismCoordinator.RestoreRoomState(run, cached);

                startMapBody = isBloodAltarMap
                    ? Array.Empty<byte>()
                    : DungeonNotificationBuilder.BuildStartMapRevisit(
                        cached.Maze,
                        cached.Seed);
                sentMapId = cached.Maze.Index;
                sentMapX = cached.Maze.X;
                sentMapY = cached.Maze.Y;
                sentActorCount = cached.Maze.Monsters?.Count ?? 0;
                sentTrackedCount = cached.MonsterCount;
                FileLogger.Log($"[DungeonHandler] START_MAP revisit: room=({maze.X},{maze.Y}) killed={cached.KilledSeqIds.Count}/{cached.MonsterCount} cleared={cached.IsCleared}");
            }
            else
            {
                var killedSet = new HashSet<ushort>();
                run.RoomKilledSeqIds = killedSet;

                var hellRoomInfo = run.HellRoomInfo;
                var isHellPartyRoom = run.HellMode
                    && hellRoomInfo != null
                    && effectiveOverrideMapId == hellRoomInfo.MapId
                    && maze.X == hellRoomInfo.X
                    && maze.Y == hellRoomInfo.Y;

                var instanceRoom = run.Instance.GetOrCreateRoom(
                    roomKey,
                    roomInstanceId =>
                    {
                        var template = isBloodAltarMap
                            ? BuildBloodAltarStartMapMaze(maze)
                            : isHellPartyRoom
                                ? BuildHellPartyStartMapMaze(
                                    session,
                                    run,
                                    maze,
                                    hellRoomInfo)
                                : maze;
                        if (!isBloodAltarMap
                            && !isHellPartyRoom
                            && !isTournamentMap)
                        {
                            var removedNamedMonsters = NamedMonsterRoomFilter.Apply(
                                run.Instance,
                                DungeonData.GetDungeonFile(run.DungeonId),
                                ref template);
                            if (removedNamedMonsters > 0)
                            {
                                FileLogger.Log(
                                    $"[DungeonHandler] NAMED_MONSTER_MAP_FILTER: " +
                                    $"dungeon={run.DungeonId} room=({template.X},{template.Y}) " +
                                    $"map={template.Index} removed={removedNamedMonsters}");
                            }
                            ApplyChampionPromotion(run, template.Monsters);
                            DungeonMechanismCoordinator.AppendStartMapActors(
                                session,
                                run,
                                template);
                        }

                        var roomSeed = (uint)(ServerRandom.Next() & ~0x40000);
                        // 旧服 ConsistMap 对每个新房使用 get_rand_int(60000)
                        // 作为 actor 运行序号起点。随机起点也避免未死亡机制 actor
                        // 在快速退本重进时与客户端残留 identity 冲突。
                        var firstActorSequenceId = (ushort)ServerRandom.Next(1, 60001);
                        return new DungeonInstanceRoom(
                            roomInstanceId,
                            roomKey,
                            template,
                            roomSeed,
                            firstActorSequenceId);
                    },
                    out var instanceRoomCreated);
                var startMapMaze = instanceRoom.Maze;
                if (isTournamentMap
                    && !_svc.Tournaments.TryBindFirstActorSequence(
                        run,
                        instanceRoom.FirstActorSequenceId))
                {
                    throw new InvalidOperationException(
                        "Tournament actor sequence could not be bound.");
                }
                if (run.Tower == null)
                {
                    instanceRoom.CopyKilledActorSequenceIdsTo(
                        killedSet,
                        death => DungeonRoomTopology.IsTrackedForRoomProgress(
                            death.ActorType));
                }
                var seed = instanceRoom.Seed;
                run.RoomStartSequence = instanceRoom.FirstActorSequenceId;
                run.Seed = seed;
                var lcg = new DnfLcg(seed);
                run.RoomLcg = lcg;

                run.RoomMonsters = startMapMaze.Monsters;

                var roomState = new RoomState
                {
                    InstanceRoom = instanceRoom,
                    Maze = startMapMaze,
                    FirstSeqId = run.RoomStartSequence,
                    MonsterCount = (ushort)CountServerTrackedMonsters(startMapMaze),
                    KilledSeqIds = killedSet,
                    Seed = seed,
                    Lcg = lcg,
                };
                roomState.TryActivate();
                if (instanceRoom.State == DungeonRoomState.Cleared)
                    roomState.TryClear();
                run.RoomStates[roomKey] = roomState;
                run.SetCurrentRoom(instanceRoom);
                DungeonEncounterApplicationService.Apply(
                    run,
                    new DungeonEncounterDirective(
                        DungeonEventEnvelope.Create(
                            run,
                            session.Player.CharacterId,
                            "start_map encounter"),
                        DungeonEncounterDirectiveKind.Start));
                if (!isBloodAltarMap)
                {
                    DungeonMechanismCoordinator.OnRoomStateCreated(
                        session,
                        run,
                        roomState);
                }
                FileLogger.Log(
                    $"[DungeonHandler] ROOM_INSTANCE: instance={run.PartyDungeonInstanceId} " +
                    $"room={instanceRoom.RoomInstanceId} created={instanceRoomCreated} " +
                    $"key=({roomKey.X},{roomKey.Y},{roomKey.OverrideMapId}) seed={seed} " +
                    $"firstActorSeq={run.RoomStartSequence} " +
                    $"actors={FormatStartMapActorSummary(startMapMaze, run.RoomStartSequence)}");

                var layeredFlag = ResolveStartMapLayeredFlag(run.LayeredMapIndex);

                if (isHellPartyRoom)
                {
                    var state = run.RoomStates[roomKey];
                    state.IsHellPartyRoom = true;
                    state.HellPartyVeryDifficult = run.VeryDifficultHell;
                    state.HellPartyPillarObjectCode = hellRoomInfo.PillarObjectCode;
                    state.HellPartySpawnX = hellRoomInfo.SpawnX;
                    state.HellPartySpawnY = hellRoomInfo.SpawnY;
                    state.HellPartyWaves = hellRoomInfo.Waves;
                    state.HellPartyPhase = HellPartyPhase.WaitingStart;
                    state.HellPartyGroupRemaining = BuildHellPartyGroupRemaining(startMapMaze.Monsters);
                    var difficultyRule = hellRoomInfo.DifficultyRule;
                    FileLogger.Log($"[DungeonHandler] HELLPARTY room initialized: pillar={state.HellPartyPillarObjectCode} spawn=({state.HellPartySpawnX},{state.HellPartySpawnY}) waves={state.HellPartyWaves?.Count ?? 0} tracked={state.MonsterCount}/{startMapMaze.Monsters.Count} rewardRolls={difficultyRule?.RewardRollCount ?? 0} probability={difficultyRule?.Probability ?? 0} ratioProbability={difficultyRule?.RatioProbability ?? 0} groups={FormatHellPartyGroups(state.HellPartyGroupRemaining)}");
                    hellPartyMonsterInfoAfterStartMap = BuildHellPartyMonsterInfoEntries(hellRoomInfo);
                }

                var ridableForRoom = isBloodAltarMap
                    ? null
                    : GetRidableEntriesForRoom(
                        run,
                        maze.X,
                        maze.Y);
                // A21 START_MAP offset 7 is the standard-dungeon mode marker.
                // Normal rooms use 2 even when abyss mode is disabled; zero is
                // not the A21 standard value and moves the minimap projection.
                var hellPartyMapMode = run.HellMode ? run.HellPartyMode : (byte)2;
                var startMapFogFlag = run.HellMode ? (byte)1 : (byte)0;

                startMapBody = Array.Empty<byte>();
                if (!isBloodAltarMap && !isTournamentMap)
                {
                    pendingStandardRoom = instanceRoom;
                    pendingStandardMaze = startMapMaze;
                    pendingFirstActorSequence = run.RoomStartSequence;
                    pendingLayeredFlag = layeredFlag;
                    pendingHellPartyMode = hellPartyMapMode;
                    pendingHellPartyFogFlag = startMapFogFlag;
                    pendingRidableEntries = ridableForRoom;
                }
                sentMapId = startMapMaze.Index;
                sentMapX = startMapMaze.X;
                sentMapY = startMapMaze.Y;
                sentActorCount = startMapMaze.Monsters?.Count ?? 0;
                sentTrackedCount = roomState.MonsterCount;
                run.MonsterCount += (ushort)startMapMaze.Monsters.Count;
            }
            } // end lock(run.SyncRoot)

            if (pendingStandardRoom != null)
            {
                var passiveObjectDrops = ProjectPassiveObjectDrops(
                    run,
                    pendingStandardRoom);
                if (passiveObjectDrops.StaleRoom)
                    return null;

                startMapBody = DungeonNotificationBuilder.BuildStartMap(
                    pendingStandardMaze,
                    pendingFirstActorSequence,
                    unchecked((int)pendingStandardRoom.Seed),
                    layeredRoomFlag: pendingLayeredFlag,
                    hellPartyMode: pendingHellPartyMode,
                    hellPartyFogFlag: pendingHellPartyFogFlag,
                    extraEntries: passiveObjectDrops.Entries,
                    ridableEntries: pendingRidableEntries);
            }

            CacheResolvedStartMapId(run, sentMapX, sentMapY, sentMapId);

            var roomIdentity = run.CaptureParticipantRoomIdentity();
            if (isBloodAltarMap)
            {
                var failureReason = string.Empty;
                if (sentMapId <= 0
                    || sentMapId > ushort.MaxValue
                    || !_svc.BloodAltars.TryBindMap(
                        run,
                        sentMapId,
                        roomIdentity,
                        out _,
                        out failureReason))
                {
                    if (string.IsNullOrEmpty(failureReason))
                        failureReason = "blood altar map id is out of range";
                    FileLogger.Log(
                        $"[BloodAltar] START_BLOOD_MAP rejected: " +
                        $"cid={session.Player.CharacterId} map={sentMapId} " +
                        $"reason={failureReason}");
                    return null;
                }

                startMapBody = BloodAltarPacketBuilder.BuildStartMap(
                    (byte)Math.Max(0, Math.Min(byte.MaxValue, sentMapX)),
                    (byte)Math.Max(0, Math.Min(byte.MaxValue, sentMapY)),
                    run.Seed,
                    (uint)sentMapId);
            }
            var tournament = run.Instance.Mechanisms.Tournament;
            var sendTournamentProjection = tournament != null
                && sentMapId == tournament.Definition.MapId;
            if (tournament != null && !sendTournamentProjection)
            {
                FileLogger.Log(
                    $"[Tournament] special projection rejected: " +
                    $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                    $"expectedMap={tournament.Definition.MapId} actualMap={sentMapId}");
                return null;
            }
            if (sendTournamentProjection)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.TOURNAMENT_INFO,
                    TournamentPacketBuilder.BuildTournamentInfo(
                        tournament,
                        run.Difficulty,
                        run.RoomStartSequence)));
                if (!session.Player.IsCurrentDungeonParticipantRoom(roomIdentity))
                    return null;
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.TOURNAMENT_MAP_INFO,
                    TournamentPacketBuilder.BuildTournamentMapInfo(
                        (byte)sentMapX,
                        (byte)sentMapY,
                        run.Seed,
                        (uint)tournament.Definition.MapId,
                        revisit: !isFirstRunStartMap)));
                FileLogger.Log(
                    $"[Tournament] START_MAP projection sent: " +
                    $"cid={session.Player.CharacterId} " +
                    $"dungeon={run.DungeonId} map={sentMapId} " +
                    $"firstSeq={run.RoomStartSequence} " +
                    $"revisit={!isFirstRunStartMap}");
            }
            else
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    isBloodAltarMap
                        ? (ushort)NotiPacketTypeA21.START_BLOOD_MAP
                        : (ushort)NotiPacketTypeA21.START_MAP,
                    startMapBody));
            }
            if (!session.Player.IsCurrentDungeonRun(runIdentity)
                || !session.Player.IsCurrentDungeonParticipantRoom(roomIdentity))
            {
                return null;
            }
            if (TowerOfDespairApcInfoBuilder.TryBuild(
                run.DungeonId,
                session.Player,
                out var towerBaseApcInfoBody,
                out var towerCurrentApcInfoBody))
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketType.USER_APC_INFO_TOD,
                    towerBaseApcInfoBody));
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketType.USER_APC_INFO_TOD,
                    towerCurrentApcInfoBody));
                FileLogger.Log(
                    $"[TowerOfDespair] base/current APC info sent after START_MAP: " +
                    $"dungeon={run.DungeonId} layers=0,{towerCurrentApcInfoBody[0]} " +
                    $"job={session.Player.Job} grow={session.Player.GrowType}");
            }
            if (!session.Player.IsCurrentDungeonRun(runIdentity)
                || !session.Player.IsCurrentDungeonParticipantRoom(roomIdentity))
            {
                return null;
            }
            if (isFirstRunStartMap)
            {
                FileLogger.Log(
                    $"[DungeonHandler] START_MAP first sent: " +
                    $"cid={session.Player.CharacterId} dungeon={run.DungeonId} maze={run.MazeIndex} " +
                    $"requested=({nextX},{nextY}) resolved=({sentMapX},{sentMapY}) map={sentMapId} " +
                    $"override={effectiveOverrideMapId} selectedStart=({run.MazeStartX},{run.MazeStartY}) " +
                        $"selectedStartMap={run.MazeStartMapId} actors={sentActorCount} tracked={sentTrackedCount}");
            }
            if (!isBloodAltarMap)
            {
                await DungeonMechanismCoordinator.OnStartMapSentAsync(
                    session,
                    roomIdentity);
            }
            if (!session.Player.IsCurrentDungeonRun(runIdentity)
                || !session.Player.IsCurrentDungeonParticipantRoom(roomIdentity))
            {
                return null;
            }

            if (hellPartyMonsterInfoAfterStartMap != null && hellPartyMonsterInfoAfterStartMap.Count > 0)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.HELL_PARTY_MONSTER_INFO,
                    DungeonNotificationBuilder.BuildHellPartyMonsterInfo(hellPartyMonsterInfoAfterStartMap)));
                if (!session.Player.IsCurrentDungeonParticipantRoom(roomIdentity))
                    return null;
                FileLogger.Log($"[DungeonHandler] HELLPARTY monster info sent after hell START_MAP: entries={hellPartyMonsterInfoAfterStartMap.Count} actorLevels={string.Join(",", hellPartyMonsterInfoAfterStartMap.Select(x => $"{x.Key}:{x.Value}"))}");
            }

            return roomIdentity;
        }

        private static DungeonData.MazeSumInfo BuildBloodAltarStartMapMaze(
            DungeonData.MazeSumInfo maze)
            => new DungeonData.MazeSumInfo
            {
                Index = maze.Index,
                X = maze.X,
                Y = maze.Y,
                Monsters = new List<DungeonData.MonsterSumInfo>(),
                EventMonsterPositions =
                    Array.Empty<EventMonsterPositionInfo>(),
                SpecialPassiveObjects =
                    Array.Empty<SpecialPassiveObjectInfo>(),
            };

        private static string FormatStartMapActorSummary(
            DungeonData.MazeSumInfo maze,
            ushort firstSequenceId)
        {
            var actors = maze.Monsters;
            if (actors == null || actors.Count == 0)
                return "[]";

            const int maxEntries = 8;
            var count = Math.Min(actors.Count, maxEntries);
            var entries = new string[count];
            var normalIndex = 0;
            var apcIndex = 0;
            for (var i = 0; i < count; i++)
            {
                var actor = actors[i];
                var isApc = actor.Type >= 5;
                var packetIndex = actor.PacketIndex
                    ?? (isApc ? apcIndex++ : normalIndex++);
                entries[i] =
                    $"{firstSequenceId + i}:{actor.Code}/t{actor.Type}/o{actor.TemplateOrder}" +
                    $"/i{packetIndex}/f{actor.Flag0},{actor.Flag1}/x{actor.ExtraState}";
            }

            var suffix = actors.Count > maxEntries
                ? $",...+{actors.Count - maxEntries}"
                : string.Empty;
            return $"[{string.Join(",", entries)}{suffix}]";
        }

        private static void CacheResolvedStartMapId(
            DungeonRun run,
            int mapX,
            int mapY,
            int mapId)
        {
            if (run == null)
                return;
            if (mapX != run.MazeStartX
                || mapY != run.MazeStartY
                || mapId <= 0)
            {
                return;
            }

            run.MazeStartMapId = mapId;
        }

        private static List<KeyValuePair<int, int>> BuildHellPartyMonsterInfoEntries(DungeonData.HellPartyRoomInfo hellRoomInfo)
        {
            var seen = new HashSet<int>();
            var result = new List<KeyValuePair<int, int>>();
            if (hellRoomInfo?.Waves != null && hellRoomInfo.Waves.Count > 0)
            {
                foreach (var wave in hellRoomInfo.Waves)
                {
                    if (wave?.Monsters == null)
                        continue;

                    foreach (var monster in wave.Monsters)
                    {
                        if (monster.Code <= 0 || seen.Contains(monster.Code))
                            continue;

                        seen.Add(monster.Code);
                        result.Add(new KeyValuePair<int, int>(monster.Code, Math.Max(1, (int)monster.Level)));
                    }
                }
            }

            return result;
        }

        private static DungeonData.MazeSumInfo BuildHellPartyStartMapMaze(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonData.MazeSumInfo maze,
            DungeonData.HellPartyRoomInfo hellRoomInfo)
        {
            if (hellRoomInfo == null || hellRoomInfo.NormalMapId <= 0)
                return maze;

            if (run == null)
                return maze;

            try
            {
                var normalMaze = DungeonData.GetDungeonMapMonsterSummaryInformation(
                    run.DungeonId,
                    hellRoomInfo.X,
                    hellRoomInfo.Y,
                    run.MazeIndex,
                    hellRoomInfo.NormalMapId,
                    run.BossMapPos);

                var monsters = new List<DungeonData.MonsterSumInfo>(
                    normalMaze.Monsters ?? new List<DungeonData.MonsterSumInfo>());
                ApplyChampionPromotion(run, monsters);
                var normalCount = monsters.Count;
                var hiddenCount = AppendHellPartyTemplateRows(monsters, hellRoomInfo);

                FileLogger.Log($"[DungeonHandler] HELLPARTY using normal room monsters: hellMap={maze.Index} normalMap={hellRoomInfo.NormalMapId} normal={normalCount} hidden={hiddenCount}");
                return new DungeonData.MazeSumInfo
                {
                    X = maze.X,
                    Y = maze.Y,
                    Index = maze.Index,
                    Monsters = monsters,
                };
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DungeonHandler] HELLPARTY normal room monster fallback: normalMap={hellRoomInfo.NormalMapId} error={ex.Message}");
            }

            return maze;
        }

        private static void ApplyChampionPromotion(
            DungeonRun run,
            List<DungeonData.MonsterSumInfo> monsters)
        {
            if (monsters == null || monsters.Count == 0)
                return;

            if (run == null)
                return;

            var champCount = DungeonData.GetChampionCount(
                run.DungeonId,
                run.Difficulty,
                run.MazeIndex,
                out var namedMonsters);
            DungeonData.PromoteChampions(
                monsters,
                champCount,
                namedMonsters,
                run.DungeonId);
        }

        private static int AppendHellPartyTemplateRows(List<DungeonData.MonsterSumInfo> monsters, DungeonData.HellPartyRoomInfo hellRoomInfo)
        {
            if (monsters == null || hellRoomInfo?.Waves == null)
                return 0;

            var hiddenCount = 0;
            foreach (var wave in hellRoomInfo.Waves)
            {
                if (wave?.Monsters == null || wave.Monsters.Count == 0)
                    continue;

                var order = wave.Order > 0 && wave.Order <= ushort.MaxValue
                    ? (ushort)wave.Order
                    : (ushort)0;
                var waveIndex = HellPartyAttachAllWavesSelector;

                foreach (var monster in wave.Monsters)
                {
                    var template = monster;
                    template.TemplateOrder = order;
                    template.PacketIndex = null;
                    template.Flag0 = HellPartyHiddenTemplateFlag;
                    template.Flag1 = waveIndex;
                    template.ExtraState = 0;
                    monsters.Add(template);
                    hiddenCount++;
                }

                FileLogger.Log($"[DungeonHandler] HELLPARTY template wave: order={order} index={waveIndex} group={wave.GroupId} count={wave.Monsters.Count} rows={string.Join(",", wave.Monsters.Select(x => $"{x.Code}:{x.Type}:{x.Level}:{waveIndex}"))}");
            }

            return hiddenCount;
        }

        private static Dictionary<int, int> BuildHellPartyGroupRemaining(IReadOnlyList<DungeonData.MonsterSumInfo> monsters)
        {
            var result = new Dictionary<int, int>();
            if (monsters == null)
                return result;

            for (var i = 0; i < monsters.Count; i++)
            {
                var monster = monsters[i];
                if (!monster.IsHellPartyActor || monster.HellPartyGroupId <= 0)
                    continue;

                result.TryGetValue(monster.HellPartyGroupId, out var count);
                result[monster.HellPartyGroupId] = count + 1;
            }
            return result;
        }

        private static string FormatHellPartyGroups(Dictionary<int, int> groups)
        {
            if (groups == null || groups.Count == 0)
                return "-";

            return string.Join(",", groups.Select(x => $"{x.Key}={x.Value}"));
        }

        internal static int CountServerTrackedMonsters(DungeonData.MazeSumInfo maze)
        {
            if (maze.Monsters == null)
                return 0;

            return maze.Monsters.Count(monster =>
                DungeonRoomTopology.IsTrackedForRoomProgress(monster.Type));
        }

        private static bool TryGetCurrentRoomState(EnhancedClientSession session, out RoomState roomState)
        {
            var run = session.Player.CurrentRun;
            if (run == null)
            {
                roomState = null;
                return false;
            }

            return run.RoomStates.TryGetValue(run.RoomKey, out roomState);
        }

        internal static bool IsCurrentHellPartyLocked(EnhancedClientSession session)
            => IsHellPartyLocked(session?.Player?.CurrentRun);

        private static bool IsHellPartyLocked(DungeonRun run)
        {
            if (run == null
                || !run.RoomStates.TryGetValue(run.RoomKey, out var roomState)
                || roomState == null
                || !roomState.IsHellPartyRoom)
            {
                return false;
            }

            return roomState.HellPartyPhase == HellPartyPhase.Started && !roomState.IsCleared;
        }

        internal Task HandleHellPartyStart(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!TryGetCurrentRoomState(session, out var roomState) || !roomState.IsHellPartyRoom)
            {
                FileLogger.Log($"[DungeonHandler] HELLPARTY_START ignored: not in hell room cmd=0x{header.type:X4} bodyLen={body?.Length ?? 0}");
                return Task.CompletedTask;
            }

            if (roomState.HellPartyPhase == HellPartyPhase.WaitingStart)
            {
                roomState.HellPartyPhase = HellPartyPhase.Started;
                FileLogger.Log($"[DungeonHandler] HELLPARTY_START: room=({roomState.Maze.X},{roomState.Maze.Y}) tracked={roomState.MonsterCount}/{roomState.Maze.Monsters.Count}");
            }
            else
            {
                FileLogger.Log($"[DungeonHandler] HELLPARTY_START ignored: phase={roomState.HellPartyPhase}");
            }

            return Task.CompletedTask;
        }

        private static List<RidableObjectSpawnEntry> GetRidableEntriesForRoom(
            DungeonRun run,
            int roomX,
            int roomY)
        {
            var all = run?.RidableObjects;
            if (all == null || all.Count == 0) return null;
            var result = new List<RidableObjectSpawnEntry>();
            foreach (var r in all)
            {
                if (r.MapX == roomX && r.MapY == roomY)
                    result.Add(r);
            }
            return result.Count > 0 ? result : null;
        }

        private static PassiveObjectDropProjectionResult ProjectPassiveObjectDrops(
            DungeonRun run,
            DungeonInstanceRoom room)
        {
            if (run == null
                || room == null
                || !run.RewardPolicy.AllowsMonsterDrops)
            {
                return PassiveObjectDropProjectionResult.Empty;
            }

            try
            {
                var dgn = DungeonData.GetDungeonFile(run.DungeonId);
                if (dgn == null
                    || !dgn.SpecialPassiveObjectItemDefinitionPresent
                    || dgn.SpecialPassiveObjectItemDefinitionMalformed
                    || dgn.SpecialPassiveObjectItemGroups.Count == 0
                    || room.Maze.SpecialPassiveObjects == null
                    || room.Maze.SpecialPassiveObjects.Count == 0)
                {
                    if (dgn?.SpecialPassiveObjectItemDefinitionMalformed == true)
                    {
                        FileLogger.Log(
                            $"[DungeonHandler] PASSIVE_OBJ_DROP disabled malformed " +
                            $"dungeon={run.DungeonId} room={room.RoomInstanceId}");
                    }
                    return PassiveObjectDropProjectionResult.Empty;
                }

                var plan = room.GetOrCreatePassiveObjectDropPlan(
                    () => PassiveObjectDropPlanningService.Default.Plan(
                        dgn.SpecialPassiveObjectItemGroups,
                        room.Maze.SpecialPassiveObjects,
                        DungeonData.GetDungeonBasicLv(run.DungeonId),
                        run.Difficulty,
                        new DnfLcg(room.Seed)));
                var result = PassiveObjectDropProjectionService.ProjectAndRegister(
                    run,
                    room,
                    plan);

                if (plan.Intents.Count > 0
                    || plan.InvalidActionCount > 0
                    || plan.UnsupportedRandomCategoryCount > 0
                    || plan.WasTruncated
                    || result.InvalidIntentCount > 0
                    || result.StaleRoom
                    || result.SceneSlotsExhausted)
                {
                    FileLogger.Log(
                        $"[DungeonHandler] PASSIVE_OBJ_DROP: " +
                        $"dungeon={run.DungeonId} room={room.RoomInstanceId} " +
                        $"planned={plan.Intents.Count} projected={result.Entries.Count} " +
                        $"specific={plan.SpecificDropCount} random={plan.RandomDropCount} " +
                        $"invalidAction={plan.InvalidActionCount} " +
                        $"unsupportedRandom={plan.UnsupportedRandomCategoryCount} " +
                        $"invalidIntent={result.InvalidIntentCount} " +
                        $"truncated={plan.WasTruncated} stale={result.StaleRoom} " +
                        $"slotsExhausted={result.SceneSlotsExhausted}");
                }
                return result;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DungeonHandler] PASSIVE_OBJ_DROP failed closed: " +
                    $"dungeon={run.DungeonId} room={room.RoomInstanceId} " +
                    $"error={ex.Message}");
                return PassiveObjectDropProjectionResult.Empty;
            }
        }
    }
}
