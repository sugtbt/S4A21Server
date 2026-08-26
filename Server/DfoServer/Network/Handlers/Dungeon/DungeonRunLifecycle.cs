using DfoServer.Game.Dungeon;
using DfoServer.Game.Quests;
using DfoServer.Infrastructure;
using DfoServer.Network.Handlers.Pets;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Dungeon
{
    // 一局副本的生命周期唯一入口。
    // 真相载体是 PlayerContext.CurrentRun: 进本 new 一个 DungeonRun, 结束置 null,
    // 单局字段随对象消失 -- 全仓不再有逐字段重置清单。
    internal static class DungeonRunLifecycle
    {
        // 进本: 掐掉旧局残留定时器 -> 换新局。
        internal static bool BeginRun(
            EnhancedClientSession session,
            int dungeonId,
            byte difficulty,
            DungeonInstance sharedInstance = null,
            DungeonInstanceRegistry instanceRegistry = null,
            DungeonParticipantExperienceBonusSnapshot?
                experienceBonusSnapshot = null,
            DungeonSelectionContext expectedSelection = null)
        {
            if (expectedSelection != null)
            {
                return BeginRunFromSelection(
                    session,
                    dungeonId,
                    difficulty,
                    expectedSelection,
                    sharedInstance,
                    experienceBonusSnapshot);
            }

            var towerItemIds = CaptureTowerItemIds(session);
            var oldRun = session?.Player?.CurrentRun;
            var returnAnchor = session?.Player?.CurrentDungeonSelection?.ReturnAnchor
                ?? oldRun?.TownReturnAnchor
                ?? default(DungeonTownReturnAnchor);
            if (oldRun != null)
            {
                instanceRegistry?.Terminate(
                    session.Player.CharacterId,
                    oldRun.CaptureIdentity(),
                    DungeonRunEndReason.ReplacedByNewRun.ToString());
            }
            CancelAllTimers(oldRun);
            DeathTowerCoordinator.ClearTowerState(session);

            var instance = sharedInstance ?? CreateInstance(dungeonId, difficulty);
            if (instance.DungeonId != dungeonId || instance.Difficulty != difficulty)
                throw new InvalidOperationException("A participant run must match its shared dungeon instance.");
            var questSnapshot = CaptureQuestSnapshot(session);

            DungeonRun newRun;
            lock (session.Player.DungeonRunLifecycleSyncRoot)
            {
                oldRun?.TryBeginEnding();
                var generation = session.Player.NextDungeonRunGeneration();
                newRun = new DungeonRun(
                    instance,
                    DungeonIdentityGenerator.NextRunId(),
                    generation,
                    DungeonRunState.Created);
                if (!newRun.TryFreezeExperienceBonusSnapshot(
                        experienceBonusSnapshot
                            ?? DungeonParticipantExperienceBonusSnapshot.None))
                {
                    throw new InvalidOperationException(
                        "Participant experience bonus snapshot was already frozen.");
                }
                ConfigureDropCharacterContext(session, newRun);
                newRun.QuestSnapshot = questSnapshot;
                newRun.TownReturnAnchor = returnAnchor;
                newRun.TryBeginSelecting();
                session.Player.ClearDungeonSelection();
                session.Player.CurrentRun = newRun;
            }
            oldRun?.TryMarkEnded();
            return FinalizeNormalRunStart(
                session,
                newRun,
                dungeonId,
                towerItemIds);
        }

        // 进塔: 塔是一局副本的变体, 同样换新局(顺带丢弃上一局的全部残留状态)。
        internal static bool BeginTowerRun(
            EnhancedClientSession session,
            int dungeonId,
            Game.DeathTower.DeathTowerSession tower,
            byte difficulty = 0,
            DungeonInstanceRegistry instanceRegistry = null,
            DungeonParticipantExperienceBonusSnapshot?
                experienceBonusSnapshot = null,
            DungeonSelectionContext expectedSelection = null)
        {
            if (expectedSelection != null)
            {
                return BeginTowerRunFromSelection(
                    session,
                    dungeonId,
                    tower,
                    difficulty,
                    expectedSelection,
                    experienceBonusSnapshot);
            }

            var towerItemIds = CaptureTowerItemIds(session);
            var oldRun = session?.Player?.CurrentRun;
            var returnAnchor = session?.Player?.CurrentDungeonSelection?.ReturnAnchor
                ?? oldRun?.TownReturnAnchor
                ?? default(DungeonTownReturnAnchor);
            if (oldRun != null)
            {
                instanceRegistry?.Terminate(
                    session.Player.CharacterId,
                    oldRun.CaptureIdentity(),
                    DungeonRunEndReason.ReplacedByNewRun.ToString());
            }
            CancelAllTimers(oldRun);
            DeathTowerCoordinator.ClearTowerState(session);

            DungeonRun newRun;
            var questSnapshot = CaptureQuestSnapshot(session);
            lock (session.Player.DungeonRunLifecycleSyncRoot)
            {
                oldRun?.TryBeginEnding();
                var instance = CreateInstance(dungeonId, difficulty);
                newRun = new DungeonRun(
                    instance,
                    DungeonIdentityGenerator.NextRunId(),
                    session.Player.NextDungeonRunGeneration(),
                    DungeonRunState.Active);
                if (!newRun.TryFreezeExperienceBonusSnapshot(
                        experienceBonusSnapshot
                            ?? DungeonParticipantExperienceBonusSnapshot.None))
                {
                    throw new InvalidOperationException(
                        "Participant experience bonus snapshot was already frozen.");
                }
                ConfigureDropCharacterContext(session, newRun);
                newRun.QuestSnapshot = questSnapshot;
                newRun.TownReturnAnchor = returnAnchor;
                newRun.Tower = tower;
                session.Player.ClearDungeonSelection();
                session.Player.CurrentRun = newRun;
            }
            oldRun?.TryMarkEnded();
            return FinalizeTowerRunStart(session, newRun, towerItemIds);
        }

        private static bool BeginRunFromSelection(
            EnhancedClientSession session,
            int dungeonId,
            byte difficulty,
            DungeonSelectionContext expectedSelection,
            DungeonInstance sharedInstance,
            DungeonParticipantExperienceBonusSnapshot?
                experienceBonusSnapshot)
        {
            var player = session?.Player;
            if (player == null)
                return false;

            var towerItemIds = CaptureTowerItemIds(session);
            var instance = sharedInstance ?? CreateInstance(dungeonId, difficulty);
            if (instance.DungeonId != dungeonId
                || instance.Difficulty != difficulty)
            {
                throw new InvalidOperationException(
                    "A participant run must match its shared dungeon instance.");
            }
            var questSnapshot = CaptureQuestSnapshot(session);
            if (!player.TryReplaceDungeonSelectionWithRun(
                    expectedSelection,
                    generation =>
                    {
                        var created = new DungeonRun(
                            instance,
                            DungeonIdentityGenerator.NextRunId(),
                            generation,
                            DungeonRunState.Created);
                        ConfigureCreatedRun(
                            session,
                            created,
                            questSnapshot,
                            expectedSelection.ReturnAnchor,
                            expectedSelection.IsA21TutorialEntry,
                            experienceBonusSnapshot);
                        created.TryBeginSelecting();
                        return created;
                    },
                    out var newRun))
            {
                return false;
            }

            return FinalizeNormalRunStart(
                session,
                newRun,
                dungeonId,
                towerItemIds);
        }

        private static bool BeginTowerRunFromSelection(
            EnhancedClientSession session,
            int dungeonId,
            Game.DeathTower.DeathTowerSession tower,
            byte difficulty,
            DungeonSelectionContext expectedSelection,
            DungeonParticipantExperienceBonusSnapshot?
                experienceBonusSnapshot)
        {
            var player = session?.Player;
            if (player == null)
                return false;

            var towerItemIds = CaptureTowerItemIds(session);
            var questSnapshot = CaptureQuestSnapshot(session);
            if (!player.TryReplaceDungeonSelectionWithRun(
                    expectedSelection,
                    generation =>
                    {
                        var created = new DungeonRun(
                            CreateInstance(dungeonId, difficulty),
                            DungeonIdentityGenerator.NextRunId(),
                            generation,
                            DungeonRunState.Active);
                        ConfigureCreatedRun(
                            session,
                            created,
                            questSnapshot,
                            expectedSelection.ReturnAnchor,
                            expectedSelection.IsA21TutorialEntry,
                            experienceBonusSnapshot);
                        created.Tower = tower;
                        return created;
                    },
                    out var newRun))
            {
                return false;
            }

            return FinalizeTowerRunStart(session, newRun, towerItemIds);
        }

        private static void ConfigureCreatedRun(
            EnhancedClientSession session,
            DungeonRun run,
            QuestRunSnapshot questSnapshot,
            DungeonTownReturnAnchor returnAnchor,
            bool isA21TutorialEntry,
            DungeonParticipantExperienceBonusSnapshot?
                experienceBonusSnapshot)
        {
            if (!run.TryFreezeExperienceBonusSnapshot(
                    experienceBonusSnapshot
                        ?? DungeonParticipantExperienceBonusSnapshot.None))
            {
                throw new InvalidOperationException(
                    "Participant experience bonus snapshot was already frozen.");
            }
            ConfigureDropCharacterContext(session, run);
            run.QuestSnapshot = questSnapshot;
            run.TownReturnAnchor = returnAnchor;
            run.IsA21TutorialEntry = isA21TutorialEntry;
        }

        private static void ConfigureDropCharacterContext(
            EnhancedClientSession session,
            DungeonRun run)
        {
            var player = session?.Player;
            if (run == null || player == null)
                return;

            run.ChronicleDropJobGroup =
                GameWorld.IndependentDropDefinitionCatalog
                    .ResolveChronicleDropJobGroup(
                        player.Job,
                        player.GrowType);
            run.DimensionDropJob = player.Job;
            run.DimensionDropGrowType = player.GrowType;
        }

        private static bool FinalizeNormalRunStart(
            EnhancedClientSession session,
            DungeonRun newRun,
            int dungeonId,
            ICollection<int> towerItemIds)
        {
            var runIdentity = newRun.CaptureIdentity();
            if (!session.Player.IsCurrentDungeonRun(runIdentity))
                return false;
            if (newRun.RewardPolicy.Kind != DungeonRewardPolicyKind.Standard)
            {
                FileLogger.Log(
                    $"[DungeonRunLifecycle] configured reward policy " +
                    $"cid={session.Player.CharacterId} dungeon={dungeonId} " +
                    $"instance={newRun.PartyDungeonInstanceId} " +
                    $"policy={newRun.RewardPolicy.Kind}");
            }
            if (newRun.DropDefinition.Kind != DungeonDropDefinitionKind.Standard)
            {
                FileLogger.Log(
                    $"[DungeonRunLifecycle] configured drop definition " +
                    $"cid={session.Player.CharacterId} dungeon={dungeonId} " +
                    $"instance={newRun.PartyDungeonInstanceId} " +
                    $"kind={newRun.DropDefinition.Kind} " +
                    $"shared={newRun.DropDefinition.SharedDungeonId} " +
                    $"classification={newRun.DropDefinition.ImpossibleClassification} " +
                    $"jobGroup={newRun.ChronicleDropJobGroup} " +
                    $"sources={newRun.DropPolicy.AllowedSources}");
            }
            DungeonMechanismCoordinator.OnRunCreated(session, newRun, "begin_run");
            RecalibrateTowerQuestOverlayWithoutNotification(session, towerItemIds);
            if (!session.Player.IsCurrentDungeonRun(runIdentity))
                return false;
            PetCreatureRuntimeService.BeginDungeon(session, runIdentity, "begin_run");
            return session.Player.IsCurrentDungeonRun(runIdentity);
        }

        private static bool FinalizeTowerRunStart(
            EnhancedClientSession session,
            DungeonRun newRun,
            ICollection<int> towerItemIds)
        {
            var runIdentity = newRun.CaptureIdentity();
            if (!session.Player.IsCurrentDungeonRun(runIdentity))
                return false;
            RecalibrateTowerQuestOverlayWithoutNotification(session, towerItemIds);
            if (!session.Player.IsCurrentDungeonRun(runIdentity))
                return false;
            PetCreatureRuntimeService.BeginDungeon(
                session,
                runIdentity,
                "begin_tower_run");
            return session.Player.IsCurrentDungeonRun(runIdentity);
        }

        private static DungeonInstance CreateInstance(
            int dungeonId,
            byte difficulty)
            => new DungeonInstance(
                (short)dungeonId,
                difficulty,
                GameWorld.DungeonRewardPolicyData.Resolve(dungeonId),
                GameWorld.DungeonDropDefinitionCatalog.Resolve(dungeonId),
                GameWorld.DungeonExperienceDefinitionCatalog.Resolve(dungeonId));

        // The only public ending entry. Every caller supplies its semantic
        // reason and, when it originates from a delayed/client continuation,
        // the run it expects to detach.
        internal static Task<bool> EndRunAsync(
            EnhancedClientSession session,
            DungeonRunEndReason reason,
            DungeonRunIdentity? expectedRun = null,
            DungeonInstanceRegistry instanceRegistry = null)
        {
            return reason == DungeonRunEndReason.SessionTeardown
                || reason == DungeonRunEndReason.CharacterSwitch
                ? Task.FromResult(EndRunOnTeardownCore(
                    session,
                    reason,
                    instanceRegistry))
                : EndRunToTownCoreAsync(
                    session,
                    reason,
                    expectedRun,
                    instanceRegistry);
        }

        internal static async Task EndRunToTownAsync(EnhancedClientSession session)
            => await EndRunAsync(
                session,
                DungeonRunEndReason.ReturnToTown,
                expectedRun: null);

        internal static Task<bool> TryEndRunToTownAsync(
            EnhancedClientSession session,
            DungeonRunIdentity expectedIdentity)
            => EndRunAsync(
                session,
                DungeonRunEndReason.ReturnToTown,
                expectedIdentity);

        internal static async Task<DungeonSelectionContext>
            RejectSelectingRunAsync(
                EnhancedClientSession session,
                DungeonRunIdentity expectedIdentity,
                DungeonInstanceRegistry instanceRegistry = null)
        {
            var player = session?.Player;
            if (player == null
                || !TryDetachSelectingRunAndRestoreSelection(
                    player,
                    expectedIdentity,
                    out var run,
                    out var selection))
            {
                return null;
            }

            await ExecuteDetachedRunCleanupAsync(
                session,
                run,
                DungeonRunEndReason.EntryRejected,
                instanceRegistry);
            return player.IsCurrentDungeonSelection(selection)
                ? selection
                : null;
        }

        internal static bool CanProjectTownState(
            EnhancedClientSession session,
            DungeonRunIdentity endedRunIdentity)
        {
            var player = session?.Player;
            return endedRunIdentity.IsValid
                && player != null
                && player.CurrentRun == null
                && player.CurrentDungeonRunGeneration
                    == endedRunIdentity.RunGeneration;
        }

        internal static void ApplyTownReturnAnchor(
            Game.Session.PlayerContext player,
            DungeonTownReturnAnchor returnAnchor,
            int listenerGamePort)
        {
            if (player == null)
                return;

            if (!returnAnchor.IsValid
                && GameChannelSpawnPolicy.TryResolveTransientSpawn(
                    listenerGamePort,
                    out var transientSpawn))
            {
                returnAnchor = new DungeonTownReturnAnchor(
                    transientSpawn.TownId,
                    transientSpawn.AreaId,
                    transientSpawn.X,
                    transientSpawn.Y,
                    transientSpawn.Direction,
                    transientSpawn.AreaState);
            }

            if (!returnAnchor.IsValid
                && GameWorld.Town.TryGetDungeonGateReturnInfo(
                    player.CurTownId,
                    player.CurAreaId,
                    out var configured))
            {
                returnAnchor = new DungeonTownReturnAnchor(
                    configured.Town,
                    configured.Area,
                    configured.X,
                    configured.Y,
                    player.CurDirection,
                    player.CurAreaState);
            }
            if (!returnAnchor.IsValid)
                return;

            player.CurTownId = returnAnchor.TownId;
            player.CurAreaId = returnAnchor.AreaId;
            player.CurPosX = returnAnchor.X;
            player.CurPosY = returnAnchor.Y;
            player.CurDirection = returnAnchor.Direction;
            // A21 town USER_AREA/AREA_USERS return samples end with state=0.
            // Preserve the anchor's coordinates and direction, but normalize
            // the town projection state instead of carrying the old A12 value 3.
            player.CurAreaState = 0x00;
        }

        private static async Task<bool> EndRunToTownCoreAsync(
            EnhancedClientSession session,
            DungeonRunEndReason reason,
            DungeonRunIdentity? expectedIdentity,
            DungeonInstanceRegistry instanceRegistry)
        {
            var player = session?.Player;
            if (player == null)
                return false;

            if (!TryDetachCurrentRun(
                    player,
                    expectedIdentity,
                    clearSelection: false,
                    out var run))
                return false;

            await ExecuteDetachedRunCleanupAsync(
                session,
                run,
                reason,
                instanceRegistry);
            return true;
        }

        private static async Task ExecuteDetachedRunCleanupAsync(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunEndReason reason,
            DungeonInstanceRegistry instanceRegistry)
        {
            var player = session.Player;
            var reasonText = reason.ToString();
            var runIdentity = run.CaptureIdentity();
            var tower = run.Tower;
            var towerItemIds = tower != null
                ? new List<int>(tower.SeenItemIds)
                : null;
            var operations = new List<DungeonRunEndCleanupOperation>
            {
                new DungeonRunEndCleanupOperation(
                    "registry-terminate",
                    () =>
                    {
                        instanceRegistry?.Terminate(
                            player.CharacterId,
                            runIdentity,
                            reasonText);
                        return Task.CompletedTask;
                    }),
                new DungeonRunEndCleanupOperation(
                    "cancel-timers",
                    () =>
                    {
                        CancelAllTimers(run);
                        return Task.CompletedTask;
                    }),
                new DungeonRunEndCleanupOperation(
                    "persist-experience",
                    () =>
                    {
                        if (!PersistSessionExp(session, run, reasonText))
                        {
                            throw new InvalidOperationException(
                                "Session experience persistence failed.");
                        }
                        return Task.CompletedTask;
                    }),
                new DungeonRunEndCleanupOperation(
                    "clear-tower-state",
                    () =>
                    {
                        run.Tower = null;
                        RecalibrateTowerQuestOverlayWithoutNotification(
                            session,
                            towerItemIds);
                        return Task.CompletedTask;
                    }),
            };
            if (tower != null)
            {
                operations.Add(new DungeonRunEndCleanupOperation(
                    "restore-persistent-inventory-projection",
                    () => RestorePersistentInventoryProjectionAsync(
                        session,
                        runIdentity)));
            }
            operations.Add(new DungeonRunEndCleanupOperation(
                "project-mechanism-cleanup",
                () => DungeonMechanismCoordinator.ClearRunEffectsAsync(
                    session,
                    run,
                    reasonText)));
            operations.Add(new DungeonRunEndCleanupOperation(
                "end-pet-runtime",
                () => PetCreatureRuntimeService.EndDungeonToTownAsync(
                    session,
                    runIdentity,
                    reasonText)));
            var cleanup = await DungeonRunEndCleanupExecutor.ExecuteAsync(
                run,
                reasonText,
                operations);
            LogIncompleteCleanup(player.CharacterId, run, reasonText, cleanup);
        }

        private static async Task RestorePersistentInventoryProjectionAsync(
            EnhancedClientSession session,
            DungeonRunIdentity endedRunIdentity)
        {
            var player = session?.Player;
            if (player == null
                || player.CharacterId <= 0
                || !CanProjectTownState(session, endedRunIdentity))
            {
                return;
            }

            if (!Game.Inventory.InventoryContext.TryGetOwnedLease(
                    session.SessionId,
                    player.CharacterId,
                    out var lease))
            {
                throw new InvalidOperationException(
                    "The ending tower run has no session-owned inventory lease.");
            }

            var projected = await InventoryRefreshSender.TrySendOwnedItemListRefresh(
                session,
                lease,
                Game.Inventory.InventoryListType.Main,
                () => CanProjectTownState(session, endedRunIdentity));
            if (!projected && CanProjectTownState(session, endedRunIdentity))
            {
                throw new InvalidOperationException(
                    "The persistent inventory projection lost its owned lease.");
            }
        }

        // 断线/换角色: 同样丢弃本局。
        // 换角色时必须丢弃当前局 -- PlayerContext 实例跨角色复用, 不丢会把上个角色的副本状态带给下个角色。
        internal static void EndRunOnTeardown(
            EnhancedClientSession session,
            string source,
            DungeonInstanceRegistry instanceRegistry = null)
        {
            var reason = string.Equals(
                source,
                "select_character",
                StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    source,
                    "return_select_character",
                    StringComparison.OrdinalIgnoreCase)
                ? DungeonRunEndReason.CharacterSwitch
                : DungeonRunEndReason.SessionTeardown;
            _ = EndRunAsync(
                session,
                reason,
                instanceRegistry: instanceRegistry).GetAwaiter().GetResult();
        }

        internal static bool DetachRunOnNetworkDisconnect(
            EnhancedClientSession session,
            DungeonInstanceRegistry instanceRegistry)
        {
            var player = session?.Player;
            if (player == null || instanceRegistry == null)
                return false;

            DungeonRun run;
            DungeonParticipantAttachmentSnapshot attachment;
            DungeonAttachmentOperationStatus status;
            lock (player.DungeonRunLifecycleSyncRoot)
            {
                run = player.CurrentRun;
                if (run == null)
                    return false;

                status = instanceRegistry.TryDetach(
                    session.Account?.AccountId ?? 0,
                    player.CharacterId,
                    player.UserId,
                    session.SessionId,
                    run.CaptureIdentity(),
                    out attachment);
                if (status != DungeonAttachmentOperationStatus.Success)
                    return false;

                player.CurrentRun = null;
                player.DungeonSceneUniqueId = 0;
                player.ClearDungeonSelection();
            }

            LinkedDungeonEntryAuthorizationStore.Clear(player);
            var suspendedTimers = run.Timers.SuspendForNetworkDetach();
            PersistSessionExp(
                session,
                run,
                DungeonRunEndReason.SessionTeardown.ToString());
            PetCreatureRuntimeService.EndCharacterSession(
                session,
                "disconnect_detached");
            FileLogger.Log(
                $"[DungeonRunLifecycle] network detach preserved " +
                $"cid={player.CharacterId} party={attachment.PartyId} " +
                $"instance={attachment.RunIdentity.PartyDungeonInstanceId} " +
                $"run={attachment.RunIdentity.RunId}/" +
                $"{attachment.RunIdentity.RunGeneration} " +
                $"attachmentGeneration={attachment.AttachmentGeneration} " +
                $"suspendedTimers={suspendedTimers}");
            return true;
        }

        internal static bool AttachResumedRun(
            EnhancedClientSession session,
            DungeonParticipantAttachmentSnapshot attachment)
        {
            var player = session?.Player;
            if (player == null
                || attachment == null
                || attachment.State != DungeonParticipantAttachmentState.Active
                || attachment.CharacterId != player.CharacterId
                || attachment.ParticipantUserId != player.UserId
                || attachment.Run == null)
            {
                return false;
            }

            if (!player.TryAttachResumedDungeonRun(attachment.Run))
                return false;

            player.UserState = 0x01;
            return true;
        }

        private static bool EndRunOnTeardownCore(
            EnhancedClientSession session,
            DungeonRunEndReason reason,
            DungeonInstanceRegistry instanceRegistry)
        {
            var player = session?.Player;
            if (player == null)
                return false;

            var detached = TryDetachCurrentRun(
                player,
                expectedIdentity: null,
                clearSelection: true,
                out var run);

            var towerItemIds = run?.Tower != null
                ? new List<int>(run.Tower.SeenItemIds)
                : null;
            var reasonText = reason.ToString();
            if (run == null)
            {
                TryRunSessionCleanup(
                    player.CharacterId,
                    reasonText,
                    "clear-linked-authorization",
                    () => LinkedDungeonEntryAuthorizationStore.Clear(player));
                TryRunSessionCleanup(
                    player.CharacterId,
                    reasonText,
                    "end-pet-session",
                    () => PetCreatureRuntimeService.EndCharacterSession(
                        session,
                        reasonText));
                return detached;
            }

            var runIdentity = run.CaptureIdentity();
            var cleanup = DungeonRunEndCleanupExecutor.ExecuteAsync(
                    run,
                    reasonText,
                    new[]
                    {
                        new DungeonRunEndCleanupOperation(
                            "registry-terminate",
                            () =>
                            {
                                instanceRegistry?.Terminate(
                                    player.CharacterId,
                                    runIdentity,
                                    reasonText);
                                return Task.CompletedTask;
                            }),
                        new DungeonRunEndCleanupOperation(
                            "clear-linked-authorization",
                            () =>
                            {
                                LinkedDungeonEntryAuthorizationStore.Clear(player);
                                return Task.CompletedTask;
                            }),
                        new DungeonRunEndCleanupOperation(
                            "cancel-timers",
                            () =>
                            {
                                CancelAllTimers(run);
                                return Task.CompletedTask;
                            }),
                        new DungeonRunEndCleanupOperation(
                            "persist-experience",
                            () =>
                            {
                                if (!PersistSessionExp(session, run, reasonText))
                                {
                                    throw new InvalidOperationException(
                                        "Session experience persistence failed.");
                                }
                                return Task.CompletedTask;
                            }),
                        new DungeonRunEndCleanupOperation(
                            "clear-tower-state",
                            () =>
                            {
                                run.Tower = null;
                                RecalibrateTowerQuestOverlayWithoutNotification(
                                    session,
                                    towerItemIds);
                                return Task.CompletedTask;
                            }),
                        new DungeonRunEndCleanupOperation(
                            "end-pet-session",
                            () =>
                            {
                                PetCreatureRuntimeService.EndCharacterSession(
                                    session,
                                    reasonText);
                                return Task.CompletedTask;
                            }),
                    })
                .GetAwaiter()
                .GetResult();
            LogIncompleteCleanup(player.CharacterId, run, reasonText, cleanup);
            return detached;
        }

        private static bool TryDetachCurrentRun(
            Game.Session.PlayerContext player,
            DungeonRunIdentity? expectedIdentity,
            bool clearSelection,
            out DungeonRun run)
        {
            run = null;
            if (player == null)
                return false;

            lock (player.DungeonRunLifecycleSyncRoot)
            {
                run = player.CurrentRun;
                if (run == null
                    || (expectedIdentity.HasValue
                        && !run.Matches(expectedIdentity.Value)))
                {
                    return false;
                }

                run.TryBeginEnding();
                player.CurrentRun = null;
                player.DungeonSceneUniqueId = 0;
                if (clearSelection)
                    player.ClearDungeonSelection();
                return true;
            }
        }

        private static bool TryDetachSelectingRunAndRestoreSelection(
            Game.Session.PlayerContext player,
            DungeonRunIdentity expectedIdentity,
            out DungeonRun run,
            out DungeonSelectionContext selection)
        {
            run = null;
            selection = null;
            if (player == null || !expectedIdentity.IsValid)
                return false;

            lock (player.DungeonRunLifecycleSyncRoot)
            {
                var current = player.CurrentRun;
                if (current == null
                    || !current.Matches(expectedIdentity)
                    || current.RunState != DungeonRunState.Selecting
                    || !current.TryBeginEnding())
                {
                    return false;
                }

                player.CurrentRun = null;
                player.DungeonSceneUniqueId = 0;
                player.ClearDungeonSelection();
                selection = player.BeginDungeonSelection(
                    current.TownReturnAnchor);
                if (selection == null)
                    return false;

                run = current;
                return true;
            }
        }

        private static List<int> CaptureTowerItemIds(EnhancedClientSession session)
        {
            var tower = session?.Player?.CurrentRun?.Tower;
            return tower == null ? null : new List<int>(tower.SeenItemIds);
        }

        private static Game.Quests.QuestRunSnapshot CaptureQuestSnapshot(
            EnhancedClientSession session)
        {
            try
            {
                return session?.GameSession?.QuestManager?.CaptureRunSnapshot()
                    ?? Game.Quests.QuestRunSnapshot.Empty;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DungeonRunLifecycle] quest snapshot failed closed: " +
                    $"cid={session?.Player?.CharacterId ?? 0}: {ex.Message}");
                return Game.Quests.QuestRunSnapshot.Empty;
            }
        }

        private static void RecalibrateTowerQuestOverlayWithoutNotification(
            EnhancedClientSession session,
            ICollection<int> towerItemIds)
        {
            if (towerItemIds == null
                || towerItemIds.Count == 0
                || session?.GameSession?.QuestManager == null)
            {
                return;
            }

            try
            {
                session.GameSession.QuestManager
                    .RecalibrateItemSeekingQuestProgressWithoutNotification(towerItemIds);
            }
            catch (System.Exception ex)
            {
                FileLogger.Log($"[DungeonRunLifecycle] ERROR: tower quest rollback failed: cid={session.Player?.CharacterId ?? 0}: {ex.Message}");
            }
        }

        // 离开一局时把会话内存的等级/经验落库(实现收口在经验系统,
        // 这里只保留"仍在一局中才需要兜底"的副本生命周期判断)。
        private static bool PersistSessionExp(
            EnhancedClientSession session,
            DungeonRun run,
            string source)
        {
            var player = session?.Player;
            if (player == null || run == null)
                return false;

            return Game.Progression.CharacterExperienceService.PersistSessionExp(
                player,
                source,
                session.GameSession?.ConnectionString
                    ?? Infrastructure.GameDatabase.CreateDefault()
                        .ConnectionString);
        }

        private static void LogIncompleteCleanup(
            int characterId,
            DungeonRun run,
            string source,
            DungeonRunEndCleanupSummary summary)
        {
            if (summary == null || summary.IsComplete)
                return;

            FileLogger.Log(
                $"[DungeonRunLifecycle] cleanup checkpoint incomplete " +
                $"cid={characterId} source={source} " +
                $"instance={run?.PartyDungeonInstanceId ?? 0} " +
                $"run={run?.RunId ?? 0}/{run?.RunGeneration ?? 0} " +
                $"failed=[{string.Join(",", summary.FailedOperations)}]");
        }

        private static void TryRunSessionCleanup(
            int characterId,
            string source,
            string operation,
            Action execute)
        {
            try
            {
                execute?.Invoke();
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DungeonRunLifecycle] detached session cleanup failed " +
                    $"cid={characterId} source={source} " +
                    $"operation={operation}: {ex.Message}");
            }
        }

        internal static void CancelAutoFlip(EnhancedClientSession session)
            => CancelAutoFlip(session?.Player?.CurrentRun);

        internal static void CancelAutoFlip(DungeonRun run)
        {
            if (run == null)
                return;

            run.Timers.Cancel(DungeonRunTimerKeys.SettlementCardAutoFlow);
        }

        internal static void CancelDeathRespawn(EnhancedClientSession session)
            => CancelDeathRespawn(session?.Player?.CurrentRun);

        internal static void CancelDeathRespawn(DungeonRun run)
        {
            if (run == null)
                return;

            run.IsWaitingDeathRespawn = false;
            run.DeathRespawnAvailableAt = System.DateTime.MinValue;
            run.Timers.Cancel(DungeonRunTimerKeys.CombatDeathRespawn);
        }

        internal static void CancelAllTimers(DungeonRun run)
        {
            if (run == null)
                return;

            run.IsWaitingDeathRespawn = false;
            run.DeathRespawnAvailableAt = DateTime.MinValue;
            run.Timers.CancelAll();
        }

    }
}
