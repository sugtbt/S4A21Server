using System;
using System.Collections.Generic;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Events
{
    internal sealed class GameEventRepository
    {
        private readonly IGameDatabase _database;

        internal GameEventRepository(IGameDatabase database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        internal bool IsEnabled(int eventId)
        {
            using (var connection = _database.OpenConnection())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT state
FROM game_event_state
WHERE event_id=@eventId;";
                command.Parameters.AddWithValue("@eventId", eventId);
                var value = command.ExecuteScalar();
                return value != null
                    && value != DBNull.Value
                    && Convert.ToInt32(value) != 0;
            }
        }

        internal static bool IsEnabled(
            SqliteConnection connection,
            SqliteTransaction transaction,
            int eventId)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
SELECT state
FROM game_event_state
WHERE event_id=@eventId;";
                command.Parameters.AddWithValue("@eventId", eventId);
                var value = command.ExecuteScalar();
                return value != null
                    && value != DBNull.Value
                    && Convert.ToInt32(value) != 0;
            }
        }

        internal GameEventInfoSnapshot LoadEventInfoSnapshot()
        {
            using (var connection = _database.OpenConnection())
            {
                return new GameEventInfoSnapshot
                {
                    Events = LoadEnabledDetails(connection),
                    ExtraEntries = LoadEnabledExtraEntries(connection),
                };
            }
        }

        private static IReadOnlyList<GameEventInfoEntry> LoadEnabledDetails(
            SqliteConnection connection)
        {
            var entries = new List<GameEventInfoEntry>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT d.event_id,
       d.unknown0,
       d.start_notice,
       d.end_notice,
       d.detail_flag,
       d.flag_a,
       d.flag_b,
       d.title,
       d.short_name,
       d.reserved_or_icon,
       d.start_unix_time,
       d.end_unix_time,
       d.link_key,
       d.description,
       d.detail_enabled
FROM game_event_info_details d
JOIN game_event_state s ON s.event_id = d.event_id
WHERE s.state != 0
ORDER BY d.sort_order, d.event_id;";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        entries.Add(new GameEventInfoEntry
                        {
                            EventId = (ushort)reader.GetInt32(0),
                            Unknown0 = (uint)reader.GetInt64(1),
                            StartNotice = ReadString(reader, 2),
                            EndNotice = ReadString(reader, 3),
                            HasDetail = reader.GetInt32(4) != 0,
                            FlagA = (byte)reader.GetInt32(5),
                            FlagB = (byte)reader.GetInt32(6),
                            Title = ReadString(reader, 7),
                            ShortName = ReadString(reader, 8),
                            ReservedOrIcon = ReadString(reader, 9),
                            StartUnixTime = (uint)reader.GetInt64(10),
                            EndUnixTime = (uint)reader.GetInt64(11),
                            LinkKey = ReadString(reader, 12),
                            Description = ReadString(reader, 13),
                            DetailEnabled = reader.GetInt32(14) != 0,
                        });
                    }
                }
            }

            return entries;
        }

        private static IReadOnlyList<GameEventExtraInfoEntry> LoadEnabledExtraEntries(
            SqliteConnection connection)
        {
            var entries = new List<GameEventExtraInfoEntry>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = @"
SELECT e.event_id,
       e.param0, e.param1, e.param2, e.param3,
       e.param4, e.param5, e.param6, e.param7,
       e.param8, e.param9, e.param10, e.param11
FROM game_event_info_extra e
JOIN game_event_state s ON s.event_id = e.event_id
WHERE s.state != 0
ORDER BY e.sort_order, e.event_id;";
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var parameters = new uint[12];
                        for (var index = 0; index < parameters.Length; index++)
                            parameters[index] = (uint)reader.GetInt64(index + 1);

                        entries.Add(new GameEventExtraInfoEntry
                        {
                            EventId = (ushort)reader.GetInt32(0),
                            Parameters = parameters,
                        });
                    }
                }
            }

            return entries;
        }

        private static string ReadString(SqliteDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal) ? string.Empty : reader.GetString(ordinal);
        }
    }
}
