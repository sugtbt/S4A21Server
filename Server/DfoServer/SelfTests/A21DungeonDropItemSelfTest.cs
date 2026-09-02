using DfoServer.Game.Accounts;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Dungeon;
using PvfLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;

namespace DfoServer.SelfTests
{
    public static class A21DungeonDropItemSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== A21_DUNGEON_DROP_ITEM selftest ===");
            var failures = 0;

            Check(
                "DROP_ITEM success ACK matches A21 client parser",
                BytesEqual(
                    DropItemBuilder.BuildDropSuccessAck(0, 3, 1),
                    new byte[] { 0x01, 0x00, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00 }),
                ref failures);

            var core = ItemCore.Create(ItemCore.KindConsumable, 3030);
            core.Count = 1;
            var drop = new DropInfo
            {
                SceneSlot = 0x0066,
                TemplateId = 3030,
                StackCount = 1,
                Core = core,
                SourceSlotIndex = 3,
                IsPlayerDropped = true,
            };

            var body = DropItemBuilder.BuildDrop(
                dropperActorId: 0x0DDB,
                positionX: 0x00BA,
                positionY: 0x00D8,
                drop: drop,
                ownerActorId: 0);

            Check(
                "DROP_ITEM ground notification carries A21 101B item entry",
                body.Length == 112
                && ReadUInt16(body, 0) == 0x0DDB
                && ReadUInt16(body, 2) == 0x00BA
                && ReadUInt16(body, 4) == 0x00D8
                && ReadUInt16(body, 6) == 0x0066
                && ReadInt16(body, 8) == 3
                && ReadInt32(body, 10) == 3030
                && ReadInt32(body, 14) == 1
                && body[109] == 0
                && ReadUInt16(body, 110) == 0,
                ref failures);

            VerifyIndependentDropParser(ref failures);
            VerifyRealPvfIndependentDrop(ref failures);
            VerifyDimensionGateParser(ref failures);
            VerifyEpicBuffPotionRarityReroll(ref failures);
            VerifyRealPvfDimensionDrop(ref failures);
            VerifySoulRechallengeContract(ref failures);
            VerifySkillPointBooks(ref failures);

            Console.WriteLine(
                failures == 0
                    ? "A21_DUNGEON_DROP_ITEM selftest passed."
                    : $"A21_DUNGEON_DROP_ITEM selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }

        private static bool BytesEqual(byte[] actual, byte[] expected)
        {
            if (actual == null || expected == null || actual.Length != expected.Length)
                return false;

            for (var index = 0; index < actual.Length; index++)
            {
                if (actual[index] != expected[index])
                    return false;
            }

            return true;
        }

        private static short ReadInt16(byte[] data, int offset)
            => BitConverter.ToInt16(data, offset);

        private static ushort ReadUInt16(byte[] data, int offset)
            => BitConverter.ToUInt16(data, offset);

        private static int ReadInt32(byte[] data, int offset)
            => BitConverter.ToInt32(data, offset);

        private static void VerifySoulRechallengeContract(ref int failures)
        {
            var body = DungeonNotificationBuilder.BuildEplpRechallengeReady();
            Check(
                "Soul rechallenge uses the exact native A21 one-byte success result",
                (ushort)NotiPacketTypeA21.EPLP_RECHALLENGE == 0x0105
                && body.Length == 1
                && body[0] == 9,
                ref failures);

            Check(
                "Soul classification requires both PVF ancient and risk flags",
                DungeonCatalog.IsSoulDungeon(new DungeonFile
                {
                    AncientDungeon = true,
                    RiskDungeon = true,
                })
                && !DungeonCatalog.IsSoulDungeon(new DungeonFile
                {
                    AncientDungeon = true,
                })
                && !DungeonCatalog.IsSoulDungeon(new DungeonFile
                {
                    RiskDungeon = true,
                })
                && !DungeonCatalog.IsSoulDungeon((DungeonFile)null),
                ref failures);

            Check(
                "Soul rechallenge notification does not widen nonstandard or non-Soul clears",
                DungeonSettlementHandler.ShouldProjectSoulRechallengeReady(
                    standardPresentation: true,
                    isSoulDungeon: true)
                && !DungeonSettlementHandler.ShouldProjectSoulRechallengeReady(
                    standardPresentation: false,
                    isSoulDungeon: true)
                && !DungeonSettlementHandler.ShouldProjectSoulRechallengeReady(
                    standardPresentation: true,
                    isSoulDungeon: false),
                ref failures);
        }

        private static bool IsPacket(
            byte[] packet,
            byte command,
            ushort type)
            => packet != null
            && packet.Length >= 15
            && packet[0] == command
            && BitConverter.ToUInt16(packet, 1) == type;

        private static void VerifyIndependentDropParser(ref int failures)
        {
            const string existingRow =
                "0 61424 3015 1000000 1000000 1000000 1000000 1000000 1 1 1 1 1 0 0 -1 0";
            const string newMonsterRow =
                "0 999001 0 1000000 1000000 1000000 1000000 1000000 1 1 1 1 1 0 0 -1 1";

            var arrows = IndependentDropDefinitionCatalog.ParseMonsterEntriesFromText(
                "#PVF_File\n" +
                "[independent drop]\n" +
                "0 → 61424 → 3015 → 1000000 → 1000000 → 1000000 → 1000000 → 1000000 → 1 → 1 → 1 → 1 → 1 → 0 → 0 → -1 → 0\n" +
                "0 → 999001 → 0 → 1000000 → 1000000 → 1000000 → 1000000 → 1000000 → 1 → 1 → 1 → 1 → 1 → 0 → 0 → -1 → 1\n" +
                "[list]\n" +
                "108040343 → 100\n" +
                "101010731 → 100 // copied from the PVF editor\n" +
                "[/list]\n" +
                "[/independent drop]\n");
            Check(
                "PVF-editor arrows keep existing independent-drop rows",
                arrows.TryGetValue(61424, out var existingFromArrows)
                && existingFromArrows.Length == 1
                && existingFromArrows[0].ItemId == 3015,
                ref failures);
            Check(
                "PVF-editor arrows keep newly added monster independent-drop rows",
                arrows.TryGetValue(999001, out var addedFromArrows)
                && addedFromArrows.Length == 1
                && addedFromArrows[0].HasItemPool
                && addedFromArrows[0].TryResolvePool(-1, out var addedPool)
                && addedPoolItemIds(addedPool).SetEquals(new[] { 108040343, 101010731 }),
                ref failures);

            var glued = IndependentDropDefinitionCatalog.ParseMonsterEntriesFromText(
                "[independent drop]\n" +
                "0→61424→3015→1000000→1000000→1000000→1000000→1000000→1→1→1→1→1→0→0→-1→0\n" +
                "[/independent drop]\n");
            Check(
                "glued arrow separators still parse a 17-column independent-drop row",
                glued.TryGetValue(61424, out var gluedEntries)
                && gluedEntries.Length == 1
                && gluedEntries[0].ItemId == 3015,
                ref failures);

            var recovered = IndependentDropDefinitionCatalog.ParseMonsterEntriesFromText(
                "[independent drop]\n" +
                existingRow + "\n" +
                "hello\n" +
                "0 20 14400 1000000 1000000 1000000 1000000 1000000 1 1 1 1 1 0 0 -1 0\n" +
                "[/independent drop]\n");
            Check(
                "a non-integer token does not discard later independent-drop rows",
                recovered.ContainsKey(61424) && recovered.ContainsKey(20),
                ref failures);

            var trailing = IndependentDropDefinitionCatalog.ParseMonsterEntriesFromText(
                "[independent drop]\n" +
                existingRow + "\n" +
                "[/independent drop]\n" +
                newMonsterRow + "\n" +
                "[list]\n" +
                "108040343 100\n" +
                "101010731 100\n" +
                "[/list]\n");
            Check(
                "rows appended after [/independent drop] still load",
                trailing.ContainsKey(61424)
                && trailing.TryGetValue(999001, out var trailingAdded)
                && trailingAdded.Length == 1
                && trailingAdded[0].HasItemPool,
                ref failures);

            var vanilla = IndependentDropDefinitionCatalog.ParseMonsterEntriesFromText(
                "[independent drop]\n" +
                existingRow + "\n" +
                "0 20 0 1000000 1000000 1000000 1000000 1000000 1 1 1 1 1 0 0 -1 1\n" +
                "[list]\n" +
                "14400 1000\n" +
                "[/list]\n" +
                "[/independent drop]\n");
            Check(
                "vanilla 17-column rows still load direct and inline-list entries",
                vanilla.TryGetValue(61424, out var vanillaDirect)
                && vanillaDirect.Length == 1
                && vanillaDirect[0].ItemId == 3015
                && vanilla.TryGetValue(20, out var vanillaList)
                && vanillaList[0].HasItemPool
                && vanillaList[0].TryResolvePool(-1, out var vanillaPool)
                && addedPoolItemIds(vanillaPool).SetEquals(new[] { 14400 }),
                ref failures);

            var typeOneThenZero = IndependentDropDefinitionCatalog.ParseMonsterEntriesFromText(
                "[independent drop]\n" +
                "1 1606 3015 4000000 4000000 4000000 4000000 4000000 1 1 1 1 1 0 99 -1 0\n" +
                existingRow + "\n" +
                "[/independent drop]\n");
            Check(
                "type-1 rows stay skipped without dropping later type-0 rows",
                !typeOneThenZero.ContainsKey(1606)
                && typeOneThenZero.ContainsKey(61424),
                ref failures);

            static HashSet<int> addedPoolItemIds(
                IndependentDropWeightedPoolDefinition pool)
            {
                var ids = new HashSet<int>();
                if (pool == null)
                    return ids;
                foreach (var item in pool.Items)
                    ids.Add(item.ItemId);
                return ids;
            }
        }

        private static void VerifyRealPvfIndependentDrop(ref int failures)
        {
            var pvfPath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(pvfPath) || !File.Exists(pvfPath))
            {
                Console.WriteLine("real PVF independent-drop checks skipped: PVF_ARCHIVE_PATH is not set");
                return;
            }

            Check(
                "real PVF independent drop table loads multiple same-monster entries",
                IndependentDropDefinitionCatalog.HasMonsterDefinition(56675)
                && IndependentDropDefinitionCatalog.TryGetMonsterEntries(
                    56675,
                    out var entries)
                && entries.Count >= 5,
                ref failures);

            var slotCounter = (ushort)0;
            var drops = IndependentDropSystem.GenerateDrops(
                monsterCode: 56675,
                difficulty: 2,
                dungeonLevel: 85,
                partyMemberCount: 1,
                chronicleDropJobGroup: -1,
                lcg: new DnfLcg(0),
                slotCounter: ref slotCounter);
            var guaranteedDrop = drops
                .Where(drop => drop.TemplateId == 10093971)
                .ToArray();

            Check(
                "real PVF independent drop count uses the solo-player count column",
                guaranteedDrop.Length == 1
                && guaranteedDrop[0].StackCount == 1,
                ref failures);
        }

        private static void VerifyDimensionGateParser(ref int failures)
        {
            const string sample = @"
[chronicle grow type]
    12 0 # job and first grow
    [normal chronicle list]
        1001 1002
    [/normal chronicle list]
    [set chronicle list]
        2001 2002 // inline comment
    [/set chronicle list]
[/chronicle grow type]
[chronicle grow type]
    12 16
    [normal chronicle list]
        1003
    [/normal chronicle list]
    [set chronicle list]
        2003
    [/set chronicle list]
[/chronicle grow type]";

            var definitions =
                DimensionGateDropDefinitionCatalog.ParseDefinitions(sample);
            Check(
                "dimension gate parser keys grow type by the low 4 bits",
                definitions.Count == 1
                && definitions.TryGetValue((12, 0), out var definition)
                && definition.NormalItems.SequenceEqual(
                    new[] { 1001, 1002, 1003 })
                && definition.SetItems.SequenceEqual(
                    new[] { 2001, 2002, 2003 }),
                ref failures);
        }

        private static void VerifyEpicBuffPotionRarityReroll(ref int failures)
        {
            var inactiveCalls = 0;
            Check(
                "epic buff potion rarity reroll is skipped while inactive",
                HellMonsterDropConfig.ApplyEpicBuffPotionRarityRerollForSelfTest(
                    2,
                    false,
                    () =>
                    {
                        inactiveCalls++;
                        return 4;
                    }) == 2
                && inactiveCalls == 0,
                ref failures);

            var alreadyEpicCalls = 0;
            Check(
                "epic buff potion rarity reroll is skipped for initial epic",
                HellMonsterDropConfig.ApplyEpicBuffPotionRarityRerollForSelfTest(
                    4,
                    true,
                    () =>
                    {
                        alreadyEpicCalls++;
                        return 0;
                    }) == 4
                && alreadyEpicCalls == 0,
                ref failures);

            Check(
                "epic buff potion rarity reroll keeps first non-epic miss",
                HellMonsterDropConfig.ApplyEpicBuffPotionRarityRerollForSelfTest(
                    2,
                    true,
                    () => 3) == 2,
                ref failures);

            Check(
                "epic buff potion rarity reroll promotes only second epic roll",
                HellMonsterDropConfig.ApplyEpicBuffPotionRarityRerollForSelfTest(
                    2,
                    true,
                    () => 4) == 4,
                ref failures);
        }

        private static void VerifyRealPvfDimensionDrop(ref int failures)
        {
            var pvfPath = Environment.GetEnvironmentVariable("PVF_ARCHIVE_PATH");
            if (string.IsNullOrWhiteSpace(pvfPath) || !File.Exists(pvfPath))
            {
                Console.WriteLine("real PVF dimension-drop checks skipped: PVF_ARCHIVE_PATH is not set");
                return;
            }

            Check(
                "real PVF marks impossible Goblin Kingdom as a dimension dungeon",
                DfoServer.GameWorld.Dungeon.IsDimensionDungeon(62),
                ref failures);

            Check(
                "real PVF dimension gate table resolves first awakening grow type",
                DimensionGateDropDefinitionCatalog.DefinitionCount > 0
                && DungeonDropPolicy.Impossible.Allows(
                    DungeonMonsterDropSource.Dimension)
                && DimensionGateDropDefinitionCatalog.TryResolve(
                    0,
                    0x11,
                    out var definition)
                && definition.HasNormalItems
                && definition.HasSetItems,
                ref failures);

            if (!DimensionGateDropDefinitionCatalog.TryResolve(
                    0,
                    0x11,
                    out var resolved)
                || !resolved.HasNormalItems
                || !resolved.HasSetItems)
            {
                return;
            }

            Check(
                "dimension free card draws one normal chronicle equipment",
                DimensionDropSystem.TryCreateFreeCard(
                    0,
                    0x11,
                    new DnfLcg(1),
                    out var freeCard)
                && freeCard.IsEquipment
                && freeCard.StackCount == 1
                && resolved.NormalItems.Contains(freeCard.ItemId),
                ref failures);

            Check(
                "dimension paid card draws one set chronicle equipment",
                DimensionDropSystem.TryCreatePaidCard(
                    0,
                    0x11,
                    new DnfLcg(2),
                    out var paidCard)
                && paidCard.IsEquipment
                && paidCard.StackCount == 1
                && resolved.SetItems.Contains(paidCard.ItemId),
                ref failures);

            var eliteSlotCounter = (ushort)0;
            var eliteDrops = DimensionDropSystem.GenerateEliteDrops(
                0,
                0x11,
                new DnfLcg(3),
                ref eliteSlotCounter);
            Check(
                "dimension elite monster drops one chronicle item and one fragment",
                eliteDrops.Count == 2
                && resolved.CombinedItems.Contains((int)eliteDrops[0].TemplateId)
                && eliteDrops[1].TemplateId == DimensionDropSystem.FragmentItemId
                && eliteDrops[1].StackCount == 1,
                ref failures);

            var bossSlotCounter = (ushort)0;
            var bossDrops = DimensionDropSystem.GenerateBossDrops(
                0,
                0x11,
                new DnfLcg(4),
                ref bossSlotCounter);
            Check(
                "dimension boss monster drops normal, set, and two separate fragments",
                bossDrops.Count == 4
                && resolved.NormalItems.Contains((int)bossDrops[0].TemplateId)
                && resolved.SetItems.Contains((int)bossDrops[1].TemplateId)
                && bossDrops[2].TemplateId == DimensionDropSystem.FragmentItemId
                && bossDrops[2].StackCount == 1
                && bossDrops[3].TemplateId == DimensionDropSystem.FragmentItemId
                && bossDrops[3].StackCount == 1,
                ref failures);

            var dimensionSlotCounter = (ushort)0;
            var dimensionMonsterDrops = DimensionDropSystem.GenerateMonsterDrops(
                dungeonId: 62,
                monsterCode: 61340,
                characterJob: 0,
                growType: 0x11,
                lcg: new DnfLcg(5),
                slotCounter: ref dimensionSlotCounter);
            Check(
                "dimension drop entry requires a dimension dungeon and matched monster",
                dimensionMonsterDrops.Count == 2
                && resolved.CombinedItems.Contains(
                    (int)dimensionMonsterDrops[0].TemplateId)
                && dimensionMonsterDrops[1].TemplateId
                    == DimensionDropSystem.FragmentItemId,
                ref failures);

            var ordinarySlotCounter = (ushort)0;
            var ordinaryMonsterDrops = DimensionDropSystem.GenerateMonsterDrops(
                dungeonId: 1,
                monsterCode: 61340,
                characterJob: 0,
                growType: 0x11,
                lcg: new DnfLcg(6),
                slotCounter: ref ordinarySlotCounter);
            Check(
                "dimension drop entry does not run outside dimension dungeons",
                ordinaryMonsterDrops.Count == 0
                && ordinarySlotCounter == 0,
                ref failures);
        }

        private static void VerifySkillPointBooks(ref int failures)
        {
            const int accountId = 198031;
            const int characterId = 298031;
            const short book5Slot = 65;
            const short book20Slot = 66;
            const byte level = 50;
            const byte job = 0;
            const byte growType = 0;
            const uint exp = 1234;
            const int initialBonusSp = 7;

            var tempDirectory = Path.Combine(
                Path.GetTempPath(),
                $"a21_sp_books_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirectory);
            var database = new GameDatabase(
                Path.Combine(tempDirectory, "inventory.db"),
                ServerPaths.SchemaFilePath);
            ServerRuntimeBuilder runtime = null;
            EnhancedClientSession session = null;
            InventoryLease lease = null;
            LoopbackPacketCapture capture = null;
            try
            {
                using (var connection = database.OpenConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, 'sp-book-handler', '');
INSERT INTO characters (
    character_id, account_id, name, job, grow_type, level, exp,
    bonus_sp, bonus_tp
) VALUES (
    @cid, @aid, 'sp-book-character', @job, @grow, @level, @exp,
    @bonusSp, 0
);
INSERT INTO character_subtype0_fields(character_id) VALUES (@cid);
INSERT INTO character_subtype1_fields(character_id) VALUES (@cid);";
                    command.Parameters.AddWithValue("@aid", accountId);
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@job", job);
                    command.Parameters.AddWithValue("@grow", growType);
                    command.Parameters.AddWithValue("@level", level);
                    command.Parameters.AddWithValue("@exp", exp);
                    command.Parameters.AddWithValue("@bonusSp", initialBonusSp);
                    command.ExecuteNonQuery();
                }

                capture = new LoopbackPacketCapture();
                session = capture.Session;
                session.Account = new AccountRecord
                {
                    AccountId = accountId,
                    MId = "sp-book-handler",
                    PasswordHash = string.Empty,
                };
                session.Player.CharacterId = characterId;
                session.Player.UserId = 3031;
                session.Player.Level = level;
                session.Player.Job = job;
                session.Player.GrowType = growType;
                session.Player.Exp = exp;

                var sessions = new SessionDirectory();
                sessions.Register(characterId, session);
                runtime = new ServerRuntimeBuilder(database);
                var core = runtime.GetOrCreateGameProtocolCoreDependencies();
                var inventoryDependencies = runtime
                    .GetOrCreateGameProtocolInventoryDependencies(core);
                var world = runtime.GetOrCreateGameProtocolWorldDependencies(
                    sessions,
                    core);
                var inventoryHandler = runtime
                    .GetOrCreateGameProtocolCharacterInventoryHandlers(
                        core,
                        inventoryDependencies,
                        world)
                    .Inventory;

                InventoryService inventory;
                using (var connection = database.OpenConnection())
                {
                    inventory = InventoryService.LoadFromDb(
                        connection,
                        characterId,
                        accountId,
                        database);
                }
                if (!InventoryCreateService.TryCreateCore(
                        ExperienceItemUseService.SkillPointBook5ItemId,
                        ItemCreateReason.NpcShopPurchase,
                        2,
                        out var book5)
                    || !InventoryCreateService.TryCreateCore(
                        ExperienceItemUseService.SkillPointBook20ItemId,
                        ItemCreateReason.NpcShopPurchase,
                        1,
                        out var book20))
                {
                    throw new InvalidOperationException(
                        "failed to create real PVF skill-point books");
                }
                inventory.SetItem(InventoryListType.Main, book5Slot, book5);
                inventory.SetItem(InventoryListType.Main, book20Slot, book20);
                lease = InventoryContext.Register(
                    session.SessionId,
                    characterId,
                    inventory);
                if (!OnlineInventoryMutationCommitCoordinator.TryCommit(
                        lease,
                        "selftest-seed-skill-point-books"))
                {
                    throw new InvalidOperationException(
                        "failed to persist skill-point book fixtures");
                }

                var repository = new SqliteCharacterProgressRepository(database);
                var initialProtocol = SkillStateService.LoadProtocolState(
                    repository,
                    characterId,
                    job,
                    level,
                    initialBonusSp,
                    bonusTp: 0,
                    persist: false,
                    growType: growType);

                inventoryHandler.Handle_ENUM_CMDPACKET_INCREASE_STATUS(
                        session,
                        new GamePacketHeader(),
                        BitConverter.GetBytes(book5Slot))
                    .GetAwaiter()
                    .GetResult();
                var packets = capture.ReadPackets(minimumCount: 3);
                var firstAck = packets.LastOrDefault(packet => IsPacket(
                    packet,
                    0x01,
                    (ushort)CmdPacketType.INCREASE_STATUS));
                var firstExp = packets.LastOrDefault(packet => IsPacket(
                    packet,
                    0x00,
                    (ushort)NotiPacketTypeA21.EXP));
                Check(
                    "SP+5 book traverses the real INCREASE_STATUS handler and commits item, DB, online state, and absolute SP notification",
                    firstAck != null
                    && firstAck.Length >= 16
                    && firstAck[15] == 1
                    && firstExp != null
                    && ReadUInt16(firstExp, 15 + 9)
                        == initialProtocol.Page0Sp + 5
                    && ReadUInt16(firstExp, 15 + 11)
                        == initialProtocol.Page1Sp + 5
                    && ReadCharacterBonusSp(database, characterId)
                        == initialBonusSp + 5
                    && ReadMainSlotCount(
                        database,
                        characterId,
                        accountId,
                        book5Slot) == 1
                    && lease.Inventory.GetItem(
                        InventoryListType.Main,
                        book5Slot)?.Count == 1,
                    ref failures);

                inventoryHandler.Handle_ENUM_CMDPACKET_INCREASE_STATUS(
                        session,
                        new GamePacketHeader(),
                        BitConverter.GetBytes(book20Slot))
                    .GetAwaiter()
                    .GetResult();
                packets = capture.ReadPackets(minimumCount: 3);
                var secondAck = packets.LastOrDefault(packet => IsPacket(
                    packet,
                    0x01,
                    (ushort)CmdPacketType.INCREASE_STATUS));
                var secondExp = packets.LastOrDefault(packet => IsPacket(
                    packet,
                    0x00,
                    (ushort)NotiPacketTypeA21.EXP));
                Check(
                    "SP+20 book adds exactly 20 more SP and consumes its last item through the same handler chain",
                    secondAck != null
                    && secondAck.Length >= 16
                    && secondAck[15] == 1
                    && secondExp != null
                    && ReadUInt16(secondExp, 15 + 9)
                        == initialProtocol.Page0Sp + 25
                    && ReadUInt16(secondExp, 15 + 11)
                        == initialProtocol.Page1Sp + 25
                    && ReadCharacterBonusSp(database, characterId)
                        == initialBonusSp + 25
                    && ReadMainSlotCount(
                        database,
                        characterId,
                        accountId,
                        book20Slot) == 0
                    && lease.Inventory.GetItem(
                        InventoryListType.Main,
                        book20Slot) == null,
                    ref failures);

                using (var connection = database.OpenConnection())
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = $@"
CREATE TRIGGER fail_skill_point_book_update
BEFORE UPDATE OF bonus_sp ON characters
WHEN OLD.character_id = {characterId}
BEGIN
    SELECT RAISE(ABORT, 'injected skill-point persistence failure');
END;";
                    command.ExecuteNonQuery();
                }

                inventoryHandler.Handle_ENUM_CMDPACKET_INCREASE_STATUS(
                        session,
                        new GamePacketHeader(),
                        BitConverter.GetBytes(book5Slot))
                    .GetAwaiter()
                    .GetResult();
                packets = capture.ReadPackets(minimumCount: 1);
                var failedAck = packets.LastOrDefault(packet => IsPacket(
                    packet,
                    0x01,
                    (ushort)CmdPacketType.INCREASE_STATUS));
                Check(
                    "SP book persistence failure returns an error and consumes nothing online or in SQLite",
                    failedAck != null
                    && failedAck.Length >= 17
                    && failedAck[15] == 0
                    && !packets.Any(packet => IsPacket(
                        packet,
                        0x00,
                        (ushort)NotiPacketTypeA21.EXP))
                    && ReadCharacterBonusSp(database, characterId)
                        == initialBonusSp + 25
                    && ReadMainSlotCount(
                        database,
                        characterId,
                        accountId,
                        book5Slot) == 1
                    && lease.Inventory.GetItem(
                        InventoryListType.Main,
                        book5Slot)?.Count == 1,
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                Check(
                    "skill-point book handler/service harness completes",
                    false,
                    ref failures);
            }
            finally
            {
                if (lease != null && session != null)
                {
                    InventoryContext.Unregister(
                        session.SessionId,
                        characterId);
                }
                capture?.Dispose();
                runtime?.Dispose();
                try
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
                catch
                {
                }
            }
        }

        private static int ReadCharacterBonusSp(
            GameDatabase database,
            int characterId)
        {
            using (var connection = database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT bonus_sp FROM characters WHERE character_id = @cid;";
                command.Parameters.AddWithValue("@cid", characterId);
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        private static int ReadMainSlotCount(
            GameDatabase database,
            int characterId,
            int accountId,
            short slotIndex)
        {
            using (var connection = database.OpenConnection())
            {
                var inventory = InventoryService.LoadFromDb(
                    connection,
                    characterId,
                    accountId,
                    database);
                return inventory.GetItem(
                    InventoryListType.Main,
                    slotIndex)?.Count ?? 0;
            }
        }

        private sealed class LoopbackPacketCapture : IDisposable
        {
            private readonly TcpClient _reader;

            internal LoopbackPacketCapture()
            {
                var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                try
                {
                    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    _reader = new TcpClient();
                    var connect = _reader.ConnectAsync(
                        IPAddress.Loopback,
                        port);
                    var writer = listener.AcceptTcpClient();
                    connect.GetAwaiter().GetResult();
                    _reader.ReceiveTimeout = 1000;
                    Session = new EnhancedClientSession(
                        writer,
                        new GamePacketHeader());
                }
                finally
                {
                    listener.Stop();
                }
            }

            internal EnhancedClientSession Session { get; }

            internal List<byte[]> ReadPackets(int minimumCount)
            {
                if (minimumCount <= 0)
                    throw new ArgumentOutOfRangeException(nameof(minimumCount));

                var packets = new List<byte[]>();
                var stream = _reader.GetStream();
                var deadline = DateTime.UtcNow.AddSeconds(1);
                while (DateTime.UtcNow < deadline)
                {
                    var needsMinimum = packets.Count < minimumCount;
                    var wait = needsMinimum
                        ? deadline - DateTime.UtcNow
                        : TimeSpan.FromMilliseconds(75);
                    if (wait <= TimeSpan.Zero)
                        break;
                    var waitMicroseconds = (int)Math.Min(
                        int.MaxValue,
                        Math.Max(1, wait.TotalMilliseconds * 1000));
                    if (!_reader.Client.Poll(
                            waitMicroseconds,
                            SelectMode.SelectRead))
                    {
                        if (!needsMinimum)
                            break;
                        continue;
                    }
                    if (_reader.Client.Available <= 0)
                        throw new EndOfStreamException();

                    var header = ReadExact(stream, 15);
                    var length = BitConverter.ToInt32(header, 3);
                    if (length < 15)
                    {
                        throw new InvalidOperationException(
                            $"invalid captured packet length {length}");
                    }

                    var packet = new byte[length];
                    Buffer.BlockCopy(header, 0, packet, 0, header.Length);
                    if (length > header.Length)
                    {
                        var body = ReadExact(stream, length - header.Length);
                        Buffer.BlockCopy(
                            body,
                            0,
                            packet,
                            header.Length,
                            body.Length);
                    }
                    packets.Add(packet);
                }
                if (packets.Count < minimumCount)
                {
                    throw new TimeoutException(
                        $"captured {packets.Count}/{minimumCount} packets");
                }
                return packets;
            }

            public void Dispose()
            {
                Session?.Close();
                _reader?.Close();
            }

            private static byte[] ReadExact(NetworkStream stream, int count)
            {
                var result = new byte[count];
                var offset = 0;
                while (offset < count)
                {
                    var read = stream.Read(result, offset, count - offset);
                    if (read <= 0)
                        throw new EndOfStreamException();
                    offset += read;
                }
                return result;
            }
        }
    }
}
