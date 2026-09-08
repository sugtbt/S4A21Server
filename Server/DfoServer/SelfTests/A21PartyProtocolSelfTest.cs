using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.Party;
using DfoServer.Game.Session;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Party;
using DfoServer.Network.Handlers;
using DfoServer.Network.Handlers.Dungeon;
using DfoServer.Network.Parsers.Party;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DfoServer.SelfTests
{
    public static class A21PartyProtocolSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== A21_PARTY_PROTOCOL selftest ===");
            var failures = 0;
            var party = new Party(3)
            {
                LeaderUserId = 10038,
                TitleIndex = 0,
                TitleBytes = new byte[]
                {
                    0xE5, 0x8F, 0xA4, 0xE5, 0x8F, 0xA4,
                    0xE5, 0x88, 0xBA, 0xE5, 0xAE, 0xA2,
                },
                UserMax = 4,
                PartyInfoBlock = new byte[]
                {
                    0x00, 0x02, 0x04, 0x00, 0x00, 0x00,
                    0x00, 0x05, 0x00, 0x00, 0xFF, 0xFF,
                },
            };
            party.TryAddMember(new PartyMember
            {
                UserId = 10038,
                CharacterId = 10038,
                SessionId = Guid.NewGuid(),
                Name = "leader",
            });

            var body = PartyInfoNotiBuilder.Build(party, 0);
            Check("zero-info0 type-0 body is 65 bytes", body.Length == 65, ref failures);
            Check(
                "zero info0 keeps the conditional empty dstr before info1",
                body[5] == 0x00
                && BitConverter.ToUInt32(body, 6) == 0
                && body[10] == 0x02
                && body[11] == 0x04
                && body[16] == 0x05
                && body[19] == 0xFF
                && body[20] == 0xFF,
                ref failures);
            Check(
                "eight five-byte roster slots follow the zero-info0 settings",
                BitConverter.ToUInt16(body, 21) == 10038
                && BitConverter.ToUInt16(body, 26) == 0xFFFF
                && BitConverter.ToUInt16(body, 56) == 0xFFFF,
                ref failures);
            Check(
                "roster tail and hasExtra are zero at A21 type-0 offsets",
                body[61] == 0
                && body[62] == 0
                && body[63] == 0
                && body[64] == 0,
                ref failures);
            Check(
                "other PARTY_INFO variants keep their parser-defined widths",
                PartyInfoNotiBuilder.Build(party, 1).Length == 22
                && PartyInfoNotiBuilder.Build(party, 2).Length == 49
                && PartyInfoNotiBuilder.Build(party, 3).Length == 5
                    && PartyInfoNotiBuilder.Build(party, 5).Length == 6,
                ref failures);
            var acceptedPeerResponse = new byte[]
            {
                0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            };
            var rejectedPeerResponse = new byte[]
            {
                0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x85, 0x00,
            };
            Check(
                "type-0 RES_PEER accepts only the exact 7-byte shape and rejects the captured 9-byte refusal",
                PartyHandler.IsAcceptedTypeZeroPeerResponse(
                    acceptedPeerResponse)
                && !PartyHandler.IsAcceptedTypeZeroPeerResponse(
                    rejectedPeerResponse),
                ref failures);
            var secondListedParty = new Party(4)
            {
                LeaderUserId = 10040,
                PartyInfoBlock = (byte[])party.PartyInfoBlock.Clone(),
            };
            secondListedParty.TryAddMember(new PartyMember
            {
                UserId = 10040,
                CharacterId = 10040,
                SessionId = Guid.NewGuid(),
                Name = "listed-leader",
            });
            var firstListedBody = PartyInfoNotiBuilder.Build(party, 0);
            var secondListedBody = PartyInfoNotiBuilder.Build(
                secondListedParty,
                0);
            var publicPartyList = PartyInfoNotiBuilder.BuildList(
                new[] { party, secondListedParty });
            var emptyPartyList = PartyInfoNotiBuilder.BuildList(
                Array.Empty<Party>());
            var removedPartyList = PartyInfoNotiBuilder.BuildList(
                Array.Empty<Party>(),
                new[] { party.PartyId });
            var replacedPartyList = PartyInfoNotiBuilder.BuildList(
                new[] { secondListedParty },
                new[] { party.PartyId });
            Check(
                "public PARTY_INFO list reuses exact type-0 blocks under one block count",
                BitConverter.ToUInt16(publicPartyList, 0) == 2
                && BitConverter.ToUInt16(publicPartyList, 2) == party.PartyId
                && BitConverter.ToUInt16(
                    publicPartyList,
                    firstListedBody.Length) == secondListedParty.PartyId
                && publicPartyList.Length ==
                    firstListedBody.Length + secondListedBody.Length - 2
                && emptyPartyList.Length == 2
                && BitConverter.ToUInt16(emptyPartyList, 0) == 0
                && removedPartyList.Length == 5
                && BitConverter.ToUInt16(removedPartyList, 0) == 1
                && BitConverter.ToUInt16(removedPartyList, 2) == party.PartyId
                && removedPartyList[4] == 3
                && BitConverter.ToUInt16(replacedPartyList, 0) == 2
                && BitConverter.ToUInt16(replacedPartyList, 2) == party.PartyId
                && replacedPartyList[4] == 3
                && BitConverter.ToUInt16(replacedPartyList, 5)
                    == secondListedParty.PartyId
                && replacedPartyList[7] == 0,
                ref failures);
            var threeMemberP2pParty = new Party(5)
            {
                LeaderUserId = 10041,
            };
            threeMemberP2pParty.TryAddMember(new PartyMember
            {
                UserId = 10041,
                CharacterId = 10041,
                SessionId = Guid.NewGuid(),
                Name = "p2p-leader",
                IpBytes = new byte[] { 127, 0, 0, 1 },
                P2pPort = 0x1234,
            });
            threeMemberP2pParty.TryAddMember(new PartyMember
            {
                UserId = 10042,
                CharacterId = 10042,
                SessionId = Guid.NewGuid(),
                Name = "p2p-second",
                IpBytes = new byte[] { 127, 0, 0, 1 },
                P2pPort = 0x5678,
            });
            threeMemberP2pParty.TryAddMember(new PartyMember
            {
                UserId = 10043,
                CharacterId = 10043,
                SessionId = Guid.NewGuid(),
                Name = "p2p-third",
                IpBytes = new byte[] { 127, 0, 0, 1 },
                P2pPort = 0x9ABC,
            });
            var directP2pProjection =
                PartyHandler.BuildDirectP2pProjectionPackets(
                    threeMemberP2pParty);
            var directEndpointPacket = directP2pProjection[0];
            var directRealtimePacket = directP2pProjection[1];
            var threeMemberContextPlan =
                PartyHandler.BuildPartyFormationContextPlan(
                    threeMemberP2pParty.MembersBySlot());
            Check(
                "three-member formation fans out six peer contexts and keeps reusable endpoint bytes for both phases",
                directP2pProjection.Length == 2
                && threeMemberContextPlan.Count == 6
                && threeMemberContextPlan.All(
                    pair => pair.RecipientUid != pair.SourceUid)
                && threeMemberContextPlan.Any(
                    pair => pair.RecipientUid == 10041
                        && pair.SourceUid == 10043)
                && threeMemberContextPlan.Any(
                    pair => pair.RecipientUid == 10043
                        && pair.SourceUid == 10041)
                && BitConverter.ToUInt16(directEndpointPacket, 1) == 0x000B
                && BitConverter.ToUInt16(directRealtimePacket, 1) == 0x0099
                && directEndpointPacket.Length == 82
                && directEndpointPacket[15] == 3
                && BitConverter.ToUInt16(directEndpointPacket, 16) == 10041
                && directEndpointPacket[18] == 127
                && directEndpointPacket[19] == 0
                && directEndpointPacket[20] == 0
                && directEndpointPacket[21] == 1
                && directEndpointPacket[26] == 0x12
                && directEndpointPacket[27] == 0x34
                && BitConverter.ToUInt16(directEndpointPacket, 38) == 10042
                && directEndpointPacket[40] == 127
                && directEndpointPacket[41] == 0
                && directEndpointPacket[42] == 0
                && directEndpointPacket[43] == 1
                && directEndpointPacket[48] == 0x56
                && directEndpointPacket[49] == 0x78
                && BitConverter.ToUInt16(directEndpointPacket, 60) == 10043
                && directEndpointPacket[62] == 127
                && directEndpointPacket[63] == 0
                && directEndpointPacket[64] == 0
                && directEndpointPacket[65] == 1
                && directEndpointPacket[70] == 0x9A
                && directEndpointPacket[71] == 0xBC
                && directRealtimePacket.Length == 31
                && directRealtimePacket[15] == 3,
                ref failures);
            threeMemberP2pParty.TryAddMember(new PartyMember
            {
                UserId = 10044,
                CharacterId = 10044,
                SessionId = Guid.NewGuid(),
                Name = "p2p-fourth",
                IpBytes = new byte[] { 127, 0, 0, 1 },
                P2pPort = 0xDEF0,
            });
            var fourMemberP2pProjection =
                PartyHandler.BuildDirectP2pProjectionPackets(
                    threeMemberP2pParty);
            var fourMemberEndpointPacket = fourMemberP2pProjection[0];
            var fourMemberRealtimePacket = fourMemberP2pProjection[1];
            var fourMemberContextPlan =
                PartyHandler.BuildPartyFormationContextPlan(
                    threeMemberP2pParty.MembersBySlot());
            Check(
                "four-member formation fans out twelve peer contexts and preserves reusable endpoint bytes for both phases",
                fourMemberContextPlan.Count == 12
                && fourMemberContextPlan.All(
                    pair => pair.RecipientUid != pair.SourceUid)
                && fourMemberContextPlan.Any(
                    pair => pair.RecipientUid == 10041
                        && pair.SourceUid == 10044)
                && fourMemberContextPlan.Any(
                    pair => pair.RecipientUid == 10044
                        && pair.SourceUid == 10041)
                && fourMemberEndpointPacket.Length == 104
                && fourMemberEndpointPacket[15] == 4
                && BitConverter.ToUInt16(
                    fourMemberEndpointPacket,
                    82) == 10044
                && fourMemberEndpointPacket[84] == 127
                && fourMemberEndpointPacket[85] == 0
                && fourMemberEndpointPacket[86] == 0
                && fourMemberEndpointPacket[87] == 1
                && fourMemberEndpointPacket[92] == 0xDE
                && fourMemberEndpointPacket[93] == 0xF0
                && fourMemberRealtimePacket.Length == 36
                && fourMemberRealtimePacket[15] == 4,
                ref failures);
            var capturedEdit = new byte[]
            {
                0x01, 0x00, 0x0C, 0x00, 0x00, 0x00,
                0xE6, 0x97, 0xA0,
                0xE9, 0x98, 0x9F,
                0xE4, 0xBC, 0x8D,
                0xE5, 0x90, 0x8D,
                0x04, 0x00, 0x00, 0x00, 0x00,
                0x05, 0x00, 0x02, 0x01, 0x00,
            };
            var capturedEditParsed = SetPartyInfoRequest.TryParse(
                capturedEdit,
                out var editRequest);
            var capturedCapacityTwoCreate = new byte[]
            {
                0x00, 0x03, 0x02, 0x00, 0x00, 0x00,
                0x00, 0x05, 0x00, 0x02, 0x00, 0x00,
            };
            var capturedCapacityThreeEdit = new byte[]
            {
                0x01, 0x00, 0x0C, 0x00, 0x00, 0x00,
                0xE6, 0x97, 0xA0,
                0xE9, 0x98, 0x9F,
                0xE4, 0xBC, 0x8D,
                0xE5, 0x90, 0x8D,
                0x03, 0x00, 0x00, 0x00, 0x00,
                0x05, 0x00, 0x02, 0x00, 0x00,
            };
            var normalEdit = (byte[])capturedEdit.Clone();
            normalEdit[0] = 0;
            var cycleEdit = (byte[])capturedEdit.Clone();
            cycleEdit[0] = 2;
            Check(
                "SET_PARTY_INFO normalizes captured create and named-edit shapes",
                SetPartyInfoRequest.TryParse(
                    party.PartyInfoBlock,
                    out var request)
                && request.Raw.Length == 12
                && capturedEditParsed
                && editRequest.Title.Length == 12
                && request.UserMax == 4
                && editRequest.UserMax == 4
                && SetPartyInfoRequest.TryParse(
                    capturedCapacityTwoCreate,
                    out var capacityTwoRequest)
                && capacityTwoRequest.UserMax == 2
                && SetPartyInfoRequest.TryParse(
                    capturedCapacityThreeEdit,
                    out var capacityThreeRequest)
                && capacityThreeRequest.UserMax == 3
                && BitConverter.ToInt32(capturedEdit, 2)
                    == editRequest.Title.Length
                && BitConverter.ToString(editRequest.Raw)
                    == "01-00-04-00-00-00-00-05-00-02-01-00"
                && SetPartyInfoRequest.TryParse(
                    normalEdit,
                    out var normalEditRequest)
                && normalEditRequest.Raw[0] == 0
                && SetPartyInfoRequest.TryParse(
                    cycleEdit,
                    out var cycleEditRequest)
                && cycleEditRequest.Raw[0] == 2
                && !SetPartyInfoRequest.TryParse(new byte[11], out _)
                && !SetPartyInfoRequest.TryParse(
                    new byte[]
                    {
                        0x01, 0x00, 0x01, 0x00, 0x00, 0x00,
                        0x41,
                    },
                    out _),
                ref failures);
            var capacityManager = new PartyManager();
            var capacityLeaderSession = Guid.NewGuid();
            var capacityFollowerSession = Guid.NewGuid();
            var capacityCreate = capacityManager.CreateParty(
                new PartyMember
                {
                    UserId = 10041,
                    CharacterId = 10041,
                    SessionId = capacityLeaderSession,
                    Name = "capacity-leader",
                });
            var capacityPartyId = capacityCreate.Party.PartyId;
            var capacityJoin = capacityManager.Join(
                capacityPartyId,
                new PartyMember
                {
                    UserId = 10042,
                    CharacterId = 10042,
                    SessionId = capacityFollowerSession,
                    Name = "capacity-follower",
                });
            var capacityFourInfo = (byte[])party.PartyInfoBlock.Clone();
            capacityFourInfo[2] = 4;
            var capacityFour = capacityManager.UpdateSettings(
                10041,
                capacityLeaderSession,
                Array.Empty<byte>(),
                4,
                capacityFourInfo);
            var capacityOneInfo = (byte[])capacityFourInfo.Clone();
            capacityOneInfo[2] = 1;
            var capacityOne = capacityManager.UpdateSettings(
                10041,
                capacityLeaderSession,
                Array.Empty<byte>(),
                1,
                capacityOneInfo);
            var capacityTwoInfo = (byte[])capacityFourInfo.Clone();
            capacityTwoInfo[2] = 2;
            var capacityTwo = capacityManager.UpdateSettings(
                10041,
                capacityLeaderSession,
                Array.Empty<byte>(),
                2,
                capacityTwoInfo);
            var capacityThirdJoin = capacityManager.Join(
                capacityPartyId,
                new PartyMember
                {
                    UserId = 10043,
                    CharacterId = 10043,
                    SessionId = Guid.NewGuid(),
                    Name = "capacity-third",
                });
            var capacityParty = capacityManager.GetPartySnapshot(
                capacityPartyId);
            Check(
                "selected capacity accepts currentCount <= limit and gates later joins",
                capacityJoin.Ok
                && capacityFour.Ok
                && !capacityOne.Ok
                && capacityOne.Reason == "member_count_exceeds_user_max"
                && capacityTwo.Ok
                && !capacityThirdJoin.Ok
                && capacityThirdJoin.Reason == "party_full"
                && capacityParty.Count == 2
                && capacityParty.Capacity == 2
                && capacityParty.PartyInfoBlock[2] == 2,
                ref failures);
            var applyManager = new PartyManager();
            var requesterSession = Guid.NewGuid();
            var targetSession = Guid.NewGuid();
            var targetMember = new PartyMember
            {
                UserId = 10051,
                CharacterId = 10051,
                SessionId = targetSession,
                Name = "apply-target",
            };
            var requesterMember = new PartyMember
            {
                UserId = 10050,
                CharacterId = 10050,
                SessionId = requesterSession,
                Name = "applicant",
            };
            var targetCreate = applyManager.CreateParty(targetMember);
            var applicationRecorded = applyManager.RecordInvite(
                10051,
                targetSession,
                10050,
                requesterSession,
                out var applicationRecordFailure);
            var staleTargetSession = Guid.NewGuid();
            var staleApplication = applyManager.AcceptInvite(
                10051,
                staleTargetSession,
                10050,
                requesterSession,
                requesterMember,
                new PartyMember
                {
                    UserId = 10051,
                    CharacterId = 10051,
                    SessionId = staleTargetSession,
                    Name = "stale-target",
                },
                out _);
            var applicationJoin = applyManager.AcceptInvite(
                10051,
                targetSession,
                10050,
                requesterSession,
                requesterMember,
                targetMember,
                out var applicationMode);
            var appliedParty = applyManager.GetPartySnapshot(
                targetCreate.Party.PartyId);
            Check(
                "join application keeps the target party generation and leader while adding requester",
                applicationRecorded
                && applicationRecordFailure == null
                && !staleApplication.Ok
                && staleApplication.Reason == "invite_not_found_or_stale"
                && applicationJoin.Ok
                && applicationMode == "apply-into-invitee-party"
                && applyManager.PartyCount == 1
                && appliedParty.PartyId == targetCreate.Party.PartyId
                && appliedParty.LeaderUserId == 10051
                && appliedParty.GetMember(10051).SlotIndex == 0
                && appliedParty.GetMember(10050).SlotIndex == 1,
                ref failures);
            var createInviteManager = new PartyManager();
            var createInviterSession = Guid.NewGuid();
            var createInviteeSession = Guid.NewGuid();
            var createInviter = new PartyMember
            {
                UserId = 10060,
                CharacterId = 10060,
                SessionId = createInviterSession,
                Name = "create-inviter",
            };
            var createInvitee = new PartyMember
            {
                UserId = 10061,
                CharacterId = 10061,
                SessionId = createInviteeSession,
                Name = "create-invitee",
            };
            var createInviteRecorded = createInviteManager.RecordInvite(
                createInvitee.UserId,
                createInviteeSession,
                createInviter.UserId,
                createInviterSession,
                out _);
            var createInviteAccepted = createInviteManager.AcceptInvite(
                createInvitee.UserId,
                createInviteeSession,
                createInviter.UserId,
                createInviterSession,
                createInviter,
                createInvitee,
                out var createInviteMode);
            var createdInviteParty = createInviteManager.GetPartySnapshotByUser(
                createInviter.UserId);
            Check(
                "ordinary type-0 acceptance creates an inviter-led party when both players are free",
                createInviteRecorded
                && createInviteAccepted.Ok
                && createInviteMode == "create-inviter-party"
                && createInviteManager.PartyCount == 1
                && createdInviteParty.LeaderUserId == createInviter.UserId
                && createdInviteParty.GetMember(createInviter.UserId).SlotIndex == 0
                && createdInviteParty.GetMember(createInvitee.UserId).SlotIndex == 1,
                ref failures);
            var existingInviteManager = new PartyManager();
            var existingLeaderSession = Guid.NewGuid();
            var existingInviteeSession = Guid.NewGuid();
            var existingLeader = new PartyMember
            {
                UserId = 10070,
                CharacterId = 10070,
                SessionId = existingLeaderSession,
                Name = "existing-leader",
            };
            var existingInvitee = new PartyMember
            {
                UserId = 10071,
                CharacterId = 10071,
                SessionId = existingInviteeSession,
                Name = "existing-invitee",
            };
            var existingCreated = existingInviteManager.CreateParty(
                existingLeader);
            var existingInviteRecorded = existingInviteManager.RecordInvite(
                existingInvitee.UserId,
                existingInviteeSession,
                existingLeader.UserId,
                existingLeaderSession,
                out _);
            var existingInviteAccepted = existingInviteManager.AcceptInvite(
                existingInvitee.UserId,
                existingInviteeSession,
                existingLeader.UserId,
                existingLeaderSession,
                existingLeader,
                existingInvitee,
                out var existingInviteMode);
            var existingInviteParty = existingInviteManager.GetPartySnapshot(
                existingCreated.Party.PartyId);
            var duplicateInviteResponse = existingInviteManager.AcceptInvite(
                existingInvitee.UserId,
                existingInviteeSession,
                existingLeader.UserId,
                existingLeaderSession,
                existingLeader,
                existingInvitee,
                out _);
            Check(
                "ordinary invite joins the existing leader party and consumes the request once",
                existingInviteRecorded
                && existingInviteAccepted.Ok
                && existingInviteMode == "invite-into-inviter-party"
                && existingInviteParty.PartyId == existingCreated.Party.PartyId
                && existingInviteParty.LeaderUserId == existingLeader.UserId
                && existingInviteParty.GetMember(existingLeader.UserId).SlotIndex == 0
                && existingInviteParty.GetMember(existingInvitee.UserId).SlotIndex == 1
                && !duplicateInviteResponse.Ok
                && duplicateInviteResponse.Reason == "invite_not_found_or_stale",
                ref failures);
            var generationManager = new PartyManager();
            var generationRequesterSession = Guid.NewGuid();
            var generationTargetSession = Guid.NewGuid();
            var generationRequester = new PartyMember
            {
                UserId = 10080,
                CharacterId = 10080,
                SessionId = generationRequesterSession,
                Name = "generation-requester",
            };
            var generationTarget = new PartyMember
            {
                UserId = 10081,
                CharacterId = 10081,
                SessionId = generationTargetSession,
                Name = "generation-target",
            };
            var generationParty = generationManager.CreateParty(
                generationTarget);
            var generationRecorded = generationManager.RecordInvite(
                generationTarget.UserId,
                generationTargetSession,
                generationRequester.UserId,
                generationRequesterSession,
                out _);
            generationManager.Leave(
                generationTarget.UserId,
                generationTargetSession);
            var replacementGeneration = generationManager.CreateParty(
                generationTarget);
            var staleGenerationAccept = generationManager.AcceptInvite(
                generationTarget.UserId,
                generationTargetSession,
                generationRequester.UserId,
                generationRequesterSession,
                generationRequester,
                generationTarget,
                out _);
            var staleGenerationReplay = generationManager.AcceptInvite(
                generationTarget.UserId,
                generationTargetSession,
                generationRequester.UserId,
                generationRequesterSession,
                generationRequester,
                generationTarget,
                out _);
            Check(
                "join application rejects a replaced target party generation without moving requester",
                generationRecorded
                && generationParty.Party.PartyId != replacementGeneration.Party.PartyId
                && !staleGenerationAccept.Ok
                && staleGenerationAccept.Reason == "invitee_party_changed"
                && !staleGenerationReplay.Ok
                && staleGenerationReplay.Reason == "invite_not_found_or_stale"
                && generationManager.GetPartySnapshotByUser(
                    generationRequester.UserId) == null
                && generationManager.GetPartySnapshotByUser(
                    generationTarget.UserId).PartyId == replacementGeneration.Party.PartyId,
                ref failures);
            var canceledInviteManager = new PartyManager();
            var canceledInviterSession = Guid.NewGuid();
            var canceledInviteeSession = Guid.NewGuid();
            var canceledInviter = new PartyMember
            {
                UserId = 10090,
                CharacterId = 10090,
                SessionId = canceledInviterSession,
                Name = "canceled-inviter",
            };
            var canceledInvitee = new PartyMember
            {
                UserId = 10091,
                CharacterId = 10091,
                SessionId = canceledInviteeSession,
                Name = "canceled-invitee",
            };
            var canceledTargetParty =
                canceledInviteManager.CreateParty(canceledInvitee);
            var canceledInviteRecorded = canceledInviteManager.RecordInvite(
                canceledInvitee.UserId,
                canceledInviteeSession,
                canceledInviter.UserId,
                canceledInviterSession,
                out _);
            var canceledExactInvite = canceledInviteManager.CancelInvite(
                canceledInvitee.UserId,
                canceledInviteeSession,
                canceledInviter.UserId,
                canceledInviterSession);
            var canceledInviteReplay = canceledInviteManager.AcceptInvite(
                canceledInvitee.UserId,
                canceledInviteeSession,
                canceledInviter.UserId,
                canceledInviterSession,
                canceledInviter,
                canceledInvitee,
                out _);
            Check(
                "a refused join application consumes its exact request without moving either player",
                canceledTargetParty.Ok
                && canceledInviteRecorded
                && canceledExactInvite
                && !canceledInviteReplay.Ok
                && canceledInviteReplay.Reason == "invite_not_found_or_stale"
                && canceledInviteManager.PartyCount == 1
                && canceledInviteManager.GetPartySnapshotByUser(
                    canceledInviter.UserId) == null
                && canceledInviteManager.GetPartySnapshotByUser(
                    canceledInvitee.UserId).PartyId ==
                    canceledTargetParty.Party.PartyId
                && canceledTargetParty.Party.LeaderUserId ==
                    canceledInvitee.UserId
                && canceledTargetParty.Party.Count == 1
                && canceledTargetParty.Party.Members[0]?.UserId ==
                    canceledInvitee.UserId,
                ref failures);
            var originalPartyInfoBlock = party.PartyInfoBlock;
            party.PartyInfoBlock = editRequest.Raw;
            var editProjection = PartyInfoNotiBuilder.Build(party, 1);
            var namedFullProjection = PartyInfoNotiBuilder.Build(party, 0);
            party.PartyInfoBlock = originalPartyInfoBlock;
            Check(
                "nonzero info0 skips the conditional dstr in settings-only and full projections",
                editProjection.Length == 18
                && editProjection[5] == 0x01
                && editProjection[6] == 0x00
                && editProjection[7] == 0x04
                && editProjection[15] == 0x01
                && editProjection[16] == 0x00
                && editProjection[17] == 0x00
                && namedFullProjection.Length == 61
                && namedFullProjection[5] == 0x01
                && namedFullProjection[6] == 0x00
                && namedFullProjection[7] == 0x04
                && BitConverter.ToUInt16(namedFullProjection, 17) == 10038
                && namedFullProjection[58] == 0
                && namedFullProjection[60] == 0,
                ref failures);
            Check(
                "party follower projection keeps ENTER_SELECT response type 0x000F",
                DungeonEntryHandler.ResolveEnterSelectDungeonResponseType(
                    requestType: 0x0010,
                    isWireRequest: false) == 0x000F
                && DungeonEntryHandler.ResolveEnterSelectDungeonResponseType(
                    requestType: 0x000F,
                    isWireRequest: true) == 0x000F,
                ref failures);
            var initialTownPlayer = new Game.Session.PlayerContext
            {
                CharacterId = 10038,
                UserState = 0x00,
            };
            var firstTownMovement = initialTownPlayer.NextTownMovementSequence();
            var secondTownMovement = initialTownPlayer.NextTownMovementSequence();
            Check(
                "fresh town login publishes the full area roster exactly once",
                TownHandler.ShouldProjectInitialAreaRoster(
                    initialTownPlayer,
                    firstTownMovement)
                && !TownHandler.ShouldProjectInitialAreaRoster(
                    initialTownPlayer,
                    secondTownMovement),
                ref failures);
            var departureSnapshot = new TownUserSnapshot
            {
                UserId = 10038,
                TownId = 1,
                AreaId = 2,
                PosX = 321,
                PosY = 654,
                Direction = 5,
                State = 0,
            };
            var departureProjection =
                TownHandler.BuildAreaTransitionDeparturePacket(
                    departureSnapshot);
            Check(
                "town transition tells the old area the user's new area through USER_AREA without USER_LEAVE",
                departureProjection.Length == 25
                && departureProjection[0] == 0x00
                && BitConverter.ToUInt16(departureProjection, 1) == 0x0017
                && BitConverter.ToUInt16(departureProjection, 15) == 10038
                && departureProjection[17] == 1
                && departureProjection[18] == 2,
                ref failures);

            party.TryAddMember(new PartyMember
            {
                UserId = 10039,
                CharacterId = 10039,
                SessionId = Guid.NewGuid(),
                Name = "follower",
            });
            var partyRoster = party.MembersBySlot();
            var selectionCohort = new DungeonPartySelectionCohort(
                projectionId: 7,
                partyId: party.PartyId,
                leaderUserId: party.LeaderUserId,
                participants: new[]
                {
                    new DungeonPartySelectionParticipant(
                        partyRoster[0].UserId,
                        partyRoster[0].CharacterId,
                        partyRoster[0].SessionId,
                        partyRoster[0].SlotIndex),
                    new DungeonPartySelectionParticipant(
                        partyRoster[1].UserId,
                        partyRoster[1].CharacterId,
                        partyRoster[1].SessionId,
                        partyRoster[1].SlotIndex),
                });
            var retrySelectionCohort = new DungeonPartySelectionCohort(
                projectionId: 8,
                partyId: party.PartyId,
                leaderUserId: party.LeaderUserId,
                participants: selectionCohort.Participants,
                returnToTownOnEntryReject: true);
            var delegatedLeaderCohort = new DungeonPartySelectionCohort(
                projectionId: 9,
                partyId: party.PartyId,
                leaderUserId: 10039,
                participants: selectionCohort.Participants);
            Check(
                "dungeon UDP host follows the frozen leader slot without reordering members",
                DungeonEntryHandler.TryResolveDungeonHostSlot(
                    delegatedLeaderCohort,
                    party,
                    out var delegatedHostSlot)
                && delegatedHostSlot == 1,
                ref failures);
            var retryIntentPlayer = new PlayerContext();
            retryIntentPlayer.MarkPendingPartyRetryEntry(144);
            Check(
                "retry selection carries one-shot whole-party return-on-reject policy",
                !selectionCohort.ReturnToTownOnEntryReject
                && retrySelectionCohort.ReturnToTownOnEntryReject
                && retryIntentPlayer.ConsumePendingPartyRetryEntry(144)
                && !retryIntentPlayer.ConsumePendingPartyRetryEntry(144),
                ref failures);
            retryIntentPlayer.MarkPendingPartyRetryEntry(144);
            retryIntentPlayer.ClearPendingPartyRetryEntry();
            Check(
                "character teardown clears a pending retry intent before PlayerContext reuse",
                !retryIntentPlayer.ConsumePendingPartyRetryEntry(144),
                ref failures);
            var groupLeaseSessionA = Guid.NewGuid();
            var groupLeaseSessionB = Guid.NewGuid();
            var groupLeaseA = InventoryContext.Register(
                groupLeaseSessionA,
                new InventoryService(910001, 920001));
            var groupLeaseB = InventoryContext.Register(
                groupLeaseSessionB,
                new InventoryService(910002, 920002));
            try
            {
                var groupCostService = new DungeonEntryCostService(
                    persistInventory: _ => true);
                var reverseOrderedCosts = new[]
                {
                    new DungeonEntryCostCommitRequest(
                        groupLeaseB,
                        new DungeonEntryCostPlan("party-selftest-b")),
                    new DungeonEntryCostCommitRequest(
                        groupLeaseA,
                        new DungeonEntryCostPlan("party-selftest-a")),
                };
                var groupCostCommitted = groupCostService.TryCommitPlans(
                    reverseOrderedCosts,
                    out var groupCostResults);
                Check(
                    "group entry cost accepts reverse CID order and returns one success per participant",
                    groupCostCommitted
                    && groupCostResults.Count == 2
                    && groupCostResults[0].Success
                    && groupCostResults[1].Success,
                    ref failures);
            }
            finally
            {
                InventoryContext.Unregister(
                    groupLeaseSessionA,
                    groupLeaseA.CharacterId);
                InventoryContext.Unregister(
                    groupLeaseSessionB,
                    groupLeaseB.CharacterId);
            }
            var leaderProjectionIdentity =
                new DungeonPartySelectionParticipant(
                    partyRoster[0].UserId,
                    partyRoster[0].CharacterId,
                    partyRoster[0].SessionId,
                    partyRoster[0].SlotIndex);
            var leaderProjectionOrderOk =
                DungeonEntryHandler.TryBuildDungeonUserInfoProjectionOrder(
                    selectionCohort,
                    leaderProjectionIdentity,
                    _ => true,
                    out var leaderProjectionOrder,
                    out _);
            var followerProjectionOrderOk =
                DungeonEntryHandler.TryBuildDungeonUserInfoProjectionOrder(
                    selectionCohort,
                    new DungeonPartySelectionParticipant(
                        partyRoster[1].UserId,
                        partyRoster[1].CharacterId,
                        partyRoster[1].SessionId,
                        partyRoster[1].SlotIndex),
                    _ => true,
                    out var followerProjectionOrder,
                    out _);
            Check(
                "dungeon USERINFO projection is receiver-first and peers stay in stable slot order",
                leaderProjectionOrderOk
                && leaderProjectionOrder.Count == 2
                && leaderProjectionOrder[0].UserId == 10038
                && leaderProjectionOrder[1].UserId == 10039
                && followerProjectionOrderOk
                && followerProjectionOrder.Count == 2
                && followerProjectionOrder[0].UserId == 10039
                && followerProjectionOrder[1].UserId == 10038,
                ref failures);
            var projectedUserIds = new HashSet<ushort>();
            foreach (var participant in followerProjectionOrder)
                projectedUserIds.Add(participant.UserId);
            Check(
                "dungeon USERINFO projection emits every frozen UID exactly once",
                projectedUserIds.Count == followerProjectionOrder.Count
                && projectedUserIds.SetEquals(
                    new ushort[] { 10038, 10039 }),
                ref failures);

            var soloIdentity = new DungeonPartySelectionParticipant(
                10100,
                10100,
                Guid.NewGuid(),
                0);
            var soloProjectionOk =
                DungeonEntryHandler.TryBuildDungeonUserInfoProjectionOrder(
                    cohort: null,
                    receiver: soloIdentity,
                    isCurrent: _ => true,
                    ordered: out var soloProjection,
                    error: out _);
            Check(
                "solo dungeon USERINFO projection remains self-only",
                soloProjectionOk
                && soloProjection.Count == 1
                && soloProjection[0].UserId == soloIdentity.UserId,
                ref failures);

            var staleProjectionAccepted =
                DungeonEntryHandler.TryBuildDungeonUserInfoProjectionOrder(
                    selectionCohort,
                    leaderProjectionIdentity,
                    participant => participant.UserId != 10039,
                    out _,
                    out _);
            var unknownProjectionAccepted =
                DungeonEntryHandler.TryBuildDungeonUserInfoProjectionOrder(
                    selectionCohort,
                    new DungeonPartySelectionParticipant(
                        10999,
                        10999,
                        Guid.NewGuid(),
                        2),
                    _ => true,
                    out _,
                    out _);
            Check(
                "dungeon USERINFO projection rejects stale generations and unknown receivers",
                !staleProjectionAccepted
                && !unknownProjectionAccepted,
                ref failures);

            var subtype1Template = new byte[24];
            subtype1Template[0] = 1;
            BitConverter.GetBytes((ushort)1).CopyTo(
                subtype1Template,
                1);
            BitConverter.GetBytes((ushort)2345).CopyTo(
                subtype1Template,
                18);
            var subtype1Bound =
                DungeonEntryHandler.TryBindDungeonPeerUserInfoIdentity(
                    subtype1Template,
                    10039,
                    2345,
                    out var subtype1Body);
            var mismatchedInnerCidAccepted =
                DungeonEntryHandler.TryBindDungeonPeerUserInfoIdentity(
                    subtype1Template,
                    10039,
                    9999,
                    out _);
            Check(
                "dungeon peer USERINFO binds routing UID without replacing the inner character id",
                subtype1Bound
                && subtype1Body[0] == 1
                && BitConverter.ToUInt16(subtype1Body, 1) == 1
                && BitConverter.ToUInt16(subtype1Body, 3) == 10039
                && BitConverter.ToUInt16(subtype1Body, 18) == 2345
                && BitConverter.ToUInt16(subtype1Template, 3) == 0
                && BitConverter.ToUInt16(subtype1Template, 18) == 2345
                && !mismatchedInnerCidAccepted,
                ref failures);

            var leaderUserInfoBody = (byte[])subtype1Template.Clone();
            BitConverter.GetBytes((ushort)1234).CopyTo(
                leaderUserInfoBody,
                18);
            var enterSelectPrefix =
                DungeonEntryHandler.BuildEnterSelectDungeonPrefix(
                    new[]
                    {
                        GamePacketEnvelopeBuilder.Build(
                            0x00,
                            (ushort)NotiPacketTypeA21.USERINFO,
                            leaderUserInfoBody),
                        GamePacketEnvelopeBuilder.Build(
                            0x00,
                            (ushort)NotiPacketTypeA21.USERINFO,
                            subtype1Body),
                    },
                    DungeonEntryHandler.StartGameResponseType,
                    new ushort[] { 10038, 10039 },
                    delegatedHostSlot);
            var prefixContainsStartMap = false;
            foreach (var packet in enterSelectPrefix)
            {
                if (BitConverter.ToUInt16(packet, 1)
                    == (ushort)NotiPacketTypeA21.START_MAP)
                {
                    prefixContainsStartMap = true;
                }
            }
            Check(
                "all dungeon USERINFO packets precede success, USER_STATE, UDP host, and START_MAP",
                enterSelectPrefix.Count == 5
                && BitConverter.ToUInt16(enterSelectPrefix[0], 1)
                    == (ushort)NotiPacketTypeA21.USERINFO
                && BitConverter.ToUInt16(enterSelectPrefix[1], 1)
                    == (ushort)NotiPacketTypeA21.USERINFO
                && BitConverter.ToUInt16(enterSelectPrefix[0], 18) == 0
                && BitConverter.ToUInt16(enterSelectPrefix[0], 33) == 1234
                && BitConverter.ToUInt16(enterSelectPrefix[1], 18) == 10039
                && BitConverter.ToUInt16(enterSelectPrefix[1], 33) == 2345
                && enterSelectPrefix[2][0] == 0x01
                && BitConverter.ToUInt16(enterSelectPrefix[2], 1)
                    == DungeonEntryHandler.StartGameResponseType
                && BitConverter.ToUInt16(enterSelectPrefix[3], 1)
                    == (ushort)NotiPacketTypeA21.USER_STATE
                && BitConverter.ToUInt16(enterSelectPrefix[4], 1)
                    == 0x001A
                && enterSelectPrefix[4].Length == 16
                && enterSelectPrefix[4][15] == 1
                && !prefixContainsStartMap,
                ref failures);
            var selectionPlayer = new PlayerContext();
            var selectionAnchor = new DungeonTownReturnAnchor(
                townId: 1,
                areaId: 2,
                x: 900,
                y: 388,
                direction: 5,
                areaState: 3);
            var firstSelection = selectionPlayer.BeginDungeonSelection(
                selectionAnchor,
                isA21TutorialEntry: false,
                partyCohort: selectionCohort,
                created: out var firstSelectionCreated);
            var duplicateSelection = selectionPlayer.BeginDungeonSelection(
                selectionAnchor,
                isA21TutorialEntry: false,
                partyCohort: selectionCohort,
                created: out var duplicateSelectionCreated);
            Check(
                "party selection freezes one stable roster cohort and does not recreate it on retry",
                firstSelectionCreated
                && !duplicateSelectionCreated
                && ReferenceEquals(firstSelection, duplicateSelection)
                && ReferenceEquals(firstSelection.PartyCohort, selectionCohort)
                && selectionCohort.Participants.Count == 2
                && selectionCohort.Participants[0].SlotIndex == 0
                && selectionCohort.Participants[1].SlotIndex == 1,
                ref failures);
            Check(
                "a projected selection becomes usable only after its local projection completes",
                !firstSelection.IsPartyProjectionComplete
                && firstSelection.TryCompletePartyProjection()
                && firstSelection.IsPartyProjectionComplete,
                ref failures);
            Check(
                "ENTER_SELECT retry reuses only the still-current selection cohort",
                DungeonEntryHandler.IsPartySelectionRetryCurrent(
                    selectionPlayer,
                    firstSelection,
                    selectionCohort),
                ref failures);
            firstSelection.TryBeginReturn();
            selectionPlayer.CompleteDungeonSelection(firstSelection);
            Check(
                "a completed selection return makes its captured retry stale",
                !DungeonEntryHandler.IsPartySelectionRetryCurrent(
                    selectionPlayer,
                    firstSelection,
                    selectionCohort),
                ref failures);
            Check(
                "selection cancel fans out only for the party back-to-village command",
                TownHandler.ShouldFanOutPartySelectionReturn(0x0084)
                && !TownHandler.ShouldFanOutPartySelectionReturn(0x002A),
                ref failures);
            Check(
                "settlement return fans out only from the party leader with a shared cohort",
                DungeonSettlementHandler.ShouldFanOutPartySettlementReturn(
                    party,
                    10038,
                    2)
                && !DungeonSettlementHandler.ShouldFanOutPartySettlementReturn(
                    party,
                    10039,
                    2)
                && !DungeonSettlementHandler.ShouldFanOutPartySettlementReturn(
                    party,
                    10038,
                    1),
                ref failures);
            Check(
                "EPLP retry and select-other degrade to town when the frozen party cannot move together",
                DungeonSettlementHandler.ResolveStandardEplpOption(
                    requestedOption: 0,
                    completePartyRoster: false,
                    retryEntryAllowed: true,
                    allParticipantsEnded: true) == 2
                && DungeonSettlementHandler.ResolveStandardEplpOption(
                    requestedOption: 1,
                    completePartyRoster: false,
                    retryEntryAllowed: true,
                    allParticipantsEnded: true) == 2
                && DungeonSettlementHandler.ResolveStandardEplpOption(
                    requestedOption: 0,
                    completePartyRoster: true,
                    retryEntryAllowed: false,
                    allParticipantsEnded: true) == 2
                && DungeonSettlementHandler.ResolveStandardEplpOption(
                    requestedOption: 0,
                    completePartyRoster: true,
                    retryEntryAllowed: true,
                    allParticipantsEnded: false) == 2
                && DungeonSettlementHandler.ResolveStandardEplpOption(
                    requestedOption: 1,
                    completePartyRoster: true,
                    retryEntryAllowed: true,
                    allParticipantsEnded: true) == 1,
                ref failures);
            var cardInstance = new DungeonInstance(144, 0);
            var cardLeaderRun = new DungeonRun(
                cardInstance,
                runId: 801,
                runGeneration: 1,
                DungeonRunState.Active)
            {
                EntryPartySlotIndex = 0,
                Phase = DungeonRunPhase.CardsRevealed,
                SettlementRuntime = new DungeonSettlementRuntime
                {
                    ShouldScheduleCardRewardFlow = true,
                },
                CardRewards = new List<ClearRewardGenerator.CardReward>
                {
                    default, default, default, default,
                    default, default, default, default,
                },
            };
            var cardFollowerRun = new DungeonRun(
                cardInstance,
                runId: 802,
                runGeneration: 1,
                DungeonRunState.Active)
            {
                EntryPartySlotIndex = 1,
                Phase = DungeonRunPhase.CardsRevealed,
                SettlementRuntime = new DungeonSettlementRuntime
                {
                    ShouldScheduleCardRewardFlow = true,
                },
                CardRewards = new List<ClearRewardGenerator.CardReward>
                {
                    default, default, default, default,
                    default, default, default, default,
                },
            };
            var cardRoom = new DungeonRoomIdentity(
                cardLeaderRun.CaptureInstanceIdentity(),
                roomInstanceId: 9001);
            var cardRoster = new List<DungeonParticipantRosterEntry>
            {
                new DungeonParticipantRosterEntry(
                    characterId: 10038,
                    participantUserId: 10038,
                    run: cardLeaderRun,
                    runIdentity: cardLeaderRun.CaptureIdentity(),
                    roomIdentity: cardRoom,
                    attachmentGeneration: 1),
                new DungeonParticipantRosterEntry(
                    characterId: 10039,
                    participantUserId: 10039,
                    run: cardFollowerRun,
                    runIdentity: cardFollowerRun.CaptureIdentity(),
                    roomIdentity: cardRoom,
                    attachmentGeneration: 1),
            };
            var cardLayoutProjection =
                CardRewardCoordinator.BuildPartyProjection(
                    cardLeaderRun,
                    cardRoster);
            var cardLayout = CardRewardNotificationSender
                .BuildCardLayoutAck(cardLayoutProjection);
            Check(
                "party card layout enables frozen slots 0 and 1 without compressing the wire roster",
                BitConverter.ToString(cardLayout) ==
                    "01-01-00-01-00-FF-FF-FF-FF-FF-FF-FF-FF-FF-FF-FF-FF",
                ref failures);
            var leaderSelected = CardRewardRules.TrySelectCardSlot(
                cardLeaderRun,
                cardType: 0,
                cardIndex: 2);
            var followerSelected = CardRewardRules.TrySelectCardSlot(
                cardFollowerRun,
                cardType: 0,
                cardIndex: 1);
            var duplicateSelectionRejected = !CardRewardRules.TrySelectCardSlot(
                cardFollowerRun,
                cardType: 0,
                cardIndex: 3);
            var cardInfoProjection = CardRewardCoordinator.BuildPartyProjection(
                cardLeaderRun,
                cardRoster);
            var cardInfo = CardRewardNotificationSender.BuildCardInfoAck(
                cardInfoProjection);
            Check(
                "party card info separates participant slot from selected card index",
                leaderSelected
                && followerSelected
                && duplicateSelectionRejected
                && cardInfo.Length == 33
                && cardInfo[1] == 0xFF
                && cardInfo[2] == 0xFF
                && cardInfo[5] == 1
                && cardInfo[6] == 0xFF
                && cardInfo[9] == 0
                && cardInfo[10] == 0xFF
                && CardRewardCoordinator.IsCardPositionOccupied(
                    cardLeaderRun,
                    cardRoster,
                    CardRewardSide.Free,
                    cardIndex: 1)
                && !CardRewardCoordinator.IsCardPositionOccupied(
                    cardLeaderRun,
                    cardRoster,
                    CardRewardSide.Free,
                    cardIndex: 3),
                ref failures);
            var dungeonUserState = EnterSelectDungeonStateBuilder.BuildUserState(
                new ushort[] { 10038, 10039 },
                0x01);
            Check(
                "dungeon USER_STATE marks every party participant active",
                dungeonUserState.Length == 7
                && BitConverter.ToString(dungeonUserState) ==
                    "02-36-27-01-37-27-01",
                ref failures);
            Check(
                "START_MAP identifies each multiplayer recipient by its party slot",
                DungeonMapHandler.ResolveStartMapPartyMemberIndex(party, 10038) == 0
                && DungeonMapHandler.ResolveStartMapPartyMemberIndex(party, 10039) == 1
                && DungeonMapHandler.ResolveStartMapPartyMemberIndex(party, 9999) == 0xFF
                && DungeonMapHandler.ResolveStartMapPartyMemberIndex(null, 10038) == 0xFF,
                ref failures);
            Check(
                "formation uses the client-proven full type 0 roster for every recipient",
                PartyInfoNotiBuilder.Build(party, 0).Length == 65,
                ref failures);
            var returnProjection =
                TownHandler.BuildTownReturnPartyProjectionPackets(party);
            Check(
                "town return restores the party roster before realtime bars without restarting P2P",
                returnProjection.Length == 2
                && returnProjection[0][0] == 0x00
                && BitConverter.ToUInt16(returnProjection[0], 1) == 0x0009
                && returnProjection[1][0] == 0x00
                && BitConverter.ToUInt16(returnProjection[1], 1) == 0x0099,
                ref failures);
            Check(
                "abandoning a live dungeon skips stale party restoration while normal settlement keeps it",
                !TownHandler.ShouldRefreshPartyProjectionAfterTownReturn(0x002A)
                && TownHandler.ShouldRefreshPartyProjectionAfterTownReturn(0x0084),
                ref failures);

            var transferManager = new PartyManager();
            var transferLeaderSession = Guid.NewGuid();
            var transferFollowerSession = Guid.NewGuid();
            var transferCreate = transferManager.CreateParty(new PartyMember
            {
                UserId = 11001,
                CharacterId = 11001,
                SessionId = transferLeaderSession,
                Name = "leader",
            });
            var transferJoin = transferManager.Join(
                transferCreate.Party.PartyId,
                new PartyMember
                {
                    UserId = 11002,
                    CharacterId = 11002,
                    SessionId = transferFollowerSession,
                    Name = "follower",
                });
            var transferPartyId = transferCreate.Party.PartyId;
            var transferToFollower = transferManager.TransferLeader(
                transferPartyId,
                11001,
                transferLeaderSession,
                11002,
                transferFollowerSession);
            var transferred = transferManager.GetPartySnapshot(transferPartyId);
            var transferPackets =
                PartyHandler.BuildPartyHostProjectionPackets(
                    transferred,
                    includeRealtime: false);
            var transferBack = transferManager.TransferLeader(
                transferPartyId,
                11002,
                transferFollowerSession,
                11001,
                transferLeaderSession);
            var transferredBack = transferManager.GetPartySnapshot(
                transferPartyId);
            Check(
                "leader transfer keeps party id and every stable member slot while projecting the host slot",
                transferJoin.Ok
                && transferToFollower.Ok
                && transferred.PartyId == transferPartyId
                && transferred.LeaderUserId == 11002
                && transferred.GetMember(11001).SlotIndex == 0
                && transferred.GetMember(11002).SlotIndex == 1
                && transferPackets.Length == 2
                && BitConverter.ToUInt16(transferPackets[0], 1) == 0x001A
                && transferPackets[0][15] == 1
                && BitConverter.ToUInt16(transferPackets[1], 1) == 0x0009
                && transferPackets[1][19] == 2
                && transferPackets[1][61] == 1
                && PartyInfoNotiBuilder.Build(transferred, 0)[62] == 1
                && PartyInfoNotiBuilder.Build(transferred, 2)[46] == 1
                && DungeonEntryHandler.ShouldRejectPartySelectionRequest(
                    transferred,
                    11001,
                    transferLeaderSession)
                && !DungeonEntryHandler.ShouldRejectPartySelectionRequest(
                    transferred,
                    11002,
                    transferFollowerSession)
                && transferBack.Ok
                && transferredBack.PartyId == transferPartyId
                && transferredBack.LeaderUserId == 11001
                && transferredBack.GetMember(11001).SlotIndex == 0
                && transferredBack.GetMember(11002).SlotIndex == 1,
                ref failures);

            var disconnectManager = new PartyManager();
            var disconnectSlot0Session = Guid.NewGuid();
            var disconnectSlot1Session = Guid.NewGuid();
            var disconnectSlot2Session = Guid.NewGuid();
            var disconnectCreate = disconnectManager.CreateParty(
                new PartyMember
                {
                    UserId = 12001,
                    CharacterId = 12001,
                    SessionId = disconnectSlot0Session,
                    Name = "slot0",
                });
            var disconnectPartyId = disconnectCreate.Party.PartyId;
            var disconnectJoin1 = disconnectManager.Join(
                disconnectPartyId,
                new PartyMember
                {
                    UserId = 12002,
                    CharacterId = 12002,
                    SessionId = disconnectSlot1Session,
                    Name = "slot1",
                });
            var disconnectJoin2 = disconnectManager.Join(
                disconnectPartyId,
                new PartyMember
                {
                    UserId = 12003,
                    CharacterId = 12003,
                    SessionId = disconnectSlot2Session,
                    Name = "slot2",
                });
            var disconnectTransfer = disconnectManager.TransferLeader(
                disconnectPartyId,
                12001,
                disconnectSlot0Session,
                12002,
                disconnectSlot1Session);
            var disconnectedMiddleLeader =
                disconnectManager.OnSessionDisconnected(
                    12002,
                    disconnectSlot1Session);
            var disconnectSurvivors =
                disconnectManager.GetPartySnapshot(disconnectPartyId);
            Check(
                "a disconnected middle-slot leader promotes the leftmost survivor without rebuilding or moving slots",
                disconnectJoin1.Ok
                && disconnectJoin2.Ok
                && disconnectTransfer.Ok
                && disconnectedMiddleLeader.Ok
                && !disconnectedMiddleLeader.Disbanded
                && disconnectedMiddleLeader.LeaderChanged
                && disconnectedMiddleLeader.NewLeaderUserId == 12001
                && disconnectedMiddleLeader.RetiredParty == null
                && disconnectSurvivors != null
                && disconnectSurvivors.PartyId == disconnectPartyId
                && disconnectSurvivors.LeaderUserId == 12001
                && disconnectSurvivors.GetMember(12001).SlotIndex == 0
                && disconnectSurvivors.GetMember(12003).SlotIndex == 2
                && disconnectManager.GetPartyByUser(12002) == null,
                ref failures);

            var sparseDisconnectManager = new PartyManager();
            var sparseSessions = new[]
            {
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            };
            var sparseCreate = sparseDisconnectManager.CreateParty(
                new PartyMember
                {
                    UserId = 12101,
                    CharacterId = 12101,
                    SessionId = sparseSessions[0],
                    Name = "slot0",
                });
            var sparsePartyId = sparseCreate.Party.PartyId;
            for (var i = 1; i < 4; i++)
            {
                sparseDisconnectManager.Join(
                    sparsePartyId,
                    new PartyMember
                    {
                        UserId = (ushort)(12101 + i),
                        CharacterId = 12101 + i,
                        SessionId = sparseSessions[i],
                        Name = $"slot{i}",
                    });
            }
            var sparseSlot1Leave = sparseDisconnectManager.Leave(
                12102,
                sparseSessions[1]);
            var sparseLeaderDisconnect =
                sparseDisconnectManager.OnSessionDisconnected(
                    12101,
                    sparseSessions[0]);
            var sparseSurvivors =
                sparseDisconnectManager.GetPartySnapshot(sparsePartyId);
            Check(
                "a disconnected slot-zero leader promotes the lowest remaining sparse slot and preserves all survivor positions",
                sparseSlot1Leave.Ok
                && sparseLeaderDisconnect.Ok
                && !sparseLeaderDisconnect.Disbanded
                && sparseLeaderDisconnect.LeaderChanged
                && sparseLeaderDisconnect.NewLeaderUserId == 12103
                && sparseLeaderDisconnect.RetiredParty == null
                && sparseSurvivors != null
                && sparseSurvivors.PartyId == sparsePartyId
                && sparseSurvivors.LeaderUserId == 12103
                && sparseSurvivors.GetMember(12103).SlotIndex == 2
                && sparseSurvivors.GetMember(12104).SlotIndex == 3
                && PartyInfoNotiBuilder.Build(sparseSurvivors, 0)[62] == 2
                && PartyInfoNotiBuilder.Build(sparseSurvivors, 2)[46] == 2,
                ref failures);

            var soleDisconnectManager = new PartyManager();
            var soleLeaderSession = Guid.NewGuid();
            var soleSurvivorSession = Guid.NewGuid();
            var soleCreate = soleDisconnectManager.CreateParty(
                new PartyMember
                {
                    UserId = 12201,
                    CharacterId = 12201,
                    SessionId = soleLeaderSession,
                    Name = "leader",
                });
            var solePartyId = soleCreate.Party.PartyId;
            var soleJoin = soleDisconnectManager.Join(
                solePartyId,
                new PartyMember
                {
                    UserId = 12202,
                    CharacterId = 12202,
                    SessionId = soleSurvivorSession,
                    Name = "survivor",
                });
            var soleLeaderDisconnect =
                soleDisconnectManager.OnSessionDisconnected(
                    12201,
                    soleLeaderSession);
            var soleSurvivor =
                soleDisconnectManager.GetPartySnapshot(solePartyId);
            var soleHostPackets = PartyHandler.BuildPartyHostProjectionPackets(
                soleSurvivor,
                includeRealtime: false);
            Check(
                "a two-person leader disconnect preserves the sole survivor in slot one with host authority",
                soleJoin.Ok
                && soleLeaderDisconnect.Ok
                && !soleLeaderDisconnect.Disbanded
                && soleLeaderDisconnect.LeaderChanged
                && soleLeaderDisconnect.NewLeaderUserId == 12202
                && soleLeaderDisconnect.RetiredParty == null
                && soleSurvivor != null
                && soleSurvivor.PartyId == solePartyId
                && soleSurvivor.Count == 1
                && soleSurvivor.LeaderUserId == 12202
                && soleSurvivor.GetMember(12202).SlotIndex == 1
                && PartyInfoNotiBuilder.Build(soleSurvivor, 0)[62] == 1
                && soleHostPackets.Length == 2
                && BitConverter.ToUInt16(soleHostPackets[0], 1) == 0x001A
                && soleHostPackets[0][15] == 1
                && BitConverter.ToUInt16(soleHostPackets[1], 1) == 0x0009
                && soleHostPackets[1][61] == 1,
                ref failures);
            var unboundSelection = new DungeonSelectionContext(
                1,
                0,
                new DungeonTownReturnAnchor(1, 1, 0, 0, 0, 0),
                isA21TutorialEntry: false);
            Check(
                "joining a party invalidates an already-open solo selection",
                DungeonEntryHandler
                    .ShouldRejectUnboundSelectionAfterPartyChange(
                        unboundSelection,
                        transferred)
                && !DungeonEntryHandler
                    .ShouldRejectUnboundSelectionAfterPartyChange(
                        unboundSelection,
                        party: null),
                ref failures);
            var transferToFollowerAgain = transferManager.TransferLeader(
                transferPartyId,
                11001,
                transferLeaderSession,
                11002,
                transferFollowerSession);
            var formerLeaderExit = transferManager.LeaveForDungeonReturn(
                11001,
                transferLeaderSession,
                transferPartyId);
            var sparseSolo = transferManager.GetPartySnapshot(
                transferPartyId);
            var sparseSoloPreserved = transferManager.LeaveForDungeonReturn(
                11002,
                transferFollowerSession,
                transferPartyId);
            Check(
                "delegated slot-one leader stays in place when becoming the preserved final dungeon member",
                transferToFollowerAgain.Ok
                && formerLeaderExit.Ok
                && sparseSolo.Count == 1
                && sparseSolo.LeaderUserId == 11002
                && sparseSolo.GetMember(11002).SlotIndex == 1
                && DungeonEntryHandler.TryResolveDungeonHostSlot(
                    null,
                    sparseSolo,
                    out var sparseHostSlot)
                && sparseHostSlot == 1
                && sparseSoloPreserved.SoleMemberPreserved
                && transferManager.GetPartyByUser(11002)?.PartyId ==
                    transferPartyId,
                ref failures);

            var giveupManager = new PartyManager();
            var leaderSession = Guid.NewGuid();
            var followerSession = Guid.NewGuid();
            var giveupParty = giveupManager.CreateParty(new PartyMember
            {
                UserId = 10038,
                CharacterId = 10038,
                SessionId = leaderSession,
                Name = "leader",
            });
            var giveupJoin = giveupManager.Join(
                giveupParty.Party.PartyId,
                new PartyMember
                {
                    UserId = 10039,
                    CharacterId = 10039,
                    SessionId = followerSession,
                    Name = "follower",
                });
            var originalGiveupPartyId = giveupParty.Party.PartyId;
            var leaderGiveup = giveupManager.LeaveForDungeonReturn(
                10038,
                leaderSession,
                originalGiveupPartyId);
            var survivorParty = giveupManager.GetPartyByUser(10039);
            var staleFollowerGiveup = giveupManager.LeaveForDungeonReturn(
                10039,
                followerSession,
                originalGiveupPartyId);
            var followerGiveup = giveupManager.LeaveForDungeonReturn(
                10039,
                followerSession,
                survivorParty.PartyId);
            Check(
                "successive dungeon giveups detach earlier members and preserve the final survivor as leader",
                giveupJoin.Ok
                && leaderGiveup.Ok
                && leaderGiveup.LeaderChanged
                && leaderGiveup.NewLeaderUserId == 10039
                && leaderGiveup.RemainingMembers.Count == 1
                && !staleFollowerGiveup.Ok
                && staleFollowerGiveup.Reason == "party_generation_mismatch"
                && followerGiveup.Ok
                && followerGiveup.SoleMemberPreserved
                && !followerGiveup.Disbanded
                && giveupManager.GetPartyByUser(10038) == null
                && giveupManager.GetPartyByUser(10039)?.LeaderUserId == 10039
                && giveupManager.GetPartyByUser(10039)?.Count == 1,
                ref failures);
            var explicitFinalLeave = giveupManager.Leave(
                10039,
                followerSession);
            Check(
                "explicit LEAVE_PARTY still disbands a preserved one-member party",
                explicitFinalLeave.Ok
                && explicitFinalLeave.Disbanded
                && giveupManager.GetPartyByUser(10039) == null,
                ref failures);

            var loadingRoom = new DungeonInstanceRoom(
                roomInstanceId: 11,
                new RoomKey(0, 0, -1),
                new GameWorld.Dungeon.MazeSumInfo
                {
                    Monsters = new List<GameWorld.Dungeon.MonsterSumInfo>(),
                },
                seed: 1234);
            loadingRoom.AttachToInstance(7);
            loadingRoom.TryActivate();
            var leaderRun = new DungeonRunIdentity(7, 101, 1);
            var followerRun = new DungeonRunIdentity(7, 102, 1);
            var loadingRoster = new[] { leaderRun, followerRun };
            var loadingGeneration =
                loadingRoom.RegisterLoadingProjection(
                    leaderRun,
                    projectionId: 1,
                    loadingRoster,
                    out var loadingGenerationStarted);
            var leaderReady = loadingRoom.MarkLoadingReady(
                leaderRun,
                new[] { leaderRun });
            var duplicateLeaderReady = loadingRoom.MarkLoadingReady(
                leaderRun,
                new[] { leaderRun });
            var followerGeneration =
                loadingRoom.RegisterLoadingProjection(
                    followerRun,
                    projectionId: 1,
                    loadingRoster,
                    out var followerGenerationStarted);
            var followerReady = loadingRoom.MarkLoadingReady(
                followerRun,
                new[] { leaderRun });
            var duplicateReady = loadingRoom.MarkLoadingReady(
                followerRun,
                loadingRoster);
            Check(
                "dungeon loading releases once only after every active run is ready",
                loadingGeneration > 0
                && loadingGenerationStarted
                && followerGeneration == loadingGeneration
                && !followerGenerationStarted
                && leaderReady.Accepted
                && leaderReady.FirstReady
                && !leaderReady.Released
                && !duplicateLeaderReady.Accepted
                && followerReady.Accepted
                && followerReady.Released
                && followerReady.Participants.Count == 2
                && !duplicateReady.Accepted,
                ref failures);

            var timeoutGeneration =
                loadingRoom.RegisterLoadingProjection(
                    leaderRun,
                    projectionId: 2,
                    loadingRoster,
                    out var timeoutGenerationStarted);
            loadingRoom.RegisterLoadingProjection(
                followerRun,
                projectionId: 2,
                loadingRoster,
                out _);
            loadingRoom.MarkLoadingReady(leaderRun, loadingRoster);
            var timeoutRelease = loadingRoom.ForceLoadingCompletion(
                timeoutGeneration,
                new[] { leaderRun });
            var staleTimeout = loadingRoom.ForceLoadingCompletion(
                loadingGeneration,
                loadingRoster);
            Check(
                "dungeon loading timeout identifies missing runs and releases ready survivors",
                timeoutGeneration > loadingGeneration
                && timeoutGenerationStarted
                && timeoutRelease.Accepted
                && timeoutRelease.Released
                && timeoutRelease.ReadyParticipants.Count == 1
                && timeoutRelease.ReadyParticipants[0].Equals(leaderRun)
                && timeoutRelease.MissingParticipants.Count == 1
                && timeoutRelease.MissingParticipants[0].Equals(followerRun)
                && !staleTimeout.Accepted,
                ref failures);

            var lateTimedOutProjection =
                loadingRoom.RegisterLoadingProjection(
                    followerRun,
                    projectionId: 2,
                    loadingRoster,
                    out var lateGenerationStarted);
            var nextProjection = loadingRoom.RegisterLoadingProjection(
                leaderRun,
                projectionId: 3,
                loadingRoster,
                out var nextGenerationStarted);
            Check(
                "late producers cannot reopen a completed loading generation",
                lateTimedOutProjection == 0
                && !lateGenerationStarted
                && nextProjection > timeoutGeneration
                && nextGenerationStarted,
                ref failures);

            var projectionInstance = new DungeonInstance(1, 0);
            var projectionRun = new DungeonRun(
                projectionInstance,
                runId: 301,
                runGeneration: 1,
                DungeonRunState.Active);
            var projectionRoomA = projectionInstance.GetOrCreateRoom(
                new RoomKey(0, 0, -1),
                roomId => new DungeonInstanceRoom(
                    roomId,
                    new RoomKey(0, 0, -1),
                    new GameWorld.Dungeon.MazeSumInfo
                    {
                        Monsters = new List<GameWorld.Dungeon.MonsterSumInfo>(),
                    },
                    seed: 1),
                out _);
            var projectionRoomB = projectionInstance.GetOrCreateRoom(
                new RoomKey(1, 0, -1),
                roomId => new DungeonInstanceRoom(
                    roomId,
                    new RoomKey(1, 0, -1),
                    new GameWorld.Dungeon.MazeSumInfo
                    {
                        Monsters = new List<GameWorld.Dungeon.MonsterSumInfo>(),
                    },
                    seed: 2),
                out _);
            var firstRunProjection = projectionRun.TryClaimLoadingProjection(10);
            var firstRunProjectionStarted =
                projectionRun.TryBeginLoadingProjection(10);
            var firstProjectionCancellationCaptured =
                projectionRun.TryCaptureLoadingProjectionCancellation(
                    10,
                    out var firstProjectionCancellation);
            if (firstRunProjection && firstRunProjectionStarted)
                projectionRun.SetCurrentRoom(projectionRoomA);
            var newerRunProjection = projectionRun.TryClaimLoadingProjection(11);
            var newerRunProjectionStarted =
                projectionRun.TryBeginLoadingProjection(11);
            if (newerRunProjection && newerRunProjectionStarted)
                projectionRun.SetCurrentRoom(projectionRoomB);
            var duplicateRunProjectionStarted =
                projectionRun.TryBeginLoadingProjection(11);
            var staleRunProjection = projectionRun.TryClaimLoadingProjection(10);
            if (staleRunProjection)
                projectionRun.SetCurrentRoom(projectionRoomA);
            Check(
                "a stale producer cannot overwrite a newer run room projection",
                firstRunProjection
                && firstRunProjectionStarted
                && firstProjectionCancellationCaptured
                && firstProjectionCancellation.IsCancellationRequested
                && newerRunProjection
                && newerRunProjectionStarted
                && !duplicateRunProjectionStarted
                && !staleRunProjection
                && projectionRun.CurrentRoomInstanceId
                    == projectionRoomB.RoomInstanceId
                && projectionRun.IsCurrentLoadingProjection(11),
                ref failures);

            var canceledProjectionRun = new DungeonRun(
                new DungeonInstance(1, 0),
                runId: 302,
                runGeneration: 1,
                DungeonRunState.Active);
            var slowProjectionClaimed =
                canceledProjectionRun.TryClaimLoadingProjection(20);
            var slowProjectionCancellationCaptured =
                canceledProjectionRun.TryCaptureLoadingProjectionCancellation(
                    20,
                    out var slowProjectionCancellation);
            var slowProjectionCanceled =
                canceledProjectionRun.TryCancelLoadingProjection(20);
            var canceledProjectionRestarted =
                canceledProjectionRun.TryBeginLoadingProjection(20);
            var nextProjectionClaimed =
                canceledProjectionRun.TryClaimLoadingProjection(21);
            Check(
                "timeout cancellation fences the run until cleanup",
                slowProjectionClaimed
                && slowProjectionCancellationCaptured
                && slowProjectionCanceled
                && slowProjectionCancellation.IsCancellationRequested
                && !canceledProjectionRestarted
                && !nextProjectionClaimed
                && canceledProjectionRun.IsLoadingProjectionCanceled(20),
                ref failures);

            const uint sharedRoomSeed = 0x12345678;
            var leaderDropSeed = DropService.DeriveParticipantDropSeed(
                sharedRoomSeed,
                partyDungeonInstanceId: 7,
                roomInstanceId: 11,
                characterId: 10038);
            var followerDropSeed = DropService.DeriveParticipantDropSeed(
                sharedRoomSeed,
                partyDungeonInstanceId: 7,
                roomInstanceId: 11,
                characterId: 10039);
            var sharedRoomLcg = new DnfLcg(sharedRoomSeed);
            _ = new DnfLcg(leaderDropSeed).Next();
            Check(
                "party members keep the shared room seed but receive stable independent drop streams",
                leaderDropSeed
                    == DropService.DeriveParticipantDropSeed(
                        sharedRoomSeed,
                        partyDungeonInstanceId: 7,
                        roomInstanceId: 11,
                        characterId: 10038)
                && leaderDropSeed != followerDropSeed
                && leaderDropSeed != DropService.DeriveParticipantDropSeed(
                    sharedRoomSeed,
                    partyDungeonInstanceId: 7,
                    roomInstanceId: 12,
                    characterId: 10038)
                && sharedRoomLcg.Seed == sharedRoomSeed,
                ref failures);

            var lifeInstance = new DungeonInstance(2, 0);
            var lifeLeader = new DungeonRunIdentity(
                lifeInstance.PartyDungeonInstanceId,
                runId: 401,
                runGeneration: 1);
            var lifeFollower = new DungeonRunIdentity(
                lifeInstance.PartyDungeonInstanceId,
                runId: 402,
                runGeneration: 1);
            var lifeRoster = new[] { lifeLeader, lifeFollower };
            lifeInstance.RegisterParticipantLife(lifeLeader);
            lifeInstance.RegisterParticipantLife(lifeFollower);
            var lifeNow = new DateTime(
                2026,
                8,
                25,
                0,
                0,
                0,
                DateTimeKind.Utc);
            var firstDeath = lifeInstance.MarkParticipantDead(
                lifeLeader,
                lifeRoster,
                lifeNow,
                TimeSpan.FromSeconds(10));
            var duplicateDeath = lifeInstance.MarkParticipantDead(
                lifeLeader,
                lifeRoster,
                lifeNow.AddSeconds(1),
                TimeSpan.FromSeconds(10));
            var fullWipe = lifeInstance.MarkParticipantDead(
                lifeFollower,
                lifeRoster,
                lifeNow.AddSeconds(2),
                TimeSpan.FromSeconds(10));
            var earlyCommit = lifeInstance.TryCommitPartyWipe(
                fullWipe.Generation,
                lifeRoster,
                lifeNow.AddSeconds(11));
            var revived = lifeInstance.MarkParticipantAlive(lifeFollower);
            var staleCommit = lifeInstance.TryCommitPartyWipe(
                fullWipe.Generation,
                lifeRoster,
                lifeNow.AddSeconds(20));
            var secondWipe = lifeInstance.MarkParticipantDead(
                lifeFollower,
                lifeRoster,
                lifeNow.AddSeconds(21),
                TimeSpan.FromSeconds(10));
            var committed = lifeInstance.TryCommitPartyWipe(
                secondWipe.Generation,
                lifeRoster,
                lifeNow.AddSeconds(31));
            var duplicateCommit = lifeInstance.TryCommitPartyWipe(
                secondWipe.Generation,
                lifeRoster,
                lifeNow.AddSeconds(32));
            Check(
                "party death waits for every member, cancels on revive, and commits one generation after ten seconds",
                firstDeath.Changed
                && !firstDeath.WipeStarted
                && !duplicateDeath.Changed
                && !duplicateDeath.WipeStarted
                && fullWipe.WipeStarted
                && !earlyCommit
                && revived.Changed
                && revived.WipeCancelled
                && !staleCommit
                && secondWipe.WipeStarted
                && secondWipe.Generation > fullWipe.Generation
                && committed
                && !duplicateCommit,
                ref failures);
            var deathState = DungeonCombatHandler
                .BuildParticipantLifeStateBody(10039, state: 0);
            var reviveState = DungeonCombatHandler
                .BuildParticipantLifeStateBody(10039, state: 1);
            Check(
                "death and revive reuse the exact four-byte A21 DIE_STATE body",
                deathState.Length == 4
                && BitConverter.ToUInt16(deathState, 0) == 10039
                && deathState[2] == 0
                && deathState[3] == 0
                && reviveState.Length == 4
                && BitConverter.ToUInt16(reviveState, 0) == 10039
                && reviveState[2] == 1
                && reviveState[3] == 0,
                ref failures);

            var exitInstance = new DungeonInstance(3, 0);
            var exitingAlive = new DungeonRunIdentity(
                exitInstance.PartyDungeonInstanceId,
                runId: 501,
                runGeneration: 1);
            var remainingDead = new DungeonRunIdentity(
                exitInstance.PartyDungeonInstanceId,
                runId: 502,
                runGeneration: 1);
            var exitRoster = new[] { exitingAlive, remainingDead };
            exitInstance.RegisterParticipantLife(exitingAlive);
            exitInstance.RegisterParticipantLife(remainingDead);
            var waitingDeath = exitInstance.MarkParticipantDead(
                remainingDead,
                exitRoster,
                lifeNow,
                DungeonInstance.PartyWipeDelay);
            var exitTriggeredWipe = exitInstance.RemoveParticipantLife(
                exitingAlive,
                new[] { remainingDead },
                lifeNow.AddSeconds(1),
                DungeonInstance.PartyWipeDelay);
            Check(
                "removing the final alive participant starts one wipe timer for the dead survivors",
                !waitingDeath.WipeStarted
                && exitTriggeredWipe.WipeStarted
                && exitTriggeredWipe.Generation > 0
                && exitInstance.TryCommitPartyWipe(
                    exitTriggeredWipe.Generation,
                    new[] { remainingDead },
                    exitTriggeredWipe.DeadlineUtc),
                ref failures);

            Console.WriteLine($"A21_PARTY_PROTOCOL failures={failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void Check(string name, bool condition, ref int failures)
        {
            Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {name}");
            if (!condition)
                failures++;
        }
    }
}
