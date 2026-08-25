using System;
using DfoServer.Game.Events.Joust;
using DfoServer.Infrastructure;

namespace DfoServer.Game.Events
{
    internal sealed class EventManager
    {
        private readonly GameEventRepository _repository;

        internal EventManager(IGameDatabase database)
        {
            Database = database ?? throw new ArgumentNullException(nameof(database));
            _repository = new GameEventRepository(Database);
        }

        internal IGameDatabase Database { get; }

        internal void Initialize()
        {
            var joustConfig = JoustConfigProvider.Instance;
            joustConfig.Warmup();
            new JoustRepository(Database)
                .EnsureStaticConfigRows(joustConfig.Current);
        }

        internal GameEventInfoSnapshot LoadEventInfoSnapshot()
        {
            return _repository.LoadEventInfoSnapshot();
        }
    }
}
