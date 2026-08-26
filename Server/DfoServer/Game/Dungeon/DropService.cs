using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.GameWorld;
using DfoServer.Network;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Dungeon
{
    internal sealed class DropService
    {
        private const int MaxGoldPerDrop = 1000;

        internal DropService()
        {
        }

        internal static void WarmUpAbyssParty()
        {
            HellMonsterDropConfig.WarmUp();
        }

        internal bool TryRegisterTemplateDrop(
            DungeonRun run,
            int itemTemplateId,
            int count,
            out DropInfo drop)
        {
            drop = default;
            if (run == null || itemTemplateId <= 0 || count <= 0)
                return false;

            ItemMetadata metadata;
            try
            {
                metadata = ItemMetadataResolver.Resolve(itemTemplateId);
            }
            catch (Exception)
            {
                return false;
            }

            if (metadata == null
                || (metadata.ItemKind != "equipment" && metadata.ItemKind != "stackable"))
            {
                return false;
            }

            lock (run.SyncRoot)
            {
                run.SceneSlotCounter++;
                if (run.SceneSlotCounter == 0)
                    run.SceneSlotCounter++;

                drop = new DropInfo
                {
                    SceneSlot = run.SceneSlotCounter,
                    TemplateId = (uint)itemTemplateId,
                    StackCount = (uint)count,
                    DropGroupId = CurrentDropGroupId(),
                    Endurance = metadata.Durability,
                };
                run.Drops[drop.SceneSlot] = drop;
            }

            return true;
        }

        internal MonsterDropResult GenerateAndRegister(DungeonRun run, MonsterDropRequest request)
        {
            if (run == null || !run.RewardPolicy.AllowsMonsterDrops)
                return default;

            var slotCounter = run.SceneSlotCounter;

            IReadOnlyList<MonsterDropTable.DropPoolEntry> dropPool = null;
            if (run.DropPolicy.Allows(
                    DungeonMonsterDropSource.MonsterTemplateItems))
            {
                dropPool = MonsterDropTable.GetDropPool(request.MonsterCode);
            }

            if (run.DropPolicy.Allows(DungeonMonsterDropSource.AreaMaterials))
            {
                var areaMaterialId = AreaMaterialDropProvider.GetAreaMaterialItem(
                    run.DungeonId);
                if (areaMaterialId > 0)
                {
                    var extended = new List<MonsterDropTable.DropPoolEntry>();
                    if (dropPool != null)
                        extended.AddRange(dropPool);
                    extended.Add(new MonsterDropTable.DropPoolEntry
                    {
                        ItemId = areaMaterialId,
                        Weight = 100,
                    });
                    dropPool = extended;
                }
            }

            var generator = new DropGenerator(run.RoomLcg);
            var result = generator.GenerateMonsterDrops(
                request.DropRateLevel, request.MonsterType, request.MonsterCode,
                run.Difficulty, request.DungeonBasisLevel,
                run.EntryPartyMemberCount,
                run.ChronicleDropJobGroup,
                run.DropPolicy,
                ref slotCounter, dropPool);
            if (run.DropPolicy.Allows(DungeonMonsterDropSource.Dimension))
            {
                var dimensionDrops = DimensionDropSystem.GenerateMonsterDrops(
                    run.DungeonId,
                    request.MonsterCode,
                    run.DimensionDropJob,
                    run.DimensionDropGrowType,
                    run.RoomLcg,
                    ref slotCounter);
                if (dimensionDrops.Count > 0)
                    result.drops.AddRange(dimensionDrops);
            }

            run.SceneSlotCounter = slotCounter;
            RegisterDrops(run, result.drops);

            return new MonsterDropResult
            {
                GoldAmount = result.goldAmount,
                Drops = result.drops
            };
        }

        internal List<DropInfo> GenerateAbyssPartyAndRegister(DungeonRun run, AbyssPartyDropRequest request)
        {
            if (run == null || !run.RewardPolicy.AllowsMonsterDrops)
                return new List<DropInfo>();

            var slotCounter = run.SceneSlotCounter;

            var drops = run.DropPolicy.Allows(DungeonMonsterDropSource.Independent)
                ? IndependentDropSystem.GenerateDrops(
                    request.MonsterCode,
                    run.Difficulty,
                    request.DungeonBasisLevel,
                    run.EntryPartyMemberCount,
                    run.ChronicleDropJobGroup,
                    run.RoomLcg,
                    ref slotCounter)
                : new List<DropInfo>();

            if (request.IsLastGroupMonster && !request.IsAbyssMonsterScript)
            {
                var rewardDrops = HellMonsterDropConfig.GenerateSpecificEquipmentDrops(
                    run.RoomLcg,
                    request.DungeonMinimumLevel,
                    request.DungeonBasisLevel,
                    run.Difficulty,
                    request.AbyssPartyDifficulty,
                    request.RewardRollCount,
                    ref slotCounter);
                drops.AddRange(rewardDrops);

                var epicPieceDrops = GenerateAbyssEpicPieceDrops(
                    run,
                    request,
                    ref slotCounter);
                drops.AddRange(epicPieceDrops);
            }

            run.SceneSlotCounter = slotCounter;
            RegisterDrops(run, drops);
            return drops;
        }

        private static List<DropInfo> GenerateAbyssEpicPieceDrops(
            DungeonRun run,
            AbyssPartyDropRequest request,
            ref ushort slotCounter)
        {
            var results = EpicPieceDropService.Instance.Roll(
                new EpicPieceDropRollRequest
                {
                    DungeonId = run.DungeonId,
                    MonsterId = request.MonsterCode,
                    DungeonDifficulty = run.Difficulty,
                    HellDifficulty = request.AbyssPartyDifficulty,
                    DungeonMinimumLevel = request.DungeonMinimumLevel,
                    DungeonBasisLevel = request.DungeonBasisLevel,
                    Random = run.RoomLcg,
                });
            if (results.Count == 0)
                return new List<DropInfo>();

            var drops = new List<DropInfo>(results.Count);
            for (var i = 0; i < results.Count; i++)
            {
                var result = results[i];
                slotCounter++;
                drops.Add(DropInfo.CreateItem(
                    slotCounter,
                    result.EpicPieceId,
                    Math.Max(1, result.Count)));
                FileLogger.Log(
                    $"[DropService] abyss epic piece roll#{i + 1}: " +
                    $"equipment={result.EpicEquipmentId} " +
                    $"piece={result.EpicPieceId} " +
                    $"count={result.Count} sceneSlot={slotCounter}");
            }

            return drops;
        }

        internal PickupResult TryPickup(DungeonRun run, ushort srcSlot, EnhancedClientSession session)
        {
            var characterId = session?.Player?.CharacterId ?? 0;
            if (characterId <= 0
                || !InventoryContext.TryGetLease(characterId, out var lease)
                || !lease.IsOwnedBy(session.SessionId))
            {
                FileLogger.Log($"[DropService] online inventory missing cid={characterId} sceneSlot={srcSlot}");
                return PickupResult.PersistenceFailed;
            }

            return TryPickup(run, srcSlot, lease);
        }

        internal PickupResult TryPickup(DungeonRun run, ushort srcSlot, InventoryLease lease)
        {
            if (run == null)
                return PickupResult.NotFound;
            if (lease == null)
                return PickupResult.PersistenceFailed;

            // The ground-drop dictionary and the inventory mutation form one
            // process-local claim boundary. Without this lock, two GET_ITEM
            // requests can both read the same drop before either removes it.
            lock (run.SyncRoot)
            {
                if (!run.Drops.TryGetValue(srcSlot, out var drop))
                    return PickupResult.NotFound;

                var carryLimit = drop.IsGold
                    ? InventoryGoldCarryLimitLoader.Load(lease.Inventory)
                    : int.MaxValue;
                lock (lease.SyncRoot)
                {
                    if (drop.IsGold)
                    {
                        var baseGold = (int)drop.StackCount;
                        var bonusPct = drop.IsPlayerDropped ? 0 : GetEquippedGoldBonus(lease.Inventory);
                        var extraGold = baseGold * bonusPct / 100;
                        if (!lease.Inventory.TryGrantGold(baseGold + extraGold, carryLimit, out var grantedGold, out _))
                            return PickupResult.PersistenceFailed;

                        run.Drops.Remove(srcSlot);
                        var grantedBaseGold = Math.Min(baseGold, grantedGold);
                        var grantedExtraGold = Math.Min(extraGold, Math.Max(0, grantedGold - grantedBaseGold));
                        return new PickupResult
                        {
                            Success = true,
                            IsGold = true,
                            GoldAmount = grantedGold,
                            ExtraGold = grantedExtraGold,
                        };
                    }

                    var pickupCount = NormalizePickupCount(drop.StackCount);
                    if (!CanPlanPickupDrop(lease.Inventory, drop, pickupCount))
                        return PickupResult.InventoryFull;

                    var pickedItemId = drop.Core != null ? drop.Core.ItemId : (int)drop.TemplateId;
                    InventoryRewardGrantResult grant;
                    bool inserted;
                    if (drop.Core != null)
                    {
                        inserted = InventoryRewardGrantService.TryInsertExisting(
                            lease.Inventory,
                            drop.Core.Copy(),
                            pickupCount,
                            out grant);
                    }
                    else
                    {
                        inserted = InventoryRewardGrantService.TryCreateAndInsert(
                            lease.Inventory,
                            (int)drop.TemplateId,
                            ItemCreateReason.DungeonDrop,
                            pickupCount,
                            out grant);
                    }

                    if (!inserted || !grant.Success)
                        return PickupResult.InventoryFull;

                    run.Drops.Remove(srcSlot);
                    return new PickupResult
                    {
                        Success = true,
                        IsGold = false,
                        InventorySlot = grant.SlotIndex,
                        PickedUpItemId = pickedItemId,
                        IsEpicPiece = grant.Kind == InventoryRewardGrantKind.EpicPiece,
                        EpicPieceBalance = grant.Kind == InventoryRewardGrantKind.EpicPiece ? grant.FinalCount : 0,
                    };
                }
            }
        }

        private static bool CanPlanPickupDrop(
            InventoryService inventory,
            DropInfo drop,
            int pickupCount)
        {
            if (inventory == null || pickupCount <= 0)
                return false;

            var request = drop.Core != null
                ? InventoryRewardGrantRequest.Existing(drop.Core.Copy(), pickupCount)
                : InventoryRewardGrantRequest.Create(
                    (int)drop.TemplateId,
                    pickupCount,
                    ItemCreateReason.DungeonDrop);

            return InventoryRewardGrantService.TryPlanBatch(
                    inventory,
                    new[] { request },
                    out var plan)
                && plan != null
                && plan.Success;
        }

        internal InventoryDropResult TryDropInventoryItem(
            DungeonRun run,
            EnhancedClientSession session,
            InventoryListType listType,
            short slotIndex,
            int count)
        {
            var characterId = session?.Player?.CharacterId ?? 0;
            if (characterId <= 0
                || !InventoryContext.TryGetLease(characterId, out var lease)
                || !lease.IsOwnedBy(session.SessionId))
                return InventoryDropResult.InventoryRejected;

            return TryDropInventoryItem(run, lease, listType, slotIndex, count);
        }

        internal InventoryDropResult TryDropInventoryItem(
            DungeonRun run,
            int characterId,
            InventoryListType listType,
            short slotIndex,
            int count)
        {
            if (characterId <= 0 || !InventoryContext.TryGetLease(characterId, out var lease))
                return InventoryDropResult.InventoryRejected;

            return TryDropInventoryItem(run, lease, listType, slotIndex, count);
        }

        internal InventoryDropResult TryDropInventoryItem(
            DungeonRun run,
            InventoryLease lease,
            InventoryListType listType,
            short slotIndex,
            int count)
        {
            if (run == null
                || lease == null
                || listType != InventoryListType.Main
                || slotIndex < 0
                || count <= 0)
                return InventoryDropResult.InvalidRequest;

            lock (lease.SyncRoot)
            {
                if (slotIndex == InventoryService.MainVirtualCurrencySlotStart)
                    return TryDropGold(run, lease.Inventory, count);

                var source = lease.Inventory.GetItem(listType, slotIndex);
                if (source == null)
                    return InventoryDropResult.InventoryRejected;

                if (IsEquipmentItemLocked(lease.Inventory, source))
                    return InventoryDropResult.InventoryRejected;

                var metadata = ItemMetadataResolver.Resolve(source.ItemId);
                if (!CanDrop(source, metadata, out var rejectReason))
                {
                    FileLogger.Log($"[DungeonDrop] REJECT: cid={lease.CharacterId} slot={slotIndex} item={source.ItemId} reason={rejectReason}");
                    return InventoryDropResult.InventoryRejected;
                }

                var stackable = InventoryStackRuleService.IsStackable(source);
                var availableCount = stackable ? Math.Max(0, source.Count) : 1;
                if (count > availableCount || (!stackable && count != 1))
                    return InventoryDropResult.InventoryRejected;

                var droppedCore = source.Copy();
                var droppedCount = stackable ? count : 1;
                if (stackable)
                    droppedCore.Count = droppedCount;

                InventoryDeleteResult delete;
                var removed = stackable
                    ? InventoryDeleteService.TryDecreaseStack(lease.Inventory, listType, slotIndex, count, out delete)
                    : InventoryDeleteService.TryRemoveSlot(lease.Inventory, listType, slotIndex, out delete);
                if (!removed || delete == null || !delete.Success)
                    return InventoryDropResult.InventoryRejected;

                var drop = RegisterInventoryDrop(run, droppedCore, droppedCount, slotIndex);
                return new InventoryDropResult
                {
                    Success = true,
                    Drop = drop,
                    RemainingStackCount = delete.RemainingCount,
                };
            }
        }

        private static void RegisterDrops(DungeonRun run, List<DropInfo> drops)
        {
            if (drops == null || drops.Count == 0) return;
            var groupId = CurrentDropGroupId();
            for (var index = 0; index < drops.Count; index++)
            {
                var drop = drops[index];
                if (drop.DropGroupId == 0)
                    drop.DropGroupId = groupId;
                drops[index] = drop;
                run.Drops[drop.SceneSlot] = drop;
            }
        }

        private static InventoryDropResult TryDropGold(DungeonRun run, InventoryService inventory, int count)
        {
            if (count > MaxGoldPerDrop)
                return InventoryDropResult.InventoryRejected;

            if (inventory == null
                || !inventory.TryConsumeMainItem(InventoryService.MainVirtualCurrencySlotStart, count, out var consume)
                || consume == null
                || !consume.Success)
                return InventoryDropResult.InventoryRejected;

            var drop = RegisterInventoryDrop(
                run,
                null,
                count,
                InventoryService.MainVirtualCurrencySlotStart);
            return new InventoryDropResult
            {
                Success = true,
                Drop = drop,
                RemainingStackCount = consume.RemainingCount,
            };
        }

        private static DropInfo RegisterInventoryDrop(
            DungeonRun run,
            ItemCore core,
            int count,
            short sourceSlotIndex)
        {
            lock (run.SyncRoot)
            {
                run.SceneSlotCounter++;
                if (run.SceneSlotCounter == 0)
                    run.SceneSlotCounter++;

                var drop = new DropInfo
                {
                    SceneSlot = run.SceneSlotCounter,
                    TemplateId = core != null ? unchecked((uint)core.ItemId) : 0,
                    StackCount = unchecked((uint)Math.Max(0, count)),
                    DropGroupId = CurrentDropGroupId(),
                    Endurance = core != null ? core.Durability : (ushort)0,
                    UpgradeLevel = core != null ? core.Upgrade : (byte)0,
                    Core = core != null ? core.Copy() : null,
                    SourceSlotIndex = sourceSlotIndex,
                    IsPlayerDropped = true,
                };
                run.Drops[drop.SceneSlot] = drop;
                return drop;
            }
        }

        private static uint CurrentDropGroupId()
            => unchecked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());

        private static int NormalizePickupCount(uint stackCount)
        {
            if (stackCount == 0)
                return 1;

            return stackCount > int.MaxValue ? int.MaxValue : (int)stackCount;
        }

        private static int GetEquippedGoldBonus(InventoryService inventory)
        {
            if (inventory == null)
                return 0;

            var totalBonus = 0;
            foreach (var pair in inventory.GetItems(InventoryListType.Equipment))
            {
                var itemId = pair.Value?.ItemId ?? 0;
                if (GoldBonusEquipments.TryGetValue(itemId, out var bonus))
                    totalBonus += bonus;
            }

            return totalBonus;
        }

        private static bool IsEquipmentItemLocked(InventoryService inventory, ItemCore core)
        {
            return inventory != null
                && core != null
                && core.EquipmentLockId != 0
                && inventory.EquipmentLocks.TryGet(core.EquipmentLockId, out var itemLock)
                && itemLock != null
                && itemLock.State != 0;
        }

        private static bool CanDrop(ItemCore item, ItemMetadata metadata, out string rejectReason)
        {
            rejectReason = null;
            if (item == null || metadata == null || string.Equals(metadata.ItemKind, "special", StringComparison.Ordinal))
            {
                rejectReason = "missing current-PVF metadata";
                return false;
            }

            if (metadata.Rarity > 2)
            {
                rejectReason = $"rarity {metadata.Rarity} exceeds 2";
                return false;
            }

            var attachType = NormalizePvfToken(metadata.AttachType);
            var allowedAttachType = attachType == "free"
                || attachType == "sealing trade"
                || attachType == "trade limit"
                || (attachType == "sealing" && item.ItemKind == ItemCore.KindEquipment);
            if (!allowedAttachType)
            {
                rejectReason = $"attach type [{attachType}]";
                return false;
            }

            if (item.TradeRestriction != 0)
            {
                rejectReason = "instance trade restriction";
                return false;
            }

            if (EquipmentTypeInfo.ParseOrUnknown(metadata.EquipmentType) == EquipmentType.TitleName)
            {
                rejectReason = "title equipment";
                return false;
            }

            return true;
        }

        private static string NormalizePvfToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var trimmed = value.Trim();
            if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[trimmed.Length - 1] == ']')
                trimmed = trimmed.Substring(1, trimmed.Length - 2);

            return trimmed.Trim().ToLowerInvariant();
        }

        private static readonly Dictionary<int, int> GoldBonusEquipments = new()
        {
            {100320775, 12},
            {24191, 10},
            {100341606, 30},
            {100331240, 10},
            {100331319, 3},
            {26626, 3},
            {26627, 4},
            {26341, 3},
            {26342, 4},
            {26115, 3},
            {104000181, 3},
            {101020286, 3},
            {101020526, 3},
            {109000133, 3}
        };
    }

    internal struct MonsterDropRequest
    {
        public int DropRateLevel;
        public int MonsterType;
        public int MonsterCode;
        public int DungeonBasisLevel;
    }

    internal struct AbyssPartyDropRequest
    {
        public int MonsterCode;
        public int DungeonMinimumLevel;
        public int DungeonBasisLevel;
        public byte AbyssPartyDifficulty;
        public int RewardRollCount;
        public bool IsLastGroupMonster;
        public bool IsAbyssMonsterScript;
    }

    internal struct MonsterDropResult
    {
        public int GoldAmount;
        public List<DropInfo> Drops;
    }

    internal struct PickupResult
    {
        public bool Success;
        public bool IsGold;
        public int GoldAmount;
        public int ExtraGold;
        public short InventorySlot;
        public int PickedUpItemId;
        public bool IsEpicPiece;
        public int EpicPieceBalance;
        public PickupFailReason FailReason;

        internal static readonly PickupResult NotFound = new PickupResult { FailReason = PickupFailReason.NotFound };
        internal static readonly PickupResult InventoryFull = new PickupResult { FailReason = PickupFailReason.InventoryFull };
        internal static readonly PickupResult PersistenceFailed = new PickupResult { FailReason = PickupFailReason.PersistenceFailed };
    }

    internal enum PickupFailReason : byte
    {
        None,
        NotFound,
        InventoryFull,
        PersistenceFailed
    }

    internal struct InventoryDropResult
    {
        internal bool Success;
        internal DropInfo Drop;
        internal int RemainingStackCount;
        internal InventoryDropFailReason FailReason;

        internal static readonly InventoryDropResult InvalidRequest = new InventoryDropResult
        {
            FailReason = InventoryDropFailReason.InvalidRequest,
        };

        internal static readonly InventoryDropResult InventoryRejected = new InventoryDropResult
        {
            FailReason = InventoryDropFailReason.InventoryRejected,
        };
    }

    internal enum InventoryDropFailReason : byte
    {
        None,
        InvalidRequest,
        InventoryRejected,
    }
}
