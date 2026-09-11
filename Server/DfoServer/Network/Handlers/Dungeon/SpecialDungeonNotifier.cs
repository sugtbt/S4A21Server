using DfoServer.Game.Dungeon;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Dungeon
{
    // Compatibility facade for existing lifecycle entry points. Parsing lives
    // at the network boundary, transitions in the application service, and all
    // protocol projection in the effect router/sender.
    internal static class SpecialDungeonNotifier
    {
        private static readonly SpecialDungeonMechanismApplicationService
            Application = new SpecialDungeonMechanismApplicationService();
        private static readonly SpecialDungeonEffectRouter Effects =
            new SpecialDungeonEffectRouter();

        internal const ushort BossSummonRuntimeKey =
            SpecialDungeonMechanismApplicationService.BossSummonRuntimeKey;

        internal static Task ClearRunBuffsAsync(
            EnhancedClientSession session,
            string reason)
            => ClearRunBuffsAsync(
                session,
                session?.Player?.CurrentRun,
                reason);

        internal static async Task ClearRunBuffsAsync(
            EnhancedClientSession session,
            DungeonRun run,
            string reason)
        {
            if (!CanProjectEndingRun(session, run))
                return;

            var sourceEvent = DungeonEventEnvelope.Create(
                run,
                session.Player.CharacterId,
                "special-dungeon run end: " + (reason ?? string.Empty),
                sourceEventId: run.GetEndSourceEventId());
            var registration = Application.BuildClearRunBuffsPlan(
                run,
                sourceEvent,
                reason);
            await Effects.RoutePlanAsync(
                session,
                run,
                registration.Plan,
                allowEndingRun: true);
        }

        internal static async Task SendStartMapStateAsync(
            EnhancedClientSession session,
            DungeonRun run,
            Func<byte[], Task<bool>> trySendPacketAsync)
        {
            if (!IsCurrent(session, run))
                return;

            await Effects.RouteAsync(
                session,
                run,
                Application.BuildStartMapState(run),
                trySendPacketAsync: trySendPacketAsync);
        }

        internal static async Task SendBossEntranceMinimapIconInfoAsync(
            EnhancedClientSession session,
            string reason)
        {
            var run = session?.Player?.CurrentRun;
            if (!IsCurrent(session, run))
                return;

            await Effects.RouteAsync(
                session,
                run,
                Application.BuildBossEntranceMinimap(run, reason));
        }

        internal static async Task ObserveMonsterKilledAsync(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonEventEnvelope sourceEvent,
            int monsterCode,
            byte monsterType)
        {
            if (!IsCurrent(session, run)
                || !IsCurrentEvent(session, sourceEvent)
                || monsterCode <= 0)
            {
                return;
            }

            var registration = Application.ApplyMonsterKilledAndPlan(
                run,
                sourceEvent,
                monsterCode,
                monsterType);
            await Effects.RoutePlanAsync(
                session,
                run,
                registration.Plan);
        }

        internal static async Task HandleBossSummonRequestAsync(
            EnhancedClientSession session,
            SummonMonsterDungeonCommand request,
            DungeonEventEnvelope sourceEvent)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null
                || request == null
                || !IsCurrentEvent(session, sourceEvent))
            {
                return;
            }

            var registration = Application.ApplyBossSummonAndPlan(
                run,
                sourceEvent,
                request);
            if (!registration.HasPlan)
            {
                FileLogger.Log(
                    $"[SpecialDungeonModule] boss summon rejected: " +
                    $"cid={session.Player.CharacterId} " +
                    $"dungeon={run.DungeonId} map={request.MapId} " +
                    $"monster={request.MonsterCode} state={request.StateId} " +
                    $"matches={request.MatchCount}");
                return;
            }

            FileLogger.Log(
                $"[SpecialDungeonModule] boss summon accepted: " +
                $"cid={session.Player.CharacterId} " +
                $"dungeon={run.DungeonId} map={request.MapId} " +
                $"monster={request.MonsterCode} state={request.StateId}");

            await Effects.RoutePlanAsync(
                session,
                run,
                registration.Plan);
        }

        internal static Task HandleGentInfiltrateTimerModifyInfoAsync(
            EnhancedClientSession session,
            TimerModifyInfoDungeonCommand command,
            DungeonEventEnvelope sourceEvent)
        {
            LogObservedCommand(
                session,
                sourceEvent,
                "TIMER_MODIFY_INFO",
                command?.WireType ?? 0,
                command?.Payload);
            return Task.CompletedTask;
        }

        internal static async Task HandleSeaChaseMiniGameResultAsync(
            EnhancedClientSession session,
            SeaChaseResultDungeonCommand command,
            DungeonEventEnvelope sourceEvent)
            => await HandleSeaChaseMiniGameResultAsync(
                session,
                command,
                sourceEvent,
                Application,
                Effects);

        internal static async Task HandleSeaChaseMiniGameResultAsync(
            EnhancedClientSession session,
            SeaChaseResultDungeonCommand command,
            DungeonEventEnvelope sourceEvent,
            SpecialDungeonMechanismApplicationService application,
            SpecialDungeonEffectRouter effects)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null
                || command == null
                || application == null
                || effects == null
                || !IsCurrentEvent(session, sourceEvent)
                || run.Mechanisms.SpecialDungeon?.Kind
                    != SpecialDungeonKind.SeaChase)
            {
                return;
            }

            var registration = application.ApplySeaChaseResultAndPlan(
                run,
                sourceEvent,
                command);
            if (!registration.HasPlan)
                return;

            if (!registration.Created && registration.WasComplete)
            {
                await effects.RouteAsync(
                    session,
                    run,
                    new[]
                    {
                        new SpecialDungeonEffectIntent
                        {
                            Kind = SpecialDungeonEffectKind.CommandSuccessAck,
                            WireType = command.WireType,
                            Reason = "sea_chase_result_replay",
                        },
                    });
            }
            else
            {
                await effects.RoutePlanAsync(
                    session,
                    run,
                    registration.Plan);
            }
            FileLogger.Log(
                $"[SpecialDungeonModule] SEA_CHASE result: " +
                $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                $"result={command.Result}");
        }

        internal static Task RecoverPendingEffectPlansAsync(
            EnhancedClientSession session)
            => RecoverPendingEffectPlansAsync(session, Effects);

        internal static Task RecoverPendingEffectPlansAsync(
            EnhancedClientSession session,
            SpecialDungeonEffectRouter effects)
        {
            var run = session?.Player?.CurrentRun;
            return run == null || effects == null
                ? Task.CompletedTask
                : effects.RecoverAsync(session, run);
        }

        internal static Task ObserveSeaChasePacketAsync(
            EnhancedClientSession session,
            SeaChaseObservedDungeonCommand command,
            DungeonEventEnvelope sourceEvent)
        {
            LogObservedCommand(
                session,
                sourceEvent,
                "SEA_CHASE",
                command?.WireType ?? 0,
                command?.Payload);
            return Task.CompletedTask;
        }

        internal static Task MarkGentInfiltrateTimeoutAsync(
            EnhancedClientSession session,
            string source)
            => MarkGentInfiltrateTimeoutAsync(
                session,
                session?.Player?.CurrentRun,
                source);

        internal static Task MarkGentInfiltrateTimeoutAsync(
            EnhancedClientSession session,
            DungeonRun run,
            string source)
        {
            if (!IsCurrent(session, run)
                || !Application.ApplyGentInfiltrateTimeout(
                    run,
                    out var destroyed,
                    out var required))
            {
                return Task.CompletedTask;
            }

            FileLogger.Log(
                $"[SpecialDungeonModule] GENT_INFILTRATE timeout: " +
                $"source={source} cid={session.Player.CharacterId} " +
                $"dungeon={run.DungeonId} progress={destroyed}/{required} " +
                $"action=mark_timeout_wait_four_towers");
            return Task.CompletedTask;
        }

        internal static bool TryPickTimeCrackBuff(
            SpecialDungeonRuntime special,
            DnfLcg lcg,
            out int buffId,
            out int roll,
            out int totalWeight,
            out string pickMode)
            => SpecialDungeonMechanismApplicationService.TryPickTimeCrackBuff(
                special,
                lcg,
                out buffId,
                out roll,
                out totalWeight,
                out pickMode);

        private static void LogObservedCommand(
            EnhancedClientSession session,
            DungeonEventEnvelope sourceEvent,
            string name,
            ushort wireType,
            byte[] payload)
        {
            var run = session?.Player?.CurrentRun;
            FileLogger.Log(
                $"[SpecialDungeonModule] {name} observe: " +
                $"cid={session?.Player?.CharacterId ?? 0} " +
                $"dungeon={run?.DungeonId ?? 0} " +
                $"kind={run?.Mechanisms.SpecialDungeon?.Kind.ToString() ?? "none"} " +
                $"current={IsCurrentEvent(session, sourceEvent)} " +
                $"type=0x{wireType:X4} body={FormatPayload(payload)}");
        }

        private static bool IsCurrent(
            EnhancedClientSession session,
            DungeonRun run)
            => run != null
                && session?.Player != null
                && session.Player.IsCurrentDungeonRun(run.CaptureIdentity());

        private static bool IsCurrentEvent(
            EnhancedClientSession session,
            DungeonEventEnvelope sourceEvent)
        {
            if (session?.Player == null || sourceEvent == null)
                return false;
            if (!session.Player.IsCurrentDungeonRun(sourceEvent.RunIdentity))
                return false;

            var run = session.Player.CurrentRun;
            return !sourceEvent.RoomInstanceId.HasValue
                || (run != null
                    && run.CurrentRoomInstanceId
                        == sourceEvent.RoomInstanceId.Value);
        }

        private static bool CanProjectEndingRun(
            EnhancedClientSession session,
            DungeonRun run)
        {
            var player = session?.Player;
            if (player == null || run == null)
                return false;

            return ReferenceEquals(player.CurrentRun, run)
                || (player.CurrentRun == null
                    && player.CurrentDungeonRunGeneration
                        == run.RunGeneration);
        }

        private static string FormatPayload(byte[] payload)
            => payload == null || payload.Length == 0
                ? string.Empty
                : BitConverter.ToString(payload);
    }
}
