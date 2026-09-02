using System;
using System.Linq;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Network.Builders;
using DfoServer.Network.Handlers;
using DfoServer.Network.Parsers.Inventory;
using PvfLib;

namespace DfoServer.SelfTests
{
    public static class DyeItemSelfTest
    {
        public static int Run()
        {
            Console.WriteLine("=== DYE_ITEM selftest ===");
            var failures = 0;

            VerifyStackableParser(ref failures);
            VerifyEquipmentParser(ref failures);
            VerifyProtocolParserAndAck(ref failures);
            VerifyRefreshPolicy(ref failures);
            VerifyUseDyeSuccess(ref failures);
            VerifyUseDyeRecordsCooltime(ref failures);
            VerifyUseDyeRejectsInvalidItems(ref failures);
            VerifyCloneAvatarCopiesDyeWhenEquipped(ref failures);
            VerifyAuroraLookReplaceDoesNotBorrowAppearance(ref failures);

            Console.WriteLine(failures == 0
                ? "DYE_ITEM selftest passed"
                : $"DYE_ITEM selftest failed: {failures}");
            return failures == 0 ? 0 : 1;
        }

        private static void VerifyStackableParser(ref int failures)
        {
            var splitLine = StackableItemFile.Parse(@"
[dye info]
24 1000
[/dye info]
");
            Check(
                "stackable parser reads split-line dye info",
                splitLine.HasDyeInfo
                && splitLine.DyeId == 24
                && splitLine.DyeInfo.Count == 2
                && splitLine.DyeInfo[1] == 1000,
                ref failures);

            var sameLine = StackableItemFile.Parse("[dye info] 25 1000 [/dye info]");
            Check(
                "stackable parser reads same-line dye info",
                sameLine.HasDyeInfo
                && sameLine.DyeId == 25
                && sameLine.DyeInfo.Count == 2
                && sameLine.DyeInfo[1] == 1000,
                ref failures);
        }

        private static void VerifyEquipmentParser(ref int failures)
        {
            var splitLine = EquipmentFile.Parse(@"
[enable dye]
1 0
[/enable dye]
");
            Check(
                "equipment parser reads split-line enable dye",
                splitLine.IsDyeEnabled
                && splitLine.EnableDye.Count == 2
                && splitLine.EnableDye[1] == 0,
                ref failures);

            var sameLine = EquipmentFile.Parse("[enable dye] 1 0 [/enable dye]");
            Check(
                "equipment parser reads same-line enable dye",
                sameLine.IsDyeEnabled
                && sameLine.EnableDye.Count == 2
                && sameLine.EnableDye[1] == 0,
                ref failures);

            var disabled = EquipmentFile.Parse(@"
[enable dye]
0 0
[/enable dye]
");
            Check(
                "equipment parser requires first enable dye value to be one",
                !disabled.IsDyeEnabled
                && disabled.EnableDye.Count == 2,
                ref failures);
        }

        private static void VerifyProtocolParserAndAck(ref int failures)
        {
            Check(
                "USE_DYE request parses dye and avatar slots",
                UseDyeRequestParser.TryParse(
                    new byte[] { 0x04, 0x00, 0x01, 0x00 },
                    out var request)
                && request.DyeSlotIndex == 4
                && request.AvatarSlotIndex == 1,
                ref failures);

            Check(
                "USE_DYE success ACK writes avatar slot and dye info block",
                DyeItemAckBuilder.BuildSuccess(0x1D, 24, 7)
                    .SequenceEqual(new byte[]
                    {
                        0x01,
                        0x1D, 0x00,
                        0x04, 0x00, 0x00, 0x00,
                        0x18, 0x00,
                        0x07, 0x00,
                    }),
                ref failures);

            Check(
                "USE_DYE error ACK writes failure flag only",
                DyeItemAckBuilder.BuildError()
                    .SequenceEqual(new byte[] { 0x00 }),
                ref failures);
        }

        private static void VerifyRefreshPolicy(ref int failures)
        {
            var result = new InventoryDyeResult
            {
                Request = new InventoryDyeRequest
                {
                    DyeSlotIndex = 3,
                    AvatarSlotIndex = 1,
                },
            };

            Check(
                "USE_DYE sends 0x0E refresh for source dye slot",
                InventoryHandler.ShouldSendUseDyeRefresh(
                    result,
                    new InventorySlotMutation(InventoryListType.Main, 3)),
                ref failures);

            Check(
                "USE_DYE does not send target avatar 0x0E refresh",
                !InventoryHandler.ShouldSendUseDyeRefresh(
                    result,
                    new InventorySlotMutation(InventoryListType.Avatar, 1)),
                ref failures);
        }

        private static void VerifyUseDyeSuccess(ref int failures)
        {
            var inventory = CreateInventory();
            AttachDye(inventory, slot: 4, itemId: 10000652, count: 2);
            AttachAvatar(inventory, slot: 1, itemId: 310001, avatarUid: 9001, color2: 7);

            var ok = InventoryDyeService.TryUse(
                inventory,
                new InventoryDyeRequest
                {
                    DyeSlotIndex = 4,
                    AvatarSlotIndex = 1,
                },
                nowUnixSeconds: 1700000000,
                stackableLoader: _ => CreateDyeStackable(24),
                equipmentLoader: _ => CreateDyeEnabledEquipment(),
                out var result);

            var detail = inventory.AvatarDetails.GetDetail(9001);
            Check(
                "USE_DYE applies color1 and consumes one dye",
                ok
                && result.Success
                && result.DyeId == 24
                && result.Color1 == 24
                && result.Color2 == 7
                && inventory.GetItem(InventoryListType.Main, 4)?.Count == 1
                && detail != null
                && detail.Color1 == 24,
                ref failures);

            Check(
                "USE_DYE records source and target refresh slots",
                result.Changes.Slots.Count == 2
                && result.Changes.Slots.Any(slot =>
                    slot.ListType == InventoryListType.Main && slot.SlotIndex == 4)
                && result.Changes.Slots.Any(slot =>
                    slot.ListType == InventoryListType.Avatar && slot.SlotIndex == 1),
                ref failures);
        }

        private static void VerifyUseDyeRejectsInvalidItems(ref int failures)
        {
            VerifyNonDyeIsRejected(ref failures);
            VerifyExpiredDyeIsDeleted(ref failures);
            VerifyCooltimeDyeIsRejected(ref failures);
            VerifyAvatarWithoutEnableDyeIsRejected(ref failures);
        }

        private static void VerifyUseDyeRecordsCooltime(ref int failures)
        {
            var inventory = CreateInventory();
            AttachDye(inventory, slot: 4, itemId: 10000652, count: 1);
            AttachAvatar(inventory, slot: 1, itemId: 310001, avatarUid: 9005);

            InventoryDyeService.TryUse(
                inventory,
                new InventoryDyeRequest
                {
                    DyeSlotIndex = 4,
                    AvatarSlotIndex = 1,
                },
                nowUnixSeconds: 1700000000,
                stackableLoader: _ => CreateDyeStackable(
                    24,
                    hasCooltimeMaintenance: true,
                    coolTime: 10000),
                equipmentLoader: _ => CreateDyeEnabledEquipment(),
                out var result);

            Check(
                "USE_DYE records cooltime state on successful maintained dye",
                result.Success
                && inventory.ItemStates.TryGetExpireTime(
                    ItemStateKinds.Cooltime,
                    10000652,
                    out var expireTime)
                && expireTime == 1700000010,
                ref failures);
        }

        private static void VerifyNonDyeIsRejected(ref int failures)
        {
            var inventory = CreateInventory();
            AttachDye(inventory, slot: 4, itemId: 200001, count: 1);
            AttachAvatar(inventory, slot: 1, itemId: 310001, avatarUid: 9002);

            InventoryDyeService.TryUse(
                inventory,
                new InventoryDyeRequest
                {
                    DyeSlotIndex = 4,
                    AvatarSlotIndex = 1,
                },
                nowUnixSeconds: 1700000000,
                stackableLoader: _ => new StackableItemFile(),
                out var result);

            Check(
                "USE_DYE rejects item without dye info",
                !result.Success
                && result.Error == InventoryDyeError.NotDyeItem
                && inventory.GetItem(InventoryListType.Main, 4)?.Count == 1
                && inventory.AvatarDetails.GetDetail(9002)?.Color1 == 0,
                ref failures);
        }

        private static void VerifyExpiredDyeIsDeleted(ref int failures)
        {
            var inventory = CreateInventory();
            AttachDye(
                inventory,
                slot: 4,
                itemId: 10000652,
                count: 1,
                expireTime: 1699999999);
            AttachAvatar(inventory, slot: 1, itemId: 310001, avatarUid: 9003);

            InventoryDyeService.TryUse(
                inventory,
                new InventoryDyeRequest
                {
                    DyeSlotIndex = 4,
                    AvatarSlotIndex = 1,
                },
                nowUnixSeconds: 1700000000,
                stackableLoader: _ => CreateDyeStackable(24),
                out var result);

            Check(
                "USE_DYE deletes expired dye and fails",
                !result.Success
                && result.Error == InventoryDyeError.DyeExpired
                && result.SourceExpiredDeleted
                && inventory.GetItem(InventoryListType.Main, 4) == null
                && result.Changes.Slots.Any(slot =>
                    slot.ListType == InventoryListType.Main && slot.SlotIndex == 4),
                ref failures);
        }

        private static void VerifyCooltimeDyeIsRejected(ref int failures)
        {
            var inventory = CreateInventory();
            AttachDye(inventory, slot: 4, itemId: 10000652, count: 1);
            AttachAvatar(inventory, slot: 1, itemId: 310001, avatarUid: 9004);
            inventory.ItemStates.Upsert(
                ItemStateKinds.Cooltime,
                10000652,
                1700000010);

            InventoryDyeService.TryUse(
                inventory,
                new InventoryDyeRequest
                {
                    DyeSlotIndex = 4,
                    AvatarSlotIndex = 1,
                },
                nowUnixSeconds: 1700000000,
                stackableLoader: _ => CreateDyeStackable(
                    24,
                    hasCooltimeMaintenance: true,
                    coolTime: 10000),
                out var result);

            Check(
                "USE_DYE rejects active cooltime when dye has cooltime maintenance",
                !result.Success
                && result.Error == InventoryDyeError.CooltimeActive
                && inventory.GetItem(InventoryListType.Main, 4)?.Count == 1
                && inventory.AvatarDetails.GetDetail(9004)?.Color1 == 0,
                ref failures);
        }

        private static void VerifyAvatarWithoutEnableDyeIsRejected(ref int failures)
        {
            var inventory = CreateInventory();
            AttachDye(inventory, slot: 4, itemId: 10000652, count: 1);
            AttachAvatar(inventory, slot: 1, itemId: 310001, avatarUid: 9006);

            InventoryDyeService.TryUse(
                inventory,
                new InventoryDyeRequest
                {
                    DyeSlotIndex = 4,
                    AvatarSlotIndex = 1,
                },
                nowUnixSeconds: 1700000000,
                stackableLoader: _ => CreateDyeStackable(24),
                equipmentLoader: _ => EquipmentFile.Parse(@"
[enable dye]
0 0
[/enable dye]
"),
                out var result);

            Check(
                "USE_DYE rejects avatar without enabled dye tag",
                !result.Success
                && result.Error == InventoryDyeError.TargetDyeDisabled
                && inventory.GetItem(InventoryListType.Main, 4)?.Count == 1
                && inventory.AvatarDetails.GetDetail(9006)?.Color1 == 0,
                ref failures);
        }

        private static void VerifyCloneAvatarCopiesDyeWhenEquipped(ref int failures)
        {
            var inventory = CreateInventory();
            var baseAvatar = CreateAvatar(itemId: 310001, avatarUid: 9101);
            var cloneAvatar = CreateAvatar(itemId: 310002, avatarUid: 9102);
            inventory.AvatarDetails.Attach(CreateAvatarDetail(inventory, baseAvatar, color1: 24, color2: 7));
            inventory.AvatarDetails.Attach(CreateAvatarDetail(inventory, cloneAvatar));

            InventoryMoveService.SyncAvatarClearAvatarId(
                inventory,
                cloneAvatar,
                InventoryListType.Equipment,
                (short)EquipmentType.HatAvatar,
                baseAvatar,
                itemId => itemId == 310002);

            var cloneDetail = inventory.AvatarDetails.GetDetail(9102);
            Check(
                "clone avatar syncs clear avatar and dye colors from replaced equipped avatar",
                cloneDetail != null
                && cloneDetail.ClearAvatarId == 310001
                && cloneDetail.Color1 == 24
                && cloneDetail.Color2 == 7
                && inventory.AvatarDetails.DirtyDetailUids.Contains(9102),
                ref failures);

            InventoryMoveService.SyncAvatarClearAvatarId(
                inventory,
                cloneAvatar,
                InventoryListType.Avatar,
                1,
                null,
                itemId => itemId == 310002);

            Check(
                "clone avatar clears borrowed dye colors when not equipped over an avatar",
                cloneDetail != null
                && cloneDetail.ClearAvatarId == 0
                && cloneDetail.Color1 == 0
                && cloneDetail.Color2 == 0,
                ref failures);
        }

        private static void VerifyAuroraLookReplaceDoesNotBorrowAppearance(ref int failures)
        {
            var auroraReplace = EquipmentFile.Parse(@"
[equipment type]
`[aurora avatar]` 0
[item category]
`clear avatar`
[/item category]
[aurora virtual motion]
`[swordman]` 3 `[rest motion]` `Character/Swordman/Animation/Challenge2ndBerserker.ani` 14
[/aurora virtual motion]
");
            var hatClone = EquipmentFile.Parse(@"
[equipment type]
`[hat avatar]` 0
[item category]
`clear avatar`
[/item category]
");
            Check(
                "aurora clear-avatar with virtual motion replaces look instead of cloning the previous aurora",
                ItemMetadataResolver.IsAuroraLookReplaceAvatar(auroraReplace)
                && !ItemMetadataResolver.IsAuroraLookReplaceAvatar(hatClone),
                ref failures);

            if (!ItemMetadataResolver.IsAuroraLookReplaceAvatar(113590006))
            {
                Console.WriteLine("aurora look-replace PVF item 113590006 skipped");
                return;
            }

            var inventory = CreateInventory();
            var baseAurora = CreateAvatar(itemId: 101590032, avatarUid: 9201);
            var lookReplace = CreateAvatar(itemId: 113590006, avatarUid: 9202);
            inventory.AvatarDetails.Attach(CreateAvatarDetail(inventory, baseAurora));
            inventory.AvatarDetails.Attach(CreateAvatarDetail(inventory, lookReplace, color1: 3, color2: 4));

            InventoryMoveService.SyncAvatarClearAvatarId(
                inventory,
                lookReplace,
                InventoryListType.Equipment,
                (short)EquipmentType.AuroraAvatar,
                baseAurora,
                itemId => itemId == 113590006);

            var detail = inventory.AvatarDetails.GetDetail(9202);
            Check(
                "equipping 转职光环 does not copy the previous aurora into clear_avatar_id",
                detail != null
                && detail.ClearAvatarId == 0
                && detail.Color1 == 0
                && detail.Color2 == 0,
                ref failures);

            var stale = new AvatarDetail { ClearAvatarId = 101590032 };
            var projection = new Noti2InventoryProjectionBuilder();
            Check(
                "stale clear_avatar_id is ignored for 转职光环 appearance",
                projection.ResolveAppearanceDisplayItemId(lookReplace, stale) == 113590006
                && Noti2InventoryProjectionBuilder.ResolveAppearanceLinkItemId(lookReplace, stale) == 0,
                ref failures);
        }

        private static InventoryService CreateInventory()
        {
            return new InventoryService(characterId: 8001, accountId: 8000);
        }

        private static void AttachDye(
            InventoryService inventory,
            short slot,
            int itemId,
            int count,
            int expireTime = 0)
        {
            inventory.AttachItem(
                InventoryListType.Main,
                slot,
                new ItemCore
                {
                    ItemKind = ItemCore.KindConsumable,
                    ItemId = itemId,
                    Count = count,
                    ExpireTime = expireTime,
                });
        }

        private static void AttachAvatar(
            InventoryService inventory,
            short slot,
            int itemId,
            int avatarUid,
            ushort color1 = 0,
            ushort color2 = 0)
        {
            inventory.AttachItem(
                InventoryListType.Avatar,
                slot,
                CreateAvatar(itemId, avatarUid));
            inventory.AvatarDetails.Attach(CreateAvatarDetail(
                inventory,
                inventory.GetItem(InventoryListType.Avatar, slot),
                color1,
                color2));
        }

        private static ItemCore CreateAvatar(int itemId, int avatarUid)
        {
            return new ItemCore
            {
                ItemKind = ItemCore.KindAvatar,
                ItemId = itemId,
                AvatarUid = avatarUid,
            };
        }

        private static AvatarDetail CreateAvatarDetail(
            InventoryService inventory,
            ItemCore avatar,
            ushort color1 = 0,
            ushort color2 = 0)
        {
            return new AvatarDetail
            {
                AvatarUid = avatar.AvatarUid,
                OwnerId = inventory.AccountId,
                CharacterId = inventory.CharacterId,
                ItemId = avatar.ItemId,
                JewelSocketView = new JewelSocket(),
                Color1 = color1,
                Color2 = color2,
            };
        }

        private static StackableItemFile CreateDyeStackable(
            int dyeId,
            bool hasCooltimeMaintenance = false,
            int coolTime = -1)
        {
            return new StackableItemFile
            {
                DyeInfo = { dyeId, 1000 },
                DyeId = dyeId,
                HasCooltimeMaintenance = hasCooltimeMaintenance,
                CoolTime = coolTime,
            };
        }

        private static EquipmentFile CreateDyeEnabledEquipment()
        {
            return EquipmentFile.Parse(@"
[enable dye]
1 0
[/enable dye]
");
        }

        private static void Check(
            string name,
            bool condition,
            ref int failures)
        {
            if (condition)
            {
                Console.WriteLine($"[PASS] {name}");
                return;
            }

            Console.WriteLine($"[FAIL] {name}");
            failures++;
        }
    }
}
