using System;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Characters;
using DfoServer.Game.Pvp;
using DfoServer.Game.SelectCharacter;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;

namespace DfoServer.SelfTests
{
    public static partial class A21PvpRoomProtocolSelfTest
    {
        private static async Task CheckDisconnectGuardrailsAsync(IGameDatabase database)
        {
            // Exercise the real SessionEnding callback and publication queues.
            // This proves protocol ordering; native client survival needs live QA.
            foreach (var playerCount in new[] { 2, 3 })
            foreach (var ownerDisconnects in new[] { false, true })
            foreach (var cleanupSignal in new[] { "ack", "late-ack", "ready", "reused-uid", "later-disconnect", "normal-exit" })
            {
                if (playerCount == 2 && (cleanupSignal == "later-disconnect" || cleanupSignal == "normal-exit"))
                    continue;
                var sessions = new SessionDirectory();
                var characters = new SqliteCharacterRepository(database);
                var source = new SqliteSelectCharacterDataSource(database, characters);
                var town = new TownHandler(characters, source, null, sessions, null, null, null, database);
                var rooms = new FreeDuelRoomRegistry();
                using var pvp = new PvpRoomHandler(sessions, town.BuildFullUserInfoPacket,
                    new CharacterTransitionCoordinator(sessions), () => true, rooms,
                    town.AnnouncePvpTownArrivalWithinTransitionAsync, database: database,
                    settlementAckTimeout: TimeSpan.FromSeconds(30));
                using var owner = await Peer.CreateAsync(database, sessions, 5001, 5001, 0x4001, 10068);
                using var member = await Peer.CreateAsync(database, sessions, 6001, 5002, 0x4002, 10068);
                using var third = await Peer.CreateAsync(database, sessions, 7001, 5003, 0x4003, 10068);
                await pvp.HandleLobbyReadyAsync(owner.Session);
                await pvp.HandleLobbyReadyAsync(member.Session);
                await pvp.HandleLobbyReadyAsync(third.Session);
                await pvp.HandleMakeRoom(owner.Session, Header(CmdPacketTypeA21.MAKE_PVP_ROOM), MakeRequest());
                var room = rooms.SnapshotForListener(10068).Single();
                await pvp.HandleSetTeamMode(owner.Session, Header(CmdPacketTypeA21.SET_PVP_TEAM_MODE), new byte[] { 1 });
                await pvp.HandleEnterRoom(member.Session, Header(CmdPacketTypeA21.ENTER_PVP_ROOM), EnterRequest(room.RoomId));
                if (playerCount == 3)
                {
                    await pvp.HandleEnterRoom(third.Session, Header(CmdPacketTypeA21.ENTER_PVP_ROOM), EnterRequest(room.RoomId));
                    await pvp.HandleSetReadyState(third.Session, Header(CmdPacketTypeA21.SET_PVP_READY_STATE), new byte[] { 1 });
                }
                await pvp.HandleSetReadyState(member.Session, Header(CmdPacketTypeA21.SET_PVP_READY_STATE), new byte[] { 1 });
                await pvp.HandleSetReadyState(owner.Session, Header(CmdPacketTypeA21.SET_PVP_READY_STATE), new byte[] { 1 });
                owner.Drain(); member.Drain(); third.Drain();
                var departed = ownerDisconnects ? owner : member;
                var survivor = ownerDisconnects ? member : owner;
                await sessions.UnregisterAsync(departed.Session.Player.CharacterId, departed.Session);
                var packets = survivor.Drain();
                if (playerCount == 2)
                    Require(!third.Drain().Any(packet => Type(packet) == (ushort)NotiPacketTypeA21.DIE_PVP_CHARACTER
                        || Type(packet) == (ushort)NotiPacketTypeA21.REQ_PVP_RANK),
                        "room departure cannot send combat death/rank events to unrelated lobby clients");
                else
                {
                    Require(rooms.SnapshotForListener(10068).Single().SettlementPhase == FreeDuelRoom.CombatSettlementPhase
                        && !packets.Any(packet => Type(packet) == (ushort)NotiPacketTypeA21.REQ_PVP_RANK),
                        "one of three combatants disconnecting preserves the remaining live match");
                    var uid=survivor.Session.Player.UserId;
                    await pvp.HandleDiePvpCharacter(third.Session, Header(CmdPacketTypeA21.DIE_PVP_CHARACTER),
                        new byte[] { 3,0x40,(byte)uid,(byte)(uid>>8),0 });
                    packets=packets.Concat(survivor.Drain()).ToList();
                    third.Drain();
                }
                Require(packets.Any(packet => Type(packet) == (ushort)NotiPacketTypeA21.DIE_PVP_CHARACTER)
                    && packets.Any(packet => Type(packet) == (ushort)NotiPacketTypeA21.REQ_PVP_RANK),
                    "disconnect starts normal death and rank settlement for either departing seat");
                Require(!packets.Any(packet => Type(packet) == (ushort)NotiPacketTypeA21.USER_LEAVE
                    || Type(packet) == (ushort)NotiPacketTypeA21.PVP_SEAT_STATE),
                    "combat departure cannot delete native identity or seats before result acknowledgement");
                await pvp.HandleReportedUdpEndpointChanged(survivor.Session);
                Require(survivor.Drain().Any(packet => Type(packet) == (ushort)NotiPacketTypeA21.USER_UDP_IP_PORT),
                    "per-UID UDP endpoint refresh remains live while old result identities are retained");
                await pvp.HandlePvpRankResponse(survivor.Session, Header(CmdPacketTypeA21.RES_PVP_RANK), new byte[70]);
                if (playerCount == 3)
                    await pvp.HandlePvpRankResponse(third.Session, Header(CmdPacketTypeA21.RES_PVP_RANK), new byte[70]);
                var result = FindBody(survivor.Drain(), NotiPacketTypeA21.END_PVP);
                Require(result[10] == playerCount && result.Length == 26+19*playerCount,
                    "disconnect result keeps every frozen participant despite removed live memberships");
                if (cleanupSignal == "late-ack" || cleanupSignal == "ready" ||
                    cleanupSignal == "later-disconnect" || cleanupSignal == "normal-exit")
                {
                    var ending = rooms.SnapshotForListener(10068).Single();
                    Require(rooms.TryForceEndSettlement(ending.RoomId, ending.GenerationId,
                            ending.MatchGeneration, out _),
                        "ACK watchdog can return the server room to waiting before native result exit");
                }
                using var reconnected = cleanupSignal == "reused-uid"
                    ? await Peer.CreateAsync(database, sessions, departed.Session.Player.CharacterId,
                        departed.Session.Account.AccountId, departed.Session.Player.UserId, 10068)
                    : null;
                if (reconnected != null)
                    await pvp.HandleLobbyReadyAsync(reconnected.Session);
                if (cleanupSignal == "later-disconnect")
                    await sessions.UnregisterAsync(third.Session.Player.CharacterId, third.Session);
                else if (cleanupSignal == "normal-exit")
                {
                    await pvp.HandleSetSeatState(third.Session, Header(CmdPacketTypeA21.SET_PVP_SEAT_STATE), new byte[] { 2, 0xFE });
                    Require(third.Session.Player.UserState == 0 &&
                        third.Drain().Any(packet => Type(packet) == (ushort)NotiPacketTypeA21.USER_LEAVE),
                        "town navigation also completes the leaving recipient's old UID cleanup");
                }
                Require(!survivor.Drain().Any(packet => Type(packet) == (ushort)NotiPacketTypeA21.USER_LEAVE
                    || Type(packet) == (ushort)NotiPacketTypeA21.PVP_SEAT_STATE),
                    "neither server waiting state nor relog deletes the native combat identity before result exit");
                if (playerCount == 2 && cleanupSignal == "ack")
                {
                    await sessions.UnregisterAsync(third.Session.Player.CharacterId, third.Session);
                    Require(survivor.Drain().Any(packet => Type(packet) == (ushort)NotiPacketTypeA21.USER_LEAVE &&
                        BitConverter.ToUInt16(packet, 15) == third.Session.Player.UserId),
                        "unrelated lobby UID cleanup is not blocked by a battle departure");
                }
                if (cleanupSignal == "ready")
                    await pvp.HandleSetReadyState(survivor.Session, Header(CmdPacketTypeA21.SET_PVP_READY_STATE), new byte[] { 1 });
                else
                    await pvp.HandleEndPvpResult(survivor.Session, Header(CmdPacketTypeA21.END_PVP_RESULT), Array.Empty<byte>());
                if (playerCount == 3 && cleanupSignal != "later-disconnect" && cleanupSignal != "normal-exit")
                    await pvp.HandleEndPvpResult(third.Session, Header(CmdPacketTypeA21.END_PVP_RESULT), Array.Empty<byte>());
                packets = survivor.Drain();
                Require(packets.Any(packet => Type(packet) == (ushort)NotiPacketTypeA21.USER_LEAVE) == (reconnected == null)
                    && rooms.SnapshotForListener(10068).Single().RoomState == FreeDuelRoom.WaitingRoomState,
                    "result exit cleans only the departed session identity and keeps a relogged UID intact");
                if (cleanupSignal == "later-disconnect")
                    Require(packets.Count(packet => Type(packet) == (ushort)NotiPacketTypeA21.USER_LEAVE) == 2,
                        "departures after the watchdog merge into this recipient's same-match cleanup");
            }
        }
    }
}
