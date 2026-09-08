using System;
using System.Collections.Generic;
using System.Threading;

namespace DfoServer.Game.Dungeon
{
    internal readonly struct DungeonPartySelectionParticipant
    {
        internal DungeonPartySelectionParticipant(
            ushort userId,
            int characterId,
            Guid sessionId,
            byte slotIndex)
        {
            UserId = userId;
            CharacterId = characterId;
            SessionId = sessionId;
            SlotIndex = slotIndex;
        }

        internal ushort UserId { get; }
        internal int CharacterId { get; }
        internal Guid SessionId { get; }
        internal byte SlotIndex { get; }
    }

    // Frozen when the leader opens the dungeon-selection screen. Every
    // projected member carries this same object so later SELECT/cancel work
    // cannot accidentally consume a newer party generation.
    internal sealed class DungeonPartySelectionCohort
    {
        private readonly DungeonPartySelectionParticipant[] _participants;

        internal DungeonPartySelectionCohort(
            long projectionId,
            int partyId,
            ushort leaderUserId,
            IReadOnlyList<DungeonPartySelectionParticipant> participants,
            bool returnToTownOnEntryReject = false)
        {
            if (projectionId <= 0)
                throw new ArgumentOutOfRangeException(nameof(projectionId));
            if (partyId <= 0)
                throw new ArgumentOutOfRangeException(nameof(partyId));
            if (leaderUserId == 0)
                throw new ArgumentOutOfRangeException(nameof(leaderUserId));
            if (participants == null
                || participants.Count <= 1
                || participants.Count > Game.Party.PartyConstants.MaxMembers)
            {
                throw new ArgumentOutOfRangeException(nameof(participants));
            }

            ProjectionId = projectionId;
            PartyId = partyId;
            LeaderUserId = leaderUserId;
            ReturnToTownOnEntryReject = returnToTownOnEntryReject;
            _participants = new DungeonPartySelectionParticipant[
                participants.Count];
            for (var i = 0; i < participants.Count; i++)
                _participants[i] = participants[i];
        }

        internal long ProjectionId { get; }
        internal int PartyId { get; }
        internal ushort LeaderUserId { get; }
        internal bool ReturnToTownOnEntryReject { get; }
        // One shared gate covers the short selection-to-run transition and a
        // concurrent selection return. It is intentionally cohort-scoped:
        // member-local locks cannot make the two operations atomic together.
        internal SemaphoreSlim TransitionGate { get; } =
            new SemaphoreSlim(1, 1);
        internal IReadOnlyList<DungeonPartySelectionParticipant> Participants =>
            _participants;
    }

    internal readonly struct DungeonTownReturnAnchor
    {
        internal DungeonTownReturnAnchor(
            byte townId,
            byte areaId,
            short x,
            short y,
            byte direction,
            byte areaState)
        {
            TownId = townId;
            AreaId = areaId;
            X = x;
            Y = y;
            Direction = direction;
            AreaState = areaState;
        }

        internal byte TownId { get; }
        internal byte AreaId { get; }
        internal short X { get; }
        internal short Y { get; }
        internal byte Direction { get; }
        internal byte AreaState { get; }
        internal bool IsValid => TownId > 0;
    }

    // The selection screen has no DungeonRun. Give it an identity so stale
    // asynchronous return work cannot project into a later run.
    internal sealed class DungeonSelectionContext
    {
        private int _returnState;
        private int _partyProjectionComplete;
        private readonly object _circleEntrySyncRoot = new object();
        private int _circleDungeonId;
        private ushort _circleQuestId;

        internal DungeonSelectionContext(
            long selectionId,
            long runGeneration,
            DungeonTownReturnAnchor returnAnchor,
            bool isA21TutorialEntry,
            DungeonPartySelectionCohort partyCohort = null)
        {
            SelectionId = selectionId;
            RunGeneration = runGeneration;
            ReturnAnchor = returnAnchor;
            IsA21TutorialEntry = isA21TutorialEntry;
            PartyCohort = partyCohort;
        }

        internal long SelectionId { get; }
        internal long RunGeneration { get; }
        internal DungeonTownReturnAnchor ReturnAnchor { get; }
        internal bool IsA21TutorialEntry { get; }
        internal DungeonPartySelectionCohort PartyCohort { get; }
        internal bool IsReturning => Volatile.Read(ref _returnState) == 1;
        internal bool IsPartyProjectionComplete => PartyCohort == null
            || Volatile.Read(ref _partyProjectionComplete) == 1;

        internal bool TryBeginReturn() =>
            Interlocked.CompareExchange(ref _returnState, 1, 0) == 0;

        internal void CancelReturn() =>
            Interlocked.CompareExchange(ref _returnState, 0, 1);

        internal bool TryCompleteReturn() =>
            Interlocked.CompareExchange(ref _returnState, 2, 1) == 1;

        internal bool TryCompletePartyProjection()
        {
            if (PartyCohort == null)
                return true;
            if (IsReturning)
                return false;

            Interlocked.CompareExchange(ref _partyProjectionComplete, 1, 0);
            return Volatile.Read(ref _partyProjectionComplete) == 1
                && !IsReturning;
        }

        internal bool TryBindCircleEntry(int dungeonId, ushort circleQuestId)
        {
            if (dungeonId <= 0 || circleQuestId == 0 || IsReturning)
                return false;

            lock (_circleEntrySyncRoot)
            {
                if (IsReturning)
                    return false;

                _circleDungeonId = dungeonId;
                _circleQuestId = circleQuestId;
                return true;
            }
        }

        internal bool TryConsumeCircleEntry(
            int dungeonId,
            out ushort circleQuestId)
        {
            lock (_circleEntrySyncRoot)
            {
                var pendingDungeonId = _circleDungeonId;
                var pendingQuestId = _circleQuestId;
                _circleDungeonId = 0;
                _circleQuestId = 0;

                circleQuestId = pendingDungeonId == dungeonId
                    ? pendingQuestId
                    : (ushort)0;
                return circleQuestId != 0;
            }
        }
    }
}
