using System;
using Microsoft.Data.Sqlite;
using DfoServer.Game.Inventory;
using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;

namespace DfoServer.Game.CharacterData
{
    public sealed class SqliteSubtype0FieldsRepository
    {
        private readonly string _connectionString;

        public SqliteSubtype0FieldsRepository(string databasePath, string schemaFilePath)
            : this(new GameDatabase(databasePath, schemaFilePath))
        {
        }

        public SqliteSubtype0FieldsRepository(IGameDatabase database)
        {
            _connectionString = (database ?? throw new ArgumentNullException(nameof(database)))
                .ConnectionString;
        }

        public UserInfoMinimumTailSnapshot Load(int characterId)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                return Load(conn, characterId);
            }
        }

        public static UserInfoMinimumTailSnapshot Load(SqliteConnection conn, int characterId)
        {
            using (var cmd = new SqliteCommand(@"SELECT
                c.clone_title_item_id,
                COALESCE(f.creature_field1, 0), COALESCE(f.creature_field2, 0),
                COALESCE(f.creature_field3, 0), COALESCE(f.creature_field4, 0),
                f.creature_buffer, COALESCE(f.stamina, 0), COALESCE(f.fatigue_penalty, 0),
                COALESCE(f.is_event_character, 0), COALESCE(f.pc_room_id, 65537),
                COALESCE(f.is_private_store, 0), COALESCE(f.is_premium_pc_room, 0),
                COALESCE(f.server_group_id, 0), COALESCE(f.black_count, 0), COALESCE(f.guild_level, 0),
                COALESCE(f.chaos_point, 0), COALESCE(f.disguise_kind, 0), COALESCE(f.is_disguised, 0),
                COALESCE(f.expert_job_type, 0), COALESCE(f.expert_job_exp, 0),
                COALESCE(f.is_hardcore_mode, 0), COALESCE(f.is_hardcore_dead, 0),
                COALESCE(f.hardcore_death_count, 0), COALESCE(f.user_state_bits, 3),
                COALESCE(f.chat_ban_end_time, 0), COALESCE(f.fatigue_update, 0),
                COALESCE(f.return_user_flag, 1), COALESCE(f.channel_display_mode, 0),
                COALESCE(f.channel_type, 0), COALESCE(f.channel_id, 2), COALESCE(f.mood_value, 0),
                COALESCE(f.is_return_user, 0), COALESCE(f.link_slot_enabled, 0),
                COALESCE(f.link_type_a, 0), COALESCE(f.link_type_b, 0), COALESCE(f.emotion_index, 0),
                COALESCE(f.action_byte, 0), COALESCE(f.fatigue_display_update, 0),
                COALESCE(f.costume_flag, 0), COALESCE(f.aura_flag, 0), COALESCE(f.pet_display_flag, 0),
                COALESCE(f.title_display_flag, 0), COALESCE(f.pvp_stat_a, 0),
                COALESCE(f.pvp_win_streak, 0), COALESCE(f.pvp_lose_streak, 0),
                COALESCE(f.pvp_rank_point, 0), COALESCE(f.trailing_byte, 0)
            FROM characters c
            LEFT JOIN character_subtype0_fields f ON f.character_id = c.character_id
            WHERE c.character_id=@cid", conn))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                using (var r = cmd.ExecuteReader())
                {
                    if (!r.Read()) return null;
                    var snapshot = new UserInfoMinimumTailSnapshot
                    {
                        CloneTitleItemId = (uint)r.GetInt64(0),
                        CreatureField1 = (byte)r.GetInt32(1),
                        CreatureField2 = (byte)r.GetInt32(2),
                        CreatureField3 = (byte)r.GetInt32(3),
                        CreatureField4 = (byte)r.GetInt32(4),
                        CreatureBuffer = r.IsDBNull(5) ? new byte[8] : (byte[])r.GetValue(5),
                        Stamina = (byte)r.GetInt32(6),
                        FatiguePenalty = (uint)r.GetInt64(7),
                        IsEventCharacter = (byte)r.GetInt32(8),
                        PcRoomId = (uint)r.GetInt64(9),
                        IsPrivateStore = (byte)r.GetInt32(10),
                        IsPremiumPcRoom = (byte)r.GetInt32(11),
                        ServerGroupId = (byte)r.GetInt32(12),
                        BlackCount = (uint)r.GetInt64(13),
                        GuildLevel = (byte)r.GetInt32(14),
                        ChaosPoint = (uint)r.GetInt64(15),
                        DisguiseKind = (byte)r.GetInt32(16),
                        IsDisguised = (byte)r.GetInt32(17),
                        ExpertJobType = (byte)r.GetInt32(18),
                        ExpertJobExp = ReadExpertJobExp(r.GetInt64(19)),
                        IsHardcoreMode = (byte)r.GetInt32(20),
                        IsHardcoreDead = (byte)r.GetInt32(21),
                        HardcoreDeathCount = (ushort)r.GetInt32(22),
                        UserStateBits = (byte)r.GetInt32(23),
                        ChatBanEndTime = (uint)r.GetInt64(24),
                        FatigueUpdate = (ushort)r.GetInt32(25),
                        ReturnUserFlag = (byte)r.GetInt32(26),
                        ChannelDisplayMode = (ushort)r.GetInt32(27),
                        ChannelType = (byte)r.GetInt32(28),
                        ChannelId = (ushort)r.GetInt32(29),
                        MoodValue = (ushort)r.GetInt32(30),
                        IsReturnUser = (byte)r.GetInt32(31),
                        LinkSlotEnabled = (byte)r.GetInt32(32),
                        LinkTypeA = (byte)r.GetInt32(33),
                        LinkTypeB = (byte)r.GetInt32(34),
                        EmotionIndex = (ushort)r.GetInt32(35),
                        ActionByte = (byte)r.GetInt32(36),
                        FatigueDisplayUpdate = (ushort)r.GetInt32(37),
                        CostumeFlag = (byte)r.GetInt32(38),
                        AuraFlag = (byte)r.GetInt32(39),
                        PetDisplayFlag = (byte)r.GetInt32(40),
                        TitleDisplayFlag = (byte)r.GetInt32(41),
                        PvpStatA = (uint)r.GetInt64(42),
                        PvpWinStreak = (byte)r.GetInt32(43),
                        PvpLoseStreak = (byte)r.GetInt32(44),
                        PvpRankPoint = (uint)r.GetInt64(45),
                        TrailingByte = (byte)r.GetInt32(46),
                    };
                    RefreshDynamicTailFields(conn, characterId, snapshot);
                    return snapshot;
                }
            }
        }

        public void RefreshDynamicTailFields(int characterId, UserInfoMinimumTailSnapshot snapshot)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                RefreshDynamicTailFields(conn, characterId, snapshot);
            }
        }

        internal static void SaveUserStateBits(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            byte value)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"INSERT INTO character_subtype0_fields(character_id, user_state_bits)
                    VALUES(@cid, @value)
                    ON CONFLICT(character_id) DO UPDATE SET user_state_bits=excluded.user_state_bits";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@value", (int)value);
                command.ExecuteNonQuery();
            }
        }

        public static void RefreshDynamicTailFields(SqliteConnection conn, int characterId, UserInfoMinimumTailSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            ClearDynamicTailFields(snapshot);
            LoadNameTagFields(conn, characterId, snapshot);
            var projectionBuilder = new Noti2InventoryProjectionBuilder();
            if (InventoryContext.TryGetLease(characterId, out var lease))
            {
                lock (lease.SyncRoot)
                    projectionBuilder.ApplySubtype0TailDynamicFields(lease.Inventory, snapshot);
            }
        }

        private static void ClearDynamicTailFields(UserInfoMinimumTailSnapshot snapshot)
        {
            snapshot.Forging = 0;
            snapshot.NameTagItemId = 0;
            snapshot.NameTagExpireTime = 0;
            snapshot.EquippedCreatureItemId = 0;
            snapshot.EquippedCreatureNameBytes = new byte[0];
            snapshot.EquippedCreatureAliveState = 0;
            snapshot.GuildNameBytes = new byte[0];
        }

        private static void LoadNameTagFields(SqliteConnection conn, int characterId, UserInfoMinimumTailSnapshot snapshot)
        {
            var state = NameTagStateRepository.Load(conn, characterId);
            if (!state.IsActive())
                return;

            snapshot.NameTagItemId = (uint)state.ItemId;
            snapshot.NameTagExpireTime = (uint)state.ExpireTime;
        }

        public static void Save(SqliteConnection conn, int characterId, UserInfoMinimumTailSnapshot s)
        {
            using (var cmd = new SqliteCommand(@"INSERT OR REPLACE INTO character_subtype0_fields(
                character_id,
                name_tag_item_id, creature_field1, creature_field2, creature_field3, creature_field4,
                creature_buffer, stamina, fatigue_penalty, is_event_character, pc_room_id,
                is_private_store, is_premium_pc_room, server_group_id, black_count, guild_level,
                chaos_point, disguise_kind, is_disguised, expert_job_type, expert_job_exp,
                is_hardcore_mode, is_hardcore_dead, hardcore_death_count, user_state_bits, chat_ban_end_time,
                fatigue_update, return_user_flag, channel_display_mode, channel_type, channel_id, mood_value,
                is_return_user, link_slot_enabled, link_type_a, link_type_b, emotion_index,
                action_byte, fatigue_display_update, costume_flag, aura_flag, pet_display_flag,
                title_display_flag, pvp_stat_a, pvp_win_streak, pvp_lose_streak, pvp_rank_point,
                trailing_byte
            ) VALUES(
                @cid, @uvp, @cf1, @cf2, @cf3, @cf4, @cb, @sta, @fp, @iec, @pcr,
                @ips, @ippr, @sgi, @bc, @gl, @cp, @dk, @id2, @ejt, @eje,
                @ihm, @ihd, @hdc, @usb, @cbe, @fu, @ruf, @cdm, @ct, @chid, @mv,
                @iru, @lse, @lta, @ltb, @ei, @ab, @fdu, @cof, @auf, @pdf,
                @tdf, @psa, @pws, @pls, @prp, @tb
            )", conn))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.Parameters.AddWithValue("@uvp", (long)s.CloneTitleItemId);
                cmd.Parameters.AddWithValue("@cf1", (int)s.CreatureField1);
                cmd.Parameters.AddWithValue("@cf2", (int)s.CreatureField2);
                cmd.Parameters.AddWithValue("@cf3", (int)s.CreatureField3);
                cmd.Parameters.AddWithValue("@cf4", (int)s.CreatureField4);
                cmd.Parameters.AddWithValue("@cb", s.CreatureBuffer ?? new byte[8]);
                cmd.Parameters.AddWithValue("@sta", (int)s.Stamina);
                cmd.Parameters.AddWithValue("@fp", (long)s.FatiguePenalty);
                cmd.Parameters.AddWithValue("@iec", (int)s.IsEventCharacter);
                cmd.Parameters.AddWithValue("@pcr", (long)s.PcRoomId);
                cmd.Parameters.AddWithValue("@ips", (int)s.IsPrivateStore);
                cmd.Parameters.AddWithValue("@ippr", (int)s.IsPremiumPcRoom);
                cmd.Parameters.AddWithValue("@sgi", (int)s.ServerGroupId);
                cmd.Parameters.AddWithValue("@bc", (long)s.BlackCount);
                cmd.Parameters.AddWithValue("@gl", (int)s.GuildLevel);
                cmd.Parameters.AddWithValue("@cp", (long)s.ChaosPoint);
                cmd.Parameters.AddWithValue("@dk", (int)s.DisguiseKind);
                cmd.Parameters.AddWithValue("@id2", (int)s.IsDisguised);
                cmd.Parameters.AddWithValue("@ejt", (int)s.ExpertJobType);
                cmd.Parameters.AddWithValue("@eje", (long)s.ExpertJobExp);
                cmd.Parameters.AddWithValue("@ihm", (int)s.IsHardcoreMode);
                cmd.Parameters.AddWithValue("@ihd", (int)s.IsHardcoreDead);
                cmd.Parameters.AddWithValue("@hdc", (int)s.HardcoreDeathCount);
                cmd.Parameters.AddWithValue("@usb", (int)s.UserStateBits);
                cmd.Parameters.AddWithValue("@cbe", (long)s.ChatBanEndTime);
                cmd.Parameters.AddWithValue("@fu", (int)s.FatigueUpdate);
                cmd.Parameters.AddWithValue("@ruf", (int)s.ReturnUserFlag);
                cmd.Parameters.AddWithValue("@cdm", (int)s.ChannelDisplayMode);
                cmd.Parameters.AddWithValue("@ct", (int)s.ChannelType);
                cmd.Parameters.AddWithValue("@chid", (int)s.ChannelId);
                cmd.Parameters.AddWithValue("@mv", (int)s.MoodValue);
                cmd.Parameters.AddWithValue("@iru", (int)s.IsReturnUser);
                cmd.Parameters.AddWithValue("@lse", (int)s.LinkSlotEnabled);
                cmd.Parameters.AddWithValue("@lta", (int)s.LinkTypeA);
                cmd.Parameters.AddWithValue("@ltb", (int)s.LinkTypeB);
                cmd.Parameters.AddWithValue("@ei", (int)s.EmotionIndex);
                cmd.Parameters.AddWithValue("@ab", (int)s.ActionByte);
                cmd.Parameters.AddWithValue("@fdu", (int)s.FatigueDisplayUpdate);
                cmd.Parameters.AddWithValue("@cof", (int)s.CostumeFlag);
                cmd.Parameters.AddWithValue("@auf", (int)s.AuraFlag);
                cmd.Parameters.AddWithValue("@pdf", (int)s.PetDisplayFlag);
                cmd.Parameters.AddWithValue("@tdf", (int)s.TitleDisplayFlag);
                cmd.Parameters.AddWithValue("@psa", (long)s.PvpStatA);
                cmd.Parameters.AddWithValue("@pws", (int)s.PvpWinStreak);
                cmd.Parameters.AddWithValue("@pls", (int)s.PvpLoseStreak);
                cmd.Parameters.AddWithValue("@prp", (long)s.PvpRankPoint);
                cmd.Parameters.AddWithValue("@tb", (int)s.TrailingByte);
                cmd.ExecuteNonQuery();
            }
        }

        internal static bool ResetExpertJobInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
            => SetExpertJobStateInTransaction(
                connection,
                transaction,
                characterId,
                0,
                0);

        internal static bool SetExpertJobInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            byte expertJobType)
            => expertJobType > 0
                && SetExpertJobStateInTransaction(
                    connection,
                    transaction,
                    characterId,
                    expertJobType,
                    0);

        internal static bool ResetStaminaInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId)
        {
            if (connection == null || transaction == null || characterId <= 0)
                return false;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO character_subtype0_fields(character_id, stamina, fatigue_penalty)
VALUES(@cid, 0, 0)
ON CONFLICT(character_id) DO UPDATE SET
    stamina=0,
    fatigue_penalty=0;";
                command.Parameters.AddWithValue("@cid", characterId);
                return command.ExecuteNonQuery() > 0;
            }
        }

        private static bool SetExpertJobStateInTransaction(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            byte expertJobType,
            long expertJobExperience)
        {
            if (connection == null || transaction == null || characterId <= 0)
                return false;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
INSERT INTO character_subtype0_fields (character_id, expert_job_type, expert_job_exp)
VALUES (@cid, @type, @exp)
ON CONFLICT(character_id) DO UPDATE SET
    expert_job_type=excluded.expert_job_type,
    expert_job_exp=excluded.expert_job_exp;";
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@type", (int)expertJobType);
                command.Parameters.AddWithValue("@exp", expertJobExperience);
                return command.ExecuteNonQuery() == 1;
            }
        }

        // 历史行可能为负数，读出时按 0。
        private static uint ReadExpertJobExp(long stored)
        {
            if (stored < 0)
                return 0;
            return stored > uint.MaxValue ? uint.MaxValue : (uint)stored;
        }

    }
}
