using System;

namespace DfoServer.Game.Dungeon
{
    public enum DungeonClearPresentationKind
    {
        Standard,
        BloodAltar,
        Tournament,
        LicensedDungeon,
    }

    public static class DungeonClearPresentationPolicy
    {
        public static bool UsesStandardResultProjection(
            DungeonClearPresentationKind kind)
            => kind == DungeonClearPresentationKind.Standard;

        public static bool UsesCommonExperienceAuthority(
            DungeonClearPresentationKind kind)
            => kind != DungeonClearPresentationKind.Tournament;

        public static bool CompletesAtClearCommit(
            DungeonClearPresentationKind kind)
            => kind == DungeonClearPresentationKind.Tournament;
    }

    public sealed class DungeonEventEnvelope
    {
        public DungeonEventEnvelope(
            Guid sourceEventId,
            DungeonRunIdentity runIdentity,
            long? roomInstanceId,
            int sourcePlayerId,
            int? affectedPlayerId,
            long? sourceActorId,
            int? sourceActorCode,
            string cause,
            long occurredTick)
        {
            if (sourceEventId == Guid.Empty)
                throw new ArgumentException("A dungeon event requires a stable ID.", nameof(sourceEventId));

            SourceEventId = sourceEventId;
            RunIdentity = runIdentity;
            RoomInstanceId = roomInstanceId;
            SourcePlayerId = sourcePlayerId;
            AffectedPlayerId = affectedPlayerId;
            SourceActorId = sourceActorId;
            SourceActorCode = sourceActorCode;
            Cause = cause ?? string.Empty;
            OccurredTick = occurredTick;
        }

        public Guid SourceEventId { get; }
        public DungeonRunIdentity RunIdentity { get; }
        public DungeonInstanceIdentity InstanceIdentity =>
            RunIdentity.InstanceIdentity;
        public DungeonParticipantRunIdentity ParticipantRunIdentity =>
            RunIdentity.ParticipantIdentity;
        public long PartyDungeonInstanceId => RunIdentity.PartyDungeonInstanceId;
        public long RunId => RunIdentity.RunId;
        public long RunGeneration => RunIdentity.RunGeneration;
        public long? RoomInstanceId { get; }
        public DungeonRoomIdentity RoomIdentity => RoomInstanceId.HasValue
            ? new DungeonRoomIdentity(InstanceIdentity, RoomInstanceId.Value)
            : default;
        public int SourcePlayerId { get; }
        public int? AffectedPlayerId { get; }
        public long? SourceActorId { get; }
        public int? SourceActorCode { get; }
        public string Cause { get; }
        public long OccurredTick { get; }

        public DungeonEventEnvelope ForAffectedPlayer(
            DungeonRunIdentity affectedRun,
            long? affectedRoomInstanceId,
            int affectedPlayerId)
        {
            return new DungeonEventEnvelope(
                SourceEventId,
                affectedRun,
                affectedRoomInstanceId,
                SourcePlayerId,
                affectedPlayerId,
                SourceActorId,
                SourceActorCode,
                Cause,
                OccurredTick);
        }

        public static DungeonEventEnvelope Create(
            DungeonRun run,
            int sourcePlayerId,
            string cause,
            long? sourceActorId = null,
            int? sourceActorCode = null,
            Guid sourceEventId = default)
        {
            if (run == null)
                throw new ArgumentNullException(nameof(run));

            return new DungeonEventEnvelope(
                sourceEventId == Guid.Empty ? Guid.NewGuid() : sourceEventId,
                run.CaptureIdentity(),
                run.CurrentRoomInstanceId > 0 ? run.CurrentRoomInstanceId : null,
                sourcePlayerId,
                sourcePlayerId,
                sourceActorId,
                sourceActorCode,
                cause,
                Environment.TickCount64);
        }
    }

    public sealed class DungeonClearIntent
    {
        public DungeonClearIntent(
            DungeonEventEnvelope source,
            string reason,
            int bossCode,
            DungeonClearPresentationKind presentationKind =
                DungeonClearPresentationKind.Standard)
        {
            Source = source ?? throw new ArgumentNullException(nameof(source));
            Reason = reason ?? string.Empty;
            BossCode = bossCode;
            PresentationKind = presentationKind;
        }

        public DungeonEventEnvelope Source { get; }
        public string Reason { get; }
        public int BossCode { get; }
        public DungeonClearPresentationKind PresentationKind { get; }
    }

    public sealed class DungeonClearedFact
    {
        internal DungeonClearedFact(DungeonClearIntent intent)
        {
            Source = intent.Source;
            Reason = intent.Reason;
            BossCode = intent.BossCode;
            PresentationKind = intent.PresentationKind;
        }

        public Guid SourceEventId => Source.SourceEventId;
        public DungeonEventEnvelope Source { get; }
        public string Reason { get; }
        public int BossCode { get; }
        public DungeonClearPresentationKind PresentationKind { get; }
    }
}
