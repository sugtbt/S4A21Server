using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers.Dungeon;
using DfoServer.Network.Parsers.Dungeon;
using DfoServer.Sqlite;
using Microsoft.Data.Sqlite;

namespace DfoServer.SelfTests
{
    public static class LicensedDungeonSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== LICENSED_DUNGEON selftest ===");
            var failures = 0;
            var tempDbPath = Path.Combine(
                Path.GetTempPath(),
                $"dfo_license_dungeon_{Guid.NewGuid():N}.db");
            var migrationDbPath = tempDbPath + ".migration";

            try
            {
                VerifyCatalog(ref failures);
                VerifyPacketBodies(ref failures);
                VerifyPlayResultRequest(ref failures);
                VerifyRequestRewardRequest(ref failures);
                VerifyRewardRuntime(ref failures);
                VerifyPeriodBoundaries(ref failures);
                VerifyMigration(migrationDbPath, ref failures);
                VerifyPersistentAdmission(tempDbPath, ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LICENSED_DUNGEON] EXCEPTION: {ex}");
                failures++;
            }
            finally
            {
                DeleteIfExists(tempDbPath);
                DeleteIfExists(tempDbPath + "-wal");
                DeleteIfExists(tempDbPath + "-shm");
                DeleteIfExists(migrationDbPath);
                DeleteIfExists(migrationDbPath + "-wal");
                DeleteIfExists(migrationDbPath + "-shm");
            }

            Console.WriteLine(
                failures == 0
                    ? "LICENSED_DUNGEON selftest passed."
                    : $"LICENSED_DUNGEON selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyCatalog(ref int failures)
        {
            var definitions = LicensedDungeonCatalog.Definitions.ToList();
            var bossDefinitions = definitions
                .Where(definition => definition.BossRule != null)
                .ToList();
            Check(
                "current PVF exposes twelve licensed dungeons in worldmap 38",
                definitions.Count == 12
                    && LicensedDungeonCatalog.WorldMapAreaId == 38,
                ref failures);
            Check(
                "PVF keeps two daily entries and two monthly group appearances",
                LicensedDungeonCatalog.DailyEnterCount == 2
                    && LicensedDungeonCatalog.GroupAppearCountPerMonth == 2,
                ref failures);
            Check(
                "licensed dungeons suppress the ordinary card layout only",
                !DungeonSettlementHandler.ShouldScheduleCardRewardFlow(5008)
                    && DungeonSettlementHandler.ShouldScheduleCardRewardFlow(1),
                ref failures);
            Check(
                "licensed dungeons use a dedicated whitelist drop policy",
                DungeonDropDefinitionCatalog.Resolve(5006).Kind
                    == DungeonDropDefinitionKind.Licensed
                    && DungeonDropDefinitionCatalog.Resolve(5006)
                        .Policy.AllowedSources
                        == DungeonMonsterDropSource.None
                    && DungeonDropDefinitionCatalog.Resolve(1).Kind
                        == DungeonDropDefinitionKind.Standard,
                ref failures);
            Check(
                "licensed clear uses a dedicated presentation kind",
                !DungeonClearPresentationPolicy.UsesStandardResultProjection(
                    DungeonClearPresentationKind.LicensedDungeon)
                    && DungeonClearPresentationPolicy
                        .UsesCommonExperienceAuthority(
                            DungeonClearPresentationKind.LicensedDungeon),
                ref failures);
            Check(
                "daily entry cap is bypassed for the current test build",
                !LicensedDungeonService.EnforceDailyEntryLimit,
                ref failures);
            Check(
                "PVF parses license clear rewards and group drop weights",
                LicensedDungeonCatalog.GetDungeonClearRewards(1).Count == 1
                    && LicensedDungeonCatalog.GetDungeonClearRewards(1)[0].ItemId
                        == 10155143
                    && LicensedDungeonCatalog.GetDungeonClearRewards(1)[0].Count == 1
                    && LicensedDungeonCatalog.GetGroupDropItems(4).Count == 15,
                ref failures);
            Check(
                "PVF parses per-dungeon daily clear rewards",
                LicensedDungeonCatalog.TryGetDailyClearReward(
                    5008,
                    out var dailyReward)
                    && dailyReward.ItemId == 10155137
                    && dailyReward.Count == 2,
                ref failures);
            Check(
                "license three/four bind six native one-normal/three-group maze rules",
                bossDefinitions.Count == 6
                    && bossDefinitions.All(definition =>
                        definition.BossRule.OrdinaryMazeIndex == 0
                        && definition.BossRule.BossMazeIndices.Count == 3
                        && definition.BossRule.BossMapIds.Count == 1)
                    && bossDefinitions
                        .SelectMany(definition => definition.BossRule.BossMapIds)
                        .Distinct()
                        .Count() == 6,
                ref failures);

            var entranceThree = definitions.Single(
                definition => definition.DungeonId == 5008);
            Check(
                "dungeon 5008 uses master difficulty and Sunday/Monday/Thursday",
                entranceThree.LicenseLevel == 3
                    && entranceThree.Difficulty == 2
                    && entranceThree.OpenDayIndexes.OrderBy(day => day)
                        .SequenceEqual(new[] { 0, 1, 4 }),
                ref failures);
            Check(
                "monthly entry rates use the PVF ten-thousand scale",
                LicensedDungeonCatalog.ResolveGroupAppearRate(1) == 10
                    && LicensedDungeonCatalog.ResolveGroupAppearRate(25) == 10000
                    && LicensedDungeonCatalog.ResolveGroupAppearRate(33) == 10000,
                ref failures);
        }

        private static void VerifyPacketBodies(ref int failures)
        {
            Check(
                "CHARAC_DUNGEON_LICENSE_INFO starts at the lowest license per group",
                LicensedDungeonPacketBuilder.BuildCharacterLicenseInfo(
                        LicensedDungeonCatalog.GetInitialLicenseRecords())
                    .SequenceEqual(new byte[]
                    {
                        0x03, 0x00,
                        0x8E, 0x13, 0x00, 0x00,
                        0x01, 0x00, 0x00, 0x00,
                        0x00, 0x00, 0x00, 0x00,
                        0x98, 0x13, 0x00, 0x00,
                        0x01, 0x00, 0x00, 0x00,
                        0x00, 0x00, 0x00, 0x00,
                        0xA2, 0x13, 0x00, 0x00,
                        0x01, 0x00, 0x00, 0x00,
                        0x00, 0x00, 0x00, 0x00,
                    }),
                ref failures);
            Check(
                "licensed dungeon day/shot/incount bodies are one byte",
                LicensedDungeonPacketBuilder.BuildDayIndex(4)
                    .SequenceEqual(new byte[] { 4 })
                    && LicensedDungeonPacketBuilder.BuildShotCount()
                        .SequenceEqual(new byte[] { 0 })
                    && LicensedDungeonPacketBuilder.BuildRemainingEnterCount(2)
                        .SequenceEqual(new byte[] { 2 }),
                ref failures);
            Check(
                "licensed boss clear preamble uses the verified 4-byte body",
                DungeonNotificationBuilder.BuildBossDieCheck(
                        result: 1,
                        state: 1,
                        bossSequence: 0x2729)
                    .SequenceEqual(new byte[] { 0x01, 0x01, 0x29, 0x27 }),
                ref failures);
            Check(
                "LICENSE_DUNGEON_CLEAR_INFO projects elapsed time and ETC rewards",
                LicensedDungeonPacketBuilder.BuildClearInfo(
                        groupBossPresent: false,
                        clearTimeMilliseconds: 30_818,
                        dungeonClearReward:
                            new LicensedDungeonRewardDisplayItem(10155143, 1),
                        dailyClearReward:
                            new LicensedDungeonRewardDisplayItem(10155139, 1))
                    .SequenceEqual(new byte[]
                    {
                        0x00,
                        0x62, 0x78, 0x00, 0x00,
                        0x83, 0xF4, 0x9A, 0x00,
                        0x01, 0x00, 0x00, 0x00,
                        0x87, 0xF4, 0x9A, 0x00,
                        0x01, 0x00, 0x00, 0x00,
                    }),
                ref failures);
            Check(
                "licensed clear hidden-card flag follows the selected group maze",
                LicensedDungeonPacketBuilder.BuildClearInfo(
                        groupBossPresent: true,
                        clearTimeMilliseconds: -1,
                        dungeonClearReward:
                            new LicensedDungeonRewardDisplayItem(1, 2),
                        dailyClearReward:
                            new LicensedDungeonRewardDisplayItem(3, 4))
                    .SequenceEqual(new byte[]
                    {
                        0x01,
                        0x00, 0x00, 0x00, 0x00,
                        0x03, 0x00, 0x00, 0x00,
                        0x04, 0x00, 0x00, 0x00,
                        0x01, 0x00, 0x00, 0x00,
                        0x02, 0x00, 0x00, 0x00,
                    }),
                ref failures);
        }

        private static void VerifyPlayResultRequest(ref int failures)
        {
            var body = new byte[]
            {
                0x01,
                0xEB, 0x03,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00,
                0x32,
            };
            Check(
                "A21 licensed play-result accepts the 11-byte business body",
                LicensedDungeonPlayResultRequest.TryParse(
                    body,
                    out var request,
                    out _)
                    && request.Body.SequenceEqual(body),
                ref failures);
            Check(
                "official 16-byte capture body is retained without accepting arbitrary lengths",
                !LicensedDungeonPlayResultRequest.TryParse(
                    body.Take(body.Length - 1).ToArray(),
                    out _,
                    out _)
                    && LicensedDungeonPlayResultRequest.TryParse(
                        body.Concat(new byte[] { 0, 0, 0, 0, 0 }).ToArray(),
                        out var capturedRequest,
                        out _)
                    && capturedRequest.Body.Length ==
                        LicensedDungeonPlayResultRequest.CapturedWireLength
                    && !LicensedDungeonPlayResultRequest.TryParse(
                        body.Concat(new byte[] { 0, 0 }).ToArray(),
                        out _,
                        out _),
                ref failures);
        }

        private static void VerifyRequestRewardRequest(ref int failures)
        {
            Check(
                "A21 licensed request-reward accepts the verified empty body",
                LicensedDungeonRequestRewardRequest.TryParse(
                    Array.Empty<byte>(),
                    out _,
                    out _),
                ref failures);
            Check(
                "licensed request-reward rejects non-empty bodies",
                !LicensedDungeonRequestRewardRequest.TryParse(
                    new byte[] { 1 },
                    out _,
                    out _),
                ref failures);
        }

        private static void VerifyRewardRuntime(ref int failures)
        {
            var ordinaryRun = new DungeonRun
            {
                DungeonId = 5008,
                MazeIndex = 0,
            };
            var ordinary = LicensedDungeonRewardService.Prepare(ordinaryRun);
            Check(
                "ordinary licensed clear freezes fixed and daily PVF rewards",
                ordinary != null
                    && !ordinary.GroupBossPresent
                    && ordinary.LicenseLevel == 3
                    && ordinary.DungeonClearReward?.ItemId == 10155143
                    && ordinary.DungeonClearReward.Count == 3
                    && ordinary.DailyClearReward?.ItemId == 10155137
                    && ordinary.DailyClearReward.Count == 2
                    && ordinary.GroupBossReward == null
                    && ordinary.Rewards.Any(reward =>
                        reward.ItemId == 10155143
                        && reward.StackCount == 3)
                    && ordinary.Rewards.Any(reward =>
                        reward.ItemId == 10155137
                        && reward.StackCount == 2),
                ref failures);

            var groupRun = new DungeonRun
            {
                DungeonId = 5008,
                MazeIndex = 1,
            };
            var group = LicensedDungeonRewardService.Prepare(groupRun);
            Check(
                "group licensed clear adds one weighted PVF group drop",
                group != null
                    && group.GroupBossPresent
                    && group.GroupBossReward?.ItemId >= 100000000
                    && group.GroupBossReward.Count == 1
                    && group.Rewards.Count == 3
                    && group.Rewards.Any(reward =>
                        reward.ItemId >= 100000000),
                ref failures);
        }

        private static void VerifyPeriodBoundaries(ref int failures)
        {
            var beforeDayReset = LicensedDungeonPeriod.FromUtc(
                new DateTime(2026, 8, 26, 21, 59, 59, DateTimeKind.Utc));
            var afterDayReset = LicensedDungeonPeriod.FromUtc(
                new DateTime(2026, 8, 26, 22, 0, 0, DateTimeKind.Utc));
            Check(
                "licensed day index rolls at Beijing 06:00",
                beforeDayReset.DayId == 20260826
                    && beforeDayReset.DayIndex == 3
                    && afterDayReset.DayId == 20260827
                    && afterDayReset.DayIndex == 4,
                ref failures);

            var beforeMonthReset = LicensedDungeonPeriod.FromUtc(
                new DateTime(2026, 8, 31, 21, 59, 59, DateTimeKind.Utc));
            var afterMonthReset = LicensedDungeonPeriod.FromUtc(
                new DateTime(2026, 8, 31, 22, 0, 0, DateTimeKind.Utc));
            Check(
                "licensed monthly state uses the same Beijing 06:00 boundary",
                beforeMonthReset.MonthId == 202608
                    && afterMonthReset.MonthId == 202609,
                ref failures);
        }

        private static void VerifyPersistentAdmission(
            string tempDbPath,
            ref int failures)
        {
            var database = new GameDatabase(
                tempDbPath,
                ServerPaths.SchemaFilePath);
            database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (1, 'license-selftest', '');
INSERT INTO characters (character_id, account_id, name, job)
VALUES (1001, 1, 'license-selftest', 0);";
                command.ExecuteNonQuery();
            });
            Check(
                "licensed dungeon state tables are current schema",
                database.Read(connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = @"
SELECT COUNT(*)
FROM sqlite_master
WHERE type='table'
  AND name IN (
      'character_license_dungeon_period_state',
      'character_license_dungeon_progress');";
                    return SqliteMigrations.ReadVersion(connection)
                        == SqliteMigrations.CurrentVersion
                        && Convert.ToInt32(command.ExecuteScalar()) == 2
                        && HasColumn(
                            connection,
                            "character_license_dungeon_progress",
                            "no_revive_clear_count");
                }),
                ref failures);

            var service = new LicensedDungeonService(database);
            var thursday = new DateTime(
                2026, 8, 27, 0, 0, 0, DateTimeKind.Utc);
            Check(
                "new character projects Thursday with two remaining entries",
                service.TryGetSelectionProjection(
                    1001,
                    thursday,
                    out var dayIndex,
                    out var remaining,
                    out _)
                    && dayIndex == 4
                    && remaining == 2,
                ref failures);

            Check(
                "new character cannot enter a locked three-star dungeon",
                !service.TryPrepareEntry(
                    1001,
                    5008,
                    thursday,
                    _ => 0,
                    out _,
                    out var lockedReason)
                    && lockedReason.Contains("not unlocked", StringComparison.Ordinal),
                ref failures);
            Check(
                "first one-star no-revive clear unlocks two stars",
                service.TryAdvanceLicenseOnClear(
                    1001,
                    5006,
                    reviveUsed: false,
                    out var advancedToTwo,
                    out var firstLicenseNoReviveCount,
                    out _)
                    && advancedToTwo
                    && firstLicenseNoReviveCount == 0
                    && service.TryGetLicenseProjection(
                        1001,
                        out var twoStarRecords,
                        out _)
                    && twoStarRecords.Any(record =>
                        record.DungeonId == 5007
                        && record.LicenseLevel == 2),
                ref failures);
            Check(
                "replaying a lower-tier clear does not regress two stars",
                service.TryAdvanceLicenseOnClear(
                    1001,
                    5006,
                    reviveUsed: false,
                    out var advancedAfterLowerTierReplay,
                    out var lowerTierReplayCount,
                    out _)
                    && !advancedAfterLowerTierReplay
                    && lowerTierReplayCount == 0
                    && service.TryGetLicenseProjection(
                        1001,
                        out var replayedTwoStarRecords,
                        out _)
                    && replayedTwoStarRecords.Any(record =>
                        record.DungeonId == 5007
                        && record.LicenseLevel == 2),
                ref failures);
            var restartedService = new LicensedDungeonService(database);
            Check(
                "recreated service keeps the two-star license in SQLite",
                restartedService.TryGetLicenseProjection(
                    1001,
                    out var persistedTwoStarRecords,
                    out _)
                    && persistedTwoStarRecords.Any(record =>
                        record.DungeonId == 5007
                        && record.LicenseLevel == 2),
                ref failures);
            Check(
                "first two-star clear without revive keeps two stars",
                service.TryAdvanceLicenseOnClear(
                    1001,
                    5007,
                    reviveUsed: false,
                    out var advancedAfterFirstTwoStar,
                    out var firstNoReviveCount,
                    out _)
                    && !advancedAfterFirstTwoStar
                    && firstNoReviveCount == 1
                    && service.TryGetLicenseProjection(
                        1001,
                        out var stillTwoStarRecords,
                        out _)
                    && stillTwoStarRecords.Any(record =>
                        record.DungeonId == 5007
                        && record.LicenseLevel == 2),
                ref failures);
            Check(
                "second two-star clear without revive keeps two stars",
                service.TryAdvanceLicenseOnClear(
                    1001,
                    5007,
                    reviveUsed: false,
                    out var advancedAfterSecondTwoStar,
                    out var secondNoReviveCount,
                    out _)
                    && !advancedAfterSecondTwoStar
                    && secondNoReviveCount == 2,
                ref failures);
            Check(
                "using a revive coin resets the two-star no-revive streak",
                service.TryAdvanceLicenseOnClear(
                    1001,
                    5007,
                    reviveUsed: true,
                    out var advancedAfterRevive,
                    out var resetNoReviveCount,
                    out _)
                    && !advancedAfterRevive
                    && resetNoReviveCount == 0,
                ref failures);
            service.TryAdvanceLicenseOnClear(
                1001,
                5007,
                reviveUsed: false,
                out var ignoredFirstAfterReset,
                out var thirdNoReviveCount,
                out _);
            service.TryAdvanceLicenseOnClear(
                1001,
                5007,
                reviveUsed: false,
                out var ignoredSecondAfterReset,
                out var fourthNoReviveCount,
                out _);
            Check(
                "third consecutive two-star clear without revive unlocks three stars",
                service.TryAdvanceLicenseOnClear(
                    1001,
                    5007,
                    reviveUsed: false,
                    out var advancedToThree,
                    out var completedNoReviveCount,
                    out _)
                    && !ignoredFirstAfterReset
                    && !ignoredSecondAfterReset
                    && thirdNoReviveCount == 1
                    && fourthNoReviveCount == 2
                    && advancedToThree
                    && completedNoReviveCount == 0
                    && service.TryGetLicenseProjection(
                        1001,
                        out var threeStarRecords,
                        out _)
                    && threeStarRecords.Any(record =>
                        record.DungeonId == 5008
                        && record.LicenseLevel == 3),
                ref failures);
            var threeStarNoReviveCounts = new List<int>();
            var threeStarAdvanced = false;
            var threeStarCompletedCount = -1;
            for (var clear = 0; clear < 7; clear++)
            {
                threeStarAdvanced = service.TryAdvanceLicenseOnClear(
                    1001,
                    5008,
                    reviveUsed: false,
                    out var advanced,
                    out var count,
                    out _)
                    && advanced;
                threeStarNoReviveCounts.Add(count);
                if (clear == 6)
                    threeStarCompletedCount = count;
            }
            Check(
                "seventh consecutive three-star no-revive clear unlocks four stars",
                threeStarAdvanced
                    && threeStarNoReviveCounts.Count == 7
                    && threeStarNoReviveCounts.Take(6).All(
                        count => count == 1 || count == 2 || count == 3
                            || count == 4 || count == 5 || count == 6)
                    && threeStarCompletedCount == 0
                    && service.TryGetLicenseProjection(
                        1001,
                        out var fourStarRecords,
                        out _)
                    && fourStarRecords.Any(record =>
                        record.DungeonId == 5009
                        && record.LicenseLevel == 4),
                ref failures);
            Check(
                "first guaranteed test roll selects a native group maze",
                service.TryPrepareEntry(
                    1001,
                    5008,
                    thursday,
                    _ => 0,
                    out var rollbackPlan,
                    out _)
                    && rollbackPlan.GroupBossPresent
                    && rollbackPlan.MazeIndex == 1
                    && rollbackPlan.Definition.Difficulty == 2
                    && rollbackPlan.GroupAppearRate == 10,
                ref failures);
            Check(
                "entry commit reserves daily/monthly/group counters",
                service.TryCommitEntry(
                    rollbackPlan,
                    out var rollbackCommitted,
                    out _)
                    && rollbackCommitted.DailyEntryCount == 1
                    && rollbackCommitted.MonthlyEntryCount == 1
                    && rollbackCommitted.MonthlyGroupAppearCount == 1,
                ref failures);
            Check(
                "failed inventory commit can roll back the exact entry reservation",
                service.TryRollbackEntry(rollbackPlan, out _)
                    && service.TryGetSelectionProjection(
                        1001,
                        thursday,
                        out _,
                        out var afterRollbackRemaining,
                        out _)
                    && afterRollbackRemaining == 2,
                ref failures);

            service.TryPrepareEntry(
                1001,
                5008,
                thursday,
                _ => 0,
                out var firstPlan,
                out _);
            service.TryCommitEntry(firstPlan, out var firstCommitted, out _);
            service.TryPrepareEntry(
                1001,
                5008,
                thursday,
                _ => 0,
                out var secondPlan,
                out _);
            service.TryCommitEntry(secondPlan, out var secondCommitted, out _);
            Check(
                "two daily entries can consume the monthly group cap",
                firstCommitted.DailyEntryCount == 1
                    && secondCommitted.DailyEntryCount == 2
                    && secondCommitted.MonthlyGroupAppearCount == 2,
                ref failures);
            Check(
                "third entry on the same game day remains available for testing",
                service.TryPrepareEntry(
                    1001,
                    5008,
                    thursday,
                    _ => 0,
                    out var thirdPlan,
                    out _)
                    && service.TryCommitEntry(
                        thirdPlan,
                        out var thirdCommitted,
                        out _)
                    && thirdCommitted.DailyEntryCount == 3
                    && service.TryGetSelectionProjection(
                        1001,
                        thursday,
                        out _,
                        out var testRemaining,
                        out _)
                    && testRemaining == LicensedDungeonCatalog.DailyEnterCount,
                ref failures);

            var monday = new DateTime(
                2026, 8, 31, 0, 0, 0, DateTimeKind.Utc);
            Check(
                "daily reset preserves the same-month group cap and selects ordinary maze",
                service.TryPrepareEntry(
                    1001,
                    5008,
                    monday,
                    _ => 0,
                    out var cappedPlan,
                    out _)
                    && !cappedPlan.GroupBossPresent
                    && cappedPlan.MazeIndex == 0
                    && cappedPlan.ExpectedStatus.DailyEntryCount == 0
                    && cappedPlan.ExpectedStatus.MonthlyGroupAppearCount == 2,
                ref failures);
            service.TryCommitEntry(cappedPlan, out _, out _);

            var friday = new DateTime(
                2026, 8, 28, 0, 0, 0, DateTimeKind.Utc);
            Check(
                "closed-day admission rejects a dungeon hidden by the day index",
                !service.TryPrepareEntry(
                    1001,
                    5008,
                    friday,
                    _ => 0,
                    out _,
                    out var closedReason)
                    && closedReason.Contains("closed", StringComparison.Ordinal),
                ref failures);

            var nextMonthThursday = new DateTime(
                2026, 9, 3, 0, 0, 0, DateTimeKind.Utc);
            Check(
                "new month resets cumulative entries and allows the group roll again",
                service.TryPrepareEntry(
                    1001,
                    5008,
                    nextMonthThursday,
                    _ => 0,
                    out var nextMonthPlan,
                    out _)
                    && nextMonthPlan.GroupBossPresent
                    && nextMonthPlan.ExpectedStatus.MonthlyEntryCount == 0
                    && nextMonthPlan.ExpectedStatus.MonthlyGroupAppearCount == 0,
                ref failures);

            VerifyPersistentRewardIdempotency(database, ref failures);
        }

        private static void VerifyPersistentRewardIdempotency(
            GameDatabase database,
            ref int failures)
        {
            var sessionId = Guid.NewGuid();
            InventoryLease lease = null;
            try
            {
                using (var connection = database.OpenConnection())
                {
                    var inventory = InventoryService.LoadFromDb(
                        connection,
                        1001,
                        1,
                        database);
                    lease = InventoryContext.Register(
                        sessionId,
                        1001,
                        inventory);
                }

                var persistentEffects =
                    new DungeonPersistentEffectApplicationService(
                        database.ConnectionString,
                        database: database);
                var effectId = new DungeonEffectId(
                    Guid.NewGuid(),
                    DungeonPersistentEffectKinds.LicensedDungeonRewardCommit,
                    DungeonEffectScope.Player,
                    1001);
                var rewards = new[]
                {
                    new LicensedDungeonRewardEffectItem
                    {
                        ItemId = 10155143,
                        StackCount = 1,
                    },
                };
                var first = persistentEffects.TryApplyLicensedDungeonReward(
                    effectId,
                    lease,
                    sessionId,
                    5008,
                    3,
                    groupBossPresent: false,
                    rewards,
                    out var firstResult,
                    out _);
                var second = persistentEffects.TryApplyLicensedDungeonReward(
                    effectId,
                    lease,
                    sessionId,
                    5008,
                    3,
                    groupBossPresent: false,
                    rewards,
                    out var secondResult,
                    out _);
                int itemCount;
                lock (lease.SyncRoot)
                    itemCount = lease.Inventory.CountMainItem(10155143);

                Check(
                    "licensed reward outbox is idempotent for repeated 0x032D",
                    first
                        && second
                        && firstResult?.Changes.Count > 0
                        && secondResult?.Changes.Count > 0
                        && itemCount == 1,
                    ref failures);

                var equipmentEffectId = new DungeonEffectId(
                    Guid.NewGuid(),
                    DungeonPersistentEffectKinds.LicensedDungeonRewardCommit,
                    DungeonEffectScope.Player,
                    1001);
                var equipmentRewards = new[]
                {
                    new LicensedDungeonRewardEffectItem
                    {
                        ItemId = 100320752,
                        StackCount = 1,
                    },
                };
                var equipmentFirst =
                    persistentEffects.TryApplyLicensedDungeonReward(
                        equipmentEffectId,
                        lease,
                        sessionId,
                        5008,
                        3,
                        groupBossPresent: true,
                        equipmentRewards,
                        out var equipmentFirstResult,
                        out _);
                var equipmentSecond =
                    persistentEffects.TryApplyLicensedDungeonReward(
                        equipmentEffectId,
                        lease,
                        sessionId,
                        5008,
                        3,
                        groupBossPresent: true,
                        equipmentRewards,
                        out var equipmentSecondResult,
                        out _);
                Check(
                    "licensed equipment reward remains in main inventory across outbox replay",
                    equipmentFirst
                        && equipmentSecond
                        && equipmentFirstResult?.Changes.Any(change =>
                            change.ListType == InventoryListType.Main) == true
                        && equipmentSecondResult?.Changes.Any(change =>
                            change.ListType == InventoryListType.Main) == true,
                    ref failures);
            }
            finally
            {
                InventoryContext.Unregister(sessionId, 1001);
            }
        }

        private static void VerifyMigration(
            string databasePath,
            ref int failures)
        {
            var database = new GameDatabase(
                databasePath,
                ServerPaths.SchemaFilePath);
            database.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
DROP TABLE character_license_dungeon_period_state;
DROP TABLE character_license_dungeon_progress;
UPDATE schema_metadata
SET schema_version = 11,
    updated_at = CURRENT_TIMESTAMP
WHERE singleton_id = 1;
PRAGMA user_version = 11;";
                command.ExecuteNonQuery();
            });

            using (var connection = database.OpenConnection())
                SqliteMigrations.Apply(connection);
            Check(
                "schema v11 upgrades to current schema with licensed state tables",
                database.Read(connection =>
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = @"
SELECT COUNT(*)
FROM sqlite_master
WHERE type='table'
  AND name IN (
      'character_license_dungeon_period_state',
      'character_license_dungeon_progress');";
                    return SqliteMigrations.ReadVersion(connection)
                        == SqliteMigrations.CurrentVersion
                        && Convert.ToInt32(command.ExecuteScalar()) == 2
                        && HasColumn(
                            connection,
                            "character_license_dungeon_progress",
                            "no_revive_clear_count");
                }),
                ref failures);
        }

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            Console.WriteLine($"{(condition ? "PASS" : "FAIL")}: {name}");
            if (!condition)
                failures++;
        }

        private static bool HasColumn(
            SqliteConnection connection,
            string tableName,
            string columnName)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({tableName});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(
                        reader.GetString(1),
                        columnName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static void DeleteIfExists(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
