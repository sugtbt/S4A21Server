using DfoServer.Game.Appearance;
using DfoServer.Game.Characters;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.TitleBook;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.Network.Builders
{
    /// <summary>
    /// Builds target-relative responses for the town inspect-player flows.
    /// </summary>
    public static class OtherUserInfoResponseBuilder
    {
        private const int InspectTitleBookCategoryCount = 4;

        public static IReadOnlyList<byte[]> Build(
            ISelectCharacterDataSource dataSource,
            ICharacterRepository characterRepository,
            EnhancedClientSession target,
            byte mode,
            byte routingByte7,
            out string fullDetailsError)
        {
            return Build(
                dataSource,
                characterRepository,
                target,
                mode,
                routingByte7,
                database: null,
                out fullDetailsError);
        }

        public static IReadOnlyList<byte[]> Build(
            ISelectCharacterDataSource dataSource,
            ICharacterRepository characterRepository,
            EnhancedClientSession target,
            byte mode,
            byte routingByte7,
            IGameDatabase database,
            out string fullDetailsError)
        {
            fullDetailsError = null;
            if (mode != 0x00 && mode != 0x01 && mode != 0x03)
            {
                fullDetailsError = "unsupported_mode";
                return Array.Empty<byte[]>();
            }

            try
            {
                if (mode == 0x00)
                {
                    if (!TryCaptureAuthorizedIdentity(
                            characterRepository,
                            target,
                            out var appearanceCharacterId,
                            out var appearanceUserId,
                            out var appearanceAccountId,
                            out fullDetailsError))
                    {
                        return Array.Empty<byte[]>();
                    }

                    var body = AppearanceService.BuildNoti2Body(
                        target.Player,
                        database);
                    if (!IdentityStillMatches(
                            target,
                            appearanceCharacterId,
                            appearanceUserId,
                            appearanceAccountId))
                    {
                        fullDetailsError = "target_generation_changed";
                        return Array.Empty<byte[]>();
                    }

                    return new[]
                    {
                        BuildUserInfoPacket(body, routingByte7),
                    };
                }

                if (!TryLoadAuthorizedSnapshot(
                        dataSource,
                        characterRepository,
                        target,
                        out var snapshot,
                        out var targetUserId,
                        out fullDetailsError))
                {
                    return Array.Empty<byte[]>();
                }

                var initialization = snapshot.InitializationSnapshot;
                if (initialization?.UserInfoAddition == null)
                {
                    fullDetailsError = "target_snapshot_incomplete";
                    return Array.Empty<byte[]>();
                }

                if (mode == 0x01)
                {
                    var writer = new GamePacketWriter();
                    UserInfoBodyBuilder.WriteA21Subtype1Prefix(
                        writer,
                        targetUserId,
                        initialization.UserInfoAddition.ManageLevel,
                        initialization.UserInfoAddition.AuraSkinFlag);
                    writer.WriteBytes(UserInfoSubtype1Builder.BuildFromSnapshot(
                        initialization.UserInfoAddition,
                        initialization.SkillInfo,
                        snapshot.CharacterRecord?.Appearance));
                    return new[]
                    {
                        BuildUserInfoPacket(writer.ToArray(), routingByte7),
                    };
                }

                // mode=3 只回 USERINFO subtype 3。
                // 称号簿走 OTHER_USER_TITLE_BOOK_LIST。
                var subtype3Body =
                    UserInfoSubtype3Builder.BuildNotificationBody(
                        targetUserId,
                        initialization.UserInfoAddition,
                        initialization.SkillInfo,
                        snapshot.CharacterRecord);
                return new[]
                {
                    BuildUserInfoPacket(subtype3Body, routingByte7),
                };
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[OtherUserInfo] target snapshot build failed: " +
                    $"{ex.GetType().Name}: {ex.Message}");
                fullDetailsError = "target_snapshot_failed";
                return Array.Empty<byte[]>();
            }
        }

        public static IReadOnlyList<byte[]> BuildTitleBookList(
            ISelectCharacterDataSource dataSource,
            ICharacterRepository characterRepository,
            EnhancedClientSession target,
            byte infoType,
            out string error)
        {
            error = null;
            try
            {
                if (!TryLoadAuthorizedSnapshot(
                        dataSource,
                        characterRepository,
                        target,
                        out var snapshot,
                        out var targetUserId,
                        out error))
                {
                    return Array.Empty<byte[]>();
                }

                return BuildTitleBookPacketsFromSnapshot(
                    snapshot,
                    targetUserId,
                    infoType);
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"[OtherUserInfo] target title-book build failed: " +
                    $"{ex.GetType().Name}: {ex.Message}");
                error = "target_snapshot_failed";
                return Array.Empty<byte[]>();
            }
        }

        private static bool TryLoadAuthorizedSnapshot(
            ISelectCharacterDataSource dataSource,
            ICharacterRepository characterRepository,
            EnhancedClientSession target,
            out SelectCharacterDataSnapshot snapshot,
            out ushort targetUserId,
            out string error)
        {
            snapshot = null;
            targetUserId = 0;
            if (dataSource == null)
            {
                error = "target_or_data_source_unavailable";
                return false;
            }

            if (!TryCaptureAuthorizedIdentity(
                    characterRepository,
                    target,
                    out var targetCharacterId,
                    out targetUserId,
                    out var targetAccountId,
                    out error))
            {
                return false;
            }

            snapshot = dataSource.Load(
                targetCharacterId,
                targetAccountId);
            if (!IdentityStillMatches(
                    target,
                    targetCharacterId,
                    targetUserId,
                    targetAccountId))
            {
                error = "target_generation_changed";
                snapshot = null;
                return false;
            }

            if (snapshot?.CharacterRecord == null
                || snapshot.InitializationSnapshot == null)
            {
                error = "target_snapshot_incomplete";
                snapshot = null;
                return false;
            }

            if (snapshot.CharacterRecord.CharacterId
                    != targetCharacterId
                || snapshot.CharacterRecord.AccountId
                    != targetAccountId)
            {
                error = "target_snapshot_identity_mismatch";
                snapshot = null;
                return false;
            }

            return true;
        }

        private static bool TryCaptureAuthorizedIdentity(
            ICharacterRepository characterRepository,
            EnhancedClientSession target,
            out int targetCharacterId,
            out ushort targetUserId,
            out int targetAccountId,
            out string error)
        {
            targetCharacterId = target?.Player?.CharacterId ?? 0;
            targetUserId = target?.Player?.UserId ?? 0;
            targetAccountId = target?.Account?.AccountId ?? 0;
            error = null;
            if (characterRepository == null
                || targetCharacterId <= 0
                || targetUserId == 0)
            {
                error = "target_or_data_source_unavailable";
                return false;
            }

            if (targetAccountId <= 0)
            {
                error = "target_account_unavailable";
                return false;
            }

            if (targetUserId != unchecked((ushort)targetCharacterId))
            {
                error = "target_identity_mismatch";
                return false;
            }

            // Validate before Load(): the SQLite implementation can perform
            // maintenance writes while materializing a character snapshot.
            var authoritative = characterRepository.GetById(
                targetCharacterId);
            if (authoritative == null
                || authoritative.CharacterId != targetCharacterId
                || authoritative.AccountId != targetAccountId)
            {
                error = "target_identity_mismatch";
                return false;
            }

            return true;
        }

        private static bool IdentityStillMatches(
            EnhancedClientSession target,
            int characterId,
            ushort userId,
            int accountId)
        {
            return target?.Player != null
                && target.Player.CharacterId == characterId
                && target.Player.UserId == userId
                && target.Account?.AccountId == accountId;
        }

        private static List<byte[]> BuildTitleBookPacketsFromSnapshot(
            SelectCharacterDataSnapshot snapshot,
            ushort targetUserId,
            byte infoType)
        {
            var categories =
                snapshot.InitializationSnapshot.TitleBookCategories;
            var packets = new List<byte[]>(
                InspectTitleBookCategoryCount);
            for (var categoryIndex = 0;
                 categoryIndex < InspectTitleBookCategoryCount;
                 categoryIndex++)
            {
                var source = categories?.FirstOrDefault(
                    candidate => candidate != null
                        && candidate.Category == categoryIndex);
                var projected = new TitleBookCategorySnapshot
                {
                    InfoType = infoType,
                    OwnerId16 = targetUserId,
                    Category = categoryIndex,
                };
                if (source != null)
                    projected.Entries.AddRange(source.Entries);

                packets.Add(GamePacketEnvelopeBuilder.Build(
                    0x00,
                    (ushort)NotiPacketTypeA21.TITLE_BOOK_LIST,
                    TitleBookListBodyBuilder.BuildCategoryBody(projected)));
            }

            return packets;
        }

        private static byte[] BuildUserInfoPacket(
            byte[] body,
            byte routingByte7)
        {
            var packet = GamePacketEnvelopeBuilder.Build(
                0x00,
                (ushort)NotiPacketTypeA21.USERINFO,
                body);
            packet[7] = routingByte7;
            return packet;
        }
    }
}
