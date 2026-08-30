using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Settings;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Auction;
using DfoServer.Network.Parsers;
using System;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers
{
    public sealed class LoginHandler
    {
        private const string DefaultLoginMid = "10038";

        private readonly IAccountRepository _accountRepository;
        private readonly ICharacterRepository _characterRepository;
        private readonly AccountSettingsRepository _settingsRepository;
        private readonly HonorLevelSyncService _honorLevel;

        public string ProtocolName => "GameProtocol";

        public LoginHandler(
            IAccountRepository accountRepository,
            ICharacterRepository characterRepository,
            IGameDatabase database = null)
        {
            _accountRepository = accountRepository ?? throw new ArgumentNullException(nameof(accountRepository));
            _characterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
            database ??= GameDatabase.CreateDefault();
            _honorLevel = new HonorLevelSyncService(_characterRepository, database);
            _settingsRepository = new AccountSettingsRepository(database);
        }

        public async Task Handle_ClientFirstConnected(EnhancedClientSession session)
        {
            if (!EnsureListenerAdmission(session, "connect"))
                return;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x0001,
                LoginPacketBuilder.BuildInitialLoginNotice(
                    session.ListenerPort)));
        }

        public async Task Handle_ENUM_CMDPACKET_CHECK_USER_CONNECTION(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            FileLogger.Log(
                $"[{ProtocolName}] CHECK_USER_CONNECTION handshake body={body?.Length ?? 0}");
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x04DD,
                CommonPacketBodyBuilder.BuildSuccessAck()));
        }

        public async Task Handle_ENUM_CMDPACKET_LOGIN(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!EnsureListenerAdmission(session, "login"))
                return;

            try
            {
                var mId = DefaultLoginMid;
                var passwordHash = string.Empty;
                if (LoginRequestParser.TryParse(body, out var parsed))
                {
                    mId = parsed.MId;
                    passwordHash = parsed.PasswordHash ?? string.Empty;
                    FileLogger.Log($"[{ProtocolName}] Login request parsed: m_id={mId} pwd_md5={passwordHash}");
                }
                else
                {
                    FileLogger.Log($"[{ProtocolName}] Login body unparseable, falling back to m_id={DefaultLoginMid}");
                }

                var account = _accountRepository.GetByMid(mId);
                if (account == null)
                {
                    var newId = _accountRepository.Create(mId, passwordHash);
                    account = _accountRepository.GetById(newId);
                    FileLogger.Log($"[{ProtocolName}] Login auto-created account id={newId} m_id={mId}");
                }
                session.Account = account;
                var remoteIp = session.TcpClient?.Client?.RemoteEndPoint?.ToString() ?? string.Empty;
                _accountRepository.UpdateLastLogin(account.AccountId, remoteIp, DateTime.UtcNow);
                FileLogger.Log($"[{ProtocolName}] Login bound session {session.SessionId} -> account_id={account.AccountId} m_id={account.MId}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{ProtocolName}] Login account lookup failed: {ex.Message}");
                return;
            }

            var optionSent = await SendSelectScreenGameOptionAsync(session);
            if (!optionSent)
            {
                // 无已保存设置时不下发 00AD；抓包证据显示客户端的 2x00C5 上行由 00AD 触发，
                // 未下发时客户端不会上行，直接进入选角。
                await SendLoginSuccessAsync(session);
                FileLogger.Log(
                    $"[{ProtocolName}] LOGIN success immediately, no saved settings " +
                    $"account={session.Account?.AccountId}");
                return;
            }

            session.A21LoginSuccessPending = true;
            session.A21SelectOptionSaveCount = 0;
            FileLogger.Log(
                $"[{ProtocolName}] LOGIN 00AD sent, waiting for 2x 00C5 " +
                $"account={session.Account?.AccountId}");
        }

        public static Task SendLoginSuccessAsync(EnhancedClientSession session)
        {
            session.A21LoginSuccessPending = false;
            return session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x01,
                0x0001,
                LoginPacketBuilder.BuildLoginSuccess(
                    session.ListenerPort)));
        }

        public static async Task TryCompletePendingLoginSuccessAsync(
            EnhancedClientSession session)
        {
            if (session == null || !session.A21LoginSuccessPending)
                return;

            session.A21SelectOptionSaveCount++;
            FileLogger.Log(
                $"[GameProtocol] 00C5 toward login success " +
                $"count={session.A21SelectOptionSaveCount}/2");
            if (session.A21SelectOptionSaveCount < 2)
                return;

            await SendLoginSuccessAsync(session);
            FileLogger.Log("[GameProtocol] LOGIN success after 2x 00C5");
        }

        internal static bool IsListenerAdmissionAllowed(
            int listenerPort,
            bool freeDuelChannelEnabled)
            => !GameNetworkConfig.IsFreeDuelListener(listenerPort)
               || freeDuelChannelEnabled;

        private bool EnsureListenerAdmission(
            EnhancedClientSession session,
            string stage)
        {
            if (IsListenerAdmissionAllowed(
                    session.ListenerPort,
                    GameNetworkConfig.FreeDuelListenerEnabled))
            {
                return true;
            }

            FileLogger.Log(
                $"[{ProtocolName}] FREE_DUEL REJECTED: " +
                $"stage={stage} listener={session.ListenerPort}");
            session.Close();
            return false;
        }

        private async Task<bool> SendSelectScreenGameOptionAsync(
            EnhancedClientSession session)
        {
            var accountId = session.Account?.AccountId ?? 0;
            if (accountId <= 0)
                return false;

            var settings = _settingsRepository.Load(accountId);
            var body = AccountSettingsPacketBuilder.BuildSelectScreenGameOption(
                settings,
                out var persistedMain);
            if (body == null)
            {
                FileLogger.Log(
                    $"[{ProtocolName}] LOGIN 00AD select-screen skipped " +
                    $"account={accountId} no saved settings");
                return false;
            }
            if (persistedMain != null)
                _settingsRepository.SaveMainOption(accountId, persistedMain);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                0x00,
                0x00AD,
                body));
            FileLogger.Log(
                $"[{ProtocolName}] LOGIN 00AD select-screen " +
                $"account={accountId} body={body.Length}B " +
                $"patchedFullAvatar={persistedMain != null}");
            return true;
        }
    }
}
