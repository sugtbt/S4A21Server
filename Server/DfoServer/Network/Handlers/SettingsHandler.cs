using System;
using System.Threading.Tasks;
using DfoServer.Game.Appearance;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Settings;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Handlers
{
    public sealed class SettingsHandler
    {
        private readonly AccountSettingsRepository _repo;
        private readonly ICharacterStateRepository _characterStateRepository;
        private readonly SqliteSubtype0FieldsRepository _subtype0FieldsRepository;
        private readonly CharacterVisibilitySettingsPersistence _visibilityPersistence;
        private readonly Game.Session.ISessionDirectory _sessions;
        private readonly IGameDatabase _database;

        public SettingsHandler(
            Game.Session.ISessionDirectory sessions = null,
            IGameDatabase database = null)
        {
            _database = database ?? GameDatabase.CreateDefault();
            _repo = new AccountSettingsRepository(_database);
            _characterStateRepository = new SqliteCharacterStateRepository(_database);
            _subtype0FieldsRepository = new SqliteSubtype0FieldsRepository(_database);
            _visibilityPersistence = new CharacterVisibilitySettingsPersistence(_database);
            _sessions = sessions;
        }

        public async Task Handle_SAVE_GAME_OPTION_1(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 4) return;
            int len = BitConverter.ToInt32(body, 0);
            if (len <= 0 || body.Length < 4 + len) return;

            var blob = new byte[len];
            Buffer.BlockCopy(body, 4, blob, 0, len);

            var (characterId, accountId) = SessionOwnerResolver.Resolve(session);

            var visibilityChanged = false;
            if (characterId > 0)
            {
                var tail = session?.Player?.Subtype0Tail ?? _subtype0FieldsRepository.Load(characterId);
                if (tail != null && AccountSettings.TryApplyCharacterVisibilityOptions(
                    blob,
                    tail.UserStateBits,
                    out var updatedBits))
                {
                    _visibilityPersistence.Save(accountId, characterId, blob, updatedBits);
                    visibilityChanged = updatedBits != tail.UserStateBits;
                    tail.UserStateBits = updatedBits;
                    if (visibilityChanged && session?.Player != null)
                    {
                        session.Player.Subtype0Tail = tail;
                        var packet = GamePacketEnvelopeBuilder.Build(
                            0x00,
                            (ushort)NotiPacketType.CHARAC_INVISIBLE_FALGS,
                            CharacterVisibilityBodyBuilder.Build(session.Player.UserId, updatedBits));
                        await session.SendPacketAsync(packet);
                        if (_sessions != null && session.Player.CurrentRun == null)
                        {
                            await _sessions.BroadcastToAreaAsync(
                                session.Player.CurTownId,
                                session.Player.CurAreaId,
                                characterId,
                                packet,
                                session.ListenerPort);
                        }
                    }
                }
                else
                {
                    _repo.SaveMainOption(accountId, blob);
                }
            }
            else
            {
                _repo.SaveMainOption(accountId, blob);
            }

            FileLogger.Log($"[GameProtocol] SAVE_GAME_OPTION_1: character={characterId} account={accountId} len={len} visibilityChanged={visibilityChanged}");
            await LoginHandler.TryCompletePendingLoginSuccessAsync(session);
        }

        public void Handle_SAVE_GAME_OPTION_2(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body == null || body.Length < 4) return;
            int len = BitConverter.ToInt32(body, 0);
            if (len <= 0 || body.Length < 4 + len) return;

            var blob = new byte[len];
            Buffer.BlockCopy(body, 4, blob, 0, len);

            var (characterId, accountId) = SessionOwnerResolver.Resolve(session);
            if (characterId > 0)
            {
                _characterStateRepository.SaveHotkeyConfig(characterId, blob);
                _repo.SaveHotkeySlots(accountId, AccountSettings.ExtractAccountScopedHotkeySlots(blob));
                FileLogger.Log($"[GameProtocol] SAVE_GAME_OPTION_2: character={characterId} account={accountId} len={len}");
                return;
            }

            _repo.SaveHotkeySlots(accountId, AccountSettings.ExtractAccountScopedHotkeySlots(blob));
            FileLogger.Log($"[GameProtocol] SAVE_GAME_OPTION_2: account={accountId} len={len}");
        }

        public void Handle_SAVE_QUICKCHAT(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            
            if (body == null || body.Length < 5) return;
            int bankIndex = body[0];
            if (bankIndex > 1) return;
            int len = BitConverter.ToInt32(body, 1);
            if (len <= 0 || body.Length < 5 + len) return;

            var blob = new byte[len];
            Buffer.BlockCopy(body, 5, blob, 0, len);

            int aid = session.Account?.AccountId ?? 1;
            _repo.SaveQuickchatBank(aid, bankIndex, blob);
            FileLogger.Log($"[GameProtocol] SAVE_QUICKCHAT: account={aid} bank={bankIndex} len={len}");
        }

        public async Task Handle_CHANGE_EMOTION(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var (characterId, accountId) = SessionOwnerResolver.Resolve(session);
            if (characterId <= 0 || body == null || body.Length < 2)
            {
                FileLogger.Log($"[GameProtocol] CHANGE_EMOTION ignored: character={characterId} account={accountId} len={body?.Length ?? 0}");
                return;
            }

            var moodValue = BitConverter.ToUInt16(body, 0);
            _characterStateRepository.SaveMoodValue(characterId, moodValue);

            if (session?.Player != null)
            {
                var tail = session.Player.Subtype0Tail ?? new Game.SelectCharacter.UserInfoMinimumTailSnapshot();
                tail.MoodValue = moodValue;
                session.Player.Subtype0Tail = tail;

                var notiBody = AppearanceService.BuildNoti2Body(
                    session.Player,
                    _database);
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0002, notiBody));
            }

            FileLogger.Log($"[GameProtocol] CHANGE_EMOTION: character={characterId} account={accountId} mood={moodValue}");
        }

        public void Handle_SAVE_CHARACTER_OPTION(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var (characterId, accountId) = SessionOwnerResolver.Resolve(session);
            if (characterId <= 0 || body == null)
            {
                FileLogger.Log($"[GameProtocol] SAVE_CHARACTER_OPTION ignored: character={characterId} account={accountId} len={body?.Length ?? 0}");
                return;
            }

            _characterStateRepository.SaveCharacterOption(characterId, body);
            FileLogger.Log($"[GameProtocol] SAVE_CHARACTER_OPTION: character={characterId} account={accountId} len={body.Length}");
        }
    }
}
