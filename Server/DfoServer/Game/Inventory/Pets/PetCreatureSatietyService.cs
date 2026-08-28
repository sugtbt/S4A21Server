using System;

namespace DfoServer.Game.Inventory
{
    internal static class PetCreatureSatietyService
    {
        internal static PetCreatureSatietyUpdate LoadEquippedCreatureSatiety(InventoryService inventory)
        {
            if (inventory == null)
                return PetCreatureSatietyUpdate.Noop(0);

            if (!PetInventoryAccessor.TryGetEquippedCreature(inventory, out _, out var detail))
                return PetCreatureSatietyUpdate.Noop(inventory.CharacterId);

            var satiety = ClampSatiety(detail.Stomach);
            var foodConsumeRatePercent = PetInventoryAccessor.ResolveEquippedCreatureFoodConsumeRatePercent(inventory);
            return new PetCreatureSatietyUpdate(
                inventory.CharacterId,
                detail.Uid,
                satiety,
                satiety,
                0,
                0,
                false,
                foodConsumeRatePercent);
        }

        internal static byte LoadEquippedCreatureAliveFlag(InventoryService inventory)
        {
            var current = LoadEquippedCreatureSatiety(inventory);
            if (current.CreatureKey <= 0)
                return 0;

            return current.Before <= 0 ? (byte)0 : (byte)1;
        }

        internal static PetCreatureSatietyUpdate ApplyDungeonElapsed(
            InventoryService inventory,
            DateTime startUtc,
            DateTime endUtc)
        {
            return EvaluateDungeonElapsed(inventory, startUtc, endUtc, apply: true);
        }

        internal static PetCreatureSatietyUpdate PreviewDungeonElapsed(
            InventoryService inventory,
            DateTime startUtc,
            DateTime endUtc)
        {
            return EvaluateDungeonElapsed(inventory, startUtc, endUtc, apply: false);
        }

        internal static PetCreatureSatietyUpdate ApplyDungeonElapsedForCommit(
            InventoryService inventory,
            DateTime startUtc,
            DateTime endUtc)
        {
            return EvaluateDungeonElapsed(inventory, startUtc, endUtc, apply: true);
        }

        private static PetCreatureSatietyUpdate EvaluateDungeonElapsed(
            InventoryService inventory,
            DateTime startUtc,
            DateTime endUtc,
            bool apply)
        {
            if (inventory == null || startUtc == DateTime.MinValue)
                return PetCreatureSatietyUpdate.Noop(inventory?.CharacterId ?? 0);

            var elapsedSeconds = Math.Max(0, (endUtc - startUtc).TotalSeconds);
            if (!PetInventoryAccessor.TryGetEquippedCreature(inventory, out _, out var detail))
                return PetCreatureSatietyUpdate.Noop(inventory.CharacterId, elapsedSeconds);

            var before = ClampSatiety(detail.Stomach);
            var foodConsumeRatePercent = PetInventoryAccessor.ResolveEquippedCreatureFoodConsumeRatePercent(inventory);
            var after = CalculateDungeonSatietyAfter(
                before,
                elapsedSeconds,
                foodConsumeRatePercent,
                clampAliveMinimum: true);

            if (apply)
                SaveSatietyIfChanged(inventory, detail, before, after);
            return new PetCreatureSatietyUpdate(
                inventory.CharacterId,
                detail.Uid,
                before,
                after,
                elapsedSeconds,
                after - before,
                after != before,
                foodConsumeRatePercent);
        }

        internal static PetCreatureSatietyUpdate ApplyDungeonDeathIfExpired(
            InventoryService inventory,
            DateTime startUtc,
            DateTime endUtc)
        {
            return EvaluateDungeonDeath(inventory, startUtc, endUtc, apply: true);
        }

        internal static PetCreatureSatietyUpdate PreviewDungeonDeath(
            InventoryService inventory,
            DateTime startUtc,
            DateTime endUtc)
        {
            return EvaluateDungeonDeath(inventory, startUtc, endUtc, apply: false);
        }

        private static PetCreatureSatietyUpdate EvaluateDungeonDeath(
            InventoryService inventory,
            DateTime startUtc,
            DateTime endUtc,
            bool apply)
        {
            if (inventory == null || startUtc == DateTime.MinValue)
                return PetCreatureSatietyUpdate.Noop(inventory?.CharacterId ?? 0);

            var elapsedSeconds = Math.Max(0, (endUtc - startUtc).TotalSeconds);
            if (!PetInventoryAccessor.TryGetEquippedCreature(inventory, out _, out var detail))
                return PetCreatureSatietyUpdate.Noop(inventory.CharacterId, elapsedSeconds);

            var before = ClampSatiety(detail.Stomach);
            var foodConsumeRatePercent = PetInventoryAccessor.ResolveEquippedCreatureFoodConsumeRatePercent(inventory);
            var stomach = CalculateDungeonStomachValue(
                before,
                elapsedSeconds,
                foodConsumeRatePercent);
            var shouldDie = stomach <= 1.0;
            var after = shouldDie
                ? 0
                : CalculateVisibleSatiety(stomach, clampAliveMinimum: true);

            if (apply)
                SaveSatietyIfChanged(inventory, detail, before, after);
            return new PetCreatureSatietyUpdate(
                inventory.CharacterId,
                detail.Uid,
                before,
                after,
                elapsedSeconds,
                shouldDie ? -before : after - before,
                shouldDie && before != 0,
                foodConsumeRatePercent);
        }

        internal static PetCreatureSatietyUpdate ApplyTownElapsed(
            InventoryService inventory,
            DateTime startUtc,
            DateTime endUtc)
        {
            return EvaluateTownElapsed(inventory, startUtc, endUtc, apply: true);
        }

        internal static PetCreatureSatietyUpdate PreviewTownElapsed(
            InventoryService inventory,
            DateTime startUtc,
            DateTime endUtc)
        {
            return EvaluateTownElapsed(inventory, startUtc, endUtc, apply: false);
        }

        private static PetCreatureSatietyUpdate EvaluateTownElapsed(
            InventoryService inventory,
            DateTime startUtc,
            DateTime endUtc,
            bool apply)
        {
            if (inventory == null || startUtc == DateTime.MinValue)
                return PetCreatureSatietyUpdate.Noop(inventory?.CharacterId ?? 0);

            var elapsedSeconds = Math.Max(0, (endUtc - startUtc).TotalSeconds);
            if (!PetInventoryAccessor.TryGetEquippedCreature(inventory, out _, out var detail))
                return PetCreatureSatietyUpdate.Noop(inventory.CharacterId, elapsedSeconds);

            var before = ClampSatiety(detail.Stomach);
            var after = CalculateTownSatietyAfter(before, elapsedSeconds);
            if (apply)
                SaveSatietyIfChanged(inventory, detail, before, after);
            return new PetCreatureSatietyUpdate(
                inventory.CharacterId,
                detail.Uid,
                before,
                after,
                elapsedSeconds,
                after - before,
                after != before);
        }

        internal static PetCreatureRevivalUpdate ReviveEquippedCreatureIfDead(InventoryService inventory)
        {
            return EvaluateRevival(inventory, apply: true);
        }

        internal static PetCreatureRevivalUpdate PreviewRevival(InventoryService inventory)
        {
            return EvaluateRevival(inventory, apply: false);
        }

        private static PetCreatureRevivalUpdate EvaluateRevival(
            InventoryService inventory,
            bool apply)
        {
            if (inventory == null)
                return PetCreatureRevivalUpdate.Noop(0);

            if (!PetInventoryAccessor.TryGetEquippedCreature(inventory, out _, out var detail))
                return PetCreatureRevivalUpdate.Noop(inventory.CharacterId);

            var before = ClampSatiety(detail.Stomach);
            var after = before;
            var revived = before <= 0;
            if (revived)
            {
                after = 1;
                if (apply)
                    SaveSatietyIfChanged(inventory, detail, before, after);
            }

            return new PetCreatureRevivalUpdate(
                inventory.CharacterId,
                detail.Uid,
                before,
                after,
                revived);
        }

        internal static int ResolveEquippedCreatureKey(InventoryService inventory)
        {
            return PetInventoryAccessor.ResolveEquippedCreatureKey(inventory);
        }

        internal static double CalculateFoodConsumeMultiplier(int foodConsumeRatePercent)
        {
            var multiplier = 1.0 + foodConsumeRatePercent / 100.0;
            return multiplier <= 0 ? 0.01 : multiplier;
        }

        private static int CalculateDungeonSatietyAfter(
            int before,
            double elapsedSeconds,
            int foodConsumeRatePercent,
            bool clampAliveMinimum)
        {
            if (before <= 0 || elapsedSeconds <= 0)
                return ClampSatiety(before);

            var stomach = CalculateDungeonStomachValue(before, elapsedSeconds, foodConsumeRatePercent);
            return CalculateVisibleSatiety(stomach, clampAliveMinimum);
        }

        private static double CalculateDungeonStomachValue(
            int before,
            double elapsedSeconds,
            int foodConsumeRatePercent)
        {
            if (before <= 0 || elapsedSeconds <= 0)
                return Math.Max(0, before);

            return before - elapsedSeconds / 60.0 * CalculateFoodConsumeMultiplier(foodConsumeRatePercent);
        }

        private static int CalculateVisibleSatiety(double stomach, bool clampAliveMinimum)
        {
            if (stomach <= 0)
                return 0;
            if (clampAliveMinimum && stomach < 1.0)
                return 1;
            return ClampSatiety((int)stomach);
        }

        private static int CalculateTownSatietyAfter(int before, double elapsedSeconds)
        {
            before = ClampSatiety(before);
            if (elapsedSeconds <= 0 || before >= 100)
                return before;

            var stomach = before + elapsedSeconds / 360.0;
            if (stomach >= 100)
                return 100;
            if (stomach <= 0)
                return 0;

            return (int)stomach;
        }

        private static void SaveSatietyIfChanged(
            InventoryService inventory,
            CreatureDetail detail,
            int before,
            int after)
        {
            after = ClampSatiety(after);
            if (after == before)
                return;

            detail.Stomach = (byte)after;
            inventory.CreatureDetails.PutDirty(detail);
        }

        private static int ClampSatiety(int value)
        {
            return Math.Max(0, Math.Min(100, value));
        }
    }

    internal readonly struct PetCreatureSatietyUpdate
    {
        internal PetCreatureSatietyUpdate(
            int characterId,
            int creatureKey,
            int before,
            int after,
            double elapsedSeconds,
            int satietyDelta,
            bool changed,
            int foodConsumeRatePercent = 0)
        {
            CharacterId = characterId;
            CreatureKey = creatureKey;
            Before = before;
            After = after;
            ElapsedSeconds = elapsedSeconds;
            SatietyDelta = satietyDelta;
            Changed = changed;
            FoodConsumeRatePercent = foodConsumeRatePercent;
        }

        public int CharacterId { get; }
        public int CreatureKey { get; }
        public int Before { get; }
        public int After { get; }
        public double ElapsedSeconds { get; }
        public int SatietyDelta { get; }
        public int ConsumedSatiety => SatietyDelta < 0 ? -SatietyDelta : 0;
        public int RecoveredSatiety => SatietyDelta > 0 ? SatietyDelta : 0;
        public bool Changed { get; }
        public bool StateChanged => Before != After;
        public int FoodConsumeRatePercent { get; }
        public double FoodConsumeMultiplier => PetCreatureSatietyService.CalculateFoodConsumeMultiplier(FoodConsumeRatePercent);

        internal static PetCreatureSatietyUpdate Noop(int characterId, double elapsedSeconds = 0)
            => new PetCreatureSatietyUpdate(characterId, 0, 0, 0, elapsedSeconds, 0, false);
    }

    internal readonly struct PetCreatureRevivalUpdate
    {
        internal PetCreatureRevivalUpdate(int characterId, int creatureKey, int before, int after, bool revived)
        {
            CharacterId = characterId;
            CreatureKey = creatureKey;
            Before = before;
            After = after;
            Revived = revived;
        }

        public int CharacterId { get; }
        public int CreatureKey { get; }
        public int Before { get; }
        public int After { get; }
        public bool Revived { get; }

        internal static PetCreatureRevivalUpdate Noop(int characterId)
            => new PetCreatureRevivalUpdate(characterId, 0, 0, 0, false);
    }
}
