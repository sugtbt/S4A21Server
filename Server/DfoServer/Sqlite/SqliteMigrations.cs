using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Sqlite
{
    // 新数据库基线迁移器。
    // 旧项目 v1-v52 迁移链已经清理，只作为本次基线设计的历史依据。
    internal static class SqliteMigrations
    {
        internal const string BaselineId = "86jp-database-v1";
        internal const int BaselineVersion = 1;

        // 后续新增功能从 v2 开始追加。迁移只能依赖 SQL/数据库基础设施，不能调用业务 Service。
        private static readonly IReadOnlyList<MigrationStep> Steps =
            new[]
            {
                new MigrationStep(2, "expand_item_core_to_99_and_shift_equipment_slots", ApplyExpandItemCoreTo99),
                new MigrationStep(3, "import_character_new_items", ApplyImportCharacterNewItems),
                new MigrationStep(4, "add_item_purchase_limits", ApplyPurchaseLimitTracking),
                new MigrationStep(5, "add_aura_skin_flag", ApplyAuraSkinFlag),
                new MigrationStep(6, "add_daily_challenge_entry_claims", ApplyDailyChallengeEntryClaims),
                new MigrationStep(7, "add_character_item_states", ApplyCharacterItemStates),
                new MigrationStep(8, "add_growup_change_count", ApplyGrowupChangeCount),
                new MigrationStep(9, "add_united_friend_relations", ApplyUnitedFriendRelations),
                new MigrationStep(10, "add_game_events_and_joust", ApplyGameEventsAndJoust),
                new MigrationStep(11, "convert_client_text_blobs_to_gbk", ApplyConvertClientTextBlobsToGbk),
                // 12: characters slot 空洞压缩。客户端会话内列表刷新(创建/删除后补发列表)要求
                // slot 连续, 空洞 slot 会被当数组索引访问越界崩溃。存量数据可能存在 slot 空洞
                // (历史软删角色仍占用 slot_index 未被回收), 无法保证所有环境数据连续。
                // 一次性把每个账号活跃角色按 slot_index 升序重排为连续 0,1,2...(保持当前排列顺序),
                // 之后删除走"前移一位"增量压缩保持连续。幂等: 已连续库与新库无变化。
                new MigrationStep(12, "compress_character_slot_holes", ApplyCompressCharacterSlotHoles),
                new MigrationStep(13, "add_dungeon_entry_limits", ApplyDungeonEntryLimits),
                new MigrationStep(14, "remove_dungeon_limit_noti2_entry_flag", ApplyRemoveDungeonLimitNoti2EntryFlag),
            };

        internal static int CurrentVersion =>
            Steps.Count == 0 ? BaselineVersion : Steps[Steps.Count - 1].Version;

        internal static void MarkCurrent(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO schema_metadata (
    singleton_id, baseline_id, schema_version, created_at, updated_at
) VALUES (
    1, @baselineId, @schemaVersion, CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
)
ON CONFLICT(singleton_id) DO UPDATE SET
    baseline_id = excluded.baseline_id,
    schema_version = excluded.schema_version,
    updated_at = CURRENT_TIMESTAMP;";
                command.Parameters.AddWithValue("@baselineId", BaselineId);
                command.Parameters.AddWithValue("@schemaVersion", CurrentVersion);
                command.ExecuteNonQuery();
            }

            SetUserVersion(connection, transaction, CurrentVersion);
        }

        internal static void Apply(SqliteConnection connection)
        {
            var metadata = ReadMetadata(connection);
            if (!string.Equals(metadata.BaselineId, BaselineId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"数据库不是 86JP 新基线（需要 baseline_id={BaselineId}）。" +
                    "请先备份并移走旧数据库，让服务端按当前代码创建新库。" +
                    "历史 v1-v52 迁移不会在服务启动时执行。");
            }

            var version = ReadVersion(connection);
            if (version > CurrentVersion || metadata.SchemaVersion > CurrentVersion)
            {
                throw new InvalidOperationException(
                    $"数据库 schema v{Math.Max(version, metadata.SchemaVersion)} 高于当前服务支持的 " +
                    $"v{CurrentVersion}。");
            }

            if (version != metadata.SchemaVersion)
            {
                throw new InvalidOperationException(
                    $"数据库 schema 元数据不一致: user_version={version}, " +
                    $"schema_metadata.schema_version={metadata.SchemaVersion}。");
            }

            foreach (var step in Steps)
            {
                if (step.Version <= version)
                    continue;
                if (step.Version != version + 1)
                {
                    throw new InvalidOperationException(
                        $"数据库迁移版本不连续: current={version}, next={step.Version}。");
                }

                using (var transaction = connection.BeginTransaction())
                {
                    step.Apply(connection, transaction);
                    WriteVersion(connection, transaction, step.Version);
                    transaction.Commit();
                }

                version = step.Version;
                FileLogger.Log($"[Db] migration v{step.Version} applied: {step.Name}");
            }
        }

        internal static bool HasCurrentBaseline(SqliteConnection connection)
        {
            var metadata = ReadMetadata(connection);
            return string.Equals(metadata.BaselineId, BaselineId, StringComparison.Ordinal);
        }

        internal static void ApplyExpandItemCoreTo99(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            ImportCharacterNewItems(connection, transaction, shiftEquipmentSlots: false, dropSourceTable: false);

            EnsureItemCoreLengths(connection, transaction, "character_inventory_items", nullable: false);
            EnsureItemCoreLengths(connection, transaction, "account_inventory_items", nullable: false);
            EnsureItemCoreLengths(connection, transaction, "character_titlebook_items", nullable: false);
            EnsureItemCoreLengths(connection, transaction, "mailbox_attachments", nullable: true);

            RebuildCharacterInventoryItems(connection, transaction);
            RebuildAccountInventoryItems(connection, transaction);
            RebuildCharacterTitleBookItems(connection, transaction);
            RebuildMailboxAttachments(connection, transaction);
            ShiftCharacterAppearanceBlobSlots(connection, transaction);
        }

        private static void ApplyImportCharacterNewItems(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            ImportCharacterNewItems(connection, transaction, shiftEquipmentSlots: true, dropSourceTable: true);
        }

        private static void ApplyDailyChallengeEntryClaims(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            ExecuteSql(connection, transaction, @"
CREATE TABLE IF NOT EXISTS character_daily_challenge_entry_claims (
    character_id INTEGER NOT NULL,
    group_index INTEGER NOT NULL CHECK (group_index >= 0 AND group_index < 6),
    entry_index INTEGER NOT NULL CHECK (entry_index >= 0),
    quest_id INTEGER NOT NULL CHECK (quest_id > 0),
    claimed_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (character_id, group_index, entry_index),
    FOREIGN KEY (character_id, group_index, entry_index)
        REFERENCES character_daily_challenge_entries(character_id, group_index, entry_index)
        ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS character_daily_challenge_progress_events (
    character_id INTEGER NOT NULL,
    source_event_id TEXT NOT NULL,
    group_index INTEGER NOT NULL,
    entry_index INTEGER NOT NULL,
    quest_id INTEGER NOT NULL,
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (character_id, source_event_id, group_index, entry_index),
    FOREIGN KEY (character_id, group_index, entry_index)
        REFERENCES character_daily_challenge_entries(character_id, group_index, entry_index)
        ON DELETE CASCADE
);");
        }

        private static void ApplyCharacterItemStates(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            var hasLegacy = TableExists(connection, transaction, "character_item_values");
            if (hasLegacy)
            {
                ExecuteSql(
                    connection,
                    transaction,
                    "ALTER TABLE character_item_values RENAME TO character_item_values_legacy;");
            }

            ExecuteSql(connection, transaction, @"
CREATE TABLE IF NOT EXISTS character_item_states (
    character_id INTEGER NOT NULL,
    state_kind TEXT NOT NULL CHECK(state_kind IN ('cooltime', 'effect')),
    item_id INTEGER NOT NULL,
    expire_time INTEGER NOT NULL,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (character_id, state_kind, item_id),
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);");

            if (!hasLegacy)
                return;

            ExecuteSql(connection, transaction, @"
INSERT OR REPLACE INTO character_item_states (
    character_id, state_kind, item_id, expire_time, updated_at
)
SELECT character_id,
       list_kind,
       item_id,
       MAX(value),
       CURRENT_TIMESTAMP
FROM character_item_values_legacy
WHERE list_kind IN ('cooltime', 'effect')
  AND item_id > 0
  AND value > 0
GROUP BY character_id, list_kind, item_id;

DROP TABLE character_item_values_legacy;");
        }

        // 好友关系表（UnitedFriendSystem，单向 A→B）。键是角色名(BINARY 大小写敏感)，
        // 见 item_schema.sql 同表注释；旧库升级路径经此迁移建表（IF NOT EXISTS 幂等，
        // 新库由 item_schema.sql 已建，此处自动跳过）。
        private static void ApplyGrowupChangeCount(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            AddColumnIfMissing(
                connection,
                transaction,
                "characters",
                "growup_change_count",
                "INTEGER NOT NULL DEFAULT 0");
        }

        private static void ApplyUnitedFriendRelations(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            ExecuteSql(connection, transaction, @"
CREATE TABLE IF NOT EXISTS united_friend_relations (
    owner_name  TEXT NOT NULL,
    friend_name TEXT NOT NULL,
    created_at  TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (owner_name, friend_name)
);
CREATE INDEX IF NOT EXISTS idx_united_friend_relations_friend
    ON united_friend_relations(friend_name);");
        }

        private static void ApplyGameEventsAndJoust(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            ExecuteSql(connection, transaction, @"
CREATE TABLE IF NOT EXISTS game_event_state (
    event_id INTEGER PRIMARY KEY,
    state INTEGER NOT NULL DEFAULT 0 CHECK(state IN (0, 1))
);

CREATE TABLE IF NOT EXISTS game_event_info_details (
    event_id INTEGER PRIMARY KEY,
    unknown0 INTEGER NOT NULL DEFAULT 0,
    start_notice TEXT NOT NULL DEFAULT '',
    end_notice TEXT NOT NULL DEFAULT '',
    detail_flag INTEGER NOT NULL DEFAULT 0 CHECK(detail_flag IN (0, 1)),
    flag_a INTEGER NOT NULL DEFAULT 0 CHECK(flag_a >= 0 AND flag_a <= 255),
    flag_b INTEGER NOT NULL DEFAULT 0 CHECK(flag_b >= 0 AND flag_b <= 255),
    title TEXT NOT NULL DEFAULT '',
    short_name TEXT NOT NULL DEFAULT '',
    reserved_or_icon TEXT NOT NULL DEFAULT '',
    start_unix_time INTEGER NOT NULL DEFAULT 0,
    end_unix_time INTEGER NOT NULL DEFAULT 0,
    link_key TEXT NOT NULL DEFAULT '',
    description TEXT NOT NULL DEFAULT '',
    detail_enabled INTEGER NOT NULL DEFAULT 0 CHECK(detail_enabled IN (0, 1)),
    sort_order INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (event_id) REFERENCES game_event_state(event_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS game_event_info_extra (
    event_id INTEGER PRIMARY KEY,
    param0 INTEGER NOT NULL DEFAULT 0,
    param1 INTEGER NOT NULL DEFAULT 0,
    param2 INTEGER NOT NULL DEFAULT 0,
    param3 INTEGER NOT NULL DEFAULT 0,
    param4 INTEGER NOT NULL DEFAULT 0,
    param5 INTEGER NOT NULL DEFAULT 0,
    param6 INTEGER NOT NULL DEFAULT 0,
    param7 INTEGER NOT NULL DEFAULT 0,
    param8 INTEGER NOT NULL DEFAULT 0,
    param9 INTEGER NOT NULL DEFAULT 0,
    param10 INTEGER NOT NULL DEFAULT 0,
    param11 INTEGER NOT NULL DEFAULT 0,
    sort_order INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (event_id) REFERENCES game_event_state(event_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS event_joust_rules (
    event_id INTEGER PRIMARY KEY,
    current_round INTEGER NOT NULL DEFAULT 1 CHECK(current_round > 0),
    current_day_id INTEGER NOT NULL DEFAULT 0,
    current_schedule_index INTEGER NOT NULL DEFAULT -1,
    start_hour INTEGER NOT NULL DEFAULT 10 CHECK(start_hour >= 0 AND start_hour < 24),
    rounds_per_day INTEGER NOT NULL DEFAULT 7 CHECK(rounds_per_day > 0),
    round_interval_minutes INTEGER NOT NULL DEFAULT 120 CHECK(round_interval_minutes > 0),
    betting_duration_minutes INTEGER NOT NULL DEFAULT 90 CHECK(betting_duration_minutes > 0),
    stop_betting_minutes INTEGER NOT NULL DEFAULT 10 CHECK(stop_betting_minutes >= 0),
    result_stage_count INTEGER NOT NULL DEFAULT 3 CHECK(result_stage_count = 3),
    result_stage_interval_seconds INTEGER NOT NULL DEFAULT 200 CHECK(result_stage_interval_seconds > 0),
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (event_id) REFERENCES game_event_state(event_id) ON DELETE CASCADE
);

CREATE TABLE IF NOT EXISTS event_joust_round_slots (
    round_no INTEGER NOT NULL,
    slot_no INTEGER NOT NULL CHECK(slot_no >= 0 AND slot_no < 8),
    knight_index INTEGER NOT NULL,
    is_black INTEGER NOT NULL DEFAULT 0 CHECK(is_black IN (0, 1)),
    attack_type INTEGER NOT NULL DEFAULT 0,
    condition_index INTEGER NOT NULL DEFAULT 0 CHECK(condition_index >= 0 AND condition_index <= 4),
    global_bet_amount INTEGER NOT NULL DEFAULT 0 CHECK(global_bet_amount >= 0),
    round_day_id INTEGER NOT NULL DEFAULT 0,
    schedule_index INTEGER NOT NULL DEFAULT -1,
    round_start_unix_time INTEGER NOT NULL DEFAULT 0,
    created_at_unix INTEGER NOT NULL DEFAULT 0,
    updated_at_unix INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (round_no, slot_no),
    UNIQUE (round_no, knight_index)
);

CREATE TABLE IF NOT EXISTS event_joust_knight_stats (
    knight_index INTEGER PRIMARY KEY,
    win_count INTEGER NOT NULL DEFAULT 0 CHECK(win_count >= 0),
    loss_count INTEGER NOT NULL DEFAULT 0 CHECK(loss_count >= 0),
    updated_at_unix INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS event_joust_character_bets (
    round_no INTEGER NOT NULL,
    character_id INTEGER NOT NULL,
    slot_no INTEGER NOT NULL CHECK(slot_no >= 0 AND slot_no < 8),
    knight_index INTEGER NOT NULL,
    bet_amount INTEGER NOT NULL DEFAULT 0 CHECK(bet_amount >= 0),
    reward_mail_sent INTEGER NOT NULL DEFAULT 0 CHECK(reward_mail_sent IN (0, 1)),
    reward_mail_sent_at INTEGER NOT NULL DEFAULT 0,
    created_at_unix INTEGER NOT NULL DEFAULT 0,
    updated_at_unix INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (round_no, character_id, slot_no),
    FOREIGN KEY (round_no, slot_no)
        REFERENCES event_joust_round_slots(round_no, slot_no)
        ON DELETE CASCADE,
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_event_joust_bets_reward
    ON event_joust_character_bets(round_no, reward_mail_sent);

CREATE TABLE IF NOT EXISTS event_joust_results (
    round_no INTEGER PRIMARY KEY,
    stage_index INTEGER NOT NULL DEFAULT -1,
    slot0 INTEGER NOT NULL DEFAULT 0,
    slot1 INTEGER NOT NULL DEFAULT 0,
    slot2 INTEGER NOT NULL DEFAULT 0,
    slot3 INTEGER NOT NULL DEFAULT 0,
    slot4 INTEGER NOT NULL DEFAULT 0,
    slot5 INTEGER NOT NULL DEFAULT 0,
    slot6 INTEGER NOT NULL DEFAULT 0,
    slot7 INTEGER NOT NULL DEFAULT 0,
    slot8 INTEGER NOT NULL DEFAULT 0,
    slot9 INTEGER NOT NULL DEFAULT 0,
    slot10 INTEGER NOT NULL DEFAULT 0,
    slot11 INTEGER NOT NULL DEFAULT 0,
    slot12 INTEGER NOT NULL DEFAULT 0,
    slot13 INTEGER NOT NULL DEFAULT 0,
    updated_at_unix INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS event_joust_match_results (
    round_no INTEGER NOT NULL,
    stage_index INTEGER NOT NULL CHECK(stage_index >= 0 AND stage_index < 3),
    match_index INTEGER NOT NULL CHECK(match_index >= 0 AND match_index < 4),
    winner_slot_no INTEGER NOT NULL CHECK(winner_slot_no >= 0 AND winner_slot_no < 8),
    loser_slot_no INTEGER NOT NULL CHECK(loser_slot_no >= 0 AND loser_slot_no < 8),
    winner_knight_index INTEGER NOT NULL,
    loser_knight_index INTEGER NOT NULL,
    resolved_at_unix INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (round_no, stage_index, match_index)
);

CREATE TABLE IF NOT EXISTS event_joust_history (
    round_no INTEGER PRIMARY KEY,
    winner_horse_id INTEGER NOT NULL,
    odds_x10 INTEGER NOT NULL DEFAULT 80,
    settled_at_unix INTEGER NOT NULL DEFAULT 0
);");
        }

        // schema v11：v11 以下旧库一次性把旧 UTF-8 线上名字节改成 GBK。新库直接标当前版本，不跑本步。
        private static void ApplyConvertClientTextBlobsToGbk(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            var converted = 0;
            if (TableExists(connection, transaction, "characters"))
                converted += ConvertCharacterNames(connection, transaction);
            if (TableExists(connection, transaction, "character_creatures"))
                converted += ConvertCreatureText(connection, transaction);
            if (TableExists(connection, transaction, "mailbox_attachments"))
                converted += ConvertMailboxCreatureNames(connection, transaction);

            FileLogger.Log(
                $"[Db] migration v11 converted {converted} client text blob(s) from legacy UTF-8 to GBK");
        }

        private static int ConvertCharacterNames(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            var pending = new List<(int Id, byte[] Gbk)>();
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT character_id, name FROM characters;";
                using (var reader = select.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (!TryReadStoredBytes(reader, 1, out var stored)
                            || !ClientTextEncoding.TryConvertLegacyUtf8WireToGbk(stored, out var gbk))
                        {
                            continue;
                        }

                        pending.Add((reader.GetInt32(0), gbk));
                    }
                }
            }

            if (pending.Count == 0)
                return 0;

            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = "UPDATE characters SET name = @name WHERE character_id = @id;";
                var idParam = update.Parameters.Add("@id", SqliteType.Integer);
                var nameParam = update.Parameters.Add("@name", SqliteType.Blob);
                foreach (var row in pending)
                {
                    idParam.Value = row.Id;
                    nameParam.Value = row.Gbk;
                    try
                    {
                        update.ExecuteNonQuery();
                    }
                    catch (SqliteException ex)
                    {
                        throw new InvalidOperationException(
                            $"schema v11: characters.name unique conflict converting character_id={row.Id}",
                            ex);
                    }
                }
            }

            return pending.Count;
        }

        private static int ConvertCreatureText(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            var pending = new List<(int CharacterId, int SortOrder, byte[] Gbk)>();
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText =
                    "SELECT character_id, sort_order, creature_text FROM character_creatures;";
                using (var reader = select.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (!TryReadStoredBytes(reader, 2, out var stored)
                            || !ClientTextEncoding.TryConvertLegacyUtf8WireToGbk(stored, out var gbk))
                        {
                            continue;
                        }

                        pending.Add((reader.GetInt32(0), reader.GetInt32(1), gbk));
                    }
                }
            }

            if (pending.Count == 0)
                return 0;

            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText =
                    "UPDATE character_creatures SET creature_text = @text " +
                    "WHERE character_id = @id AND sort_order = @ord;";
                var idParam = update.Parameters.Add("@id", SqliteType.Integer);
                var ordParam = update.Parameters.Add("@ord", SqliteType.Integer);
                var textParam = update.Parameters.Add("@text", SqliteType.Blob);
                foreach (var row in pending)
                {
                    idParam.Value = row.CharacterId;
                    ordParam.Value = row.SortOrder;
                    textParam.Value = row.Gbk;
                    update.ExecuteNonQuery();
                }
            }

            return pending.Count;
        }

        private static int ConvertMailboxCreatureNames(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            var pending = new List<(int Id, string Json)>();
            using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText =
                    "SELECT attachment_id, detail_json FROM mailbox_attachments " +
                    "WHERE detail_json IS NOT NULL AND length(detail_json) > 0;";
                using (var reader = select.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var json = reader.IsDBNull(1) ? null : reader.GetString(1);
                        if (!TryConvertMailboxDetailJson(json, out var rewritten))
                            continue;

                        pending.Add((reader.GetInt32(0), rewritten));
                    }
                }
            }

            if (pending.Count == 0)
                return 0;

            using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText =
                    "UPDATE mailbox_attachments SET detail_json = @json WHERE attachment_id = @id;";
                var idParam = update.Parameters.Add("@id", SqliteType.Integer);
                var jsonParam = update.Parameters.Add("@json", SqliteType.Text);
                foreach (var row in pending)
                {
                    idParam.Value = row.Id;
                    jsonParam.Value = row.Json;
                    update.ExecuteNonQuery();
                }
            }

            return pending.Count;
        }

        private static bool TryReadStoredBytes(SqliteDataReader reader, int ordinal, out byte[] stored)
        {
            stored = null;
            if (reader.IsDBNull(ordinal))
                return false;

            var value = reader.GetValue(ordinal);
            if (value is byte[] bytes)
            {
                stored = bytes;
                return stored.Length > 0;
            }

            if (value is string text && text.Length > 0)
            {
                stored = Encoding.UTF8.GetBytes(text);
                return stored.Length > 0;
            }

            return false;
        }

        private static bool TryConvertMailboxDetailJson(string json, out string rewritten)
        {
            rewritten = json;
            if (string.IsNullOrWhiteSpace(json))
                return false;

            try
            {
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement;
                if (!TryGetProperty(root, "Creature", "creature", out var creature)
                    || !TryGetProperty(creature, "NameBytes", "nameBytes", out var nameNode)
                    || nameNode.ValueKind != JsonValueKind.String)
                {
                    return false;
                }

                byte[] stored;
                try
                {
                    stored = nameNode.GetBytesFromBase64();
                }
                catch
                {
                    return false;
                }

                if (!ClientTextEncoding.TryConvertLegacyUtf8WireToGbk(stored, out var gbk))
                    return false;

                rewritten = RewriteMailboxNameBytes(root, gbk);
                return rewritten != json;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string RewriteMailboxNameBytes(JsonElement root, byte[] gbk)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                WriteJsonReplacingNameBytes(root, writer, gbk);
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static void WriteJsonReplacingNameBytes(
            JsonElement element,
            Utf8JsonWriter writer,
            byte[] gbk)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject())
                    {
                        writer.WritePropertyName(property.Name);
                        if ((property.Name == "NameBytes" || property.Name == "nameBytes")
                            && property.Value.ValueKind == JsonValueKind.String)
                        {
                            writer.WriteBase64StringValue(gbk);
                        }
                        else
                        {
                            WriteJsonReplacingNameBytes(property.Value, writer, gbk);
                        }
                    }

                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                        WriteJsonReplacingNameBytes(item, writer, gbk);
                    writer.WriteEndArray();
                    break;
                default:
                    element.WriteTo(writer);
                    break;
            }
        }

        private static bool TryGetProperty(
            JsonElement element,
            string pascal,
            string camel,
            out JsonElement value)
        {
            return element.TryGetProperty(pascal, out value)
                || element.TryGetProperty(camel, out value);
        }

        private static void ApplyPurchaseLimitTracking(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
CREATE TABLE IF NOT EXISTS item_purchase_limits (
    account_id INTEGER NOT NULL,
    character_id INTEGER NOT NULL DEFAULT 0,
    npc_id INTEGER NOT NULL,
    item_id INTEGER NOT NULL,
    buy_count INTEGER NOT NULL DEFAULT 0,
    limit_type INTEGER NOT NULL DEFAULT 0,
    reset_type INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (account_id, character_id, npc_id, item_id, limit_type, reset_type),
    CHECK(limit_type IN (0, 1)),
    CHECK(reset_type IN (0, 1))
);";
                command.ExecuteNonQuery();
                command.CommandText = @"
CREATE INDEX IF NOT EXISTS idx_item_purchase_limits_account_reset
    ON item_purchase_limits(account_id, reset_type);";
                command.ExecuteNonQuery();
            }
        }

        private static void ApplyAuraSkinFlag(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            AddColumnIfMissing(
                connection,
                transaction,
                "characters",
                "aura_skin_flag",
                "INTEGER NOT NULL DEFAULT 0");
        }

        private static void ApplyCompressCharacterSlotHoles(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE characters SET slot_index = (
    SELECT cnt FROM (
        SELECT c1.character_id,
               (SELECT COUNT(*) FROM characters c2
                WHERE c2.account_id = c1.account_id
                  AND c2.delete_flag = 0
                  AND (c2.slot_index < c1.slot_index
                       OR (c2.slot_index = c1.slot_index AND c2.character_id <= c1.character_id))) - 1 AS cnt
        FROM characters c1
        WHERE c1.character_id = characters.character_id
    )
) WHERE delete_flag = 0;";
                command.ExecuteNonQuery();
            }
        }

        private static void ApplyDungeonEntryLimits(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            ExecuteSql(connection, transaction, @"
CREATE TABLE IF NOT EXISTS dungeon_limit_config (
    dgn_id INTEGER PRIMARY KEY CHECK (dgn_id > 0),
    scope_type TEXT NOT NULL DEFAULT 'charac'
        CHECK (scope_type IN ('charac', 'account')),
    limit_count INTEGER NOT NULL DEFAULT 0
        CHECK (limit_count >= 0 AND limit_count <= 255),
    enabled INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
    sort_order INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE INDEX IF NOT EXISTS idx_dungeon_limit_config_sort
    ON dungeon_limit_config(enabled, sort_order, dgn_id);

INSERT OR IGNORE INTO dungeon_limit_config (
    dgn_id, scope_type, limit_count, enabled, sort_order
) VALUES
    (11006, 'charac', 3, 1, 0),
    (11007, 'charac', 3, 1, 1),
    (3054, 'charac', 3, 1, 2),
    (3056, 'charac', 3, 1, 3),
    (3057, 'charac', 1, 1, 4),
    (122, 'charac', 9, 1, 5),
    (4000, 'charac', 1, 1, 6),
    (3706, 'charac', 3, 1, 7),
    (4108, 'charac', 1, 1, 8),
    (4109, 'charac', 1, 1, 9),
    (4110, 'charac', 1, 1, 10),
    (4111, 'charac', 1, 1, 11),
    (4103, 'charac', 3, 1, 12),
    (4114, 'charac', 3, 1, 13),
    (4115, 'charac', 3, 1, 14),
    (4116, 'charac', 3, 1, 15),
    (4117, 'charac', 3, 1, 16),
    (4118, 'charac', 3, 1, 17),
    (4130, 'charac', 3, 1, 18),
    (3900, 'charac', 3, 1, 19),
    (4124, 'charac', 1, 1, 20),
    (4125, 'charac', 1, 1, 21),
    (4126, 'charac', 1, 1, 22),
    (4127, 'charac', 1, 1, 23),
    (4128, 'charac', 1, 1, 24),
    (4123, 'charac', 3, 1, 25);

CREATE TABLE IF NOT EXISTS dungeon_limit_records (
    account_id INTEGER NOT NULL,
    character_id INTEGER NOT NULL DEFAULT 0 CHECK (character_id >= 0),
    dgn_id INTEGER NOT NULL,
    day_id INTEGER NOT NULL DEFAULT 0,
    current_count INTEGER NOT NULL DEFAULT 0 CHECK (current_count >= 0),
    extra_count INTEGER NOT NULL DEFAULT 0 CHECK (extra_count >= 0),
    used_count INTEGER NOT NULL DEFAULT 0 CHECK (used_count >= 0),
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (account_id, character_id, dgn_id),
    FOREIGN KEY (account_id) REFERENCES accounts(account_id) ON DELETE CASCADE,
    FOREIGN KEY (dgn_id) REFERENCES dungeon_limit_config(dgn_id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS idx_dungeon_limit_records_account_char_day
    ON dungeon_limit_records(account_id, character_id, day_id);

CREATE TABLE IF NOT EXISTS character_dimensiongate_records (
    character_id INTEGER PRIMARY KEY,
    day_id INTEGER NOT NULL DEFAULT 0,
    current_count INTEGER NOT NULL DEFAULT 0 CHECK (current_count >= 0),
    extra_count INTEGER NOT NULL DEFAULT 0 CHECK (extra_count >= 0),
    used_count INTEGER NOT NULL DEFAULT 0 CHECK (used_count >= 0),
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);");
        }

        private static void ApplyRemoveDungeonLimitNoti2EntryFlag(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            if (!TableExists(connection, transaction, "dungeon_limit_config"))
                return;

            ExecuteSql(connection, transaction, @"
CREATE TABLE dungeon_limit_config_v14 (
    dgn_id INTEGER PRIMARY KEY CHECK (dgn_id > 0),
    scope_type TEXT NOT NULL DEFAULT 'charac'
        CHECK (scope_type IN ('charac', 'account')),
    limit_count INTEGER NOT NULL DEFAULT 0
        CHECK (limit_count >= 0 AND limit_count <= 255),
    enabled INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
    sort_order INTEGER NOT NULL DEFAULT 0,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

INSERT OR REPLACE INTO dungeon_limit_config_v14 (
    dgn_id, scope_type, limit_count, enabled, sort_order, updated_at
)
SELECT dgn_id,
       CASE WHEN scope_type = 'account' THEN 'account' ELSE 'charac' END,
       MAX(0, MIN(255, limit_count)),
       CASE WHEN enabled = 0 THEN 0 ELSE 1 END,
       sort_order,
       COALESCE(updated_at, CURRENT_TIMESTAMP)
FROM dungeon_limit_config;

DROP TABLE dungeon_limit_config;
ALTER TABLE dungeon_limit_config_v14 RENAME TO dungeon_limit_config;

CREATE INDEX IF NOT EXISTS idx_dungeon_limit_config_sort
    ON dungeon_limit_config(enabled, sort_order, dgn_id);

INSERT OR IGNORE INTO dungeon_limit_config (
    dgn_id, scope_type, limit_count, enabled, sort_order
) VALUES
    (11006, 'charac', 3, 1, 0),
    (11007, 'charac', 3, 1, 1),
    (3054, 'charac', 3, 1, 2),
    (3056, 'charac', 3, 1, 3),
    (3057, 'charac', 1, 1, 4),
    (122, 'charac', 9, 1, 5),
    (4000, 'charac', 1, 1, 6),
    (3706, 'charac', 3, 1, 7),
    (4108, 'charac', 1, 1, 8),
    (4109, 'charac', 1, 1, 9),
    (4110, 'charac', 1, 1, 10),
    (4111, 'charac', 1, 1, 11),
    (4103, 'charac', 3, 1, 12),
    (4114, 'charac', 3, 1, 13),
    (4115, 'charac', 3, 1, 14),
    (4116, 'charac', 3, 1, 15),
    (4117, 'charac', 3, 1, 16),
    (4118, 'charac', 3, 1, 17),
    (4130, 'charac', 3, 1, 18),
    (3900, 'charac', 3, 1, 19),
    (4124, 'charac', 1, 1, 20),
    (4125, 'charac', 1, 1, 21),
    (4126, 'charac', 1, 1, 22),
    (4127, 'charac', 1, 1, 23),
    (4128, 'charac', 1, 1, 24),
    (4123, 'charac', 3, 1, 25);");
        }

        private static void ImportCharacterNewItems(
            SqliteConnection connection,
            SqliteTransaction transaction,
            bool shiftEquipmentSlots,
            bool dropSourceTable)
        {
            if (!TableExists(connection, transaction, "character_new_items")
                || !TableExists(connection, transaction, "character_inventory_items"))
                return;

            var slotExpression = shiftEquipmentSlots
                ? "CASE WHEN src.list_type = 3 AND src.slot_index BETWEEN 11 AND 30 THEN src.slot_index + 1 ELSE src.slot_index END"
                : "src.slot_index";

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT COUNT(*)
FROM character_new_items
WHERE owner_scope = 'character'
  AND character_id IS NOT NULL
  AND (item_core IS NULL OR length(item_core) NOT IN (82, 99));";
                var invalidCount = Convert.ToInt64(command.ExecuteScalar());
                if (invalidCount != 0)
                    throw new InvalidOperationException(
                        $"character_new_items.item_core 存在 {invalidCount} 条非82/99字节数据，无法迁移。");
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $@"
SELECT COUNT(*)
FROM (
    SELECT src.character_id, src.list_type, {slotExpression} AS mapped_slot
    FROM character_new_items src
    WHERE src.owner_scope = 'character'
      AND src.character_id IS NOT NULL
    GROUP BY src.character_id, src.list_type, mapped_slot
    HAVING COUNT(*) > 1
);";
                var duplicateCount = Convert.ToInt64(command.ExecuteScalar());
                if (duplicateCount != 0)
                    throw new InvalidOperationException(
                        $"character_new_items 迁移后存在 {duplicateCount} 组重复 character/list/slot，无法迁移。");
            }

            ExecuteSql(connection, transaction, $@"
DELETE FROM character_inventory_items
WHERE item_uid IN (
    SELECT item_uid
    FROM character_new_items
    WHERE owner_scope = 'character'
      AND character_id IS NOT NULL
)
OR EXISTS (
    SELECT 1
    FROM character_new_items src
    WHERE src.owner_scope = 'character'
      AND src.character_id IS NOT NULL
      AND src.character_id = character_inventory_items.character_id
      AND src.list_type = character_inventory_items.list_type
      AND {slotExpression} = character_inventory_items.slot_index
);

INSERT INTO character_inventory_items (
    item_uid, character_id, list_type, slot_index, item_core, created_at, updated_at
)
SELECT src.item_uid,
       src.character_id,
       src.list_type,
       {slotExpression},
       CASE
           WHEN length(src.item_core) = 82 THEN CAST(src.item_core || zeroblob(17) AS BLOB)
           ELSE src.item_core
       END,
       COALESCE(src.created_at, CURRENT_TIMESTAMP),
       COALESCE(src.updated_at, CURRENT_TIMESTAMP)
FROM character_new_items src
WHERE src.owner_scope = 'character'
  AND src.character_id IS NOT NULL;");

            if (dropSourceTable)
                ExecuteSql(connection, transaction, "DROP TABLE IF EXISTS character_new_items;");
        }

        private static void EnsureItemCoreLengths(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string tableName,
            bool nullable)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = nullable
                    ? $"SELECT COUNT(*) FROM {tableName} WHERE item_core IS NOT NULL AND length(item_core) NOT IN (82, 99);"
                    : $"SELECT COUNT(*) FROM {tableName} WHERE item_core IS NULL OR length(item_core) NOT IN (82, 99);";
                var invalidCount = Convert.ToInt64(command.ExecuteScalar());
                if (invalidCount != 0)
                    throw new InvalidOperationException($"{tableName}.item_core 存在非82/99字节数据，无法自动补零迁移。");
            }
        }

        private static void RebuildCharacterInventoryItems(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            ExecuteSql(connection, transaction, @"
CREATE TABLE character_inventory_items_v2 (
    item_uid INTEGER PRIMARY KEY AUTOINCREMENT,
    character_id INTEGER NOT NULL,
    list_type INTEGER NOT NULL,
    slot_index INTEGER NOT NULL,
    item_core BLOB NOT NULL CHECK(length(item_core) = 99),
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(character_id, list_type, slot_index),
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

INSERT INTO character_inventory_items_v2 (
    item_uid, character_id, list_type, slot_index, item_core, created_at, updated_at
)
SELECT item_uid,
       character_id,
       list_type,
       CASE
           WHEN list_type = 3 AND slot_index BETWEEN 11 AND 30 THEN slot_index + 1
           ELSE slot_index
       END,
       CASE WHEN length(item_core) = 82 THEN CAST(item_core || zeroblob(17) AS BLOB) ELSE item_core END,
       created_at,
       updated_at
FROM character_inventory_items;

DROP TABLE character_inventory_items;
ALTER TABLE character_inventory_items_v2 RENAME TO character_inventory_items;
CREATE INDEX IF NOT EXISTS idx_character_inventory_items_character_space
    ON character_inventory_items(character_id, list_type, slot_index);");
        }

        private static void RebuildAccountInventoryItems(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            ExecuteSql(connection, transaction, @"
CREATE TABLE account_inventory_items_v2 (
    item_uid INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id INTEGER NOT NULL,
    slot_index INTEGER NOT NULL,
    item_core BLOB NOT NULL CHECK(length(item_core) = 99),
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    UNIQUE(account_id, slot_index),
    FOREIGN KEY (account_id) REFERENCES accounts(account_id) ON DELETE CASCADE
);

INSERT INTO account_inventory_items_v2 (
    item_uid, account_id, slot_index, item_core, created_at, updated_at
)
SELECT item_uid,
       account_id,
       slot_index,
       CASE WHEN length(item_core) = 82 THEN CAST(item_core || zeroblob(17) AS BLOB) ELSE item_core END,
       created_at,
       updated_at
FROM account_inventory_items;

DROP TABLE account_inventory_items;
ALTER TABLE account_inventory_items_v2 RENAME TO account_inventory_items;");
        }

        private static void ShiftCharacterAppearanceBlobSlots(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            var updates = new List<KeyValuePair<int, byte[]>>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT character_id, appearance_blob
FROM characters
WHERE appearance_blob IS NOT NULL AND length(appearance_blob) > 0;";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var characterId = reader.GetInt32(0);
                        var blob = (byte[])reader.GetValue(1);
                        if (TryShiftAppearanceBlobSlots(blob, out var shifted))
                            updates.Add(new KeyValuePair<int, byte[]>(characterId, shifted));
                    }
                }
            }

            foreach (var update in updates)
            {
                using (var command = connection.CreateCommand())
                {
                    command.Transaction = transaction;
                    command.CommandText = @"
UPDATE characters
SET appearance_blob = @blob,
    updated_at = CURRENT_TIMESTAMP
WHERE character_id = @characterId;";
                    command.Parameters.AddWithValue("@blob", update.Value);
                    command.Parameters.AddWithValue("@characterId", update.Key);
                    command.ExecuteNonQuery();
                }
            }
        }

        private static bool TryShiftAppearanceBlobSlots(
            byte[] blob,
            out byte[] shifted)
        {
            shifted = null;
            if (blob == null || blob.Length == 0)
                return false;

            var count = blob[0];
            var expectedLength = 1 + count * 23;
            if (blob.Length < expectedLength)
                throw new InvalidOperationException(
                    $"characters.appearance_blob 长度不足: count={count}, length={blob.Length}, expected={expectedLength}。");

            byte[] copy = null;
            for (var index = 0; index < count; index++)
            {
                var offset = 1 + index * 23;
                var slot = blob[offset];
                if (slot < 11 || slot > 30)
                    continue;

                copy ??= (byte[])blob.Clone();
                copy[offset] = (byte)(slot + 1);
            }

            if (copy == null)
                return false;

            shifted = copy;
            return true;
        }

        private static void RebuildCharacterTitleBookItems(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            ExecuteSql(connection, transaction, @"
CREATE TABLE character_titlebook_items_v2 (
    character_id INTEGER NOT NULL,
    category INTEGER NOT NULL,
    slot_index INTEGER NOT NULL,
    item_core BLOB NOT NULL CHECK(length(item_core) = 99),
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (character_id, category, slot_index),
    FOREIGN KEY (character_id) REFERENCES characters(character_id) ON DELETE CASCADE
);

INSERT INTO character_titlebook_items_v2 (
    character_id, category, slot_index, item_core, updated_at
)
SELECT character_id,
       category,
       slot_index,
       CASE WHEN length(item_core) = 82 THEN CAST(item_core || zeroblob(17) AS BLOB) ELSE item_core END,
       updated_at
FROM character_titlebook_items;

DROP TABLE character_titlebook_items;
ALTER TABLE character_titlebook_items_v2 RENAME TO character_titlebook_items;");
        }

        private static void RebuildMailboxAttachments(
            SqliteConnection connection,
            SqliteTransaction transaction)
        {
            ExecuteSql(connection, transaction, @"
CREATE TABLE mailbox_attachments_v2 (
    attachment_id INTEGER PRIMARY KEY AUTOINCREMENT,
    message_id INTEGER NOT NULL,
    ordinal INTEGER NOT NULL DEFAULT 0,
    item_type INTEGER NOT NULL DEFAULT 0,
    source_list_type INTEGER NOT NULL DEFAULT 0,
    source_slot_index INTEGER NOT NULL DEFAULT 0,
    source_item_uid INTEGER NOT NULL DEFAULT 0,
    item_template_id INTEGER NOT NULL CHECK(item_template_id > 0),
    item_kind TEXT NOT NULL DEFAULT 'unknown',
    item_count INTEGER NOT NULL CHECK(item_count > 0),
    instance_value INTEGER NOT NULL DEFAULT 0,
    durability INTEGER NOT NULL DEFAULT 0,
    seal_flag INTEGER NOT NULL DEFAULT 0,
    option_value INTEGER NOT NULL DEFAULT 0,
    equipment_lock_id INTEGER NOT NULL DEFAULT 0,
    expire_time INTEGER NOT NULL DEFAULT 0,
    marker_16 INTEGER NOT NULL DEFAULT -1,
    pet_serial_or_handle INTEGER NOT NULL DEFAULT 0,
    extra_json TEXT NOT NULL DEFAULT '{}',
    item_core BLOB CHECK(item_core IS NULL OR length(item_core) = 99),
    detail_json TEXT NOT NULL DEFAULT '',
    claimed_flag INTEGER NOT NULL DEFAULT 0 CHECK(claimed_flag IN (0, 1, 2)),
    claimed_at TEXT,
    FOREIGN KEY (message_id) REFERENCES mailbox_messages(message_id) ON DELETE CASCADE
);

INSERT INTO mailbox_attachments_v2 (
    attachment_id, message_id, ordinal, item_type, source_list_type, source_slot_index,
    source_item_uid, item_template_id, item_kind, item_count, instance_value, durability,
    seal_flag, option_value, equipment_lock_id, expire_time, marker_16,
    pet_serial_or_handle, extra_json, item_core, detail_json, claimed_flag, claimed_at
)
SELECT attachment_id,
       message_id,
       ordinal,
       item_type,
       source_list_type,
       source_slot_index,
       source_item_uid,
       item_template_id,
       item_kind,
       item_count,
       instance_value,
       durability,
       seal_flag,
       option_value,
       equipment_lock_id,
       expire_time,
       marker_16,
       pet_serial_or_handle,
       extra_json,
       CASE
           WHEN item_core IS NULL THEN NULL
           WHEN length(item_core) = 82 THEN CAST(item_core || zeroblob(17) AS BLOB)
           ELSE item_core
       END,
       detail_json,
       claimed_flag,
       claimed_at
FROM mailbox_attachments;

DROP TABLE mailbox_attachments;
ALTER TABLE mailbox_attachments_v2 RENAME TO mailbox_attachments;
CREATE INDEX IF NOT EXISTS idx_mailbox_attachments_message
    ON mailbox_attachments(message_id, ordinal);
CREATE UNIQUE INDEX IF NOT EXISTS ux_mailbox_attachments_message_ordinal
    ON mailbox_attachments(message_id, ordinal);");
        }

        private static void ExecuteSql(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string sql)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }

        private static bool TableExists(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string tableName)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT COUNT(*)
FROM sqlite_master
WHERE type = 'table' AND name = @name;";
                command.Parameters.AddWithValue("@name", tableName);
                return Convert.ToInt32(command.ExecuteScalar()) != 0;
            }
        }

        private static bool ColumnExists(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string tableName,
            string columnName)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"PRAGMA table_info({tableName});";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        if (string.Equals(
                                reader.GetString(1),
                                columnName,
                                StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
            }

            return false;
        }

        private static void AddColumnIfMissing(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string tableName,
            string columnName,
            string columnDefinition)
        {
            if (ColumnExists(connection, transaction, tableName, columnName))
                return;

            ExecuteSql(
                connection,
                transaction,
                $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};");
        }

        internal static long ReadVersion(SqliteConnection connection)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "PRAGMA user_version;";
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }

        private static (string BaselineId, int SchemaVersion) ReadMetadata(
            SqliteConnection connection)
        {
            using (var exists = connection.CreateCommand())
            {
                exists.CommandText = @"
SELECT COUNT(*)
FROM sqlite_master
WHERE type = 'table' AND name = 'schema_metadata';";
                if (Convert.ToInt32(exists.ExecuteScalar()) == 0)
                    return (string.Empty, 0);
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT baseline_id, schema_version
FROM schema_metadata
WHERE singleton_id = 1;";
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return (string.Empty, 0);

                    return (
                        reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                        reader.IsDBNull(1) ? 0 : reader.GetInt32(1));
                }
            }
        }

        private static void SetUserVersion(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int version)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = $"PRAGMA user_version = {version};";
                command.ExecuteNonQuery();
            }
        }

        private static void WriteVersion(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int version)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
UPDATE schema_metadata
SET schema_version = @schemaVersion,
    updated_at = CURRENT_TIMESTAMP
WHERE singleton_id = 1 AND baseline_id = @baselineId;";
                command.Parameters.AddWithValue("@schemaVersion", version);
                command.Parameters.AddWithValue("@baselineId", BaselineId);
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("数据库基线元数据丢失，无法写入迁移版本。");
            }

            SetUserVersion(connection, transaction, version);
        }

        private sealed class MigrationStep
        {
            internal MigrationStep(
                int version,
                string name,
                Action<SqliteConnection, SqliteTransaction> apply)
            {
                Version = version;
                Name = name ?? throw new ArgumentNullException(nameof(name));
                Apply = apply ?? throw new ArgumentNullException(nameof(apply));
            }

            internal int Version { get; }

            internal string Name { get; }

            internal Action<SqliteConnection, SqliteTransaction> Apply { get; }
        }
    }
}
