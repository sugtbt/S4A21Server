using DfoServer.Game.Characters;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.KnightShield
{
    public sealed class KnightShieldService
    {
        private readonly KnightShieldDeckRepository _repository;
        private readonly Func<int, ISet<int>> _clearedQuestIdsLoader;
        private readonly object _mutationGate = new object();

        public KnightShieldService(
            KnightShieldDeckRepository repository,
            Func<int, ISet<int>> clearedQuestIdsLoader = null)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _clearedQuestIdsLoader = clearedQuestIdsLoader;
        }

        public KnightShieldDeckSnapshot Load(int characterId)
        {
            return _repository.Load(characterId);
        }

        public bool TryEquipMain(
            CharacterRecord character,
            int shieldItemId,
            out KnightShieldDeckSnapshot snapshot,
            out string rejectReason,
            ISet<int> clearedQuestIds = null)
        {
            return TryEquipSlot(
                character,
                KnightShieldDeckSnapshot.MainSlotIndex,
                shieldItemId,
                out snapshot,
                out rejectReason,
                clearedQuestIds);
        }

        public bool TryEquipSlot(
            CharacterRecord character,
            int slotIndex,
            int shieldItemId,
            out KnightShieldDeckSnapshot snapshot,
            out string rejectReason,
            ISet<int> clearedQuestIds = null)
        {
            snapshot = null;
            if (!ValidateCharacter(character, out rejectReason)
                || !ValidateShield(
                    character,
                    shieldItemId,
                    ResolveClearedQuestIds(character, clearedQuestIds),
                    out rejectReason))
                return false;
            if (!IsDeckSlotIndex(slotIndex))
            {
                rejectReason = $"deck slot is outside 0..{KnightShieldDeckSnapshot.SlotCount - 1}: {slotIndex}";
                return false;
            }

            lock (_mutationGate)
            {
                var values = _repository.Load(character.CharacterId).ToArray();
                for (var index = 0; index < values.Length; index++)
                {
                    if (values[index] == shieldItemId)
                        values[index] = 0;
                }

                values[slotIndex] = shieldItemId;
                snapshot = new KnightShieldDeckSnapshot(values);
                _repository.Save(character.CharacterId, snapshot);
            }
            return true;
        }

        public bool TryUnequipMain(
            CharacterRecord character,
            out KnightShieldDeckSnapshot snapshot,
            out string rejectReason)
        {
            snapshot = null;
            if (!ValidateCharacter(character, out rejectReason))
                return false;

            lock (_mutationGate)
            {
                var values = _repository.Load(character.CharacterId).ToArray();
                values[KnightShieldDeckSnapshot.MainSlotIndex] = 0;
                snapshot = new KnightShieldDeckSnapshot(values);
                _repository.Save(character.CharacterId, snapshot);
            }
            return true;
        }

        public bool TryUnequipSlot(
            CharacterRecord character,
            int slotIndex,
            out KnightShieldDeckSnapshot snapshot,
            out string rejectReason)
        {
            if (slotIndex == KnightShieldDeckSnapshot.MainSlotIndex)
                return TryUnequipMain(character, out snapshot, out rejectReason);

            snapshot = null;
            if (!ValidateCharacter(character, out rejectReason))
                return false;
            if (!IsDeckSlotIndex(slotIndex))
            {
                rejectReason = $"deck slot is outside 0..{KnightShieldDeckSnapshot.SlotCount - 1}: {slotIndex}";
                return false;
            }

            lock (_mutationGate)
            {
                var values = _repository.Load(character.CharacterId).ToArray();
                values[slotIndex] = 0;
                snapshot = new KnightShieldDeckSnapshot(values);
                _repository.Save(character.CharacterId, snapshot);
            }
            return true;
        }

        public bool TryReplaceMainFromOwnedShield(
            CharacterRecord character,
            int shieldItemId,
            out KnightShieldDeckSnapshot snapshot,
            out string rejectReason)
        {
            snapshot = null;
            if (!ValidateCharacter(character, out rejectReason))
                return false;
            if (shieldItemId < 0)
            {
                rejectReason = "owned shield item id cannot be negative";
                return false;
            }
            if (shieldItemId > 0 && !KnightShieldDataProvider.IsKnightShield(shieldItemId))
            {
                rejectReason = $"item {shieldItemId} is not a knight shield";
                return false;
            }

            lock (_mutationGate)
            {
                var values = _repository.Load(character.CharacterId).ToArray();
                values[KnightShieldDeckSnapshot.MainSlotIndex] = shieldItemId;
                if (shieldItemId > 0)
                {
                    for (var slotIndex = 1; slotIndex < values.Length; slotIndex++)
                    {
                        if (values[slotIndex] == shieldItemId)
                            values[slotIndex] = 0;
                    }
                }

                snapshot = new KnightShieldDeckSnapshot(values);
                _repository.Save(character.CharacterId, snapshot);
            }

            rejectReason = null;
            return true;
        }

        public bool TryMoveDeckSlot(
            CharacterRecord character,
            int sourceSlotIndex,
            int destinationSlotIndex,
            out KnightShieldDeckSnapshot snapshot,
            out string rejectReason)
        {
            snapshot = null;
            if (!ValidateCharacter(character, out rejectReason))
                return false;
            if (!IsDeckSlotIndex(sourceSlotIndex) || !IsDeckSlotIndex(destinationSlotIndex))
            {
                rejectReason = $"deck slot move is outside 0..{KnightShieldDeckSnapshot.SlotCount - 1}: "
                    + $"source={sourceSlotIndex} destination={destinationSlotIndex}";
                return false;
            }

            lock (_mutationGate)
            {
                var values = _repository.Load(character.CharacterId).ToArray();
                if (values[sourceSlotIndex] == 0)
                {
                    rejectReason = $"source deck slot {sourceSlotIndex} is empty";
                    return false;
                }

                if (sourceSlotIndex != destinationSlotIndex)
                {
                    var sourceShieldItemId = values[sourceSlotIndex];
                    values[sourceSlotIndex] = values[destinationSlotIndex];
                    values[destinationSlotIndex] = sourceShieldItemId;
                    snapshot = new KnightShieldDeckSnapshot(values);
                    _repository.Save(character.CharacterId, snapshot);
                }
                else
                {
                    snapshot = new KnightShieldDeckSnapshot(values);
                }
            }

            rejectReason = null;
            return true;
        }

        public KnightShieldDeckSnapshot ReconcileOnSelect(
            CharacterRecord character,
            int equippedSupportWeaponItemId,
            ISet<int> clearedQuestIds = null)
        {
            if (!ValidateCharacter(character, out _))
                return new KnightShieldDeckSnapshot();

            lock (_mutationGate)
            {
                var values = _repository.Load(character.CharacterId).ToArray();
                var equippedShield = equippedSupportWeaponItemId > 0
                    && KnightShieldDataProvider.IsKnightShield(equippedSupportWeaponItemId);
                var changed = false;
                if (equippedSupportWeaponItemId > 0 && !equippedShield)
                {
                    if (values[KnightShieldDeckSnapshot.MainSlotIndex] != 0)
                    {
                        values[KnightShieldDeckSnapshot.MainSlotIndex] = 0;
                        changed = true;
                    }
                }
                else
                {
                    changed = ApplyEquippedMain(values, equippedSupportWeaponItemId);
                }
                var unlockedQuestIds = ResolveClearedQuestIds(character, clearedQuestIds);
                var mainProtected = equippedShield
                    && values[KnightShieldDeckSnapshot.MainSlotIndex] == equippedSupportWeaponItemId;
                for (var slotIndex = 0; slotIndex < values.Length; slotIndex++)
                {
                    var shieldItemId = values[slotIndex];
                    if (shieldItemId <= 0)
                        continue;
                    if (slotIndex == KnightShieldDeckSnapshot.MainSlotIndex && mainProtected)
                        continue;
                    if (KnightShieldDataProvider.IsCatalogShieldUnlocked(
                        character.Job,
                        character.GrowType,
                        shieldItemId,
                        character.Level,
                        unlockedQuestIds))
                        continue;

                    values[slotIndex] = 0;
                    changed = true;
                }

                var snapshot = new KnightShieldDeckSnapshot(values);
                if (changed)
                {
                    _repository.Save(character.CharacterId, snapshot);
                    FileLogger.Log(
                        $"[KnightShield] reconcile cid={character.CharacterId} "
                        + $"equipped={equippedSupportWeaponItemId} "
                        + $"deck=[{string.Join(",", snapshot.ShieldItemIds)}]");
                }

                return snapshot;
            }
        }

        public bool TrySaveDeck(
            CharacterRecord character,
            IReadOnlyList<int> shieldItemIds,
            out KnightShieldDeckSnapshot snapshot,
            out string rejectReason,
            ISet<int> clearedQuestIds = null)
        {
            snapshot = null;
            if (!ValidateCharacter(character, out rejectReason))
                return false;
            if (shieldItemIds == null || shieldItemIds.Count != KnightShieldDeckSnapshot.SlotCount)
            {
                rejectReason = $"deck slot count must be {KnightShieldDeckSnapshot.SlotCount}";
                return false;
            }

            var unlockedQuestIds = ResolveClearedQuestIds(character, clearedQuestIds);
            var seen = new HashSet<int>();
            for (var slotIndex = 0; slotIndex < shieldItemIds.Count; slotIndex++)
            {
                var shieldItemId = shieldItemIds[slotIndex];
                if (shieldItemId == 0)
                    continue;
                if (!ValidateShield(
                        character,
                        shieldItemId,
                        unlockedQuestIds,
                        out rejectReason))
                    return false;
                if (!seen.Add(shieldItemId))
                {
                    rejectReason = $"duplicate shield item {shieldItemId}";
                    return false;
                }
            }

            lock (_mutationGate)
            {
                snapshot = new KnightShieldDeckSnapshot(shieldItemIds);
                _repository.Save(character.CharacterId, snapshot);
            }
            rejectReason = null;
            return true;
        }

        private static bool ValidateCharacter(CharacterRecord character, out string rejectReason)
        {
            if (character == null || character.CharacterId <= 0)
            {
                rejectReason = "character is unavailable";
                return false;
            }
            if (!KnightShieldDataProvider.IsEligibleCharacter(character))
            {
                rejectReason = $"character is not a guardian: job={character.Job} grow={character.GrowType}";
                return false;
            }

            rejectReason = null;
            return true;
        }

        private static bool ValidateShield(
            CharacterRecord character,
            int shieldItemId,
            ISet<int> clearedQuestIds,
            out string rejectReason)
        {
            if (!KnightShieldDataProvider.IsCatalogShield(character.GrowType, shieldItemId))
            {
                rejectReason =
                    $"item {shieldItemId} is not in guardian shield catalog for grow={character.GrowType}";
                return false;
            }

            if (!KnightShieldDataProvider.IsCatalogShieldUnlocked(
                character.Job,
                character.GrowType,
                shieldItemId,
                character.Level,
                clearedQuestIds))
            {
                rejectReason = DescribeUnlockFailure(character, shieldItemId);
                return false;
            }

            rejectReason = null;
            return true;
        }

        private static string DescribeUnlockFailure(CharacterRecord character, int shieldItemId)
        {
            KnightShieldDataProvider.TryGetCatalogEntry(
                character.GrowType,
                shieldItemId,
                out var entry);
            if (entry != null && entry.UnlockKind == KnightShieldUnlockKind.Level)
            {
                return $"item {shieldItemId} is not unlocked (level {entry.RequiredLevel}, have {character.Level})";
            }

            return $"item {shieldItemId} is not unlocked (clear quest {entry?.ClearQuestId ?? 0})";
        }

        private ISet<int> ResolveClearedQuestIds(
            CharacterRecord character,
            ISet<int> clearedQuestIds)
        {
            if (clearedQuestIds != null)
                return clearedQuestIds;
            if (_clearedQuestIdsLoader == null || character == null)
                return new HashSet<int>();
            return _clearedQuestIdsLoader(character.CharacterId) ?? new HashSet<int>();
        }

        private static bool ApplyEquippedMain(int[] values, int equippedSupportWeaponItemId)
        {
            if (values == null
                || equippedSupportWeaponItemId <= 0
                || !KnightShieldDataProvider.IsKnightShield(equippedSupportWeaponItemId))
                return false;

            var changed = false;
            if (values[KnightShieldDeckSnapshot.MainSlotIndex] != equippedSupportWeaponItemId)
            {
                values[KnightShieldDeckSnapshot.MainSlotIndex] = equippedSupportWeaponItemId;
                changed = true;
            }

            for (var slotIndex = 1; slotIndex < values.Length; slotIndex++)
            {
                if (values[slotIndex] != equippedSupportWeaponItemId)
                    continue;
                values[slotIndex] = 0;
                changed = true;
            }

            return changed;
        }

        private static bool IsDeckSlotIndex(int slotIndex)
        {
            return slotIndex >= 0 && slotIndex < KnightShieldDeckSnapshot.SlotCount;
        }
    }
}
