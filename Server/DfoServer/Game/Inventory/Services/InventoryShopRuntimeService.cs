using System;
using DfoServer.Game.Currency;
using DfoServer.Game.CraneMiniGame;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal static class InventoryShopRuntimeService
    {
        private static readonly Lazy<CraneMiniGameCatalog> CraneCatalog =
            new Lazy<CraneMiniGameCatalog>(CraneMiniGameCatalog.Load);

        internal static bool TryBuyNpcItem(
            InventoryService inventory,
            int itemTemplateId,
            int buyCount,
            int npcId,
            out InventoryMutationResult result)
        {
            return TryBuyNpcItem(
                inventory,
                itemTemplateId,
                buyCount,
                npcId,
                null,
                null,
                out result);
        }

        internal static bool TryBuyNpcItem(
            InventoryService inventory,
            int itemTemplateId,
            int buyCount,
            int npcId,
            SqliteConnection sharedConnection,
            SqliteTransaction sharedTransaction,
            out InventoryMutationResult result)
        {
            result = null;
            if (!TryNormalizeShopRequest(inventory, itemTemplateId, buyCount, out var metadata, out var effectiveCount))
                return false;

            var totalGoldCost = checked(metadata.BuyGold * effectiveCount);
            var totalCeraCost = checked(metadata.BuyCoin * effectiveCount);
            var materialItemId = metadata.NeedMaterialId;
            var materialCount = metadata.NeedMaterialCount;
            if (CraneCatalog.Value.TryResolveCoinExchange(
                    itemTemplateId,
                    out var craneMaterialItemId,
                    out var craneMaterialCount))
            {
                materialItemId = craneMaterialItemId;
                materialCount = craneMaterialCount;
            }
            var usesMaterialExchange = materialItemId > 0 && materialCount > 0;
            var totalMaterialCost = usesMaterialExchange
                ? checked(materialCount * effectiveCount)
                : 0;

            if (!CanGrant(inventory, itemTemplateId, effectiveCount))
                return false;

            if (!ItemPurchaseLimitService.TryRecordPurchase(
                    inventory,
                    npcId,
                    itemTemplateId,
                    effectiveCount,
                    sharedConnection,
                    sharedTransaction))
                return false;

            if (!TrySpendCosts(
                    inventory,
                    totalGoldCost,
                    totalCeraCost,
                    materialItemId,
                    totalMaterialCost,
                    sharedConnection,
                    sharedTransaction,
                    out var updatedGold,
                    out var updatedSp,
                    out var updatedCera,
                    out var costItemSlot,
                    out var costItemRemaining))
                return false;

            if (!InventoryRewardGrantService.TryCreateAndInsert(
                    inventory,
                    itemTemplateId,
                    ItemCreateReason.NpcShopPurchase,
                    effectiveCount,
                    out var grant)
                || grant == null
                || !grant.Success)
            {
                return false;
            }

            result = ToMutationResult(inventory, grant, updatedGold, updatedSp, updatedCera, effectiveCount);
            if (metadata.IsStackable
                || InventoryService.TryResolveMainVirtualSlotByItemId(itemTemplateId, out _, out _))
            {
                result.RemainingStackCount = effectiveCount;
                result.InstanceValue = effectiveCount;
            }

            if (usesMaterialExchange)
            {
                result.CostItemTemplateId = materialItemId;
                result.CostItemRemainingCount = costItemRemaining;
                result.CostItemSlotIndex = costItemSlot;
            }

            result.GoldSpent = totalGoldCost > 0;
            return true;
        }

        internal static bool TryBuySecretShopItem(
            InventoryService inventory,
            int itemTemplateId,
            int itemCount,
            int goldCost,
            int requiredItemId,
            int requiredItemCount,
            out InventoryMutationResult result)
        {
            result = null;
            if (!TryNormalizeShopRequest(inventory, itemTemplateId, itemCount, out _, out var effectiveCount))
                return false;
            if (effectiveCount != itemCount)
                return false;

            var usesItemCurrency = requiredItemId > 0;
            if (usesItemCurrency != (requiredItemCount > 0))
                return false;
            if (usesItemCurrency && goldCost != 0)
                return false;
            if (goldCost < 0)
                return false;

            if (!CanGrant(inventory, itemTemplateId, effectiveCount))
                return false;

            if (!TrySpendCosts(
                    inventory,
                    goldCost,
                    0,
                    usesItemCurrency ? requiredItemId : 0,
                    usesItemCurrency ? requiredItemCount : 0,
                    null,
                    null,
                    out var updatedGold,
                    out var updatedSp,
                    out var updatedCera,
                    out var costItemSlot,
                    out var costItemRemaining))
                return false;

            if (!InventoryRewardGrantService.TryCreateAndInsert(
                    inventory,
                    itemTemplateId,
                    ItemCreateReason.NpcShopPurchase,
                    effectiveCount,
                    out var grant)
                || grant == null
                || !grant.Success)
            {
                return false;
            }

            result = ToMutationResult(inventory, grant, updatedGold, updatedSp, updatedCera, effectiveCount);
            result.GoldSpent = goldCost > 0;
            if (usesItemCurrency)
            {
                result.CostItemTemplateId = requiredItemId;
                result.CostItemRemainingCount = costItemRemaining;
                result.CostItemSlotIndex = costItemSlot;
            }

            return true;
        }

        internal static bool TryRentWeapon(
            InventoryService inventory,
            int itemTemplateId,
            int expireTime,
            out InventoryMutationResult result,
            SqliteConnection sharedConnection = null,
            SqliteTransaction sharedTransaction = null)
        {
            result = null;
            if (inventory == null
                || itemTemplateId <= 0
                || !RentalWeaponInventoryMapper.IsValidInventoryTemplate(itemTemplateId)
                || !ItemMetadataResolver.TryResolveItemKind(itemTemplateId, out _))
            {
                return false;
            }

            if (TryRefreshExistingRentalWeapon(
                    inventory,
                    InventoryListType.Equipment,
                    itemTemplateId,
                    expireTime,
                    out result)
                || TryRefreshExistingRentalWeapon(
                    inventory,
                    InventoryListType.Main,
                    itemTemplateId,
                    expireTime,
                    out result))
            {
                return true;
            }

            if (!InventoryRewardGrantService.TryCreateAndInsert(
                    inventory,
                    itemTemplateId,
                    ItemCreateReason.NpcShopPurchase,
                    1,
                    CreateRentalWeaponOptions(expireTime),
                    out var grant)
                || grant == null
                || !grant.Success)
            {
                return false;
            }

            var walletSp = 0;
            var walletCera = 0;
            if (TryLoadCurrentWallet(inventory, sharedConnection, sharedTransaction, out var wallet))
            {
                walletSp = wallet.Sp;
                walletCera = wallet.Cera;
            }

            result = ToMutationResult(inventory, grant, inventory.CountMainItem(0), walletSp, walletCera, 1);
            return true;
        }

        internal static int TrySellItem(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            short sellCount,
            out InventoryMutationResult result)
        {
            return TrySellItem(
                inventory,
                listType,
                slotIndex,
                sellCount,
                null,
                null,
                out result);
        }

        internal static int TrySellItem(
            InventoryService inventory,
            InventoryListType listType,
            short slotIndex,
            short sellCount,
            SqliteConnection sharedConnection,
            SqliteTransaction sharedTransaction,
            out InventoryMutationResult result)
        {
            result = null;
            if (inventory == null || !IsSupportedSellListType(listType))
                return 1;

            var source = inventory.GetItem(listType, slotIndex);
            if (source == null || IsItemLocked(inventory, source))
                return 0xd6;

            var metadata = ItemMetadataResolver.Resolve(source.ItemId);
            var appliedCount = NormalizeSellRemovalCount(source, sellCount);
            if (appliedCount <= 0)
                return 1;

            var goldDelta = (int)Math.Min(
                int.MaxValue,
                Math.Max(0L, (long)metadata.SellGold * appliedCount));
            var currentGold = inventory.CountMainItem(InventoryService.MainVirtualCurrencySlotStart);
            var carryLimit = LoadGoldCarryLimit(inventory, sharedConnection, sharedTransaction);
            var targetGold = (long)currentGold + goldDelta;
            if (targetGold > carryLimit)
                return 22;

            if (!InventoryDeleteService.TryDecreaseStack(
                    inventory,
                    listType,
                    slotIndex,
                    appliedCount,
                    out var delete)
                || delete == null
                || !delete.Success)
            {
                return 1;
            }

            var soldSource = delete.SourceSnapshot;
            var finalGold = (int)Math.Min(int.MaxValue, Math.Max(0L, targetGold));
            if (!inventory.SetMainVirtualCount(InventoryService.MainVirtualCurrencySlotStart, finalGold))
                return 22;

            result = new InventoryMutationResult
            {
                ListType = listType,
                SlotIndex = slotIndex,
                ItemTemplateId = soldSource.ItemId,
                RemainingStackCount = delete.RemainingCount,
                InstanceValue = InventoryStackRuleService.IsStackable(soldSource)
                    ? delete.RemainingCount
                    : soldSource.InstanceValue,
                Durability = soldSource.Durability,
                UpdatedGold = finalGold,
                RequestedCount = sellCount,
                AppliedCount = (short)Math.Min(short.MaxValue, appliedCount),
            };
            return 0;
        }

        internal static bool CanRentWeapon(InventoryService inventory, int itemTemplateId)
        {
            if (inventory == null
                || itemTemplateId <= 0
                || !RentalWeaponInventoryMapper.IsValidInventoryTemplate(itemTemplateId)
                || !ItemMetadataResolver.TryResolveItemKind(itemTemplateId, out _))
            {
                return false;
            }

            if (FindExistingItem(inventory, InventoryListType.Equipment, itemTemplateId)
                || FindExistingItem(inventory, InventoryListType.Main, itemTemplateId))
                return true;

            var requests = new[]
            {
                InventoryRewardGrantRequest.Create(
                    itemTemplateId,
                    1,
                    ItemCreateReason.NpcShopPurchase)
            };
            return InventoryRewardGrantService.TryPlanBatch(inventory, requests, out var plan)
                && plan != null
                && plan.Success;
        }

        private static bool TryNormalizeShopRequest(
            InventoryService inventory,
            int itemTemplateId,
            int requestedCount,
            out ItemMetadata metadata,
            out int effectiveCount)
        {
            metadata = null;
            effectiveCount = 0;
            if (inventory == null || itemTemplateId <= 0 || requestedCount <= 0 || requestedCount > short.MaxValue)
                return false;

            metadata = ItemMetadataResolver.Resolve(itemTemplateId);
            if (metadata == null)
                return false;

            if (string.Equals(metadata.ItemKind, "special", StringComparison.Ordinal)
                && !InventoryService.TryResolveMainVirtualSlotByItemId(itemTemplateId, out _, out _))
                return false;

            effectiveCount = (metadata.IsStackable
                || InventoryService.TryResolveMainVirtualSlotByItemId(itemTemplateId, out _, out _))
                ? requestedCount
                : 1;
            return effectiveCount > 0;
        }

        private static bool CanGrant(InventoryService inventory, int itemTemplateId, int count)
        {
            var requests = new[]
            {
                InventoryRewardGrantRequest.Create(itemTemplateId, count, ItemCreateReason.NpcShopPurchase)
            };
            return InventoryRewardGrantService.TryPlanBatch(inventory, requests, out var plan)
                && plan != null
                && plan.Success;
        }

        private static bool TrySpendCosts(
            InventoryService inventory,
            int goldCost,
            int ceraCost,
            int materialItemId,
            int materialCount,
            SqliteConnection sharedConnection,
            SqliteTransaction sharedTransaction,
            out int updatedGold,
            out int updatedSp,
            out int updatedCera,
            out short materialSlot,
            out int materialRemaining)
        {
            updatedGold = inventory != null ? inventory.CountMainItem(0) : 0;
            updatedSp = 0;
            updatedCera = 0;
            materialSlot = -1;
            materialRemaining = -1;
            if (inventory == null || goldCost < 0 || ceraCost < 0 || materialCount < 0)
                return false;

            if (!TryLoadCurrentWallet(
                    inventory,
                    sharedConnection,
                    sharedTransaction,
                    out var wallet))
                return false;

            updatedSp = wallet.Sp;
            updatedCera = wallet.Cera;

            if (goldCost > 0)
            {
                if (!inventory.TryConsumeMainItem(0, goldCost, out var gold) || !gold.Success)
                    return false;

                updatedGold = gold.RemainingCount;
            }

            if (materialItemId > 0 || materialCount > 0)
            {
                if (materialItemId <= 0 || materialCount <= 0)
                    return false;
                if (!inventory.TryConsumeMainItem(materialItemId, materialCount, out var material) || !material.Success)
                    return false;

                materialSlot = material.SlotIndex;
                materialRemaining = material.RemainingCount;
            }

            if (ceraCost <= 0)
                return true;

            return TrySpendCera(
                inventory,
                ceraCost,
                out updatedCera,
                sharedConnection,
                sharedTransaction);
        }

        private static bool TryLoadCurrentWallet(
            InventoryService inventory,
            SqliteConnection sharedConnection,
            SqliteTransaction sharedTransaction,
            out WalletSnapshot wallet)
        {
            wallet = null;
            var characterId = inventory?.CharacterId ?? 0;
            if (characterId <= 0)
                return false;

            if (sharedConnection != null || sharedTransaction != null)
            {
                if (sharedConnection == null || sharedTransaction == null)
                    return false;

                wallet = CurrencyService.LoadWallet(
                    sharedConnection,
                    sharedTransaction,
                    characterId);
                return wallet != null;
            }

            try
            {
                var database = inventory.Database
                    ?? GameDatabase.CreateDefault();
                using (var connection = database.OpenConnection())
                {
                    wallet = CurrencyService.LoadWallet(connection, null, characterId);
                    return true;
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[InventoryShopRuntime] load wallet failed cid={characterId}: {ex.Message}");
                return false;
            }
        }

        private static bool TrySpendCera(
            InventoryService inventory,
            int ceraCost,
            out int updatedCera,
            SqliteConnection sharedConnection = null,
            SqliteTransaction sharedTransaction = null)
        {
            updatedCera = 0;
            var characterId = inventory?.CharacterId ?? 0;
            if (characterId <= 0 || ceraCost < 0)
                return false;
            if (ceraCost == 0)
                return true;

            try
            {
                if (sharedConnection != null || sharedTransaction != null)
                {
                    if (sharedConnection == null || sharedTransaction == null)
                        return false;

                    var wallet = CurrencyService.LoadWallet(
                        sharedConnection,
                        sharedTransaction,
                        characterId);
                    if (wallet == null
                        || wallet.Cera < ceraCost
                        || !CurrencyService.TrySpendCera(
                            sharedConnection,
                            sharedTransaction,
                            characterId,
                            ceraCost))
                    {
                        return false;
                    }

                    updatedCera = wallet.Cera - ceraCost;
                    return true;
                }

                var database = inventory.Database
                    ?? GameDatabase.CreateDefault();
                using (var connection = database.OpenConnection())
                {
                    using (var transaction = connection.BeginTransaction())
                    {
                        var wallet = CurrencyService.LoadWallet(connection, transaction, characterId);
                        if (wallet.Cera < ceraCost
                            || !CurrencyService.TrySpendCera(connection, transaction, characterId, ceraCost))
                        {
                            return false;
                        }

                        updatedCera = wallet.Cera - ceraCost;
                        transaction.Commit();
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[InventoryShopRuntime] spend cera failed cid={characterId} cost={ceraCost}: {ex.Message}");
                return false;
            }
        }

        private static bool TryRefreshExistingRentalWeapon(
            InventoryService inventory,
            InventoryListType listType,
            int itemTemplateId,
            int expireTime,
            out InventoryMutationResult result)
        {
            result = null;
            foreach (var pair in inventory.GetItems(listType))
            {
                var core = pair.Value;
                if (core == null || core.ItemId != itemTemplateId)
                    continue;

                var updated = core.Copy();
                ApplyRentalWeaponRenewalFields(updated, expireTime);
                if (!inventory.SetItem(listType, pair.Key, updated))
                    return false;

                result = new InventoryMutationResult
                {
                    ListType = listType,
                    SlotIndex = pair.Key,
                    ItemTemplateId = updated.ItemId,
                    RemainingStackCount = 1,
                    InstanceValue = updated.InstanceValue,
                    Durability = updated.Durability,
                    CoreSnapshot = updated.Copy(),
                    UpdatedGold = inventory.CountMainItem(0),
                    RequestedCount = 1,
                    AppliedCount = 1,
                };
                return true;
            }

            return false;
        }

        private static bool FindExistingItem(
            InventoryService inventory,
            InventoryListType listType,
            int itemTemplateId)
        {
            foreach (var pair in inventory.GetItems(listType))
            {
                var core = pair.Value;
                if (core != null && core.ItemId == itemTemplateId)
                    return true;
            }

            return false;
        }

        private static InventoryCreateOptions CreateRentalWeaponOptions(int expireTime)
        {
            return new InventoryCreateOptions
            {
                ExpireTime = expireTime,
            };
        }

        private static void ApplyRentalWeaponRenewalFields(ItemCore core, int expireTime)
        {
            if (core == null)
                return;

            core.ExpireTime = expireTime;
            core.Marker16 = ItemCore.Marker16Default;
            var metadata = ItemMetadataResolver.Resolve(core.ItemId);
            if (metadata != null && metadata.Durability > 0)
                core.Durability = metadata.Durability;
        }

        private static InventoryMutationResult ToMutationResult(
            InventoryService inventory,
            InventoryRewardGrantResult grant,
            int updatedGold,
            int updatedSp,
            int updatedCera,
            int requestedCount)
        {
            var result = new InventoryMutationResult
            {
                ListType = grant.ListType,
                SlotIndex = grant.SlotIndex,
                ItemTemplateId = grant.ItemTemplateId,
                UpdatedGold = updatedGold,
                UpdatedSp = updatedSp,
                UpdatedCoin = updatedCera,
                RequestedCount = checked((short)Math.Min(short.MaxValue, requestedCount)),
                AppliedCount = checked((short)Math.Min(short.MaxValue, grant.GrantedCount)),
            };

            var core = grant.SlotIndex >= 0
                ? inventory.GetItem(grant.ListType, grant.SlotIndex)
                : null;
            if (core == null)
                core = grant.Core;

            if (grant.Kind == InventoryRewardGrantKind.MainVirtualCount)
            {
                result.RemainingStackCount = grant.FinalCount;
                result.InstanceValue = grant.FinalCount;
                result.Durability = 0;
                return result;
            }

            if (core != null)
            {
                result.ItemTemplateId = core.ItemId;
                result.RemainingStackCount = InventoryStackRuleService.IsStackable(core)
                    ? core.Count
                    : Math.Max(1, grant.GrantedCount);
                result.InstanceValue = core.Value;
                result.Durability = core.Durability;
                result.ExtData0 = core.Attr;
                result.ExpireTime = core.ExpireTime;
                result.CoreSnapshot = core.Copy();
            }

            return result;
        }

        private static bool IsSupportedSellListType(InventoryListType listType)
        {
            return listType == InventoryListType.Main
                || listType == InventoryListType.PersonalCargo
                || listType == InventoryListType.Avatar
                || listType == InventoryListType.Equipment
                || listType == InventoryListType.Pet;
        }

        private static bool IsItemLocked(InventoryService inventory, ItemCore core)
        {
            return inventory != null
                && core != null
                && core.EquipmentLockId != 0
                && inventory.EquipmentLocks.TryGet(core.EquipmentLockId, out var itemLock)
                && itemLock != null
                && itemLock.State != 0;
        }

        private static int NormalizeSellRemovalCount(ItemCore source, short requestedCount)
        {
            if (source == null)
                return 0;
            if (!InventoryStackRuleService.IsStackable(source))
                return 1;

            var currentCount = Math.Max(0, source.Count);
            if (requestedCount <= 0 || requestedCount >= currentCount)
                return currentCount;

            return requestedCount;
        }

        private static int LoadGoldCarryLimit(
            InventoryService inventory,
            SqliteConnection sharedConnection = null,
            SqliteTransaction sharedTransaction = null)
        {
            var characterId = inventory?.CharacterId ?? 0;
            if (characterId <= 0)
                return int.MaxValue;

            try
            {
                if (sharedConnection != null || sharedTransaction != null)
                {
                    if (sharedConnection == null || sharedTransaction == null)
                        return int.MaxValue;

                    var sharedLimit = CharacterGoldLimitRepository.LoadEffectiveGoldCarryLimit(
                        sharedConnection,
                        sharedTransaction,
                        characterId);
                    return sharedLimit <= 0 ? int.MaxValue : sharedLimit;
                }

                var database = inventory.Database
                    ?? GameDatabase.CreateDefault();
                using (var connection = database.OpenConnection())
                {
                    using (var transaction = connection.BeginTransaction())
                    {
                        var limit = CharacterGoldLimitRepository.LoadEffectiveGoldCarryLimit(
                            connection,
                            transaction,
                            characterId);
                        transaction.Commit();
                        return limit <= 0 ? int.MaxValue : limit;
                    }
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"[InventoryShopRuntime] load gold carry limit failed cid={characterId}: {ex.Message}");
                return int.MaxValue;
            }
        }

    }
}
