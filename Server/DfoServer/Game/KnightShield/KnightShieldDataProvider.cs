using DfoServer.Game.Characters;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.SelectCharacter;
using DfoServer.GameWorld;
using PvfLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace DfoServer.Game.KnightShield
{
    public static class KnightShieldDataProvider
    {
        public const byte GuardianJob = 12;

        private const string ShieldWindowNewDataPath = "etc/character/knight/shieldwindownewdata.etc";

        private static readonly Lazy<CatalogTables> Catalog = new Lazy<CatalogTables>(LoadCatalog);

        private static readonly ConcurrentDictionary<int, bool> ShieldValidationCache =
            new ConcurrentDictionary<int, bool>();

        public static bool IsEligibleCharacter(CharacterRecord character)
        {
            return character != null
                && IsEligibleCharacter(character.Job);
        }

        public static bool IsEligibleCharacter(byte job)
        {
            return job == GuardianJob;
        }

        public static int NormalizeGrowType(int growType)
        {
            return growType & 0x0F;
        }

        public static IReadOnlyList<KnightShieldCatalogEntry> GetCatalogEntries(int growType)
        {
            var catalog = Catalog.Value;
            var combined = new List<KnightShieldCatalogEntry>();
            AppendGrowEntries(combined, catalog, 0);
            var normalized = NormalizeGrowType(growType);
            if (normalized != 0)
                AppendGrowEntries(combined, catalog, normalized);
            return combined;
        }

        public static bool TryGetCatalogEntry(int growType, int itemId, out KnightShieldCatalogEntry entry)
        {
            entry = null;
            if (itemId <= 0)
                return false;

            var catalog = Catalog.Value;
            if (TryFindEntryInGrow(catalog, 0, itemId, out entry))
                return true;

            var normalized = NormalizeGrowType(growType);
            return normalized != 0
                && TryFindEntryInGrow(catalog, normalized, itemId, out entry);
        }

        public static bool IsCatalogShield(int growType, int itemId)
        {
            if (!IsKnightShield(itemId))
                return false;
            if (TryGetCatalogEntry(growType, itemId, out _))
                return true;
            return NormalizeGrowType(growType) == 0
                && IsStartingSupportWeapon(GuardianJob, itemId);
        }

        public static bool IsCatalogShieldUnlocked(
            byte job,
            int growType,
            int itemId,
            int characterLevel,
            ISet<int> clearedQuestIds)
        {
            if (!IsCatalogShield(growType, itemId))
                return false;
            if (IsStartingSupportWeapon(job, itemId))
                return true;
            if (!TryGetCatalogEntry(growType, itemId, out var entry))
                return false;
            if (entry.UnlockKind == KnightShieldUnlockKind.Level)
                return characterLevel >= entry.RequiredLevel;
            if (clearedQuestIds == null)
                return false;
            if (entry.ClearQuestId > 0
                && clearedQuestIds.Contains(entry.ClearQuestId))
                return true;

            // Some PVF revisions reference a clear quest that is absent from
            // quest.lst; those revisions persist completion under the open
            // quest id instead.
            return entry.OpenQuestId > 0
                && QuestCatalog.Get(entry.ClearQuestId) == null
                && clearedQuestIds.Contains(entry.OpenQuestId);
        }

        public static bool IsStartingSupportWeapon(byte job, int itemId)
        {
            if (itemId <= 0)
                return false;

            var initialEquip = InitialCharacterEquipment.Get(job);
            if (initialEquip == null)
                return false;

            for (var index = 0; index < initialEquip.Length; index++)
            {
                var entry = initialEquip[index];
                if (entry.slot == (short)EquipmentType.SupportWeapon && entry.itemId == itemId)
                    return true;
            }

            return false;
        }

        public static bool IsKnightShield(int itemId)
        {
            if (itemId <= 0)
                return false;

            return ShieldValidationCache.GetOrAdd(itemId, IsKnightShieldCore);
        }

        private static bool IsKnightShieldCore(int itemId)
        {
            try
            {
                if (!ItemMetadataResolver.TryLoadEquipmentFile(itemId, out var equipment))
                    return false;

                return EquipmentTypeInfo.ParseOrUnknown(equipment.EquipmentType) == EquipmentType.SupportWeapon
                    && string.Equals(equipment.ItemGroupName?.Trim(), "shield", StringComparison.OrdinalIgnoreCase)
                    && ContainsKnightJob(equipment);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[KnightShield] PVF validation failed item={itemId}: {ex.Message}");
                return false;
            }
        }

        private static bool ContainsKnightJob(PvfLib.EquipmentFile equipment)
        {
            if (equipment?.Root == null)
                return false;

            foreach (var node in equipment.Root.GetChildren("usable job"))
            {
                foreach (var item in node.DataItems)
                {
                    var label = (item.GetContent(equipment.Content) ?? string.Empty)
                        .Trim()
                        .Trim('`')
                        .Trim();
                    if (label.Length >= 2 && label[0] == '[' && label[label.Length - 1] == ']')
                        label = label.Substring(1, label.Length - 2).Trim();
                    if (string.Equals(label, "knight", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }

            return false;
        }

        private static CatalogTables LoadCatalog()
        {
            var content = PvfArchiveAccessor.ReadText(ShieldWindowNewDataPath);
            var root = new ScriptParser().Parse(content);
            var mutable = new Dictionary<int, List<KnightShieldCatalogEntry>>();
            var seenItemIds = new HashSet<int>();

            foreach (var tab in root.GetChildren("tab"))
            {
                foreach (var shield in tab.GetChildren("shield"))
                {
                    var itemId = ReadRequiredInt(shield, "item index", content);
                    var rawGrowType = ReadRequiredInt(shield, "grow type", content);
                    var growType = NormalizeGrowType(rawGrowType);
                    var unlockKind = ReadUnlockKind(shield, content);
                    var openQuestId = 0;
                    var clearQuestId = 0;
                    var requiredLevel = 0;
                    if (unlockKind == KnightShieldUnlockKind.Level)
                    {
                        requiredLevel = ReadRequiredInt(shield, "get shield level", content);
                    }
                    else
                    {
                        openQuestId = ReadRequiredInt(shield, "open quest index", content);
                        clearQuestId = ReadRequiredInt(shield, "clear quest index", content);
                    }

                    if (itemId <= 0
                        || rawGrowType < 0
                        || (unlockKind == KnightShieldUnlockKind.Level && requiredLevel <= 0)
                        || (unlockKind == KnightShieldUnlockKind.Quest
                            && (openQuestId <= 0 || clearQuestId <= 0)))
                    {
                        throw new FormatException(
                            $"{ShieldWindowNewDataPath} contains invalid shield item={itemId} growType={growType} kind={unlockKind}");
                    }

                    if (!seenItemIds.Add(itemId))
                    {
                        throw new FormatException(
                            $"{ShieldWindowNewDataPath} duplicates shield item={itemId}");
                    }

                    if (!mutable.TryGetValue(growType, out var entries))
                    {
                        entries = new List<KnightShieldCatalogEntry>();
                        mutable.Add(growType, entries);
                    }

                    entries.Add(new KnightShieldCatalogEntry(
                        itemId,
                        growType,
                        unlockKind,
                        openQuestId,
                        clearQuestId,
                        requiredLevel));
                }
            }

            var entriesByGrow = new Dictionary<int, IReadOnlyList<KnightShieldCatalogEntry>>();
            foreach (var pair in mutable)
                entriesByGrow.Add(pair.Key, pair.Value.AsReadOnly());

            return new CatalogTables(
                new ReadOnlyDictionary<int, IReadOnlyList<KnightShieldCatalogEntry>>(entriesByGrow));
        }

        private static KnightShieldUnlockKind ReadUnlockKind(ScriptNode shield, string content)
        {
            var label = ReadRequiredLabel(shield, "get condition", content);
            if (string.Equals(label, "quest", StringComparison.OrdinalIgnoreCase))
                return KnightShieldUnlockKind.Quest;
            if (string.Equals(label, "level", StringComparison.OrdinalIgnoreCase))
                return KnightShieldUnlockKind.Level;

            throw new FormatException($"{ShieldWindowNewDataPath} has unsupported [get condition] `{label}`");
        }

        private static void AppendGrowEntries(
            List<KnightShieldCatalogEntry> destination,
            CatalogTables catalog,
            int growType)
        {
            if (!catalog.EntriesByGrow.TryGetValue(growType, out var entries) || entries == null)
                return;
            for (var index = 0; index < entries.Count; index++)
                destination.Add(entries[index]);
        }

        private static bool TryFindEntryInGrow(
            CatalogTables catalog,
            int growType,
            int itemId,
            out KnightShieldCatalogEntry entry)
        {
            entry = null;
            return catalog.EntriesByGrow.TryGetValue(growType, out var entries)
                && TryFindEntry(entries, itemId, out entry);
        }

        private static bool TryFindEntry(
            IReadOnlyList<KnightShieldCatalogEntry> entries,
            int itemId,
            out KnightShieldCatalogEntry entry)
        {
            if (entries != null)
            {
                for (var index = 0; index < entries.Count; index++)
                {
                    if (entries[index].ItemId == itemId)
                    {
                        entry = entries[index];
                        return true;
                    }
                }
            }

            entry = null;
            return false;
        }

        private static int ReadRequiredInt(ScriptNode parent, string tag, string content)
        {
            foreach (var node in parent.GetChildren(tag))
            {
                if (node.DataItems.Count > 0
                    && int.TryParse(node.GetFirstDataContent(content).Trim(), out var value))
                    return value;
            }

            throw new FormatException($"{ShieldWindowNewDataPath} shield entry is missing [{tag}]");
        }

        private static string ReadRequiredLabel(ScriptNode parent, string tag, string content)
        {
            foreach (var node in parent.GetChildren(tag))
            {
                if (node.DataItems.Count == 0)
                    continue;
                var label = (node.GetFirstDataContent(content) ?? string.Empty)
                    .Trim()
                    .Trim('`')
                    .Trim();
                if (label.Length > 0)
                    return label;
            }

            throw new FormatException($"{ShieldWindowNewDataPath} shield entry is missing [{tag}]");
        }

        private sealed class CatalogTables
        {
            public CatalogTables(
                IReadOnlyDictionary<int, IReadOnlyList<KnightShieldCatalogEntry>> entriesByGrow)
            {
                EntriesByGrow = entriesByGrow;
            }

            public IReadOnlyDictionary<int, IReadOnlyList<KnightShieldCatalogEntry>> EntriesByGrow { get; }
        }
    }
}
