using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    internal sealed class SpecialDungeonMechanismApplicationService
    {
        internal const ushort BossSummonRuntimeKey = 0x42DD;

        private readonly struct BossTemplate
        {
            internal BossTemplate(
                int mapId,
                int monsterCode,
                byte level,
                int localIndex)
            {
                MapId = mapId;
                MonsterCode = monsterCode;
                Level = level;
                LocalIndex = localIndex;
            }

            internal int MapId { get; }
            internal int MonsterCode { get; }
            internal byte Level { get; }
            internal int LocalIndex { get; }
        }

        internal IReadOnlyList<SpecialDungeonEffectIntent> BuildClearRunBuffs(
            DungeonRun run,
            string reason)
        {
            if (run == null)
                return Array.Empty<SpecialDungeonEffectIntent>();

            lock (run.SyncRoot)
            {
                var special = run.Mechanisms.SpecialDungeon;
                if (special == null)
                    return Array.Empty<SpecialDungeonEffectIntent>();

                List<int> buffIds;
                switch (special.Kind)
                {
                    case SpecialDungeonKind.SealForest:
                        if (!special.TryConsumeSealForestBuffIds(out buffIds))
                            return Array.Empty<SpecialDungeonEffectIntent>();
                        break;

                    case SpecialDungeonKind.SeaChase:
                        if (!special.TryConsumeSeaChaseAppliedBuffIds(
                                out buffIds))
                        {
                            return Array.Empty<SpecialDungeonEffectIntent>();
                        }
                        break;

                    case SpecialDungeonKind.TimeCrack:
                        if (!special.TryConsumeTimeCrackBuffIds(out buffIds))
                            return Array.Empty<SpecialDungeonEffectIntent>();
                        break;

                    default:
                        return Array.Empty<SpecialDungeonEffectIntent>();
                }

                return new[]
                {
                    new SpecialDungeonEffectIntent
                    {
                        Kind = SpecialDungeonEffectKind.BuffsCleared,
                        Reason = reason,
                        BuffIds = Copy(buffIds),
                    },
                };
            }
        }

        internal SpecialDungeonEffectPlanRegistration
            BuildClearRunBuffsPlan(
                DungeonRun run,
                DungeonEventEnvelope source,
                string reason)
            => ApplyAndPlan(
                run,
                source,
                "run-end-buffs",
                () => BuildClearRunBuffs(run, reason));

        internal IReadOnlyList<SpecialDungeonEffectIntent> BuildStartMapState(
            DungeonRun run)
        {
            if (run == null)
                return Array.Empty<SpecialDungeonEffectIntent>();

            lock (run.SyncRoot)
            {
                var special = run.Mechanisms.SpecialDungeon;
                if (special?.Kind == SpecialDungeonKind.SeizeMoney)
                {
                    return Gauge(
                        special.SeizeMoneyGauge,
                        "seize_money");
                }
                if (special?.Kind == SpecialDungeonKind.TimeCrack)
                {
                    return Gauge(
                        special.TimeCrackGauge,
                        "time_crack");
                }
            }

            return Array.Empty<SpecialDungeonEffectIntent>();
        }

        internal IReadOnlyList<SpecialDungeonEffectIntent>
            BuildBossEntranceMinimap(DungeonRun run, string reason)
        {
            if (run == null)
                return Array.Empty<SpecialDungeonEffectIntent>();

            lock (run.SyncRoot)
            {
                if (!run.Mechanisms.HasBossEntranceConditionalSummon)
                    return Array.Empty<SpecialDungeonEffectIntent>();

                var entries = new List<(byte X, byte Y, int MonsterCode)>();
                foreach (var target in
                    run.Mechanisms.BossEntranceConditionTargets)
                {
                    if (target != null && target.MonsterCode > 0)
                    {
                        entries.Add((
                            target.X,
                            target.Y,
                            target.MonsterCode));
                    }
                }

                if (entries.Count == 0)
                    return Array.Empty<SpecialDungeonEffectIntent>();

                return new[]
                {
                    new SpecialDungeonEffectIntent
                    {
                        Kind = SpecialDungeonEffectKind.BossEntranceMinimap,
                        Reason = reason,
                        MinimapEntries = entries.AsReadOnly(),
                    },
                };
            }
        }

        internal IReadOnlyList<SpecialDungeonEffectIntent> ApplyMonsterKilled(
            DungeonRun run,
            int monsterCode,
            byte monsterType)
        {
            if (run == null || monsterCode <= 0)
                return Array.Empty<SpecialDungeonEffectIntent>();

            var effects = new List<SpecialDungeonEffectIntent>();
            lock (run.SyncRoot)
            {
                var special = run.Mechanisms.SpecialDungeon;
                if (special != null)
                {
                    ApplySealForestKill(special, monsterCode, effects);
                    ApplyTimeCrackKill(
                        run,
                        special,
                        monsterCode,
                        monsterType,
                        effects);
                }

                ApplyBossEntranceKill(run, monsterCode, effects);
                if (special != null)
                    ApplyGentInfiltrateKill(special, monsterCode, effects);
            }

            return effects;
        }

        internal SpecialDungeonEffectPlanRegistration
            ApplyMonsterKilledAndPlan(
                DungeonRun run,
                DungeonEventEnvelope source,
                int monsterCode,
                byte monsterType)
            => ApplyAndPlan(
                run,
                source,
                "monster-kill/" + source?.SourceEventId.ToString("N"),
                () => ApplyMonsterKilled(
                    run,
                    monsterCode,
                    monsterType));

        internal IReadOnlyList<SpecialDungeonEffectIntent> ApplyBossSummon(
            DungeonRun run,
            SummonMonsterDungeonCommand request)
        {
            if (run == null || request == null)
                return Array.Empty<SpecialDungeonEffectIntent>();

            lock (run.SyncRoot)
            {
                if (!run.Mechanisms.HasBossEntranceConditionalSummon
                    || !run.Mechanisms.BossEntranceConditionComplete
                    || run.Mechanisms.ConditionalBossSpawned
                    || !TryFindBossTemplate(run, out var template)
                    || request.MapId != template.MapId
                    || request.MonsterCode != template.MonsterCode)
                {
                    return Array.Empty<SpecialDungeonEffectIntent>();
                }

                if (run.Instance == null
                    || !run.Instance.Mechanisms.TryRegisterConditionalBossSpawn(
                        template.MonsterCode))
                {
                    return Array.Empty<SpecialDungeonEffectIntent>();
                }

                run.Mechanisms.ConditionalBossSpawned = true;
                run.Mechanisms.ConditionalBossCode = template.MonsterCode;
                var level = template.Level > 0
                    ? (ushort)template.Level
                    : ResolveDungeonLevel(run.DungeonId);
                return new[]
                {
                    new SpecialDungeonEffectIntent
                    {
                        Kind = SpecialDungeonEffectKind.SummonMonsterResponse,
                        Reason = "boss_entrance_condition",
                        StateId = request.StateId,
                        MonsterCode = template.MonsterCode,
                        MonsterLevel = level,
                        MapId = template.MapId,
                        LocalIndex = template.LocalIndex,
                    },
                };
            }
        }

        internal SpecialDungeonEffectPlanRegistration ApplyBossSummonAndPlan(
            DungeonRun run,
            DungeonEventEnvelope source,
            SummonMonsterDungeonCommand request)
            => ApplyAndPlan(
                run,
                source,
                "conditional-boss-summon",
                () => ApplyBossSummon(run, request));

        internal IReadOnlyList<SpecialDungeonEffectIntent>
            ApplySeaChaseResult(
                DungeonRun run,
                SeaChaseResultDungeonCommand command)
        {
            if (run == null || command == null)
                return Array.Empty<SpecialDungeonEffectIntent>();

            lock (run.SyncRoot)
            {
                var special = run.Mechanisms.SpecialDungeon;
                if (special?.Kind != SpecialDungeonKind.SeaChase)
                    return Array.Empty<SpecialDungeonEffectIntent>();

                var effects = new List<SpecialDungeonEffectIntent>
                {
                    new SpecialDungeonEffectIntent
                    {
                        Kind = SpecialDungeonEffectKind.CommandSuccessAck,
                        WireType = command.WireType,
                        Reason = "sea_chase_result",
                    },
                };
                var succeeded = command.Result != 0;
                var firstResult = !special.SeaChaseMiniGameSucceeded.HasValue;
                special.NoteSeaChaseMiniGameResult(succeeded);
                if (!firstResult)
                    return effects;

                var buffIds = succeeded
                    ? special.Definition.SeaChase.SuccessBuffIds
                    : special.Definition.SeaChase.FailBuffIds;
                effects.Add(new SpecialDungeonEffectIntent
                {
                    Kind = SpecialDungeonEffectKind.BuffAddedAndActivated,
                    Reason = succeeded
                        ? "sea_chase_success"
                        : "sea_chase_failure",
                    BuffIds = Copy(buffIds),
                    ActiveBuffIds = Copy(buffIds),
                });
                effects.Add(new SpecialDungeonEffectIntent
                {
                    Kind = SpecialDungeonEffectKind.RecordSeaChaseBuffs,
                    Reason = "sea_chase_record_buffs",
                    BuffIds = Copy(buffIds),
                });
                return effects;
            }
        }

        internal SpecialDungeonEffectPlanRegistration
            ApplySeaChaseResultAndPlan(
                DungeonRun run,
                DungeonEventEnvelope source,
                SeaChaseResultDungeonCommand command)
            => ApplyAndPlan(
                run,
                source,
                "sea-chase-result",
                () => ApplySeaChaseResult(run, command));

        internal bool ApplyGentInfiltrateTimeout(
            DungeonRun run,
            out int destroyed,
            out int required)
        {
            destroyed = 0;
            required = 0;
            if (run == null)
                return false;

            lock (run.SyncRoot)
            {
                var special = run.Mechanisms.SpecialDungeon;
                if (special?.Kind != SpecialDungeonKind.GentInfiltrate)
                    return false;

                special.TryCompleteGentInfiltrateByTimer(
                    out destroyed,
                    out required);
                return true;
            }
        }

        internal static bool TryPickTimeCrackBuff(
            SpecialDungeonRuntime special,
            DnfLcg lcg,
            out int buffId,
            out int roll,
            out int totalWeight,
            out string pickMode)
        {
            buffId = 0;
            roll = 0;
            totalWeight = 0;
            pickMode = "none";

            var weights = special?.Definition?.TimeCrack?.BuffWeights;
            if (weights == null || weights.Count == 0)
                return false;

            var candidates = new List<TimeCrackBuffWeight>();
            foreach (var entry in weights)
            {
                if (entry.BuffId > 0
                    && entry.Weight > 0
                    && !Contains(special.TimeCrackBuffIds, entry.BuffId))
                {
                    candidates.Add(entry);
                }
            }

            if (candidates.Count > 0)
            {
                pickMode = "missing_first";
            }
            else
            {
                pickMode = "refresh_all";
                foreach (var entry in weights)
                {
                    if (entry.BuffId > 0 && entry.Weight > 0)
                        candidates.Add(entry);
                }
            }

            foreach (var entry in candidates)
                totalWeight += entry.Weight;
            if (totalWeight <= 0)
                return false;

            roll = lcg != null
                ? lcg.Next(totalWeight)
                : ServerRandom.Next(totalWeight);
            var cursor = roll;
            foreach (var entry in candidates)
            {
                if (cursor < entry.Weight)
                {
                    buffId = entry.BuffId;
                    return true;
                }
                cursor -= entry.Weight;
            }

            buffId = candidates[candidates.Count - 1].BuffId;
            return buffId > 0;
        }

        private static void ApplySealForestKill(
            SpecialDungeonRuntime special,
            int monsterCode,
            ICollection<SpecialDungeonEffectIntent> effects)
        {
            if (!special.TryMarkSealForestBuffMonster(
                    monsterCode,
                    out var entry))
            {
                return;
            }

            effects.Add(new SpecialDungeonEffectIntent
            {
                Kind = SpecialDungeonEffectKind.BuffAddedAndActivated,
                Reason = "seal_forest_kill",
                BuffIds = new[] { entry.BuffId },
                ActiveBuffIds = Copy(special.SealForestBuffIds),
                MonsterCode = monsterCode,
            });
        }

        private static void ApplyTimeCrackKill(
            DungeonRun run,
            SpecialDungeonRuntime special,
            int monsterCode,
            byte monsterType,
            ICollection<SpecialDungeonEffectIntent> effects)
        {
            if (special.Kind != SpecialDungeonKind.TimeCrack
                || !special.TryAddTimeCrackGauge(
                    monsterCode,
                    monsterType == 1,
                    out _,
                    out var current,
                    out _,
                    out var filled))
            {
                return;
            }

            effects.Add(new SpecialDungeonEffectIntent
            {
                Kind = SpecialDungeonEffectKind.GaugeChanged,
                Reason = "time_crack_kill",
                Value = current,
                MonsterCode = monsterCode,
            });

            if (!filled
                || !TryPickTimeCrackBuff(
                    special,
                    run.Combat.RoomLcg,
                    out var buffId,
                    out _,
                    out _,
                    out _))
            {
                return;
            }

            special.NoteTimeCrackBuffApplied(buffId);
            effects.Add(new SpecialDungeonEffectIntent
            {
                Kind = SpecialDungeonEffectKind.BuffAddedAndActivated,
                Reason = "time_crack_filled",
                BuffIds = new[] { buffId },
                ActiveBuffIds = Copy(special.TimeCrackBuffIds),
            });
            effects.Add(new SpecialDungeonEffectIntent
            {
                Kind = SpecialDungeonEffectKind.ResetTimeCrackGauge,
                Reason = "time_crack_reset_state",
            });
            effects.Add(new SpecialDungeonEffectIntent
            {
                Kind = SpecialDungeonEffectKind.GaugeChanged,
                Reason = "time_crack_reset",
                Value = 0,
            });
        }

        private static void ApplyBossEntranceKill(
            DungeonRun run,
            int monsterCode,
            ICollection<SpecialDungeonEffectIntent> effects)
        {
            if (!run.Mechanisms.HasBossEntranceConditionalSummon)
                return;

            var matched = false;
            var completed = 0;
            var total = 0;
            foreach (var target in
                run.Mechanisms.BossEntranceConditionTargets)
            {
                if (target == null)
                    continue;

                total++;
                if (!matched
                    && !target.Completed
                    && target.MonsterCode == monsterCode
                    && target.X == run.Combat.RoomKey.X
                    && target.Y == run.Combat.RoomKey.Y)
                {
                    target.Completed = true;
                    matched = true;
                }

                if (target.Completed)
                    completed++;
            }

            if (!matched)
                return;

            if (total > 0 && completed >= total)
            {
                run.Mechanisms.BossEntranceConditionComplete = true;
                effects.Add(new SpecialDungeonEffectIntent
                {
                    Kind = SpecialDungeonEffectKind.PassGate,
                    Reason = "boss_entrance_complete",
                    MonsterCode = monsterCode,
                });
            }
        }

        private static void ApplyGentInfiltrateKill(
            SpecialDungeonRuntime special,
            int monsterCode,
            ICollection<SpecialDungeonEffectIntent> effects)
        {
            if (special.Kind != SpecialDungeonKind.GentInfiltrate
                || !special.TryMarkGentInfiltrateTowerDestroyed(
                    monsterCode,
                    out _,
                    out _,
                    out _,
                    out _,
                    out var completed)
                || !completed)
            {
                return;
            }

            effects.Add(new SpecialDungeonEffectIntent
            {
                Kind = SpecialDungeonEffectKind.CancelMechanismTimer,
                Reason = "gent_four_towers",
            });
            effects.Add(new SpecialDungeonEffectIntent
            {
                Kind = SpecialDungeonEffectKind.PassGate,
                Reason = "gent_four_towers",
                MonsterCode = monsterCode,
            });
            if (special.GentInfiltrateStrongWarlord)
            {
                effects.Add(new SpecialDungeonEffectIntent
                {
                    Kind = SpecialDungeonEffectKind.StrongWarlordSelected,
                    Reason = "gent_strong_warlord",
                });
            }
        }

        private static bool TryFindBossTemplate(
            DungeonRun run,
            out BossTemplate template)
        {
            template = default;
            var codes = run.Mechanisms.BossEntranceConditionalSummonCodes;
            if (codes == null || codes.Count == 0)
                return false;

            if (run.Combat.RoomStates == null
                || !run.Combat.RoomStates.TryGetValue(
                    run.Combat.RoomKey,
                    out var roomState)
                || roomState == null
                || roomState.Maze.Monsters == null)
            {
                return false;
            }

            for (var index = 0; index < roomState.Maze.Monsters.Count; index++)
            {
                var monster = roomState.Maze.Monsters[index];
                if (monster.Flag0 == 0 || !codes.Contains(monster.Code))
                    continue;

                template = new BossTemplate(
                    roomState.Maze.Index,
                    monster.Code,
                    monster.Level,
                    index);
                return true;
            }

            return false;
        }

        private static ushort ResolveDungeonLevel(int dungeonId)
        {
            try
            {
                return (ushort)Math.Max(
                    1,
                    Math.Min(
                        ushort.MaxValue,
                        (int)GameWorld.Dungeon.GetDungeonBasicLv(dungeonId)));
            }
            catch
            {
                return 1;
            }
        }

        private static IReadOnlyList<SpecialDungeonEffectIntent> Gauge(
            int value,
            string reason)
            => new[]
            {
                new SpecialDungeonEffectIntent
                {
                    Kind = SpecialDungeonEffectKind.GaugeChanged,
                    Reason = reason,
                    Value = value,
                },
            };

        private static IReadOnlyList<int> Copy(IReadOnlyList<int> values)
        {
            if (values == null || values.Count == 0)
                return Array.Empty<int>();

            var copy = new int[values.Count];
            for (var index = 0; index < values.Count; index++)
                copy[index] = values[index];
            return copy;
        }

        private static SpecialDungeonEffectPlanRegistration ApplyAndPlan(
            DungeonRun run,
            DungeonEventEnvelope source,
            string operation,
            Func<IReadOnlyList<SpecialDungeonEffectIntent>> transition)
        {
            if (run == null
                || source == null
                || transition == null
                || string.IsNullOrWhiteSpace(operation))
            {
                return default;
            }

            lock (run.SyncRoot)
            {
                if (!run.Matches(source.RunIdentity))
                    return default;

                if (run.SpecialDungeonEffectPlans.TryGet(
                        operation,
                        out var existing))
                {
                    return new SpecialDungeonEffectPlanRegistration(
                        existing,
                        created: false,
                        wasComplete: existing.IsComplete(run.Effects));
                }

                var intents = transition();
                return run.SpecialDungeonEffectPlans.Register(
                    run,
                    source,
                    operation,
                    intents);
            }
        }

        private static bool Contains(
            IReadOnlyList<int> values,
            int value)
        {
            if (values == null)
                return false;

            for (var index = 0; index < values.Count; index++)
            {
                if (values[index] == value)
                    return true;
            }
            return false;
        }
    }
}
