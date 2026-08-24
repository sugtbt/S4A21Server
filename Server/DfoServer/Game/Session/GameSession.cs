using System.Threading.Tasks;
using DfoServer.Game.Characters;
using DfoServer.Game.Quests;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using DfoServer.Network;

namespace DfoServer.Game.Session
{
    public sealed class GameSession : ISessionPacketSender
    {
        private readonly EnhancedClientSession _networkSession;
        private readonly string _connStr;

        public QuestManager QuestManager { get; private set; }

        internal string ConnectionString => _connStr;

        public PlayerContext Player { get { return _networkSession.Player; } }
        public int CharacterId { get { return _networkSession.Player != null ? _networkSession.Player.CharacterId : 0; } }
        public int AccountId { get { return _networkSession.Account != null ? _networkSession.Account.AccountId : 1; } }

        public GameSession(EnhancedClientSession networkSession, string connStr)
            : this(
                networkSession,
                GameDatabase.AttachInitialized(connStr))
        {
        }

        internal GameSession(
            EnhancedClientSession networkSession,
            IGameDatabase database,
            ICharacterRepository characterRepository = null,
            SqliteSelectCharacterDataSource selectCharacterDataSource = null,
            ISessionDirectory sessionDirectory = null)
        {
            _networkSession = networkSession
                ?? throw new System.ArgumentNullException(nameof(networkSession));
            database = database
                ?? throw new System.ArgumentNullException(nameof(database));
            _connStr = database.ConnectionString;
            QuestManager = new QuestManager(
                this,
                database,
                characterRepository,
                selectCharacterDataSource,
                sessionDirectory);
        }

        public Task SendPacketAsync(byte[] rawPacket)
        {
            return _networkSession.SendPacketAsync(rawPacket);
        }

        public Task SendNotiAsync(ushort notiType, byte[] body)
        {
            return _networkSession.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(0x00, notiType, body));
        }

        public Task SendCmdAckAsync(ushort cmdType, byte[] body)
        {
            return _networkSession.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(0x01, cmdType, body));
        }

    }
}
