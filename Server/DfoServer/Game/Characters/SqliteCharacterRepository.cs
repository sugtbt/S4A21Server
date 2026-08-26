using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using System.Data;
using Microsoft.Data.Sqlite;
using System.Globalization;
using DfoServer.Sqlite;

namespace DfoServer.Game.Characters
{
    public sealed class SqliteCharacterRepository : ICharacterRepository
    {
        private readonly IGameDatabase _database;

        public SqliteCharacterRepository(string databasePath, string schemaFilePath)
            : this(new GameDatabase(databasePath, schemaFilePath))
        {
        }

        public SqliteCharacterRepository(IGameDatabase database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        public CharacterRecord GetById(int characterId)
        {
            using (var conn = Open())
                return LoadById(conn, characterId);
        }

        public static CharacterRecord LoadById(SqliteConnection conn, int characterId)
        {
            if (conn == null)
                throw new ArgumentNullException(nameof(conn));

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = SelectColumns + " WHERE character_id = @id;";
                cmd.Parameters.AddWithValue("@id", characterId);
                using (var reader = cmd.ExecuteReader())
                    return reader.Read() ? Map(reader) : null;
            }
        }

        public IReadOnlyList<CharacterRecord> ListByAccount(int accountId)
        {
            var list = new List<CharacterRecord>();
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = SelectColumns + " WHERE account_id = @aid AND delete_flag = 0 ORDER BY slot_index, character_id;";
                cmd.Parameters.AddWithValue("@aid", accountId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                        list.Add(Map(reader));
                }
            }
            return list;
        }

        public int Create(CharacterRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (record.Name == null || record.Name.Length == 0) throw new ArgumentException("character name is empty", nameof(record));

            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                // 自动分配 slot_index：取该 account 下未删除角色中最大 slot_index + 1
                using (var maxCmd = conn.CreateCommand())
                {
                    maxCmd.CommandText = "SELECT COALESCE(MAX(slot_index), -1) FROM characters WHERE account_id = @accid AND delete_flag = 0;";
                    maxCmd.Parameters.AddWithValue("@accid", record.AccountId);
                    var maxSlot = Convert.ToInt32(maxCmd.ExecuteScalar());
                    record.SlotIndex = (byte)(maxSlot + 1);
                }

                cmd.CommandText = @"
INSERT INTO characters
    (character_id, account_id, name, job, grow_type, level,
     town_id, area_id, pos_x, pos_y, direction, area_state, appearance_blob, delete_flag, slot_index)
VALUES
    (@cid, @aid, @name, @job, @grow, @lvl,
     @town, @area, @px, @py, @dir, @astate, @blob, 0, @slot);
SELECT character_id FROM characters WHERE rowid = last_insert_rowid();";

                if (record.CharacterId > 0)
                    cmd.Parameters.AddWithValue("@cid", record.CharacterId);
                else
                    cmd.Parameters.AddWithValue("@cid", DBNull.Value);

                cmd.Parameters.AddWithValue("@aid", record.AccountId);
                cmd.Parameters.AddWithValue("@name", record.Name);
                cmd.Parameters.AddWithValue("@job", record.Job);
                cmd.Parameters.AddWithValue("@grow", record.GrowType);
                cmd.Parameters.AddWithValue("@lvl", record.Level);
                cmd.Parameters.AddWithValue("@town", record.TownId);
                cmd.Parameters.AddWithValue("@area", record.AreaId);
                cmd.Parameters.AddWithValue("@px", record.PosX);
                cmd.Parameters.AddWithValue("@py", record.PosY);
                cmd.Parameters.AddWithValue("@dir", record.Direction);
                cmd.Parameters.AddWithValue("@astate", record.AreaState);
                cmd.Parameters.AddWithValue("@blob", (object)CharacterAppearanceCodec.Encode(record.Appearance) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@slot", (int)record.SlotIndex);

                return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        public void UpdatePosition(int characterId, byte townId, byte areaId, short posX, short posY, byte direction, byte areaState)
        {
            using (var conn = Open())
            {
                UpdatePositionInTransaction(
                    conn,
                    null,
                    characterId,
                    townId,
                    areaId,
                    posX,
                    posY,
                    direction,
                    areaState);
            }
        }

        internal static bool UpdatePositionInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            byte townId,
            byte areaId,
            short posX,
            short posY,
            byte direction,
            byte areaState)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (characterId <= 0)
                return false;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"UPDATE characters
SET town_id = @town,
    area_id = @area,
    pos_x = @px,
    pos_y = @py,
    direction = @dir,
    area_state = @astate,
    updated_at = CURRENT_TIMESTAMP
WHERE character_id = @id;";
                command.Parameters.AddWithValue("@town", townId);
                command.Parameters.AddWithValue("@area", areaId);
                command.Parameters.AddWithValue("@px", posX);
                command.Parameters.AddWithValue("@py", posY);
                command.Parameters.AddWithValue("@dir", direction);
                command.Parameters.AddWithValue("@astate", areaState);
                command.Parameters.AddWithValue("@id", characterId);
                return command.ExecuteNonQuery() == 1;
            }
        }

        internal static bool UpdateAuraSkinFlagInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            byte auraSkinFlag)
        {
            if (connection == null)
                throw new ArgumentNullException(nameof(connection));
            if (characterId <= 0)
                return false;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"UPDATE characters
SET aura_skin_flag = @flag,
    updated_at = CURRENT_TIMESTAMP
WHERE character_id = @id;";
                command.Parameters.AddWithValue("@flag", auraSkinFlag != 0 ? 1 : 0);
                command.Parameters.AddWithValue("@id", characterId);
                return command.ExecuteNonQuery() == 1;
            }
        }

        public void UpdateSeedFields(int characterId, byte[] name, byte job, byte growType, byte level,
            byte pvpGrade, byte pvpRatingGrade, byte userState,
            CharacterAppearanceEntry[] appearance, DateTime? createdAt = null)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"UPDATE characters
                                    SET name = @name, job = @job, grow_type = @grow, level = @lvl,
                                        pvp_grade = @pvpG, pvp_rating_grade = @pvpR, user_state = @ustate,
                                        appearance_blob = @blob, created_at = @cat, updated_at = CURRENT_TIMESTAMP
                                    WHERE character_id = @id;";
                cmd.Parameters.AddWithValue("@name", (object)name ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@job", (int)job);
                cmd.Parameters.AddWithValue("@grow", (int)growType);
                cmd.Parameters.AddWithValue("@lvl", (int)level);
                cmd.Parameters.AddWithValue("@pvpG", (int)pvpGrade);
                cmd.Parameters.AddWithValue("@pvpR", (int)pvpRatingGrade);
                cmd.Parameters.AddWithValue("@ustate", (int)userState);
                cmd.Parameters.AddWithValue("@blob", (object)CharacterAppearanceCodec.Encode(appearance) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@cat", (createdAt ?? DateTime.UtcNow).ToString("o", CultureInfo.InvariantCulture));
                cmd.Parameters.AddWithValue("@id", characterId);
                cmd.ExecuteNonQuery();
            }
        }

        // 仅供自测 setup 使用: 裸写 level/exp, 绕过战斗属性重算。
        // 业务代码一律走经验系统(Game/Progression), 已从 ICharacterRepository 撤下。
        internal void UpdateLevelAndExp(int characterId, byte level, uint exp)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"UPDATE characters SET level = @lvl, exp = @exp, updated_at = CURRENT_TIMESTAMP WHERE character_id = @id;";
                cmd.Parameters.AddWithValue("@lvl", (int)level);
                cmd.Parameters.AddWithValue("@exp", (long)exp);
                cmd.Parameters.AddWithValue("@id", characterId);
                cmd.ExecuteNonQuery();
            }
        }

        public void UpdateAppearance(int characterId, CharacterAppearanceEntry[] appearance)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"UPDATE characters
                                    SET appearance_blob = @blob, updated_at = CURRENT_TIMESTAMP
                                    WHERE character_id = @id;";
                cmd.Parameters.AddWithValue("@blob", (object)CharacterAppearanceCodec.Encode(appearance) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@id", characterId);
                cmd.ExecuteNonQuery();
            }
        }

        public void SoftDelete(int characterId)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"UPDATE characters SET delete_flag = 1, updated_at = CURRENT_TIMESTAMP
                                    WHERE character_id = @id;";
                cmd.Parameters.AddWithValue("@id", characterId);
                cmd.ExecuteNonQuery();
            }
        }

        public void SoftDeleteAndCompactSlots(int accountId, int characterId, byte slotIndex)
        {
            using (var conn = Open())
            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = @"UPDATE characters SET delete_flag = 1, updated_at = CURRENT_TIMESTAMP
                                            WHERE character_id = @id;";
                        cmd.Parameters.AddWithValue("@id", characterId);
                        cmd.ExecuteNonQuery();
                    }

                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = @"UPDATE characters SET slot_index = slot_index - 1
                                            WHERE account_id = @aid AND delete_flag = 0 AND slot_index > @slot;";
                        cmd.Parameters.AddWithValue("@aid", accountId);
                        cmd.Parameters.AddWithValue("@slot", (int)slotIndex);
                        cmd.ExecuteNonQuery();
                    }

                    tx.Commit();
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
            }
        }

        public CharacterRecord GetByName(string name)
        {
            return FindByName(name, includeDeleted: false);
        }

        public CharacterRecord GetByNameIncludingDeleted(string name)
        {
            return FindByName(name, includeDeleted: true);
        }

        private CharacterRecord FindByName(string name, bool includeDeleted)
        {
            var gbkBytes = ClientTextEncoding.GetBytes(name ?? string.Empty);
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = includeDeleted
                    ? SelectColumns + " WHERE name = @name OR name = @gbkBytes ORDER BY delete_flag ASC LIMIT 1;"
                    : SelectColumns + " WHERE (name = @name OR name = @gbkBytes) AND delete_flag = 0;";
                cmd.Parameters.AddWithValue("@name", name ?? string.Empty);
                cmd.Parameters.AddWithValue("@gbkBytes", gbkBytes);
                using (var reader = cmd.ExecuteReader())
                    return reader.Read() ? Map(reader) : null;
            }
        }

        public int CountByAccount(int accountId)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM characters WHERE account_id = @aid AND delete_flag = 0;";
                cmd.Parameters.AddWithValue("@aid", accountId);
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        public void SwapSlotIndexes(int accountId, byte slotA, byte slotB)
        {
            if (slotA == slotB) return;
            using (var conn = Open())
            using (var tx = conn.BeginTransaction())
            {
                try
                {
                    // Step 1: move slotA to temp -1
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "UPDATE characters SET slot_index = -1 WHERE account_id = @aid AND delete_flag = 0 AND slot_index = @slot;";
                        cmd.Parameters.AddWithValue("@aid", accountId);
                        cmd.Parameters.AddWithValue("@slot", (int)slotA);
                        cmd.ExecuteNonQuery();
                    }
                    // Step 2: move slotB to slotA
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "UPDATE characters SET slot_index = @slotA WHERE account_id = @aid AND delete_flag = 0 AND slot_index = @slotB;";
                        cmd.Parameters.AddWithValue("@aid", accountId);
                        cmd.Parameters.AddWithValue("@slotA", (int)slotA);
                        cmd.Parameters.AddWithValue("@slotB", (int)slotB);
                        cmd.ExecuteNonQuery();
                    }
                    // Step 3: move temp to slotB
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.CommandText = "UPDATE characters SET slot_index = @slotB WHERE account_id = @aid AND delete_flag = 0 AND slot_index = -1;";
                        cmd.Parameters.AddWithValue("@aid", accountId);
                        cmd.Parameters.AddWithValue("@slotB", (int)slotB);
                        cmd.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
            }
        }

        private const string SelectColumns = @"
SELECT character_id, account_id, CAST(name AS BLOB), job, grow_type, level,
       town_id, area_id, pos_x, pos_y, direction, area_state, appearance_blob,
       delete_flag, created_at, updated_at, exp, ex_equip_slot_stat,
       pvp_grade, pvp_rating_grade, user_state, bonus_sp, bonus_tp, slot_index,
       aura_skin_flag, growup_change_count
FROM characters";

        private static CharacterRecord Map(IDataRecord r)
        {
            var appearBlob = r.IsDBNull(12) ? null : (byte[])r.GetValue(12);
            return new CharacterRecord
            {
                CharacterId = r.GetInt32(0),
                AccountId = r.GetInt32(1),
                Name = r.IsDBNull(2) ? null : ReadNameBlob(r, 2),
                Job = (byte)r.GetInt32(3),
                GrowType = (byte)r.GetInt32(4),
                Level = (byte)r.GetInt32(5),
                TownId = (byte)r.GetInt32(6),
                AreaId = (byte)r.GetInt32(7),
                PosX = (short)r.GetInt32(8),
                PosY = (short)r.GetInt32(9),
                Direction = (byte)r.GetInt32(10),
                AreaState = (byte)r.GetInt32(11),
                Appearance = CharacterAppearanceCodec.Decode(appearBlob),
                Deleted = r.GetInt32(13) != 0,
                CreatedAt = ParseDate(r.GetString(14)),
                UpdatedAt = ParseDate(r.GetString(15)),
                Exp = r.FieldCount > 16 && !r.IsDBNull(16) ? (uint)r.GetInt64(16) : 0u,
                ExEquipSlotStat = r.FieldCount > 17 && !r.IsDBNull(17) ? (byte)r.GetInt32(17) : (byte)0,
                PvpGrade = r.FieldCount > 18 && !r.IsDBNull(18) ? (byte)r.GetInt32(18) : (byte)0,
                PvpRatingGrade = r.FieldCount > 19 && !r.IsDBNull(19) ? (byte)r.GetInt32(19) : (byte)0,
                UserState = r.FieldCount > 20 && !r.IsDBNull(20) ? (byte)r.GetInt32(20) : (byte)0,
                BonusSp = r.FieldCount > 21 && !r.IsDBNull(21) ? r.GetInt32(21) : 0,
                BonusTp = r.FieldCount > 22 && !r.IsDBNull(22) ? r.GetInt32(22) : 0,
                SlotIndex = r.FieldCount > 23 && !r.IsDBNull(23) ? (byte)r.GetInt32(23) : (byte)0,
                AuraSkinFlag = r.FieldCount > 24 && !r.IsDBNull(24) ? (byte)r.GetInt32(24) : (byte)0,
                GrowupChangeCount = r.FieldCount > 25 && !r.IsDBNull(25) ? r.GetInt32(25) : 0,
            };
        }

        private static byte[] ReadNameBlob(IDataRecord r, int ordinal)
        {
            var val = r.GetValue(ordinal);
            if (val is byte[] b) return b;
            if (val is string s) return ClientTextEncoding.GetBytes(s);
            return null;
        }

        private static DateTime ParseDate(string text)
        {
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                return dt;
            return DateTime.MinValue;
        }

        private SqliteConnection Open()
        {
            return _database.OpenConnection();
        }
    }
}
