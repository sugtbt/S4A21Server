using System;
using System.Collections.Generic;
using DfoServer.Game.Characters;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Game.SelectCharacter;

namespace DfoServer.Game.Inventory
{
    internal sealed class Noti2InventoryProjectionBuilder
    {
        internal UserInfoAdditionSnapshot BuildUserInfoAddition(InventoryService inventory)
        {
            if (inventory == null)
                return new UserInfoAdditionSnapshot();

            var snapshot = BuildUserInfoAddition(
                inventory.GetItems(InventoryListType.Equipment),
                core => ResolveAvatarDetail(inventory, core),
                core => ResolveCreatureDetail(inventory, core));
            snapshot.AuraSkinFlag = inventory.AuraSkinFlag;
            ApplyNameTagFields(inventory, snapshot);
            return snapshot;
        }

        internal UserInfoAdditionSnapshot BuildUserInfoAddition(
            IEnumerable<InventoryItem> equippedItems,
            IReadOnlyDictionary<long, AvatarDetail> avatarDetails,
            IReadOnlyDictionary<int, CreatureDetail> creatureDetails)
        {
            if (equippedItems == null)
                return new UserInfoAdditionSnapshot();

            var entries = new List<KeyValuePair<short, ItemCore>>();
            foreach (var item in equippedItems)
            {
                if (item == null)
                    continue;

                var slot = (short)EquipmentTypeInfo.ToA21AppearanceSlot(item.SlotIndex);
                if (!EquipmentTypeInfo.IsA21Noti2EquippedSlot(slot))
                    continue;

                entries.Add(new KeyValuePair<short, ItemCore>(slot, item.Core));
            }

            return BuildUserInfoAddition(
                entries,
                core => ResolveAvatarDetail(avatarDetails, core),
                core => ResolveCreatureDetail(creatureDetails, core));
        }

        internal List<EquippedEntrySnapshot> BuildEquippedEntries(InventoryService inventory)
        {
            return BuildUserInfoAddition(inventory).EquippedEntries;
        }

        internal EquippedEntrySnapshot BuildEquippedEntry(short slotIndex, ItemCore core)
        {
            if (core == null || core.ItemId <= 0)
                return null;

            var slot = (short)EquipmentTypeInfo.ToA21AppearanceSlot(slotIndex);
            if (!EquipmentTypeInfo.IsA21Noti2EquippedSlot(slot))
                return null;

            return new EquippedEntrySnapshot
            {
                Slot = slot,
                Core = core.Copy(),
            };
        }

        internal CharacterAppearanceEntry[] BuildAppearanceEntries(InventoryService inventory)
        {
            if (inventory == null)
                return Array.Empty<CharacterAppearanceEntry>();

            return BuildAppearanceEntries(
                inventory.GetItems(InventoryListType.Equipment),
                core => ResolveAvatarDetail(inventory, core));
        }

        internal CharacterAppearanceEntry[] BuildAppearanceEntries(
            IEnumerable<InventoryItem> equippedItems,
            IReadOnlyDictionary<long, AvatarDetail> avatarDetails)
        {
            if (equippedItems == null)
                return Array.Empty<CharacterAppearanceEntry>();

            var entries = new List<KeyValuePair<short, ItemCore>>();
            foreach (var item in equippedItems)
            {
                if (item == null)
                    continue;

                entries.Add(new KeyValuePair<short, ItemCore>(
                    (short)EquipmentTypeInfo.ToA21AppearanceSlot(item.SlotIndex),
                    item.Core));
            }

            return BuildAppearanceEntries(entries, core => ResolveAvatarDetail(avatarDetails, core));
        }

        private CharacterAppearanceEntry[] BuildAppearanceEntries(
            IEnumerable<KeyValuePair<short, ItemCore>> equippedItems,
            Func<ItemCore, AvatarDetail> resolveAvatarDetail)
        {
            var result = new List<CharacterAppearanceEntry>();
            if (equippedItems == null)
                return result.ToArray();

            var nowUnixTime = GetNowUnixTime();
            foreach (var pair in equippedItems)
            {
                var slotIndex = pair.Key;
                var core = pair.Value;
                var appearanceSlot = EquipmentTypeInfo.ToA21AppearanceSlot(slotIndex);
                if (core == null || !ShouldEmitAppearanceSlot(appearanceSlot))
                    continue;

                var avatarDetail = resolveAvatarDetail?.Invoke(core);
                if (InventoryItemExpirationService.IsExpired(core, avatarDetail, nowUnixTime))
                    continue;

                var displayItemId = ResolveAppearanceDisplayItemId(core, avatarDetail);
                if (displayItemId == 0)
                    continue;

                result.Add(new CharacterAppearanceEntry(
                    (byte)appearanceSlot,
                    displayItemId,
                    4,
                    BuildAppearanceExpansionData(core, avatarDetail),
                    BuildAppearanceState(core),
                    ResolveAppearanceLinkItemId(core, avatarDetail),
                    0u,
                    core.EnchantUpgradeCount));
            }

            return result.ToArray();
        }

        private UserInfoAdditionSnapshot BuildUserInfoAddition(
            IEnumerable<KeyValuePair<short, ItemCore>> equippedItems,
            Func<ItemCore, AvatarDetail> resolveAvatarDetail,
            Func<ItemCore, CreatureDetail> resolveCreatureDetail)
        {
            var snapshot = new UserInfoAdditionSnapshot();
            if (equippedItems == null)
                return snapshot;

            var nowUnixTime = GetNowUnixTime();
            foreach (var pair in equippedItems)
            {
                var core = pair.Value;
                var avatarDetail = resolveAvatarDetail?.Invoke(core);
                var creatureDetail = resolveCreatureDetail?.Invoke(core);
                if (InventoryItemExpirationService.IsExpired(core, avatarDetail, creatureDetail, nowUnixTime))
                    continue;

                var entry = BuildEquippedEntry(pair.Key, core);
                if (entry == null)
                    continue;

                snapshot.EquippedEntries.Add(entry);
                snapshot.SetAvatarDetail(entry.Core.Value, CopyAvatarDetail(avatarDetail));

                if (entry.Core.ItemKind == ItemCore.KindCreature)
                    snapshot.SetCreatureDetail(
                        entry.Core.Value,
                        CopyCreatureDetail(creatureDetail, entry.Core));
            }

            return snapshot;
        }

        internal void ApplySubtype0TailDynamicFields(
            InventoryService inventory,
            UserInfoMinimumTailSnapshot snapshot)
        {
            if (snapshot == null || inventory == null)
                return;

            ApplyNameTagFields(inventory, snapshot);

            if (inventory.TryGetItem(
                    InventoryListType.Equipment,
                    (short)EquipmentType.Weapon,
                    out var weapon)
                && weapon != null)
                snapshot.Forging = weapon.GenuineUpgrade;

            if (!inventory.TryGetItem(
                    InventoryListType.Equipment,
                    (short)EquipmentType.Creature,
                    out var creature)
                || creature == null
                || creature.ItemId <= 0)
                return;

            snapshot.EquippedCreatureItemId = (uint)creature.ItemId;
            var creatureKey = creature.Value;
            if (creatureKey <= 0)
                return;

            var detail = inventory.CreatureDetails.GetDetail(creatureKey);
            if (detail == null)
                return;

            snapshot.EquippedCreatureNameBytes = detail.NameBytes;
            snapshot.EquippedCreatureAliveState = detail.GetAliveState();
        }

        internal void ApplyNameTagFields(
            InventoryService inventory,
            UserInfoAdditionSnapshot snapshot)
        {
            if (inventory == null || snapshot == null)
                return;

            if (!inventory.NameTag.IsActive())
            {
                snapshot.NameTagItemId = 0;
                snapshot.NameTagExpireTime = 0;
                return;
            }

            snapshot.NameTagItemId = (uint)inventory.NameTag.ItemId;
            snapshot.NameTagExpireTime = (uint)inventory.NameTag.ExpireTime;
        }

        private static void ApplyNameTagFields(
            InventoryService inventory,
            UserInfoMinimumTailSnapshot snapshot)
        {
            if (inventory == null || snapshot == null)
                return;

            if (!inventory.NameTag.IsActive())
            {
                snapshot.NameTagItemId = 0;
                snapshot.NameTagExpireTime = 0;
                return;
            }

            snapshot.NameTagItemId = (uint)inventory.NameTag.ItemId;
            snapshot.NameTagExpireTime = (uint)inventory.NameTag.ExpireTime;
        }

        internal int ResolveAppearanceDisplayItemId(ItemCore core, AvatarDetail avatarDetail)
        {
            if (core.ItemKind == ItemCore.KindAvatar
                && avatarDetail != null
                && avatarDetail.ClearAvatarId != 0)
                return avatarDetail.ClearAvatarId;

            return core.ItemId;
        }

        internal static int ResolveAppearanceLinkItemId(
            ItemCore core,
            AvatarDetail avatarDetail)
        {
            if (core != null
                && core.ItemKind == ItemCore.KindAvatar
                && avatarDetail != null
                && avatarDetail.ClearAvatarId != 0)
            {
                return core.ItemId;
            }

            return 0;
        }

        internal byte BuildAppearanceState(ItemCore core)
        {
            var upgrade = core.Attr & 0x1F;
            return unchecked((byte)(upgrade * 2 + (core.AmplifyType != 0 ? 1 : 0)));
        }

        private static bool ShouldEmitAppearanceSlot(int slotIndex)
        {
            return EquipmentTypeInfo.IsA21RosterAppearanceSlot(slotIndex);
        }

        private static byte[] BuildAppearanceExpansionData(ItemCore core, AvatarDetail avatarDetail)
        {
            return core.ItemKind == ItemCore.KindAvatar
                ? BuildAvatarColorData(avatarDetail)
                : new byte[4];
        }

        private static byte[] BuildAvatarColorData(AvatarDetail avatarDetail)
        {
            var data = new byte[4];
            if (avatarDetail == null)
                return data;

            BitConverter.GetBytes(avatarDetail.Color1).CopyTo(data, 0);
            BitConverter.GetBytes(avatarDetail.Color2).CopyTo(data, 2);
            return data;
        }

        private static AvatarDetail ResolveAvatarDetail(InventoryService inventory, ItemCore core)
        {
            if (inventory == null || core == null || core.ItemKind != ItemCore.KindAvatar || core.Value <= 0)
                return null;

            return inventory.AvatarDetails.GetDetail(core.Value);
        }

        private static AvatarDetail ResolveAvatarDetail(
            IReadOnlyDictionary<long, AvatarDetail> avatarDetails,
            ItemCore core)
        {
            if (avatarDetails == null || core == null || core.ItemKind != ItemCore.KindAvatar || core.Value <= 0)
                return null;

            avatarDetails.TryGetValue(core.Value, out var detail);
            return detail;
        }

        private static CreatureDetail ResolveCreatureDetail(InventoryService inventory, ItemCore core)
        {
            if (inventory == null || core == null || core.ItemKind != ItemCore.KindCreature || core.Value <= 0)
                return null;

            return inventory.CreatureDetails.GetDetail(core.Value);
        }

        private static CreatureDetail ResolveCreatureDetail(
            IReadOnlyDictionary<int, CreatureDetail> creatureDetails,
            ItemCore core)
        {
            if (creatureDetails == null || core == null || core.ItemKind != ItemCore.KindCreature || core.Value <= 0)
                return null;

            creatureDetails.TryGetValue(core.Value, out var detail);
            return detail;
        }

        private static AvatarDetail CopyAvatarDetail(AvatarDetail source)
        {
            if (source == null)
                return null;

            return new AvatarDetail
            {
                AvatarUid = source.AvatarUid,
                OwnerId = source.OwnerId,
                CharacterId = source.CharacterId,
                ItemId = source.ItemId,
                ExpireDate = source.ExpireDate,
                ClearAvatarId = source.ClearAvatarId,
                JewelSocket = source.JewelSocket,
                Color1 = source.Color1,
                Color2 = source.Color2,
                DeleteDate = source.DeleteDate,
            };
        }

        private static CreatureDetail CopyCreatureDetail(CreatureDetail source, ItemCore core)
        {
            if (source == null && core == null)
                return null;

            return new CreatureDetail
            {
                Uid = source?.Uid ?? core?.Value ?? 0,
                NameBytes = source?.NameBytes,
                Field04 = source?.Field04 ?? 0,
                ModeFlag = source?.ModeFlag ?? 0,
                ProgressValue32 = source?.ProgressValue32 ?? 0,
                FieldAfterValue32 = source?.FieldAfterValue32 ?? 0,
                ExpireDate = ResolveCreatureExpireDate(source, core),
            };
        }

        private static int ResolveCreatureExpireDate(CreatureDetail source, ItemCore core)
        {
            if (source != null && source.ExpireDate > 0)
                return source.ExpireDate;
            if (core != null && core.ExpireTime > 0)
                return core.ExpireTime;
            if (core != null && core.Marker16 >= 1_000_000_000)
                return core.Marker16;

            return core != null && core.ItemId > 0
                ? CreatureDetail.GetExpireDate(core.ItemId)
                : 0;
        }

        private static long GetNowUnixTime()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }
}
