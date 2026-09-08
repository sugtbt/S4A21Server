using DfoServer.Game.DeathTower;
using DfoServer.Game.Inventory;
using DfoServer.GameWorld;
using System;
using System.Collections.Generic;
using System.Linq;
using DungeonData = DfoServer.GameWorld.Dungeon;

namespace DfoServer.Game.Dungeon
{
    internal sealed class DungeonEntryAdmissionApplicationService
    {
        internal const int GorgeousChallengeGoldCost = 190000;

        private readonly DungeonEntryCostService _costs;

        internal DungeonEntryAdmissionApplicationService(
            DungeonEntryCostService costs)
        {
            _costs = costs
                ?? throw new ArgumentNullException(nameof(costs));
        }

        internal bool CheckHellQuestRequirement(
            int characterId,
            WorldMapArea area,
            out int missingQuestId)
            => _costs.CheckHellQuestRequirement(
                characterId,
                area,
                out missingQuestId);

        internal bool TryPrepareTower(
            InventoryLease lease,
            DeathTowerSession tower,
            out DungeonEntryAdmissionPreparation preparation,
            out EntryCostResult validation)
        {
            preparation = null;
            var config = tower?.Config;
            if (config == null)
            {
                validation = new EntryCostResult().Fail(
                    "death tower definition is missing",
                    EntryCostFailureKind.Unavailable);
                return false;
            }

            var plan = new DungeonEntryCostPlan("death-tower");
            var alternatives =
                new List<DungeonEntryCostAlternative>();
            AddTowerAlternative(alternatives, config.RequiredEntryItems);
            AddTowerAlternative(
                alternatives,
                config.AddedRequiredEntryItems);
            if (alternatives.Count > 0)
                plan.AddAlternativeGroup(alternatives);

            validation = _costs.TryValidatePlan(lease, plan);
            if (!validation.Success)
                return false;

            preparation = new DungeonEntryAdmissionPreparation(plan);
            return true;
        }

        internal bool TryPrepareRun(
            InventoryLease lease,
            DungeonRun run,
            TournamentDungeonDefinition tournamentDefinition,
            bool manualHellParty,
            byte requestedHellDifficulty,
            PvfLib.MazeInfo maze,
            int mazeIndex,
            bool gorgeousChallengeEnabled,
            out DungeonEntryAdmissionPreparation preparation,
            out EntryCostResult validation)
        {
            preparation = null;
            if (run == null)
            {
                validation = new EntryCostResult().Fail(
                    "dungeon run is missing",
                    EntryCostFailureKind.InvalidState);
                return false;
            }
            if (lease?.Inventory == null
                || lease.CharacterId != lease.Inventory.CharacterId)
            {
                validation = new EntryCostResult().Fail(
                    "owned inventory lease is missing",
                    EntryCostFailureKind.InvalidState);
                return false;
            }

            var plan = new DungeonEntryCostPlan("dungeon-entry");
            if (!TryAddDungeonRequiredItems(
                    plan,
                    run.DungeonId,
                    tournamentDefinition,
                    out validation))
            {
                return false;
            }

            var candidate = new DungeonEntryAdmissionPreparation(plan);
            if (manualHellParty
                && !TryPrepareHellParty(
                    lease,
                    run.DungeonId,
                    requestedHellDifficulty,
                    maze,
                    mazeIndex,
                    gorgeousChallengeEnabled,
                    candidate,
                    out validation))
            {
                return false;
            }

            validation = _costs.TryValidatePlan(lease, plan);
            if (!validation.Success)
                return false;

            preparation = candidate;
            return true;
        }

        internal EntryCostResult TryCommit(
            InventoryLease lease,
            DungeonEntryAdmissionPreparation preparation)
        {
            if (preparation?.CostPlan == null)
            {
                return new EntryCostResult().Fail(
                    "dungeon entry preparation is missing",
                    EntryCostFailureKind.InvalidState);
            }

            return _costs.TryCommitPlan(lease, preparation.CostPlan);
        }

        internal bool TryCommitGroup(
            IReadOnlyList<DungeonEntryAdmissionCommitRequest> source,
            out IReadOnlyList<EntryCostResult> results)
        {
            results = Array.Empty<EntryCostResult>();
            if (source == null || source.Count == 0)
                return false;
            var requests = new DungeonEntryCostCommitRequest[source.Count];
            for (var index = 0; index < source.Count; index++)
            {
                var entry = source[index];
                if (entry.Lease == null || entry.Preparation?.CostPlan == null)
                    return false;
                requests[index] = new DungeonEntryCostCommitRequest(
                    entry.Lease,
                    entry.Preparation.CostPlan);
            }
            return _costs.TryCommitPlans(requests, out results);
        }

        private static bool TryAddDungeonRequiredItems(
            DungeonEntryCostPlan plan,
            int dungeonId,
            TournamentDungeonDefinition tournamentDefinition,
            out EntryCostResult failure)
        {
            failure = null;
            if (tournamentDefinition != null)
            {
                foreach (var entry in tournamentDefinition.EntryItems)
                {
                    plan.AddRequiredItems(new[]
                    {
                        new DungeonEntryItemRequirement(
                            entry.ItemId,
                            entry.Count,
                            entry.ConsumeOnEntry),
                    });
                }
                return true;
            }

            try
            {
                var dungeon = DungeonData.GetDungeonFile(dungeonId);
                if (dungeon == null)
                {
                    failure = new EntryCostResult().Fail(
                        "dungeon required item definition is missing",
                        EntryCostFailureKind.Unavailable);
                    return false;
                }

                if (dungeon.RequiredItems != null)
                {
                    plan.AddRequiredItems(
                        DungeonEntryCostService.ProjectPvfRequiredItems(
                            dungeon.RequiredItems));
                }
                return true;
            }
            catch (Exception ex)
            {
                failure = new EntryCostResult().Fail(
                    "dungeon required item definition failed: " +
                    ex.Message,
                    EntryCostFailureKind.Unavailable);
                return false;
            }
        }

        private bool TryPrepareHellParty(
            InventoryLease lease,
            int dungeonId,
            byte requestedHellDifficulty,
            PvfLib.MazeInfo maze,
            int mazeIndex,
            bool gorgeousChallengeEnabled,
            DungeonEntryAdmissionPreparation preparation,
            out EntryCostResult failure)
        {
            failure = null;
            if (maze == null)
            {
                failure = new EntryCostResult().Fail(
                    "hell party maze is missing",
                    EntryCostFailureKind.Unavailable);
                return false;
            }

            var area = WorldMap.GetAreaByDungeonId(dungeonId);
            if (area?.HellDungeon != true)
            {
                failure = new EntryCostResult().Fail(
                    "worldmap area is not a hell dungeon area",
                    EntryCostFailureKind.Unavailable);
                return false;
            }
            if (!_costs.CheckHellQuestRequirement(
                    lease.CharacterId,
                    area,
                    out var missingQuestId))
            {
                failure = new EntryCostResult().Fail(
                    $"hell quest not cleared quest={missingQuestId}",
                    EntryCostFailureKind.MissingPermission);
                return false;
            }

            var hellMode = ResolveHellPartyMode(requestedHellDifficulty);
            DungeonData.HellPartyRoomInfo hellRoom = null;
            var gorgeousPlanned = false;
            if (gorgeousChallengeEnabled)
            {
                var veryHardRoom = DungeonData.FindHellMapRoom(
                    dungeonId,
                    maze,
                    mazeIndex,
                    difficulty: 1,
                    preferSeasonSealDoor: true);
                int gold;
                lock (lease.SyncRoot)
                    gold = lease.Inventory.CountMainItem(0);
                if (veryHardRoom.Found
                    && gold >= GorgeousChallengeGoldCost)
                {
                    hellMode = 1;
                    hellRoom = veryHardRoom;
                    gorgeousPlanned = true;
                    if (!preparation.CostPlan.TryAddGoldCost(
                            GorgeousChallengeGoldCost))
                    {
                        failure = new EntryCostResult().Fail(
                            "gorgeous challenge gold cost overflow",
                            EntryCostFailureKind.Unavailable);
                        return false;
                    }
                }
                else
                {
                    hellMode = HellPartyData.ResolveManualHellPartyMode();
                }
            }

            if (hellRoom == null || !hellRoom.Found)
            {
                hellRoom = DungeonData.FindHellMapRoom(
                    dungeonId,
                    maze,
                    mazeIndex,
                    hellMode,
                    preferSeasonSealDoor: gorgeousPlanned);
            }
            if (hellRoom == null || !hellRoom.Found)
            {
                failure = new EntryCostResult().Fail(
                    "hell party room is missing",
                    EntryCostFailureKind.Unavailable);
                return false;
            }

            if (!TryAddHellTicketAlternatives(
                    preparation.CostPlan,
                    area,
                    DungeonData.GetDungeonMinimumRequiredLevel(dungeonId),
                    out failure))
            {
                return false;
            }

            preparation.HellParty = new HellPartyEntryPreparation
            {
                Area = area,
                DungeonMinimumLevel =
                    DungeonData.GetDungeonMinimumRequiredLevel(dungeonId),
                Mode = hellMode,
                Room = hellRoom,
                GorgeousChallenge = gorgeousPlanned,
            };
            return true;
        }

        private static bool TryAddHellTicketAlternatives(
            DungeonEntryCostPlan plan,
            WorldMapArea area,
            int dungeonMinimumLevel,
            out EntryCostResult failure)
        {
            failure = null;
            var alternatives =
                new List<DungeonEntryCostAlternative>();
            foreach (var ticket in area.HellFreePassItems)
            {
                if (ticket.ItemId <= 0 || ticket.Count <= 0)
                    continue;
                alternatives.Add(new DungeonEntryCostAlternative(
                    new[]
                    {
                        new DungeonEntryItemRequirement(
                            ticket.ItemId,
                            ticket.Count,
                            consumeOnEntry: true),
                    },
                    isFreePass: true));
            }

            var normalNeed = WorldMap.GetHellNormalTicketNeedCount(
                dungeonMinimumLevel);
            if (normalNeed <= 0)
            {
                failure = new EntryCostResult().Fail(
                    $"invalid hell ticket count minLevel=" +
                    dungeonMinimumLevel,
                    EntryCostFailureKind.Unavailable);
                return false;
            }

            var missingAlternativeIndex = alternatives.Count;
            foreach (var itemId in area.HellNormalTicketItemIds
                         .Where(itemId => itemId > 0))
            {
                alternatives.Add(new DungeonEntryCostAlternative(
                    new[]
                    {
                        new DungeonEntryItemRequirement(
                            itemId,
                            normalNeed,
                            consumeOnEntry: true),
                    }));
            }
            if (missingAlternativeIndex >= alternatives.Count)
            {
                failure = new EntryCostResult().Fail(
                    "normal hell ticket definition is missing",
                    EntryCostFailureKind.Unavailable);
                return false;
            }

            plan.AddAlternativeGroup(
                alternatives,
                missingAlternativeIndex);
            return true;
        }

        private static void AddTowerAlternative(
            ICollection<DungeonEntryCostAlternative> alternatives,
            IReadOnlyList<DeathTowerData.TowerEntryItem> source)
        {
            if (source == null || source.Count == 0)
                return;
            alternatives.Add(new DungeonEntryCostAlternative(
                source.Select(item => new DungeonEntryItemRequirement(
                        item.ItemId,
                        item.Count,
                        item.ConsumeOnEntry))
                    .ToList()));
        }

        private static byte ResolveHellPartyMode(byte requestFlag)
        {
            return requestFlag == 1 || requestFlag == 2
                ? requestFlag
                : HellPartyData.ResolveManualHellPartyMode();
        }
    }

    internal sealed class DungeonEntryAdmissionPreparation
    {
        internal DungeonEntryAdmissionPreparation(
            DungeonEntryCostPlan costPlan)
        {
            CostPlan = costPlan;
        }

        internal DungeonEntryCostPlan CostPlan { get; }
        internal HellPartyEntryPreparation HellParty { get; set; }

        internal void ApplyTo(DungeonRun run)
        {
            if (run == null || HellParty == null)
                return;
            run.HellMode = true;
            run.HellPartyMode = HellParty.Mode;
            run.VeryDifficultHell = HellParty.Mode == 1;
            run.HellGorgeousChallenge = HellParty.GorgeousChallenge;
            run.HellMapId = HellParty.Room.MapId;
            run.HellMapX = (byte)HellParty.Room.X;
            run.HellMapY = (byte)HellParty.Room.Y;
            run.HellRoomInfo = HellParty.Room;
        }
    }

    internal readonly struct DungeonEntryAdmissionCommitRequest
    {
        internal DungeonEntryAdmissionCommitRequest(
            InventoryLease lease,
            DungeonEntryAdmissionPreparation preparation)
        {
            Lease = lease;
            Preparation = preparation;
        }

        internal InventoryLease Lease { get; }
        internal DungeonEntryAdmissionPreparation Preparation { get; }
    }

    internal sealed class HellPartyEntryPreparation
    {
        internal WorldMapArea Area;
        internal int DungeonMinimumLevel;
        internal byte Mode;
        internal DungeonData.HellPartyRoomInfo Room;
        internal bool GorgeousChallenge;
    }
}
