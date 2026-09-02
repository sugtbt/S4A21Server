using PvfLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Inventory
{
    internal static class StackableItemProvider
    {
        internal const string LegacyType = "[legacy]";
        internal const string UpgradableLegacyType = "[upgradable legacy]";
        internal const string RandomUpgradableLegacyType = "[random upgradable legacy]";

        private static readonly object CacheLock = new object();
        private static readonly Dictionary<int, StackableItemFile> Cache =
            new Dictionary<int, StackableItemFile>();

        internal static StackableItemFile Load(int itemTemplateId)
        {
            if (itemTemplateId <= 0)
                return null;

            lock (CacheLock)
            {
                if (Cache.TryGetValue(itemTemplateId, out var cached))
                    return cached;
            }

            try
            {
                var entry = ItemMetadataResolver.GetStackableEntry(itemTemplateId);
                if (entry == null)
                    return null;

                var parsed = StackableItemFile.Parse(
                    GameWorld.PvfArchiveAccessor.ReadText(
                        Path.Combine("stackable", entry.FilePath)));
                lock (CacheLock)
                    Cache[itemTemplateId] = parsed;
                return parsed;
            }
            catch (Exception ex)
            {
                FileLogger.Log(
                    $"  [StackableItemProvider] failed to load item=0x{itemTemplateId:X8}: {ex.Message}");
                return null;
            }
        }

        internal static int ResolveCooltimeGroup(int itemTemplateId)
        {
            var item = Load(itemTemplateId);
            return TryParsePositiveInt(item?.CooltimeGroup, out var group) ? group : 0;
        }

        private static bool TryParsePositiveInt(string raw, out int value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            var text = raw.Trim().Trim('`').Trim();
            var match = Regex.Match(text, @"(?<!\d)\d+");
            return match.Success
                && int.TryParse(match.Value, out value)
                && value > 0;
        }

        internal static bool IsLegacyContainer(int itemTemplateId)
        {
            var stackable = Load(itemTemplateId);
            if (stackable == null)
                return false;

            var type = NormalizeType(stackable.StackableType);
            return type.Equals(UpgradableLegacyType, StringComparison.OrdinalIgnoreCase)
                || type.Equals(RandomUpgradableLegacyType, StringComparison.OrdinalIgnoreCase);
        }

        internal static string NormalizeType(string stackableType)
        {
            if (string.IsNullOrWhiteSpace(stackableType))
                return string.Empty;

            var text = stackableType.Trim();
            var firstQuote = text.IndexOf('`');
            if (firstQuote >= 0)
            {
                var secondQuote = text.IndexOf('`', firstQuote + 1);
                if (secondQuote > firstQuote)
                    return text.Substring(firstQuote + 1, secondQuote - firstQuote - 1).Trim();
            }

            var bracketStart = text.IndexOf('[');
            if (bracketStart >= 0)
            {
                var bracketEnd = text.IndexOf(']', bracketStart + 1);
                if (bracketEnd > bracketStart)
                    return text.Substring(bracketStart, bracketEnd - bracketStart + 1).Trim();
            }

            return text.Replace("`", string.Empty).Trim();
        }
    }
}
