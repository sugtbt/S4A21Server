using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using DfoServer.Game.Dungeon;
using DfoServer.Game.Inventory;
using DfoServer.Game.KnightShield;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;

namespace DfoServer.Game.CharacterData
{
    public sealed class SqliteSubtype1Repository
    {
        private readonly string _connectionString;
        private readonly KnightShieldDeckRepository _knightShieldDeckRepository;
        private readonly DungeonEntryLimitService _entryLimitService;

        public SqliteSubtype1Repository(string databasePath, string schemaFilePath)
            : this(new GameDatabase(databasePath, schemaFilePath))
        {
        }

        public SqliteSubtype1Repository(IGameDatabase database)
        {
            if (database == null)
                throw new ArgumentNullException(nameof(database));

            _connectionString = database.ConnectionString;
            _knightShieldDeckRepository = KnightShieldDeckRepository.FromConnectionString(_connectionString);
            _entryLimitService = new DungeonEntryLimitService(_connectionString);
        }

        private SqliteSubtype1Repository(string connectionString)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
            _knightShieldDeckRepository = KnightShieldDeckRepository.FromConnectionString(_connectionString);
            _entryLimitService = new DungeonEntryLimitService(_connectionString);
        }

        public static SqliteSubtype1Repository FromConnectionString(string connectionString)
        {
            return new SqliteSubtype1Repository(connectionString);
        }

        public bool HasData(int characterId)
        {
            using (var conn = Open())
            using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM character_subtype1_fields WHERE character_id=@cid", conn))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
        }

        public UserInfoAdditionSnapshot Load(int characterId)
        {
            return Load(characterId, null);
        }

        internal UserInfoAdditionSnapshot Load(
            int characterId,
            KnightShieldDeckSnapshot knightShieldDeck)
        {
            var snap = new UserInfoAdditionSnapshot();
            byte characterJob = 0;
            int characterGrowType = 0;
            int characterAccountId = 0;

            using (var conn = Open())
            {
                
                using (var cmd = new SqliteCommand(@"SELECT
                    stat_hp_max, stat_mp_max, stat_physical_attack, stat_physical_defense,
                    stat_magical_attack, stat_magical_defense, stat_fire_resistance, stat_water_resistance,
                    stat_dark_resistance, stat_light_resistance, stat_inventory_limit,
                    stat_hp_regen_speed, stat_mp_regen_speed, stat_move_speed, stat_attack_speed,
                    stat_cast_speed, stat_hit_recovery, stat_jump_power, stat_weight, stat_level,
                    name_tag_item_id, name_tag_expire_time, skill_tree_index, equipped_creature_level, equip_list_trailing,
                    manage_level, flag_byte, guild_power_war, server_timestamp, quest_shop_count,
                    progress1, progress2
                FROM character_subtype1_fields WHERE character_id=@cid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (!r.Read()) return null;
                        snap.StatHpMax = (uint)r.GetInt64(0);
                        snap.StatMpMax = (uint)r.GetInt64(1);
                        snap.StatPhysicalAttack = (short)r.GetInt32(2);
                        snap.StatPhysicalDefense = (short)r.GetInt32(3);
                        snap.StatMagicalAttack = (short)r.GetInt32(4);
                        snap.StatMagicalDefense = (short)r.GetInt32(5);
                        snap.StatFireResistance = (short)r.GetInt32(6);
                        snap.StatWaterResistance = (short)r.GetInt32(7);
                        snap.StatDarkResistance = (short)r.GetInt32(8);
                        snap.StatLightResistance = (short)r.GetInt32(9);
                        snap.StatInventoryLimit = (uint)r.GetInt64(10);
                        snap.StatHpRegenSpeed = (ushort)r.GetInt32(11);
                        snap.StatMpRegenSpeed = (ushort)r.GetInt32(12);
                        snap.StatMoveSpeed = (uint)r.GetInt64(13);
                        snap.StatAttackSpeed = (ushort)r.GetInt32(14);
                        snap.StatCastSpeed = (ushort)r.GetInt32(15);
                        snap.StatHitRecovery = (ushort)r.GetInt32(16);
                        snap.StatJumpPower = (ushort)r.GetInt32(17);
                        snap.StatWeight = (uint)r.GetInt64(18);
                        snap.StatLevel = (byte)r.GetInt32(19);
                        snap.NameTagItemId = (uint)r.GetInt64(20);
                        snap.NameTagExpireTime = (uint)r.GetInt64(21);
                        snap.SkillTreeIndex = NormalizeSkillTreeIndex(r.GetInt32(22));
                        snap.EquippedCreatureLevel = (byte)r.GetInt32(23);
                        snap.ManageLevel = (byte)r.GetInt32(25);
                        snap.FlagByte = (byte)r.GetInt32(26);
                        snap.GuildPowerWar = (uint)r.GetInt64(27);
                        snap.ServerTimestamp = (uint)r.GetInt64(28);
                        snap.QuestShopCount = (ushort)r.GetInt32(29);
                        snap.Progress1 = (uint)r.GetInt64(30);
                        snap.Progress2 = (uint)r.GetInt64(31);
                    }
                }

                
                using (var cmd = new SqliteCommand("SELECT exp, ex_equip_slot_stat, clone_title_item_id, job, grow_type, aura_skin_flag, account_id FROM characters WHERE character_id=@cid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (r.Read())
                        {
                            snap.CharacExp = (uint)r.GetInt64(0);
                            snap.ExEquipSlotStat = (byte)r.GetInt32(1);
                            snap.CloneTitleItemId = r.IsDBNull(2) ? 0u : (uint)r.GetInt64(2);
                            characterJob = (byte)r.GetInt32(3);
                            characterGrowType = r.GetInt32(4);
                            snap.AuraSkinFlag = r.FieldCount > 5 && !r.IsDBNull(5) ? (byte)r.GetInt32(5) : (byte)0;
                            characterAccountId = r.FieldCount > 6 && !r.IsDBNull(6) ? r.GetInt32(6) : 0;
                        }
                    }
                }

                
                
                var projectionBuilder = new Noti2InventoryProjectionBuilder();
                if (InventoryContext.TryGetLease(characterId, out var lease))
                {
                    lock (lease.SyncRoot)
                    {
                        var equippedProjection = projectionBuilder.BuildUserInfoAddition(lease.Inventory);
                        snap.AuraSkinFlag = equippedProjection.AuraSkinFlag;
                        snap.NameTagItemId = equippedProjection.NameTagItemId;
                        snap.NameTagExpireTime = equippedProjection.NameTagExpireTime;
                        snap.EquippedEntries.AddRange(equippedProjection.EquippedEntries);
                        foreach (var pair in equippedProjection.AvatarDetails)
                            snap.AvatarDetails[pair.Key] = pair.Value;
                        foreach (var pair in equippedProjection.CreatureDetails)
                            snap.CreatureDetails[pair.Key] = pair.Value;
                    }
                }
                else
                {
                    ApplyNameTagState(NameTagStateRepository.Load(conn, characterId), snap);
                }

                if (snap.EquippedEntries.Count == 0)
                {
                    var equippedItems = InventoryItemRepository.LoadEquippedItems(conn, characterId);
                    var avatarDetails = AvatarDetailRepository.LoadForCharacter(conn, characterId);
                    var creatureDetails = CreatureDetailRepository.LoadForCharacter(conn, characterId);
                    var fromDb = projectionBuilder.BuildUserInfoAddition(
                        equippedItems,
                        avatarDetails,
                        creatureDetails);
                    snap.EquippedEntries.AddRange(fromDb.EquippedEntries);
                    foreach (var pair in fromDb.AvatarDetails)
                        snap.AvatarDetails[pair.Key] = pair.Value;
                    foreach (var pair in fromDb.CreatureDetails)
                        snap.CreatureDetails[pair.Key] = pair.Value;
                    DfoServer.FileLogger.Log(
                        $"[A21UserInfo1] cid={characterId} equipped fallback from DB count={fromDb.EquippedEntries.Count}");
                }

                foreach (var entry in _entryLimitService.LoadSpecialDungeonLimits(
                             conn,
                             null,
                             characterAccountId,
                             characterId))
                {
                    snap.Dimensions.Add(new DimensionEntrySnapshot
                    {
                        Key = (uint)entry.DungeonId,
                        Val1 = 0,
                        Val2 = entry.CurrentCount,
                    });
                }

                
                using (var cmd = new SqliteCommand("SELECT flag1, flag2, flag3, flag4 FROM character_dimension_flags WHERE character_id=@cid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var r = cmd.ExecuteReader())
                    {
                        if (r.Read())
                        {
                            snap.DimFlag1 = (byte)r.GetInt32(0);
                            snap.DimFlag2 = (byte)r.GetInt32(1);
                            snap.DimFlag3 = (byte)r.GetInt32(2);
                            snap.DimFlag4 = (byte)r.GetInt32(3);
                        }
                    }
                }

                // The client consumes this as a list of completed QST ids and
                // resolves [special reward status] locally. Do not send raw
                // advanced attribute values through subtype1.
                using (var cmd = new SqliteCommand(@"
SELECT quest_id
FROM character_quest_completions
WHERE character_id=@cid AND completion_value<>0
ORDER BY quest_id", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var r = cmd.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            int questId = r.GetInt32(0);
                            if (GameWorld.QuestData.HasSpecialRewardStatus(questId))
                                snap.SpecialRewardQuestIds.Add((uint)questId);
                        }
                    }
                }
            }

            if (KnightShieldDataProvider.IsEligibleCharacter(characterJob))
            {
                KnightShieldEquipmentSnapshotSynchronizer.Apply(
                    characterJob,
                    characterGrowType,
                    snap,
                    knightShieldDeck ?? _knightShieldDeckRepository.Load(characterId));
            }

            return snap;
        }

        private static void ApplyNameTagState(NameTagState state, UserInfoAdditionSnapshot snap)
        {
            if (snap == null)
                return;

            if (state == null || !state.IsActive())
            {
                snap.NameTagItemId = 0;
                snap.NameTagExpireTime = 0;
                return;
            }

            snap.NameTagItemId = (uint)state.ItemId;
            snap.NameTagExpireTime = (uint)state.ExpireTime;
        }

        public int UpdateSkillTreeIndex(int characterId, byte skillTreeIndex)
        {
            var databaseValue = Game.Skills.SkillTreeExpansionState.ToDatabase(skillTreeIndex);
            using (var conn = Open())
            using (var cmd = new SqliteCommand(@"
INSERT INTO character_subtype1_fields(character_id, skill_tree_index)
VALUES(@cid, @idx)
ON CONFLICT(character_id) DO UPDATE SET skill_tree_index=@idx;", conn))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@idx", databaseValue);
                return cmd.ExecuteNonQuery();
            }
        }

        internal int UpdateSkillTreeIndex(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            byte skillTreeIndex)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));

            using (var cmd = new SqliteCommand(@"
INSERT INTO character_subtype1_fields(character_id, skill_tree_index)
VALUES(@cid, @idx)
ON CONFLICT(character_id) DO UPDATE SET skill_tree_index=@idx;", connection, transaction))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@idx", Game.Skills.SkillTreeExpansionState.ToDatabase(skillTreeIndex));
                return cmd.ExecuteNonQuery();
            }
        }

        public byte? LoadSkillTreeIndex(int characterId)
        {
            using (var conn = Open())
            using (var cmd = new SqliteCommand(
                "SELECT skill_tree_index FROM character_subtype1_fields WHERE character_id=@cid", conn))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                var value = cmd.ExecuteScalar();
                if (value == null || value == DBNull.Value)
                    return null;

                return NormalizeSkillTreeIndex(Convert.ToInt32(value));
            }
        }

        internal byte? LoadSkillTreeIndex(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            if (connection == null) throw new ArgumentNullException(nameof(connection));
            if (transaction == null) throw new ArgumentNullException(nameof(transaction));

            using (var cmd = new SqliteCommand(
                "SELECT skill_tree_index FROM character_subtype1_fields WHERE character_id=@cid",
                connection,
                transaction))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                var value = cmd.ExecuteScalar();
                if (value == null || value == DBNull.Value)
                    return null;
                return NormalizeSkillTreeIndex(Convert.ToInt32(value));
            }
        }

        /// <summary>
        /// CharacterStatComputer.BuildAdditionalInfo 输出的 82 字节 stat blob,
        /// 拆成 character_subtype1_fields 各 stat_* 列。偏移与 BuildAdditionalInfo 写入顺序一致。
        /// </summary>
        private readonly struct CombatStatFields
        {
            public readonly long HpMax, MpMax, InventoryLimit, MoveSpeed, Weight;
            public readonly int PhysicalAttack, PhysicalDefense, MagicalAttack, MagicalDefense;
            public readonly int FireRes, WaterRes, DarkRes, LightRes;
            public readonly int HpRegen, MpRegen, AttackSpeed, CastSpeed, HitRecovery, JumpPower;

            private CombatStatFields(byte[] b)
            {
                int o = 0;
                HpMax = (long)BitConverter.ToUInt32(b, o); o += 4;
                MpMax = (long)BitConverter.ToUInt32(b, o); o += 4;
                PhysicalAttack = BitConverter.ToInt16(b, o); o += 2;
                PhysicalDefense = BitConverter.ToInt16(b, o); o += 2;
                MagicalAttack = BitConverter.ToInt16(b, o); o += 2;
                MagicalDefense = BitConverter.ToInt16(b, o); o += 2;
                FireRes = BitConverter.ToInt16(b, o); o += 2;
                WaterRes = BitConverter.ToInt16(b, o); o += 2;
                DarkRes = BitConverter.ToInt16(b, o); o += 2;
                LightRes = BitConverter.ToInt16(b, o); o += 2;
                o += 34; // 17 × u16 占位, 与 BuildAdditionalInfo 的零占位对齐
                InventoryLimit = (long)BitConverter.ToUInt32(b, o); o += 4;
                HpRegen = BitConverter.ToUInt16(b, o); o += 2;
                MpRegen = BitConverter.ToUInt16(b, o); o += 2;
                MoveSpeed = (long)BitConverter.ToUInt32(b, o); o += 4;
                AttackSpeed = BitConverter.ToUInt16(b, o); o += 2;
                CastSpeed = BitConverter.ToUInt16(b, o); o += 2;
                HitRecovery = BitConverter.ToUInt16(b, o); o += 2;
                JumpPower = BitConverter.ToUInt16(b, o); o += 2;
                Weight = (long)BitConverter.ToUInt32(b, o);
            }

            public static CombatStatFields Parse(byte[] blob)
            {
                if (blob == null || blob.Length < 82)
                    throw new ArgumentException($"[Subtype1Repo] stat blob 长度不足: {blob?.Length ?? 0}/82");
                return new CombatStatFields(blob);
            }

            public void AddTo(SqliteCommand cmd)
            {
                cmd.Parameters.AddWithValue("@hp", HpMax);
                cmd.Parameters.AddWithValue("@mp", MpMax);
                cmd.Parameters.AddWithValue("@pa", PhysicalAttack);
                cmd.Parameters.AddWithValue("@pd", PhysicalDefense);
                cmd.Parameters.AddWithValue("@ma", MagicalAttack);
                cmd.Parameters.AddWithValue("@md", MagicalDefense);
                cmd.Parameters.AddWithValue("@fr", FireRes);
                cmd.Parameters.AddWithValue("@wr", WaterRes);
                cmd.Parameters.AddWithValue("@dr", DarkRes);
                cmd.Parameters.AddWithValue("@lr", LightRes);
                cmd.Parameters.AddWithValue("@il", InventoryLimit);
                cmd.Parameters.AddWithValue("@hr", HpRegen);
                cmd.Parameters.AddWithValue("@mr", MpRegen);
                cmd.Parameters.AddWithValue("@ms", MoveSpeed);
                cmd.Parameters.AddWithValue("@as2", AttackSpeed);
                cmd.Parameters.AddWithValue("@cs", CastSpeed);
                cmd.Parameters.AddWithValue("@hrc", HitRecovery);
                cmd.Parameters.AddWithValue("@jp", JumpPower);
                cmd.Parameters.AddWithValue("@wt", Weight);
            }
        }

        /// <summary>
        /// 按升级后的新等级重算战斗属性(HP/MP/攻防/抗性/速度/重量)并持久化。
        /// statBlob = CharacterStatComputer.BuildAdditionalInfo(job, level, first, second)。
        /// 必须用升级后的 level: 14级以下用基础表, 15-49 用转职成长表, 50+ 用觉醒成长表。
        /// </summary>
        public int UpdateCombatStats(int characterId, byte[] statBlob)
        {
            using (var conn = Open())
                return UpdateCombatStatsOnConnection(conn, characterId, statBlob);
        }

        /// <summary>同连接版本, 供 RecomputeAllCombatStats 在单连接内顺序执行避免锁冲突;
        /// 传入 tx 可并入外部事务(等级与属性写同生共死)。</summary>
        internal static int UpdateCombatStatsOnConnection(SqliteConnection conn, int characterId, byte[] statBlob, SqliteTransaction tx = null)
        {
            var f = CombatStatFields.Parse(statBlob);
            using (var cmd = new SqliteCommand(@"
UPDATE character_subtype1_fields SET
    stat_hp_max=@hp, stat_mp_max=@mp,
    stat_physical_attack=@pa, stat_physical_defense=@pd,
    stat_magical_attack=@ma, stat_magical_defense=@md,
    stat_fire_resistance=@fr, stat_water_resistance=@wr,
    stat_dark_resistance=@dr, stat_light_resistance=@lr,
    stat_inventory_limit=@il,
    stat_hp_regen_speed=@hr, stat_mp_regen_speed=@mr,
    stat_move_speed=@ms, stat_attack_speed=@as2,
    stat_cast_speed=@cs, stat_hit_recovery=@hrc,
    stat_jump_power=@jp, stat_weight=@wt, stat_level=@sl
WHERE character_id=@cid;", conn))
            {
                cmd.Transaction = tx;
                f.AddTo(cmd);
                cmd.Parameters.AddWithValue("@cid", characterId);
                // stat_level 固定 100, 与种子创建(SqliteSelectCharacterDataSource 建号 INSERT)保持一致;
                // 该字段非角色等级锚点, 属性面板由上方各 stat_* 列直接驱动, 升级不修改。
                cmd.Parameters.AddWithValue("@sl", 100);
                return cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// 启动时一次性按当前等级重算所有角色战斗属性, 修复历史"升级未重算属性"的存量数据。
        /// 幂等: 重复执行结果一致。单连接内顺序执行避免 SQLite 锁冲突。
        /// </summary>
        public int RecomputeAllCombatStats()
        {
            int repaired = 0;
            using (var conn = Open())
            {
                // 先收集所有角色到内存再关 reader, 否则循环内 UPDATE 会触发 SQLite 锁冲突。
                var rows = new List<(int cid, byte job, byte level, byte grow)>();
                using (var cmd = new SqliteCommand(@"
SELECT s.character_id, c.job, c.level, c.grow_type
FROM character_subtype1_fields s
JOIN characters c ON c.character_id = s.character_id;", conn))
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                        rows.Add((r.GetInt32(0), (byte)r.GetInt32(1), (byte)r.GetInt32(2), (byte)r.GetInt32(3)));
                }

                foreach (var (cid, job, level, grow) in rows)
                {
                    try
                    {
                        DfoServer.Game.Characters.CharacterStatComputer.DecodeGrowType(grow, out int first, out int second);
                        var blob = DfoServer.Game.Characters.CharacterStatComputer.BuildAdditionalInfo(job, level, first, second);
                        if (UpdateCombatStatsOnConnection(conn, cid, blob) > 0)
                            repaired++;
                    }
                    catch (Exception ex)
                    {
                        DfoServer.FileLogger.Log($"[Subtype1Repo] RecomputeAllCombatStats cid={cid} skip: {ex.Message}");
                    }
                }
            }
            return repaired;
        }

        private static byte NormalizeSkillTreeIndex(int skillTreeIndex)
        {
            return Game.Skills.SkillTreeExpansionState.FromDatabase(skillTreeIndex);
        }

        internal static byte[] ClearEquippedSortLockForClient(byte[] raw)
        {
            if (raw == null || raw.Length == 0)
                return Array.Empty<byte>();

            var copy = new byte[raw.Length];
            Buffer.BlockCopy(raw, 0, copy, 0, raw.Length);
            if (copy.Length >= 10)
                copy[copy.Length - 1] = 0;
            return copy;
        }

        private SqliteConnection Open()
        {
            var conn = new SqliteConnection(_connectionString);
            conn.Open();
            return conn;
        }
    }
}
