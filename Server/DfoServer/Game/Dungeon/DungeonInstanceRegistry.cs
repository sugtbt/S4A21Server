using System;
using System.Collections.Generic;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Dungeon
{
    internal sealed class DungeonInstanceRegistry : IDisposable
    {
        private sealed class Entry
        {
            internal int AccountId;
            internal int CharacterId;
            internal ushort ParticipantUserId;
            internal int PartyId;
            internal Guid ActiveSessionId;
            internal DungeonRun Run;
            internal DungeonRunIdentity RunIdentity;
            internal DungeonParticipantRoomIdentity DetachedRoomIdentity;
            internal long AttachmentGeneration;
            internal long LastAcceptedOfferGeneration;
            internal long LastCancelledOfferGeneration;
            internal DungeonParticipantAttachmentState State;
            internal DateTime DetachedUtc;
            internal DateTime HardExpiresUtc;
            internal DateTime IdleExpiresUtc;
            internal ClockService.ClockTimerHandle ExpiryTimer;
        }

        private readonly object _syncRoot = new object();
        private readonly Dictionary<int, Entry> _byCharacterId =
            new Dictionary<int, Entry>();
        private readonly Dictionary<long, HashSet<int>> _charactersByInstance =
            new Dictionary<long, HashSet<int>>();
        private readonly ClockService _clock;
        private readonly DungeonParticipantAttachmentOptions _options;
        private readonly Func<DateTime> _utcNow;
        private readonly string _timerPrefix;
        private Action<DungeonInstance, long, DateTime, string>
            _partyWipeStarted;
        private bool _disposed;

        internal DungeonInstanceRegistry(
            ClockService clock = null,
            DungeonParticipantAttachmentOptions options = null,
            Func<DateTime> utcNow = null)
        {
            _clock = clock ?? ClockService.Instance;
            _options = options ?? DungeonParticipantAttachmentOptions.Default;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
            _timerPrefix = "dungeon-rejoin:" + Guid.NewGuid().ToString("N") + ":";
        }

        internal void ConfigurePartyWipeStarted(
            Action<DungeonInstance, long, DateTime, string> callback)
        {
            lock (_syncRoot)
                _partyWipeStarted = callback;
        }

        internal DungeonParticipantAttachmentSnapshot RegisterActive(
            DungeonParticipantRegistration registration)
        {
            ValidateRegistration(registration);

            ClockService.ClockTimerHandle staleTimer = null;
            DungeonRun staleRun = null;
            DungeonParticipantAttachmentSnapshot snapshot;
            lock (_syncRoot)
            {
                ThrowIfDisposed();
                if (_byCharacterId.TryGetValue(
                        registration.CharacterId,
                        out var existing))
                {
                    if (existing.State == DungeonParticipantAttachmentState.Active
                        && existing.ActiveSessionId == registration.SessionId
                        && existing.RunIdentity.Equals(
                            registration.Run.CaptureIdentity()))
                    {
                        return BuildSnapshotLocked(existing);
                    }

                    staleRun = existing.Run;
                    staleTimer = RemoveEntryLocked(
                        existing,
                        DungeonParticipantAttachmentState.Terminated);
                }

                var entry = new Entry
                {
                    AccountId = registration.AccountId,
                    CharacterId = registration.CharacterId,
                    ParticipantUserId = registration.ParticipantUserId,
                    PartyId = registration.PartyId,
                    ActiveSessionId = registration.SessionId,
                    Run = registration.Run,
                    RunIdentity = registration.Run.CaptureIdentity(),
                    AttachmentGeneration = 1,
                    State = DungeonParticipantAttachmentState.Active,
                };
                _byCharacterId.Add(entry.CharacterId, entry);
                AddInstanceIndexLocked(entry);
                snapshot = BuildSnapshotLocked(entry);
            }

            staleTimer?.Cancel();
            if (staleRun != null
                && !ReferenceEquals(staleRun, registration.Run))
            {
                EndDetachedRun(staleRun);
            }
            return snapshot;
        }

        internal DungeonAttachmentOperationStatus TryDetach(
            int accountId,
            int characterId,
            ushort participantUserId,
            Guid sessionId,
            DungeonRunIdentity expectedRun,
            out DungeonParticipantAttachmentSnapshot snapshot)
        {
            snapshot = null;
            Entry entry;
            long generation;
            DateTime dueUtc;
            lock (_syncRoot)
            {
                if (_disposed)
                    return DungeonAttachmentOperationStatus.NotFound;
                if (!_byCharacterId.TryGetValue(characterId, out entry))
                    return DungeonAttachmentOperationStatus.NotFound;
                if (!MatchesOwner(
                        entry,
                        accountId,
                        characterId,
                        participantUserId)
                    || !entry.RunIdentity.Equals(expectedRun))
                {
                    return DungeonAttachmentOperationStatus.IdentityMismatch;
                }

                if (entry.State == DungeonParticipantAttachmentState.Detached)
                {
                    snapshot = BuildSnapshotLocked(entry);
                    return DungeonAttachmentOperationStatus.Success;
                }
                if (entry.State != DungeonParticipantAttachmentState.Active
                    || entry.ActiveSessionId != sessionId)
                {
                    return DungeonAttachmentOperationStatus.InvalidState;
                }
                if (!CanDetach(entry.Run))
                {
                    return DungeonAttachmentOperationStatus.InvalidState;
                }

                var now = NormalizeUtc(_utcNow());
                entry.State = DungeonParticipantAttachmentState.Detached;
                entry.ActiveSessionId = Guid.Empty;
                entry.AttachmentGeneration = NextGeneration(
                    entry.AttachmentGeneration);
                entry.DetachedRoomIdentity =
                    entry.Run.CaptureParticipantRoomIdentity();
                entry.DetachedUtc = now;
                entry.HardExpiresUtc = now.Add(_options.HardTimeout);
                entry.IdleExpiresUtc = Min(
                    entry.HardExpiresUtc,
                    now.Add(_options.IdleTimeout));
                entry.LastAcceptedOfferGeneration = 0;
                entry.LastCancelledOfferGeneration = 0;
                generation = entry.AttachmentGeneration;
                dueUtc = entry.IdleExpiresUtc;
                snapshot = BuildSnapshotLocked(entry);
            }

            ReplaceExpiryTimer(entry, generation, dueUtc);
            return DungeonAttachmentOperationStatus.Success;
        }

        internal DungeonAttachmentOperationStatus TryGetCandidate(
            int accountId,
            int characterId,
            ushort participantUserId,
            out DungeonParticipantAttachmentSnapshot snapshot)
        {
            snapshot = null;
            Entry entry;
            long generation = 0;
            DateTime dueUtc = DateTime.MinValue;
            DungeonRun expiredRun = null;
            var status = DungeonAttachmentOperationStatus.Success;
            lock (_syncRoot)
            {
                if (_disposed
                    || !_byCharacterId.TryGetValue(characterId, out entry))
                {
                    return DungeonAttachmentOperationStatus.NotFound;
                }
                if (!MatchesOwner(
                        entry,
                        accountId,
                        characterId,
                        participantUserId))
                {
                    return DungeonAttachmentOperationStatus.IdentityMismatch;
                }
                if (entry.State != DungeonParticipantAttachmentState.Detached)
                    return DungeonAttachmentOperationStatus.InvalidState;
                if (entry.PartyId <= 0 || entry.PartyId > ushort.MaxValue)
                    return DungeonAttachmentOperationStatus.PartyUnavailable;

                var now = NormalizeUtc(_utcNow());
                if (IsExpired(entry, now))
                {
                    ExpireEntryLocked(entry);
                    expiredRun = entry.Run;
                    status = DungeonAttachmentOperationStatus.Expired;
                }
                else
                {
                    entry.IdleExpiresUtc = Min(
                        entry.HardExpiresUtc,
                        now.Add(_options.IdleTimeout));
                    generation = entry.AttachmentGeneration;
                    dueUtc = entry.IdleExpiresUtc;
                    snapshot = BuildSnapshotLocked(entry);
                }
            }

            if (expiredRun != null)
                EndDetachedRun(expiredRun);
            if (status != DungeonAttachmentOperationStatus.Success)
                return status;
            ReplaceExpiryTimer(entry, generation, dueUtc);
            return DungeonAttachmentOperationStatus.Success;
        }

        internal DungeonAttachmentOperationStatus TryResume(
            int accountId,
            int characterId,
            ushort participantUserId,
            int partyId,
            ushort targetParticipantUserId,
            long expectedAttachmentGeneration,
            Guid newSessionId,
            out DungeonParticipantAttachmentSnapshot snapshot)
        {
            return TryResume(
                accountId,
                characterId,
                participantUserId,
                partyId,
                targetParticipantUserId,
                expectedAttachmentGeneration,
                newSessionId,
                out snapshot,
                out _);
        }

        internal DungeonAttachmentOperationStatus TryResume(
            int accountId,
            int characterId,
            ushort participantUserId,
            int partyId,
            ushort targetParticipantUserId,
            long expectedAttachmentGeneration,
            Guid newSessionId,
            out DungeonParticipantAttachmentSnapshot snapshot,
            out bool didTransition)
        {
            snapshot = null;
            didTransition = false;
            ClockService.ClockTimerHandle timer = null;
            DungeonRun expiredRun = null;
            var status = DungeonAttachmentOperationStatus.Success;
            lock (_syncRoot)
            {
                if (_disposed
                    || !_byCharacterId.TryGetValue(characterId, out var entry))
                {
                    return DungeonAttachmentOperationStatus.NotFound;
                }
                if (!MatchesOwner(
                        entry,
                        accountId,
                        characterId,
                        participantUserId)
                    || entry.PartyId != partyId)
                {
                    return DungeonAttachmentOperationStatus.IdentityMismatch;
                }

                if (entry.State == DungeonParticipantAttachmentState.Active
                    && entry.ActiveSessionId == newSessionId
                    && entry.LastAcceptedOfferGeneration
                        == expectedAttachmentGeneration)
                {
                    snapshot = BuildSnapshotLocked(entry);
                    return DungeonAttachmentOperationStatus.Success;
                }
                if (entry.State != DungeonParticipantAttachmentState.Detached)
                    return DungeonAttachmentOperationStatus.InvalidState;
                if (entry.AttachmentGeneration != expectedAttachmentGeneration)
                    return DungeonAttachmentOperationStatus.StaleGeneration;

                var now = NormalizeUtc(_utcNow());
                if (IsExpired(entry, now))
                {
                    ExpireEntryLocked(entry);
                    expiredRun = entry.Run;
                    status = DungeonAttachmentOperationStatus.Expired;
                }
                else if (!ContainsParticipantLocked(
                             entry,
                             targetParticipantUserId))
                {
                    return DungeonAttachmentOperationStatus.TargetParticipantMissing;
                }
                else if (!entry.RunIdentity.Equals(entry.Run.CaptureIdentity())
                    || !RoomIdentityStillMatches(entry))
                {
                    return DungeonAttachmentOperationStatus.IdentityMismatch;
                }
                else
                {
                    timer = entry.ExpiryTimer;
                    entry.ExpiryTimer = null;
                    entry.State = DungeonParticipantAttachmentState.Active;
                    entry.ActiveSessionId = newSessionId;
                    entry.LastAcceptedOfferGeneration =
                        expectedAttachmentGeneration;
                    entry.AttachmentGeneration = NextGeneration(
                        entry.AttachmentGeneration);
                    entry.DetachedUtc = DateTime.MinValue;
                    entry.HardExpiresUtc = DateTime.MinValue;
                    entry.IdleExpiresUtc = DateTime.MinValue;
                    didTransition = true;
                    snapshot = BuildSnapshotLocked(entry);
                }
            }

            if (expiredRun != null)
                EndDetachedRun(expiredRun);
            if (status != DungeonAttachmentOperationStatus.Success)
                return status;
            timer?.Cancel();
            return DungeonAttachmentOperationStatus.Success;
        }

        internal DungeonAttachmentOperationStatus TryCancel(
            int accountId,
            int characterId,
            ushort participantUserId,
            int partyId,
            long expectedAttachmentGeneration,
            out DungeonParticipantAttachmentSnapshot snapshot)
        {
            snapshot = null;
            ClockService.ClockTimerHandle timer = null;
            DungeonRun runToEnd = null;
            var status = DungeonAttachmentOperationStatus.Success;
            lock (_syncRoot)
            {
                if (_disposed
                    || !_byCharacterId.TryGetValue(characterId, out var entry))
                {
                    return DungeonAttachmentOperationStatus.NotFound;
                }
                if (!MatchesOwner(
                        entry,
                        accountId,
                        characterId,
                        participantUserId)
                    || entry.PartyId != partyId)
                {
                    return DungeonAttachmentOperationStatus.IdentityMismatch;
                }
                if (entry.State == DungeonParticipantAttachmentState.Cancelled
                    && entry.LastCancelledOfferGeneration
                        == expectedAttachmentGeneration)
                {
                    snapshot = BuildSnapshotLocked(entry);
                    return DungeonAttachmentOperationStatus.Success;
                }
                if (entry.State != DungeonParticipantAttachmentState.Detached)
                    return DungeonAttachmentOperationStatus.InvalidState;
                if (entry.AttachmentGeneration != expectedAttachmentGeneration)
                    return DungeonAttachmentOperationStatus.StaleGeneration;

                var now = NormalizeUtc(_utcNow());
                if (IsExpired(entry, now))
                {
                    ExpireEntryLocked(entry);
                    runToEnd = entry.Run;
                    status = DungeonAttachmentOperationStatus.Expired;
                }
                else
                {
                    timer = entry.ExpiryTimer;
                    entry.ExpiryTimer = null;
                    RemoveInstanceIndexLocked(entry);
                    entry.State = DungeonParticipantAttachmentState.Cancelled;
                    entry.LastCancelledOfferGeneration =
                        expectedAttachmentGeneration;
                    entry.AttachmentGeneration = NextGeneration(
                        entry.AttachmentGeneration);
                    runToEnd = entry.Run;
                    snapshot = BuildSnapshotLocked(entry);
                }
            }

            timer?.Cancel();
            EndDetachedRun(runToEnd);
            return status;
        }

        internal bool Terminate(
            int characterId,
            DungeonRunIdentity expectedRun,
            string reason)
        {
            DungeonRun run;
            lock (_syncRoot)
            {
                if (_disposed
                    || !_byCharacterId.TryGetValue(characterId, out var entry)
                    || !entry.RunIdentity.Equals(expectedRun))
                {
                    return false;
                }
                run = entry.Run;
            }

            var lifeGate = run?.Instance?.ParticipantLifeGate;
            lifeGate?.Wait();
            ClockService.ClockTimerHandle timer = null;
            var removed = false;
            try
            {
                Action<DungeonInstance, long, DateTime, string>
                    partyWipeStarted = null;
                lock (_syncRoot)
                {
                    if (!_disposed
                        && _byCharacterId.TryGetValue(characterId, out var entry)
                        && entry.RunIdentity.Equals(expectedRun))
                    {
                        timer = RemoveEntryLocked(
                            entry,
                            DungeonParticipantAttachmentState.Terminated);
                        partyWipeStarted = _partyWipeStarted;
                        removed = true;
                    }
                }

                if (removed && run?.Instance != null)
                {
                    var remainingRuns = new List<DungeonRunIdentity>();
                    foreach (var participant in CaptureInstanceParticipantRoster(
                                 run.CaptureInstanceIdentity()))
                    {
                        if (participant.RunIdentity.IsValid
                            && !remainingRuns.Contains(participant.RunIdentity))
                        {
                            remainingRuns.Add(participant.RunIdentity);
                        }
                    }
                    var transition = run.Instance.RemoveParticipantLife(
                        expectedRun,
                        remainingRuns,
                        NormalizeUtc(_utcNow()),
                        DungeonInstance.PartyWipeDelay);
                    if (transition.WipeStarted && partyWipeStarted != null)
                    {
                        try
                        {
                            partyWipeStarted(
                                run.Instance,
                                transition.Generation,
                                transition.DeadlineUtc,
                                reason);
                        }
                        catch (Exception ex)
                        {
                            FileLogger.Log(
                                $"[DungeonInstanceRegistry] party wipe callback " +
                                $"failed instance={run.PartyDungeonInstanceId}: " +
                                ex.Message);
                        }
                    }
                }
            }
            finally
            {
                lifeGate?.Release();
            }

            timer?.Cancel();
            if (!removed)
                return false;
            FileLogger.Log(
                $"[DungeonInstanceRegistry] participant terminated " +
                $"cid={characterId} instance={expectedRun.PartyDungeonInstanceId} " +
                $"run={expectedRun.RunId}/{expectedRun.RunGeneration} reason={reason}");
            return true;
        }

        internal int ExpireDue(DateTime utcNow)
        {
            var expiredRuns = new List<DungeonRun>();
            var timers = new List<ClockService.ClockTimerHandle>();
            var count = 0;
            utcNow = NormalizeUtc(utcNow);
            lock (_syncRoot)
            {
                if (_disposed)
                    return 0;

                var entries = new List<Entry>(_byCharacterId.Values);
                foreach (var entry in entries)
                {
                    if (entry.State != DungeonParticipantAttachmentState.Detached
                        || !IsExpired(entry, utcNow))
                    {
                        continue;
                    }

                    if (entry.ExpiryTimer != null)
                        timers.Add(entry.ExpiryTimer);
                    entry.ExpiryTimer = null;
                    RemoveInstanceIndexLocked(entry);
                    entry.State = DungeonParticipantAttachmentState.Expired;
                    expiredRuns.Add(entry.Run);
                    count++;
                }
            }

            foreach (var timer in timers)
                timer.Cancel();
            foreach (var run in expiredRuns)
                EndDetachedRun(run);
            return count;
        }

        internal bool TryGetForRun(
            int characterId,
            DungeonRunIdentity expectedRun,
            out DungeonParticipantAttachmentSnapshot snapshot)
        {
            lock (_syncRoot)
            {
                if (!_disposed
                    && _byCharacterId.TryGetValue(characterId, out var entry)
                    && entry.RunIdentity.Equals(expectedRun))
                {
                    snapshot = BuildSnapshotLocked(entry);
                    return true;
                }
            }

            snapshot = null;
            return false;
        }

        // Captures the participant set for one physical room. The returned list
        // is a value snapshot; callers must not re-query PartyManager while
        // executing the event because membership/room changes are then TOCTOU.
        internal IReadOnlyList<DungeonParticipantRosterEntry> CaptureParticipantRoster(
            DungeonRoomIdentity roomIdentity,
            int partyId = 0)
        {
            var result = new List<DungeonParticipantRosterEntry>();
            if (!roomIdentity.IsValid)
                return result;

            lock (_syncRoot)
            {
                if (_disposed
                    || !_charactersByInstance.TryGetValue(
                        roomIdentity.Instance.PartyDungeonInstanceId,
                        out var characterIds))
                {
                    return result;
                }

                foreach (var characterId in characterIds)
                {
                    if (!_byCharacterId.TryGetValue(characterId, out var entry)
                        || (entry.State != DungeonParticipantAttachmentState.Active
                            && entry.State != DungeonParticipantAttachmentState.Detached)
                        || (partyId > 0 && entry.PartyId != partyId))
                    {
                        continue;
                    }

                    var participantRoom = entry.State ==
                        DungeonParticipantAttachmentState.Detached
                        ? entry.DetachedRoomIdentity.Room
                        : entry.Run.CaptureRoomIdentity();
                    if (!participantRoom.Equals(roomIdentity))
                        continue;

                    result.Add(new DungeonParticipantRosterEntry(
                        entry.CharacterId,
                        entry.ParticipantUserId,
                        entry.Run,
                        entry.RunIdentity,
                        roomIdentity,
                        entry.AttachmentGeneration));
                }
            }

            result.Sort((left, right) =>
                left.CharacterId.CompareTo(right.CharacterId));
            return result;
        }

        // Clear facts are instance-wide. Their frozen roster deliberately does
        // not require every participant to remain in the source room.
        internal IReadOnlyList<DungeonParticipantRosterEntry>
            CaptureInstanceParticipantRoster(
                DungeonInstanceIdentity instanceIdentity,
                int partyId = 0)
        {
            var result = new List<DungeonParticipantRosterEntry>();
            if (!instanceIdentity.IsValid)
                return result;

            lock (_syncRoot)
            {
                if (_disposed
                    || !_charactersByInstance.TryGetValue(
                        instanceIdentity.PartyDungeonInstanceId,
                        out var characterIds))
                {
                    return result;
                }

                foreach (var characterId in characterIds)
                {
                    if (!_byCharacterId.TryGetValue(characterId, out var entry)
                        || (entry.State != DungeonParticipantAttachmentState.Active
                            && entry.State != DungeonParticipantAttachmentState.Detached)
                        || (partyId > 0 && entry.PartyId != partyId))
                    {
                        continue;
                    }

                    var roomIdentity = entry.State ==
                        DungeonParticipantAttachmentState.Detached
                        ? entry.DetachedRoomIdentity.Room
                        : entry.Run.CaptureRoomIdentity();
                    if (!roomIdentity.IsValid
                        || !roomIdentity.Instance.Equals(instanceIdentity))
                    {
                        continue;
                    }

                    result.Add(new DungeonParticipantRosterEntry(
                        entry.CharacterId,
                        entry.ParticipantUserId,
                        entry.Run,
                        entry.RunIdentity,
                        roomIdentity,
                        entry.AttachmentGeneration));
                }
            }

            result.Sort((left, right) =>
                left.CharacterId.CompareTo(right.CharacterId));
            return result;
        }

        public void Dispose()
        {
            List<ClockService.ClockTimerHandle> timers;
            HashSet<DungeonInstance> instances;
            lock (_syncRoot)
            {
                if (_disposed)
                    return;
                _disposed = true;
                timers = new List<ClockService.ClockTimerHandle>();
                instances = new HashSet<DungeonInstance>();
                foreach (var entry in _byCharacterId.Values)
                {
                    if (entry.ExpiryTimer != null)
                        timers.Add(entry.ExpiryTimer);
                    entry.ExpiryTimer = null;
                    entry.State = DungeonParticipantAttachmentState.Terminated;
                    if (entry.Run?.Instance != null)
                        instances.Add(entry.Run.Instance);
                }
                _byCharacterId.Clear();
                _charactersByInstance.Clear();
            }

            foreach (var timer in timers)
                timer.Cancel();
            foreach (var instance in instances)
            {
                instance.TryBeginEnding();
                instance.TryMarkEnded();
            }
        }

        private void ReplaceExpiryTimer(
            Entry entry,
            long expectedGeneration,
            DateTime dueUtc)
        {
            var handle = _clock.ScheduleOneShot(
                BuildTimerName(entry.CharacterId),
                dueUtc,
                now => ExpireAttachment(
                    entry.CharacterId,
                    expectedGeneration,
                    now));
            ClockService.ClockTimerHandle previous = null;
            var keep = false;
            lock (_syncRoot)
            {
                if (!_disposed
                    && _byCharacterId.TryGetValue(
                        entry.CharacterId,
                        out var current)
                    && ReferenceEquals(current, entry)
                    && entry.State == DungeonParticipantAttachmentState.Detached
                    && entry.AttachmentGeneration == expectedGeneration)
                {
                    previous = entry.ExpiryTimer;
                    entry.ExpiryTimer = handle;
                    keep = true;
                }
            }

            previous?.Cancel();
            if (!keep)
                handle.Cancel();
        }

        private void ExpireAttachment(
            int characterId,
            long expectedGeneration,
            DateTime utcNow)
        {
            DungeonRun runToEnd = null;
            DateTime rescheduleUtc = DateTime.MinValue;
            Entry rescheduleEntry = null;
            lock (_syncRoot)
            {
                if (_disposed
                    || !_byCharacterId.TryGetValue(characterId, out var entry)
                    || entry.State != DungeonParticipantAttachmentState.Detached
                    || entry.AttachmentGeneration != expectedGeneration)
                {
                    return;
                }

                entry.ExpiryTimer = null;
                utcNow = NormalizeUtc(utcNow);
                if (!IsExpired(entry, utcNow))
                {
                    rescheduleEntry = entry;
                    rescheduleUtc = Min(
                        entry.HardExpiresUtc,
                        entry.IdleExpiresUtc);
                }
                else
                {
                    ExpireEntryLocked(entry);
                    runToEnd = entry.Run;
                }
            }

            if (rescheduleEntry != null)
                ReplaceExpiryTimer(
                    rescheduleEntry,
                    expectedGeneration,
                    rescheduleUtc);
            if (runToEnd != null)
            {
                EndDetachedRun(runToEnd);
                FileLogger.Log(
                    $"[DungeonInstanceRegistry] detached participant expired " +
                    $"cid={characterId} instance={runToEnd.PartyDungeonInstanceId} " +
                    $"run={runToEnd.RunId}/{runToEnd.RunGeneration}");
            }
        }

        private DungeonParticipantAttachmentSnapshot BuildSnapshotLocked(
            Entry entry)
        {
            var participantIds = new List<ushort>();
            if (_charactersByInstance.TryGetValue(
                    entry.RunIdentity.PartyDungeonInstanceId,
                    out var characterIds))
            {
                foreach (var characterId in characterIds)
                {
                    if (_byCharacterId.TryGetValue(characterId, out var participant)
                        && participant.PartyId == entry.PartyId
                        && (participant.State == DungeonParticipantAttachmentState.Active
                            || participant.State == DungeonParticipantAttachmentState.Detached))
                    {
                        participantIds.Add(participant.ParticipantUserId);
                    }
                }
            }
            participantIds.Sort();

            return new DungeonParticipantAttachmentSnapshot(
                entry.AccountId,
                entry.CharacterId,
                entry.ParticipantUserId,
                entry.PartyId,
                entry.AttachmentGeneration,
                entry.State,
                entry.Run,
                entry.RunIdentity,
                entry.DetachedRoomIdentity,
                entry.DetachedUtc,
                entry.HardExpiresUtc,
                entry.IdleExpiresUtc,
                participantIds);
        }

        private bool ContainsParticipantLocked(
            Entry source,
            ushort participantUserId)
        {
            if (!_charactersByInstance.TryGetValue(
                    source.RunIdentity.PartyDungeonInstanceId,
                    out var characterIds))
            {
                return false;
            }

            foreach (var characterId in characterIds)
            {
                if (_byCharacterId.TryGetValue(characterId, out var candidate)
                    && candidate.PartyId == source.PartyId
                    && candidate.ParticipantUserId == participantUserId
                    && (candidate.State == DungeonParticipantAttachmentState.Active
                        || candidate.State == DungeonParticipantAttachmentState.Detached))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool RoomIdentityStillMatches(Entry entry)
        {
            return !entry.DetachedRoomIdentity.IsValid
                || entry.Run.Matches(entry.DetachedRoomIdentity);
        }

        private static bool CanDetach(DungeonRun run)
        {
            return run != null
                && run.Tower == null
                && (run.RunState == DungeonRunState.Active
                    || run.RunState == DungeonRunState.ClearCommitting
                    || run.RunState == DungeonRunState.Cleared);
        }

        private static void EndDetachedRun(DungeonRun run)
        {
            if (run == null)
                return;
            run.Timers.CancelAll();
            run.TryBeginEnding();
            run.TryMarkEnded();
        }

        private void ExpireEntryLocked(Entry entry)
        {
            entry.ExpiryTimer = null;
            RemoveInstanceIndexLocked(entry);
            entry.State = DungeonParticipantAttachmentState.Expired;
        }

        private ClockService.ClockTimerHandle RemoveEntryLocked(
            Entry entry,
            DungeonParticipantAttachmentState terminalState)
        {
            _byCharacterId.Remove(entry.CharacterId);
            RemoveInstanceIndexLocked(entry);
            entry.State = terminalState;
            var timer = entry.ExpiryTimer;
            entry.ExpiryTimer = null;
            return timer;
        }

        private void AddInstanceIndexLocked(Entry entry)
        {
            if (!_charactersByInstance.TryGetValue(
                    entry.RunIdentity.PartyDungeonInstanceId,
                    out var characterIds))
            {
                characterIds = new HashSet<int>();
                _charactersByInstance.Add(
                    entry.RunIdentity.PartyDungeonInstanceId,
                    characterIds);
            }
            characterIds.Add(entry.CharacterId);
        }

        private void RemoveInstanceIndexLocked(Entry entry)
        {
            if (!_charactersByInstance.TryGetValue(
                    entry.RunIdentity.PartyDungeonInstanceId,
                    out var characterIds))
            {
                return;
            }
            characterIds.Remove(entry.CharacterId);
            if (characterIds.Count == 0)
            {
                _charactersByInstance.Remove(
                    entry.RunIdentity.PartyDungeonInstanceId);
                entry.Run?.Instance?.TryBeginEnding();
                entry.Run?.Instance?.TryMarkEnded();
            }
        }

        private string BuildTimerName(int characterId) =>
            _timerPrefix + characterId;

        private static bool MatchesOwner(
            Entry entry,
            int accountId,
            int characterId,
            ushort participantUserId)
        {
            return entry.AccountId == accountId
                && entry.CharacterId == characterId
                && entry.ParticipantUserId == participantUserId;
        }

        private static bool IsExpired(Entry entry, DateTime utcNow) =>
            utcNow >= entry.HardExpiresUtc
            || utcNow >= entry.IdleExpiresUtc;

        private static DateTime Min(DateTime left, DateTime right) =>
            left <= right ? left : right;

        private static DateTime NormalizeUtc(DateTime value)
        {
            if (value.Kind == DateTimeKind.Local)
                return value.ToUniversalTime();
            return value.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
                : value;
        }

        private static long NextGeneration(long generation)
        {
            if (generation == long.MaxValue)
                throw new InvalidOperationException(
                    "Dungeon attachment generation exhausted.");
            return generation + 1;
        }

        private static void ValidateRegistration(
            DungeonParticipantRegistration registration)
        {
            if (registration == null)
                throw new ArgumentNullException(nameof(registration));
            if (registration.AccountId <= 0)
                throw new ArgumentOutOfRangeException(nameof(registration.AccountId));
            if (registration.CharacterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(registration.CharacterId));
            if (registration.ParticipantUserId == 0)
                throw new ArgumentOutOfRangeException(
                    nameof(registration.ParticipantUserId));
            if (registration.PartyId < 0)
                throw new ArgumentOutOfRangeException(nameof(registration.PartyId));
            if (registration.SessionId == Guid.Empty)
                throw new ArgumentException(
                    "A live participant requires a session ID.",
                    nameof(registration));
            if (registration.Run == null
                || !registration.Run.CaptureIdentity().IsValid)
            {
                throw new ArgumentException(
                    "A live participant requires a valid dungeon run.",
                    nameof(registration));
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(DungeonInstanceRegistry));
        }
    }
}
