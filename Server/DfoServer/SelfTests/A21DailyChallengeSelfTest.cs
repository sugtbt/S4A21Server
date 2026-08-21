using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.CharacterData;
using DfoServer.Game.DailyReset;
using DfoServer.Game.Inventory;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Sqlite;
using Microsoft.Data.Sqlite;
using PvfLib;

namespace DfoServer.SelfTests
{
    public static class A21DailyChallengeSelfTest
    {
        private const int AccountId = 986026;
        private const int CharacterId = 986126;

        public static int Run()
        {
            Console.WriteLine("=== A21_DAILY_CHALLENGE selftest ===");
            var failures = 0;
            var tempDirectory = Path.Combine(
                Path.GetTempPath(),
                $"dfo_a21_daily_challenge_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDirectory);
            var databasePath = Path.Combine(tempDirectory, "challenge.db");
            var sessionId = Guid.NewGuid();

            try
            {
                var database = new GameDatabase(databasePath, ServerPaths.SchemaFilePath);
                Seed(database.ConnectionString);
                VerifyV5ToV6Migration(database, ref failures);

                Check(
                    "A21 daily challenge opcodes come from PacketTypesA21",
                    (ushort)NotiPacketTypeA21.DAILY_CHALLENGE == 0x0286
                    && (ushort)NotiPacketTypeA21.DAILY_CHALLENGE_CLEAR_DUNGEON == 0x0287
                    && (ushort)CmdPacketTypeA21.DAILY_CHALLENGE_REWARD == 0x02BC,
                    ref failures);
                var challengeTableText = PvfArchiveAccessor.ReadText(
                    "etc/dailychallengetable.etc");
                var challengeTable = new ScriptParser().Parse(challengeTableText);
                Check(
                    "A21 PVF defines five ordinary groups and no A12 special challenge",
                    challengeTable.GetChildren("group").Count == 5
                    && challengeTable.GetChild("special challenge") == null,
                    ref failures);
                var clearEventId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
                var clearCompletionToken = DailyChallengeClearDungeonBodyBuilder
                    .ResolveCompletionToken(clearEventId);
                var clearDungeonBody = DailyChallengeClearDungeonBodyBuilder
                    .Build(clearCompletionToken);
                Check(
                    "A21 0x0287 carries exactly one stable UInt32 completion token",
                    clearCompletionToken != 0
                    && clearDungeonBody.Length == sizeof(uint)
                    && BitConverter.ToUInt32(clearDungeonBody, 0) == clearCompletionToken
                    && DailyChallengeClearDungeonBodyBuilder
                        .ResolveCompletionToken(clearEventId) == clearCompletionToken,
                    ref failures);
                Check(
                    "challenge target extraction uses compact A21 QST fields",
                    QuestData.GetInitTrigger(14653) == 3
                    && QuestData.GetInitTrigger(14710) == 5,
                    ref failures);
                var bossRuleResolved = QuestData
                    .TryGetSuitableDungeonBossKillChallengeRule(
                        14522,
                        out var bossMinimumDifficulty,
                        out var bossTargetCount);
                Check(
                    "A21 boss challenge is resource-backed recommended-dungeon any-boss x5",
                    bossRuleResolved
                    && bossMinimumDifficulty == -1
                    && bossTargetCount == 5
                    && QuestData.GetQuestFile(14522)?.Name?.Contains("击杀领主") == true,
                    ref failures);
                var levelSixteenReward = QuestData.ResolveReward(
                    14561,
                    playerLevel: 16,
                    playerJob: 5,
                    playerGrowType: 0);
                Check(
                    "A21 challenge 14561 resource reward includes item 10099414 and formula EXP 31",
                    levelSixteenReward.IsValid
                    && levelSixteenReward.Reward.Exp == 31
                    && levelSixteenReward.Reward.Items.Count == 1
                    && levelSixteenReward.Reward.Items[0].ItemId == 10099414
                    && levelSixteenReward.Reward.Items[0].Count == 1,
                    ref failures);

                var configured = DailyChallengeData.GetConfiguredQuestIds()
                    .OrderBy(id => id)
                    .ToList();
                var suitableQuestId = configured.FirstOrDefault(id =>
                    QuestData.TryGetSuitableDungeonClearChallengeRule(id, out _));
                var questCompletionId = configured.FirstOrDefault(id =>
                    QuestData.TryGetQuestCompletionChallengeRule(
                        id,
                        out var selector,
                        out _)
                    && selector < 0);
                var questGradeSelectors = configured
                    .Select(id => QuestData.TryGetQuestCompletionChallengeRule(
                            id,
                            out var selector,
                            out _)
                        ? (int?)selector
                        : null)
                    .Where(selector => selector.HasValue)
                    .Select(selector => selector.Value)
                    .Distinct()
                    .OrderBy(selector => selector)
                    .ToArray();
                Console.WriteLine(
                    $"A21 challenge quest-grade selectors: "
                    + string.Join(",", questGradeSelectors));
                var mainlineChallengeIds = configured.Where(id =>
                    QuestData.TryGetQuestCompletionChallengeRule(
                        id,
                        out var selector,
                        out _)
                    && selector == 0).ToArray();
                Check(
                    "A21 PVF exposes server-owned suitable and quest-completion rules",
                    configured.Count > 200
                    && suitableQuestId > 0
                    && questCompletionId > 0
                    && questGradeSelectors.SequenceEqual(new[] { -1, 0 }),
                    ref failures);
                Check(
                    "A21 selector 0 is resource-backed mainline/epic and unknown selectors fail closed",
                    mainlineChallengeIds.Length > 0
                    && mainlineChallengeIds.All(id =>
                        QuestData.GetQuestFile(id)?.Name?.Contains("主线") == true)
                    && QuestData.MatchesQuestGradeSelector(0, "epic")
                    && !QuestData.MatchesQuestGradeSelector(0, "normal")
                    && !QuestData.MatchesQuestGradeSelector(1, "epic"),
                    ref failures);

                var service = new DailyChallengeService(
                    database.ConnectionString,
                    new DailyResetService(database));
                var generated = service.EnsureInitialized(CharacterId);
                Check(
                    "level 61 generates only unlocked PVF entries in groups 0/4",
                    generated.Refreshed
                    && generated.Snapshot.RacingDungeonGroups.Count == 2
                    && generated.Snapshot.RacingDungeonGroups[0].GroupId == 0
                    && generated.Snapshot.RacingDungeonGroups[0].Entries.Count == 5
                    && generated.Snapshot.RacingDungeonGroups[1].GroupId == 4
                    && generated.Snapshot.RacingDungeonGroups[1].Entries.Count == 3,
                    ref failures);
                Check(
                    "generated entries are configured challenges with remaining=target",
                    generated.Snapshot.RacingDungeonGroups
                        .SelectMany(group => group.Entries)
                        .All(entry => QuestData.IsDailyChallengeQuest((int)entry.TrackLikeId)
                            && DailyChallengeData.IsQuestEligibleAtLevel(
                                (int)entry.TrackLikeId,
                                61)
                            && entry.TargetValue > 0
                            && entry.RemainingValue == entry.TargetValue)
                    && generated.Snapshot.RacingDungeonGroups
                        .SelectMany(group => group.Entries)
                        .Select(entry => entry.TrackLikeId)
                        .Distinct()
                        .Count() == generated.Snapshot.RacingDungeonGroups
                            .Sum(group => group.Entries.Count)
                    && GenerationPlansKeepUniqueEligibleEntries(),
                    ref failures);

                var levelFourPlan = DailyChallengeData.BuildGenerationPlan(
                    CharacterId,
                    characterLevel: 4,
                    dayId: 20260821);
                Check(
                    "low-level generation excludes locked high-level challenge QSTs",
                    levelFourPlan.Groups.Count == 1
                    && levelFourPlan.Groups[0].Entries.Count == 3
                    && levelFourPlan.Groups[0].Entries.All(entry =>
                        DailyChallengeData.IsQuestEligibleAtLevel(
                            entry.QuestId,
                            4))
                    && levelFourPlan.Groups[0].Entries.All(entry =>
                        entry.QuestId != 14650),
                    ref failures);
                Check(
                    "A21 omitted group reward threshold resolves to the client default 2",
                    DailyChallengeData.TryResolveReward(
                        groupIndex: 0,
                        characterLevel: 16,
                        activeEntryCount: 3,
                        out var levelSixteenGroupReward)
                    && levelSixteenGroupReward.RequiredCompletionCount == 2
                    && levelSixteenGroupReward.ItemId == 10099407
                    && levelSixteenGroupReward.ItemCount == 2,
                    ref failures);

                var wire = DailyChallengeBodyBuilder.Build(generated.Snapshot);
                Check(
                    "0x0286 serializes level and remaining,target order",
                    BitConverter.ToUInt32(wire, 0) == 61
                    && BitConverter.ToUInt32(wire, 4) == 2
                    && FirstEntryMatchesSnapshot(wire, generated.Snapshot),
                    ref failures);
                var claimFlagCountOffset = 4 + 4
                    + generated.Snapshot.RacingDungeonGroups.Sum(
                        group => 8 + group.Entries.Count * 12);
                var expectedA21WireLength = claimFlagCountOffset + 4 + 5;
                generated.Snapshot.DailyChallengeRewardClaimFlags =
                    new byte[] { 0, 0, 0, 0, 0, 1 };
                generated.Snapshot.RacingDungeonTailIds.Add(0xA2100286u);
                var wireWithLegacyTailId = DailyChallengeBodyBuilder.Build(generated.Snapshot);
                Check(
                    "A21 0x0286 has exactly five flags and drops A12 special state",
                    wire.Length == expectedA21WireLength
                    && BitConverter.ToUInt32(wire, claimFlagCountOffset) == 5
                    && wireWithLegacyTailId.AsSpan().SequenceEqual(wire),
                    ref failures);

                SetFirstEntryRemaining(database.ConnectionString, 1);
                var repeated = service.EnsureInitialized(CharacterId);
                Check(
                    "same-day initialization preserves the existing ledger",
                    !repeated.Refreshed
                    && repeated.Snapshot.RacingDungeonGroups[0].Entries[0].RemainingValue == 1,
                    ref failures);

                ReplaceFirstEntryQuest(
                    database.ConnectionString,
                    questId: 14650,
                    target: 3,
                    remaining: 1);
                var repairedLockedLedger = service.EnsureInitialized(CharacterId);
                Check(
                    "same-day initialization repairs ledgers generated with a locked future-level QST",
                    repairedLockedLedger.Refreshed
                    && repairedLockedLedger.Snapshot.RacingDungeonGroups
                        .SelectMany(group => group.Entries)
                        .All(entry => DailyChallengeData.IsQuestEligibleAtLevel(
                            (int)entry.TrackLikeId,
                            61)),
                    ref failures);

                MarkForRollover(database.ConnectionString);
                var rolled = service.EnsureInitialized(CharacterId);
                Check(
                    "daily rollover regenerates entries and clears transient claims/flags",
                    rolled.Refreshed
                    && rolled.Snapshot.RacingDungeonGroups
                        .SelectMany(group => group.Entries)
                        .All(entry => entry.RemainingValue == entry.TargetValue)
                    && Scalar(database.ConnectionString,
                        "SELECT COUNT(*) FROM character_daily_challenge_claims WHERE character_id=@cid;") == 0
                    && Scalar(database.ConnectionString,
                        "SELECT COUNT(*) FROM character_daily_challenge_entry_claims WHERE character_id=@cid;") == 0
                    && Scalar(database.ConnectionString,
                        "SELECT COUNT(*) FROM character_quest_completions WHERE character_id=@cid;") == 0,
                    ref failures);

                SeedSingleEntry(database.ConnectionString, suitableQuestId, target: 3, remaining: 3);
                var setTriggerBody = new byte[4];
                Buffer.BlockCopy(BitConverter.GetBytes((ushort)suitableQuestId), 0, setTriggerBody, 0, 2);
                setTriggerBody[2] = 0;
                service.TryHandleSetTrigger(CharacterId, setTriggerBody, out var echo);
                Check(
                    "client SET_TRIGGER cannot advance server-owned suitable clear",
                    echo.Found && !echo.Changed
                    && ReadRemaining(database.ConnectionString, suitableQuestId) == 3,
                    ref failures);

                var sourceEventId = Guid.NewGuid();
                var suitableClear = service.ApplySuitableDungeonClear(
                    CharacterId,
                    dungeonId: 84,
                    difficulty: 3,
                    characterLevel: 62,
                    sourceEventId);
                var suitableReplay = service.ApplySuitableDungeonClear(
                    CharacterId,
                    dungeonId: 84,
                    difficulty: 3,
                    characterLevel: 62,
                    sourceEventId);
                Check(
                    "recommended dungeon progress is authoritative and source-event idempotent",
                    suitableClear.ChangedEntries == 1
                    && suitableReplay.ChangedEntries == 0
                    && suitableReplay.HasRelevantProgress
                    && ReadRemaining(database.ConnectionString, suitableQuestId) == 2,
                    ref failures);

                SeedSingleEntry(
                    database.ConnectionString,
                    suitableQuestId,
                    target: 1,
                    remaining: 1);
                var completingClearEventId = Guid.NewGuid();
                var completingClear = service.ApplySuitableDungeonClear(
                    CharacterId,
                    dungeonId: 84,
                    difficulty: 3,
                    characterLevel: 62,
                    completingClearEventId);
                var completingClearReplay = service.ApplySuitableDungeonClear(
                    CharacterId,
                    dungeonId: 84,
                    difficulty: 3,
                    characterLevel: 62,
                    completingClearEventId);
                var laterClear = service.ApplySuitableDungeonClear(
                    CharacterId,
                    dungeonId: 84,
                    difficulty: 3,
                    characterLevel: 62,
                    sourceEventId: Guid.NewGuid());
                Check(
                    "completed suitable challenge projects only its committed event or replay",
                    completingClear.ChangedEntries == 1
                    && completingClearReplay.ChangedEntries == 0
                    && completingClearReplay.HasRelevantProgress
                    && laterClear.ChangedEntries == 0
                    && !laterClear.HasRelevantProgress,
                    ref failures);

                const int bossChallengeQuestId = 14522;
                SeedSingleEntry(
                    database.ConnectionString,
                    bossChallengeQuestId,
                    target: 5,
                    remaining: 5);
                var bossSetTriggerBody = new byte[4];
                Buffer.BlockCopy(
                    BitConverter.GetBytes((ushort)bossChallengeQuestId),
                    0,
                    bossSetTriggerBody,
                    0,
                    2);
                bossSetTriggerBody[2] = 0;
                bossSetTriggerBody[3] = 1;
                service.TryHandleSetTrigger(
                    CharacterId,
                    bossSetTriggerBody,
                    out var bossEcho);
                var nonBossKill = service.ApplySuitableDungeonBossKill(
                    CharacterId,
                    dungeonId: 84,
                    difficulty: 3,
                    characterLevel: 62,
                    monsterCode: 12345,
                    monsterType: 2,
                    sourceEventId: Guid.NewGuid());
                var bossEventId = Guid.NewGuid();
                var bossKill = service.ApplySuitableDungeonBossKill(
                    CharacterId,
                    dungeonId: 84,
                    difficulty: 3,
                    characterLevel: 62,
                    monsterCode: 12345,
                    monsterType: 3,
                    sourceEventId: bossEventId);
                var bossReplay = service.ApplySuitableDungeonBossKill(
                    CharacterId,
                    dungeonId: 84,
                    difficulty: 3,
                    characterLevel: 62,
                    monsterCode: 12345,
                    monsterType: 3,
                    sourceEventId: bossEventId);
                Check(
                    "recommended boss challenge is server-owned, boss-only and source-event idempotent",
                    bossEcho.Found
                    && !bossEcho.Changed
                    && nonBossKill.ChangedEntries == 0
                    && bossKill.ChangedEntries == 1
                    && bossReplay.ChangedEntries == 0
                    && ReadRemaining(
                        database.ConnectionString,
                        bossChallengeQuestId) == 4,
                    ref failures);

                var mainlineChallengeId = mainlineChallengeIds[0];
                SeedSingleEntry(
                    database.ConnectionString,
                    mainlineChallengeId,
                    target: 3,
                    remaining: 3);
                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    var sideQuestProgress = DailyChallengeRepository.ApplyQuestCompletion(
                        connection,
                        transaction,
                        CharacterId,
                        "normal",
                        completionCount: 1);
                    var mainlineProgress = DailyChallengeRepository.ApplyQuestCompletion(
                        connection,
                        transaction,
                        CharacterId,
                        "epic",
                        completionCount: 1);
                    Check(
                        "selector 0 advances only for an epic mainline completion",
                        sideQuestProgress.ChangedEntries == 0
                        && mainlineProgress.ChangedEntries == 1,
                        ref failures);
                    transaction.Commit();
                }

                SeedSingleEntry(database.ConnectionString, questCompletionId, target: 3, remaining: 3);
                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    var progress = DailyChallengeRepository.ApplyQuestCompletion(
                        connection,
                        transaction,
                        CharacterId,
                        "epic",
                        completionCount: 1);
                    Check(
                        "ordinary quest completion advances matching daily challenge in transaction",
                        progress.ChangedEntries == 1
                        && progress.Snapshot != null,
                        ref failures);
                    transaction.Commit();
                }
                Check(
                    "ordinary completion decrements remaining before any client completion-count echo",
                    ReadRemaining(database.ConnectionString, questCompletionId) == 2,
                    ref failures);

                var inventory = new InventoryService(CharacterId, AccountId);
                if (!InventoryRewardGrantService.TryGrant(
                        inventory,
                        InventoryRewardGrantRequest.Create(
                            3309,
                            18,
                            ItemCreateReason.AdminGrant),
                        out var requirementGrant)
                    || !requirementGrant.Success)
                {
                    throw new InvalidOperationException(
                        "daily challenge entry requirement fixture failed");
                }
                inventory.ClearDirtyState();
                var lease = InventoryContext.Register(sessionId, inventory);
                var owner = new QuestCommandOwnerContext(
                    CharacterId,
                    AccountId,
                    sessionId,
                    lease);
                const ushort rewardChallengeQuestId = 14732;
                SeedSingleEntry(
                    database.ConnectionString,
                    rewardChallengeQuestId,
                    target: 1,
                    remaining: 0);
                var questService = new QuestService(database.ConnectionString);
                var forgedBatchFinish = questService.HandleFinishQuest(
                    owner,
                    new QuestFinishCommand(
                        rewardChallengeQuestId,
                        hasRewardSelection: false,
                        rewardSelectionIndex: 0,
                        completionCount: 2));
                var recordingSender = new RecordingSender();
                new QuestManager(recordingSender, database.ConnectionString)
                    .HandleFinishQuestAsync(
                        (ushort)CmdPacketTypeA21.FINISH_QUEST,
                        BuildWireFinishBody(rewardChallengeQuestId),
                        sessionId)
                    .GetAwaiter()
                    .GetResult();
                var finishReplay = new QuestService(database.ConnectionString)
                    .HandleFinishQuest(
                        owner,
                        new QuestFinishCommand(
                            rewardChallengeQuestId,
                            hasRewardSelection: false,
                            rewardSelectionIndex: 0,
                            completionCount: 1));
                var claimedClearFlags = new QuestRepository(
                        database.ConnectionString)
                    .LoadClearedFlags(CharacterId);
                var claimedClearList = ClearQuestListBodyBuilder.BuildBody(
                    claimedClearFlags);
                var ackIndex = recordingSender.Calls.IndexOf("ACK:0022");
                var clearIndex = recordingSender.Calls.IndexOf("NOTI:0164");
                var snapshotIndex = recordingSender.Calls.IndexOf("NOTI:0286");
                Check(
                    "individual challenge FINISH projects ACK, online clear marker and daily snapshot in order",
                    QuestData.IsDailyChallengeQuest(rewardChallengeQuestId)
                    && !forgedBatchFinish.Success
                    && recordingSender.LastAckBody?.Length > 12
                    && recordingSender.LastAckBody[0] == 1
                    && BitConverter.ToUInt16(recordingSender.LastAckBody, 1)
                        == rewardChallengeQuestId
                    && BitConverter.ToUInt32(recordingSender.LastAckBody, 8) == 1
                    && ackIndex >= 0
                    && clearIndex > ackIndex
                    && snapshotIndex > clearIndex
                    && recordingSender.NotiBodies.TryGetValue(
                        (ushort)NotiPacketTypeA21.CLEAR_QUEST_LIST,
                        out var onlineClearList)
                    && onlineClearList[4 + rewardChallengeQuestId] == 1
                    && !finishReplay.Success
                    && inventory.CountMainItem(3309) == 9
                    && inventory.CountMainItem(3300) == 4
                    && new QuestRepository(database.ConnectionString)
                        .ReadClearedFlagValue(CharacterId, rewardChallengeQuestId) == 1
                    && claimedClearList.Length == 4 + ClearQuestListBodyBuilder.PayloadLength
                    && claimedClearList[4 + rewardChallengeQuestId] == 1,
                    ref failures);

                SeedSingleEntry(database.ConnectionString, questCompletionId, target: 1, remaining: 0);
                using (var connection = database.OpenConnection())
                using (var transaction = connection.BeginTransaction(deferred: false))
                {
                    var state = DailyChallengeRepository.LoadEntryRewardState(
                        connection,
                        transaction,
                        CharacterId,
                        (ushort)questCompletionId);
                    var firstClaim = DailyChallengeRepository.TryMarkEntryRewardClaimed(
                        connection,
                        transaction,
                        CharacterId,
                        state);
                    var replayClaim = DailyChallengeRepository.TryMarkEntryRewardClaimed(
                        connection,
                        transaction,
                        CharacterId,
                        state);
                    Check(
                        "individual entry reward claim is completed-only and idempotent",
                        state.CanClaim && firstClaim && !replayClaim,
                        ref failures);
                    transaction.Commit();
                }

                InsertProgressEvent(
                    database.ConnectionString,
                    questCompletionId,
                    "tutorial-flag-save-preservation");
                var stateRepository = new SqliteCharacterStateRepository(database);
                var genericFlagsSnapshot = new Game.SelectCharacter
                    .SelectCharacterInitializationSnapshot();
                stateRepository.LoadFlags(CharacterId, genericFlagsSnapshot);
                stateRepository.SaveFlags(CharacterId, genericFlagsSnapshot);
                Check(
                    "generic init-flag save preserves the daily challenge owner ledger",
                    Scalar(
                        database.ConnectionString,
                        "SELECT COUNT(*) FROM character_daily_challenge_entries WHERE character_id=@cid;") == 1
                    && Scalar(
                        database.ConnectionString,
                        "SELECT COUNT(*) FROM character_daily_challenge_entry_claims WHERE character_id=@cid;") == 1
                    && Scalar(
                        database.ConnectionString,
                        "SELECT COUNT(*) FROM character_daily_challenge_progress_events WHERE character_id=@cid;") == 1,
                    ref failures);

                MarkForRollover(database.ConnectionString);
                service.EnsureInitialized(CharacterId);
                CompleteFirstEntries(
                    database.ConnectionString,
                    groupIndex: 0,
                    count: 2);
                var firstReward = service.ClaimReward(owner, 0);
                var replayReward = service.ClaimReward(owner, 0);
                Check(
                    "group reward accepts the A21 2/2 threshold and is replay-idempotent",
                    firstReward.Status == DailyChallengeRewardClaimStatus.Success
                    && replayReward.Status == DailyChallengeRewardClaimStatus.AlreadyClaimed
                    && BitConverter.ToString(
                        DailyChallengeRewardAckBuilder.Build(firstReward))
                        == "01-00-00-00-00-00-00-00-00",
                    ref failures);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[FAIL] unhandled: " + ex);
                failures++;
            }
            finally
            {
                InventoryContext.Unregister(sessionId, CharacterId);
                SqliteConnection.ClearAllPools();
                try
                {
                    if (Directory.Exists(tempDirectory))
                        Directory.Delete(tempDirectory, recursive: true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[WARN] temp cleanup failed: " + ex.Message);
                }
            }

            Console.WriteLine(failures == 0
                ? "=== A21_DAILY_CHALLENGE PASS ==="
                : $"=== A21_DAILY_CHALLENGE FAIL ({failures}) ===");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyV5ToV6Migration(
            GameDatabase database,
            ref int failures)
        {
            using (var connection = database.OpenConnection())
            {
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = @"
DROP TABLE character_daily_challenge_progress_events;
DROP TABLE character_daily_challenge_entry_claims;
UPDATE schema_metadata SET schema_version=5 WHERE singleton_id=1;
PRAGMA user_version=5;";
                    command.ExecuteNonQuery();
                }
                SqliteMigrations.Apply(connection);
                Check(
                    "schema v5 migrates continuously to v6 daily challenge ledgers",
                    SqliteMigrations.ReadVersion(connection) == 6
                    && TableExists(connection, "character_daily_challenge_entry_claims")
                    && TableExists(connection, "character_daily_challenge_progress_events"),
                    ref failures);
            }
        }

        private static bool FirstEntryMatchesSnapshot(
            byte[] body,
            Game.SelectCharacter.SelectCharacterInitializationSnapshot snapshot)
        {
            var entry = snapshot.RacingDungeonGroups[0].Entries[0];
            return BitConverter.ToUInt32(body, 16) == entry.TrackLikeId
                && BitConverter.ToUInt32(body, 20) == entry.RemainingValue
                && BitConverter.ToUInt32(body, 24) == entry.TargetValue;
        }

        private static bool GenerationPlansKeepUniqueEligibleEntries()
        {
            foreach (var characterSeed in Enumerable.Range(1, 16))
            foreach (var dayOffset in Enumerable.Range(0, 8))
            foreach (var level in Enumerable.Range(1, 100))
            {
                var levelPlan = DailyChallengeData.BuildGenerationPlan(
                    CharacterId + characterSeed,
                    level,
                    dayId: 20260821 + dayOffset);
                var ids = levelPlan.Groups
                    .SelectMany(group => group.Entries)
                    .Select(entry => entry.QuestId)
                    .ToArray();
                if (ids.Distinct().Count() != ids.Length
                    || ids.Any(id => !DailyChallengeData
                        .IsQuestEligibleAtLevel(id, level)))
                {
                    return false;
                }
            }

            return true;
        }

        private static void Seed(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            using (var command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = @"
INSERT INTO accounts (account_id, m_id, password_hash)
VALUES (@aid, 'a21-daily-challenge-selftest', '');
INSERT INTO characters (character_id, account_id, name, level)
VALUES (@cid, @aid, 'a21-daily-challenge', 61);";
                command.Parameters.AddWithValue("@aid", AccountId);
                command.Parameters.AddWithValue("@cid", CharacterId);
                command.ExecuteNonQuery();
            }
        }

        private static void MarkForRollover(string connectionString)
        {
            using (var connection = new SqliteConnection(connectionString))
            using (var command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = @"
UPDATE character_daily_challenge_entries SET value_b=0 WHERE character_id=@cid;
INSERT OR IGNORE INTO character_daily_challenge_claims(character_id,group_index) VALUES(@cid,0);
INSERT OR IGNORE INTO character_daily_challenge_entry_claims(character_id,group_index,entry_index,quest_id)
SELECT character_id,group_index,entry_index,track_like_id
FROM character_daily_challenge_entries WHERE character_id=@cid LIMIT 1;
INSERT INTO character_quest_completions(character_id,quest_id,completion_value)
SELECT character_id,track_like_id,1
FROM character_daily_challenge_entries WHERE character_id=@cid LIMIT 1
ON CONFLICT(character_id,quest_id) DO UPDATE SET completion_value=1;
UPDATE character_daily_reset SET day_id=0 WHERE character_id=@cid;";
                command.Parameters.AddWithValue("@cid", CharacterId);
                command.ExecuteNonQuery();
            }
        }

        private static void SeedSingleEntry(
            string connectionString,
            int questId,
            uint target,
            uint remaining)
        {
            using (var connection = new SqliteConnection(connectionString))
            using (var command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = @"
DELETE FROM character_daily_challenge_progress_events WHERE character_id=@cid;
DELETE FROM character_daily_challenge_entry_claims WHERE character_id=@cid;
DELETE FROM character_daily_challenge_claims WHERE character_id=@cid;
DELETE FROM character_daily_challenge_entries WHERE character_id=@cid;
DELETE FROM character_daily_challenge_groups WHERE character_id=@cid;
INSERT INTO character_daily_challenge_groups(character_id,group_index,group_id) VALUES(@cid,0,0);
INSERT INTO character_daily_challenge_entries
    (character_id,group_index,entry_index,track_like_id,value_a,value_b)
VALUES(@cid,0,0,@questId,@target,@remaining);";
                command.Parameters.AddWithValue("@cid", CharacterId);
                command.Parameters.AddWithValue("@questId", questId);
                command.Parameters.AddWithValue("@target", (long)target);
                command.Parameters.AddWithValue("@remaining", (long)remaining);
                command.ExecuteNonQuery();
            }
        }

        private static void SetFirstEntryRemaining(string connectionString, uint remaining)
        {
            using (var connection = new SqliteConnection(connectionString))
            using (var command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = @"
UPDATE character_daily_challenge_entries SET value_b=@remaining
WHERE character_id=@cid AND group_index=0 AND entry_index=0;";
                command.Parameters.AddWithValue("@cid", CharacterId);
                command.Parameters.AddWithValue("@remaining", (long)remaining);
                command.ExecuteNonQuery();
            }
        }

        private static void InsertProgressEvent(
            string connectionString,
            int questId,
            string sourceEventId)
        {
            using (var connection = new SqliteConnection(connectionString))
            using (var command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = @"
INSERT INTO character_daily_challenge_progress_events
    (character_id, source_event_id, group_index, entry_index, quest_id)
SELECT character_id, @eventId, group_index, entry_index, track_like_id
FROM character_daily_challenge_entries
WHERE character_id=@cid AND track_like_id=@questId;";
                command.Parameters.AddWithValue("@cid", CharacterId);
                command.Parameters.AddWithValue("@questId", questId);
                command.Parameters.AddWithValue("@eventId", sourceEventId);
                command.ExecuteNonQuery();
            }
        }

        private static void ReplaceFirstEntryQuest(
            string connectionString,
            int questId,
            uint target,
            uint remaining)
        {
            using (var connection = new SqliteConnection(connectionString))
            using (var command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = @"
UPDATE character_daily_challenge_entries
SET track_like_id=@questId,
    value_a=@target,
    value_b=@remaining
WHERE character_id=@cid
  AND group_index=(
      SELECT MIN(group_index)
      FROM character_daily_challenge_entries
      WHERE character_id=@cid
  )
  AND entry_index=0;";
                command.Parameters.AddWithValue("@cid", CharacterId);
                command.Parameters.AddWithValue("@questId", questId);
                command.Parameters.AddWithValue("@target", (long)target);
                command.Parameters.AddWithValue("@remaining", (long)remaining);
                command.ExecuteNonQuery();
            }
        }

        private static void CompleteFirstEntries(
            string connectionString,
            int groupIndex,
            int count)
        {
            using (var connection = new SqliteConnection(connectionString))
            using (var command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = @"
UPDATE character_daily_challenge_entries SET value_b=0
WHERE character_id=@cid
  AND group_index=@groupIndex
  AND entry_index < @count;";
                command.Parameters.AddWithValue("@cid", CharacterId);
                command.Parameters.AddWithValue("@groupIndex", groupIndex);
                command.Parameters.AddWithValue("@count", count);
                command.ExecuteNonQuery();
            }
        }

        private static uint ReadRemaining(string connectionString, int questId)
        {
            using (var connection = new SqliteConnection(connectionString))
            using (var command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = @"
SELECT value_b FROM character_daily_challenge_entries
WHERE character_id=@cid AND track_like_id=@questId;";
                command.Parameters.AddWithValue("@cid", CharacterId);
                command.Parameters.AddWithValue("@questId", questId);
                return Convert.ToUInt32(command.ExecuteScalar());
            }
        }

        private static long Scalar(string connectionString, string sql)
        {
            using (var connection = new SqliteConnection(connectionString))
            using (var command = connection.CreateCommand())
            {
                connection.Open();
                command.CommandText = sql;
                command.Parameters.AddWithValue("@cid", CharacterId);
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        private static bool TableExists(SqliteConnection connection, string table)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@name;";
                command.Parameters.AddWithValue("@name", table);
                return Convert.ToInt32(command.ExecuteScalar()) == 1;
            }
        }

        private static void Check(string name, bool ok, ref int failures)
        {
            Console.WriteLine($"[{(ok ? "OK" : "FAIL")}] {name}");
            if (!ok)
                failures++;
        }

        private static byte[] BuildWireFinishBody(ushort questId)
        {
            var body = new byte[10];
            BitConverter.GetBytes((ushort)CmdPacketTypeA21.FINISH_QUEST)
                .CopyTo(body, 0);
            BitConverter.GetBytes(questId).CopyTo(body, 2);
            BitConverter.GetBytes(ushort.MaxValue).CopyTo(body, 4);
            BitConverter.GetBytes((ushort)1).CopyTo(body, 6);
            BitConverter.GetBytes(ushort.MaxValue).CopyTo(body, 8);
            return body;
        }

        private sealed class RecordingSender : ISessionPacketSender
        {
            internal List<string> Calls { get; } = new List<string>();
            internal Dictionary<ushort, byte[]> NotiBodies { get; } =
                new Dictionary<ushort, byte[]>();
            internal byte[] LastAckBody { get; private set; }

            public PlayerContext Player { get; } = new PlayerContext
            {
                CharacterId = A21DailyChallengeSelfTest.CharacterId,
                Level = 61,
            };

            public int CharacterId => A21DailyChallengeSelfTest.CharacterId;
            public int AccountId => A21DailyChallengeSelfTest.AccountId;

            public Task SendPacketAsync(byte[] rawPacket) => Task.CompletedTask;

            public Task SendNotiAsync(ushort notiType, byte[] body)
            {
                Calls.Add($"NOTI:{notiType:X4}");
                NotiBodies[notiType] = body;
                return Task.CompletedTask;
            }

            public Task SendCmdAckAsync(ushort cmdType, byte[] body)
            {
                Calls.Add($"ACK:{cmdType:X4}");
                LastAckBody = body;
                return Task.CompletedTask;
            }
        }
    }
}
