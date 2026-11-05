using System;
using System.IO;
using System.Linq;
using DfoServer.Game.Inventory;
using DfoServer.Game.ItemUpgrade;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers.Inventory;
using PvfLib;

namespace DfoServer.SelfTests
{
    public static class GuildMedalUpgradeSelfTest
    {
        private const int MedalId = 100380044;
        private const int MaterialId = 10154439;
        private const short MaterialSlot = 100;
        private static int _checks, _failures;

        public static int Run()
        {
            _checks = _failures = 0;
            Console.WriteLine("=== GUILD_MEDAL_UPGRADE selftest ===");
            try
            {
                VerifyWire();
                VerifyRules();
                VerifyPersistence();
            }
            catch (Exception ex) { Check(false, ex.ToString()); }
            Console.WriteLine($"GUILD_MEDAL_UPGRADE checks={_checks} failures={_failures}");
            return _failures == 0 ? 0 : 1;
        }

        private static void VerifyWire()
        {
            var body = new byte[16];
            body[1] = (byte)InventoryListType.GuildMedal;
            BitConverter.GetBytes(MedalId).CopyTo(body, 4);
            BitConverter.GetBytes(MaterialSlot).CopyTo(body, 8);
            BitConverter.GetBytes((short)-1).CopyTo(body, 10);
            Check(ItemUpgradeRequest.TryParse(body, out var parsed)
                && parsed.Method == ItemUpgradeMethod.Reinforce
                && parsed.ToCommand().TargetListType == InventoryListType.GuildMedal
                && parsed.TargetSlotIndex == 0 && parsed.TargetItemTemplateId == MedalId,
                "native 00-26 medal request uses separate method/list bytes");
            body[1] = 0;
            Check(ItemUpgradeRequest.TryParse(body, out parsed)
                && parsed.TargetListType == InventoryListType.Main, "ordinary upgrade wire preserved");
            body[1] = (byte)InventoryListType.PersonalCargo;
            Check(!ItemUpgradeRequest.TryParse(body, out _), "cargo cannot be upgraded remotely");
            body[1] = 38;
            BitConverter.GetBytes(int.MaxValue).CopyTo(body, 12);
            Check(!ItemUpgradeRequest.TryParse(body, out _), "overflowing name length rejected");

            var ack = ItemUpgradeAckBuilder.BuildSuccess(new ItemUpgradeResult
            {
                TargetListType = InventoryListType.GuildMedal, TargetSlotIndex = 4,
                MaterialSlotIndex = MaterialSlot, MaterialRemainingStackCount = 90,
                OldLevel = 0, NewLevel = 1,
            });
            Check(ack.Length == 19 && ack[14] == 38 && BitConverter.ToInt16(ack, 15) == 4,
                "native ACK targets medal list at byte 14");

            var ordinaryAck = ItemUpgradeAckBuilder.BuildSuccess(new ItemUpgradeResult
            {
                TargetSlotIndex = 3, MaterialSlotIndex = 5, MaterialRemainingStackCount = 42,
                OldLevel = 7, NewLevel = 8,
            });
            Check(ordinaryAck.Length == 19 && ordinaryAck[14] == 0 && BitConverter.ToInt16(ordinaryAck, 15) == 3,
                "ordinary ACK still targets Main at byte 14");
        }

        private static InventoryService Stock(byte level = 0)
        {
            var inventory = new InventoryService(99151, 99151);
            Fill(inventory, level);
            return inventory;
        }

        private static void Fill(InventoryService inventory, byte level)
        {
            var medal = new ItemCore { ItemKind = ItemCore.KindGuildMedal, ItemId = MedalId,
                Value = 12345, Upgrade = level };
            medal.SetGuardianGemItemId(0, 90032);
            medal.SetGuardianGemItemId(3, 90042);
            inventory.SetItem(InventoryListType.GuildMedal, 0, medal);
            inventory.SetItem(InventoryListType.Main, MaterialSlot,
                new ItemCore { ItemKind = ItemCore.KindMaterial, ItemId = MaterialId, Count = 10000 });
            var metadata = ItemMetadataResolver.Resolve(31044);
            inventory.SetItem(InventoryListType.Main, 10, new ItemCore
            {
                ItemKind = ItemCore.KindEquipment, ItemId = 31044,
                Durability = (ushort)metadata.Durability, Upgrade = 7,
            });
            inventory.SetMainVirtualCount(0, 100000000);
        }

        private static ItemUpgradeCommand Command() => new ItemUpgradeCommand
        {
            TargetListType = InventoryListType.GuildMedal, TargetSlotIndex = 0,
            TargetItemTemplateId = MedalId, MaterialSlotIndex = MaterialSlot,
        };

        private static void VerifyRules()
        {
            var file = ItemUpgradeTableProvider.GetFile(ItemUpgradeTableKind.Normal);
            var table = file.GetTable("flag_upgrade_table");
            Check(table != null && table.Rows.Count == 20, "actual PVF medal table has 20 levels");
            Check(file.CostWeightsByGuildFlagRarity.Count == 7
                && file.CostWeightsByGuildFlagRarity[3] == 10.5,
                "actual PVF medal rarity cost parsed");
            var inventory = Stock();
            var before = inventory.GetItem(InventoryListType.GuildMedal, 0).Copy();
            Check(InventoryItemUpgradeService.TryUpgradeItem(inventory, Command(), out var result)
                && result.UpgradeSucceeded && result.NewLevel == 1 && result.FinalSuccessWeight == 100000
                && result.MaterialItemTemplateId == MaterialId && result.MaterialRemainingStackCount == 9990,
                "first medal upgrade consumes 10 contributions from native rarity multiplier");
            Check(result != null && !result.MainRefreshSlots.Contains(0)
                && result.MainRefreshSlots.Contains(MaterialSlot), "medal slot is not refreshed in Main");
            before.Upgrade = 1;
            Check(before.ToBytes().SequenceEqual(inventory.GetItem(InventoryListType.GuildMedal, 0).ToBytes()),
                "upgrade preserves gem sockets and every other ItemCore byte");
            Check(inventory.GetItem(InventoryListType.Main, 10).Upgrade == 7,
                "ordinary equipment stays unchanged");
            var collision = Stock(); var collisionCommand = Command(); collisionCommand.TargetSlotIndex=10;
            collision.SetItem(InventoryListType.GuildMedal,10,collision.GetItem(InventoryListType.GuildMedal,0).Copy());
            Check(InventoryItemUpgradeService.TryUpgradeItem(collision,collisionCommand,out _)
                && collision.GetItem(InventoryListType.GuildMedal,10).Upgrade==1
                && collision.GetItem(InventoryListType.Main,10).Upgrade==7,
                "same-number Main equipment and medal slots remain isolated");
            foreach (var change in new Action<InventoryService, ItemUpgradeCommand>[]
            {
                (i,c) => c.TargetItemTemplateId++,
                (i,c) => c.TargetListType = InventoryListType.Main,
                (i,c) => c.TargetListType = InventoryListType.Equipment,
                (i,c) => c.TargetSlotIndex = 49,
                (i,c) => c.Method = ItemUpgradeMethod.AdvancedReinforce,
                (i,c) => { c.Method = ItemUpgradeMethod.Amplify; c.Mode = ItemUpgradeMode.Amplify; },
                (i,c) => c.Method = ItemUpgradeMethod.LuckyEnchantDeed,
                (i,c) => c.OptionalTicketSlotIndex = 30,
                (i,c) => i.GetItem(InventoryListType.Main, MaterialSlot).Count = 9,
                (i,c) => i.SetMainVirtualCount(0, 0),
                (i,c) => i.GetItem(InventoryListType.GuildMedal, 0).Upgrade = 20,
                (i,c) => i.GetItem(InventoryListType.GuildMedal, 0).ItemKind = ItemCore.KindGuardianGem,
            })
            {
                var i = Stock(); var c = Command(); change(i,c);
                var gold = i.GetMainVirtualCount(0).Count;
                var material = i.GetItem(InventoryListType.Main, MaterialSlot).Count;
                var target = i.GetItem(InventoryListType.GuildMedal, 0).ToBytes();
                Check(!InventoryItemUpgradeService.TryUpgradeItem(i,c,out _)
                    && gold == i.GetMainVirtualCount(0).Count
                    && material == i.GetItem(InventoryListType.Main, MaterialSlot).Count
                    && target.SequenceEqual(i.GetItem(InventoryListType.GuildMedal, 0).ToBytes()),
                    "invalid medal request rejects without consuming assets");
            }

            foreach (var level in new byte[] { 1, 4, 7, 10, 19 })
            {
                var row = table.GetRowByTargetLevel(level + 1);
                var originalFailureWeight = row.Values[7];
                try
                {
                    row.Values[7] = 100000; // Deterministic failure; restore shared fixture immediately.
                    var i = Stock(level);
                    Check(InventoryItemUpgradeService.TryUpgradeItem(i, Command(), out result)
                        && !result.UpgradeSucceeded && result.FinalSuccessWeight == 0,
                        $"actual medal failure path +{level}");
                    if (row.PenaltyType == 3)
                    {
                        Check(i.GetItem(InventoryListType.GuildMedal, 0) == null
                            && result.ResultCode == 3 && result.DestroyRewardItems.All(r=>r.ItemTemplateId==MaterialId),
                            "medal destruction uses contribution refund, never ordinary equipment debris");
                        var cost = (int)(row.MaterialCount * (double)(float)file.CostWeightsByGuildFlagRarity[3]);
                        var contributionTotal = i.GetItems(InventoryListType.Main)
                            .Where(p=>p.Value.ItemId==MaterialId).Sum(p=>p.Value.Count);
                        Check(contributionTotal == 10000-cost+file.PayBackByGuildFlagRarity[3],
                            "destruction refund cannot restore the consumed materials");
                        Check(result.MaterialRemainingStackCount == i.GetItem(InventoryListType.Main,MaterialSlot)?.Count,
                            "ACK material balance agrees with final inventory after refund");
                    }
                    else
                    {
                        var expected = row.PenaltyType == 2 ? Math.Max(0,level-row.PenaltyValue) : level;
                        Check(i.GetItem(InventoryListType.GuildMedal, 0).Upgrade == expected,
                            "medal failure follows flag table penalty");
                    }
                }
                finally { row.Values[7] = originalFailureWeight; }
            }

            // 材料恰好等于本次费用：扣费清空原栈后返还仍须成功（规划基于扣费后库存）。
            var destroyRow = default(UpgradeTableRow);
            var destroyLevel = 0;
            foreach (var level in new byte[] { 10, 19, 1, 4, 7 })
            {
                var candidate = table.GetRowByTargetLevel(level + 1);
                if (candidate != null && candidate.PenaltyType == 3)
                {
                    destroyRow = candidate;
                    destroyLevel = level;
                    break;
                }
            }
            var exactOriginalWeight = destroyRow.Values[7];
            try
            {
                destroyRow.Values[7] = 100000;
                var cost = (int)(destroyRow.MaterialCount * (double)(float)file.CostWeightsByGuildFlagRarity[3]);
                var i = Stock((byte)destroyLevel);
                i.SetItem(InventoryListType.Main, MaterialSlot,
                    new ItemCore { ItemKind = ItemCore.KindMaterial, ItemId = MaterialId, Count = cost });
                Check(InventoryItemUpgradeService.TryUpgradeItem(i, Command(), out result)
                    && !result.UpgradeSucceeded && result.ResultCode == 3
                    && i.GetItem(InventoryListType.GuildMedal, 0) == null
                    && i.GetItem(InventoryListType.Main, MaterialSlot) == null
                    && result.MaterialRemainingStackCount == 0,
                    "material stack emptied by cost still destroys and refunds");
                var refundTotal = i.GetItems(InventoryListType.Main)
                    .Where(p => p.Value.ItemId == MaterialId).Sum(p => p.Value.Count);
                var refundSlot = result.DestroyRewardItems.FirstOrDefault(r => r.ItemTemplateId == MaterialId);
                Check(refundTotal == file.PayBackByGuildFlagRarity[3] && refundSlot != null
                    && result.MainRefreshSlots.Contains(refundSlot.SlotIndex),
                    "refund is planned against post-consumption inventory and refreshed");
            }
            finally { destroyRow.Values[7] = exactOriginalWeight; }
        }

        private static void VerifyPersistence()
        {
            using var db = SelfTestDatabase.Create("guild-medal-upgrade");
            SeedOwner(db.Database, 99151, 99151, "medal-test");
            var sid = Guid.NewGuid();
            var lease = LoadAndRegister(db.Database, sid, 99151, 99151);
            try
            {
                lock(lease.SyncRoot) Fill(lease.Inventory,0);
                Check(InventoryPersistenceService.SaveDirty(lease), "seed persisted via inventory owner");
                Check(InventoryItemUpgradeCommitService.TryCommit(lease,Command(),out var result,out var failed)
                    && !failed && result.NewLevel == 1, "medal upgrade commits through existing transaction");
                using(var connection = db.Database.OpenConnection())
                {
                    var reloaded = InventoryService.LoadFromDb(connection,99151,99151,db.Database);
                    Check(reloaded.GetItem(InventoryListType.GuildMedal,0).ToBytes().SequenceEqual(
                            lease.Inventory.GetItem(InventoryListType.GuildMedal,0).ToBytes())
                        && reloaded.GetItem(InventoryListType.Main,MaterialSlot).Count == 9990,
                        "relogin keeps upgrade, gems and consumed materials");
                }

                var file = ItemUpgradeTableProvider.GetFile(ItemUpgradeTableKind.Normal);
                var table = file.GetTable("flag_upgrade_table");
                var destroyRow = default(UpgradeTableRow);
                var destroyLevel = 0;
                foreach (var level in new byte[] { 10, 19, 1, 4, 7 })
                {
                    var candidate = table.GetRowByTargetLevel(level + 1);
                    if (candidate != null && candidate.PenaltyType == 3)
                    {
                        destroyRow = candidate;
                        destroyLevel = level;
                        break;
                    }
                }
                var destroyOriginalWeight = destroyRow.Values[7];
                try
                {
                    destroyRow.Values[7] = 100000;
                    lock(lease.SyncRoot) Fill(lease.Inventory,(byte)destroyLevel);
                    var exactCost = (int)(destroyRow.MaterialCount * (double)(float)file.CostWeightsByGuildFlagRarity[3]);
                    lock(lease.SyncRoot) lease.Inventory.SetItem(InventoryListType.Main, MaterialSlot,
                        new ItemCore { ItemKind = ItemCore.KindMaterial, ItemId = MaterialId, Count = exactCost });
                    Check(InventoryPersistenceService.SaveDirty(lease), "destruction fixture saved");
                    Check(InventoryItemUpgradeCommitService.TryCommit(lease,Command(),out result,out failed)
                        && !failed && result.ResultCode == 3,
                        "exact-cost medal destruction commits through existing transaction");
                    using(var connection = db.Database.OpenConnection())
                    {
                        var reloaded = InventoryService.LoadFromDb(connection,99151,99151,db.Database);
                        var contributionTotal = reloaded.GetItems(InventoryListType.Main)
                            .Where(p=>p.Value.ItemId==MaterialId).Sum(p=>p.Value.Count);
                        Check(reloaded.GetItem(InventoryListType.GuildMedal,0)==null
                            && contributionTotal == file.PayBackByGuildFlagRarity[3],
                            "relogin keeps exact-cost destruction and post-consumption refund");
                    }
                }
                finally { destroyRow.Values[7] = destroyOriginalWeight; }
                lock(lease.SyncRoot) Fill(lease.Inventory,0);
                Check(InventoryPersistenceService.SaveDirty(lease), "rollback fixture saved");
                void Ddl(string sql) => db.Database.Write((c, t) =>
                {
                    using var command = c.CreateCommand(); command.Transaction = t;
                    command.CommandText = sql; command.ExecuteNonQuery();
                });
                Ddl("CREATE TRIGGER medal_save_fail BEFORE UPDATE ON character_inventory_items BEGIN SELECT RAISE(ABORT,'medal rollback test'); END");
                try
                {
                    Check(!InventoryItemUpgradeCommitService.TryCommit(lease,Command(),out _,out failed)
                        && failed && lease.Inventory.GetItem(InventoryListType.GuildMedal,0).Upgrade==0
                        && lease.Inventory.GetItem(InventoryListType.Main,MaterialSlot).Count==10000
                        && lease.Inventory.GetMainVirtualCount(0).Count==100000000,
                        "failed save rolls back medal, contributions and gold together");
                }
                finally { Ddl("DROP TRIGGER medal_save_fail"); }
                InventoryContext.Unregister(sid,99151);
                Check(!InventoryItemUpgradeCommitService.TryCommit(lease,Command(),out _,out _),
                    "expired inventory lease cannot upgrade medals");
            }
            finally { InventoryContext.Unregister(sid, 99151); }
        }

        private static void SeedOwner(GameDatabase database, int accountId, int characterId, string name)
        {
            database.Write((c, t) =>
            {
                using var command = c.CreateCommand(); command.Transaction = t;
                command.CommandText = "INSERT INTO accounts (account_id, m_id, password_hash) VALUES (@aid, @name, '');"
                    + "INSERT INTO characters (character_id, account_id, name) VALUES (@cid, @aid, @name);";
                command.Parameters.AddWithValue("@aid", accountId); command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@name", name); command.ExecuteNonQuery();
            });
        }

        private static InventoryLease LoadAndRegister(GameDatabase database, Guid sessionId, int accountId, int characterId)
        {
            using var connection = database.OpenConnection();
            return InventoryContext.Register(sessionId, InventoryService.LoadFromDb(connection, characterId, accountId, database));
        }

        // master 基线是 SQLite：临时文件库，Dispose 时连同 WAL/SHM 一起清理。
        private sealed class SelfTestDatabase : IDisposable
        {
            private readonly string _path;
            private SelfTestDatabase(string path)
            { _path = path; Database = new GameDatabase(path, ServerPaths.SchemaFilePath); }
            internal GameDatabase Database { get; }
            internal Microsoft.Data.Sqlite.SqliteConnection OpenConnection() => Database.OpenConnection();
            internal static SelfTestDatabase Create(string hint) => new(
                Path.Combine(Path.GetTempPath(), $"dfo_st_{hint}_{Guid.NewGuid():N}.db"));
            public void Dispose()
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                foreach (var path in new[] { _path, _path + "-wal", _path + "-shm" })
                    if (File.Exists(path)) File.Delete(path);
            }
        }

        private static void Check(bool passed,string name)
        {
            _checks++; if(!passed) _failures++;
            Console.WriteLine($"[{(passed?"PASS":"FAIL")}] {name}");
        }
    }
}
