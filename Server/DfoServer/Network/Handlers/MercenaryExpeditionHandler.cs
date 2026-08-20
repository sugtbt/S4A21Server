using DfoServer.Game.Mercenary;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Mercenary;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class MercenaryExpeditionHandler
    {
        public static readonly ushort ReturnCommand = (ushort)CmdPacketTypeA21.MERCENARY_RETURN;
        public static readonly ushort InfoCommand = (ushort)CmdPacketTypeA21.MERCENARY_INFO;
        public static readonly ushort CompetitionCommand = (ushort)CmdPacketTypeA21.MERCENARY_COMPETITION;
        internal const byte CompetitionErrorCode = 21;

        private readonly MercenaryService _service;

        public MercenaryExpeditionHandler(MercenaryService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
        }

        public async Task HandleInfo(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (session?.Account == null)
            {
                FileLogger.Log($"[Mercenary] INFO rejected authenticated=false body={body?.Length ?? 0}");
                if (session != null)
                    await Send(session, InfoCommand, MercenaryExpeditionBodyBuilder.BuildError(1));
                return;
            }

            var snapshot = _service.GetInfo(session.Account.AccountId);
            await Send(session, InfoCommand, MercenaryExpeditionBodyBuilder.BuildInfoSuccess(snapshot));
            FileLogger.Log(
                $"[Mercenary] INFO account={session.Account.AccountId} level={snapshot.ManageLevel} " +
                $"point={snapshot.ManagePoint} records={snapshot.Records.Count}");
        }

        public async Task HandleReturn(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (session?.Account == null || !MercenaryCommandParser.TryParseReturn(body, out var command))
            {
                FileLogger.Log($"[Mercenary] RETURN rejected body={body?.Length ?? 0} authenticated={session?.Account != null}");
                if (session != null)
                    await Send(session, ReturnCommand, MercenaryExpeditionBodyBuilder.BuildError(7));
                return;
            }

            var result = _service.Return(session.Account.AccountId, command.CharacterId, command.Purpose);
            var response = result.Success
                ? MercenaryExpeditionBodyBuilder.BuildReturnSuccess(
                    command.CharacterId,
                    result.Reward?.ItemTemplateId ?? 0,
                    result.Reward?.ItemCount ?? 0,
                    (result.Reward?.CompletedHours ?? 0) > 0)
                : MercenaryExpeditionBodyBuilder.BuildError(7);
            await Send(session, ReturnCommand, response);
            if (!result.Success)
                FileLogger.Log($"[Mercenary] RETURN failed account={session.Account.AccountId} char={command.CharacterId} status={result.Status}");
        }

        public async Task HandleCompetition(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (session?.Account == null || !MercenaryCommandParser.TryParseCompetition(body, out var command))
            {
                FileLogger.Log($"[Mercenary] COMPETITION rejected body={body?.Length ?? 0} authenticated={session?.Account != null}");
                if (session != null)
                    await Send(session, CompetitionCommand, MercenaryExpeditionBodyBuilder.BuildError(CompetitionErrorCode));
                return;
            }

            var activeCharacterId = session.Player?.CharacterId ?? 0;
            var result = _service.Dispatch(
                session.Account.AccountId,
                activeCharacterId,
                command.CharacterId,
                command.AreaIndex,
                command.PeriodIndex);

            var response = result.Success
                ? MercenaryExpeditionBodyBuilder.BuildCompetitionSuccess(
                    result.Assignment.CharacterId,
                    result.Assignment.AreaIndex,
                    result.Assignment.PeriodIndex)
                : MercenaryExpeditionBodyBuilder.BuildError(CompetitionErrorCode);
            await Send(session, CompetitionCommand, response);
            if (!result.Success)
            {
                FileLogger.Log(
                    $"[Mercenary] COMPETITION failed account={session.Account.AccountId} active={activeCharacterId} " +
                    $"char={command.CharacterId} area={command.AreaIndex} period={command.PeriodIndex} status={result.Status}");
            }
        }

        private static Task Send(EnhancedClientSession session, ushort command, byte[] body)
            => session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, command, body));
    }
}
