using DfoServer.Game.CharacterData;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Friends;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using DfoServer.Game.Skills;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace DfoServer.Network.Handlers.Dungeon
{
    internal sealed class DungeonTutorialHandler
    {
        private readonly DungeonSharedServices _svc;
        private readonly DungeonSettlementHandler _settlement;
        private readonly DungeonEntryHandler _entry;

        // df_game_r=59; FBS new0610 tested TUTORIAL_LEVEL_UP only levels to Lv2
        private const byte TutorialTargetLevel = 2;

        internal DungeonTutorialHandler(
            DungeonSharedServices svc,
            DungeonSettlementHandler settlement,
            DungeonEntryHandler entry)
        {
            _svc = svc;
            _settlement = settlement;
            _entry = entry;
        }

        internal async Task HandleStoryPause(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (body.Length < 2) return;

            if (session?.Player?.CurrentRun == null
                && session.A21TutorialReturnNeedsVillageObjectList)
            {
                session.A21TutorialReturnNeedsVillageObjectList = false;
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    0x00CA,
                    new byte[] { 0x00 }));
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x01,
                    0x00BF,
                    new byte[6]));
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    "town STORY_PAUSE: sent A21 VILLAGE_OBJECT_LIST and CMD response");
                return;
            }

            byte pauseFlag = body[0];
            byte requestType = body[1];
            var storyRun = session.Player.CurrentRun;
            var storyRunIdentity = storyRun?.CaptureIdentity() ?? default;

            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] STORY_PAUSE CMD: pauseFlag={pauseFlag} requestType={requestType} cid={session.Player.CharacterId}");

            var w = new GamePacketWriter();
            w.WriteUInt16(session.Player.UserId);
            w.WriteByte(pauseFlag);
            w.WriteByte(requestType);
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x00AA, w.ToArray()));

            var clearRequest = DungeonMechanismCoordinator.OnStoryPause(
                session,
                storyRun,
                storyRunIdentity,
                pauseFlag);
            if (!clearRequest.ShouldClearDungeon
                || storyRun == null
                || !session.Player.IsCurrentDungeonRun(storyRunIdentity))
            {
                return;
            }

            var clearSource = DungeonEventEnvelope.Create(
                storyRun,
                session.Player.CharacterId,
                clearRequest.ClearReason);
            await _settlement.SubmitClearIntentAsync(
                session,
                new DungeonClearIntent(
                    clearSource,
                    clearRequest.ClearReason,
                    clearRequest.BossCode));
        }

        // CMD 0x008F (wire 143) CHANGE_TUTORIAL_FLAG
        // A21 body: leading mode byte + u32 flagIndex + u8 rewardFlag. The
        // client may append nine reserved bytes (15B capture layout), but the
        // live client also sends the compact 6B form.
        // The first tutorial capture is:
        // 00 1E 00 00 00 01 00 00 00 00 00 00 00 00 00.
        // df_game_r: setCurCharacTutorialFlag(flagIndex), if rewardFlag -> RewardTutorial(flagIndex)
        //            flagIndex==31 + in dungeon -> giveup_game (tutorial complete, return to town)
        //            flagIndex==77 -> set ALL flags 0-77
        internal async Task HandleChangeTutorialFlag(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            if (!Network.Parsers.Dungeon.ChangeTutorialFlagRequest.TryParse(
                    body,
                    out var request))
            {
                FileLogger.Log(
                    $"[{DungeonSharedServices.ProtocolLogName}] " +
                    $"CHANGE_TUTORIAL_FLAG rejected A21 body length={body?.Length ?? 0} (expected >=6B)");
                return;
            }
            var tutorialRun = session.Player.CurrentRun;
            var tutorialRunIdentity = tutorialRun?.CaptureIdentity() ?? default;
            uint flagIndex = request.FlagIndex;
            byte rewardFlag = request.RewardFlag;
            var activeCharacterId = session.Player.CharacterId;
            var tutorialCharacterId = activeCharacterId > 0
                ? activeCharacterId
                : session.PendingReturnSelectCharacterId;
            SelectCharacterInitializationSnapshot tutorialSnapshot = null;
            var tutorialSkipAlreadySaved = false;
            if (flagIndex == 31 && tutorialCharacterId > 0)
            {
                tutorialSnapshot = new SelectCharacterInitializationSnapshot();
                _svc.CharacterStateRepository.LoadFlags(tutorialCharacterId, tutorialSnapshot);
                tutorialSkipAlreadySaved = tutorialSnapshot.AckTutorialSkipable == 1;
            }

            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] CHANGE_TUTORIAL_FLAG: flagIndex={flagIndex} rewardFlag={rewardFlag} dungeon={(session.Player.CurrentRun?.DungeonId ?? 0)} cid={activeCharacterId} pendingCid={session.PendingReturnSelectCharacterId}");

            // RewardTutorial: PVF serverparameter.etc [escalade tutorial reward]
            var inserted = new List<(short slot, int itemId, int count)>();
            if (rewardFlag != 0 && !tutorialSkipAlreadySaved)
            {
                if (flagIndex == 31 && activeCharacterId <= 0)
                {
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] RewardTutorial: flag=31 skipped because no active character is available after returning to selection");
                }
                else
                {
                    var rewards = TutorialRewardProvider.GetRewards(flagIndex);
                    if (rewards != null)
                    {
                        foreach (var r in rewards)
                        {
                            short slot;
                            if (TryGrantTutorialReward(session, r.ItemId, r.Count, out slot))
                            {
                                inserted.Add((slot, r.ItemId, r.Count));
                                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] RewardTutorial: flag={flagIndex} gave item {r.ItemId} x{r.Count} -> slot {slot}");
                            }
                            else
                            {
                                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] RewardTutorial: flag={flagIndex} FAILED to insert item {r.ItemId}");
                            }
                        }
                    }
                }
            }

            // A21 first-tutorial order is SELECT_DUNGEON -> CHANGE_TUTORIAL_FLAG
            // -> CMD 15/NOTI 27/DUNGEON_INFO/START_MAP -> CHANGE flag ACK.
            // Complete the pending projection before writing the flag ACK so the
            // client sees the same order as the capture.
            if (flagIndex == 30
                && tutorialRun != null
                && session.Player.IsCurrentDungeonRun(tutorialRunIdentity))
            {
                await _entry.CompletePendingTutorialEntryAsync(session);
                if (!session.Player.IsCurrentDungeonRun(tutorialRunIdentity))
                    return;
            }

            // ACK: resultCode=1 + u8 count + count x { u16 slot, u32 itemId, u32 count }
            var ack = new GamePacketWriter();
            ack.WriteByte(0x01);
            ack.WriteByte((byte)inserted.Count);
            foreach (var item in inserted)
            {
                ack.WriteUInt16((ushort)item.slot);
                ack.WriteUInt32((uint)item.itemId);
                ack.WriteUInt32((uint)item.count);
            }
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x008F, ack.ToArray()));

            // flagIndex==31: tutorial complete -> return to town (only when in dungeon, df_game_r: state>1 + giveup_game)
            if (flagIndex == 31)
            {
                var cid = tutorialCharacterId;
                FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] CHANGE_TUTORIAL_FLAG: tutorial complete (flag=31), marking skip. cid={cid} pendingCid={session.PendingReturnSelectCharacterId}");

                if (cid <= 0)
                {
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] CHANGE_TUTORIAL_FLAG: skip persist because no character context is available. pendingCid={session.PendingReturnSelectCharacterId}");
                    return;
                }

                tutorialSnapshot ??= new SelectCharacterInitializationSnapshot();
                if (tutorialSnapshot.AckTutorialSkipable != 1)
                {
                    tutorialSnapshot.AckTutorialSkipable = 1;
                    _svc.CharacterStateRepository.SaveFlags(cid, tutorialSnapshot);
                }
                else
                {
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] CHANGE_TUTORIAL_FLAG: tutorial skip already persisted. cid={cid}");
                }

                session.PendingReturnSelectCharacterId = 0;

                if (tutorialRun != null
                    && session.Player.IsCurrentDungeonRun(tutorialRunIdentity))
                {
                    FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] CHANGE_TUTORIAL_FLAG: returning to town from dungeon={tutorialRun.DungeonId}");
                    await ReturnToVillage(session, tutorialRun);
                }
            }
        }

        // CMD 0x01E4 (wire 484) TUTORIAL_LEVEL_UP
        // 86JP body: empty (0B). df_game_r: check level==1 + map in {61001,61009,61016},
        // CalLevelUpItemState(1, targetLevel) bulk exp to target level, SendCmdOkPacket(484)
        internal async Task HandleTutorialLevelUp(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            var run = session.Player.CurrentRun;
            var runIdentity = run?.CaptureIdentity() ?? default;
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] TUTORIAL_LEVEL_UP: cid={session.Player.CharacterId} level={session.Player.Level} dungeon={(run?.DungeonId ?? 0)}");

            if (session.Player.Level != 1 || run == null)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x01E4, new byte[] { 0x13 }));
                return;
            }

            _svc.CharacterExperience.GrantToLevel(session.Player, TutorialTargetLevel, "tutorial");

            var hasSkillPoints = _svc.ProgressNotifications.TryGetSkillPointProtocolState(
                session, persist: true, logTag: "TUTORIAL_LEVEL_UP", out var skillPoints);
            var honorLevel = _svc.ProgressNotifications.ResolveHonorLevelForExp(session);

            if (hasSkillPoints)
            {
                await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.EXP,
                    ExpNotificationBuilder.Build(
                        session.Player.Level, session.Player.Exp, skillPoints, honorLevel)));
                if (!session.Player.IsCurrentDungeonRun(runIdentity))
                    return;
            }

            await _svc.ProgressNotifications.SendInDungeonLevelUpFollowups(session);
            if (!session.Player.IsCurrentDungeonRun(runIdentity))
                return;

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x01, 0x01E4, new byte[] { 0x01 }));
        }

        internal async Task HandleBack2Village(EnhancedClientSession session, GamePacketHeader header, byte[] body)
        {
            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] BACK_2_VILLAGE: returning to town");
            await ReturnToVillage(session, session?.Player?.CurrentRun);
        }

        private async Task ReturnToVillage(
            EnhancedClientSession session,
            DungeonRun run)
        {
            var runIdentity = run?.CaptureIdentity() ?? default(DungeonRunIdentity);
            if (run != null)
            {
                if (!await DungeonRunLifecycle.EndRunAsync(
                        session,
                        DungeonRunEndReason.TutorialExit,
                        runIdentity,
                        _svc.InstanceRegistry))
                {
                    return;
                }
            }
            else
            {
                await DungeonRunLifecycle.EndRunAsync(
                    session,
                    DungeonRunEndReason.TutorialExit,
                    instanceRegistry: _svc.InstanceRegistry);
            }
            if (run != null
                && !DungeonRunLifecycle.CanProjectTownState(session, runIdentity))
            {
                return;
            }
            session.Player.UserState = 0x00;
            // 教程退出回城 → 状态回空闲：同频道在线好友推 USERINFO(0x0002) 更新场景实体状态。
            if (_svc.Sessions != null)
                await UnitedFriendSystem.NotifyUserStateChanged(
                    session, _svc.Sessions);

            var snapshot = TownAreaNotificationBuilder.CreateCurrentSnapshot(session.Player);

            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0003,
                EnterSelectDungeonStateBuilder.BuildUserState(session.Player)));
            if (run != null && !DungeonRunLifecycle.CanProjectTownState(session, runIdentity))
                return;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0017,
                TownAreaNotificationBuilder.BuildUserArea(snapshot)));
            if (run != null && !DungeonRunLifecycle.CanProjectTownState(session, runIdentity))
                return;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x0018,
                TownAreaNotificationBuilder.BuildAreaUsers(snapshot)));
            if (run != null && !DungeonRunLifecycle.CanProjectTownState(session, runIdentity))
                return;
            await session.SendPacketAsync(GamePacketEnvelopeBuilder.Build(0x00, 0x00CA,
                new byte[] { 0x00 }));
            if (run != null && !DungeonRunLifecycle.CanProjectTownState(session, runIdentity))
                return;

            FileLogger.Log($"[{DungeonSharedServices.ProtocolLogName}] ReturnToVillage: A21 town response sequence sent");
        }

        private bool TryGrantTutorialReward(
            EnhancedClientSession session,
            int itemTemplateId,
            int stackCount,
            out short assignedSlot)
        {
            assignedSlot = -1;
            try
            {
                var characterId = session?.Player?.CharacterId ?? 0;
                if (characterId <= 0
                    || !InventoryContext.TryGetLease(characterId, out var lease)
                    || !lease.IsOwnedBy(session.SessionId))
                {
                    FileLogger.Log($"[DungeonTutorial] TryGrantTutorialReward missing inventory cid={characterId} item={itemTemplateId}");
                    return false;
                }

                var requests = new[]
                {
                    new DungeonItemGrantRequest
                    {
                        ItemTemplateId = itemTemplateId,
                        Count = stackCount,
                        Source = DungeonItemAcquisitionSource.TutorialReward,
                    },
                };
                if (!_svc.ItemAcquisition.TryGrantItems(
                        lease,
                        requests,
                        out var grants)
                    || grants.Entries.Count != 1
                    || grants.Entries[0].Grant == null
                    || !grants.Entries[0].Grant.Success)
                    return false;

                assignedSlot = grants.Entries[0].Grant.SlotIndex;
                return true;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[DungeonTutorial] TryGrantTutorialReward ERROR: {ex.Message}");
                return false;
            }
        }
    }
}
