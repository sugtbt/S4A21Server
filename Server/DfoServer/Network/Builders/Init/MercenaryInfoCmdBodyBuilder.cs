using DfoServer.Game.Characters;
using DfoServer.Game.Mercenary;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using System;

namespace DfoServer.Network.Builders
{
    // 进号以 cmd=1 / MERCENARY_INFO 推送佣兵列表。
    public sealed class MercenaryInfoCmdBodyBuilder : IInitCmdPacketBuilder
    {
        public ushort CmdType => (ushort)CmdPacketTypeA21.MERCENARY_INFO;

        private readonly MercenaryService _service;

        public MercenaryInfoCmdBodyBuilder()
            : this(GameDatabase.CreateDefault())
        {
        }

        public MercenaryInfoCmdBodyBuilder(IGameDatabase database)
            : this(database, service: null)
        {
        }

        public MercenaryInfoCmdBodyBuilder(IGameDatabase database, MercenaryService service)
        {
            database = database ?? throw new ArgumentNullException(nameof(database));
            _service = service ?? new MercenaryService(
                new MercenaryRepository(database),
                new SqliteCharacterRepository(database),
                new MercenaryAvatarBonusTierProvider(database));
        }

        public bool TryBuild(SelectCharacterDataSnapshot snapshot, out byte[] body)
        {
            var accountId = snapshot?.CharacterRecord?.AccountId ?? 0;
            var info = accountId > 0
                ? _service.GetInfo(accountId)
                : new MercenaryInfoSnapshot();
            body = MercenaryExpeditionBodyBuilder.BuildInfoSuccess(info);
            return true;
        }
    }
}
