using System;
using System.Collections.Generic;
using DfoServer.Game.CharacterData;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Game.Dungeon
{
    internal sealed class AntonNormalClearApplicationResult
    {
        internal AntonNormalClearApplicationResult(
            AntonNormalSyncState state,
            IReadOnlyList<DungeonPermissionEntrySnapshot> changes)
        {
            State = state;
            Changes = changes ?? Array.Empty<DungeonPermissionEntrySnapshot>();
        }

        internal AntonNormalSyncState State { get; }
        internal IReadOnlyList<DungeonPermissionEntrySnapshot> Changes { get; }
    }

    internal sealed class AntonNormalConquestApplicationService
    {
        private const int LinkedChallengeRate = 100;
        private const int LinkedChallengeCondition = -1;
        private readonly SqliteCharacterStateRepository _repository;

        internal AntonNormalConquestApplicationService(
            SqliteCharacterStateRepository repository)
        {
            _repository = repository
                ?? throw new ArgumentNullException(nameof(repository));
        }

        internal void ConfigureLinkedChallenge(DungeonRun run)
        {
            if (run == null || !AntonNormalConquest.TryGetSequence(run.DungeonId, out _))
                return;
            if (!AntonNormalConquest.TryResolveLinkedNext(
                    run.DungeonId,
                    out var nextDungeonId))
            {
                run.LinkedDungeonNextId = 0;
                run.LinkedDungeonNextRate = 0;
                run.LinkedDungeonNextCondition = 0;
                return;
            }

            run.LinkedDungeonNextId = nextDungeonId;
            run.LinkedDungeonNextRate = LinkedChallengeRate;
            run.LinkedDungeonNextCondition = LinkedChallengeCondition;
        }

        // CMD SEQUENTIAL_DUNGEON_INFO(0x035D) 应答: 只解析客户端询问的
        // configKey 对应的序列。
        internal bool TryRestore(
            int characterId,
            int configKey,
            out AntonNormalSyncState state)
        {
            state = null;
            if (characterId <= 0)
                return false;
            return AntonNormalConquest.TryResolveSyncState(
                configKey,
                _repository.LoadDungeonPermissions(characterId),
                out state);
        }

        internal bool TryApplyClear(
            int characterId,
            int dungeonId,
            out AntonNormalClearApplicationResult result)
        {
            result = null;
            if (characterId <= 0
                || !AntonNormalConquest.TryResolveClearPlan(dungeonId, out var plan))
            {
                return false;
            }

            var updates = new List<DungeonPermissionEntrySnapshot>();
            AddPermissionUpdate(
                updates,
                dungeonId,
                plan.Sequence.Difficulty,
                completed: true);
            AddPermissionUpdate(
                updates,
                plan.NextDungeonId,
                plan.Sequence.Difficulty,
                completed: false);
            AddPreviewPermissionUpdate(
                updates,
                plan.PreviewDungeonId,
                plan.Sequence.Difficulty);

            var permissions = _repository.ApplyDungeonPermissionBatch(
                characterId,
                updates,
                out var changes);
            if (!AntonNormalConquest.TryResolveSyncState(
                    permissions,
                    out var state)
                || state.Sequence.IndexOf(dungeonId) < 0)
            {
                return false;
            }

            result = new AntonNormalClearApplicationResult(state, changes);
            return true;
        }

        private static void AddPermissionUpdate(
            ICollection<DungeonPermissionEntrySnapshot> updates,
            int dungeonId,
            byte difficulty,
            bool completed)
        {
            if (dungeonId <= 0)
                return;
            var resolved = completed
                ? AntonNormalConquest.TryResolveCompletedState(
                    dungeonId,
                    difficulty,
                    out var clearState)
                : AntonNormalConquest.TryResolveUnlockedState(
                    dungeonId,
                    difficulty,
                    out clearState);
            if (resolved)
            {
                updates.Add(new DungeonPermissionEntrySnapshot
                {
                    DungeonId = (ushort)dungeonId,
                    ClearState = clearState,
                });
            }
        }

        private static void AddPreviewPermissionUpdate(
            ICollection<DungeonPermissionEntrySnapshot> updates,
            int dungeonId,
            byte difficulty)
        {
            if (dungeonId <= 0
                || !AntonNormalConquest.TryResolveUnlockedState(
                    dungeonId,
                    difficulty,
                    out var unlockedState))
            {
                return;
            }
            updates.Add(new DungeonPermissionEntrySnapshot
            {
                DungeonId = (ushort)dungeonId,
                ClearState = (byte)Math.Max(1, unlockedState - 1),
            });
        }
    }
}
