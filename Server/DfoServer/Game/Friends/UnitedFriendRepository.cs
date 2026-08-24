using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Friends
{
    /// <summary>
    /// 好友关系表（united_friend_relations）数据访问。
    ///
    /// 建表由 item_schema.sql（新库）+ SqliteMigrations v8（旧库）负责，构造时经
    /// SqliteDatabaseBootstrap.Initialize 先跑 schema+迁移，因此本仓储只做表 CRUD，
    /// 不得运行时隐式建表（见 AGENTS.md 硬性约定）。
    /// 单条 INSERT/DELETE 由 SQLite 原子执行，无需显式事务；多表业务写入才需同一事务。
    /// 内存图（UnitedFriendSystem.Friends）为运行期权威，本表仅在启动/写边/删边时访问。
    /// </summary>
    public sealed class UnitedFriendRepository
    {
        private readonly string _connectionString;

        public UnitedFriendRepository(string databasePath, string schemaFilePath)
        {
            if (string.IsNullOrWhiteSpace(databasePath))
                throw new ArgumentException("databasePath is empty", nameof(databasePath));
            if (string.IsNullOrWhiteSpace(schemaFilePath))
                throw new ArgumentException("schemaFilePath is empty", nameof(schemaFilePath));

            _connectionString = SqliteDatabaseBootstrap.Initialize(databasePath, schemaFilePath);
        }

        private SqliteConnection Open()
        {
            var conn = new SqliteConnection(_connectionString);
            conn.Open();
            return conn;
        }

        /// <summary>
        /// 全量读取好友关系 → a→{b...}（键区分大小写，与角色名 characters.name 一致）。启动时一次性载入内存图。
        /// 表在 schema/迁移中已建好，这里直接 SELECT。
        /// </summary>
        public Dictionary<string, HashSet<string>> LoadAll()
        {
            var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT owner_name, friend_name FROM united_friend_relations "
                    + "ORDER BY owner_name, friend_name;";
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var a = reader.GetString(0);
                        var b = reader.GetString(1);
                        if (!result.TryGetValue(a, out var set))
                        {
                            set = new HashSet<string>(StringComparer.Ordinal);
                            result[a] = set;
                        }
                        set.Add(b);
                    }
                }
            }
            return result;
        }

        /// <summary>写一条单向关系 a→b（幂等）。调用方保证在内存图去重后只写新增边。</summary>
        public void InsertRelation(string a, string b)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT OR IGNORE INTO united_friend_relations (owner_name, friend_name)
VALUES (@a, @b);";
                cmd.Parameters.AddWithValue("@a", a);
                cmd.Parameters.AddWithValue("@b", b);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>删除单向关系 a→b。返回是否删除了行（幂等：不存在返回 false）。</summary>
        public bool DeleteRelation(string a, string b)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
DELETE FROM united_friend_relations
WHERE owner_name = @a AND friend_name = @b;";
                cmd.Parameters.AddWithValue("@a", a);
                cmd.Parameters.AddWithValue("@b", b);
                return cmd.ExecuteNonQuery() > 0;
            }
        }

        /// <summary>
        /// 角色删除清理：删除该名字的所有关系（owner_name=name 或 friend_name=name）。
        /// 关系键是角色名，角色删除后名字不再存在，任一方向的关系都悬空，必须全量清理。
        /// 单条 DELETE 由 SQLite 原子执行，无需显式事务。
        /// </summary>
        public void DeleteAllRelations(string name)
        {
            using (var conn = Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
DELETE FROM united_friend_relations
WHERE owner_name = @n OR friend_name = @n;";
                cmd.Parameters.AddWithValue("@n", name);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>
        /// 角色更名：把该名字在表里所有出现位置换成新名（owner_name 与 friend_name 两方向）。
        /// 两条 UPDATE 在一个事务里提交，避免更名产生"owner 已换名但 friend 未换"的半更新状态
        /// （关系键是角色名，见 UnitedFriendSystem 类注释）。
        /// </summary>
        public void RenameAll(string oldName, string newName)
        {
            using (var conn = Open())
            using (var tx = conn.BeginTransaction())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
UPDATE united_friend_relations SET owner_name = @new
WHERE owner_name = @old;";
                    cmd.Parameters.AddWithValue("@new", newName);
                    cmd.Parameters.AddWithValue("@old", oldName);
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
UPDATE united_friend_relations SET friend_name = @new
WHERE friend_name = @old;";
                    cmd.Parameters.AddWithValue("@new", newName);
                    cmd.Parameters.AddWithValue("@old", oldName);
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }
    }
}
