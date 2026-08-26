using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Dungeon.BloodAltar;
using DfoServer.Game.Inventory;
using DfoServer.Game.Premium;
using DfoServer.Game.Progression;
using DfoServer.Game.SecretShop;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class DungeonSettlementHandler
    {
        private readonly DungeonSharedServices _svc;
        private readonly DungeonEntryHandler _entry;
        private Func<EnhancedClientSession, DungeonRun, Task>
            _bloodAltarClearedProjection;
        private Func<EnhancedClientSession, GamePacketHeader, byte[], Task<bool>>
            _bloodAltarEplpHandler;

        private const int SetPlayResultRankPointOffset = 10;
        // 成长之契约经验加成从 PVF premiumlist_new.etc 读取(PremiumEffectProvider)。
        private const float BlackDiamondBonusRate = 0.10f;
        private static readonly int[] BlackDiamondPremiumTypes = { 1, 17 };

        internal DungeonSettlementHandler(
            DungeonSharedServices svc,
            DungeonEntryHandler entry)
        {
            _svc = svc;
            _entry = entry;
        }

        internal void ConfigureBloodAltarPresentation(
            Func<EnhancedClientSession, DungeonRun, Task> clearedProjection,
            Func<EnhancedClientSession, GamePacketHeader, byte[], Task<bool>>
                eplpHandler)
        {
            _bloodAltarClearedProjection = clearedProjection
                ?? throw new ArgumentNullException(nameof(clearedProjection));
            _bloodAltarEplpHandler = eplpHandler
                ?? throw new ArgumentNullException(nameof(eplpHandler));
        }

        // Settlement result.
        // df_game_r CParty::CheckPlayResult -> CParty::SetPlayResult
        // Sends 3 NOTI packets (34, 37, 35) to show the settlement screen.
        // Card layout is deferred: a 2 s server timer sends it automatically
        // so the player sees the settlement summary first, then the cards appear.
        // After the card layout, a 4 s timer auto-flips the free card
        // (the client shows a 3 s countdown; 4 s on the server gives it room to finish).
        // If the player presses a key before the layout timer fires, the layout
        // is sent immediately and a fresh 3 s auto-flip timer starts.
        internal async Task HandleSetPlayResult(
            EnhancedClientSession session,
            GamePacketHeader header,
            byte[] body)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null)
                return;
            if (_svc.BloodAltars.IsBloodAltar(run))
            {
                FileLogger.Log(
                    $"[BloodAltar] SET_PLAY_RESULT ignored: " +
                    $"cid={session.Player.CharacterId} run={run.RunId}");
                return;
            }

            var presentationRankPoint = ExtractClientRankPoint(body);
            if (run.TryQueueSettlementPresentation(presentationRankPoint))
            {
                FileLogger.Log(
                    $"[DungeonHandler] SET_PLAY_RESULT queued until clear commit: " +
                    $"instance={run.PartyDungeonInstanceId} run={run.RunId} " +
                    $"rank={presentationRankPoint}");
                if (run.RunState != DungeonRunState.Cleared
                    || run.SettlementState != DungeonSettlementState.Preparing)
                {
                    return;
                }
            }

            await ProjectSettlementPresentationAsync(
                session,
                run,
                presentationRankPoint);
        }

        private async Task ProjectSettlementPresentationAsync(
            EnhancedClientSession session,
            DungeonRun run,
            int presentationRankPoint)
        {
            if (session?.Player == null
                || run == null
                || (run.ClearedFact != null
                    && !DungeonClearPresentationPolicy
                        .UsesStandardResultProjection(
                            run.ClearedFact.PresentationKind))
                || !run.RewardPolicy.AllowsSettlement
                || run.RunState != DungeonRunState.Cleared
                || run.SettlementState != DungeonSettlementState.Preparing)
            {
                return;
            }

            var settlement = run.SettlementRuntime;
            var authoritativeEffectId = GetAuthoritativeSettlementEffectId(run);
            if (settlement == null
                || run.Effects.GetState(authoritativeEffectId)
                    != DungeonEffectState.Committed)
            {
                FileLogger.Log(
                    $"[DungeonHandler] SET_PLAY_RESULT rejected before authoritative " +
                    $"settlement commit: instance={run.PartyDungeonInstanceId} " +
                    $"run={run.RunId}");
                return;
            }

            var presentationEffectId = GetSettlementPresentationEffectId(run);
            if (!run.Effects.TryReserve(
                    presentationEffectId,
                    out var presentationReservation))
            {
                return;
            }

            var identity = run.CaptureIdentity();
            try
            {
                // The presentation reservation serializes late-rank
                // adjustment with duplicate SET_PLAY_RESULT requests.
                if (!await ApplyCapturedRankAsync(session, run))
                {
                    run.Effects.TryFail(presentationReservation);
                    return;
                }

                CapturePresentationRank(settlement, presentationRankPoint);
                if (!await ExecuteSettlementProjectionEffectAsync(
                        session,
                        run,
                        identity,
                        "play-result-notification",
                        async () => await session.SendPacketAsync(
                            GamePacketEnvelopeBuilder.Build(
                                0x00,
                                0x0022,
                                DungeonNotificationBuilder.BuildPlayResult(
                                    session.Player.UserId,
                                    settlement.ClearTimeMilliseconds,
                                    // A21 PLAY_RESULT offset 0 is the wire
                                    // presentation flag, not the server rank
                                    // bonus index. The latter only affects the
                                    // authoritative EXP calculation.
                                    rankIndex: 0,
                                    timeBonusPoint: (byte)Math.Max(
                                        0,
                                        Math.Min(255, settlement.TimeBonusPoint)),
                                    clientRankPoint:
                                        settlement.ClientRankPoint))))
                    || !await ExecuteSettlementProjectionEffectAsync(
                        session,
                        run,
                        identity,
                        "experience-notification",
                        async () => await _svc.ProgressNotifications
                            .SendExpGrantNotificationAsync(
                                session,
                                settlement.ExperienceGrant,
                                "SET_PLAY_RESULT",
                                reloadMissingAccountProgress: true))
                    || !await ExecuteSettlementProjectionEffectAsync(
                        session,
                        run,
                        identity,
                        "clear-reward-notification",
                        async () => await session.SendPacketAsync(
                            GamePacketEnvelopeBuilder.Build(
                                0x00,
                                0x0023,
                                DungeonNotificationBuilder.BuildClearDungeonReward(
                                    settlement.ClearBaseExp,
                                    scoreBonusExp: ToInt32Saturated(
                                        settlement.ScoreBonusExp),
                                    partyClearBreakdownExp:
                                        settlement.PartyClearBreakdownExp,
                                    avatarExp: ToInt32Saturated(
                                        settlement.AvatarBonusExp),
                                    creatureExp: ToInt32Saturated(
                                        settlement.CreatureBonusExp),
                                    blackDiamondExp: ToInt32Saturated(
                                        settlement.BlackDiamondBonusExp),
                                    growthContractExp: ToInt32Saturated(
                                        settlement.GrowthContractBonusExp),
                                    monsterGrowthContractExp: ToInt32Saturated(
                                        settlement.MonsterGrowthContractBonusExp),
                                    adventureGroupExp: ToInt32Saturated(
                                        settlement.AdventureGroupBonusExp),
                                    monsterExp: settlement.MonsterTotalExp,
                                    bossExp: ToInt32Saturated(
                                        settlement.BossTotalExp),
                                    championExp: ToInt32Saturated(
                                        settlement.ChampionTotalExp),
                                    superChampionExp: ToInt32Saturated(
                                        settlement.SuperChampionTotalExp),
                                    freeCardGold: settlement.FreeGold.GoldAmount,
                                    freeCardItemId: settlement.FreeItem.ItemId,
                                    freeCardItemCount: settlement.FreeItem.StackCount,
                                    paidCardCost: settlement.PaidCardCost,
                                    objectExperienceEntries:
                                        settlement.ObjectExperienceEntries)))))
                {
                    run.Effects.TryFail(presentationReservation);
                    return;
                }

                if (settlement.IsTowerOfDespair)
                {
                    if (!await ExecuteSettlementProjectionEffectAsync(
                            session,
                            run,
                            identity,
                            "tower-of-despair-inventory-notification",
                            async () => await SendTowerOfDespairInventoryUpdates(
                                session,
                                settlement.TowerGrantedRewards)))
                    {
                        run.Effects.TryFail(presentationReservation);
                        return;
                    }

                    if (TryBuildTowerOfDespairClearRewardWithTime(
                            run.DungeonId,
                            (uint)Math.Max(0, settlement.ClearTimeMilliseconds),
                            settlement.TowerGrantedRewards
                                .Select(reward => reward.Reward)
                                .ToArray(),
                            out var towerClearReward)
                        && !await ExecuteSettlementProjectionEffectAsync(
                            session,
                            run,
                            identity,
                            "tower-of-despair-clear-notification",
                            async () => await session.SendPacketAsync(
                                GamePacketEnvelopeBuilder.Build(
                                    0x00,
                                    0x015C,
                                    towerClearReward))))
                    {
                        run.Effects.TryFail(presentationReservation);
                        return;
                    }
                }

                if (!await ExecuteSettlementProjectionEffectAsync(
                        session,
                        run,
                        identity,
                        "linked-dungeon-notification",
                        async () => await SendLinkedDungeonInfoAsync(session, run)))
                {
                    run.Effects.TryFail(presentationReservation);
                    return;
                }

                if ((settlement.ExperienceGrant?.LeveledUp == true
                        || settlement.ScoreAdjustmentExperienceGrant?.LeveledUp == true)
                    && !await ExecuteSettlementProjectionEffectAsync(
                        session,
                        run,
                        identity,
                        "level-up-followup-notification",
                        async () => await _svc.ProgressNotifications
                            .SendInDungeonLevelUpFollowups(session)))
                {
                    run.Effects.TryFail(presentationReservation);
                    return;
                }

                if (settlement.DungeonPermissionChanged
                    && !await ExecuteSettlementProjectionEffectAsync(
                        session,
                        run,
                        identity,
                        "dungeon-permission-notification",
                        () => SendDungeonPermissionUpdateAsync(
                            session,
                            settlement.DungeonPermissionEntries)))
                {
                    run.Effects.TryFail(presentationReservation);
                    return;
                }

                if (settlement.ShouldScheduleCardRewardFlow
                    && !await ExecuteSettlementProjectionEffectAsync(
                        session,
                        run,
                        identity,
                        "card-flow-schedule",
                        () =>
                        {
                            run.CardFlipCount = 0;
                            run.FreeCardSlots =
                                new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
                            run.PaidCardSlots =
                                new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
                            run.FreeCardRewardDelivered = false;
                            run.PaidCardRewardDelivered = false;
                            _svc.CardRewards.ScheduleAutoFlow(
                                session,
                                layoutDelayMs: 2000,
                                autoFlipDelayMs: 4000);
                            return Task.CompletedTask;
                        }))
                {
                    run.Effects.TryFail(presentationReservation);
                    return;
                }

                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] CLEAR_EXP: " +
                    $"dungeon={run.DungeonId} diff={run.Difficulty} " +
                    $"clientRank={settlement.ClientRankPoint} " +
                    $"rewardRank={settlement.RankPoint} " +
                    $"rewardRankBonusIndex={settlement.RankBonusIndex} " +
                    $"base={settlement.ClearBaseExp} " +
                    $"scoreBonus={settlement.ScoreBonusExp} " +
                    $"total={settlement.ClearTotalExp} " +
                    $"charExp={session.Player.Exp}");

                if (!session.Player.IsCurrentDungeonRun(identity)
                    || !run.TryMarkResultShown())
                {
                    run.Effects.TryFail(presentationReservation);
                    return;
                }

                if (!settlement.ShouldScheduleCardRewardFlow)
                    run.TryCompleteSettlement();

                if (!run.Effects.TryCommit(presentationReservation))
                    throw new InvalidOperationException(
                        "Settlement presentation reservation was lost.");
            }
            catch (Exception ex)
            {
                run.Effects.TryFail(presentationReservation);
                FileLogger.Log(
                    $"[DungeonHandler] SET_PLAY_RESULT projection failed: " +
                    $"instance={run.PartyDungeonInstanceId} run={run.RunId} " +
                    $"event={presentationEffectId.SourceEventId:N} " +
                    $"error={ex.Message}");
            }
        }

        internal async Task RecoverPendingSettlementPresentationAsync(
            EnhancedClientSession session)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null
                || (run.ClearedFact != null
                    && !DungeonClearPresentationPolicy
                        .UsesStandardResultProjection(
                            run.ClearedFact.PresentationKind))
                || !run.TryGetPendingSettlementPresentation(
                    out var presentationRankPoint))
            {
                return;
            }

            await ProjectSettlementPresentationAsync(
                session,
                run,
                presentationRankPoint);
            if (run.Effects.GetState(GetSettlementPresentationEffectId(run))
                == DungeonEffectState.Committed)
            {
                run.TryAcknowledgePendingSettlementPresentation(
                    presentationRankPoint);
            }
        }

        private async Task<bool> PrepareSettlementFromClearAsync(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonClearedFact clearFact)
        {
            if (session?.Player == null
                || run == null
                || clearFact == null
                || !run.RewardPolicy.AllowsSettlement)
            {
                return run != null && !run.RewardPolicy.AllowsSettlement;
            }

            var authoritativeEffectId = GetAuthoritativeSettlementEffectId(run);
            if (!run.Effects.TryReserve(
                    authoritativeEffectId,
                    out var authoritativeReservation))
            {
                return run.Effects.GetState(authoritativeEffectId)
                    == DungeonEffectState.Committed;
            }

            if (!run.TryBeginSettlementPreparationFromClear(clearFact)
                && !run.CanResumeSettlementPreparationFromClear(clearFact))
            {
                run.Effects.TryFail(authoritativeReservation);
                return false;
            }

            var identity = run.CaptureIdentity();
            try
            {
                var settlement = run.SettlementRuntime;
                if (settlement == null)
                {
                    settlement = BuildSettlementRuntime(session, run);
                    run.SettlementRuntime = settlement;
                }

                if (!await ExecuteSettlementEffectAsync(
                        session,
                        run,
                        identity,
                        "settlement-mechanism-clear",
                        async () => await DungeonMechanismCoordinator
                            .OnDungeonClearedAsync(session, run)))
                {
                    run.Effects.TryFail(authoritativeReservation);
                    return false;
                }

                if (DungeonClearPresentationPolicy
                        .UsesCommonExperienceAuthority(
                            clearFact.PresentationKind)
                    && !await ApplyCapturedRankAsync(session, run)
                )
                {
                    run.Effects.TryFail(authoritativeReservation);
                    return false;
                }

                if (DungeonClearPresentationPolicy
                        .UsesCommonExperienceAuthority(
                            clearFact.PresentationKind)
                    && !await ExecuteSettlementEffectAsync(
                        session,
                        run,
                        identity,
                        "settlement-experience-grant",
                        () =>
                        {
                            settlement.ExperienceGrant =
                                GrantSettlementExperienceInTransaction(
                                    session,
                                    run,
                                    settlement);
                            return Task.CompletedTask;
                        }))
                {
                    run.Effects.TryFail(authoritativeReservation);
                    return false;
                }

                if (DungeonClearPresentationPolicy
                        .UsesCommonExperienceAuthority(
                            clearFact.PresentationKind)
                    && !await ExecuteSettlementEffectAsync(
                        session,
                        run,
                        identity,
                        "suitable-dungeon-lucky-star",
                        async () =>
                        {
                            if (!await GrantSuitableDungeonLuckyStar(
                                    session,
                                    run,
                                    settlement.PreviousLevel))
                            {
                                throw new InvalidOperationException(
                                    "Suitable-dungeon reward persistence failed.");
                            }
                        }))
                {
                    run.Effects.TryFail(authoritativeReservation);
                    return false;
                }

                if (DungeonClearPresentationPolicy
                        .UsesCommonExperienceAuthority(
                            clearFact.PresentationKind)
                    && !await ExecuteSettlementEffectAsync(
                        session,
                        run,
                        identity,
                        DungeonPersistentEffectKinds.SuitableDungeonDailyChallenge,
                        async () =>
                        {
                            if (!await ApplySuitableDungeonDailyChallenge(
                                    session,
                                    run,
                                    settlement.PreviousLevel))
                            {
                                throw new InvalidOperationException(
                                    "Suitable-dungeon daily challenge persistence failed.");
                            }
                        }))
                {
                    run.Effects.TryFail(authoritativeReservation);
                    return false;
                }

                _svc.PersistentMechanisms.ConfigureLinkedChallenge(run);
                if (!await ExecuteSettlementEffectAsync(
                        session,
                        run,
                        identity,
                        "dungeon-permission-persistence",
                        () =>
                        {
                            if (!EnsureDungeonPermissionPlan(
                                    session,
                                    run.DungeonId,
                                    run.Difficulty,
                                    settlement))
                            {
                                throw new InvalidOperationException(
                                    "Dungeon permission persistence failed.");
                            }
                            return Task.CompletedTask;
                        })
                    || !await ExecuteSettlementEffectAsync(
                        session,
                        run,
                        identity,
                        "persistent-dungeon-mechanisms",
                        async () => await _svc.PersistentMechanisms
                            .ApplyDungeonClearAsync(session, run)))
                {
                    run.Effects.TryFail(authoritativeReservation);
                    return false;
                }

                if (settlement.IsTowerOfDespair
                    && !await ExecuteSettlementEffectAsync(
                        session,
                        run,
                        identity,
                        DungeonPersistentEffectKinds
                            .TowerOfDespairSettlementCommit,
                        () =>
                        {
                            CommitTowerOfDespairSettlement(
                                session,
                                run,
                                settlement);
                            return Task.CompletedTask;
                        }))
                {
                    run.Effects.TryFail(authoritativeReservation);
                    return false;
                }

                if (!run.Effects.TryCommit(authoritativeReservation))
                    throw new InvalidOperationException(
                        "Authoritative settlement reservation was lost.");

                FileLogger.Log(
                    $"[DungeonHandler] authoritative settlement committed: " +
                    $"instance={run.PartyDungeonInstanceId} run={run.RunId} " +
                    $"event={clearFact.SourceEventId:N} " +
                    $"rewardRank={settlement.RankPoint} " +
                    $"totalExp={settlement.ClearTotalExp}");
                return true;
            }
            catch (Exception ex)
            {
                run.Effects.TryFail(authoritativeReservation);
                FileLogger.Log(
                    $"[DungeonHandler] authoritative settlement failed: " +
                    $"instance={run.PartyDungeonInstanceId} run={run.RunId} " +
                    $"event={clearFact.SourceEventId:N} error={ex.Message}");
                return false;
            }
        }

        private static DungeonEffectId GetAuthoritativeSettlementEffectId(
            DungeonRun run)
            => new DungeonEffectId(
                run.GetSettlementSourceEventId(),
                "settlement-authoritative-commit",
                DungeonEffectScope.Player,
                run.RunId);

        private static DungeonEffectId GetSettlementPresentationEffectId(
            DungeonRun run)
            => new DungeonEffectId(
                run.GetSettlementSourceEventId(),
                "settlement-presentation",
                DungeonEffectScope.Player,
                run.RunId);

        private static DungeonEffectId GetSettlementScoreAdjustmentEffectId(
            DungeonRun run)
            => new DungeonEffectId(
                run.GetSettlementSourceEventId(),
                DungeonPersistentEffectKinds.SettlementScoreExperienceAdjustment,
                DungeonEffectScope.Player,
                run.RunId);

        private async Task<bool> ApplyCapturedRankAsync(
            EnhancedClientSession session,
            DungeonRun run)
        {
            var settlement = run?.SettlementRuntime;
            if (session?.Player == null || run == null || settlement == null)
                return true;
            if (!run.TryGetCapturedSettlementRank(out var rankPoint))
                return true;

            var rank = BuildClearRank(rankPoint);
            uint previousScore;
            lock (run.SyncRoot)
            {
                if (settlement.AuthoritativeRankCaptured)
                    return true;

                previousScore = settlement.ScoreBonusExp;
            }

            if (rank.RankBonusIndex < 0 && previousScore > 0)
            {
                FileLogger.Log(
                    $"[DungeonHandler] captured rank would reduce score bonus; " +
                    $"instance={run.PartyDungeonInstanceId} run={run.RunId} " +
                    $"previous={previousScore} rank={rankPoint}");
                return false;
            }

            var scoreBonus = ToUInt32Floor(
                settlement.ClearBaseExp
                * MonsterRewardTable.GetClearRankExpBonusRate(
                    rank.RankBonusIndex));
            var delta = scoreBonus > previousScore
                ? scoreBonus - previousScore
                : 0;
            var authoritativeEffectId = GetAuthoritativeSettlementEffectId(run);
            var authoritativeCommitted = run.Effects.GetState(
                authoritativeEffectId) == DungeonEffectState.Committed;

            if (!authoritativeCommitted || delta == 0)
            {
                ApplyRankFields(settlement, rank, scoreBonus);
                return true;
            }

            ExperienceGrantResult adjustmentGrant = null;
            if (!await ExecuteSettlementEffectAsync(
                    session,
                    run,
                    run.CaptureIdentity(),
                    DungeonPersistentEffectKinds
                        .SettlementScoreExperienceAdjustment,
                    () =>
                    {
                        var effectId = GetSettlementScoreAdjustmentEffectId(run);
                        if (!_svc.PersistentEffects
                                .TryApplySettlementScoreExperienceAdjustment(
                                    effectId,
                                    session.Player.CharacterId,
                                    session.Account?.AccountId ?? 0,
                                    session.Player.Level,
                                    session.Player.Exp,
                                    delta,
                                    out adjustmentGrant,
                                    out var error))
                        {
                            throw new InvalidOperationException(
                                "Settlement score experience adjustment failed: "
                                + error);
                        }

                        session.Player.Level = adjustmentGrant.NewLevel;
                        session.Player.Exp = adjustmentGrant.NewExp;
                        return Task.CompletedTask;
                    }))
            {
                return false;
            }

            ApplyRankFields(settlement, rank, scoreBonus);
            settlement.ScoreAdjustmentExperienceGrant = adjustmentGrant;
            settlement.ExperienceGrant ??= adjustmentGrant;
            FileLogger.Log(
                $"[DungeonHandler] settlement score experience adjustment: " +
                $"instance={run.PartyDungeonInstanceId} run={run.RunId} " +
                $"rank={rankPoint} scoreBonus={scoreBonus} delta={delta}");
            return true;
        }

        private static void ApplyRankFields(
            DungeonSettlementRuntime settlement,
            ClearRankParts rank,
            uint scoreBonus)
        {
            settlement.ClientRankPoint = rank.ClientRankPoint;
            settlement.PresentationRankPoint = rank.RankPoint;
            settlement.PresentationRankGrade = rank.RankGrade;
            settlement.PresentationRankBonusIndex = rank.RankBonusIndex;
            settlement.TimeBonusPoint = rank.TimeBonusPoint;
            settlement.RankPoint = rank.RankPoint;
            settlement.RankGrade = rank.RankGrade;
            settlement.RankBonusIndex = rank.RankBonusIndex;
            settlement.ScoreBonusExp = scoreBonus;
            settlement.ClearBonusExp = CharacterExperienceService.AddSaturating(
                CharacterExperienceService.AddSaturating(
                    CharacterExperienceService.AddSaturating(
                        CharacterExperienceService.AddSaturating(
                            CharacterExperienceService.AddSaturating(
                                scoreBonus,
                                settlement.AvatarBonusExp),
                            settlement.CreatureBonusExp),
                        settlement.GrowthContractBonusExp),
                    settlement.BlackDiamondBonusExp),
                settlement.AdventureGroupBonusExp);
            settlement.ClearTotalExp = CharacterExperienceService.AddSaturating(
                settlement.ClearBaseExp,
                settlement.ClearBonusExp);
            settlement.AuthoritativeRankCaptured = true;
        }

        private DungeonSettlementRuntime BuildSettlementRuntime(
            EnhancedClientSession session,
            DungeonRun run)
        {
            var isTowerOfDespair = DungeonData.TryGetTowerOfDespairFloor(
                run.DungeonId,
                out var towerOfDespairFloor);
            var presentationKind = run.ClearedFact?.PresentationKind
                ?? DungeonClearPresentationKind.Standard;
            var isBloodAltar = presentationKind
                == DungeonClearPresentationKind.BloodAltar;
            var standardPresentation = DungeonClearPresentationPolicy
                .UsesStandardResultProjection(presentationKind);
            var shouldScheduleCardRewardFlow = standardPresentation
                && ShouldScheduleCardRewardFlow(run.DungeonId);
            var isDimensionDungeon = DungeonData.IsDimensionDungeon(run.DungeonId);
            var dungeonLevel = DungeonData.GetDungeonBasicLv(run.DungeonId);
            if (dungeonLevel <= 0)
                throw new InvalidOperationException(
                    $"Dungeon {run.DungeonId} has no valid basic level.");

            var instance = run.Instance;
            var selection = instance?.Selection;
            var killStatistics = instance != null
                ? instance.KillStatistics
                : default(DungeonKillStatistics);
            var partyMemberCount = selection?.PartyMemberCount
                ?? run.EntryPartyMemberCount;
            var hasCapturedRank = run.TryGetCapturedSettlementRank(
                out var capturedRankPoint);
            var clearRank = CalculateAuthoritativeClearRank(
                capturedRankPoint,
                hasCapturedRank);
            var clearExp = DungeonClearPresentationPolicy
                    .UsesCommonExperienceAuthority(presentationKind)
                ? CalculateClearRewardExp(
                    session,
                    run,
                    clearRank.RankBonusIndex,
                    dungeonLevel,
                    presentationKind,
                    killStatistics,
                    partyMemberCount)
                : default;
            var partyClearBreakdownExp = DungeonExperienceCalculator
                .CalculatePartyClearBreakdown(
                    run.ExperienceDefinition,
                    clearExp.Base,
                    partyMemberCount,
                    partyEventBonusRate: 0.0);

            var lcg = run.RoomLcg ?? new DnfLcg(run.Seed);
            var characterJob = session.Player != null ? session.Player.Job : -1;
            var characterGrowType = session.Player != null
                ? session.Player.GrowType
                : -1;
            var rewardContext = new ClearRewardGenerationContext(
                dungeonLevel,
                run.Difficulty,
                partyMemberCount: partyMemberCount,
                rankBonusRate: MonsterRewardTable.GetClearRankExpBonusRate(
                    clearRank.RankBonusIndex),
                normalKillCount: killStatistics.NormalKillCount,
                championKillCount: killStatistics.ChampionKillCount,
                bossKillCount: killStatistics.BossKillCount,
                visitedRoomCount: Math.Max(
                    1,
                    instance?.VisitedRoomCount ?? run.RoomStates.Count),
                totalRoomCount: Math.Max(
                    1,
                    selection?.TotalRoomCount ?? run.TotalRoomCount));
            var freeGold = shouldScheduleCardRewardFlow
                ? ClearRewardGenerator.GenerateFreeGoldCard(rewardContext, lcg)
                : default;
            var freeItem = default(ClearRewardGenerator.CardReward);
            if (shouldScheduleCardRewardFlow)
            {
                if (isDimensionDungeon)
                {
                    DimensionDropSystem.TryCreateFreeCard(
                        characterJob,
                        characterGrowType,
                        lcg,
                        out freeItem);
                }
                else
                {
                    freeItem = ClearRewardGenerator.GenerateFreeItemCard(
                        rewardContext,
                        lcg);
                }
            }
            var towerRewardCandidates = isTowerOfDespair
                ? BuildTowerOfDespairRewardCandidates(
                    towerOfDespairFloor,
                    () => ClearRewardGenerator.GenerateItemCard(
                        dungeonLevel,
                        run.Difficulty,
                        lcg))
                : Array.Empty<ClearRewardGenerator.CardReward>();
            var paidGold = new ClearRewardGenerator.CardReward
            {
                IsGold = true,
                GoldAmount = 0,
            };
            var paidItem = default(ClearRewardGenerator.CardReward);
            var paidCardCost = 0;
            if (shouldScheduleCardRewardFlow
                && ShouldGeneratePaidCardRewards(run.DungeonId))
            {
                paidCardCost = ClearRewardGenerator.GetPaidCardCost(dungeonLevel);
                if (isDimensionDungeon)
                {
                    DimensionDropSystem.TryCreatePaidCard(
                        characterJob,
                        characterGrowType,
                        lcg,
                        out paidItem);
                }
                else
                {
                    paidItem = ClearRewardGenerator.GeneratePaidItemCard(
                        rewardContext,
                        lcg);
                }
            }

            run.PaidCardCost = paidCardCost;
            run.CardRewards = shouldScheduleCardRewardFlow
                ? new List<ClearRewardGenerator.CardReward>
                {
                    freeGold,
                    freeItem,
                    default,
                    default,
                    paidGold,
                    paidItem,
                    default,
                    default,
                }
                : null;

            if (shouldScheduleCardRewardFlow)
            {
                FileLogger.Log(
                    $"[ClearReward] dungeon={run.DungeonId} level={dungeonLevel} " +
                    $"difficulty={run.Difficulty} party={rewardContext.PartyMemberCount} " +
                    $"dimension={isDimensionDungeon} " +
                    $"rooms={rewardContext.VisitedRoomCount}/{rewardContext.TotalRoomCount} " +
                    $"kills={rewardContext.NormalKillCount}/" +
                    $"{rewardContext.ChampionKillCount}/" +
                    $"{rewardContext.BossKillCount} " +
                    $"freeGold={freeGold.GoldAmount} freeItem={freeItem.ItemId} " +
                    $"paidCost={paidCardCost} paidItem={paidItem.ItemId}");
            }

            var monsterExperience = run.CaptureExperienceSnapshot();
            var clearTimeMilliseconds = run.CalculateElapsedMilliseconds(
                DateTime.UtcNow);
            var bloodAltarSettlement = isBloodAltar
                ? BuildBloodAltarSettlementRuntime(
                    run,
                    dungeonLevel,
                    clearExp.Total,
                    clearTimeMilliseconds,
                    lcg)
                : null;
            return new DungeonSettlementRuntime
            {
                IsTowerOfDespair = isTowerOfDespair,
                BloodAltar = bloodAltarSettlement,
                TowerOfDespairFloor = towerOfDespairFloor,
                ShouldScheduleCardRewardFlow = shouldScheduleCardRewardFlow,
                ClientRankPoint = clearRank.ClientRankPoint,
                PresentationRankPoint = clearRank.RankPoint,
                PresentationRankGrade = clearRank.RankGrade,
                PresentationRankBonusIndex = clearRank.RankBonusIndex,
                AuthoritativeRankCaptured = hasCapturedRank,
                TimeBonusPoint = clearRank.TimeBonusPoint,
                RankPoint = clearRank.RankPoint,
                RankGrade = clearRank.RankGrade,
                RankBonusIndex = clearRank.RankBonusIndex,
                ClearBaseExp = clearExp.Base,
                ScoreBonusExp = clearExp.ScoreBonus,
                PartyClearBreakdownExp = partyClearBreakdownExp,
                AvatarBonusExp = clearExp.AvatarBonus,
                CreatureBonusExp = clearExp.CreatureBonus,
                GrowthContractBonusExp = clearExp.GrowthContractBonus,
                BlackDiamondBonusExp = clearExp.BlackDiamondBonus,
                AdventureGroupBonusExp = clearExp.AdventureGroupBonus,
                ClearBonusExp = clearExp.Bonus,
                ClearTotalExp = clearExp.Total,
                PreviousLevel = session.Player.Level,
                PreviousExp = session.Player.Exp,
                DungeonLevel = dungeonLevel,
                PaidCardCost = paidCardCost,
                FreeGold = freeGold,
                FreeItem = freeItem,
                TowerRewardCandidates = towerRewardCandidates,
                MonsterTotalExp = monsterExperience.MonsterTotalExperience,
                BossTotalExp = Math.Min(
                    monsterExperience.BossBaseExperience,
                    monsterExperience.MonsterBaseExperience),
                ChampionTotalExp = Math.Min(
                    monsterExperience.ChampionBaseExperience,
                    monsterExperience.MonsterBaseExperience),
                SuperChampionTotalExp = Math.Min(
                    monsterExperience.SuperChampionBaseExperience,
                    monsterExperience.MonsterBaseExperience),
                NamedMonsterTotalExp = Math.Min(
                    monsterExperience.NamedMonsterBaseExperience,
                    monsterExperience.MonsterBaseExperience),
                MonsterGrowthContractBonusExp =
                    monsterExperience.MonsterGrowthContractBonusExperience,
                ObjectExperienceEntries =
                    monsterExperience.ObjectExperienceEntries,
                ClearTimeMilliseconds = clearTimeMilliseconds,
            };
        }

        private BloodAltarParticipantSettlementRuntime
            BuildBloodAltarSettlementRuntime(
                DungeonRun run,
                int dungeonLevel,
                uint rewardExperience,
                int clearTimeMilliseconds,
                DnfLcg lcg)
        {
            var altar = _svc.BloodAltars.GetRuntime(run)
                ?? throw new InvalidOperationException(
                    "Blood altar clear has no instance runtime.");
            if (!altar.IsDungeonComplete)
            {
                throw new InvalidOperationException(
                    "Blood altar settlement was requested before all rounds completed.");
            }

            return _svc.BloodAltarRewardPlanner.Prepare(
                altar,
                dungeonLevel,
                run.Difficulty,
                rewardExperience,
                clearTimeMilliseconds,
                lcg);
        }

        private ExperienceGrantResult GrantSettlementExperienceInTransaction(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonSettlementRuntime settlement)
        {
            if (run == null
                || !session.Player.IsCurrentDungeonRun(run.CaptureIdentity()))
            {
                throw new InvalidOperationException(
                    "Settlement experience belongs to a stale dungeon run.");
            }
            if (settlement.ClearTotalExp == 0)
                return null;

            var effectId = new DungeonEffectId(
                run.GetSettlementSourceEventId(),
                DungeonPersistentEffectKinds.SettlementExperienceGrant,
                DungeonEffectScope.Player,
                run.RunId);
            if (!_svc.PersistentEffects.TryApplySettlementExperience(
                    effectId,
                    session.Player.CharacterId,
                    session.Account?.AccountId ?? 0,
                    settlement.PreviousLevel,
                    settlement.PreviousExp,
                    settlement.ClearTotalExp,
                    out var grant,
                    out var error))
            {
                throw new InvalidOperationException(
                    "Settlement experience persistent effect failed: " + error);
            }

            session.Player.Level = grant.NewLevel;
            session.Player.Exp = grant.NewExp;
            return grant;
        }

        internal static async Task<bool> ExecuteSettlementEffectAsync(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            string effectKind,
            Func<Task> execute)
        {
            if (session?.Player == null
                || run == null
                || execute == null
                || !session.Player.IsCurrentDungeonRun(identity))
            {
                return false;
            }

            var effectId = new DungeonEffectId(
                run.GetSettlementSourceEventId(),
                effectKind,
                DungeonEffectScope.Player,
                run.RunId);
            if (!run.Effects.TryReserve(effectId, out var reservation))
            {
                return run.Effects.GetState(effectId)
                    == DungeonEffectState.Committed;
            }

            try
            {
                await execute();
                if (!run.Effects.TryCommit(reservation))
                    return false;
                return session.Player.IsCurrentDungeonRun(identity);
            }
            catch
            {
                run.Effects.TryFail(reservation);
                throw;
            }
        }

        private static bool TryBuildTowerOfDespairClearRewardWithTime(
            int dungeonId,
            uint clearTimeMilliseconds,
            IReadOnlyList<ClearRewardGenerator.CardReward> rewards,
            out byte[] body)
        {
            body = null;
            if (!DungeonData.TryGetTowerOfDespairFloor(dungeonId, out var floor))
                return false;

            body = DungeonNotificationBuilder.BuildTowerOfDespairClearReward(
                clearTimeMilliseconds,
                floor,
                rewards);
            return true;
        }

        private static IReadOnlyList<ClearRewardGenerator.CardReward>
            BuildTowerOfDespairRewardCandidates(
                int floor,
                Func<ClearRewardGenerator.CardReward> randomRewardFactory)
        {
            if (randomRewardFactory == null)
                throw new ArgumentNullException(nameof(randomRewardFactory));

            var isPlayerMirrorFloor =
                floor >= 10
                && floor <= 90
                && floor % 10 == 0;
            var randomRewardCount = isPlayerMirrorFloor ? 9 : 5;
            var rewards = new List<ClearRewardGenerator.CardReward>(10);
            for (var i = 0; i < randomRewardCount; i++)
            {
                var reward = randomRewardFactory();
                if (!reward.IsGold
                    && reward.ItemId > 0
                    && reward.StackCount > 0)
                {
                    rewards.Add(reward);
                }
            }

            if (isPlayerMirrorFloor)
            {
                rewards.Add(new ClearRewardGenerator.CardReward
                {
                    ItemId = 1252,
                    StackCount = 1,
                });
            }
            else if (floor == 100)
            {
                rewards.Add(new ClearRewardGenerator.CardReward
                {
                    ItemId = 3314,
                    StackCount = 1,
                });
            }

            return rewards;
        }

        private async Task SendTowerOfDespairInventoryUpdates(
            EnhancedClientSession session,
            IReadOnlyList<TowerOfDespairGrantedReward> granted)
        {
            if (_svc.InventoryRefresh == null
                || granted == null
                || granted.Count == 0)
                return;

            try
            {
                foreach (var group in granted.GroupBy(
                             reward => reward.ListType))
                {
                    await _svc.InventoryRefresh.SendUpdateItemList(
                        session,
                        group.Key,
                        group.Select(reward => reward.Slot));
                }
            }
            catch (Exception ex)
            {
                // The rewards are already applied. A refresh failure must not
                // suppress TOD_CLEAR_REWARD or abort the remaining settlement flow.
                FileLogger.Log(
                    $"[TowerOfDespair] inventory refresh failed after reward grant: " +
                    $"cid={session.Player.CharacterId} error={ex.Message}");
            }
        }

        private void CommitTowerOfDespairSettlement(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonSettlementRuntime settlement)
        {
            var characterId = session?.Player?.CharacterId ?? 0;
            if (characterId <= 0
                || run == null
                || !InventoryContext.TryGetOwnedLease(
                    session.SessionId,
                    characterId,
                    out var lease))
            {
                throw new InvalidOperationException(
                    $"Tower settlement inventory lease is unavailable for " +
                    $"character {characterId}.");
            }

            var effectId = new DungeonEffectId(
                run.GetSettlementSourceEventId(),
                DungeonPersistentEffectKinds.TowerOfDespairSettlementCommit,
                DungeonEffectScope.Player,
                run.RunId);
            if (!_svc.PersistentEffects.TryApplyTowerOfDespairSettlement(
                    effectId,
                    lease,
                    session.SessionId,
                    run.DungeonId,
                    settlement.TowerRewardCandidates,
                    out var result,
                    out var error))
            {
                throw new InvalidOperationException(
                    "Tower of Despair settlement commit failed: " + error);
            }

            settlement.TowerGrantedRewards = result.GrantedRewards;
            FileLogger.Log(
                $"[{DungeonSharedServices.ProtocolLogName}] " +
                $"TOWER_OF_DESPAIR_SETTLEMENT: cid={characterId} " +
                $"dungeon={run.DungeonId} nextFloor={result.NextFloor} " +
                $"rewards={result.GrantedRewards.Count}");
        }

        private static bool ShouldGeneratePaidCardRewards(int dungeonId)
        {
            return ShouldScheduleCardRewardFlow(dungeonId);
        }

        private static bool ShouldScheduleCardRewardFlow(int dungeonId)
        {
            return !DungeonData.TryGetTowerOfDespairFloor(dungeonId, out _);
        }

        private static ClearRankParts CalculateAuthoritativeClearRank(
            int capturedRankPoint,
            bool hasCapturedRank)
            => BuildClearRank(hasCapturedRank ? capturedRankPoint : 0);

        internal static void CapturePresentationRank(
            DungeonSettlementRuntime settlement,
            byte[] body)
            => CapturePresentationRank(
                settlement,
                ExtractClientRankPoint(body));

        private static void CapturePresentationRank(
            DungeonSettlementRuntime settlement,
            int presentationRankPoint)
        {
            if (settlement == null)
                throw new ArgumentNullException(nameof(settlement));

            var rank = BuildClearRank(presentationRankPoint);
            settlement.ClientRankPoint = rank.ClientRankPoint;
            settlement.PresentationRankPoint = rank.RankPoint;
            settlement.PresentationRankGrade = rank.RankGrade;
            settlement.PresentationRankBonusIndex = rank.RankBonusIndex;
        }

        internal static async Task<bool> ExecuteSettlementProjectionEffectAsync(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            string effectKind,
            Func<Task> execute)
        {
            if (session?.Player == null
                || run == null
                || execute == null
                || !session.Player.IsCurrentDungeonRun(identity))
            {
                return false;
            }

            var effectId = new DungeonEffectId(
                run.GetSettlementSourceEventId(),
                effectKind,
                DungeonEffectScope.Player,
                run.RunId);
            if (!run.Effects.TryReserve(effectId, out var reservation))
            {
                return run.Effects.GetState(effectId)
                    == DungeonEffectState.Committed;
            }

            try
            {
                await execute();
                if (!session.Player.IsCurrentDungeonRun(identity))
                {
                    run.Effects.TryFail(reservation);
                    return false;
                }

                return run.Effects.TryCommit(reservation);
            }
            catch
            {
                run.Effects.TryFail(reservation);
                throw;
            }
        }

        private static ClearRankParts BuildClearRank(int rankPointValue)
        {
            var clientRankPoint = Math.Max(0, Math.Min(255, rankPointValue));
            var timeBonusPoint = 0;
            var rankPoint = Math.Min(255, clientRankPoint + timeBonusPoint);
            var rankGrade = MonsterRewardTable.GetClearRankGrade(rankPoint);
            var rankBonusIndex = MonsterRewardTable.GetClearRankBonusIndex(rankPoint);

            return new ClearRankParts(
                (byte)clientRankPoint,
                timeBonusPoint,
                rankPoint,
                (byte)rankGrade,
                rankBonusIndex);
        }

        private static int ExtractClientRankPoint(byte[] body)
        {
            if (body == null || body.Length <= SetPlayResultRankPointOffset)
                return 0;

            return body[SetPlayResultRankPointOffset];
        }

        private static int ReadInt32(byte[] body, int offset)
        {
            if (body == null || offset < 0 || offset + 3 >= body.Length)
                return 0;

            return BitConverter.ToInt32(body, offset);
        }

        private ClearExpParts CalculateClearRewardExp(
            EnhancedClientSession session,
            DungeonRun run,
            int rankBonusIndex,
            int dungeonLevel,
            DungeonClearPresentationKind presentationKind,
            DungeonKillStatistics killStatistics,
            int partyMemberCount)
        {
            var definition = run.ExperienceDefinition;
            if (definition == null || !definition.IsAvailable)
                return default;

            var experienceBonusSnapshot = run.CaptureExperienceBonusSnapshot();
            var experienceDifficulty = experienceBonusSnapshot
                .ResolveExperienceDifficulty(run.Difficulty);
            var storyWeightMultiplier = DungeonExperienceCalculator
                .ResolveStoryExperienceWeightMultiplier(
                    experienceBonusSnapshot);
            uint clearBaseExp;
            uint storyBonus = 0;
            if (definition.UsesStandardFormula
                && presentationKind == DungeonClearPresentationKind.Standard
                && run.Tower == null)
            {
                var calculated = DungeonExperienceCalculator.CalculateStandardClear(
                    definition,
                    new DungeonClearExperienceContext(
                        session.Player.Level,
                        experienceDifficulty,
                        killStatistics.TotalKillCount,
                        partyMemberCount,
                        partyEventBonusRate: 0.0,
                        memberPenaltyRate: 1.0,
                        experienceWeightMultiplier: storyWeightMultiplier));
                clearBaseExp = calculated.ParticipantBaseExperience;
                if (storyWeightMultiplier > 1.0)
                {
                    var nonStory = DungeonExperienceCalculator
                        .CalculateStandardClear(
                            definition,
                            new DungeonClearExperienceContext(
                                session.Player.Level,
                                experienceDifficulty,
                                killStatistics.TotalKillCount,
                                partyMemberCount));
                    storyBonus = clearBaseExp > nonStory.ParticipantBaseExperience
                        ? clearBaseExp - nonStory.ParticipantBaseExperience
                        : 0;
                }
                FileLogger.Log(
                    $"[DungeonExperience] clear model=Standard " +
                    $"dungeon={run.DungeonId} level={session.Player.Level} " +
                    $"kills={killStatistics.TotalKillCount} " +
                    $"party={Math.Max(1, partyMemberCount)} " +
                    $"shared={calculated.SharedBaseExperience} " +
                    $"participant={calculated.ParticipantBaseExperience}");
            }
            else
            {
                clearBaseExp = CalculateNonStandardCompatibilityClearBase(
                    definition,
                    experienceDifficulty,
                    dungeonLevel);
            }

            if (clearBaseExp == 0)
                return default;

            var storyAdjustedBaseExp = clearBaseExp;

            var connStr = _svc.ConnectionString;
            // Account 缺失时传 0(查不到契约, 无加成), 不能回退到账号 1 借用其契约效果。
            var accountId = session.Account?.AccountId ?? 0;
            var scoreBonusRate = MonsterRewardTable.GetClearRankExpBonusRate(rankBonusIndex);
            var scoreBonus = ToUInt32Floor(
                storyAdjustedBaseExp * scoreBonusRate);
            var premiumEffects = Game.Premium.PremiumEffectProvider.GetCombinedEffects(connStr, accountId);
            var growthContractBonus = premiumEffects.ComputeBonusExp(
                storyAdjustedBaseExp);
            var blackDiamondBonus = PremiumService.HasActivePremium(connStr, accountId, BlackDiamondPremiumTypes)
                ? ToUInt32Floor(
                    storyAdjustedBaseExp * BlackDiamondBonusRate)
                : 0;
            var adventureGroupBonus = CalculateAdventureGroupClearExpBonus(
                session,
                accountId,
                storyAdjustedBaseExp);
            var participantBonuses = DungeonExperienceCalculator
                .CalculateClearParticipantBonuses(
                    definition,
                    storyAdjustedBaseExp,
                    experienceBonusSnapshot);
            FileLogger.Log(
                $"[{DungeonSharedServices.ProtocolLogName}] "
                + $"CLEAR_EXP: dungeon={run.DungeonId} "
                + $"difficulty={run.Difficulty} "
                + $"experienceDifficulty={experienceDifficulty} "
                + $"storyRate={experienceBonusSnapshot.StoryExperienceBonusRatePercent}% "
                + $"storyWeight={storyWeightMultiplier:R} "
                + $"storyBonus={storyBonus} base={clearBaseExp} "
                + $"adjustedBase={storyAdjustedBaseExp}");

            return new ClearExpParts(
                storyAdjustedBaseExp,
                scoreBonus,
                participantBonuses.AvatarBonusExperience,
                participantBonuses.CreatureBonusExperience,
                growthContractBonus,
                blackDiamondBonus,
                adventureGroupBonus);
        }

        private static uint CalculateNonStandardCompatibilityClearBase(
            GameWorld.DungeonExperienceDefinition definition,
            int difficulty,
            int dungeonLevel)
        {
            var baseExp = ExpTableProvider.GetQuestRewardBase(dungeonLevel);
            if (baseExp <= 0)
                return 0;

            return DungeonExperienceCalculator.FloorToUInt32(
                baseExp
                * definition.ExperienceWeight
                * definition.GetDifficultyRate(difficulty));
        }

        private uint CalculateAdventureGroupClearExpBonus(EnhancedClientSession session, int accountId, uint clearBaseExp)
        {
            if (session == null || clearBaseExp == 0)
                return 0;

            try
            {
                var characters = _svc.CharacterRepository.ListByAccount(accountId);
                var summary = AdventureGroupDataProvider.Calculate(characters);
                if (summary.ExpBonusPercent == 0 || IsHighestLevelCharacter(session, characters))
                    return 0;

                return ToUInt32Floor(clearBaseExp * (summary.ExpBonusPercent / 100.0f));
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] CLEAR_EXP adventure group bonus skipped: {ex.Message}");
                return 0;
            }
        }

        private static bool IsHighestLevelCharacter(EnhancedClientSession session, IReadOnlyList<Game.Characters.CharacterRecord> characters)
        {
            if (session?.Player == null || characters == null || characters.Count == 0)
                return true;

            var highestLevel = 0;
            foreach (var character in characters)
            {
                if (character == null || character.Deleted)
                    continue;
                if (character.Level > highestLevel)
                    highestLevel = character.Level;
            }

            return session.Player.Level >= highestLevel;
        }

        private static uint ToUInt32Floor(float value)
        {
            if (value <= 0)
                return 0;
            return value >= uint.MaxValue ? uint.MaxValue : (uint)value;
        }

        private static int ToInt32Saturated(uint value)
        {
            return value > int.MaxValue ? int.MaxValue : (int)value;
        }

        private readonly struct ClearRankParts
        {
            internal ClearRankParts(byte clientRankPoint, int timeBonusPoint, int rankPoint, byte rankGrade, int rankBonusIndex)
            {
                ClientRankPoint = clientRankPoint;
                TimeBonusPoint = timeBonusPoint;
                RankPoint = rankPoint;
                RankGrade = rankGrade;
                RankBonusIndex = rankBonusIndex;
            }

            internal byte ClientRankPoint { get; }
            internal int TimeBonusPoint { get; }
            internal int RankPoint { get; }
            internal byte RankGrade { get; }
            internal int RankBonusIndex { get; }
        }

        private readonly struct ClearExpParts
        {
            internal ClearExpParts(
                uint baseExp,
                uint scoreBonus,
                uint avatarBonus,
                uint creatureBonus,
                uint growthContractBonus,
                uint blackDiamondBonus,
                uint adventureGroupBonus)
            {
                Base = baseExp;
                ScoreBonus = scoreBonus;
                AvatarBonus = avatarBonus;
                CreatureBonus = creatureBonus;
                GrowthContractBonus = growthContractBonus;
                BlackDiamondBonus = blackDiamondBonus;
                AdventureGroupBonus = adventureGroupBonus;
            }

            internal uint Base { get; }
            internal uint ScoreBonus { get; }
            internal uint AvatarBonus { get; }
            internal uint CreatureBonus { get; }
            internal uint GrowthContractBonus { get; }
            internal uint BlackDiamondBonus { get; }
            internal uint AdventureGroupBonus { get; }
            internal uint Bonus
            {
                get
                {
                    var value = CharacterExperienceService.AddSaturating(
                        ScoreBonus,
                        AvatarBonus);
                    value = CharacterExperienceService.AddSaturating(
                        value,
                        CreatureBonus);
                    value = CharacterExperienceService.AddSaturating(
                        value,
                        GrowthContractBonus);
                    value = CharacterExperienceService.AddSaturating(
                        value,
                        BlackDiamondBonus);
                    return CharacterExperienceService.AddSaturating(
                        value,
                        AdventureGroupBonus);
                }
            }
            internal uint Total => CharacterExperienceService.AddSaturating(Base, Bonus);
        }

        internal async Task HandleSelectCard(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var clearFact = session?.Player?.CurrentRun?.ClearedFact;
            if (clearFact != null
                && !DungeonClearPresentationPolicy
                    .UsesStandardResultProjection(clearFact.PresentationKind))
            {
                return;
            }
            await _svc.CardRewards.HandleSelectCard(session, body);
        }

        internal async Task HandleEplpCommand(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (await _svc.DeathTower.TryHandleEplpCommandAsync(
                    session,
                    header,
                    body))
            {
                return;
            }
            if (_bloodAltarEplpHandler != null
                && await _bloodAltarEplpHandler(session, header, body))
            {
                return;
            }

            var run = session?.Player?.CurrentRun;
            if (run == null)
                return;
            var runIdentity = run.CaptureIdentity();
            var linkedNextId = run?.LinkedDungeonNextId ?? 0;
            var difficulty = run?.Difficulty ?? 0;
            var shouldReturnToTown = await _svc.CardRewards.HandleEplpCommand(session, body);
            if (!session.Player.IsCurrentDungeonRun(runIdentity))
                return;
            if (IsLinkedChallengeCommand(body) && linkedNextId > 0)
            {
                FileLogger.Log(
                    $"[DungeonHandler] LINKED_DUNGEON continue selected: " +
                    $"current={run.DungeonId} next={linkedNextId} " +
                    $"diff={difficulty}");
                await _entry.EnterLinkedDungeonAsync(
                    session,
                    header,
                    linkedNextId,
                    difficulty);
                return;
            }
            if (shouldReturnToTown)
                await ReturnToVillage(session, runIdentity);
        }

        internal async Task HandleCardStartRequest(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var clearFact = session?.Player?.CurrentRun?.ClearedFact;
            if (clearFact != null
                && !DungeonClearPresentationPolicy
                    .UsesStandardResultProjection(clearFact.PresentationKind))
            {
                return;
            }
            await _svc.CardRewards.HandleCardStartRequest(session);
        }

        // df_game_r CParty::ClearDungeon (0x85A9330)
        // Preamble: if (!cleared_flag) return; Epilogue: cleared_flag = 1;
        // Normal dungeon sends NOTI 31 (ENABLE_CLEAR_DUNGEON), advances phase to Cleared
        // + NOTI 279 (0x0117) SECRET_SHOP_NPC: settlement mystery merchant NPC ID
        internal async Task SubmitClearIntentAsync(
            EnhancedClientSession session,
            DungeonClearIntent intent,
            bool deferParticipantFanout = false)
        {
            var run = session?.Player?.CurrentRun;
            if (run == null || intent == null)
                return;
            if (!run.Matches(intent.Source.RunIdentity)
                || (intent.Source.RoomInstanceId.HasValue
                    && run.CurrentRoomInstanceId != intent.Source.RoomInstanceId.Value))
                return;
            if (!run.RewardPolicy.AllowsClearCommit)
                return;
            if (run.Instance.State == DungeonInstanceState.Ending
                || run.Instance.State == DungeonInstanceState.Ended)
            {
                return;
            }

            DungeonEncounterApplicationService.Apply(
                run,
                new DungeonEncounterDirective(
                    intent.Source,
                    DungeonEncounterDirectiveKind.Succeed,
                    cause: intent.Reason));
            run.Instance.Diagnostics.Record(
                DungeonDiagnosticRecordKind.ClearIntent,
                intent.Source,
                "dungeon-clear-intent",
                "accepted",
                intent.Reason);

            var clearFact = run.Instance.GetOrCreateClearedFact(
                intent,
                out var factCreated);
            var roster = CaptureClearParticipantRoster(session, run);
            run.Instance.ParticipantEffects.TryFreeze(
                clearFact.Source,
                DungeonParticipantEffectAudience.Instance,
                roster,
                out _);

            if (deferParticipantFanout)
                return;

            if (await ProcessClearForParticipantAsync(
                    session,
                    run,
                    clearFact,
                    factCreated))
            {
                await RelayClearToFrozenRosterAsync(
                    session,
                    run,
                    clearFact);
            }
        }

        internal async Task RecoverParticipantClearEffectsAsync(
            EnhancedClientSession session)
        {
            var run = session?.Player?.CurrentRun;
            var clearFact = run?.Instance?.ClearedFact;
            if (run == null || clearFact == null || run.Tower != null)
                return;

            var recoverable = run.Instance.ParticipantEffects
                .GetRecoverableForParticipant(
                    run.CaptureParticipantIdentity(),
                    DungeonParticipantEffectAudience.Instance,
                    DungeonParticipantEffectKinds.DungeonClear);
            foreach (var work in recoverable)
            {
                if (work.Source.SourceEventId != clearFact.SourceEventId
                    || !ReferenceEquals(work.Participant.Run, run)
                    || !run.Matches(work.Participant.RunIdentity))
                {
                    continue;
                }

                if (await ProcessClearForParticipantAsync(
                        session,
                        run,
                        clearFact,
                        factCreated: false))
                {
                    await RelayClearToFrozenRosterAsync(
                        session,
                        run,
                        clearFact);
                }
            }
        }

        private async Task<bool> ProcessClearForParticipantAsync(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonClearedFact clearFact,
            bool factCreated)
        {
            if (session?.Player == null
                || run == null
                || clearFact == null
                || !ReferenceEquals(session.Player.CurrentRun, run))
            {
                return false;
            }

            var identity = run.CaptureIdentity();
            var participant = FindParticipant(
                run.Instance.ParticipantEffects.GetRoster(
                    clearFact.SourceEventId,
                    DungeonParticipantEffectAudience.Instance),
                session.Player.CharacterId,
                identity);
            if (participant == null)
            {
                FileLogger.Log(
                    $"[DungeonHandler] clear participant missing from frozen roster: " +
                    $"cid={session.Player.CharacterId} event={clearFact.SourceEventId:N}");
                return false;
            }

            var killParticipant = FindParticipant(
                run.Instance.ParticipantEffects.GetRoster(
                    clearFact.SourceEventId,
                    DungeonParticipantEffectAudience.Room),
                session.Player.CharacterId,
                identity);
            if (killParticipant != null
                && run.Instance.ParticipantEffects.GetState(
                    clearFact.SourceEventId,
                    DungeonParticipantEffectAudience.Room,
                    identity.ParticipantIdentity,
                    DungeonParticipantEffectKinds.MonsterKill)
                    != DungeonParticipantEffectState.Committed)
            {
                return false;
            }

            if (!run.Instance.ParticipantEffects.TryBegin(
                    clearFact.SourceEventId,
                    DungeonParticipantEffectAudience.Instance,
                    participant,
                    DungeonParticipantEffectKinds.DungeonClear,
                    out var participantReservation,
                    out var existingState))
            {
                return existingState == DungeonParticipantEffectState.Committed;
            }

            var participantSource = clearFact.Source.ForAffectedPlayer(
                identity,
                participant.RoomIdentity.IsValid
                    ? participant.RoomIdentity.RoomInstanceId
                    : run.CurrentRoomInstanceId > 0
                        ? run.CurrentRoomInstanceId
                        : null,
                session.Player.CharacterId);
            var clearProjectionId = new DungeonEffectId(
                clearFact.SourceEventId,
                "dungeon-clear-projection",
                DungeonEffectScope.Player,
                run.RunId);
            DungeonEffectReservation clearReservation = default;
            try
            {
                if (!run.TryBeginClearCommit(clearFact)
                    && !run.CanResumeClearCommit(clearFact))
                {
                    var alreadyCommitted = run.RunState == DungeonRunState.Cleared
                        && ReferenceEquals(run.ClearedFact, clearFact)
                        && run.Effects.GetState(clearProjectionId)
                            == DungeonEffectState.Committed;
                    if (alreadyCommitted)
                        run.Instance.ParticipantEffects.TryCommit(participantReservation);
                    else
                        run.Instance.ParticipantEffects.TryFail(participantReservation);
                    return alreadyCommitted;
                }
                if (!run.Effects.TryReserve(clearProjectionId, out clearReservation))
                {
                    var alreadyCommitted = run.Effects.GetState(clearProjectionId)
                        == DungeonEffectState.Committed
                        && run.RunState == DungeonRunState.Cleared;
                    if (alreadyCommitted)
                        run.Instance.ParticipantEffects.TryCommit(participantReservation);
                    else
                        run.Instance.ParticipantEffects.TryFail(participantReservation);
                    return alreadyCommitted;
                }

                if (clearFact.BossCode != 0)
                    run.BossCode = clearFact.BossCode;

                var standardPresentation = clearFact.PresentationKind
                    == DungeonClearPresentationKind.Standard;
                var offer = standardPresentation
                    ? run.SecretShopOffer ?? CreateSecretShopOffer(run)
                    : null;
                if (standardPresentation)
                {
                    run.SecretShopOffer = offer;
                    if (!await ExecuteClearEffectAsync(
                            session,
                            run,
                            identity,
                            clearFact,
                            "enable-clear-notification",
                            async () => await session.SendPacketAsync(
                                GamePacketEnvelopeBuilder.Build(
                                    0x00,
                                    0x001F,
                                    DungeonNotificationBuilder
                                        .BuildEnableClearDungeon())))
                        || !await ExecuteClearEffectAsync(
                            session,
                            run,
                            identity,
                            clearFact,
                            "secret-shop-notification",
                            async () =>
                            {
                                foreach (var packet in
                                    SecretShopClearPacketBuilder.Build(offer))
                                {
                                    await session.SendPacketAsync(packet);
                                }
                            }))
                    {
                        run.Effects.TryFail(clearReservation);
                        run.Instance.ParticipantEffects.TryFail(
                            participantReservation);
                        return false;
                    }
                }

                if (!await ExecuteClearEffectAsync(
                        session,
                        run,
                        identity,
                        clearFact,
                        "quest-clear-drop",
                        async () => await _svc.QuestDrops.CheckDungeonClearReward(
                            session,
                            run,
                            participantSource)))
                {
                    run.Effects.TryFail(clearReservation);
                    run.Instance.ParticipantEffects.TryFail(participantReservation);
                    return false;
                }

                var currentMapId = ResolveCurrentMapId(run);
                if (!await ExecuteClearEffectAsync(
                        session,
                        run,
                        identity,
                        clearFact,
                        $"quest-clear-map:{run.DungeonId}:{currentMapId}",
                        async () => await DungeonClearMapQuestSync.SyncAsync(
                            session,
                            run.DungeonId,
                            currentMapId,
                            "dungeon_clear",
                            participantSource)))
                {
                    run.Effects.TryFail(clearReservation);
                    run.Instance.ParticipantEffects.TryFail(participantReservation);
                    return false;
                }

                if (ShouldSyncQuestConnectedStartMapOnDungeonClear(run, currentMapId))
                {
                    FileLogger.Log($"[DungeonHandler] CLEAR_MAP sync deferred quest-connected start map: dungeon={run.DungeonId} maze={run.MazeIndex} map={run.MazeStartMapId}");
                    if (!await ExecuteClearEffectAsync(
                            session,
                            run,
                            identity,
                            clearFact,
                            $"quest-clear-map:0:{run.MazeStartMapId}",
                            async () => await DungeonClearMapQuestSync.SyncAsync(
                                session,
                                0,
                                run.MazeStartMapId,
                                "dungeon_clear_deferred_start_map",
                                participantSource)))
                    {
                        run.Effects.TryFail(clearReservation);
                        run.Instance.ParticipantEffects.TryFail(participantReservation);
                        return false;
                    }
                }

                if (!await PrepareSettlementFromClearAsync(
                        session,
                        run,
                        clearFact)
                    || !session.Player.IsCurrentDungeonRun(identity)
                    || !run.TryCompleteClearCommit(clearFact))
                {
                    run.Effects.TryFail(clearReservation);
                    run.Instance.ParticipantEffects.TryFail(participantReservation);
                    return false;
                }

                if (!run.Effects.TryCommit(clearReservation)
                    || !run.Instance.ParticipantEffects.TryCommit(participantReservation))
                {
                    throw new InvalidOperationException(
                        "Dungeon clear effect reservation was lost before commit.");
                }

                if (clearFact.PresentationKind
                        == DungeonClearPresentationKind.BloodAltar
                    && _bloodAltarClearedProjection != null)
                {
                    await _bloodAltarClearedProjection(session, run);
                }
                else if (DungeonClearPresentationPolicy.CompletesAtClearCommit(
                    clearFact.PresentationKind))
                {
                    if ((!run.TryMarkResultShown()
                            && run.SettlementState
                                != DungeonSettlementState.ResultShown)
                        || (!run.TryCompleteSettlement()
                            && run.SettlementState
                                != DungeonSettlementState.Completed))
                    {
                        throw new InvalidOperationException(
                            "Dedicated settlement could not reach Completed.");
                    }
                }
                else
                {
                    await RecoverPendingSettlementPresentationAsync(session);
                }

                run.Instance.Diagnostics.Record(
                    DungeonDiagnosticRecordKind.ClearCommit,
                    participantSource,
                    "dungeon-clear-commit",
                    "committed",
                    clearFact.Reason);
                var itemSummary = offer == null
                    ? "disabled"
                    : string.Join(",", offer.Items.Select(
                        x => $"{x.ItemId}:price={x.Price}:count={x.Count}"));
                FileLogger.Log(
                    $"[DungeonHandler] ClearDungeon: {clearFact.Reason} " +
                    $"event={clearFact.SourceEventId:N} factCreated={factCreated} " +
                    $"instance={run.PartyDungeonInstanceId} run={run.RunId} " +
                    $"presentation={clearFact.PresentationKind} " +
                    $"secretShopNpc={offer?.NpcId ?? 0} items=[{itemSummary}]");
                return true;
            }
            catch (Exception ex)
            {
                run.Effects.TryFail(clearReservation);
                run.Instance.ParticipantEffects.TryFail(participantReservation);
                run.Instance.Diagnostics.Record(
                    DungeonDiagnosticRecordKind.ClearCommit,
                    participantSource,
                    "dungeon-clear-commit",
                    "failed",
                    ex.Message);
                FileLogger.Log(
                    $"[DungeonHandler] ClearDungeon participant effect failed: " +
                    $"event={clearFact.SourceEventId:N} instance={run.PartyDungeonInstanceId} " +
                    $"run={run.RunId} error={ex.Message}");
                return false;
            }
        }

        internal async Task CompleteDeferredClearFanoutAsync(
            EnhancedClientSession sourceSession,
            DungeonRun sourceRun,
            Guid sourceEventId)
        {
            if (sourceRun == null)
            {
                return;
            }

            var clearFact = sourceRun.Instance.ClearedFact;
            if (clearFact == null || clearFact.SourceEventId != sourceEventId)
                return;

            if (sourceSession?.Player != null
                && ReferenceEquals(sourceSession.Player.CurrentRun, sourceRun))
            {
                await ProcessClearForParticipantAsync(
                    sourceSession,
                    sourceRun,
                    clearFact,
                    factCreated: false);
            }

            await RelayClearToFrozenRosterAsync(
                sourceSession,
                sourceRun,
                clearFact);
        }

        private IReadOnlyList<DungeonParticipantRosterEntry>
            CaptureClearParticipantRoster(
                EnhancedClientSession sourceSession,
                DungeonRun sourceRun)
        {
            var result = new List<DungeonParticipantRosterEntry>();
            var seen = new HashSet<DungeonParticipantRunIdentity>();

            void Add(
                int characterId,
                ushort participantUserId,
                DungeonRun candidateRun,
                long attachmentGeneration)
            {
                if (candidateRun == null
                    || characterId <= 0
                    || participantUserId == 0
                    || !candidateRun.SharesPhysicalInstanceWith(sourceRun)
                    || !candidateRun.TryCaptureCurrentRoomSnapshot(out var snapshot)
                    || !seen.Add(snapshot.RunIdentity.ParticipantIdentity))
                {
                    return;
                }

                result.Add(new DungeonParticipantRosterEntry(
                    characterId,
                    participantUserId,
                    candidateRun,
                    snapshot.RunIdentity,
                    snapshot.RoomIdentity,
                    attachmentGeneration));
            }

            foreach (var participant in _svc.InstanceRegistry
                         .CaptureInstanceParticipantRoster(
                             sourceRun.CaptureInstanceIdentity()))
            {
                if (seen.Add(participant.RunIdentity.ParticipantIdentity))
                    result.Add(participant);
            }

            var sourcePlayer = sourceSession?.Player;
            if (sourcePlayer == null)
                return result;

            Add(
                sourcePlayer.CharacterId,
                sourcePlayer.UserId,
                sourceRun,
                attachmentGeneration: 0);

            var partyManager = _svc.PartyManager;
            var sessions = _svc.Sessions;
            var party = partyManager?.GetPartyByUser(sourcePlayer.UserId);
            if (party == null || sessions == null)
                return result;

            foreach (var member in party.MembersBySlot())
            {
                if (!sessions.TryGet(member.CharacterId, out var memberSession))
                    continue;
                var player = memberSession?.Player;
                Add(
                    member.CharacterId,
                    player?.UserId ?? 0,
                    player?.CurrentRun,
                    attachmentGeneration: 0);
            }

            return result;
        }

        private async Task RelayClearToFrozenRosterAsync(
            EnhancedClientSession sourceSession,
            DungeonRun sourceRun,
            DungeonClearedFact clearFact)
        {
            var sessions = _svc.Sessions;
            if (sessions == null || clearFact == null)
                return;

            var sourceCharacterId = sourceSession?.Player?.CharacterId ?? 0;
            var roster = sourceRun.Instance.ParticipantEffects.GetRoster(
                clearFact.SourceEventId,
                DungeonParticipantEffectAudience.Instance);
            foreach (var participant in roster)
            {
                if (!sessions.TryGet(participant.CharacterId, out var memberSession)
                    || memberSession?.Player?.CurrentRun == null
                    || memberSession.TcpClient == null
                    || !memberSession.TcpClient.Connected)
                {
                    continue;
                }

                var memberRun = memberSession.Player.CurrentRun;
                if (!ReferenceEquals(memberRun, participant.Run)
                    || !memberRun.Matches(participant.RunIdentity))
                {
                    continue;
                }

                try
                {
                    await ProcessClearForParticipantAsync(
                        memberSession,
                        memberRun,
                        clearFact,
                        factCreated: false);
                }
                catch (Exception ex)
                {
                    FileLogger.Log(
                        $"[DungeonHandler] clear participant relay failed: " +
                        $"source={sourceCharacterId} member={participant.CharacterId} " +
                        $"event={clearFact.SourceEventId:N} error={ex.Message}");
                }
            }
        }

        private static DungeonParticipantRosterEntry FindParticipant(
            IReadOnlyList<DungeonParticipantRosterEntry> roster,
            int characterId,
            DungeonRunIdentity runIdentity)
        {
            if (roster == null)
                return null;

            foreach (var participant in roster)
            {
                if (participant.CharacterId == characterId
                    && participant.RunIdentity.Equals(runIdentity))
                {
                    return participant;
                }
            }
            return null;
        }

        private static async Task<bool> ExecuteClearEffectAsync(
            EnhancedClientSession session,
            DungeonRun run,
            DungeonRunIdentity identity,
            DungeonClearedFact clearFact,
            string effectKind,
            Func<Task> execute)
        {
            var effectId = new DungeonEffectId(
                clearFact.SourceEventId,
                effectKind,
                DungeonEffectScope.Player,
                run.RunId);
            if (!run.Effects.TryReserve(effectId, out var reservation))
                return run.Effects.GetState(effectId) == DungeonEffectState.Committed;

            try
            {
                if (!session.Player.IsCurrentDungeonRun(identity))
                {
                    run.Effects.TryFail(reservation);
                    return false;
                }

                await execute();
                if (!session.Player.IsCurrentDungeonRun(identity))
                {
                    run.Effects.TryFail(reservation);
                    return false;
                }

                return run.Effects.TryCommit(reservation);
            }
            catch
            {
                run.Effects.TryFail(reservation);
                throw;
            }
        }

        internal async Task TryClearQuestNpcDungeonAsync(
            EnhancedClientSession session,
            Game.Quests.QuestSetTriggerResult result,
            DungeonEventEnvelope sourceEvent)
        {
            var run = session?.Player?.CurrentRun;
            if (!IsQuestCompletionSourceCurrent(run, sourceEvent)
                || result == null)
            {
                return;
            }

            var questCompletionClear =
                DungeonMechanismCoordinator.OnQuestProgressCompleted(
                    session,
                    run,
                    sourceEvent.RunIdentity,
                    result);
            if (questCompletionClear.ShouldClearDungeon
                && session.Player.IsCurrentDungeonRun(sourceEvent.RunIdentity))
            {
                await SubmitClearIntentAsync(
                    session,
                    new DungeonClearIntent(
                        sourceEvent,
                        questCompletionClear.ClearReason,
                        questCompletionClear.BossCode));
                return;
            }

            PvfLib.DungeonFile dungeonFile;
            try
            {
                dungeonFile = DungeonData.GetDungeonFile(run.DungeonId);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DungeonHandler] quest NPC clear config failed: " +
                    $"dungeon={run.DungeonId} quest={result.QuestId} " +
                    $"error={ex.Message}");
                return;
            }

            var questNpcMatched = ShouldClearQuestNpcDungeon(
                run,
                dungeonFile.QuestNpcDungeon,
                GameWorld.QuestData.IsMeetNpcQuest(result.QuestId),
                result);
            var currentMapId = ResolveCurrentMapId(run);
            var connectedQuestId = ResolveSelectedMazeQuestConnection(
                dungeonFile,
                run.MazeIndex);
            var questConnectedClearMapMatched =
                ShouldClearQuestConnectedClearMapDungeon(
                    run,
                    connectedQuestId,
                    currentMapId,
                    GameWorld.QuestData.IsClearMapQuest(result.QuestId),
                    result);
            if (!questNpcMatched && !questConnectedClearMapMatched)
            {
                return;
            }

            FileLogger.Log(
                $"[DungeonHandler] quest completion clear matched: " +
                $"cid={session.Player.CharacterId} dungeon={run.DungeonId} " +
                $"maze={run.MazeIndex} map={currentMapId} " +
                $"quest={result.QuestId} source=" +
                $"{(questNpcMatched ? "quest-npc" : "quest-connected-clear-map")} " +
                $"trigger={result.PreviousTriggerValue}->{result.TriggerValue}");
            await SubmitClearIntentAsync(
                session,
                new DungeonClearIntent(
                    sourceEvent,
                    $"quest completion quest={result.QuestId}",
                    bossCode: 0));
        }

        internal static bool IsQuestCompletionSourceCurrent(
            DungeonRun run,
            DungeonEventEnvelope sourceEvent)
        {
            if (run == null
                || sourceEvent == null
                || run.Phase != DungeonRunPhase.InProgress
                || !run.Matches(sourceEvent.RunIdentity))
            {
                return false;
            }

            return !sourceEvent.RoomInstanceId.HasValue
                || run.CurrentRoomInstanceId == sourceEvent.RoomInstanceId.Value;
        }

        internal static bool ShouldClearQuestNpcDungeon(
            DungeonRun run,
            int questNpcDungeon,
            bool isMeetNpcQuest,
            Game.Quests.QuestSetTriggerResult result)
        {
            if (run == null
                || run.Phase != DungeonRunPhase.InProgress
                || questNpcDungeon != 1
                || !isMeetNpcQuest
                || result == null
                || !result.Success
                || result.PreviousTriggerValue == 0
                || result.TriggerValue != 0
                || run.BossMapPos == null
                || run.BossMapPos.Length < 2)
            {
                return false;
            }

            return run.RoomKey.X == run.BossMapPos[0]
                && run.RoomKey.Y == run.BossMapPos[1];
        }

        internal static bool ShouldClearQuestConnectedClearMapDungeon(
            DungeonRun run,
            int connectedQuestId,
            int currentMapId,
            bool isClearMapQuest,
            Game.Quests.QuestSetTriggerResult result)
        {
            if (run == null
                || run.Phase != DungeonRunPhase.InProgress
                || !run.MazeQuestConnected
                || connectedQuestId <= 0
                || currentMapId <= 0
                || !isClearMapQuest
                || result == null
                || !result.Success
                || result.QuestId != connectedQuestId
                || result.PreviousTriggerValue == 0
                || result.TriggerValue != 0
                || run.BossMapPos == null
                || run.BossMapPos.Length < 2
                || run.RoomKey.X != run.BossMapPos[0]
                || run.RoomKey.Y != run.BossMapPos[1])
            {
                return false;
            }

            return GameWorld.QuestData.MatchesClearMapTarget(
                result.QuestId,
                run.DungeonId,
                currentMapId);
        }

        private static int ResolveSelectedMazeQuestConnection(
            PvfLib.DungeonFile dungeonFile,
            int mazeIndex)
        {
            if (dungeonFile?.Mazes == null
                || mazeIndex < 0
                || mazeIndex >= dungeonFile.Mazes.Count)
            {
                return -1;
            }

            var connection = dungeonFile.Mazes[mazeIndex].QuestConnection;
            if (connection == null || connection.Length < 2)
                connection = dungeonFile.QuestConnection;
            if (connection == null
                || connection.Length < 2
                || connection[0] != 0)
            {
                return -1;
            }

            return connection[1];
        }

        private static SecretShopOffer CreateSecretShopOffer(DungeonRun run)
        {
            try
            {
                var dungeonBasisLevel = DungeonData.GetDungeonBasicLv(run.DungeonId);
                return SecretShopOfferFactory.Create(
                    SecretShopCatalogProvider.Current,
                    run.DungeonId,
                    dungeonBasisLevel,
                    partySize: 1,
                    ServerRandom.Next);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[SecretShop] offer creation failed closed: dungeon={run.DungeonId} error={ex.Message}");
                return new SecretShopOffer(1000, Array.Empty<SecretShopItemCandidate>());
            }
        }

        private static int ResolveCurrentMapId(DungeonRun run)
        {
            if (run == null)
                return 0;

            RoomState state;
            if (run.RoomStates != null
                && run.RoomStates.TryGetValue(run.RoomKey, out state)
                && state != null
                && state.Maze.Index > 0)
                return state.Maze.Index;

            return 0;
        }

        private static bool ShouldSyncQuestConnectedStartMapOnDungeonClear(
            DungeonRun run,
            int currentMapId)
        {
            if (run == null || !run.MazeQuestConnected)
                return false;
            if (run.MazeStartMapId <= 0 || run.MazeStartMapId == currentMapId)
                return false;
            return true;
        }

        internal static bool IsLinkedChallengeCommand(byte[] body)
            => body != null
                && body.Length >= 2
                && body[0] == 1
                && body[1] == 3;

        private static async Task SendLinkedDungeonInfoAsync(
            EnhancedClientSession session,
            DungeonRun run)
        {
            if (session?.Player == null
                || run == null
                || run.LinkedDungeonNextId <= 0)
            {
                return;
            }

            var difficulty = Math.Min(4, (int)run.Difficulty);
            var body = DungeonNotificationBuilder.BuildLinkedDungeonInfo(
                run.LinkedDungeonNextId,
                difficulty);
            await session.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketType.LINKED_DUNGEON_INFO,
                    body));
            if (!session.Player.IsCurrentDungeonRun(run.CaptureIdentity()))
                return;
            LinkedDungeonEntryAuthorizationStore.Grant(
                session.Player,
                run.DungeonId,
                run.LinkedDungeonNextId,
                (byte)difficulty);
            FileLogger.Log(
                $"[DungeonHandler] LINKED_DUNGEON_INFO sent: " +
                $"current={run.DungeonId} " +
                $"next={run.LinkedDungeonNextId} " +
                $"difficulty={difficulty} " +
                $"rate={run.LinkedDungeonNextRate} " +
                $"condition={run.LinkedDungeonNextCondition} " +
                $"body={BitConverter.ToString(body)}");
        }

        // Synchronous return-to-town: mirrors DungeonTutorialHandler.ReturnToVillage packet sequence.
        // Key points: UserState=0x00 (not 0x01), sync await (not fire-and-forget), includes NOTI 0x00CA.
        private async Task ReturnToVillage(
            EnhancedClientSession session,
            DungeonRunIdentity runIdentity)
        {
            await _svc.TownReturn.ReturnAsync(session, runIdentity);
        }

        private bool EnsureDungeonPermissionPlan(
            EnhancedClientSession session,
            int dungeonId,
            int difficulty,
            DungeonSettlementRuntime settlement)
        {
            if (settlement == null)
                return false;
            try
            {
                if (!DungeonPermissionScopePolicy.IsAccountDifficulty(dungeonId))
                {
                    settlement.DungeonPermissionPlanReady = true;
                    settlement.DungeonPermissionChanged = false;
                    FileLogger.Log(
                        $"[DungeonHandler] DungeonPermission account update skipped " +
                        $"dungeon={dungeonId} " +
                        $"scope={DungeonPermissionScopePolicy.Resolve(dungeonId)}");
                    return true;
                }

                var accountId = session?.Account?.AccountId ?? 0;
                if (accountId <= 0)
                    throw new InvalidOperationException(
                        "Dungeon difficulty permission requires an account identity.");

                if (settlement.DungeonPermissionPlanReady)
                {
                    if (!settlement.DungeonPermissionChanged)
                        return true;
                    if (settlement.DungeonPermissionAccountId != accountId)
                        return false;
                    var replaySnapshot = _svc.DungeonDifficultyPermissions
                        .ApplyBatch(
                            accountId,
                            settlement.DungeonPermissionEntries,
                            out _);
                    return DungeonPermissionProjector.IsApplied(
                        replaySnapshot,
                        settlement.DungeonPermissionEntries);
                }

                if (dungeonId <= 0)
                {
                    settlement.DungeonPermissionPlanReady = true;
                    return true;
                }
                int maxClearState = GameWorld.Dungeon.GetMaxDifficultyCount(dungeonId) - 1;
                if (maxClearState <= 0)
                {
                    settlement.DungeonPermissionPlanReady = true;
                    return true;
                }
                byte newClearState = (byte)(difficulty + 1);
                if (newClearState < 1) newClearState = 1;
                if (newClearState > maxClearState) newClearState = (byte)maxClearState;

                var plan = _svc.DungeonDifficultyPermissions
                    .BuildProgressionPlan(
                    accountId,
                    dungeonId,
                    newClearState);
                settlement.DungeonPermissionEntries = plan.Entries;
                settlement.DungeonPermissionChanged = plan.RequiresPersistence;
                settlement.DungeonPermissionAccountId = accountId;
                settlement.DungeonPermissionPlanReady = true;
                if (!settlement.DungeonPermissionChanged)
                    return true;

                var snapshot = _svc.DungeonDifficultyPermissions
                    .ApplyBatch(
                        accountId,
                        settlement.DungeonPermissionEntries,
                        out _);
                return DungeonPermissionProjector.IsApplied(
                    snapshot,
                    settlement.DungeonPermissionEntries);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DungeonHandler] DungeonPermission ERROR: {ex.Message}");
                return false;
            }
        }

        private Task SendDungeonPermissionUpdateAsync(
            EnhancedClientSession session,
            IReadOnlyList<DungeonPermissionEntrySnapshot> entries)
        {
            if (entries == null || entries.Count == 0)
                return Task.CompletedTask;

            FileLogger.Log(
                $"[DungeonHandler] DungeonPermission: " +
                $"entries={string.Join(",", entries.Select(
                    entry => $"{entry.DungeonId}:{entry.ClearState}"))}");
            return session.SendPacketAsync(
                GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.DUNGEON_PERMISSION,
                    DungeonPermissionBodyBuilder.BuildEntries(entries)));
        }

        private async Task<bool> GrantSuitableDungeonLuckyStar(
            EnhancedClientSession session,
            DungeonRun run,
            int clearLevel)
        {
            if (run == null
                || !session.Player.IsCurrentDungeonRun(run.CaptureIdentity())
                || !GameWorld.Dungeon.IsSuitableLevelDungeon(
                    run.DungeonId,
                    clearLevel))
            {
                return true;
            }

            var characterId = session.Player.CharacterId;
            var accountId = session.Account?.AccountId ?? 0;
            if (characterId <= 0 || accountId <= 0)
                return true;

            var effectId = new DungeonEffectId(
                run.GetSettlementSourceEventId(),
                DungeonPersistentEffectKinds.SuitableDungeonLuckyStar,
                DungeonEffectScope.Player,
                run.RunId);
            if (!_svc.PersistentEffects.TryApplySuitableDungeonLuckyStar(
                    effectId,
                    characterId,
                    accountId,
                    run.DungeonId,
                    clearLevel,
                    out var result,
                    out var error))
            {
                FileLogger.Log(
                    $"[DungeonHandler] SUITABLE_LUCKY_STAR ERROR: " +
                    $"char={characterId} dungeon={run.DungeonId} " +
                    $"level={clearLevel} {error}");
                return false;
            }

            if (!result.Granted)
            {
                FileLogger.Log(
                    $"[DungeonHandler] SUITABLE_LUCKY_STAR skipped: " +
                    $"cap reached char={characterId} dungeon={run.DungeonId} " +
                    $"level={clearLevel}");
                return true;
            }

            FileLogger.Log(
                $"[DungeonHandler] SUITABLE_LUCKY_STAR grant: " +
                $"char={characterId} dungeon={run.DungeonId} " +
                $"level={clearLevel} stars={result.NewTotal}");
            try
            {
                if (!session.Player.IsCurrentDungeonRun(run.CaptureIdentity()))
                    return false;
                await LuckyStarClientNotifier.NotifyRewardAsync(
                    session,
                    characterId,
                    1,
                    result.NewTotal,
                    _svc.RentalTimeProvider);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DungeonHandler] SUITABLE_LUCKY_STAR sync ERROR: " +
                    $"char={characterId} dungeon={run.DungeonId} " +
                    $"stars={result.NewTotal} {ex.Message}");
            }
            return true;
        }

        private async Task<bool> ApplySuitableDungeonDailyChallenge(
            EnhancedClientSession session,
            DungeonRun run,
            int clearLevel)
        {
            if (run == null
                || !session.Player.IsCurrentDungeonRun(run.CaptureIdentity()))
            {
                return false;
            }

            var characterId = session.Player.CharacterId;
            if (characterId <= 0)
                return true;

            try
            {
                var sourceEventId = run.GetSettlementSourceEventId();
                var result = _svc.DailyChallenges.ApplySuitableDungeonClear(
                    characterId,
                    run.DungeonId,
                    run.Difficulty,
                    clearLevel,
                    sourceEventId);
                if (!result.HasRelevantProgress)
                    return true;
                if (!session.Player.IsCurrentDungeonRun(run.CaptureIdentity()))
                    return false;

                // A committed snapshot is the recovery path when a previous
                // notification failed after persistence. Publish it before the
                // edge-triggered clear notification so reconnect/retry converges.
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.DAILY_CHALLENGE,
                    DailyChallengeBodyBuilder.Build(result.Snapshot)));

                if (result.ChangedEntries > 0)
                {
                    if (!session.Player.IsCurrentDungeonRun(run.CaptureIdentity()))
                        return false;

                    var completionToken = DailyChallengeClearDungeonBodyBuilder
                        .ResolveCompletionToken(sourceEventId);
                    await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                        0x00,
                        (ushort)NotiPacketTypeA21.DAILY_CHALLENGE_CLEAR_DUNGEON,
                        DailyChallengeClearDungeonBodyBuilder.Build(completionToken)));
                    FileLogger.Log(
                        $"[DungeonHandler] DAILY_CHALLENGE clear noti "
                        + $"cid={characterId} dungeon={run.DungeonId} "
                        + $"event={sourceEventId:N} token={completionToken}");
                }
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[DungeonHandler] DAILY_CHALLENGE suitable clear ERROR: "
                    + $"char={characterId} dungeon={run.DungeonId} "
                    + $"difficulty={run.Difficulty} level={clearLevel} {ex.Message}");
                return false;
            }
        }
    }
}
