using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Pets;
using System;

namespace DfoServer.Infrastructure
{
    // 剩余单体业务协议入口模块。这里只保存组合根已装配的 Handler，
    // 不注册命令、不处理请求，也不另行创建数据库生命周期。
    internal sealed class GameProtocolFeatureHandlers
    {
        internal GameProtocolFeatureHandlers(
            LotteryItemHandler lotteryItem,
            PetCreatureHandler petCreature,
            SecretShopHandler secretShop,
            StaminaHandler stamina,
            SettingsHandler settings,
            CeraShopHandler ceraShop,
            SkillHandler skill,
            LuckyStarHandler luckyStar,
            RentalHandler rental,
            MercenaryExpeditionHandler mercenaryExpedition,
            MailboxHandler mailbox,
            CollectionBoxHandler collectionBox,
            ShopCoinEventHandler shopCoinEvent,
            MercenaryHandler mercenary,
            GrowthCapsuleHandler growthCapsule,
            GoldLimitHandler goldLimit,
            CraneMiniGameHandler craneMiniGame,
            EventJoustHandler eventJoust)
        {
            LotteryItem = lotteryItem
                ?? throw new ArgumentNullException(nameof(lotteryItem));
            PetCreature = petCreature
                ?? throw new ArgumentNullException(nameof(petCreature));
            SecretShop = secretShop
                ?? throw new ArgumentNullException(nameof(secretShop));
            Stamina = stamina ?? throw new ArgumentNullException(nameof(stamina));
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            CeraShop = ceraShop ?? throw new ArgumentNullException(nameof(ceraShop));
            Skill = skill ?? throw new ArgumentNullException(nameof(skill));
            LuckyStar = luckyStar
                ?? throw new ArgumentNullException(nameof(luckyStar));
            Rental = rental ?? throw new ArgumentNullException(nameof(rental));
            MercenaryExpedition = mercenaryExpedition
                ?? throw new ArgumentNullException(nameof(mercenaryExpedition));
            Mailbox = mailbox ?? throw new ArgumentNullException(nameof(mailbox));
            CollectionBox = collectionBox
                ?? throw new ArgumentNullException(nameof(collectionBox));
            ShopCoinEvent = shopCoinEvent
                ?? throw new ArgumentNullException(nameof(shopCoinEvent));
            Mercenary = mercenary
                ?? throw new ArgumentNullException(nameof(mercenary));
            GrowthCapsule = growthCapsule
                ?? throw new ArgumentNullException(nameof(growthCapsule));
            GoldLimit = goldLimit
                ?? throw new ArgumentNullException(nameof(goldLimit));
            CraneMiniGame = craneMiniGame
                ?? throw new ArgumentNullException(nameof(craneMiniGame));
            EventJoust = eventJoust
                ?? throw new ArgumentNullException(nameof(eventJoust));
        }

        internal LotteryItemHandler LotteryItem { get; }

        internal PetCreatureHandler PetCreature { get; }

        internal SecretShopHandler SecretShop { get; }

        internal StaminaHandler Stamina { get; }

        internal SettingsHandler Settings { get; }

        internal CeraShopHandler CeraShop { get; }

        internal SkillHandler Skill { get; }

        internal LuckyStarHandler LuckyStar { get; }

        internal RentalHandler Rental { get; }

        internal MercenaryExpeditionHandler MercenaryExpedition { get; }

        internal MailboxHandler Mailbox { get; }

        internal CollectionBoxHandler CollectionBox { get; }

        internal ShopCoinEventHandler ShopCoinEvent { get; }

        internal MercenaryHandler Mercenary { get; }

        internal GrowthCapsuleHandler GrowthCapsule { get; }

        internal GoldLimitHandler GoldLimit { get; }

        internal CraneMiniGameHandler CraneMiniGame { get; }

        internal EventJoustHandler EventJoust { get; }
    }
}
