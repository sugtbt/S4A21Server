using System.Collections.Generic;
using DfoServer.Infrastructure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Game.Inventory
{
    internal sealed class CreatureDetailManager
    {
        private Dictionary<int, CreatureDetail> _details = new Dictionary<int, CreatureDetail>();
        private readonly HashSet<int> _dirtyDetailUids = new HashSet<int>();
        private readonly HashSet<int> _deletedDetailUids = new HashSet<int>();
        private readonly IGameDatabase _database;
        private int _characterId;

        internal CreatureDetailManager(IGameDatabase database = null)
        {
            _database = database;
        }

        public IReadOnlyCollection<CreatureDetail> Details => _details.Values;

        public IReadOnlyCollection<int> DirtyDetailUids => _dirtyDetailUids;

        public IReadOnlyCollection<int> DeletedDetailUids => _deletedDetailUids;

        public void LoadForCharacter(int characterId)
        {
            var database = _database ?? GameDatabase.CreateDefault();
            using (var connection = database.OpenConnection())
            {
                LoadForCharacter(connection, characterId);
            }
        }

        internal void LoadForCharacter(SqliteConnection connection, int characterId)
        {
            _characterId = characterId;
            _details = CreatureDetailRepository.LoadForCharacter(connection, characterId);
            _dirtyDetailUids.Clear();
            _deletedDetailUids.Clear();
        }

        internal void BindCharacter(int characterId)
        {
            if (characterId > 0)
                _characterId = characterId;
        }

        public CreatureDetail GetDetail(int creatureKey)
        {
            _details.TryGetValue(creatureKey, out var detail);
            return detail;
        }

        public bool TryGetDetail(int creatureKey, out CreatureDetail detail)
        {
            return _details.TryGetValue(creatureKey, out detail);
        }

        public void Attach(CreatureDetail detail)
        {
            if (detail == null || detail.Uid <= 0)
                return;

            _deletedDetailUids.Remove(detail.Uid);
            _details[detail.Uid] = detail;
        }

        public bool Put(CreatureDetail detail)
        {
            Attach(detail);
            var saved = InventoryPersistenceService.SaveCreatureDetailImmediately(
                _characterId,
                detail,
                _database);
            if (saved && detail != null)
            {
                _dirtyDetailUids.Remove(detail.Uid);
                _deletedDetailUids.Remove(detail.Uid);
            }
            return saved;
        }

        internal bool PutDirty(CreatureDetail detail)
        {
            if (detail == null || detail.Uid <= 0)
                return false;

            Attach(detail);
            MarkDirty(detail.Uid);
            return true;
        }

        public CreatureDetail CreateDetail(ItemCore core)
        {
            return CreateDetail(core, true);
        }

        internal CreatureDetail CreateDetail(ItemCore core, bool persistImmediately)
        {
            if (core == null || core.ItemKind != ItemCore.KindCreature || core.ItemId <= 0)
                return null;

            if (core.CreatureUid <= 0)
            {
                var creatureUid = CreatureDetailRepository.AllocateCreatureUid(
                    _database);
                if (creatureUid <= 0 || creatureUid > int.MaxValue)
                    return null;

                core.CreatureUid = (int)creatureUid;
            }

            if (_details.TryGetValue(core.CreatureUid, out var existing))
                return existing;

            var detail = new CreatureDetail
            {
                Uid = core.CreatureUid,
                Field04 = 100,
                ModeFlag = 0,
                Mode1Field0A = 0,
                Mode1Field0B = 0,
                ProgressValue32 = 0,
                FieldAfterValue32 = 1,
                ExpireDate = CreatureDetail.GetExpireDate(core.ItemId),
                TailFlag = 0,
            };

            if (persistImmediately)
            {
                if (!Put(detail))
                    return null;
            }
            else
            {
                Attach(detail);
                MarkDirty(detail.Uid);
            }
            return detail;
        }

        public bool Detach(int creatureKey)
        {
            if (creatureKey <= 0)
                return false;

            _dirtyDetailUids.Remove(creatureKey);
            _deletedDetailUids.Remove(creatureKey);
            return _details.Remove(creatureKey);
        }

        internal bool RemoveDirty(int creatureKey)
        {
            if (creatureKey <= 0)
                return false;

            _dirtyDetailUids.Remove(creatureKey);
            _deletedDetailUids.Add(creatureKey);
            return _details.Remove(creatureKey);
        }

        public bool Remove(int creatureKey)
        {
            if (creatureKey <= 0)
                return false;

            _dirtyDetailUids.Remove(creatureKey);
            _deletedDetailUids.Remove(creatureKey);
            var removed = _details.Remove(creatureKey);
            InventoryPersistenceService.DeleteCreatureDetailImmediately(
                _characterId,
                creatureKey,
                _database);
            return removed;
        }

        internal void MarkDirty(int creatureKey)
        {
            if (creatureKey > 0)
            {
                _deletedDetailUids.Remove(creatureKey);
                _dirtyDetailUids.Add(creatureKey);
            }
        }

        internal IReadOnlyList<CreatureDetail> GetDirtyDetails()
        {
            var result = new List<CreatureDetail>();
            foreach (var creatureKey in _dirtyDetailUids)
            {
                if (_details.TryGetValue(creatureKey, out var detail))
                    result.Add(detail);
            }

            return result;
        }

        internal void ClearDirtyState()
        {
            _dirtyDetailUids.Clear();
            _deletedDetailUids.Clear();
        }
    }
}
