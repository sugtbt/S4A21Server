using System.Collections.Generic;
using DfoServer.Game.Characters;

namespace DfoServer.Game.KnightShield
{
    internal static class KnightShieldAppearanceSynchronizer
    {
        internal static CharacterAppearanceEntry[] Apply(
            IEnumerable<CharacterAppearanceEntry> appearanceEntries,
            byte job,
            int growType,
            KnightShieldDeckSnapshot deck)
        {
            var result = appearanceEntries != null
                ? new List<CharacterAppearanceEntry>(appearanceEntries)
                : new List<CharacterAppearanceEntry>();
            if (deck == null || !KnightShieldDataProvider.IsEligibleCharacter(job))
                return result.ToArray();

            var supportWeaponSlot = KnightShieldEquipmentSnapshotSynchronizer.SupportWeaponSlot;
            var shieldItemId = deck.MainShieldItemId;
            var hasValidDeckShield = shieldItemId > 0
                && KnightShieldDataProvider.IsCatalogShield(growType, shieldItemId);
            if (!hasValidDeckShield)
                return result.ToArray();

            CharacterAppearanceEntry existing = null;
            for (var index = 0; index < result.Count; index++)
            {
                var entry = result[index];
                if (entry != null && entry.Slot == supportWeaponSlot)
                {
                    existing = entry;
                    break;
                }
            }

            // A real slot-24 appearance is authoritative; the deck only
            // fills the slot when no physical support weapon is present.
            if (existing != null)
                return result.ToArray();

            result.Add(new CharacterAppearanceEntry(
                supportWeaponSlot,
                shieldItemId,
                4,
                new byte[4],
                0,
                0,
                0u,
                0));
            result.Sort((left, right) => left.Slot.CompareTo(right.Slot));
            return result.ToArray();
        }
    }
}
