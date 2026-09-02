using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;

namespace DfoServer.Game.Quests
{
    internal sealed class QuestActiveTriggerRepairService
    {
        private readonly string _connectionString;

        internal QuestActiveTriggerRepairService(string connectionString)
        {
            _connectionString = connectionString
                ?? throw new ArgumentNullException(nameof(connectionString));
        }

        internal IReadOnlyList<QuestSetTriggerResult>
            RepairWorldMapHuntMonsterTriggers(int characterId)
        {
            var repairs = new List<QuestSetTriggerResult>();
            if (characterId <= 0)
                return repairs;

            using (var connection = new SqliteConnection(_connectionString))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction(
                           deferred: false))
                {
                    var active = QuestRepository.LoadActiveQuests(
                        connection,
                        transaction,
                        characterId);
                    foreach (var quest in active)
                    {
                        if (quest == null
                            || !GameWorld.QuestData
                                .TryRepairWorldMapHuntMonsterTrigger(
                                    quest.QuestId,
                                    quest.TriggerValue,
                                    out var repaired)
                            || repaired == quest.TriggerValue)
                        {
                            continue;
                        }

                        if (!QuestRepository.TryUpdateTriggerValueCas(
                                connection,
                                transaction,
                                characterId,
                                quest.QuestId,
                                quest.ActivationId,
                                quest.Version,
                                quest.TriggerValue,
                                repaired))
                        {
                            throw new InvalidOperationException(
                                $"quest trigger repair CAS conflict " +
                                $"quest={quest.QuestId}");
                        }

                        repairs.Add(new QuestSetTriggerResult
                        {
                            QuestId = quest.QuestId,
                            PreviousTriggerValue = quest.TriggerValue,
                            TriggerValue = repaired,
                        });
                    }

                    transaction.Commit();
                }
            }

            return repairs;
        }
    }
}
