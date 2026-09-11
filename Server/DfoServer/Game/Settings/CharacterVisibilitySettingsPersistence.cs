using DfoServer.Game.CharacterData;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;
using System;

namespace DfoServer.Game.Settings
{
    internal sealed class CharacterVisibilitySettingsPersistence
    {
        private readonly string _connectionString;

        public CharacterVisibilitySettingsPersistence(string databasePath, string schemaFilePath)
            : this(new GameDatabase(databasePath, schemaFilePath))
        {
        }

        public CharacterVisibilitySettingsPersistence(IGameDatabase database)
        {
            _connectionString = (database ?? throw new ArgumentNullException(nameof(database)))
                .ConnectionString;
        }

        public byte[] Save(int accountId, int characterId, byte[] mainGameOption, byte userStateBits)
        {
            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    var packed = mainGameOption;
                    if (packed != null && packed.Length > AccountSettings.PackedMainGameOptionLength)
                    {
                        packed = new byte[AccountSettings.PackedMainGameOptionLength];
                        Buffer.BlockCopy(mainGameOption, 0, packed, 0, packed.Length);
                    }
                    AccountSettingsRepository.SaveMainOption(
                        connection,
                        transaction,
                        accountId,
                        packed);
                    SqliteSubtype0FieldsRepository.SaveUserStateBits(
                        connection,
                        transaction,
                        characterId,
                        userStateBits);

                    byte[] existing = null;
                    using (var load = connection.CreateCommand())
                    {
                        load.Transaction = transaction;
                        load.CommandText =
                            "SELECT character_option_blob FROM character_init_flags WHERE character_id=@cid";
                        load.Parameters.AddWithValue("@cid", characterId);
                        var value = load.ExecuteScalar();
                        if (value != null && value != DBNull.Value)
                            existing = (byte[])value;
                    }

                    var projected = AccountSettings.ProjectCharacterOptionBlob(existing, userStateBits, packed);
                    using (var save = connection.CreateCommand())
                    {
                        save.Transaction = transaction;
                        save.CommandText = @"
INSERT INTO character_init_flags (character_id, character_option_blob)
VALUES (@cid, @body)
ON CONFLICT(character_id) DO UPDATE SET character_option_blob=@body";
                        save.Parameters.AddWithValue("@cid", characterId);
                        save.Parameters.AddWithValue("@body", projected);
                        save.ExecuteNonQuery();
                    }

                    transaction.Commit();
                    return projected;
                }
            }
        }
    }
}
