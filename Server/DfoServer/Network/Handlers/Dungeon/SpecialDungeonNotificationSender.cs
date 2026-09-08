using DfoServer.Game.Dungeon;
using DfoServer.Network.Builders;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal interface ISpecialDungeonNotificationSender
    {
        Task SendAsync(
            EnhancedClientSession session,
            SpecialDungeonEffectIntent effect,
            Func<byte[], Task<bool>> trySendPacketAsync);
    }

    internal sealed class SpecialDungeonNotificationSender
        : ISpecialDungeonNotificationSender
    {
        private const byte SummonMonsterResult = 0x01;
        private const byte SummonMonsterMode = 0x03;
        private const byte StrongWarlordResult = 0x01;

        Task ISpecialDungeonNotificationSender.SendAsync(
            EnhancedClientSession session,
            SpecialDungeonEffectIntent effect,
            Func<byte[], Task<bool>> trySendPacketAsync)
            => SendAsync(session, effect, trySendPacketAsync);

        internal async Task SendAsync(
            EnhancedClientSession session,
            SpecialDungeonEffectIntent effect,
            Func<byte[], Task<bool>> trySendPacketAsync = null)
        {
            if (session == null || effect == null)
                return;

            switch (effect.Kind)
            {
                case SpecialDungeonEffectKind.GaugeChanged:
                    await SendGaugeAsync(
                        session,
                        effect.Value,
                        trySendPacketAsync);
                    return;

                case SpecialDungeonEffectKind.BuffAddedAndActivated:
                    await DungeonBuffNotificationSender
                        .SendAddedAndActivateAsync(
                            session,
                            effect.BuffIds,
                            effect.ActiveBuffIds,
                            trySendPacketAsync);
                    return;

                case SpecialDungeonEffectKind.BuffsCleared:
                    await DungeonBuffNotificationSender.ClearAsync(
                        session,
                        effect.BuffIds,
                        trySendPacketAsync);
                    return;

                case SpecialDungeonEffectKind.BossEntranceMinimap:
                    await SendMinimapAsync(
                        session,
                        effect,
                        trySendPacketAsync);
                    return;

                case SpecialDungeonEffectKind.PassGate:
                    await DungeonMechanismNotificationSender
                        .SendCompleteConditionPassGateAsync(
                            session,
                            "ordinary-special-dungeon",
                            effect.Reason,
                            trySendPacketAsync == null
                                ? null
                                : (packet, _, _) =>
                                    trySendPacketAsync(packet));
                    return;

                case SpecialDungeonEffectKind.StrongWarlordSelected:
                    await SendPacketAsync(
                        session,
                        GamePacketEnvelopeBuilder.Build(
                            0x01,
                            (ushort)CmdPacketType.TIMER_MODIFY_INFO,
                            new[] { StrongWarlordResult }),
                        trySendPacketAsync);
                    return;

                case SpecialDungeonEffectKind.SummonMonsterResponse:
                    await SendSummonMonsterAsync(
                        session,
                        effect,
                        trySendPacketAsync);
                    return;

                case SpecialDungeonEffectKind.CommandSuccessAck:
                    await SendPacketAsync(
                        session,
                        GamePacketEnvelopeBuilder.Build(
                            0x01,
                            effect.WireType,
                            CommonPacketBodyBuilder.BuildSuccessAck()),
                        trySendPacketAsync);
                    return;
            }
        }

        private static Task SendGaugeAsync(
            EnhancedClientSession session,
            int value,
            Func<byte[], Task<bool>> trySendPacketAsync)
        {
            var body = SpecialDungeonNotificationBuilder
                .BuildGaugeObjectBarData(value);
            return SendPacketAsync(
                session,
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketType.GAUGE_OBJECT_BAR_DATA,
                    body),
                trySendPacketAsync);
        }

        private static Task SendMinimapAsync(
            EnhancedClientSession session,
            SpecialDungeonEffectIntent effect,
            Func<byte[], Task<bool>> trySendPacketAsync)
        {
            var body = SpecialDungeonNotificationBuilder.BuildMinimapIconInfo(
                effect.MinimapEntries);
            return SendPacketAsync(
                session,
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketType.MINIMAP_ICON_INFO,
                    body),
                trySendPacketAsync);
        }

        private static Task SendSummonMonsterAsync(
            EnhancedClientSession session,
            SpecialDungeonEffectIntent effect,
            Func<byte[], Task<bool>> trySendPacketAsync)
        {
            var body = SpecialDungeonNotificationBuilder
                .BuildSummonMonsterCommandCreateResponse(
                    SummonMonsterResult,
                    effect.StateId,
                    1,
                    SpecialDungeonMechanismApplicationService
                        .BossSummonRuntimeKey,
                    effect.MonsterCode,
                    SummonMonsterMode,
                    effect.MonsterLevel);
            return SendPacketAsync(
                session,
                GamePacketEnvelopeBuilder.Build(
                    0x01,
                    (ushort)CmdPacketType.SUMMON_MONSTER,
                    body),
                trySendPacketAsync);
        }

        private static async Task SendPacketAsync(
            EnhancedClientSession session,
            byte[] packet,
            Func<byte[], Task<bool>> trySendPacketAsync)
        {
            if (trySendPacketAsync != null)
            {
                await trySendPacketAsync(packet);
                return;
            }

            await session.SendPacketAsync(packet);
        }
    }
}
