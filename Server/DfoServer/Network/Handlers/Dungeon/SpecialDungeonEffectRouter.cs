using DfoServer.Game.Dungeon;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class SpecialDungeonEffectRouter
    {
        private readonly ISpecialDungeonNotificationSender _sender;

        internal SpecialDungeonEffectRouter(
            ISpecialDungeonNotificationSender sender = null)
        {
            _sender = sender ?? new SpecialDungeonNotificationSender();
        }

        internal async Task RouteAsync(
            EnhancedClientSession session,
            DungeonRun run,
            IReadOnlyList<SpecialDungeonEffectIntent> effects,
            bool allowEndingRun = false,
            Func<byte[], Task<bool>> trySendPacketAsync = null)
        {
            if (session == null
                || run == null
                || effects == null
                || effects.Count == 0)
            {
                return;
            }

            for (var index = 0; index < effects.Count; index++)
            {
                if (!CanProject(session, run, allowEndingRun))
                    return;

                var effect = effects[index];
                if (effect == null)
                    continue;

                if (ApplyStateEffect(run, effect))
                    continue;

                await _sender.SendAsync(
                    session,
                    effect,
                    trySendPacketAsync);
            }
        }

        internal async Task RoutePlanAsync(
            EnhancedClientSession session,
            DungeonRun run,
            SpecialDungeonEffectPlan plan,
            bool allowEndingRun = false)
        {
            if (session == null
                || run == null
                || plan == null
                || !run.Matches(plan.RunIdentity))
            {
                return;
            }

            foreach (var item in plan.Items)
            {
                if (!CanProject(session, run, allowEndingRun))
                    return;

                var state = run.Effects.GetState(item.EffectId);
                if (state == DungeonEffectState.Committed)
                    continue;
                if (!run.Effects.TryReserve(
                        item.EffectId,
                        out var reservation))
                {
                    if (run.Effects.GetState(item.EffectId)
                        == DungeonEffectState.Committed)
                    {
                        continue;
                    }

                    return;
                }

                try
                {
                    if (!ApplyStateEffect(run, item.Intent))
                        await _sender.SendAsync(
                            session,
                            item.Intent,
                            trySendPacketAsync: null);

                    if (!run.Effects.TryCommit(reservation))
                    {
                        run.Effects.TryFail(reservation);
                        throw new InvalidOperationException(
                            "Special dungeon effect checkpoint commit failed.");
                    }
                }
                catch
                {
                    run.Effects.TryFail(reservation);
                    throw;
                }
            }
        }

        internal async Task RecoverAsync(
            EnhancedClientSession session,
            DungeonRun run)
        {
            if (!CanProject(session, run, allowEndingRun: false))
                return;

            var plans = run.SpecialDungeonEffectPlans.GetRecoverable(
                run.Effects);
            foreach (var plan in plans)
            {
                if (!CanProject(session, run, allowEndingRun: false))
                    return;
                await RoutePlanAsync(session, run, plan);
            }
        }

        private static bool ApplyStateEffect(
            DungeonRun run,
            SpecialDungeonEffectIntent effect)
        {
            switch (effect.Kind)
            {
                case SpecialDungeonEffectKind.CancelMechanismTimer:
                    DungeonMechanismTimerCoordinator.Cancel(run);
                    return true;

                case SpecialDungeonEffectKind.ResetTimeCrackGauge:
                    lock (run.SyncRoot)
                        run.Mechanisms.SpecialDungeon?.ResetTimeCrackGauge();
                    return true;

                case SpecialDungeonEffectKind.RecordSeaChaseBuffs:
                    lock (run.SyncRoot)
                    {
                        run.Mechanisms.SpecialDungeon
                            ?.NoteSeaChaseBuffsApplied(effect.BuffIds);
                    }
                    return true;

                default:
                    return false;
            }
        }

        private static bool CanProject(
            EnhancedClientSession session,
            DungeonRun run,
            bool allowEndingRun)
        {
            var player = session?.Player;
            if (player == null || run == null)
                return false;
            if (player.IsCurrentDungeonRun(run.CaptureIdentity()))
                return true;

            return allowEndingRun
                && player.CurrentRun == null
                && player.CurrentDungeonRunGeneration == run.RunGeneration;
        }
    }
}
