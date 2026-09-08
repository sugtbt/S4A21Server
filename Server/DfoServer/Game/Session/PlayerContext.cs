using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using DfoServer.Game.Characters;
using DfoServer.Network;

namespace DfoServer.Game.Session
{
    /// <summary>
    /// One validated, immutable client SET_UDP_IP_PORT report. Keeping the
    /// fields together prevents readers from observing a mixture of two
    /// endpoint reports while a session refreshes its UDP mapping.
    /// </summary>
    public sealed class ReportedUdpEndpointState
    {
        // RFC 791 permits 576-byte IPv4 datagrams without path-specific
        // knowledge. The 2014 client reports the Ethernet ceiling (1500);
        // larger values are not safe for the legacy P2P wire path.
        public const uint MinimumMtu = 576;
        public const uint MaximumMtu = 1500;

        internal ReportedUdpEndpointState(
            byte natType,
            IPAddress innerIpv4,
            IPAddress outerIpv4,
            ushort port,
            uint mtu)
        {
            NatType = natType;
            InnerIpv4 = CopyAndValidateIpv4(
                innerIpv4,
                nameof(innerIpv4));
            OuterIpv4 = CopyAndValidateIpv4(
                outerIpv4,
                nameof(outerIpv4));
            if (port == 0)
                throw new ArgumentOutOfRangeException(nameof(port));
            if (mtu < MinimumMtu || mtu > MaximumMtu)
                throw new ArgumentOutOfRangeException(nameof(mtu));

            Port = port;
            Mtu = mtu;
        }

        public byte NatType { get; }
        public IPAddress InnerIpv4 { get; }
        public IPAddress OuterIpv4 { get; }
        public ushort Port { get; }
        public uint Mtu { get; }

        internal static bool IsUsableIpv4(IPAddress address)
        {
            if (address == null
                || address.AddressFamily != AddressFamily.InterNetwork)
            {
                return false;
            }

            var bytes = address.GetAddressBytes();
            // Reject unspecified/class-0 and multicast/reserved/class-E
            // ranges. Private, CGNAT, link-local and loopback addresses remain
            // syntactically valid reports; reachability is decided later by
            // the direct/relay path rather than by this wire parser.
            return bytes.Length == 4
                && bytes[0] != 0
                && bytes[0] < 224;
        }

        private static IPAddress CopyAndValidateIpv4(
            IPAddress address,
            string parameterName)
        {
            if (!IsUsableIpv4(address))
            {
                throw new ArgumentException(
                    "A usable IPv4 address is required.",
                    parameterName);
            }

            return new IPAddress(address.GetAddressBytes());
        }
    }

    /// <summary>
    /// </summary>
    public partial class PlayerContext
    {
        /// <summary>
        /// Complete client-reported P2P endpoint state from
        /// CS 0x0002 SET_UDP_IP_PORT.
        /// </summary>
        private ReportedUdpEndpointState _reportedUdpEndpoint;

        /// <summary>
        /// True only after a complete, validated SET_UDP_IP_PORT report.
        /// A standalone fallback port must never imply endpoint readiness.
        /// </summary>
        public bool HasReportedUdpEndpoint =>
            Volatile.Read(ref _reportedUdpEndpoint) != null;

        /// <summary>
        /// Returns the current immutable report, or null before the client has
        /// reported one. Read this once when multiple endpoint fields must be
        /// used together.
        /// </summary>
        public ReportedUdpEndpointState ReportedUdpEndpoint =>
            Volatile.Read(ref _reportedUdpEndpoint);

        /// <summary>
        /// Compatibility projection used by the existing party builders.
        /// Zero means the client has not supplied a complete valid report.
        /// </summary>
        public ushort P2pPort =>
            Volatile.Read(ref _reportedUdpEndpoint)?.Port ?? (ushort)0;

        /// <summary>
        /// Atomically replaces the session's current client-reported endpoint.
        /// </summary>
        public void UpdateReportedUdpEndpoint(
            byte natType,
            IPAddress innerIpv4,
            IPAddress outerIpv4,
            ushort port,
            uint mtu)
        {
            var next = new ReportedUdpEndpointState(
                natType,
                innerIpv4,
                outerIpv4,
                port,
                mtu);
            Volatile.Write(ref _reportedUdpEndpoint, next);
        }

        /// <summary>
        /// Clears endpoint readiness for a fresh transport generation.
        /// Character hydration intentionally does not call this because the
        /// endpoint belongs to the TCP client session, not to one character.
        /// </summary>
        public void ResetReportedUdpEndpoint()
        {
            Volatile.Write(ref _reportedUdpEndpoint, null);
        }

        /// <summary>
        /// </summary>
        public void HydrateFrom(CharacterRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            HydrateFrom(
                record,
                GameChannelSpawnPolicy.Resolve(
                    listenerGamePort: 0,
                    persistedTownId: record.TownId));
        }

        public void HydrateFrom(
            CharacterRecord record,
            GameChannelSpawn spawn)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (spawn == null) throw new ArgumentNullException(nameof(spawn));

            HydrateIdentityFrom(record);
            ResetTownMovementSequence();
            {
                CurTownId = spawn.TownId;
                CurAreaId = spawn.AreaId;
                CurPosX = spawn.X;
                CurPosY = spawn.Y;
                CurDirection = spawn.Direction;
                CurAreaState = spawn.AreaState;
            }

            if (record.Appearance != null && record.Appearance.Length > 0)
                AppearanceEntries = record.Appearance;

            Subtype0Tail = record.Subtype0Tail;
        }

        // Kept separate from PVF-backed town lookup so persistent identity/state
        // hydration can be verified without loading Script.pvf.
        internal void HydrateIdentityFrom(CharacterRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            CharacterId = record.CharacterId;
            Name = record.Name ?? Name;
            UserId = (ushort)record.CharacterId;
            DungeonSceneUniqueId = 0;
            Job = record.Job;
            GrowType = record.GrowType;
            GrowupChangeCount = record.GrowupChangeCount;
            Level = record.Level == 0 ? Level : record.Level;
            Exp = record.Exp;
            UserState = record.UserState;
        }
    }
}
