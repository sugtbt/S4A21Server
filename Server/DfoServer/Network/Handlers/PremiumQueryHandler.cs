using DfoServer.Infrastructure;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    // 高级服务(契约等)查询: A21 CMD PREMIUM_SERVICE (0x036F)。
    // 与副本流程无关, 原先寄居在副本共享服务里, 拆出独立成域。
    public static class PremiumQueryHandler
    {
        private const string ProtocolLogName = "GameProtocol";

        public static async Task Handle_PREMIUM_SERVICE(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            await Handle_PREMIUM_SERVICE(
                session,
                header,
                body,
                GameDatabase.CreateDefault());
        }

        internal static async Task Handle_PREMIUM_SERVICE(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body,
            IGameDatabase database)
        {
            if (database == null)
                throw new ArgumentNullException(nameof(database));

            var aid = session?.Account?.AccountId ?? 0;
            var cid = session?.Player?.CharacterId ?? 0;
            FileLogger.Log($"[{ProtocolLogName}] CMD_PREMIUM_SERVICE: uid={session?.Player?.UserId ?? 0} cid={cid} aid={aid} body={BitConverter.ToString(body ?? Array.Empty<byte>())}");

            var connStr = database.ConnectionString;
            var dailyResetService = new Game.DailyReset.DailyResetService(database);
            var usage = new Game.Premium.DevilContractUsagePolicy(
                database,
                dailyResetService).BuildPremiumServiceUsage(cid);
            var serviceData = Game.Premium.PremiumService.BuildPremiumServiceData(
                connStr,
                aid,
                usage);

            var responseBody = Game.Premium.PremiumService.BuildPremiumServiceStateBody(
                Game.Premium.PremiumService.DefaultServiceType,
                serviceData);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.PREMIUM_SERVICE,
                responseBody));
            FileLogger.Log($"[{ProtocolLogName}] CMD_PREMIUM_SERVICE: responded with NOTI_PREMIUM_SERVICE character={cid} account={aid}");
        }
    }
}
