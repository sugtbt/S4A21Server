using DfoServer.Game.Dungeon;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using PvfLib;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    // Advances PVF event-monster candidate rooms after START_MAP has created the
    // client-side scene containers. Candidate detection is configuration-driven;
    // COMPLETE_CONDITION_PASS_GATE activates the event actor already in START_MAP.
    internal static class EventMonsterConditionCoordinator
    {
        internal readonly struct CandidateRoomDescriptor
        {
            internal CandidateRoomDescriptor(
                int mapId,
                int targetCode,
                int targetX,
                int targetY,
                int specialObjectCount,
                int eventPositionCount)
            {
                MapId = mapId;
                TargetCode = targetCode;
                TargetX = targetX;
                TargetY = targetY;
                SpecialObjectCount = specialObjectCount;
                EventPositionCount = eventPositionCount;
            }

            internal int MapId { get; }
            internal int TargetCode { get; }
            internal int TargetX { get; }
            internal int TargetY { get; }
            internal int SpecialObjectCount { get; }
            internal int EventPositionCount { get; }
        }

        internal static async Task AdvanceAfterStartMapAsync(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonParticipantRoomIdentity roomIdentity,
            Func<byte[], Func<bool>, Action, Task<bool>>
                trySendPacketAsync,
            Func<bool> canProject)
        {
            if (run == null
                || session?.Player == null
                || !run.Matches(roomIdentity)
                || !session.Player.IsCurrentDungeonParticipantRoom(roomIdentity))
                return;

            RoomKey roomKey;
            DungeonData.MazeSumInfo room;
            RoomState capturedRoomState;
            lock (run.SyncRoot)
            {
                roomKey = run.RoomKey;
                if (!run.RoomStates.TryGetValue(
                        roomKey,
                        out capturedRoomState)
                    || capturedRoomState == null
                    || capturedRoomState.EventMonsterConditionAdvanced)
                {
                    return;
                }

                room = capturedRoomState.Maze;
            }

            if (!TryDescribeCandidateRoom(run, room, out var descriptor))
                return;

            lock (run.SyncRoot)
            {
                if (!run.RoomKey.Equals(roomKey)
                    || !run.RoomStates.TryGetValue(
                        roomKey,
                        out var currentRoomState)
                    || !ReferenceEquals(
                        currentRoomState,
                        capturedRoomState)
                    || capturedRoomState.EventMonsterConditionAdvanced
                    || (canProject != null && !canProject()))
                {
                    return;
                }
            }

            if (!session.Player.IsCurrentDungeonParticipantRoom(roomIdentity))
                return;

            var sent = await DungeonMechanismNotificationSender
                .SendCompleteConditionPassGateAsync(
                    session,
                    "event-monster-condition",
                    "start-map-ready",
                    (packet, packetCanSend, onSent) =>
                        trySendPacketAsync(
                            packet,
                            () =>
                            {
                                if (packetCanSend != null
                                    && !packetCanSend())
                                {
                                    return false;
                                }

                                lock (run.SyncRoot)
                                {
                                    return run.RoomStates.TryGetValue(
                                            roomKey,
                                            out var currentRoomState)
                                        && ReferenceEquals(
                                            currentRoomState,
                                            capturedRoomState)
                                        && !capturedRoomState
                                            .EventMonsterConditionAdvanced;
                                }
                            },
                            () =>
                            {
                                onSent?.Invoke();
                                lock (run.SyncRoot)
                                {
                                    capturedRoomState
                                        .EventMonsterConditionAdvanced = true;
                                }
                            }));
            if (!sent)
                return;

            FileLogger.Log(
                $"[EventMonsterCondition] scene condition advanced: " +
                $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                $"maze={run.MazeIndex} room=({room.X},{room.Y}) map={descriptor.MapId} " +
                $"target={descriptor.TargetCode}@({descriptor.TargetX},{descriptor.TargetY}) " +
                $"specialObjects={descriptor.SpecialObjectCount} " +
                $"eventPositions={descriptor.EventPositionCount}");
        }

        internal static bool TryDescribeCandidateRoom(
            DungeonRun run,
            DungeonData.MazeSumInfo room,
            out CandidateRoomDescriptor descriptor)
        {
            descriptor = default;
            if (run == null
                || run.MazeQuestConnected
                || room.Index <= 0
                || room.Monsters == null
                || room.EventMonsterPositions == null
                || room.EventMonsterPositions.Count == 0
                || room.SpecialPassiveObjects == null
                || room.SpecialPassiveObjects.Count == 0)
            {
                return false;
            }

            DungeonFile dungeonFile;
            MazeInfo maze;
            try
            {
                dungeonFile = DungeonData.GetDungeonFile(run.DungeonId);
                if (dungeonFile?.Mazes == null
                    || run.MazeIndex < 0
                    || run.MazeIndex >= dungeonFile.Mazes.Count)
                {
                    return false;
                }

                maze = dungeonFile.Mazes[run.MazeIndex];
            }
            catch
            {
                return false;
            }

            if (maze.EventMonsterRandomMap < 0
                || maze.MinimapIcons == null
                || maze.MinimapIcons.Count == 0
                || IsBossRoom(run, room.X, room.Y)
                || !IsMarkedCandidateRoom(maze.MinimapIcons, room.X, room.Y))
            {
                return false;
            }

            var conditionObjectCount = CountConditionObjects(
                room.SpecialPassiveObjects);
            if (conditionObjectCount == 0
                || dungeonFile.NamedMonster == null
                || dungeonFile.NamedMonster.Length == 0)
            {
                return false;
            }

            var namedCodes = new HashSet<int>(dungeonFile.NamedMonster);
            if (!TryGetEventBounds(
                    room.EventMonsterPositions,
                    out var minX,
                    out var minY,
                    out var maxX,
                    out var maxY))
            {
                return false;
            }

            foreach (var monster in room.Monsters)
            {
                if (monster.Type > 3
                    || !namedCodes.Contains(monster.Code)
                    || !IsOutsideBounds(monster.X, monster.Y, minX, minY, maxX, maxY))
                {
                    continue;
                }

                descriptor = new CandidateRoomDescriptor(
                    room.Index,
                    monster.Code,
                    monster.X,
                    monster.Y,
                    conditionObjectCount,
                    room.EventMonsterPositions.Count);
                return true;
            }

            return false;
        }

        private static bool IsBossRoom(DungeonRun run, int x, int y)
            => run.BossMapPos != null
                && run.BossMapPos.Length >= 2
                && run.BossMapPos[0] == x
                && run.BossMapPos[1] == y;

        private static bool IsMarkedCandidateRoom(
            IReadOnlyList<MazeMinimapIconInfo> icons,
            int x,
            int y)
        {
            foreach (var icon in icons)
                if (icon != null && icon.X == x && icon.Y == y)
                    return true;
            return false;
        }

        private static int CountConditionObjects(
            IReadOnlyList<SpecialPassiveObjectInfo> objects)
        {
            var count = 0;
            foreach (var obj in objects)
            {
                if (obj?.Spawns == null)
                    continue;

                foreach (var spawn in obj.Spawns)
                {
                    if (spawn != null
                        && string.Equals(
                            spawn.Kind,
                            "[item]",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        count++;
                        break;
                    }
                }
            }

            return count;
        }

        private static bool TryGetEventBounds(
            IReadOnlyList<EventMonsterPositionInfo> positions,
            out int minX,
            out int minY,
            out int maxX,
            out int maxY)
        {
            minX = int.MaxValue;
            minY = int.MaxValue;
            maxX = int.MinValue;
            maxY = int.MinValue;
            foreach (var position in positions)
            {
                if (position == null)
                    continue;

                minX = Math.Min(minX, position.X);
                minY = Math.Min(minY, position.Y);
                maxX = Math.Max(maxX, position.X);
                maxY = Math.Max(maxY, position.Y);
            }

            return minX != int.MaxValue;
        }

        private static bool IsOutsideBounds(
            int x,
            int y,
            int minX,
            int minY,
            int maxX,
            int maxY)
            => x < minX || x > maxX || y < minY || y > maxY;
    }
}
