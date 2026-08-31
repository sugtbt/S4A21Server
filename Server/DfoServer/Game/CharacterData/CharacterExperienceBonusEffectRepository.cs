using System;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.CharacterData
{
    // character_experience_bonus_effects 表（schema v15）：
    // 远古精灵秘药等 [exp bonus rate] 消耗品的活跃效果真源。
    // bonus_rate 存千分率（倍率 * 1000），expires_at 存到期 unix 秒。
    internal static class CharacterExperienceBonusEffectRepository
    {
        internal static void UpsertEffect(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int sourceItemId,
            int bonusRate,
            long expiresAtUnixSeconds)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO character_experience_bonus_effects(character_id, source_item_id, bonus_rate, expires_at)
VALUES (@cid, @iid, @rate, @exp)
ON CONFLICT(character_id) DO UPDATE SET
    source_item_id = excluded.source_item_id,
    bonus_rate = excluded.bonus_rate,
    expires_at = excluded.expires_at;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@iid", sourceItemId);
                command.Parameters.AddWithValue("@rate", bonusRate);
                command.Parameters.AddWithValue("@exp", expiresAtUnixSeconds);
                command.ExecuteNonQuery();
            }
        }

        // 读取未到期效果；返回 false 表示无活跃效果。
        internal static bool TryGetActiveEffect(
            SqliteConnection connection,
            int characterId,
            long nowUnixSeconds,
            out int sourceItemId,
            out int bonusRate,
            out long expiresAtUnixSeconds)
        {
            sourceItemId = 0;
            bonusRate = 0;
            expiresAtUnixSeconds = 0;
            if (connection == null || characterId <= 0)
                return false;

            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT source_item_id, bonus_rate, expires_at FROM character_experience_bonus_effects
WHERE character_id = @cid AND expires_at > @now;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@now", nowUnixSeconds);
                using (var reader = command.ExecuteReader())
                {
                    if (!reader.Read())
                        return false;

                    sourceItemId = reader.GetInt32(0);
                    bonusRate = reader.GetInt32(1);
                    expiresAtUnixSeconds = reader.GetInt64(2);
                    return true;
                }
            }
        }

        internal static bool TryGetActiveEffect(
            string connectionString,
            int characterId,
            long nowUnixSeconds,
            out int sourceItemId,
            out int bonusRate,
            out long expiresAtUnixSeconds)
        {
            sourceItemId = 0;
            bonusRate = 0;
            expiresAtUnixSeconds = 0;
            if (string.IsNullOrWhiteSpace(connectionString) || characterId <= 0)
                return false;

            using (var connection = new SqliteConnection(connectionString))
            {
                connection.Open();
                return TryGetActiveEffect(
                    connection,
                    characterId,
                    nowUnixSeconds,
                    out sourceItemId,
                    out bonusRate,
                    out expiresAtUnixSeconds);
            }
        }
    }
}
