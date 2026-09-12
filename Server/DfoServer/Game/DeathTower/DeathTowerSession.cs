using System;
using System.Collections.Generic;
using System.Linq;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;

namespace DfoServer.Game.DeathTower
{
    public sealed class DeathTowerSession
    {
        private readonly Dictionary<ushort, List<StageTowerItem>> _stageItemsByMonster =
            new Dictionary<ushort, List<StageTowerItem>>();
        private readonly Dictionary<ushort, DropInfo> _groundItems =
            new Dictionary<ushort, DropInfo>();
        private readonly HashSet<ushort> _deadMonsters = new HashSet<ushort>();
        private readonly Dictionary<DeathTowerInventoryEndpoint, TowerInventoryItem> _inventoryItems =
            new Dictionary<DeathTowerInventoryEndpoint, TowerInventoryItem>();
        private readonly HashSet<int> _seenItemIds = new HashSet<int>();
        private readonly object _stageLoadingReleaseSyncRoot = new object();
        private DnfLcg _stageLcg;
        private DungeonRunIdentity _pendingStageLoadingRun;
        private int _pendingStageLoadingStage = -1;

        public DeathTowerData.TowerConfig Config { get; }
        public int CurrentStage { get; private set; }
        public int EndStage => Config.TotalStages - 1;
        public ushort MonsterSequence { get; private set; }
        public ushort ItemSequence { get; private set; }
        public int State { get; private set; }  // 0=init, 1=fighting, 2=cleared
        public uint StageSeed { get; private set; }
        internal DnfLcg StageLcg => _stageLcg;
        public IReadOnlyDictionary<ushort, DropInfo> GroundItems => _groundItems;
        public IReadOnlyDictionary<DeathTowerInventoryEndpoint, TowerInventoryItem> InventoryItems
            => _inventoryItems;
        public IReadOnlyCollection<int> SeenItemIds => _seenItemIds;

        public DeathTowerSession(DeathTowerData.TowerConfig config)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            CurrentStage = 0;
            MonsterSequence = 1;
            ItemSequence = 1;
            State = 0;
        }

        public int GetCurrentMapId()
        {
            if (CurrentStage < 0 || CurrentStage >= Config.StageMapIds.Count)
                return -1;
            return Config.StageMapIds[CurrentStage];
        }

        public ushort NextMonsterSeq() => MonsterSequence++;

        public ushort NextItemSeq()
        {
            var value = ItemSequence++;
            if (value != 0)
                return value;
            return ItemSequence++;
        }

        public void BeginStage(uint seed, IReadOnlyList<StageTowerItem> items)
        {
            StageSeed = seed;
            _stageLcg = new DnfLcg(seed);
            _stageItemsByMonster.Clear();
            _groundItems.Clear();
            _deadMonsters.Clear();

            if (items == null || items.Count == 0)
                return;

            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if (item.ItemId > 0)
                    _seenItemIds.Add(item.ItemId);
                if (item.SourceMonsterUniqueId == 0
                    || item.ItemUniqueId == 0
                    || item.ItemId <= 0)
                {
                    continue;
                }

                if (!_stageItemsByMonster.TryGetValue(item.SourceMonsterUniqueId, out var bucket))
                {
                    bucket = new List<StageTowerItem>();
                    _stageItemsByMonster[item.SourceMonsterUniqueId] = bucket;
                }
                bucket.Add(item);
            }
        }

        public IReadOnlyList<DropInfo> GenerateDropsForMonster(ushort monsterUniqueId)
        {
            if (monsterUniqueId == 0 || !_deadMonsters.Add(monsterUniqueId))
                return Array.Empty<DropInfo>();
            if (!_stageItemsByMonster.TryGetValue(monsterUniqueId, out var configuredItems))
                return Array.Empty<DropInfo>();

            var drops = new List<DropInfo>();
            var dropGroupId = unchecked((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            foreach (var item in configuredItems)
            {
                var dropRate = Math.Max(0, Math.Min(10000, item.DropRate));
                if (dropRate == 0)
                    continue;
                if (dropRate < 10000 && (_stageLcg == null || _stageLcg.Next(10000) >= dropRate))
                    continue;

                var drop = new DropInfo
                {
                    SceneSlot = item.ItemUniqueId,
                    TemplateId = (uint)item.ItemId,
                    StackCount = (uint)Math.Max(1, item.StackCount),
                    DropGroupId = dropGroupId,
                };
                if (_groundItems.ContainsKey(drop.SceneSlot))
                    continue;

                _groundItems[drop.SceneSlot] = drop;
                drops.Add(drop);
            }

            return drops;
        }

        public bool TryPickupGroundItem(ushort sceneSlot, out TowerPickupResult result)
            => TryPickupGroundItem(
                sceneSlot,
                ItemSlotBoundService.MainExpandStageFull,
                out result);

        internal bool TryPickupGroundItem(
            ushort sceneSlot,
            int mainExpandStageKey,
            out TowerPickupResult result)
            => TryPickupGroundItem(
                sceneSlot,
                mainExpandStageKey,
                null,
                out result);

        internal bool TryPickupGroundItem(
            ushort sceneSlot,
            int mainExpandStageKey,
            Func<DeathTowerInventoryEndpoint, bool> isPersistentSlotOccupied,
            out TowerPickupResult result)
        {
            result = null;
            if (!_groundItems.TryGetValue(sceneSlot, out var drop)
                || drop.TemplateId == 0
                || drop.TemplateId > int.MaxValue
                || drop.StackCount == 0
                || drop.StackCount > int.MaxValue)
            {
                return false;
            }

            var itemId = (int)drop.TemplateId;
            if (!TryAddInventoryItem(
                    itemId,
                    (int)drop.StackCount,
                    mainExpandStageKey,
                    isPersistentSlotOccupied,
                    out var destination,
                    out var changedEndpoints))
                return false;

            _groundItems.Remove(sceneSlot);
            _seenItemIds.Add(itemId);
            result = new TowerPickupResult
            {
                DestinationEndpoint = destination,
                DestinationSlot = destination.SlotIndex,
                ItemId = itemId,
                ChangedSlots = changedEndpoints
                    .Select(endpoint => endpoint.SlotIndex)
                    .ToArray(),
                ChangedEndpoints = changedEndpoints,
            };
            return true;
        }

        public bool TryUseItem(short slot, int expectedItemId, out TowerInventoryMutation result)
            => TryUseItem(
                ResolveLegacyEndpoint(slot),
                expectedItemId,
                out result);

        internal bool TryUseItem(
            DeathTowerInventoryEndpoint endpoint,
            int expectedItemId,
            out TowerInventoryMutation result)
        {
            result = null;
            if (!_inventoryItems.TryGetValue(endpoint, out var item)
                || item.ItemId != expectedItemId
                || (!item.IsQuickSlotConsumable && !item.IsWaste)
                || item.Count <= 0)
            {
                return false;
            }

            item.Count--;
            var remaining = item.Count;
            if (remaining == 0)
                _inventoryItems.Remove(endpoint);

            result = new TowerInventoryMutation
            {
                ItemId = item.ItemId,
                RemainingCount = remaining,
                ChangedSlots = new[] { endpoint.SlotIndex },
                Endpoint = endpoint,
            };
            return true;
        }

        public bool TryMoveItem(
            short sourceSlot,
            short destinationSlot,
            int requestedCount,
            out TowerInventoryMoveResult result)
            => TryMoveItem(
                ResolveLegacyEndpoint(sourceSlot),
                ResolveLegacyEndpoint(destinationSlot),
                requestedCount,
                out result);

        internal bool TryMoveItem(
            DeathTowerInventoryEndpoint sourceEndpoint,
            DeathTowerInventoryEndpoint destinationEndpoint,
            int requestedCount,
            out TowerInventoryMoveResult result)
            => TryMoveItem(
                sourceEndpoint,
                destinationEndpoint,
                requestedCount,
                null,
                out result);

        internal bool TryMoveItem(
            DeathTowerInventoryEndpoint sourceEndpoint,
            DeathTowerInventoryEndpoint destinationEndpoint,
            int requestedCount,
            Func<DeathTowerInventoryEndpoint, bool> isPersistentSlotOccupied,
            out TowerInventoryMoveResult result)
        {
            result = null;
            if (!_inventoryItems.TryGetValue(sourceEndpoint, out var source))
                return false;
            var sourceMetadata = Inventory.ItemMetadataResolver.Resolve(source.ItemId);
            if (!DeathTowerItemSlotPolicy.IsSlotAllowed(sourceMetadata, destinationEndpoint))
                return false;

            if (sourceEndpoint.Equals(destinationEndpoint))
            {
                result = CreateMoveResult(
                    requestedCount,
                    Array.Empty<DeathTowerInventoryEndpoint>());
                return true;
            }

            // Main is a shared physical coordinate space. A tower overlay may
            // never hide or overwrite an online item in that space. QuickSlot
            // remains a separate typed endpoint and is intentionally excluded.
            if (IsPersistentMainSlotOccupied(
                    destinationEndpoint,
                    isPersistentSlotOccupied))
            {
                return false;
            }

            var moveCount = requestedCount <= 0
                ? source.Count
                : Math.Min(requestedCount, source.Count);
            if (moveCount <= 0)
                return false;

            if (!_inventoryItems.TryGetValue(destinationEndpoint, out var destination))
            {
                var moved = CreateInventoryItem(source.ItemId, moveCount, sourceMetadata);
                _inventoryItems[destinationEndpoint] = moved;
                source.Count -= moveCount;
                if (source.Count == 0)
                    _inventoryItems.Remove(sourceEndpoint);
                result = CreateMoveResult(
                    requestedCount,
                    new[] { sourceEndpoint, destinationEndpoint });
                return true;
            }

            if (destination.ItemId == source.ItemId)
            {
                var available = Math.Max(0, destination.StackLimit - destination.Count);
                var merged = Math.Min(moveCount, available);
                if (merged <= 0)
                    return false;
                destination.Count += merged;
                source.Count -= merged;
                if (source.Count == 0)
                    _inventoryItems.Remove(sourceEndpoint);
                result = CreateMoveResult(
                    requestedCount,
                    new[] { sourceEndpoint, destinationEndpoint });
                return true;
            }

            if (moveCount != source.Count)
                return false;
            var destinationMetadata = Inventory.ItemMetadataResolver.Resolve(destination.ItemId);
            if (!DeathTowerItemSlotPolicy.IsSlotAllowed(destinationMetadata, sourceEndpoint))
                return false;

            _inventoryItems[sourceEndpoint] = destination;
            _inventoryItems[destinationEndpoint] = source;
            result = CreateMoveResult(
                requestedCount,
                new[] { sourceEndpoint, destinationEndpoint });
            return true;
        }

        public bool TryGetInventoryItem(short slot, out TowerInventoryItem item)
            => TryGetInventoryItem(ResolveLegacyEndpoint(slot), out item);

        internal bool TryGetInventoryItem(
            DeathTowerInventoryEndpoint endpoint,
            out TowerInventoryItem item)
            => _inventoryItems.TryGetValue(endpoint, out item);

        internal IReadOnlyList<DeathTowerInventoryEndpoint> FindInventoryEndpointsByItemId(
            int itemId)
        {
            if (itemId <= 0)
                return Array.Empty<DeathTowerInventoryEndpoint>();

            var result = new List<DeathTowerInventoryEndpoint>();
            foreach (var pair in _inventoryItems)
            {
                if (pair.Value != null && pair.Value.ItemId == itemId)
                    result.Add(pair.Key);
            }
            return result;
        }

        internal Dictionary<DeathTowerInventoryEndpoint, TowerInventoryItem> CopyInventoryItems()
        {
            var result = new Dictionary<DeathTowerInventoryEndpoint, TowerInventoryItem>();
            foreach (var pair in _inventoryItems)
                result[pair.Key] = pair.Value.Copy();
            return result;
        }

        internal void ReplaceInventoryItems(
            IReadOnlyDictionary<DeathTowerInventoryEndpoint, TowerInventoryItem> items)
        {
            _inventoryItems.Clear();
            if (items == null)
                return;

            foreach (var pair in items)
            {
                if (pair.Value == null
                    || pair.Value.ItemId <= 0
                    || pair.Value.Count <= 0)
                {
                    continue;
                }

                _inventoryItems[pair.Key] = pair.Value.Copy();
            }
        }

        internal void ReplaceInventoryItems(
            IReadOnlyDictionary<short, TowerInventoryItem> items)
        {
            var typed = new Dictionary<DeathTowerInventoryEndpoint, TowerInventoryItem>();
            if (items != null)
            {
                foreach (var pair in items)
                {
                    typed[ResolveLegacyEndpoint(pair.Key)] = pair.Value;
                }
            }
            ReplaceInventoryItems(typed);
        }

        public IReadOnlyDictionary<int, int> GetItemCountsSnapshot()
        {
            var result = new Dictionary<int, int>();
            foreach (var item in _inventoryItems.Values)
            {
                result.TryGetValue(item.ItemId, out var current);
                result[item.ItemId] = current > int.MaxValue - item.Count
                    ? int.MaxValue
                    : current + item.Count;
            }
            return result;
        }

        public void SetFighting() { State = 1; }

        public void SetCleared() { State = 2; }

        internal bool HasPendingStageLoadingRelease(
            DungeonRunIdentity runIdentity,
            int stage)
        {
            if (!runIdentity.IsValid || stage <= 0)
                return false;

            lock (_stageLoadingReleaseSyncRoot)
            {
                return _pendingStageLoadingRun.IsValid
                    && _pendingStageLoadingRun.Equals(runIdentity)
                    && _pendingStageLoadingStage == stage;
            }
        }

        internal bool TryDeferStageLoadingRelease(
            DungeonRunIdentity runIdentity,
            int stage)
        {
            if (!runIdentity.IsValid || stage <= 0 || stage != CurrentStage)
                return false;

            lock (_stageLoadingReleaseSyncRoot)
            {
                if (_pendingStageLoadingRun.IsValid)
                    return false;

                _pendingStageLoadingRun = runIdentity;
                _pendingStageLoadingStage = stage;
                return true;
            }
        }

        internal bool TryConsumeStageLoadingRelease(
            DungeonRunIdentity runIdentity,
            int stage)
        {
            if (!runIdentity.IsValid || stage <= 0)
                return false;

            lock (_stageLoadingReleaseSyncRoot)
            {
                if (!_pendingStageLoadingRun.IsValid
                    || !_pendingStageLoadingRun.Equals(runIdentity)
                    || _pendingStageLoadingStage != stage)
                {
                    return false;
                }

                ClearPendingStageLoadingReleaseLocked();
                return true;
            }
        }

        internal bool TryCancelStageLoadingRelease(
            DungeonRunIdentity runIdentity,
            int stage)
        {
            if (!runIdentity.IsValid || stage <= 0)
                return false;

            lock (_stageLoadingReleaseSyncRoot)
            {
                if (!_pendingStageLoadingRun.IsValid
                    || !_pendingStageLoadingRun.Equals(runIdentity)
                    || _pendingStageLoadingStage != stage)
                {
                    return false;
                }

                ClearPendingStageLoadingReleaseLocked();
                return true;
            }
        }

        // 允许从 state>=1 推进(state==1: 86JP可能不发0x009F(2)直接MOVE_MAP; state==2: 正常流程)
        // state==0(init, 未开始战斗)不允许推进。
        public bool TryAdvanceStage()
        {
            if (State < 1)
                return false;
            if (CurrentStage >= EndStage)
                return false;
            ClearStageState();
            CurrentStage++;
            State = 0;
            return true;
        }

        public bool IsLastStage => CurrentStage >= EndStage;

        private void ClearStageState()
        {
            StageSeed = 0;
            _stageLcg = null;
            _stageItemsByMonster.Clear();
            _groundItems.Clear();
            _deadMonsters.Clear();
        }

        private void ClearPendingStageLoadingReleaseLocked()
        {
            _pendingStageLoadingRun = default;
            _pendingStageLoadingStage = -1;
        }

        private bool TryAddInventoryItem(
            int itemId,
            int count,
            int mainExpandStageKey,
            Func<DeathTowerInventoryEndpoint, bool> isPersistentSlotOccupied,
            out DeathTowerInventoryEndpoint destinationEndpoint,
            out IReadOnlyList<DeathTowerInventoryEndpoint> changedEndpoints)
        {
            destinationEndpoint = default;
            changedEndpoints = Array.Empty<DeathTowerInventoryEndpoint>();
            if (itemId <= 0 || count <= 0)
                return false;

            var metadata = Inventory.ItemMetadataResolver.Resolve(itemId);
            var stackLimit = DeathTowerItemSlotPolicy.ResolveStackLimit(metadata);
            var allocationOrder = DeathTowerItemSlotPolicy.GetAllocationOrder(
                itemId,
                metadata,
                mainExpandStageKey);
            var allocationEndpoints = new HashSet<DeathTowerInventoryEndpoint>(
                allocationOrder);
            var mergeOrder = allocationOrder
                .Where(endpoint => _inventoryItems.ContainsKey(endpoint))
                .Concat(_inventoryItems.Keys
                    .Where(endpoint => !allocationEndpoints.Contains(endpoint)
                        && DeathTowerItemSlotPolicy.IsSlotAllowed(
                            metadata,
                            endpoint))
                    .OrderBy(endpoint => endpoint.ListType)
                    .ThenBy(endpoint => endpoint.SlotIndex))
                .ToArray();
            var remaining = count;
            var additions = new Dictionary<DeathTowerInventoryEndpoint, int>();

            foreach (var endpoint in mergeOrder)
            {
                if (IsPersistentMainSlotOccupied(
                        endpoint,
                        isPersistentSlotOccupied))
                {
                    continue;
                }

                if (!_inventoryItems.TryGetValue(endpoint, out var existing)
                    || existing.ItemId != itemId)
                {
                    continue;
                }

                var endpointStackLimit = existing.StackLimit > 0
                    ? Math.Min(stackLimit, existing.StackLimit)
                    : stackLimit;
                if (existing.Count >= endpointStackLimit)
                    continue;

                var add = Math.Min(
                    remaining,
                    endpointStackLimit - existing.Count);
                if (add <= 0)
                    continue;
                additions[endpoint] = add;
                remaining -= add;
                if (remaining == 0)
                    break;
            }

            if (remaining > 0)
            {
                foreach (var endpoint in allocationOrder)
                {
                    if (IsPersistentMainSlotOccupied(
                            endpoint,
                            isPersistentSlotOccupied))
                    {
                        continue;
                    }

                    if (_inventoryItems.ContainsKey(endpoint)
                        || additions.ContainsKey(endpoint))
                        continue;
                    var add = Math.Min(remaining, stackLimit);
                    additions[endpoint] = add;
                    remaining -= add;
                    if (remaining == 0)
                        break;
                }
            }

            if (remaining > 0)
                return false;

            var changed = new List<DeathTowerInventoryEndpoint>();
            foreach (var entry in additions)
            {
                if (_inventoryItems.TryGetValue(entry.Key, out var existing))
                {
                    existing.Count += entry.Value;
                }
                else
                {
                    _inventoryItems[entry.Key] = CreateInventoryItem(
                        itemId,
                        entry.Value,
                        metadata);
                }
                changed.Add(entry.Key);
            }

            destinationEndpoint = changed[0];
            changedEndpoints = changed;
            return true;
        }

        private static bool IsPersistentMainSlotOccupied(
            DeathTowerInventoryEndpoint endpoint,
            Func<DeathTowerInventoryEndpoint, bool> isPersistentSlotOccupied)
        {
            return isPersistentSlotOccupied != null
                && endpoint.ListType == InventoryListType.Main
                && endpoint.SlotIndex >= InventoryService.MainSlotStart
                && endpoint.SlotIndex <= InventoryService.MainSlotEnd
                && isPersistentSlotOccupied(endpoint);
        }

        private static TowerInventoryItem CreateInventoryItem(
            int itemId,
            int count,
            Inventory.ItemMetadata metadata)
        {
            return new TowerInventoryItem
            {
                ItemId = itemId,
                Count = count,
                StackLimit = DeathTowerItemSlotPolicy.ResolveStackLimit(metadata),
                IsQuickSlotConsumable = DeathTowerItemSlotPolicy.IsQuickSlotConsumable(metadata),
                IsWaste = DeathTowerItemSlotPolicy.IsWaste(metadata),
            };
        }

        private static TowerInventoryMoveResult CreateMoveResult(
            int moveValue32,
            IReadOnlyList<DeathTowerInventoryEndpoint> changedEndpoints)
        {
            return new TowerInventoryMoveResult
            {
                MoveValue32 = moveValue32,
                ChangedSlots = changedEndpoints
                    .Select(endpoint => endpoint.SlotIndex)
                    .ToArray(),
                ChangedEndpoints = changedEndpoints,
            };
        }

        private static DeathTowerInventoryEndpoint ResolveLegacyEndpoint(short slot)
        {
            return new DeathTowerInventoryEndpoint(
                ItemSlotBoundService.IsMainQuickSlot(slot)
                    ? InventoryListType.QuickSlot
                    : InventoryListType.Main,
                slot);
        }
    }
}

