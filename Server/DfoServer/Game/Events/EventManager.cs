using System;
using DfoServer.Game.Events.DailyAttendanceAnytime;
using DfoServer.Game.Events.Joust;
using DfoServer.Game.Events.PcRoomTimePoint;
using DfoServer.Game.Events.TotalAttendance;
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

            var pcRoomConfig = PcRoomTimePointConfigProvider.Instance;
            pcRoomConfig.Warmup();
            new PcRoomTimePointRepository(Database)
                .EnsureStaticConfigRows(pcRoomConfig.Current);

            var dailyAttendanceConfig =
                DailyAttendanceAnytimeConfigProvider.Instance;
            dailyAttendanceConfig.Warmup();
            new DailyAttendanceAnytimeRepository(Database)
                .EnsureStaticConfigRows(dailyAttendanceConfig.Current);

            var totalAttendanceConfig = TotalAttendanceConfigProvider.Instance;
            totalAttendanceConfig.Warmup();
            new TotalAttendanceRepository(Database)
                .EnsureStaticConfigRows(totalAttendanceConfig.Current);
        }

        internal GameEventInfoSnapshot LoadEventInfoSnapshot()
        {
            return _repository.LoadEventInfoSnapshot();
        }
    }
}
