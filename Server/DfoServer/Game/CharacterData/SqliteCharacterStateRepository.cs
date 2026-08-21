using DfoServer.Game.SelectCharacter;
using DfoServer.Infrastructure;
using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.CharacterData
{
    public sealed class SqliteCharacterStateRepository : ICharacterStateRepository
    {
        private readonly string _connectionString;
        private readonly CharacterAchievementRepository _achievement;
        private readonly CharacterItemValueRepository _itemValue;
        private readonly CharacterMiscStateRepository _miscState;

        public SqliteCharacterStateRepository(string databasePath, string schemaFilePath)
            : this(new GameDatabase(databasePath, schemaFilePath))
        {
        }

        public SqliteCharacterStateRepository(IGameDatabase database)
        {
            _connectionString = (database ?? throw new ArgumentNullException(nameof(database)))
                .ConnectionString;
            _achievement = new CharacterAchievementRepository(_connectionString);
            _itemValue = new CharacterItemValueRepository(_connectionString);
            _miscState = new CharacterMiscStateRepository(_connectionString);
        }



        public void LoadFlags(int characterId, SelectCharacterInitializationSnapshot snapshot)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(
                    @"SELECT pc_room_state,
                             champion_break_key_id, champion_break_mode, champion_break_value,
                             character_option_blob, charac_invisible_falgs_payload_len,
                             racing_dungeon_current_enter_count,
                             ack_char_slot_index, ack_fatigue_battery, ack_fatigue_grownup_buff,
                             ack_trade_punish_flag, ack_extra_field_86jp,
                             ack_tutorial_skipable
                      FROM character_init_flags WHERE character_id = @cid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (!reader.Read())
                            return;
                        snapshot.PcRoomPlayTimeState = (byte)reader.GetInt32(0);

                        snapshot.ChampionBreakSystem.KeyId = reader.GetInt32(1);
                        snapshot.ChampionBreakSystem.Mode = (byte)reader.GetInt32(2);
                        snapshot.ChampionBreakSystem.Value = reader.GetInt32(3);

                        snapshot.CharacterOptionBlob = reader.IsDBNull(4) ? null : (byte[])reader[4];
                        snapshot.CharacInvisibleFalgsPayloadLen = reader.IsDBNull(5) ? 0u : (uint)reader.GetInt64(5);
                        snapshot.RacingDungeonCurrentEnterCount = reader.IsDBNull(6) ? 0u : (uint)reader.GetInt64(6);

                        snapshot.AckCharSlotIndex = reader.IsDBNull(7) ? (byte)0 : (byte)reader.GetInt32(7);
                        snapshot.AckFatigueBattery = reader.IsDBNull(8) ? (ushort)0 : (ushort)reader.GetInt32(8);
                        snapshot.AckFatigueGrownUpBuff = reader.IsDBNull(9) ? (ushort)0 : (ushort)reader.GetInt32(9);
                        snapshot.AckTradePunishFlag = reader.IsDBNull(10) ? (byte)0 : (byte)reader.GetInt32(10);
                        snapshot.AckExtraField86JP = reader.IsDBNull(11) ? (ushort)0 : (ushort)reader.GetInt32(11);
                        snapshot.AckTutorialSkipable = reader.IsDBNull(12) ? (byte)0 : (byte)reader.GetInt32(12);
                    }
                }

                snapshot.GrowthWeaponStageIds.Clear();
                using (var cmd = new SqliteCommand(
                    "SELECT stage_id FROM character_growth_weapon_stages WHERE character_id = @cid ORDER BY sort_order", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            snapshot.GrowthWeaponStageIds.Add((byte)reader.GetInt32(0));
                    }
                }



                snapshot.PvpMissions.Clear();
                using (var cmd = new SqliteCommand(
                    "SELECT mission_id, progress_value FROM character_pvp_missions WHERE character_id = @cid ORDER BY sort_order", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            snapshot.PvpMissions.Add(new PvpMissionEntrySnapshot
                            {
                                MissionId = (uint)reader.GetInt64(0),
                                ProgressValue = (uint)reader.GetInt64(1),
                            });
                        }
                    }
                }

                snapshot.DungeonPermissions.Clear();
                using (var cmd = new SqliteCommand(
                    "SELECT dungeon_id, clear_state FROM character_dungeon_permissions WHERE character_id = @cid ORDER BY sort_order", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            snapshot.DungeonPermissions.Add(new DungeonPermissionEntrySnapshot
                            {
                                DungeonId = (ushort)reader.GetInt32(0),
                                ClearState = (byte)reader.GetInt32(1),
                            });
                        }
                    }
                }

                snapshot.HotkeyConfigSlots.Clear();
                using (var cmd = new SqliteCommand(
                    "SELECT hotkey_value FROM character_hotkey_slots WHERE character_id = @cid ORDER BY slot_index", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            snapshot.HotkeyConfigSlots.Add((ushort)reader.GetInt32(0));
                    }
                }

                snapshot.CharacInvisibleFalgs.Clear();
                foreach (var entry in Game.Quests.QuestRepository.LoadAllFlagEntries(conn, null, characterId))
                {
                    snapshot.CharacInvisibleFalgs.Add(new CharacInvisibleFalgEntrySnapshot
                    {
                        SlotIndex = (ushort)entry.Key,
                        FlagValue = (byte)entry.Value,
                    });
                }

                snapshot.RacingDungeonGroups.Clear();
                var racingGroupsByIndex = new Dictionary<int, RacingDungeonGroupSnapshot>();
                using (var cmd = new SqliteCommand(
                    "SELECT group_index, group_id FROM character_daily_challenge_groups WHERE character_id = @cid ORDER BY group_index", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var groupIndex = reader.GetInt32(0);
                            var group = new RacingDungeonGroupSnapshot { GroupId = (uint)reader.GetInt64(1) };
                            racingGroupsByIndex[groupIndex] = group;
                            snapshot.RacingDungeonGroups.Add(group);
                        }
                    }
                }
                using (var cmd = new SqliteCommand(
                    "SELECT group_index, entry_index, track_like_id, value_a, value_b FROM character_daily_challenge_entries WHERE character_id = @cid ORDER BY group_index, entry_index", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var groupIndex = reader.GetInt32(0);
                            if (!racingGroupsByIndex.TryGetValue(groupIndex, out var group))
                                continue;
                            group.Entries.Add(new RacingDungeonEntrySnapshot
                            {
                                TrackLikeId = (uint)reader.GetInt64(2),
                                ValueA = (uint)reader.GetInt64(3),
                                ValueB = (uint)reader.GetInt64(4),
                            });
                        }
                    }
                }

                snapshot.DailyChallengeRewardClaimFlags = new byte[
                    SelectCharacterInitializationSnapshot.DailyChallengeClaimFlagCount];
                using (var cmd = new SqliteCommand(
                    "SELECT group_index FROM character_daily_challenge_claims WHERE character_id = @cid ORDER BY group_index", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var groupIndex = reader.GetInt32(0);
                            if (groupIndex >= 0 && groupIndex < snapshot.DailyChallengeRewardClaimFlags.Length)
                                snapshot.DailyChallengeRewardClaimFlags[groupIndex] = 1;
                        }
                    }
                }

                snapshot.RacingDungeonTailIds.Clear();
                using (var cmd = new SqliteCommand(
                    "SELECT id_value FROM character_daily_challenge_tail_ids WHERE character_id = @cid ORDER BY sort_order", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                            snapshot.RacingDungeonTailIds.Add((uint)reader.GetInt64(0));
                    }
                }
            }
        }

        public bool UpsertDungeonPermission(int characterId, int dungeonId, byte newClearState)
        {
            if (dungeonId <= 0
                || dungeonId > ushort.MaxValue
                || newClearState == 0)
                return false;

            ApplyDungeonPermissionBatch(
                characterId,
                new[]
                {
                    new DungeonPermissionEntrySnapshot
                    {
                        DungeonId = (ushort)dungeonId,
                        ClearState = newClearState,
                    },
                },
                out var changes);
            return changes.Count > 0;
        }

        internal List<DungeonPermissionEntrySnapshot>
            ApplyDungeonPermissionBatch(
                int characterId,
                IReadOnlyCollection<DungeonPermissionEntrySnapshot> updates,
                out List<DungeonPermissionEntrySnapshot> changes)
        {
            if (characterId <= 0)
                throw new ArgumentOutOfRangeException(nameof(characterId));
            if (updates == null)
                throw new ArgumentNullException(nameof(updates));

            var normalized = new List<DungeonPermissionEntrySnapshot>();
            var indexes = new Dictionary<ushort, int>();
            foreach (var update in updates)
            {
                if (update == null
                    || update.DungeonId == 0
                    || update.ClearState == 0)
                {
                    throw new ArgumentException(
                        "Dungeon permission updates require non-zero dungeon and state values.",
                        nameof(updates));
                }

                if (indexes.TryGetValue(update.DungeonId, out var index))
                {
                    if (normalized[index].ClearState < update.ClearState)
                        normalized[index].ClearState = update.ClearState;
                    continue;
                }

                indexes[update.DungeonId] = normalized.Count;
                normalized.Add(new DungeonPermissionEntrySnapshot
                {
                    DungeonId = update.DungeonId,
                    ClearState = update.ClearState,
                });
            }

            changes = new List<DungeonPermissionEntrySnapshot>();
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction(deferred: false))
                {
                    foreach (var update in normalized)
                    {
                        if (!UpsertDungeonPermission(
                                conn,
                                tx,
                                characterId,
                                update.DungeonId,
                                update.ClearState))
                        {
                            continue;
                        }

                        changes.Add(new DungeonPermissionEntrySnapshot
                        {
                            DungeonId = update.DungeonId,
                            ClearState = update.ClearState,
                        });
                    }

                    var snapshot = LoadDungeonPermissions(
                        conn,
                        tx,
                        characterId);
                    tx.Commit();
                    return snapshot;
                }
            }
        }

        public List<DungeonPermissionEntrySnapshot> LoadDungeonPermissions(
            int characterId)
        {
            var result = new List<DungeonPermissionEntrySnapshot>();
            if (characterId <= 0)
                return result;

            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                return LoadDungeonPermissions(
                    conn,
                    transaction: null,
                    characterId);
            }
        }

        private static bool UpsertDungeonPermission(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int characterId,
            int dungeonId,
            byte newClearState)
        {
            var currentState = 0;
            var existingRows = 0;
            using (var command = new SqliteCommand(@"
SELECT COUNT(*), COALESCE(MAX(clear_state), 0)
FROM character_dungeon_permissions
WHERE character_id = @cid AND dungeon_id = @did;",
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                command.Parameters.AddWithValue("@did", dungeonId);
                using (var reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        existingRows = reader.GetInt32(0);
                        currentState = reader.GetInt32(1);
                    }
                }
            }

            if (currentState >= newClearState)
                return false;

            if (existingRows > 0)
            {
                using (var command = new SqliteCommand(@"
UPDATE character_dungeon_permissions
SET clear_state = @state
WHERE character_id = @cid AND dungeon_id = @did;",
                    connection,
                    transaction))
                {
                    command.Parameters.AddWithValue("@state", (int)newClearState);
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@did", dungeonId);
                    command.ExecuteNonQuery();
                }
            }
            else
            {
                using (var command = new SqliteCommand(@"
INSERT INTO character_dungeon_permissions
    (character_id, sort_order, dungeon_id, clear_state)
VALUES
    (@cid,
     (SELECT COALESCE(MAX(sort_order), 0) + 1
      FROM character_dungeon_permissions
      WHERE character_id = @cid),
     @did,
     @state);",
                    connection,
                    transaction))
                {
                    command.Parameters.AddWithValue("@cid", characterId);
                    command.Parameters.AddWithValue("@did", dungeonId);
                    command.Parameters.AddWithValue("@state", (int)newClearState);
                    command.ExecuteNonQuery();
                }
            }

            return true;
        }

        private static List<DungeonPermissionEntrySnapshot>
            LoadDungeonPermissions(
                SqliteConnection connection,
                SqliteTransaction transaction,
                int characterId)
        {
            var result = new List<DungeonPermissionEntrySnapshot>();
            using (var command = new SqliteCommand(@"
SELECT dungeon_id, clear_state
FROM character_dungeon_permissions
WHERE character_id = @cid
ORDER BY sort_order;",
                connection,
                transaction))
            {
                command.Parameters.AddWithValue("@cid", characterId);
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        result.Add(new DungeonPermissionEntrySnapshot
                        {
                            DungeonId = (ushort)reader.GetInt32(0),
                            ClearState = (byte)reader.GetInt32(1),
                        });
                    }
                }
            }

            return result;
        }

        public void SaveFlags(int characterId, SelectCharacterInitializationSnapshot snapshot)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    using (var cmd = new SqliteCommand(
                        @"INSERT INTO character_init_flags
                          (character_id, pc_room_state,
                           champion_break_key_id, champion_break_mode, champion_break_value,
                           character_option_blob, charac_invisible_falgs_payload_len,
                           racing_dungeon_current_enter_count,
                           ack_char_slot_index, ack_fatigue_battery, ack_fatigue_grownup_buff,
                           ack_trade_punish_flag, ack_extra_field_86jp,
                           ack_tutorial_skipable)
                          VALUES (@cid, @pcr,
                                  @champKey, @champMode, @champValue,
                                  @charOpt, @ciplen,
                                  @rdcc,
                                  @ackSlot, @ackFatBat, @ackFatGrown,
                                  @ackTrade, @ackExtra86,
                                  @ackTutSkip)
                          ON CONFLICT(character_id) DO UPDATE SET
                            pc_room_state=excluded.pc_room_state,
                            champion_break_key_id=excluded.champion_break_key_id,
                            champion_break_mode=excluded.champion_break_mode,
                            champion_break_value=excluded.champion_break_value,
                            character_option_blob=COALESCE(excluded.character_option_blob, character_init_flags.character_option_blob),
                            charac_invisible_falgs_payload_len=excluded.charac_invisible_falgs_payload_len,
                            racing_dungeon_current_enter_count=excluded.racing_dungeon_current_enter_count,
                            ack_char_slot_index=excluded.ack_char_slot_index,
                            ack_fatigue_battery=excluded.ack_fatigue_battery,
                            ack_fatigue_grownup_buff=excluded.ack_fatigue_grownup_buff,
                            ack_trade_punish_flag=excluded.ack_trade_punish_flag,
                            ack_extra_field_86jp=excluded.ack_extra_field_86jp,
                            ack_tutorial_skipable=excluded.ack_tutorial_skipable", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.Parameters.AddWithValue("@pcr", (int)snapshot.PcRoomPlayTimeState);
                        cmd.Parameters.AddWithValue("@champKey", snapshot.ChampionBreakSystem.KeyId);
                        cmd.Parameters.AddWithValue("@champMode", (int)snapshot.ChampionBreakSystem.Mode);
                        cmd.Parameters.AddWithValue("@champValue", snapshot.ChampionBreakSystem.Value);
                        cmd.Parameters.AddWithValue("@charOpt", (object)snapshot.CharacterOptionBlob ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@ciplen", (long)snapshot.CharacInvisibleFalgsPayloadLen);
                        cmd.Parameters.AddWithValue("@rdcc", (long)snapshot.RacingDungeonCurrentEnterCount);
                        cmd.Parameters.AddWithValue("@ackSlot", (int)snapshot.AckCharSlotIndex);
                        cmd.Parameters.AddWithValue("@ackFatBat", (int)snapshot.AckFatigueBattery);
                        cmd.Parameters.AddWithValue("@ackFatGrown", (int)snapshot.AckFatigueGrownUpBuff);
                        cmd.Parameters.AddWithValue("@ackTrade", (int)snapshot.AckTradePunishFlag);
                        cmd.Parameters.AddWithValue("@ackExtra86", (int)snapshot.AckExtraField86JP);
                        cmd.Parameters.AddWithValue("@ackTutSkip", (int)snapshot.AckTutorialSkipable);
                        cmd.ExecuteNonQuery();
                    }

                    using (var cmd = new SqliteCommand("DELETE FROM character_growth_weapon_stages WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }

                    var stages = snapshot.GrowthWeaponStageIds;
                    for (int i = 0; i < stages.Count; i++)
                    {
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_growth_weapon_stages (character_id, sort_order, stage_id) VALUES (@cid, @ord, @sid)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@ord", i);
                            cmd.Parameters.AddWithValue("@sid", (int)stages[i]);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    using (var cmd = new SqliteCommand("DELETE FROM character_pvp_missions WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }

                    var missions = snapshot.PvpMissions;
                    for (int i = 0; i < missions.Count; i++)
                    {
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_pvp_missions (character_id, sort_order, mission_id, progress_value) VALUES (@cid, @ord, @mid, @pv)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@ord", i);
                            cmd.Parameters.AddWithValue("@mid", (long)missions[i].MissionId);
                            cmd.Parameters.AddWithValue("@pv", (long)missions[i].ProgressValue);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    using (var cmd = new SqliteCommand("DELETE FROM character_dungeon_permissions WHERE character_id = @cid", conn, tx))
                    {
                        cmd.Parameters.AddWithValue("@cid", characterId);
                        cmd.ExecuteNonQuery();
                    }

                    var dungeons = snapshot.DungeonPermissions;
                    for (int i = 0; i < dungeons.Count; i++)
                    {
                        using (var cmd = new SqliteCommand(
                            "INSERT INTO character_dungeon_permissions (character_id, sort_order, dungeon_id, clear_state) VALUES (@cid, @ord, @did, @cs)", conn, tx))
                        {
                            cmd.Parameters.AddWithValue("@cid", characterId);
                            cmd.Parameters.AddWithValue("@ord", i);
                            cmd.Parameters.AddWithValue("@did", (int)dungeons[i].DungeonId);
                            cmd.Parameters.AddWithValue("@cs", (int)dungeons[i].ClearState);
                            cmd.ExecuteNonQuery();
                        }
                    }

                    ReplaceHotkeySlots(conn, tx, characterId, snapshot.HotkeyConfigSlots);

                    Game.Quests.QuestRepository.ReplaceAllClearedFlags(conn, tx, characterId,
                        snapshot.CharacInvisibleFalgs.ConvertAll(
                            entry => new KeyValuePair<int, int>(entry.SlotIndex, entry.FlagValue)));

                    // DailyChallengeRepository is the ledger owner. Generic
                    // init-flag saves (notably tutorial flag 31) must not
                    // replace entries: deleting them cascades entry claims and
                    // source-event dedup records from schema v6.

                    tx.Commit();
                }
            }
        }

        public void SaveCharacterOption(int characterId, byte[] body)
        {
            if (characterId <= 0 || body == null)
                return;

            var copy = new byte[body.Length];
            Buffer.BlockCopy(body, 0, copy, 0, body.Length);

            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(@"
INSERT INTO character_init_flags (character_id, character_option_blob)
VALUES (@cid, @body)
ON CONFLICT(character_id) DO UPDATE SET character_option_blob = @body", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@body", copy);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public void SaveMoodValue(int characterId, ushort moodValue)
        {
            if (characterId <= 0)
                return;

            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand(@"
INSERT INTO character_subtype0_fields (character_id, mood_value)
VALUES (@cid, @mood)
ON CONFLICT(character_id) DO UPDATE SET
    mood_value = @mood", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@mood", (int)moodValue);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public void SaveHotkeyConfig(int characterId, byte[] hotkeys)
        {
            if (characterId <= 0 || hotkeys == null)
                return;

            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var tx = conn.BeginTransaction())
                {
                    ReplaceHotkeySlots(conn, tx, characterId, DecodeHotkeySlots(hotkeys));
                    tx.Commit();
                }
            }
        }

        private static List<ushort> DecodeHotkeySlots(byte[] hotkeys)
        {
            var slots = new List<ushort>();
            if (hotkeys == null)
                return slots;

            for (var offset = 0; offset + 1 < hotkeys.Length; offset += 2)
                slots.Add(BitConverter.ToUInt16(hotkeys, offset));
            return slots;
        }

        private static void ReplaceHotkeySlots(
            SqliteConnection conn,
            SqliteTransaction tx,
            int characterId,
            IReadOnlyList<ushort> slots)
        {
            using (var cmd = new SqliteCommand("DELETE FROM character_hotkey_slots WHERE character_id = @cid", conn, tx))
            {
                cmd.Parameters.AddWithValue("@cid", characterId);
                cmd.ExecuteNonQuery();
            }

            slots = slots ?? Array.Empty<ushort>();
            for (var i = 0; i < slots.Count; i++)
            {
                using (var cmd = new SqliteCommand(
                    "INSERT INTO character_hotkey_slots (character_id, slot_index, hotkey_value) VALUES (@cid, @si, @hv)", conn, tx))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    cmd.Parameters.AddWithValue("@si", i);
                    cmd.Parameters.AddWithValue("@hv", (int)slots[i]);
                    cmd.ExecuteNonQuery();
                }
            }
        }

        public bool HasFlags(int characterId)
        {
            using (var conn = new SqliteConnection(_connectionString))
            {
                conn.Open();
                using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM character_init_flags WHERE character_id = @cid", conn))
                {
                    cmd.Parameters.AddWithValue("@cid", characterId);
                    return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
                }
            }
        }



        public void InitializeEmptyState(int characterId)
        {
            SeedFromSnapshotCore(characterId, new SelectCharacterInitializationSnapshot());
        }

        private void SeedFromSnapshotCore(
            int characterId,
            SelectCharacterInitializationSnapshot snapshot)
        {
            if (!HasFlags(characterId))
                SaveFlags(characterId, snapshot);

            _itemValue.SaveItemValueListIfEmpty(characterId, "cooltime", snapshot.CooltimeItems);
            _itemValue.SaveItemValueListIfEmpty(characterId, "effect", snapshot.EffectItems);

            if (_achievement.LoadAchievementComplete(characterId).Entries.Count == 0 && snapshot.AchievementComplete.Entries.Count > 0)
                _achievement.SaveAchievementComplete(characterId, snapshot.AchievementComplete);

            if (_miscState.LoadUnknown725(characterId).Count == 0 && snapshot.Unknown725Packets.Count > 0)
                _miscState.SaveUnknown725(characterId, snapshot.Unknown725Packets);

            if (_miscState.LoadUnknown730(characterId).Entries.Count == 0 && snapshot.Unknown730.Entries.Count > 0)
                _miscState.SaveUnknown730(characterId, snapshot.Unknown730);
        }

        // Compatibility-only snapshot backfill. Formal character creation uses InitializeEmptyState.
        internal void SeedFromSnapshot(int characterId, SelectCharacterInitializationSnapshot snapshot)
        {
            if (snapshot == null)
                throw new ArgumentNullException(nameof(snapshot));

            SeedFromSnapshotCore(characterId, snapshot);
        }

        public void LoadAll(int characterId, SelectCharacterInitializationSnapshot snapshot)
        {
            LoadFlags(characterId, snapshot);

            var cooltime = _itemValue.LoadItemValueList(characterId, "cooltime");
            snapshot.CooltimeItems.Clear();
            snapshot.CooltimeItems.AddRange(cooltime);

            var effect = _itemValue.LoadItemValueList(characterId, "effect");
            snapshot.EffectItems.Clear();
            snapshot.EffectItems.AddRange(effect);

            snapshot.AchievementComplete = _achievement.LoadAchievementComplete(characterId);

            var u725 = _miscState.LoadUnknown725(characterId);
            snapshot.Unknown725Packets.Clear();
            snapshot.Unknown725Packets.AddRange(u725);

            snapshot.Unknown730 = _miscState.LoadUnknown730(characterId);
        }

    }
}
