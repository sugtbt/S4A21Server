using DfoServer.GameWorld;
using DfoServer.Game.Currency;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Infrastructure;
using PvfLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace DfoServer.Game.Inventory
{
    public sealed class ItemMetadata
    {
        public string ItemKind { get; set; } = "unknown";

        public string StackableType { get; set; }

        public string PvfFilePath { get; set; }

        public int BuyGold { get; set; }

        public int BuyCoin { get; set; }

        public int SellGold { get; set; }

        /// <summary>PVF item weight used by inventory-capacity checks.</summary>
        public int Weight { get; set; }

        public ushort Durability { get; set; }

        public int StackLimit { get; set; }

        public int NeedMaterialId { get; set; }

        public int NeedMaterialCount { get; set; }

        public int Grade { get; set; }

        public int MinimumLevel { get; set; }

        public int Rarity { get; set; }

        public string EquipmentType { get; set; }

        public string ItemCategory { get; set; }

        public string AttachType { get; set; }

        /// <summary>
        /// Maximum number of successful transfers for PVF [trade limit] items.
        /// The 86 client stores the remaining count in the high three bits of
        /// the common inventory attr/extData0 byte.
        /// </summary>
        public int TradeLimitMax { get; set; }

        public IReadOnlyList<string> ImpossibleContents { get; set; } = Array.Empty<string>();

        public bool IsSealed => string.Equals(AttachType?.Trim('[', ']', ' '), "sealing", StringComparison.OrdinalIgnoreCase);

        public bool IsStackable => string.Equals(ItemKind, "stackable", StringComparison.Ordinal);

        public bool IsMaterialExchange => NeedMaterialId > 0 && NeedMaterialCount > 0;

        internal static ItemMetadata CreateDefaultStackable()
            => new ItemMetadata { ItemKind = "stackable" };

        internal bool IsPrimaryStackableFamily(string family)
        {
            if (string.IsNullOrWhiteSpace(family)
                || !TryGetPrimaryStackableTag(out var tag))
            {
                return false;
            }

            var familyEnd = 0;
            while (familyEnd < tag.Length && !char.IsWhiteSpace(tag[familyEnd]))
                familyEnd++;
            if (familyEnd == 0)
                return false;

            return string.Equals(
                tag.Substring(0, familyEnd),
                family.Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        public void GetSlotRange(out int slotStart, out int slotEnd)
        {
            if (string.Equals(ItemKind, "equipment", StringComparison.Ordinal))
            {
                if (IsFlagEquipmentType())
                { slotStart = 0; slotEnd = 48; return; }

                slotStart = 9; slotEnd = 64; return;
            }
            var st = NormalizeStackableType();
            TryGetPrimaryStackableTag(out var primaryTag);
            if (string.Equals(primaryTag, "flag gem", StringComparison.OrdinalIgnoreCase))
            { slotStart = 49; slotEnd = 97; return; }
            if (string.Equals(primaryTag, "material expert job", StringComparison.OrdinalIgnoreCase))
            { slotStart = 233; slotEnd = 288; return; }
            if (string.Equals(primaryTag, "avatar emblem", StringComparison.OrdinalIgnoreCase))
            { slotStart = 289; slotEnd = 344; return; }
            if (IsPrimaryStackableFamily("material"))
            {
                if (st.EndsWith("4", StringComparison.Ordinal))
                { slotStart = 345; slotEnd = 359; }
                else
                { slotStart = 121; slotEnd = 176; }
                return;
            }
            if (IsPrimaryStackableFamily("quest"))
            { slotStart = 177; slotEnd = 232; return; }
            slotStart = 65; slotEnd = 120;
        }

        private bool TryGetPrimaryStackableTag(out string tag)
        {
            tag = string.Empty;
            if (!IsStackable)
                return false;

            var normalized = NormalizeStackableType();
            if (normalized.Length < 3 || normalized[0] != '[')
                return false;

            var end = normalized.IndexOf(']', 1);
            if (end <= 1)
                return false;

            tag = normalized.Substring(1, end - 1).Trim();
            return tag.Length > 0;
        }

        private string NormalizeStackableType()
            => (StackableType ?? string.Empty).Replace("`", string.Empty).Trim();

        private bool IsFlagEquipmentType()
        {
            var normalized = (EquipmentType ?? string.Empty).Replace("`", string.Empty).Trim();
            return normalized.StartsWith("[flag]", StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class ItemSellRates
    {
        public int Equipment { get; set; } = 200;  
        public int NonStackable { get; set; } = 150; 
        public int Stackable { get; set; } = 30;    

        public static ItemSellRates Parse(string content)
        {
            var r = new ItemSellRates();
            if (string.IsNullOrEmpty(content)) return r;
            
            var m = System.Text.RegularExpressions.Regex.Match(content, @"\]\s*\r?\n\s*(\d+)\s+(\d+)\s+(\d+)");
            if (m.Success)
            {
                r.Equipment = int.Parse(m.Groups[1].Value);
                r.NonStackable = int.Parse(m.Groups[2].Value);
                r.Stackable = int.Parse(m.Groups[3].Value);
            }
            return r;
        }
    }

    public static class ItemMetadataResolver 
    {
        internal static readonly Lazy<LstFile> EquipmentList = new Lazy<LstFile>(() => LstFile.Parse(PvfArchiveAccessor.ReadText("equipment/equipment.lst")));
        private static readonly Lazy<LstFile> StackableList = new Lazy<LstFile>(() => LstFile.Parse(PvfArchiveAccessor.ReadText("stackable/stackable.lst")));
        private static readonly Lazy<ItemSellRates> SellRates = new Lazy<ItemSellRates>(() => ItemSellRates.Parse(PvfArchiveAccessor.ReadText("equipment/pricetable.tbl")));
        private static readonly ConcurrentDictionary<int, Lazy<ItemMetadata>> MetadataCache
            = new ConcurrentDictionary<int, Lazy<ItemMetadata>>();
        // PvfArchiveAccessor与equipment.lst都是进程级不可变Lazy，装备类型也按进程缓存。
        private static readonly ConcurrentDictionary<int, Lazy<string>> EquipmentTypeCache
            = new ConcurrentDictionary<int, Lazy<string>>();
        private static readonly ConcurrentDictionary<int, Lazy<byte>> EmblemSocketTypeCache
            = new ConcurrentDictionary<int, Lazy<byte>>();
        private static readonly ConcurrentDictionary<int, Lazy<EquipmentFile>> EquipmentFileCache
            = new ConcurrentDictionary<int, Lazy<EquipmentFile>>();
        private static readonly ConcurrentDictionary<int, Lazy<StackableItemFile>> StackableFileCache
            = new ConcurrentDictionary<int, Lazy<StackableItemFile>>();
        private static readonly Regex AvatarSocketRegex = new Regex(@"\[\s*([ABCDSM])\s+socket\s*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private const string AvatarTypeSelectTag = "[avatar type select]";
        private const string AvatarTypeSelectEndTag = "[/avatar type select]";
        private const string EmblemSocketDefaultTag = "[emblem socket default]";
        private const string EmblemSocketDefaultEndTag = "[/emblem socket default]";
        private const string AvatarEmblemSocketNumTag = "[avatar emblem socket num]";

        public static void Warmup()
        {
            _ = EquipmentList.Value;
            _ = StackableList.Value;
        }

        internal static bool AreItemListsWarmed
            => EquipmentList.IsValueCreated && StackableList.IsValueCreated;

        public static ItemMetadata Resolve(int itemTemplateId)
        {
            return MetadataCache.GetOrAdd(
                itemTemplateId,
                id => new Lazy<ItemMetadata>(() => ResolveCore(id))).Value;
        }

        private static ItemMetadata ResolveCore(int itemTemplateId)
        {
            var equipmentEntry = EquipmentList.Value.GetById(itemTemplateId);
            if (equipmentEntry != null)
            {
                if (!TryLoadEquipmentFile(itemTemplateId, out var equipment))
                    return CreateUnknownMetadata();
                ResolveNeedMaterial(equipment.NeedMaterial, out var equipmentNeedMatId, out var equipmentNeedMatCount);
                // Keep legacy ordinary-NPC pricing intact.  Only entries that
                // actually exchange [need material] use PVF's price correction.
                var buyGold = equipmentNeedMatId > 0 && equipmentNeedMatCount > 0
                    ? ResolveBuyGold(equipment.Price, equipment.AddPrice)
                    : Math.Max(0, equipment.Price >= 0 ? equipment.Price : equipment.Value);
                
                
                var baseSellPrice = equipment.Value >= 0 ? equipment.Value : buyGold;
                var sellGold = Math.Max(1, baseSellPrice * SellRates.Value.Equipment / 1000);
                // 只有武器和防具有耐久度，其他装备类型（首饰/魔法石/称号/装扮/宠物等）无耐久。
                var eqType = NormalizeEquipmentType(equipment.EquipmentType);
                var hasDurability = equipment.Durability > 0 && HasDurabilityByType(eqType);
                var durability = hasDurability ? equipment.Durability : 0;

                return new ItemMetadata
                {
                    ItemKind = "equipment",
                    PvfFilePath = equipmentEntry.FilePath,
                    BuyGold = buyGold,
                    SellGold = sellGold,
                    Weight = Math.Max(0, equipment.Weight),
                    Durability = (ushort)durability,
                    StackLimit = 1,
                    Grade = equipment.Grade,
                    MinimumLevel = equipment.MinimumLevel,
                    Rarity = equipment.Rarity,
                    EquipmentType = NormalizeEquipmentType(equipment.EquipmentType),
                    ItemCategory = equipment.ItemCategory,
                    AttachType = equipment.AttachType,
                    ImpossibleContents = equipment.ImpossibleContentItems,
                    NeedMaterialId = equipmentNeedMatId,
                    NeedMaterialCount = equipmentNeedMatCount,
                };
            }

            var stackableEntry = StackableList.Value.GetById(itemTemplateId);
            if (stackableEntry != null)
            {
                if (!TryLoadStackableFile(itemTemplateId, out var stackable))
                    return CreateUnknownMetadata();
                var sellGold = stackable.Value >= 0
                    ? stackable.Value / 5
                    : (stackable.Price > 0 ? stackable.Price / 5 : 0);

                int needMatId = 0, needMatCount = 0;
                if (!string.IsNullOrWhiteSpace(stackable.NeedMaterial))
                {
                    var parts = stackable.NeedMaterial.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        int.TryParse(parts[0], out needMatId);
                        int.TryParse(parts[1], out needMatCount);
                    }
                }

                
                
                // Keep legacy ordinary-NPC pricing intact.  Material exchanges
                // are the only path where [add price] corrects [price] and
                // [value] must never become a purchase cost.
                var buyGold = needMatId > 0 && needMatCount > 0
                    ? ResolveBuyGold(stackable.Price, stackable.AddPrice)
                    : Math.Max(0, stackable.Price >= 0 ? stackable.Price : stackable.Value);
                return new ItemMetadata
                {
                    ItemKind = "stackable",
                    StackableType = stackable.StackableType,
                    PvfFilePath = stackableEntry.FilePath,
                    BuyGold = buyGold,
                    SellGold = sellGold,
                    Weight = Math.Max(0, stackable.Weight),
                    Durability = 0,
                    StackLimit = stackable.StackLimit,
                    NeedMaterialId = needMatId,
                    NeedMaterialCount = needMatCount,
                    Grade = stackable.Grade,
                    MinimumLevel = stackable.MinimumLevel,
                    Rarity = stackable.Rarity,
                    ItemCategory = stackable.ItemCategory,
                    AttachType = stackable.AttachType,
                    TradeLimitMax = Math.Max(0, stackable.TradeLimit),
                    ImpossibleContents = stackable.ImpossibleContentItems,
                };
            }

            return CreateUnknownMetadata();
        }

        private static ItemMetadata CreateUnknownMetadata()
        {
            return new ItemMetadata
            {
                ItemKind = "special",
                BuyGold = 0,
                SellGold = 1,
                Durability = 0,
                StackLimit = 1,
            };
        }

        public static LstEntry GetStackableEntry(int itemTemplateId)
        {
            return StackableList.Value.GetById(itemTemplateId);
        }

        // [value] is the NPC sell value only.  NPC purchase cost comes solely
        // from [price], adjusted by the optional signed [add price] field.
        // A missing [price] therefore means a zero-gold purchase, even when an
        // exchange also defines [need material].
        internal static int ResolveBuyGold(int price, int addPrice)
        {
            if (price < 0)
                return 0;

            var effectivePrice = (long)price + addPrice;
            return effectivePrice <= 0 ? 0 : effectivePrice > int.MaxValue ? int.MaxValue : (int)effectivePrice;
        }

        private static void ResolveNeedMaterial(string needMaterial, out int itemId, out int count)
        {
            itemId = 0;
            count = 0;
            if (string.IsNullOrWhiteSpace(needMaterial))
                return;

            var parts = needMaterial.Trim().Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return;

            int.TryParse(parts[0], out itemId);
            int.TryParse(parts[1], out count);
            if (itemId <= 0 || count <= 0)
            {
                itemId = 0;
                count = 0;
            }
        }

        public static LstEntry GetEquipmentEntry(int itemTemplateId)
        {
            return EquipmentList.Value.GetById(itemTemplateId);
        }

        public static bool TryLoadEquipmentFile(int itemTemplateId, out EquipmentFile equipment)
        {
            equipment = EquipmentFileCache.GetOrAdd(
                itemTemplateId,
                id => new Lazy<EquipmentFile>(() => LoadEquipmentFile(id))).Value;
            return equipment != null;
        }

        internal static bool IsTitleEquipment(int itemTemplateId)
        {
            return TryLoadEquipmentFile(itemTemplateId, out var equipment)
                && EquipmentTypeInfo.ParseOrUnknown(equipment?.EquipmentType)
                    == EquipmentType.TitleName;
        }

        // 快捷栏纹章 [exp advantage] 杀怪经验加成(百分比)。非纹章/无该字段返回 0。
        public static int ResolveExpAdvantage(int itemTemplateId)
        {
            if (itemTemplateId <= 0)
                return 0;
            return TryLoadEquipmentFile(itemTemplateId, out var equipment)
                ? Math.Max(0, equipment.ExpAdvantage)
                : 0;
        }

        internal static bool IsEquipmentUsableByJob(int itemTemplateId, byte characterJob)
        {
            if (!TryLoadEquipmentFile(itemTemplateId, out var equipment)
                || equipment?.Root == null)
            {
                return false;
            }

            var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var usableJob in equipment.Root.GetChildren("usable job"))
            {
                foreach (var dataItem in usableJob.DataItems)
                {
                    foreach (Match match in Regex.Matches(
                        dataItem.GetContent(equipment.Content) ?? string.Empty,
                        @"\[(?<job>[^\]]+)\]",
                        RegexOptions.IgnoreCase))
                    {
                        var label = match.Groups["job"].Value.Trim();
                        if (label.Length > 0)
                            labels.Add(label);
                    }
                }
            }

            if (labels.Count == 0 || labels.Contains("all"))
                return true;

            var jobLabel = ResolveCharacterJobLabel(characterJob);
            return jobLabel.Length > 0 && labels.Contains(jobLabel);
        }

        private static string ResolveCharacterJobLabel(byte characterJob)
        {
            switch (characterJob)
            {
                case 0: return "swordman";
                case 1: return "fighter";
                case 2: return "gunner";
                case 3: return "mage";
                case 4: return "priest";
                case 5: return "at gunner";
                case 6: return "thief";
                case 7: return "at fighter";
                case 8: return "at mage";
                case 9: return "demonic swordman";
                case 10: return "creator mage";
                case 11: return "at swordman";
                case 12: return "knight";
                default: return string.Empty;
            }
        }

        public static bool TryLoadStackableFile(int itemTemplateId, out StackableItemFile stackable)
        {
            return TryLoadStackable(itemTemplateId, out stackable);
        }

        public static string ResolveEquipmentType(int itemTemplateId)
        {
            return TryGetEquipmentType(itemTemplateId, out var equipmentType)
                ? equipmentType
                : null;
        }

        internal static bool TryResolveItemKind(int itemTemplateId, out byte itemKind)
        {
            itemKind = ItemCore.KindUnknown;
            if (itemTemplateId <= 0)
                return false;

            ItemMetadata metadata;
            try
            {
                metadata = Resolve(itemTemplateId);
            }
            catch
            {
                return false;
            }

            return TryResolveItemKind(itemTemplateId, metadata, out itemKind);
        }

        internal static bool TryResolveItemKind(int itemTemplateId, ItemMetadata metadata, out byte itemKind)
        {
            itemKind = ItemCore.KindUnknown;
            if (EpicPieceCatalogService.IsEpicPieceId(itemTemplateId))
            {
                itemKind = ItemCore.KindEpicPiece;
                return true;
            }

            if (metadata == null)
                return false;

            if (string.Equals(metadata.ItemKind, "equipment", StringComparison.Ordinal))
            {
                itemKind = ResolveEquipmentItemKind(metadata);
                return true;
            }

            if (metadata.IsStackable)
            {
                itemKind = ResolveStackableItemKind(itemTemplateId, metadata);
                return true;
            }

            return false;
        }

        public static byte ResolveEmblemSocketType(int itemTemplateId)
        {
            return EmblemSocketTypeCache.GetOrAdd(
                itemTemplateId,
                id => new Lazy<byte>(() => ResolveEmblemSocketTypeCore(id))).Value;
        }

        private static byte ResolveEmblemSocketTypeCore(int itemTemplateId)
        {
            var stackableEntry = StackableList.Value.GetById(itemTemplateId);
            if (stackableEntry == null)
                return 0;

            StackableItemFile stackable;
            try
            {
                stackable = StackableItemFile.Parse(PvfArchiveAccessor.ReadText(Path.Combine("stackable", stackableEntry.FilePath)));
            }
            catch (Exception ex)
            {
                FileLogger.Log($"  [EmblemAttach] ResolveEmblemSocketType(0x{itemTemplateId:X8}) failed: {ex.Message}");
                return 0;
            }

            return stackable.AvatarEmblemSocketType;
        }

        public static IReadOnlyList<byte> ResolveAvatarSocketTypes(int itemTemplateId)
        {
            return ResolveAvatarOpenSocketTypes(itemTemplateId);
        }

        public static IReadOnlyList<byte> ResolveAvatarOpenSocketTypes(int itemTemplateId)
        {
            var result = new List<byte>();
            var equipmentEntry = EquipmentList.Value.GetById(itemTemplateId);
            if (equipmentEntry == null)
                return result;

            try
            {
                var text = PvfArchiveAccessor.ReadText(Path.Combine("equipment", equipmentEntry.FilePath));
                var section = ExtractAvatarTypeSelectSection(text);
                AddAvatarSocketMatches(result, section, 5);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"  [AvatarSocket] ResolveAvatarOpenSocketTypes(0x{itemTemplateId:X8}) failed: {ex.Message}");
            }

            return result;
        }

        public static IReadOnlyList<byte> ResolveAvatarDefaultSocketTypes(int itemTemplateId)
        {
            var result = new List<byte>();
            var equipmentEntry = EquipmentList.Value.GetById(itemTemplateId);
            if (equipmentEntry == null)
                return result;

            try
            {
                var text = PvfArchiveAccessor.ReadText(Path.Combine("equipment", equipmentEntry.FilePath));
                AddAvatarSocketMatches(result, ExtractEmblemSocketDefaultSection(text), ResolveAvatarEmblemSocketNum(text));
            }
            catch (Exception ex)
            {
                FileLogger.Log($"  [AvatarSocket] ResolveAvatarDefaultSocketTypes(0x{itemTemplateId:X8}) failed: {ex.Message}");
            }

            return result;
        }

        private static void AddAvatarSocketMatches(List<byte> result, string section, int maxCount)
        {
            if (result == null || string.IsNullOrEmpty(section) || maxCount <= 0)
                return;

            foreach (Match match in AvatarSocketRegex.Matches(section))
            {
                if (!match.Success || match.Groups.Count < 2)
                    continue;

                if (TryMapAvatarSocketCode(match.Groups[1].Value[0], out var socketType))
                {
                    result.Add(socketType);
                    if (result.Count >= maxCount)
                        break;
                }
            }
        }

        public static byte ResolveAvatarSocketType(int itemTemplateId)
        {
            var socketTypes = ResolveAvatarSocketTypes(itemTemplateId);
            return socketTypes != null && socketTypes.Count > 0 ? socketTypes[0] : (byte)0;
        }

        private static string ExtractAvatarTypeSelectSection(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var start = text.IndexOf(AvatarTypeSelectTag, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return string.Empty;

            start += AvatarTypeSelectTag.Length;
            var end = text.IndexOf(AvatarTypeSelectEndTag, start, StringComparison.OrdinalIgnoreCase);
            return end > start ? text.Substring(start, end - start) : text.Substring(start);
        }

        private static string ExtractEmblemSocketDefaultSection(string text)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;

            var start = text.IndexOf(EmblemSocketDefaultTag, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return string.Empty;

            start += EmblemSocketDefaultTag.Length;
            var end = text.IndexOf(EmblemSocketDefaultEndTag, start, StringComparison.OrdinalIgnoreCase);
            return end > start ? text.Substring(start, end - start) : text.Substring(start);
        }

        private static int ResolveAvatarEmblemSocketNum(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 5;

            var start = text.IndexOf(AvatarEmblemSocketNumTag, StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return 5;

            start += AvatarEmblemSocketNumTag.Length;
            var end = text.IndexOf('[', start);
            var section = end > start ? text.Substring(start, end - start) : text.Substring(start);
            foreach (var token in section.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(token, out var count))
                    return Math.Max(0, Math.Min(5, count));
            }

            return 5;
        }

        private static bool TryMapAvatarSocketCode(char code, out byte socketType)
        {
            switch (char.ToUpperInvariant(code))
            {
                case 'A':
                    socketType = 0x01;
                    return true;
                case 'B':
                    socketType = 0x02;
                    return true;
                case 'C':
                    socketType = 0x04;
                    return true;
                case 'D':
                    socketType = 0x08;
                    return true;
                case 'S':
                    socketType = 0x10;
                    return true;
                case 'M':
                    socketType = 0xEF;
                    return true;
                default:
                    socketType = 0;
                    return false;
            }
        }

        public static bool TryValidateEnchantByBeadTarget(int beadItemTemplateId, int targetItemTemplateId, byte enchantUpgradeCount, out int enchantCardItemId, out string rejectReason)
        {
            enchantCardItemId = 0;
            rejectReason = null;

            if (!TryLoadStackable(beadItemTemplateId, out var bead))
            {
                rejectReason = "bead is not found in stackable.lst";
                return false;
            }

            if (!TryResolveBeadEnchantCardId(bead, out enchantCardItemId, out var requiresCardValidation))
            {
                rejectReason = "bead has no monster card id/enchant index";
                return false;
            }

            // 宝珠直接声明 target item id 时，只有白名单装备能被附魔。
            if (bead.TargetItemIds != null && bead.TargetItemIds.Count > 0 && !bead.TargetItemIds.Contains(targetItemTemplateId))
            {
                rejectReason = "target item id is not allowed by bead target item id";
                return false;
            }

            if (bead.BeadLimitedUsableItemIds != null
                && bead.BeadLimitedUsableItemIds.Count > 0
                && !bead.BeadLimitedUsableItemIds.Contains(targetItemTemplateId))
            {
                rejectReason = "target item id is not allowed by bead limited usable item";
                return false;
            }

            if (!TryGetEquipmentType(targetItemTemplateId, out var targetEquipmentType))
            {
                rejectReason = "target is not found in equipment.lst";
                return false;
            }

            StackableItemFile card = null;
            if (requiresCardValidation)
            {
                if (!TryLoadStackable(enchantCardItemId, out card))
                {
                    rejectReason = "monster card is not found in stackable.lst";
                    return false;
                }
            }
            else
                TryLoadStackable(enchantCardItemId, out card);

            if (card != null
                && !TryValidateMonsterCardTargetMetadata(
                    card,
                    targetEquipmentType,
                    enchantUpgradeCount,
                    requireAllowedType: false,
                    upgradedItemName: "bead",
                    out rejectReason))
                return false;

            if (card == null && enchantUpgradeCount != 0)
            {
                rejectReason = "upgraded enchant bead requires monster card enchant table";
                return false;
            }

            return true;
        }

        internal static bool TryValidateMonsterCardTarget(
            int cardItemTemplateId,
            int targetItemTemplateId,
            byte enchantUpgradeCount,
            out string rejectReason)
        {
            rejectReason = null;
            if (!TryLoadStackable(cardItemTemplateId, out var card)
                || !IsEnchanterCard(card))
            {
                rejectReason = "item is not a monster card";
                return false;
            }

            if (!TryGetEquipmentType(targetItemTemplateId, out var targetEquipmentType))
            {
                rejectReason = "target is not found in equipment.lst";
                return false;
            }

            return TryValidateMonsterCardTargetMetadata(
                card,
                targetEquipmentType,
                enchantUpgradeCount,
                requireAllowedType: true,
                upgradedItemName: "card",
                out rejectReason);
        }

        public static bool TryValidatePetEnchantByBeadTarget(int beadItemTemplateId, int targetItemTemplateId, byte enchantUpgradeCount, out int enchantCardItemId, out string rejectReason)
        {
            enchantCardItemId = 0;
            rejectReason = null;

            if (!TryLoadStackable(beadItemTemplateId, out var bead))
            {
                rejectReason = "bead is not found in stackable.lst";
                return false;
            }

            if (!TryResolveBeadEnchantCardId(bead, out enchantCardItemId, out var requiresCardValidation))
            {
                rejectReason = "bead has no monster card id/enchant index";
                return false;
            }

            if (bead.TargetItemIds != null && bead.TargetItemIds.Count > 0 && !bead.TargetItemIds.Contains(targetItemTemplateId))
            {
                rejectReason = "target item id is not allowed by bead target item id";
                return false;
            }

            if (bead.BeadLimitedUsableItemIds != null
                && bead.BeadLimitedUsableItemIds.Count > 0
                && !bead.BeadLimitedUsableItemIds.Contains(targetItemTemplateId))
            {
                rejectReason = "target item id is not allowed by bead limited usable item";
                return false;
            }

            StackableItemFile card = null;
            if (requiresCardValidation)
            {
                if (!TryLoadStackable(enchantCardItemId, out card))
                {
                    rejectReason = "monster card is not found in stackable.lst";
                    return false;
                }
            }
            else
                TryLoadStackable(enchantCardItemId, out card);

            if (card != null
                && !TryValidateMonsterCardTargetMetadata(
                    card,
                    "[creature]",
                    enchantUpgradeCount,
                    requireAllowedType: true,
                    upgradedItemName: "bead",
                    out rejectReason))
                return false;

            if (card == null && enchantUpgradeCount != 0)
            {
                rejectReason = "upgraded enchant bead requires monster card enchant table";
                return false;
            }

            return true;
        }

        private static bool TryValidateMonsterCardTargetMetadata(
            StackableItemFile card,
            string targetEquipmentType,
            byte enchantUpgradeCount,
            bool requireAllowedType,
            string upgradedItemName,
            out string rejectReason)
        {
            rejectReason = null;
            var allowedTypes = ExtractAllowedEquipmentTypes(card.StringDataItems);
            if ((requireAllowedType && allowedTypes.Count == 0)
                || (allowedTypes.Count > 0 && !allowedTypes.Contains(targetEquipmentType)))
            {
                rejectReason = "target equipment type is not allowed by monster card string data";
                return false;
            }

            if (card.EnchantTable.Count > 0)
            {
                if (!card.EnchantTable.Contains(enchantUpgradeCount))
                {
                    rejectReason = "enchant upgrade count is not allowed by monster card enchant table";
                    return false;
                }
                return true;
            }

            if (enchantUpgradeCount != 0)
            {
                rejectReason = $"monster card has no enchant table for upgraded {upgradedItemName}";
                return false;
            }
            return true;
        }

        private static bool TryResolveBeadEnchantCardId(
            StackableItemFile bead,
            out int enchantCardItemId,
            out bool requiresCardValidation)
        {
            enchantCardItemId = 0;
            requiresCardValidation = false;
            if (bead == null)
                return false;

            if (TryPickMonsterCardId(bead, out enchantCardItemId))
            {
                requiresCardValidation = true;
                return true;
            }

            if (bead.EnchantIndex > 0)
            {
                enchantCardItemId = bead.EnchantIndex;
                return true;
            }

            return false;
        }

        private static bool TryPickMonsterCardId(StackableItemFile bead, out int monsterCardItemId)
        {
            monsterCardItemId = 0;
            if (bead == null)
                return false;

            var candidates = new List<int>();
            if (bead.MonsterCardIds != null)
            {
                foreach (var itemId in bead.MonsterCardIds)
                {
                    if (itemId > 0)
                        candidates.Add(itemId);
                }
            }
            if (candidates.Count == 0 && bead.MonsterCardId > 0)
                candidates.Add(bead.MonsterCardId);
            if (candidates.Count == 0)
                return false;

            monsterCardItemId = candidates.Count == 1
                ? candidates[0]
                : candidates[ServerRandom.Next(candidates.Count)];
            return true;
        }

        private static bool TryLoadStackable(int itemTemplateId, out StackableItemFile stackable)
        {
            stackable = StackableFileCache.GetOrAdd(
                itemTemplateId,
                id => new Lazy<StackableItemFile>(() => LoadStackableFile(id))).Value;
            return stackable != null;
        }

        private static bool TryGetEquipmentType(int itemTemplateId, out string equipmentType)
        {
            var equipmentList = EquipmentList.Value;
            var cached = EquipmentTypeCache.GetOrAdd(
                itemTemplateId,
                id => new Lazy<string>(() => LoadEquipmentType(equipmentList, id)));
            equipmentType = cached.Value;
            return !string.IsNullOrWhiteSpace(equipmentType);
        }

        private static string LoadEquipmentType(LstFile equipmentList, int itemTemplateId)
        {
            if (!TryLoadEquipmentFile(itemTemplateId, out var equipment))
                return null;
            return NormalizeEquipmentType(equipment.EquipmentType);
        }

        private static EquipmentFile LoadEquipmentFile(int itemTemplateId)
        {
            var entry = EquipmentList.Value.GetById(itemTemplateId);
            return entry == null
                ? null
                : EquipmentFile.Parse(PvfArchiveAccessor.ReadText(
                    Path.Combine("equipment", entry.FilePath)));
        }

        private static StackableItemFile LoadStackableFile(int itemTemplateId)
        {
            var entry = StackableList.Value.GetById(itemTemplateId);
            return entry == null
                ? null
                : StackableItemFile.Parse(PvfArchiveAccessor.ReadText(
                    Path.Combine("stackable", entry.FilePath)));
        }

        private static HashSet<string> ExtractAllowedEquipmentTypes(List<string> stringDataItems)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (stringDataItems == null || stringDataItems.Count <= 1)
                return result;

            for (var i = 1; i < stringDataItems.Count; i++)
            {
                var normalized = NormalizeEquipmentType(stringDataItems[i]);
                if (!string.IsNullOrWhiteSpace(normalized))
                    result.Add(normalized);
            }

            return result;
        }

        private static string NormalizeItemCategory(string raw)
            => (raw ?? string.Empty).Replace("`", string.Empty).Trim();

        internal static bool IsMonsterCardCategory(string raw)
        {
            var normalized = NormalizeItemCategory(raw);
            const string category = "monster card";
            return normalized.StartsWith(category, StringComparison.OrdinalIgnoreCase)
                && (normalized.Length == category.Length
                    || char.IsWhiteSpace(normalized[category.Length]));
        }

        internal static bool IsMonsterCard(StackableItemFile card)
        {
            return card != null && IsMonsterCardCategory(card.ItemCategory);
        }

        internal static bool IsMonsterCardBead(StackableItemFile bead)
        {
            if (bead == null)
                return false;

            if (bead.MonsterCardIds != null)
            {
                foreach (var itemId in bead.MonsterCardIds)
                {
                    if (itemId > 0)
                        return true;
                }
            }

            return bead.MonsterCardId > 0;
        }

        internal static bool IsEnchanterCard(StackableItemFile card)
        {
            if (card == null)
                return false;

            var stackableType = NormalizeItemCategory(card.StackableType);
            return IsMonsterCard(card)
                || (string.Equals(
                        stackableType,
                        "[material expert job] 1",
                        StringComparison.OrdinalIgnoreCase)
                    && card.EnchantTable.Count > 0
                    && ExtractAllowedEquipmentTypes(card.StringDataItems).Count > 0);
        }

        private static bool HasDurabilityByType(string normalizedType)
        {
            if (string.IsNullOrEmpty(normalizedType))
                return false;
            // 武器
            if (normalizedType == "[weapon]" || normalizedType == "[support weapon]" || normalizedType == "[charm]")
                return true;
            // 防具
            if (normalizedType == "[coat]" || normalizedType == "[pants]"
                || normalizedType == "[shoulder]" || normalizedType == "[shoes]"
                || normalizedType == "[waist]")
                return true;
            return false;
        }

        // 全部修理("一键修理")适用的装备类型 (客户端 handler 过滤一致)。
        // 只修这 13 类穿戴装备; 
        private static readonly HashSet<string> RepairAllEquipmentTypes = new HashSet<string>
        {
            "[weapon]", "[coat]", "[pants]", "[hat]", "[shoulder]", "[waist]",
            "[shoes]", "[amulet]", "[wrist]", "[ring]", "[support]",
            "[aurora avatar]", "[magic stone]",
        };

        // itemTemplateId 是否属于"全部修理"适用的装备类型。
        public static bool IsRepairAllEligible(int itemTemplateId)
        {
            return TryGetEquipmentType(itemTemplateId, out var type)
                && type != null
                && RepairAllEquipmentTypes.Contains(type);
        }

        private static string NormalizeEquipmentType(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var start = raw.IndexOf('[', StringComparison.Ordinal);
            var end = start >= 0 ? raw.IndexOf(']', start + 1) : -1;
            if (start < 0 || end <= start)
                return raw.Trim('`', ' ', '\t', '\r', '\n').ToLowerInvariant();

            return raw.Substring(start, end - start + 1).ToLowerInvariant();
        }

        /// <summary>
        /// 判断物品是否为克隆装扮。克隆装扮的 PVF [item category] 段值为 "clear avatar"。
        /// 转职光环也是 clear avatar，但它用虚拟动作替代光环外观，不能借用原光环 ID。
        /// </summary>
        public static bool IsCloneAvatarItem(int itemTemplateId)
        {
            return TryLoadEquipmentFile(itemTemplateId, out var equipment)
                && IsClearAvatarCategory(equipment);
        }

        public static bool ShouldApplyCloneAppearance(int itemTemplateId)
        {
            return IsCloneAvatarItem(itemTemplateId)
                && !IsAuroraLookReplaceAvatar(itemTemplateId);
        }

        public static bool IsAuroraLookReplaceAvatar(int itemTemplateId)
        {
            return TryLoadEquipmentFile(itemTemplateId, out var equipment)
                && IsAuroraLookReplaceAvatar(equipment);
        }

        internal static bool IsAuroraLookReplaceAvatar(EquipmentFile equipment)
        {
            if (equipment == null)
                return false;
            if (EquipmentTypeInfo.ParseOrUnknown(equipment.EquipmentType) != EquipmentType.AuroraAvatar)
                return false;
            return IsClearAvatarCategory(equipment) || HasAuroraVirtualMotion(equipment);
        }

        private static bool IsClearAvatarCategory(EquipmentFile equipment)
        {
            return equipment.ClearAvatar == 1
                || string.Equals(equipment.ItemCategory, "clear avatar", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasAuroraVirtualMotion(EquipmentFile equipment)
        {
            return equipment.Root != null
                && equipment.Root.GetChildren("aurora virtual motion").Count > 0;
        }

        public static bool IsNameTagItem(int itemTemplateId)
        {
            var meta = Resolve(itemTemplateId);
            return meta != null
                && string.Equals(meta.ItemKind, "equipment", StringComparison.Ordinal)
                && string.Equals(meta.EquipmentType, "[name tag]", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsPetInventoryEquipment(int itemTemplateId)
        {
            return CreatureExtraResolver.IsPetInventoryEquipment(itemTemplateId);
        }

        internal static bool IsCreatureItem(int itemTemplateId)
        {
            try
            {
                return CreatureExtraResolver.HasCreatureExtra(itemTemplateId);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[ItemMetadataResolver] IsCreatureItem(0x{itemTemplateId:X8}) failed: {ex.Message}");
                return false;
            }
        }

        internal static bool IsAvatarItem(ItemMetadata metadata)
        {
            var equipmentType = EquipmentTypeInfo.ParseOrUnknown(metadata?.EquipmentType);
            if (equipmentType >= EquipmentType.HatAvatar
                && equipmentType <= EquipmentType.AuroraIllusionAvatar)
            {
                return true;
            }

            var path = metadata?.PvfFilePath;
            if (string.IsNullOrWhiteSpace(path))
                return false;

            var normalizedPath = "/" + path.Replace('\\', '/').Trim('/');
            return normalizedPath.IndexOf("/avatar/", StringComparison.OrdinalIgnoreCase) >= 0
                || normalizedPath.IndexOf("/at_avatar/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsPetConsumableItem(ItemMetadata metadata)
        {
            if (metadata == null || !metadata.IsStackable || string.IsNullOrWhiteSpace(metadata.StackableType))
                return false;
            var stackableType = metadata.StackableType.Replace("`", "").Trim();
            return stackableType.StartsWith("[creature]", StringComparison.OrdinalIgnoreCase)
                || stackableType.StartsWith("[feed]", StringComparison.OrdinalIgnoreCase);
        }

        private static byte ResolveEquipmentItemKind(ItemMetadata metadata)
        {
            if (IsAvatarItem(metadata))
                return ItemCore.KindAvatar;

            var equipmentType = NormalizePvfKindTag(metadata.EquipmentType);
            if (equipmentType == "creature")
                return ItemCore.KindCreature;

            if (equipmentType == "artifact red"
                || equipmentType == "artifact blue"
                || equipmentType == "artifact green")
                return ItemCore.KindCreatureEquipment;

            if (equipmentType == "flag")
                return ItemCore.KindGuildMedal;

            return ItemCore.KindEquipment;
        }

        private static byte ResolveStackableItemKind(int itemTemplateId, ItemMetadata metadata)
        {
            if (IsPetConsumableItem(metadata))
                return ItemCore.KindCreatureConsumable;

            var stackableType = NormalizePvfKindTag(metadata.StackableType);
            if (stackableType == "avatar emblem")
                return ItemCore.KindAvatarEmblem;

            if (IsGuardianGemStackable(stackableType))
                return ItemCore.KindGuardianGem;

            if (stackableType == "material expert job")
                return ItemCore.KindExpertJobMaterial;

            if (metadata.IsPrimaryStackableFamily("quest"))
                return ItemCore.KindQuest;

            if (metadata.IsPrimaryStackableFamily("material"))
                return IsSpecialMaterialItem(itemTemplateId)
                    ? ItemCore.KindSpecialMaterial
                    : ItemCore.KindMaterial;

            return ItemCore.KindConsumable;
        }

        private static bool IsGuardianGemStackable(string stackableType)
        {
            return string.Equals(stackableType, "flag gem", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGuardianGemStackable(ItemMetadata metadata, string stackableType)
        {
            if (string.Equals(stackableType, "guardian gem", StringComparison.OrdinalIgnoreCase)
                || string.Equals(stackableType, "guild gem", StringComparison.OrdinalIgnoreCase)
                || string.Equals(stackableType, "gem", StringComparison.OrdinalIgnoreCase)
                || stackableType.IndexOf("guardian gem", StringComparison.OrdinalIgnoreCase) >= 0
                || stackableType.IndexOf("守护珠", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }

            var path = metadata?.PvfFilePath;
            if (string.IsNullOrWhiteSpace(path))
                return false;

            var normalizedPath = path.Replace('\\', '/').ToLowerInvariant();
            return normalizedPath.IndexOf("guardian", StringComparison.Ordinal) >= 0
                && normalizedPath.IndexOf("gem", StringComparison.Ordinal) >= 0;
        }

        private static bool IsSpecialMaterialItem(int itemTemplateId)
        {
            return CurrencyService.IsAccountWarehouseItem(itemTemplateId);
        }

        private static string NormalizePvfKindTag(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var normalized = value.Replace("`", string.Empty).Trim();
            if (normalized.Length >= 2 && normalized[0] == '[')
            {
                var end = normalized.IndexOf(']', 1);
                if (end > 1)
                    normalized = normalized.Substring(1, end - 1);
            }

            return normalized.Trim().ToLowerInvariant();
        }
    }
}
