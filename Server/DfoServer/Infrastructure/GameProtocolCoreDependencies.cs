using DfoServer.Game.Accounts;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Events;
using DfoServer.Game.Inventory;
using DfoServer.Game.Characters;
using DfoServer.Game.SelectCharacter;
using DfoServer.Network.Builders;
using System;

namespace DfoServer.Infrastructure
{
    // 组合根传给 GameProtocolHandler 的首批共享依赖。
    // 只承载已迁出的账号/角色/选角核心，不在这里注册命令或处理协议。
    internal sealed class GameProtocolCoreDependencies
    {
        internal GameProtocolCoreDependencies(
            IGameDatabase database,
            SqliteAccountRepository accountRepository,
            SqliteCharacterRepository characterRepository,
            IRentalTimeProvider rentalTimeProvider,
            DailyResetService dailyResetService,
            DungeonPersistentEffectApplicationService dungeonPersistentEffects,
            ExperienceItemUseService experienceItemUseService,
            SqliteSelectCharacterDataSource selectCharacterDataSource,
            GetUserInfoTemplate getUserInfoTemplate,
            EventManager eventManager)
        {
            Database = database ?? throw new ArgumentNullException(nameof(database));
            AccountRepository = accountRepository ?? throw new ArgumentNullException(nameof(accountRepository));
            CharacterRepository = characterRepository ?? throw new ArgumentNullException(nameof(characterRepository));
            RentalTimeProvider = rentalTimeProvider ?? throw new ArgumentNullException(nameof(rentalTimeProvider));
            DailyResetService = dailyResetService ?? throw new ArgumentNullException(nameof(dailyResetService));
            DungeonPersistentEffects = dungeonPersistentEffects ?? throw new ArgumentNullException(nameof(dungeonPersistentEffects));
            ExperienceItemUseService = experienceItemUseService ?? throw new ArgumentNullException(nameof(experienceItemUseService));
            SelectCharacterDataSource = selectCharacterDataSource ?? throw new ArgumentNullException(nameof(selectCharacterDataSource));
            GetUserInfoTemplate = getUserInfoTemplate;
            EventManager = eventManager ?? throw new ArgumentNullException(nameof(eventManager));
        }

        internal IGameDatabase Database { get; }

        internal SqliteAccountRepository AccountRepository { get; }

        internal SqliteCharacterRepository CharacterRepository { get; }

        internal IRentalTimeProvider RentalTimeProvider { get; }

        internal DailyResetService DailyResetService { get; }

        internal DungeonPersistentEffectApplicationService DungeonPersistentEffects { get; }

        internal ExperienceItemUseService ExperienceItemUseService { get; }

        internal SqliteSelectCharacterDataSource SelectCharacterDataSource { get; }

        internal GetUserInfoTemplate GetUserInfoTemplate { get; }

        internal EventManager EventManager { get; }
    }
}
