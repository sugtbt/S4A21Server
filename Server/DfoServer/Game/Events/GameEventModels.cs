using System;
using System.Collections.Generic;

namespace DfoServer.Game.Events
{
    internal sealed class GameEventInfoEntry
    {
        public ushort EventId { get; set; }

        public uint Unknown0 { get; set; }

        public string StartNotice { get; set; } = string.Empty;

        public string EndNotice { get; set; } = string.Empty;

        public bool HasDetail { get; set; }

        public byte FlagA { get; set; }

        public byte FlagB { get; set; }

        public string Title { get; set; } = string.Empty;

        public string ShortName { get; set; } = string.Empty;

        public string ReservedOrIcon { get; set; } = string.Empty;

        public uint StartUnixTime { get; set; }

        public uint EndUnixTime { get; set; }

        public string LinkKey { get; set; } = string.Empty;

        public string Description { get; set; } = string.Empty;

        public bool DetailEnabled { get; set; }
    }

    internal sealed class GameEventExtraInfoEntry
    {
        public ushort EventId { get; set; }

        public IReadOnlyList<uint> Parameters { get; set; } = Array.Empty<uint>();
    }

    internal sealed class GameEventInfoSnapshot
    {
        public IReadOnlyList<GameEventInfoEntry> Events { get; set; } =
            Array.Empty<GameEventInfoEntry>();

        public IReadOnlyList<GameEventExtraInfoEntry> ExtraEntries { get; set; } =
            Array.Empty<GameEventExtraInfoEntry>();
    }
}
