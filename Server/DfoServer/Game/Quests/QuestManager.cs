using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.CharacterData;
using DfoServer.Game.Dungeon;
using DfoServer.Game.ExpertJob;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers.Quest;

namespace DfoServer.Game.Quests
{
    public sealed class QuestManager
    {
        private static readonly TimeSpan DefaultServerTriggerEchoGrace =
            TimeSpan.FromMilliseconds(500);

        private readonly ISessionPacketSender _sender;
        private readonly string _connStr;
        private readonly QuestService _service;
        private readonly DailyChallengeService _dailyChallengeService;
        private readonly ImageCommunicationApplicationService
            _imageCommunicationService;
        private readonly QuestNotifySelectionService _notifySelectionService;
        private readonly QuestNotificationProjector _notifications;
        private readonly TimeSpan _serverTriggerEchoGrace;
        private readonly ClockService _clock;
        private readonly object _serverTriggerProjectionSync = new object();
        private ClockService.ClockTimerHandle _serverTriggerProjectionTimer;
        private int _serverTriggerProjectionVersion;

        public QuestManager(ISessionPacketSender sender, string connStr)
            : this(
                sender,
                GameDatabase.AttachInitialized(connStr),
                characterRepository: null,
                selectCharacterDataSource: null,
                sessionDirectory: null,
                DefaultServerTriggerEchoGrace,
                ClockService.Instance)
        {
        }

        internal QuestManager(
            ISessionPacketSender sender,
            IGameDatabase database,
            ICharacterRepository characterRepository = null,
            SqliteSelectCharacterDataSource selectCharacterDataSource = null,
            ISessionDirectory sessionDirectory = null)
            : this(
                sender,
                database,
                characterRepository,
                selectCharacterDataSource,
                sessionDirectory,
                DefaultServerTriggerEchoGrace,
                ClockService.Instance)
        {
        }

        internal QuestManager(
            ISessionPacketSender sender,
            string connStr,
            TimeSpan serverTriggerEchoGrace,
            ClockService clock)
            : this(
                sender,
                GameDatabase.AttachInitialized(connStr),
                characterRepository: null,
                selectCharacterDataSource: null,
                sessionDirectory: null,
                serverTriggerEchoGrace,
                clock)
        {
        }

        private QuestManager(
            ISessionPacketSender sender,
            IGameDatabase database,
            ICharacterRepository characterRepository,
            SqliteSelectCharacterDataSource selectCharacterDataSource,
            ISessionDirectory sessionDirectory,
            TimeSpan serverTriggerEchoGrace,
            ClockService clock)
        {
            _sender = sender ?? throw new ArgumentNullException(nameof(sender));
            database = database ?? throw new ArgumentNullException(nameof(database));
            _connStr = database.ConnectionString;
            _serverTriggerEchoGrace = serverTriggerEchoGrace > TimeSpan.Zero
                ? serverTriggerEchoGrace
                : DefaultServerTriggerEchoGrace;
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _service = new QuestService(_connStr);
            _dailyChallengeService = new DailyChallengeService(_connStr);
            _imageCommunicationService =
                new ImageCommunicationApplicationService(_connStr);
            _notifySelectionService = new QuestNotifySelectionService(_connStr);
            characterRepository ??= new SqliteCharacterRepository(database);
            var progressRepository = new SqliteCharacterProgressRepository(database);
            var honorLevel = new HonorLevelSyncService(
                characterRepository,
                database);
            var subtype0Repository = new SqliteSubtype0FieldsRepository(database);
            var growthCapsuleRepository = new GrowthCapsuleProgressRepository(database);
            selectCharacterDataSource ??= new SqliteSelectCharacterDataSource(
                database,
                characterRepository);
            _notifications = new QuestNotificationProjector(
                sender,
                database,
                characterRepository,
                progressRepository,
                honorLevel,
                subtype0Repository,
                growthCapsuleRepository,
                new SqliteExpertJobStateRepository(database),
                new SqliteSubtype1Repository(database),
                selectCharacterDataSource,
                sessionDirectory);
        }

        public QuestRunSnapshot CaptureRunSnapshot()
        {
            var characterId = _sender.CharacterId;
            return characterId > 0
                ? QuestRunSnapshot.Capture(
                    QuestService.LoadActiveQuests(_connStr, characterId))
                : QuestRunSnapshot.Empty;
        }

        private static byte[] StripEcho(byte[] body)
        {
            if (body == null || body.Length <= 2) return body;
            var stripped = new byte[body.Length - 2];
            Buffer.BlockCopy(body, 2, stripped, 0, stripped.Length);
            return stripped;
        }

        public async Task HandleAcceptQuestAsync(
            ushort wireType,
            byte[] body,
            Guid sessionId)
        {
            var qBody = StripEcho(body);
            FileLogger.Log($"[GameProtocol] ACCEPT_QUEST payload: {(qBody != null ? BitConverter.ToString(qBody) : "null")} ({qBody?.Length ?? 0}B)");
            int cid = _sender.CharacterId;
            if (cid <= 0) return;
            InventoryContext.TryGetOwnedLease(sessionId, cid, out var lease);
            var owner = new QuestCommandOwnerContext(
                cid,
                _sender.AccountId,
                sessionId,
                lease);
            var result = QuestCommandParser.TryParseAccept(qBody, out var command)
                ? _service.HandleAcceptQuest(owner, command, _sender.AccountId)
                : QuestAcceptResult.Fail(23);
            await _sender.SendCmdAckAsync(wireType, QuestAckBuilder.BuildAccept(result));
            if (result.Success && result.PostAcceptTriggerProjection != null)
            {
                await _notifications.SendTriggerChangesAsync(
                    new[] { result.PostAcceptTriggerProjection });
            }
            if (result.Success)
                await _notifications.SendAcceptableQuestListAsync();
        }

        public async Task HandleImageCommunicationEquipmentUseAsync(
            ushort wireType,
            byte[] body)
        {
            ImageCommunicationUseResult result;
            if (!QuestCommandParser.TryParseImageCommunicationUse(
                    body,
                    out var command))
            {
                result = new ImageCommunicationUseResult
                {
                    Status = ImageCommunicationUseStatus.NoMatchingActiveQuest,
                };
            }
            else
            {
                result = _imageCommunicationService.Apply(
                    _sender.CharacterId,
                    command);
            }

            await _sender.SendCmdAckAsync(
                wireType,
                ImageCommunicationAckBuilder.Build(result.NpcIndex));

            if (!result.Success)
            {
                FileLogger.Log(
                    $"[ImageCommunication] use rejected "
                    + $"cid={_sender.CharacterId} status={result.Status} "
                    + $"bodyLen={body?.Length ?? 0}");
            }
        }

        public async Task HandleGiveupQuestAsync(
            ushort wireType,
            byte[] body,
            Guid sessionId)
        {
            var qBody = StripEcho(body);
            int cid = _sender.CharacterId;
            if (cid <= 0) return;
            InventoryContext.TryGetOwnedLease(sessionId, cid, out var lease);
            var owner = new QuestCommandOwnerContext(
                cid,
                _sender.AccountId,
                sessionId,
                lease);
            var result = QuestCommandParser.TryParseGiveup(qBody, out var command)
                ? _service.HandleGiveupQuest(owner, command)
                : QuestGiveupResult.Fail(19);
            if (result.Success && result.InventoryChanges.HasChanges)
            {
                foreach (var group in result.InventoryChanges.Slots
                    .GroupBy(change => change.ListType))
                {
                    await InventoryRefreshSender.SendOnlineUpdateItemList(
                        _sender,
                        group.Key,
                        group.Select(change => change.SlotIndex));
                }
            }
            await _sender.SendCmdAckAsync(wireType, QuestAckBuilder.BuildGiveup(result));
        }

        public async Task<QuestSetTriggerResult> HandleSetTriggerAsync(
            ushort wireType,
            byte[] body,
            Guid sessionId)
        {
            var qBody = StripEcho(body);
            int cid = _sender.CharacterId;
            if (cid <= 0) return null;
            if (!InventoryContext.TryGetOwnedLease(sessionId, cid, out var lease))
            {
                var rejected = QuestSetTriggerResult.Fail(22);
                await _sender.SendCmdAckAsync(
                    wireType,
                    QuestAckBuilder.BuildSetTrigger(rejected));
                return rejected;
            }
            var owner = new QuestCommandOwnerContext(
                cid,
                _sender.AccountId,
                sessionId,
                lease,
                _sender.Player?.Exp);

            if (_dailyChallengeService.TryHandleSetTrigger(cid, qBody, out var dailyChallenge))
            {
                await _sender.SendNotiAsync(
                    (ushort)NotiPacketTypeA21.DAILY_CHALLENGE,
                    DailyChallengeBodyBuilder.Build(dailyChallenge.Snapshot));
                return dailyChallenge.Ack;
            }

            QuestSetTriggerResult deferred;
            if (TryBuildServerDrivenQuestTriggerEcho(cid, qBody, out deferred))
            {
                await _sender.SendCmdAckAsync(
                    wireType,
                    QuestAckBuilder.BuildSetTrigger(deferred));
                return deferred;
            }

            if (TryBuildDeferredClearMapSetTrigger(cid, qBody, out deferred))
            {
                await _sender.SendCmdAckAsync(wireType, QuestAckBuilder.BuildSetTrigger(deferred));
                return deferred;
            }

            var result = _service.HandleSetTrigger(owner, qBody);
            await _sender.SendCmdAckAsync(wireType, QuestAckBuilder.BuildSetTrigger(result));
            return result;
        }

        internal DailyChallengeRewardClaimResult HandleDailyChallengeReward(
            Guid sessionId,
            byte[] body)
        {
            var characterId = _sender.CharacterId;
            if (characterId <= 0 || body == null || body.Length != 4)
            {
                return DailyChallengeRewardClaimResult.Rejected(
                    DailyChallengeRewardClaimStatus.InvalidRequest,
                    -1,
                    null);
            }

            var groupIndex = BitConverter.ToInt32(body, 0);
            if (!InventoryContext.TryGetOwnedLease(sessionId, characterId, out var lease))
            {
                return DailyChallengeRewardClaimResult.Rejected(
                    DailyChallengeRewardClaimStatus.InvalidRequest,
                    groupIndex,
                    null);
            }

            var owner = new QuestCommandOwnerContext(
                characterId,
                _sender.AccountId,
                sessionId,
                lease);
            return _dailyChallengeService.ClaimReward(
                owner,
                groupIndex);
        }

        public async Task HandleFinishQuestAsync(
            ushort wireType,
            byte[] body,
            Guid sessionId)
        {
            var qBody = StripEcho(body);
            int cid = _sender.CharacterId;
            if (cid <= 0) return;
            InventoryContext.TryGetOwnedLease(sessionId, cid, out var lease);
            var owner = new QuestCommandOwnerContext(
                cid,
                _sender.AccountId,
                sessionId,
                lease,
                _sender.Player?.Exp);
            var result = QuestCommandParser.TryParseFinish(qBody, out var command)
                ? _service.HandleFinishQuest(owner, command)
                : QuestFinishResult.Fail(22);
            await _notifications.SendPreFinishAckNotificationsAsync(cid, result);
            await _sender.SendCmdAckAsync(wireType, QuestAckBuilder.BuildFinish(result));
            await _notifications.ProjectFinishedQuestAsync(cid, result);
            if (result?.DailyChallengeSnapshot != null)
            {
                // The daily snapshot owns progress/claim state, while the
                // standard clear list owns the completed marker rendered by
                // the A21 button. Re-login already publishes both; keep the
                // online claim projection convergent in the same order.
                await _notifications.SendClearQuestListAsync();
                await _sender.SendNotiAsync(
                    (ushort)NotiPacketTypeA21.DAILY_CHALLENGE,
                    DailyChallengeBodyBuilder.Build(result.DailyChallengeSnapshot));
            }
        }

        public async Task HandleScenarioModeClearQuestAsync(
            ushort wireType,
            byte[] body,
            Guid sessionId)
        {
            int cid = _sender.CharacterId;
            if (cid <= 0 || _sender.Player == null)
                return;

            if (!QuestCommandParser.TryParseScenarioModeClearQuest(body, out var command))
            {
                FileLogger.Log(
                    $"[QuestManager] SCENARIO_MODE_CLEAR_QUEST rejected malformed body: " +
                    $"wireType=0x{wireType:X4} len={body?.Length ?? 0} " +
                    $"body={(body != null ? BitConverter.ToString(body) : "null")}");
                return;
            }

            InventoryContext.TryGetOwnedLease(sessionId, cid, out var lease);
            var owner = new QuestCommandOwnerContext(
                cid,
                _sender.AccountId,
                sessionId,
                lease);
            var player = _sender.Player;
            var result = _service.HandleScenarioModeClearQuest(
                owner,
                command,
                player.Level,
                player.Job,
                player.GrowType);
            if (result == null || !result.Success)
                return;

            if (result.InventoryChanges.HasChanges)
            {
                foreach (var group in result.InventoryChanges.Slots
                    .GroupBy(change => change.ListType))
                {
                    await InventoryRefreshSender.SendOnlineUpdateItemList(
                        _sender,
                        group.Key,
                        group.Select(change => change.SlotIndex));
                }
            }

            await TrySendQuestStatePartAsync(
                "active",
                () => SendActiveQuestListAsync());
            await TrySendQuestStatePartAsync(
                "acceptable",
                () => SendAcceptableQuestListAsync());
            await TrySendQuestStatePartAsync(
                "clear",
                () => SendClearQuestListAsync());
        }

        internal async Task SendPreFinishAckNotificationsAsync(
            QuestFinishResult result)
        {
            int cid = _sender.CharacterId;
            if (cid <= 0)
                return;

            await _notifications.SendPreFinishAckNotificationsAsync(cid, result);
        }

        internal async Task ProjectFinishedQuestAsync(
            QuestFinishResult result,
            bool sendAcceptableQuestList = true)
        {
            int cid = _sender.CharacterId;
            if (cid <= 0)
                return;

            await _notifications.ProjectFinishedQuestAsync(
                cid,
                result,
                sendAcceptableQuestList);
        }

        public void HandleSaveQuestNotify(byte[] body)
        {
            var characterId = _sender.CharacterId;
            if (characterId <= 0)
                return;

            if (!QuestCommandParser.TryParseSaveNotify(body, out var command)
                || !_notifySelectionService.TryReplace(characterId, command))
            {
                var bodyHex = body == null ? "null" : BitConverter.ToString(body);
                FileLogger.Log(
                    $"[QuestManager] SAVE_QUEST_NOTIFY rejected: " +
                    $"cid={characterId} body={bodyHex}");
                return;
            }

            FileLogger.Log(
                $"[QuestManager] SAVE_QUEST_NOTIFY persisted: " +
                $"cid={characterId} count={command.QuestIds.Count}");
        }

        public async Task SendActiveQuestListAsync()
        {
            int cid = _sender.CharacterId;
            if (cid <= 0) return;
            await _notifications.SendActiveQuestListAsync(cid);
        }

        public async Task SendGrowupChangeRefreshAsync()
        {
            int cid = _sender.CharacterId;
            if (cid <= 0) return;

            await _notifications.SendGrowupChangeRefreshAsync(cid);
            await _notifications.SendActiveQuestListAsync(cid);
            await _notifications.SendAcceptableQuestListAsync();
        }

        public async Task SyncItemSeekingQuestProgressAsync(
            ICollection<int> itemFilter,
            IReadOnlyDictionary<int, int> temporaryHeldCounts = null)
        {
            int cid = _sender.CharacterId;
            if (cid <= 0) return;

            bool matched = SyncItemSeekingQuestProgressWithoutNotification(
                itemFilter,
                temporaryHeldCounts);
            if (!matched)
                return;

            await _notifications.SendActiveQuestListAsync(cid);
        }

        internal async Task SyncItemSeekingQuestProgressAfterInventoryMutationAsync(
            InventoryLease expectedLease,
            InventoryMutationResult mutation)
        {
            if (mutation == null)
                return;

            await SyncItemSeekingQuestProgressAfterInventoryMutationsAsync(
                expectedLease,
                new[] { mutation });
        }

        internal async Task SyncItemSeekingQuestProgressAfterInventoryMutationsAsync(
            InventoryLease expectedLease,
            IEnumerable<InventoryMutationResult> mutations)
        {
            var changes = RecalibrateItemSeekingQuestProgressAfterInventoryMutations(
                expectedLease,
                mutations);
            if (changes.Count == 0)
                return;

            var cid = _sender.CharacterId;
            if (!InventoryContext.IsCurrentLease(
                    expectedLease,
                    expectedLease.SessionId,
                    cid))
                return;

            await _notifications.SendTriggerChangesAsync(changes);
        }

        internal bool RecalibrateItemSeekingQuestProgressAfterInventoryMutationWithoutNotification(
            InventoryLease expectedLease,
            InventoryMutationResult mutation)
        {
            return mutation != null
                && RecalibrateItemSeekingQuestProgressAfterInventoryMutationsWithoutNotification(
                    expectedLease,
                    new[] { mutation });
        }

        internal bool RecalibrateItemSeekingQuestProgressAfterInventoryMutationsWithoutNotification(
            InventoryLease expectedLease,
            IEnumerable<InventoryMutationResult> mutations)
            => RecalibrateItemSeekingQuestProgressAfterInventoryMutations(
                expectedLease,
                mutations).Count > 0;

        private IReadOnlyList<QuestSetTriggerResult>
            RecalibrateItemSeekingQuestProgressAfterInventoryMutations(
                InventoryLease expectedLease,
                IEnumerable<InventoryMutationResult> mutations)
        {
            var cid = _sender.CharacterId;
            if (expectedLease == null
                || !InventoryContext.IsCurrentLease(
                    expectedLease,
                    expectedLease.SessionId,
                    cid))
            {
                return Array.Empty<QuestSetTriggerResult>();
            }

            var itemFilter = CollectInventoryMutationItemFilter(mutations);
            return itemFilter.Count > 0
                ? _service.SyncItemSeekingQuestProgressChanges(
                    cid,
                    _sender.AccountId,
                    itemFilter)
                : Array.Empty<QuestSetTriggerResult>();
        }

        private static HashSet<int> CollectInventoryMutationItemFilter(
            IEnumerable<InventoryMutationResult> mutations)
        {
            var itemFilter = new HashSet<int>();
            var pending = new Stack<InventoryMutationResult>();
            var visited = new HashSet<InventoryMutationResult>();
            if (mutations != null)
            {
                foreach (var mutation in mutations)
                {
                    if (mutation != null)
                        pending.Push(mutation);
                }
            }
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (current == null || !visited.Add(current))
                    continue;

                if (current.ItemTemplateId > 0)
                    itemFilter.Add(current.ItemTemplateId);
                if (current.CostItemTemplateId > 0)
                    itemFilter.Add(current.CostItemTemplateId);
                if (current.GoldSpent)
                    itemFilter.Add(0);
                if (current.MainVirtualCountChanged
                    && InventoryService.TryResolveMainVirtualItemId(
                        current.SlotIndex,
                        out var virtualItemId))
                {
                    itemFilter.Add(virtualItemId);
                }

                foreach (var extra in current.ExtraResults)
                {
                    if (extra != null)
                        pending.Push(extra);
                }
            }

            return itemFilter;
        }

        public bool SyncItemSeekingQuestProgressWithoutNotification(
            ICollection<int> itemFilter,
            IReadOnlyDictionary<int, int> temporaryHeldCounts = null)
        {
            int cid = _sender.CharacterId;
            if (cid <= 0)
                return false;

            return _service.SyncItemSeekingQuestProgress(
                cid,
                _sender.AccountId,
                itemFilter,
                temporaryHeldCounts);
        }

        public bool RecalibrateItemSeekingQuestProgressWithoutNotification(
            ICollection<int> itemFilter,
            IReadOnlyDictionary<int, int> temporaryHeldCounts = null)
        {
            return SyncItemSeekingQuestProgressWithoutNotification(
                itemFilter,
                temporaryHeldCounts);
        }

        public async Task SyncClearMapQuestProgressAsync(
            int dungeonId,
            int mapId,
            Guid sourceEventId = default,
            IReadOnlyCollection<ushort> eligibleQuestIds = null,
            IReadOnlyDictionary<ushort, QuestActivationId>
                eligibleQuestActivations = null)
        {
            int cid = _sender.CharacterId;
            if (cid <= 0) return;

            bool changed = _service.SyncClearMapQuestProgress(
                cid,
                dungeonId,
                mapId,
                sourceEventId,
                eligibleQuestIds,
                eligibleQuestActivations);
            if (!changed)
                return;

            await _notifications.SendActiveQuestListAsync(cid);
        }

        public Task SyncHuntMonsterQuestProgressAsync(
            int dungeonId,
            int difficulty,
            int monsterCode,
            Guid sourceEventId = default,
            IReadOnlyCollection<ushort> eligibleQuestIds = null,
            DungeonRunIdentity sourceRunIdentity = default,
            IReadOnlyDictionary<ushort, QuestActivationId>
                eligibleQuestActivations = null,
            byte monsterType = 0)
        {
            var cid = _sender.CharacterId;
            if (cid <= 0
                || dungeonId <= 0
                || monsterCode <= 0
                || monsterType > 3)
                return Task.CompletedTask;

            var changes = _service.SyncHuntMonsterQuestProgress(
                cid,
                dungeonId,
                difficulty,
                monsterCode,
                sourceEventId,
                eligibleQuestIds,
                eligibleQuestActivations,
                monsterType);
            TrackServerDrivenTriggerChanges(
                cid,
                changes,
                sourceRunIdentity);
            return Task.CompletedTask;
        }

        public Task SyncHuntEnemyQuestProgressAsync(
            int dungeonId,
            int difficulty,
            int enemyCode,
            int enemyType,
            Guid sourceEventId = default,
            IReadOnlyCollection<ushort> eligibleQuestIds = null,
            DungeonRunIdentity sourceRunIdentity = default,
            IReadOnlyDictionary<ushort, QuestActivationId>
                eligibleQuestActivations = null)
        {
            var cid = _sender.CharacterId;
            if (cid <= 0 || dungeonId <= 0 || enemyCode <= 0)
                return Task.CompletedTask;

            var changes = _service.SyncHuntEnemyQuestProgress(
                cid,
                dungeonId,
                difficulty,
                enemyCode,
                enemyType,
                sourceEventId,
                eligibleQuestIds,
                eligibleQuestActivations);
            TrackServerDrivenTriggerChanges(
                cid,
                changes,
                sourceRunIdentity);
            return Task.CompletedTask;
        }

        private void TrackServerDrivenTriggerChanges(
            int characterId,
            IReadOnlyList<QuestSetTriggerResult> changes,
            DungeonRunIdentity sourceRunIdentity)
        {
            if (changes == null || changes.Count == 0)
                return;

            var run = _sender.Player?.CurrentRun;
            if (run == null
                || (sourceRunIdentity.IsValid
                    && !run.Matches(sourceRunIdentity)))
            {
                return;
            }

            foreach (var change in changes)
            {
                var channelIndex = FindDecrementedTriggerChannel(change);
                if (channelIndex >= 0)
                {
                    run.MarkServerDrivenQuestTrigger(
                        change.QuestId,
                        channelIndex);
                }
            }

            if (run.HasPendingServerDrivenQuestTriggers())
                ScheduleServerTriggerProjectionFallback(run, characterId);
        }

        private bool TryBuildServerDrivenQuestTriggerEcho(
            int characterId,
            byte[] qBody,
            out QuestSetTriggerResult result)
        {
            result = null;
            if (qBody == null || qBody.Length < 3)
                return false;

            var triggerType = qBody[2];
            var isIncrement = qBody.Length >= 4 && qBody[3] != 0;
            if (triggerType == 1 || isIncrement)
                return false;

            var questId = BitConverter.ToUInt16(qBody, 0);
            var run = _sender.Player?.CurrentRun;
            if (run == null
                || !run.TryConsumeServerDrivenQuestTrigger(
                    questId,
                    triggerType))
            {
                return false;
            }

            if (!run.HasPendingServerDrivenQuestTriggers())
                CancelServerTriggerProjectionFallback();

            var active = QuestService.LoadActiveQuests(_connStr, characterId);
            var quest = QuestService.FindByQuestId(active, questId);
            var trigger = quest?.TriggerValue ?? 0;
            result = new QuestSetTriggerResult
            {
                QuestId = questId,
                PreviousTriggerValue = trigger,
                TriggerValue = trigger,
            };
            FileLogger.Log(
                $"[QuestManager] SET_TRIGGER echo suppressed after " +
                $"server quest progress: cid={characterId} quest={questId} " +
                $"type=0x{triggerType:X2} trigger={trigger}");
            return true;
        }

        private static int FindDecrementedTriggerChannel(
            QuestSetTriggerResult change)
        {
            if (change == null)
                return -1;

            var previous = new QuestTrigger(change.PreviousTriggerValue);
            var current = new QuestTrigger(change.TriggerValue);
            for (var channelIndex = 0; channelIndex < 3; channelIndex++)
            {
                if (previous.GetChannel(channelIndex)
                    == current.GetChannel(channelIndex) + 1)
                {
                    return channelIndex;
                }
            }

            return -1;
        }

        private void ScheduleServerTriggerProjectionFallback(
            DungeonRun run,
            int characterId)
        {
            if (run == null || characterId <= 0)
                return;

            var identity = run.CaptureIdentity();
            ClockService.ClockTimerHandle previous;
            int version;
            lock (_serverTriggerProjectionSync)
            {
                previous = _serverTriggerProjectionTimer;
                _serverTriggerProjectionTimer = null;
                version = NextProjectionVersion(_serverTriggerProjectionVersion);
                _serverTriggerProjectionVersion = version;
            }
            previous?.Cancel();

            var timer = _clock.ScheduleOneShotAfterAsync(
                $"quest-trigger:{characterId}:{identity.RunId}:projection",
                _serverTriggerEchoGrace,
                _ => FlushServerTriggerProjectionFallbackAsync(
                    run,
                    identity,
                    characterId,
                    version));

            lock (_serverTriggerProjectionSync)
            {
                if (_serverTriggerProjectionVersion != version)
                {
                    timer.Cancel();
                    return;
                }

                _serverTriggerProjectionTimer = timer;
            }
        }

        private async Task FlushServerTriggerProjectionFallbackAsync(
            DungeonRun run,
            DungeonRunIdentity identity,
            int characterId,
            int version)
        {
            lock (_serverTriggerProjectionSync)
            {
                if (_serverTriggerProjectionVersion != version)
                    return;
                _serverTriggerProjectionTimer = null;
            }

            var currentRun = _sender.Player?.CurrentRun;
            if (_sender.CharacterId != characterId
                || currentRun == null
                || !ReferenceEquals(currentRun, run)
                || !currentRun.Matches(identity)
                || !run.HasPendingServerDrivenQuestTriggers())
            {
                return;
            }

            await _notifications.SendActiveQuestListAsync(characterId);
            FileLogger.Log(
                $"[QuestManager] SERVER_TRIGGER projection fallback: " +
                $"cid={characterId} run={identity.RunId} " +
                $"pending client echo after {_serverTriggerEchoGrace.TotalMilliseconds:F0}ms");
        }

        private void CancelServerTriggerProjectionFallback()
        {
            ClockService.ClockTimerHandle timer;
            lock (_serverTriggerProjectionSync)
            {
                _serverTriggerProjectionVersion = NextProjectionVersion(
                    _serverTriggerProjectionVersion);
                timer = _serverTriggerProjectionTimer;
                _serverTriggerProjectionTimer = null;
            }
            timer?.Cancel();
        }

        private static int NextProjectionVersion(int version)
        {
            version = unchecked(version + 1);
            return version == 0 ? 1 : version;
        }

        private bool TryBuildDeferredClearMapSetTrigger(int characterId, byte[] qBody, out QuestSetTriggerResult result)
        {
            result = null;
            if (qBody == null || qBody.Length < 3)
                return false;

            var run = _sender.Player?.CurrentRun;
            if (run == null || run.DungeonId <= 0)
                return false;

            ushort questId = BitConverter.ToUInt16(qBody, 0);
            byte triggerType = qBody[2];
            bool isIncrement = qBody.Length >= 4 && qBody[3] != 0;
            if (!ShouldDeferQuestConnectedStartMapSetTrigger(
                    questId,
                    triggerType,
                    isIncrement,
                    run.Phase >= Dungeon.DungeonRunPhase.Cleared,
                    run.MazeQuestConnected,
                    run.MazeStartMapId))
                return false;

            var active = QuestService.LoadActiveQuests(_connStr, characterId);
            var quest = QuestService.FindByQuestId(active, questId);
            if (quest == null || quest.TriggerValue == 0)
                return false;

            result = new QuestSetTriggerResult
            {
                QuestId = questId,
                PreviousTriggerValue = quest.TriggerValue,
                TriggerValue = quest.TriggerValue,
            };
            FileLogger.Log($"[QuestManager] SET_TRIGGER deferred clear-map start target: cid={characterId} quest={questId} trigger={quest.TriggerValue} dungeon={run.DungeonId} maze={run.MazeIndex} map={run.MazeStartMapId}");
            return true;
        }

        internal static bool ShouldDeferQuestConnectedStartMapSetTrigger(
            ushort questId,
            byte triggerType,
            bool isIncrement,
            bool dungeonCleared,
            bool mazeQuestConnected,
            int mazeStartMapId)
        {
            if (questId == 0 || dungeonCleared || !mazeQuestConnected || mazeStartMapId <= 0)
                return false;
            if (triggerType != 0 || isIncrement)
                return false;

            return ShouldDeferQuestConnectedStartMapQuest(questId, mazeStartMapId);
        }

        internal bool HasDeferredQuestConnectedStartMapClearQuest(int characterId, int mazeStartMapId)
        {
            if (characterId <= 0 || mazeStartMapId <= 0)
                return false;

            var active = QuestService.LoadActiveQuests(_connStr, characterId);
            foreach (var quest in active)
            {
                if (quest.TriggerValue == 0)
                    continue;
                if (ShouldDeferQuestConnectedStartMapQuest(quest.QuestId, mazeStartMapId))
                    return true;
            }

            return false;
        }

        private static bool ShouldDeferQuestConnectedStartMapQuest(ushort questId, int mazeStartMapId)
        {
            var qst = GameWorld.QuestData.GetQuestFile(questId);
            if (qst == null || qst.CompleteNpcIndex < 0)
                return false;

            return GameWorld.QuestData.MatchesClearMapTarget(qst, dungeonId: 0, mapId: mazeStartMapId);
        }

        public async Task SendAcceptableQuestListAsync()
        {
            await _notifications.SendAcceptableQuestListAsync();
        }

        public async Task SendClearQuestListAsync()
        {
            await _notifications.SendClearQuestListAsync();
        }

        private static async Task TrySendQuestStatePartAsync(
            string name,
            Func<Task> send)
        {
            try
            {
                await send();
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[QuestManager] SCENARIO_MODE_CLEAR_QUEST {name} " +
                    $"quest-state refresh failed: {ex.Message}");
            }
        }
    }
}
