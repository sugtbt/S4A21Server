using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DfoServer.Game.Pvp;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Pvp;

namespace DfoServer.Network.Handlers
{
    internal sealed partial class PvpRoomHandler
    {
        // Deferred wire projections, never a second membership/settlement owner.
        // Only _roomPublicationGate may access this dictionary. Native battle
        // actors still use their original UID records until result-scene exit.
        private sealed class DeferredPvpDeparture
        {
            internal FreeDuelRoom Match;
            internal readonly Dictionary<byte, (ushort UserId, Guid SessionId)> Seats = new();
        }

        private readonly Dictionary<Guid, DeferredPvpDeparture> _deferredPvpDepartures = new();

        private byte[][] FilterDeferredDeparturePacketsUnderGate(
            EnhancedClientSession target, byte[][] packets)
        {
            if (!_deferredPvpDepartures.TryGetValue(target.SessionId, out var pending))
                return packets;
            if (target.Player == null ||
                !_rooms.TryGetRoomForMember(target.Player.CharacterId, target.SessionId, out var room, out _) ||
                room.GenerationId != pending.Match.GenerationId ||
                room.MatchGeneration != pending.Match.MatchGeneration)
            {
                _deferredPvpDepartures.Remove(target.SessionId);
                return packets;
            }

            var deliver = new List<byte[]>(packets.Length);
            foreach (var packet in packets)
            {
                // These are builder-owned outbound envelopes, with a 15B
                // server-to-client header and u16 room/UID first in the body.
                if (packet.Length >= 17 && packet[0] == 0)
                {
                    var type = (NotiPacketTypeA21)BitConverter.ToUInt16(packet, 1);
                    var id = BitConverter.ToUInt16(packet, 15);
                    if (type == NotiPacketTypeA21.PVP_SEAT_STATE && id == pending.Match.RoomId)
                        continue;
                    if (type == NotiPacketTypeA21.USER_LEAVE && RememberDeferredUidUnderGate(pending, id))
                        continue;
                }
                deliver.Add(packet);
            }
            return deliver.ToArray();
        }

        private static bool RememberDeferredUidUnderGate(DeferredPvpDeparture pending, ushort userId)
        {
            for (byte seat = 0; seat < FreeDuelRoom.SeatCount; seat++)
            {
                if (pending.Seats.TryGetValue(seat, out var departed) && departed.UserId == userId)
                    return true;
                if (pending.Match.IsOccupiedSeat(seat) && pending.Match.GetSeatUserId(seat) == userId)
                {
                    pending.Seats[seat] = (userId, pending.Match.GetSeatSessionId(seat));
                    return true;
                }
            }
            return false;
        }

        private List<byte[]> BuildDeferredUserLeavesUnderGate(DeferredPvpDeparture pending)
        {
            var packets = new List<byte[]>();
            foreach (var departed in pending.Seats.Values)
            {
                // A relog can reuse the UID. Never remove its new record.
                if (!GetReadyListenerTargets(pending.Match.ListenerPort).Any(target =>
                    target.Player.UserId == departed.UserId && target.SessionId != departed.SessionId))
                    packets.Add(TownHandler.BuildUserLeavePacket(departed.UserId));
            }
            return packets;
        }

        private void DeferPvpDepartureUnderGate(EnhancedClientSession target, FreeDuelRoom room,
            byte seat, ushort userId, Guid sessionId)
        {
            if (!_deferredPvpDepartures.TryGetValue(target.SessionId, out var pending)
                || pending.Match.GenerationId != room.GenerationId
                || pending.Match.MatchGeneration != room.MatchGeneration)
            {
                pending = new DeferredPvpDeparture { Match = room };
                _deferredPvpDepartures[target.SessionId] = pending;
            }
            pending.Seats[seat] = (userId, sessionId);
        }

        private async Task FlushDeferredPvpDeparturesAsync(EnhancedClientSession session)
        {
            Task publication = Task.CompletedTask;
            await _roomPublicationGate.WaitAsync();
            try
            {
                if (session?.Player == null
                    || !_deferredPvpDepartures.TryGetValue(session.SessionId, out var pending))
                    return;
                if (!_rooms.TryGetRoomForMember(session.Player.CharacterId, session.SessionId, out var room, out _)
                    || room.GenerationId != pending.Match.GenerationId
                    || room.MatchGeneration != pending.Match.MatchGeneration)
                {
                    _deferredPvpDepartures.Remove(session.SessionId);
                    return;
                }
                if (room.SettlementPhase != FreeDuelRoom.AwaitingEndSettlementPhase
                    && room.RoomState != FreeDuelRoom.WaitingRoomState)
                    return;
                if (!_characterTransitions.IsCurrent(session)) return;
                var packets = new List<byte[]>();
                // Seat changes (including normal exits and kicks) may have
                // accumulated while the server watchdog was already waiting.
                // Rebuild all eight from the current registry snapshot.
                for (var seat = 0; seat < FreeDuelRoom.SeatCount; seat++)
                {
                    packets.Add(GamePacketEnvelopeBuilder.Build(0, SeatStateNotificationType,
                        PvpRoomNotificationBuilder.BuildSeatStateBody(room, seat)));
                }
                packets.AddRange(BuildDeferredUserLeavesUnderGate(pending));
                packets.Add(GamePacketEnvelopeBuilder.Build(0, RoomStateNotificationType,
                    PvpRoomNotificationBuilder.BuildRoomStateBody(room)));
                _deferredPvpDepartures.Remove(session.SessionId);
                publication = _requiredPublication.QueueRequired(new[] { session }, room.ListenerPort, packets.ToArray());
            }
            finally { _roomPublicationGate.Release(); }
            await publication;
        }
    }
}
