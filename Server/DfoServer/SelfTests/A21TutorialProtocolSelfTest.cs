using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.Game.TitleBook;
using DfoServer.GameWorld;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Dungeon;
using DfoServer.Network.Parsers.Dungeon;
using DfoServer.Network.Parsers.Inventory;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class A21TutorialProtocolSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== A21_TUTORIAL_PROTOCOL selftest ===");
            var failures = 0;

            Check(
                "A21 EXP/DIE_MONSTER/GET_ITEM opcodes are direction-specific",
                (ushort)NotiPacketTypeA21.EXP == 0x0025
                && (ushort)NotiPacketTypeA21.DIE_MONSTER == 0x0026
                && (ushort)NotiPacketTypeA21.GET_ITEM == 0x0027
                && (ushort)CmdPacketTypeA21.FINISH_LOADING == 0x0025
                && (ushort)CmdPacketTypeA21.DIE_MONSTER == 0x0027
                && (ushort)CmdPacketTypeA21.GET_ITEM == 0x002B,
                ref failures);

            Check(
                "A21 HELL_PARTY_MONSTER_INFO uses the enum-defined NOTI opcode",
                (ushort)NotiPacketTypeA21.HELL_PARTY_MONSTER_INFO == 0x02A7
                && DungeonNotificationBuilder.BuildHellPartyMonsterInfo(
                    new[] { new KeyValuePair<int, int>(125, 85) }).Length == 12,
                ref failures);

            var dungeonPermission = DungeonPermissionBodyBuilder.BuildEntries(
                new[]
                {
                    new DungeonPermissionEntrySnapshot
                    {
                        DungeonId = 0x0092,
                        ClearState = 1,
                    },
                    new DungeonPermissionEntrySnapshot
                    {
                        DungeonId = 0x07D9,
                        ClearState = 2,
                    },
                });
            Check(
                "A21 DUNGEON_PERMISSION uses u16 count and u32+u8 entries",
                (ushort)NotiPacketTypeA21.DUNGEON_PERMISSION == 0x0005
                && dungeonPermission.Length == 12
                && BitConverter.ToUInt16(dungeonPermission, 0) == 2
                && BitConverter.ToUInt32(dungeonPermission, 2) == 0x0092
                && dungeonPermission[6] == 1
                && BitConverter.ToUInt32(dungeonPermission, 7) == 0x07D9
                && dungeonPermission[11] == 2,
                ref failures);

            var levelUpTicketShortRequest = LevelUpTicketRequest.TryParse(
                new byte[] { 0x03, 0x00, 0x00 },
                out var parsedLevelUpTicket)
                && parsedLevelUpTicket.SlotIndex == 3
                && parsedLevelUpTicket.Reserved == 0;
            var levelUpTicketLongRequest = new byte[16];
            levelUpTicketLongRequest[0] = 0x03;
            var levelUpTicketLongRequestOk = LevelUpTicketRequest.TryParse(
                levelUpTicketLongRequest,
                out var parsedLongLevelUpTicket)
                && parsedLongLevelUpTicket.SlotIndex == 3;
            var levelUpTicketAckBody = LevelUpTicketAckBuilder.BuildSuccess();
            var levelUpTicketAckPacket = GamePacketEnvelopeBuilder.Build(
                0x01,
                (ushort)CmdPacketTypeA21.REQUEST_EVENT_SERVER_LEVEL_UP,
                levelUpTicketAckBody);
            var autoQuestClearRewardPacket = GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.EVENT_SERVER_AUTO_QUEST_CLEAR_REWARD_DATA,
                CommonPacketBodyBuilder.BuildZeroBytes(10));
            Check(
                "A21 REQUEST_EVENT_SERVER_LEVEL_UP parses slot and uses captured short ack",
                (ushort)CmdPacketTypeA21.REQUEST_EVENT_SERVER_LEVEL_UP == 0x01A2
                && (ushort)NotiPacketTypeA21.EVENT_SERVER_AUTO_QUEST_CLEAR_REWARD_DATA == 0x0169
                && levelUpTicketShortRequest
                && levelUpTicketLongRequestOk
                && levelUpTicketAckBody.Length == 2
                && levelUpTicketAckBody[0] == 0
                && levelUpTicketAckBody[1] == 0
                && levelUpTicketAckPacket.Length == 17
                && BitConverter.ToUInt16(levelUpTicketAckPacket, 1) == 0x01A2
                && BitConverter.ToInt32(levelUpTicketAckPacket, 3) == 17
                && autoQuestClearRewardPacket.Length == 25
                && BitConverter.ToUInt16(autoQuestClearRewardPacket, 1) == 0x0169
                && BitConverter.ToInt32(autoQuestClearRewardPacket, 3) == 25,
                ref failures);

            var titleBookCategory = new TitleBookCategorySnapshot
            {
                InfoType = 0,
                OwnerId16 = 0,
                Category = 0,
            };
            titleBookCategory.Entries.Add(new TitleBookListEntrySnapshot
            {
                SlotIndex = 0,
                ItemId = 26596,
                Value = 0x089B66ED,
                Attr = 1,
                Durability = 2,
                SealFlag = 3,
                EnchantIndex = 4,
                EnchantUpgradeCount = 5,
                AmplifyType = 6,
                AmplifyValue = 7,
            });
            var titleBook = TitleBookListBodyBuilder.BuildCategoryBody(titleBookCategory);
            Check(
                "A21 TITLE_BOOK_LIST uses 11B header and 26B entries",
                (ushort)NotiPacketTypeA21.TITLE_BOOK_LIST == 0x0166
                && titleBook.Length == 37
                && BitConverter.ToInt32(titleBook, 7) == 1
                && BitConverter.ToUInt16(titleBook, 11) == 0
                && BitConverter.ToInt32(titleBook, 13) == 26596
                && BitConverter.ToInt32(titleBook, 17) == 0x089B66ED
                && titleBook[21] == 1
                && BitConverter.ToUInt16(titleBook, 22) == 2
                && titleBook[24] == 3
                && BitConverter.ToInt32(titleBook, 25) == 4
                && titleBook[29] == 5
                && titleBook[30] == 6
                && BitConverter.ToUInt16(titleBook, 31) == 7
                && BitConverter.ToInt32(titleBook, 33) == 0,
                ref failures);

            var rentalInfo = new RentalInfoSnapshot();
            rentalInfo.Items.Add(new RentalItemSnapshot
            {
                ItemId = 891,
                InventoryTemplateId = 35004,
                ExpireTime = 200,
            });
            rentalInfo.Items.Add(new RentalItemSnapshot
            {
                ItemId = 892,
                InventoryTemplateId = 35005,
                ExpireTime = 99,
            });
            var rentalBody = RentalInfoBodyBuilder.BuildWireBody(
                luckyStar: 8,
                rental: rentalInfo,
                nowUnixSeconds: 100);
            Check(
                "A21 EQUIPMENT_RENTAL_LIST reads total/count and 8B active entries",
                (ushort)NotiPacketTypeA21.EQUIPMENT_RENTAL_LIST == 0x03C1
                && rentalBody.Length == 16
                && BitConverter.ToUInt32(rentalBody, 0) == 8
                && BitConverter.ToUInt32(rentalBody, 4) == 1
                && BitConverter.ToUInt32(rentalBody, 8) == 35004
                && BitConverter.ToUInt32(rentalBody, 12) == 200,
                ref failures);

            var settlementLuckyStar =
                LuckyStarClientNotifier.BuildChargeRentPointSuccessBody(
                    changeCount: 1,
                    totalLuckyStar: 9,
                    requestBody: null);
            Check(
                "A21 dungeon lucky-star ACK uses mode 2/success-flag-0/total",
                (ushort)CmdPacketTypeA21.CHARGE_RENTPOINT == 0x03D0
                && settlementLuckyStar.Length == 13
                && settlementLuckyStar[0] == 1
                && BitConverter.ToInt32(settlementLuckyStar, 1) == 2
                && BitConverter.ToInt32(settlementLuckyStar, 5) == 0
                && BitConverter.ToInt32(settlementLuckyStar, 9) == 9,
                ref failures);

            var chargeRequest = new byte[RentalCatalogCodec.ChargeRentPointRequestSize];
            Buffer.BlockCopy(
                BitConverter.GetBytes(1),
                0,
                chargeRequest,
                RentalCatalogCodec.ChargeRentPointModeOffset,
                4);
            Buffer.BlockCopy(
                BitConverter.GetBytes(3),
                0,
                chargeRequest,
                RentalCatalogCodec.ChargeRentPointQuantityOffset,
                4);
            var purchaseParsed = RentalCatalogCodec.TryParseShopPacketBuyCount(
                chargeRequest,
                out var purchaseCount);
            var purchaseLuckyStar =
                LuckyStarClientNotifier.BuildChargeRentPointSuccessBody(
                    changeCount: 3,
                    totalLuckyStar: 12,
                    requestBody: chargeRequest);
            Check(
                "A21 CHARGE_RENTPOINT request and ACK preserve mode/quantity",
                purchaseParsed
                && purchaseCount == 3
                && purchaseLuckyStar.Length == 13
                && purchaseLuckyStar[0] == 1
                && BitConverter.ToInt32(purchaseLuckyStar, 1) == 1
                && BitConverter.ToInt32(purchaseLuckyStar, 5) == 3
                && BitConverter.ToInt32(purchaseLuckyStar, 9) == 12,
                ref failures);

            var rentalCatalog = RentalWeaponInventoryMapper.ParseRentalCatalog(
                "[group]\n[package selection]\n401000037 3 401030032 3\n[/package selection]\n[/group]");
            Check(
                "A21 rental catalog parses item/star pairs from rentsysteminfo.etc",
                rentalCatalog.Count == 2
                && rentalCatalog[401000037] == 3
                && rentalCatalog[401030032] == 3,
                ref failures);

            var rentalRequest = new byte[]
            {
                0xAC, 0xF6, 0x19, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x70, 0xC9, 0xC1, 0x34, 0xB8, 0x3E, 0x65, 0xC6,
                0xE6, 0x17, 0x0B, 0x00, 0x00, 0x00,
            };
            var secondRentalRequest = new byte[]
            {
                0xAC, 0xF6, 0x19, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x70, 0xC9, 0xC1, 0x34, 0xB8, 0x3E, 0x90, 0x3B,
                0xE7, 0x17, 0x0B, 0x00, 0x00, 0x00,
            };
            Check(
                "A21 RENT_EQUIPMENT_ITEM reads both captured non-aligned item fields at offset 14",
                RentalHandler.CommandType == (ushort)CmdPacketTypeA21.RENT_EQUIPMENT_ITEM
                && RentalWeaponRequestCodec.TryParse(
                    rentalRequest,
                    out var rentalItem,
                    out var rentalContext,
                    out var rentalCost)
                && rentalItem == 401000037u
                && rentalContext == 11u
                && rentalCost == 3
                && RentalWeaponRequestCodec.TryParse(
                    secondRentalRequest,
                    out var secondRentalItem,
                    out var secondRentalContext,
                    out var secondRentalCost)
                && secondRentalItem == 401030032u
                && secondRentalContext == 11u
                && secondRentalCost == 3,
                ref failures);

            var rentalInventory = new InventoryService(1003, 1003);
            var rentalExpireTime = 2_000_000_000;
            var rentalMetadata = ItemMetadataResolver.Resolve(401000037);
            var rentalGrantOk = InventoryShopRuntimeService.TryRentWeapon(
                rentalInventory,
                401000037,
                rentalExpireTime,
                out var rentalGrant);
            var rentalCore = rentalGrantOk && rentalGrant != null
                ? rentalInventory.GetItem(rentalGrant.ListType, rentalGrant.SlotIndex)
                : null;
            Check(
                "A21 rental weapon normal-create metadata is available",
                rentalMetadata != null,
                ref failures);
            Check(
                "A21 rental weapon normal-create grant succeeds",
                rentalGrantOk
                && rentalGrant != null,
                ref failures);
            Check(
                "A21 rental weapon normal-create inserts the requested item",
                rentalCore != null
                && rentalCore.ItemId == 401000037,
                ref failures);
            Check(
                "A21 rental weapon normal-create uses PVF durability",
                rentalMetadata != null
                && rentalCore != null
                && rentalCore.Durability == rentalMetadata.Durability,
                ref failures);
            Check(
                "A21 rental weapon normal-create writes ItemCore expire",
                rentalCore != null
                && rentalCore.ExpireTime == rentalExpireTime,
                ref failures);

            var rentalSuccess = RentalWeaponPacketBuilder.BuildSuccessAck();
            var rentalFull = RentalWeaponPacketBuilder.BuildResultAck(RentalWeaponPacketBuilder.InventoryFullResult);
            Check(
                "A21 RENT_EQUIPMENT_ITEM ACK uses success plus u32 result",
                rentalSuccess.Length == 5
                && rentalSuccess[0] == 1
                && BitConverter.ToUInt32(rentalSuccess, 1) == 0
                && rentalFull.Length == 5
                && rentalFull[0] == 1
                && BitConverter.ToUInt32(rentalFull, 1) == 2,
                ref failures);

            var invalidRentalRequest = new byte[RentalWeaponRequestCodec.RequestBodySize];
            Buffer.BlockCopy(BitConverter.GetBytes(727014u), 0, invalidRentalRequest, RentalWeaponRequestCodec.ItemTemplateOffset, 4);
            Check(
                "A21 RENT_EQUIPMENT_ITEM rejects the old client-token interpretation",
                !RentalWeaponRequestCodec.TryParse(invalidRentalRequest, out _, out _, out _),
                ref failures);

            var initSequence = NewCharacterInitSequence.Build();
            Check(
                "A21 select-character init sends rental list without legacy lucky-star packets",
                initSequence.Exists(packet =>
                    packet.Command == 0
                    && packet.Type == (ushort)NotiPacketTypeA21.EQUIPMENT_RENTAL_LIST)
                && !initSequence.Exists(packet =>
                    packet.Command == 0 && packet.Type == 0x0357)
                && !initSequence.Exists(packet =>
                    packet.Command == 0 && packet.Type == 0x019D),
                ref failures);

            var enterFirst = EnterSelectDungeonStateBuilder
                .BuildA21EnterSelectDungeon(0x0439);
            Check(
                "A21 ENTER_SELECT_DUNGEON NOTI 27 is 37B without blocked slots",
                enterFirst.Length == 37
                && enterFirst[8] == 0
                && enterFirst[9] == 0
                && enterFirst[10] == 1
                && BitConverter.ToUInt16(enterFirst, 11) == 0x0439
                && enterFirst[18] == 1,
                ref failures);

            var enterLater = EnterSelectDungeonStateBuilder
                .BuildA21EnterSelectDungeon(
                    new ushort[] { 0x0439 },
                    new ushort[] { 0 });
            Check(
                "A21 ENTER_SELECT_DUNGEON NOTI 27 inserts blocked slots before users",
                enterLater.Length == 39
                && enterLater[8] == 1
                && BitConverter.ToUInt16(enterLater, 9) == 0
                && enterLater[11] == 0
                && enterLater[12] == 1
                && BitConverter.ToUInt16(enterLater, 13) == 0x0439
                && enterLater[20] == 1,
                ref failures);

            var info = DungeonNotificationBuilder.BuildDungeonInfo(
                144,
                difficulty: 0,
                mazeIndex: 1,
                bossX: 5,
                bossY: 0,
                hellPartyRoomX: 0xFF,
                hellPartyRoomY: 0xFF);
            var infoExpected = new byte[]
            {
                0x90, 0x00, 0x00, 0x00, 0x00, 0x01, 0x05, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
                0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            };
            Check(
                "A21 DUNGEON_INFO keeps maze index and boss coordinates",
                info.Length == 32
                && info.AsSpan().SequenceEqual(infoExpected),
                ref failures);

            var nonzeroDifficultyInfo =
                DungeonNotificationBuilder.BuildDungeonInfo(
                    160,
                    difficulty: 1,
                    mazeIndex: 1,
                    bossX: 4,
                    bossY: 0,
                    hellPartyRoomX: 0xFF,
                    hellPartyRoomY: 0xFF);
            Check(
                "A21 DUNGEON_INFO keeps u32 dungeon id before nonzero difficulty",
                nonzeroDifficultyInfo.Length == 32
                && BitConverter.ToInt32(nonzeroDifficultyInfo, 0) == 160
                && nonzeroDifficultyInfo[4] == 1
                && nonzeroDifficultyInfo[5] == 1
                && nonzeroDifficultyInfo[6] == 4
                && nonzeroDifficultyInfo[7] == 0,
                ref failures);

            var hellInfoMode1 = DungeonNotificationBuilder.BuildDungeonInfo(
                104,
                difficulty: 0,
                mazeIndex: 3,
                bossX: 1,
                bossY: 2,
                hellPartyRoomX: 2,
                hellPartyRoomY: 1,
                dungeonMode: 1,
                hellPartyEnabled: 1);
            var hellInfoMode2 = DungeonNotificationBuilder.BuildDungeonInfo(
                104,
                difficulty: 0,
                mazeIndex: 3,
                bossX: 1,
                bossY: 2,
                hellPartyRoomX: 2,
                hellPartyRoomY: 1,
                dungeonMode: 2,
                hellPartyEnabled: 1);
            var hellInfoSeason = DungeonNotificationBuilder.BuildDungeonInfo(
                104,
                difficulty: 0,
                mazeIndex: 0,
                bossX: 3,
                bossY: 1,
                hellPartyRoomX: 1,
                hellPartyRoomY: 0,
                hellPartyEnabled: 1);
            var hellInfoExpected = new byte[]
            {
                0x68, 0x00, 0x00, 0x00, 0x00, 0x03, 0x01, 0x02,
                0x02, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
                0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            };
            Check(
                "A21 DUNGEON_INFO projects the frozen Hell room coordinate",
                hellInfoMode1.Length == 32
                && hellInfoMode1.AsSpan().SequenceEqual(hellInfoExpected)
                && hellInfoMode2.AsSpan().SequenceEqual(hellInfoExpected),
                ref failures);
            Check(
                "A21 DUNGEON_INFO keeps a different Hell room coordinate independent of mode",
                hellInfoSeason.Length == 32
                && hellInfoSeason[8] == 1
                && hellInfoSeason[9] == 0
                && hellInfoSeason[12] == 1
                && hellInfoSeason[13] == 0
                && hellInfoSeason[14] == 0
                && hellInfoSeason[15] == 0
                && hellInfoSeason[17] == 0
                && hellInfoSeason[18] == 0xFF
                && hellInfoSeason[21] == 0xFF,
                ref failures);

            var trombeHellInfo = DungeonNotificationBuilder.BuildDungeonInfo(
                103,
                difficulty: 0,
                mazeIndex: 0,
                bossX: 2,
                bossY: 2,
                hellPartyRoomX: 3,
                hellPartyRoomY: 0,
                hellPartyEnabled: 1);
            Check(
                "A21 Trombe DUNGEON_INFO follows PVF Hell room (3,0)",
                trombeHellInfo.Length == 32
                && trombeHellInfo[8] == 3
                && trombeHellInfo[9] == 0,
                ref failures);

            var minimapInfo = DungeonNotificationBuilder.BuildDungeonInfo(
                104,
                difficulty: 0,
                mazeIndex: 0,
                bossX: 5,
                bossY: 1,
                extraPairGroups: new List<IReadOnlyList<(byte, byte)>>
                {
                    new List<(byte, byte)> { (2, 1), (4, 0) },
                    new List<(byte, byte)> { (5, 1) },
                });
            Check(
                "A21 DUNGEON_INFO serializes minimap groups without dropping coordinates",
                minimapInfo.Length == 40
                && minimapInfo[6] == 5
                && minimapInfo[7] == 1
                && minimapInfo[11] == 2
                && minimapInfo[12] == 2
                && minimapInfo[13] == 2
                && minimapInfo[14] == 1
                && minimapInfo[15] == 4
                && minimapInfo[16] == 0
                && minimapInfo[17] == 1
                && minimapInfo[18] == 5
                && minimapInfo[19] == 1,
                ref failures);
            Check(
                "A21 DUNGEON_INFO preserves the captured fixed value after groups",
                minimapInfo[20] == 1
                && minimapInfo[21] == 0
                && minimapInfo[25] == 0
                && minimapInfo[26] == 0xFF,
                ref failures);

            var normalizedMinimapGroups = DungeonMinimapProjectionService.Resolve(
                new List<IReadOnlyList<(byte, byte)>>
                {
                    new List<(byte, byte)> { (2, 1), (2, 1) },
                    Array.Empty<(byte, byte)>(),
                },
                null);
            Check(
                "minimap projection removes empty groups and duplicate coordinates",
                normalizedMinimapGroups != null
                && normalizedMinimapGroups.Count == 1
                && normalizedMinimapGroups[0].Count == 1
                && normalizedMinimapGroups[0][0] == (2, 1),
                ref failures);

            var grandine = Dungeon.GetDungeonFile(104);
            var grandineMaze = grandine?.Mazes != null
                && grandine.Mazes.Count > 0
                    ? grandine.Mazes[0]
                    : null;
            var grandineStartMap = grandineMaze == null
                ? -1
                : DungeonMapResolver.ResolveMapId(
                    104,
                    0,
                    0,
                    grandineMaze,
                    0,
                    grandineMaze.BossMap);
            Check(
                "explicit start MAP wins over an incompatible same-coordinate quest start",
                grandineMaze != null
                && DungeonMapResolver.TryGetMazeCellGreed(
                    grandineMaze,
                    0,
                    0,
                    out var grandineStartGreed)
                && DungeonMapResolver.TryDecodeGreedSymbol(
                    grandineStartGreed,
                    out var grandineStartMask)
                && grandineStartMask == 1
                && DungeonMapResolver.TryGetMapEntranceMask(
                    42001,
                    out var explicitStartMask)
                && explicitStartMask == grandineStartMask
                && DungeonMapResolver.TryGetMapEntranceMask(
                    15424,
                    out var questStartMask)
                && questStartMask == 8
                && grandineStartMap == 42001,
                ref failures);

            VerifyQuestMazeMapEntranceAffinity(ref failures);

            var maze = new Dungeon.MazeSumInfo
            {
                X = 0,
                Y = 1,
                Index = 70000,
                Monsters = new List<Dungeon.MonsterSumInfo>
                {
                    new Dungeon.MonsterSumInfo
                    {
                        TemplateOrder = 0,
                        PacketIndex = 1,
                        Code = 61670,
                        Level = 0,
                        Type = 1,
                    },
                    new Dungeon.MonsterSumInfo
                    {
                        TemplateOrder = 0,
                        PacketIndex = 0,
                        Code = 30122489,
                        Level = 0,
                        Type = 0x50,
                        Flag1 = 5,
                    },
                },
            };
            var start = DungeonNotificationBuilder.BuildStartMap(
                maze,
                firstMonsterSequence: 10002,
                randomSeed: 232968,
                hellPartyMode: 2,
                hellPartyFogFlag: 0);
            Check(
                "A21 START_MAP uses a u32 map id and 21B actors",
                start.Length == 65
                && BitConverter.ToInt32(start, 14) == 70000
                && start[7] == 2
                && start[18] == 2
                && start[39] == 0
                && start[61] == 0
                && start[64] == 0xFF,
                ref failures);
            Check(
                "A21 START_MAP keeps Hell/MAP overrides out of the layered-room branch",
                DungeonMapHandler.ResolveStartMapLayeredFlag(-1) == 0
                && DungeonMapHandler.ResolveStartMapLayeredFlag(0) == 1,
                ref failures);

            var revisit = DungeonNotificationBuilder.BuildStartMapRevisit(
                maze,
                seed: 232968);
            Check(
                "A21 START_MAP revisit keeps the complete zero-count tail",
                revisit.Length == 23
                && revisit[7] == 2
                && BitConverter.ToInt32(revisit, 14) == 70000
                && revisit[18] == 0
                && revisit[19] == 0
                && revisit[20] == 0
                && revisit[21] == 0
                && revisit[22] == 0xFF,
                ref failures);

            var townSnapshot = new TownUserSnapshot
            {
                UserId = 0x0439,
                TownId = 1,
                AreaId = 2,
                PosX = 0x0123,
                PosY = 0x0045,
                Direction = 5,
                State = 0,
            };
            var townPlayer = new PlayerContext
            {
                UserId = townSnapshot.UserId,
                UserState = townSnapshot.State,
            };
            var userState = EnterSelectDungeonStateBuilder.BuildUserState(townPlayer);
            Check(
                "A21 town return starts with USER_STATE body=4B",
                userState.Length == 4
                && userState[0] == 1
                && BitConverter.ToUInt16(userState, 1) == 0x0439
                && userState[3] == 0,
                ref failures);

            var userArea = TownAreaNotificationBuilder.BuildUserArea(townSnapshot);
            Check(
                "A21 USER_AREA is 10B with town/area before coordinates",
                userArea.Length == 10
                && BitConverter.ToUInt16(userArea, 0) == 0x0439
                && userArea[2] == 1
                && userArea[3] == 2
                && BitConverter.ToInt16(userArea, 4) == 0x0123
                && BitConverter.ToInt16(userArea, 6) == 0x0045
                && userArea[8] == 5
                && userArea[9] == 0,
                ref failures);

            var areaUsers = TownAreaNotificationBuilder.BuildAreaUsers(townSnapshot);
            Check(
                "A21 AREA_USERS is 12B with a uint16 count",
                areaUsers.Length == 12
                && areaUsers[0] == 1
                && areaUsers[1] == 2
                && BitConverter.ToUInt16(areaUsers, 2) == 1
                && BitConverter.ToUInt16(areaUsers, 4) == 0x0439
                && BitConverter.ToInt16(areaUsers, 6) == 0x0123
                && BitConverter.ToInt16(areaUsers, 8) == 0x0045
                && areaUsers[10] == 5
                && areaUsers[11] == 0,
                ref failures);

            var userPosition = TownAreaNotificationBuilder.BuildUserPosition(
                townSnapshot,
                motionState: 0x0064);
            Check(
                "A21 USER_POSITION is 9B with a uint16 motion state",
                userPosition.Length == 9
                && BitConverter.ToUInt16(userPosition, 0) == 0x0439
                && BitConverter.ToInt16(userPosition, 2) == 0x0123
                && BitConverter.ToInt16(userPosition, 4) == 0x0045
                && userPosition[6] == 5
                && BitConverter.ToUInt16(userPosition, 7) == 0x0064,
                ref failures);

            var pcRoomResponse = Network.Handlers.TownHandler
                .BuildGetPcRoomTimePointItemResponsePacket();
            Check(
                "A21 town return PC-room response is CMD 0x0279 with a 6B zero body",
                pcRoomResponse.Length == 21
                && pcRoomResponse[0] == 0x01
                && BitConverter.ToUInt16(pcRoomResponse, 1)
                    == (ushort)CmdPacketTypeA21.GET_PCROOM_TIME_POINT_ITEM
                && pcRoomResponse[15] == 0
                && pcRoomResponse[20] == 0,
                ref failures);

            var changeBody = new byte[15];
            changeBody[0] = 0;
            changeBody[1] = 0x1E;
            changeBody[5] = 1;
            Check(
                "A21 CHANGE_TUTORIAL_FLAG parses flag at offset 1",
                ChangeTutorialFlagRequest.TryParse(changeBody, out var change)
                && change.Mode == 0
                && change.FlagIndex == 30
                && change.RewardFlag == 1,
                ref failures);

            var compactChangeBody = new byte[]
            {
                0x00, 0x1E, 0x00, 0x00, 0x00, 0x01,
            };
            Check(
                "A21 live CHANGE_TUTORIAL_FLAG accepts compact 6B body",
                ChangeTutorialFlagRequest.TryParse(compactChangeBody, out var compactChange)
                && compactChange.Mode == 0
                && compactChange.FlagIndex == 30
                && compactChange.RewardFlag == 1,
                ref failures);

            Check(
                "A21 CHANGE_TUTORIAL_FLAG rejects body shorter than field prefix",
                !ChangeTutorialFlagRequest.TryParse(new byte[] { 0x00, 0x1E, 0x00, 0x00, 0x00 }, out _),
                ref failures);

            var selectBody = new byte[]
            {
                0x10, 0x27, 0x00, 0x00, 0x00, 0x00, 0x00,
                0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            };
            var select = SelectDungeonRequest.Parse(selectBody);
            Check(
                "A21 SELECT_DUNGEON keeps 15B body and zero hell flags",
                select.DungeonId == 10000
                && select.Difficulty == 0
                && select.HellPartyRequestFlag == 0
                && select.HellPartyDifficultyFlag == 0
                && select.A21Sentinel == 0xFFFF
                && select.TrailingLength == 6
                && !select.HasNonZeroTrailingBytes,
                ref failures);

            var nonzeroDifficultySelectBody = new byte[]
            {
                0xD9, 0x07, 0x00, 0x00, 0x01, 0x00, 0x00,
                0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            };
            var nonzeroDifficultySelect = SelectDungeonRequest.Parse(
                nonzeroDifficultySelectBody);
            Check(
                "A21 SELECT_DUNGEON reads difficulty at body offset +4",
                nonzeroDifficultySelect.DungeonId == 2009
                && nonzeroDifficultySelect.Difficulty == 1
                && nonzeroDifficultySelect.HellPartyRequestFlag == 0
                && nonzeroDifficultySelect.HellPartyDifficultyFlag == 0
                && nonzeroDifficultySelect.A21Sentinel == 0xFFFF,
                ref failures);

            var highDungeonIdSelectBody = new byte[]
            {
                0x70, 0x11, 0x01, 0x00, 0x03, 0x00, 0x00,
                0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            };
            var highDungeonIdSelect = SelectDungeonRequest.Parse(
                highDungeonIdSelectBody);
            Check(
                "A21 SELECT_DUNGEON preserves the high 16 bits of dungeon id",
                highDungeonIdSelect.DungeonId == 70000
                && highDungeonIdSelect.Difficulty == 3,
                ref failures);

            var hellSelectBody = new byte[]
            {
                0x68, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00,
                0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            };
            var hellSelect = SelectDungeonRequest.Parse(hellSelectBody);
            Check(
                "A21 SELECT_DUNGEON accepts the 16B hell-entry variant",
                hellSelect.DungeonId == 104
                && hellSelect.HellPartyRequestFlag == 1
                && hellSelect.TrailingLength == 7
                && !hellSelect.HasNonZeroTrailingBytes,
                ref failures);

            var enterSelect4 = EnterSelectDungeonRequest.TryParse(
                new byte[] { 0x68, 0x00, 0x00, 0x00 },
                out var enter4);
            var enterSelect7 = EnterSelectDungeonRequest.TryParse(
                new byte[] { 0x68, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
                out var enter7);
            var enterSelect8 = EnterSelectDungeonRequest.TryParse(
                new byte[] { 0x68, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 },
                out var enter8);
            Check(
                "A21 ENTER_SELECT_DUNGEON accepts 4B, 7B, and 8B bodies",
                enterSelect4
                && enterSelect7
                && enterSelect8
                && enter4.DungeonId == 104
                && enter4.TrailingLength == 0
                && enter7.TrailingLength == 3
                && enter8.TrailingLength == 4
                && !enter7.HasNonZeroTrailingBytes
                && !enter8.HasNonZeroTrailingBytes,
                ref failures);

            var circleEntryBody = new byte[CircleDungeonEntryRequest.BodySize];
            Buffer.BlockCopy(BitConverter.GetBytes((uint)147), 0, circleEntryBody, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes((uint)14880), 0, circleEntryBody, 4, 4);
            var circleParsed = CircleDungeonEntryRequest.TryParse(
                circleEntryBody,
                out var circleEntry);
            var circleDecision = CircleDungeonEntryPolicy.Evaluate(
                circleEntry.DungeonId,
                circleEntry.CircleQuestId);
            var circleResponse = CircleDungeonEntryResponseBuilder.BuildSuccess(
                circleDecision.CircleQuestId);
            Check(
                "REQUEST_CIRCLE_ENTER uses strict dungeonId/circleQuestId u32 pair",
                (ushort)CmdPacketTypeA21.REQUEST_CIRCLE_ENTER == 0x0308
                && circleParsed
                && circleEntry.DungeonId == 147
                && circleEntry.CircleQuestId == 14880
                && circleDecision.Allowed
                && circleResponse.Length == 9
                && circleResponse[0] == 1
                && BitConverter.ToUInt32(circleResponse, 1) == 1
                && BitConverter.ToUInt32(circleResponse, 5) == 14880
                && BitConverter.ToUInt16(circleResponse, 5) == 14880,
                ref failures);

            var observedCircleResponse =
                CircleDungeonEntryResponseBuilder.BuildSuccess(14877);
            Check(
                "REQUEST_CIRCLE_ENTER keeps the command result byte outside both u32 fields",
                observedCircleResponse.Length == 9
                && observedCircleResponse[0] == 1
                && observedCircleResponse[1] == 1
                && observedCircleResponse[2] == 0
                && observedCircleResponse[3] == 0
                && observedCircleResponse[4] == 0
                && observedCircleResponse[5] == 0x1D
                && observedCircleResponse[6] == 0x3A
                && observedCircleResponse[7] == 0
                && observedCircleResponse[8] == 0,
                ref failures);

            Check(
                "circle entry policy follows all PVF role branches without id routing",
                CircleDungeonEntryPolicy.Evaluate(147, 14880).Allowed
                && CircleDungeonEntryPolicy.Evaluate(147, 14881).Allowed
                && CircleDungeonEntryPolicy.Evaluate(147, 14882).Allowed,
                ref failures);

            var circleSelectionContext = new DungeonSelectionContext(
                selectionId: 1,
                runGeneration: 1,
                returnAnchor: default,
                isA21TutorialEntry: false);
            var circleContextBound = circleSelectionContext.TryBindCircleEntry(
                dungeonId: 146,
                circleQuestId: 14877);
            var circleContextConsumed = circleSelectionContext.TryConsumeCircleEntry(
                dungeonId: 146,
                out var selectedCircleQuestId);
            var circleMazeResolved = DfoServer.GameWorld.Dungeon.TrySelectActiveQuestMaze(
                dungeonId: 146,
                difficulty: 0,
                activeQuestId: selectedCircleQuestId,
                out var selectedCircleMaze);
            Check(
                "circle selection binds the requested PVF quest maze to one selection",
                circleContextBound
                && circleContextConsumed
                && selectedCircleQuestId == 14877
                && circleMazeResolved
                && selectedCircleMaze.Index == 4
                && selectedCircleMaze.Maze.QuestConnection != null
                && selectedCircleMaze.Maze.QuestConnection.Length >= 2
                && selectedCircleMaze.Maze.QuestConnection[1] == 14877
                && !circleSelectionContext.TryConsumeCircleEntry(
                    dungeonId: 146,
                    out _),
                ref failures);

            var iceCrystalStory = Dungeon.GetDungeonFile(145)?.StoryMode;
            var prisonStory = Dungeon.GetDungeonFile(149)?.StoryMode;
            var ardenStory = Dungeon.GetDungeonFile(93)?.StoryMode;
            var nonStoryWeightedDungeon = Dungeon.GetDungeonFile(1);
            Check(
                $"DGN story mode preserves quest lists and EXP-rate arrays " +
                $"iceQuests={iceCrystalStory?.QuestIds.Count ?? -1} " +
                $"iceExp={FormatInts(iceCrystalStory?.IncreaseExperienceRates)} " +
                $"prisonQuests={prisonStory?.QuestIds.Count ?? -1} " +
                $"ardenQuests={ardenStory?.QuestIds.Count ?? -1} " +
                $"ardenExp={FormatInts(ardenStory?.IncreaseExperienceRates)}",
                iceCrystalStory != null
                && iceCrystalStory.DifficultySize == 2
                && iceCrystalStory.IncreaseExperienceRates.Length == 2
                && iceCrystalStory.IncreaseExperienceRates[0] == 0
                && iceCrystalStory.IncreaseExperienceRates[1] == 0
                && iceCrystalStory.QuestIds.Contains(1779)
                && iceCrystalStory.QuestIds.Contains(1780)
                && prisonStory != null
                && prisonStory.QuestIds.Contains(1790)
                && prisonStory.QuestIds.Contains(1791)
                && prisonStory.QuestIds.Contains(1792)
                && ardenStory != null
                && ardenStory.IncreaseExperienceRates.Length == 2
                && ardenStory.IncreaseExperienceRates[0] == 0
                && ardenStory.IncreaseExperienceRates[1] == 30
                && ardenStory.QuestIds.Contains(2280)
                && ardenStory.QuestIds.Contains(2292)
                && nonStoryWeightedDungeon?.StoryMode == null
                && Math.Abs(
                    nonStoryWeightedDungeon.ExperienceIncreasingPoint - 1.3f)
                    < 0.0001f,
                ref failures);

            var independentStory = PvfLib.DungeonFile.Parse(
                "[story mode]\n" +
                "[difficulty size]\n2\n" +
                "[increase exp rate]\n0 35\n" +
                "[quest list]\n2269\n{7=` , `}2270\n" +
                "[independent rate]\n30 75 0 150 2274\n" +
                "[/independent rate]\n[/quest list]\n[/story mode]\n");
            Check(
                "DGN story mode preserves independent-rate quest references",
                independentStory.StoryMode != null
                && independentStory.StoryMode.QuestIds.Count == 3
                && independentStory.StoryMode.QuestIds[0] == 2269
                && independentStory.StoryMode.QuestIds[1] == 2270
                && independentStory.StoryMode.QuestIds[2] == 2274
                && independentStory.StoryMode.IndependentRates.Count == 1
                && independentStory.StoryMode.IndependentRates[0]
                    .ReferencedQuestId == 2274
                && independentStory.StoryMode.IndependentRates[0]
                    .Values.Length == 5,
                ref failures);

            Check(
                "randomized objects disambiguate colliding PVF template IDs",
                DungeonRandomizedObjectTemplateCatalog.ResolveSpawnMode(18865) == 1
                && DungeonRandomizedObjectTemplateCatalog.ResolveSpawnMode(58530) == 0
                && DungeonRandomizedObjectTemplateCatalog.ResolveSpawnMode(61235) == 0
                && DungeonRandomizedObjectTemplateCatalog.ResolveSpawnMode(69001) == 0
                && DungeonRandomizedObjectTemplateCatalog.ResolveSpawnMode(69002) == 0
                && DungeonRandomizedObjectTemplateCatalog.ResolveSpawnMode(69003) == 0,
                ref failures);
            var northGateObjects = DungeonRandomizedObjectSelectionService.Select(
                DungeonRandomizedObjectDefinitionProjector.Project(
                    Dungeon.GetDungeonDefaultMaze(88)),
                _ => 0);
            Check(
                "North Gate walker uses the ridable monster spawn path",
                northGateObjects.Count == 1
                && northGateObjects[0].ObjectIndex == 61235
                && northGateObjects[0].SpawnMode == 0,
                ref failures);

            var warroomExpParsed = DungeonExperienceDefinitionCatalog
                .TryNormalizeMonsterKindExperienceRates(
                    "171",
                    "344",
                    "515",
                    "687",
                    out var warroomExpRates);
            var warroomExpRateSource = warroomExpParsed
                ? "dgn-exp-const"
                : "invalid";
            var fallbackExpRates = DungeonExperienceDefinitionCatalog
                .ResolveMonsterKindExperienceRates(
                    new PvfLib.DungeonFile(),
                    out var fallbackExpRateSource);
            Check(
                "DGN monster exp constants normalize actor-kind rates",
                warroomExpParsed
                && warroomExpRateSource == "dgn-exp-const"
                && warroomExpRates.Length == 4
                && Math.Abs(warroomExpRates[0] - 1.0) < 0.0001
                && Math.Abs(warroomExpRates[1] - 2.0) < 0.0001
                && Math.Abs(warroomExpRates[2] - 3.0) < 0.0001
                && Math.Abs(warroomExpRates[3] - 4.0) < 0.0001
                && fallbackExpRateSource == "fallback-1-2-3-4"
                && fallbackExpRates.Length == 4
                && fallbackExpRates[0] == 1.0
                && fallbackExpRates[1] == 2.0
                && fallbackExpRates[2] == 3.0
                && fallbackExpRates[3] == 4.0,
                ref failures);

            var warroomDefinition = DungeonExperienceDefinitionCatalog.Resolve(2000);
            Check(
                "WarRoom PVF DGN exposes 3x super-champion experience",
                warroomDefinition != null
                && warroomDefinition.UsesStandardFormula
                && Math.Abs(warroomDefinition.GetMonsterKindRate(0) - 1.0) < 0.0001
                && Math.Abs(warroomDefinition.GetMonsterKindRate(1) - 2.0) < 0.0001
                && Math.Abs(warroomDefinition.GetMonsterKindRate(2) - 3.0) < 0.0001
                && Math.Abs(warroomDefinition.GetMonsterKindRate(3) - 4.0) < 0.0001,
                ref failures);

            var expDefinition = new DungeonExperienceDefinition(
                dungeonId: 9901,
                kind: DungeonExperienceDefinitionKind.Standard,
                standardLevel: 1,
                experienceWeight: 1.0,
                difficultyRates: new[] { 1.0, 2.0, 2.5 },
                partyMemberRates: new[] { 1.0 },
                monsterKindExperienceRates: warroomExpRates,
                legacyMonsterOverallRate: 1.0);
            var normalMonsterExperience = DungeonExperienceCalculator
                .CalculateStandardMonster(
                    expDefinition,
                    new DungeonMonsterExperienceContext(
                        characterLevel: 1,
                        monsterLevel: 1,
                        difficulty: 0,
                        monsterKind: 0,
                        isNamedMonster: false,
                        partyMemberCount: 1));
            var superChampionExperience = DungeonExperienceCalculator
                .CalculateStandardMonster(
                    expDefinition,
                    new DungeonMonsterExperienceContext(
                        characterLevel: 1,
                        monsterLevel: 1,
                        difficulty: 0,
                        monsterKind: 2,
                        isNamedMonster: false,
                        partyMemberCount: 1));
            var championExperience = DungeonExperienceCalculator
                .CalculateStandardMonster(
                    expDefinition,
                    new DungeonMonsterExperienceContext(
                        characterLevel: 1,
                        monsterLevel: 1,
                        difficulty: 0,
                        monsterKind: 1,
                        isNamedMonster: false,
                        partyMemberCount: 1));
            var bossExperience = DungeonExperienceCalculator
                .CalculateStandardMonster(
                    expDefinition,
                    new DungeonMonsterExperienceContext(
                        characterLevel: 1,
                        monsterLevel: 1,
                        difficulty: 0,
                        monsterKind: 3,
                        isNamedMonster: false,
                        partyMemberCount: 1));
            var namedNormalExperience = DungeonExperienceCalculator
                .CalculateStandardMonster(
                    expDefinition,
                    new DungeonMonsterExperienceContext(
                        characterLevel: 1,
                        monsterLevel: 1,
                        difficulty: 0,
                        monsterKind: 0,
                        isNamedMonster: true,
                        partyMemberCount: 1));
            var namedChampionExperience = DungeonExperienceCalculator
                .CalculateStandardMonster(
                    expDefinition,
                    new DungeonMonsterExperienceContext(
                        characterLevel: 1,
                        monsterLevel: 1,
                        difficulty: 0,
                        monsterKind: 1,
                        isNamedMonster: true,
                        partyMemberCount: 1));
            var namedDungeon = Dungeon.GetDungeonFile(5006);
            Check(
                "PVF champion and named monster categories remain independent",
                normalMonsterExperience.SharedBaseExperience > 0
                && championExperience.SharedBaseExperience
                    == normalMonsterExperience.SharedBaseExperience * 2
                && superChampionExperience.SharedBaseExperience
                    == normalMonsterExperience.SharedBaseExperience * 3
                && bossExperience.SharedBaseExperience
                    == normalMonsterExperience.SharedBaseExperience * 4
                && namedNormalExperience.SharedBaseExperience
                    == normalMonsterExperience.SharedBaseExperience * 3
                && namedChampionExperience.SharedBaseExperience
                    == normalMonsterExperience.SharedBaseExperience * 3
                && namedDungeon?.NamedMonster?.Contains(56349) == true
                && Dungeon.IsNamedMonster(5006, 56349),
                ref failures);

            var timeBreakPromotionMap =
                Dungeon.GetDungeonMapMonsterSummaryInformation(
                    2007,
                    5,
                    0,
                    mazeIndex: 0,
                    overrideMapId: 17091);
            Dungeon.PromoteChampions(
                timeBreakPromotionMap.Monsters,
                count: 10,
                dungeonId: 2007);
            Check(
                "special-dungeon mechanism actors stay out of random champion promotion",
                MonsterCaptureDefinitionCatalog
                    .IsChampionPromotionDisabled(61794)
                && !MonsterCaptureDefinitionCatalog
                    .IsChampionPromotionDisabled(56603)
                && IndependentDropDefinitionCatalog
                    .HasMonsterDefinition(56603)
                && IndependentDropDefinitionCatalog
                    .HasMonsterDefinition(61794)
                && !IndependentDropDefinitionCatalog
                    .HasMonsterDefinition(69235)
                && timeBreakPromotionMap.Monsters
                    .Where(monster => monster.Code == 56603
                        || monster.Code == 61794)
                    .All(monster => monster.Type == 0)
                && timeBreakPromotionMap.Monsters
                    .Where(monster => monster.Code == 69235)
                    .All(monster => monster.Type == 1),
                ref failures);

            var togPromotionMap =
                Dungeon.GetDungeonMapMonsterSummaryInformation(
                    225,
                    1,
                    0,
                    mazeIndex: 0,
                    overrideMapId: 32210);
            var medelPromotionMap =
                Dungeon.GetDungeonMapMonsterSummaryInformation(
                    231,
                    3,
                    0,
                    mazeIndex: 0,
                    overrideMapId: 32234);
            Dungeon.PromoteChampions(
                togPromotionMap.Monsters,
                count: 10,
                dungeonId: 225);
            Dungeon.PromoteChampions(
                medelPromotionMap.Monsters,
                count: 10,
                dungeonId: 231);
            Check(
                "sequential-dungeon mechanism monsters stay normal",
                SequentialDungeonMonsterCatalog.Contains(225, 56639)
                && SequentialDungeonMonsterCatalog.Contains(231, 56649)
                && togPromotionMap.Monsters.Count == 1
                && togPromotionMap.Monsters[0].Code == 56639
                && togPromotionMap.Monsters[0].Type == 0
                && medelPromotionMap.Monsters.Count == 1
                && medelPromotionMap.Monsters[0].Code == 56649
                && medelPromotionMap.Monsters[0].Type == 0,
                ref failures);

            var ordinaryPromotionMap =
                Dungeon.GetDungeonMapMonsterSummaryInformation(
                    145,
                    0,
                    1,
                    mazeIndex: 1,
                    overrideMapId: 55616);
            Dungeon.PromoteChampions(
                ordinaryPromotionMap.Monsters,
                count: 1,
                dungeonId: 145);
            Check(
                "ordinary repeated monsters remain eligible for random champion promotion",
                ordinaryPromotionMap.Monsters.Count == 4
                && ordinaryPromotionMap.Monsters
                    .Count(monster => monster.Code == 65311
                        && monster.Type == 1) == 1
                && ordinaryPromotionMap.Monsters
                    .Count(monster => monster.Code == 65311
                        && monster.Type == 0) == 3,
                ref failures);
            Check(
                "APC actor kinds share the monster experience normalization",
                DungeonExperienceCalculator.ResolveMonsterKind(5) == 0
                && DungeonExperienceCalculator.ResolveMonsterKind(6) == 1
                && DungeonExperienceCalculator.ResolveMonsterKind(8) == 3,
                ref failures);

            var storyBonusSnapshot =
                new DungeonParticipantExperienceBonusSnapshot(
                    partyMemberCount: 1,
                    partyHasEquippedAvatar: false,
                    hasEquippedCreature: false,
                    storyExperienceBonusRatePercent: 30,
                    storyExperienceDifficulty: 0);
            var nonStoryBonusSnapshot =
                DungeonParticipantExperienceBonusSnapshot.None;
            Check(
                "story dungeon rate joins kill and clear weight without shifting difficulty",
                DungeonExperienceCalculator.ResolveStoryExperienceWeightMultiplier(
                    storyBonusSnapshot) == 1.3
                && DungeonExperienceCalculator.ResolveStoryExperienceWeightMultiplier(
                    nonStoryBonusSnapshot) == 1.0
                && storyBonusSnapshot.ResolveExperienceDifficulty(0) == 0
                && nonStoryBonusSnapshot.ResolveExperienceDifficulty(0) == 0,
                ref failures);

            var prisonMazeResolved = Dungeon.TrySelectActiveQuestMaze(
                dungeonId: 149,
                difficulty: 0,
                activeQuestId: 1790,
                out var prisonMaze);
            var frozenActiveQuestMazeId = prisonMazeResolved
                ? Dungeon.ResolveActiveQuestMazeQuestId(
                    dungeonId: 149,
                    maze: prisonMaze.Maze,
                    activeQuestIds: new HashSet<int> { 1790 },
                    difficulty: 0)
                : 0;
            var frozenSelection = new DungeonSelectionSnapshot
            {
                MazeIndex = prisonMazeResolved ? prisonMaze.Index : -1,
                MazeQuestConnected = prisonMazeResolved,
                ActiveQuestMazeQuestId = frozenActiveQuestMazeId,
            };
            var participantRun = new DungeonRun();
            frozenSelection.ApplyTo(participantRun);
            Check(
                "dungeon selection freezes the exact active quest maze id",
                prisonMazeResolved
                && prisonMaze.Index == 1
                && frozenActiveQuestMazeId == 1790
                && Dungeon.ResolveActiveQuestMazeQuestId(
                    dungeonId: 149,
                    maze: prisonMaze.Maze,
                    activeQuestIds: new HashSet<int> { 1791 },
                    difficulty: 0) == 0
                && participantRun.ActiveQuestMazeQuestId == 1790,
                ref failures);

            Dungeon.TryGetSuitableLevelRange(
                dungeonId: 93,
                out var ardenSuitableMinLevel,
                out var ardenSuitableMaxLevel);
            var mainlineBonusRun = CreateQuestRun(
                dungeonId: 93,
                difficulty: 1,
                activeQuestMazeQuestId: 2280,
                snapshotQuestId: 2280);
            var mainlineBonus = DungeonStoryExperienceProfilePolicy.Capture(
                mainlineBonusRun);
            var mainlineReward = new QuestReward
            {
                Exp = 1000,
                Gold = 77,
                ChainType = 2,
                GrowNumber = 3,
                CreatureKind = 4,
                CreatureLevel = 5,
                Items = new List<QuestRewardItem>
                {
                    new QuestRewardItem { ItemId = 1234, Count = 2 },
                },
                ConsumeItems = new List<QuestRewardItem>
                {
                    new QuestRewardItem { ItemId = 5678, Count = 1 },
                },
            };
            Check(
                "story-mode rate does not modify quest completion EXP",
                mainlineBonus.IsEligible
                && mainlineBonus.RatePercent == 30
                && mainlineBonus.ExperienceDifficulty == 1
                && mainlineReward.Exp == 1000
                && mainlineReward.Gold == 77
                && mainlineReward.ChainType == 2
                && mainlineReward.GrowNumber == 3
                && mainlineReward.CreatureKind == 4
                && mainlineReward.CreatureLevel == 5
                && mainlineReward.Items.Count == 1
                && mainlineReward.Items[0].ItemId == 1234
                && mainlineReward.Items[0].Count == 2
                && mainlineReward.ConsumeItems.Count == 1
                && mainlineReward.ConsumeItems[0].ItemId == 5678,
                ref failures);

            var difficultyZeroBonus = DungeonStoryExperienceProfilePolicy.Capture(
                CreateQuestRun(93, 0, 2280, 2280));
            var nonStoryBonus = DungeonStoryExperienceProfilePolicy.Capture(
                CreateQuestRun(93, 1, 1790, 1790));
            var missingFrozenQuestBonus =
                DungeonStoryExperienceProfilePolicy.Capture(
                    CreateQuestRun(93, 1, 2280, 2292));
            Check(
                "story profile filters zero-rate, non-story and unfrozen runs",
                !difficultyZeroBonus.IsEligible
                && difficultyZeroBonus.IsStoryRun
                && difficultyZeroBonus.RatePercent == 0
                && difficultyZeroBonus.ExperienceDifficulty == 0
                && !nonStoryBonus.IsEligible
                && !nonStoryBonus.IsStoryRun
                && !missingFrozenQuestBonus.IsEligible
                && !missingFrozenQuestBonus.IsStoryRun,
                ref failures);

            var chessStoryRun = CreateQuestRun(
                dungeonId: 160,
                difficulty: 0,
                activeQuestMazeQuestId: 1842,
                snapshotQuestId: 1842);
            var chessStoryProfile = DungeonStoryExperienceProfilePolicy.Capture(
                chessStoryRun);
            var chessProfileFrozen = chessStoryRun
                    .TryFreezeExperienceBonusSnapshot(
                        DungeonParticipantExperienceBonusSnapshot.None)
                && chessStoryRun.TryFreezeStoryExperienceProfile(
                    chessStoryProfile.RatePercent,
                    chessStoryProfile.ExperienceDifficulty);
            var chessBonusSnapshot = chessStoryRun
                .CaptureExperienceBonusSnapshot();
            var chessDefinition = chessStoryRun.ExperienceDefinition;
            var chessStoryWeight = DungeonExperienceCalculator
                .ResolveStoryExperienceWeightMultiplier(chessBonusSnapshot);
            var chessWireNormal = DungeonExperienceCalculator
                .CalculateStandardMonster(
                    chessDefinition,
                    new DungeonMonsterExperienceContext(
                        characterLevel: 26,
                        monsterLevel: 28,
                        difficulty: 0,
                        monsterKind: 0,
                        isNamedMonster: false,
                        partyMemberCount: 1,
                        experienceWeightMultiplier: chessStoryWeight));
            var chessExperienceNormal = DungeonExperienceCalculator
                .CalculateStandardMonster(
                    chessDefinition,
                    new DungeonMonsterExperienceContext(
                        characterLevel: 26,
                        monsterLevel: 28,
                        difficulty: chessBonusSnapshot
                            .ResolveExperienceDifficulty(0),
                        monsterKind: 0,
                        isNamedMonster: false,
                        partyMemberCount: 1));
            var chessChampionContext = new DungeonMonsterExperienceContext(
                characterLevel: 26,
                monsterLevel: 28,
                difficulty: chessBonusSnapshot.ResolveExperienceDifficulty(0),
                monsterKind: 1,
                isNamedMonster: false,
                partyMemberCount: 1,
                experienceWeightMultiplier: chessStoryWeight);
            var chessChampion = DungeonExperienceCalculator
                .CalculateStandardMonster(
                    chessDefinition,
                    chessChampionContext);
            var chessSuperChampion = DungeonExperienceCalculator
                .CalculateStandardMonster(
                    chessDefinition,
                    new DungeonMonsterExperienceContext(
                        characterLevel: 26,
                        monsterLevel: 28,
                        difficulty: chessBonusSnapshot
                            .ResolveExperienceDifficulty(0),
                        monsterKind: 2,
                        isNamedMonster: false,
                        partyMemberCount: 1,
                        experienceWeightMultiplier: chessStoryWeight));
            var chessNamedContext = new DungeonMonsterExperienceContext(
                characterLevel: 26,
                monsterLevel: 28,
                difficulty: chessBonusSnapshot.ResolveExperienceDifficulty(0),
                monsterKind: 1,
                isNamedMonster: true,
                partyMemberCount: 1,
                experienceWeightMultiplier: chessStoryWeight);
            var chessNamed = DungeonExperienceCalculator
                .CalculateStandardMonster(
                    chessDefinition,
                    chessNamedContext);
            var chessNamedDisplayBonus = DungeonKillApplicationService
                .CalculateNamedMonsterDisplayBonus(
                    chessStoryRun,
                    chessNamedContext,
                    chessNamed.ParticipantBaseExperience,
                    chessNamed.ParticipantBaseExperience,
                    allowsExperience: true,
                    isNamedMonster: true);
            var chessChampionAwardBase = chessChampion.ParticipantBaseExperience;
            var chessLedger = new DungeonParticipantExperienceRuntime();
            chessLedger.RecordMonster(
                chessChampionAwardBase,
                growthContractBonusExperience: 0,
                isBoss: false,
                isChampion: true,
                isSuperChampion: false,
                isNamedMonster: false);
            var chessLedgerSnapshot = chessLedger.Capture();
            var chessNamedLedger = new DungeonParticipantExperienceRuntime();
            chessNamedLedger.RecordMonster(
                chessNamed.ParticipantBaseExperience,
                growthContractBonusExperience: 0,
                isBoss: false,
                isChampion: false,
                isSuperChampion: false,
                isNamedMonster: true);
            var chessNamedLedgerSnapshot = chessNamedLedger.Capture();
            var chessClearShort = DungeonExperienceCalculator
                .CalculateStandardClear(
                    chessDefinition,
                    new DungeonClearExperienceContext(
                        characterLevel: 26,
                        difficulty: chessBonusSnapshot
                            .ResolveExperienceDifficulty(0),
                        totalKilledMonsterCount: 1,
                        partyMemberCount: 1,
                        partyEventBonusRate: 0.0,
                        memberPenaltyRate: 1.0,
                        experienceWeightMultiplier: chessStoryWeight));
            var chessClearLong = DungeonExperienceCalculator
                .CalculateStandardClear(
                    chessDefinition,
                    new DungeonClearExperienceContext(
                        characterLevel: 90,
                        difficulty: chessBonusSnapshot
                            .ResolveExperienceDifficulty(0),
                        totalKilledMonsterCount: 99,
                        partyMemberCount: 4,
                        partyEventBonusRate: 0.5,
                        memberPenaltyRate: 0.05,
                        experienceWeightMultiplier: chessStoryWeight));
            var chessHardStorySnapshot =
                new DungeonParticipantExperienceBonusSnapshot(
                    partyMemberCount: 1,
                    partyHasEquippedAvatar: false,
                    hasEquippedCreature: false,
                    storyExperienceBonusRatePercent: 30,
                    storyExperienceDifficulty: 1);
            var chessHardStoryWeight = DungeonExperienceCalculator
                .ResolveStoryExperienceWeightMultiplier(
                    chessHardStorySnapshot);
            var chessHardStoryMonster = DungeonExperienceCalculator
                .CalculateStandardMonster(
                    chessDefinition,
                    new DungeonMonsterExperienceContext(
                        characterLevel: 26,
                        monsterLevel: 28,
                        difficulty: chessHardStorySnapshot
                            .ResolveExperienceDifficulty(0),
                        monsterKind: 0,
                        isNamedMonster: false,
                        partyMemberCount: 1,
                        experienceWeightMultiplier: chessHardStoryWeight));
            var chessHardStoryClear = DungeonExperienceCalculator
                .CalculateStandardClear(
                    chessDefinition,
                    new DungeonClearExperienceContext(
                        characterLevel: 26,
                        difficulty: chessHardStorySnapshot
                            .ResolveExperienceDifficulty(0),
                        totalKilledMonsterCount: 1,
                        partyMemberCount: 1,
                        partyEventBonusRate: 0.0,
                        memberPenaltyRate: 1.0,
                        experienceWeightMultiplier: chessHardStoryWeight));
            Check(
                "90-version kill, clear and story formulas share one frozen EXP owner",
                chessStoryProfile.IsStoryRun
                && chessStoryProfile.RatePercent == 0
                && chessStoryProfile.ExperienceDifficulty == 0
                && chessProfileFrozen
                && chessBonusSnapshot.ResolveExperienceDifficulty(0) == 0
                && chessWireNormal.ParticipantBaseExperience == 840
                && chessExperienceNormal.ParticipantBaseExperience == 840
                && chessChampion.ParticipantBaseExperience == 1680
                && chessNamed.ParticipantBaseExperience
                    == chessSuperChampion.ParticipantBaseExperience
                && chessNamedDisplayBonus
                    == chessNamed.ParticipantBaseExperience
                        - chessExperienceNormal.ParticipantBaseExperience
                && chessChampionAwardBase
                    == chessExperienceNormal.ParticipantBaseExperience * 2
                && chessSuperChampion.ParticipantBaseExperience
                    >= chessExperienceNormal.ParticipantBaseExperience * 3
                && chessSuperChampion.ParticipantBaseExperience
                    <= chessExperienceNormal.ParticipantBaseExperience * 3 + 1
                && chessClearShort.ParticipantBaseExperience == 66690
                && chessClearLong.ParticipantBaseExperience
                    == chessClearShort.ParticipantBaseExperience
                && chessHardStoryMonster.ParticipantBaseExperience == 1680
                && chessHardStoryClear.ParticipantBaseExperience == 133380
                && chessLedgerSnapshot.MonsterBaseExperience
                    == chessChampionAwardBase
                && chessLedgerSnapshot.ChampionBaseExperience
                    == chessChampionAwardBase
                && chessNamedLedgerSnapshot.MonsterBaseExperience
                    == chessNamed.ParticipantBaseExperience
                && chessNamedLedgerSnapshot.NamedMonsterBaseExperience
                    == chessNamed.ParticipantBaseExperience,
                ref failures);

            Check(
                "dungeon EXP penalty starts only after the standard level is exceeded",
                DungeonExperienceCalculator.GetLevelPenalty(10, 10) == 1.0
                && DungeonExperienceCalculator.GetLevelPenalty(9, 10) == 1.0
                && DungeonExperienceCalculator.GetLevelPenalty(13, 10) == 1.0
                && DungeonExperienceCalculator.GetLevelPenalty(14, 10) == 0.75
                && DungeonExperienceCalculator.GetLevelPenalty(16, 10) == 0.20
                && DungeonExperienceCalculator.GetLevelPenalty(17, 10) == 0.05,
                ref failures);

            var filteredReward = new QuestReward
            {
                Exp = 1000,
                Gold = 88,
                ChainType = 1,
                Items = new List<QuestRewardItem>(),
                ConsumeItems = new List<QuestRewardItem>(),
            };
            var overflowingReward = new QuestReward
            {
                Exp = uint.MaxValue,
                Items = new List<QuestRewardItem>(),
                ConsumeItems = new List<QuestRewardItem>(),
            };
            Check(
                "dungeon story profile does not modify quest completion rewards",
                filteredReward.Exp == 1000
                && filteredReward.Gold == 88
                && filteredReward.ChainType == 1
                && overflowingReward.Exp == uint.MaxValue,
                ref failures);

            Check(
                "REQUEST_CIRCLE_ENTER rejects non-exact body lengths",
                !CircleDungeonEntryRequest.TryParse(
                    new byte[CircleDungeonEntryRequest.BodySize - 1],
                    out _)
                && !CircleDungeonEntryRequest.TryParse(
                    new byte[CircleDungeonEntryRequest.BodySize + 1],
                    out _),
                ref failures);

            var rejectedCircleResponse =
                CircleDungeonEntryResponseBuilder.BuildRejected();
            Check(
                "circle entry policy rejects non-circle and forged dungeon pairs",
                !CircleDungeonEntryPolicy.Evaluate(147, 1793).Allowed
                && CircleDungeonEntryPolicy.Evaluate(147, 1793).RejectReason
                    == CircleDungeonEntryRejectReason.NotCircleQuest
                && !CircleDungeonEntryPolicy.Evaluate(146, 14880).Allowed
                && CircleDungeonEntryPolicy.Evaluate(146, 14880).RejectReason
                    == CircleDungeonEntryRejectReason.DungeonMismatch
                && !CircleDungeonEntryPolicy.Evaluate(147, 0x10000).Allowed
                && rejectedCircleResponse.Length == 1
                && rejectedCircleResponse[0] == 0,
                ref failures);

            var circleNoItemDefinitionResolved =
                QuestData.TryResolveCompletionDefinition(
                    14887,
                    out var circleNoItemDefinition,
                    out _);
            var circleNoItemReward = circleNoItemDefinitionResolved
                ? QuestRewardProjector.Resolve(
                    circleNoItemDefinition.RewardDefinition,
                    hasRewardSelection: false,
                    rewardSelectIdx: -1,
                    playerLevel: 17,
                    playerJob: 0,
                    playerGrowType: 0)
                : null;
            Check(
                "circle dungeon reward accepts the zero marker without a fake item",
                circleNoItemDefinitionResolved
                && circleNoItemDefinition.RewardDefinition.Kind
                    == QuestRewardKind.CircleDungeon
                && circleNoItemReward.IsValid
                && circleNoItemReward.Reward.ChainType == 0
                && circleNoItemReward.Reward.Exp > 0
                && circleNoItemReward.Reward.Gold > 0
                && circleNoItemReward.Reward.Items.Count == 0,
                ref failures);

            var circleItemDefinitionResolved =
                QuestData.TryResolveCompletionDefinition(
                    14886,
                    out var circleItemDefinition,
                    out _);
            var circleItemReward = circleItemDefinitionResolved
                ? QuestRewardProjector.Resolve(
                    circleItemDefinition.RewardDefinition,
                    hasRewardSelection: false,
                    rewardSelectIdx: -1,
                    playerLevel: 17,
                    playerJob: 0,
                    playerGrowType: 0)
                : null;
            Check(
                "circle dungeon reward projects the PVF fixed item pairs",
                circleItemDefinitionResolved
                && circleItemReward.IsValid
                && circleItemReward.Reward.Items.Count == 3
                && circleItemReward.Reward.Items[0].ItemId == 100310840
                && circleItemReward.Reward.Items[0].Count == 1
                && circleItemReward.Reward.Items[1].ItemId == 100310581
                && circleItemReward.Reward.Items[1].Count == 1
                && circleItemReward.Reward.Items[2].ItemId == 100320649
                && circleItemReward.Reward.Items[2].Count == 1,
                ref failures);

            var circleCompletionReward = circleItemReward != null
                ? QuestCompletionApplicationService.ApplyCompletionRewardPolicy(
                    circleItemReward.Reward,
                    QuestRewardKind.CircleDungeon)
                : QuestRewardProjector.CreateEmptyReward();
            Check(
                "circle completion keeps EXP but filters copied ordinary rewards",
                circleItemReward != null
                && circleCompletionReward.Exp == circleItemReward.Reward.Exp
                && circleCompletionReward.Gold == 0
                && circleCompletionReward.ChainType == 0
                && circleCompletionReward.GrowNumber == 0
                && circleCompletionReward.Items.Count == 0,
                ref failures);

            var ordinaryCompletionReward = circleItemReward != null
                ? QuestCompletionApplicationService.ApplyCompletionRewardPolicy(
                    circleItemReward.Reward,
                    QuestRewardKind.Item)
                : QuestRewardProjector.CreateEmptyReward();
            Check(
                "ordinary quest completion keeps its projected reward",
                circleItemReward != null
                && ordinaryCompletionReward.Gold
                    == circleItemReward.Reward.Gold
                && ordinaryCompletionReward.ChainType
                    == circleItemReward.Reward.ChainType
                && ordinaryCompletionReward.Items.Count
                    == circleItemReward.Reward.Items.Count,
                ref failures);

            var circleGrowDefinitionResolved =
                QuestData.TryResolveCompletionDefinition(
                    14889,
                    out var circleGrowDefinition,
                    out _);
            var circleGrowReward = circleGrowDefinitionResolved
                ? QuestRewardProjector.Resolve(
                    circleGrowDefinition.RewardDefinition,
                    hasRewardSelection: false,
                    rewardSelectIdx: -1,
                    playerLevel: 15,
                    playerJob: 0,
                    playerGrowType: 0)
                : null;
            Check(
                "circle reward parser preserves the copied grow-type payload",
                circleGrowDefinitionResolved
                && circleGrowDefinition.RewardDefinition.Kind
                    == QuestRewardKind.CircleDungeon
                && circleGrowReward.IsValid
                && circleGrowReward.Reward.ChainType == 1
                && circleGrowReward.Reward.GrowNumber == 1
                && circleGrowReward.Reward.Items.Count == 0,
                ref failures);

            var circleGrowCompletionReward = circleGrowReward != null
                ? QuestCompletionApplicationService.ApplyCompletionRewardPolicy(
                    circleGrowReward.Reward,
                    QuestRewardKind.CircleDungeon)
                : QuestRewardProjector.CreateEmptyReward();
            Check(
                "circle completion filters copied grow-type effects",
                circleGrowReward != null
                && circleGrowCompletionReward.Exp
                    == circleGrowReward.Reward.Exp
                && circleGrowCompletionReward.ChainType == 0
                && circleGrowCompletionReward.GrowNumber == 0,
                ref failures);

            var circleBoxRewards = QuestData
                .GetCircleDungeonWorldmapRewardItems(14878);
            var circleBoxOtherQuestRewards = QuestData
                .GetCircleDungeonWorldmapRewardItems(14883);
            var circleJobChangeRewards = QuestData
                .GetCircleDungeonWorldmapRewardItems(14889);
            Check(
                "circle dungeon independent reward maps quest to PVF worldmap box",
                circleBoxRewards.Count == 1
                && circleBoxRewards[0].ItemId == 10149695
                && circleBoxRewards[0].Count == 1
                && circleBoxOtherQuestRewards.Count == 1
                && circleBoxOtherQuestRewards[0].ItemId == 10149695
                && circleJobChangeRewards.Count == 0,
                ref failures);

            var penaltyTable = QuestParameterTable.Parse(
                "[green level penalty]\n80\n" +
                "[grey level penalty]\n30\n" +
                "[epic green level penalty]\n120\n" +
                "[epic grey level penalty]\n140\n");
            Check(
                "questParameter preserves ordinary and epic level penalties",
                penaltyTable.ComputeLevelPenalty(7, "normal") == 80
                && penaltyTable.ComputeLevelPenalty(12, "normal") == 30
                && penaltyTable.ComputeLevelPenalty(7, "epic") == 120
                && penaltyTable.ComputeLevelPenalty(12, "epic") == 140,
                ref failures);

            Check(
                "questParameter difficulty is an additive permille rate",
                QuestParameterTable.Parse(
                        "[difficulty]\n`1` 964\n" +
                        "[exp reward table]\n2403\n3856\n")
                    .ComputeExp(
                        playerLevel: 1,
                        rewardLevel: 1,
                        difficulty: '1',
                        questGrade: "epic",
                        ignoreLevel: false) == 4719,
                ref failures);

            var quest1786DefinitionResolved = QuestData.TryResolveRewardDefinition(
                1786,
                out var quest1786Definition,
                out _);
            var quest1787DefinitionResolved = QuestData.TryResolveRewardDefinition(
                1787,
                out var quest1787Definition,
                out _);
            var quest1789DefinitionResolved = QuestData.TryResolveRewardDefinition(
                1789,
                out var quest1789Definition,
                out _);
            var quest1786Reward = QuestData.ResolveReward(
                1786,
                playerLevel: 12,
                playerJob: 0,
                playerGrowType: 0);
            var quest1787Reward = QuestData.ResolveReward(
                1787,
                playerLevel: 13,
                playerJob: 0,
                playerGrowType: 0);
            var quest1789Reward = QuestData.ResolveReward(
                1789,
                playerLevel: 14,
                playerJob: 0,
                playerGrowType: 0);
            Check(
                "silvercrown epic rewards use DGN minimum level and permille difficulty",
                quest1786DefinitionResolved
                && quest1786Definition.RewardLevel == 11
                && quest1786Reward.IsValid
                && quest1786Reward.Reward.Exp == 4719
                && quest1787DefinitionResolved
                && quest1787Definition.RewardLevel == 13
                && quest1787Reward.IsValid
                && quest1787Reward.Reward.Exp == 7573
                && quest1789DefinitionResolved
                && quest1789Definition.RewardLevel == 13
                && quest1789Reward.IsValid
                && quest1789Reward.Reward.Gold == 554,
                ref failures);

            var acceptableQuestBody = QuestListBodyBuilder.BuildBody(
                level: 13,
                job: 0,
                growType: 0,
                clearedFlags: new Dictionary<int, int>());
            Check(
                "A21 ACCEPTABLE_QUEST_LIST starts with character level",
                acceptableQuestBody.Length >= 3
                && acceptableQuestBody[0] == 13
                && BitConverter.ToUInt16(acceptableQuestBody, 1)
                    == (acceptableQuestBody.Length - 3) / 2,
                ref failures);

            var capturedCircleRewardPairs = new[]
            {
                (OrdinaryQuestId: 1776, CircleQuestId: 14873, PlayerLevel: 5),
                (OrdinaryQuestId: 1777, CircleQuestId: 14874, PlayerLevel: 6),
                (OrdinaryQuestId: 1780, CircleQuestId: 14875, PlayerLevel: 9),
                (OrdinaryQuestId: 1779, CircleQuestId: 14876, PlayerLevel: 7),
                (OrdinaryQuestId: 1781, CircleQuestId: 14877, PlayerLevel: 10),
                (OrdinaryQuestId: 1782, CircleQuestId: 14878, PlayerLevel: 10),
                (OrdinaryQuestId: 1783, CircleQuestId: 14879, PlayerLevel: 11),
                (OrdinaryQuestId: 1784, CircleQuestId: 14880, PlayerLevel: 12),
                (OrdinaryQuestId: 1785, CircleQuestId: 14881, PlayerLevel: 12),
                (OrdinaryQuestId: 1786, CircleQuestId: 14882, PlayerLevel: 13),
                (OrdinaryQuestId: 1787, CircleQuestId: 14883, PlayerLevel: 13),
                (OrdinaryQuestId: 1788, CircleQuestId: 14884, PlayerLevel: 14),
                (OrdinaryQuestId: 1789, CircleQuestId: 14885, PlayerLevel: 14),
                (OrdinaryQuestId: 1790, CircleQuestId: 14886, PlayerLevel: 15),
                (OrdinaryQuestId: 1791, CircleQuestId: 14887, PlayerLevel: 16),
                (OrdinaryQuestId: 1792, CircleQuestId: 14888, PlayerLevel: 16),
            };
            var circleRewardPairsMatch = true;
            var firstCircleRewardPairMismatch = string.Empty;
            foreach (var pair in capturedCircleRewardPairs)
            {
                var ordinaryReward = QuestData.ResolveReward(
                    pair.OrdinaryQuestId,
                    rewardSelectIdx: -1,
                    playerLevel: pair.PlayerLevel,
                    playerJob: 0,
                    playerGrowType: 0);
                var circleReward = QuestData.ResolveReward(
                    pair.CircleQuestId,
                    rewardSelectIdx: -1,
                    playerLevel: pair.PlayerLevel,
                    playerJob: 0,
                    playerGrowType: 0);
                var worldmapRewards = QuestData
                    .GetCircleDungeonWorldmapRewardItems(pair.CircleQuestId);
                if (!ordinaryReward.IsValid
                    || !circleReward.IsValid
                    || ordinaryReward.Reward.Exp == 0
                    || circleReward.Reward.Exp == 0
                    || !QuestRewardsMatchNonExperience(
                        ordinaryReward.Reward,
                        circleReward.Reward)
                    || worldmapRewards.Count != 1
                    || worldmapRewards[0].ItemId != 10149695
                    || worldmapRewards[0].Count != 1)
                {
                    circleRewardPairsMatch = false;
                    firstCircleRewardPairMismatch =
                        $"ordinary={pair.OrdinaryQuestId} " +
                        $"circle={pair.CircleQuestId} " +
                        $"ordinaryValid={ordinaryReward.IsValid} " +
                        $"circleValid={circleReward.IsValid} " +
                        $"ordinaryExp={ordinaryReward.Reward.Exp} " +
                        $"circleExp={circleReward.Reward.Exp} " +
                        $"worldmapCount={worldmapRewards.Count}";
                    break;
                }
            }
            Check(
                "circle QST resource mirrors ordinary reward before delivery filtering " +
                firstCircleRewardPairMismatch,
                circleRewardPairsMatch,
                ref failures);

            var level15Mainline = QuestRelationIndex.ComputeAcceptableQuests(
                characterLevel: 15,
                characterJob: 11,
                growType: 4,
                clearedQuestIds: new HashSet<int> { 1789 },
                clearedFlags: new Dictionary<int, int> { [1789] = 1 },
                allowedCreatureKinds: new HashSet<int>());
            var level16Mainline = QuestRelationIndex.ComputeAcceptableQuests(
                characterLevel: 16,
                characterJob: 11,
                growType: 4,
                clearedQuestIds: new HashSet<int> { 1790, 1791, 1792 },
                clearedFlags: new Dictionary<int, int>
                {
                    [1790] = 1,
                    [1791] = 1,
                    [1792] = 1,
                },
                allowedCreatureKinds: new HashSet<int>());
            var level17Mainline = QuestRelationIndex.ComputeAcceptableQuests(
                characterLevel: 17,
                characterJob: 11,
                growType: 4,
                clearedQuestIds: new HashSet<int> { 1793 },
                clearedFlags: new Dictionary<int, int> { [1793] = 1 },
                allowedCreatureKinds: new HashSet<int>());
            Check(
                "level 15-17 epic quest availability has no catalog gap",
                level15Mainline.Contains(1790)
                && level16Mainline.Contains(1793)
                && level17Mainline.Contains(1796),
                ref failures);

            var demonicLancerChoice = QuestRelationIndex.ComputeAcceptableQuests(
                characterLevel: 15,
                characterJob: 13,
                growType: 0,
                clearedQuestIds: new HashSet<int>(),
                clearedFlags: new Dictionary<int, int>(),
                allowedCreatureKinds: new HashSet<int>());
            var demonicLancerTransfers = QuestRelationIndex
                .ComputeAcceptableQuests(
                    characterLevel: 15,
                    characterJob: 13,
                    growType: 0,
                    clearedQuestIds: new HashSet<int> { 13099 },
                    clearedFlags: new Dictionary<int, int> { [13099] = 1 },
                    allowedCreatureKinds: new HashSet<int>());
            var demonicLancerGiftAfterDuelist = QuestRelationIndex
                .ComputeAcceptableQuests(
                    characterLevel: 15,
                    characterJob: 13,
                    growType: 0,
                    clearedQuestIds: new HashSet<int> { 13099, 2633 },
                    clearedFlags: new Dictionary<int, int>
                    {
                        [13099] = 1,
                        [2633] = 1,
                    },
                    allowedCreatureKinds: new HashSet<int>());
            var demonicLancerGiftAfterVanguard = QuestRelationIndex
                .ComputeAcceptableQuests(
                    characterLevel: 15,
                    characterJob: 13,
                    growType: 0,
                    clearedQuestIds: new HashSet<int> { 13099, 2634 },
                    clearedFlags: new Dictionary<int, int>
                    {
                        [13099] = 1,
                        [2634] = 1,
                    },
                    allowedCreatureKinds: new HashSet<int>());
            var demonicLancerAfterTransfer = QuestRelationIndex
                .ComputeAcceptableQuests(
                    characterLevel: 15,
                    characterJob: 13,
                    growType: 1,
                    clearedQuestIds: new HashSet<int> { 13099, 2633 },
                    clearedFlags: new Dictionary<int, int>
                    {
                        [13099] = 1,
                        [2633] = 1,
                    },
                    allowedCreatureKinds: new HashSet<int>());
            var darkKnightChoice = QuestRelationIndex.ComputeAcceptableQuests(
                characterLevel: 15,
                characterJob: 9,
                growType: 0,
                clearedQuestIds: new HashSet<int>(),
                clearedFlags: new Dictionary<int, int>(),
                allowedCreatureKinds: new HashSet<int>());
            var creatorChoice = QuestRelationIndex.ComputeAcceptableQuests(
                characterLevel: 15,
                characterJob: 10,
                growType: 0,
                clearedQuestIds: new HashSet<int>(),
                clearedFlags: new Dictionary<int, int>(),
                allowedCreatureKinds: new HashSet<int>());
            Check(
                "PVF job identity exposes demonic-lancer choice and transfer quests " +
                "while external classes stay outside the ordinary transfer chain",
                demonicLancerChoice.Contains(13099)
                && demonicLancerTransfers.Contains(2633)
                && demonicLancerTransfers.Contains(2634)
                && demonicLancerGiftAfterDuelist.Contains(2637)
                && demonicLancerGiftAfterVanguard.Contains(2637)
                && !demonicLancerAfterTransfer.Contains(2634)
                && demonicLancerAfterTransfer.Contains(2637)
                && !darkKnightChoice.Contains(13099)
                && !creatorChoice.Contains(13099)
                && QuestRelationIndex.MeetsCharacterRestrictions(
                    2633,
                    characterLevel: 15,
                    characterJob: 13,
                    growType: 0)
                && QuestRelationIndex.MeetsCharacterRestrictions(
                    2637,
                    characterLevel: 15,
                    characterJob: 13,
                    growType: 0)
                && !QuestRelationIndex.MeetsCharacterRestrictions(
                    7803,
                    characterLevel: 15,
                    characterJob: 9,
                    growType: 0)
                && !QuestRelationIndex.MeetsCharacterRestrictions(
                    4065,
                    characterLevel: 15,
                    characterJob: 10,
                    growType: 0),
                ref failures);

            var firstAwakeningSample = QuestCatalog.OrderedIds
                .Select(questId => new
                {
                    QuestId = questId,
                    Quest = QuestData.GetQuestFile(questId),
                })
                .FirstOrDefault(entry => entry.Quest != null
                    && entry.Quest.JobChangeQuestValue == 2
                    && entry.Quest.GrowType >= 0
                    && entry.Quest.Job == "[fighter]");
            var secondAwakeningSample = QuestCatalog.OrderedIds
                .Select(questId => new
                {
                    QuestId = questId,
                    Quest = QuestData.GetQuestFile(questId),
                })
                .FirstOrDefault(entry => entry.Quest != null
                    && entry.Quest.JobChangeQuestValue == 3
                    && entry.Quest.GrowType >= 0
                    && entry.Quest.Job == "[swordman]");
            Check(
                "PVF awakening quest stages follow persisted grow-type high nibble",
                firstAwakeningSample != null
                && secondAwakeningSample != null
                && QuestRelationIndex.MeetsCharacterRestrictions(
                    firstAwakeningSample.QuestId,
                    characterLevel: 50,
                    characterJob: 1,
                    growType: firstAwakeningSample.Quest.GrowType)
                && !QuestRelationIndex.MeetsCharacterRestrictions(
                    firstAwakeningSample.QuestId,
                    characterLevel: 50,
                    characterJob: 1,
                    growType: firstAwakeningSample.Quest.GrowType | 0x10)
                && QuestRelationIndex.MeetsCharacterRestrictions(
                    secondAwakeningSample.QuestId,
                    characterLevel: 75,
                    characterJob: 0,
                    growType: secondAwakeningSample.Quest.GrowType | 0x10)
                && !QuestRelationIndex.MeetsCharacterRestrictions(
                    secondAwakeningSample.QuestId,
                    characterLevel: 75,
                    characterJob: 0,
                    growType: secondAwakeningSample.Quest.GrowType | 0x20),
                ref failures);

            var xilanQuest = QuestData.GetQuestFile(2404);
            var xilanMetadata = ItemMetadataResolver.Resolve(10100158);
            var xilanPrerequisite = QuestPrerequisiteCatalog.Get(2404);
            var xilanAvailable = QuestRelationIndex.ComputeAcceptableQuests(
                characterLevel: 70,
                characterJob: 0,
                growType: 0,
                clearedQuestIds: new HashSet<int> { 2403 },
                clearedFlags: new Dictionary<int, int> { [2403] = 1 },
                allowedCreatureKinds: new HashSet<int>());
            var xilanEventItems = QuestData.GetEventItems(2404);
            var xilanPreviousQuestRecovery = QuestGiveupItemRecoveryPolicy.Build(
                new List<ActiveQuest>
                {
                    new ActiveQuest { QuestId = 2402 },
                },
                abandonedQuestId: 2402);
            var xilanPreviousEventItemRecovery = xilanPreviousQuestRecovery
                .FirstOrDefault(entry => entry.ItemId == 10100142);
            var xilanRecoverySummary = string.Join(
                ",",
                xilanPreviousQuestRecovery.Select(
                    entry => entry.ItemId + ":" + entry.RetainCount));
            var existingXilanInventory = new InventoryService(1003, 1003);
            existingXilanInventory.SetItem(
                InventoryListType.Main,
                65,
                new ItemCore
                {
                    ItemKind = ItemCore.KindConsumable,
                    ItemId = 10100158,
                    Count = 1,
                });
            var existingXilanSlots = new List<ushort> { 0 };
            var existingXilanPending =
                QuestAcceptanceApplicationService.BuildMissingEventItemGrants(
                    existingXilanInventory,
                    new List<QuestRewardItem>
                    {
                        new QuestRewardItem
                        {
                            ItemId = 10100158,
                            Count = 1,
                        },
                    },
                    existingXilanSlots,
                    out var existingXilanRequests,
                    out _);
            Check(
                "PVF 西岚的箴言 answer prerequisite and event item remain available " +
                "recovery=" + xilanRecoverySummary,
                xilanQuest != null
                && QuestData.NormalizeQuestTag(xilanQuest.Type) == "use item"
                && xilanQuest.NpcIndex == 126
                && xilanQuest.Level != null
                && xilanQuest.Level.Length >= 2
                && xilanQuest.Level[0] == 70
                && xilanQuest.Level[1] == 99
                && xilanPrerequisite != null
                && xilanPrerequisite.IsValid
                && xilanPrerequisite.RequiredAnswers.Count == 1
                && xilanPrerequisite.RequiredAnswers[0].QuestId == 2403
                && xilanPrerequisite.RequiredAnswers[0].AnswerIndex == 0
                && xilanEventItems.Count == 1
                && xilanEventItems[0].ItemId == 10100158
                && xilanEventItems[0].Count == 1
                && xilanAvailable.Contains(2404)
                && xilanPreviousEventItemRecovery != null
                && xilanPreviousEventItemRecovery.ItemId == 10100142
                && xilanPreviousEventItemRecovery.RetainCount == 0
                && existingXilanPending.Count == 0
                && existingXilanRequests.Count == 0
                && existingXilanSlots[0] == 65,
                ref failures);

            var xilanEventGrant =
                InventoryRewardGrantRequest.CreateQuestEventItem(
                    10100158,
                    1,
                    ItemCreateReason.QuestReward);
            ItemMetadataResolver.TryResolveItemKind(
                10100158,
                out var xilanItemKind);
            Check(
                "quest depend-give items retain the PVF item classification",
                xilanItemKind == ItemCore.KindConsumable
                && xilanMetadata != null
                && xilanMetadata.ItemKind == "stackable"
                && xilanMetadata.StackableType != null
                && xilanMetadata.StackableType.IndexOf(
                    "upgradable legacy",
                    StringComparison.OrdinalIgnoreCase) >= 0,
                ref failures);

            var fullConsumableInventory = new InventoryService(1004, 1004);
            fullConsumableInventory.SetListParam16(
                InventoryListType.Main,
                ItemSlotBoundService.MainExpandStageFull);
            for (short slot = 65; slot <= 120; slot++)
            {
                fullConsumableInventory.SetItem(
                    InventoryListType.Main,
                    slot,
                    new ItemCore
                    {
                        ItemKind = ItemCore.KindConsumable,
                        ItemId = 900000 + slot,
                        Count = 1,
                    });
            }
            var eventPlanCreated =
                InventoryRewardGrantService.TryPlanBatch(
                    fullConsumableInventory,
                    new List<InventoryRewardGrantRequest>
                    {
                        xilanEventGrant,
                    },
                    out var eventPlan);
            Check(
                "quest event item planning follows the ordinary consumable range",
                eventPlanCreated
                && eventPlan != null
                && eventPlan.Success
                && eventPlan.Entries.Count == 1
                && eventPlan.Entries[0].ListType == InventoryListType.Main
                && eventPlan.Entries[0].SlotIndex >= 0
                && eventPlan.Entries[0].SlotIndex <= 120,
                ref failures);

            var trapMetadata = ItemMetadataResolver.Resolve(6056);
            var trapEventGrant =
                InventoryRewardGrantRequest.CreateQuestEventItem(
                    6056,
                    1,
                    ItemCreateReason.QuestReward);
            var trapPlanCreated = InventoryRewardGrantService.TryPlanBatch(
                new InventoryService(1005, 1005),
                new List<InventoryRewardGrantRequest> { trapEventGrant },
                out var trapPlan);
            Check(
                "PVF throw quest consumables enter the ordinary consumable range",
                trapMetadata != null
                && trapMetadata.StackableType != null
                && trapMetadata.StackableType.IndexOf(
                    "[throw]",
                    StringComparison.OrdinalIgnoreCase) >= 0
                && trapPlanCreated
                && trapPlan != null
                && trapPlan.Success
                && trapPlan.Entries.Count == 1
                && trapPlan.Entries[0].Core != null
                && trapPlan.Entries[0].Core.ItemKind == ItemCore.KindConsumable
                && trapPlan.Entries[0].ListType == InventoryListType.Main
                && trapPlan.Entries[0].SlotIndex >= 0
                && trapPlan.Entries[0].SlotIndex <= 120,
                ref failures);

            Check(
                "dungeon minimum level gates entry without delaying quest acceptance",
                level15Mainline.Contains(1790)
                && Dungeon.GetDungeonMinimumRequiredLevel(149) == 15
                && !Dungeon.MeetsMinimumRequiredLevel(
                    dungeonId: 149,
                    characterLevel: 14,
                    out var prisonMinimumLevel)
                && prisonMinimumLevel == 15
                && Dungeon.MeetsMinimumRequiredLevel(
                    dungeonId: 149,
                    characterLevel: 15,
                    out prisonMinimumLevel)
                && prisonMinimumLevel == 15,
                ref failures);

            var levelPriorityAdmission = WorldMap.EvaluateDungeonAdmission(
                dungeonId: 149,
                characterLevel: 14,
                activeQuestIds: new HashSet<int> { 1790 },
                clearedQuestIds: new HashSet<int>());
            var taskAfterLevelAdmission = WorldMap.EvaluateDungeonAdmission(
                dungeonId: 149,
                characterLevel: 15,
                activeQuestIds: new HashSet<int> { 1790 },
                clearedQuestIds: new HashSet<int>());
            Check(
                "story dungeon admission follows linked task state before DGN minimum level",
                levelPriorityAdmission.Allowed
                && levelPriorityAdmission.Reason != "minimum_level_not_met:14/15"
                && taskAfterLevelAdmission.Allowed,
                ref failures);

            var circleRewardDefinitionCount = 0;
            var invalidCircleRewardDefinitionCount = 0;
            var firstInvalidCircleRewardDefinition = string.Empty;
            foreach (var questId in QuestCatalog.OrderedIds)
            {
                var quest = QuestData.GetQuestFile(questId);
                if (QuestData.NormalizeQuestTag(quest?.RewardType)
                    != "circle dungeon")
                {
                    continue;
                }

                circleRewardDefinitionCount++;
                if (!QuestData.TryResolveRewardDefinition(
                        questId,
                        out _,
                        out var circleRewardError))
                {
                    invalidCircleRewardDefinitionCount++;
                    if (string.IsNullOrEmpty(firstInvalidCircleRewardDefinition))
                    {
                        firstInvalidCircleRewardDefinition =
                            $"quest={questId} error={circleRewardError}";
                    }
                }
            }
            Check(
                $"current PVF circle reward catalog is fully parseable " +
                $"count={circleRewardDefinitionCount} " +
                $"invalid={invalidCircleRewardDefinitionCount} " +
                $"first={firstInvalidCircleRewardDefinition}",
                circleRewardDefinitionCount == 480
                && invalidCircleRewardDefinitionCount == 0,
                ref failures);

            var pickupItem = DropItemBuilder.BuildPickupItem(
                sceneSlot: 0x67,
                pickerActorId: 1081,
                dstInvSlot: 0x79,
                moveFlag: 7);
            Check(
                "A21 GET_ITEM item notification is 18B",
                pickupItem.Length == 18
                && BitConverter.ToUInt16(pickupItem, 0) == 0x67
                && BitConverter.ToUInt16(pickupItem, 2) == 1081
                && pickupItem[4] == 1
                && BitConverter.ToUInt16(pickupItem, 15) == 0x79,
                ref failures);

            var pickupEpicPiece = DropItemBuilder.BuildPickupEpicPiece(
                sceneSlot: 0x71,
                pickerActorId: 0x0BEB);
            Check(
                "A21 GET_ITEM epic piece notification has no destination slot",
                pickupEpicPiece.Length == 18
                && BitConverter.ToUInt16(pickupEpicPiece, 0) == 0x71
                && BitConverter.ToUInt16(pickupEpicPiece, 2) == 0x0BEB
                && pickupEpicPiece[4] == 1
                && BitConverter.ToUInt16(pickupEpicPiece, 13) == 0x0BEB
                && BitConverter.ToUInt16(pickupEpicPiece, 15) == 0,
                ref failures);

            var pickupGold = DropItemBuilder.BuildPickupGold(
                sceneSlot: 0x66,
                pickerActorId: 1081,
                goldAmount: 8);
            Check(
                "A21 GET_ITEM gold notification is 117B",
                pickupGold.Length == 117
                && BitConverter.ToUInt16(pickupGold, 0) == 0x66
                && BitConverter.ToUInt16(pickupGold, 2) == 1081
                && BitConverter.ToInt32(pickupGold, 6) == 8,
                ref failures);

            var pickupAck = DropItemBuilder.BuildGetItemSuccessAck();
            Check(
                "A21 GET_ITEM success ACK is one byte",
                pickupAck.Length == 1 && pickupAck[0] == 1,
                ref failures);

            var noDropDie = DungeonNotificationBuilder.BuildMonsterDie(
                monsterSeqId: 0x66E6,
                drops: Array.Empty<DropInfo>(),
                ownerActorId: 9);
            Check(
                "A21 DIE_MONSTER without drops is 7B",
                noDropDie.Length == 7
                && BitConverter.ToUInt16(noDropDie, 0) == 0x66E6
                && noDropDie[2] == 0,
                ref failures);

            var oneGoldDropDie = DungeonNotificationBuilder.BuildMonsterDie(
                monsterSeqId: 0x66E6,
                drops: new[]
                {
                    new DropInfo
                    {
                        SceneSlot = 11,
                        TemplateId = 0,
                        StackCount = 1,
                    },
                },
                ownerActorId: 9);
            Check(
                "A21 DIE_MONSTER one-drop entry is 48B with owner at body offset 47",
                oneGoldDropDie.Length == 55
                && oneGoldDropDie[2] == 1
                && BitConverter.ToUInt16(oneGoldDropDie, 3) == 11
                && BitConverter.ToUInt32(oneGoldDropDie, 10) == 1
                && BitConverter.ToUInt16(oneGoldDropDie, 3 + 44) == 9,
                ref failures);

            var equipmentCore = new DfoServer.Game.Inventory.ItemCore
            {
                ItemKind = DfoServer.Game.Inventory.ItemCore.KindEquipment,
                ItemId = 35004,
                Value = unchecked((int)0x343863C0),
                Durability = 0x1234,
                AmplifyType = 0x05,
                AmplifyValue = 0x6789,
            };
            var equipmentDropDie = DungeonNotificationBuilder.BuildMonsterDie(
                monsterSeqId: 0x66E6,
                drops: new[]
                {
                    new DropInfo
                    {
                        SceneSlot = 9,
                        TemplateId = 35004,
                        StackCount = 1,
                        DropGroupId = 0x6A7DE18E,
                        Core = equipmentCore,
                    },
                },
                ownerActorId: 1);
            Check(
                "A21 DIE_MONSTER equipment drop writes ItemCore value and durability fields",
                equipmentDropDie.Length == 55
                && BitConverter.ToUInt32(equipmentDropDie, 5) == 35004
                && BitConverter.ToUInt32(equipmentDropDie, 10) == unchecked((uint)equipmentCore.Value)
                && BitConverter.ToUInt16(equipmentDropDie, 14) == 0x1234
                && equipmentDropDie[16] == 0x05
                && BitConverter.ToUInt16(equipmentDropDie, 17) == 0x6789
                && BitConverter.ToUInt32(equipmentDropDie, 19) == 0x6A7DE18E,
                ref failures);

            var exp = ExpNotificationBuilder.Build(
                level: 1,
                totalExp: 0,
                skillPoints: default,
                honorLevel: new DfoServer.Game.Accounts.HonorLevelSummary());
            Check(
                "A21 EXP keeps rejected labelled trial slots zero",
                exp.Length == ExpNotificationBuilder.BodyLength
                && exp.Length == 83
                && BitConverter.ToUInt32(
                    exp,
                    ExpNotificationBuilder.RemovedChannelExpOffset) == 0,
                ref failures);

            var eliteExp = ExpNotificationBuilder.Build(
                level: 1,
                totalExp: 100,
                skillPoints: default,
                honorLevel: new DfoServer.Game.Accounts.HonorLevelSummary(),
                eliteMonsterKillBonusExp: 200);
            Check(
                "A21 EXP projects elite kill component and keeps removed channel component zero",
                BitConverter.ToUInt32(eliteExp, 0x2F) == 200
                && BitConverter.ToUInt32(
                    eliteExp,
                    ExpNotificationBuilder.RemovedChannelExpOffset) == 0,
                ref failures);

            var playResult = DungeonNotificationBuilder.BuildPlayResult(
                userId: 0x0439,
                clearTimeMs: 0x182,
                rankIndex: 0,
                timeBonusPoint: 0x63,
                clientRankPoint: 1);
            Check(
                "A21 PLAY_RESULT keeps presentation flag zero at body offset 0",
                playResult.Length == 16
                && playResult[0] == 0
                && BitConverter.ToUInt16(playResult, 9) == 0x0439,
                ref failures);

            var settlementExperience =
                new DungeonParticipantExperienceRuntime();
            settlementExperience.RecordMonster(
                baseExperience: 85,
                growthContractBonusExperience: 0,
                isBoss: false,
                isChampion: false,
                isSuperChampion: false,
                isNamedMonster: false,
                actorSequenceId: 10004);
            settlementExperience.RecordMonster(
                baseExperience: 85,
                growthContractBonusExperience: 0,
                isBoss: false,
                isChampion: false,
                isSuperChampion: false,
                isNamedMonster: false,
                actorSequenceId: 10003);
            var settlementExperienceSnapshot = settlementExperience.Capture();
            var clearReward = DungeonNotificationBuilder.BuildClearDungeonReward(
                clearBaseExp: 1786,
                scoreBonusExp: 535,
                monsterExp: 999,
                bossExp: 135,
                championExp: 340,
                superChampionExp: 120,
                paidCardCost: 580,
                objectExperienceEntries:
                    settlementExperienceSnapshot.ObjectExperienceEntries);
            var clearTailOffset = 159 + 2 * 8;
            Check(
                "A21 CLEAR_DUNGEON_REWARD uses score quartet, u32 object count, actor sequence keys and 115B tail",
                clearReward.Length == 290
                && settlementExperienceSnapshot.ObjectExperienceEntries.Count == 2
                && settlementExperienceSnapshot.ObjectExperienceEntries[0].ObjectKey == 10004
                && settlementExperienceSnapshot.ObjectExperienceEntries[1].ObjectKey == 10003
                && BitConverter.ToUInt32(
                    clearReward,
                    DungeonNotificationBuilder.ObjectExperienceCountOffset) == 2
                && BitConverter.ToUInt32(
                    clearReward,
                    DungeonNotificationBuilder.ObjectExperienceEntriesOffset) == 10004
                && BitConverter.ToUInt32(
                    clearReward,
                    DungeonNotificationBuilder.ObjectExperienceEntriesOffset + 4) == 85
                && BitConverter.ToInt32(
                    clearReward,
                    DungeonNotificationBuilder.RewardSlotBlockOffset
                        + (DungeonNotificationBuilder.EquipmentBonusExpSlotIndex * 4)) == 0
                && clearReward[
                    DungeonNotificationBuilder.FirstVariableRewardCountOffset] == 0
                && clearReward[
                    DungeonNotificationBuilder.SecondVariableRewardCountOffset] == 0
                && BitConverter.ToUInt32(
                    clearReward,
                    DungeonNotificationBuilder.PostVariableRewardBlockOffset) == 0
                && BitConverter.ToInt32(
                    clearReward,
                    DungeonNotificationBuilder.ScoreBreakdownOffset) == 0
                && BitConverter.ToInt32(
                    clearReward,
                    DungeonNotificationBuilder.ChampionExperienceOffset) == 340
                && BitConverter.ToInt32(
                    clearReward,
                    DungeonNotificationBuilder.SuperChampionExperienceOffset) == 120
                && BitConverter.ToInt32(
                    clearReward,
                    DungeonNotificationBuilder.BossExperienceOffset) == 135
                && clearReward[clearTailOffset] == 0
                && clearReward[clearTailOffset + 1] == 1
                && BitConverter.ToInt32(clearReward, clearTailOffset + 6) == 0
                && BitConverter.ToInt32(clearReward, clearTailOffset + 73) == 580
                && BitConverter.ToUInt32(clearReward, clearTailOffset + 99) == 0
                && BitConverter.ToUInt32(clearReward, clearTailOffset + 103) == 595,
                ref failures);

            ClearRewardGenerator.WarmUp();
            var freeGold = ClearRewardGenerator.GenerateFreeGoldCard(
                new ClearRewardGenerationContext(
                    dungeonLevel: 15,
                    difficulty: 0,
                    partyMemberCount: 1,
                    rankBonusRate: 0.0f,
                    normalKillCount: 33,
                    championKillCount: 2,
                    bossKillCount: 1,
                    visitedRoomCount: 7,
                    totalRoomCount: 7),
                new DnfLcg(6));
            Check(
                "A21 free card gold applies PVF difficulty/party rates once",
                freeGold.IsGold
                && freeGold.GoldAmount == 479,
                ref failures);

            Check(
                "A21 FINISH_QUEST application projection follows the capture-backed PVF types",
                QuestCompletionApplicationService.ProjectFinishType("seeking") == QuestFinishType.Seeking
                && QuestCompletionApplicationService.ProjectFinishType("condition under clear") == QuestFinishType.ConditionUnderClear
                && QuestCompletionApplicationService.ProjectFinishType("hunt monster") == QuestFinishType.HuntMonster
                && QuestCompletionApplicationService.ProjectFinishType("meet npc") == QuestFinishType.MeetNpc
                && QuestCompletionApplicationService.ProjectFinishType("hunt enemy") == QuestFinishType.HuntEnemy
                && QuestCompletionApplicationService.ProjectFinishType("custom quest") == QuestFinishType.CustomQuest
                && QuestCompletionApplicationService.ProjectFinishType("use item") == QuestFinishType.UseItem,
                ref failures);

            var finishQuest = new QuestFinishResult
            {
                QuestId = 1016,
                FinishType = QuestFinishType.MeetNpc,
                Exp = 10,
                CompletionCount = 0,
                ChainType = 0,
                RewardAcquiredAtUnixTime = 0x6A7DE18E,
            };
            finishQuest.InsertedEntries.Add(new InsertedItemEntry
            {
                SlotIndex = 65,
                ItemId = 10088609,
                GrantedCount = 3,
            });
            finishQuest.InsertedEntries.Add(new InsertedItemEntry
            {
                SlotIndex = 66,
                ItemId = 10088610,
                GrantedCount = 3,
            });
            finishQuest.InsertedEntries.Add(new InsertedItemEntry
            {
                SlotIndex = 9,
                ItemId = 100260012,
                GrantedCount = 1,
            });
            var finishQuestBody = QuestAckBuilder.BuildFinish(finishQuest);
            var finishQuestExpected = new byte[]
            {
                0x01, 0xF8, 0x03, 0x04, 0x0A, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x03,
                0x41, 0x00, 0xA1, 0xF0, 0x99, 0x00, 0x03, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x8E, 0xE1, 0x7D,
                0x6A, 0x00, 0x00,
                0x42, 0x00, 0xA2, 0xF0, 0x99, 0x00, 0x03, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x8E, 0xE1, 0x7D,
                0x6A, 0x00, 0x00,
                0x09, 0x00, 0xAC, 0xD8, 0xF9, 0x05, 0x01, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x8E, 0xE1, 0x7D,
                0x6A, 0x00, 0x00,
            };
            Check(
                "A21 FINISH_QUEST quest 1016 matches the captured 71B ACK",
                finishQuestBody.Length == 71
                && finishQuestBody.AsSpan().SequenceEqual(finishQuestExpected),
                ref failures);

            var capturedGoldQuest = new QuestFinishResult
            {
                QuestId = 13099,
                FinishType = QuestFinishType.MeetNpc,
                Exp = 6932,
                CompletionCount = 0,
                ChainType = 0,
                RewardAcquiredAtUnixTime = 0x6A7DEC50,
            };
            capturedGoldQuest.InsertedEntries.Add(new InsertedItemEntry
            {
                SlotIndex = 0,
                ItemId = 0,
                GrantedCount = 65,
            });
            var capturedGoldQuestBody = QuestAckBuilder.BuildFinish(
                capturedGoldQuest);
            var capturedGoldQuestExpected = new byte[]
            {
                0x01, 0x2B, 0x33, 0x04, 0x14, 0x1B, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x01,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x41, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x50, 0xEC, 0x7D,
                0x6A, 0x00, 0x00,
            };
            Check(
                "A21 captured quest 13099 keeps its completion-count sample at zero",
                capturedGoldQuestBody.Length == 33
                && capturedGoldQuestBody.AsSpan().SequenceEqual(
                    capturedGoldQuestExpected),
                ref failures);

            var seekingFinishQuest = new QuestFinishResult
            {
                QuestId = 13081,
                FinishType = QuestFinishType.Seeking,
                Exp = 2600,
                CompletionCount = 0,
                ChainType = 0,
                RewardAcquiredAtUnixTime = 0x6A830D0D,
            };
            seekingFinishQuest.ConsumedEntries.Add(new ConsumedItemEntry
            {
                UpdateType = 0,
                SlotIndex = 182,
                ConsumedCount = 1,
            });
            seekingFinishQuest.InsertedEntries.Add(new InsertedItemEntry
            {
                SlotIndex = 0,
                ItemId = 0,
                GrantedCount = 60,
            });
            seekingFinishQuest.InsertedEntries.Add(new InsertedItemEntry
            {
                SlotIndex = 22,
                ItemId = 100060157,
                GrantedCount = 1,
            });
            var seekingFinishBody = QuestAckBuilder.BuildFinish(
                seekingFinishQuest);
            Check(
                "A21 seeking FINISH_QUEST uses a 7B consumed entry followed by chain",
                seekingFinishBody.Length == 60
                && BitConverter.ToUInt32(seekingFinishBody, 8) == 0
                && seekingFinishBody[12] == 1
                && seekingFinishBody[13] == 0
                && BitConverter.ToUInt16(seekingFinishBody, 14) == 182
                && BitConverter.ToUInt32(seekingFinishBody, 16) == 1
                && seekingFinishBody[20] == 0
                && seekingFinishBody[21] == 2
                && BitConverter.ToUInt16(seekingFinishBody, 22) == 0
                && BitConverter.ToUInt32(seekingFinishBody, 24) == 0
                && BitConverter.ToUInt32(seekingFinishBody, 28) == 60
                && BitConverter.ToUInt16(seekingFinishBody, 41) == 22
                && BitConverter.ToUInt32(seekingFinishBody, 43) == 100060157
                && BitConverter.ToUInt32(seekingFinishBody, 47) == 1,
                ref failures);

            var dailyChallengeFinish = new QuestFinishResult
            {
                QuestId = 14650,
                FinishType = QuestFinishType.Seeking,
                Exp = 427677,
                CompletionCount = 1,
                ChainType = 0,
                RewardAcquiredAtUnixTime = 0x6A886DC1,
            };
            dailyChallengeFinish.InsertedEntries.Add(new InsertedItemEntry
            {
                SlotIndex = 6,
                ItemId = 10099411,
                GrantedCount = 2,
            });
            var dailyChallengeFinishBody = QuestAckBuilder.BuildFinish(
                dailyChallengeFinish);
            var dailyChallengeFinishExpected = new byte[]
            {
                0x01, 0x3A, 0x39, 0x00, 0x9D, 0x86, 0x06, 0x00,
                0x01, 0x00, 0x00, 0x00,
                0x00, // consumed count
                0x00, // chain type
                0x01, // inserted reward count
                0x06, 0x00, 0xD3, 0x1A, 0x9A, 0x00, 0x02, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0xC1, 0x6D, 0x88,
                0x6A, 0x00, 0x00,
            };
            Check(
                "A21 seeking FINISH_QUEST keeps explicit chain zero when no items are consumed",
                dailyChallengeFinishBody.Length == 34
                && dailyChallengeFinishBody.AsSpan().SequenceEqual(
                    dailyChallengeFinishExpected),
                ref failures);

            var capturedSeekingQuest = new QuestFinishResult
            {
                QuestId = 1782,
                FinishType = QuestFinishType.Seeking,
                Exp = 771,
                CompletionCount = 0,
                ChainType = 0,
                RewardAcquiredAtUnixTime = 0x6A7DE550,
            };
            capturedSeekingQuest.ConsumedEntries.Add(new ConsumedItemEntry
            {
                UpdateType = 0,
                SlotIndex = 182,
                ConsumedCount = 10,
            });
            capturedSeekingQuest.InsertedEntries.Add(new InsertedItemEntry
            {
                SlotIndex = 0,
                ItemId = 0,
                GrantedCount = 80,
            });
            var capturedSeekingBody = QuestAckBuilder.BuildFinish(
                capturedSeekingQuest);
            var capturedSeekingExpected = new byte[]
            {
                0x01, 0xF6, 0x06, 0x00, 0x03, 0x03, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0xB6, 0x00,
                0x0A, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x50, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x50, 0xE5, 0x7D, 0x6A, 0x00,
                0x00,
            };
            Check(
                "A21 FINISH_QUEST quest 1782 matches the captured 41B ACK",
                capturedSeekingBody.Length == 41
                && capturedSeekingBody.AsSpan().SequenceEqual(
                    capturedSeekingExpected),
                ref failures);

            var capturedTitleQuest = new QuestFinishResult
            {
                QuestId = 4303,
                FinishType = QuestFinishType.HuntMonster,
                Exp = 2403,
                CompletionCount = 0,
                ChainType = QuestData.ChainTypeTitle,
            };
            var capturedTitleBody = QuestAckBuilder.BuildFinish(capturedTitleQuest);
            var capturedTitleExpected = new byte[]
            {
                0x01, 0xCF, 0x10, 0x02, 0x63, 0x09, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x05,
            };
            Check(
                "A21 title FINISH_QUEST uses chain type 5",
                capturedTitleBody.Length == 13
                && capturedTitleBody.AsSpan().SequenceEqual(capturedTitleExpected),
                ref failures);

            var capturedTitleSeekingQuest = new QuestFinishResult
            {
                QuestId = 1028,
                FinishType = QuestFinishType.Seeking,
                Exp = 0x1693,
                CompletionCount = 0,
                ChainType = QuestData.ChainTypeTitle,
            };
            for (var slot = 0x0162; slot <= 0x0165; slot++)
            {
                capturedTitleSeekingQuest.ConsumedEntries.Add(new ConsumedItemEntry
                {
                    UpdateType = 0,
                    SlotIndex = (ushort)slot,
                    ConsumedCount = 21,
                });
            }
            var capturedTitleSeekingBody = QuestAckBuilder.BuildFinish(
                capturedTitleSeekingQuest);
            var capturedTitleSeekingExpected = new byte[]
            {
                0x01, 0x04, 0x04, 0x00, 0x93, 0x16, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x04,
                0x00, 0x62, 0x01, 0x15, 0x00, 0x00, 0x00,
                0x00, 0x63, 0x01, 0x15, 0x00, 0x00, 0x00,
                0x00, 0x64, 0x01, 0x15, 0x00, 0x00, 0x00,
                0x00, 0x65, 0x01, 0x15, 0x00, 0x00, 0x00,
                0x05,
            };
            Check(
                "A21 title seeking FINISH_QUEST uses 7B consumed entries",
                capturedTitleSeekingBody.Length == 42
                && capturedTitleSeekingBody.AsSpan().SequenceEqual(
                    capturedTitleSeekingExpected),
                ref failures);

            var capturedCareerFinish = new QuestFinishResult
            {
                QuestId = 7810,
                FinishType = QuestFinishType.HuntMonster,
                Exp = 6932,
                CompletionCount = 0,
                ChainType = 1,
                // PVF reward parameter is 3, while the captured wire byte is 0.
                GrowNumber = 3,
            };
            var capturedCareerExpected = new byte[]
            {
                0x01, 0x82, 0x1E, 0x02, 0x14, 0x1B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x01, 0x00, 0x0B, 0x66, 0xB3, 0x00, 0x07, 0x67, 0xAE, 0x00, 0x01, 0x68,
                0xA9, 0x00, 0x01, 0x00, 0x05, 0x00, 0x01, 0x01, 0x2E, 0x00, 0x01, 0x69,
                0xFF, 0x01, 0x01, 0x6A, 0x38, 0x00, 0x01, 0x02, 0x19, 0x00, 0x01, 0x03,
                0x41, 0x00, 0x01, 0x04, 0x4C, 0x00, 0x01, 0x06, 0xC5, 0x00, 0x01, 0x0B,
                0x66, 0xB3, 0x00, 0x07, 0x67, 0xAE, 0x00, 0x01, 0x68, 0xA9, 0x00, 0x01,
                0x00, 0x05, 0x00, 0x01, 0x01, 0x2E, 0x00, 0x01, 0x69, 0xFF, 0x01, 0x01,
                0x6A, 0x38, 0x00, 0x01, 0x02, 0x19, 0x00, 0x01, 0x03, 0x41, 0x00, 0x01,
                0x04, 0x4C, 0x00, 0x01, 0x06, 0xC5, 0x00, 0x01,
            };
            FillSkillPagesFromCapturedFinishBody(
                capturedCareerFinish,
                capturedCareerExpected,
                offset: 14);
            var capturedCareerBody = QuestAckBuilder.BuildFinish(
                capturedCareerFinish);
            Check(
                "A21 career FINISH_QUEST quest 7810 matches the captured 104B ACK",
                capturedCareerBody.Length == 104
                && capturedCareerBody.AsSpan().SequenceEqual(
                    capturedCareerExpected)
                && capturedCareerBody[12] == 1
                && capturedCareerBody[13] == 0
                && capturedCareerFinish.SkillPages[0].Entries.Count == 11
                && capturedCareerFinish.SkillPages[1].Entries.Count == 11,
                ref failures);

            var launcherCareerSkills = CharacterSkillProfile.BuildSnapshot(
                job: 5,
                growType: 2,
                secondGrowType: 0,
                charLevel: 21);
            var launcherCareerFinish = new QuestFinishResult
            {
                QuestId = 7873,
                FinishType = QuestFinishType.HuntMonster,
                Exp = 5836,
                CompletionCount = 0,
                ChainType = 1,
                GrowNumber = 2,
                SkillPages = QuestCompletionApplicationService
                    .CaptureFinishSkillPages(launcherCareerSkills),
            };
            var launcherCareerBody = QuestAckBuilder.BuildFinish(
                launcherCareerFinish);
            Check(
                "A21 launcher career snapshot has two captured 11-entry pages",
                launcherCareerFinish.SkillPages.Count == 2
                && launcherCareerFinish.SkillPages[0].Entries.Count == 11
                && launcherCareerFinish.SkillPages[1].Entries.Count == 11,
                ref failures);
            Check(
                "A21 launcher career snapshot remains profession-specific",
                launcherCareerFinish.SkillPages.Count == 2
                && launcherCareerFinish.SkillPages[0].Entries.Count > 0
                && capturedCareerFinish.SkillPages.Count == 2
                && capturedCareerFinish.SkillPages[0].Entries.Count > 0
                && launcherCareerFinish.SkillPages[0].Entries.Exists(
                    entry => entry.SkillId == 92)
                && !capturedCareerFinish.SkillPages[0].Entries.Exists(
                    entry => entry.SkillId == 92)
                && capturedCareerFinish.SkillPages[0].Entries.Exists(
                    entry => entry.SkillId == 56)
                && !launcherCareerFinish.SkillPages[0].Entries.Exists(
                    entry => entry.SkillId == 56),
                ref failures);
            Check(
                "A21 career ACK keeps reserved zero and serializes the selected profession pages",
                launcherCareerBody.Length == 16
                    + 4 * (launcherCareerFinish.SkillPages[0].Entries.Count
                        + launcherCareerFinish.SkillPages[1].Entries.Count)
                && launcherCareerBody[12] == 1
                && launcherCareerBody[13] == 0
                && !launcherCareerBody.AsSpan().SequenceEqual(
                    capturedCareerExpected),
                ref failures);

            var capturedExpertJobFinish = new QuestFinishResult
            {
                QuestId = 2710,
                FinishType = QuestFinishType.Seeking,
                Exp = 5351,
                CompletionCount = 0,
                ChainType = 20,
                GrowNumber = 3,
            };
            capturedExpertJobFinish.ConsumedEntries.Add(new ConsumedItemEntry
            {
                UpdateType = 0,
                SlotIndex = 358,
                ConsumedCount = 100,
            });
            var capturedExpertJobExpected = new byte[]
            {
                0x01, 0x96, 0x0A, 0x00, 0xE7, 0x14, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x01, 0x00, 0x66, 0x01, 0x64, 0x00, 0x00, 0x00, 0x14, 0x03, 0x2B, 0x66,
                0xB3, 0x00, 0x07, 0x06, 0x60, 0x00, 0x01, 0x67, 0xAE, 0x00, 0x01, 0x68,
                0xA9, 0x00, 0x01, 0x09, 0x01, 0x00, 0x01, 0x0A, 0x08, 0x00, 0x01, 0x69,
                0xFF, 0x01, 0x01, 0x36, 0xC6, 0x00, 0x01, 0x37, 0x3E, 0x00, 0x01, 0x0B,
                0x2E, 0x00, 0x01, 0x38, 0x18, 0x00, 0x01, 0x07, 0xC5, 0x00, 0x01, 0x39,
                0x4B, 0x00, 0x0F, 0x42, 0x4A, 0x00, 0x01, 0x00, 0x40, 0x00, 0x24, 0x46,
                0x3C, 0x00, 0x01, 0x08, 0x02, 0x00, 0x01, 0x02, 0x6A, 0x00, 0x08, 0x6A,
                0xBC, 0x00, 0x0A, 0x6B, 0xBE, 0x00, 0x01, 0x6C, 0xB0, 0x00, 0x0A, 0x47,
                0x28, 0x00, 0x01, 0x01, 0x43, 0x00, 0x01, 0xCB, 0x44, 0x00, 0x1F, 0x3A,
                0x4C, 0x00, 0x01, 0x3B, 0x41, 0x00, 0x0A, 0xC6, 0x42, 0x00, 0x15, 0xCA,
                0x4E, 0x00, 0x0B, 0x3D, 0x2B, 0x00, 0x01, 0x3E, 0x46, 0x00, 0x01, 0x03,
                0x3D, 0x00, 0x0A, 0x05, 0x47, 0x00, 0x17, 0x04, 0x4D, 0x00, 0x10, 0xC8,
                0x45, 0x00, 0x1C, 0x43, 0x6B, 0x00, 0x06, 0xC7, 0x69, 0x00, 0x06, 0xC9,
                0x68, 0x00, 0x02, 0x96, 0x80, 0x00, 0x05, 0x97, 0x7B, 0x00, 0x05, 0x98,
                0x7A, 0x00, 0x01, 0x99, 0x7C, 0x00, 0x05, 0x9A, 0x82, 0x00, 0x01, 0x6D,
                0xC2, 0x00, 0x01, 0x0F, 0x66, 0xB3, 0x00, 0x07, 0x06, 0x60, 0x00, 0x01,
                0x67, 0xAE, 0x00, 0x01, 0x68, 0xA9, 0x00, 0x01, 0x00, 0x01, 0x00, 0x01,
                0x01, 0x08, 0x00, 0x01, 0x69, 0xFF, 0x01, 0x01, 0x36, 0xC6, 0x00, 0x01,
                0x37, 0x3E, 0x00, 0x01, 0x02, 0x2E, 0x00, 0x01, 0x38, 0x18, 0x00, 0x01,
                0x07, 0xC5, 0x00, 0x01, 0x39, 0x4B, 0x00, 0x01, 0x03, 0x4A, 0x00, 0x01,
                0x6A, 0xC2, 0x00, 0x01,
            };
            FillSkillPagesFromCapturedFinishBody(
                capturedExpertJobFinish,
                capturedExpertJobExpected,
                offset: 22);
            var capturedExpertJobBody = QuestAckBuilder.BuildFinish(
                capturedExpertJobFinish);
            Check(
                "A21 expert-job FINISH_QUEST uses 7B consume and two compact skill pages",
                capturedExpertJobBody.Length == capturedExpertJobExpected.Length
                && capturedExpertJobBody.AsSpan().SequenceEqual(
                    capturedExpertJobExpected)
                && capturedExpertJobExpected[20] == 20
                && capturedExpertJobExpected[21] == 3
                && capturedExpertJobExpected[22] == 43
                && capturedExpertJobFinish.SkillPages[0].Entries.Count == 43
                && capturedExpertJobFinish.SkillPages[1].Entries.Count == 15
                && capturedExpertJobFinish.SkillPages[0].Entries[
                    capturedExpertJobFinish.SkillPages[0].Entries.Count - 1].SkillId == 194
                && capturedExpertJobFinish.SkillPages[1].Entries[
                    capturedExpertJobFinish.SkillPages[1].Entries.Count - 1].SkillId == 194,
                ref failures);

            var titleReward = QuestData.ResolveReward(
                4303,
                rewardSelectIdx: -1,
                playerLevel: 19,
                playerJob: -1,
                playerGrowType: -1);
            Check(
                "PVF title reward projects chain type 5 without ordinary gold",
                titleReward.IsValid
                && titleReward.Reward.ChainType == QuestData.ChainTypeTitle
                && titleReward.Reward.Gold == 0,
                ref failures);

            var careerTransferQuest = PvfLib.QuestFile.Parse(
                "[cant giveup]\n2\n" +
                "[job change quest]\n1\n" +
                "[reward type]\n`[grow type]`\n");
            var ordinaryRestrictedQuest = PvfLib.QuestFile.Parse(
                "[cant giveup]\n2\n" +
                "[reward type]\n`[item]`\n");
            var permanentRestrictedQuest = PvfLib.QuestFile.Parse(
                "[cant giveup]\n1\n" +
                "[job change quest]\n1\n" +
                "[reward type]\n`[grow type]`\n");
            var creatureEvolutionQuest = PvfLib.QuestFile.Parse(
                "[cant giveup]\n2\n" +
                "[job change quest]\n10\n" +
                "[reward type]\n`[creature evolution]`\n");
            Check(
                "PVF career transfer quests can be given up without widening restricted quests",
                careerTransferQuest.CantGiveupValue == 2
                && careerTransferQuest.CantGiveup
                && QuestData.CanGiveup(careerTransferQuest)
                && QuestData.CanGiveup(7803)
                && !QuestData.CanGiveup(ordinaryRestrictedQuest)
                && !QuestData.CanGiveup(permanentRestrictedQuest)
                && !QuestData.CanGiveup(creatureEvolutionQuest),
                ref failures);

            var beginnerArmorPrerequisite = QuestPrerequisiteCatalog.Get(13081);
            var beginnerArmorBeforeJobGift = beginnerArmorPrerequisite?.Evaluate(
                new QuestPrerequisiteEvaluationState(
                    new HashSet<int>(),
                    new Dictionary<int, int>()));
            var beginnerArmorAfterJobGift = beginnerArmorPrerequisite?.Evaluate(
                new QuestPrerequisiteEvaluationState(
                    new HashSet<int> { 4728 },
                    new Dictionary<int, int> { [4728] = 1 }));
            var beginnerArmorReward = QuestData.ResolveReward(
                13081,
                rewardSelectIdx: -1,
                playerLevel: 15,
                playerJob: 11,
                playerGrowType: 4);
            Check(
                "quest 13081 follows the 4728 prerequisite and job-specific reward",
                beginnerArmorPrerequisite != null
                && beginnerArmorPrerequisite.IsValid
                && beginnerArmorBeforeJobGift.HasValue
                && !beginnerArmorBeforeJobGift.Value.IsAllowed
                && beginnerArmorBeforeJobGift.Value.Reason
                    == QuestPrerequisiteBlockReason.MissingCompletedQuest
                && beginnerArmorAfterJobGift.HasValue
                && beginnerArmorAfterJobGift.Value.IsAllowed
                && beginnerArmorReward.IsValid
                && beginnerArmorReward.Reward.Exp == 6039
                && beginnerArmorReward.Reward.Gold == 60
                && beginnerArmorReward.Reward.Items.Count == 1
                && beginnerArmorReward.Reward.Items[0].ItemId == 100060157
                && beginnerArmorReward.Reward.Items[0].Count == 1,
                ref failures);

            var secondArmorPrerequisite = QuestPrerequisiteCatalog.Get(13082);
            var secondArmorBeforeFirstPart = secondArmorPrerequisite?.Evaluate(
                new QuestPrerequisiteEvaluationState(
                    new HashSet<int>(),
                    new Dictionary<int, int>()));
            var secondArmorAfterFirstPart = secondArmorPrerequisite?.Evaluate(
                new QuestPrerequisiteEvaluationState(
                    new HashSet<int> { 13081 },
                    new Dictionary<int, int> { [13081] = 1 }));
            var secondArmorReward = QuestData.ResolveReward(
                13082,
                rewardSelectIdx: -1,
                playerLevel: 16,
                playerJob: 11,
                playerGrowType: 4);
            Check(
                "quest 13082 follows part one and resolves the current reward",
                secondArmorPrerequisite != null
                && secondArmorPrerequisite.IsValid
                && secondArmorBeforeFirstPart.HasValue
                && !secondArmorBeforeFirstPart.Value.IsAllowed
                && secondArmorBeforeFirstPart.Value.Reason
                    == QuestPrerequisiteBlockReason.MissingCompletedQuest
                && secondArmorAfterFirstPart.HasValue
                && secondArmorAfterFirstPart.Value.IsAllowed
                && secondArmorReward.IsValid
                && secondArmorReward.Reward.Exp == 7243
                && secondArmorReward.Reward.Gold == 65
                && secondArmorReward.Reward.Items.Count == 1
                && secondArmorReward.Reward.Items[0].ItemId == 100110146
                && secondArmorReward.Reward.Items[0].Count == 1,
                ref failures);

            var callDaimus = SkillDataProvider.GetSkill(11, 31);
            Check(
                "female swordman keeps the PVF-defined level 15 trial transfer skill",
                callDaimus != null
                && callDaimus.SkillIndex == 31
                && callDaimus.Name != null
                && callDaimus.Name.Contains("蛇腹剑")
                && callDaimus.SkillFitnessGrowtypes != null
                && callDaimus.SkillFitnessGrowtypes.Length == 1
                && callDaimus.SkillFitnessGrowtypes[0] == 3
                && callDaimus.IsTrialTransferSkill(0, 0)
                && callDaimus.GetMaxLearnableLevel(15, 0, 0) == 1
                && callDaimus.GetMaxLearnableLevel(15, 3, 0) == 1,
                ref failures);

            var elementalIgnite = SkillDataProvider.GetSkill(3, 29);
            Check(
                "PVF multi-value skill maximum levels follow the active growType",
                elementalIgnite != null
                && elementalIgnite.MaximumLevels != null
                && elementalIgnite.MaximumLevels.Length == 6
                && elementalIgnite.MaximumLevels[0] == 15
                && elementalIgnite.MaximumLevels[1] == 30
                && elementalIgnite.GrowtypeMaxLevels != null
                && elementalIgnite.GrowtypeMaxLevels.Length == 6
                && elementalIgnite.GrowtypeMaxLevels[0] == 5
                && elementalIgnite.GrowtypeMaxLevels[1] == 20
                && elementalIgnite.GetMaxLevelFor(0, 0) == 5
                && elementalIgnite.GetMaxLevelFor(1, 0) == 20
                && elementalIgnite.GetMaxLearnableLevel(99, 0, 0) == 5
                && elementalIgnite.GetMaxLearnableLevel(99, 1, 0) == 20,
                ref failures);

            var atGunnerNitroMotor = SkillDataProvider.GetSkill(5, 16);
            var atGunnerTransferSnapshot = CharacterSkillProfile.BuildSnapshot(
                job: 5,
                growType: 4,
                secondGrowType: 0,
                charLevel: 15);
            var atGunnerNitroMotorGranted = atGunnerTransferSnapshot.Pages.Exists(
                page => page.Entries.Exists(entry => entry.SkillId == 16 && entry.Level >= 1));
            var atGunnerRemoved = SkillStateService.RemoveUnavailableSkills(
                atGunnerTransferSnapshot,
                job: 5,
                growType: 4,
                secondGrowType: 0);
            Check(
                "PVF ATGunner transfer skill uses maximum-level growType owner",
                atGunnerNitroMotor != null
                && atGunnerNitroMotor.GetMaxLevelFor(4, 0) == 1
                && atGunnerNitroMotor.GetMaxLevelFor(3, 0) == 0
                && atGunnerNitroMotor.GetMaxLearnableLevel(15, 4, 0) == 1
                && atGunnerNitroMotorGranted
                && atGunnerRemoved == 0,
                ref failures);

            var berserkerRejection = SkillDataProvider.GetSkill(0, 248);
            Check(
                "PVF Berserker transfer skill ignores non-indexed fitness metadata",
                berserkerRejection != null
                && berserkerRejection.GetMaxLevelFor(3, 0) == 1
                && berserkerRejection.GetMaxLearnableLevel(15, 3, 0) == 1
                && berserkerRejection.GetMaxLevelFor(1, 0) == 0,
                ref failures);

            var unavailableCareerSkill = new SkillInfoSnapshot();
            unavailableCareerSkill.Pages.Add(new SkillInfoPageSnapshot());
            unavailableCareerSkill.Pages.Add(new SkillInfoPageSnapshot());
            unavailableCareerSkill.Pages[0].Entries.Add(
                new SkillInfoEntrySnapshot
                {
                    Slot = 0,
                    SkillId = 31,
                    Level = 1,
                });
            var unavailableSkillId = -1;
            for (var candidateId = 1; candidateId < 512; candidateId++)
            {
                var candidate = SkillDataProvider.GetSkill(11, candidateId);
                if (candidate != null
                    && !candidate.IsTrialTransferSkill(0, 0)
                    && !candidate.IsAvailableFor(0, 0))
                {
                    unavailableSkillId = candidateId;
                    break;
                }
            }
            if (unavailableSkillId > 0)
            {
                unavailableCareerSkill.Pages[0].Entries.Add(
                    new SkillInfoEntrySnapshot
                    {
                        Slot = 1,
                        SkillId = (ushort)unavailableSkillId,
                        Level = 1,
                    });
            }
            var removedUnavailableCareerSkills = SkillStateService.RemoveUnavailableSkills(
                unavailableCareerSkill,
                job: 11,
                growType: 0,
                secondGrowType: 0);
            Check(
                "skill synchronization keeps trial skills and removes other unavailable skills",
                unavailableSkillId > 0
                && removedUnavailableCareerSkills == 1
                && unavailableCareerSkill.Pages[0].Entries.Count == 1,
                ref failures);

            var projectedSkillInfoBody = SkillInfoBodyBuilder.BuildFrom(unavailableCareerSkill);
            var projectedUserInfoSubtype1Body = UserInfoSubtype1Builder.BuildFromSnapshot(
                new UserInfoAdditionSnapshot(),
                unavailableCareerSkill);
            var skill31WireBytes = new byte[] { 0x1F, 0x00 };
            Check(
                "trial skill remains in SKILLINFO and USERINFO subtype1",
                new SkillInfoBodyBuilder().NotiType
                    == (ushort)NotiPacketTypeA21.SKILLINFO
                && new UserInfoBodyBuilder().NotiType
                    == (ushort)NotiPacketTypeA21.USERINFO
                && ContainsBytes(projectedSkillInfoBody, skill31WireBytes)
                && ContainsBytes(projectedUserInfoSubtype1Body, skill31WireBytes),
                ref failures);

            var flaggedMonsterBody = new byte[79];
            Buffer.BlockCopy(
                BitConverter.GetBytes((ushort)18648),
                0,
                flaggedMonsterBody,
                0,
                2);
            flaggedMonsterBody[20] = 0;
            flaggedMonsterBody[27] = 1;
            var flaggedMonster = DieMonsterRequest.Parse(flaggedMonsterBody);
            var flaggedMonsterRoomState = new RoomState
            {
                Maze = new DfoServer.GameWorld.Dungeon.MazeSumInfo
                {
                    PassiveObjectCodes = new[] { 18648 },
                },
            };
            var flaggedMonsterRoom = new DungeonRunRoomSnapshot(
                default,
                default,
                default,
                roomStartSequence: 18646,
                new DfoServer.GameWorld.Dungeon.MonsterSumInfo[4],
                flaggedMonsterRoomState);
            Check(
                "A21 DIE_MONSTER current-room actor overrides unknown passive marker",
                flaggedMonster.IsPassiveObject
                && flaggedMonsterRoom.ContainsStaticActorSequence(18648)
                && !DungeonCombatHandler.ShouldTreatAsPassiveObject(
                    flaggedMonster.IsPassiveObject,
                    flaggedMonster.HasMapOwnedPassiveObjectSignature,
                    flaggedMonster.LocalIndex,
                    flaggedMonsterRoom)
                && DungeonCombatHandler.ShouldTreatAsPassiveObject(
                    flaggedMonster.IsPassiveObject,
                    flaggedMonster.HasMapOwnedPassiveObjectSignature,
                    20000,
                    flaggedMonsterRoom),
                ref failures);

            var ordinaryPassiveBody = new byte[66];
            Buffer.BlockCopy(
                BitConverter.GetBytes((ushort)52853),
                0,
                ordinaryPassiveBody,
                0,
                2);
            ordinaryPassiveBody[4] = 0xFF;
            ordinaryPassiveBody[5] = 0xFF;
            var ordinaryPassiveRequest = DieMonsterRequest.Parse(
                ordinaryPassiveBody);
            var ordinaryPassiveMaze = new DfoServer.GameWorld.Dungeon.MazeSumInfo
            {
                PassiveObjectCodes = new[] { 52853, 52853 },
            };
            var ordinaryPassiveRoomState = new RoomState
            {
                Maze = ordinaryPassiveMaze,
            };
            var ordinaryPassiveRoomSnapshot = new DungeonRunRoomSnapshot(
                default,
                default,
                default,
                roomStartSequence: 41024,
                new DfoServer.GameWorld.Dungeon.MonsterSumInfo[3],
                ordinaryPassiveRoomState);
            Check(
                "A21 ordinary MAP passive object resolves from frozen code without legacy marker",
                ordinaryPassiveRequest.LocalIndex == 52853
                && !ordinaryPassiveRequest.IsPassiveObject
                && ordinaryPassiveRequest.HasMapOwnedPassiveObjectSignature
                && ordinaryPassiveRoomSnapshot
                    .ContainsMapOwnedPassiveObjectCode(52853)
                && DungeonCombatHandler.ShouldTreatAsPassiveObject(
                    ordinaryPassiveRequest.IsPassiveObject,
                    ordinaryPassiveRequest.HasMapOwnedPassiveObjectSignature,
                    ordinaryPassiveRequest.LocalIndex,
                    ordinaryPassiveRoomSnapshot),
                ref failures);

            var mapOwnedRoom = new DungeonInstanceRoom(
                roomInstanceId: 7003,
                new RoomKey(4, 1, 59241),
                ordinaryPassiveMaze,
                seed: 1);
            mapOwnedRoom.AttachToInstance(7001);
            var mapOwnedRunIdentity = new DungeonRunIdentity(7001, 7002, 1);
            DungeonEventEnvelope CreatePassiveObjectEvent() =>
                new DungeonEventEnvelope(
                    Guid.NewGuid(),
                    mapOwnedRunIdentity,
                    roomInstanceId: 7003,
                    sourcePlayerId: 11,
                    affectedPlayerId: 11,
                    sourceActorId: null,
                    sourceActorCode: 52853,
                    cause: "selftest-map-passive",
                    occurredTick: Environment.TickCount64);
            var firstOrdinaryPassiveDeath = mapOwnedRoom
                .TryRecordNextMapOwnedPassiveObjectDeath(
                    CreatePassiveObjectEvent(),
                    actorCode: 52853,
                    out var firstOrdinaryPassiveDefined);
            var secondOrdinaryPassiveDeath = mapOwnedRoom
                .TryRecordNextMapOwnedPassiveObjectDeath(
                    CreatePassiveObjectEvent(),
                    actorCode: 52853,
                    out var secondOrdinaryPassiveDefined);
            var exhaustedOrdinaryPassiveDeath = mapOwnedRoom
                .TryRecordNextMapOwnedPassiveObjectDeath(
                    CreatePassiveObjectEvent(),
                    actorCode: 52853,
                    out var exhaustedOrdinaryPassiveDefined);
            Check(
                "ordinary MAP passive objects consume same-code instances at most once",
                firstOrdinaryPassiveDefined
                && firstOrdinaryPassiveDeath.Accepted
                && firstOrdinaryPassiveDeath.Created
                && secondOrdinaryPassiveDefined
                && secondOrdinaryPassiveDeath.Accepted
                && secondOrdinaryPassiveDeath.Created
                && exhaustedOrdinaryPassiveDefined
                && !exhaustedOrdinaryPassiveDeath.Accepted
                && !exhaustedOrdinaryPassiveDeath.Created,
                ref failures);

            var storyPauseCondition = new ClearConditionState(
                new List<PvfLib.ClearConditionEntry>
                {
                    new PvfLib.ClearConditionEntry
                    {
                        Type = 0,
                        TargetId = 10601,
                        Count = 1,
                    },
                });
            var storyPauseFirst = storyPauseCondition.TryCheckAny(
                type: 0,
                targetIds: new[] { 1050, 10601 },
                out var storyPauseTarget);
            var storyPauseRepeat = storyPauseCondition.TryCheckAny(
                type: 0,
                targetIds: new[] { 10601 },
                out _);
            Check(
                "story pause consumes one pending destroy-object condition atomically",
                storyPauseFirst
                && storyPauseTarget == 10601
                && storyPauseCondition.IsCleared
                && !storyPauseRepeat,
                ref failures);

            var storyPauseRoom = new RoomState();
            storyPauseRoom.TryActivate();
            var storyPauseRoomFirst = storyPauseRoom.TryBeginStoryPauseClear();
            var storyPauseRoomRepeat = storyPauseRoom.TryBeginStoryPauseClear();
            Check(
                "story pause clear is consumed at most once per room",
                storyPauseRoomFirst && !storyPauseRoomRepeat,
                ref failures);

            var pvfArchivePath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (!string.IsNullOrWhiteSpace(pvfArchivePath))
            {
                var storyDungeon = Dungeon.GetDungeonFile(160);
                var storyMaze = storyDungeon?.Mazes != null
                    && storyDungeon.Mazes.Count > 2
                    ? storyDungeon.Mazes[2]
                    : null;
                var storyBossMap = Dungeon.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 160,
                    x: 4,
                    y: 2,
                    mazeIndex: 2,
                    overrideMapId: 59199,
                    bossPos: new[] { 4, 2 });
                var hasDestroyObjectCondition = storyMaze?.ClearConditions != null
                    && storyMaze.ClearConditions.Exists(
                        condition => condition.Type == 0
                            && condition.TargetId == 10601
                            && condition.Count == 1);
                var hasBossPassiveObject = storyBossMap.PassiveObjectCodes != null
                    && ContainsInt(storyBossMap.PassiveObjectCodes, 10601);
                Check(
                    "PVF story dungeon clear condition and boss passive object are resource-driven",
                    hasDestroyObjectCondition && hasBossPassiveObject,
                    ref failures);

                var questConnectedMaze = storyDungeon?.Mazes != null
                    && storyDungeon.Mazes.Count > 4
                    ? storyDungeon.Mazes[4]
                    : null;
                var questConnectedBossMap = Dungeon.GetDungeonMapMonsterSummaryInformation(
                    dungeonId: 160,
                    x: 4,
                    y: 3,
                    mazeIndex: 4,
                    overrideMapId: 59216,
                    bossPos: new[] { 4, 3 });
                var hasQuestConnectedCondition = questConnectedMaze?.QuestConnection != null
                    && questConnectedMaze.QuestConnection.Length >= 2
                    && questConnectedMaze.QuestConnection[0] == 0
                    && questConnectedMaze.QuestConnection[1] == 1845
                    && questConnectedMaze.ClearConditions != null
                    && questConnectedMaze.ClearConditions.Exists(
                        condition => condition.Type == 0
                            && condition.TargetId == 10601
                            && condition.Count == 1);
                var hasQuestTargetPassiveObject = questConnectedBossMap.PassiveObjectCodes != null
                    && ContainsInt(questConnectedBossMap.PassiveObjectCodes, 13099)
                    && ContainsInt(questConnectedBossMap.PassiveObjectCodes, 10601);
                Check(
                    "PVF quest-connected maze exposes task and clear-condition passive objects",
                    hasQuestConnectedCondition && hasQuestTargetPassiveObject,
                    ref failures);

                var seekingPassiveCandidates = QuestDropProvider.CheckEnemyDrop(
                    new HashSet<int> { 1849 },
                    dungeonIndex: 160,
                    difficulty: 0,
                    enemyCode: 52853,
                    enemyType: QuestDropProvider.EnemyTypePassiveObject);
                var seekingPassiveMap = Dungeon
                    .GetDungeonMapMonsterSummaryInformation(
                        dungeonId: 160,
                        x: 4,
                        y: 1,
                        mazeIndex: 6,
                        overrideMapId: 59241,
                        bossPos: new[] { 4, 1 });
                var hasSeekingPassiveCandidate =
                    seekingPassiveCandidates != null
                    && seekingPassiveCandidates.Exists(candidate =>
                        candidate.QuestId == 1849
                        && candidate.ItemId == 10099811
                        && candidate.Count == 1
                        && candidate.DropRate == 100
                        && candidate.MaxStack == 5
                        && candidate.PreferQuestInventory);
                Check(
                    "PVF seeking quest passive-object reward resolves through the frozen MAP",
                    hasSeekingPassiveCandidate
                    && seekingPassiveMap.PassiveObjectCodes != null
                    && ContainsInt(
                        seekingPassiveMap.PassiveObjectCodes,
                        52853),
                    ref failures);

                var consciousnessDungeon = Dungeon.GetDungeonFile(77);
                var consciousnessMaze = consciousnessDungeon?.Mazes != null
                    && consciousnessDungeon.Mazes.Count > 0
                    ? consciousnessDungeon.Mazes[0]
                    : null;
                var consciousnessSeasonRoom = consciousnessMaze != null
                    ? Dungeon.FindHellMapRoom(
                        77,
                        consciousnessMaze,
                        0,
                        difficulty: 1,
                        preferSeasonSealDoor: true)
                    : null;
                var consciousnessDifficultyARoom = consciousnessMaze != null
                    ? Dungeon.FindHellMapRoom(
                        77,
                        consciousnessMaze,
                        0,
                        difficulty: 1)
                    : null;
                var consciousnessOrdinaryRoom = consciousnessMaze != null
                    ? Dungeon.FindHellMapRoom(
                        77,
                        consciousnessMaze,
                        0,
                        difficulty: 2)
                    : null;
                Check(
                    "PVF season route is explicit and independent from A/B difficulty",
                    HellPartyData.IsSeasonHellPartyEnabled()
                    && consciousnessMaze != null
                    && consciousnessMaze.SealDoorMapIndex == 60064
                    && consciousnessMaze.SealDoorPos != null
                    && consciousnessMaze.SealDoorPos.Length >= 2
                    && consciousnessMaze.SealDoorPos[0] == 3
                    && consciousnessMaze.SealDoorPos[1] == 1
                    && consciousnessMaze.SeasonSealDoorMapIndex == 91020
                    && consciousnessMaze.SeasonSealDoorPos != null
                    && consciousnessMaze.SeasonSealDoorPos.Length >= 2
                    && consciousnessMaze.SeasonSealDoorPos[0] == 1
                    && consciousnessMaze.SeasonSealDoorPos[1] == 0
                    && consciousnessSeasonRoom != null
                    && consciousnessSeasonRoom.Found
                    && consciousnessSeasonRoom.MapId == 91020
                    && consciousnessSeasonRoom.NormalMapId > 0
                    && consciousnessSeasonRoom.X == 1
                    && consciousnessSeasonRoom.Y == 0
                    && consciousnessDifficultyARoom != null
                    && consciousnessDifficultyARoom.Found
                    && consciousnessDifficultyARoom.MapId == 60064
                    && consciousnessDifficultyARoom.X == 3
                    && consciousnessDifficultyARoom.Y == 1
                    && consciousnessOrdinaryRoom != null
                    && consciousnessOrdinaryRoom.Found
                    && consciousnessOrdinaryRoom.MapId == 60064
                    && consciousnessOrdinaryRoom.NormalMapId > 0
                    && consciousnessOrdinaryRoom.X == 3
                    && consciousnessOrdinaryRoom.Y == 1,
                    ref failures);

                var trombeDungeon = Dungeon.GetDungeonFile(103);
                var trombeMaze = trombeDungeon?.Mazes != null
                    && trombeDungeon.Mazes.Count > 0
                    ? trombeDungeon.Mazes[0]
                    : null;
                var trombeSeasonRoom = trombeMaze != null
                    ? Dungeon.FindHellMapRoom(
                        103,
                        trombeMaze,
                        0,
                        difficulty: 1,
                        preferSeasonSealDoor: true)
                    : null;
                var trombeDifficultyARoom = trombeMaze != null
                    ? Dungeon.FindHellMapRoom(103, trombeMaze, 0, difficulty: 1)
                    : null;
                var grandineDungeon = Dungeon.GetDungeonFile(104);
                var grandineHellMaze = grandineDungeon?.Mazes != null
                    && grandineDungeon.Mazes.Count > 0
                    ? grandineDungeon.Mazes[0]
                    : null;
                var grandineOrdinaryRoom = grandineHellMaze != null
                    ? Dungeon.FindHellMapRoom(
                        104,
                        grandineHellMaze,
                        0,
                        difficulty: 2)
                    : null;
                Check(
                    "PVF room owner keeps A/B difficulty on ordinary route and reserves season route",
                    trombeSeasonRoom != null
                    && trombeSeasonRoom.Found
                    && trombeSeasonRoom.MapId == 91007
                    && trombeSeasonRoom.X == 1
                    && trombeSeasonRoom.Y == 0
                    && trombeDifficultyARoom != null
                    && trombeDifficultyARoom.Found
                    && trombeDifficultyARoom.MapId == 60069
                    && trombeDifficultyARoom.X == 3
                    && trombeDifficultyARoom.Y == 0
                    && grandineOrdinaryRoom != null
                    && grandineOrdinaryRoom.Found
                    && grandineOrdinaryRoom.MapId == 60070
                    && grandineOrdinaryRoom.X == 2
                    && grandineOrdinaryRoom.Y == 1,
                ref failures);

            var manualHellModes = new HashSet<byte>();
            for (var index = 0; index < 256; index++)
                manualHellModes.Add(HellPartyData.ResolveManualHellPartyMode());
            Check(
                "A21 manual hell entry uses PVF A/B weights instead of fixed ordinary mode",
                manualHellModes.Contains(1)
                && manualHellModes.Contains(2),
                ref failures);

            var banquetDungeon = Dungeon.GetDungeonFile(194);
                var banquetMaze = banquetDungeon?.Mazes != null
                    && banquetDungeon.Mazes.Count > 0
                    ? banquetDungeon.Mazes[0]
                    : null;
                var banquetSeasonRoom = banquetMaze != null
                    ? Dungeon.FindHellMapRoom(
                        194,
                        banquetMaze,
                        0,
                        difficulty: 1,
                        preferSeasonSealDoor: true)
                    : null;
                Check(
                    "PVF mode owner rule is shared across the Castle of the Dead region",
                    banquetSeasonRoom != null
                    && banquetSeasonRoom.Found
                    && banquetSeasonRoom.MapId == 91013
                    && banquetSeasonRoom.X == 4
                    && banquetSeasonRoom.Y == 2,
                    ref failures);
            }

            Console.WriteLine(
                failures == 0
                    ? "A21_TUTORIAL_PROTOCOL selftest passed."
                    : $"A21_TUTORIAL_PROTOCOL selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyQuestMazeMapEntranceAffinity(ref int failures)
        {
            var dungeon = Dungeon.GetDungeonFile(72);
            var maze = dungeon?.Mazes?.FirstOrDefault(candidate =>
                candidate?.QuestConnection != null
                && candidate.QuestConnection.Length >= 2
                && candidate.QuestConnection[0] == 0
                && candidate.QuestConnection[1] == 15145);

            var resolved = maze == null
                ? -1
                : DungeonMapResolver.ResolveMapId(
                    72,
                    1,
                    2,
                    maze,
                    0,
                    maze.BossMap);

            var allExplicitRoomsKeepGateTopology = maze != null;
            if (allExplicitRoomsKeepGateTopology)
            {
                var roomKeys = new HashSet<long>();
                foreach (var specification in maze.MapSpecifications)
                {
                    if (specification == null
                        || !roomKeys.Add(
                            ((long)specification.X << 32)
                            | (uint)specification.Y))
                    {
                        continue;
                    }

                    var mapId = DungeonMapResolver.ResolveMapId(
                        72,
                        specification.X,
                        specification.Y,
                        maze,
                        0,
                        maze.BossMap);
                    if (!DungeonMapResolver.TryGetMazeCellGreed(
                            maze,
                            specification.X,
                            specification.Y,
                            out var roomGreed)
                        || !DungeonMapResolver.TryDecodeGreedSymbol(
                            roomGreed,
                            out var roomMask)
                        || !DungeonMapResolver.TryGetMapEntranceMask(
                            mapId,
                            out var mapMask)
                        || roomMask != mapMask)
                    {
                        allExplicitRoomsKeepGateTopology = false;
                        break;
                    }
                }
            }

            Check(
                "quest Maze 15145 selects the MAP whose gate layout matches the maze",
                maze != null
                && resolved == 32986
                && DungeonMapResolver.TryGetMazeCellGreed(
                    maze,
                    1,
                    2,
                    out var cellGreed)
                && DungeonMapResolver.TryDecodeGreedSymbol(
                    cellGreed,
                    out var expectedMask)
                && DungeonMapResolver.TryGetMapEntranceMask(
                    resolved,
                    out var resolvedMask)
                && expectedMask == resolvedMask,
                ref failures);
            Check(
                "quest Maze 15145 explicit rooms keep PVF gate topology",
                allExplicitRoomsKeepGateTopology,
                ref failures);
        }

        private static void FillSkillPagesFromCapturedFinishBody(
            QuestFinishResult result,
            byte[] capturedBody,
            int offset)
        {
            result.SkillPages.Clear();
            for (var pageIndex = 0; pageIndex < 2; pageIndex++)
            {
                var page = new QuestFinishSkillPage();
                var count = capturedBody[offset++];
                for (var index = 0; index < count; index++)
                {
                    page.Entries.Add(new QuestFinishSkillEntry
                    {
                        Slot = capturedBody[offset],
                        SkillId = BitConverter.ToUInt16(capturedBody, offset + 1),
                        Level = capturedBody[offset + 3],
                    });
                    offset += 4;
                }

                result.SkillPages.Add(page);
            }
        }

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }

        private static bool ContainsBytes(byte[] haystack, byte[] needle)
        {
            if (haystack == null || needle == null || needle.Length == 0)
                return false;

            for (var offset = 0; offset <= haystack.Length - needle.Length; offset++)
            {
                var match = true;
                for (var index = 0; index < needle.Length; index++)
                {
                    if (haystack[offset + index] != needle[index])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                    return true;
            }

            return false;
        }

        private static bool ContainsInt(
            IReadOnlyList<int> values,
            int expected)
        {
            if (values == null)
                return false;

            for (var index = 0; index < values.Count; index++)
                if (values[index] == expected)
                    return true;

            return false;
        }

        private static bool QuestRewardsMatchNonExperience(
            QuestReward left,
            QuestReward right)
        {
            if (left.Gold != right.Gold
                || left.ChainType != right.ChainType
                || left.GrowNumber != right.GrowNumber
                || left.Items == null
                || right.Items == null
                || left.Items.Count != right.Items.Count)
            {
                return false;
            }

            for (var index = 0; index < left.Items.Count; index++)
            {
                if (left.Items[index].ItemId != right.Items[index].ItemId
                    || left.Items[index].Count != right.Items[index].Count)
                {
                    return false;
                }
            }

            return true;
        }

        private static DungeonRun CreateQuestRun(
            short dungeonId,
            byte difficulty,
            ushort activeQuestMazeQuestId,
            ushort snapshotQuestId)
        {
            var run = new DungeonRun(dungeonId, difficulty)
            {
                ActiveQuestMazeQuestId = activeQuestMazeQuestId,
                QuestSnapshot = QuestRunSnapshot.Capture(
                    new[]
                    {
                        new ActiveQuest
                        {
                            QuestId = snapshotQuestId,
                        },
                    }),
            };
            return run;
        }

        private static string FormatInts(int[] values)
            => values == null ? "null" : $"[{string.Join(",", values)}]";
    }
}
